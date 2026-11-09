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
using System.Linq;

namespace NINA.Test.Equipment.Origin {

    [TestFixture]
    public class OriginModelBuilderTest {

        [Test]
        public void GoldenSpiral_GivesTheCountAskedForWithinTheLimits() {
            var points = OriginModelBuilder.GoldenSpiral(12, 25, 80, 0, 360);

            points.Should().HaveCount(12);
            points.Should().OnlyContain(p => p.Altitude > 25 && p.Altitude < 80 && p.Azimuth >= 0 && p.Azimuth < 360);
        }

        /// <summary>Each point stands for an equal area: the sines of the altitudes are evenly spaced, so there are fewer points high up where the sky is smaller.</summary>
        [Test]
        public void GoldenSpiral_SpacesThePointsByEqualArea() {
            var points = OriginModelBuilder.GoldenSpiral(10, 20, 80, 0, 360);
            var sines = points.Select(p => Math.Sin(p.Altitude * Math.PI / 180)).ToList();
            var steps = sines.Zip(sines.Skip(1), (a, b) => b - a).ToList();

            steps.Should().OnlyContain(s => Math.Abs(s - steps[0]) < 1e-9);
            sines[0].Should().BeApproximately(Math.Sin(20 * Math.PI / 180) + steps[0] / 2, 1e-9, "each point sits in the middle of its band");
        }

        /// <summary>Consecutive points are far apart in azimuth, so that the points of any altitude band are spread round the sky.</summary>
        [Test]
        public void GoldenSpiral_SpreadsTheAzimuthsRoundTheSky() {
            var points = OriginModelBuilder.GoldenSpiral(12, 25, 80, 0, 360);
            var sorted = points.Select(p => p.Azimuth).OrderBy(a => a).ToList();
            var gaps = sorted.Zip(sorted.Skip(1), (a, b) => b - a).Append(360 - sorted[^1] + sorted[0]);

            gaps.Max().Should().BeLessThan(60, "no quarter of the sky is left out");
        }

        /// <summary>An azimuth span from a larger number to a smaller one goes through north: 300 to 60 is the northern third of the sky.</summary>
        [Test]
        public void GoldenSpiral_AnAzimuthSpanCanGoThroughNorth() {
            var points = OriginModelBuilder.GoldenSpiral(20, 25, 80, 300, 60);

            points.Should().OnlyContain(p => p.Azimuth >= 300 || p.Azimuth <= 60);
        }

        [Test]
        public void GoldenSpiral_RefusesLimitsTheWrongWayRound() {
            FluentActions.Invoking(() => OriginModelBuilder.GoldenSpiral(10, 80, 25, 0, 360)).Should().Throw<ArgumentOutOfRangeException>();
            FluentActions.Invoking(() => OriginModelBuilder.GoldenSpiral(0, 25, 80, 0, 360)).Should().Throw<ArgumentOutOfRangeException>();
        }

        [Test]
        public void Options_ResetToDefaults() {
            var options = new OriginModelBuilderOptions { MinPointAltitude = 5, GoldenSpiralStarCount = 99 };

            options.ResetDefaults();

            options.MinPointAltitude.Should().Be(25);
            options.MaxPointAltitude.Should().Be(80);
            options.GoldenSpiralStarCount.Should().Be(12);
        }
    }
}
