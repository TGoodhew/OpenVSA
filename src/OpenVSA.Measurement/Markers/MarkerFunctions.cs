using System;
using System.Globalization;
using OpenVSA.Dsp.Spectrum;

namespace OpenVSA.Measurement.Markers
{
    /// <summary>
    /// What a marker function writes to (<c>REQ-MKR-005</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// An interface rather than a direct call into the measurement, because "marker to centre
    /// frequency" is a statement about a setting and the marker layer has no business knowing which
    /// object holds it. The requirement's criterion — "the measurement updates" — is then testable
    /// without a measurement, which is the point.
    /// </para>
    /// <para>
    /// <strong>Copy-value-to-parameter is named, not typed.</strong> The parameters a marker value
    /// can be copied into differ by measurement and by personality, so an enumeration here would
    /// have to be extended by every one of them. A name and a value is the contract; rejecting an
    /// unknown name is the implementer's job, and refusing is expected of it.
    /// </para>
    /// </remarks>
    public interface IMarkerParameterTarget
    {
        /// <summary>Sets the analysis centre frequency, in hertz.</summary>
        /// <param name="hz">The frequency.</param>
        void SetCenterFrequency(double hz);

        /// <summary>Sets the reference level, in dBm.</summary>
        /// <param name="dbm">The level.</param>
        void SetReferenceLevel(double dbm);

        /// <summary>Writes a value into a named parameter.</summary>
        /// <param name="parameter">The parameter's name.</param>
        /// <param name="value">The value.</param>
        /// <exception cref="ArgumentException">The target has no such parameter.</exception>
        void SetParameter(string parameter, double value);
    }

    /// <summary>
    /// The marker functions that act on the measurement rather than on the marker
    /// (<c>REQ-MKR-005</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Peak search, next peak and minimum search move a marker and live on
    /// <see cref="MarkerSet"/>; these three move a <em>setting</em>, and are separate for that
    /// reason. Mixing them would give <see cref="MarkerSet"/> a dependency on whatever holds the
    /// centre frequency, which is the coupling the interface above exists to avoid.
    /// </para>
    /// <para>
    /// <strong>Every one of them reads through <see cref="Marker.Read"/>.</strong> A marker's X is
    /// where it was put; its <em>reading</em> is the nearest bin's frequency, which is what the
    /// trace actually shows and what a user pointing at a peak means. Copying the raw X instead
    /// would set the centre frequency to a fraction of a bin away from the peak the user clicked,
    /// and the difference would show up as a measurement that never quite centres.
    /// </para>
    /// </remarks>
    public static class MarkerFunctions
    {
        /// <summary>
        /// Sets the centre frequency to a marker's frequency.
        /// </summary>
        /// <param name="marker">The marker.</param>
        /// <param name="frame">The trace it is reading.</param>
        /// <param name="target">What to write to.</param>
        /// <returns>The frequency written, in hertz.</returns>
        /// <exception cref="ArgumentNullException">An argument is null.</exception>
        /// <exception cref="InvalidOperationException">The marker has no valid reading.</exception>
        /// <remarks>
        /// A delta marker's reading is a <em>difference</em>, which is not a frequency to tune to,
        /// so its absolute position is used instead. Tuning to the difference is arithmetically
        /// possible and always wrong.
        /// </remarks>
        public static double ToCenterFrequency(
            Marker marker, SpectrumFrame frame, IMarkerParameterTarget target)
        {
            RequireAll(marker, target);

            double hz = AbsoluteX(marker, frame);

            target.SetCenterFrequency(hz);

            return hz;
        }

        /// <summary>
        /// Sets the reference level to a marker's level.
        /// </summary>
        /// <param name="marker">The marker.</param>
        /// <param name="frame">The trace it is reading.</param>
        /// <param name="target">What to write to.</param>
        /// <returns>The level written, in dBm.</returns>
        /// <exception cref="ArgumentNullException">An argument is null.</exception>
        /// <exception cref="InvalidOperationException">The marker has no valid reading.</exception>
        /// <remarks>
        /// As above, a delta marker contributes its absolute level rather than its difference: a
        /// reference level set to "12 dB" would be a reference level of 12 dBm, arrived at by
        /// accident.
        /// </remarks>
        public static double ToReferenceLevel(
            Marker marker, SpectrumFrame frame, IMarkerParameterTarget target)
        {
            RequireAll(marker, target);

            double dbm = AbsoluteY(marker, frame);

            target.SetReferenceLevel(dbm);

            return dbm;
        }

        /// <summary>
        /// Copies a marker's level into a named parameter.
        /// </summary>
        /// <param name="marker">The marker.</param>
        /// <param name="frame">The trace it is reading.</param>
        /// <param name="parameter">The parameter's name.</param>
        /// <param name="target">What to write to.</param>
        /// <returns>The value written.</returns>
        /// <exception cref="ArgumentNullException">An argument is null.</exception>
        /// <exception cref="ArgumentException"><paramref name="parameter"/> is missing.</exception>
        /// <exception cref="InvalidOperationException">The marker has no valid reading.</exception>
        /// <remarks>
        /// The marker's Y as read, delta or not: unlike the two above, "copy value" means the value
        /// the marker is displaying, and for a delta marker that value is the difference. It is
        /// what the user is looking at when they reach for the function.
        /// </remarks>
        public static double CopyValueToParameter(
            Marker marker,
            SpectrumFrame frame,
            string parameter,
            IMarkerParameterTarget target)
        {
            RequireAll(marker, target);

            if (string.IsNullOrEmpty(parameter))
            {
                throw new ArgumentException("A parameter needs a name.", nameof(parameter));
            }

            MarkerReading reading = marker.Read(frame);

            RequireValid(marker, reading);

            target.SetParameter(parameter, reading.YDbm);

            return reading.YDbm;
        }

        /// <summary>A marker's absolute frequency, whatever kind of marker it is.</summary>
        private static double AbsoluteX(Marker marker, SpectrumFrame frame)
        {
            if (marker.Type == MarkerType.Delta && marker.Reference != null)
            {
                MarkerReading own = marker.Reference.Read(frame);

                RequireValid(marker, own);

                // The delta's own position, resolved to a bin the same way a normal marker's is.
                MarkerReading absolute = AsNormal(marker).Read(frame);

                RequireValid(marker, absolute);

                return absolute.XHz;
            }

            MarkerReading reading = marker.Read(frame);

            RequireValid(marker, reading);

            return reading.XHz;
        }

        private static double AbsoluteY(Marker marker, SpectrumFrame frame)
        {
            if (marker.Type == MarkerType.Delta && marker.Reference != null)
            {
                MarkerReading absolute = AsNormal(marker).Read(frame);

                RequireValid(marker, absolute);

                return absolute.YDbm;
            }

            MarkerReading reading = marker.Read(frame);

            RequireValid(marker, reading);

            return reading.YDbm;
        }

        /// <summary>
        /// The same marker read as a normal one, so a delta's absolute position can be resolved.
        /// </summary>
        /// <remarks>
        /// A throwaway that is never added to a set and never seen by a caller. The alternative —
        /// duplicating <see cref="Marker.Read"/>'s bin resolution here — is how the two come to
        /// disagree about which bin a frequency lands in.
        /// </remarks>
        private static Marker AsNormal(Marker marker) =>
            new Marker(marker.Number, MarkerType.Normal, marker.XHz, marker.TraceLetter);

        private static void RequireAll(Marker marker, IMarkerParameterTarget target)
        {
            if (marker == null)
            {
                throw new ArgumentNullException(nameof(marker));
            }

            if (target == null)
            {
                throw new ArgumentNullException(nameof(target));
            }
        }

        private static void RequireValid(Marker marker, MarkerReading reading)
        {
            if (!reading.IsValid)
            {
                throw new InvalidOperationException(
                    "Marker " + marker.Number.ToString(CultureInfo.CurrentCulture) +
                    " has no valid reading, so there is no value to copy.");
            }
        }
    }
}
