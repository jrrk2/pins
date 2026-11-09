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

namespace NINA.Equipment.Equipment.Origin {

    /// <summary>
    /// What the telescope last said about itself. Each status object is replaced whole on update, so a reader on another thread always
    /// sees one consistent message. Angles are in radians, as the firmware sends them.
    /// </summary>
    public class MountStatus {
        public string BatteryLevel { get; internal set; }
        public double BatteryVoltage { get; internal set; }
        public double BatteryCurrent { get; internal set; }
        public string ChargerStatus { get; internal set; }
        public string Date { get; internal set; }
        public string Time { get; internal set; }
        public string TimeZone { get; internal set; }
        public double Latitude { get; internal set; }
        public double Longitude { get; internal set; }
        public bool IsAligned { get; internal set; }

        // True until the telescope says otherwise: a mount that has not reported is not known to be slewing.
        public bool IsGotoOver { get; internal set; } = true;

        public bool IsTracking { get; internal set; }
        public int NumAlignRefs { get; internal set; }
        public double Ra { get; internal set; }
        public double Dec { get; internal set; }
        public double Altitude { get; internal set; }
        public double Azimuth { get; internal set; }
        public double AltitudeError { get; internal set; }
        public double AzimuthError { get; internal set; }
        public double Enc0 { get; internal set; }
        public double Enc1 { get; internal set; }

        internal MountStatus Copy() => (MountStatus)MemberwiseClone();
    }

    public class CameraStatus {
        public int Binning { get; internal set; } = 1;
        public int BitDepth { get; internal set; }
        public double ColorBBalance { get; internal set; }
        public double ColorGBalance { get; internal set; }
        public double ColorRBalance { get; internal set; }
        public double Exposure { get; internal set; }
        public int Iso { get; internal set; }
        public int Offset { get; internal set; }

        internal CameraStatus Copy() => (CameraStatus)MemberwiseClone();
    }

    public class FocuserStatus {
        public int Backlash { get; internal set; }
        public int CalibrationLowerLimit { get; internal set; }
        public int CalibrationUpperLimit { get; internal set; }
        public bool IsCalibrationComplete { get; internal set; }
        public bool IsMoveToOver { get; internal set; } = true;
        public bool NeedAutoFocus { get; internal set; }
        public int PercentageCalibrationComplete { get; internal set; }
        public int Position { get; internal set; }
        public bool RequiresCalibration { get; internal set; }
        public double Velocity { get; internal set; }

        internal FocuserStatus Copy() => (FocuserStatus)MemberwiseClone();
    }

    public class EnvironmentStatus {
        public double AmbientTemperature { get; internal set; } = double.NaN;
        public double CameraTemperature { get; internal set; } = double.NaN;
        public bool CpuFanOn { get; internal set; }
        public double CpuTemperature { get; internal set; } = double.NaN;
        public double DewPoint { get; internal set; } = double.NaN;
        public double FrontCellTemperature { get; internal set; } = double.NaN;
        public double Humidity { get; internal set; } = double.NaN;
        public bool OtaFanOn { get; internal set; }
        public bool Recalibrating { get; internal set; }

        internal EnvironmentStatus Copy() => (EnvironmentStatus)MemberwiseClone();
    }

    public class ImageInfo {
        public string FileLocation { get; internal set; }
        public string ImageType { get; internal set; }
        public double Dec { get; internal set; }
        public double Ra { get; internal set; }
        public double Orientation { get; internal set; }
        public double FovX { get; internal set; }
        public double FovY { get; internal set; }

        internal ImageInfo Copy() => (ImageInfo)MemberwiseClone();
    }

    public class DiskStatus {
        public long Capacity { get; internal set; }
        public long FreeBytes { get; internal set; }
        public string Level { get; internal set; }

        internal DiskStatus Copy() => (DiskStatus)MemberwiseClone();
    }

    public class DewHeaterStatus {
        public int Aggression { get; internal set; }
        public double HeaterLevel { get; internal set; }
        public double ManualPowerLevel { get; internal set; }
        public string Mode { get; internal set; }

        internal DewHeaterStatus Copy() => (DewHeaterStatus)MemberwiseClone();
    }

    public class OrientationStatus {
        public double Altitude { get; internal set; }
    }

    public class TaskControllerStatus {
        public string State { get; internal set; }
        public string Stage { get; internal set; }
        public string CurrentStep { get; internal set; }
        public string ImagingName { get; internal set; }
        public bool IsReady { get; internal set; }

        /// <summary>Whether any message has carried IsReady, so that a false value means not ready rather than not yet heard.</summary>
        public bool IsReadyReported { get; internal set; }
        public int NumPoints { get; internal set; }
        public int FocusPosition { get; internal set; }
        public int PercentageComplete { get; internal set; }
        public int FocusPercentageComplete { get; internal set; }

        internal TaskControllerStatus Copy() => (TaskControllerStatus)MemberwiseClone();
    }

    /// <summary>
    /// Keeps <see cref="TelescopeData"/> up to date from the telescope's messages.
    ///
    /// A message updates only the fields it carries. An acknowledgement from the mount has none of the status fields, and reading those as
    /// their defaults would, for instance, report a slew in progress and a mount at RA 0 after every command.
    /// </summary>
    public class TelescopeData {
        public MountStatus Mount { get; private set; } = new();
        public CameraStatus Camera { get; private set; } = new();
        public FocuserStatus Focuser { get; private set; } = new();
        public EnvironmentStatus Environment { get; private set; } = new();
        public ImageInfo LastImage { get; private set; } = new();
        public DiskStatus Disk { get; private set; } = new();
        public DewHeaterStatus DewHeater { get; private set; } = new();
        public OrientationStatus Orientation { get; private set; } = new();
        public TaskControllerStatus TaskController { get; private set; } = new();

        public DateTime? MountLastUpdate { get; private set; }

        /// <summary>When the focuser last reported its position and whether it is moving; about once a second, unasked.</summary>
        public DateTime? FocuserLastUpdate { get; private set; }

        /// <summary>When the tilt sensor last reported. It is the only position known before the mount has aligned.</summary>
        public DateTime? OrientationLastUpdate { get; private set; }

        /// <summary>Returns false for anything but a notification or a response.</summary>
        internal bool Process(JObject message) {
            var type = message.Value<string>("Type");
            if (type != "Notification" && type != "Response") {
                return false;
            }
            var command = message.Value<string>("Command");
            switch (message.Value<string>("Source")) {
                case "Mount":
                    UpdateMount(message);
                    break;

                case "Camera" when command == "GetCaptureParameters":
                    UpdateCamera(message);
                    break;

                case "Focuser":
                    UpdateFocuser(message);
                    break;

                case "Environment":
                    UpdateEnvironment(message);
                    break;

                case "ImageServer" when command == "NewImageReady":
                    UpdateImage(message);
                    break;

                case "Disk":
                    UpdateDisk(message);
                    break;

                case "DewHeater":
                    UpdateDewHeater(message);
                    break;

                case "OrientationSensor":
                    if (message["Altitude"] != null) {
                        Orientation = new OrientationStatus { Altitude = message.Value<double>("Altitude") };
                        OrientationLastUpdate = DateTime.UtcNow;
                    }
                    break;

                case "TaskController":
                    UpdateTaskController(message);
                    break;
            }
            return true;
        }

        private static T Get<T>(JObject o, string key, T current) {
            var token = o[key];
            return token == null || token.Type == JTokenType.Null ? current : token.ToObject<T>();
        }

        private void UpdateMount(JObject o) {
            var m = Mount.Copy();
            m.BatteryCurrent = Get(o, "BatteryCurrent", m.BatteryCurrent);
            m.BatteryLevel = Get(o, "BatteryLevel", m.BatteryLevel);
            m.BatteryVoltage = Get(o, "BatteryVoltage", m.BatteryVoltage);
            m.ChargerStatus = Get(o, "ChargerStatus", m.ChargerStatus);
            m.Date = Get(o, "Date", m.Date);
            m.Time = Get(o, "Time", m.Time);
            m.TimeZone = Get(o, "TimeZone", m.TimeZone);
            m.Latitude = Get(o, "Latitude", m.Latitude);
            m.Longitude = Get(o, "Longitude", m.Longitude);
            m.IsAligned = Get(o, "IsAligned", m.IsAligned);
            m.IsGotoOver = Get(o, "IsGotoOver", m.IsGotoOver);
            m.IsTracking = Get(o, "IsTracking", m.IsTracking);
            m.NumAlignRefs = Get(o, "NumAlignRefs", m.NumAlignRefs);
            // Until it is aligned the mount does not report a position at all.
            m.Ra = Get(o, "Ra", m.Ra);
            m.Dec = Get(o, "Dec", m.Dec);
            m.Altitude = Get(o, "Alt", m.Altitude);
            m.AltitudeError = Get(o, "AltitudeError", m.AltitudeError);
            m.Azimuth = Get(o, "Azm", m.Azimuth);
            m.AzimuthError = Get(o, "AzimuthError", m.AzimuthError);
            m.Enc0 = Get(o, "Enc0", m.Enc0);
            m.Enc1 = Get(o, "Enc1", m.Enc1);
            Mount = m;
            MountLastUpdate = DateTime.UtcNow;
        }

        private void UpdateCamera(JObject o) {
            var c = Camera.Copy();
            c.Binning = Get(o, "Binning", c.Binning);
            c.BitDepth = Get(o, "BitDepth", c.BitDepth);
            c.ColorBBalance = Get(o, "ColorBBalance", c.ColorBBalance);
            c.ColorGBalance = Get(o, "ColorGBalance", c.ColorGBalance);
            c.ColorRBalance = Get(o, "ColorRBalance", c.ColorRBalance);
            c.Exposure = Get(o, "Exposure", c.Exposure);
            c.Iso = Get(o, "ISO", c.Iso);
            c.Offset = Get(o, "Offset", c.Offset);
            Camera = c;
        }

        private void UpdateFocuser(JObject o) {
            var f = Focuser.Copy();
            f.Backlash = Get(o, "Backlash", f.Backlash);
            f.CalibrationLowerLimit = Get(o, "CalibrationLowerLimit", f.CalibrationLowerLimit);
            f.CalibrationUpperLimit = Get(o, "CalibrationUpperLimit", f.CalibrationUpperLimit);
            f.IsCalibrationComplete = Get(o, "IsCalibrationComplete", f.IsCalibrationComplete);
            f.IsMoveToOver = Get(o, "IsMoveToOver", f.IsMoveToOver);
            f.NeedAutoFocus = Get(o, "NeedAutoFocus", f.NeedAutoFocus);
            f.PercentageCalibrationComplete = Get(o, "PercentageCalibrationComplete", f.PercentageCalibrationComplete);
            f.Position = Get(o, "Position", f.Position);
            f.RequiresCalibration = Get(o, "RequiresCalibration", f.RequiresCalibration);
            f.Velocity = Get(o, "Velocity", f.Velocity);
            Focuser = f;
            // Only a status says where the focuser is now: an acknowledgement or a limits reply has none of this.
            if (o["IsMoveToOver"] != null) {
                FocuserLastUpdate = DateTime.UtcNow;
            }
        }

        private void UpdateEnvironment(JObject o) {
            var e = Environment.Copy();
            e.AmbientTemperature = Get(o, "AmbientTemperature", e.AmbientTemperature);
            e.CameraTemperature = Get(o, "CameraTemperature", e.CameraTemperature);
            e.CpuFanOn = Get(o, "CpuFanOn", e.CpuFanOn);
            e.CpuTemperature = Get(o, "CpuTemperature", e.CpuTemperature);
            e.DewPoint = Get(o, "DewPoint", e.DewPoint);
            e.FrontCellTemperature = Get(o, "FrontCellTemperature", e.FrontCellTemperature);
            e.Humidity = Get(o, "Humidity", e.Humidity);
            e.OtaFanOn = Get(o, "OtaFanOn", e.OtaFanOn);
            e.Recalibrating = Get(o, "Recalibrating", e.Recalibrating);
            Environment = e;
        }

        private void UpdateImage(JObject o) {
            var i = LastImage.Copy();
            i.FileLocation = Get(o, "FileLocation", i.FileLocation);
            i.ImageType = Get(o, "ImageType", i.ImageType);
            i.Dec = Get(o, "Dec", i.Dec);
            i.Ra = Get(o, "Ra", i.Ra);
            i.Orientation = Get(o, "Orientation", i.Orientation);
            i.FovX = Get(o, "FovX", i.FovX);
            i.FovY = Get(o, "FovY", i.FovY);
            LastImage = i;
        }

        private void UpdateDisk(JObject o) {
            var d = Disk.Copy();
            d.Capacity = Get(o, "Capacity", d.Capacity);
            d.FreeBytes = Get(o, "FreeBytes", d.FreeBytes);
            d.Level = Get(o, "Level", d.Level);
            Disk = d;
        }

        private void UpdateDewHeater(JObject o) {
            var d = DewHeater.Copy();
            d.Aggression = Get(o, "Aggression", d.Aggression);
            d.HeaterLevel = Get(o, "HeaterLevel", d.HeaterLevel);
            d.ManualPowerLevel = Get(o, "ManualPowerLevel", d.ManualPowerLevel);
            d.Mode = Get(o, "Mode", d.Mode);
            DewHeater = d;
        }

        private void UpdateTaskController(JObject o) {
            var t = TaskController.Copy();
            t.State = Get(o, "State", t.State);
            t.Stage = Get(o, "Stage", t.Stage);
            t.IsReady = Get(o, "IsReady", t.IsReady);
            t.IsReadyReported |= o.ContainsKey("IsReady");
            t.CurrentStep = Get(o, "ErrorMessage", t.CurrentStep);
            if (o["InitializationInfo"] is JObject init) {
                t.CurrentStep = Get(init, "CurrentStep", t.CurrentStep);
                t.NumPoints = Get(init, "NumPoints", t.NumPoints);
                t.PercentageComplete = Get(init, "PercentageComplete", t.PercentageComplete);
            }
            if (o["FocusInfo"] is JObject focus) {
                t.FocusPosition = Get(focus, "Position", t.FocusPosition);
                t.FocusPercentageComplete = Get(focus, "PercentageComplete", t.FocusPercentageComplete);
            }
            t.ImagingName = Get(o, "ImagingName", t.ImagingName);
            TaskController = t;
        }
    }
}
