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
using NINA.Equipment.Interfaces;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Equipment.Equipment.Origin {

    /// <summary>
    /// What the Origin devices share: one <see cref="OriginBackend"/> for the telescope, and a background poll so that property getters, which PI.N.S. reads from its
    /// device-update loop, never wait on the network.
    /// </summary>
    public abstract class OriginDevice : BaseINPC, IDevice {
        private readonly string host;
        private readonly int port;
        private readonly string logDirectory;
        private readonly string filterFile;
        private CancellationTokenSource pollCts;
        private bool connected;
        private DateTime lastGoodPoll;

        protected OriginDevice(string host, int port, string logDirectory = null, string filterFile = null) {
            this.host = host;
            this.port = port;
            this.logDirectory = logDirectory;
            this.filterFile = filterFile;
        }

        /// <summary>The telescope's address.</summary>
        public string Host => host;

        protected OriginBackend Backend { get; private set; }

        internal TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);

        // A telescope reboot or a Wi-Fi drop takes 15-45 s and the backend reconnects by itself, so a device stays connected through that; beyond it PI.N.S. should know.
        internal TimeSpan LinkLossGrace { get; set; } = TimeSpan.FromSeconds(60);

        public bool HasSetupDialog => false;
        public abstract string Id { get; }
        public abstract string Name { get; }
        public string DisplayName => Name;
        public string Category => "Celestron Origin";
        public abstract string Description { get; }
        public string DriverInfo => $"Direct connection to the telescope at {host}";
        public string DriverVersion => CoreUtil.Version;
        public virtual IList<string> SupportedActions => new List<string>();

        public bool Connected {
            get => connected;
            private set {
                if (connected != value) {
                    connected = value;
                    RaisePropertyChanged();
                }
            }
        }

        public async Task<bool> Connect(CancellationToken token) {
            if (Connected) {
                return true;
            }
            var backend = OriginBackend.Acquire(host, port, logDirectory, filterFile);
            try {
                if (!await backend.ConnectAsync(token)) {
                    throw new OriginException($"Could not connect to the telescope at {host}:{port}. It accepts one client, so stop OriginHeadless or the Celestron app if one is connected");
                }
                Backend = backend;
                await Poll(token);
                await OnConnecting(token);
            } catch (Exception) {
                Backend = null;
                backend.Release();
                throw;
            }
            lastGoodPoll = DateTime.UtcNow;
            Connected = true;
            pollCts = new CancellationTokenSource();
            _ = PollLoop(pollCts.Token);
            return true;
        }

        public void Disconnect() {
            pollCts?.Cancel();
            pollCts = null;
            var backend = Backend;
            if (!Connected || backend == null) {
                return;
            }
            OnDisconnecting();
            Connected = false;
            Backend = null;
            backend.Release();
        }

        protected virtual Task OnConnecting(CancellationToken token) => Task.CompletedTask;

        /// <summary>Called while the link is still available.</summary>
        protected virtual void OnDisconnecting() {
        }

        /// <summary>Refreshes the device's cached state; throwing counts as a failed poll.</summary>
        protected abstract Task Poll(CancellationToken token);

        private async Task PollLoop(CancellationToken token) {
            try {
                while (!token.IsCancellationRequested) {
                    await Task.Delay(PollInterval, token);
                    try {
                        await Poll(token);
                        lastGoodPoll = DateTime.UtcNow;
                    } catch (Exception ex) when (ex is not OperationCanceledException) {
                        // Not only OriginException: a reply with an unexpected field type must not end this loop and leave the device looking connected.
                        Logger.Debug($"{Name}: status request failed: {ex.Message}");
                        if (DateTime.UtcNow - lastGoodPoll > LinkLossGrace) {
                            Logger.Error($"{Name}: no status for {LinkLossGrace.TotalSeconds:0} s, reporting the device as disconnected");
                            Disconnect();
                            return;
                        }
                    }
                }
            } catch (OperationCanceledException) {
            }
        }

        public void SetupDialog() {
        }

        public virtual string Action(string actionName, string actionParameters) => throw new NotSupportedException($"{actionName} is not an action of this device");

        public string SendCommandString(string command, bool raw = true) => throw new NotSupportedException();

        public bool SendCommandBool(string command, bool raw = true) => throw new NotSupportedException();

        public void SendCommandBlind(string command, bool raw = true) => throw new NotSupportedException();
    }
}
