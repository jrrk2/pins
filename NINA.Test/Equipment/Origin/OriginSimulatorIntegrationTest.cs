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
using NINA.Image.ImageData;
using NINA.Profile.Interfaces;
using NUnit.Framework;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace NINA.Test.Equipment.Origin {

    /// <summary>
    /// End to end against OriginSimulator, with no OriginHeadless in between. These move the "telescope", so they run only when ORIGIN_SIM names the simulator, and refuse port 80,
    /// where a real telescope listens:
    ///
    ///   ORIGIN_SIM=127.0.0.1:18080 dotnet test --filter OriginSimulatorIntegrationTest
    ///
    /// Passing here does not show that a stop works on a real telescope: the simulator accepts commands the firmware does not.
    /// </summary>
    [TestFixture]
    [NonParallelizable]
    public class OriginSimulatorIntegrationTest {
        private string host;
        private int port;
        private string workDirectory;
        private Mock<IProfileService> profile;

        [SetUp]
        public void SetUp() {
            var location = OriginDiscovery.ParseHost(Environment.GetEnvironmentVariable("ORIGIN_SIM"));
            if (location == null) {
                Assert.Ignore("ORIGIN_SIM is not set");
            }
            (host, port) = location.Value;
            Assert.That(port, Is.Not.EqualTo(OriginBackend.DefaultPort), "this would drive a real telescope");
            workDirectory = Path.Combine(Path.GetTempPath(), "origin-sim-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workDirectory);
            profile = new Mock<IProfileService> { DefaultValue = DefaultValue.Mock };
            profile.SetupGet(p => p.ActiveProfile.AstrometrySettings.Latitude).Returns(52.2);
            profile.SetupGet(p => p.ActiveProfile.AstrometrySettings.Longitude).Returns(0.12);
        }

        [TearDown]
        public void TearDown() {
            if (workDirectory != null && Directory.Exists(workDirectory)) {
                Directory.Delete(workDirectory, recursive: true);
            }
        }

        private OriginTelescope NewMount() => new(profile.Object, host, port, Path.Combine(workDirectory, "logs"), Path.Combine(workDirectory, "filter")) { SettleGrace = TimeSpan.Zero, AllowDaylight = true };

        [Test]
        public async Task Mount_SlewsAndReportsTheNewPosition() {
            var mount = NewMount();
            (await mount.Connect(default)).Should().BeTrue();
            mount.AtPark.Should().BeFalse();

            (await mount.SlewToCoordinates(new Coordinates(Angle.ByHours(13.5), Angle.ByDegree(47), Epoch.J2000), default)).Should().BeTrue();
            await Task.Delay(TimeSpan.FromSeconds(3));

            mount.RightAscension.Should().BeApproximately(13.5, 0.2);
            mount.Declination.Should().BeApproximately(47, 1);
            mount.Slewing.Should().BeFalse();
            mount.Disconnect();
        }

        [Test]
        public async Task Mount_StopWhileSlewingEndsTheSlewWithoutArriving() {
            var mount = NewMount();
            await mount.Connect(default);
            var slew = mount.SlewToCoordinates(new Coordinates(Angle.ByHours(2), Angle.ByDegree(10), Epoch.J2000), default);
            await Task.Delay(TimeSpan.FromSeconds(1));

            mount.StopSlew();

            (await slew.WaitAsync(TimeSpan.FromSeconds(30))).Should().BeFalse();
            mount.Disconnect();
        }

        [Test]
        public async Task Focuser_MovesAndReportsItsPosition() {
            var focuser = new OriginFocuser(host, port, Path.Combine(workDirectory, "logs"), Path.Combine(workDirectory, "filter")) { PollInterval = TimeSpan.FromMilliseconds(100) };
            await focuser.Connect(default);
            for (var i = 0; i < 100 && focuser.Position == 0; i++) {
                await Task.Delay(100);
            }
            focuser.Position.Should().BeGreaterThan(0);
            var target = focuser.Position + 200;

            await focuser.Move(target, default);

            focuser.Position.Should().BeInRange(target - 2, target + 2);
            focuser.Disconnect();
        }

        [Test]
        public async Task Camera_DeliversARawFrameThatPinsCanRead() {
            var factory = new ImageDataFactoryTestUtility();
            var frameRoot = Path.Combine(workDirectory, "frames");
            var camera = new OriginCamera(profile.Object, factory.ImageDataFactory, factory.ExposureDataFactory, host, port, frameRoot, Path.Combine(workDirectory, "logs"), Path.Combine(workDirectory, "filter"));
            await camera.Connect(default);
            camera.Gain = 800;

            camera.StartExposure(new CaptureSequence(1, CaptureSequence.ImageTypes.LIGHT, null, null, 1));
            await camera.WaitUntilExposureIsReady(default);
            var exposure = await camera.DownloadExposure(default);
            var image = await exposure.ToImageData();

            image.Properties.Width.Should().Be(camera.CameraXSize);
            image.Properties.Height.Should().Be(camera.CameraYSize);
            image.Properties.IsBayered.Should().BeTrue();
            image.Data.FlatArray.Max().Should().BeGreaterThan((ushort)0);
            Directory.GetFiles(frameRoot, "*.fits", SearchOption.AllDirectories).Should().BeEmpty();
            camera.Disconnect();
        }
    }
}
