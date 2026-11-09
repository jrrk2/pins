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

namespace NINA.Test.Equipment.Origin {

    [TestFixture]
    public class OriginPointingModelTest {

        // A mount like the one on grass on 2026-11-08: base tilted 5 degrees with the low side toward azimuth 18, azimuth zero 137 degrees round, altitude zero
        // 43.5 and flexure -0.4 degrees at the horizon.
        internal static OriginPointingModel TrueModel() {
            static double[] Mul(double[] a, double[] b) => new[] {
                a[0] * b[0] - a[1] * b[1] - a[2] * b[2] - a[3] * b[3],
                a[0] * b[1] + a[1] * b[0] + a[2] * b[3] - a[3] * b[2],
                a[0] * b[2] - a[1] * b[3] + a[2] * b[0] + a[3] * b[1],
                a[0] * b[3] + a[1] * b[2] - a[2] * b[1] + a[3] * b[0] };
            static double[] About(double x, double y, double z, double degrees) {
                var h = degrees * Math.PI / 360;
                return new[] { Math.Cos(h), x * Math.Sin(h), y * Math.Sin(h), z * Math.Sin(h) };
            }
            // tilt the vertical toward azimuth 18: a rotation about the horizontal axis at right angles to that direction
            var toward = 18 * Math.PI / 180;
            var tilt = About(-Math.Sin(toward), Math.Cos(toward), 0, 5);
            var q = Mul(tilt, About(0, 0, 1, 137));
            return new OriginPointingModel(q[0], q[1], q[2], q[3], 43.5, -0.4);
        }

        internal static List<OriginCalibrationPoint> Points(OriginPointingModel truth, double noiseArcmin, int seed = 1) {
            var random = new Random(seed);
            var points = new List<OriginCalibrationPoint>();
            foreach (var (enc0, enc1) in new[] { (-140.0, 3.5), (-95, 18.5), (-50, -10), (-5, 3.5), (40, 18.5), (85, -10), (130, 3.5), (130, 18.5), (85, 3.5) }) {
                var (alt, az) = truth.ToSky(enc0, enc1);
                var n = noiseArcmin / 60;
                points.Add(new OriginCalibrationPoint(enc0, enc1, alt + n * (random.NextDouble() * 2 - 1), az + n * (random.NextDouble() * 2 - 1) / Math.Cos(alt * Math.PI / 180)));
            }
            return points;
        }

        [Test]
        public void Fit_RecoversATiltedBaseItsZerosAndFlexure() {
            var truth = TrueModel();
            var model = OriginPointingModel.Fit(Points(truth, noiseArcmin: 0.3));

            model.RmsArcmin.Should().BeLessThan(0.5);
            model.AltitudeZero.Should().BeApproximately(43.5, 0.2);
            model.Flexure.Should().BeApproximately(-0.4, 0.15);
            model.BaseTilt.Degrees.Should().BeApproximately(5, 0.1);
            model.BaseTilt.TowardAzimuth.Should().BeApproximately(18, 2);
            foreach (var (enc0, enc1) in new[] { (0.0, 0.0), (100, 30), (-170, -15) }) {
                var a = truth.ToSky(enc0, enc1);
                var b = model.ToSky(enc0, enc1);
                var miss = Math.Acos(Math.Clamp(Dot(OriginPointingModel.Vector(a.Altitude, a.Azimuth), OriginPointingModel.Vector(b.Altitude, b.Azimuth)), -1, 1)) * 180 / Math.PI * 60;
                miss.Should().BeLessThan(1, "the model should point where the mount does, between the points too");
            }
        }

        [Test]
        public void Fit_WithoutFlexureIsWorseWhenThereIsSome() {
            var points = Points(TrueModel(), noiseArcmin: 0);
            OriginPointingModel.Fit(points, fitFlexure: false).RmsArcmin.Should().BeGreaterThan(OriginPointingModel.Fit(points).RmsArcmin + 1);
        }

        [Test]
        public void ToEncoders_UndoesToSky() {
            var model = TrueModel();
            var random = new Random(7);
            for (var i = 0; i < 200; i++) {
                var enc0 = random.NextDouble() * 360 - 180;
                var enc1 = random.NextDouble() * 60 - 20;
                var (alt, az) = model.ToSky(enc0, enc1);
                var back = model.ToEncoders(alt, az);
                back.Enc0.Should().BeApproximately(enc0, 1e-6);
                back.Enc1.Should().BeApproximately(enc1, 1e-6);
            }
        }

        [Test]
        public void Horn_FindsAnExactRotation() {
            var truth = TrueModel();
            var from = new[] { new[] { 1.0, 0, 0 }, new[] { 0.0, 1, 0 }, new[] { 0.6, 0, 0.8 } };
            var (alt, az) = truth.ToSky(0, -43.5);       // the mount's own x axis, flexure included at the horizon is small; use the rotation alone below
            var q = OriginPointingModel.Horn(from, from);
            Math.Abs(q[0]).Should().BeApproximately(1, 1e-9, "a set of vectors onto itself is no rotation");
        }

        [Test]
        public void SaveAndLoad_KeepTheModel() {
            var path = Path.Combine(Path.GetTempPath(), "origin-model-" + Guid.NewGuid().ToString("N"), "pointing.json");
            try {
                var model = OriginPointingModel.Fit(Points(TrueModel(), 0.3));
                model.Save(path);
                var loaded = OriginPointingModel.Load(path);
                loaded.ToSky(12, 7).Should().Be(model.ToSky(12, 7));
                loaded.RmsArcmin.Should().Be(model.RmsArcmin);
            } finally {
                Directory.Delete(Path.GetDirectoryName(path), recursive: true);
            }
        }

        // ---- cord wrap ----

        private static double Rad(double degrees) => degrees * Math.PI / 180;

        [Test]
        public void CordWrap_CountsTurnsAcrossTheEncodersWrap() {
            var wrap = new OriginCordWrap(null);
            wrap.Update(Rad(170));
            wrap.SetNeutralHere();
            foreach (var e in new[] { 174.0, 178, -178, -174, -170, -150 }) {     // past the encoder's +-180 seam
                wrap.Update(Rad(e));
            }
            wrap.Turned.Should().BeApproximately(40, 1e-9);
            foreach (var e in new[] { 180.0, 160, 140, 120, 100, 80, 60, 40, 20, 0 }) {
                wrap.Update(Rad(e));
            }
            wrap.Turned.Should().BeApproximately(-170, 1e-9);
            wrap.CanMove(-1, OriginCordWrap.MarginFor(6)).Should().BeTrue("at the slowest rate the last 10 degrees are still open");
            wrap.CanMove(-1, OriginCordWrap.MarginFor(9)).Should().BeFalse("at full speed a late check could carry it past 180");
            wrap.CanMove(+1, OriginCordWrap.MarginFor(9)).Should().BeTrue();
        }

        [Test]
        public void CordWrap_PlansInTheCountAndRefusesTheFarSide() {
            var wrap = new OriginCordWrap(null);
            wrap.Update(Rad(-45));
            wrap.SetNeutralHere();
            wrap.TurnedFor(130).Should().BeApproximately(175, 1e-9);
            wrap.TurnedFor(-170).Should().BeApproximately(-125, 1e-9);
            wrap.TurnedFor(140).Should().BeApproximately(-175, 1e-9, "185 degrees one way is beyond the limit, so the long way round");
            Math.Abs(wrap.TurnedFor(135)).Should().BeApproximately(180, 1e-9, "the point opposite cable-neutral is reachable");
            var everywhere = Enumerable.Range(0, 360).Select(a => wrap.TurnedFor(a - 180.0));
            everywhere.Should().OnlyContain(t => Math.Abs(t) <= 180, "every azimuth can be reached within the limit");
        }

        [Test]
        public void CordWrap_ResumesFromTheSavedNeutral() {
            var file = Path.Combine(Path.GetTempPath(), "origin-cord-" + Guid.NewGuid().ToString("N"), "cordwrap.json");
            try {
                var first = new OriginCordWrap(file);
                first.Update(Rad(10));
                first.SetNeutralHere();
                var second = new OriginCordWrap(file);
                second.HasNeutral.Should().BeTrue();
                second.Update(Rad(60));
                second.Turned.Should().BeApproximately(50, 1e-9);
            } finally {
                Directory.Delete(Path.GetDirectoryName(file), recursive: true);
            }
        }

        // ---- tracks in the firmware's frame ----

        /// <summary>A plain spherical sky without precession, enough to check that a track converts back to the encoders it was built from.</summary>
        private sealed class PlainSky : IOriginSky {
            private const double Latitude = 52.2054, Longitude = 0.1218;

            private static double Lst(DateTime utc) => OriginPointingModel.Norm360(280.46061837 + 360.98564736629 * (OriginCalibration.JulianDate(utc) - 2451545.0) + Longitude);

            public (double Altitude, double Azimuth) ToHorizon(double ra, double dec, DateTime utc) {
                double lat = Rad(Latitude), ha = Rad(Lst(utc) - ra), d = Rad(dec);
                var alt = Math.Asin(Math.Sin(lat) * Math.Sin(d) + Math.Cos(lat) * Math.Cos(d) * Math.Cos(ha));
                var az = Math.Atan2(-Math.Sin(ha) * Math.Cos(d), Math.Sin(d) * Math.Cos(lat) - Math.Cos(d) * Math.Cos(ha) * Math.Sin(lat));
                return (alt * 180 / Math.PI, OriginPointingModel.Norm360(az * 180 / Math.PI));
            }

            public (double RaDegrees, double DecDegrees) ToJ2000(double altitude, double azimuth, DateTime utc) {
                double lat = Rad(Latitude), a = Rad(altitude), z = Rad(azimuth);
                var dec = Math.Asin(Math.Sin(lat) * Math.Sin(a) + Math.Cos(lat) * Math.Cos(a) * Math.Cos(z));
                var ha = Math.Atan2(-Math.Sin(z) * Math.Cos(a), Math.Sin(a) * Math.Cos(lat) - Math.Cos(a) * Math.Cos(z) * Math.Sin(lat));
                return (OriginPointingModel.Norm360(Lst(utc) - ha * 180 / Math.PI), dec * 180 / Math.PI);
            }
        }

        [Test]
        public void Track_InTheFirmwaresFrameLeadsBackToTheModelsEncoders() {
            // The firmware's frame after a fake initialisation (2026-11-08): altitude = Enc1 + 40.377, azimuth = Enc0 + 74.616.
            var backend = new OriginBackend("127.0.0.1", 1, Path.GetTempPath(), Path.Combine(Path.GetTempPath(), "origin-" + Guid.NewGuid().ToString("N"), "filter"));
            backend.Data.Process(new JObject { ["Type"] = "Notification", ["Source"] = "Mount", ["Command"] = "GetStatus", ["Enc0"] = Rad(-45), ["Enc1"] = Rad(3), ["Alt"] = Rad(3 + 40.377), ["Azm"] = Rad(-45 + 74.616) });
            backend.CordWrap.Update(Rad(-45));
            backend.CordWrap.SetNeutralHere();
            var sky = new PlainSky();
            var calibration = new OriginCalibration(backend, sky, (f, t) => throw new NotSupportedException());
            var truth = TrueModel();
            foreach (var p in Points(truth, 0)) {
                calibration.Points.Add(p);
            }
            typeof(OriginCalibration).GetProperty(nameof(OriginCalibration.Model)).SetValue(calibration, OriginPointingModel.Fit(calibration.Points));
            var start = new DateTime(2026, 11, 8, 19, 30, 0, DateTimeKind.Utc);
            // a star high in the south-west at that time, so that its encoders are within reach of the cables
            var (vegaRa, vegaDec) = (279.2347, 38.7837);

            var track = calibration.BuildTrack(vegaRa, vegaDec, start, TimeSpan.FromMinutes(20), TimeSpan.FromSeconds(30));

            track.Should().HaveCount(41);
            for (var i = 0; i < track.Count; i++) {
                var when = start + TimeSpan.FromSeconds(30 * i);
                var (fAlt, fAz) = sky.ToHorizon(track[i].RaHours * 15, track[i].DecDegrees, when);
                var (tAlt, tAz) = sky.ToHorizon(vegaRa, vegaDec, when);
                var want = calibration.Model.ToEncoders(tAlt, tAz);
                (fAlt - 40.377).Should().BeApproximately(want.Enc1, 1e-6);
                Math.IEEERemainder(fAz - 74.616 - want.Enc0, 360).Should().BeApproximately(0, 1e-6);
                track[i].JulianDate.Should().BeApproximately(OriginCalibration.JulianDate(when), 1e-9);
            }
        }

        [Test]
        public void Track_CarriesOnPastTheLimitUpToTheOverrun() {
            var (vegaRa, vegaDec) = (279.2347, 38.7837);
            var start = new DateTime(2026, 11, 8, 19, 30, 0, DateTimeKind.Utc);
            var sky = new PlainSky();
            var model = OriginPointingModel.Fit(Points(TrueModel(), 0).ToList());
            var first = model.ToEncoders(sky.ToHorizon(vegaRa, vegaDec, start).Altitude, sky.ToHorizon(vegaRa, vegaDec, start).Azimuth).Enc0;
            var later = model.ToEncoders(sky.ToHorizon(vegaRa, vegaDec, start.AddMinutes(20)).Altitude, sky.ToHorizon(vegaRa, vegaDec, start.AddMinutes(20)).Azimuth).Enc0;
            var direction = Math.Sign(Math.IEEERemainder(later - first, 360));
            direction.Should().NotBe(0);
            // cable-neutral placed so that the track starts 178 degrees out and moves further out
            var calibration = CalibrationWithNeutral(first - direction * 178, model, sky);

            var full = calibration.BuildTrack(vegaRa, vegaDec, start, TimeSpan.FromMinutes(20), TimeSpan.FromSeconds(30));
            full.Should().HaveCount(41, "a track that reaches the limit carries on to the end of the observation");
            var cut = calibration.BuildTrack(vegaRa, vegaDec, start, TimeSpan.FromHours(10), TimeSpan.FromSeconds(30));
            cut.Count.Should().BeLessThan(1201, "it stops at the overrun");
            // in the count the last point is past the limit but within the overrun, and the next would be beyond it
            var turned = direction * 178.0;
            var previous = first;
            for (var i = 1; i < cut.Count; i++) {
                var e = model.ToEncoders(sky.ToHorizon(vegaRa, vegaDec, start.AddSeconds(30 * i)).Altitude, sky.ToHorizon(vegaRa, vegaDec, start.AddSeconds(30 * i)).Azimuth).Enc0;
                turned += Math.IEEERemainder(e - previous, 360);
                previous = e;
            }
            Math.Abs(turned).Should().BeGreaterThan(OriginCordWrap.DefaultLimit).And.BeLessThanOrEqualTo(OriginCordWrap.DefaultLimit + OriginCordWrap.TrackOverrun);
        }

        [Test]
        public void Track_OfAMovingTargetFollowsItsEphemeris() {
            // a target moving 0.5 degrees a minute in RA and -0.3 in Dec, as a fast comet or a slow satellite might
            var sky = new PlainSky();
            var model = OriginPointingModel.Fit(Points(TrueModel(), 0).ToList());
            var start = new DateTime(2026, 11, 8, 19, 30, 0, DateTimeKind.Utc);
            (double, double) Ephemeris(DateTime t) => (279.2347 + 0.5 * (t - start).TotalMinutes, 38.7837 - 0.3 * (t - start).TotalMinutes);
            var (alt0, az0) = sky.ToHorizon(279.2347, 38.7837, start);
            var calibration = CalibrationWithNeutral(model.ToEncoders(alt0, az0).Enc0, model, sky);

            var track = calibration.BuildTrack(Ephemeris, start, TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(30));

            track.Should().HaveCount(21);
            for (var i = 0; i < track.Count; i++) {
                var when = start + TimeSpan.FromSeconds(30 * i);
                var (fAlt, fAz) = sky.ToHorizon(track[i].RaHours * 15, track[i].DecDegrees, when);
                var (ra, dec) = Ephemeris(when);
                var (tAlt, tAz) = sky.ToHorizon(ra, dec, when);
                var want = model.ToEncoders(tAlt, tAz);
                (fAlt - 40.377).Should().BeApproximately(want.Enc1, 1e-6, "the firmware's frame is the encoders plus its offsets");
                Math.IEEERemainder(fAz - 74.616 - want.Enc0, 360).Should().BeApproximately(0, 1e-6);
            }
        }

        [Test]
        public void CordWrap_TurnedNearFollowsOnPastTheLimit() {
            var wrap = new OriginCordWrap(null);
            wrap.Update(Rad(-45));
            wrap.SetNeutralHere();
            wrap.TurnedNear(-45 + 190, 178).Should().BeApproximately(190, 1e-9);
            wrap.TurnedNear(-45 - 190, -178).Should().BeApproximately(-190, 1e-9);
            wrap.TurnedNear(-45 + 10, 178).Should().BeApproximately(10, 1e-9);
        }

        private static OriginCalibration CalibrationWithNeutral(double neutralEnc0, OriginPointingModel model, IOriginSky sky) {
            var dir = Path.Combine(Path.GetTempPath(), "origin-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "cordwrap.json"), new JObject { ["neutral_enc0_degrees"] = Math.IEEERemainder(neutralEnc0, 360) }.ToString());
            var backend = new OriginBackend("127.0.0.1", 1, Path.GetTempPath(), Path.Combine(dir, "filter"));
            backend.Data.Process(new JObject { ["Type"] = "Notification", ["Source"] = "Mount", ["Command"] = "GetStatus", ["Enc0"] = Rad(-45), ["Enc1"] = Rad(3), ["Alt"] = Rad(3 + 40.377), ["Azm"] = Rad(-45 + 74.616) });
            var calibration = new OriginCalibration(backend, sky, (f, t) => throw new NotSupportedException());
            typeof(OriginCalibration).GetProperty(nameof(OriginCalibration.Model)).SetValue(calibration, model);
            return calibration;
        }

        private static double Dot(double[] a, double[] b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
    }
}
