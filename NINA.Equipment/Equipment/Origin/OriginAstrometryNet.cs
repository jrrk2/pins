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
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Equipment.Equipment.Origin {

    /// <summary>
    /// Plate solves an Origin frame with astrometry.net's solve-field, given index files for 20-120 arcminute fields (4208-4211 cover the Origin's 75 by 50).
    /// The frame is a raw Bayer FITS, so it is binned by two (-z 2), as OriginMonitor does.
    /// </summary>
    public sealed class OriginAstrometryNet {
        private static readonly Regex center = new(@"Field center: \(RA,Dec\) = \(([-\d.]+), ([-\d.]+)\) deg", RegexOptions.Compiled);
        private readonly string[] indexDirectories;
        private readonly TimeSpan timeout;

        /// <param name="indexDirectory">Where the index files are; otherwise ORIGIN_INDEX_DIR, or whichever of ~/astrometry_indices, /usr/share/astrometry and
        /// /usr/local/share/astrometry exist (Ubuntu installs its index packages, such as astrometry-data-tycho2-07-littleendian, in /usr/share/astrometry).</param>
        public OriginAstrometryNet(string indexDirectory = null, TimeSpan? timeout = null) {
            var configured = indexDirectory ?? Environment.GetEnvironmentVariable("ORIGIN_INDEX_DIR");
            indexDirectories = configured != null
                ? new[] { configured }
                : new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "astrometry_indices"), "/usr/share/astrometry", "/usr/local/share/astrometry" }
                    .Where(Directory.Exists).ToArray();
            this.timeout = timeout ?? TimeSpan.FromSeconds(90);
        }

        /// <summary>The J2000 centre of the frame, degrees, or null if it did not solve.</summary>
        public async Task<(double RaDegrees, double DecDegrees)?> Solve(OriginFrame frame, CancellationToken token = default) {
            if (!frame.IsFits) {
                return null;
            }
            if (indexDirectories.Length == 0) {
                throw new OriginException("No astrometry.net index files: install astrometry-data-tycho2-07-littleendian, or put index files in ~/astrometry_indices");
            }
            var dir = Path.Combine(Path.GetTempPath(), "origin-solve-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try {
                var image = Path.Combine(dir, "frame.fits");
                var config = Path.Combine(dir, "astrometry.cfg");
                await File.WriteAllBytesAsync(image, frame.Data, token);
                await File.WriteAllTextAsync(config, $"cpulimit {(int)timeout.TotalSeconds}\n{string.Concat(indexDirectories.Select(d => $"add_path {d}\n"))}autoindex\n", token);
                var start = new ProcessStartInfo("solve-field") {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false
                };
                foreach (var arg in new[] { "--config", config, "--overwrite", "--no-plots", "-z", "2", "--scale-units", "degwidth", "--scale-low", "0.8", "--scale-high", "2.0",
                                            "--cpulimit", ((int)timeout.TotalSeconds).ToString(CultureInfo.InvariantCulture), "--dir", dir, "--new-fits", "none", "--index-xyls", "none",
                                            "--rdls", "none", "--corr", "none", "--match", "none", "--solved", "none", image }) {
                    start.ArgumentList.Add(arg);
                }
                using var process = Process.Start(start) ?? throw new OriginException("Could not start solve-field");
                var output = process.StandardOutput.ReadToEndAsync(token);
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
                limit.CancelAfter(timeout + TimeSpan.FromSeconds(30));
                try {
                    await process.WaitForExitAsync(limit.Token);
                } catch (OperationCanceledException) {
                    process.Kill(entireProcessTree: true);
                    if (token.IsCancellationRequested) {
                        throw;
                    }
                    return null;
                }
                var m = center.Match(await output);
                return m.Success ? (double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture)) : null;
            } catch (System.ComponentModel.Win32Exception ex) {
                throw new OriginException("solve-field (astrometry.net) is not installed", ex);
            } finally {
                try {
                    Directory.Delete(dir, recursive: true);
                } catch (IOException ex) {
                    Logger.Debug($"Origin: could not remove {dir}: {ex.Message}");
                }
            }
        }
    }
}
