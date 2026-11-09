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
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;

namespace NINA.Test.Equipment.Origin {

    /// <summary>
    /// PI.N.S.'s own calibration, end to end against OriginSimulator's mechanical mount: a secret level error (up to 10 degrees, any direction), a random
    /// azimuth zero, flexure and tilt-sensor offset; Gaia stars in the frames, plate solved by solve-field; the firmware's fake-initialisation frame;
    /// cables that count turns. The simulator's truth is read only after calibrating, to score it.
    ///
    ///   ORIGIN_SIM=127.0.0.1:18080 LD_LIBRARY_PATH=~/pins-app/External/linux-x64/SOFA dotnet test --filter OriginSimulatorCalibrationTest --logger "console;verbosity=detailed"
    ///
    /// Needs solve-field with the 4208-4211 indices (~/astrometry_indices). The mount moves at the real speeds, so the chain takes about a quarter of an hour.
    /// </summary>
    [TestFixture]
    [NonParallelizable]
    [Explicit("Drives OriginSimulator for minutes; set ORIGIN_SIM")]
    public class OriginSimulatorCalibrationTest {
        private static readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(10) };
        private string host;
        private int port;
        private string workDirectory;
        private OriginBackend backend;

        private static readonly (string Name, double Ra, double Dec)[] brightStars = {
            ("Deneb", 310.3580, 45.2803), ("Vega", 279.2347, 38.7837), ("Capella", 79.1723, 45.9980), ("Arcturus", 213.9153, 19.1824), ("Alkaid", 206.8852, 49.3133),
            ("Dubhe", 165.9320, 61.7510), ("Regulus", 152.0930, 11.9672), ("Pollux", 116.3290, 28.0262), ("Castor", 113.6495, 31.8883), ("Procyon", 114.8255, 5.2250),
            ("Betelgeuse", 88.7929, 7.4071), ("Aldebaran", 68.9802, 16.5093), ("Mirfak", 51.0807, 49.8612), ("Alpheratz", 2.0969, 29.0904), ("Altair", 297.6958, 8.8683),
            ("Kochab", 222.6764, 74.1555), ("Alioth", 193.5073, 55.9598), ("Schedar", 10.1268, 56.5373), ("Rasalhague", 263.7336, 12.5600), ("Hamal", 31.7934, 23.4624) };

        [SetUp]
        public async Task SetUp() {
            var location = OriginDiscovery.ParseHost(Environment.GetEnvironmentVariable("ORIGIN_SIM"));
            if (location == null) {
                Assert.Ignore("ORIGIN_SIM is not set");
            }
            (host, port) = location.Value;
            Assert.That(port, Is.Not.EqualTo(OriginBackend.DefaultPort), "this would drive a real telescope");
            workDirectory = Path.Combine(Path.GetTempPath(), "origin-simcal-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workDirectory);
            // the cables' neutral, as the person at the telescope would set it after unwinding them
            var state = await Sim("state");
            File.WriteAllText(Path.Combine(workDirectory, "cordwrap.json"), new JObject { ["neutral_enc0_degrees"] = state["cord"].Value<double>("neutral_enc0_deg") }.ToString());
            backend = new OriginBackend(host, port, Path.Combine(workDirectory, "logs"), Path.Combine(workDirectory, "filter"));
            (await backend.ConnectAsync()).Should().BeTrue();
            await Task.Delay(TimeSpan.FromSeconds(2));
        }

        [TearDown]
        public void TearDown() {
            backend?.Release();
        }

        private static void Report(string text) => TestContext.Progress.WriteLine($"{DateTime.Now:HH:mm:ss} {text}");

        private async Task<JObject> Sim(string what) => JObject.Parse(await http.GetStringAsync($"http://{host}:{port}/sim/{what}"));

        /// <summary>The sky with refraction from the telescope's temperature and humidity sensor, as the tube sees it.</summary>
        private async Task<SofaOriginSky> Site() {
            var s = await Sim("state");
            return SofaOriginSky.ForOrigin(backend, s.Value<double>("latitude_deg"), s.Value<double>("longitude_deg"), 0);
        }

        /// <summary>The firmware's frame: OriginSimulator's firmware does not refract.</summary>
        private async Task<SofaOriginSky> FirmwareSite() {
            var s = await Sim("state");
            return new SofaOriginSky(s.Value<double>("latitude_deg"), s.Value<double>("longitude_deg"), 0);
        }

        [Test, Order(1)]
        public async Task FakeInit_DeclaresAzimuth180AtTheTiltSensorAltitudeAndThenFollowsTheEncoders() {
            var site = await Sim("state");
            await backend.Initialize(site.Value<double>("latitude_deg"), site.Value<double>("longitude_deg"), "Europe/London", fake: true);
            await backend.QueryMount();
            var tilt = (await Sim("state")).Value<double>("tilt_sensor_deg");
            var m = backend.Data.Mount;
            Report($"after the fake initialisation: firmware alt {Deg(m.Altitude):0.00} az {Deg(m.Azimuth):0.00}, tilt sensor {tilt:0.00}");
            Deg(m.Azimuth).Should().BeApproximately(180, 0.01);
            Deg(m.Altitude).Should().BeApproximately(tilt, 0.01);
            var altitudeOffset = Deg(m.Altitude) - Deg(m.Enc1);
            var azimuthOffset = Deg(m.Azimuth) - Deg(m.Enc0);

            await backend.SlewRates(7, 7);
            await Task.Delay(TimeSpan.FromSeconds(3));
            await backend.SlewRates(0, 0);
            await Task.Delay(TimeSpan.FromSeconds(1));
            await backend.QueryMount();

            m = backend.Data.Mount;
            (Deg(m.Altitude) - Deg(m.Enc1)).Should().BeApproximately(altitudeOffset, 1e-6, "the firmware's frame is the encoders plus constant offsets");
            Math.IEEERemainder(Deg(m.Azimuth) - Deg(m.Enc0) - azimuthOffset, 360).Should().BeApproximately(0, 1e-6);
        }

        [Test, Order(2)]
        public async Task Frames_PlateSolveWhereTheTubeReallyPoints() {
            var solver = new OriginAstrometryNet();
            // the frame shows mid-exposure; the sky drifts past a still mount at up to 15"/s, so the truth is read either side and interpolated
            var before = await Sim("truth");
            var midExposure = DateTime.UtcNow + TimeSpan.FromSeconds(1);
            var frame = await backend.TakeFrame(2, 800, 1, TimeSpan.FromSeconds(60));
            var after = await Sim("truth");
            var solved = await solver.Solve(frame);

            solved.Should().NotBeNull("Gaia stars in the frame should solve");
            var f = (OriginCalibration.JulianDate(midExposure) - before.Value<double>("jd")) / (after.Value<double>("jd") - before.Value<double>("jd"));
            var ra = before.Value<double>("true_ra_deg") + f * Math.IEEERemainder(after.Value<double>("true_ra_deg") - before.Value<double>("true_ra_deg"), 360);
            var dec = before.Value<double>("true_dec_deg") + f * (after.Value<double>("true_dec_deg") - before.Value<double>("true_dec_deg"));
            var miss = Separation(solved.Value.RaDegrees, solved.Value.DecDegrees, ra, dec);
            Report($"solved {solved.Value.RaDegrees:0.000}, {solved.Value.DecDegrees:0.000}: {miss:0.00}' from the true pointing");
            miss.Should().BeLessThan(0.5);
        }

        [Test, Order(3)]
        public async Task CordWrap_MovesTakeTheLongWayAndNeverPassTheLimit() {
            var calibration = new OriginCalibration(backend, await Site(), (f, t) => throw new NotSupportedException());
            var before = await Sim("state");
            var neutral = before["cord"].Value<double>("neutral_enc0_deg");

            await calibration.MoveTo(Math.IEEERemainder(neutral + 170, 360), 5);
            // 190 the same way would be past the limit: the same place reached the long way round, at -170
            await calibration.MoveTo(Math.IEEERemainder(neutral + 190, 360), 5);

            var after = await Sim("state");
            Report($"cables: now {after["cord"].Value<double>("turned_deg"):0.0} from neutral, range {after["cord"].Value<double>("min_turned_deg"):0.0} to {after["cord"].Value<double>("max_turned_deg"):0.0}");
            after["cord"].Value<double>("turned_deg").Should().BeApproximately(-170, 0.2);
            after["cord"].Value<double>("max_turned_deg").Should().BeLessThanOrEqualTo(180);
            after["cord"].Value<double>("min_turned_deg").Should().BeGreaterThanOrEqualTo(-180);
            after["cord"].Value<int>("times_taut").Should().Be(before["cord"].Value<int>("times_taut"));
            await calibration.MoveTo(neutral, 5);
        }

        [Test, Order(4)]
        public async Task FullChain_QuickCalibrationRefinedAtEachStarThenGotoAndTrack() {
            var sky = await Site();
            var solver = new OriginAstrometryNet();
            var calibration = new OriginCalibration(backend, sky, solver.Solve, await FirmwareSite());
            var site = await Sim("state");
            await calibration.EnsureReady(site.Value<double>("latitude_deg"), site.Value<double>("longitude_deg"), "Europe/London");

            // quick start: three points about 120 degrees apart, one sweep, at two altitudes
            var started = DateTime.UtcNow;
            var model = await calibration.Calibrate(new (double, double)[] { (-120, 3.5), (0, 18.5), (120, 3.5) }, progress: new Progress<string>(Report));
            var quickMinutes = (DateTime.UtcNow - started).TotalMinutes;
            Report($"quick calibration: {calibration.Points.Count} points in {quickMinutes:0.0} min, rms {model.RmsArcmin:0.0}', base tilt {model.BaseTilt.Degrees:0.00} toward {model.BaseTilt.TowardAzimuth:0.0}");
            calibration.Points.Should().HaveCount(3);

            // refined at each star: the goto's check frame is the next point
            var stars = Spread(brightStars.Select(s => (s.Name, s.Ra, s.Dec, Horizon: sky.ToHorizon(s.Ra, s.Dec, DateTime.UtcNow)))
                                          .Where(s => s.Horizon.Altitude is > 25 and < 80).ToList(), 6);
            stars.Should().HaveCountGreaterThanOrEqualTo(4, "enough bright stars should be up");
            foreach (var s in stars) {
                var error = await calibration.GotoAndRefine(s.Ra, s.Dec);
                Report($"{s.Name} (alt {s.Horizon.Altitude:0}, az {s.Horizon.Azimuth:0}): landed {(error is double e ? $"{e:0.0}'" : "unsolved")} out; {calibration.Points.Count} points, rms {calibration.Model.RmsArcmin:0.0}'");
            }
            var refineMinutes = (DateTime.UtcNow - started).TotalMinutes - quickMinutes;

            // score against the secret
            var truth = await Sim("truth");
            var q = truth["quaternion"].Values<double>().ToArray();
            // the truth's geometry, without the periodic error, which no model of this kind can follow
            var actual = new OriginPointingModel(q[0], q[1], q[2], q[3], truth.Value<double>("altitude_zero_deg"), truth.Value<double>("flexure_deg"), truth.Value<double?>("cone_arcmin") / 60 ?? 0);
            var fitted = calibration.Model;
            Report($"truth: tilt {truth.Value<double>("tilt_deg"):0.00} toward {truth.Value<double>("tilt_toward_deg"):0.0}, altitude zero {truth.Value<double>("altitude_zero_deg"):0.00}, flexure {truth.Value<double>("flexure_deg"):0.00}, tilt sensor {truth.Value<double>("tilt_sensor_bias_deg"):+0.00;-0.00}");
            Report($"truth: cone {truth.Value<double?>("cone_arcmin") ?? 0:+0.0;-0.0}', periodic error {truth["periodic_error_arcsec"]?.ToString(Newtonsoft.Json.Formatting.None)}\"");
            Report($"fit:   tilt {fitted.BaseTilt.Degrees:0.00} toward {fitted.BaseTilt.TowardAzimuth:0.0}, altitude zero {fitted.AltitudeZero:0.00}, flexure {fitted.Flexure:0.00}, cone {fitted.Cone * 60:+0.0;-0.0}', rms {fitted.RmsArcmin:0.0}', {fitted.Points} points");
            var worst = WorstMiss(actual, fitted);
            Report($"worst pointing error over the sky: {worst:0.0}'; quick calibration {quickMinutes:0.0} min, refinement {refineMinutes:0.0} min");
            fitted.BaseTilt.Degrees.Should().BeApproximately(truth.Value<double>("tilt_deg"), 0.2);
            if (truth.Value<double>("tilt_deg") > 1) {
                Math.Abs(Math.IEEERemainder(fitted.BaseTilt.TowardAzimuth - truth.Value<double>("tilt_toward_deg"), 360)).Should().BeLessThan(3);
            }
            worst.Should().BeLessThan(5, "the refined model should point anywhere in the sky to within a few arcminutes");

            // a goto with the refined model, checked by a plate solve
            var target = stars[0];
            await calibration.Goto(target.Ra, target.Dec);
            var gotoMiss = await OffTarget(solver, target.Ra, target.Dec);
            Report($"goto {target.Name} with the refined model: {gotoMiss:0.0}' from the target");
            gotoMiss.Should().BeLessThan(5);

            // a track by the model, checked every minute for three minutes
            await calibration.Track(target.Ra, target.Dec, TimeSpan.FromMinutes(5));
            for (var minute = 1; minute <= 3; minute++) {
                await Task.Delay(TimeSpan.FromSeconds(60));
                var miss = await OffTarget(solver, target.Ra, target.Dec);
                Report($"tracking {target.Name} +{minute} min: {miss:0.0}' from the target");
                miss.Should().BeLessThan(5);
            }
            await backend.AbortMotion();

            // the cables throughout
            var cord = (await Sim("state"))["cord"];
            Report($"cables: range {cord.Value<double>("min_turned_deg"):0.0} to {cord.Value<double>("max_turned_deg"):0.0} from neutral, past the limit {cord.Value<int>("times_taut")} times");
            cord.Value<double>("max_turned_deg").Should().BeLessThanOrEqualTo(OriginCordWrap.DefaultLimit + OriginCordWrap.TrackOverrun);
            cord.Value<double>("min_turned_deg").Should().BeGreaterThanOrEqualTo(-OriginCordWrap.DefaultLimit - OriginCordWrap.TrackOverrun);

            // a knock (or a slipping clutch): the next star's solve is far from the model, and is turned away
            await Sim("slip?degrees=20");
            try {
                var next = stars[1];
                var afterKnock = await calibration.GotoAndRefine(next.Ra, next.Dec);
                Report($"after a 20 degree azimuth slip, {next.Name}: {(afterKnock is double k ? $"{k:0.0}' (accepted)" : "turned away")}");
                afterKnock.Should().BeNull("a point 20 degrees from the model means the mechanics moved");
            } finally {
                await Sim("slip?degrees=-20");
            }
        }

        [Test, Order(5)]
        public async Task MovingTarget_FollowedThenCarriedPastTheCordLimitAndBackTheLongWay() {
            var sky = await Site();
            var solver = new OriginAstrometryNet();
            var calibration = new OriginCalibration(backend, sky, solver.Solve, await FirmwareSite());
            var site = await Sim("state");
            await calibration.EnsureReady(site.Value<double>("latitude_deg"), site.Value<double>("longitude_deg"), "Europe/London");
            var model = await calibration.Calibrate(new (double, double)[] { (-120, 3.5), (0, 18.5), (120, 3.5) });
            Report($"quick calibration: {calibration.Points.Count} points, rms {model.RmsArcmin:0.0}'");

            // a comet-like mover, 0.05 degrees a minute in RA and -0.03 in Dec, from a star well up: plate solves against the ephemeris at mid-exposure. Much faster
            // and a 2 s frame trails the stars past solving (0.5 degrees a minute is 60" in 2 s), as a real camera would.
            var star = brightStars.Select(b => (b.Name, b.Ra, b.Dec, Horizon: sky.ToHorizon(b.Ra, b.Dec, DateTime.UtcNow)))
                                  .Where(b => b.Horizon.Altitude is > 40 and < 70).MaxBy(b => b.Horizon.Altitude);
            var t0 = DateTime.UtcNow;
            (double, double) Mover(DateTime t) => (star.Ra + 0.05 * (t - t0).TotalMinutes, star.Dec - 0.03 * (t - t0).TotalMinutes);
            // The quick model leaves a steady offset of some arcminutes (worst high up, before flexure and cone are fitted); what the track must do is follow
            // the target: a track that did not follow it would fall behind by about 3' a minute. So the offset (east, north, arcminutes) must stay put.
            await calibration.Track(Mover, TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(15));
            (double East, double North)? first = null;
            for (var minute = 1; minute <= 3; minute++) {
                await Task.Delay(TimeSpan.FromSeconds(60));
                var midExposure = DateTime.UtcNow + TimeSpan.FromSeconds(1);
                var frame = await backend.TakeFrame(2, 800, 1, TimeSpan.FromSeconds(60));
                var solved = await solver.Solve(frame);
                solved.Should().NotBeNull("the field should plate solve");
                var (ra, dec) = Mover(midExposure);
                var offset = OriginTrackGuider.Offset((ra, dec), solved.Value);
                offset = (offset.East / 60, offset.North / 60);
                first ??= offset;
                var drift = Math.Sqrt(Math.Pow(offset.East - first.Value.East, 2) + Math.Pow(offset.North - first.Value.North, 2));
                Report($"moving target near {star.Name}, +{minute} min: {offset.East:+0.0;-0.0}' east, {offset.North:+0.0;-0.0}' north of the ephemeris; {drift:0.0}' from the first");
                Math.Sqrt(offset.East * offset.East + offset.North * offset.North).Should().BeLessThan(15, "within the quick model's error");
                drift.Should().BeLessThan(1.5, "the track should follow the target, which moves about 3' a minute");
            }
            await backend.AbortMotion();

            // a target moving outward in azimuth from 172 degrees past cable-neutral: followed past the limit to the end, then back the long way
            var neutral = (await Sim("state"))["cord"].Value<double>("neutral_enc0_deg");
            var startEnc0 = Math.IEEERemainder(neutral + 172, 360);
            var enc1 = model.ToEncoders(45, 180).Enc1;
            var (alt0, az0) = model.ToSky(startEnc0, enc1);
            var outward = Math.IEEERemainder(model.ToEncoders(alt0, az0 + 1).Enc0 - startEnc0, 360) > 0 ? 1 : -1;
            var t1 = DateTime.UtcNow + TimeSpan.FromSeconds(45);
            (double, double) Outward(DateTime t) => sky.ToJ2000(alt0, OriginPointingModel.Norm360(az0 + outward * 2.0 * (t - t1).TotalMinutes), t);
            await calibration.Track(Outward, TimeSpan.FromMinutes(8), TimeSpan.FromSeconds(15));
            Report($"tracking outward from {(await Sim("state"))["cord"].Value<double>("turned_deg"):0.0} degrees from cable-neutral");
            for (var waited = 0; waited < 600 && (await Sim("state")).Value<string>("mode") == "TRACK"; waited += 10) {
                await Task.Delay(TimeSpan.FromSeconds(10));
            }
            var afterTrack = (await Sim("state"))["cord"];
            Report($"the track ended at {afterTrack.Value<double>("turned_deg"):0.0} degrees from cable-neutral (furthest {afterTrack.Value<double>("max_turned_deg"):0.0})");
            afterTrack.Value<double>("max_turned_deg").Should().BeGreaterThan(OriginCordWrap.DefaultLimit, "the observation is finished past the limit");
            afterTrack.Value<double>("max_turned_deg").Should().BeLessThanOrEqualTo(OriginCordWrap.DefaultLimit + OriginCordWrap.TrackOverrun);

            await calibration.MoveTo(neutral, 5);
            var back = (await Sim("state"))["cord"];
            Report($"back at {back.Value<double>("turned_deg"):0.0} degrees; range {back.Value<double>("min_turned_deg"):0.0} to {back.Value<double>("max_turned_deg"):0.0}");
            back.Value<double>("turned_deg").Should().BeApproximately(0, 0.5);
            back.Value<double>("max_turned_deg").Should().BeApproximately(afterTrack.Value<double>("max_turned_deg"), 0.5, "back the long way: no further out first");
            back.Value<double>("min_turned_deg").Should().BeGreaterThanOrEqualTo(-OriginCordWrap.DefaultLimit);
        }

        private async Task<double> OffTarget(OriginAstrometryNet solver, double ra, double dec) {
            var frame = await backend.TakeFrame(2, 800, 1, TimeSpan.FromSeconds(60));
            var solved = await solver.Solve(frame);
            return solved is (double r, double d) ? Separation(r, d, ra, dec) : double.PositiveInfinity;
        }

        /// <summary>Up to n stars, chosen one by one to be as far as possible in azimuth from those already chosen.</summary>
        private static List<(string Name, double Ra, double Dec, (double Altitude, double Azimuth) Horizon)> Spread(List<(string Name, double Ra, double Dec, (double Altitude, double Azimuth) Horizon)> stars, int n) {
            var chosen = new List<(string Name, double Ra, double Dec, (double Altitude, double Azimuth) Horizon)>();
            while (chosen.Count < n && chosen.Count < stars.Count) {
                var next = stars.Except(chosen).MaxBy(s => chosen.Count == 0 ? s.Horizon.Altitude : chosen.Min(c => Math.Abs(Math.IEEERemainder(c.Horizon.Azimuth - s.Horizon.Azimuth, 360))));
                chosen.Add(next);
            }
            return chosen;
        }

        private static double WorstMiss(OriginPointingModel truth, OriginPointingModel model) {
            var worst = 0.0;
            for (var enc0 = -180.0; enc0 < 180; enc0 += 10) {
                for (var enc1 = -20.0; enc1 <= 40; enc1 += 5) {
                    var a = truth.ToSky(enc0, enc1);
                    if (a.Altitude < 15 || a.Altitude > 85) {
                        continue;
                    }
                    var b = model.ToSky(enc0, enc1);
                    worst = Math.Max(worst, Separation(a.Azimuth, a.Altitude, b.Azimuth, b.Altitude));
                }
            }
            return worst;
        }

        /// <summary>Arcminutes between two directions given as (longitude, latitude) in degrees.</summary>
        private static double Separation(double lon1, double lat1, double lon2, double lat2) {
            double r(double d) => d * Math.PI / 180;
            var c = Math.Sin(r(lat1)) * Math.Sin(r(lat2)) + Math.Cos(r(lat1)) * Math.Cos(r(lat2)) * Math.Cos(r(lon1 - lon2));
            return Math.Acos(Math.Clamp(c, -1, 1)) * 180 / Math.PI * 60;
        }

        private static double Deg(double radians) => radians * 180 / Math.PI;
    }
}
