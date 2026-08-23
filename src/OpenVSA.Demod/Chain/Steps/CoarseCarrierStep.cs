using System;
using OpenVSA.Dsp.Fft;
using OpenVSA.Demod.Signal;

namespace OpenVSA.Demod.Chain.Steps
{
    /// <summary>
    /// Step 3: estimate the carrier offset over the block, and take it out.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Block estimation, not a loop.</strong> <c>REQ-DEM-002</c> makes this a design
    /// choice with a stated rationale: steps 3 and 8 fit one solution across the whole block rather
    /// than tracking, so there is no settling transient and every sample contributes. What is
    /// implemented here is the non-data-aided half of that — the modulation is removed by raising
    /// the signal to the power of the constellation's rotational symmetry, which leaves a tone at
    /// that multiple of the carrier offset, and the tone's frequency is read from where it lands in
    /// a transform of the whole window.
    /// </para>
    /// <para>
    /// <strong>A transform, not an average phase advance.</strong> The first form of this step
    /// averaged the phase advance between neighbouring samples of the raised signal, which is the
    /// textbook delay-and-multiply estimator and is a great deal shorter to write. On this signal
    /// it was wrong by 10 kHz in 8 kHz. The reason is that a pulse-shaped signal raised to a power
    /// carries a large self-noise term away from the symbol instants, and averaging a phase mixes
    /// that noise into the answer instead of rejecting it; a transform puts the wanted component in
    /// one bin and the self-noise across all of them, which is what rejecting it looks like. The
    /// peak's position is then interpolated across its neighbours, because a bin is 2 to 3 kHz wide
    /// here and the estimate is worth more than that.
    /// </para>
    /// <para>
    /// <strong>The magnitudes are kept.</strong> Raising the signal to the fourth power weights it
    /// by the fourth power of its envelope, which concentrates the estimate on the samples nearest
    /// the symbol instants — where the phase is a constellation phase and not a transition. That is
    /// a feature of the estimator rather than a defect to normalise away: an earlier version divided
    /// each sample by its magnitude first and gave the noise between symbols an equal vote.
    /// </para>
    /// <para>
    /// <strong>The unambiguous range.</strong> The transform is of the signal raised to the power of
    /// the symmetry, so an offset is only distinguishable up to the sample rate divided by twice
    /// that power. At the rates this chain works with that is a large fraction of the symbol rate,
    /// and far more than step 8 could then refine. <c>REQ-DEM-036</c> owns what the analyser does
    /// when the offset is larger than the search can reach.
    /// </para>
    /// </remarks>
    internal sealed class CoarseCarrierStep : IChainStep
    {
        /// <inheritdoc />
        public DemodStep Step => DemodStep.CoarseCarrier;

        /// <inheritdoc />
        public StepOutcome Run(DemodContext context)
        {
            double[] search = DemodContext.Require(
                context.Search, DemodStep.SearchWindow, DemodStep.CoarseCarrier);

            int samples = Iq.Count(search);

            int from = context.BurstFound ? context.BurstStartSample : 0;
            int to = context.BurstFound
                ? Math.Min(samples, context.BurstStartSample + context.BurstLengthSamples)
                : samples;

            int order = context.Settings.Constellation.RotationalSymmetry;
            int span = to - from;

            double cyclesPerSample = span < 8
                ? 0.0
                : Estimate(search, from, span, order);

            context.CoarseFrequencyHz = cyclesPerSample * context.SampleRateHz;
            context.Search = Derotate(search, samples, -cyclesPerSample);

            return StepOutcome.Continue;
        }

        private static double Estimate(double[] search, int from, int span, int order)
        {
            int length = TransformLength(span);
            var raised = new double[2 * length];

            for (int sample = 0; sample < span; sample++)
            {
                Iq value = Power(Iq.At(search, from + sample), order);

                Iq.Set(raised, sample, value);
            }

            IFftProvider fft = FftProviders.Active;

            if (!fft.SupportsLength(length))
            {
                return 0.0;
            }

            fft.Forward(new Span<double>(raised));

            int peak = 0;
            double best = -1.0;

            for (int bin = 0; bin < length; bin++)
            {
                double magnitude = Iq.At(raised, bin).MagnitudeSquared;

                if (magnitude > best)
                {
                    best = magnitude;
                    peak = bin;
                }
            }

            double offset = Interpolate(raised, length, peak);

            // Bins above the halfway point are negative frequencies. Reading them as positive ones
            // would turn a small negative offset into an enormous positive one, and the chain would
            // then derotate a clean signal into nonsense.
            double bins = peak + offset;

            if (bins > length / 2.0)
            {
                bins -= length;
            }

            return bins / (length * (double)order);
        }

        private static double Interpolate(double[] transform, int length, int peak)
        {
            if (peak <= 0 || peak >= length - 1)
            {
                return 0.0;
            }

            double left = Iq.At(transform, peak - 1).Magnitude;
            double centre = Iq.At(transform, peak).Magnitude;
            double right = Iq.At(transform, peak + 1).Magnitude;

            double denominator = left - (2.0 * centre) + right;

            if (Math.Abs(denominator) < 1e-18)
            {
                return 0.0;
            }

            double offset = 0.5 * (left - right) / denominator;

            return offset < -0.5 || offset > 0.5 ? 0.0 : offset;
        }

        private static int TransformLength(int span)
        {
            // At least twice the data, so the zero padding interpolates the transform and the peak
            // is found on a finer grid than the record length alone would give.
            int length = 16;

            while (length < span * 2)
            {
                length *= 2;
            }

            return length;
        }

        private static Iq Power(Iq value, int order)
        {
            Iq raised = value;

            for (int step = 1; step < order; step++)
            {
                raised = raised * value;
            }

            return raised;
        }

        private static double[] Derotate(double[] interleaved, int samples, double cyclesPerSample)
        {
            var rotated = new double[interleaved.Length];

            for (int sample = 0; sample < samples; sample++)
            {
                Iq value = Iq.At(interleaved, sample);
                Iq turn = Iq.FromPhase(2.0 * Math.PI * cyclesPerSample * sample);

                Iq.Set(rotated, sample, value * turn);
            }

            return rotated;
        }
    }
}
