using System;

namespace OpenVSA.PerformanceGate
{
    /// <summary>
    /// What a benchmark actually measured for one target, with its spread.
    /// </summary>
    /// <remarks>
    /// The spread is not optional. <c>REQ-TST-007</c> requires that "measurements report variance,
    /// and a run too noisy to distinguish 15 % is reported as inconclusive rather than passed" —
    /// a mean with no spread beside it cannot answer that question, so a comparison against one
    /// would have to either assume the run was quiet or refuse to decide. Both are worse than
    /// carrying the number.
    /// </remarks>
    public sealed class TargetMeasurement
    {
        /// <summary>Records a measurement.</summary>
        /// <param name="name">The benchmark name, matching a <see cref="PerformanceTarget"/>.</param>
        /// <param name="mean">The mean of the samples, in the target's unit.</param>
        /// <param name="standardDeviation">The sample standard deviation, same unit.</param>
        /// <param name="sampleCount">How many samples the mean is over.</param>
        /// <param name="againstStated">
        /// The figure to compare against the requirement's own stated target, when that is a
        /// different population from the one the regression gate tracks. Leave unset — the default
        /// is <paramref name="mean"/>, which is right for every target whose two questions have the
        /// same answer.
        /// </param>
        /// <exception cref="ArgumentNullException"><paramref name="name"/> is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException">
        /// The mean is not finite and positive, the deviation is negative or not finite, or fewer
        /// than two samples were taken — a single sample has no spread to report.
        /// </exception>
        public TargetMeasurement(
            string name,
            double mean,
            double standardDeviation,
            int sampleCount,
            double againstStated = double.NaN)
        {
            if (name == null)
            {
                throw new ArgumentNullException(nameof(name));
            }

            if (double.IsNaN(mean) || double.IsInfinity(mean) || mean <= 0.0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(mean), mean, "A measured mean must be finite and positive.");
            }

            if (double.IsNaN(standardDeviation) || double.IsInfinity(standardDeviation) ||
                standardDeviation < 0.0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(standardDeviation), standardDeviation,
                    "A standard deviation must be finite and non-negative.");
            }

            if (sampleCount < 2)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(sampleCount), sampleCount,
                    "At least two samples are needed for a measurement to have a spread.");
            }

            if (double.IsInfinity(againstStated) ||
                (!double.IsNaN(againstStated) && againstStated <= 0.0))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(againstStated), againstStated,
                    "A figure to compare against a stated target must be finite and positive.");
            }

            Name = name;
            Mean = mean;
            StandardDeviation = standardDeviation;
            SampleCount = sampleCount;
            AgainstStated = double.IsNaN(againstStated) ? mean : againstStated;
        }

        /// <summary>The benchmark name.</summary>
        public string Name { get; }

        /// <summary>The mean, in the target's unit.</summary>
        public double Mean { get; }

        /// <summary>
        /// The figure to hold against the requirement's own stated target.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <see cref="Mean"/> for every target but one, and the distinction only exists because of
        /// that one. <c>REQ-NFR-025</c> states a <em>cold</em> start of 3 s; only the first launch of
        /// a session is cold, so the reproducible figure a 15 % regression gate needs is the warm
        /// mean over the launches after it. Reporting the warm mean and then comparing it against the
        /// cold requirement is how a missed requirement reads as met — 1.36 s against 3 s looks
        /// comfortable while the cold start it is standing in for is 3.29 s.
        /// </para>
        /// <para>
        /// So the two questions get the two figures. <see cref="Mean"/> answers "has this got worse
        /// than it was", <see cref="AgainstStated"/> answers "does it do what the requirement says",
        /// and neither is asked to stand in for the other.
        /// </para>
        /// </remarks>
        public double AgainstStated { get; }

        /// <summary>The sample standard deviation.</summary>
        public double StandardDeviation { get; }

        /// <summary>How many samples the mean is over.</summary>
        public int SampleCount { get; }

        /// <summary>
        /// The half-width of the 95 % confidence interval on the mean, as a fraction of the mean.
        /// </summary>
        /// <remarks>
        /// This is the run's resolving power: a change smaller than this cannot be told from noise.
        /// Compared against the regression threshold, it is what decides whether a verdict of
        /// "passed" is a measurement or a shrug.
        /// </remarks>
        public double RelativeResolution =>
            1.96 * StandardDeviation / Math.Sqrt(SampleCount) / Mean;

        /// <inheritdoc />
        public override string ToString() =>
            Name + " " + Mean.ToString("G6") + " ± " +
            (RelativeResolution * 100.0).ToString("F1") + "% (n=" + SampleCount + ")";
    }
}
