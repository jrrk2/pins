#region "copyright"

/*
    Copyright © 2026 the PI.N.S. contributors

    This file is part of PI 'N' Stars.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using FluentAssertions;
using Moq;
using NINA.Astrometry;
using NINA.Core.Enum;
using NINA.Core.Model;
using NINA.Equipment.Equipment.MyGuider;
using NINA.Equipment.Equipment.MyTelescope;
using NINA.Equipment.Equipment.Origin;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Profile.Interfaces;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Test.Equipment.Origin {

    /// <summary>
    /// Dithering as PI.N.S. does it, on the Origin, against OriginSimulator. Mount Dither fails: the mount's guide input is hardware (a standalone autoguider
    /// such as an SSAG drives it directly, with no host software), no firmware pulse command is known, and PI.N.S.'s Origin driver does not implement
    /// PulseGuide, so the first pulse throws. It would pass only if such a command turned up. The plate-solve guider (OriginTrackGuider) needs no pulses.
    /// Mount Dither is N.I.N.A.'s DirectGuider, choosing its offsets and pulse guiding the
    /// mount through the telescope mediator, as a dither instruction in a sequence would. Each dither is checked by plate solving: the field should move by
    /// the offset chosen, east and north, at 1.478"/pixel. Guiding with PHD2 (an SSAG, say) sends its corrections and its dithers to the mount the same way,
    /// as pulses, so short pulses are checked too.
    ///
    ///   ORIGIN_SIM=127.0.0.1:18080 dotnet test --filter OriginSimulatorDitherTest --logger "console;verbosity=detailed"
    /// </summary>
    [TestFixture]
    [NonParallelizable]
    [Explicit("Drives OriginSimulator; set ORIGIN_SIM")]
    public class OriginSimulatorDitherTest {
        private const double PixelScale = 1.478;                 // arcsec per pixel: 2.4 um pixels at 335 mm
        private string host;
        private int port;
        private string workDirectory;
        private Mock<IProfileService> profile;
        private OriginTelescope mount;
        private OriginBackend backend;
        private Mock<ITelescopeMediator> mediator;

        [SetUp]
        public async Task SetUp() {
            var location = OriginDiscovery.ParseHost(Environment.GetEnvironmentVariable("ORIGIN_SIM"));
            if (location == null) {
                Assert.Ignore("ORIGIN_SIM is not set");
            }
            (host, port) = location.Value;
            Assert.That(port, Is.Not.EqualTo(OriginBackend.DefaultPort), "this would drive a real telescope");
            workDirectory = Path.Combine(Path.GetTempPath(), "origin-dither-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workDirectory);
            profile = new Mock<IProfileService> { DefaultValue = DefaultValue.Mock };
            profile.SetupGet(p => p.ActiveProfile.AstrometrySettings.Latitude).Returns(52.2054);
            profile.SetupGet(p => p.ActiveProfile.AstrometrySettings.Longitude).Returns(0.1218);
            profile.SetupGet(p => p.ActiveProfile.CameraSettings.PixelSize).Returns(2.4);
            profile.SetupGet(p => p.ActiveProfile.TelescopeSettings.FocalLength).Returns(335);
            // the cables' neutral, as the person at the telescope would set it
            using (var http = new System.Net.Http.HttpClient()) {
                var state = Newtonsoft.Json.Linq.JObject.Parse(await http.GetStringAsync($"http://{host}:{port}/sim/state"));
                File.WriteAllText(Path.Combine(workDirectory, "cordwrap.json"), new Newtonsoft.Json.Linq.JObject { ["neutral_enc0_degrees"] = state["cord"].Value<double>("neutral_enc0_deg") }.ToString());
            }
            mount = new OriginTelescope(profile.Object, host, port, Path.Combine(workDirectory, "logs"), Path.Combine(workDirectory, "filter")) { SettleGrace = TimeSpan.Zero };
            (await mount.Connect(default)).Should().BeTrue();
            backend = OriginBackend.Acquire(host, port);          // the mount's own link, for frames
            // the mediator, as TelescopeVM forwards to the mount
            mediator = new Mock<ITelescopeMediator>();
            mediator.Setup(m => m.PulseGuide(It.IsAny<GuideDirections>(), It.IsAny<int>())).Callback<GuideDirections, int>((d, ms) => mount.PulseGuide(d, ms));
        }

        [TearDown]
        public void TearDown() {
            backend?.Release();
            mount?.Disconnect();
        }

        private static void Report(string text) => TestContext.Progress.WriteLine($"{DateTime.Now:HH:mm:ss} {text}");

        private TelescopeInfo Info() => new() {
            Connected = mount.Connected,
            CanPulseGuide = mount.CanPulseGuide,
            GuideRateRightAscensionArcsecPerSec = mount.GuideRateRightAscensionArcsecPerSec,
            GuideRateDeclinationArcsecPerSec = mount.GuideRateDeclinationArcsecPerSec,
            IsPulseGuiding = mount.IsPulseGuiding
        };

        private async Task<(double Ra, double Dec)> Centre() {
            var frame = await backend.TakeFrame(2, 800, 1, TimeSpan.FromSeconds(60));
            var solved = await new OriginAstrometryNet().Solve(frame);
            solved.Should().NotBeNull("the frame should plate solve");
            return solved.Value;
        }

        [Test]
        public async Task PlateSolveGuider_HoldsTheTargetAndDithersByAdjustingTheTrack() {
            var site = profile.Object.ActiveProfile.AstrometrySettings;
            var sky = SofaOriginSky.ForOrigin(backend, site.Latitude, site.Longitude, 0);
            var solver = new OriginAstrometryNet();
            var calibration = new OriginCalibration(backend, sky, solver.Solve, new SofaOriginSky(site.Latitude, site.Longitude, 0));
            await calibration.EnsureReady(site.Latitude, site.Longitude, "Europe/London");
            var model = await calibration.Calibrate(new (double, double)[] { (-120, 3.5), (0, 18.5), (120, 3.5) });
            Report($"quick calibration: rms {model.RmsArcmin:0.0}'");

            // Capella, held by the guider from plate solves alone
            var offsets = new Queue<DitherOffset>(new[] { new DitherOffset(20, 0), new DitherOffset(-15, 15), new DitherOffset(5, -20) });
            var guider = new OriginTrackGuider(calibration, backend, solver.Solve, new DitherOffsetSelector(_ => offsets.Dequeue()));
            await guider.Start(_ => (79.1723, 45.9980), TimeSpan.FromMinutes(30));
            await Task.Delay(TimeSpan.FromSeconds(20));
            for (var frame = 1; frame <= 4; frame++) {
                var e = await guider.GuideOnce();
                Report($"guiding frame {frame}: {(e is var (east, north) ? $"{east:+0.0;-0.0}\" east, {north:+0.0;-0.0}\" north" : "no solve")}; correction {guider.Correction.East:+0.0;-0.0}\", {guider.Correction.North:+0.0;-0.0}\"");
            }
            var held = guider.Errors[^1];
            Math.Sqrt(held.East * held.East + held.North * held.North).Should().BeLessThan(10, "the guider should hold the target once it has learnt the correction");

            // dithers: the field moves to target + dither, and stays there
            while (offsets.Count > 0) {
                await guider.DitherBy(20, TimeSpan.FromSeconds(20));
                var e = await guider.GuideOnce();
                e.Should().NotBeNull();
                var (east, north) = e.Value;
                Report($"after a dither to {guider.Dither.East:+0.0;-0.0}\", {guider.Dither.North:+0.0;-0.0}\": field {east:+0.0;-0.0}\" east, {north:+0.0;-0.0}\" north of target + dither");
                Math.Sqrt(east * east + north * north).Should().BeLessThan(12, "the field should be at the dithered position");
            }
            await backend.AbortMotion();
        }

        [Test]
        public async Task MountDither_MovesTheFieldByTheOffsetItChose() {
            // a star well up, tracked by the firmware after its goto
            var target = new Coordinates(Angle.ByDegree(79.1723), Angle.ByDegree(45.9980), Epoch.J2000);       // Capella
            (await mount.SlewToCoordinates(target, default)).Should().BeTrue();
            await Task.Delay(TimeSpan.FromSeconds(31));                                                       // the tube settles
            Report($"mount: can pulse guide {mount.CanPulseGuide}, guide rates {mount.GuideRateRightAscensionArcsecPerSec:0.0} / {mount.GuideRateDeclinationArcsecPerSec:0.0} \"/s");

            // Mount Dither with its own selector fed a known sequence of offsets (pixels west-east, north-south), large as a dither and small as a guide correction
            var offsets = new Queue<DitherOffset>(new[] { new DitherOffset(20, 0), new DitherOffset(20, 20), new DitherOffset(-10, 5), new DitherOffset(-9, 4) });
            var guider = new DirectGuider(profile.Object, mediator.Object, new DitherOffsetSelector(_ => offsets.Dequeue()));
            guider.UpdateDeviceInfo(Info());
            (await guider.Connect(default)).Should().BeTrue();

            var before = await Centre();
            var previous = new DitherOffset(0, 0);
            while (offsets.Count > 0) {
                var next = offsets.Peek();
                guider.UpdateDeviceInfo(Info());
                (await guider.Dither(20, TimeSpan.FromSeconds(2), false, new Progress<ApplicationStatus>(), CancellationToken.None)).Should().BeTrue();
                var after = await Centre();

                // east is increasing RA; a positive west-east offset is a pulse east
                var east = Math.IEEERemainder(after.Ra - before.Ra, 360) * Math.Cos(after.Dec * Math.PI / 180) * 3600;
                var north = (after.Dec - before.Dec) * 3600;
                var wantEast = (next.WestEastPixels - previous.WestEastPixels) * PixelScale;
                var wantNorth = (next.NorthSouthPixels - previous.NorthSouthPixels) * PixelScale;
                Report($"dither to ({next.WestEastPixels}, {next.NorthSouthPixels}) px: field moved {east:+0.0;-0.0}\" east, {north:+0.0;-0.0}\" north; wanted {wantEast:+0.0;-0.0}\", {wantNorth:+0.0;-0.0}\"");
                east.Should().BeApproximately(wantEast, Math.Max(3, Math.Abs(wantEast) * 0.25));
                north.Should().BeApproximately(wantNorth, Math.Max(3, Math.Abs(wantNorth) * 0.25));
                before = after;
                previous = next;
            }
        }
    }
}
