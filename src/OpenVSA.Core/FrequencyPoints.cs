using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;

namespace OpenVSA.Core
{
    /// <summary>
    /// The displayed frequency-point counts a measurement may use (<c>REQ-DSP-022</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>N_f − 1</c> is constrained to <c>50 · 2^k</c> so that <c>N_FFT = 1.28 (N_f − 1)</c> is an
    /// integer power of two, which gives the ladder 51, 101, 201, 401, 801, … 409 601 and the FFT
    /// sizes 64, 128, 256, … 524 288. Point counts are therefore odd: the axis has a point at each
    /// end and one exactly at the centre frequency, which is what lets a centre-frequency marker
    /// land on a point rather than between two.
    /// </para>
    /// <para>
    /// <strong>The ceiling is the front end's, not this table's.</strong> 409 601 is the largest
    /// count the relations permit; how many of them a given measurement can actually have is
    /// settled by what the connected instrument can capture in one block, which is why
    /// <c>AcquisitionPlanner</c> reads it from <c>IFrontEndCapabilities</c> and nothing here
    /// assumes a particular instrument's depth.
    /// </para>
    /// <para>
    /// The documented inconsistency in the reference product's datasheet — 524 288 quoted as a
    /// displayed point count when <c>1.28 × 524 287</c> is neither an integer nor a power of two —
    /// is resolved as the specification resolves it: 524 288 is the maximum FFT size, and the
    /// maximum displayed point count is 409 601.
    /// </para>
    /// </remarks>
    public static class FrequencyPoints
    {
        /// <summary>Fewest displayed points, 51.</summary>
        public const int Minimum = 51;

        /// <summary>Most displayed points, 409 601.</summary>
        public const int Maximum = 409601;

        /// <summary>Largest FFT size the relations permit, 2^19.</summary>
        public const int MaximumTransformLength = 524288;

        private static readonly ReadOnlyCollection<int> SupportedCounts = BuildLadder();

        /// <summary>Every valid point count, ascending.</summary>
        public static IReadOnlyList<int> Supported => SupportedCounts;

        /// <summary>Whether a point count is on the ladder.</summary>
        /// <param name="points">Candidate point count.</param>
        public static bool IsValid(int points)
        {
            if (points < Minimum || points > Maximum)
            {
                return false;
            }

            int steps = points - 1;

            if (steps % 50 != 0)
            {
                return false;
            }

            int multiple = steps / 50;
            return (multiple & (multiple - 1)) == 0;
        }

        /// <summary>
        /// Throws unless a point count is on the ladder.
        /// </summary>
        /// <param name="points">Candidate point count.</param>
        /// <param name="parameterName">Name to report.</param>
        /// <exception cref="ArgumentOutOfRangeException">The count is not available.</exception>
        /// <remarks>
        /// The message names the two neighbouring available counts rather than only stating the
        /// rule. "409 602 is not available" leaves the user to work out what is;
        /// <c>REQ-DSP-022</c>'s criterion asks for a clear message, and the nearest usable values
        /// are what makes it actionable.
        /// </remarks>
        public static void Validate(int points, string parameterName)
        {
            if (IsValid(points))
            {
                return;
            }

            throw new ArgumentOutOfRangeException(
                parameterName ?? nameof(points), points, Explain(points));
        }

        /// <summary>The largest available count at or below a value, or 0 if there is none.</summary>
        /// <param name="points">Candidate point count.</param>
        public static int SnapDown(int points)
        {
            if (points < Minimum)
            {
                return 0;
            }

            if (points >= Maximum)
            {
                return Maximum;
            }

            int chosen = 0;

            foreach (int candidate in SupportedCounts)
            {
                if (candidate > points)
                {
                    break;
                }

                chosen = candidate;
            }

            return chosen;
        }

        /// <summary>The available count nearest a value, in the ratio sense.</summary>
        /// <param name="points">Candidate point count.</param>
        /// <remarks>
        /// Nearest by ratio rather than by difference, because the ladder is geometric: 1200 sits
        /// between 801 and 1601, and is 1.5× the one and 0.75× the other, so 1601 is the nearer of
        /// the two in the only sense that matches how the counts are spaced.
        /// </remarks>
        public static int Nearest(int points)
        {
            if (points <= Minimum)
            {
                return Minimum;
            }

            if (points >= Maximum)
            {
                return Maximum;
            }

            int below = SnapDown(points);
            int above = below >= Maximum ? Maximum : (below - 1) * 2 + 1;

            double toBelow = (double)points / below;
            double toAbove = (double)above / points;

            return toBelow <= toAbove ? below : above;
        }

        /// <summary>
        /// A message explaining why a point count is unavailable, and what to use instead.
        /// </summary>
        /// <param name="points">The rejected count.</param>
        public static string Explain(int points)
        {
            if (points < Minimum)
            {
                return points.ToString(CultureInfo.CurrentCulture) +
                    " frequency points is below the minimum of " +
                    Minimum.ToString(CultureInfo.CurrentCulture) + " (REQ-DSP-022).";
            }

            if (points > Maximum)
            {
                return points.ToString(CultureInfo.CurrentCulture) +
                    " frequency points is above the maximum of " +
                    Maximum.ToString(CultureInfo.CurrentCulture) +
                    ". The 524 288 figure sometimes quoted is the maximum FFT size, not a point " +
                    "count (REQ-DSP-022).";
            }

            int below = SnapDown(points);
            int above = (below - 1) * 2 + 1;

            return points.ToString(CultureInfo.CurrentCulture) +
                " is not an available point count: N_f − 1 must be 50 × 2^k so that the FFT size " +
                "is a power of two. The nearest available counts are " +
                below.ToString(CultureInfo.CurrentCulture) + " and " +
                above.ToString(CultureInfo.CurrentCulture) + " (REQ-DSP-022).";
        }

        private static ReadOnlyCollection<int> BuildLadder()
        {
            var counts = new List<int>();

            for (int steps = 50; steps <= Maximum - 1; steps *= 2)
            {
                counts.Add(steps + 1);
            }

            return new ReadOnlyCollection<int>(counts);
        }
    }
}
