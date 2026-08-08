using System;
using System.Collections.Generic;

namespace OpenVSA.TestHarness
{
    /// <summary>
    /// Finds the tones of a comb in a measured spectrum (issue #393).
    /// </summary>
    /// <remarks>
    /// <para>
    /// A pure function over levels, separate from the runner, because it is the part of a comb
    /// scenario that can be got wrong quietly and the only part that can be checked without a
    /// generator. Everything else in the scenario is plumbing.
    /// </para>
    /// </remarks>
    public static class ToneSearch
    {
        /// <summary>
        /// Head room below the weakest tone the comb should have, in decibels.
        /// </summary>
        /// <remarks>
        /// Admits a real tone the analyser has under-read while still excluding a strong tone's
        /// skirts, which fall far faster than this.
        /// </remarks>
        public const double HeadroomDb = 6.0;

        /// <summary>
        /// The bins that are local maxima above the comb's search floor, in frequency order.
        /// </summary>
        /// <param name="levelsDbm">The measured spectrum, in dBm.</param>
        /// <param name="expectedTones">How many tones the comb should have; at least two.</param>
        /// <returns>Indices of the tones found, ascending. Empty if none rose above the floor.</returns>
        /// <remarks>
        /// <para>
        /// <strong>Local maxima above a floor, NOT the N largest bins.</strong> The shoulders of a
        /// strong tone are larger than a weak tone's peak, so taking the N largest bins returns one
        /// tone reported N times and a spacing of one bin — which would read as a catastrophic
        /// failure of the frequency axis while the axis was perfectly correct.
        /// </para>
        /// <para>
        /// <strong>The floor is referred to the weakest tone the comb should have</strong>, not to
        /// the largest bin: equal tones share the total power, so each sits about
        /// 10·log10(<paramref name="expectedTones"/>) below a single carrier of the same total.
        /// A floor referred to the largest bin alone would reject every tone of a wide comb.
        /// </para>
        /// </remarks>
        public static IReadOnlyList<int> Find(IReadOnlyList<float> levelsDbm, int expectedTones)
        {
            var tones = new List<int>();

            if (levelsDbm == null || levelsDbm.Count < 3 || expectedTones < 2)
            {
                return tones;
            }

            double strongest = double.NegativeInfinity;

            for (int index = 0; index < levelsDbm.Count; index++)
            {
                float level = levelsDbm[index];

                if (!float.IsNaN(level) && level > strongest)
                {
                    strongest = level;
                }
            }

            if (double.IsNegativeInfinity(strongest))
            {
                return tones;
            }

            double floor = strongest - (10.0 * Math.Log10(expectedTones)) - HeadroomDb;

            for (int index = 1; index < levelsDbm.Count - 1; index++)
            {
                float level = levelsDbm[index];

                if (float.IsNaN(level) || level < floor)
                {
                    continue;
                }

                // Not-less on the left and strictly-greater on the right, so a two-bin flat top is
                // counted once rather than twice or not at all.
                if (level >= levelsDbm[index - 1] && level > levelsDbm[index + 1])
                {
                    tones.Add(index);
                }
            }

            return tones;
        }
    }
}
