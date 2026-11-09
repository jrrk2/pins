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
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;

namespace NINA.Test.Equipment.Origin {

    /// <summary>
    /// The mount driver as the web page uses it, against OriginSimulator's mechanical mount with a secret level error: Unpark (a fake initialisation and one
    /// plate solve, a level-base model), slews, centring as N.I.N.A. does it (plate solve, Sync, slew again), which turns three syncs into the full model, and
    /// Park back to cable-neutral.
    ///
    ///   ORIGIN_SIM=127.0.0.1:18080 dotnet test --filter OriginSimulatorPointingTest --logger "console;verbosity=detailed"
    /// </summary>
    [TestFixture]
    [NonParallelizable]
    [Explicit("Drives OriginSimulator for minutes; set ORIGIN_SIM")]
    public class OriginSimulatorPointingTest {
        private static readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(10) };
        private string host;
        private int port;
        private string workDirectory;
        private Mock<IProfileService> profile;

        private static readonly (string Name, double Ra, double Dec)[] brightStars = {
            ("Deneb", 310.3580, 45.2803), ("Vega", 279.2347, 38.7837), ("Capella", 79.1723, 45.9980), ("Arcturus", 213.9153, 19.1824), ("Alkaid", 206.8852, 49.3133),
            ("Dubhe", 165.9320, 61.7510), ("Regulus", 152.0930, 11.9672), ("Pollux", 116.3290, 28.0262), ("Castor", 113.6495, 31.8883), ("Procyon", 114.8255, 5.2250),
            ("Betelgeuse", 88.7929, 7.4071), ("Aldebaran", 68.9802, 16.5093), ("Mirfak", 51.0807, 49.8612), ("Alpheratz", 2.0969, 29.0904), ("Altair", 297.6958, 8.8683),
            ("Alioth", 193.5073, 55.9598), ("Schedar", 10.1268, 56.5373), ("Hamal", 31.7934, 23.4624) };

        [SetUp]
        public async Task SetUp() {
            var location = OriginDiscovery.ParseHost(Environment.GetEnvironmentVariable("ORIGIN_SIM"));
            if (location == null) {
                Assert.Ignore("ORIGIN_SIM is not set");
            }
            (host, port) = location.Value;
            Assert.That(port, Is.Not.EqualTo(OriginBackend.DefaultPort), "this would drive a real telescope");
            workDirectory = Path.Combine(Path.GetTempPath(), "origin-pointing-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workDirectory);
            var state = await Sim("state");
            File.WriteAllText(Path.Combine(workDirectory, "cordwrap.json"), new JObject { ["neutral_enc0_degrees"] = state["cord"].Value<double>("neutral_enc0_deg") }.ToString());
            profile = new Mock<IProfileService> { DefaultValue = DefaultValue.Mock };
            profile.SetupGet(p => p.ActiveProfile.AstrometrySettings.Latitude).Returns(state.Value<double>("latitude_deg"));
            profile.SetupGet(p => p.ActiveProfile.AstrometrySettings.Longitude).Returns(state.Value<double>("longitude_deg"));
        }

        private static void Report(string text) => TestContext.Progress.WriteLine($"{DateTime.Now:HH:mm:ss} {text}");

        private async Task<JObject> Sim(string what) => JObject.Parse(await http.GetStringAsync($"http://{host}:{port}/sim/{what}"));

        [Test]
        public async Task UnparkSolvesOnceThenCentringBuildsTheModelAndParkUnwinds() {
            var mount = new OriginTelescope(profile.Object, host, port, Path.Combine(workDirectory, "logs"), Path.Combine(workDirectory, "filter")) {
                FakeInitialize = true, UnparkPoints = 1, SettleGrace = TimeSpan.FromSeconds(2), AllowDaylight = true     // the simulator, at any hour
            };
            OriginBackend backend = null;
            var solver = new OriginAstrometryNet();
            try {
                (await mount.Connect(default)).Should().BeTrue();
                // the mount's own link, for frames: acquired after the mount connects, so that it is the one with this test's cordwrap.json and not the
                // default ~/.config/origin, which holds the real telescope's cable-neutral
                backend = OriginBackend.Acquire(host, port);
                await mount.Unpark(default);
                mount.AtPark.Should().BeFalse();
                mount.PointingModel.Should().NotBeNull("Unpark should plate solve once");
                mount.PointingModel.Points.Should().Be(1);
                Report("Unpark: fake initialisation and one plate solve, a level-base model");

                var site = new SofaOriginSky(mount.SiteLatitude, mount.SiteLongitude, 0);
                var stars = brightStars.Select(s => (s.Name, s.Ra, s.Dec, Horizon: site.ToHorizon(s.Ra, s.Dec, DateTime.UtcNow)))
                                       .Where(s => s.Horizon.Altitude is > 25 and < 75).ToList();
                // four stars spread round the sky: each as far in azimuth as can be from those already chosen
                var chosen = new List<(string Name, double Ra, double Dec, (double Altitude, double Azimuth) Horizon)>();
                while (chosen.Count < 4 && chosen.Count < stars.Count) {
                    chosen.Add(stars.Except(chosen).MaxBy(s => chosen.Count == 0 ? s.Horizon.Altitude : chosen.Min(c => Math.Abs(Math.IEEERemainder(c.Horizon.Azimuth - s.Horizon.Azimuth, 360)))));
                }
                chosen.Should().HaveCountGreaterThanOrEqualTo(4, "four bright stars in different directions should be up");

                async Task<double> Slew((string Name, double Ra, double Dec, (double Altitude, double Azimuth) Horizon) star) {
                    (await mount.SlewToCoordinates(new Coordinates(Angle.ByDegree(star.Ra), Angle.ByDegree(star.Dec), Epoch.J2000), default)).Should().BeTrue();
                    await Task.Delay(TimeSpan.FromSeconds(3));
                    var frame = await backend.TakeFrame(2, 800, 1, TimeSpan.FromSeconds(60));
                    var solved = await solver.Solve(frame);
                    solved.Should().NotBeNull($"the field at {star.Name} should plate solve");
                    var (ra, dec) = solved.Value;
                    lastSolved = (ra, dec);
                    return Separation(ra, dec, star.Ra, star.Dec);
                }

                // the first star: the level-base model from one solve
                var first = await Slew(chosen[0]);
                var truth = await Sim("truth");
                var tilt = truth.Value<double>("tilt_deg");
                Report($"{chosen[0].Name}: landed {first / 60:0.00} degrees out with one plate solve (the base is {tilt:0.0} degrees out of level)");
                (first / 60).Should().BeLessThan(2 * tilt + 0.5, "a level-base model is out by about the base's tilt, not the firmware's invented tens of degrees");

                // centring at three stars, as N.I.N.A.'s centring does it: slew, solve, and while out by more than the tolerance sync and slew again, a few times at most
                for (var i = 0; i < 3; i++) {
                    var star = chosen[i];
                    var miss = i == 0 ? first : await Slew(star);
                    var misses = new List<double> { miss };
                    // Before the full model, a level-base one on a tilted base tracks a few arcminutes off in the minute between a frame and the next (about
                    // 0.26' per degree of tilt), so centring settles there. The first star's syncs all lie near it, and a fit from clustered points cannot pin
                    // the tilt down either; from the second star, with points spread over the sky, centring settles within 2'.
                    var isFirstStar = i == 0;
                    double Tolerance() => !isFirstStar && mount.PointingModel.Points >= 3 ? 2 : Math.Max(2, 0.4 * tilt);
                    for (var attempt = 0; attempt < 4 && miss > Tolerance(); attempt++) {
                        mount.Sync(new Coordinates(Angle.ByDegree(lastSolved.Ra), Angle.ByDegree(lastSolved.Dec), Epoch.J2000)).Should().BeTrue("the solve should be taken as a point");
                        miss = await Slew(star);
                        misses.Add(miss);
                    }
                    Report($"{star.Name}: centred in {misses.Count - 1} sync{(misses.Count == 2 ? "" : "s")}: {string.Join(" -> ", misses.Select(m => $"{m:0.0}'"))}; {mount.PointingModel.Points} points, rms {mount.PointingModel.RmsArcmin:0.0}'");
                    miss.Should().BeLessThan(Tolerance(), "centring should settle within its tolerance");
                    if (misses.Count == 1) {
                        // already within 2' with no sync: sync anyway, as a centring that ends in a solve adds its point
                        mount.Sync(new Coordinates(Angle.ByDegree(lastSolved.Ra), Angle.ByDegree(lastSolved.Dec), Epoch.J2000));
                    }
                }
                mount.PointingModel.Points.Should().BeGreaterThanOrEqualTo(3, "three syncs make the full model");

                // a fourth star with the full model, and the position the mount reports
                var fourth = await Slew(chosen[3]);
                await Task.Delay(TimeSpan.FromSeconds(3));
                var reported = Separation(mount.Coordinates.RADegrees, mount.Coordinates.Dec, lastSolved.Ra, lastSolved.Dec);
                Report($"{chosen[3].Name} with the full model: {fourth:0.0}' out; the mount reports a position {reported:0.0}' from the solve");
                fourth.Should().BeLessThan(5);
                reported.Should().BeLessThan(5, "the reported position comes from the model, not the invented frame");

                // a sync typed in after a slew, with no frame since (the last frame shows another part of the sky): it must pair with the encoders now
                (await mount.SlewToCoordinates(new Coordinates(Angle.ByDegree(chosen[1].Ra), Angle.ByDegree(chosen[1].Dec), Epoch.J2000), default)).Should().BeTrue();
                await Task.Delay(TimeSpan.FromSeconds(3));
                var here = await Sim("truth");
                mount.Sync(new Coordinates(Angle.ByDegree(here.Value<double>("true_ra_deg")), Angle.ByDegree(here.Value<double>("true_dec_deg")), Epoch.J2000)).Should().BeTrue();
                var afterTypedSync = await Slew(chosen[1]);
                Report($"{chosen[1].Name} after a sync typed in with no frame since the slew: {afterTypedSync:0.0}' out");
                afterTypedSync.Should().BeLessThan(5, "a sync after a slew must not be paired with an older frame's encoders");

                // Park: the celestial pole, where the sun never comes, with tracking stopped
                await mount.Park(default);
                await Task.Delay(TimeSpan.FromSeconds(5));
                var parked = await Sim("truth");
                var state = await Sim("state");
                var cord = state["cord"];
                Report($"parked at Dec {parked.Value<double>("true_dec_deg"):0.00} (alt {parked.Value<double>("apparent_alt_deg"):0.0}, az {parked.Value<double>("true_az_deg"):0.0}), {state.Value<string>("mode")}; {cord.Value<double>("turned_deg"):0.0} degrees from cable-neutral, range {cord.Value<double>("min_turned_deg"):0.0} to {cord.Value<double>("max_turned_deg"):0.0}");
                mount.AtPark.Should().BeTrue();
                parked.Value<double>("true_dec_deg").Should().BeGreaterThan(89, "the tube should point at the celestial pole");
                state.Value<string>("mode").Should().Be("IDLE", "a parked tube does not track");
                Math.Abs(cord.Value<double>("max_turned_deg")).Should().BeLessThanOrEqualTo(OriginCordWrap.DefaultLimit + OriginCordWrap.TrackOverrun);
                Math.Abs(cord.Value<double>("min_turned_deg")).Should().BeLessThanOrEqualTo(OriginCordWrap.DefaultLimit + OriginCordWrap.TrackOverrun);
                await mount.Unpark(default);
                mount.AtPark.Should().BeFalse();
                mount.PointingModel.Points.Should().BeGreaterThanOrEqualTo(3, "Unpark after Park keeps the model");
            } finally {
                backend?.Release();
                mount.Disconnect();
            }
        }

        private (double Ra, double Dec) lastSolved;

        /// <summary>Arcminutes between two J2000 positions in degrees.</summary>
        private static double Separation(double ra1, double dec1, double ra2, double dec2) {
            double r(double d) => d * Math.PI / 180;
            var c = Math.Sin(r(dec1)) * Math.Sin(r(dec2)) + Math.Cos(r(dec1)) * Math.Cos(r(dec2)) * Math.Cos(r(ra1 - ra2));
            return Math.Acos(Math.Clamp(c, -1, 1)) * 180 / Math.PI * 60;
        }
    }
}
