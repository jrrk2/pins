#region "copyright"

/*
    Copyright © 2026 the PI.N.S. contributors

    This file is part of PI 'N' Stars.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NINA.Astrometry;
using NINA.Core.Enum;
using NINA.Core.Utility;
using NINA.Equipment.Interfaces;
using NINA.Profile.Interfaces;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Equipment.Equipment.Origin {

    /// <summary>
    /// The Origin's alt-az mount.
    ///
    /// The commands were chosen from what the real firmware was seen to answer (packet captures of the Celestron app and the telescope logs), not from
    /// the names in OriginMonitor's backend or the simulator; see <see cref="OriginBackend.AbortMotion"/>. SyncToRaDec has never been seen working, so Sync returns
    /// false and PI.N.S. centring then applies a measured correction.
    ///
    /// There is also no park on the firmware: as in OriginHeadless's own 'park', this means a slew to Polaris. A mount that has not been initialised reports AtPark,
    /// so that Unpark is the way to initialise it.
    /// </summary>
    public class OriginTelescope : OriginDevice, ITelescope {
        /// <summary>Set to 1 or true, Unpark asks for the firmware's fake initialisation, for testing: no star alignment, and the mount then reports aligned. The firmware may still move the mount, to 45° altitude.</summary>
        public const string FakeInitializeVariable = "ORIGIN_FAKE_INIT";

        // Polaris (J2000), a safe place for the tube.
        private const double ParkRaHours = 2.5303;
        private const double ParkDecDegrees = 89.264;

        private const int MaxFirmwareRate = 9;
        private const double FullRateDegreesPerSecond = 4;

        // Degrees a second for each firmware rate from 1 to 9, measured on the encoders of the real telescope on 2026-11-07 (rate 2 and rates 4 to 9, rate 9 on both axes). They are far from
        // proportional. Rates 1 and 3 were not measured and are estimates between their neighbours.
        private static readonly double[] FirmwareSpeeds = { 0.002, 0.004, 0.012, 0.033, 0.068, 0.27, 0.97, 2.0, FullRateDegreesPerSecond };
        private const double DefaultMovingRate = 1;

        private static readonly TimeSpan GotoTimeout = TimeSpan.FromSeconds(240);
        private static readonly TimeSpan InitializeTimeout = TimeSpan.FromSeconds(300);

        // Below this the view from the initial position is likely blocked: the tube is raised before it looks for stars to align on.
        private const double TreeClearDegrees = 40;
        private static readonly TimeSpan SettleTimeout = TimeSpan.FromSeconds(180);

        private readonly IProfileService profileService;
        private MountStatus mount = new();
        private bool slewInProgress;
        private volatile bool stopRequested;
        private bool parkedAtPolaris;
        private readonly int[] axisRates = new int[2];  // altitude, azimuth
        private Coordinates targetCoordinates;

        public OriginTelescope(IProfileService profileService, string host, int port = OriginBackend.DefaultPort, string logDirectory = null, string filterFile = null) : base(host, port, logDirectory, filterFile) {
            this.profileService = profileService;
        }

        // After a goto the task controller can still say IDLE for a moment before the centring starts.
        internal TimeSpan SettleGrace { get; set; } = TimeSpan.FromSeconds(5);
        internal TimeSpan SettleCheckInterval { get; set; } = TimeSpan.FromSeconds(1);

        internal bool FakeInitialize { get; set; } = FakeInitializeRequested(Environment.GetEnvironmentVariable(FakeInitializeVariable));

        // On the real telescope the mount reported aligned within a second of a fake initialisation (2026-10-15, 2026-10-31).
        internal TimeSpan FakeAlignedWait { get; set; } = TimeSpan.FromSeconds(10);

        internal static bool FakeInitializeRequested(string value) => value?.Trim() is "1" || string.Equals(value?.Trim(), "true", StringComparison.OrdinalIgnoreCase);

        public override string Id => "Origin_Mount";
        public override string Name => "Celestron Origin Mount";
        public override string Description => "Celestron Origin alt-az mount";

        protected override async Task Poll(CancellationToken token) {
            if (Backend?.IsConnected != true) {
                throw new OriginException("Not connected to the telescope");
            }
            if (await Backend.QueryMount(token) == null) {
                throw new OriginException("No status from the mount");
            }
            mount = Backend.Data.Mount;
            RaisePropertyChanged(nameof(Slewing));
        }

        private bool aligned => mount.IsAligned;
        private bool gotoOver => mount.IsGotoOver;
        private bool tracking => mount.IsTracking;
        private double raRadians => mount.Ra;
        private double decRadians => mount.Dec;
        private double altRadians => mount.Altitude;
        private double azRadians => mount.Azimuth;

        // Radians on the telescope (the simulator sends degrees, which this rejects in favour of the profile's site), and only meaningful once aligned.
        private bool SiteIsPlausible => aligned && Math.Abs(mount.Latitude) <= Math.PI / 2 && Math.Abs(mount.Longitude) <= Math.PI;

        public Coordinates Coordinates => new Coordinates(Angle.ByRadians(raRadians), Angle.ByRadians(decRadians), Epoch.J2000);
        public double RightAscension => AstroUtil.EuclidianModulus(AstroUtil.DegreesToHours(AstroUtil.ToDegree(raRadians)), 24);
        public string RightAscensionString => AstroUtil.HoursToHMS(RightAscension);
        public double Declination => AstroUtil.ToDegree(decRadians);
        public string DeclinationString => AstroUtil.DegreesToDMS(Declination);
        public double SiderealTime => AstroUtil.GetLocalSiderealTimeNow(SiteLongitude);
        public string SiderealTimeString => AstroUtil.HoursToHMS(SiderealTime);
        public double Altitude => AstroUtil.ToDegree(altRadians);
        public string AltitudeString => AstroUtil.DegreesToDMS(Altitude);
        public double Azimuth => AstroUtil.ToDegree(azRadians);
        public string AzimuthString => AstroUtil.DegreesToDMS(Azimuth);

        public double HoursToMeridian => tracking
            ? Astrometry.MeridianFlip.TimeToMeridian(Coordinates, Angle.ByHours(SiderealTime)).TotalHours
            : 24;

        public string HoursToMeridianString => AstroUtil.HoursToHMS(HoursToMeridian);

        // An alt-az mount has nothing to flip.
        public double TimeToMeridianFlip => 24;

        public string TimeToMeridianFlipString => AstroUtil.HoursToHMS(TimeToMeridianFlip);
        public double PrimaryMovingRate { get; set; } = DefaultMovingRate;
        public double SecondaryMovingRate { get; set; } = DefaultMovingRate;
        public PierSide SideOfPier => PierSide.pierUnknown;

        // StartTracking and StopTracking have not been seen on real firmware. The telescope starts tracking after a goto by itself.
        public bool CanSetTrackingEnabled => false;

        public bool TrackingEnabled {
            get => tracking;
            set => Logger.Debug($"{Name}: tracking is managed by the telescope; ignoring a request to set it to {value}");
        }

        public IList<TrackingMode> TrackingModes { get; } = new List<TrackingMode> { TrackingMode.Sidereal, TrackingMode.Stopped };
        public TrackingRate TrackingRate => new TrackingRate { TrackingMode = TrackingMode };

        public TrackingMode TrackingMode {
            get => tracking ? TrackingMode.Sidereal : TrackingMode.Stopped;
            set { }
        }

        public double SiteLatitude {
            get => SiteIsPlausible ? AstroUtil.ToDegree(mount.Latitude) : profileService.ActiveProfile.AstrometrySettings.Latitude;
            set { }
        }

        public double SiteLongitude {
            get => SiteIsPlausible ? AstroUtil.ToDegree(mount.Longitude) : profileService.ActiveProfile.AstrometrySettings.Longitude;
            set { }
        }

        public double SiteElevation {
            get => profileService.ActiveProfile.AstrometrySettings.Elevation;
            set { }
        }

        public bool AtHome => false;
        public bool CanFindHome => false;
        public bool AtPark => parkedAtPolaris || !aligned;
        public bool CanPark => true;
        public bool CanUnpark => true;
        public bool CanSetPark => false;
        public Epoch EquatorialSystem => Epoch.J2000;
        public bool HasUnknownEpoch => false;
        public Coordinates TargetCoordinates => targetCoordinates;
        public PierSide? TargetSideOfPier => null;
        public bool Slewing => slewInProgress || !gotoOver;
        public double GuideRateRightAscensionArcsecPerSec => 0;
        public double GuideRateDeclinationArcsecPerSec => 0;
        public bool CanMovePrimaryAxis => true;
        public bool CanMoveSecondaryAxis => true;
        public bool CanSetDeclinationRate => false;
        public bool CanSetRightAscensionRate => false;
        public AlignmentMode AlignmentMode => AlignmentMode.AltAz;
        public bool CanPulseGuide => false;
        public bool IsPulseGuiding => false;
        public bool CanSetPierSide => false;
        public bool CanSlew => true;
        public bool CanSlewAltAz => false;
        public DateTime UTCDate => DateTime.UtcNow;

        public IList<(double, double)> GetAxisRates(TelescopeAxes axis) => new List<(double, double)> { (FirmwareSpeeds[0], FullRateDegreesPerSecond) };

        public Task<bool> MeridianFlip(Coordinates targetCoordinates, CancellationToken token) => Task.FromResult(false);

        /// <summary>
        /// Relative motion, which the firmware accepts before initialisation, unlike a goto. A rate is kept until it is replaced, and 0 stops. The primary axis is azimuth and the secondary altitude.
        /// Waits for the telescope's answer, so that a refused command is not silent.
        /// </summary>
        public void MoveAxis(TelescopeAxes axis, double rate) {
            var backend = Backend ?? throw new OriginException($"{Name} is not connected");
            if (slewInProgress && rate != 0) {
                throw new InvalidOperationException($"{Name} is in a goto: stop it first");
            }
            var firmwareRate = FirmwareRate(rate);
            int alt, azm;
            lock (axisRates) {
                axisRates[axis == TelescopeAxes.Secondary ? 0 : 1] = firmwareRate;
                alt = axisRates[0];
                azm = axisRates[1];
            }
            Task.Run(() => backend.SlewRates(alt, azm)).GetAwaiter().GetResult();
        }

        // The firmware rate whose measured speed is nearest on a logarithmic scale, because each step is a multiple of the one before.
        internal static int FirmwareRate(double degreesPerSecond) {
            if (degreesPerSecond == 0 || double.IsNaN(degreesPerSecond)) {
                return 0;
            }
            var wanted = Math.Log(Math.Abs(degreesPerSecond));
            var best = 0;
            for (var i = 1; i < MaxFirmwareRate; i++) {
                if (Math.Abs(Math.Log(FirmwareSpeeds[i]) - wanted) < Math.Abs(Math.Log(FirmwareSpeeds[best]) - wanted)) {
                    best = i;
                }
            }
            return Math.Sign(degreesPerSecond) * (best + 1);
        }

        public void PulseGuide(GuideDirections direction, int duration) => throw new NotSupportedException($"{Name} does not support pulse guiding");

        public void Setpark() => throw new NotSupportedException($"{Name} parks on Polaris; the position cannot be changed");

        public void SetCustomTrackingRate(double rightAscensionRate, double declinationRate) => throw new NotSupportedException($"{Name} tracks at the sidereal rate only");

        public Task FindHome(CancellationToken token) => throw new NotSupportedException($"{Name} has no home command");

        public PierSide DestinationSideOfPier(Coordinates coordinates) => PierSide.pierUnknown;

        public Task<bool> SlewToAltAz(TopocentricCoordinates coordinates, CancellationToken token) => Task.FromResult(false);

        /// <summary>
        /// Slews and returns once the telescope is settled, or false if it was stopped. The goto reporting done is not the end: the
        /// firmware then centres the target and may autofocus (about a minute), and a frame taken meanwhile would be trailed.
        /// </summary>
        public async Task<bool> SlewToCoordinates(Coordinates coordinates, CancellationToken token) {
            var backend = Backend ?? throw new OriginException($"{Name} is not connected");
            if (!aligned) {
                throw new InvalidOperationException($"{Name} is not initialised: unpark it first (the mount homes and aligns on the stars)");
            }

            var j2000 = coordinates.Transform(Epoch.J2000);
            targetCoordinates = j2000;
            stopRequested = false;
            parkedAtPolaris = false;
            slewInProgress = true;
            RaisePropertyChanged(nameof(Slewing));
            try {
                await backend.Goto(AstroUtil.EuclidianModulus(j2000.RA, 24), j2000.Dec, GotoTimeout, token);
                if (!stopRequested) {
                    var state = await backend.WaitUntilIdle(SettleGrace, SettleTimeout, () => !stopRequested, SettleCheckInterval, token);
                    if (!stopRequested && !string.Equals(state, "IDLE", StringComparison.OrdinalIgnoreCase)) {
                        Logger.Warning($"{Name}: the telescope still reports '{state}' {SettleTimeout.TotalSeconds:0} s after the goto");
                    }
                }
                return !stopRequested;
            } catch (OperationCanceledException) {
                stopRequested = true;
                await backend.AbortMotion();
                throw;
            } finally {
                slewInProgress = false;
                RaisePropertyChanged(nameof(Slewing));
            }
        }

        /// <summary>Stops a slew. Throws if the telescope accepted neither stop command, so a failed stop is never silent.</summary>
        public void StopSlew() {
            var backend = Backend ?? throw new OriginException($"{Name} is not connected");
            stopRequested = true;
            lock (axisRates) {
                Array.Clear(axisRates);
            }
            Task.Run(() => backend.AbortMotion()).GetAwaiter().GetResult();
        }

        /// <summary>Always false: see the class comment.</summary>
        public bool Sync(Coordinates coordinates) {
            Logger.Info($"{Name}: no sync on this mount; PI.N.S. centring uses a measured correction instead");
            return false;
        }

        public async Task Park(CancellationToken token) {
            parkedAtPolaris = await SlewToCoordinates(new Coordinates(Angle.ByHours(ParkRaHours), Angle.ByDegree(ParkDecDegrees), Epoch.J2000), token);
        }

        /// <summary>
        /// On an initialised mount this only clears the parked state. Otherwise it runs the telescope's initialisation: the tube is raised if the tilt sensor says it is low, then the
        /// mount homes and aligns on the stars, which takes minutes and needs a clear sky. The site comes from the profile, and an unset site is refused rather than aligning for the wrong place.
        /// </summary>
        public async Task Unpark(CancellationToken token) {
            if (aligned) {
                parkedAtPolaris = false;
                return;
            }
            var backend = Backend ?? throw new OriginException($"{Name} is not connected");
            var site = profileService.ActiveProfile.AstrometrySettings;
            if (site.Latitude == 0 && site.Longitude == 0) {
                throw new InvalidOperationException($"{Name} cannot be initialised without a site: set the latitude and longitude in the profile");
            }
            var fake = FakeInitialize;
            if (fake) {
                Logger.Warning($"{Name}: {FakeInitializeVariable} is set: skipping the star alignment, so the mount's pointing cannot be trusted and it will not avoid the sun");
            } else if (backend.Data.OrientationLastUpdate.HasValue && backend.Data.Orientation.Altitude < TreeClearDegrees) {
                await backend.RaiseTube(token: token);      // only to give the alignment a view of the stars
            }
            // Without a link to the zone database .NET calls the local zone "Local", which the telescope would not know.
            var zone = TimeZoneInfo.Local.Id == "Local" ? "UTC" : TimeZoneInfo.Local.Id;
            await backend.Initialize(site.Latitude, site.Longitude, zone, fake, InitializeTimeout, token);
            parkedAtPolaris = false;
            await Poll(token);
            if (fake) {
                var waited = System.Diagnostics.Stopwatch.StartNew();
                while (!aligned) {
                    if (waited.Elapsed >= FakeAlignedWait) {
                        throw new OriginException($"{Name} did not report aligned {FakeAlignedWait.TotalSeconds:0} s after a fake initialisation");
                    }
                    await Task.Delay(TimeSpan.FromMilliseconds(250), token);
                    await Poll(token);
                }
            }
        }
    }
}
