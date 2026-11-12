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
        /// <summary>
        /// Unpark normally asks for the firmware's fake initialisation (no star alignment of its own; the firmware may still move the tube, to 45 degrees altitude)
        /// and then aligns from PI.N.S.'s own plate solves (<see cref="UnparkPointsVariable"/>). Set to 0 or false, Unpark runs the firmware's own initialisation
        /// instead; that is also the action <see cref="CelestronInitializeAction"/>.
        /// </summary>
        public const string FakeInitializeVariable = "ORIGIN_FAKE_INIT";

        /// <summary>
        /// Set to 1, the mount moves with the sun up: for OriginSimulator in daylight only. Otherwise nothing moves while the sun is above the horizon, because a
        /// fake initialisation, PI.N.S.'s default, does not have the firmware's own refusal ("Sun is above horizon").
        /// </summary>
        public const string AllowDaylightVariable = "ORIGIN_ALLOW_DAYLIGHT";

        /// <summary>The driver action that runs the telescope's own initialisation: a focus search, two alignment stars it chooses, and a level base assumed.</summary>
        public const string CelestronInitializeAction = "CelestronInitialize";

        // Polaris (J2000), near the pole: the park for the firmware's own frame.
        private const double ParkRaHours = 2.5303;
        private const double ParkDecDegrees = 89.264;

        /// <summary>
        /// Set to 0, Unpark takes no plate solve after a fake initialisation (gotos then go to the right coordinates in the firmware's invented frame, not on the
        /// sky). Otherwise it takes one, for a level-base model: Unpark only gates the mount. A fuller model is built with <see cref="ModelBuilder"/>.
        /// </summary>
        public const string UnparkPointsVariable = "UNPARK_ALIGN";

        // A track by the model runs this long and is renewed this long before it ends; N.I.N.A. expects tracking to go on.
        private static readonly TimeSpan TrackLength = TimeSpan.FromHours(1);
        private static readonly TimeSpan TrackRenewBefore = TimeSpan.FromMinutes(10);
        private static readonly TimeSpan TrackStep = TimeSpan.FromSeconds(30);

        // A sync within this long of a frame arriving is taken to be that frame's plate solve.
        private static readonly TimeSpan SyncFrameAge = TimeSpan.FromMinutes(3);

        private const int MaxFirmwareRate = 9;
        private const double FullRateDegreesPerSecond = 4;

        // Degrees a second for each firmware rate from 1 to 9, measured on the encoders of the real telescope on 2026-11-07 (rate 2 and rates 4 to 9, rate 9 on both axes). They are far from
        // proportional. Rates 1 and 3 were not measured and are estimates between their neighbours.
        private static readonly double[] FirmwareSpeeds = { 0.002, 0.004, 0.012, 0.033, 0.068, 0.27, 0.97, 2.0, FullRateDegreesPerSecond };
        private const double DefaultMovingRate = 1;

        private static readonly TimeSpan GotoTimeout = TimeSpan.FromSeconds(240);
        private static readonly TimeSpan InitializeTimeout = TimeSpan.FromSeconds(600);   // a failed focus search alone took 308 s (2026-11-07)

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

        // PI.N.S.'s own pointing (OriginCalibration), set up by Unpark after a fake initialisation and refined by every Sync. While it has a model, slews and the
        // reported position go by it rather than by the firmware's frame.
        private OriginCalibration calibration;
        private readonly object unparkLock = new();
        private Task unparking;
        private IOriginSky sky;
        private (double RaDegrees, double DecDegrees)? trackTarget;
        private DateTime trackUntil;
        private (double Ra, double Dec, double Alt, double Az)? modelled;      // radians, from the model at the last poll
        private bool firmwareAligned;                                          // the telescope's own initialisation ran: its frame is real
        private bool unparkedByPins;
        private DateTime lastMoveUtc = DateTime.MinValue;                        // the end of the last slew, park or manual move: a frame from before it shows somewhere else                                           // PI.N.S.'s Unpark has run this session (counts only with UNPARK_ALIGN=0)

        public OriginTelescope(IProfileService profileService, string host, int port = OriginBackend.DefaultPort, string logDirectory = null, string filterFile = null) : base(host, port, logDirectory, filterFile) {
            this.profileService = profileService;
            ModelBuilder = new OriginModelBuilder(this);
        }

        /// <summary>The Origin mount last connected, for the model builder screens (which check <see cref="OriginDevice.Connected"/>); null before any.</summary>
        public static OriginTelescope Current { get; private set; }

        /// <summary>Builds PI.N.S.'s pointing model from many plate-solved points: Touch-N-Stars' model builder screens.</summary>
        public OriginModelBuilder ModelBuilder { get; }

        protected override Task OnConnecting(CancellationToken token) {
            Current = this;
            return base.OnConnecting(token);
        }

        protected override void OnDisconnecting() {
            ModelBuilder.Cancel();
            base.OnDisconnecting();
        }

        // ── for OriginModelBuilder ──

        /// <summary>PI.N.S.'s pointing, set up by Unpark; null before it.</summary>
        internal OriginCalibration Calibration => calibration;

        internal OriginBackend Link => Backend;

        internal void RefuseToMoveInDaylight(string what) => RefuseInDaylight(what);

        /// <summary>Stops a track and any motion before a model build moves the mount itself.</summary>
        internal async Task StopForModelBuild(CancellationToken token) {
            trackTarget = null;
            parkedAtPolaris = false;
            if (Backend is { } backend) {
                await backend.AbortMotion(token);
            }
        }

        /// <summary>A model build moved the mount: frames from before show somewhere else.</summary>
        internal void MovedByModelBuild() => lastMoveUtc = DateTime.UtcNow;

        private void ThrowIfModelBuilding(string what) {
            if (ModelBuilder.BuildInProgress) {
                throw new InvalidOperationException($"{Name}: not {what} while a pointing model is being built; stop the build first");
            }
        }

        // After a goto the task controller can still say IDLE for a moment before the centring starts.
        internal TimeSpan SettleGrace { get; set; } = TimeSpan.FromSeconds(5);
        internal TimeSpan SettleCheckInterval { get; set; } = TimeSpan.FromSeconds(1);

        internal bool FakeInitialize { get; set; } = FakeInitializeRequested(Environment.GetEnvironmentVariable(FakeInitializeVariable));

        // On the real telescope the mount reported aligned within a second of a fake initialisation (2026-10-15, 2026-10-31).
        internal TimeSpan FakeAlignedWait { get; set; } = TimeSpan.FromSeconds(10);

        // the default: only "0" or "false" asks for the firmware's own initialisation on Unpark
        internal static bool FakeInitializeRequested(string value) => !(value?.Trim() is "0" || string.Equals(value?.Trim(), "false", StringComparison.OrdinalIgnoreCase));

        public override IList<string> SupportedActions => new List<string> { CelestronInitializeAction };

        /// <summary><see cref="CelestronInitializeAction"/>: the telescope's own initialisation, whatever Unpark would do. PI.N.S.'s own pointing is dropped, the firmware's frame then being real.</summary>
        public override string Action(string actionName, string actionParameters) {
            if (!string.Equals(actionName, CelestronInitializeAction, StringComparison.OrdinalIgnoreCase)) {
                return base.Action(actionName, actionParameters);
            }
            Task.Run(() => Initialize(fake: false, CancellationToken.None)).GetAwaiter().GetResult();
            return "Initialised by the telescope";
        }

        internal bool AllowDaylight { get; set; } = Environment.GetEnvironmentVariable(AllowDaylightVariable)?.Trim() == "1";

        /// <summary>The sun's altitude, degrees, at a UTC time and a site (latitude, longitude in degrees): <see cref="GeometricSunAltitude"/>.</summary>
        internal Func<DateTime, double, double, double> SunAltitude { get; set; } = GeometricSunAltitude;

        /// <summary>
        /// The sun's geometric altitude, degrees, from the Astronomical Almanac's low-precision formulas for the sun (about 0.01 degrees, 1950-2050) and for
        /// Greenwich mean sidereal time, with no files or native libraries: a safety check must not depend on N.I.N.A.'s NOVAS, which without its JPLEPH file
        /// returns a wrong position rather than failing (2026-11-09, under test).
        /// </summary>
        internal static double GeometricSunAltitude(DateTime utc, double latitude, double longitude) {
            const double d2r = Math.PI / 180;
            var n = utc.ToUniversalTime().Subtract(new DateTime(2000, 1, 1, 12, 0, 0, DateTimeKind.Utc)).TotalDays;
            var l = 280.460 + 0.9856474 * n;
            var g = (357.528 + 0.9856003 * n) * d2r;
            var lambda = (l + 1.915 * Math.Sin(g) + 0.020 * Math.Sin(2 * g)) * d2r;
            var epsilon = (23.439 - 0.0000004 * n) * d2r;
            var ra = Math.Atan2(Math.Cos(epsilon) * Math.Sin(lambda), Math.Cos(lambda));
            var dec = Math.Asin(Math.Sin(epsilon) * Math.Sin(lambda));
            var lst = (18.697374558 + 24.06570982441908 * n) * 15 + longitude;
            var hourAngle = lst * d2r - ra;
            var phi = latitude * d2r;
            return Math.Asin(Math.Sin(phi) * Math.Sin(dec) + Math.Cos(phi) * Math.Cos(dec) * Math.Cos(hourAngle)) / d2r;
        }

        // Nothing moves with the sun up: the tube could be pointed at it. Stopping always works.
        private void RefuseInDaylight(string what) {
            if (AllowDaylight) {
                return;
            }
            var site = profileService.ActiveProfile.AstrometrySettings;
            var sun = SunAltitude(DateTime.UtcNow, site.Latitude, site.Longitude);
            if (sun > 0) {
                throw new InvalidOperationException($"{Name}: not {what} with the sun {sun:0} degrees above the horizon");
            }
        }

        internal int UnparkPoints { get; set; } = int.TryParse(Environment.GetEnvironmentVariable(UnparkPointsVariable), out var n) && n >= 0 ? n : 1;

        /// <summary>The plate solver for PI.N.S.'s own pointing: astrometry.net's solve-field by default.</summary>
        internal Func<OriginFrame, CancellationToken, Task<(double RaDegrees, double DecDegrees)?>> Solve { get; set; } = new OriginAstrometryNet().Solve;

        /// <summary>PI.N.S.'s pointing model, or null while the firmware's frame is used.</summary>
        public OriginPointingModel PointingModel => calibration?.Model;

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
            if (calibration?.Model is { } model && sky != null) {
                var (alt, az) = model.ToSky(OriginPointingModel.Deg(mount.Enc0), OriginPointingModel.Deg(mount.Enc1));
                var (ra, dec) = sky.ToJ2000(alt, az, DateTime.UtcNow);
                modelled = (OriginPointingModel.Rad(ra), OriginPointingModel.Rad(dec), OriginPointingModel.Rad(alt), OriginPointingModel.Rad(az));
            } else {
                modelled = null;
            }
            if (trackTarget != null && !slewInProgress && DateTime.UtcNow > trackUntil - TrackRenewBefore) {
                try {
                    await RenewTrack(token);
                } catch (OriginException ex) {
                    Logger.Warning($"{Name}: could not renew the track: {ex.Message}");
                }
            }
            RaisePropertyChanged(nameof(Slewing));
            int azimuthRate;
            lock (axisRates) {
                azimuthRate = axisRates[1];
            }
            // Polls come every 2 s and the axis coasts after a stop: the margin grows with the speed.
            if (azimuthRate != 0 && !Backend.CordWrap.CanMove(Math.Sign(azimuthRate), OriginCordWrap.MarginFor(azimuthRate))) {
                Logger.Warning($"{Name}: stopping the azimuth {Backend.CordWrap.Turned:0} degrees from cable-neutral");
                lock (axisRates) {
                    axisRates[1] = 0;
                }
                await Backend.SlewRates(axisRates[0], 0, token: token);
            }
        }

        // IsAligned is not enough: after a failed second plate solve the mount says aligned with one reference, and the firmware still refuses gotos
        // (-5 "not yet initialized", 2026-11-08). TaskController's IsReady decided all 57 gotos in the logs, whatever the number of references.
        private bool aligned => mount.IsAligned && (Backend?.Data.TaskController is not { IsReadyReported: true } task || task.IsReady);
        private bool gotoOver => mount.IsGotoOver;
        private bool tracking => mount.IsTracking;
        private double raRadians => modelled?.Ra ?? mount.Ra;
        private double decRadians => modelled?.Dec ?? mount.Dec;
        private double altRadians => modelled?.Alt ?? mount.Altitude;
        private double azRadians => modelled?.Az ?? mount.Azimuth;

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
        // With the fake initialisation (the default), the mount stays parked until a plate solve has succeeded, even when the firmware reports itself aligned: the
        // firmware initialises by itself after a reboot, the web page refuses to unpark an unparked mount, and without a solve every goto would go by the invented
        // frame. UNPARK_ALIGN=0 asks for no solve, so there Unpark alone unparks; after the telescope's own initialisation its frame is real.
        private bool AwaitingPlateSolve => FakeInitialize && !firmwareAligned && (UnparkPoints > 0 ? calibration?.Model == null : !unparkedByPins);

        public bool AtPark => parkedAtPolaris || !aligned || AwaitingPlateSolve;
        public bool CanPark => true;
        public bool CanUnpark => true;
        public bool CanSetPark => false;
        public Epoch EquatorialSystem => Epoch.J2000;
        public bool HasUnknownEpoch => false;
        public Coordinates TargetCoordinates => targetCoordinates;
        public PierSide? TargetSideOfPier => null;
        // a model build moves the mount from point to point: N.I.N.A. and Touch-N-Stars hold back their own moves and syncs meanwhile
        public bool Slewing => slewInProgress || !gotoOver || ModelBuilder.BuildInProgress;
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
            if (firmwareRate != 0) {
                RefuseInDaylight("moving");
                ThrowIfModelBuilding("moving");
            }
            if (axis == TelescopeAxes.Primary && !backend.CordWrap.CanMove(Math.Sign(firmwareRate), OriginCordWrap.MarginFor(firmwareRate))) {
                throw new InvalidOperationException($"{Name}: the azimuth is {backend.CordWrap.Turned:0} degrees from where the cables hang straight; turn the other way");
            }
            int alt, azm;
            lastMoveUtc = DateTime.UtcNow;
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
            RefuseInDaylight("slewing");
            ThrowIfModelBuilding("slewing");

            var j2000 = coordinates.Transform(Epoch.J2000);
            targetCoordinates = j2000;
            stopRequested = false;
            parkedAtPolaris = false;
            slewInProgress = true;
            RaisePropertyChanged(nameof(Slewing));
            try {
                if (calibration?.Model != null) {
                    // PI.N.S.'s own pointing: a move by encoder feedback, then a moving-target track by the model, which the firmware follows
                    var raDegrees = AstroUtil.EuclidianModulus(j2000.RA, 24) * 15;
                    if (sky.ToHorizon(raDegrees, j2000.Dec, DateTime.UtcNow).Altitude < 10) {
                        throw new InvalidOperationException($"{Name}: the target is below 10 degrees altitude");
                    }
                    trackTarget = null;
                    await calibration.Track(raDegrees, j2000.Dec, TrackLength, TrackStep, token);
                    trackTarget = (raDegrees, j2000.Dec);
                    trackUntil = DateTime.UtcNow + TrackLength;
                } else {
                    trackTarget = null;
                    await backend.Goto(AstroUtil.EuclidianModulus(j2000.RA, 24), j2000.Dec, GotoTimeout, token);
                }
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
                lastMoveUtc = DateTime.UtcNow;
                RaisePropertyChanged(nameof(Slewing));
            }
        }

        /// <summary>Stops a slew. Throws if the telescope accepted neither stop command, so a failed stop is never silent.</summary>
        public void StopSlew() {
            var backend = Backend ?? throw new OriginException($"{Name} is not connected");
            stopRequested = true;
            trackTarget = null;
            ModelBuilder.Cancel();
            lock (axisRates) {
                Array.Clear(axisRates);
            }
            Task.Run(() => backend.AbortMotion()).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Adds where the tube really points, from a plate solve (N.I.N.A.'s centring, Touch-N-Stars' plate solve and sync), to PI.N.S.'s pointing model: every
        /// centring is a calibration point, and three make the full model. A track in progress is renewed by the new model. False without PI.N.S.'s pointing (after
        /// the firmware's own initialisation, whose SyncToRaDec has never been seen working: centring then uses a measured correction), or when the point is
        /// turned away (degrees from a fitted model: a bad solve, or the mount moved).
        /// </summary>
        public bool Sync(Coordinates coordinates) {
            var backend = Backend;
            if (calibration == null || sky == null || backend == null) {
                Logger.Info($"{Name}: no sync without PI.N.S.'s own pointing; centring uses a measured correction instead");
                return false;
            }
            var j2000 = coordinates.Transform(Epoch.J2000);
            // The solve is of the last frame: pair it with the encoders at that frame's mid-exposure, not now. The plate solve takes 10-30 s, and meanwhile the mount
            // tracks by a model that may still be rough, so now the encoders point elsewhere (a 2.6' floor on centring without this, OriginSimulator 2026-11-09).
            var when = DateTime.UtcNow;
            var m = backend.Data.Mount;
            var (enc0, enc1) = (OriginPointingModel.Deg(m.Enc0), OriginPointingModel.Deg(m.Enc1));
            // only a frame taken since the mount last moved shows where it is now: a sync typed in after a slew, with the last frame from before it, would otherwise
            // be paired with the encoders of another part of the sky (47 degrees out after one such sync, OriginSimulator 2026-11-09)
            if (backend.LastExposure is { } exposure && DateTime.UtcNow - exposure.Arrived < SyncFrameAge && exposure.MidExposure > lastMoveUtc
                && backend.EncodersAt(exposure.MidExposure) is { } then) {
                (when, enc0, enc1) = (exposure.MidExposure, then.Enc0, then.Enc1);
            } else {
                Logger.Info($"{Name}: no frame since the mount last moved to match the sync to: using the encoders now");
            }
            var (alt, az) = sky.ToHorizon(AstroUtil.EuclidianModulus(j2000.RA, 24) * 15, j2000.Dec, when);
            var point = new OriginCalibrationPoint(Math.IEEERemainder(enc0, 360), Math.IEEERemainder(enc1, 360), alt, az);
            calibration.Refine(point);
            if (!calibration.Points.Contains(point)) {
                return false;
            }
            Logger.Info($"{Name}: synced; {calibration.Points.Count} pointing points, rms {calibration.Model.RmsArcmin:0.0} arcmin");
            if (trackTarget != null) {
                Task.Run(() => RenewTrack(CancellationToken.None)).GetAwaiter().GetResult();
            }
            return true;
        }

        /// <summary>
        /// The Origin has no park of its own, so PI.N.S. defines it: the tube points at the celestial pole and stops tracking. A tube left there into the day can
        /// never see the sun, which stays within 23.4 degrees of the equator and so at least 66.6 degrees from the pole, and the pole does not move. That matters
        /// more than unwinding the cables, which only go the way that keeps them within the limit. With PI.N.S.'s pointing: due north (south in the southern
        /// hemisphere) at the latitude's altitude, by encoder feedback. After the telescope's own initialisation: its goto to Polaris. After a fake initialisation
        /// with no plate solve yet, where north is not known: refused. Unpark only clears it.
        /// </summary>
        public async Task Park(CancellationToken token) {
            var backend = Backend ?? throw new OriginException($"{Name} is not connected");
            RefuseInDaylight("parking");
            ThrowIfModelBuilding("parking");
            if (calibration?.Model == null && !firmwareAligned && FakeInitialize) {
                // no plate solve yet: where north is, and so the pole, is not known; the mount already reports parked
                Logger.Warning($"{Name}: no plate solve yet, so the celestial pole cannot be found to park on; leaving the tube where it is");
                trackTarget = null;
                await backend.AbortMotion(token);
                return;
            }
            if (calibration?.Model == null) {
                // the firmware's own frame is real: its goto, then no tracking
                parkedAtPolaris = await SlewToCoordinates(new Coordinates(Angle.ByHours(ParkRaHours), Angle.ByDegree(ParkDecDegrees), Epoch.J2000), token);
                await backend.AbortMotion(token);
                return;
            }
            trackTarget = null;
            stopRequested = false;
            slewInProgress = true;
            RaisePropertyChanged(nameof(Slewing));
            try {
                await backend.AbortMotion(token);
                var latitude = SiteLatitude;
                var (enc0, enc1) = calibration.Aim(Math.Abs(latitude), latitude >= 0 ? 0 : 180);
                await calibration.MoveTo(enc0, enc1, token);
                parkedAtPolaris = !stopRequested;
            } finally {
                slewInProgress = false;
                lastMoveUtc = DateTime.UtcNow;
                RaisePropertyChanged(nameof(Slewing));
            }
        }

        // A fresh track from now for the target, by the current model: before the last one ends, and after a sync.
        private async Task RenewTrack(CancellationToken token) {
            if (trackTarget is not var (ra, dec) || calibration?.Model == null || Backend is not { } backend) {
                return;
            }
            await backend.QueryMount(token);
            await backend.TrackMovingTarget(calibration.BuildTrack(ra, dec, DateTime.UtcNow + TimeSpan.FromSeconds(1), TrackLength, TrackStep), token);
            trackUntil = DateTime.UtcNow + TrackLength;
        }

        /// <summary>
        /// The altitude, by the tilt sensor, for Unpark's plate solve. Where the tube happens to stop it could be looking at a tree or a wall; 60 degrees clears
        /// them, and is far enough from the zenith for one solve to fix the azimuth well.
        /// </summary>
        internal const double UnparkSolveAltitude = 60;

        // Raised by the tilt sensor if low; lowered by the altitude encoder if well above. Without a tilt reading the solve is taken where the tube points.
        private async Task PointUpForUnparkSolve(OriginBackend backend, CancellationToken token) {
            try {
                var altitude = await backend.RaiseTube(UnparkSolveAltitude, token: token);
                if (altitude > UnparkSolveAltitude + 3) {
                    var (enc0, enc1) = await calibration.Encoders(token);
                    await calibration.MoveTo(enc0, enc1 - (altitude - UnparkSolveAltitude), token);
                }
                lastMoveUtc = DateTime.UtcNow;
                Logger.Info($"{Name}: the tube at {backend.Data.Orientation.Altitude:0} degrees altitude for Unpark's plate solve");
            } catch (OriginException ex) {
                Logger.Warning($"{Name}: could not bring the tube to {UnparkSolveAltitude:0} degrees ({ex.Message}); plate solving where it points");
            }
        }

        // After a fake initialisation: plate-solved points where PI.N.S. chooses, so that gotos land on the sky and not in the firmware's invented frame.
        private async Task AlignFromPlateSolves(OriginBackend backend, double latitude, double longitude, double elevation, CancellationToken token) {
            if (UnparkPoints <= 0) {
                Logger.Warning($"{Name}: {UnparkPointsVariable}=0: no plate solves; gotos go to the right coordinates in the firmware's invented frame, not on the sky");
                return;
            }
            sky = SofaOriginSky.ForOrigin(backend, latitude, longitude, elevation);
            calibration = new OriginCalibration(backend, sky, Solve, new SofaOriginSky(latitude, longitude, elevation));
            try {
                await PointUpForUnparkSolve(backend, token);
                calibration.Refine(await calibration.Measure(2, backend.SolveIso(800), token));
            } catch (OriginException ex) {
                Logger.Error($"{Name}: could not plate solve during Unpark ({ex.Message}); gotos use the firmware's invented frame until a plate solve and sync");
            }
            if (calibration.Model == null) {
                Logger.Warning($"{Name}: no plate solve during Unpark (cloud, or solve-field and its indices are not installed); gotos use the firmware's invented frame until a plate solve and sync");
            } else {
                Logger.Info($"{Name}: pointing from {calibration.Points.Count} plate solve{(calibration.Points.Count == 1 ? "" : "s")}, base tilt {calibration.Model.BaseTilt.Degrees:0.0} degrees");
            }
        }

        /// <summary>
        /// On an initialised mount this only clears the parked state. Otherwise it runs the telescope's initialisation: the tube is raised if the tilt sensor says it is low, then the
        /// mount homes and aligns on the stars, which takes minutes and needs a clear sky. With ORIGIN_FAKE_INIT the firmware's alignment is skipped and PI.N.S. takes its own
        /// plate solves instead (<see cref="UnparkPointsVariable"/>). The site comes from the profile, and an unset site is refused rather than aligning for the wrong place.
        /// </summary>
        public Task Unpark(CancellationToken token) {
            // A second Unpark while one runs (the web page shows no progress, and a press can seem lost) waits for the first and gets its result: two at once
            // drove the mount to each other's points and spoiled both (2026-11-09, three presses).
            lock (unparkLock) {
                if (unparking is { IsCompleted: false }) {
                    Logger.Info($"{Name}: Unpark is already running; waiting for it");
                    return unparking;
                }
                ThrowIfModelBuilding("unparking");
                return unparking = UnparkOnce(token);
            }
        }

        private async Task UnparkOnce(CancellationToken token) {
            var backend = Backend ?? throw new OriginException($"{Name} is not connected");
            RefuseInDaylight("unparking");
            var site = profileService.ActiveProfile.AstrometrySettings;
            if (aligned) {
                parkedAtPolaris = false;
                unparkedByPins = true;
                // PI.N.S. restarted on a telescope already fake-initialised: its own pointing has to be set up again
                if (FakeInitialize && !firmwareAligned && calibration?.Model == null && !(site.Latitude == 0 && site.Longitude == 0)) {
                    await AlignFromPlateSolves(backend, site.Latitude, site.Longitude, site.Elevation, token);
                    await Poll(token);
                }
                ThrowIfStillAwaitingPlateSolve();
                return;
            }
            await Initialize(FakeInitialize, token);
            ThrowIfStillAwaitingPlateSolve();
        }

        private void ThrowIfStillAwaitingPlateSolve() {
            if (AwaitingPlateSolve) {
                throw new OriginException($"{Name}: no successful plate solve, so the mount stays parked: gotos would otherwise go by the firmware's invented frame. " +
                                          "Check the sky is clear and astrometry.net's index files are installed (the log says which), then unpark again");
            }
        }

        private async Task Initialize(bool fake, CancellationToken token) {
            var backend = Backend ?? throw new OriginException($"{Name} is not connected");
            RefuseInDaylight("initialising");
            var site = profileService.ActiveProfile.AstrometrySettings;
            if (site.Latitude == 0 && site.Longitude == 0) {
                throw new InvalidOperationException($"{Name} cannot be initialised without a site: set the latitude and longitude in the profile");
            }
            trackTarget = null;
            calibration = null;
            sky = null;
            modelled = null;
            firmwareAligned = false;
            unparkedByPins = false;
            if (fake) {
                Logger.Info($"{Name}: fake initialisation: PI.N.S. aligns from its own plate solves; the firmware will not avoid the sun");
            } else if (backend.Data.OrientationLastUpdate.HasValue && backend.Data.Orientation.Altitude < TreeClearDegrees) {
                await backend.RaiseTube(token: token);      // only to give the alignment a view of the stars
            }
            // Without a link to the zone database .NET calls the local zone "Local", which the telescope would not know.
            var zone = TimeZoneInfo.Local.Id == "Local" ? "UTC" : TimeZoneInfo.Local.Id;
            // The alignment adds its references from the live stream: with it off, which PI.N.S. keeps it for captures, a reference fails with
            // -53 "Can't add alignment reference" (2026-11-08, with 53 stars in view). It is turned on only for the initialisation.
            if (!fake) {
                backend.SetLiveStreamDisabled(false);
            }
            try {
                await backend.Initialize(site.Latitude, site.Longitude, zone, fake, InitializeTimeout, token);
            } finally {
                if (!fake) {
                    backend.SetLiveStreamDisabled(true);
                }
            }
            parkedAtPolaris = false;
            firmwareAligned = !fake;
            unparkedByPins = true;
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
                await AlignFromPlateSolves(backend, site.Latitude, site.Longitude, site.Elevation, token);
                await Poll(token);
            }
        }
    }
}
