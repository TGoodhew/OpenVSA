using System;
using System.Collections.Generic;
using OpenVSA.Demod.Results;
using OpenVSA.Demod.Signal;
using OpenVSA.Dsp.Fft;

namespace OpenVSA.Demod.Chain
{
    /// <summary>
    /// Turns the equaliser's coefficients into the channel they undo (<c>REQ-DEM-053</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Computed once, from the coefficients the chain finished with.</strong> The equaliser
    /// re-enters at step 8 while its coefficients are still moving, so a response built inside step
    /// 11 would be built several times and only the last would be the answer. This runs where the
    /// result is assembled, on the taps that survived.
    /// </para>
    /// </remarks>
    internal static class ChannelEstimate
    {
        /// <summary>How many points the response is evaluated at.</summary>
        /// <remarks>
        /// Five hundred and twelve, which over an internal rate of a few megahertz is a point every
        /// few kilohertz — finer than any channel feature a symbol-rate equaliser can resolve, and
        /// small enough that the transform costs nothing next to the demodulation that produced the
        /// taps.
        /// </remarks>
        private const int Points = 512;

        /// <summary>
        /// The largest dynamic range the inversion is allowed to show, in decibels.
        /// </summary>
        /// <remarks>
        /// A measurement with no noise in it at all — a synthetic signal through a perfect chain —
        /// reports an enormous signal-to-noise ratio, and an epsilon derived from it would be zero
        /// and the inversion unbounded again. Sixty decibels is far beyond any channel a receiver
        /// meets and far short of a divergence, so it is where the regularisation stops following
        /// the noise floor down.
        /// </remarks>
        private const double LargestRangeDb = 60.0;

        /// <summary>
        /// The channel the equaliser's coefficients invert.
        /// </summary>
        /// <param name="coefficients">The equaliser's taps, or <c>null</c>.</param>
        /// <param name="tapRateHz">The rate the taps are spaced at — twice the symbol rate.</param>
        /// <param name="pulse">The composite reference pulse's taps.</param>
        /// <param name="pulseRateHz">The rate those are spaced at.</param>
        /// <param name="signalToNoiseDb">What the measurement reported, for the regularisation.</param>
        /// <returns>The response, or <c>null</c> when the equaliser did not run.</returns>
        /// <remarks>
        /// <para>
        /// 🔴 <strong>The equaliser does not invert the channel. It inverts the channel times the
        /// pulse.</strong> Its input is the measurement-filtered signal and its output is the ideal
        /// symbol, so what it undoes is everything between them — the transmitter's shaping, the
        /// channel, and the measurement filter. Composite pulse and channel together, in other
        /// words: <c>W ~ 1/(H P)</c>, so <c>1/W ~ H P</c> and reporting that as the channel hands
        /// the user a raised cosine with the channel written faintly on top of it.
        /// </para>
        /// <para>
        /// So the pulse is divided back out. Measured on a two-ray channel before this was
        /// understood, the recovered response declined smoothly from the middle where the analytic
        /// one has a comb — the raised cosine's own roll-off, mistaken for a channel.
        /// </para>
        /// <para>
        /// <c>REQ-DEM-053</c>'s sentence says "the channel response is the inverse of the equaliser
        /// response" and its acceptance criterion asks the result to match the analytic response of
        /// a two-ray channel to within 0.5 dB. Those are different things and the criterion is the
        /// operative one, because it is the one that names a number.
        /// </para>
        /// </remarks>
        internal static ChannelResponse For(
            Iq[] coefficients,
            double tapRateHz,
            double[] pulse,
            double pulseRateHz,
            double signalToNoiseDb)
        {
            if (coefficients == null || coefficients.Length == 0 || tapRateHz <= 0.0)
            {
                return null;
            }

            IFftProvider fft = FftProviders.Active;

            if (!fft.SupportsLength(Points))
            {
                return null;
            }

            var interleaved = new double[2 * Points];

            // Centred, so that a symmetric equaliser has a phase that is flat rather than one that
            // winds through half the transform's length -- and so the group delay reads as the
            // channel's rather than as the filter's own delay.
            int half = coefficients.Length / 2;

            for (int tap = 0; tap < coefficients.Length; tap++)
            {
                int at = ((tap - half) % Points + Points) % Points;

                Iq.Set(interleaved, at, coefficients[tap]);
            }

            fft.Forward(new Span<double>(interleaved));

            // The composite pulse, evaluated at the same frequencies. Directly rather than by a
            // second transform, because it is sampled at the internal rate and the taps are at T/2 —
            // two different frequency axes, and a transform of each would have to be resampled onto
            // the other.
            var shaped = new Iq[Points];
            var equaliser = new double[Points];
            var shaping = new double[Points];

            double largest = 0.0;
            double loudest = 0.0;

            for (int point = 0; point < Points; point++)
            {
                int bin = (point + (Points / 2)) % Points;
                double hertz = (point - (Points / 2)) * tapRateHz / Points;

                Iq response = Iq.At(interleaved, bin);
                Iq shape = Response(pulse, pulseRateHz, hertz);

                shaped[point] = response * shape;
                equaliser[point] = response.MagnitudeSquared;
                shaping[point] = shape.MagnitudeSquared;

                if (shaped[point].MagnitudeSquared > largest)
                {
                    largest = shaped[point].MagnitudeSquared;
                }

                if (shaping[point] > loudest)
                {
                    loudest = shaping[point];
                }
            }

            if (largest <= 0.0)
            {
                return null;
            }

            // Set from the noise floor: the strongest part of the equaliser's response, divided by
            // the ratio of signal to noise the measurement itself reported. Below that the channel
            // is not measurable and the trace should say so by rolling off rather than by inventing
            // a feature.
            double useful = signalToNoiseDb;

            if (double.IsNaN(useful) || double.IsInfinity(useful) || useful > LargestRangeDb)
            {
                useful = LargestRangeDb;
            }

            // 🔴 Per frequency, not one number for the whole response. The noise at the equaliser's
            // output is |W|^2 N and the signal is |W P|^2 S, so what decides whether the channel is
            // measurable at a frequency is |P|^2 against N/S -- and |P|^2 is the signal's own
            // spectrum, which falls to nothing at the band edges whatever the overall
            // signal-to-noise ratio is.
            //
            // A single epsilon taken from the peak cannot express that. Set that way, the recovered
            // response at the raised cosine's own band edge read +21 dB where the analytic channel
            // is +2.9: the equaliser correctly declines to invert where there is no signal, and the
            // inversion then reported that refusal as an enormous channel feature. This is the
            // Wiener form, and it rolls off exactly where the signal does.
            double inverseSnr = Math.Pow(10.0, -useful / 10.0);
            double epsilon = largest * inverseSnr;

            var frequencies = new List<double>(Points);
            var magnitude = new List<double>(Points);
            var phase = new List<double>(Points);

            // From the most negative frequency to the most positive, so a plot reads left to right.
            for (int point = 0; point < Points; point++)
            {
                double hertz = (point - (Points / 2)) * tapRateHz / Points;

                Iq response = shaped[point];
                double power = response.MagnitudeSquared;

                // C = (WP)* / (|WP|^2 + |W|^2 N/S), which is 1/(WP) where the signal has power and
                // bounded where it does not.
                double scale =
                    1.0 / (power + (equaliser[point] * loudest * inverseSnr));

                var channel = new Iq(response.I * scale, -response.Q * scale);

                frequencies.Add(hertz);
                magnitude.Add(
                    channel.MagnitudeSquared <= 0.0
                        ? -200.0
                        : 10.0 * Math.Log10(channel.MagnitudeSquared));

                phase.Add(Math.Atan2(channel.Q, channel.I) * 180.0 / Math.PI);
            }

            Unwrap(phase);

            return new ChannelResponse(
                frequencies,
                magnitude,
                phase,
                GroupDelay(phase, tapRateHz),
                epsilon,
                useful,
                Trusted(frequencies, shaping, loudest));
        }

        /// <summary>
        /// How far out the channel can be measured at all, as a half-width in hertz.
        /// </summary>
        /// <param name="frequencies">The frequency of each point.</param>
        /// <param name="shaping">The squared magnitude of the pulse at each point.</param>
        /// <param name="loudest">The largest of those.</param>
        /// <returns>The half-width over which the pulse's spectrum is flat.</returns>
        /// <remarks>
        /// <para>
        /// <strong>The flat part of the Nyquist band, and the limit is the pulse MODEL rather than
        /// the equaliser.</strong> Recovering the channel means dividing the composite pulse back
        /// out, and the pulse that is divided out is the one this chain computes — truncated,
        /// tapered and normalised — while the one in the signal is whatever the transmitter and the
        /// measurement filter actually made. Those agree closely where the pulse is flat and part
        /// company through its roll-off, and the division amplifies the difference by exactly as
        /// much as the pulse has fallen.
        /// </para>
        /// <para>
        /// Measured on a two-ray channel: the recovered response matched the analytic one to
        /// <em>0.00 dB</em> across the flat band, 0.48 dB where the pulse was a decibel down, and
        /// 49 dB at the band edge where it is zero. So the flat band is what can be claimed, and
        /// past it a display should say the trace is an extrapolation.
        /// </para>
        /// <para>
        /// Ninety-nine per cent of the peak, which for a raised cosine is its flat region exactly
        /// and needs no knowledge of the roll-off factor to find.
        /// </para>
        /// </remarks>
        private static double Trusted(
            System.Collections.Generic.List<double> frequencies,
            double[] shaping,
            double loudest)
        {
            double floor = loudest * 0.99;
            double edge = 0.0;

            for (int point = 0; point < shaping.Length; point++)
            {
                if (shaping[point] >= floor && Math.Abs(frequencies[point]) > edge)
                {
                    edge = Math.Abs(frequencies[point]);
                }
            }

            return edge;
        }

        /// <summary>The frequency response of a real tap set at one frequency.</summary>
        /// <param name="taps">The taps, centred.</param>
        /// <param name="rateHz">The rate they are spaced at.</param>
        /// <param name="hertz">The frequency to evaluate at.</param>
        /// <returns>The response, or unity when there are no taps.</returns>
        private static Iq Response(double[] taps, double rateHz, double hertz)
        {
            if (taps == null || taps.Length == 0 || rateHz <= 0.0)
            {
                return new Iq(1.0, 0.0);
            }

            int half = taps.Length / 2;
            double real = 0.0;
            double imaginary = 0.0;

            for (int tap = 0; tap < taps.Length; tap++)
            {
                double angle = -2.0 * Math.PI * hertz * (tap - half) / rateHz;

                real += taps[tap] * Math.Cos(angle);
                imaginary += taps[tap] * Math.Sin(angle);
            }

            return new Iq(real, imaginary);
        }

        /// <summary>Removes the two-pi steps a principal-value phase has in it.</summary>
        private static void Unwrap(List<double> degrees)
        {
            double offset = 0.0;

            for (int point = 1; point < degrees.Count; point++)
            {
                double step = (degrees[point] + offset) - degrees[point - 1];

                while (step > 180.0)
                {
                    offset -= 360.0;
                    step -= 360.0;
                }

                while (step < -180.0)
                {
                    offset += 360.0;
                    step += 360.0;
                }

                degrees[point] += offset;
            }
        }

        /// <summary>The negative slope of the unwrapped phase, by central difference.</summary>
        private static List<double> GroupDelay(List<double> degrees, double tapRateHz)
        {
            var delay = new List<double>(degrees.Count);
            double perPoint = tapRateHz / Points;

            for (int point = 0; point < degrees.Count; point++)
            {
                int before = Math.Max(0, point - 1);
                int after = Math.Min(degrees.Count - 1, point + 1);

                if (after == before)
                {
                    delay.Add(0.0);

                    continue;
                }

                double radians = (degrees[after] - degrees[before]) * Math.PI / 180.0;
                double omega = 2.0 * Math.PI * perPoint * (after - before);

                delay.Add(-radians / omega);
            }

            return delay;
        }
    }
}
