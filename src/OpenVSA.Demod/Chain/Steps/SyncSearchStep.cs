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
        /// <summary>
        /// How near a perfect correlation a match has to be, from 0 to 1.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <strong>An absolute criterion, because "the first match" needs the threshold to mean
        /// "a match".</strong> The score below is normalised so that a perfect correlation is
        /// exactly 1 and a random alignment lands near 1/√N — so this asks "is this nearly the
        /// pattern", not "is this better than most of the window", and those are very different
        /// questions when the answer decides where a measurement is taken from.
        /// </para>
        /// <para>
        /// <strong>It was the second question, and that was wrong.</strong> The step used to accept
        /// any correlation three times the window's average. For a sixteen-symbol pattern in four
        /// thousand symbols of random QPSK that is met by chance about twice per record: measured,
        /// a search for a pattern planted at symbol 1000 settled on symbol 623 and reported nothing
        /// wrong, because 623 really was three times more like the pattern than the average sample
        /// was. Taking the FIRST such peak, which <c>REQ-DEM-040</c> requires, made a loose
        /// threshold worse — the strongest peak was usually the true one, so the looseness had been
        /// hidden.
        /// </para>
        /// <para>
        /// Four fifths, and the arithmetic behind it: a random alignment exceeds a normalised score
        /// of <em>f</em> with probability about exp(−f²N), so a sixteen-symbol pattern over sixteen
        /// thousand sample positions expects 0.6 false peaks at 0.8 and a thirty-two-symbol one
        /// expects 2e-5. A pattern's length is the user's lever on that, and the message below
        /// reports the best score so a pattern too short to be distinctive says so rather than
        /// silently landing somewhere.
        /// </para>
        /// </remarks>
        private const double MatchFraction = 0.8;

        /// <inheritdoc />
        public DemodStep Step => DemodStep.SyncSearch;

        /// <inheritdoc />
        public StepOutcome Run(DemodContext context)
        {
            double[] working = DemodContext.Require(
                context.Working, DemodStep.Resample, DemodStep.SyncSearch);

            int[] pattern = context.Settings.SyncSymbols();
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

            // Two passes, because "only the first match shall be used" and what counts as a match
            // is measured against the whole window's average. One pass cannot know whether the
            // first strong correlation is strong RELATIVE TO ANYTHING until it has seen the rest.
            var scores = new double[samples - span];

            double best = -1.0;
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
                // Normalised so that a perfect correlation is exactly one: |sum| can be no larger
                // than sqrt(N x energy) by Cauchy-Schwarz, and reaches it only when every symbol
                // agrees in phase and amplitude. A random alignment is a walk of N steps and lands
                // near 1/sqrt(N).
                double score = energy < 1e-18
                    ? 0.0
                    : sum.Magnitude / Math.Sqrt(pattern.Length * energy);

                scores[start] = score;
                total += score;
                tried++;

                if (score > best)
                {
                    best = score;
                }
            }

            double average = tried == 0 ? 0.0 : total / tried;

            if (tried == 0 || best < MatchFraction)
            {
                context.Note(
                    "Step 6 did not find the sync pattern: the best correlation scored " +
                    best.ToString("G3", CultureInfo.InvariantCulture) + " where 1 is exact and " +
                    MatchFraction.ToString("G3", CultureInfo.InvariantCulture) +
                    " is the least that counts as a match (the window's average was " +
                    average.ToString("G3", CultureInfo.InvariantCulture) +
                    "). A longer pattern is the lever if this signal really does carry one. The " +
                    "result window was positioned without it.");

                return StepOutcome.Continue;
            }

            // THE FIRST match, not the strongest. A pattern that occurs several times in a capture
            // -- which is what a sync word in a repeating frame does -- would otherwise be found at
            // whichever repetition happened to correlate best, and a measurement would move from
            // one frame to another between acquisitions for no reason the user could see.
            int firstSample = 0;

            for (int start = 0; start < scores.Length; start++)
            {
                if (scores[start] >= MatchFraction)
                {
                    firstSample = start;

                    break;
                }
            }

            context.SyncFound = true;
            context.SyncSampleOffset = firstSample;

            return StepOutcome.Continue;
        }
    }
}
