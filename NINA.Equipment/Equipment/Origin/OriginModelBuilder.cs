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
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Equipment.Equipment.Origin {

    /// <summary>
    /// Where a point of a model build stands. The numbers are those Touch-N-Stars' model builder screens show (from the 10micron plugin's states).
    /// </summary>
    public enum OriginModelPointState {
        Generated = 0,
        UpNext = 1,
        Exposing = 2,
        Processing = 3,
        AddedToModel = 4,
        Failed = 97,
        HighRMS = 98
    }

    /// <summary>A place on the sky to measure: where the mount is sent, not where the solve finds it.</summary>
    public sealed class OriginModelPoint {

        internal OriginModelPoint(int index, double altitude, double azimuth) {
            (Index, Altitude, Azimuth) = (index, altitude, azimuth);
        }

        /// <summary>1, 2, 3 and so on, in the order the points were generated.</summary>
        public int Index { get; }

        public double Altitude { get; }
        public double Azimuth { get; }
        public OriginModelPointState State { get; internal set; }

        /// <summary>The solved point added to the model from here, or null.</summary>
        internal OriginCalibrationPoint Measured { get; set; }
    }

    /// <summary>The choices for generating points and running a build. The names are the 10micron plugin's, which Touch-N-Stars' screens set.</summary>
    public sealed class OriginModelBuilderOptions {
        public int MinPointAltitude { get; set; }
        public int MaxPointAltitude { get; set; }
        public double MinPointAzimuth { get; set; }
        public double MaxPointAzimuth { get; set; }

        /// <summary>A point further from the model than this, arcseconds, is taken out after the build when <see cref="RemoveHighRMSPointsAfterBuild"/>; 0 keeps every point.</summary>
        public double MaxPointRMS { get; set; }

        public bool RemoveHighRMSPointsAfterBuild { get; set; }

        /// <summary>Further rounds for the points that did not solve.</summary>
        public int BuilderNumRetries { get; set; }

        /// <summary>The build stops after this many points have failed; 0 for no limit.</summary>
        public int MaxFailedPoints { get; set; }

        public int GoldenSpiralStarCount { get; set; }
        public double ExposureSeconds { get; set; }
        public int Iso { get; set; }

        public OriginModelBuilderOptions() {
            ResetDefaults();
        }

        /// <summary>
        /// 25 to 80 degrees: below 25 the air spoils the solves and trees get in the way, above 80 an alt-az mount's azimuth races. Twelve points fit the base's
        /// tilt, the altitude zero, flexure and cone error with some to spare, in about 6 minutes.
        /// </summary>
        public void ResetDefaults() {
            MinPointAltitude = 25;
            MaxPointAltitude = 80;
            MinPointAzimuth = 0;
            MaxPointAzimuth = 360;
            MaxPointRMS = 0;
            RemoveHighRMSPointsAfterBuild = true;
            BuilderNumRetries = 1;
            MaxFailedPoints = 0;
            GoldenSpiralStarCount = 12;
            ExposureSeconds = 2;
            Iso = 800;
        }
    }

    /// <summary>A point of the model as fitted, and how far the model misses it.</summary>
    public sealed record OriginModelStar(double Altitude, double Azimuth, double ErrorArcsec);

    /// <summary>The pointing model now: its points, its fit and the base's tilt.</summary>
    public sealed record OriginModelSummary(int Points, double RmsArcsec, double TiltDegrees, double TiltTowardAzimuth, double AltitudeZero, double Flexure,
                                            double ConeDegrees, int Terms, IReadOnlyList<OriginModelStar> Stars);

    /// <summary>
    /// Builds PI.N.S.'s pointing model from plate solves at many places: the job of a mount's model builder. Unpark gives a model from one solve; a build
    /// adds points spread over the sky, so that the base's tilt, the altitude zero, flexure and cone error are fitted (<see cref="OriginPointingModel"/>).
    ///
    /// <para>Points are generated on a golden spiral, even over the sky between the altitude limits. A build visits them in order of azimuth from
    /// cable-neutral, one sweep within the cord-wrap limit, and each plate solve runs while the mount moves on to the next. Every solved point refits the
    /// model, so the next is aimed better. A point that did not solve is tried again at the end, <see cref="OriginModelBuilderOptions.BuilderNumRetries"/> times.</para>
    ///
    /// <para>It works on the model Unpark set up, which a sync refines as well. Saved models are kept as files beside the cable-neutral; one is only right while the
    /// mount stands as it did when the model was built.</para>
    /// </summary>
    public sealed class OriginModelBuilder {
        private readonly OriginTelescope mount;
        private readonly object sync = new();
        private List<OriginModelPoint> points = new();
        private CancellationTokenSource buildCts;
        private volatile bool stopRequested;
        private Task building = Task.CompletedTask;

        internal OriginModelBuilder(OriginTelescope mount) {
            this.mount = mount;
        }

        public OriginModelBuilderOptions Options { get; } = new();

        public bool BuildInProgress => !building.IsCompleted;

        /// <summary>Why the last build ended early, or null.</summary>
        public string LastError { get; private set; }

        /// <summary>The build running, for tests and callers that wait for it.</summary>
        public Task Building => building;

        public IReadOnlyList<OriginModelPoint> Points {
            get {
                lock (sync) {
                    return points.ToList();
                }
            }
        }

        /// <summary>Replaces the points with a golden spiral of this many within the altitude and azimuth limits.</summary>
        public IReadOnlyList<OriginModelPoint> GenerateGoldenSpiral(int count) {
            ThrowIfBuilding();
            Options.GoldenSpiralStarCount = count;
            var list = GoldenSpiral(count, Options.MinPointAltitude, Options.MaxPointAltitude, Options.MinPointAzimuth, Options.MaxPointAzimuth)
                .Select((p, i) => new OriginModelPoint(i + 1, p.Altitude, p.Azimuth)).ToList();
            lock (sync) {
                points = list;
            }
            Logger.Info($"Origin model builder: {list.Count} points between {Options.MinPointAltitude} and {Options.MaxPointAltitude} degrees altitude");
            return list;
        }

        /// <summary>
        /// Points spread evenly over the band of sky between two altitudes and two azimuths: the sine of the altitude in equal steps, so that each point stands
        /// for an equal area, and the azimuth turned by the golden ratio of the span each time (a Fibonacci grid: R. Swinbank and R. J. Purser, "Fibonacci grids:
        /// A novel approach to global modelling", Q. J. R. Meteorol. Soc. 132, 2006). An azimuth span from a larger to a smaller number goes through north.
        /// </summary>
        internal static IReadOnlyList<(double Altitude, double Azimuth)> GoldenSpiral(int count, double minAltitude, double maxAltitude, double minAzimuth, double maxAzimuth) {
            if (count < 1 || count > 500) {
                throw new ArgumentOutOfRangeException(nameof(count), "1 to 500 points");
            }
            if (minAltitude >= maxAltitude || minAltitude < 0 || maxAltitude > 90) {
                throw new ArgumentOutOfRangeException(nameof(minAltitude), "The altitude limits must be 0 to 90 degrees, lower first");
            }
            var low = Math.Sin(minAltitude * Math.PI / 180);
            var high = Math.Sin(maxAltitude * Math.PI / 180);
            var span = OriginPointingModel.Norm360(maxAzimuth - minAzimuth);
            if (span < 1e-9) {
                span = 360;
            }
            var golden = (Math.Sqrt(5) - 1) / 2;
            var list = new List<(double, double)>(count);
            for (var i = 0; i < count; i++) {
                var altitude = Math.Asin(low + (high - low) * (i + 0.5) / count) * 180 / Math.PI;
                var turn = i * golden % 1;
                list.Add((altitude, OriginPointingModel.Norm360(minAzimuth + span * turn)));
            }
            return list;
        }

        public void ClearPoints() {
            ThrowIfBuilding();
            lock (sync) {
                points = new List<OriginModelPoint>();
            }
        }

        /// <summary>Starts a build of the points generated, in the background; <see cref="BuildInProgress"/> says when it is over.</summary>
        public void StartBuild() {
            lock (sync) {
                if (BuildInProgress) {
                    throw new InvalidOperationException("A model build is already running");
                }
                if (!mount.Connected || mount.Link == null) {
                    throw new InvalidOperationException("The mount is not connected");
                }
                if (mount.Calibration?.Model == null) {
                    throw new InvalidOperationException("Unpark the mount first: the build starts from the pointing Unpark sets up");
                }
                if (points.Count == 0) {
                    throw new InvalidOperationException("Generate the points first");
                }
                mount.RefuseToMoveInDaylight("building a pointing model");
                stopRequested = false;
                LastError = null;
                buildCts?.Dispose();
                buildCts = new CancellationTokenSource();
                var token = buildCts.Token;
                building = Task.Run(() => Build(token));
            }
        }

        /// <summary>Ends the build after the point in hand, keeping every point measured.</summary>
        public void Stop() => stopRequested = true;

        /// <summary>Ends the build now. Points already solved stay in the model.</summary>
        public void Cancel() {
            stopRequested = true;
            try {
                buildCts?.Cancel();
            } catch (ObjectDisposedException) {
            }
        }

        private async Task Build(CancellationToken token) {
            var calibration = mount.Calibration;
            var backend = mount.Link;
            var solving = new List<Task>();
            var started = DateTime.UtcNow;
            List<OriginModelPoint> todo;
            lock (sync) {
                foreach (var p in points) {
                    p.State = OriginModelPointState.Generated;
                    p.Measured = null;
                }
                todo = points.ToList();
            }
            try {
                await mount.StopForModelBuild(token);
                for (var round = 0; round <= Options.BuilderNumRetries && todo.Count > 0 && !stopRequested; round++) {
                    if (round > 0) {
                        Logger.Info($"Origin model builder: trying {todo.Count} point{(todo.Count == 1 ? "" : "s")} again");
                    }
                    foreach (var point in InCordWrapOrder(todo, calibration, backend.CordWrap)) {
                        if (stopRequested) {
                            break;
                        }
                        token.ThrowIfCancellationRequested();
                        SetState(point, OriginModelPointState.UpNext);
                        (double Enc0, double Enc1) target;
                        lock (sync) {
                            target = calibration.Aim(point.Altitude, point.Azimuth);
                        }
                        try {
                            await calibration.MoveTo(target.Enc0, target.Enc1, token);
                        } catch (OriginException ex) {
                            Logger.Warning($"Origin model builder: point {point.Index} left out: {ex.Message}");
                            SetState(point, OriginModelPointState.Failed);
                            continue;
                        } finally {
                            mount.MovedByModelBuild();
                        }
                        SetState(point, OriginModelPointState.Exposing);
                        var solveIt = await calibration.Capture(Options.ExposureSeconds, Options.Iso, token);
                        SetState(point, OriginModelPointState.Processing);
                        solving.Add(Task.Run(async () => Apply(point, await solveIt(token), calibration), token));
                        ThrowIfTooManyFailed();
                    }
                    await WhenAllSettled(solving);
                    solving.Clear();
                    ThrowIfTooManyFailed();
                    lock (sync) {
                        todo = points.Where(p => p.State == OriginModelPointState.Failed).ToList();
                    }
                }
                RemoveHighRmsPoints(calibration);
            } catch (OperationCanceledException) {
                Logger.Info("Origin model builder: cancelled");
            } catch (Exception ex) {
                LastError = ex.Message;
                Logger.Error($"Origin model builder: {ex.Message}");
            } finally {
                await WhenAllSettled(solving);
                lock (sync) {
                    foreach (var p in points.Where(p => p.State is OriginModelPointState.UpNext or OriginModelPointState.Exposing or OriginModelPointState.Processing)) {
                        p.State = OriginModelPointState.Generated;
                    }
                }
                var summary = Summary();
                Logger.Info($"Origin model builder: finished in {(DateTime.UtcNow - started).TotalMinutes:0.0} min: " +
                            (summary == null ? "no model" : $"{summary.Points} points, rms {summary.RmsArcsec:0} arcsec, base tilt {summary.TiltDegrees:0.0} degrees toward {summary.TiltTowardAzimuth:0}"));
            }
        }

        // a solve that has come back: into the model, or the point failed
        private void Apply(OriginModelPoint point, OriginCalibrationPoint measured, OriginCalibration calibration) {
            lock (sync) {
                if (measured == null) {
                    point.State = OriginModelPointState.Failed;
                    return;
                }
                calibration.Refine(measured);
                if (calibration.Points.Contains(measured)) {
                    point.Measured = measured;
                    point.State = OriginModelPointState.AddedToModel;
                } else {
                    point.State = OriginModelPointState.Failed;      // turned away: degrees from the fitted model
                }
            }
        }

        private void ThrowIfTooManyFailed() {
            if (Options.MaxFailedPoints <= 0) {
                return;
            }
            int failed;
            lock (sync) {
                failed = points.Count(p => p.State == OriginModelPointState.Failed);
            }
            if (failed > Options.MaxFailedPoints) {
                throw new OriginException($"{failed} points failed, more than the {Options.MaxFailedPoints} allowed: is the sky clear?");
            }
        }

        private void RemoveHighRmsPoints(OriginCalibration calibration) {
            if (!Options.RemoveHighRMSPointsAfterBuild || Options.MaxPointRMS <= 0) {
                return;
            }
            lock (sync) {
                while (calibration.Model is { } model && calibration.Points.Count > 3) {
                    var worst = calibration.Points.MaxBy(p => model.ResidualArcmin(p));
                    var error = model.ResidualArcmin(worst) * 60;
                    if (error <= Options.MaxPointRMS) {
                        break;
                    }
                    calibration.Remove(worst);
                    foreach (var p in points.Where(p => p.Measured == worst)) {
                        p.State = OriginModelPointState.HighRMS;
                    }
                    Logger.Info($"Origin model builder: a point {error:0} arcsec from the model taken out (more than {Options.MaxPointRMS:0})");
                }
            }
        }

        // One sweep in azimuth from cable-neutral, starting from the end nearer where the mount is: no point needs the long way round.
        private IEnumerable<OriginModelPoint> InCordWrapOrder(IEnumerable<OriginModelPoint> list, OriginCalibration calibration, OriginCordWrap wrap) {
            var placed = new List<(OriginModelPoint Point, double Turned)>();
            lock (sync) {
                foreach (var p in list) {
                    try {
                        placed.Add((p, wrap.TurnedFor(calibration.Aim(p.Altitude, p.Azimuth).Enc0)));
                    } catch (OriginException) {
                        placed.Add((p, double.NaN));
                    }
                }
            }
            var reachable = placed.Where(p => !double.IsNaN(p.Turned)).OrderBy(p => p.Turned).ToList();
            if (reachable.Count > 1 && Math.Abs(reachable[^1].Turned - wrap.Turned) < Math.Abs(reachable[0].Turned - wrap.Turned)) {
                reachable.Reverse();
            }
            foreach (var p in placed.Where(p => double.IsNaN(p.Turned))) {
                Logger.Warning($"Origin model builder: point {p.Point.Index} is beyond the cord-wrap limit");
                SetState(p.Point, OriginModelPointState.Failed);
            }
            return reachable.Select(p => p.Point);
        }

        private static async Task WhenAllSettled(IEnumerable<Task> tasks) {
            foreach (var task in tasks.ToList()) {
                try {
                    await task;
                } catch (OperationCanceledException) {
                } catch (Exception ex) {
                    Logger.Error($"Origin model builder: a plate solve failed: {ex.Message}");
                }
            }
        }

        private void SetState(OriginModelPoint point, OriginModelPointState state) {
            lock (sync) {
                point.State = state;
            }
        }

        private void ThrowIfBuilding() {
            if (BuildInProgress) {
                throw new InvalidOperationException("A model build is running: stop it first");
            }
        }

        /// <summary>The model now, or null without one.</summary>
        public OriginModelSummary Summary() {
            lock (sync) {
                var calibration = mount.Calibration;
                if (calibration?.Model is not { } model || calibration.Points.Count == 0) {
                    return null;
                }
                var stars = calibration.Points.Select(p => new OriginModelStar(p.Altitude, p.Azimuth, model.ResidualArcmin(p) * 60)).ToList();
                var rms = Math.Sqrt(stars.Average(s => s.ErrorArcsec * s.ErrorArcsec));
                var (tilt, toward) = model.BaseTilt;
                // the rotation (3) and the altitude zero, then flexure and cone error once there are points enough; a level-base model has the azimuth zero only
                var terms = stars.Count < 3 ? 1 : 4 + (stars.Count >= OriginPointingModel.PointsForFlexure ? 1 : 0) + (stars.Count >= OriginPointingModel.PointsForCone ? 1 : 0);
                return new OriginModelSummary(stars.Count, rms, tilt, toward, model.AltitudeZero, model.Flexure, model.Cone, terms, stars);
            }
        }

        /// <summary>Takes out the point the model fits worst. Returns its place in <see cref="OriginModelSummary.Stars"/> (from 1) and its error, arcseconds.</summary>
        public (int Index, double ErrorArcsec) DeleteWorstPoint() {
            ThrowIfBuilding();
            lock (sync) {
                var calibration = mount.Calibration;
                if (calibration?.Model is not { } model || calibration.Points.Count == 0) {
                    throw new InvalidOperationException("There is no model");
                }
                if (calibration.Points.Count == 1) {
                    throw new InvalidOperationException("The model has one point only; clear the alignment instead");
                }
                var worst = calibration.Points.MaxBy(p => model.ResidualArcmin(p));
                var index = calibration.Points.IndexOf(worst) + 1;
                var error = model.ResidualArcmin(worst) * 60;
                calibration.Remove(worst);
                foreach (var p in points.Where(p => p.Measured == worst)) {
                    p.State = OriginModelPointState.HighRMS;
                }
                Logger.Info($"Origin model builder: the worst point, {error:0} arcsec out, taken out; {calibration.Points.Count} left");
                return (index, error);
            }
        }

        /// <summary>Drops the whole model: the mount reports parked until Unpark plate solves again.</summary>
        public async Task ClearAlignment(CancellationToken token = default) {
            ThrowIfBuilding();
            await mount.StopForModelBuild(token);
            lock (sync) {
                mount.Calibration?.Clear();
                foreach (var p in points) {
                    p.State = OriginModelPointState.Generated;
                    p.Measured = null;
                }
            }
            Logger.Info("Origin model builder: the pointing model cleared");
        }

        // ── saved models ──

        private sealed class SavedModel {
            public DateTime Saved { get; set; }
            public List<OriginCalibrationPoint> Points { get; set; } = new();
        }

        internal string ModelDirectory => Path.Combine(mount.Link?.ConfigDirectory ?? throw new InvalidOperationException("The mount is not connected"), "models");

        private string ModelFile(string name) {
            var trimmed = name?.Trim();
            if (string.IsNullOrEmpty(trimmed) || trimmed.Length > 64 || trimmed.Any(c => !(char.IsLetterOrDigit(c) || c is ' ' or '-' or '_' or '.')) || trimmed.StartsWith('.')) {
                throw new ArgumentException("A model name is up to 64 letters, digits, spaces, dots, dashes and underscores");
            }
            return Path.Combine(ModelDirectory, trimmed + ".json");
        }

        public IReadOnlyList<string> ModelNames() {
            var directory = ModelDirectory;
            return Directory.Exists(directory)
                ? Directory.GetFiles(directory, "*.json").Select(Path.GetFileNameWithoutExtension).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList()
                : new List<string>();
        }

        public void SaveModel(string name) {
            var file = ModelFile(name);
            List<OriginCalibrationPoint> saved;
            lock (sync) {
                saved = mount.Calibration?.Points.ToList() ?? new List<OriginCalibrationPoint>();
            }
            if (saved.Count == 0) {
                throw new InvalidOperationException("There is no model to save");
            }
            Directory.CreateDirectory(ModelDirectory);
            File.WriteAllText(file, JsonConvert.SerializeObject(new SavedModel { Saved = DateTime.UtcNow, Points = saved }, Formatting.Indented));
            Logger.Info($"Origin model builder: {saved.Count} points saved as {file}");
        }

        /// <summary>Puts a saved model's points in place of the model's. Right only if the mount has not been moved or knocked since it was built.</summary>
        public void LoadModel(string name) {
            ThrowIfBuilding();
            var file = ModelFile(name);
            if (!File.Exists(file)) {
                throw new FileNotFoundException($"No model named {name.Trim()}");
            }
            var calibration = mount.Calibration ?? throw new InvalidOperationException("Unpark the mount first");
            var saved = JsonConvert.DeserializeObject<SavedModel>(File.ReadAllText(file));
            if (saved?.Points is not { Count: > 0 } loaded) {
                throw new InvalidDataException($"{file} holds no points");
            }
            lock (sync) {
                calibration.Load(loaded);
            }
            Logger.Info($"Origin model builder: {loaded.Count} points loaded from {file} (saved {saved.Saved:u})");
        }

        public void DeleteModel(string name) {
            var file = ModelFile(name);
            if (!File.Exists(file)) {
                throw new FileNotFoundException($"No model named {name.Trim()}");
            }
            File.Delete(file);
        }
    }
}
