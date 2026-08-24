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
        /// <param name="tapRateHz">The rate the taps are spaced at.</param>
        /// <param name="signalToNoiseDb">What the measurement reported, for the regularisation.</param>
        /// <returns>The response, or <c>null</c> when the equaliser did not run.</returns>
        internal static ChannelResponse For(
            Iq[] coefficients, double tapRateHz, double signalToNoiseDb)
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

            double largest = 0.0;

            for (int bin = 0; bin < Points; bin++)
            {
                double power = Iq.At(interleaved, bin).MagnitudeSquared;

                if (power > largest)
                {
                    largest = power;
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

            double epsilon = largest * Math.Pow(10.0, -useful / 10.0);

            var frequencies = new List<double>(Points);
            var magnitude = new List<double>(Points);
            var phase = new List<double>(Points);

            // From the most negative frequency to the most positive, so a plot reads left to right.
            for (int point = 0; point < Points; point++)
            {
                int bin = (point + (Points / 2)) % Points;
                double hertz = (point - (Points / 2)) * tapRateHz / Points;

                Iq response = Iq.At(interleaved, bin);
                double power = response.MagnitudeSquared;

                // C = W* / (|W|^2 + e), which is 1/W away from the nulls and bounded at them.
                double scale = 1.0 / (power + epsilon);
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
                frequencies, magnitude, phase, GroupDelay(phase, tapRateHz), epsilon, useful);
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
