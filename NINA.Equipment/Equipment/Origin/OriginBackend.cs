#region "copyright"

/*
    Copyright © 2026 the PI.N.S. contributors

    This file is part of PI 'N' Stars.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NINA.Core.Utility;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Equipment.Equipment.Origin {

    /// <summary>A command the telescope refused, did not answer, or could not carry out.</summary>
    public class OriginException : Exception {

        public OriginException(string message, Exception inner = null) : base(message, inner) {
        }
    }

    /// <summary>One frame as the telescope served it.</summary>
    public sealed record OriginFrame(string RemotePath, byte[] Data, double Ra, double Dec, double ExposureSeconds) {
        public bool IsFits => RemotePath.EndsWith(".fits", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The link to a Celestron Origin: a WebSocket carrying JSON commands and status, and plain HTTP for the frames. Ported to C# by its author
    /// from OriginBackend, TelescopeDataProcessor and the device operations of ControlServer in OriginMonitor.
    ///
    /// <para>Manual focus is not ported. In OriginMonitor it derives from Origin Live View, which is GPL, while this project is MPL 2.0. The focuser region below was instead worked
    /// out from a packet capture of the Celestron app, and nothing may be copied into it from OriginMonitor. See NINA.Equipment/ARCHITECTURE.md.</para>
    ///
    /// <para>The telescope accepts one client, so all of the camera, mount and focuser drivers share one instance through <see cref="Acquire"/>.
    /// An OriginHeadless daemon or the Celestron app connected to the same telescope excludes this one.</para>
    /// </summary>
    public sealed class OriginBackend {
        public const int DefaultPort = 80;
        private const int FirstSequenceId = 2000;
        private const int MaxEvents = 500;
        private const long MaxLogBytes = 256L * 1024 * 1024;

        // Files are 12 MB over Wi-Fi.
        private static readonly HttpClient http = new() { Timeout = TimeSpan.FromMinutes(2) };

        private static readonly Dictionary<string, OriginBackend> shared = new();

        private readonly object stateLock = new();
        private readonly SemaphoreSlim connectLock = new(1, 1);
        private readonly SemaphoreSlim sendLock = new(1, 1);
        private readonly SemaphoreSlim captureLock = new(1, 1);
        private readonly ConcurrentDictionary<int, TaskCompletionSource<JObject>> pending = new();
        private readonly ConcurrentDictionary<string, JObject> last = new();
        private readonly ConcurrentDictionary<string, DateTime> auxLost = new();
        private readonly List<JObject> events = new();
        private readonly string logDirectory;
        private readonly string filterFile;

        private ClientWebSocket socket;
        private CancellationTokenSource linkCts;
        private CancellationTokenSource reconnectCts;
        private TaskCompletionSource<OriginFrame> frameWaiter;
        private int nextSequenceId = FirstSequenceId;
        private int statusRotation;
        private volatile bool connected;
        private volatile bool closing;
        private Task receiving = Task.CompletedTask;
        private int linkGeneration;
        private bool wantLink;
        private DateTime? linkLost;
        private int reconnectTries;
        private long eventSeq;
        private string taskError;
        private DateTime taskErrorAt;
        private int useCount;
        private double requestedExposure;
        private int requestedIso;
        private int requestedBitDepth;
        private double lastRequestedExposure;
        private int lastRequestedIso;
        private StreamWriter log;
        private long logBytes;

        internal OriginBackend(string host, int port = DefaultPort, string logDirectory = null, string filterFile = null) {
            Host = host;
            Port = port;
            this.logDirectory = logDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Documents", "CelestronOriginLogs");
            this.filterFile = filterFile ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "origin", "filter");
        }

        public string Host { get; }
        public int Port { get; }
        public bool IsConnected => connected;
        public TelescopeData Data { get; } = new();

        /// <summary>Every JSON message from the telescope, on the receive thread: handlers must not block.</summary>
        public event Action<JObject> MessageReceived;

        public event Action Connected;

        public event Action Disconnected;

        // Waits for a reconnect, overridden by the tests.
        internal TimeSpan FirstReconnectDelay { get; set; } = TimeSpan.FromSeconds(5);

        internal TimeSpan ReconnectDelay { get; set; } = TimeSpan.FromSeconds(15);
        internal TimeSpan SlowReconnectDelay { get; set; } = TimeSpan.FromSeconds(60);
        internal TimeSpan StatusInterval { get; set; } = TimeSpan.FromSeconds(5);
        internal TimeSpan KeepAliveInterval { get; set; } = TimeSpan.FromSeconds(15);
        internal TimeSpan KeepAliveTimeout { get; set; } = TimeSpan.FromSeconds(20);

        #region sharing

        /// <summary>The instance for this telescope, counted until the matching <see cref="Release"/>.</summary>
        public static OriginBackend Acquire(string host, int port = DefaultPort, string logDirectory = null, string filterFile = null) {
            lock (shared) {
                var key = $"{host}:{port}";
                if (!shared.TryGetValue(key, out var backend)) {
                    backend = new OriginBackend(host, port, logDirectory, filterFile);
                    shared[key] = backend;
                }
                backend.useCount++;
                return backend;
            }
        }

        /// <summary>Closes the link when the last device lets go of it.</summary>
        public void Release() {
            lock (shared) {
                if (--useCount > 0) {
                    return;
                }
                shared.Remove($"{Host}:{Port}");
            }
            Disconnect();
        }

        #endregion sharing

        #region connection

        public async Task<bool> ConnectAsync(CancellationToken token = default) {
            await connectLock.WaitAsync(token);
            try {
                if (connected) {
                    return true;
                }
                var ws = new ClientWebSocket();
                // The telescope drops the link when pings stop, whatever TCP is doing, so they keep the link up as well as showing that it is gone: a Wi-Fi loss closes nothing, and only an
                // unanswered ping shows it. The timeout is what makes .NET send real PING frames, as OriginMonitor does every 15 s; with an interval alone it sends unsolicited pongs instead.
                ws.Options.KeepAliveInterval = KeepAliveInterval;
                ws.Options.KeepAliveTimeout = KeepAliveTimeout;
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                var url = new Uri($"ws://{Host}:{Port}/SmartScope-1.0/mountControlEndpoint");
                try {
                    await ws.ConnectAsync(url, timeout.Token);
                } catch (Exception ex) when (ex is WebSocketException or OperationCanceledException) {
                    ws.Dispose();
                    token.ThrowIfCancellationRequested();
                    Logger.Debug($"Origin: could not connect to {url}: {ex.Message}");
                    return false;
                }
                OnSocketOpen(ws);
                return true;
            } finally {
                connectLock.Release();
            }
        }

        private void OnSocketOpen(ClientWebSocket ws) {
            OpenLog();
            WriteLog("SYSTEM", $"Connected to {Host}:{Port}");
            var reconnected = false;
            int attempts;
            double downSeconds = 0;
            CancellationToken linkToken;
            lock (stateLock) {
                socket = ws;
                linkCts = new CancellationTokenSource();
                linkToken = linkCts.Token;
                connected = true;
                attempts = reconnectTries;
                if (wantLink && linkLost.HasValue) {
                    reconnected = true;
                    downSeconds = (DateTime.UtcNow - linkLost.Value).TotalSeconds;
                }
                wantLink = true;
                linkLost = null;
                reconnectTries = 0;
            }
            if (reconnected) {
                last.Clear();          // statuses from before the drop describe a telescope that may have rebooted since
                RecordEvent("reconnected", $"telescope link restored after {downSeconds:0} s ({attempts} attempts)");
            }

            closing = false;
            receiving = ReceiveLoop(ws, linkToken);
            _ = StatusLoop(linkToken);
            Send("GetStatus", "Mount");
            SetCameraBitDepth(16);     // raw 16-bit Bayer snapshots (re-sent after each capture, the camera reverts it)
            SetLiveStreamDisabled(true);
            _ = DeclareConfiguredFilterLogged();
            Connected?.Invoke();
        }

        /// <summary>Closes the link on purpose: it is not reopened.</summary>
        public void Disconnect() {
            ClientWebSocket ws;
            lock (stateLock) {
                wantLink = false;
                linkLost = null;
                reconnectCts?.Cancel();
                reconnectCts = null;
                ws = socket;
            }
            if (ws == null) {
                CloseLog();             // it may have been waiting to reconnect
                return;
            }
            // The telescope has one client slot: tell it that this one is free, without letting a dead link hold up the caller.
            closing = true;
            if (ws.State == WebSocketState.Open) {
                try {
                    Task.Run(() => CloseSocket(ws)).Wait(TimeSpan.FromSeconds(1));
                } catch (AggregateException ex) {
                    Logger.Debug($"Origin: could not close the link cleanly: {ex.InnerException?.Message}");
                }
            }
            OnLinkDown(ws, "closed");
        }

        // Not while a command is being sent, and the socket is only torn down after the telescope has answered: aborting earlier can lose the close on its way out.
        private async Task CloseSocket(ClientWebSocket ws) {
            await sendLock.WaitAsync();
            try {
                await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, string.Empty, CancellationToken.None);
            } finally {
                sendLock.Release();
            }
            await receiving;
        }

        private void OnLinkDown(ClientWebSocket ws, string why) {
            bool wasUp;
            bool reconnect;
            lock (stateLock) {
                if (!ReferenceEquals(ws, socket) || ws == null) {
                    return;
                }
                wasUp = connected;
                connected = false;
                linkGeneration++;
                socket = null;
                linkCts?.Cancel();
                reconnect = wantLink && !linkLost.HasValue;
                if (reconnect) {
                    linkLost = DateTime.UtcNow;
                    reconnectTries = 0;
                }
            }
            ws.Dispose();
            if (!wasUp) {
                return;
            }
            WriteLog("SYSTEM", "Disconnected from telescope");
            foreach (var key in pending.Keys) {
                if (pending.TryRemove(key, out var waiter)) {
                    waiter.TrySetResult(null);
                }
            }
            lock (stateLock) {
                frameWaiter?.TrySetException(new OriginException("The link to the telescope was lost during the capture"));
            }
            if (why != "closed") {
                RecordEvent("disconnected", $"websocket connection to the telescope was lost: {why}");
            }
            auxLost.Clear();
            Disconnected?.Invoke();
            if (reconnect) {
                Logger.Info($"Origin: telescope link lost; reconnecting to {Host} in {FirstReconnectDelay.TotalSeconds:0} s");
                ScheduleReconnect(FirstReconnectDelay);
            } else if (why == "closed") {
                CloseLog();
            }
        }

        // A telescope reboot, asked for or not, or a Wi-Fi drop must not need anybody to reconnect by hand. First after 5 s, then every 15 s, every 60 s after 5 minutes.
        private void ScheduleReconnect(TimeSpan delay) {
            CancellationToken token;
            lock (stateLock) {
                if (!wantLink) {
                    return;
                }
                reconnectCts?.Cancel();
                reconnectCts = new CancellationTokenSource();
                token = reconnectCts.Token;
            }
            _ = Task.Run(async () => {
                try {
                    await Task.Delay(delay, token);
                    int attempt;
                    lock (stateLock) {
                        attempt = ++reconnectTries;
                    }
                    if (await ConnectAsync(token)) {
                        return;
                    }
                    double down;
                    lock (stateLock) {
                        down = linkLost.HasValue ? (DateTime.UtcNow - linkLost.Value).TotalSeconds : 0;
                    }
                    if (attempt == 1 || attempt % 10 == 0) {
                        Logger.Info($"Origin: reconnect attempt {attempt} failed; link down {down:0} s");
                    }
                    ScheduleReconnect(down < 300 ? ReconnectDelay : SlowReconnectDelay);
                } catch (OperationCanceledException) {
                }
            }, CancellationToken.None);
        }

        private async Task ReceiveLoop(ClientWebSocket ws, CancellationToken token) {
            var why = "the telescope closed it";
            var buffer = new byte[64 * 1024];
            using var message = new MemoryStream();
            try {
                while (ws.State == WebSocketState.Open && !token.IsCancellationRequested) {
                    var part = await ws.ReceiveAsync(buffer, token);
                    if (part.MessageType == WebSocketMessageType.Close) {
                        break;
                    }
                    message.Write(buffer, 0, part.Count);
                    if (!part.EndOfMessage) {
                        continue;
                    }
                    if (part.MessageType == WebSocketMessageType.Text) {
                        OnTextMessage(Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length));
                    }
                    message.SetLength(0);
                }
            } catch (OperationCanceledException) when (token.IsCancellationRequested) {
                return;
            } catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or IOException or OperationCanceledException) {
                // An unanswered keep-alive ping aborts the socket, and .NET reports that to a pending receive as a cancellation nobody asked for.
                why = ex is OperationCanceledException ? "no answer to keep-alive pings" : ex.Message;
                RecordEvent("connection_error", why);
            }
            OnLinkDown(ws, closing ? "closed" : why);
        }

        private async Task StatusLoop(CancellationToken token) {
            try {
                while (true) {
                    await Task.Delay(StatusInterval, token);
                    switch (statusRotation++ % 3) {
                        case 0:
                            Send("GetStatus", "Mount");
                            break;

                        case 1:
                            Send("GetStatus", "Environment");
                            break;

                        case 2:
                            Send("GetCaptureParameters", "Camera");
                            break;
                    }
                }
            } catch (OperationCanceledException) {
            }
        }

        #endregion connection

        #region messages

        /// <summary>
        /// The command as the telescope has always received it from OriginMonitor: Qt writes object keys sorted and a whole-numbered double as an
        /// integer. JSON says neither should matter, but this firmware's parser has only been seen working on that form.
        /// </summary>
        internal string CreateCommand(string command, string destination, JObject parameters, out int sequenceId) {
            sequenceId = Interlocked.Increment(ref nextSequenceId) - 1;
            var fields = new SortedDictionary<string, JToken>(StringComparer.Ordinal) {
                ["Command"] = command,
                ["Destination"] = destination,
                ["SequenceID"] = sequenceId,
                ["Source"] = "OriginMobileApp",
                ["Type"] = "Command"
            };
            if (parameters != null) {
                foreach (var p in parameters.Properties()) {
                    fields[p.Name] = WholeAsInteger(p.Value);
                }
            }
            return new JObject(fields.Select(f => new JProperty(f.Key, f.Value))).ToString(Formatting.None);
        }

        private static JToken WholeAsInteger(JToken value) {
            return value.Type == JTokenType.Float && value.Value<double>() is var d && Math.Abs(d) < 1e15 && d == Math.Floor(d) ? new JValue((long)d) : value;
        }

        // A long wait must notice a drop even if the link was reopened between two of its checks: the command it is waiting on went to a telescope that may since have rebooted.
        private int LinkGeneration {
            get {
                lock (stateLock) {
                    return linkGeneration;
                }
            }
        }

        private bool SameLink(int generation) => connected && LinkGeneration == generation;

        /// <summary>Sends without waiting for the answer. Returns false, without sending, if the link is down.</summary>
        public bool Send(string command, string destination, JObject parameters = null) {
            if (!connected) {
                return false;
            }
            _ = SendText(CreateCommand(command, destination, parameters, out _));
            return true;
        }

        private async Task<bool> SendText(string text) {
            ClientWebSocket ws;
            lock (stateLock) {
                ws = socket;
            }
            if (ws == null) {
                return false;
            }
            WriteLog("SEND", text);
            await sendLock.WaitAsync();
            try {
                await ws.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, CancellationToken.None);
                return true;
            } catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or IOException or InvalidOperationException) {
                Logger.Warning($"Origin: could not send: {ex.Message}");
                return false;
            } finally {
                sendLock.Release();
            }
        }

        /// <summary>
        /// Sends and waits for the telescope's response with the same SequenceID. Returns null if none came in time or the link dropped; the
        /// caller decides whether that matters and reads the response's own ErrorCode.
        /// </summary>
        public async Task<JObject> Request(string command, string destination, JObject parameters = null, TimeSpan? timeout = null, CancellationToken token = default) {
            if (!connected) {
                return null;
            }
            var text = CreateCommand(command, destination, parameters, out var sequenceId);
            var waiter = new TaskCompletionSource<JObject>(TaskCreationOptions.RunContinuationsAsynchronously);
            pending[sequenceId] = waiter;
            try {
                if (!await SendText(text)) {
                    return null;
                }
                return await waiter.Task.WaitAsync(timeout ?? TimeSpan.FromSeconds(5), token);
            } catch (TimeoutException) {
                return null;
            } finally {
                pending.TryRemove(sequenceId, out _);
            }
        }

        private async Task<JObject> RequireOk(string command, string destination, JObject parameters, TimeSpan timeout, CancellationToken token) {
            var reply = await Request(command, destination, parameters, timeout, token)
                ?? throw new OriginException($"No answer from the telescope to {command}");
            ThrowOnError(command, reply);
            return reply;
        }

        private static void ThrowOnError(string command, JObject reply) {
            var code = reply.Value<int?>("ErrorCode") ?? 0;
            if (code != 0) {
                throw new OriginException($"The telescope refused {command} (code {code}): {reply.Value<string>("ErrorMessage")}");
            }
        }

        /// <summary>The newest message with this "Source/Command", such as "TaskController/GetStatus".</summary>
        public JObject LastMessage(string key) => last.TryGetValue(key, out var message) ? message : null;

        internal void OnTextMessage(string text) {
            WriteLog("RECV", text);
            JObject message;
            try {
                message = JObject.Parse(text);
            } catch (JsonException) {
                return;
            }
            try {
                HandleMessage(message);
            } catch (Exception ex) {
                // A field of an unexpected type must not stop everything that follows it on the link.
                Logger.Warning($"Origin: could not process a message: {ex.Message}: {text}");
            }
        }

        private void HandleMessage(JObject message) {
            var source = message.Value<string>("Source") ?? string.Empty;
            var command = message.Value<string>("Command") ?? string.Empty;
            var type = message.Value<string>("Type");

            ReportErrors(message, source, command, type);
            Data.Process(message);
            last[$"{source}/{command}"] = message;
            if (source == "TaskController" && !string.IsNullOrEmpty(message.Value<string>("ErrorMessage"))) {
                lock (stateLock) {
                    taskError = message.Value<string>("ErrorMessage");
                    taskErrorAt = DateTime.UtcNow;
                }
            }
            MessageReceived?.Invoke(message);

            if (type == "Response" && message.Value<int?>("SequenceID") is int id && pending.TryRemove(id, out var waiter)) {
                waiter.TrySetResult(message);
            }
            if (source == "ImageServer" && command == "NewImageReady" && type == "Notification") {
                OnImageReady(message);
            }
        }

        private void ReportErrors(JObject o, string source, string command, string type) {
            var errorMessage = o.Value<string>("ErrorMessage") ?? string.Empty;
            var errorCode = o.Value<int?>("ErrorCode") ?? 0;
            var state = o.Value<string>("State") ?? string.Empty;
            var words = $"{command} {type} {state}".ToLowerInvariant();

            // -802 is the telescope computer failing to read a device over its internal aux bus (one garbled reply, the next poll fine). -809 is a device not answering at all (seen
            // for 4 h 40 min on 2026-11-04 on Mount, Focuser and Environment; reboots did not clear it, a power cycle did). One report for the loss and one for the recovery, not one per poll.
            if (errorCode is -802 or -809) {
                if (auxLost.TryAdd(source, DateTime.UtcNow)) {
                    RecordEvent("aux_lost", $"lost communication with aux device {source} ({command}: {errorMessage})", o);
                }
            } else if (errorCode == 0 && errorMessage.Length == 0 && auxLost.TryRemove(source, out var since)) {
                RecordEvent("aux_restored", $"regained communication with aux device {source} after {(DateTime.UtcNow - since).TotalSeconds:0.0} s");
            } else if (errorCode != 0 || errorMessage.Length > 0) {
                RecordEvent("telescope_error", $"{source} {command}: {errorMessage} (code {errorCode})", o);
            } else if (new[] { "error", "fault", "alert", "warn", "fail", "alarm" }.Any(words.Contains)) {
                RecordEvent("telescope_alert", $"{source} {command} {type} {state}".Trim(), o);
            }
        }

        /// <summary>Devices whose internal link is down (-802 or -809 until a clean reply), and since when.</summary>
        public IReadOnlyDictionary<string, DateTime> AuxLostDevices => auxLost;

        /// <summary>Remembers something a supervisor should know. Identical events within 5 s are one event: the telescope repeats status errors every tick.</summary>
        public void RecordEvent(string kind, string text, JObject detail = null) {
            lock (events) {
                if (events.Count > 0) {
                    var previous = events[^1];
                    if (previous.Value<string>("kind") == kind && previous.Value<string>("text") == text
                        && DateTime.UtcNow - previous.Value<DateTime>("time") < TimeSpan.FromSeconds(5)) {
                        return;
                    }
                }
                var entry = new JObject { ["seq"] = ++eventSeq, ["time"] = DateTime.UtcNow, ["kind"] = kind, ["text"] = text };
                if (detail != null) {
                    entry["detail"] = detail;
                }
                events.Add(entry);
                if (events.Count > MaxEvents) {
                    events.RemoveAt(0);
                }
            }
            Logger.Warning($"Origin event {kind}: {text}");
        }

        public IReadOnlyList<JObject> EventsSince(long afterSeq, int limit = 200) {
            lock (events) {
                return events.Where(e => e.Value<long>("seq") > afterSeq).Take(limit).ToList();
            }
        }

        #endregion messages

        #region mount

        public Task<JObject> QueryMount(CancellationToken token = default) => Request("GetStatus", "Mount", null, TimeSpan.FromSeconds(5), token);

        /// <summary>
        /// Slews to J2000 coordinates and returns when the mount reports the goto over. That is not the end of the telescope's work: it then centres
        /// and may autofocus, see <see cref="WaitUntilIdle"/>. Throws if it was refused or did not finish in time.
        /// </summary>
        public async Task<JObject> Goto(double raHours, double decDegrees, TimeSpan timeout, CancellationToken token = default) {
            if (raHours is < 0 or >= 24 || decDegrees is < -90 or > 90) {
                throw new ArgumentOutOfRangeException(nameof(raHours), "Coordinates out of range");
            }
            var link = LinkGeneration;
            await RequireOk("GotoRaDec", "Mount", new JObject { ["Ra"] = raHours * Math.PI / 12, ["Dec"] = decDegrees * Math.PI / 180 }, TimeSpan.FromSeconds(5), token);

            var waited = System.Diagnostics.Stopwatch.StartNew();
            var started = false;                       // IsGotoOver may still be true for a moment after the acknowledgement
            while (waited.Elapsed < timeout) {
                await Task.Delay(GotoPollInterval, token);
                if (!SameLink(link)) {
                    throw new OriginException("The link to the telescope was lost during the slew");
                }
                var mount = await Request("GetStatus", "Mount", null, TimeSpan.FromSeconds(3), token);
                if (mount == null) {
                    continue;
                }
                var over = mount.Value<bool?>("IsGotoOver") ?? false;
                started |= !over;
                if (over && (started || waited.Elapsed > GotoStartGrace)) {
                    return mount;
                }
            }
            throw new OriginException($"The goto did not complete in {timeout.TotalSeconds:0} s");
        }

        internal TimeSpan GotoPollInterval { get; set; } = TimeSpan.FromSeconds(1);
        internal TimeSpan GotoStartGrace { get; set; } = TimeSpan.FromSeconds(8);

        /// <summary>
        /// Waits for the task controller to go back to IDLE after a goto: until it has, the firmware may still be centring or focusing, and a frame taken meanwhile would be trailed.
        /// Returns the last state seen; on a timeout that is not IDLE. <paramref name="keepWaiting"/> ends the wait early.
        /// </summary>
        public async Task<string> WaitUntilIdle(TimeSpan grace, TimeSpan timeout, Func<bool> keepWaiting, TimeSpan? checkInterval = null, CancellationToken token = default) {
            var waited = System.Diagnostics.Stopwatch.StartNew();
            var state = string.Empty;
            // After a goto the controller can still say IDLE for a moment before the centring starts.
            while (keepWaiting() && waited.Elapsed < timeout) {
                state = Data.TaskController.State ?? state;
                if (string.Equals(state, "IDLE", StringComparison.OrdinalIgnoreCase) && waited.Elapsed >= grace) {
                    return state;
                }
                await Task.Delay(checkInterval ?? TimeSpan.FromSeconds(1), token);
            }
            return state;
        }

        /// <summary>
        /// Stops a slew. AbortAxisMovement and StopAll answer "Unknown command" on firmware 1.5.x (OriginMonitor's abortMotion sends the first and so does nothing). HaltTasks is what the
        /// Celestron app sends, and a zero Slew stops a manual motion, so both go out. Throws only if the telescope accepted neither, so that a failed stop is never silent.
        /// </summary>
        public async Task AbortMotion(CancellationToken token = default) {
            var timeout = TimeSpan.FromSeconds(8);
            Exception halt = null, slew = null;
            // Zero speed goes first and is the stop: nothing may delay it, and HaltTasks could wait out its whole timeout.
            try {
                await SlewRates(0, 0, timeout, token);
            } catch (OriginException ex) {
                slew = ex;
                Logger.Error($"Origin: Slew 0/0 failed: {ex.Message}");
            }
            try {
                await RequireOk("HaltTasks", "TaskController", null, timeout, token);
            } catch (OriginException ex) {
                halt = ex;
                Logger.Error($"Origin: HaltTasks failed: {ex.Message}");
            }
            // Twice, 300 ms apart, as ControlServer has always stopped the mount.
            try {
                await Task.Delay(TimeSpan.FromMilliseconds(300), token);
                await SlewRates(0, 0, timeout, token);
                slew = null;
            } catch (OriginException ex) {
                slew ??= ex;
                Logger.Error($"Origin: the second Slew 0/0 failed: {ex.Message}");
            }
            if (halt != null && slew != null) {
                throw new OriginException($"The mount could not be told to stop: {halt.Message}");
            }
        }

        /// <summary>Manual motion on each axis, -9 to 9. A rate is kept until it is replaced: 0, 0 stops.</summary>
        public Task<JObject> SlewRates(int altRate, int azmRate, TimeSpan? timeout = null, CancellationToken token = default) {
            if (Math.Abs(altRate) > 9 || Math.Abs(azmRate) > 9) {
                throw new ArgumentOutOfRangeException(nameof(altRate), "Rates are -9 to 9");
            }
            return RequireOk("Slew", "Mount", new JObject { ["AltRate"] = altRate, ["AzmRate"] = azmRate }, timeout ?? TimeSpan.FromSeconds(5), token);
        }

        /// <summary>SyncToRaDec has no record of being used on real firmware. Throws if the telescope refuses it.</summary>
        public Task SyncPosition(double raHours, double decDegrees, CancellationToken token = default) {
            return RequireOk("SyncToRaDec", "Mount", new JObject { ["Ra"] = raHours * Math.PI / 12, ["Dec"] = decDegrees * Math.PI / 180 }, TimeSpan.FromSeconds(5), token);
        }

        /// <summary>StartTracking and StopTracking have no record of being used on real firmware. Throws if the telescope refuses them.</summary>
        public Task SetTracking(bool enabled, CancellationToken token = default) {
            return RequireOk(enabled ? "StartTracking" : "StopTracking", "Mount", null, TimeSpan.FromSeconds(5), token);
        }

        /// <summary>Firmware moving-target tracking, as the Celestron app's imaging request carries it. Each point is (ra hours, dec degrees, UTC Julian date), J2000 topocentric.</summary>
        public Task TrackMovingTarget(IEnumerable<(double RaHours, double DecDegrees, double JulianDate)> track, CancellationToken token = default) {
            var points = new JArray(track.Select(p => new JObject { ["Ra"] = p.RaHours * Math.PI / 12, ["Dec"] = p.DecDegrees * Math.PI / 180, ["JulianDate"] = p.JulianDate }));
            return RequireOk("TrackMovingTarget", "Mount", new JObject { ["TargetTrack"] = points }, TimeSpan.FromSeconds(8), token);
        }

        /// <summary>
        /// Raises the tube at +AltRate with no azimuth motion until the telescope's tilt sensor reads <paramref name="targetAltitude"/>, checking every half second. Stops on arrival,
        /// after the timeout, or if the tilt reading goes stale, and refuses to move at all without a fresh one. Does not initialise: the azimuth stays unknown. Returns the final tilt.
        /// </summary>
        public async Task<double> RaiseTube(double targetAltitude = 60, int rate = 9, TimeSpan? timeout = null, TimeSpan? maxAge = null, CancellationToken token = default) {
            if (targetAltitude is < 10 or > 85) {
                throw new ArgumentOutOfRangeException(nameof(targetAltitude), "The target altitude must be between 10 and 85 degrees");
            }
            if (rate is < 1 or > 9) {
                throw new ArgumentOutOfRangeException(nameof(rate), "The rate must be 1 to 9");
            }
            var limit = maxAge ?? TimeSpan.FromSeconds(5);
            var fresh = System.Diagnostics.Stopwatch.StartNew();
            while (TiltAge() > limit) {
                if (fresh.Elapsed > TiltWait) {
                    throw new OriginException("No recent tilt reading from the telescope (OrientationSensor); not moving");
                }
                await Task.Delay(TiltCheckInterval, token);
            }
            if (Data.Orientation.Altitude >= targetAltitude) {
                return Data.Orientation.Altitude;
            }

            await SlewRates(rate, 0, token: token);
            var moving = System.Diagnostics.Stopwatch.StartNew();
            var deadline = timeout ?? TimeSpan.FromSeconds(90);
            string problem = null;
            try {
                while (true) {
                    await Task.Delay(TiltCheckInterval, token);
                    if (TiltAge() > limit) {
                        problem = $"the tilt reading went stale ({TiltAge().TotalSeconds:0} s old)";
                        break;
                    }
                    if (Data.Orientation.Altitude >= targetAltitude) {
                        break;
                    }
                    if (moving.Elapsed > deadline) {
                        problem = "timed out before the tilt reached the target";
                        break;
                    }
                }
            } finally {
                // Twice, as a lost stop would leave the tube moving: nothing else limits this motion.
                for (var i = 0; i < 2; i++) {
                    Send("Slew", "Mount", new JObject { ["AltRate"] = 0, ["AzmRate"] = 0 });
                    await Task.Delay(TimeSpan.FromMilliseconds(300), CancellationToken.None);
                }
            }
            if (problem != null) {
                throw new OriginException($"Stopped raising the tube: {problem}");
            }
            return Data.Orientation.Altitude;
        }

        internal TimeSpan TiltWait { get; set; } = TimeSpan.FromSeconds(8);
        internal TimeSpan TiltCheckInterval { get; set; } = TimeSpan.FromMilliseconds(500);

        private TimeSpan TiltAge() => Data.OrientationLastUpdate is DateTime at ? DateTime.UtcNow - at : TimeSpan.MaxValue;

        /// <summary>
        /// RunInitialize with an explicit site and zone: the mount homes and aligns on the stars, which takes minutes. Returns when the telescope is initialised and throws if it refused,
        /// failed or ran out of time. Ending in IDLE is not enough: a failed alignment ends there too ("Did not find enough stars", 2026-10-11), so IsAligned is checked.
        /// </summary>
        public async Task Initialize(double latitude, double longitude, string timeZoneId, bool fake = false, TimeSpan? timeout = null, CancellationToken token = default) {
            if (latitude is < -90 or > 90 || longitude is < -180 or > 180) {
                throw new ArgumentOutOfRangeException(nameof(latitude), "Site out of range");
            }
            TimeZoneInfo zone;
            try {
                zone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
            } catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException) {
                throw new OriginException($"Unknown timezone: {timeZoneId}", ex);
            }
            var now = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, zone);     // wall-clock time in the stated zone
            await DeclareConfiguredFilter(token);                                 // before the mount moves and aligns
            var start = DateTime.UtcNow;
            var link = LinkGeneration;
            var reply = await Request("RunInitialize", "TaskController", new JObject {
                ["Date"] = now.ToString("dd MM yyyy", CultureInfo.InvariantCulture),
                ["Time"] = now.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
                ["TimeZone"] = timeZoneId,
                ["Latitude"] = latitude * Math.PI / 180,
                ["Longitude"] = longitude * Math.PI / 180,
                ["FakeInitialize"] = fake
            }, TimeSpan.FromSeconds(8), token) ?? throw new OriginException("No answer from the telescope to RunInitialize");
            // The firmware starts an initialisation by itself after a reboot (2026-11-07) and refuses a second. A real one can simply be waited for; that one will not be fake.
            var alreadyRunning = !fake && reply.Value<int?>("ErrorCode") == InitializationAlreadyRunning;
            if (alreadyRunning) {
                lock (stateLock) {
                    taskError = null;       // this refusal, recorded like any other task error, must not end the wait
                }
                RecordEvent("initialization_joined", "The telescope was already initialising; waiting for it");
            } else {
                ThrowOnError("RunInitialize", reply);
            }

            var waited = System.Diagnostics.Stopwatch.StartNew();
            var limit = timeout ?? TimeSpan.FromSeconds(300);
            var state = string.Empty;
            var step = string.Empty;
            var percent = 0;
            var seenInitializing = alreadyRunning;
            while (true) {
                if (waited.Elapsed >= limit) {
                    throw new OriginException($"Still {(state.Length == 0 ? "silent" : state)} after {limit.TotalSeconds:0} s (step {step}, {percent}%)");
                }
                await Task.Delay(InitializePollInterval, token);
                if (!SameLink(link)) {
                    throw new OriginException("The link to the telescope was lost during the initialisation");
                }
                lock (stateLock) {
                    if (taskError != null && taskErrorAt >= start) {        // an error that the next status message may already have overwritten
                        throw new OriginException($"The initialisation failed during {(step.Length == 0 ? "an unknown step" : step)}: {taskError}");
                    }
                }
                var status = LastMessage("TaskController/GetStatus");
                state = status?.Value<string>("State") ?? string.Empty;
                if (state.Length == 0) {
                    continue;                                               // no usable status while the mount is busy
                }
                if (status["InitializationInfo"] is JObject info) {
                    step = info.Value<string>("CurrentStep") ?? step;
                    percent = info.Value<int?>("PercentageComplete") ?? percent;
                }
                seenInitializing |= state == "INITIALIZING";
                // A fake initialisation can be over in under half a second (2026-10-15, 2026-10-31), so polling can miss INITIALIZING altogether. It can also take 8 s, moving the mount (2026-11-07).
                if (fake && state == "IDLE" && status.Value<bool?>("IsFakeInitialized") == true) {
                    return;
                }
                // Finished: left INITIALIZING after having been in it, or 15 s in with the state already settled.
                if (state != "INITIALIZING" && (seenInitializing || waited.Elapsed > InitializeStartGrace)) {
                    break;
                }
            }
            if (fake) {
                return;
            }
            var mount = await QueryMount(token);
            if (mount?.Value<bool?>("IsAligned") != true) {
                throw new OriginException($"The initialisation ended in {state} but the mount reports that it is not aligned");
            }
        }

        private const int InitializationAlreadyRunning = -73;

        internal TimeSpan InitializePollInterval { get; set; } = TimeSpan.FromMilliseconds(500);
        internal TimeSpan InitializeStartGrace { get; set; } = TimeSpan.FromSeconds(15);

        /// <summary>
        /// Reboots the telescope: the only command found that clears a camera stuck in SAMPLE_CAPTURE (2026-10-28; CancelImaging is acknowledged and ignored). Accepted even mid-exposure.
        /// The link drops at once, the telescope is off the network for 15-25 s, the link is reopened automatically, and the mount comes back not aligned.
        /// </summary>
        public async Task Reboot(string reason, CancellationToken token = default) {
            RecordEvent("telescope_reboot", reason);
            await RequireOk("Reboot", "System", null, TimeSpan.FromSeconds(8), token);
        }

        #endregion mount

        #region focuser

        // Everything here comes from a capture of the Celestron app driving a real telescope: origin_anal/origin1.txt, 2025-12-17, frames 621-658 s. See NINA.Equipment/ARCHITECTURE.md.

        /// <summary>The focuser's travel, as the telescope states it.</summary>
        public async Task<(int Min, int Max)> GetFocuserLimits(CancellationToken token = default) {
            var reply = await RequireOk("GetPositionLimits", "Focuser", null, TimeSpan.FromSeconds(5), token);
            if (reply.Value<int?>("Min") is int min && reply.Value<int?>("Max") is int max && min < max) {
                return (min, max);
            }
            throw new OriginException("The telescope did not state the focuser's travel limits");
        }

        /// <summary>
        /// Moves the focuser to an absolute position and returns where it stopped, which is what the last status says. Nothing is polled: the focuser reports about once a second by itself,
        /// false in IsMoveToOver during a move and true on arrival. The firmware handles backlash: going down it overshoots by about 500 steps and comes back up.
        ///
        /// The capture shows no way to stop a move, or a move sent during another or during a task, so this waits for those to end. Cancelling stops the waiting, not the focuser.
        /// </summary>
        public async Task<int> MoveFocuser(int target, CancellationToken token = default) {
            await WaitForFocuserToBeFree(token);
            var start = Data.Focuser.Position;
            var link = LinkGeneration;
            await RequireOk("MoveTo", "Focuser", new JObject { ["Value"] = target }, TimeSpan.FromSeconds(5), token);

            var acknowledged = DateTime.UtcNow;
            var waited = System.Diagnostics.Stopwatch.StartNew();
            var limit = TimeSpan.FromSeconds(Math.Abs(target - start) / FocuserStepsPerSecond) + FocuserSettleMargin;
            var started = false;
            while (true) {
                await Task.Delay(FocuserPollInterval, token);
                if (!SameLink(link)) {
                    throw new OriginException("The link to the telescope was lost during the focuser move");
                }
                // A status sent before the telescope acted on the command can still arrive after the acknowledgement, saying over at the old position.
                if (Data.FocuserLastUpdate > acknowledged) {
                    var focuser = Data.Focuser;
                    started |= !focuser.IsMoveToOver;
                    if (focuser.IsMoveToOver && (started || focuser.Position == target || focuser.Position != start)) {
                        if (focuser.Position != target) {
                            Logger.Warning($"Origin: the focuser was sent to {target} and stopped at {focuser.Position}");
                        }
                        return focuser.Position;
                    }
                    if (!started && waited.Elapsed > FocuserStartGrace) {
                        throw new OriginException($"The focuser did not start moving to {target}");
                    }
                }
                if (waited.Elapsed > limit) {
                    var aux = auxLost.ContainsKey("Focuser") ? "; the focuser is not answering inside the telescope" : string.Empty;
                    throw new OriginException($"The focuser did not reach {target} in {limit.TotalSeconds:0} s{aux}");
                }
            }
        }

        private async Task WaitForFocuserToBeFree(CancellationToken token) {
            var waited = System.Diagnostics.Stopwatch.StartNew();
            while (!Data.Focuser.IsMoveToOver || Data.TaskController.State is not (null or "IDLE")) {
                if (waited.Elapsed > FocuserBusyWait) {
                    throw new OriginException($"The focuser is busy: the telescope reports {Data.TaskController.State ?? "a move in progress"}");
                }
                await Task.Delay(FocuserPollInterval, token);
            }
        }

        // The capture: 355 steps a second, plus the overshoot. Slower here, so that this only fires when something is wrong.
        private const double FocuserStepsPerSecond = 250;

        internal TimeSpan FocuserPollInterval { get; set; } = TimeSpan.FromMilliseconds(100);
        internal TimeSpan FocuserStartGrace { get; set; } = TimeSpan.FromSeconds(5);
        internal TimeSpan FocuserSettleMargin { get; set; } = TimeSpan.FromSeconds(20);
        internal TimeSpan FocuserBusyWait { get; set; } = TimeSpan.FromSeconds(15);

        #endregion focuser

        #region filter

        /// <summary>
        /// The filter is inserted by hand, so the firmware only knows what it was told. ~/.config/origin/filter holds "nebula" or "clear"; when present it is declared after every connect and
        /// before every initialize, so that plate solves and centring pick exposures for the filter really fitted. A failure is reported and does not stop what comes next.
        /// </summary>
        public async Task DeclareConfiguredFilter(CancellationToken token = default) {
            string name;
            try {
                name = File.Exists(filterFile) ? File.ReadAllText(filterFile).Trim().ToLowerInvariant() : string.Empty;
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                Logger.Warning($"Origin: could not read {filterFile}: {ex.Message}");
                return;
            }
            if (name is not ("clear" or "nebula")) {
                return;
            }
            try {
                await SetFilter(name, force: false, token);
            } catch (OriginException ex) {
                Logger.Warning($"Origin: filter {name} not declared: {ex.Message}");
            }
        }

        private async Task DeclareConfiguredFilterLogged() {
            try {
                await DeclareConfiguredFilter();
            } catch (Exception ex) {
                Logger.Warning($"Origin: could not declare the filter: {ex.Message}");
            }
        }

        /// <summary>Tells the firmware which filter is fitted ("clear" or "nebula"), and reads it back.</summary>
        public async Task SetFilter(string name, bool force = true, CancellationToken token = default) {
            var declared = name == "clear" ? "Clear" : "Nebula";
            if (!force) {
                var current = await Request("GetFilter", "Camera", null, TimeSpan.FromSeconds(5), token);
                if (current?.Value<string>("Filter") == declared) {
                    return;
                }
            }
            // The Celestron app's capture showed a reply after about 11 s, but that included a prompt in the app: the firmware's own latency is untested.
            await RequireOk("SetFilter", "Camera", new JObject { ["Filter"] = declared }, TimeSpan.FromSeconds(30), token);
            var check = await RequireOk("GetFilter", "Camera", null, TimeSpan.FromSeconds(5), token);
            if (check.Value<string>("Filter") != declared) {
                throw new OriginException($"The telescope reports filter {check.Value<string>("Filter")} after being told {declared}");
            }
        }

        #endregion filter

        #region camera

        public bool SetCameraManualMode() => Send("SetEnableManual", "LiveStream");

        public bool SetCameraAutoMode() {
            requestedExposure = 0;
            requestedIso = 0;
            return Send("SetEnableAuto", "LiveStream");
        }

        // The live stream is never wanted: with it on, a sample capture can wait for the live frame in progress. Sent on every connect (the telescope forgets it when the stream is closed).
        public bool SetLiveStreamDisabled(bool disable) => Send("SetDisableLiveStream", "LiveStream", new JObject { ["Disable"] = disable });

        public bool SetCameraExposure(double seconds) {
            requestedExposure = seconds;
            return Send("SetCaptureParameters", "Camera", new JObject { ["Exposure"] = seconds });
        }

        public bool SetCameraIso(int iso) {
            requestedIso = iso;
            return Send("SetCaptureParameters", "Camera", new JObject { ["ISO"] = iso });
        }

        public bool SetCameraBinning(int binning) => Send("SetCaptureParameters", "Camera", new JObject { ["Binning"] = binning });

        public bool SetCameraBitDepth(int bitDepth) {
            requestedBitDepth = bitDepth;
            return Send("SetCaptureParameters", "Camera", new JObject { ["BitDepth"] = bitDepth });
        }

        public Task<JObject> GetCameraInfo(CancellationToken token = default) => Request("GetCameraInfo", "Camera", null, TimeSpan.FromSeconds(5), token);

        /// <summary>
        /// Takes one raw frame and downloads it. A PNG instead of the FITS (seen on the first frame after a slew, and whenever the telescope has reset the capture parameters) or a header
        /// showing the previous exposure or ISO (seen after a change of either, 2026-10-14) is asked for once more, now that the parameters were resent. A second PNG is returned for the
        /// caller to refuse; check <see cref="OriginFrame.IsFits"/>. One capture at a time: a second call waits for the first.
        /// </summary>
        public async Task<OriginFrame> TakeFrame(double exposureSeconds, int iso, int binning, TimeSpan timeout, CancellationToken token = default) {
            await captureLock.WaitAsync(token);
            try {
                var frame = await CaptureOnce(exposureSeconds, iso, binning, timeout, token);
                var mismatch = FrameSettingsMismatch(frame, exposureSeconds, iso);
                if (frame.IsFits && mismatch == null) {
                    return frame;
                }
                Logger.Info($"Origin: asking again for a frame: {mismatch ?? $"got {frame.RemotePath} instead of a raw FITS"}");
                await Task.Delay(RetryPause, token);
                frame = await CaptureOnce(exposureSeconds, iso, binning, timeout, token);
                mismatch = FrameSettingsMismatch(frame, exposureSeconds, iso);
                if (mismatch != null) {
                    RecordEvent("frame_settings", mismatch);
                }
                return frame;
            } finally {
                captureLock.Release();
            }
        }

        internal TimeSpan RetryPause { get; set; } = TimeSpan.FromMilliseconds(1500);

        private async Task<OriginFrame> CaptureOnce(double exposureSeconds, int iso, int binning, TimeSpan timeout, CancellationToken token) {
            if (!connected) {
                throw new OriginException("Not connected to the telescope");
            }
            var waiter = new TaskCompletionSource<OriginFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (stateLock) {
                frameWaiter = waiter;
            }
            try {
                lastRequestedExposure = exposureSeconds;
                lastRequestedIso = iso;
                requestedExposure = exposureSeconds;
                requestedIso = iso;
                SetCameraBitDepth(16);
                // Older firmware defaulted to a debayered TIFF when "Debayer" was absent, current firmware to raw FITS: ask explicitly so that an update cannot change the file type.
                Send("RunSampleCapture", "TaskController", new JObject { ["ExposureTime"] = exposureSeconds, ["ISO"] = iso, ["Binning"] = binning, ["Debayer"] = false });
                try {
                    return await waiter.Task.WaitAsync(timeout, token);
                } catch (TimeoutException) {
                    var aux = auxLost.IsEmpty ? string.Empty : $"; no answer from {string.Join(", ", auxLost.Keys)} inside the telescope";
                    throw new OriginException($"No image arrived within {timeout.TotalSeconds:0} s{aux}");
                }
            } finally {
                lock (stateLock) {
                    if (ReferenceEquals(frameWaiter, waiter)) {
                        frameWaiter = null;
                    }
                }
            }
        }

        private void OnImageReady(JObject o) {
            var remote = o.Value<string>("FileLocation");
            if (string.IsNullOrEmpty(remote)) {
                return;
            }
            // Live view images (ImageType LIVE, .jpg) are not wanted, and the stream is disabled anyway.
            var isSnapshot = o.Value<string>("ImageType") == "SAMPLE_CAPTURE"
                || remote.EndsWith(".fits", StringComparison.OrdinalIgnoreCase)
                || remote.EndsWith(".tiff", StringComparison.OrdinalIgnoreCase)
                || remote.EndsWith(".tif", StringComparison.OrdinalIgnoreCase);
            if (!isSnapshot) {
                return;
            }
            var ra = o.Value<double?>("Ra") ?? 0;
            var dec = o.Value<double?>("Dec") ?? 0;
            var exposure = o.Value<double?>("ExposureTime") ?? 0;
            _ = Task.Run(() => DownloadFrame(remote, ra, dec, exposure));
        }

        private async Task DownloadFrame(string remote, double ra, double dec, double exposure) {
            TaskCompletionSource<OriginFrame> waiter;
            lock (stateLock) {
                waiter = frameWaiter;
            }
            try {
                var hostPort = Port == 80 ? Host : $"{Host}:{Port}";
                using var request = new HttpRequestMessage(HttpMethod.Get, $"http://{hostPort}/SmartScope-1.0/dev2/{remote}");
                request.Headers.TryAddWithoutValidation("Cache-Control", "no-cache");
                request.Headers.TryAddWithoutValidation("Accept", "*/*");
                using var response = await http.SendAsync(request);
                response.EnsureSuccessStatusCode();
                var data = await response.Content.ReadAsByteArrayAsync();
                ResendCaptureParameters();
                waiter?.TrySetResult(new OriginFrame(remote, data, ra, dec, exposure));
            } catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException) {
                RecordEvent("download_error", ex.Message);
                waiter?.TrySetException(new OriginException($"Could not download {remote}: {ex.Message}", ex));
            }
        }

        // The telescope resets exposure, ISO and bit depth after a sample capture, and a frame taken with the reset values comes back as a PNG instead of the raw FITS.
        private void ResendCaptureParameters() {
            if (requestedExposure > 0) {
                SetCameraExposure(requestedExposure);
            }
            if (requestedIso > 0) {
                SetCameraIso(requestedIso);
            }
            if (requestedBitDepth > 0) {
                SetCameraBitDepth(requestedBitDepth);
            }
        }

        /// <summary>Why a frame's own header shows it was not taken with the settings asked for, or null if it was (or does not say).</summary>
        internal static string FrameSettingsMismatch(OriginFrame frame, double exposureSeconds, int iso) {
            if (!frame.IsFits) {
                return null;
            }
            var headerExposure = FitsHeaderNumber(frame.Data, "EXPTIME");
            var headerIso = FitsHeaderNumber(frame.Data, "ISOSPEED");
            var badExposure = exposureSeconds > 0 && !double.IsNaN(headerExposure) && Math.Abs(headerExposure - exposureSeconds) > 0.02 * exposureSeconds + 1e-7;
            var badIso = iso > 0 && !double.IsNaN(headerIso) && (int)(headerIso + 0.5) != iso;
            return badExposure || badIso
                ? $"the frame header says exposure {headerExposure} s ISO {headerIso} but {exposureSeconds} s ISO {iso} was requested"
                : null;
        }

        /// <summary>The value of a numeric FITS header keyword, from the first blocks only; NaN when absent.</summary>
        internal static double FitsHeaderNumber(byte[] data, string key) {
            var end = Math.Min(data.Length, 2880 * 6);
            for (var i = 0; i + 80 <= end; i += 80) {
                var card = Encoding.ASCII.GetString(data, i, 80);
                if (card.StartsWith("END", StringComparison.Ordinal) && card.Substring(3).Trim().Length == 0) {
                    break;
                }
                if (card[..8].Trim() == key && card[8] == '=') {
                    return double.TryParse(card.Substring(10, 20).Split('/')[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : double.NaN;
                }
            }
            return double.NaN;
        }

        /// <summary>
        /// Cancels a capture and waits for a status showing the telescope has left it. Returns false if it still reports the capture: a camera can hang in SAMPLE_CAPTURE for hours,
        /// acknowledging this and ignoring every later capture (2026-10-28), and then only <see cref="Reboot"/> clears it.
        /// </summary>
        public async Task<bool> CancelCapture(TimeSpan wait, CancellationToken token = default) {
            var before = LastMessage("TaskController/GetStatus");
            static bool Stuck(JObject s) => s?.Value<string>("State") == "SAMPLE_CAPTURE" && s.Value<string>("Stage") == "IN_PROGRESS";
            await Request("CancelImaging", "TaskController", null, TimeSpan.FromSeconds(8), token);
            var waited = System.Diagnostics.Stopwatch.StartNew();
            while (waited.Elapsed < wait) {
                await Task.Delay(CancelPollInterval, token);
                var now = LastMessage("TaskController/GetStatus");
                if (now != null && now.Value<int?>("SequenceID") != before?.Value<int?>("SequenceID") && !Stuck(now)) {
                    return true;
                }
            }
            return false;
        }

        internal TimeSpan CancelPollInterval { get; set; } = TimeSpan.FromMilliseconds(500);

        #endregion camera

        #region traffic log

        // Every message to and from the telescope, in the format OriginMonitor's tools read: these logs are the evidence for what the firmware really does.
        private void OpenLog() {
            lock (events) {
                if (log != null) {
                    return;
                }
                try {
                    Directory.CreateDirectory(logDirectory);
                    log = new StreamWriter(Path.Combine(logDirectory, $"websocket_log_{DateTime.Now:yyyyMMdd_HHmmss}.txt"), append: true, new UTF8Encoding(false)) { AutoFlush = true };
                    logBytes = 0;
                } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                    Logger.Warning($"Origin: no telescope traffic log: {ex.Message}");
                }
            }
        }

        private void WriteLog(string direction, string text) {
            lock (events) {
                if (log == null) {
                    return;
                }
                try {
                    var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {direction}: {text}";
                    log.WriteLine(line);
                    logBytes += line.Length + 1;
                    // Status messages run to tens of MB a day, and this may be on a Raspberry Pi's SD card.
                    if (logBytes > MaxLogBytes) {
                        log.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] SYSTEM: log size limit reached, no more traffic is logged in this session");
                        log.Dispose();
                        log = null;
                    }
                } catch (Exception ex) when (ex is IOException or ObjectDisposedException) {
                    Logger.Warning($"Origin: telescope traffic log stopped: {ex.Message}");
                    log = null;
                }
            }
        }

        private void CloseLog() {
            lock (events) {
                log?.Dispose();
                log = null;
            }
        }

        #endregion traffic log
    }
}
