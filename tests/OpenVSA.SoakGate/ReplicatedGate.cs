using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text;

namespace OpenVSA.SoakGate
{
    /// <summary>What several soak runs, taken together, say about the managed floor.</summary>
    public sealed class ReplicatedReport
    {
        internal ReplicatedReport(
            int runs, IReadOnlyList<double> slopes, double mean, double standardError,
            double medianWithinRunError, IList<SoakFinding> findings)
        {
            Runs = runs;
            Slopes = slopes;
            MeanSlope = mean;
            StandardError = standardError;
            MedianWithinRunError = medianWithinRunError;
            Findings = new ReadOnlyCollection<SoakFinding>(findings);
        }

        /// <summary>How many runs were judged.</summary>
        public int Runs { get; }

        /// <summary>Each run's own fitted slope, in bytes per hour.</summary>
        public IReadOnlyList<double> Slopes { get; }

        /// <summary>The mean of them.</summary>
        public double MeanSlope { get; }

        /// <summary>
        /// The standard error of that mean, computed from how much the runs disagree.
        /// </summary>
        /// <remarks>
        /// <strong>From the spread between runs, never from the residuals inside one.</strong> That
        /// distinction is the whole reason this type exists; see the class remarks.
        /// </remarks>
        public double StandardError { get; }

        /// <summary>The typical within-run standard error, for comparison.</summary>
        public double MedianWithinRunError { get; }

        /// <summary>
        /// How far the within-run error understates the real one.
        /// </summary>
        /// <remarks>
        /// Reported because it is the number that decides whether a single run may be quoted at all.
        /// A ratio near one means a run reproduces itself; the runs that prompted this gate came out
        /// near twenty.
        /// </remarks>
        public double Understatement =>
            MedianWithinRunError <= 0.0 ? double.NaN : StandardError / MedianWithinRunError;

        /// <summary>One finding per claim.</summary>
        public IReadOnlyList<SoakFinding> Findings { get; }

        /// <summary>Whether every claim held.</summary>
        public bool Passed => !Findings.Any(f => f.Fails);

        /// <summary>The report as text.</summary>
        public string Render()
        {
            var text = new StringBuilder();

            text.Append("OpenVSA soak, ")
                .Append(Runs.ToString(CultureInfo.InvariantCulture))
                .Append(" replicate runs (REQ-TST-009)")
                .Append('\n')
                .Append('\n');

            for (int i = 0; i < Slopes.Count; i++)
            {
                text.Append("  run ")
                    .Append((i + 1).ToString(CultureInfo.InvariantCulture))
                    .Append("  ")
                    .Append(Kib(Slopes[i]))
                    .Append("/hour")
                    .Append('\n');
            }

            text.Append('\n');

            foreach (SoakFinding finding in Findings)
            {
                text.Append("  ").Append(finding).Append('\n');
            }

            return text.ToString();
        }

        private static string Kib(double bytes) =>
            (bytes / 1024.0).ToString("0.00", CultureInfo.InvariantCulture) + " KiB";
    }

    /// <summary>
    /// Judges the managed-memory claim of <c>REQ-TST-009</c> across repeated runs.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Why one run is not enough, measured rather than argued.</strong> The collected
    /// managed floor is not a trend: it is a staircase that moves in both directions, holding a
    /// value for ten minutes at a time and then stepping by four to eight kibibytes. Two runs of one
    /// identical configuration fitted <c>0.06 ±0.70</c> and <c>54.96 ±2.06</c> KiB/hour — the same
    /// shell, the same machine, the same duration.
    /// </para>
    /// <para>
    /// <strong>And a staircase fits a straight line tightly.</strong> Its residuals about the line
    /// are small, so <see cref="Trend.StandardError"/> comes out small and describes only how well
    /// that one run's points sit on their own line. It says nothing about whether the next run will
    /// agree, and taking it for the measurement uncertainty is what produced a published conclusion
    /// that its own replicate contradicted.
    /// </para>
    /// <para>
    /// So the uncertainty here comes from the <em>spread between runs</em>, which is the only thing
    /// that can answer "would this happen again". <see cref="EnduranceGate"/> keeps its within-run
    /// rule, because for a single run that is the honest thing to report; what it must not do is
    /// let one run settle the claim.
    /// </para>
    /// </remarks>
    public sealed class ReplicatedGate
    {
        /// <summary>
        /// Runs needed before a spread means anything.
        /// </summary>
        /// <remarks>
        /// Three, because two give a difference and no way to tell whether it is typical. The same
        /// reason <see cref="Trend.IsDetermined"/> insists on three points: with two, the spread is
        /// exactly the gap and carries no degrees of freedom to judge it by.
        /// </remarks>
        public const int MinimumRuns = 3;

        /// <summary>How many standard errors a rise must exceed to count.</summary>
        public const double Sigma = 2.0;

        /// <summary>Judges a set of runs.</summary>
        /// <param name="runs">Each run's samples, in the order they were taken.</param>
        /// <exception cref="ArgumentNullException"><paramref name="runs"/> is null.</exception>
        public ReplicatedReport Judge(IReadOnlyList<IReadOnlyList<SoakSample>> runs)
        {
            if (runs == null)
            {
                throw new ArgumentNullException(nameof(runs));
            }

            var slopes = new List<double>();
            var withinRun = new List<double>();

            foreach (IReadOnlyList<SoakSample> run in runs)
            {
                Trend trend = FloorTrend(run);

                if (trend.IsDetermined)
                {
                    slopes.Add(trend.Slope);
                    withinRun.Add(trend.StandardError);
                }
            }

            double mean = slopes.Count == 0 ? double.NaN : slopes.Average();
            double error = StandardErrorOfMean(slopes);
            double median = Median(withinRun);

            var findings = new List<SoakFinding>
            {
                EnoughRuns(runs.Count, slopes.Count),
                BoundedAcrossRuns(slopes, mean, error),
                RunsAgreeWithEachOther(slopes, error, median),
            };

            return new ReplicatedReport(
                runs.Count,
                new ReadOnlyCollection<double>(slopes),
                mean,
                error,
                median,
                findings);
        }

        /// <summary>One run's floor against elapsed hours, on the same terms the single-run gate uses.</summary>
        private static Trend FloorTrend(IReadOnlyList<SoakSample> run)
        {
            if (run == null)
            {
                return Trend.Fit(new double[0], new double[0]);
            }

            bool cycled = run.Any(s => s.Cycles >= 1);

            List<SoakSample> floor = run
                .Where(s => s.Collected &&
                            s.ElapsedSeconds >= EnduranceGate.WarmUpMinutes * 60.0 &&
                            (!cycled || s.Cycles >= 1))
                .ToList();

            return Trend.Fit(
                floor.Select(s => s.ElapsedHours).ToList(),
                floor.Select(s => (double)s.CollectedManagedBytes).ToList());
        }

        private static SoakFinding EnoughRuns(int offered, int usable)
        {
            const string Claim = "Enough runs to say whether a rise would happen again";

            if (usable < MinimumRuns)
            {
                return new SoakFinding(
                    Claim, "REQ-TST-009", SoakVerdict.Inconclusive,
                    usable + " of " + offered + " runs could be fitted, fewer than the " +
                    MinimumRuns + " a spread needs — one run's own error bar cannot answer this");
            }

            return new SoakFinding(
                Claim, "REQ-TST-009", SoakVerdict.Passed,
                usable + " runs fitted of " + offered + " offered");
        }

        private static SoakFinding BoundedAcrossRuns(
            List<double> slopes, double mean, double error)
        {
            const string Claim = "Managed memory is bounded across repeated runs";

            if (slopes.Count < MinimumRuns)
            {
                return new SoakFinding(
                    Claim, "REQ-TST-009", SoakVerdict.Inconclusive,
                    "only " + slopes.Count + " runs");
            }

            string figures =
                "mean " + Kib(mean) + "/hour ±" + Kib(error) + " from the spread between runs (" +
                string.Join(", ", slopes.Select(s => Kib(s))) + ")";

            return mean > Sigma * error
                ? new SoakFinding(Claim, "REQ-TST-009", SoakVerdict.Failed, figures)
                : new SoakFinding(Claim, "REQ-TST-009", SoakVerdict.Passed, figures);
        }

        /// <summary>
        /// Reports how far a single run's own error bar understates the real uncertainty.
        /// </summary>
        /// <remarks>
        /// Reported rather than judged: a large ratio is a fact about the quantity being measured,
        /// not a fault in the run. What it decides is whether anybody may quote one run's slope, and
        /// that is a judgement for whoever reads the report — so it is put in front of them instead
        /// of being folded into a pass or a fail.
        /// </remarks>
        private static SoakFinding RunsAgreeWithEachOther(
            List<double> slopes, double between, double medianWithin)
        {
            const string Claim = "How far one run's own error bar understates the real one";

            if (slopes.Count < MinimumRuns || medianWithin <= 0.0)
            {
                return new SoakFinding(
                    Claim, "REQ-TST-009", SoakVerdict.Inconclusive, "too few runs to compare");
            }

            double ratio = between / medianWithin;

            return new SoakFinding(
                Claim, "REQ-TST-009", SoakVerdict.Passed,
                "between runs ±" + Kib(between) + "/hour against a typical within-run ±" +
                Kib(medianWithin) + " — a factor of " +
                ratio.ToString("0.0", CultureInfo.InvariantCulture) +
                (ratio >= 2.0
                    ? ", so no single run's slope may be quoted"
                    : ", so a single run is representative"));
        }

        private static double StandardErrorOfMean(List<double> values)
        {
            if (values.Count < 2)
            {
                return double.PositiveInfinity;
            }

            double mean = values.Average();
            double sum = values.Sum(v => (v - mean) * (v - mean));

            // Sample standard deviation, then the error of the mean. n - 1 because the mean was
            // estimated from these same values.
            double deviation = Math.Sqrt(sum / (values.Count - 1));

            return deviation / Math.Sqrt(values.Count);
        }

        private static double Median(List<double> values)
        {
            if (values.Count == 0)
            {
                return 0.0;
            }

            List<double> sorted = values.OrderBy(v => v).ToList();

            return sorted.Count % 2 == 1
                ? sorted[sorted.Count / 2]
                : 0.5 * (sorted[(sorted.Count / 2) - 1] + sorted[sorted.Count / 2]);
        }

        private static string Kib(double bytes) =>
            (bytes / 1024.0).ToString("0.00", CultureInfo.InvariantCulture) + " KiB";
    }
}
