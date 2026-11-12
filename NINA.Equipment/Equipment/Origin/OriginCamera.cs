#region "copyright"

/*
    Copyright © 2026 the PI.N.S. contributors

    This file is part of PI 'N' Stars.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NINA.Core.Enum;
using NINA.Core.Model.Equipment;
using NINA.Core.Utility;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Model;
using NINA.Image.Interfaces;
using NINA.Profile.Interfaces;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Equipment.Equipment.Origin {

    /// <summary>
    /// The Origin's camera. Each exposure is one RunSampleCapture, a raw 16-bit RGGB FITS frame which the telescope serves over HTTP. The frame goes through a file because
    /// that is what the image loader reads; the file is deleted once loaded.
    /// </summary>
    public class OriginCamera : OriginDevice, ICamera {
        private const int SensorWidth = 3056;
        private const int SensorHeight = 2048;
        private const double SensorPixelSize = 2.4;

        // How long past the exposure the frame may take to arrive.
        private const int FrameMarginSeconds = 60;

        private readonly IProfileService profileService;
        private readonly IImageDataFactory imageDataFactory;
        private readonly IExposureDataFactory exposureDataFactory;
        private readonly string frameRoot;
        private readonly object exposureLock = new();
        private CancellationTokenSource exposureCts;
        private Task<string> exposureTask;
        private Task abortTask = Task.CompletedTask;
        private CameraStates cameraState = CameraStates.Idle;
        private double temperature = double.NaN;
        private int gain = 800;

        public OriginCamera(IProfileService profileService, IImageDataFactory imageDataFactory, IExposureDataFactory exposureDataFactory,
                            string host, int port = OriginBackend.DefaultPort, string frameRoot = null, string logDirectory = null, string filterFile = null) : base(host, port, logDirectory, filterFile) {
            this.profileService = profileService;
            this.imageDataFactory = imageDataFactory;
            this.exposureDataFactory = exposureDataFactory;
            this.frameRoot = frameRoot ?? Path.Combine(Path.GetTempPath(), "pins-origin-frames");
        }

        public override string Id => "Origin_Camera";
        public override string Name => "Celestron Origin Camera";
        public override string Description => "Celestron Origin built-in colour camera";

        public bool HasShutter => false;
        public double Temperature => temperature;
        public double TemperatureSetPoint { get => double.NaN; set { } }
        public short BinX { get => 1; set { } }
        public short BinY { get => 1; set { } }
        public string SensorName => "IMX178";
        public SensorType SensorType => SensorType.RGGB;
        public short BayerOffsetX => 0;
        public short BayerOffsetY => 0;
        public int CameraXSize => SensorWidth;
        public int CameraYSize => SensorHeight;
        public double ExposureMin => 0.00005;                                   // 50 microseconds; above that, any whole number of microseconds
        public double ExposureMax => 3600;
        public short MaxBinX => 1;
        public short MaxBinY => 1;
        public double PixelSizeX => SensorPixelSize;
        public double PixelSizeY => SensorPixelSize;
        public bool CanSetTemperature => false;
        public bool CoolerOn { get => false; set { } }
        public double CoolerPower => double.NaN;
        public bool HasDewHeater => false;
        public bool DewHeaterOn { get => false; set { } }

        public CameraStates CameraState {
            get => cameraState;
            private set {
                cameraState = value;
                RaisePropertyChanged();
            }
        }

        public bool CanSubSample => false;
        public bool EnableSubSample { get => false; set { } }
        public int SubSampleX { get; set; }
        public int SubSampleY { get; set; }
        public int SubSampleWidth { get; set; }
        public int SubSampleHeight { get; set; }
        public bool CanShowLiveView => false;
        public bool LiveViewEnabled => false;
        public bool HasBattery => false;
        public int BatteryLevel => 0;
        public int BitDepth => 16;
        public bool CanSetOffset => false;
        public int Offset { get => 0; set { } }
        public int OffsetMin => 0;
        public int OffsetMax => 0;
        public bool CanSetUSBLimit => false;
        public int USBLimit { get => 0; set { } }
        public int USBLimitMin => 0;
        public int USBLimitMax => 0;
        public int USBLimitStep => 0;
        public bool CanGetGain => true;
        public bool CanSetGain => true;

        // The ISO values the telescope has been seen to use.
        public int GainMax => 3200;
        public int GainMin => 100;

        public int Gain {
            get => gain;
            set {
                gain = Math.Clamp(value, GainMin, GainMax);
                RaisePropertyChanged();
            }
        }

        // The sensitivity depends on the ISO (FITS header EGAIN) and the measured values are disputed: see OriginMonitor tools/binning_test.py.
        public double ElectronsPerADU => double.NaN;

        public IList<string> ReadoutModes { get; } = new List<string> { "Default" };
        public short ReadoutMode { get => 0; set { } }
        public short ReadoutModeForSnapImages { get => 0; set { } }
        public short ReadoutModeForNormalImages { get => 0; set { } }
        public IList<int> Gains { get; } = new List<int>();
        public AsyncObservableCollection<BinningMode> BinningModes { get; } = new() { new BinningMode(1, 1) };

        public void SetBinning(short x, short y) {
        }

        public void UpdateSubSampleArea() {
        }

        protected override Task OnConnecting(CancellationToken token) {
            ClearFrames();
            return Task.CompletedTask;
        }

        protected override void OnDisconnecting() {
            AbortExposure();
        }

        protected override Task Poll(CancellationToken token) {
            if (Backend?.IsConnected != true) {
                throw new OriginException("Not connected to the telescope");
            }
            temperature = Backend.Data.Environment.CameraTemperature;
            RaisePropertyChanged(nameof(Temperature));
            return Task.CompletedTask;
        }

        public void StartExposure(CaptureSequence sequence) {
            lock (exposureLock) {
                if (exposureTask is { IsCompleted: false }) {
                    throw new InvalidOperationException($"{Name} is already exposing");
                }
                exposureCts?.Dispose();
                exposureCts = new CancellationTokenSource();
                CameraState = CameraStates.Exposing;
                exposureTask = Capture(sequence.ExposureTime, Gain, abortTask, exposureCts.Token);
            }
        }

        private async Task<string> Capture(double exposureSeconds, int iso, Task afterAbort, CancellationToken token) {
            await afterAbort;
            var backend = Backend ?? throw new OriginException($"{Name} is not connected");
            var frame = await backend.TakeFrame(exposureSeconds, iso, 1, TimeSpan.FromSeconds(Math.Ceiling(exposureSeconds) + FrameMarginSeconds), token);
            // The backend has already asked again for a frame that came back as a PNG; a second one is a failed exposure, not an image.
            if (!frame.IsFits) {
                throw new OriginException($"The telescope returned '{frame.RemotePath}' instead of a raw FITS frame");
            }
            var directory = Path.Combine(frameRoot, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, Path.GetFileName(frame.RemotePath));
            await File.WriteAllBytesAsync(path, frame.Data, token);
            return path;
        }

        public async Task WaitUntilExposureIsReady(CancellationToken token) {
            var pending = exposureTask ?? throw new InvalidOperationException($"{Name} has no exposure in progress");
            try {
                await pending.WaitAsync(token);
                CameraState = CameraStates.Download;
            } catch (Exception ex) when (ex is not OperationCanceledException) {
                CameraState = CameraStates.Error;
                throw;
            } catch (OperationCanceledException) when (token.IsCancellationRequested) {
                AbortExposure();
                throw;
            }
        }

        public async Task<IExposureData> DownloadExposure(CancellationToken token) {
            var pending = exposureTask ?? throw new InvalidOperationException($"{Name} has no exposure to download");
            var path = await pending.WaitAsync(token);
            try {
                CameraState = CameraStates.LoadingFile;
                var image = await imageDataFactory.CreateFromFile(path, BitDepth, isBayered: true, profileService.ActiveProfile.CameraSettings.RawConverter, token);
                return exposureDataFactory.CreateCachedExposureData(image);
            } finally {
                DeleteFrame(path);
                CameraState = CameraStates.Idle;
            }
        }

        /// <summary>
        /// Stops waiting for the frame and asks the telescope to cancel the capture. The next exposure waits for that answer. A capture
        /// that will not cancel (seen on real firmware, cleared only by a reboot) is logged, and the next exposure then times out.
        /// </summary>
        public void AbortExposure() {
            lock (exposureLock) {
                if (exposureTask is not { IsCompleted: false }) {
                    return;
                }
                exposureCts?.Cancel();
                abortTask = Backend is { } backend ? CancelCapture(backend) : Task.CompletedTask;
                CameraState = CameraStates.Idle;
            }
        }

        // A sample capture has no early readout that could give a usable frame.
        public void StopExposure() {
            AbortExposure();
        }

        private async Task CancelCapture(OriginBackend backend) {
            try {
                if (!await backend.CancelCapture(TimeSpan.FromSeconds(10))) {
                    Logger.Warning($"{Name}: the telescope still reports a capture in progress; if later exposures time out, it needs a reboot");
                }
            } catch (Exception ex) {
                Logger.Error($"{Name}: could not cancel the capture: {ex.Message}");
            }
        }

        private static void DeleteFrame(string path) {
            if (string.IsNullOrEmpty(path)) {
                return;
            }
            try {
                File.Delete(path);
                var directory = Path.GetDirectoryName(path);
                if (directory != null && Directory.GetFileSystemEntries(directory).Length == 0) {
                    Directory.Delete(directory);
                }
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                Logger.Warning($"Could not delete the Origin frame {path}: {ex.Message}");
            }
        }

        // Frames left by an aborted exposure or a crash. This directory belongs to this driver alone.
        private void ClearFrames() {
            try {
                if (Directory.Exists(frameRoot)) {
                    Directory.Delete(frameRoot, recursive: true);
                }
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                Logger.Warning($"Could not clear {frameRoot}: {ex.Message}");
            }
        }

        public void StartLiveView(CaptureSequence sequence) => throw new NotSupportedException($"{Name} has no live view");

        public Task<IExposureData> DownloadLiveView(CancellationToken token) => throw new NotSupportedException($"{Name} has no live view");

        public void StopLiveView() {
        }
    }
}
