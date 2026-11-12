#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NINA.Image.Interfaces;
using NINA.Profile.Interfaces;
using NINA.Core.Utility;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NINA.Core.Model;
using NINA.Core.Locale;
using NINA.PlateSolving.Interfaces;

namespace NINA.PlateSolving {

    public class ImageSolver : IImageSolver {

        public ImageSolver(IPlateSolver plateSolver, IPlateSolver blindSolver) {
            this.plateSolver = plateSolver;
            this.blindSolver = blindSolver;
        }

        /// <summary>
        /// Fewer stars than this and no solver can match the frame (astrometry.net builds its quads from four stars and checks them against
        /// more): a planet in twilight, cloud, or a defocused frame. Such a frame is not solved, near or blind, which would only take a minute to fail.
        /// </summary>
        public const int MinimumStars = 5;

        public async Task<PlateSolveResult> Solve(IImageData source, PlateSolveParameter parameter, IProgress<ApplicationStatus> progress, CancellationToken ct) {
            ValidatePrerequisites(parameter);
            // the star count from a star detection already run on this frame (the camera page runs one); -1 when none ran, and then the frame is solved as ever
            var stars = source?.StarDetectionAnalysis?.DetectedStars ?? -1;
            if (stars >= 0 && stars < MinimumStars) {
                Logger.Info($"Platesolve skipped: {stars} star{(stars == 1 ? "" : "s")} detected, fewer than the {MinimumStars} a solve needs");
                return new PlateSolveResult() { Success = false };
            }
            var solver = GetSolver(parameter);

            Logger.Info($"Platesolving with parameters: {parameter}");
            progress?.Report(new ApplicationStatus() { Status = Loc.Instance["LblPlateSolving"] });

            var result = await solver.SolveAsync(source, parameter, progress, ct);
            if (parameter.BlindFailoverEnabled && result.Success == false && parameter.Coordinates != null && blindSolver != null) {
                //Blind solve failover
                Logger.Debug($"Initial solve failed. Falling back to blind solver");
                var blindParameter = parameter.Clone();
                blindParameter.Coordinates = null;
                result = await Solve(source, blindParameter, progress, ct);
            }

            if (result.Success) {
                Logger.Info($"Platesolve successful: Coordinates: {result.Coordinates} - Position Angle: {result.PositionAngle}");
            } else {
                Logger.Info($"Platesolve failed");
            }

            progress?.Report(new ApplicationStatus() { Status = string.Empty });

            return result;
        }

        protected IProfileService profileService;
        private IPlateSolver plateSolver;
        private IPlateSolver blindSolver;

        protected IPlateSolver GetSolver(PlateSolveParameter parameter) {
            if (parameter.Coordinates == null) {
                return blindSolver;
            } else {
                return plateSolver;
            }
        }

        /// <summary>
        /// Validates general prerequisites that need to be set up to use the plate solvers
        /// </summary>
        protected void ValidatePrerequisites(PlateSolveParameter parameter) {
            if (parameter == null) {
                throw new ArgumentNullException(nameof(PlateSolveParameter));
            }

            double focalLength = parameter.FocalLength;

            // Check to make sure user has supplied the telescope's effective focal length (in mm)
            if (double.IsNaN(focalLength) || focalLength <= 0) {
                throw new Exception(Loc.Instance["LblPlateSolveNoFocalLength"]);
            }
        }
    }
}