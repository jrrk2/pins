#region "copyright"

/*
    Copyright © 2026 the PI.N.S. contributors

    This file is part of PI 'N' Stars.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NINA.Astrometry;
using System;

namespace NINA.Equipment.Equipment.Origin {

    /// <summary>
    /// Conversions between J2000 coordinates and the horizon at the site. With refraction, altitudes are apparent: where the tube points to see a star, which
    /// is what the encoders measure. A plate solve gives the J2000 of the stars imaged; converted with refraction it gives the tube's apparent direction.
    /// </summary>
    public interface IOriginSky {

        (double Altitude, double Azimuth) ToHorizon(double raDegrees, double decDegrees, DateTime utc);

        (double RaDegrees, double DecDegrees) ToJ2000(double altitude, double azimuth, DateTime utc);
    }

    /// <summary>
    /// The conversions of PI.N.S.'s astrometry, which uses the IAU SOFA routines, refraction included (SOFA's model, from pressure, temperature, humidity and
    /// wavelength). Refraction lifts a star by about 1' at 45 degrees and 2' at 28: left out, it is an altitude-dependent pointing error that the
    /// model's altitude zero and flexure only partly absorb.
    /// </summary>
    public sealed class SofaOriginSky : IOriginSky {
        private const double Wavelength = 0.55;       // micrometres: the visible
        private readonly double latitude, longitude, elevation;
        private readonly Func<(double TemperatureC, double HumidityPercent)?> weather;
        private readonly bool refract;

        /// <summary>Without <paramref name="weather"/> there is no refraction: geometric altitudes, as the firmware's own frame appears to use.</summary>
        public SofaOriginSky(double latitude, double longitude, double elevation, Func<(double TemperatureC, double HumidityPercent)?> weather = null) {
            (this.latitude, this.longitude, this.elevation, this.weather) = (latitude, longitude, elevation, weather);
            refract = weather != null;
        }

        /// <summary>
        /// Refraction from the telescope's own environment sensor: ambient temperature and humidity as it reports them (10 C and 70 % until it has). The
        /// Origin has no barometer, so the pressure is the standard atmosphere's at the site's elevation.
        /// </summary>
        public static SofaOriginSky ForOrigin(OriginBackend backend, double latitude, double longitude, double elevation) =>
            new(latitude, longitude, elevation, () => {
                var e = backend.Data.Environment;
                return (double.IsFinite(e.AmbientTemperature) ? e.AmbientTemperature : 10, double.IsFinite(e.Humidity) ? e.Humidity : 70);
            });

        /// <summary>The standard atmosphere's pressure at an elevation, hPa (scale height 8434 m).</summary>
        public static double StandardPressure(double elevationMetres) => 1013.25 * Math.Exp(-elevationMetres / 8434.0);

        private (double Pressure, double Temperature, double Humidity) Air() {
            if (!refract || weather() is not (double t, double h)) {
                return (0, 0, 0);
            }
            return (StandardPressure(elevation), t, Math.Clamp(h, 0, 100) / 100);
        }

        public (double Altitude, double Azimuth) ToHorizon(double raDegrees, double decDegrees, DateTime utc) {
            var (p, t, h) = Air();
            var c = new Coordinates(Angle.ByDegree(raDegrees), Angle.ByDegree(decDegrees), Epoch.J2000)
                .Transform(Angle.ByDegree(latitude), Angle.ByDegree(longitude), elevation, p, t, h, Wavelength, utc);
            return (c.Altitude.Degree, c.Azimuth.Degree);
        }

        public (double RaDegrees, double DecDegrees) ToJ2000(double altitude, double azimuth, DateTime utc) {
            var (p, t, h) = Air();
            var c = new TopocentricCoordinates(Angle.ByDegree(azimuth), Angle.ByDegree(altitude), Angle.ByDegree(latitude), Angle.ByDegree(longitude), elevation)
                .Transform(utc, Epoch.J2000, p, t, h, Wavelength);
            return (c.RA * 15, c.Dec);
        }
    }
}
