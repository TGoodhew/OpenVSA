using System;
using System.Globalization;
using OpenVSA.Demod.Signal;

namespace OpenVSA.Demod.Chain.Steps
{
    /// <summary>
    /// Step 6, optional: find the sync pattern, so the result window can be positioned on it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Scope.</strong> <c>REQ-DEM-040</c> specifies the sync-pattern search — how the
    /// pattern is entered, what happens when it appears more than once, how far into the waveform
    /// the search reaches. This is the correlation that step 7 needs a position from, and no more.
    /// </para>
    /// <para>
    /// <strong>Magnitude, not the complex value.</strong> The carrier phase is still unknown at
    /// this point in the chain — step 8 has not run — so a correlation that kept its phase would
    /// peak or cancel depending on where the carrier happened to be. The magnitude is invariant to
    /// that, which is why the search can run before the phase is known and the result window can be
    /// positioned before it too.
    /// </para>
    /// <para>
    /// <strong>Not finding it is reported, not hidden.</strong> A search that quietly returned
    /// position zero would put the result window at the start of the waveform and demodulate
    /// something plausible, and nothing in the result would say the pattern had never been seen.
    /// </para>
    /// </remarks>
    internal sealed class SyncSearchStep : IChainStep
    {
        /// <summary>
        /// How far above the average correlation the peak must stand to count as found.
        /// </summary>
        private const double PeakRatio = 3.0;

        /// <inheritdoc />
        public DemodStep Step => DemodStep.SyncSearch;

        /// <inheritdoc />
        public StepOutcome Run(DemodContext context)
        {
            double[] working = DemodContext.Require(
                context.Working, DemodStep.Resample, DemodStep.SyncSearch);

            int[] pattern = context.Settings.SyncPattern;
            int perSymbol = context.Settings.PointsPerSymbol;
            int samples = Iq.Count(working);
            int span = (pattern.Length - 1) * perSymbol;

            if (span >= samples)
            {
                context.Note(
                    "The sync pattern spans more of the waveform than there is, so step 6 had " +
                    "nothing to search.");

                return StepOutcome.Continue;
            }

            var ideal = new Iq[pattern.Length];

            for (int symbol = 0; symbol < pattern.Length; symbol++)
            {
                ideal[symbol] = context.Settings.Constellation.Ideal(pattern[symbol]);
            }

            double best = -1.0;
            int bestSample = 0;
            double total = 0.0;
            int tried = 0;

            for (int start = 0; start + span < samples; start++)
            {
                Iq sum = Iq.Zero;
                double energy = 0.0;

                for (int symbol = 0; symbol < pattern.Length; symbol++)
                {
                    Iq value = Iq.At(working, start + (symbol * perSymbol));

                    sum = sum + (value * ideal[symbol].Conjugate());
                    energy += value.MagnitudeSquared;
                }

                // Normalised by the energy under the correlation, so a loud stretch of noise does
                // not beat a quiet match. Without this the search reliably finds the strongest part
                // of the signal rather than the pattern.
                double score = energy < 1e-18 ? 0.0 : sum.Magnitude / Math.Sqrt(energy);

                total += score;
                tried++;

                if (score > best)
                {
                    best = score;
                    bestSample = start;
                }
            }

            double average = tried == 0 ? 0.0 : total / tried;

            if (tried == 0 || best < average * PeakRatio)
            {
                context.Note(
                    "Step 6 did not find the sync pattern: the best correlation was " +
                    best.ToString("G3", CultureInfo.InvariantCulture) + " against an average of " +
                    average.ToString("G3", CultureInfo.InvariantCulture) +
                    ", which is not a peak. The result window was positioned without it.");

                return StepOutcome.Continue;
            }

            context.SyncFound = true;
            context.SyncSampleOffset = bestSample;

            return StepOutcome.Continue;
        }
    }
}
