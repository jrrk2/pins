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
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Equipment.Equipment.Origin {

    /// <summary>
    /// Alignment by PI.N.S. rather than by the telescope: the firmware is only asked to move and to follow a track. Its own initialisation needs a clear sky
    /// at two places it chooses, and its level-base frame is wrong on uneven ground; here the pointing comes from plate solves where PI.N.S. chooses, fitted
    /// with <see cref="OriginPointingModel"/>.
    ///
    /// <list type="number">
    /// <item>A fake initialisation if the firmware is not ready, so that it accepts gotos and tracks. Its frame is then the encoders plus two constant offsets
    /// (2026-11-08: firmware altitude = Enc1 + 40.377, azimuth = Enc0 + 74.616 in 3467 readings), with RA/Dec in J2000.</item>
    /// <item>Points: each a move by encoder feedback within the cord-wrap limit, a raw frame and a plate solve.</item>
    /// <item>The fit, kept for gotos and tracks until the mechanics change (a slipping clutch, or a knock).</item>
    /// <item>Gotos by encoder feedback to the model's encoder readings, the long way round within the limit.</item>
    /// <item>Tracking as a TargetTrack: the model's encoder path for the target, written in the firmware's frame.</item>
    /// </list>
    /// </summary>
    public sealed class OriginCalibration {
        // The rates used for moves under encoder feedback and their measured speeds, degrees a second (2026-11-07).
        private static readonly (int Rate, double Speed, double Above)[] ladder = { (9, 4.0, 8), (8, 2.0, 3), (7, 0.97, 0.8), (6, 0.27, 0) };

        private readonly OriginBackend backend;
        private readonly IOriginSky sky;
        private readonly IOriginSky firmwareSky;
        private readonly Func<OriginFrame, CancellationToken, Task<(double RaDegrees, double DecDegrees)?>> solve;

        /// <param name="sky">The sky as the tube sees it: with refraction (<see cref="SofaOriginSky.ForOrigin"/>), so that the model maps encoders to the apparent sky.</param>
        /// <param name="firmwareSky">The conversion the firmware uses between its frame and J2000, for tracks handed to it: <paramref name="sky"/> unless given.
        /// Whether the real firmware refracts is not known; OriginSimulator's does not.</param>
        public OriginCalibration(OriginBackend backend, IOriginSky sky, Func<OriginFrame, CancellationToken, Task<(double RaDegrees, double DecDegrees)?>> solve, IOriginSky firmwareSky = null) {
            (this.backend, this.sky, this.solve, this.firmwareSky) = (backend, sky, solve, firmwareSky ?? sky);
        }

        public OriginPointingModel Model { get; private set; }

        public List<OriginCalibrationPoint> Points { get; } = new();

        /// <summary>Pause after each move for the status to catch up, and the tolerance of a move, degrees.</summary>
        internal TimeSpan SettleTime { get; set; } = TimeSpan.FromSeconds(1.2);

        internal double Tolerance { get; set; } = 0.05;

        /// <summary>A fake initialisation unless the firmware already accepts gotos.</summary>
        public async Task EnsureReady(double latitude, double longitude, string timeZoneId, CancellationToken token = default) {
            if (backend.Data.TaskController.IsReadyReported && backend.Data.TaskController.IsReady) {
                return;
            }
            Logger.Info("Origin calibration: fake initialisation, so that the firmware takes gotos and tracks");
            await backend.Initialize(latitude, longitude, timeZoneId, fake: true, token: token);
        }

        /// <summary>The encoders now, degrees, after asking for a fresh status.</summary>
        public async Task<(double Enc0, double Enc1)> Encoders(CancellationToken token = default) {
            await backend.QueryMount(token);
            var m = backend.Data.Mount;
            return (Math.IEEERemainder(OriginPointingModel.Deg(m.Enc0), 360), Math.IEEERemainder(OriginPointingModel.Deg(m.Enc1), 360));
        }

        /// <summary>
        /// Moves both axes to these encoder readings by relative moves under encoder feedback. Azimuth goes the long way round when the short one would cross
        /// the far side of cable-neutral; a target beyond the limit is refused.
        /// </summary>
        public async Task MoveTo(double enc0, double enc1, CancellationToken token = default) {
            var wrap = backend.CordWrap;
            await Encoders(token);
            var target = wrap.TurnedFor(enc0);
            await Drive(isAltitude: true, async () => (await Encoders(token)).Enc1, enc1, token);
            await Drive(isAltitude: false, async () => { await Encoders(token); return wrap.Turned; }, target, token);
        }

        private async Task Drive(bool isAltitude, Func<Task<double>> read, double target, CancellationToken token) {
            for (var attempt = 0; attempt < 10; attempt++) {
                var error = target - await read();
                if (Math.Abs(error) < Tolerance) {
                    return;
                }
                var step = ladder.First(l => Math.Abs(error) > l.Above || l.Rate == 6);
                var sign = Math.Sign(error);
                if (!isAltitude && !backend.CordWrap.CanMove(sign)) {
                    throw new OriginException($"Azimuth is {backend.CordWrap.Turned:0} degrees from cable-neutral: not turning further");
                }
                await backend.SlewRates(isAltitude ? sign * step.Rate : 0, isAltitude ? 0 : sign * step.Rate, token: token);
                try {
                    var seconds = Math.Max(0.15, Math.Abs(error) / step.Speed - (step.Rate >= 8 ? 0.8 : 0));
                    var end = DateTime.UtcNow + TimeSpan.FromSeconds(seconds);
                    while (DateTime.UtcNow < end) {
                        await Task.Delay(100, token);
                        // stop only a move outward past the limit: after a track that carried on past it, the way back starts out there
                        if (!isAltitude && backend.CordWrap.Turned * sign > backend.CordWrap.Limit) {
                            break;
                        }
                    }
                } finally {
                    // Zero speed twice, as OriginMonitor stops a manual move; HaltTasks is left out so that a running track is not cancelled.
                    await backend.SlewRates(0, 0, token: CancellationToken.None);
                    await Task.Delay(300, CancellationToken.None);
                    await backend.SlewRates(0, 0, token: CancellationToken.None);
                }
                await Task.Delay(SettleTime, token);
            }
            throw new OriginException($"The {(isAltitude ? "altitude" : "azimuth")} did not reach {target:0.00}");
        }

        /// <summary>A frame here, plate solved: a calibration point, or null if it did not solve. The encoders are read after the frame, the mount being still.</summary>
        public async Task<OriginCalibrationPoint> Measure(double exposure, int iso, CancellationToken token = default) => await (await Capture(exposure, iso, token))(token);

        /// <summary>
        /// Takes the frame and reads the encoders, and returns the plate solve still to do, so that it can run while the mount moves on: the solve is the slow part
        /// (10-30 s against a few seconds for the frame) and needs nothing from the mount.
        /// </summary>
        private async Task<Func<CancellationToken, Task<OriginCalibrationPoint>>> Capture(double exposure, int iso, CancellationToken token) {
            var taken = DateTime.UtcNow;
            var frame = await backend.TakeFrame(exposure, iso, 1, TimeSpan.FromSeconds(exposure + 60), token);
            var midExposure = taken + TimeSpan.FromSeconds(exposure / 2);
            var (enc0, enc1) = await Encoders(token);
            return async t => {
                var solved = await solve(frame, t);
                if (solved is not (double ra, double dec)) {
                    Logger.Info($"Origin calibration: no solve at Enc0 {enc0:0.00} Enc1 {enc1:0.00}");
                    return null;
                }
                var (alt, az) = sky.ToHorizon(ra, dec, midExposure);
                Logger.Info($"Origin calibration: Enc0 {enc0:0.00} Enc1 {enc1:0.00} -> alt {alt:0.00} az {az:0.00}");
                return new OriginCalibrationPoint(enc0, enc1, alt, az);
            };
        }

        /// <summary>
        /// Measures a point at each (degrees from cable-neutral, altitude encoder) in the plan, then fits. Each plate solve runs while the mount moves to the next
        /// point. Three points spread round the sky are a quick start, to be improved by <see cref="Refine"/> at each target; spread the plan in azimuth for the tilt
        /// and in altitude for the flexure. Points that do not solve, under cloud, are skipped.
        /// </summary>
        public async Task<OriginPointingModel> Calibrate(IEnumerable<(double FromNeutral, double Enc1)> plan, double exposure = 2, int iso = 800, IProgress<string> progress = null, CancellationToken token = default) {
            var solving = new List<Task<OriginCalibrationPoint>>();
            try {
                foreach (var (fromNeutral, enc1) in plan) {
                    if (Math.Abs(fromNeutral) > backend.CordWrap.Limit) {
                        throw new ArgumentOutOfRangeException(nameof(plan), $"{fromNeutral} degrees from cable-neutral is beyond the limit");
                    }
                    await Encoders(token);
                    var enc0 = OriginPointingModel.Deg(backend.Data.Mount.Enc0) + (fromNeutral - backend.CordWrap.Turned);
                    await MoveTo(Math.IEEERemainder(enc0, 360), enc1, token);
                    var solveIt = await Capture(exposure, iso, token);
                    solving.Add(Task.Run(async () => {
                        var p = await solveIt(token);
                        progress?.Report(p == null ? "a point did not solve" : $"solved alt {p.Altitude:0} az {p.Azimuth:0}");
                        return p;
                    }, token));
                    progress?.Report($"{solving.Count} frames taken");
                }
            } finally {
                // the solves already started finish (or fail) before the points are used or the caller moves on
                await Task.WhenAll(solving.Select(t => t.ContinueWith(_ => { }, TaskScheduler.Default)));
            }
            Points.AddRange((await Task.WhenAll(solving)).Where(p => p != null));
            Model = OriginPointingModel.Fit(Points);
            Logger.Info($"Origin calibration: {Points.Count} points, rms {Model.RmsArcmin:0.0} arcmin, base tilt {Model.BaseTilt.Degrees:0.0} deg toward {Model.BaseTilt.TowardAzimuth:0}");
            return Model;
        }

        /// <summary>A point further than this from the model, degrees, is a bad solve or a sign that the mechanics moved (a slipping clutch, a knock): it is not added.</summary>
        public const double RefineRejectDegrees = 3;

        /// <summary>A new point this close to an older one, degrees on the sky, replaces it: the newest measurement of a place is the one that counts.</summary>
        public const double RefineSamePlaceDegrees = 2;

        /// <summary>
        /// Adds a solved point, such as the solve that checks a goto, and refits: the model gets better with each target at no extra cost. Below three points the
        /// model is a level-base one from the newest point (<see cref="OriginPointingModel.Level"/>), right where the mount last synced; from three, the fitted one. Returns how far the point was from the model before, arcminutes
        /// (the pointing error there), or null if there was no model yet, no point, or it was turned away.
        /// </summary>
        public double? Refine(OriginCalibrationPoint point) {
            if (point == null) {
                return null;
            }
            var error = Model?.ResidualArcmin(point);
            // a level-base model from one or two points can be degrees out away from them on uneven ground: only a fitted model turns a point away
            if (Points.Count >= 3 && error > RefineRejectDegrees * 60) {
                Logger.Warning($"Origin calibration: a point {error / 60:0.0} degrees from the model is left out: a bad solve, or the mount slipped (check the azimuth clutch) and needs calibrating again");
                return null;
            }
            Points.RemoveAll(p => Separation(p, point) < RefineSamePlaceDegrees);
            Points.Add(point);
            if (Points.Count >= 3) {
                Model = OriginPointingModel.Fit(Points);
                Logger.Info($"Origin calibration: refined with a point {error:0.0} arcmin out, now {Points.Count} points, rms {Model.RmsArcmin:0.0} arcmin{(Points.Count >= OriginPointingModel.PointsForFlexure ? $", flexure {Model.Flexure:0.00}" : "")}");
            } else {
                // From the newest point only: averaging two far-apart points on a tilted base puts both out (4.8 degrees each on a 9.9 degree tilt, OriginSimulator
                // 2026-11-09), and centring expects a slew back to where it synced to land there. The older point waits for the fit.
                Model = OriginPointingModel.Level(new[] { point });
                Logger.Info($"Origin calibration: {Points.Count} point{(Points.Count == 1 ? "" : "s")}; a level-base model from the newest until there are three");
            }
            return error;
        }

        /// <summary>A goto with the model, a frame there to check it, and that frame's solve added to the model. Returns the goto's pointing error, arcminutes, or null.</summary>
        public async Task<double?> GotoAndRefine(double raDegrees, double decDegrees, double exposure = 2, int iso = 800, CancellationToken token = default) {
            await Goto(raDegrees, decDegrees, token);
            return Refine(await Measure(exposure, iso, token));
        }

        private static double Separation(OriginCalibrationPoint a, OriginCalibrationPoint b) {
            var u = OriginPointingModel.Vector(a.Altitude, a.Azimuth);
            var v = OriginPointingModel.Vector(b.Altitude, b.Azimuth);
            return OriginPointingModel.Deg(Math.Acos(Math.Clamp(u[0] * v[0] + u[1] * v[1] + u[2] * v[2], -1, 1)));
        }

        /// <summary>Points the tube at a J2000 position with the model, by encoder feedback.</summary>
        public async Task Goto(double raDegrees, double decDegrees, CancellationToken token = default) {
            var model = Model ?? throw new InvalidOperationException("Not calibrated");
            // The mount does not track during or after these moves, and the sky turns 15"/s: aim where the target will be on arrival (from the distance,
            // the long way round where the cables need it), then make a short second move to where it is by then. Hand over a track to hold it there.
            var (_, now1) = await Encoders(token);
            var (alt, az) = sky.ToHorizon(raDegrees, decDegrees, DateTime.UtcNow);
            if (alt < 20) {
                throw new OriginException($"The target is at {alt:0} degrees: below 20");
            }
            var (enc0, enc1) = model.ToEncoders(alt, az);
            var arrival = SlewTime(Math.Abs(backend.CordWrap.TurnedFor(enc0) - backend.CordWrap.Turned), Math.Abs(enc1 - now1));
            foreach (var lead in new[] { arrival, TimeSpan.FromSeconds(3) }) {
                (alt, az) = sky.ToHorizon(raDegrees, decDegrees, DateTime.UtcNow + lead);
                (enc0, enc1) = model.ToEncoders(alt, az);
                await MoveTo(enc0, enc1, token);
            }
        }

        /// <summary>
        /// About how long a move takes: the axes go one after the other, each at up to 4 degrees a second, with a few seconds of finer steps at the end
        /// (measured on OriginSimulator at the real speeds, 2026-11-09).
        /// </summary>
        internal static TimeSpan SlewTime(double azimuthDegrees, double altitudeDegrees) =>
            TimeSpan.FromSeconds(azimuthDegrees / 4.0 + altitudeDegrees / 4.0 + (azimuthDegrees > 0.05 ? 6 : 0) + (altitudeDegrees > 0.05 ? 6 : 0));

        /// <summary>
        /// The track that keeps a J2000 position in view, in the firmware's frame: for each time the model's encoder readings, plus the firmware's offsets
        /// read from its status now, converted to its J2000 RA/Dec. Each point is (RA hours, Dec degrees, UTC Julian date). The start must be within the cord-wrap
        /// limit; a target that reaches the limit is followed on past it, up to <see cref="OriginCordWrap.TrackOverrun"/>, so that the observation is finished,
        /// and the next move brings the mount back the long way.
        /// </summary>
        public IReadOnlyList<(double RaHours, double DecDegrees, double JulianDate)> BuildTrack(double raDegrees, double decDegrees, DateTime startUtc, TimeSpan length, TimeSpan step) =>
            BuildTrack(_ => (raDegrees, decDegrees), startUtc, length, step);

        /// <summary>
        /// The same for a moving target (a comet, an asteroid, a satellite): <paramref name="ephemeris"/> gives its J2000 position, degrees, at each UTC time.
        /// Choose the step so that the firmware's straight lines between points stay close to the path.
        /// </summary>
        public IReadOnlyList<(double RaHours, double DecDegrees, double JulianDate)> BuildTrack(Func<DateTime, (double RaDegrees, double DecDegrees)> ephemeris, DateTime startUtc, TimeSpan length, TimeSpan step) {
            var model = Model ?? throw new InvalidOperationException("Not calibrated");
            var m = backend.Data.Mount;
            var altitudeOffset = OriginPointingModel.Deg(m.Altitude) - OriginPointingModel.Deg(m.Enc1);
            var azimuthOffset = OriginPointingModel.Deg(m.Azimuth) - OriginPointingModel.Deg(m.Enc0);
            var wrap = backend.CordWrap;
            var track = new List<(double, double, double)>();
            double? turned = null;
            for (var t = TimeSpan.Zero; t <= length; t += step) {
                var when = startUtc + t;
                var (raDegrees, decDegrees) = ephemeris(when);
                var (alt, az) = sky.ToHorizon(raDegrees, decDegrees, when);
                var (enc0, enc1) = model.ToEncoders(alt, az);
                // The start must be within the limit; after it the track follows on in the count and may carry on past the limit, up to the overrun.
                turned = turned is double previous ? wrap.TurnedNear(enc0, previous) : wrap.TurnedFor(enc0);
                if (Math.Abs(turned.Value) > wrap.Limit + OriginCordWrap.TrackOverrun) {
                    Logger.Warning($"Origin calibration: the track ends after {t.TotalMinutes:0} of {length.TotalMinutes:0} minutes, {OriginCordWrap.TrackOverrun:0} degrees past the cord-wrap limit");
                    break;
                }
                var (ra, dec) = firmwareSky.ToJ2000(enc1 + altitudeOffset, OriginPointingModel.Norm360(enc0 + azimuthOffset), when);
                track.Add((OriginPointingModel.Norm360(ra) / 15, dec, JulianDate(when)));
            }
            return track;
        }

        /// <summary>Moves to the start of the track by encoder feedback, so that the firmware has no long slew of its own, then hands the track over.</summary>
        public Task Track(double raDegrees, double decDegrees, TimeSpan length, TimeSpan? step = null, CancellationToken token = default) =>
            Track(_ => (raDegrees, decDegrees), length, step, token);

        /// <summary>The same for a moving target, given by its J2000 ephemeris.</summary>
        public async Task Track(Func<DateTime, (double RaDegrees, double DecDegrees)> ephemeris, TimeSpan length, TimeSpan? step = null, CancellationToken token = default) {
            var model = Model ?? throw new InvalidOperationException("Not calibrated");
            var start = DateTime.UtcNow + TimeSpan.FromSeconds(45);
            var (ra0, dec0) = ephemeris(start);
            var (alt, az) = sky.ToHorizon(ra0, dec0, start);
            var (enc0, enc1) = model.ToEncoders(alt, az);
            await MoveTo(enc0, enc1, token);
            await backend.QueryMount(token);
            await backend.TrackMovingTarget(BuildTrack(ephemeris, DateTime.UtcNow + TimeSpan.FromSeconds(3), length, step ?? TimeSpan.FromSeconds(30)), token);
        }

        internal static double JulianDate(DateTime utc) => utc.ToUniversalTime().Subtract(new DateTime(2000, 1, 1, 12, 0, 0, DateTimeKind.Utc)).TotalDays + 2451545.0;
    }
}
