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
using NUnit.Framework;
using System;

namespace NINA.Test.Equipment.Origin {

    /// <summary>Refraction in the calibration's sky, checked against an independent formula: Saemundsson (1986), R = 1.02' cot(h + 10.3/(h + 5.11)) at 1010 hPa and 10 C.</summary>
    [TestFixture]
    public class OriginSkyTest {
        private const double Latitude = 52.2054, Longitude = 0.1218;
        private static readonly DateTime When = new(2026, 11, 8, 21, 0, 0, DateTimeKind.Utc);

        private static double Saemundsson(double geometricAltitude, double pressure, double temperature) =>
            1.02 / Math.Tan((geometricAltitude + 10.3 / (geometricAltitude + 5.11)) * Math.PI / 180) * (pressure / 1010) * (283 / (273 + temperature));

        // Vega at 33 degrees in the west, Altair at 22 in the south-west, Diphda at 20 in the south (2026-11-08 21:00 UTC, Cambridge)
        [TestCase(279.2347, 38.7837)]
        [TestCase(297.6958, 8.8683)]
        [TestCase(10.8974, -17.9866)]
        public void ToHorizon_LiftsAStarByTheRefractionAtItsAltitude(double ra, double dec) {
            var geometric = new SofaOriginSky(Latitude, Longitude, 0);
            var refracted = new SofaOriginSky(Latitude, Longitude, 0, () => (10, 50));

            var g = geometric.ToHorizon(ra, dec, When);
            var r = refracted.ToHorizon(ra, dec, When);

            g.Altitude.Should().BeGreaterThan(8, "the test stars should be up");
            ((r.Altitude - g.Altitude) * 60).Should().BeApproximately(Saemundsson(g.Altitude, SofaOriginSky.StandardPressure(0), 10), 0.15);
            (Math.IEEERemainder(r.Azimuth - g.Azimuth, 360) * 60).Should().BeApproximately(0, 0.01, "refraction only lifts");
        }

        [Test]
        public void ToJ2000_UndoesToHorizon() {
            var sky = new SofaOriginSky(Latitude, Longitude, 20, () => (3, 90));
            var (alt, az) = sky.ToHorizon(297.6958, 8.8683, When);

            var (ra, dec) = sky.ToJ2000(alt, az, When);

            (Math.IEEERemainder(ra - 297.6958, 360) * Math.Cos(dec * Math.PI / 180) * 60).Should().BeApproximately(0, 0.02);
            ((dec - 8.8683) * 60).Should().BeApproximately(0, 0.02);
        }

        [Test]
        public void ColderAirRefractsMore() {
            var warm = new SofaOriginSky(Latitude, Longitude, 0, () => (25, 50));
            var cold = new SofaOriginSky(Latitude, Longitude, 0, () => (-5, 50));

            cold.ToHorizon(297.6958, 8.8683, When).Altitude.Should().BeGreaterThan(warm.ToHorizon(297.6958, 8.8683, When).Altitude);
        }

        [Test]
        public void StandardPressure_FallsWithHeight() {
            SofaOriginSky.StandardPressure(0).Should().BeApproximately(1013.25, 1e-9);
            SofaOriginSky.StandardPressure(1000).Should().BeApproximately(899.6, 0.5);
        }
    }
}
