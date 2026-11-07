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
using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Equipment.Equipment.Origin {

    /// <summary>
    /// Finds the telescope: it broadcasts "Identity:Origin-... Origin IP Address = a.b.c.d" on UDP 55555 about every 5 s. ORIGIN_HOST (host or host:port) overrides this,
    /// for a telescope on another subnet, where the broadcast does not reach, or for the simulator.
    /// </summary>
    public static class OriginDiscovery {
        public const int BroadcastPort = 55555;
        public const string HostVariable = "ORIGIN_HOST";

        private static readonly Regex ipAddress = new(@"\b(?:\d{1,3}\.){3}\d{1,3}\b", RegexOptions.Compiled);
        private static readonly TimeSpan keepFound = TimeSpan.FromMinutes(2);
        private static readonly TimeSpan keepNotFound = TimeSpan.FromSeconds(30);
        private static readonly SemaphoreSlim listening = new(1, 1);
        private static string found;
        private static DateTime foundAt;
        private static DateTime notFoundAt = DateTime.MinValue;

        /// <summary>The address in a telescope's broadcast, or null if this is something else.</summary>
        public static string ParseBroadcast(string datagram) {
            if (datagram == null || datagram.IndexOf("Origin", StringComparison.OrdinalIgnoreCase) < 0 || datagram.IndexOf("IP Address", StringComparison.OrdinalIgnoreCase) < 0) {
                return null;
            }
            var match = ipAddress.Match(datagram);
            return match.Success ? match.Value : null;
        }

        /// <summary>"host" or "host:port", with the default port when none is given. Null when empty or the port is not valid.</summary>
        public static (string Host, int Port)? ParseHost(string value) {
            if (string.IsNullOrWhiteSpace(value)) {
                return null;
            }
            var parts = value.Trim().Split(':');
            if (parts.Length == 1) {
                return (parts[0], OriginBackend.DefaultPort);
            }
            return parts.Length == 2 && parts[0].Length > 0 && int.TryParse(parts[1], out var port) && port is > 0 and < 65536 ? (parts[0], port) : null;
        }

        /// <summary>Where the telescope is, or null if ORIGIN_HOST is not set and no broadcast arrives within the timeout.</summary>
        public static async Task<(string Host, int Port)?> Locate(TimeSpan timeout, CancellationToken token = default) {
            var configured = Environment.GetEnvironmentVariable(HostVariable);
            if (!string.IsNullOrWhiteSpace(configured)) {
                var parsed = ParseHost(configured);
                if (parsed == null) {
                    Logger.Error($"Origin: {HostVariable}='{configured}' is not host or host:port");
                }
                return parsed;
            }

            // The three device choosers ask one after the other. Each would otherwise wait for a broadcast, which for everybody without an Origin never comes.
            await listening.WaitAsync(token);
            try {
                if (found != null && DateTime.UtcNow - foundAt < keepFound) {
                    return (found, OriginBackend.DefaultPort);
                }
                if (DateTime.UtcNow - notFoundAt < keepNotFound) {
                    return null;
                }
                var host = await ListenForBroadcast(BroadcastPort, timeout, token);
                if (host != null) {
                    found = host;
                    foundAt = DateTime.UtcNow;
                    return (host, OriginBackend.DefaultPort);
                }
                notFoundAt = DateTime.UtcNow;
                return null;
            } finally {
                listening.Release();
            }
        }

        internal static async Task<string> ListenForBroadcast(int port, TimeSpan timeout, CancellationToken token = default) {
            try {
                using var udp = new UdpClient(AddressFamily.InterNetwork);
                udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                udp.Client.Bind(new IPEndPoint(IPAddress.Any, port));
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
                limit.CancelAfter(timeout);
                while (true) {
                    var received = await udp.ReceiveAsync(limit.Token);
                    var host = ParseBroadcast(Encoding.UTF8.GetString(received.Buffer));
                    if (host != null) {
                        return host;
                    }
                }
            } catch (OperationCanceledException) when (!token.IsCancellationRequested) {
                return null;
            } catch (SocketException ex) {
                Logger.Warning($"Origin: cannot listen for the telescope on UDP {port}: {ex.Message}");
                return null;
            }
        }
    }
}
