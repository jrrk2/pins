#region "copyright"

/*
    Copyright © 2026 the PI.N.S. contributors

    This file is part of PI 'N' Stars.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NINA.Equipment.Equipment.Origin;
using NINA.Equipment.Interfaces;
using NINA.Image.Interfaces;
using NINA.Profile.Interfaces;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace NINA.Equipment.Utility {

    /// <summary>
    /// Lists the Celestron Origin devices. They appear only when a telescope has been found (its broadcast, or ORIGIN_HOST), so that nobody without an Origin sees them.
    /// </summary>
    public class OriginInteraction(IProfileService profileService) {

        // The telescope broadcasts about every 5 s, and the chooser waits for this while it lists devices.
        private static readonly TimeSpan BroadcastWait = TimeSpan.FromSeconds(6);

        private static async Task<List<T>> IfFound<T>(Func<string, int, T> create) {
            var location = await OriginDiscovery.Locate(BroadcastWait);
            return location is { } l ? new List<T> { create(l.Host, l.Port) } : new List<T>();
        }

        public Task<List<ICamera>> GetCameras(IExposureDataFactory exposureDataFactory, IImageDataFactory imageDataFactory) {
            return IfFound<ICamera>((host, port) => new OriginCamera(profileService, imageDataFactory, exposureDataFactory, host, port));
        }

        public Task<List<ITelescope>> GetTelescopes() {
            return IfFound<ITelescope>((host, port) => new OriginTelescope(profileService, host, port));
        }

        public Task<List<IFocuser>> GetFocusers() {
            return IfFound<IFocuser>((host, port) => new OriginFocuser(host, port));
        }
    }
}
