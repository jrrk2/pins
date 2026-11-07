#region "copyright"

/*
    Copyright © 2026 the PI.N.S. contributors

    This file is part of PI 'N' Stars.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Test.Equipment.Origin {

    /// <summary>
    /// A telescope on localhost: the WebSocket endpoint and the frame server, with the firmware behaviours that matter and that OriginSimulator lacks. As on real firmware,
    /// a command it does not know is answered "Unknown command" (the simulator acknowledges anything).
    /// </summary>
    internal sealed class OriginFakeTelescope : IDisposable {

        // Everything the real firmware was seen to accept that these tests use.
        private static readonly HashSet<string> Known = new() {
            "GetStatus", "GotoRaDec", "HaltTasks", "Slew", "SetCaptureParameters", "SetDisableLiveStream", "RunSampleCapture", "CancelImaging", "GetFilter", "SetFilter", "RunInitialize", "Reboot", "GetCameraInfo", "MoveTo", "GetPositionLimits"
        };

        private readonly HttpListener listener = new();
        private readonly CancellationTokenSource cts = new();
        private readonly SemaphoreSlim sendLock = new(1, 1);
        private WebSocket client;
        private HttpListenerContext clientContext;

        public OriginFakeTelescope() {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            Port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
            listener.Start();
            _ = AcceptLoop();
        }

        public int Port { get; }
        public int Connections;
        public bool RefuseConnections { get; set; }
        public volatile bool ClosedCleanly;
        public ConcurrentQueue<string> RawReceived { get; } = new();
        public ConcurrentDictionary<string, byte[]> Files { get; } = new();

        /// <summary>Replaces the reply to a command. Return null to stay silent.</summary>
        public ConcurrentDictionary<string, Func<JObject, JObject>> Handlers { get; } = new();

        /// <summary>The next files to announce after a RunSampleCapture. Nothing queued: the capture never finishes.</summary>
        public ConcurrentQueue<string> CaptureResults { get; } = new();

        public JObject MountStatus { get; set; } = new() { ["IsAligned"] = true, ["IsGotoOver"] = true, ["IsTracking"] = true, ["Ra"] = 1.0, ["Dec"] = 0.5, ["Alt"] = 0.9, ["Azm"] = 3.0, ["Latitude"] = 0.91, ["Longitude"] = 0.002 };

        public IEnumerable<JObject> Received => RawReceived.Select(JObject.Parse);

        public IEnumerable<string> Commands => Received.Select(m => m.Value<string>("Command"));

        public int Count(string command) => Commands.Count(c => c == command);

        public void Dispose() {
            cts.Cancel();
            try {
                listener.Close();
            } catch (ObjectDisposedException) {
            }
        }

        /// <summary>The link drops, as in a reboot or a Wi-Fi loss.</summary>
        public void Drop() {
            client?.Abort();
            clientContext?.Response.Abort();
        }

        public async Task Push(JObject message) {
            message["Type"] ??= "Notification";
            message["Destination"] ??= "All";
            message["SequenceID"] ??= 1;
            await SendRaw(message.ToString(Newtonsoft.Json.Formatting.None));
        }

        public Task SendRaw(string text) => Send(client, text);

        private async Task Send(WebSocket socket, string text) {
            await sendLock.WaitAsync();
            try {
                await socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, CancellationToken.None);
            } finally {
                sendLock.Release();
            }
        }

        private async Task AcceptLoop() {
            try {
                while (!cts.IsCancellationRequested) {
                    var context = await listener.GetContextAsync();
                    _ = Task.Run(() => Serve(context));
                }
            } catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException) {
            }
        }

        private async Task Serve(HttpListenerContext context) {
            var path = context.Request.Url.AbsolutePath;
            if (context.Request.IsWebSocketRequest && path == "/SmartScope-1.0/mountControlEndpoint" && !RefuseConnections) {
                var ws = (await context.AcceptWebSocketAsync(null)).WebSocket;
                client = ws;
                clientContext = context;
                Interlocked.Increment(ref Connections);
                await Receive(ws);
                return;
            }
            const string prefix = "/SmartScope-1.0/dev2/";
            if (path.StartsWith(prefix) && Files.TryGetValue(path.Substring(prefix.Length), out var data)) {
                await context.Response.OutputStream.WriteAsync(data);
            } else {
                context.Response.StatusCode = 404;
            }
            context.Response.Close();
        }

        private async Task Receive(WebSocket ws) {
            var buffer = new byte[65536];
            try {
                while (ws.State == WebSocketState.Open) {
                    var result = await ws.ReceiveAsync(buffer, cts.Token);
                    if (result.MessageType == WebSocketMessageType.Close) {
                        ClosedCleanly = true;
                        await sendLock.WaitAsync();
                        try {
                            await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, string.Empty, CancellationToken.None);
                        } finally {
                            sendLock.Release();
                        }
                        return;
                    }
                    var text = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    RawReceived.Enqueue(text);
                    await Answer(ws, JObject.Parse(text));
                }
            } catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException) {
            }
        }

        private async Task Answer(WebSocket ws, JObject command) {
            var name = command.Value<string>("Command");
            var destination = command.Value<string>("Destination");
            JObject reply;
            if (Handlers.TryGetValue(name, out var handler)) {
                reply = handler(command);
                if (reply == null) {
                    return;
                }
            } else if (!Known.Contains(name)) {
                reply = new JObject { ["ErrorCode"] = -2, ["ErrorMessage"] = "Unknown command" };
            } else if (name == "GetPositionLimits") {
                // The telescope in origin1.txt
                reply = new JObject { ["ErrorCode"] = 0, ["ErrorMessage"] = string.Empty, ["ExpiredAt"] = 0, ["Max"] = 37527, ["Min"] = 1975 };
            } else if (name == "GetStatus" && destination == "Mount") {
                reply = (JObject)MountStatus.DeepClone();
            } else {
                reply = new JObject { ["ErrorCode"] = 0, ["ErrorMessage"] = string.Empty };
            }
            reply["Command"] = name;
            reply["Source"] = destination;
            reply["Destination"] = command.Value<string>("Source");
            reply["SequenceID"] = command.Value<int>("SequenceID");
            reply["Type"] = "Response";
            await Send(ws, reply.ToString(Newtonsoft.Json.Formatting.None));

            if (name == "RunSampleCapture" && CaptureResults.TryDequeue(out var file)) {
                await Send(ws, new JObject {
                    ["Command"] = "NewImageReady",
                    ["Destination"] = "All",
                    ["ExposureTime"] = command["ExposureTime"],
                    ["FileLocation"] = file,
                    ["ISO"] = command["ISO"],
                    ["ImageType"] = "SAMPLE_CAPTURE",
                    ["SequenceID"] = 1,
                    ["Source"] = "ImageServer",
                    ["Type"] = "Notification"
                }.ToString(Newtonsoft.Json.Formatting.None));
            }
        }

        /// <summary>The header block of a FITS file, with the keywords the backend reads. Enough for everything but the image loader.</summary>
        public static byte[] FitsHeader(double exposure, int iso) {
            var cards = new[] {
                "SIMPLE  =                    T".PadRight(80),
                $"EXPTIME = {exposure.ToString(System.Globalization.CultureInfo.InvariantCulture),20} / seconds".PadRight(80),
                $"ISOSPEED= {iso,20}".PadRight(80),
                "END".PadRight(80)
            };
            return Encoding.ASCII.GetBytes(string.Concat(cards).PadRight(2880));
        }
    }
}
