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
using NINA.Profile.Interfaces;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;

namespace NINA.Test.Equipment.Origin {

    /// <summary>
    /// The model builder as Touch-N-Stars' screens drive it, against OriginSimulator's mechanical mount with a secret level error: Unpark (pressed twice, as
    /// on the web page), a golden spiral of points, the build, a goto by the model, delete the worst point, save, clear, load and delete a model, then Park.
    ///
    ///   ORIGIN_SIM=127.0.0.1:18080 dotnet test --filter OriginSimulatorModelBuilderTest --logger "console;verbosity=detailed"
    /// </summary>
    [TestFixture]
    [NonParallelizable]
    [Explicit("Drives OriginSimulator for minutes; set ORIGIN_SIM")]
    public class OriginSimulatorModelBuilderTest {
        private static readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(10) };
        private string host;
        private int port;
        private string workDirectory;
        private Mock<IProfileService> profile;

        private static readonly (string Name, double Ra, double Dec)[] brightStars = {
            ("Deneb", 310.3580, 45.2803), ("Vega", 279.2347, 38.7837), ("Capella", 79.1723, 45.9980), ("Arcturus", 213.9153, 19.1824), ("Alkaid", 206.8852, 49.3133),
            ("Dubhe", 165.9320, 61.7510), ("Regulus", 152.0930, 11.9672), ("Pollux", 116.3290, 28.0262), ("Mirfak", 51.0807, 49.8612), ("Altair", 297.6958, 8.8683),
            ("Alioth", 193.5073, 55.9598), ("Schedar", 10.1268, 56.5373) };

        [SetUp]
        public async Task SetUp() {
            var location = OriginDiscovery.ParseHost(Environment.GetEnvironmentVariable("ORIGIN_SIM"));
            if (location == null) {
                Assert.Ignore("ORIGIN_SIM is not set");
            }
            (host, port) = location.Value;
            Assert.That(port, Is.Not.EqualTo(OriginBackend.DefaultPort), "this would drive a real telescope");
            workDirectory = Path.Combine(Path.GetTempPath(), "origin-builder-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workDirectory);
            var state = await Sim("state");
            File.WriteAllText(Path.Combine(workDirectory, "cordwrap.json"), new JObject { ["neutral_enc0_degrees"] = state["cord"].Value<double>("neutral_enc0_deg") }.ToString());
            profile = new Mock<IProfileService> { DefaultValue = DefaultValue.Mock };
            profile.SetupGet(p => p.ActiveProfile.AstrometrySettings.Latitude).Returns(state.Value<double>("latitude_deg"));
            profile.SetupGet(p => p.ActiveProfile.AstrometrySettings.Longitude).Returns(state.Value<double>("longitude_deg"));
        }

        [TearDown]
        public void TearDown() {
            try {
                Directory.Delete(workDirectory, true);
            } catch (Exception) {
            }
        }

        private static void Report(string text) => TestContext.Progress.WriteLine($"{DateTime.Now:HH:mm:ss} {text}");

        private async Task<JObject> Sim(string what) => JObject.Parse(await http.GetStringAsync($"http://{host}:{port}/sim/{what}"));

        [Test]
        public async Task UnparkGatesThenTheBuilderFitsTheMountAndModelsComeAndGo() {
            var mount = new OriginTelescope(profile.Object, host, port, Path.Combine(workDirectory, "logs"), Path.Combine(workDirectory, "filter")) {
                FakeInitialize = true, UnparkPoints = 1, SettleGrace = TimeSpan.FromSeconds(2), AllowDaylight = true
            };
            OriginBackend backend = null;
            try {
                (await mount.Connect(default)).Should().BeTrue();
                backend = OriginBackend.Acquire(host, port);

                // Unpark pressed twice: the second waits for the first
                var watch = Stopwatch.StartNew();
                var first = mount.Unpark(default);
                var second = mount.Unpark(default);
                second.Should().BeSameAs(first, "a second Unpark joins the one running");
                await Task.WhenAll(first, second);
                mount.AtPark.Should().BeFalse();
                mount.PointingModel.Points.Should().Be(1, "Unpark only gates the mount: one plate solve");
                Report($"Unpark (pressed twice): one plate solve, {watch.Elapsed.TotalSeconds:0} s");

                var builder = mount.ModelBuilder;
                var generated = builder.GenerateGoldenSpiral(10);
                generated.Should().HaveCount(10);
                generated.Should().OnlyContain(p => p.Altitude >= 25 && p.Altitude <= 80);

                watch.Restart();
                builder.StartBuild();
                builder.BuildInProgress.Should().BeTrue();
                mount.Slewing.Should().BeTrue("the web page holds back slews and syncs during a build");
                Func<Task> slew = () => mount.SlewToCoordinates(new Coordinates(Angle.ByDegree(279.2347), Angle.ByDegree(38.7837), Epoch.J2000), default);
                await slew.Should().ThrowAsync<InvalidOperationException>("nothing else moves the mount during a build");
                var done = await Task.WhenAny(builder.Building, Task.Delay(TimeSpan.FromMinutes(20)));
                done.Should().BeSameAs(builder.Building, "a build of 10 points should finish within 20 minutes");
                builder.LastError.Should().BeNull();
                var summary = builder.Summary();
                var truth = await Sim("truth");
                var tilt = truth.Value<double>("tilt_deg");
                Report($"build: {summary.Points} points in {watch.Elapsed.TotalMinutes:0.0} min, rms {summary.RmsArcsec:0} arcsec, base tilt {summary.TiltDegrees:0.00} degrees " +
                       $"(truly {tilt:0.00}), flexure {summary.Flexure:0.000}, cone {summary.ConeDegrees * 60:0.0} arcmin, {summary.Terms} terms");
                builder.Points.Count(p => p.State == OriginModelPointState.AddedToModel).Should().BeGreaterThanOrEqualTo(8, "the simulator's sky is clear");
                summary.Points.Should().BeGreaterThanOrEqualTo(8);
                summary.RmsArcsec.Should().BeLessThan(60);
                summary.TiltDegrees.Should().BeApproximately(tilt, 0.2, "the build should find the hidden level error");

                // a goto by the model, checked by a plate solve
                var site = new SofaOriginSky(mount.SiteLatitude, mount.SiteLongitude, 0);
                var star = brightStars.Select(s => (s.Name, s.Ra, s.Dec, Horizon: site.ToHorizon(s.Ra, s.Dec, DateTime.UtcNow)))
                                      .Where(s => s.Horizon.Altitude is > 30 and < 70).First();
                (await mount.SlewToCoordinates(new Coordinates(Angle.ByDegree(star.Ra), Angle.ByDegree(star.Dec), Epoch.J2000), default)).Should().BeTrue();
                await Task.Delay(TimeSpan.FromSeconds(3));
                var solved = await new OriginAstrometryNet().Solve(await backend.TakeFrame(2, 800, 1, TimeSpan.FromSeconds(60)));
                solved.Should().NotBeNull();
                var miss = Separation(solved.Value.RaDegrees, solved.Value.DecDegrees, star.Ra, star.Dec);
                Report($"{star.Name} by the model: {miss:0.0} arcmin out");
                miss.Should().BeLessThan(3, "the built model points within a few arcminutes");

                // the screens' other buttons
                var before = builder.Summary().Points;
                var (index, error) = builder.DeleteWorstPoint();
                builder.Summary().Points.Should().Be(before - 1);
                Report($"deleted the worst point, {index}, {error:0} arcsec");

                builder.SaveModel("simulator test");
                builder.ModelNames().Should().Contain("simulator test");
                File.Exists(Path.Combine(workDirectory, "models", "simulator test.json")).Should().BeTrue("models are kept beside the cable-neutral");
                await builder.ClearAlignment();
                mount.PointingModel.Should().BeNull();
                mount.AtPark.Should().BeTrue("with no model the mount waits for a plate solve");
                builder.LoadModel("simulator test");
                mount.PointingModel.Should().NotBeNull();
                builder.Summary().Points.Should().Be(before - 1);
                mount.AtPark.Should().BeFalse();
                builder.DeleteModel("simulator test");
                builder.ModelNames().Should().NotContain("simulator test");
                FluentActions.Invoking(() => builder.SaveModel("../escape")).Should().Throw<ArgumentException>();

                await mount.Park(default);
                var parked = await Sim("truth");
                Report($"parked at true Dec {parked.Value<double>("true_dec_deg"):0.00}");
                parked.Value<double>("true_dec_deg").Should().BeGreaterThan(89, "the built model finds the pole");
            } finally {
                mount.ModelBuilder.Cancel();
                backend?.Release();
                mount.Disconnect();
            }
        }

        private static double Separation(double ra1, double dec1, double ra2, double dec2) {
            double r(double d) => d * Math.PI / 180;
            var c = Math.Sin(r(dec1)) * Math.Sin(r(dec2)) + Math.Cos(r(dec1)) * Math.Cos(r(dec2)) * Math.Cos(r(ra1 - ra2));
            return Math.Acos(Math.Clamp(c, -1, 1)) * 180 / Math.PI * 60;
        }
    }
}
