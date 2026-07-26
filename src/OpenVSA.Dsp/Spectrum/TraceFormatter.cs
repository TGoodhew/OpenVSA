using System;

namespace OpenVSA.Dsp.Spectrum
{
    /// <summary>
    /// Converts a calibrated complex spectrum into each display format of <c>REQ-DSP-041</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Every format is a pure function of the same complex data.</strong> That is what
    /// <c>REQ-TRC-001</c> requires — changing format must not recompute the transform — and it is
    /// also what makes the requirement's own consistency checks hold rather than needing to be
    /// arranged: log and linear magnitude agree because one is the logarithm of the other, and
    /// real and imaginary recombine to the magnitude because they are its components.
    /// </para>
    /// <para>
    /// The input is in volts peak, referred to the input, with the amplitude chain already
    /// applied. Nothing here knows about full scale, window gain or impedance beyond the one
    /// offset it needs to express power in dBm.
    /// </para>
    /// </remarks>
    public static class TraceFormatter
    {
        /// <summary>Values a format produces per point: two for <see cref="TraceFormat.IQ"/>, one otherwise.</summary>
        /// <param name="format">The format.</param>
        public static int ValuesPerPoint(TraceFormat format) =>
            format == TraceFormat.IQ ? 2 : 1;

        /// <summary>
        /// Formats a complex spectrum.
        /// </summary>
        /// <param name="complex">Interleaved real, imaginary values in volts; two per point.</param>
        /// <param name="format">The format to produce.</param>
        /// <param name="scale">The amplitude scale, for the decibel formats.</param>
        /// <param name="binWidthHz">Spacing between points, for <see cref="TraceFormat.GroupDelay"/>.</param>
        /// <param name="destination">
        /// Receives <c>points × <see cref="ValuesPerPoint"/></c> values.
        /// </param>
        /// <exception cref="ArgumentException">The spans are not consistent with each other.</exception>
        /// <exception cref="ArgumentOutOfRangeException">The format is not a known one.</exception>
        public static void Format(
            ReadOnlySpan<float> complex,
            TraceFormat format,
            AmplitudeScale scale,
            double binWidthHz,
            Span<float> destination)
        {
            if (complex.Length % 2 != 0)
            {
                throw new ArgumentException(
                    "A complex spectrum needs two values per point.", nameof(complex));
            }

            int points = complex.Length / 2;
            int expected = points * ValuesPerPoint(format);

            if (destination.Length != expected)
            {
                throw new ArgumentException(
                    "Expected " + expected + " values for " + points + " points in " + format +
                    ", got " + destination.Length + ".",
                    nameof(destination));
            }

            switch (format)
            {
                case TraceFormat.LogMagnitude:
                    for (int i = 0; i < points; i++)
                    {
                        // A blanked point stays blanked. Without this it becomes the floor, and a
                        // gap in a trace turns into a line along the bottom of the graticule —
                        // which reads as a measurement rather than as an absence of one.
                        destination[i] = float.IsNaN(complex[i * 2])
                            ? float.NaN
                            : (float)scale.VoltsSquaredToDbm(MagnitudeSquared(complex, i));
                    }

                    break;

                case TraceFormat.LinearMagnitude:
                    for (int i = 0; i < points; i++)
                    {
                        destination[i] = (float)Math.Sqrt(MagnitudeSquared(complex, i));
                    }

                    break;

                case TraceFormat.Real:
                    for (int i = 0; i < points; i++)
                    {
                        destination[i] = complex[i * 2];
                    }

                    break;

                case TraceFormat.Imaginary:
                    for (int i = 0; i < points; i++)
                    {
                        destination[i] = complex[i * 2 + 1];
                    }

                    break;

                case TraceFormat.WrappedPhase:
                    for (int i = 0; i < points; i++)
                    {
                        destination[i] = (float)(Math.Atan2(complex[i * 2 + 1], complex[i * 2]) * 180.0 / Math.PI);
                    }

                    break;

                case TraceFormat.UnwrappedPhase:
                    Unwrap(complex, points, destination);
                    break;

                case TraceFormat.GroupDelay:
                    GroupDelay(complex, points, binWidthHz, destination);
                    break;

                case TraceFormat.IQ:
                    complex.CopyTo(destination);
                    break;

                default:
                    throw new ArgumentOutOfRangeException(nameof(format), format, "Unknown trace format.");
            }
        }

        /// <summary>
        /// Phase in degrees, unwrapped across ±180° boundaries (<c>REQ-DSP-044</c>).
        /// </summary>
        /// <remarks>
        /// The reference point is the first point of the trace, which is stated here because a
        /// phase trace is only reproducible if the point it is measured from is fixed. A standard
        /// ±180° threshold unwrap: any step larger than half a turn is taken to be a wrap rather
        /// than a real excursion.
        /// </remarks>
        private static void Unwrap(ReadOnlySpan<float> complex, int points, Span<float> destination)
        {
            double turns = 0.0;
            double previous = 0.0;

            for (int i = 0; i < points; i++)
            {
                double phase = Math.Atan2(complex[i * 2 + 1], complex[i * 2]);

                if (i > 0)
                {
                    double step = phase - previous;

                    if (step > Math.PI)
                    {
                        turns -= 2.0 * Math.PI;
                    }
                    else if (step < -Math.PI)
                    {
                        turns += 2.0 * Math.PI;
                    }
                }

                previous = phase;
                destination[i] = (float)((phase + turns) * 180.0 / Math.PI);
            }
        }

        /// <summary>
        /// Group delay in seconds: <c>−dφ/dω</c>, from the unwrapped phase.
        /// </summary>
        /// <remarks>
        /// Computed from unwrapped phase, because a wrap in the middle of the difference produces a
        /// delay spike of a full turn's worth that looks like a real feature. The first point takes
        /// the second's value, since a derivative needs two points and blanking one end of the
        /// trace is worse than repeating a value at it.
        /// </remarks>
        private static void GroupDelay(
            ReadOnlySpan<float> complex, int points, double binWidthHz, Span<float> destination)
        {
            if (points < 2 || !(binWidthHz > 0.0))
            {
                for (int i = 0; i < points; i++)
                {
                    destination[i] = 0.0f;
                }

                return;
            }

            var phase = new float[points];
            Unwrap(complex, points, new Span<float>(phase));

            double radiansPerHz = Math.PI / 180.0 / (2.0 * Math.PI * binWidthHz);

            for (int i = 1; i < points; i++)
            {
                destination[i] = (float)(-(phase[i] - phase[i - 1]) * radiansPerHz);
            }

            destination[0] = destination[1];
        }

        private static double MagnitudeSquared(ReadOnlySpan<float> complex, int index)
        {
            double re = complex[index * 2];
            double im = complex[index * 2 + 1];
            return re * re + im * im;
        }
    }
}
