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
            Span<float> destination) =>
            Format(complex, format, scale, binWidthHz, destination, null);

        /// <summary>
        /// Formats a complex spectrum with explicit phase and group-delay settings.
        /// </summary>
        /// <param name="complex">Interleaved real, imaginary values in volts; two per point.</param>
        /// <param name="format">The format to produce.</param>
        /// <param name="scale">The amplitude scale, for the decibel formats.</param>
        /// <param name="binWidthHz">Spacing between points, for <see cref="TraceFormat.GroupDelay"/>.</param>
        /// <param name="destination">
        /// Receives <c>points × <see cref="ValuesPerPoint"/></c> values.
        /// </param>
        /// <param name="options">
        /// Aperture and jump tolerance, or <c>null</c> for <see cref="TraceFormatOptions.Default"/>.
        /// </param>
        /// <exception cref="ArgumentException">The spans are not consistent with each other.</exception>
        /// <exception cref="ArgumentOutOfRangeException">The format is not a known one.</exception>
        public static void Format(
            ReadOnlySpan<float> complex,
            TraceFormat format,
            AmplitudeScale scale,
            double binWidthHz,
            Span<float> destination,
            TraceFormatOptions options)
        {
            TraceFormatOptions settings = options ?? TraceFormatOptions.Default;

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
                    Unwrap(complex, points, destination, settings.JumpToleranceDegrees);
                    break;

                case TraceFormat.GroupDelay:
                    GroupDelay(complex, points, binWidthHz, destination, settings);
                    break;

                case TraceFormat.IQ:
                    complex.CopyTo(destination);
                    break;

                default:
                    throw new ArgumentOutOfRangeException(nameof(format), format, "Unknown trace format.");
            }
        }

        /// <summary>
        /// Phase in degrees, unwrapped across the jump tolerance (<c>REQ-DSP-044</c>).
        /// </summary>
        /// <param name="complex">The spectrum.</param>
        /// <param name="points">Points in it.</param>
        /// <param name="destination">Receives the unwrapped phase, in degrees.</param>
        /// <param name="jumpToleranceDegrees">
        /// Step above which a wrap is assumed rather than a real excursion; 180° is the standard
        /// ±π threshold.
        /// </param>
        /// <remarks>
        /// <strong>The reference point is the first point of the trace</strong>, which
        /// <c>REQ-DSP-044</c> requires be documented and <see cref="TraceFormatOptions"/> states
        /// again where a reader of the annotation will find it. Any reference that moved with the
        /// signal — the peak, say — would make a phase trace unreproducible between two runs of the
        /// same measurement.
        /// </remarks>
        private static void Unwrap(
            ReadOnlySpan<float> complex,
            int points,
            Span<float> destination,
            double jumpToleranceDegrees)
        {
            double tolerance = jumpToleranceDegrees * Math.PI / 180.0;
            double turns = 0.0;
            double previous = 0.0;

            for (int i = 0; i < points; i++)
            {
                double phase = Math.Atan2(complex[i * 2 + 1], complex[i * 2]);

                if (i > TraceFormatOptions.ReferencePointIndex)
                {
                    double step = phase - previous;

                    if (step > tolerance)
                    {
                        turns -= 2.0 * Math.PI;
                    }
                    else if (step < -tolerance)
                    {
                        turns += 2.0 * Math.PI;
                    }
                }

                previous = phase;
                destination[i] = (float)((phase + turns) * 180.0 / Math.PI);
            }
        }

        /// <summary>
        /// Group delay in seconds: <c>−dφ/dω</c> over the configured aperture
        /// (<c>REQ-DSP-045</c>).
        /// </summary>
        /// <remarks>
        /// <para>
        /// Computed from unwrapped phase, because a wrap in the middle of the difference produces a
        /// delay spike of a full turn's worth that looks like a real feature.
        /// </para>
        /// <para>
        /// <strong>The aperture is a span, and a wider one is a real trade.</strong> The difference
        /// is taken across <c>aperture</c> bins and divided by that width, which averages the
        /// derivative: a noisy trace comes out smoother and a narrow feature comes out broader.
        /// That is why <c>REQ-DSP-045</c> puts the figure in the annotation — the trace cannot be
        /// read without it.
        /// </para>
        /// <para>
        /// The aperture is centred where it can be and one-sided at the ends, so a delay is
        /// produced for every point. Blanking half an aperture at each end of the trace would lose
        /// exactly the band edges a group-delay measurement is usually made to look at.
        /// </para>
        /// </remarks>
        private static void GroupDelay(
            ReadOnlySpan<float> complex,
            int points,
            double binWidthHz,
            Span<float> destination,
            TraceFormatOptions options)
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
            Unwrap(complex, points, new Span<float>(phase), options.JumpToleranceDegrees);

            int aperture = Math.Min(options.ApertureBins, points - 1);
            double radiansPerHz = Math.PI / 180.0 / (2.0 * Math.PI * binWidthHz);

            for (int i = 0; i < points; i++)
            {
                int first = i - aperture / 2;

                if (first < 0)
                {
                    first = 0;
                }

                if (first + aperture > points - 1)
                {
                    first = points - 1 - aperture;
                }

                destination[i] =
                    (float)(-(phase[first + aperture] - phase[first]) * radiansPerHz / aperture);
            }
        }

        private static double MagnitudeSquared(ReadOnlySpan<float> complex, int index)
        {
            double re = complex[index * 2];
            double im = complex[index * 2 + 1];
            return re * re + im * im;
        }
    }
}
