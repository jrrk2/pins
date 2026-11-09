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
using NINA.Equipment.Equipment.MyGuider;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Equipment.Equipment.Origin {

    /// <summary>
    /// Guiding and dithering without a guide camera or pulse guiding. The mount's guide input is hardware: a guide camera such as an SSAG drives it directly
    /// (PHD2 "on camera"), and PHD2 dithers through it too, so that set-up needs nothing from PI.N.S.'s driver; no firmware pulse command is known, so the
    /// driver does not implement PulseGuide (CanPulseGuide is false) and Mount Dither cannot work. Without a guide camera, instead, the frames
    /// themselves are plate solved in the background, and the firmware is handed a fresh moving-target track for the target plus a correction learned
    /// from the solves plus a dither offset. The correction takes up what the pointing model leaves (a minute or so) and any drift; the dither moves the
    /// field between frames by N.I.N.A.'s own choice of offset (<see cref="DitherOffsetSelector"/>), as Mount Dither does.
    ///
    /// Not to be run together with PHD2 guiding through the mount's guide input: both would correct the same pointing.
    ///
    /// Offsets are on the sky at the target, arcseconds east (increasing RA) and north. Frames come from the imaging itself through <see cref="Feed"/>, so
    /// the guider never takes the camera; <see cref="GuideOnce"/> takes one itself, for when nothing else is using it.
    /// </summary>
    public sealed class OriginTrackGuider {
        private readonly OriginCalibration calibration;
        private readonly OriginBackend backend;
        private readonly Func<OriginFrame, CancellationToken, Task<(double RaDegrees, double DecDegrees)?>> solve;
        private readonly DitherOffsetSelector ditherSelector;
        private readonly SemaphoreSlim retrack = new(1, 1);
        private Func<DateTime, (double RaDegrees, double DecDegrees)> ephemeris;
        private DateTime trackEnd;
        private DitherOffset ditherPixels = new(0, 0);

        public OriginTrackGuider(OriginCalibration calibration, OriginBackend backend, Func<OriginFrame, CancellationToken, Task<(double RaDegrees, double DecDegrees)?>> solve)
            : this(calibration, backend, solve, new DitherOffsetSelector()) {
        }

        internal OriginTrackGuider(OriginCalibration calibration, OriginBackend backend, Func<OriginFrame, CancellationToken, Task<(double RaDegrees, double DecDegrees)?>> solve, DitherOffsetSelector ditherSelector) {
            (this.calibration, this.backend, this.solve, this.ditherSelector) = (calibration, backend, solve, ditherSelector);
        }

        /// <summary>Arcseconds a pixel: the Origin's 2.4 um pixels at 335 mm.</summary>
        public double PixelScale { get; set; } = 1.478;

        /// <summary>Share of each measured error taken up at once: below 1 so that one poor solve does not throw the mount about.</summary>
        public double Gain { get; set; } = 0.7;

        /// <summary>Errors smaller than this, arcseconds, are left alone: about the solves' own scatter.</summary>
        public double DeadBand { get; set; } = 3;

        /// <summary>Spacing of the track's points.</summary>
        public TimeSpan TrackStep { get; set; } = TimeSpan.FromSeconds(15);

        /// <summary>The correction learned so far, arcseconds east and north.</summary>
        public (double East, double North) Correction { get; private set; }

        /// <summary>The dither offset now, arcseconds east and north.</summary>
        public (double East, double North) Dither => (ditherPixels.WestEastPixels * PixelScale, ditherPixels.NorthSouthPixels * PixelScale);

        /// <summary>Every measured error, arcseconds east and north from where the field should be, with its time.</summary>
        public List<(DateTime Utc, double East, double North)> Errors { get; } = new();

        /// <summary>Moves to the target and starts tracking it, with no correction or dither yet.</summary>
        public async Task Start(Func<DateTime, (double RaDegrees, double DecDegrees)> target, TimeSpan length, CancellationToken token = default) {
            ephemeris = target;
            trackEnd = DateTime.UtcNow + length;
            Correction = (0, 0);
            ditherPixels = new DitherOffset(0, 0);
            Errors.Clear();
            await calibration.Track(Commanded, length, TrackStep, token);
        }

        /// <summary>Where the field should be centred at a time: the target plus the dither.</summary>
        public (double RaDegrees, double DecDegrees) Wanted(DateTime utc) => Shift(ephemeris(utc), Dither.East, Dither.North);

        /// <summary>What the track is built for: the target plus the correction plus the dither.</summary>
        private (double RaDegrees, double DecDegrees) Commanded(DateTime utc) => Shift(ephemeris(utc), Correction.East + Dither.East, Correction.North + Dither.North);

        /// <summary>A frame taken while tracking, with the middle of its exposure: plate solved, and the track corrected if the field is off. Returns the error, or null if it did not solve.</summary>
        public async Task<(double East, double North)?> Feed(OriginFrame frame, DateTime midExposure, CancellationToken token = default) {
            if (ephemeris == null) {
                throw new InvalidOperationException("Not guiding");
            }
            var solved = await solve(frame, token);
            if (solved is not (double ra, double dec)) {
                Logger.Info("Origin guider: the frame did not solve; no correction");
                return null;
            }
            var error = Offset(Wanted(midExposure), (ra, dec));
            Errors.Add((midExposure, error.East, error.North));
            var next = Correct(Correction, error, Gain, DeadBand);
            Logger.Info($"Origin guider: field {error.East:+0.0;-0.0}\" east, {error.North:+0.0;-0.0}\" north of where it should be; correction now {next.East:+0.0;-0.0}\", {next.North:+0.0;-0.0}\"");
            if (next != Correction) {
                Correction = next;
                await Retrack(token);
            }
            return error;
        }

        /// <summary>Takes a frame itself and feeds it in.</summary>
        public async Task<(double East, double North)?> GuideOnce(double exposure = 2, int iso = 800, CancellationToken token = default) {
            var midExposure = DateTime.UtcNow + TimeSpan.FromSeconds(exposure / 2);
            var frame = await backend.TakeFrame(exposure, iso, 1, TimeSpan.FromSeconds(exposure + 60), token);
            return await Feed(frame, midExposure, token);
        }

        /// <summary>
        /// Moves the field to a new dither offset, chosen as Mount Dither chooses (normal with this many pixels' spread, at least <paramref name="minimumPixels"/>
        /// from the last), and waits for it to settle.
        /// </summary>
        public async Task DitherBy(double pixels, TimeSpan settle, double minimumPixels = 0, bool raOnly = false, CancellationToken token = default) {
            if (ephemeris == null) {
                throw new InvalidOperationException("Not guiding");
            }
            ditherPixels = ditherSelector.SelectOffset(ditherPixels, pixels, minimumPixels, raOnly);
            Logger.Info($"Origin guider: dither to {Dither.East:+0.0;-0.0}\" east, {Dither.North:+0.0;-0.0}\" north");
            await Retrack(token);
            await Task.Delay(settle, token);
        }

        /// <summary>The new correction after an error: moved against the error by the gain, unless the error is within the dead band.</summary>
        internal static (double East, double North) Correct((double East, double North) correction, (double East, double North) error, double gain, double deadBand) =>
            Math.Sqrt(error.East * error.East + error.North * error.North) <= deadBand ? correction : (correction.East - gain * error.East, correction.North - gain * error.North);

        /// <summary>A position moved on the sky by arcseconds east and north.</summary>
        internal static (double RaDegrees, double DecDegrees) Shift((double RaDegrees, double DecDegrees) p, double east, double north) =>
            (p.RaDegrees + east / 3600 / Math.Cos(p.DecDegrees * Math.PI / 180), p.DecDegrees + north / 3600);

        /// <summary>Arcseconds east and north from one position to another (small angles).</summary>
        internal static (double East, double North) Offset((double RaDegrees, double DecDegrees) from, (double RaDegrees, double DecDegrees) to) =>
            (Math.IEEERemainder(to.RaDegrees - from.RaDegrees, 360) * Math.Cos(to.DecDegrees * Math.PI / 180) * 3600, (to.DecDegrees - from.DecDegrees) * 3600);

        /// <summary>A fresh track from now, for the target with the current correction and dither: no move first, the firmware takes up the small change itself.</summary>
        private async Task Retrack(CancellationToken token) {
            await retrack.WaitAsync(token);
            try {
                var remaining = trackEnd - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero) {
                    return;
                }
                await backend.QueryMount(token);
                await backend.TrackMovingTarget(calibration.BuildTrack(Commanded, DateTime.UtcNow + TimeSpan.FromSeconds(1), remaining, TrackStep), token);
            } finally {
                retrack.Release();
            }
        }
    }
}
