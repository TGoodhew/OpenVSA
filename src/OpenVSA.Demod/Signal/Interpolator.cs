using System;

namespace OpenVSA.Demod.Signal
{
    /// <summary>
    /// Reads a band-limited signal at a position between its samples.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two steps need this and they need the same thing. Step 4 resamples to a whole number of
    /// points per symbol from whatever rate the acquisition arrived at, which is a ratio nobody
    /// chose and is almost never an integer. Step 8 estimates symbol timing, and a timing estimate
    /// is exactly a request to read the waveform a fraction of a sample away from where it was
    /// taken.
    /// </para>
    /// <para>
    /// <strong>Windowed sinc, not linear or cubic.</strong> Linear interpolation of a signal at
    /// four samples per symbol costs several percent EVM on its own — enough to swamp what the
    /// equaliser of step 11 is supposed to be shown improving. A Kaiser-windowed sinc over sixteen
    /// samples costs a fraction of a tenth of a percent, which is below anything the chain's
    /// criteria measure.
    /// </para>
    /// <para>
    /// <strong>Off the end reads zero.</strong> The alternative — clamping to the end sample —
    /// invents a constant tail that the matched filter then rings on, and the ringing appears in
    /// the result as EVM at the block edges that no impairment put there.
    /// </para>
    /// </remarks>
    internal static class Interpolator
    {
        /// <summary>Half the kernel's length, in samples.</summary>
        internal const int HalfLength = 8;

        /// <summary>
        /// The Kaiser shape parameter. 8.6 is the classic value for about 90 dB of stopband
        /// rejection, which puts the interpolator's own error below the quantisation of anything
        /// this chain will be asked to demodulate.
        /// </summary>
        private const double Beta = 8.6;

        private static readonly double BesselAtBeta = BesselI0(Beta);

        /// <summary>
        /// The signal at a fractional sample position.
        /// </summary>
        /// <param name="interleaved">The signal, real and imaginary alternating.</param>
        /// <param name="position">Where to read, in samples from the start.</param>
        /// <returns>The interpolated sample; zero where the kernel falls entirely outside.</returns>
        internal static Iq At(double[] interleaved, double position)
        {
            int samples = Iq.Count(interleaved);
            int centre = (int)Math.Floor(position);
            double fraction = position - centre;

            double i = 0.0;
            double q = 0.0;

            for (int tap = -HalfLength + 1; tap <= HalfLength; tap++)
            {
                int index = centre + tap;

                if (index < 0 || index >= samples)
                {
                    continue;
                }

                double offset = tap - fraction;
                double weight = Sinc(offset) * Window(offset);

                i += weight * interleaved[2 * index];
                q += weight * interleaved[(2 * index) + 1];
            }

            return new Iq(i, q);
        }

        /// <summary>
        /// The signal's rate of change at a fractional sample position, per sample.
        /// </summary>
        /// <param name="interleaved">The signal, real and imaginary alternating.</param>
        /// <param name="position">Where to read, in samples from the start.</param>
        /// <returns>The derivative with respect to position.</returns>
        /// <remarks>
        /// A central difference over a hundredth of a sample rather than the kernel's analytic
        /// derivative. It is used only as the direction of step 8's timing update, where the
        /// convergence test is on the step's size and not on the exactness of its slope; the
        /// analytic form would be four more lines of trigonometry to buy accuracy the iteration
        /// discards.
        /// </remarks>
        internal static Iq SlopeAt(double[] interleaved, double position)
        {
            const double Step = 0.01;

            Iq ahead = At(interleaved, position + Step);
            Iq behind = At(interleaved, position - Step);

            return (ahead - behind) / (2.0 * Step);
        }

        /// <summary>
        /// Resamples a signal to a new rate.
        /// </summary>
        /// <param name="interleaved">The signal, real and imaginary alternating.</param>
        /// <param name="ratio">
        /// Output samples per input sample: greater than one interpolates, less than one decimates.
        /// </param>
        /// <returns>A new buffer at the new rate.</returns>
        /// <exception cref="ArgumentOutOfRangeException">The ratio is not positive.</exception>
        /// <remarks>
        /// <strong>No anti-alias filter on the way down.</strong> This is stated rather than
        /// overlooked: the chain resamples a signal that the acquisition's own decimation has
        /// already band-limited, and by the time step 4 runs the only thing outside the symbol
        /// bandwidth is noise. A general-purpose resampler would need the filter, and the polyphase
        /// decimator of <c>OpenVSA.Dsp</c> is where that work already lives.
        /// </remarks>
        internal static double[] Resample(double[] interleaved, double ratio)
        {
            if (ratio <= 0.0 || double.IsNaN(ratio) || double.IsInfinity(ratio))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(ratio), ratio, "The output rate is a positive multiple of the input's.");
            }

            int samples = Iq.Count(interleaved);
            int output = (int)Math.Floor((samples - 1) * ratio) + 1;

            if (output < 1)
            {
                return new double[0];
            }

            var resampled = new double[2 * output];

            for (int sample = 0; sample < output; sample++)
            {
                Iq value = At(interleaved, sample / ratio);

                Iq.Set(resampled, sample, value);
            }

            return resampled;
        }

        private static double Sinc(double x)
        {
            if (Math.Abs(x) < 1e-12)
            {
                return 1.0;
            }

            double angle = Math.PI * x;

            return Math.Sin(angle) / angle;
        }

        private static double Window(double x)
        {
            double normalised = x / HalfLength;

            if (normalised <= -1.0 || normalised >= 1.0)
            {
                return 0.0;
            }

            return BesselI0(Beta * Math.Sqrt(1.0 - (normalised * normalised))) / BesselAtBeta;
        }

        private static double BesselI0(double x)
        {
            double sum = 1.0;
            double term = 1.0;

            for (int order = 1; order < 40; order++)
            {
                term *= (x / (2.0 * order)) * (x / (2.0 * order));
                sum += term;

                if (term < sum * 1e-16)
                {
                    break;
                }
            }

            return sum;
        }
    }
}
