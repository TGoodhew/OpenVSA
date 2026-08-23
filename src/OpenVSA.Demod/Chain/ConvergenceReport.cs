using System.Globalization;

namespace OpenVSA.Demod.Chain
{
    /// <summary>
    /// What step 8's iteration did: how many times round, whether it converged, and by what
    /// criterion (<c>REQ-DEM-001</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Reaching the bound is a result, not an error.</strong> The requirement's words are
    /// that step 8 "iterates to a stated convergence criterion with a bounded iteration count, and
    /// reaching that bound is reported rather than silently accepted". A demodulator that threw on
    /// non-convergence would be useless on the signals people most want to look at, and one that
    /// returned the numbers with no flag would be worse: the answer would be an estimate that had
    /// not finished, and nothing about it would say so.
    /// </para>
    /// <para>
    /// <see cref="Criterion"/> carries the tolerance that was applied, so a report can be read
    /// years later without knowing what the settings were on the day.
    /// </para>
    /// </remarks>
    public sealed class ConvergenceReport
    {
        internal ConvergenceReport(
            int iterations, int bound, bool converged, double largestChange, double tolerance)
        {
            Iterations = iterations;
            Bound = bound;
            Converged = converged;
            LargestChange = largestChange;
            Tolerance = tolerance;
        }

        /// <summary>How many iterations were run.</summary>
        public int Iterations { get; }

        /// <summary>The bound the iterations were held to.</summary>
        public int Bound { get; }

        /// <summary>Whether the criterion was met.</summary>
        public bool Converged { get; }

        /// <summary>Whether the iteration stopped because it ran out of iterations.</summary>
        public bool ReachedBound => !Converged;

        /// <summary>
        /// The largest parameter change on the last iteration, in the units the criterion is
        /// stated in.
        /// </summary>
        public double LargestChange { get; }

        /// <summary>The tolerance the change was compared against.</summary>
        public double Tolerance { get; }

        /// <summary>The criterion, in words, with its tolerance.</summary>
        public string Criterion =>
            "every parameter changes by less than " +
            Tolerance.ToString("G3", CultureInfo.InvariantCulture) +
            " on an iteration — frequency in cycles per symbol, phase in radians, timing in " +
            "samples, gain as a fraction";

        /// <inheritdoc />
        public override string ToString() =>
            Converged
                ? "converged after " + Iterations.ToString(CultureInfo.InvariantCulture) +
                  " iteration(s), largest change " +
                  LargestChange.ToString("G3", CultureInfo.InvariantCulture)
                : "did NOT converge: stopped at the bound of " +
                  Bound.ToString(CultureInfo.InvariantCulture) +
                  " iterations with a largest change of " +
                  LargestChange.ToString("G3", CultureInfo.InvariantCulture) +
                  ", against a tolerance of " +
                  Tolerance.ToString("G3", CultureInfo.InvariantCulture);
    }
}
