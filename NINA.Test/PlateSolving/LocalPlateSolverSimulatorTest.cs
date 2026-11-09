#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using FluentAssertions;
using Moq;
using NINA.Astrometry;
using NINA.Core.Enum;
using NINA.Equipment.Equipment.Origin;
using NINA.PlateSolving;
using NINA.Profile.Interfaces;
using NUnit.Framework;
using System;
using System.IO;
using System.Threading.Tasks;

namespace NINA.Test.PlateSolving {

    /// <summary>
    /// N.I.N.A.'s own plate solve path, the one the web page's Solve button and centring use, with the Local solver: astrometry.net's
    /// solve-field run directly on Linux. It solves a frame from OriginSimulator and compares with where the simulator points.
    ///
    ///   ORIGIN_SIM=127.0.0.1:18080 dotnet test --filter LocalPlateSolverSimulatorTest
    /// </summary>
    [TestFixture]
    [Explicit("Needs OriginSimulator and astrometry.net with an index; set ORIGIN_SIM")]
    [NonParallelizable]
    public class LocalPlateSolverSimulatorTest {

        [Test]
        public async Task LocalSolverSolvesAnOriginFrameOnLinux() {
            var location = OriginDiscovery.ParseHost(Environment.GetEnvironmentVariable("ORIGIN_SIM"));
            if (location == null) {
                Assert.Ignore("ORIGIN_SIM is not set");
            }
            if (OperatingSystem.IsWindows()) {
                Assert.Ignore("the native solve-field path is for Linux");
            }
            var (host, port) = location.Value;
            var settings = new Mock<IPlateSolveSettings>();
            settings.SetupAllProperties();
            settings.Object.PlateSolverType = PlateSolverEnum.LOCAL;
            settings.Object.CygwinLocation = "unused";

            var backend = OriginBackend.Acquire(host, port);
            var path = Path.Combine(Path.GetTempPath(), $"origin-local-solve-{Guid.NewGuid():N}.fits");
            try {
                (await backend.ConnectAsync()).Should().BeTrue();
                var frame = await backend.TakeFrame(2, 800, 1, TimeSpan.FromSeconds(60));
                await File.WriteAllBytesAsync(path, frame.Data);
                var image = await new ImageDataFactoryTestUtility().ImageDataFactory.CreateFromFile(path, 16, false, RawConverterEnum.FREEIMAGE);

                var solver = PlateSolverFactory.GetPlateSolver(settings.Object);
                // the Origin: 335 mm focal length, 2.4 micron pixels; a blind solve, as with no telescope coordinates
                var result = await solver.SolveAsync(image, new PlateSolveParameter { FocalLength = 335, PixelSize = 2.4, Binning = 1, MaxObjects = 500 }, null, default);

                result.Success.Should().BeTrue("solve-field should solve a simulator frame");
                // where the simulated tube really points (the firmware's own Ra and Dec mean nothing before an alignment)
                using var http = new System.Net.Http.HttpClient();
                var sim = Newtonsoft.Json.Linq.JObject.Parse(await http.GetStringAsync($"http://{host}:{port}/sim/truth"));
                var (ra, dec) = (sim.Value<double>("true_ra_deg"), sim.Value<double>("true_dec_deg"));
                var truth = new Coordinates(Angle.ByDegree(ra), Angle.ByDegree(dec), Epoch.J2000);
                var off = result.Coordinates.Transform(Epoch.J2000) - truth;
                TestContext.Progress.WriteLine($"solved RA {result.Coordinates.RADegrees:0.000} Dec {result.Coordinates.Dec:0.000}, scale {result.Pixscale:0.000} arcsec/px, the tube points at RA {ra:0.000} Dec {dec:0.000}: {off.Distance.ArcMinutes:0.0} arcmin apart");
                off.Distance.Degree.Should().BeLessThan(1, "the solve should agree with where the tube points");
            } finally {
                File.Delete(path);
                backend.Release();
            }
        }
    }
}
