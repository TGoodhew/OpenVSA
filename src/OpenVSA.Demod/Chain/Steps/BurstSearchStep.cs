using System;
using System.Globalization;
using OpenVSA.Demod.Signal;

namespace OpenVSA.Demod.Chain.Steps
{
    /// <summary>
    /// Step 2, optional: find the burst within the Search Length window.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Scope.</strong> <c>REQ-DEM-041</c> specifies the burst and pulse search properly —
    /// thresholds, run lengths, what happens when several bursts are present.
    /// <c>REQ-DEM-001</c> needs step 2 to exist, to be skippable, and to narrow the region of
    /// interest when it runs. This is a power-threshold search over a smoothed magnitude, and it is
    /// deliberately the simplest thing that does that job.
    /// </para>
    /// <para>
    /// <strong>Not finding a burst is not a failure.</strong> A continuous signal has no burst in
    /// it, and the honest answer is to say so and leave the window alone rather than to return the
    /// loudest stretch of a signal that is loud throughout.
    /// </para>
    /// </remarks>
    internal sealed class BurstSearchStep : IChainStep
    {
        /// <summary>How far below the peak the burst's edges are taken to be.</summary>
        private const double ThresholdDb = 6.0;

        /// <summary>The shortest run of samples above the threshold that counts as a burst.</summary>
        private const int MinimumSymbols = 8;

        /// <inheritdoc />
        public DemodStep Step => DemodStep.BurstSearch;

        /// <inheritdoc />
        public StepOutcome Run(DemodContext context)
        {
            double[] search = DemodContext.Require(
                context.Search, DemodStep.SearchWindow, DemodStep.BurstSearch);

            int samples = Iq.Count(search);
            double perSymbol = context.SampleRateHz / context.Settings.SymbolRateHz;
            int smoothing = Math.Max(1, (int)Math.Round(perSymbol));

            double[] power = Smoothed(search, samples, smoothing);

            double peak = 0.0;

            foreach (double value in power)
            {
                if (value > peak)
                {
                    peak = value;
                }
            }

            if (peak <= 0.0)
            {
                context.Note("Step 2 found no signal in the Search Length window to search for a burst in.");

                return StepOutcome.Continue;
            }

            double threshold = peak * Math.Pow(10.0, -ThresholdDb / 10.0);

            int first = -1;
            int last = -1;

            for (int sample = 0; sample < samples; sample++)
            {
                if (power[sample] < threshold)
                {
                    continue;
                }

                if (first < 0)
                {
                    first = sample;
                }

                last = sample;
            }

            int length = first < 0 ? 0 : (last - first) + 1;
            int shortest = (int)Math.Round(MinimumSymbols * perSymbol);

            if (length < shortest)
            {
                context.Note(
                    "Step 2 found nothing above " +
                    ThresholdDb.ToString("0", CultureInfo.InvariantCulture) +
                    " dB below the peak that lasted " +
                    MinimumSymbols.ToString(CultureInfo.InvariantCulture) +
                    " symbols. The Search Length window was left as it was.");

                return StepOutcome.Continue;
            }

            if (length >= samples)
            {
                // Above the threshold from end to end: a continuous signal, not a burst. Reporting
                // "a burst filling the window" would be true and useless, and would then have the
                // result window positioned as though an edge had been found.
                context.Note(
                    "Step 2 found the signal above its threshold across the whole Search Length " +
                    "window, so there is no burst edge in it. The window was left as it was.");

                return StepOutcome.Continue;
            }

            context.BurstFound = true;
            context.BurstStartSample = first;
            context.BurstLengthSamples = length;

            return StepOutcome.Continue;
        }

        private static double[] Smoothed(double[] interleaved, int samples, int window)
        {
            var trailing = new double[samples];
            double running = 0.0;

            for (int sample = 0; sample < samples; sample++)
            {
                Iq value = Iq.At(interleaved, sample);

                running += value.MagnitudeSquared;

                if (sample >= window)
                {
                    Iq leaving = Iq.At(interleaved, sample - window);

                    running -= leaving.MagnitudeSquared;
                }

                trailing[sample] = running / Math.Min(sample + 1, window);
            }

            // Attributed to the centre of the window it was measured over, not to its trailing
            // edge. A trailing attribution moves every edge the search finds later by half a
            // symbol, which is the size of the timing error step 8 then has to remove.
            var power = new double[samples];
            int half = window / 2;

            for (int sample = 0; sample < samples; sample++)
            {
                power[sample] = trailing[Math.Min(samples - 1, sample + half)];
            }

            return power;
        }
    }
}
