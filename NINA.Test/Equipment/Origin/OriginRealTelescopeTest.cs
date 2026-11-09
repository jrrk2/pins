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
using NINA.Equipment.Equipment.Origin;
using NINA.Equipment.Model;
using NINA.Profile.Interfaces;
using NUnit.Framework;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace NINA.Test.Equipment.Origin {

    /// <summary>
    /// What only the real telescope can show. Stop OriginHeadless first (the telescope accepts one client), set ORIGIN_REAL to its address, and go from harmless to moving:
    ///
    ///   export ORIGIN_REAL=192.168.1.195
    ///   dotnet test --filter "FullyQualifiedName~OriginRealTelescopeTest" --logger "console;verbosity=detailed"     (1-3: nothing moves but the camera)
    ///   dotnet test --filter "Name=Step4_FocuserMovesAndComesBack" --logger "console;verbosity=detailed"            (the focuser moves, and returns)
    ///   ORIGIN_REAL_SLEW="ra hours,dec degrees" dotnet test --filter "Name=Step5_StopHaltsTheAxes" ...              (the mount slews: choose a clear path and keep a hand near the power)
    ///
    /// The moving steps are [Explicit], so they run only when named. Every message goes to ~/Documents/CelestronOriginLogs as usual, which is the evidence if something disagrees.
    /// </summary>
    [TestFixture]
    [NonParallelizable]
    public class OriginRealTelescopeTest {
        private string host;
        private Mock<IProfileService> profile;

        [SetUp]
        public void SetUp() {
            host = Environment.GetEnvironmentVariable("ORIGIN_REAL");
            if (string.IsNullOrEmpty(host)) {
                Assert.Ignore("ORIGIN_REAL is not set");
            }
            profile = new Mock<IProfileService> { DefaultValue = DefaultValue.Mock };
        }

        private static void Report(string text) => TestContext.Progress.WriteLine(text);

        [Test, Order(1)]
        public async Task Step1_TheLinkStaysUpOnOurPings() {
            // The telescope drops the link when pings stop, so a minute without a drop says they are good.
            var backend = OriginBackend.Acquire(host);
            try {
                (await backend.ConnectAsync()).Should().BeTrue("the telescope should accept this client: is OriginHeadless or the app still connected?");
                await Task.Delay(TimeSpan.FromSeconds(60));

                backend.IsConnected.Should().BeTrue();
                backend.EventsSince(0).Select(e => e.Value<string>("kind")).Should().NotContain("disconnected");
                var mount = backend.Data.Mount;
                Report($"aligned {mount.IsAligned}, tracking {mount.IsTracking}, battery {mount.BatteryLevel}, focuser {backend.Data.Focuser.Position}, camera {backend.Data.Environment.CameraTemperature} C, task {backend.Data.TaskController.State}");
                backend.Data.MountLastUpdate.Should().NotBeNull();
                backend.Data.FocuserLastUpdate.Should().NotBeNull("the focuser should report by itself, as in the capture");
                foreach (var e in backend.EventsSince(0)) {
                    Report($"event: {e.Value<string>("kind")}: {e.Value<string>("text")}");
                }
            } finally {
                backend.Release();
            }
        }

        [Test, Order(2)]
        public async Task Step2_TheFocuserStatesItsTravel() {
            var backend = OriginBackend.Acquire(host);
            try {
                await backend.ConnectAsync();
                var (min, max) = await backend.GetFocuserLimits();
                Report($"focuser travels {min} to {max}");
                min.Should().BeLessThan(max);
            } finally {
                backend.Release();
            }
        }

        [Test, Order(3)]
        public async Task Step3_TheCameraDeliversARawFrame() {
            var factory = new ImageDataFactoryTestUtility();
            var frameRoot = Path.Combine(Path.GetTempPath(), "origin-real-" + Guid.NewGuid().ToString("N"));
            var camera = new OriginCamera(profile.Object, factory.ImageDataFactory, factory.ExposureDataFactory, host, frameRoot: frameRoot);
            try {
                await camera.Connect(default);
                camera.Gain = 800;
                foreach (var seconds in new[] { 1.0, 2.0 }) {           // two: a change of exposure is where frames with the previous settings were seen
                    camera.StartExposure(new CaptureSequence(seconds, CaptureSequence.ImageTypes.LIGHT, null, null, 1));
                    await camera.WaitUntilExposureIsReady(default);
                    var image = await (await camera.DownloadExposure(default)).ToImageData();
                    image.Properties.Width.Should().Be(3056);
                    image.Properties.Height.Should().Be(2048);
                    image.Properties.IsBayered.Should().BeTrue();
                    var sorted = image.Data.FlatArray.OrderBy(v => v).ToArray();
                    Report($"{seconds} s: median {sorted[sorted.Length / 2]} ADU, max {sorted[^1]}");
                }
            } finally {
                camera.Disconnect();
                if (Directory.Exists(frameRoot)) {
                    Directory.Delete(frameRoot, recursive: true);
                }
            }
        }

        /// <summary>
        /// How much of the time the camera is exposing when frames are taken back to back through TakeFrame, the port of OriginMonitor's snapshot.
        /// OriginMonitor's own sequencer reaches 87-96% by starting the next exposure as soon as the previous frame is ready. Only the camera is used.
        /// </summary>
        [Test, Explicit("Takes frames for two minutes"), Order(6)]
        public async Task Step6_SnapshotDutyCycle() {
            var exposure = double.TryParse(Environment.GetEnvironmentVariable("ORIGIN_REAL_EXPOSURE"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var e) ? e : 10;
            var backend = OriginBackend.Acquire(host);
            try {
                (await backend.ConnectAsync()).Should().BeTrue();
                var clock = System.Diagnostics.Stopwatch.StartNew();
                var frames = 0;
                var longest = TimeSpan.Zero;
                while (clock.Elapsed < TimeSpan.FromMinutes(2)) {
                    var started = clock.Elapsed;
                    var frame = await backend.TakeFrame(exposure, 800, 1, TimeSpan.FromSeconds(exposure + 60));
                    frame.IsFits.Should().BeTrue();
                    frames++;
                    var took = clock.Elapsed - started;
                    longest = took > longest ? took : longest;
                }
                var cycle = clock.Elapsed.TotalSeconds / frames;
                Report($"{frames} frames of {exposure} s in {clock.Elapsed.TotalSeconds:0.0} s: {cycle:0.00} s a frame, the slowest {longest.TotalSeconds:0.00} s, duty {100 * exposure / cycle:0}%");
            } finally {
                backend.Release();
            }
        }

        [Test, Explicit("The focuser moves"), Order(4)]
        public async Task Step4_FocuserMovesAndComesBack() {
            var focuser = new OriginFocuser(host);
            await focuser.Connect(default);
            try {
                await Task.Delay(TimeSpan.FromSeconds(3));              // for a status to arrive
                var start = focuser.Position;
                start.Should().BeGreaterThan(0);
                var clock = System.Diagnostics.Stopwatch.StartNew();
                // Up as the capture's first move did (100 steps), then down, which should overshoot and come back.
                await focuser.Move(start + 100, default);
                Report($"up: {start} to {focuser.Position} in {clock.Elapsed.TotalSeconds:0.0} s");
                focuser.Position.Should().Be(start + 100);
                clock.Restart();
                await focuser.Move(start, default);
                Report($"down: back to {focuser.Position} in {clock.Elapsed.TotalSeconds:0.0} s");
                focuser.Position.Should().Be(start);
            } finally {
                focuser.Disconnect();
            }
        }

        [Test, Explicit("The mount slews: choose a clear path and keep a hand near the power"), Order(5)]
        public async Task Step5_StopHaltsTheAxes() {
            // Seen through PI.N.S. on 2026-11-07; this repeats it with the encoders: that HaltTasks and a zero Slew stop the axes in the fast part of a goto. Read the encoder, not the task state.
            var target = Environment.GetEnvironmentVariable("ORIGIN_REAL_SLEW")?.Split(',');
            if (target?.Length != 2) {
                Assert.Ignore("ORIGIN_REAL_SLEW is not set to 'ra hours,dec degrees', a long way from where the mount points");
            }
            var mount = new OriginTelescope(profile.Object, host);
            await mount.Connect(default);
            var backend = OriginBackend.Acquire(host);
            try {
                mount.AtPark.Should().BeFalse("the mount must be initialised");
                var slew = mount.SlewToCoordinates(new Coordinates(Angle.ByHours(double.Parse(target[0], System.Globalization.CultureInfo.InvariantCulture)), Angle.ByDegree(double.Parse(target[1], System.Globalization.CultureInfo.InvariantCulture)), Epoch.J2000), default);
                await Task.Delay(TimeSpan.FromSeconds(4));              // into the fast part
                var before = await Encoders(backend);
                await Task.Delay(TimeSpan.FromSeconds(1));
                var during = await Encoders(backend);
                Report($"moving at {Rate(before, during, 1):0.00} and {Rate(before, during, 2):0.00} deg/s on the two axes");
                Math.Max(Rate(before, during, 1), Rate(before, during, 2)).Should().BeGreaterThan(0.5, "or the slew had not reached its fast part, and this says nothing");

                mount.StopSlew();

                await Task.Delay(TimeSpan.FromSeconds(3));              // to decelerate
                var after1 = await Encoders(backend);
                await Task.Delay(TimeSpan.FromSeconds(1));
                var after2 = await Encoders(backend);
                Report($"after the stop: {Rate(after1, after2, 1):0.000} and {Rate(after1, after2, 2):0.000} deg/s");
                Math.Max(Rate(after1, after2, 1), Rate(after1, after2, 2)).Should().BeLessThan(0.1, "sidereal tracking is under 0.01 deg/s, and a goto over 1");
                (await slew.WaitAsync(TimeSpan.FromSeconds(30))).Should().BeFalse();
            } finally {
                backend.Release();
                mount.Disconnect();
            }
        }

        private static async Task<(DateTime At, double Enc0, double Enc1)> Encoders(OriginBackend backend) {
            await backend.QueryMount();
            return (DateTime.UtcNow, backend.Data.Mount.Enc0, backend.Data.Mount.Enc1);
        }

        private static double Rate((DateTime At, double Enc0, double Enc1) a, (DateTime At, double Enc0, double Enc1) b, int axis) {
            var radians = axis == 1 ? b.Enc0 - a.Enc0 : b.Enc1 - a.Enc1;
            // The encoder wraps at a full turn.
            radians = Math.Abs(Math.IEEERemainder(radians, 2 * Math.PI));
            return radians * 180 / Math.PI / (b.At - a.At).TotalSeconds;
        }

        /// <summary>
        /// PI.N.S.'s own alignment, to compare with the firmware's: a quick three-point calibration by encoder feedback and plate solve (solve-field and indices
        /// needed), refined by a goto to each of several bright stars, then a goto checked by a plate solve and a track checked every minute. Starts with a fake initialisation if the firmware is not ready. The cables' neutral must be set
        /// (~/.config/origin/cordwrap.json). ORIGIN_REAL_TARGET="ra degrees,dec degrees" (J2000) chooses the target, Deneb by default.
        /// </summary>
        [Test, Explicit("The mount moves all round the sky: keep a hand near the power"), Order(7)]
        public async Task Step7_OwnCalibrationGotoAndTrack() {
            const double latitude = 52.2054, longitude = 0.1218, elevation = 20;
            var target = (Environment.GetEnvironmentVariable("ORIGIN_REAL_TARGET") ?? "310.3580,45.2803").Split(',').Select(v => double.Parse(v, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            var backend = OriginBackend.Acquire(host);
            try {
                (await backend.ConnectAsync()).Should().BeTrue();
                backend.CordWrap.HasNeutral.Should().BeTrue("the cables' neutral must be known before the mount goes all round");
                await Task.Delay(TimeSpan.FromSeconds(3));
                var task = backend.Data.TaskController;
                Report($"{DateTime.Now:HH:mm:ss} ready {task.IsReady} (reported {task.IsReadyReported}), task {task.State}, azimuth {backend.CordWrap.Turned:0.0} from neutral, battery {backend.Data.Mount.BatteryLevel}");
                // the tube's sky with refraction from the telescope's sensor; the firmware's frame without, as far as is known
                var sky = SofaOriginSky.ForOrigin(backend, latitude, longitude, elevation);
                var solver = new OriginAstrometryNet();
                var calibration = new OriginCalibration(backend, sky, solver.Solve, new SofaOriginSky(latitude, longitude, elevation));
                await calibration.EnsureReady(latitude, longitude, "Europe/London");

                // the quick start: three points about 120 degrees apart in one sweep, at two altitudes
                var plan = new (double, double)[] { (-120, 3.5), (0, 18.5), (120, 3.5) };
                var started = DateTime.UtcNow;
                var model = await calibration.Calibrate(plan, progress: new Progress<string>(text => Report($"{DateTime.Now:HH:mm:ss} {text}, azimuth {backend.CordWrap.Turned:0.0} from neutral")));
                Report($"{DateTime.Now:HH:mm:ss} quick calibration in {(DateTime.UtcNow - started).TotalMinutes:0.0} min: {calibration.Points.Count} of {plan.Length} points, rms {model.RmsArcmin:0.0}', altitude zero {model.AltitudeZero:0.00}, base tilt {model.BaseTilt.Degrees:0.0} toward {model.BaseTilt.TowardAzimuth:0}");
                var output = Environment.GetEnvironmentVariable("ORIGIN_REAL_OUT");

                // refined at each target: the goto's check frame is the next point
                var stars = new (string Name, double Ra, double Dec)[] {
                    ("Deneb", 310.3580, 45.2803), ("Capella", 79.1723, 45.9980), ("Alpheratz", 2.0969, 29.0904), ("Mirfak", 51.0807, 49.8612), ("Vega", 279.2347, 38.7837), ("Alkaid", 206.8852, 49.3133) };
                foreach (var star in stars) {
                    try {
                        var error = await calibration.GotoAndRefine(star.Ra, star.Dec);
                        Report($"{DateTime.Now:HH:mm:ss} {star.Name}: {(error is double e ? $"landed {e:0.0}' out" : "no solve or turned away")}; now {calibration.Points.Count} points, rms {calibration.Model.RmsArcmin:0.0}', flexure {calibration.Model.Flexure:0.00}");
                    } catch (OriginException ex) {
                        Report($"{DateTime.Now:HH:mm:ss} {star.Name}: skipped: {ex.Message}");
                    }
                }
                foreach (var p in calibration.Points) {
                    Report($"  Enc0 {p.Enc0,8:0.00} Enc1 {p.Enc1,7:0.00} -> alt {p.Altitude,6:0.00} az {p.Azimuth,7:0.00}, residual {calibration.Model.ResidualArcmin(p):0.0}'");
                }
                if (output != null) {
                    calibration.Model.Save(Path.Combine(output, $"model-{DateTime.Now:HHmm}.json"));
                }

                await calibration.Goto(target[0], target[1]);
                Report($"{DateTime.Now:HH:mm:ss} goto with the refined model: {await OffTarget(backend, solver, target)}");

                await calibration.Track(target[0], target[1], TimeSpan.FromMinutes(12));
                Report($"{DateTime.Now:HH:mm:ss} tracking");
                for (var minute = 1; minute <= 10; minute++) {
                    await Task.Delay(TimeSpan.FromSeconds(60));
                    Report($"{DateTime.Now:HH:mm:ss} track +{minute} min: {await OffTarget(backend, solver, target)}, azimuth {backend.CordWrap.Turned:0.0} from neutral, task {backend.Data.TaskController.State}");
                }
            } finally {
                backend.Release();
            }
        }

        private static async Task<string> OffTarget(OriginBackend backend, OriginAstrometryNet solver, double[] target) {
            var frame = await backend.TakeFrame(2, 800, 1, TimeSpan.FromSeconds(60));
            var solved = await solver.Solve(frame);
            if (solved is not (double ra, double dec)) {
                return "no solve";
            }
            double r(double d) => d * Math.PI / 180;
            var cos = Math.Sin(r(dec)) * Math.Sin(r(target[1])) + Math.Cos(r(dec)) * Math.Cos(r(target[1])) * Math.Cos(r(ra - target[0]));
            return $"field centre {Math.Acos(Math.Min(1, cos)) * 180 / Math.PI * 60:0.0}' from the target";
        }
    }
}
