#region "copyright"

/*
    Copyright © 2026 the PI.N.S. contributors

    This file is part of PI 'N' Stars.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace NINA.Equipment.Equipment.Origin {

    /// <summary>A plate-solved point: the encoders, in degrees, and where the tube really pointed, as true (apparent, unrefracted) altitude and azimuth.</summary>
    public sealed record OriginCalibrationPoint(double Enc0, double Enc1, double Altitude, double Azimuth);

    /// <summary>
    /// Where the tube points for given encoder readings, measured rather than assumed: the Origin's own alignment takes the base as level, and on
    /// uneven ground it is not (5 degrees on grass, 2026-11-08).
    ///
    /// The mount's frame is built from the encoders: azimuth A = Enc0, elevation E = a - F cos(a) with a = Enc1 + K, where K is the altitude encoder's zero
    /// and F the tube's flexure at the horizon; the optical axis is C off square to the altitude axis (cone error), so it points along
    /// (cos C cos E, sin C, cos C sin E) turned by A. A rotation, stored as a unit quaternion, takes that frame onto the true horizon frame (x north,
    /// y east, z up); it absorbs the base's tilt in any direction and the azimuth encoder's zero. Cone error shows as an azimuth error growing as
    /// 1/cos(altitude), so it matters most near the zenith.
    ///
    /// The rotation for given K and F is the least-squares one from Horn's closed-form solution with unit quaternions (B. K. P. Horn, "Closed-form
    /// solution of absolute orientation using unit quaternions", J. Opt. Soc. Am. A 4, 629-642, 1987). K, F and C are found by a search around it.
    /// </summary>
    public sealed class OriginPointingModel {

        [JsonProperty] public double W { get; private set; } = 1;
        [JsonProperty] public double X { get; private set; }
        [JsonProperty] public double Y { get; private set; }
        [JsonProperty] public double Z { get; private set; }

        /// <summary>The altitude encoder's zero, degrees: the mount frame's altitude before flexure is Enc1 plus this.</summary>
        [JsonProperty] public double AltitudeZero { get; private set; }

        /// <summary>Tube flexure at the horizon, degrees; it falls off as the cosine of the altitude.</summary>
        [JsonProperty] public double Flexure { get; private set; }

        /// <summary>Cone error, degrees: the optical axis's angle off square to the altitude axis, positive toward increasing azimuth. Zero in models saved before it was fitted.</summary>
        [JsonProperty] public double Cone { get; private set; }

        [JsonProperty] public double RmsArcmin { get; private set; }
        [JsonProperty] public int Points { get; private set; }
        [JsonProperty] public DateTime Created { get; private set; }

        /// <summary>How far, and toward which azimuth, the mount's vertical axis leans from the true vertical: the low side of the base.</summary>
        [JsonIgnore]
        public (double Degrees, double TowardAzimuth) BaseTilt {
            get {
                var up = Rotate(new[] { 0.0, 0, 1 });
                return (Deg(Math.Acos(Math.Clamp(up[2], -1, 1))), Norm360(Deg(Math.Atan2(up[1], up[0]))));
            }
        }

        public OriginPointingModel() {
        }

        internal OriginPointingModel(double w, double x, double y, double z, double altitudeZero, double flexure, double cone = 0) {
            var n = Math.Sqrt(w * w + x * x + y * y + z * z);
            (W, X, Y, Z) = (w / n, x / n, y / n, z / n);
            AltitudeZero = altitudeZero;
            Flexure = flexure;
            Cone = cone;
            Created = DateTime.UtcNow;
        }

        /// <summary>True altitude and azimuth, degrees, for these encoder readings in degrees.</summary>
        public (double Altitude, double Azimuth) ToSky(double enc0, double enc1) {
            var t = Rotate(MountVector(enc0, enc1, AltitudeZero, Flexure, Cone));
            return (Deg(Math.Asin(Math.Clamp(t[2], -1, 1))), Norm360(Deg(Math.Atan2(t[1], t[0]))));
        }

        /// <summary>The encoder readings, degrees, that point the tube at this true altitude and azimuth. Enc0 is in (-180, 180].</summary>
        public (double Enc0, double Enc1) ToEncoders(double altitude, double azimuth) {
            var m = RotateInverse(Vector(altitude, azimuth));
            // undo the cone error: m_z = cos C sin E, and the horizontal part (cos C cos E, sin C) is turned by A
            var c = Rad(Cone);
            var elevation = Math.Asin(Math.Clamp(m[2] / Math.Cos(c), -1, 1));
            var azimuthAxis = Math.Atan2(m[1], m[0]) - Math.Atan2(Math.Sin(c), Math.Cos(c) * Math.Cos(elevation));
            var mountAltitude = Deg(elevation);
            var a = mountAltitude;
            for (var i = 0; i < 30; i++) {                   // a - F cos(a) = mountAltitude; converges at once for any plausible flexure
                a = mountAltitude + Flexure * Math.Cos(Rad(a));
            }
            return (Math.IEEERemainder(Deg(azimuthAxis), 360), a - AltitudeZero);
        }

        /// <summary>Points from which the flexure is fitted as well: below this the fit leaves it at zero, having too few spare measurements to tell it apart from noise.</summary>
        public const int PointsForFlexure = 5;

        /// <summary>Points from which the cone error is fitted too: one more than for the flexure, being a sixth number.</summary>
        public const int PointsForCone = 6;

        /// <summary>
        /// Fits a model to plate-solved points: at least three, spread in azimuth for the tilt and in altitude for the flexure. Three are a quick start (rotation and
        /// altitude zero, four numbers from six measurements, so the rms means something); two would leave the altitude zero with two solutions. By default the
        /// flexure is fitted from <see cref="PointsForFlexure"/> points and the cone error from <see cref="PointsForCone"/>; put points high as well as low for the
        /// cone error, which shows most near the zenith.
        /// </summary>
        public static OriginPointingModel Fit(IReadOnlyList<OriginCalibrationPoint> points, bool? fitFlexure = null, bool? fitCone = null) {
            if (points.Count < 3) {
                throw new ArgumentException("At least three solved points are needed", nameof(points));
            }
            var withFlexure = fitFlexure ?? points.Count >= PointsForFlexure;
            var withCone = fitCone ?? points.Count >= PointsForCone;
            var truth = points.Select(p => Vector(p.Altitude, p.Azimuth)).ToArray();
            (double Rms, double K, double F, double C, double[] Q) Try(double k, double f, double c) {
                var mount = points.Select(p => MountVector(p.Enc0, p.Enc1, k, f, c)).ToArray();
                var q = Horn(mount, truth);
                var model = new OriginPointingModel(q[0], q[1], q[2], q[3], k, f, c);
                var sum = 0.0;
                for (var i = 0; i < points.Count; i++) {
                    var r = model.Rotate(mount[i]);
                    sum += Math.Pow(Deg(Math.Acos(Math.Clamp(r[0] * truth[i][0] + r[1] * truth[i][1] + r[2] * truth[i][2], -1, 1))) * 60, 2);
                }
                return (Math.Sqrt(sum / points.Count), k, f, c, q);
            }
            var flexures = withFlexure ? Steps(-2, 2, 0.1) : new[] { 0.0 };
            var best = Steps(-90, 90, 0.5).SelectMany(k => flexures.Select(f => Try(k, f, 0))).MinBy(r => r.Rms);
            // K and F trade off against each other (both shift the altitude, F less toward the zenith), so the best pair lies along a narrow sloping valley:
            // a fixed window round the coarse winner can miss it (2026-11-09, OriginSimulator: 2.0' instead of 0.33'). A pattern search follows the valley,
            // re-centring on each better pair and then shrinking its steps.
            // The cone error joins the search from zero, in the same way.
            var around = Steps(-2, 2, 1).ToArray();
            var none = new[] { 0.0 };
            for (double kStep = 0.5, fStep = withFlexure ? 0.1 : 0, cStep = withCone ? 0.1 : 0; kStep > 2e-4; kStep /= 5, fStep /= 5, cStep /= 5) {
                for (var moved = true; moved;) {
                    var centre = best;
                    var candidate = around.SelectMany(i => (withFlexure ? around : none).SelectMany(j => (withCone ? around : none).Select(l => Try(centre.K + i * kStep, centre.F + j * fStep, centre.C + l * cStep)))).MinBy(r => r.Rms);
                    moved = candidate.Rms < best.Rms - 1e-9;
                    if (moved) {
                        best = candidate;
                    }
                }
            }
            return new OriginPointingModel(best.Q[0], best.Q[1], best.Q[2], best.Q[3], best.K, best.F, best.C) { RmsArcmin = best.Rms, Points = points.Count };
        }

        /// <summary>The angle between where the model says the tube points and where a point says it did, arcminutes.</summary>
        public double ResidualArcmin(OriginCalibrationPoint p) {
            var (alt, az) = ToSky(p.Enc0, p.Enc1);
            var a = Vector(alt, az);
            var b = Vector(p.Altitude, p.Azimuth);
            return Deg(Math.Acos(Math.Clamp(a[0] * b[0] + a[1] * b[1] + a[2] * b[2], -1, 1))) * 60;
        }

        public void Save(string path) {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, JsonConvert.SerializeObject(this, Formatting.Indented));
        }

        public static OriginPointingModel Load(string path) => File.Exists(path) ? JsonConvert.DeserializeObject<OriginPointingModel>(File.ReadAllText(path)) : null;

        // ---- vectors ----

        internal static double[] Vector(double altitude, double azimuth) {
            double a = Rad(altitude), z = Rad(azimuth);
            return new[] { Math.Cos(a) * Math.Cos(z), Math.Cos(a) * Math.Sin(z), Math.Sin(a) };
        }

        private static double[] MountVector(double enc0, double enc1, double k, double f, double cone = 0) {
            var a = enc1 + k;
            double e = Rad(a - f * Math.Cos(Rad(a))), az = Rad(enc0), c = Rad(cone);
            double bx = Math.Cos(c) * Math.Cos(e), by = Math.Sin(c), bz = Math.Cos(c) * Math.Sin(e);
            return new[] { bx * Math.Cos(az) - by * Math.Sin(az), bx * Math.Sin(az) + by * Math.Cos(az), bz };
        }

        private double[] Rotate(double[] v) => Apply(W, X, Y, Z, v);

        private double[] RotateInverse(double[] v) => Apply(W, -X, -Y, -Z, v);

        private static double[] Apply(double w, double x, double y, double z, double[] v) {
            // v' = q v q*, written out as the rotation matrix of a unit quaternion
            return new[] {
                (1 - 2 * (y * y + z * z)) * v[0] + 2 * (x * y - w * z) * v[1] + 2 * (x * z + w * y) * v[2],
                2 * (x * y + w * z) * v[0] + (1 - 2 * (x * x + z * z)) * v[1] + 2 * (y * z - w * x) * v[2],
                2 * (x * z - w * y) * v[0] + 2 * (y * z + w * x) * v[1] + (1 - 2 * (x * x + y * y)) * v[2]
            };
        }

        /// <summary>Horn (1987): the unit quaternion (w, x, y, z) of the rotation taking <paramref name="from"/> onto <paramref name="to"/> in the least-squares sense,
        /// as the eigenvector of the largest eigenvalue of his symmetric matrix N.</summary>
        internal static double[] Horn(IReadOnlyList<double[]> from, IReadOnlyList<double[]> to) {
            var s = new double[3, 3];
            for (var i = 0; i < from.Count; i++) {
                for (var r = 0; r < 3; r++) {
                    for (var c = 0; c < 3; c++) {
                        s[r, c] += from[i][r] * to[i][c];
                    }
                }
            }
            double sxx = s[0, 0], sxy = s[0, 1], sxz = s[0, 2], syx = s[1, 0], syy = s[1, 1], syz = s[1, 2], szx = s[2, 0], szy = s[2, 1], szz = s[2, 2];
            var n = new double[4, 4] {
                { sxx + syy + szz, syz - szy, szx - sxz, sxy - syx },
                { syz - szy, sxx - syy - szz, sxy + syx, szx + sxz },
                { szx - sxz, sxy + syx, -sxx + syy - szz, syz + szy },
                { sxy - syx, szx + sxz, syz + szy, -sxx - syy + szz }
            };
            return LargestEigenvector(n);
        }

        /// <summary>Cyclic Jacobi rotations on a symmetric 4x4 matrix; returns the eigenvector of the largest eigenvalue.</summary>
        private static double[] LargestEigenvector(double[,] a) {
            const int size = 4;
            var v = new double[size, size];
            for (var i = 0; i < size; i++) {
                v[i, i] = 1;
            }
            for (var sweep = 0; sweep < 100; sweep++) {
                var off = 0.0;
                for (var p = 0; p < size; p++) {
                    for (var q = p + 1; q < size; q++) {
                        off += a[p, q] * a[p, q];
                    }
                }
                if (off < 1e-24) {
                    break;
                }
                for (var p = 0; p < size; p++) {
                    for (var q = p + 1; q < size; q++) {
                        if (Math.Abs(a[p, q]) < 1e-300) {
                            continue;
                        }
                        var theta = (a[q, q] - a[p, p]) / (2 * a[p, q]);
                        var t = Math.Sign(theta == 0 ? 1 : theta) / (Math.Abs(theta) + Math.Sqrt(theta * theta + 1));
                        var c = 1 / Math.Sqrt(t * t + 1);
                        var sn = t * c;
                        for (var k = 0; k < size; k++) {
                            double akp = a[k, p], akq = a[k, q];
                            a[k, p] = c * akp - sn * akq;
                            a[k, q] = sn * akp + c * akq;
                        }
                        for (var k = 0; k < size; k++) {
                            double apk = a[p, k], aqk = a[q, k];
                            a[p, k] = c * apk - sn * aqk;
                            a[q, k] = sn * apk + c * aqk;
                        }
                        for (var k = 0; k < size; k++) {
                            double vkp = v[k, p], vkq = v[k, q];
                            v[k, p] = c * vkp - sn * vkq;
                            v[k, q] = sn * vkp + c * vkq;
                        }
                    }
                }
            }
            var best = Enumerable.Range(0, size).MaxBy(i => a[i, i]);
            var e = new[] { v[0, best], v[1, best], v[2, best], v[3, best] };
            var norm = Math.Sqrt(e.Sum(x => x * x));
            return e.Select(x => x / norm).ToArray();
        }

        private static IEnumerable<double> Steps(double from, double to, double step) {
            for (var x = from; x <= to + step / 2; x += step) {
                yield return x;
            }
        }

        internal static double Rad(double degrees) => degrees * Math.PI / 180;

        internal static double Deg(double radians) => radians * 180 / Math.PI;

        internal static double Norm360(double degrees) => ((degrees % 360) + 360) % 360;
    }
}
