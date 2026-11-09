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
using NINA.Equipment.Equipment.Origin;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using static NINA.Test.Equipment.Origin.OriginPointingModelTest;

namespace NINA.Test.Equipment.Origin {

    /// <summary>The quick calibration and its refinement at each target: the fit's choice of flexure, Refine's rules, and Calibrate against a simulated mount.</summary>
    [TestFixture]
    public class OriginCalibrationTest {

        // ---- quick fit ----

        [Test]
        public void Fit_LeavesTheFlexureOutUntilThereAreFivePoints() {
            var points = Points(TrueModel(), 0);

            OriginPointingModel.Fit(points.Take(3).ToList()).Flexure.Should().Be(0, "three points leave too little to tell flexure from noise");
            OriginPointingModel.Fit(points.Take(4).ToList()).Flexure.Should().Be(0);
            OriginPointingModel.Fit(points.Take(5).ToList()).Flexure.Should().BeApproximately(-0.4, 0.05);
            OriginPointingModel.Fit(points.Take(3).ToList(), fitFlexure: true).Flexure.Should().NotBe(0, "asking for it still fits it");
        }

        [Test]
        public void Fit_FromThreePointsRoundTheSkyIsAUsableStart() {
            var truth = TrueModel();
            var all = Points(truth, 0.3);
            // the quick plan: about 120 degrees apart, one sweep
            var quick = new[] { all[0], all[3], all[6] }.ToList();

            var model = OriginPointingModel.Fit(quick);

            model.BaseTilt.Degrees.Should().BeApproximately(5, 0.3);
            WorstMiss(truth, model).Should().BeLessThan(30, "near enough to land a 75 by 50 arcminute field on the target, and so to refine from there");
        }

        [Test]
        public void Fit_FollowsTheValleyWhereAltitudeZeroAndFlexureTradeOff() {
            // Nine plate-solved points from OriginSimulator (2026-11-09) with a secret mount: altitude zero 40.78, flexure -0.09. The best pair for them is
            // (40.78, -0.10) at 0.33'; the coarse grid's winner (41.0, +0.1) is down the valley from it, and a fixed window round that stopped at 2.0'.
            var points = new[] { (-120.01, 3.48, 41.02, 188.17), (-0.00, 18.50, 62.34, 305.85), (120.02, 3.52, 44.48, 73.29), (89.52, 28.63, 71.25, 48.19),
                                 (-75.40, 24.58, 64.00, 227.05), (20.67, -16.77, 27.69, 329.94), (-153.25, 10.58, 47.77, 156.90), (-21.74, 10.02, 52.86, 283.98),
                                 (73.13, 7.07, 50.69, 25.62) }.Select(p => new OriginCalibrationPoint(p.Item1, p.Item2, p.Item3, p.Item4)).ToList();

            var model = OriginPointingModel.Fit(points);

            model.RmsArcmin.Should().BeLessThan(0.35);
            model.AltitudeZero.Should().BeApproximately(40.78, 0.03);
            model.Flexure.Should().BeApproximately(-0.10, 0.03);
            model.BaseTilt.Degrees.Should().BeApproximately(3.68, 0.03);
        }

        // the 2026-11-08 mount with a cone error of 6', at encoders spread round the sky and from 33 to 78 degrees up (cone error shows most high up)
        private static OriginPointingModel WithCone(double coneDegrees) {
            var t = TrueModel();
            return new OriginPointingModel(t.W, t.X, t.Y, t.Z, t.AltitudeZero, t.Flexure, coneDegrees);
        }

        private static List<OriginCalibrationPoint> HighAndLow(OriginPointingModel truth) =>
            (from enc0 in new[] { -150.0, -90, -30, 30, 90, 150 } from enc1 in new[] { -10.0, 5, 34 }
             let sky = truth.ToSky(enc0, enc1) select new OriginCalibrationPoint(enc0, enc1, sky.Altitude, sky.Azimuth)).ToList();

        [Test]
        public void Fit_RecoversTheConeError() {
            var truth = WithCone(0.1);
            var points = HighAndLow(truth);

            var model = OriginPointingModel.Fit(points);

            model.Cone.Should().BeApproximately(0.1, 0.005);
            model.RmsArcmin.Should().BeLessThan(0.05);
            WorstMiss(truth, model).Should().BeLessThan(0.2);
            OriginPointingModel.Fit(points, fitCone: false).RmsArcmin.Should().BeGreaterThan(1, "without the term a 6' cone error cannot be fitted away");
        }

        [Test]
        public void ToEncoders_UndoesToSkyWithAConeError() {
            var model = WithCone(0.3);
            foreach (var (enc0, enc1) in new[] { (0.0, 0.0), (100, 30), (-170, -15), (45, 40) }) {
                var (alt, az) = model.ToSky(enc0, enc1);

                var back = model.ToEncoders(alt, az);

                Math.IEEERemainder(back.Enc0 - enc0, 360).Should().BeApproximately(0, 1e-6);
                back.Enc1.Should().BeApproximately(enc1, 1e-6);
            }
        }

        [Test]
        public void Fit_LeavesTheConeOutUntilThereAreSixPoints() {
            var points = HighAndLow(WithCone(0.1));

            OriginPointingModel.Fit(points.Take(5).ToList()).Cone.Should().Be(0);
            OriginPointingModel.Fit(points.Take(6).ToList()).Cone.Should().NotBe(0);
        }

        // ---- the plate-solve guider ----

        [Test]
        public void Guider_LeavesSmallErrorsAloneAndMovesAgainstLargeOnes() {
            OriginTrackGuider.Correct((5, -2), (1, 2), 0.7, 3).Should().Be((5, -2), "inside the dead band");
            var next = OriginTrackGuider.Correct((5, -2), (10, -4), 0.7, 3);
            next.East.Should().BeApproximately(-2, 1e-9);
            next.North.Should().BeApproximately(0.8, 1e-9);
        }

        [Test]
        public void Guider_ShiftAndOffsetAgreeOnTheSky() {
            foreach (var dec in new[] { 0.0, 45, 75 }) {
                var p = (12.0, dec);
                var moved = OriginTrackGuider.Shift(p, 30, -20);
                var (east, north) = OriginTrackGuider.Offset(p, moved);
                east.Should().BeApproximately(30, 0.05);
                north.Should().BeApproximately(-20, 1e-6);
            }
        }

        [Test]
        public void Guider_TakesUpAConstantPointingErrorWithinAFewFrames() {
            // the model leaves the field 60" east and 40" south of where it is sent; each frame measures (where it is) - (where it should be)
            var bias = (East: 60.0, North: -40.0);
            var correction = (East: 0.0, North: 0.0);
            var errors = new List<double>();
            for (var frame = 0; frame < 8; frame++) {
                var error = (correction.East + bias.East, correction.North + bias.North);
                errors.Add(Math.Sqrt(error.Item1 * error.Item1 + error.Item2 * error.Item2));
                correction = OriginTrackGuider.Correct(correction, error, 0.7, 3);
            }
            errors[0].Should().BeApproximately(72.1, 0.1);
            errors[5].Should().BeLessThan(3, "within the dead band after a few frames");
            errors.Should().BeInDescendingOrder();
        }

        [Test]
        public void SlewTime_AllowsForBothAxesAndTheFinalSteps() {
            OriginCalibration.SlewTime(120, 20).TotalSeconds.Should().BeApproximately(30 + 5 + 12, 1e-9);
            OriginCalibration.SlewTime(0, 0).Should().Be(TimeSpan.Zero, "already there");
        }

        [Test]
        public void EncodersAt_InterpolatesBetweenReadingsAcrossTheSeam() {
            var backend = new OriginBackend("127.0.0.1", 1, Path.GetTempPath(), Path.Combine(Path.GetTempPath(), "origin-" + Guid.NewGuid().ToString("N"), "filter"));
            var t0 = new DateTime(2026, 11, 9, 20, 0, 0, DateTimeKind.Utc);
            backend.RecordEncoders(t0, 179, 10);
            backend.RecordEncoders(t0.AddSeconds(1), -179, 12);

            var mid = backend.EncodersAt(t0.AddSeconds(0.5));

            mid.Should().NotBeNull();
            Math.IEEERemainder(mid.Value.Enc0 - 180, 360).Should().BeApproximately(0, 1e-9, "the short way across the seam");
            mid.Value.Enc1.Should().BeApproximately(11, 1e-9);
            backend.EncodersAt(t0.AddSeconds(-10)).Should().BeNull("before the history");
            backend.EncodersAt(t0.AddSeconds(30)).Should().BeNull("too long after the last reading");
        }

        [Test]
        public void Fit_NeedsThreePoints() {
            var points = Points(TrueModel(), 0);

            FluentActions.Invoking(() => OriginPointingModel.Fit(points.Take(2).ToList())).Should().Throw<ArgumentException>();
        }

        // ---- refine ----

        [Test]
        public void Refine_ImprovesTheQuickModelTargetByTarget() {
            var truth = TrueModel();
            var all = Points(truth, 0.3);
            var calibration = Offline();
            foreach (var p in new[] { all[0], all[3], all[6] }) {
                calibration.Refine(p);
            }
            var quickMiss = WorstMiss(truth, calibration.Model);

            foreach (var p in new[] { all[1], all[2], all[4], all[5], all[7], all[8] }) {
                calibration.Refine(p).Should().NotBeNull();
            }

            calibration.Points.Should().HaveCount(9);
            calibration.Model.Flexure.Should().BeApproximately(-0.4, 0.15, "fitted once there are five points");
            WorstMiss(truth, calibration.Model).Should().BeLessThan(Math.Min(1.5, quickMiss / 3), "every target's solve makes it better");
        }

        [Test]
        public void Refine_ReturnsThePointingErrorBeforeTheRefit() {
            var truth = TrueModel();
            var all = Points(truth, 0);
            var calibration = Offline();
            foreach (var p in all.Take(5)) {
                calibration.Refine(p);
            }
            var expected = calibration.Model.ResidualArcmin(all[5]);

            calibration.Refine(all[5]).Should().BeApproximately(expected, 1e-9);
            calibration.Model.ResidualArcmin(all[5]).Should().BeLessThan(expected + 1e-9);
        }

        [Test]
        public void Refine_TurnsAwayAPointFarFromTheModel() {
            var all = Points(TrueModel(), 0);
            var calibration = Offline();
            foreach (var p in all.Take(5)) {
                calibration.Refine(p);
            }
            var before = calibration.Model;
            var p5 = all[5];
            // a wrong solve, or the azimuth slipping as it did on 2026-11-08 (104 degrees)
            var wild = p5 with { Azimuth = OriginPointingModel.Norm360(p5.Azimuth + 104) };

            calibration.Refine(wild).Should().BeNull();
            calibration.Points.Should().HaveCount(5);
            calibration.Model.Should().BeSameAs(before);
        }

        [Test]
        public void Refine_ANewPointReplacesAnOlderOneAtTheSamePlace() {
            var truth = TrueModel();
            var all = Points(truth, 0);
            var calibration = Offline();
            foreach (var p in all.Take(4)) {
                calibration.Refine(p);
            }
            var again = all[1] with { Altitude = all[1].Altitude + 0.5 / 60 };

            calibration.Refine(again);

            calibration.Points.Should().HaveCount(4);
            calibration.Points.Should().Contain(again).And.NotContain(all[1]);
        }

        [Test]
        public void Refine_WithoutAPointChangesNothing() {
            var calibration = Offline();

            calibration.Refine(null).Should().BeNull();
            calibration.Points.Should().BeEmpty();
            calibration.Model.Should().BeNull();
        }

        // ---- calibrate against a simulated mount ----

        [Test, NonParallelizable]
        public async Task Calibrate_SolvesWhileTheMountMovesOnAndPairsEachSolveWithItsOwnFrame() {
            var truth = TrueModel();
            using var telescope = new OriginFakeTelescope();
            var mount = new SimulatedMount(telescope);
            var plan = new (double, double)[] { (3, 2), (-2, 5), (4, -2), (0, 25), (-3, 1) };
            var dir = Path.Combine(TestContext.CurrentContext.WorkDirectory, "origin-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "cordwrap.json"), new JObject { ["neutral_enc0_degrees"] = 0.0 }.ToString());
            var backend = new OriginBackend("127.0.0.1", telescope.Port, Path.Combine(dir, "logs"), Path.Combine(dir, "filter"));
            try {
                (await backend.ConnectAsync()).Should().BeTrue();
                var solving = 0;
                var solves = 0;
                async Task<(double, double)?> Solve(OriginFrame frame, CancellationToken token) {
                    Interlocked.Increment(ref solving);
                    try {
                        var (index, enc0, enc1) = SimulatedMount.Read(frame.Data);
                        // a slow solve: it lasts until the next frame has been taken, which happens only if the mount moves on meanwhile
                        var giveUp = DateTime.UtcNow + TimeSpan.FromSeconds(30);
                        while (index + 1 < plan.Length && mount.Frames <= index + 1 && DateTime.UtcNow < giveUp) {
                            await Task.Delay(50, token);
                        }
                        Interlocked.Increment(ref solves);
                        if (enc1 > 20) {
                            return null;                                    // under cloud
                        }
                        var (alt, az) = truth.ToSky(enc0, enc1);
                        return (az, alt);                                   // FlatSky: RA is the azimuth and Dec the altitude
                    } finally {
                        Interlocked.Decrement(ref solving);
                    }
                }
                mount.WhileCapturing = () => Volatile.Read(ref solving) > 0;
                var calibration = new OriginCalibration(backend, new FlatSky(), Solve) { SettleTime = TimeSpan.FromMilliseconds(100) };

                var model = await calibration.Calibrate(plan);

                solves.Should().Be(5, "every solve finishes before Calibrate returns");
                mount.CapturedWhileSolving.Should().BeTrue("a frame should be taken while the solve before it is still running");
                calibration.Points.Should().HaveCount(4, "the point under cloud is skipped");
                foreach (var p in calibration.Points) {
                    var (alt, az) = truth.ToSky(p.Enc0, p.Enc1);
                    p.Altitude.Should().BeApproximately(alt, 1e-6, "each solve is paired with the encoders of its own frame, not those of the mount moving on");
                    p.Azimuth.Should().BeApproximately(az, 1e-6);
                }
                var wanted = plan.Where(s => s.Item2 <= 20).ToList();
                for (var i = 0; i < wanted.Count; i++) {
                    calibration.Points[i].Enc0.Should().BeApproximately(wanted[i].Item1, 0.1);
                    calibration.Points[i].Enc1.Should().BeApproximately(wanted[i].Item2, 0.1);
                }
                model.Points.Should().Be(4);
                model.Flexure.Should().Be(0);
            } finally {
                backend.Release();
            }
        }

        [Test, NonParallelizable]
        public async Task MoveTo_ComesBackFromPastTheLimitAfterATrack() {
            // a track carried on past the limit (to about +188); the way back starts out there, and must go back the long way without stopping at once
            using var telescope = new OriginFakeTelescope();
            var mount = new SimulatedMount(telescope, startEnc0: 170, startEnc1: 5);
            var dir = Path.Combine(TestContext.CurrentContext.WorkDirectory, "origin-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "cordwrap.json"), new JObject { ["neutral_enc0_degrees"] = 0.0 }.ToString());
            var backend = new OriginBackend("127.0.0.1", telescope.Port, Path.Combine(dir, "logs"), Path.Combine(dir, "filter"));
            try {
                (await backend.ConnectAsync()).Should().BeTrue();
                var calibration = new OriginCalibration(backend, new FlatSky(), (f, t) => throw new NotSupportedException()) { SettleTime = TimeSpan.FromMilliseconds(100) };
                await calibration.Encoders();
                await backend.SlewRates(0, 8);                 // as the firmware would, following a track outward
                await Task.Delay(TimeSpan.FromSeconds(9));
                await backend.SlewRates(0, 0);
                await calibration.Encoders();
                backend.CordWrap.Turned.Should().BeGreaterThan(185);
                var furthest = backend.CordWrap.Turned;

                await calibration.MoveTo(0, 5);

                backend.CordWrap.Turned.Should().BeApproximately(0, 0.1);
                mount.MaxEnc0.Should().BeLessThan(furthest + 1, "back the long way: not further out first");
            } finally {
                backend.Release();
            }
        }

        private static OriginCalibration Offline() => new(
            new OriginBackend("127.0.0.1", 1, Path.GetTempPath(), Path.Combine(Path.GetTempPath(), "origin-" + Guid.NewGuid().ToString("N"), "filter")),
            new FlatSky(), (f, t) => throw new NotSupportedException());

        /// <summary>The worst miss of a model against the truth over the reachable sky, arcminutes.</summary>
        private static double WorstMiss(OriginPointingModel truth, OriginPointingModel model) {
            var worst = 0.0;
            for (var enc0 = -180.0; enc0 < 180; enc0 += 15) {
                for (var enc1 = -20.0; enc1 <= 35; enc1 += 5) {
                    var a = truth.ToSky(enc0, enc1);
                    var b = model.ToSky(enc0, enc1);
                    var u = OriginPointingModel.Vector(a.Altitude, a.Azimuth);
                    var v = OriginPointingModel.Vector(b.Altitude, b.Azimuth);
                    worst = Math.Max(worst, Math.Acos(Math.Clamp(u[0] * v[0] + u[1] * v[1] + u[2] * v[2], -1, 1)) * 180 / Math.PI * 60);
                }
            }
            return worst;
        }

        /// <summary>A sky with no time in it: RA is the azimuth and Dec the altitude, so that solves need no clock.</summary>
        private sealed class FlatSky : IOriginSky {

            public (double Altitude, double Azimuth) ToHorizon(double raDegrees, double decDegrees, DateTime utc) => (decDegrees, OriginPointingModel.Norm360(raDegrees));

            public (double RaDegrees, double DecDegrees) ToJ2000(double altitude, double azimuth, DateTime utc) => (azimuth, altitude);
        }

        /// <summary>
        /// The mount behind the fake telescope: Slew sets the rates, which move the encoders at the speeds measured on 2026-11-07, and each frame carries the encoders
        /// at the moment it was taken.
        /// </summary>
        private sealed class SimulatedMount {
            private static readonly Dictionary<int, double> speeds = new() { [9] = 4.0, [8] = 2.0, [7] = 0.97, [6] = 0.27, [5] = 0.068, [4] = 0.033, [3] = 0.01, [2] = 0.004, [1] = 0.002, [0] = 0 };
            private readonly object sync = new();
            private readonly OriginFakeTelescope telescope;
            private double enc0, enc1;
            private int altRate, azmRate;
            private DateTime last = DateTime.UtcNow;
            private int frames;

            public SimulatedMount(OriginFakeTelescope telescope, double startEnc0 = 0, double startEnc1 = 0) {
                this.telescope = telescope;
                (enc0, enc1) = (startEnc0, startEnc1);
                telescope.Handlers["Slew"] = command => {
                    lock (sync) {
                        Advance();
                        altRate = command.Value<int>("AltRate");
                        azmRate = command.Value<int>("AzmRate");
                    }
                    return Ok();
                };
                telescope.Handlers["GetStatus"] = command => {
                    if (command.Value<string>("Destination") != "Mount") {
                        return Ok();
                    }
                    lock (sync) {
                        Advance();
                        var status = (JObject)telescope.MountStatus.DeepClone();
                        status["Enc0"] = enc0 * Math.PI / 180;
                        status["Enc1"] = enc1 * Math.PI / 180;
                        status["ErrorCode"] = 0;
                        return status;
                    }
                };
                telescope.Handlers["RunSampleCapture"] = command => {
                    string file;
                    lock (sync) {
                        Advance();
                        file = $"Images/Temp/cal{frames++}.fits";
                        var header = OriginFakeTelescope.FitsHeader(command.Value<double>("ExposureTime"), command.Value<int>("ISO"));
                        telescope.Files[file] = header.Concat(Encoding.ASCII.GetBytes(string.Create(CultureInfo.InvariantCulture, $"ENC={frames - 1},{enc0:R},{enc1:R};"))).ToArray();
                    }
                    if (WhileCapturing?.Invoke() == true) {
                        CapturedWhileSolving = true;
                    }
                    telescope.CaptureResults.Enqueue(file);
                    return Ok();
                };
            }

            public Func<bool> WhileCapturing { get; set; }

            public bool CapturedWhileSolving { get; private set; }

            public int Frames {
                get {
                    lock (sync) {
                        return frames;
                    }
                }
            }

            /// <summary>The frame's number and the encoders when it was taken.</summary>
            public static (int Index, double Enc0, double Enc1) Read(byte[] frame) {
                var text = Encoding.ASCII.GetString(frame, 2880, frame.Length - 2880);
                var values = text.Substring(4, text.IndexOf(';') - 4).Split(',');
                return (int.Parse(values[0], CultureInfo.InvariantCulture), double.Parse(values[1], CultureInfo.InvariantCulture), double.Parse(values[2], CultureInfo.InvariantCulture));
            }

            public double MaxEnc0 { get; private set; } = double.MinValue;

            private void Advance() {
                var now = DateTime.UtcNow;
                var seconds = (now - last).TotalSeconds;
                last = now;
                enc0 += Math.Sign(azmRate) * speeds[Math.Abs(azmRate)] * seconds;
                MaxEnc0 = Math.Max(MaxEnc0, enc0);
                enc1 += Math.Sign(altRate) * speeds[Math.Abs(altRate)] * seconds;
            }

            private static JObject Ok() => new() { ["ErrorCode"] = 0, ["ErrorMessage"] = string.Empty };
        }
    }
}
