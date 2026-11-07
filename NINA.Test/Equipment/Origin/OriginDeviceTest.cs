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
using NINA.Core.Enum;
using NINA.Equipment.Equipment.Origin;
using NINA.Equipment.Model;
using NINA.Image.Interfaces;
using NINA.Profile.Interfaces;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Test.Equipment.Origin {

    [TestFixture]
    [NonParallelizable]
    public class OriginDeviceTest {
        private OriginFakeTelescope telescope;
        private Mock<IProfileService> profile;
        private string workDirectory;
        private string logDirectory;
        private string filterFile;
        private string frameRoot;
        private OriginBackend backend;

        [SetUp]
        public void SetUp() {
            telescope = new OriginFakeTelescope();
            profile = new Mock<IProfileService> { DefaultValue = DefaultValue.Mock };
            profile.SetupGet(p => p.ActiveProfile.AstrometrySettings.Latitude).Returns(52.2);
            profile.SetupGet(p => p.ActiveProfile.AstrometrySettings.Longitude).Returns(0.12);
            workDirectory = Path.Combine(TestContext.CurrentContext.WorkDirectory, "origin-" + Guid.NewGuid().ToString("N"));
            logDirectory = Path.Combine(workDirectory, "logs");
            filterFile = Path.Combine(workDirectory, "filter");
            frameRoot = Path.Combine(workDirectory, "frames");
            Directory.CreateDirectory(workDirectory);
            backend = NewBackend();
        }

        [TearDown]
        public void TearDown() {
            backend.Disconnect();
            telescope.Dispose();
            if (Directory.Exists(workDirectory)) {
                Directory.Delete(workDirectory, recursive: true);
            }
        }

        private OriginBackend NewBackend() => new("127.0.0.1", telescope.Port, logDirectory, filterFile) {
            GotoPollInterval = TimeSpan.FromMilliseconds(10),
            GotoStartGrace = TimeSpan.FromMilliseconds(50),
            TiltCheckInterval = TimeSpan.FromMilliseconds(10),
            TiltWait = TimeSpan.FromMilliseconds(200),
            InitializePollInterval = TimeSpan.FromMilliseconds(10),
            InitializeStartGrace = TimeSpan.FromMilliseconds(100),
            CancelPollInterval = TimeSpan.FromMilliseconds(10),
            RetryPause = TimeSpan.FromMilliseconds(1),
            FirstReconnectDelay = TimeSpan.FromMilliseconds(20),
            ReconnectDelay = TimeSpan.FromMilliseconds(20),
            StatusInterval = TimeSpan.FromMilliseconds(20)
        };

        private OriginTelescope NewTelescope() => new(profile.Object, "127.0.0.1", telescope.Port, logDirectory, filterFile) {
            SettleGrace = TimeSpan.Zero,
            SettleCheckInterval = TimeSpan.FromMilliseconds(10),
            PollInterval = TimeSpan.FromMilliseconds(20)
        };

        private OriginFocuser NewFocuser() => new("127.0.0.1", telescope.Port, logDirectory, filterFile) { PollInterval = TimeSpan.FromMilliseconds(10) };

        private OriginCamera NewCamera(Mock<IImageDataFactory> images = null, Mock<IExposureDataFactory> exposures = null) {
            return new OriginCamera(profile.Object, (images ?? new Mock<IImageDataFactory>()).Object, (exposures ?? new Mock<IExposureDataFactory>()).Object,
                                    "127.0.0.1", telescope.Port, frameRoot, logDirectory, filterFile) { PollInterval = TimeSpan.FromMilliseconds(20) };
        }

        private static CaptureSequence Light(double seconds) => new(seconds, CaptureSequence.ImageTypes.LIGHT, null, null, 1);

        private static async Task WaitFor(Func<bool> condition) {
            for (var i = 0; i < 500 && !condition(); i++) {
                await Task.Delay(10);
            }
            condition().Should().BeTrue();
        }

        private static readonly TimeSpan Short = TimeSpan.FromSeconds(10);

        private Task PushTaskState(string state, string stage = "IN_PROGRESS", int sequence = 0, JObject extra = null) {
            var message = new JObject { ["Command"] = "GetStatus", ["Source"] = "TaskController", ["State"] = state, ["Stage"] = stage, ["SequenceID"] = sequence };
            if (extra != null) {
                message.Merge(extra);
            }
            return telescope.Push(message);
        }

        // ---- the wire ----

        [Test]
        public void Command_IsByteForByteWhatTheRealTelescopeReceived() {
            // Lines from a real session's log, websocket_log_20261027_164003.txt: the sender was OriginMonitor, and the telescope answered these.
            OriginBackend b = new("h");
            var first = b.CreateCommand("GetStatus", "Mount", null, out var id);
            id.Should().Be(2000);
            first.Should().Be("""{"Command":"GetStatus","Destination":"Mount","SequenceID":2000,"Source":"OriginMobileApp","Type":"Command"}""");

            b.CreateCommand("SetCaptureParameters", "Camera", new JObject { ["BitDepth"] = 16 }, out _)
                .Should().Be("""{"BitDepth":16,"Command":"SetCaptureParameters","Destination":"Camera","SequenceID":2001,"Source":"OriginMobileApp","Type":"Command"}""");

            b.CreateCommand("RunSampleCapture", "TaskController", new JObject { ["ExposureTime"] = 0.001, ["ISO"] = 800, ["Binning"] = 1, ["Debayer"] = false }, out _)
                .Should().Be("""{"Binning":1,"Command":"RunSampleCapture","Debayer":false,"Destination":"TaskController","ExposureTime":0.001,"ISO":800,"SequenceID":2002,"Source":"OriginMobileApp","Type":"Command"}""");
        }

        [Test]
        public void Command_WritesAWholeDoubleAsQtDoes() {
            new OriginBackend("h").CreateCommand("X", "Y", new JObject { ["ExposureTime"] = 6.0 }, out _).Should().Contain("\"ExposureTime\":6,");
        }

        [Test]
        public async Task Connect_SetsUpTheCameraAsOriginMonitorDoes() {
            (await backend.ConnectAsync()).Should().BeTrue();
            await WaitFor(() => telescope.Count("SetDisableLiveStream") == 1 && telescope.Count("SetCaptureParameters") >= 1);

            telescope.Received.First(m => m.Value<string>("Command") == "SetDisableLiveStream").Value<bool>("Disable").Should().BeTrue();
            telescope.Received.First(m => m.Value<string>("Command") == "SetCaptureParameters").Value<int>("BitDepth").Should().Be(16);
        }

        [Test]
        public async Task Connect_ReturnsFalseWhenThereIsNoTelescope() {
            telescope.RefuseConnections = true;
            (await backend.ConnectAsync()).Should().BeFalse();
            backend.IsConnected.Should().BeFalse();
        }

        [Test]
        public async Task Status_RotatesThroughMountEnvironmentAndCamera() {
            await backend.ConnectAsync();
            await WaitFor(() => telescope.Received.Any(m => m.Value<string>("Destination") == "Environment") && telescope.Count("GetCaptureParameters") > 0 && telescope.Count("GetStatus") > 2);
        }

        [Test]
        public async Task Log_KeepsTheFormatOriginMonitorsToolsRead() {
            await backend.ConnectAsync();
            await telescope.Push(new JObject { ["Command"] = "GetStatus", ["Source"] = "Focuser", ["Position"] = 19031 });
            await WaitFor(() => backend.Data.Focuser.Position == 19031);
            backend.Disconnect();

            var lines = File.ReadAllLines(Directory.GetFiles(logDirectory, "websocket_log_*.txt").Single());
            lines.Should().Contain(l => System.Text.RegularExpressions.Regex.IsMatch(l, @"^\[\d{4}-\d\d-\d\d \d\d:\d\d:\d\d\.\d{3}\] SEND: \{""Command"":""GetStatus"""));
            lines.Should().Contain(l => l.Contains("] RECV: ") && l.Contains("\"Position\":19031"));
        }

        [Test]
        public async Task Message_OfAnUnexpectedShapeDoesNotStopTheLink() {
            await backend.ConnectAsync();
            await telescope.SendRaw("not json");
            await telescope.SendRaw("""{"Source":"Focuser","Type":"Notification","Position":{"nested":1}}""");
            await telescope.Push(new JObject { ["Command"] = "GetStatus", ["Source"] = "Focuser", ["Position"] = 7 });
            await WaitFor(() => backend.Data.Focuser.Position == 7);
        }

        // ---- status, from messages the real telescope sent ----

        [Test]
        public void Status_ReadsARealMountMessage() {
            var data = new TelescopeData();
            data.Process(JObject.Parse("""{"Alt":1.1678203397921167,"Azm":5.907059642735994,"BatteryCurrent":0.0,"BatteryLevel":"HIGH","BatteryVoltage":10.570455551147461,"ChargerStatus":"CHARGING","Command":"GetStatus","Date":"03 11 2026","Dec":1.2532476926584206,"Destination":"All","Enc0":6.283184932672558,"Enc1":0.5753757454521592,"IsAligned":true,"IsGotoOver":true,"IsTracking":true,"Latitude":0.9111561173206476,"Longitude":0.0021258110289290935,"NumAlignRefs":1,"Ra":5.2927662853483515,"SequenceID":687,"Source":"Mount","Time":"19:10:32","TimeZone":"Europe/London","Type":"Notification"}""")).Should().BeTrue();

            data.Mount.Ra.Should().Be(5.2927662853483515);
            data.Mount.Dec.Should().Be(1.2532476926584206);
            data.Mount.Altitude.Should().Be(1.1678203397921167);
            data.Mount.Azimuth.Should().Be(5.907059642735994);
            data.Mount.IsAligned.Should().BeTrue();
            data.Mount.TimeZone.Should().Be("Europe/London");
        }

        [Test]
        public void Status_AnAcknowledgementDoesNotWipeTheMountsState() {
            var data = new TelescopeData();
            data.Process(new JObject { ["Source"] = "Mount", ["Type"] = "Notification", ["Ra"] = 1.5, ["IsAligned"] = true, ["IsGotoOver"] = true });

            data.Process(new JObject { ["Source"] = "Mount", ["Type"] = "Response", ["Command"] = "GotoRaDec", ["ErrorCode"] = 0 });

            data.Mount.Ra.Should().Be(1.5);
            data.Mount.IsAligned.Should().BeTrue();
            data.Mount.IsGotoOver.Should().BeTrue();
        }

        [Test]
        public void Status_IgnoresOurOwnCommandEcho() {
            new TelescopeData().Process(new JObject { ["Source"] = "Mount", ["Type"] = "Command" }).Should().BeFalse();
        }

        [Test]
        public void Status_ReadsTheTaskControllersFocusAndInitialisationProgress() {
            var data = new TelescopeData();
            data.Process(new JObject {
                ["Source"] = "TaskController", ["Type"] = "Notification", ["State"] = "INITIALIZING", ["Stage"] = "IN_PROGRESS",
                ["InitializationInfo"] = new JObject { ["CurrentStep"] = "FOCUSING", ["NumPoints"] = 2, ["PercentageComplete"] = 40 },
                ["FocusInfo"] = new JObject { ["Position"] = 18500, ["PercentageComplete"] = 70 }
            });

            data.TaskController.CurrentStep.Should().Be("FOCUSING");
            data.TaskController.PercentageComplete.Should().Be(40);
            data.TaskController.FocusPosition.Should().Be(18500);
            data.TaskController.FocusPercentageComplete.Should().Be(70);
        }

        [Test]
        public void Status_AColdEnvironmentIsNotReportedAsZeroBeforeItsFirstMessage() {
            var data = new TelescopeData();
            data.Environment.CameraTemperature.Should().Be(double.NaN);
            data.Process(JObject.Parse("""{"AmbientTemperature":9.164999961853027,"CameraTemperature":19.0,"Command":"GetStatus","Source":"Environment","Type":"Notification"}"""));
            data.Environment.CameraTemperature.Should().Be(19.0);
        }

        // ---- events ----

        [Test]
        public async Task Events_AnAuxLossIsReportedOnceAndItsRecoveryOnce() {
            await backend.ConnectAsync();
            for (var i = 0; i < 3; i++) {
                await telescope.Push(new JObject { ["Source"] = "DewHeater", ["Command"] = "GetStatus", ["ErrorCode"] = -809, ["ErrorMessage"] = "Timeout" });
            }
            await WaitFor(() => backend.AuxLostDevices.ContainsKey("DewHeater"));

            await telescope.Push(new JObject { ["Source"] = "DewHeater", ["Command"] = "GetStatus", ["ErrorCode"] = 0 });
            await WaitFor(() => !backend.AuxLostDevices.ContainsKey("DewHeater"));

            var kinds = backend.EventsSince(0).Select(e => e.Value<string>("kind")).ToList();
            kinds.Count(k => k == "aux_lost").Should().Be(1);
            kinds.Count(k => k == "aux_restored").Should().Be(1);
            kinds.Should().NotContain("telescope_error");
        }

        [Test]
        public void Events_RepeatsWithinFiveSecondsAreOneEvent() {
            backend.RecordEvent("x", "same");
            backend.RecordEvent("x", "same");
            backend.RecordEvent("x", "different");
            backend.EventsSince(0).Should().HaveCount(2);
            backend.EventsSince(1).Should().HaveCount(1);
        }

        // ---- reconnect ----

        [Test]
        public async Task Link_ThatDropsIsReopenedAndTheCameraSetUpAgain() {
            await backend.ConnectAsync();
            await WaitFor(() => telescope.Count("SetDisableLiveStream") == 1);

            telescope.Drop();

            await WaitFor(() => telescope.Connections == 2 && backend.IsConnected);
            await WaitFor(() => telescope.Count("SetDisableLiveStream") == 2);
            backend.EventsSince(0).Select(e => e.Value<string>("kind")).Should().Contain("reconnected");
        }

        [Test]
        public async Task Link_ThatGoesSilentIsNoticedByItsPings() {
            // Wi-Fi loss: the socket stays open and nothing arrives. This peer completes the handshake and then never reads or writes again.
            var silent = new TcpListener(IPAddress.Loopback, 0);
            silent.Start();
            var port = ((IPEndPoint)silent.LocalEndpoint).Port;
            var accept = Task.Run(async () => {
                var peer = await silent.AcceptTcpClientAsync();
                var stream = peer.GetStream();
                var buffer = new byte[4096];
                var request = Encoding.ASCII.GetString(buffer, 0, await stream.ReadAsync(buffer));
                var key = System.Text.RegularExpressions.Regex.Match(request, @"Sec-WebSocket-Key: (\S+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Groups[1].Value;
                var accepted = Convert.ToBase64String(System.Security.Cryptography.SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accepted}\r\n\r\n"));
                return peer;
            });
            var quiet = new OriginBackend("127.0.0.1", port, logDirectory, filterFile) {
                KeepAliveInterval = TimeSpan.FromMilliseconds(100),
                KeepAliveTimeout = TimeSpan.FromMilliseconds(100),
                FirstReconnectDelay = TimeSpan.FromMinutes(5)
            };

            (await quiet.ConnectAsync()).Should().BeTrue();

            await WaitFor(() => !quiet.IsConnected);
            quiet.Disconnect();
            (await accept).Dispose();
            silent.Stop();
        }

        [Test]
        public async Task Link_SendsRealPingsAtTheRateTheTelescopeIsUsedTo() {
            // The telescope drops the link when pings stop. An unsolicited pong, which is what .NET sends without a keep-alive timeout, would not count as one.
            var peerListener = new TcpListener(IPAddress.Loopback, 0);
            peerListener.Start();
            var port = ((IPEndPoint)peerListener.LocalEndpoint).Port;
            var firstPing = Task.Run(async () => {
                using var peer = await peerListener.AcceptTcpClientAsync();
                var stream = peer.GetStream();
                var buffer = new byte[4096];
                var request = Encoding.ASCII.GetString(buffer, 0, await stream.ReadAsync(buffer));
                var key = System.Text.RegularExpressions.Regex.Match(request, @"Sec-WebSocket-Key: (\S+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Groups[1].Value;
                var accepted = Convert.ToBase64String(System.Security.Cryptography.SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accepted}\r\n\r\n"));
                // Client frames: FIN and opcode, then masked length (with a 16-bit extension for larger ones), 4 mask bytes, payload.
                var head = new byte[2];
                while (true) {
                    await stream.ReadExactlyAsync(head);
                    var opcode = head[0] & 0x0F;
                    var length = head[1] & 0x7F;
                    if (length == 126) {
                        await stream.ReadExactlyAsync(head);
                        length = (head[0] << 8) | head[1];
                    }
                    await stream.ReadExactlyAsync(new byte[length + 4]);
                    if (opcode is 0x9 or 0xA) {
                        return opcode;
                    }
                }
            });
            var pinging = new OriginBackend("127.0.0.1", port, logDirectory, filterFile) { KeepAliveInterval = TimeSpan.FromMilliseconds(50), KeepAliveTimeout = TimeSpan.FromSeconds(5), FirstReconnectDelay = TimeSpan.FromMinutes(5) };

            await pinging.ConnectAsync();

            (await firstPing.WaitAsync(Short)).Should().Be(0x9, "0x9 is PING and 0xA is PONG");
            new OriginBackend("h").KeepAliveInterval.Should().Be(TimeSpan.FromSeconds(15));
            pinging.Disconnect();
            peerListener.Stop();
        }

        [Test]
        public async Task Link_ClosedOnPurposeIsNotReopened() {
            await backend.ConnectAsync();
            backend.Disconnect();
            await Task.Delay(200);
            telescope.Connections.Should().Be(1);
            backend.IsConnected.Should().BeFalse();
            telescope.ClosedCleanly.Should().BeTrue("the telescope has one client slot and should know that it is free");
        }

        [Test]
        public async Task Link_StatusFromBeforeADropIsForgotten() {
            await backend.ConnectAsync();
            await PushTaskState("SAMPLE_CAPTURE");
            await WaitFor(() => backend.LastMessage("TaskController/GetStatus") != null);

            telescope.Drop();
            await WaitFor(() => telescope.Connections == 2 && backend.IsConnected);

            await WaitFor(() => backend.LastMessage("TaskController/GetStatus") == null);
        }

        [Test]
        public async Task Request_ReturnsNullAtOnceWhenTheLinkDrops() {
            await backend.ConnectAsync();
            telescope.Handlers["GetCameraInfo"] = _ => null;
            var request = backend.GetCameraInfo();
            await WaitFor(() => telescope.Count("GetCameraInfo") == 1);

            telescope.Drop();

            (await request.WaitAsync(TimeSpan.FromSeconds(3))).Should().BeNull();
        }

        // ---- filter ----

        [Test]
        public async Task Filter_TheConfiguredOneIsDeclaredOnConnect() {
            File.WriteAllText(filterFile, "nebula\n");
            var fitted = "Clear";
            telescope.Handlers["GetFilter"] = _ => new JObject { ["ErrorCode"] = 0, ["Filter"] = fitted };
            telescope.Handlers["SetFilter"] = c => {
                fitted = c.Value<string>("Filter");
                return new JObject { ["ErrorCode"] = 0 };
            };

            await backend.ConnectAsync();

            await WaitFor(() => fitted == "Nebula");
        }

        [Test]
        public async Task Filter_NotResentWhenAlreadyDeclared() {
            File.WriteAllText(filterFile, "clear");
            telescope.Handlers["GetFilter"] = _ => new JObject { ["ErrorCode"] = 0, ["Filter"] = "Clear" };
            await backend.ConnectAsync();
            await WaitFor(() => telescope.Count("GetFilter") == 1);
            await Task.Delay(50);
            telescope.Count("SetFilter").Should().Be(0);
        }

        [Test]
        public async Task Filter_NoneIsSentWithoutAConfigurationFile() {
            await backend.ConnectAsync();
            await Task.Delay(50);
            telescope.Count("GetFilter").Should().Be(0);
            telescope.Count("SetFilter").Should().Be(0);
        }

        // ---- mount ----

        [Test]
        public async Task Goto_SendsRadiansAndWaitsForTheMountToFinish() {
            await backend.ConnectAsync();
            var polls = 0;
            telescope.Handlers["GetStatus"] = c => new JObject { ["ErrorCode"] = 0, ["IsGotoOver"] = c.Value<string>("Destination") == "Mount" && ++polls > 3 };

            await backend.Goto(6, 45, Short);

            var sent = telescope.Received.Single(m => m.Value<string>("Command") == "GotoRaDec");
            sent.Value<double>("Ra").Should().BeApproximately(Math.PI / 2, 1e-12);
            sent.Value<double>("Dec").Should().BeApproximately(Math.PI / 4, 1e-12);
            polls.Should().BeGreaterThan(3);
        }

        [Test]
        public async Task Goto_ARefusalCarriesTheTelescopesMessage() {
            await backend.ConnectAsync();
            telescope.Handlers["GotoRaDec"] = _ => new JObject { ["ErrorCode"] = -5, ["ErrorMessage"] = "below the horizon" };
            var act = () => backend.Goto(6, 45, Short);
            await act.Should().ThrowAsync<OriginException>().WithMessage("*below the horizon*");
        }

        [Test]
        public async Task Goto_ThatNeverFinishesFails() {
            await backend.ConnectAsync();
            telescope.MountStatus = new JObject { ["IsGotoOver"] = false };
            var act = () => backend.Goto(6, 45, TimeSpan.FromMilliseconds(100));
            await act.Should().ThrowAsync<OriginException>().WithMessage("*did not complete*");
        }

        [Test]
        public async Task Goto_FailsAtOnceWhenTheLinkDrops() {
            await backend.ConnectAsync();
            telescope.MountStatus = new JObject { ["IsGotoOver"] = false };
            var slew = backend.Goto(6, 45, TimeSpan.FromSeconds(30));
            await WaitFor(() => telescope.Count("GotoRaDec") == 1);
            telescope.Drop();
            var act = () => slew.WaitAsync(TimeSpan.FromSeconds(5));
            await act.Should().ThrowAsync<OriginException>().WithMessage("*lost*");
        }

        [Test]
        public async Task AbortMotion_SendsWhatTheRealFirmwareAcceptsAndNotWhatItRefuses() {
            await backend.ConnectAsync();
            await backend.AbortMotion();

            telescope.Commands.Should().Contain("HaltTasks").And.Contain("Slew");
            telescope.Commands.Should().NotContain("AbortAxisMovement").And.NotContain("StopAll");
            var slews = telescope.Received.Where(m => m.Value<string>("Command") == "Slew").ToList();
            slews.Should().HaveCount(2);
            slews.Should().OnlyContain(s => s.Value<int>("AltRate") == 0 && s.Value<int>("AzmRate") == 0);
        }

        [Test]
        public async Task AbortMotion_ZeroSpeedGoesFirstAndDoesNotWaitForHaltTasks() {
            await backend.ConnectAsync();
            telescope.Handlers["HaltTasks"] = _ => null;      // never answers

            var stopping = backend.AbortMotion();
            await WaitFor(() => telescope.Count("HaltTasks") == 1);

            var commands = telescope.Commands.ToList();
            commands.IndexOf("Slew").Should().BeLessThan(commands.IndexOf("HaltTasks"));
            var zero = telescope.Received.First(m => m.Value<string>("Command") == "Slew");
            zero.Value<int>("AltRate").Should().Be(0);
            zero.Value<int>("AzmRate").Should().Be(0);

            await stopping;
            telescope.Received.Count(m => m.Value<string>("Command") == "Slew").Should().Be(2);
        }

        [Test]
        public async Task AbortMotion_OneAcceptedStopIsEnough() {
            await backend.ConnectAsync();
            telescope.Handlers["HaltTasks"] = _ => new JObject { ["ErrorCode"] = -2, ["ErrorMessage"] = "Unknown command" };
            await backend.AbortMotion();
        }

        [Test]
        public async Task AbortMotion_ThrowsWhenTheTelescopeAcceptsNoStop() {
            await backend.ConnectAsync();
            telescope.Handlers["HaltTasks"] = _ => new JObject { ["ErrorCode"] = -1, ["ErrorMessage"] = "busy" };
            telescope.Handlers["Slew"] = _ => new JObject { ["ErrorCode"] = -1, ["ErrorMessage"] = "busy" };
            var act = () => backend.AbortMotion();
            await act.Should().ThrowAsync<OriginException>().WithMessage("*could not be told to stop*");
        }

        [Test]
        public async Task SyncPosition_ARefusalIsAnError() {
            // As on firmware 1.5.x for the commands with no record of working: the point is that the error is seen, not swallowed.
            await backend.ConnectAsync();
            var act = () => backend.SyncPosition(1, 2);
            await act.Should().ThrowAsync<OriginException>().WithMessage("*Unknown command*");
        }

        [Test]
        public async Task RaiseTube_RefusesToMoveWithoutATiltReading() {
            await backend.ConnectAsync();
            var act = () => backend.RaiseTube();
            await act.Should().ThrowAsync<OriginException>().WithMessage("*tilt*");
            telescope.Count("Slew").Should().Be(0);
        }

        [Test]
        public async Task RaiseTube_StopsWhenTheTiltReachesTheTarget() {
            await backend.ConnectAsync();
            await telescope.Push(new JObject { ["Source"] = "OrientationSensor", ["Command"] = "GetStatus", ["Altitude"] = 20.0 });
            await WaitFor(() => backend.Data.OrientationLastUpdate != null);

            var raise = backend.RaiseTube(60);
            await WaitFor(() => telescope.Count("Slew") == 1);
            telescope.Received.Single(m => m.Value<string>("Command") == "Slew").Value<int>("AltRate").Should().Be(9);
            await telescope.Push(new JObject { ["Source"] = "OrientationSensor", ["Command"] = "GetStatus", ["Altitude"] = 61.0 });

            (await raise).Should().Be(61);
            await WaitFor(() => telescope.Count("Slew") == 3);
            telescope.Received.Last(m => m.Value<string>("Command") == "Slew").Value<int>("AltRate").Should().Be(0);
        }

        [Test]
        public async Task RaiseTube_StopsWhenTheTiltReadingGoesStale() {
            await backend.ConnectAsync();
            await telescope.Push(new JObject { ["Source"] = "OrientationSensor", ["Command"] = "GetStatus", ["Altitude"] = 20.0 });
            await WaitFor(() => backend.Data.OrientationLastUpdate != null);

            var act = () => backend.RaiseTube(60, maxAge: TimeSpan.FromMilliseconds(200));

            await act.Should().ThrowAsync<OriginException>().WithMessage("*stale*");
            telescope.Received.Last(m => m.Value<string>("Command") == "Slew").Value<int>("AltRate").Should().Be(0);
        }

        [Test]
        public async Task RaiseTube_DoesNothingWhenAlreadyHighEnough() {
            await backend.ConnectAsync();
            await telescope.Push(new JObject { ["Source"] = "OrientationSensor", ["Command"] = "GetStatus", ["Altitude"] = 70.0 });
            await WaitFor(() => backend.Data.OrientationLastUpdate != null);
            (await backend.RaiseTube(60)).Should().Be(70);
            telescope.Count("Slew").Should().Be(0);
        }

        // ---- initialisation ----

        [Test]
        public async Task Initialize_SendsTheSiteInRadiansAndTheWallClockOfTheZone() {
            await backend.ConnectAsync();
            await PushTaskState("IDLE");
            telescope.Handlers["RunInitialize"] = command => {
                _ = PushTaskState("INITIALIZING", sequence: 1).ContinueWith(_ => PushTaskState("IDLE", sequence: 2));
                return new JObject { ["ErrorCode"] = 0 };
            };

            await backend.Initialize(52.2, 0.12, "UTC");

            var sent = telescope.Received.Single(m => m.Value<string>("Command") == "RunInitialize");
            sent.Value<double>("Latitude").Should().BeApproximately(52.2 * Math.PI / 180, 1e-12);
            sent.Value<double>("Longitude").Should().BeApproximately(0.12 * Math.PI / 180, 1e-12);
            sent.Value<string>("TimeZone").Should().Be("UTC");
            sent.Value<bool>("FakeInitialize").Should().BeFalse();
            sent.Value<string>("Date").Should().Be(DateTime.UtcNow.ToString("dd MM yyyy", System.Globalization.CultureInfo.InvariantCulture));
            sent.Value<string>("Time").Should().MatchRegex(@"^\d\d:\d\d:\d\d$");
        }

        [Test]
        public async Task Initialize_EndingIdleButNotAlignedIsAFailure() {
            // 2026-10-11: "Did not find enough stars" ends in IDLE too.
            await backend.ConnectAsync();
            telescope.MountStatus = new JObject { ["IsAligned"] = false };
            telescope.Handlers["RunInitialize"] = command => {
                _ = PushTaskState("INITIALIZING", sequence: 1).ContinueWith(_ => PushTaskState("IDLE", sequence: 2));
                return new JObject { ["ErrorCode"] = 0 };
            };

            var act = () => backend.Initialize(52.2, 0.12, "UTC");

            await act.Should().ThrowAsync<OriginException>().WithMessage("*not aligned*");
        }

        [Test]
        public async Task Initialize_ReportsTheFirmwaresError() {
            await backend.ConnectAsync();
            telescope.Handlers["RunInitialize"] = command => {
                _ = PushTaskState("INITIALIZING", sequence: 1, extra: new JObject { ["ErrorMessage"] = "Did not find enough stars" });
                return new JObject { ["ErrorCode"] = 0 };
            };

            var act = () => backend.Initialize(52.2, 0.12, "UTC");

            await act.Should().ThrowAsync<OriginException>().WithMessage("*Did not find enough stars*");
        }

        [Test]
        public async Task Initialize_WaitsForOneTheFirmwareStartedItself() {
            // After a reboot the firmware initialises by itself and answers -73 to RunInitialize (2026-11-07).
            await backend.ConnectAsync();
            await PushTaskState("INITIALIZING");
            telescope.Handlers["RunInitialize"] = command => {
                _ = Task.Delay(300).ContinueWith(_ => PushTaskState("IDLE", sequence: 2));
                return new JObject { ["ErrorCode"] = -73, ["ErrorMessage"] = "TaskController: initialization was already running when RunInitialize called." };
            };

            await backend.Initialize(52.2, 0.12, "UTC");

            telescope.Count("RunInitialize").Should().Be(1);
        }

        [Test]
        public async Task Initialize_ReportsWhenOneTheFirmwareStartedItselfFails() {
            await backend.ConnectAsync();
            await PushTaskState("INITIALIZING");
            telescope.MountStatus = new JObject { ["IsAligned"] = false };
            telescope.Handlers["RunInitialize"] = command => {
                _ = Task.Delay(300).ContinueWith(_ => PushTaskState("INITIALIZING", "STOPPED", 2, new JObject { ["ErrorMessage"] = "Failed to find focus!" }));
                return new JObject { ["ErrorCode"] = -73, ["ErrorMessage"] = "already running" };
            };

            var act = () => backend.Initialize(52.2, 0.12, "UTC");

            await act.Should().ThrowAsync<OriginException>().WithMessage("*Failed to find focus*");
        }

        [Test]
        public async Task Initialize_FakeIsNotSatisfiedByARealOneAlreadyRunning() {
            await backend.ConnectAsync();
            telescope.Handlers["RunInitialize"] = command => new JObject { ["ErrorCode"] = -73, ["ErrorMessage"] = "already running" };

            var act = () => backend.Initialize(52.2, 0.12, "UTC", fake: true);

            await act.Should().ThrowAsync<OriginException>().WithMessage("*-73*");
        }

        [Test]
        public async Task Initialize_RefusesAnUnknownTimeZoneBeforeMovingAnything() {
            await backend.ConnectAsync();
            var act = () => backend.Initialize(52.2, 0.12, "Not/AZone");
            await act.Should().ThrowAsync<OriginException>().WithMessage("*timezone*");
            telescope.Count("RunInitialize").Should().Be(0);
        }

        // ---- frames ----

        [Test]
        public async Task TakeFrame_AsksForRawAndDownloadsIt() {
            await backend.ConnectAsync();
            telescope.Files["Images/Temp/sample0.fits"] = OriginFakeTelescope.FitsHeader(2, 800);
            telescope.CaptureResults.Enqueue("Images/Temp/sample0.fits");

            var frame = await backend.TakeFrame(2, 800, 1, Short);

            frame.IsFits.Should().BeTrue();
            frame.Data.Should().Equal(telescope.Files["Images/Temp/sample0.fits"]);
            var sent = telescope.Received.Single(m => m.Value<string>("Command") == "RunSampleCapture");
            sent.Value<double>("ExposureTime").Should().Be(2);
            sent.Value<int>("ISO").Should().Be(800);
            sent.Value<bool>("Debayer").Should().BeFalse();
        }

        [Test]
        public async Task TakeFrame_ResendsTheParametersTheTelescopeResetsAfterACapture() {
            await backend.ConnectAsync();
            telescope.Files["a.fits"] = OriginFakeTelescope.FitsHeader(2, 800);
            telescope.CaptureResults.Enqueue("a.fits");

            await backend.TakeFrame(2, 800, 1, Short);

            await WaitFor(() => telescope.Received.Any(m => m.Value<double?>("Exposure") == 2) && telescope.Received.Any(m => m.Value<int?>("ISO") == 800 && m.Value<string>("Command") == "SetCaptureParameters"));
        }

        [Test]
        public async Task TakeFrame_APngIsAskedForAgain() {
            await backend.ConnectAsync();
            telescope.Files["a.png"] = new byte[4];
            telescope.Files["b.fits"] = OriginFakeTelescope.FitsHeader(1, 800);
            telescope.CaptureResults.Enqueue("a.png");
            telescope.CaptureResults.Enqueue("b.fits");

            (await backend.TakeFrame(1, 800, 1, Short)).RemotePath.Should().Be("b.fits");

            telescope.Count("RunSampleCapture").Should().Be(2);
        }

        [Test]
        public async Task TakeFrame_AFrameWithThePreviousSettingsIsAskedForAgain() {
            await backend.ConnectAsync();
            telescope.Files["old.fits"] = OriginFakeTelescope.FitsHeader(10, 800);
            telescope.Files["new.fits"] = OriginFakeTelescope.FitsHeader(1, 800);
            telescope.CaptureResults.Enqueue("old.fits");
            telescope.CaptureResults.Enqueue("new.fits");

            (await backend.TakeFrame(1, 800, 1, Short)).RemotePath.Should().Be("new.fits");
        }

        [Test]
        public async Task TakeFrame_TimesOutWhenTheCameraHangs() {
            await backend.ConnectAsync();
            var act = () => backend.TakeFrame(1, 800, 1, TimeSpan.FromMilliseconds(100));
            await act.Should().ThrowAsync<OriginException>().WithMessage("*No image arrived*");
        }

        [Test]
        public async Task TakeFrame_SaysSoWhenADeviceInsideTheTelescopeIsNotAnswering() {
            await backend.ConnectAsync();
            await telescope.Push(new JObject { ["Source"] = "Focuser", ["Command"] = "GetStatus", ["ErrorCode"] = -809, ["ErrorMessage"] = "Timeout" });
            await WaitFor(() => backend.AuxLostDevices.ContainsKey("Focuser"));
            var act = () => backend.TakeFrame(1, 800, 1, TimeSpan.FromMilliseconds(100));
            await act.Should().ThrowAsync<OriginException>().WithMessage("*no answer from Focuser*");
        }

        [Test]
        public async Task TakeFrame_FailsAtOnceWhenTheLinkDrops() {
            await backend.ConnectAsync();
            var capture = backend.TakeFrame(60, 800, 1, TimeSpan.FromMinutes(2));
            await WaitFor(() => telescope.Count("RunSampleCapture") == 1);
            telescope.Drop();
            var act = () => capture.WaitAsync(TimeSpan.FromSeconds(5));
            await act.Should().ThrowAsync<OriginException>().WithMessage("*lost*");
        }

        [Test]
        public async Task TakeFrame_AFailedDownloadIsAnError() {
            await backend.ConnectAsync();
            telescope.CaptureResults.Enqueue("missing.fits");
            var act = () => backend.TakeFrame(1, 800, 1, Short);
            await act.Should().ThrowAsync<OriginException>().WithMessage("*missing.fits*");
        }

        [Test]
        public async Task LiveImages_AreNotDownloaded() {
            await backend.ConnectAsync();
            var capture = backend.TakeFrame(1, 800, 1, TimeSpan.FromMilliseconds(200));
            await WaitFor(() => telescope.Count("RunSampleCapture") == 1);
            await telescope.Push(new JObject { ["Source"] = "ImageServer", ["Command"] = "NewImageReady", ["ImageType"] = "LIVE", ["FileLocation"] = "Images/Temp/5.jpg" });
            var act = () => capture;
            await act.Should().ThrowAsync<OriginException>().WithMessage("*No image arrived*");
        }

        [Test]
        public void FitsHeader_ReadsNumbersAndSaysWhenOneIsAbsent() {
            var header = OriginFakeTelescope.FitsHeader(6.5, 800);
            OriginBackend.FitsHeaderNumber(header, "EXPTIME").Should().Be(6.5);
            OriginBackend.FitsHeaderNumber(header, "ISOSPEED").Should().Be(800);
            OriginBackend.FitsHeaderNumber(header, "NOSUCH").Should().Be(double.NaN);
            OriginBackend.FitsHeaderNumber(new byte[10], "EXPTIME").Should().Be(double.NaN);
        }

        [Test]
        public async Task CancelCapture_ReportsWhetherTheTelescopeLeftTheCapture() {
            await backend.ConnectAsync();
            await PushTaskState("SAMPLE_CAPTURE", sequence: 1);
            await WaitFor(() => backend.LastMessage("TaskController/GetStatus") != null);

            (await backend.CancelCapture(TimeSpan.FromMilliseconds(100))).Should().BeFalse("no new status has arrived");

            await PushTaskState("SAMPLE_CAPTURE", sequence: 2);
            (await backend.CancelCapture(TimeSpan.FromMilliseconds(100))).Should().BeFalse("a new status still shows the capture");

            telescope.Handlers["CancelImaging"] = command => {
                _ = PushTaskState("IDLE", sequence: 3);
                return new JObject { ["ErrorCode"] = 0 };
            };
            (await backend.CancelCapture(TimeSpan.FromSeconds(5))).Should().BeTrue();
        }

        // ---- discovery ----

        [Test]
        public void Discovery_ReadsTheTelescopesBroadcast() {
            OriginDiscovery.ParseBroadcast("Identity:Origin-140025Z Origin IP Address = 192.168.1.195").Should().Be("192.168.1.195");
        }

        [TestCase("Identity:Other device IP Address = 10.0.0.1")]
        [TestCase("Origin at 10.0.0.1")]
        [TestCase("Origin IP Address unknown")]
        [TestCase("")]
        public void Discovery_IgnoresAnythingElse(string datagram) {
            OriginDiscovery.ParseBroadcast(datagram).Should().BeNull();
            OriginDiscovery.ParseBroadcast(null).Should().BeNull();
        }

        [TestCase("192.168.1.195", "192.168.1.195", 80)]
        [TestCase("localhost:18080", "localhost", 18080)]
        public void Discovery_ReadsTheHostSetting(string value, string host, int port) {
            OriginDiscovery.ParseHost(value).Should().Be((host, port));
        }

        [TestCase("")]
        [TestCase("host:notaport")]
        [TestCase("host:70000")]
        [TestCase(":80")]
        [TestCase("a:b:c")]
        public void Discovery_RefusesABadHostSetting(string value) {
            OriginDiscovery.ParseHost(value).Should().BeNull();
        }

        [Test]
        public async Task Discovery_HearsABroadcastOnTheNetwork() {
            var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            var port = ((IPEndPoint)probe.Client.LocalEndPoint).Port;
            probe.Dispose();
            var listen = OriginDiscovery.ListenForBroadcast(port, TimeSpan.FromSeconds(5));
            using var sender = new UdpClient();
            var bytes = Encoding.UTF8.GetBytes("Identity:Origin-1Z Origin IP Address = 10.1.2.3");
            for (var i = 0; i < 50 && !listen.IsCompleted; i++) {
                await sender.SendAsync(bytes, bytes.Length, new IPEndPoint(IPAddress.Loopback, port));
                await Task.Delay(20);
            }
            (await listen).Should().Be("10.1.2.3");
        }

        [Test]
        public async Task Discovery_TheHostSettingNeedsNoBroadcast() {
            Environment.SetEnvironmentVariable(OriginDiscovery.HostVariable, "10.9.8.7:1234");
            try {
                (await OriginDiscovery.Locate(TimeSpan.FromMinutes(1))).Should().Be(("10.9.8.7", 1234));
            } finally {
                Environment.SetEnvironmentVariable(OriginDiscovery.HostVariable, null);
            }
        }

        [Test]
        public async Task Discovery_NoTelescopeCostsOneWaitNotOneForEachChooser() {
            await OriginDiscovery.Locate(TimeSpan.FromMilliseconds(100));
            var clock = System.Diagnostics.Stopwatch.StartNew();
            await OriginDiscovery.Locate(TimeSpan.FromSeconds(30));
            await OriginDiscovery.Locate(TimeSpan.FromSeconds(30));
            clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
        }

        [Test]
        public async Task Discovery_GivesUpQuietly() {
            var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            var port = ((IPEndPoint)probe.Client.LocalEndPoint).Port;
            probe.Dispose();
            (await OriginDiscovery.ListenForBroadcast(port, TimeSpan.FromMilliseconds(50))).Should().BeNull();
        }

        // ---- the devices ----

        [Test]
        public async Task Devices_ShareOneConnectionAndCloseItWithTheLast() {
            var mount = NewTelescope();
            var focuser = NewFocuser();
            var camera = NewCamera();
            await mount.Connect(default);
            await focuser.Connect(default);
            await camera.Connect(default);
            telescope.Connections.Should().Be(1);

            mount.Disconnect();
            focuser.Disconnect();
            await Task.Delay(50);
            camera.Connected.Should().BeTrue();
            var before = telescope.Count("GetStatus");
            await WaitFor(() => telescope.Count("GetStatus") > before);  // the link is still in use

            camera.Disconnect();
            await Task.Delay(100);
            var last = telescope.Count("GetStatus");
            await Task.Delay(100);
            telescope.Count("GetStatus").Should().Be(last);
        }

        [Test]
        public async Task Device_SaysWhyWhenItCannotConnect() {
            telescope.RefuseConnections = true;
            var mount = NewTelescope();
            var act = () => mount.Connect(default);
            await act.Should().ThrowAsync<OriginException>().WithMessage("*accepts one client*");
            mount.Connected.Should().BeFalse();
        }

        [Test]
        public async Task Device_StaysConnectedThroughAShortDrop() {
            var focuser = NewFocuser();
            await focuser.Connect(default);
            telescope.RefuseConnections = true;
            telescope.Drop();
            await Task.Delay(200);
            focuser.Connected.Should().BeTrue();
            focuser.Disconnect();
        }

        [Test]
        public async Task Device_ReportsDisconnectedAfterALongDrop() {
            var focuser = NewFocuser();
            focuser.LinkLossGrace = TimeSpan.FromMilliseconds(100);
            await focuser.Connect(default);
            telescope.RefuseConnections = true;
            telescope.Drop();
            await WaitFor(() => !focuser.Connected);
        }

        [Test]
        public async Task Mount_ReportsItsPositionInTheUnitsPinsUses() {
            var mount = NewTelescope();
            telescope.MountStatus = new JObject { ["IsAligned"] = true, ["IsGotoOver"] = true, ["IsTracking"] = true, ["Ra"] = Math.PI / 2, ["Dec"] = Math.PI / 4, ["Alt"] = Math.PI / 6, ["Azm"] = Math.PI, ["Latitude"] = 0.9, ["Longitude"] = 0.002 };
            await mount.Connect(default);

            mount.RightAscension.Should().BeApproximately(6, 1e-9);
            mount.Declination.Should().BeApproximately(45, 1e-9);
            mount.Altitude.Should().BeApproximately(30, 1e-9);
            mount.Azimuth.Should().BeApproximately(180, 1e-9);
            mount.SiteLatitude.Should().BeApproximately(0.9 * 180 / Math.PI, 1e-9);
            mount.AtPark.Should().BeFalse();
            mount.TrackingEnabled.Should().BeTrue();
            mount.AlignmentMode.Should().Be(AlignmentMode.AltAz);
            mount.CanSlew.Should().BeTrue("a daemon flag is no longer involved: the stop is always available");
            mount.Disconnect();
        }

        [Test]
        public async Task Mount_FallsBackToTheProfilesSiteOnDegrees() {
            // The simulator reports the site in degrees.
            telescope.MountStatus["Latitude"] = 52.2;
            telescope.MountStatus["Longitude"] = 0.12;
            var mount = NewTelescope();
            await mount.Connect(default);
            mount.SiteLatitude.Should().Be(52.2);
            mount.SiteLongitude.Should().Be(0.12);
            mount.Disconnect();
        }

        [Test]
        public async Task Mount_RefusesToSlewBeforeItIsInitialised() {
            telescope.MountStatus = new JObject { ["IsAligned"] = false, ["IsGotoOver"] = true };
            var mount = NewTelescope();
            await mount.Connect(default);
            mount.AtPark.Should().BeTrue();
            var act = () => mount.SlewToCoordinates(new Coordinates(Angle.ByHours(1), Angle.ByDegree(10), Epoch.J2000), default);
            await act.Should().ThrowAsync<InvalidOperationException>();
            telescope.Count("GotoRaDec").Should().Be(0);
            mount.Disconnect();
        }

        [Test]
        public async Task Mount_ASlewWaitsForTheFirmwaresCentringAndAutofocus() {
            var mount = NewTelescope();
            mount.SettleGrace = TimeSpan.FromMilliseconds(50);
            await mount.Connect(default);
            await PushTaskState("CENTERING_TARGET");

            var slew = mount.SlewToCoordinates(new Coordinates(Angle.ByHours(1), Angle.ByDegree(10), Epoch.J2000), default);
            await WaitFor(() => telescope.Count("GotoRaDec") == 1);
            await Task.Delay(300);
            slew.IsCompleted.Should().BeFalse();

            await PushTaskState("IDLE");

            (await slew.WaitAsync(Short)).Should().BeTrue();
            mount.Disconnect();
        }

        [Test]
        public async Task Mount_StopEndsTheSlewAndReturnsFalse() {
            var mount = NewTelescope();
            await mount.Connect(default);
            await PushTaskState("CENTERING_TARGET");
            var slew = mount.SlewToCoordinates(new Coordinates(Angle.ByHours(1), Angle.ByDegree(10), Epoch.J2000), default);
            await WaitFor(() => telescope.Count("GotoRaDec") == 1);

            mount.StopSlew();

            (await slew.WaitAsync(Short)).Should().BeFalse();
            telescope.Commands.Should().Contain("HaltTasks");
            mount.Disconnect();
        }

        [Test]
        public async Task Mount_CancellingASlewStopsTheMount() {
            var mount = NewTelescope();
            await mount.Connect(default);
            await PushTaskState("CENTERING_TARGET");
            using var cts = new CancellationTokenSource();
            var slew = mount.SlewToCoordinates(new Coordinates(Angle.ByHours(1), Angle.ByDegree(10), Epoch.J2000), cts.Token);
            await WaitFor(() => telescope.Count("GotoRaDec") == 1);

            cts.Cancel();

            var act = () => slew.WaitAsync(Short);
            await act.Should().ThrowAsync<OperationCanceledException>();
            telescope.Commands.Should().Contain("HaltTasks");
            mount.Disconnect();
        }

        [Test]
        public async Task Mount_ParkIsASlewToPolarisAndClearsOnUnpark() {
            var mount = NewTelescope();
            await mount.Connect(default);
            await PushTaskState("IDLE");

            await mount.Park(default);

            mount.AtPark.Should().BeTrue();
            var sent = telescope.Received.Single(m => m.Value<string>("Command") == "GotoRaDec");
            sent.Value<double>("Dec").Should().BeApproximately(89.264 * Math.PI / 180, 1e-9);
            await mount.Unpark(default);
            mount.AtPark.Should().BeFalse();
            telescope.Count("RunInitialize").Should().Be(0);
            mount.Disconnect();
        }

        [Test]
        public async Task Mount_UnparkInitialisesAnUnalignedMount() {
            telescope.MountStatus = new JObject { ["IsAligned"] = false, ["IsGotoOver"] = true };
            var mount = NewTelescope();
            await mount.Connect(default);
            telescope.Handlers["RunInitialize"] = command => {
                telescope.MountStatus = new JObject { ["IsAligned"] = true, ["IsGotoOver"] = true };
                _ = PushTaskState("INITIALIZING", sequence: 1).ContinueWith(_ => PushTaskState("IDLE", sequence: 2));
                return new JObject { ["ErrorCode"] = 0 };
            };

            await mount.Unpark(default);

            mount.AtPark.Should().BeFalse();
            mount.Disconnect();
        }

        [Test]
        public async Task Mount_UnparkRaisesALowTubeFirst() {
            telescope.MountStatus = new JObject { ["IsAligned"] = false, ["IsGotoOver"] = true };
            var mount = NewTelescope();
            await mount.Connect(default);
            var keepReporting = new CancellationTokenSource();
            var altitude = 20.0;
            _ = Task.Run(async () => {
                while (!keepReporting.IsCancellationRequested) {
                    await telescope.Push(new JObject { ["Source"] = "OrientationSensor", ["Command"] = "GetStatus", ["Altitude"] = altitude });
                    await Task.Delay(20);
                }
            });
            telescope.Handlers["Slew"] = c => {
                if (c.Value<int>("AltRate") > 0) {
                    altitude = 62;
                }
                return new JObject { ["ErrorCode"] = 0 };
            };
            telescope.Handlers["RunInitialize"] = command => {
                telescope.MountStatus = new JObject { ["IsAligned"] = true, ["IsGotoOver"] = true };
                _ = PushTaskState("INITIALIZING", sequence: 1).ContinueWith(_ => PushTaskState("IDLE", sequence: 2));
                return new JObject { ["ErrorCode"] = 0 };
            };
            await Task.Delay(50);

            await mount.Unpark(default);
            keepReporting.Cancel();

            var commands = telescope.Commands.ToList();
            commands.IndexOf("Slew").Should().BeLessThan(commands.IndexOf("RunInitialize"));
            mount.Disconnect();
        }

        [Test]
        public async Task Mount_FakeInitialiseSkipsTheAlignmentAndDoesNotMoveALowTube() {
            // As on the real telescope when already level (2026-10-15, 2026-10-31): over at once, no INITIALIZING seen, aligned a moment later. Any move is the firmware's own (2026-11-07); pins sends no Slew.
            telescope.MountStatus = new JObject { ["IsAligned"] = false, ["IsGotoOver"] = true };
            var mount = NewTelescope();
            mount.FakeInitialize = true;
            await mount.Connect(default);
            await telescope.Push(new JObject { ["Source"] = "OrientationSensor", ["Command"] = "GetStatus", ["Altitude"] = 20.0 });
            await Task.Delay(100);
            telescope.Handlers["RunInitialize"] = command => {
                _ = Task.Delay(500).ContinueWith(_ => telescope.MountStatus = new JObject { ["IsAligned"] = true, ["IsGotoOver"] = true, ["IsTracking"] = true });
                _ = PushTaskState("IDLE", sequence: 1, extra: new JObject { ["IsFakeInitialized"] = true });
                return new JObject { ["ErrorCode"] = 0 };
            };
            var took = System.Diagnostics.Stopwatch.StartNew();

            await mount.Unpark(default);

            took.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10), "it should not sit out the 15 s start grace");
            mount.AtPark.Should().BeFalse();
            telescope.Received.Single(m => m.Value<string>("Command") == "RunInitialize").Value<bool>("FakeInitialize").Should().BeTrue();
            telescope.Count("Slew").Should().Be(0);
            mount.Disconnect();
        }

        [Test]
        public async Task Mount_FakeInitialiseThatNeverAlignsIsAFailure() {
            telescope.MountStatus = new JObject { ["IsAligned"] = false, ["IsGotoOver"] = true };
            var mount = NewTelescope();
            mount.FakeInitialize = true;
            mount.FakeAlignedWait = TimeSpan.FromMilliseconds(500);
            await mount.Connect(default);
            telescope.Handlers["RunInitialize"] = command => {
                _ = PushTaskState("IDLE", sequence: 1, extra: new JObject { ["IsFakeInitialized"] = true });
                return new JObject { ["ErrorCode"] = 0 };
            };

            var act = () => mount.Unpark(default);

            await act.Should().ThrowAsync<OriginException>().WithMessage("*did not report aligned*");
            mount.Disconnect();
        }

        [Test]
        public void Mount_FakeInitialiseIsOffWhenTheVariableIsNotSet() {
            OriginTelescope.FakeInitializeRequested(null).Should().BeFalse();
        }

        [TestCase("", false)]
        [TestCase("0", false)]
        [TestCase("false", false)]
        [TestCase("1", true)]
        [TestCase("true", true)]
        [TestCase(" TRUE ", true)]
        public void Mount_FakeInitialiseIsOnlyOnWhenAskedFor(string value, bool expected) {
            OriginTelescope.FakeInitializeRequested(value).Should().Be(expected);
        }

        [Test]
        public async Task Mount_UnparkRefusesAnUnsetSite() {
            telescope.MountStatus = new JObject { ["IsAligned"] = false, ["IsGotoOver"] = true };
            profile.SetupGet(p => p.ActiveProfile.AstrometrySettings.Latitude).Returns(0);
            profile.SetupGet(p => p.ActiveProfile.AstrometrySettings.Longitude).Returns(0);
            var mount = NewTelescope();
            await mount.Connect(default);
            var act = () => mount.Unpark(default);
            await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*site*");
            telescope.Count("RunInitialize").Should().Be(0);
            mount.Disconnect();
        }

        [TestCase(0.0, 0)]
        [TestCase(4.0, 9)]
        [TestCase(-4.0, -9)]
        [TestCase(100.0, 9)]
        [TestCase(1.0, 7)]
        [TestCase(2.0, 8)]
        [TestCase(0.27, 6)]
        [TestCase(0.004, 2)]
        [TestCase(0.001, 1)]
        [TestCase(-0.001, -1)]
        public void Mount_AxisRatesMapOntoTheFirmwaresSteps(double degreesPerSecond, int expected) {
            OriginTelescope.FirmwareRate(degreesPerSecond).Should().Be(expected);
        }

        [Test]
        public async Task Mount_MoveAxisWorksBeforeInitialisationAndKeepsTheOtherAxis() {
            telescope.MountStatus = new JObject { ["IsAligned"] = false, ["IsGotoOver"] = true };
            var mount = NewTelescope();
            await mount.Connect(default);
            mount.CanMoveSecondaryAxis.Should().BeTrue();

            mount.MoveAxis(TelescopeAxes.Secondary, 4);
            mount.MoveAxis(TelescopeAxes.Primary, -4);
            mount.MoveAxis(TelescopeAxes.Secondary, 0);
            mount.MoveAxis(TelescopeAxes.Primary, 0);

            var slews = telescope.Received.Where(m => m.Value<string>("Command") == "Slew").Select(m => (m.Value<int>("AltRate"), m.Value<int>("AzmRate"))).ToList();
            slews.Should().Equal((9, 0), (9, -9), (0, -9), (0, 0));
            telescope.Count("RunInitialize").Should().Be(0);
            mount.Disconnect();
        }

        [Test]
        public async Task Mount_StopForgetsTheAxisRates() {
            var mount = NewTelescope();
            await mount.Connect(default);
            mount.MoveAxis(TelescopeAxes.Secondary, 4);

            mount.StopSlew();
            mount.MoveAxis(TelescopeAxes.Primary, 4);

            var last = telescope.Received.Last(m => m.Value<string>("Command") == "Slew");
            last.Value<int>("AltRate").Should().Be(0, "the altitude was stopped");
            last.Value<int>("AzmRate").Should().Be(9);
            mount.Disconnect();
        }

        [Test]
        public async Task Mount_DoesNotClaimASyncItCannotDo() {
            var mount = NewTelescope();
            await mount.Connect(default);
            mount.Sync(new Coordinates(Angle.ByHours(1), Angle.ByDegree(10), Epoch.J2000)).Should().BeFalse();
            telescope.Count("SyncToRaDec").Should().Be(0);
            mount.Disconnect();
        }

        [Test]
        public async Task Focuser_ReportsPositionFromTheTelescopesNotifications() {
            var focuser = NewFocuser();
            await focuser.Connect(default);
            // A real notification, websocket_log_20261027_164003.txt
            await telescope.SendRaw("""{"Command":"GetStatus","Destination":"All","ExpiredAt":1793119204000,"IsCalibrationComplete":true,"IsMoveToOver":true,"Position":19031,"SequenceID":3021,"Source":"Focuser","Type":"Notification","Velocity":0.0}""");
            await WaitFor(() => focuser.Position == 19031);
            focuser.IsMoving.Should().BeFalse();
            focuser.Disconnect();
        }

        [Test]
        public async Task Focuser_ShowsTheFirmwaresAutofocusAsMoving() {
            var focuser = NewFocuser();
            await focuser.Connect(default);
            await PushTaskState("INITIALIZING", extra: new JObject { ["InitializationInfo"] = new JObject { ["CurrentStep"] = "FOCUSING" } });
            await WaitFor(() => focuser.IsMoving);

            await PushTaskState("IDLE");
            await WaitFor(() => !focuser.IsMoving);
            focuser.Disconnect();
        }

        // The focuser's status as origin1.txt shows it: what the telescope pushes about once a second, position and whether the move is over.
        private Task PushFocuser(int position, bool over) {
            return telescope.SendRaw($$$"""{"Command":"GetStatus","Destination":"All","ExpiredAt":1765967981000,"IsCalibrationComplete":true,"IsMoveToOver":{{{(over ? "true" : "false")}}},"Position":{{{position}}},"SequenceID":150897,"Source":"Focuser","Type":"Notification","Velocity":0.0}""");
        }

        // Replays a move from the capture, one status every 20 ms instead of every second.
        private void ReplayOnMoveTo(params (int Position, bool Over)[] statuses) {
            telescope.Handlers["MoveTo"] = _ => {
                Task.Run(async () => {
                    foreach (var (position, over) in statuses) {
                        await Task.Delay(20);
                        await PushFocuser(position, over);
                    }
                });
                return new JObject { ["ErrorCode"] = 0, ["ErrorMessage"] = string.Empty, ["ExpiredAt"] = 0 };
            };
        }

        [Test]
        public void Focuser_TheCommandIsTheOneInTheCapture() {
            // origin1.txt, 634.794614 s: {'Command': 'MoveTo'; 'Destination': 'Focuser'; 'SequenceID': 37; 'Source': 'OriginMobileApp'; 'Type': 'Command'; 'Value': 18596}
            new OriginBackend("h").CreateCommand("MoveTo", "Focuser", new JObject { ["Value"] = 18596 }, out _)
                .Should().Be("""{"Command":"MoveTo","Destination":"Focuser","SequenceID":2000,"Source":"OriginMobileApp","Type":"Command","Value":18596}""");
        }

        [Test]
        public async Task Focuser_TakesItsTravelFromTheTelescope() {
            var focuser = NewFocuser();
            await focuser.Connect(default);
            focuser.MaxStep.Should().Be(37527);
            focuser.MaxIncrement.Should().Be(37527 - 1975);
            focuser.Disconnect();
        }

        [Test]
        public async Task Focuser_ASmallMoveEndsWhenTheTelescopeSaysSo() {
            // origin1.txt, 634.79-636.71 s
            var focuser = NewFocuser();
            await focuser.Connect(default);
            await PushFocuser(18496, true);
            await WaitFor(() => focuser.Position == 18496);
            ReplayOnMoveTo((18579, false), (18596, true));

            await focuser.Move(18596, default);

            focuser.Position.Should().Be(18596);
            focuser.IsMoving.Should().BeFalse();
            telescope.Received.Single(m => m.Value<string>("Command") == "MoveTo").Value<int>("Value").Should().Be(18596);
            telescope.Received.Should().NotContain(m => m.Value<string>("Destination") == "Focuser" && m.Value<string>("Command") == "GetStatus");
            focuser.Disconnect();
        }

        [Test]
        public async Task Focuser_AMoveDownIsNotOverWhenItPassesTheTarget() {
            // origin1.txt, 646.90-657.87 s: the firmware goes through 17000 to 16511 and comes back up, taking up backlash.
            var focuser = NewFocuser();
            await focuser.Connect(default);
            await PushFocuser(18596, true);
            await WaitFor(() => focuser.Position == 18596);
            ReplayOnMoveTo((18495, false), (18144, false), (17790, false), (17436, false), (17081, false), (16725, false), (16511, false), (16543, false), (16831, false), (16999, false), (17000, true));

            var move = focuser.Move(17000, default);
            await WaitFor(() => focuser.IsMoving);
            await move;

            focuser.Position.Should().Be(17000);
            focuser.Disconnect();
        }

        [Test]
        public async Task Focuser_AStatusFromBeforeTheMoveIsNotItsEnd() {
            // Sent before the telescope acted on the command and arriving after the acknowledgement: over, at the old position.
            var focuser = NewFocuser();
            await focuser.Connect(default);
            await PushFocuser(18496, true);
            await WaitFor(() => focuser.Position == 18496);
            ReplayOnMoveTo((18496, true), (18496, true), (18579, false), (18596, true));

            await focuser.Move(18596, default);

            focuser.Position.Should().Be(18596);
            focuser.Disconnect();
        }

        [Test]
        public async Task Focuser_AMoveQuickerThanOneStatusStillEnds() {
            var focuser = NewFocuser();
            await focuser.Connect(default);
            await PushFocuser(18496, true);
            await WaitFor(() => focuser.Position == 18496);
            ReplayOnMoveTo((18506, true));

            await focuser.Move(18506, default).WaitAsync(Short);

            focuser.Position.Should().Be(18506);
            focuser.Disconnect();
        }

        [Test]
        public async Task Focuser_AMoveToWhereItAlreadyIsEnds() {
            var focuser = NewFocuser();
            await focuser.Connect(default);
            await PushFocuser(18496, true);
            await WaitFor(() => focuser.Position == 18496);
            ReplayOnMoveTo((18496, true));

            await focuser.Move(18496, default).WaitAsync(Short);
            focuser.Disconnect();
        }

        [TestCase(1974)]
        [TestCase(37528)]
        public async Task Focuser_RefusesAPositionOutsideItsTravel(int position) {
            var focuser = NewFocuser();
            await focuser.Connect(default);
            var act = () => focuser.Move(position, default);
            await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
            telescope.Count("MoveTo").Should().Be(0);
            focuser.Disconnect();
        }

        [Test]
        public async Task Focuser_RefusesToMoveWithoutKnowingItsTravel() {
            telescope.Handlers["GetPositionLimits"] = _ => new JObject { ["ErrorCode"] = 0 };
            var focuser = NewFocuser();
            await focuser.Connect(default);
            focuser.Connected.Should().BeTrue();
            var act = () => focuser.Move(18000, default);
            await act.Should().ThrowAsync<OriginException>().WithMessage("*travel limits*");
            telescope.Count("MoveTo").Should().Be(0);
            focuser.Disconnect();
        }

        [Test]
        public async Task Focuser_ARefusalCarriesTheTelescopesMessage() {
            var focuser = NewFocuser();
            await focuser.Connect(default);
            telescope.Handlers["MoveTo"] = _ => new JObject { ["ErrorCode"] = -809, ["ErrorMessage"] = "Timeout" };
            var act = () => focuser.Move(18000, default);
            await act.Should().ThrowAsync<OriginException>().WithMessage("*Timeout*");
            focuser.IsMoving.Should().BeFalse();
            focuser.Disconnect();
        }

        [Test]
        public async Task Backend_AFocuserThatNeverStartsIsAnError() {
            backend.FocuserPollInterval = TimeSpan.FromMilliseconds(5);
            backend.FocuserStartGrace = TimeSpan.FromMilliseconds(100);
            await backend.ConnectAsync();
            await PushFocuser(18496, true);
            await WaitFor(() => backend.Data.Focuser.Position == 18496);
            var report = Task.Run(async () => {
                for (var i = 0; i < 50; i++) {
                    await Task.Delay(10);
                    await PushFocuser(18496, true);
                }
            });
            var act = () => backend.MoveFocuser(18596);
            await act.Should().ThrowAsync<OriginException>().WithMessage("*did not start*");
            await report;
        }

        [Test]
        public async Task Backend_AFocuserThatNeverArrivesIsAnError() {
            backend.FocuserPollInterval = TimeSpan.FromMilliseconds(5);
            backend.FocuserSettleMargin = TimeSpan.FromMilliseconds(200);
            await backend.ConnectAsync();
            await PushFocuser(18496, true);
            await WaitFor(() => backend.Data.Focuser.Position == 18496);
            telescope.Handlers["MoveTo"] = command => {
                _ = PushFocuser(18500, false);
                return new JObject { ["ErrorCode"] = 0 };
            };
            var act = () => backend.MoveFocuser(18510);
            await act.Should().ThrowAsync<OriginException>().WithMessage("*did not reach*");
        }

        [Test]
        public async Task Backend_AMoveFailsAtOnceWhenTheLinkDrops() {
            await backend.ConnectAsync();
            await PushFocuser(18000, true);
            await WaitFor(() => backend.Data.Focuser.Position == 18000);
            var move = backend.MoveFocuser(30000);
            await WaitFor(() => telescope.Count("MoveTo") == 1);
            telescope.Drop();
            var act = () => move.WaitAsync(TimeSpan.FromSeconds(5));
            await act.Should().ThrowAsync<OriginException>().WithMessage("*lost*");
        }

        [Test]
        public async Task Backend_CancellingStopsTheWaitingAndSendsNothingToTheFocuser() {
            // No stop command is known: the focuser finishes what it was told.
            await backend.ConnectAsync();
            await PushFocuser(18000, true);
            await WaitFor(() => backend.Data.Focuser.Position == 18000);
            using var cts = new CancellationTokenSource();
            var move = backend.MoveFocuser(30000, cts.Token);
            await WaitFor(() => telescope.Count("MoveTo") == 1);

            cts.Cancel();

            var act = () => move;
            await act.Should().ThrowAsync<OperationCanceledException>();
            telescope.Received.Count(m => m.Value<string>("Destination") == "Focuser" && m.Value<string>("Command") == "MoveTo").Should().Be(1);
        }

        [Test]
        public async Task Backend_AMoveWaitsForTheOneBefore() {
            await backend.ConnectAsync();
            await PushFocuser(18000, false);
            await WaitFor(() => !backend.Data.Focuser.IsMoveToOver);
            ReplayOnMoveTo((18100, true));

            var move = backend.MoveFocuser(18100);
            await Task.Delay(200);
            telescope.Count("MoveTo").Should().Be(0);

            await PushFocuser(18000, true);
            await move.WaitAsync(Short);
            telescope.Count("MoveTo").Should().Be(1);
        }

        [Test]
        public async Task Backend_AMoveWaitsForTheFirmwaresOwnTaskAndGivesUp() {
            backend.FocuserBusyWait = TimeSpan.FromMilliseconds(100);
            await backend.ConnectAsync();
            await PushTaskState("CENTERING_TARGET");
            await WaitFor(() => backend.Data.TaskController.State == "CENTERING_TARGET");

            var act = () => backend.MoveFocuser(18100);

            await act.Should().ThrowAsync<OriginException>().WithMessage("*CENTERING_TARGET*");
            telescope.Count("MoveTo").Should().Be(0);
        }

        [Test]
        public async Task Focuser_HaltSendsNothing() {
            var focuser = NewFocuser();
            await focuser.Connect(default);
            focuser.Halt();
            telescope.Received.Should().NotContain(m => m.Value<string>("Destination") == "Focuser" && m.Value<string>("Command") != "GetPositionLimits");
            focuser.Disconnect();
        }

        [Test]
        public async Task Camera_AnExposureGoesThroughAFileThatIsRemovedAfterwards() {
            var images = new Mock<IImageDataFactory>();
            var exposures = new Mock<IExposureDataFactory>();
            string loaded = null;
            byte[] loadedBytes = null;
            images.Setup(f => f.CreateFromFile(It.IsAny<string>(), 16, true, It.IsAny<RawConverterEnum>(), It.IsAny<CancellationToken>()))
                .Callback<string, int, bool, RawConverterEnum, CancellationToken>((path, _, _, _, _) => {
                    loaded = path;
                    loadedBytes = File.ReadAllBytes(path);
                })
                .ReturnsAsync(new Mock<IImageData>().Object);
            var camera = NewCamera(images, exposures);
            await camera.Connect(default);
            camera.Gain = 400;
            telescope.Files["Images/Temp/sample0.fits"] = OriginFakeTelescope.FitsHeader(1, 400);
            telescope.CaptureResults.Enqueue("Images/Temp/sample0.fits");

            camera.StartExposure(Light(1));
            camera.CameraState.Should().Be(CameraStates.Exposing);
            await camera.WaitUntilExposureIsReady(default);
            await camera.DownloadExposure(default);

            loaded.Should().EndWith(".fits");
            loadedBytes.Should().Equal(telescope.Files["Images/Temp/sample0.fits"]);
            File.Exists(loaded).Should().BeFalse();
            camera.CameraState.Should().Be(CameraStates.Idle);
            telescope.Received.Single(m => m.Value<string>("Command") == "RunSampleCapture").Value<int>("ISO").Should().Be(400);
            camera.Disconnect();
        }

        [Test]
        public async Task Camera_TwoPngsAreAFailedExposureNotAnImage() {
            var camera = NewCamera();
            await camera.Connect(default);
            telescope.Files["a.png"] = new byte[4];
            telescope.CaptureResults.Enqueue("a.png");
            telescope.CaptureResults.Enqueue("a.png");

            camera.StartExposure(Light(1));

            var act = () => camera.WaitUntilExposureIsReady(default);
            await act.Should().ThrowAsync<OriginException>().WithMessage("*a.png*");
            camera.CameraState.Should().Be(CameraStates.Error);
            camera.Disconnect();
        }

        [Test]
        public async Task Camera_RejectsASecondExposureWhileOneIsRunning() {
            var camera = NewCamera();
            await camera.Connect(default);
            camera.StartExposure(Light(30));
            var act = () => camera.StartExposure(Light(30));
            act.Should().Throw<InvalidOperationException>();
            camera.AbortExposure();
            camera.Disconnect();
        }

        [Test]
        public async Task Camera_AbortCancelsTheCaptureOnTheTelescope() {
            var camera = NewCamera();
            await camera.Connect(default);
            camera.StartExposure(Light(30));
            await WaitFor(() => telescope.Count("RunSampleCapture") == 1);

            camera.AbortExposure();

            await WaitFor(() => telescope.Count("CancelImaging") == 1);
            camera.CameraState.Should().Be(CameraStates.Idle);
            camera.Disconnect();
        }

        [Test]
        public async Task Camera_DisconnectingMidExposureCancelsTheCapture() {
            var camera = NewCamera();
            await camera.Connect(default);
            camera.StartExposure(Light(30));
            await WaitFor(() => telescope.Count("RunSampleCapture") == 1);

            camera.Disconnect();

            await WaitFor(() => telescope.Count("CancelImaging") == 1);
        }

        [Test]
        public async Task Camera_TheNextExposureWaitsForTheCancelToBeAnswered() {
            var camera = NewCamera();
            await camera.Connect(default);
            camera.StartExposure(Light(30));
            await WaitFor(() => telescope.Count("RunSampleCapture") == 1);
            await PushTaskState("SAMPLE_CAPTURE", sequence: 1);
            telescope.Handlers["CancelImaging"] = _ => null;

            camera.AbortExposure();
            await WaitFor(() => telescope.Count("CancelImaging") == 1);
            camera.StartExposure(Light(30));

            await Task.Delay(300);
            telescope.Count("RunSampleCapture").Should().Be(1);
            camera.Disconnect();
        }

        [Test]
        public async Task Camera_ReportsTheSensorTemperature() {
            var camera = NewCamera();
            await camera.Connect(default);
            await telescope.Push(new JObject { ["Source"] = "Environment", ["Command"] = "GetStatus", ["CameraTemperature"] = 19.0 });
            await WaitFor(() => camera.Temperature == 19.0);
            camera.Disconnect();
        }

        [Test]
        public void Camera_DescribesTheSensorInTheRealFrames() {
            var camera = NewCamera();
            camera.CameraXSize.Should().Be(3056);
            camera.CameraYSize.Should().Be(2048);
            camera.SensorType.Should().Be(SensorType.RGGB);
            camera.PixelSizeX.Should().Be(2.4);
            camera.BitDepth.Should().Be(16);
            camera.Gain = 99999;
            camera.Gain.Should().Be(camera.GainMax);
        }

        [Test]
        public async Task Camera_LoadsARealFrame() {
            // A real frame is 12.5 MB and not in the repository: ORIGIN_REAL_FRAME names one.
            var source = Environment.GetEnvironmentVariable("ORIGIN_REAL_FRAME");
            if (string.IsNullOrEmpty(source)) {
                Assert.Ignore("ORIGIN_REAL_FRAME is not set");
            }
            var bytes = File.ReadAllBytes(source);
            var exposure = OriginBackend.FitsHeaderNumber(bytes, "EXPTIME");
            var iso = (int)OriginBackend.FitsHeaderNumber(bytes, "ISOSPEED");
            var factory = new ImageDataFactoryTestUtility();
            var camera = new OriginCamera(profile.Object, factory.ImageDataFactory, factory.ExposureDataFactory, "127.0.0.1", telescope.Port, frameRoot, logDirectory, filterFile);
            await camera.Connect(default);
            camera.Gain = iso;
            telescope.Files["real.fits"] = bytes;
            telescope.CaptureResults.Enqueue("real.fits");

            camera.StartExposure(Light(exposure));
            await camera.WaitUntilExposureIsReady(default);
            var image = await (await camera.DownloadExposure(default)).ToImageData();

            image.Properties.Width.Should().Be(3056);
            image.Properties.Height.Should().Be(2048);
            image.Properties.IsBayered.Should().BeTrue();
            // unsigned 16-bit through BZERO: the black level is about 4985, not a value offset by 32768
            var sorted = image.Data.FlatArray.OrderBy(v => v).ToArray();
            ((int)sorted[sorted.Length / 2]).Should().BeInRange(1000, 60000);
            camera.Disconnect();
        }
    }
}
