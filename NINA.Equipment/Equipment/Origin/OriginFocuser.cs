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
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Equipment.Equipment.Origin {

    /// <summary>
    /// The Origin's built-in focuser. Its position comes from the telescope's status notifications. The firmware also autofocuses by itself as part of a goto and an
    /// initialisation, and this device shows that as moving; <see cref="OriginTelescope.SlewToCoordinates"/> waits for it to finish.
    ///
    /// The firmware takes up backlash itself, so PI.N.S. backlash compensation should stay off for this focuser. Nothing here may be copied from OriginMonitor's manual focus
    /// code, which is GPL: see <see cref="OriginBackend.MoveFocuser"/> and NINA.Equipment/ARCHITECTURE.md.
    /// </summary>
    public class OriginFocuser : OriginDevice, IFocuser {
        private FocuserStatus status = new();
        private (int Min, int Max)? limits;
        private bool moving;

        public OriginFocuser(string host, int port = OriginBackend.DefaultPort, string logDirectory = null, string filterFile = null) : base(host, port, logDirectory, filterFile) {
        }

        public override string Id => "Origin_Focuser";
        public override string Name => "Celestron Origin Focuser";
        public override string Description => "Celestron Origin built-in focuser ";

        public bool CanReverse => false;
        public bool Reverse { get => false; set { } }
        public bool IsMoving => moving || !status.IsMoveToOver || FirmwareIsFocusing;

        private bool FirmwareIsFocusing => Backend?.Data.TaskController is { State: not (null or "IDLE"), CurrentStep: "FOCUSING" };
        public int MaxIncrement => limits is { } l ? l.Max - l.Min : 0;
        public bool CanSetMaxStep => false;
        public int MaxStep { get => limits?.Max ?? 0; set { } }
        public int Position => status.Position;
        public double StepSize => double.NaN;
        public bool TempCompAvailable => false;
        public bool TempComp { get => false; set { } }
        public double Temperature => Backend?.Data.Environment.FrontCellTemperature ?? double.NaN;

        protected override async Task OnConnecting(CancellationToken token) {
            try {
                limits = await Backend.GetFocuserLimits(token);
            } catch (OriginException ex) {
                // A mount or camera session should not fail over this. A move asks again, and refuses without them.
                Logger.Warning($"{Name}: {ex.Message}");
            }
        }

        protected override Task Poll(CancellationToken token) {
            if (Backend?.IsConnected != true) {
                throw new OriginException("Not connected to the telescope");
            }
            var previous = status.Position;
            status = Backend.Data.Focuser;
            if (status.Position != previous) {
                RaisePropertyChanged(nameof(Position));
            }
            return Task.CompletedTask;
        }

        /// <summary>Returns when the focuser has arrived. There is no known way to stop a move, so cancelling stops the waiting and the focuser finishes.</summary>
        public async Task Move(int position, CancellationToken ct, int waitInMs = 1000) {
            var backend = Backend ?? throw new OriginException($"{Name} is not connected");
            limits ??= await backend.GetFocuserLimits(ct);
            var (min, max) = limits.Value;
            // Refused rather than clamped: an autofocus curve must not take a frame from another position as this one.
            if (position < min || position > max) {
                throw new ArgumentOutOfRangeException(nameof(position), position, $"{Name} travels from {min} to {max}");
            }
            moving = true;
            RaisePropertyChanged(nameof(IsMoving));
            try {
                await backend.MoveFocuser(position, ct);
                status = backend.Data.Focuser;
                RaisePropertyChanged(nameof(Position));
            } finally {
                moving = false;
                RaisePropertyChanged(nameof(IsMoving));
            }
        }

        public void Halt() {
            Logger.Warning($"{Name}: no command to stop a move is known; the focuser will finish the current one");
        }
    }
}
