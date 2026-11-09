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
using Newtonsoft.Json.Linq;
using System;
using System.IO;

namespace NINA.Equipment.Equipment.Origin {

    /// <summary>
    /// Keeps the cables safe: the running total of azimuth rotation from the position where they hang straight (cable-neutral). The cables are safe within
    /// 180 degrees either side of it, and all of that is needed, or part of the sky cannot be reached: the margin for a late check and the coast after a stop
    /// comes from the speed instead (<see cref="MarginFor"/>), and moves under encoder feedback slow down before their target. On 2026-11-08 moves limited only by their target, and
    /// driven the short way round, added up to almost three turns: the power cord wrapped round the azimuth drive and snapped, and the azimuth clutch slipped.
    ///
    /// The count follows the encoder from one status message to the next, about a second apart, when the axis moves at most about 4 degrees. A move is planned
    /// in this count, never with a wrap-around, so it cannot cross the far side. The neutral reading is kept in a file between sessions; resuming assumes the
    /// mount turned less than half a turn while nothing was listening.
    /// </summary>
    public sealed class OriginCordWrap {
        public const double DefaultLimit = 180;

        /// <summary>
        /// How far past the limit a track may carry on, degrees, so that an observation is not cut short: it starts within the limit and the mount comes
        /// back the long way afterwards, through this count. The cables have slack beyond 180; this keeps well short of the three turns of 2026-11-08.
        /// </summary>
        public const double TrackOverrun = 45;
        private readonly object sync = new();
        private readonly string file;
        private double? last;
        private double? neutral;

        public OriginCordWrap(string file, double limit = DefaultLimit) {
            this.file = file;
            Limit = limit;
            try {
                if (file != null && File.Exists(file)) {
                    neutral = JObject.Parse(File.ReadAllText(file)).Value<double?>("neutral_enc0_degrees");
                }
            } catch (Exception ex) when (ex is IOException or Newtonsoft.Json.JsonException) {
                neutral = null;
            }
        }

        public double Limit { get; }

        /// <summary>Whether a cable-neutral position has been set, here or in an earlier session.</summary>
        public bool HasNeutral {
            get {
                lock (sync) {
                    return neutral.HasValue;
                }
            }
        }

        /// <summary>Degrees of azimuth from cable-neutral, positive with increasing Enc0. Zero until the first reading.</summary>
        public double Turned { get; private set; }

        /// <summary>
        /// Follows a new azimuth encoder reading, in radians as the mount reports it. With no neutral yet, here or kept from an earlier session, the first reading
        /// becomes cable-neutral and is kept: whoever connects first is asked (in the release notes) to do so with the cables hanging straight. Delete the file
        /// after unwinding the cables to set it again.
        /// </summary>
        public void Update(double enc0Radians) {
            var e = Wrap(OriginPointingModel.Deg(enc0Radians));
            var save = false;
            lock (sync) {
                if (last is double previous) {
                    Turned += Wrap(e - previous);
                } else if (neutral is double n) {
                    Turned = Wrap(e - n);
                } else {
                    neutral = e;
                    Turned = 0;
                    save = true;
                }
                last = e;
            }
            if (save) {
                Save();
            }
        }

        /// <summary>Makes the current position cable-neutral: the person at the telescope has checked that the cables hang straight.</summary>
        public void SetNeutralHere() {
            lock (sync) {
                if (last is not double e) {
                    throw new InvalidOperationException("No azimuth reading yet");
                }
                neutral = e;
                Turned = 0;
            }
            Save();
        }

        private void Save() {
            if (file == null) {
                return;
            }
            double? n;
            lock (sync) {
                n = neutral;
            }
            try {
                Directory.CreateDirectory(Path.GetDirectoryName(file));
                File.WriteAllText(file, new JObject { ["neutral_enc0_degrees"] = n, ["set_utc"] = DateTime.UtcNow.ToString("o") }.ToString());
                Logger.Info($"Origin: cable-neutral at Enc0 {n:0.00} degrees, kept in {file}");
            } catch (IOException ex) {
                Logger.Warning($"Origin: could not keep the cable-neutral in {file}: {ex.Message}");
            }
        }

        /// <summary>The count of an encoder reading within the limit, or an exception if it lies in the narrow band beyond it on the far side.</summary>
        public double TurnedFor(double enc0Degrees) {
            double n;
            lock (sync) {
                n = neutral ?? last ?? 0;
            }
            var t = Wrap(enc0Degrees - n);
            if (Math.Abs(t) > Limit) {
                throw new OriginException($"An azimuth {t:0} degrees from cable-neutral is beyond the {Limit:0} degree limit");
            }
            return t;
        }

        /// <summary>
        /// The count of an encoder reading that lies nearest to <paramref name="nearTurned"/>: a point along a track, following on from the one before it,
        /// whichever side of the limit that is.
        /// </summary>
        public double TurnedNear(double enc0Degrees, double nearTurned) {
            double n;
            lock (sync) {
                n = neutral ?? last ?? 0;
            }
            return nearTurned + Wrap(enc0Degrees - n - nearTurned);
        }

        /// <summary>Degrees to keep in hand when moving at this firmware rate and checking every two seconds: the distance covered before the check, plus the coast.</summary>
        public static double MarginFor(int rate) => Math.Abs(rate) switch { >= 9 => 12, 8 => 6, 7 => 3, _ => 1 };

        /// <summary>Whether moving in this direction (+1 or -1) is allowed from here, keeping <paramref name="margin"/> degrees in hand for a check that comes late.</summary>
        public bool CanMove(int direction, double margin = 0) => direction == 0 || Turned * Math.Sign(direction) < Limit - margin;

        private static double Wrap(double degrees) => Math.IEEERemainder(degrees, 360);
    }
}
