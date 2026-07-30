using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text;

namespace OpenVSA.SoakGate
{
    /// <summary>Which explanation of a managed-memory rise a retention run supports.</summary>
    public enum RetentionConclusion
    {
        /// <summary>The run could not tell the two apart.</summary>
        Undecided = 0,

        /// <summary>
        /// Memory grows with each create-and-destroy cycle, at about the rate under test.
        /// </summary>
        PerCycle,

        /// <summary>
        /// It does not. Whatever the rise in the long run was, the cycles did not cause it, and the
        /// hunt belongs somewhere that scales with elapsed time instead.
        /// </summary>
        NotPerCycle,
    }

    /// <summary>What a retention run concluded.</summary>
    public sealed class RetentionReport
    {
        internal RetentionReport(
            int cycles,
            double hours,
            int samples,
            Trend trend,
            double hypothesisBytesPerCycle,
            RetentionConclusion conclusion,
            string reasoning,
            IList<SoakFinding> findings)
        {
            Cycles = cycles;
            Hours = hours;
            Samples = samples;
            Trend = trend;
            HypothesisBytesPerCycle = hypothesisBytesPerCycle;
            Conclusion = conclusion;
            Reasoning = reasoning;
            Findings = new ReadOnlyCollection<SoakFinding>(findings);
        }

        /// <summary>Cycles the run drove.</summary>
        public int Cycles { get; }

        /// <summary>How long it took, which is what makes elapsed time a rival explanation.</summary>
        public double Hours { get; }

        /// <summary>Collected samples the line was fitted through.</summary>
        public int Samples { get; }

        /// <summary>The fitted line: bytes of managed floor per cycle.</summary>
        public Trend Trend { get; }

        /// <summary>The per-cycle rate under test, in bytes.</summary>
        public double HypothesisBytesPerCycle { get; }

        /// <summary>Which explanation the run supports.</summary>
        public RetentionConclusion Conclusion { get; }

        /// <summary>The arithmetic behind <see cref="Conclusion"/>, in words.</summary>
        public string Reasoning { get; }

        /// <summary>One finding per claim about the run itself.</summary>
        public IReadOnlyList<SoakFinding> Findings { get; }

        /// <summary>
        /// Whether the run answered the question it was set, either way.
        /// </summary>
        /// <remarks>
        /// A refutation is a result, not a failure. This run exists to choose between two
        /// explanations, so it succeeds when it chooses — and only <see
        /// cref="RetentionConclusion.Undecided"/> means it did not.
        /// </remarks>
        public bool Decided => Conclusion != RetentionConclusion.Undecided;

        /// <summary>The report as text.</summary>
        public string Render()
        {
            var text = new StringBuilder();

            text.Append("OpenVSA retention (REQ-TST-009, diagnosing #356): ")
                .Append(Cycles.ToString("N0", CultureInfo.InvariantCulture))
                .Append(" cycles over ")
                .Append(Hours.ToString("0.00", CultureInfo.InvariantCulture))
                .Append(" hours, ")
                .Append(Samples.ToString(CultureInfo.InvariantCulture))
                .Append(" collected samples")
                .Append('\n')
                .Append('\n');

            foreach (SoakFinding finding in Findings)
            {
                text.Append("  ").Append(finding).Append('\n');
            }

            text.Append('\n')
                .Append("  CONCLUSION  ")
                .Append(Conclusion.ToString().ToUpperInvariant())
                .Append(" — ")
                .Append(Reasoning)
                .Append('\n');

            return text.ToString();
        }
    }

    /// <summary>
    /// Decides whether a managed-memory rise is caused by create-and-destroy cycles.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Why this exists beside <see cref="EnduranceGate"/>.</strong> The eight-hour soak of
    /// <c>REQ-TST-009</c> found a managed floor rising 0.0106 MiB/hour, 5.5 times its own
    /// uncertainty. Over that run's 96 cycles the same rise is 0.91 KiB per cycle — the two fit the
    /// data equally well, because the run drove its cycles at a constant rate and so cycle count and
    /// elapsed time are the same straight line. Nothing in an eight-hour log can separate them.
    /// </para>
    /// <para>
    /// <strong>What separates them is changing the cycle rate.</strong> Drive cycles back to back and
    /// the run reaches in minutes a cycle count the soak needed a night for, while elapsed time — and
    /// anything that scales with it — stays negligible. A rate of 0.91 KiB per cycle then shows up as
    /// megabytes; a rate per hour shows up as nothing. That is the whole design: one experiment whose
    /// two rival explanations predict answers three orders of magnitude apart.
    /// </para>
    /// <para>
    /// <strong>A refutation needs an upper bound, not a null result.</strong> "No growth was seen" is
    /// what a run that measured nothing also says. So the finding is stated as the two-sigma upper
    /// bound on bytes per cycle, and the conclusion compares the hypothesis with <em>that</em> — a run
    /// too noisy to place the hypothesis is reported <see cref="RetentionConclusion.Undecided"/>
    /// rather than allowed to refute it by being bad.
    /// </para>
    /// </remarks>
    public sealed class RetentionGate
    {
        /// <summary>
        /// The per-cycle rate the eight-hour soak's rise would correspond to, in bytes.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The <em>fitted</em> rise — 0.0106 MiB/hour over 8 hours, spread across that run's 96
        /// cycles — from <c>evidence/req-tst-009/</c>. It is the hypothesis this gate exists to test,
        /// so it is stated once, with its provenance, rather than typed into a command line each
        /// time.
        /// </para>
        /// <para>
        /// <strong>Not the first-to-last difference</strong>, which is the other figure that run
        /// reports (16.93 to 17.04 MiB, or 1,136 bytes a cycle). Two readings out of forty-eight
        /// carry the scatter of two readings; the fitted slope is the one the ±0.0019 MiB/hour
        /// uncertainty belongs to, and an interval is the whole point here. Using the difference
        /// would overstate the hypothesis by 23 % and make it correspondingly easier to refute.
        /// </para>
        /// </remarks>
        public const double EightHourBytesPerCycle = 0.0106 * 8.0 * 1024.0 * 1024.0 / 96.0;

        /// <summary>
        /// Cycles a run must drive before its answer means anything.
        /// </summary>
        /// <remarks>
        /// The soak itself managed 96 in eight hours, and could not resolve the question. A run that
        /// drove fewer than this has done less than the run it is trying to improve on, and is
        /// reported inconclusive rather than believed. Five hundred also keeps <see
        /// cref="WarmUpCycles"/> under a tenth of anything this gate will judge.
        /// </remarks>
        public const int MinimumCycles = 500;

        /// <summary>
        /// Opening cycles excluded from the fit.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <strong>Because the first cycles are not a leak, and they dominate the line.</strong> The
        /// first rehearsal of this mode measured a floor of 16.29 MiB at cycle zero and 17.14 MiB by
        /// cycle five, flat thereafter — the window-creation path being jitted and its caches first
        /// touched. Fitted from cycle zero that step reads as 33.6 KiB per cycle, thirty-six times
        /// the rate under test, and it would have <em>confirmed</em> the hypothesis out of warm-up
        /// alone. The same discard, and the same reasoning, as <c>EnduranceGate.WarmUpMinutes</c>.
        /// </para>
        /// <para>
        /// Fifty, when the step finished by five: generous, because it is a discarded window rather
        /// than a threshold, and it only has to be small enough that a leak cannot hide inside it.
        /// At <see cref="MinimumCycles"/> it is a tenth of the run and a real per-cycle rate still
        /// shows across the other nine, which is asserted rather than assumed.
        /// </para>
        /// </remarks>
        public const int WarmUpCycles = 50;

        /// <summary>
        /// Collected samples a run must take, spread across its cycles.
        /// </summary>
        /// <remarks>
        /// Three is enough for a line to have a residual; ten is enough for the residual to describe
        /// the scatter rather than three accidents.
        /// </remarks>
        public const int MinimumSamples = 10;

        /// <summary>How many standard errors a bound or a separation is stated at.</summary>
        public const double Sigma = 2.0;

        /// <summary>Creates a gate.</summary>
        /// <param name="hypothesisBytesPerCycle">
        /// The per-cycle rate under test. Defaults to <see cref="EightHourBytesPerCycle"/>.
        /// </param>
        /// <exception cref="ArgumentOutOfRangeException">The rate is not positive.</exception>
        public RetentionGate(double hypothesisBytesPerCycle = EightHourBytesPerCycle)
        {
            if (double.IsNaN(hypothesisBytesPerCycle) || hypothesisBytesPerCycle <= 0.0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(hypothesisBytesPerCycle),
                    hypothesisBytesPerCycle,
                    "There is no hypothesis to test unless it predicts some growth.");
            }

            HypothesisBytesPerCycle = hypothesisBytesPerCycle;
        }

        /// <summary>The per-cycle rate this gate is testing.</summary>
        public double HypothesisBytesPerCycle { get; }

        /// <summary>Judges a retention run.</summary>
        /// <param name="samples">The samples, in the order they were taken.</param>
        /// <exception cref="ArgumentNullException"><paramref name="samples"/> is null.</exception>
        public RetentionReport Judge(IEnumerable<SoakSample> samples)
        {
            if (samples == null)
            {
                throw new ArgumentNullException(nameof(samples));
            }

            List<SoakSample> taken = samples.OrderBy(s => s.ElapsedSeconds).ToList();

            // Only the samples that forced a collection, for the reason ManagedMemoryIsBounded gives:
            // an uncollected heap rises and falls by design, and a line through it fits the sawtooth.
            // And only after the warm-up cycles, for the reason WarmUpCycles gives.
            List<SoakSample> floor = taken
                .Where(s => s.Collected && s.Cycles >= WarmUpCycles)
                .ToList();

            int cycles = taken.Count == 0 ? 0 : taken[taken.Count - 1].Cycles;
            double hours = taken.Count == 0 ? 0.0 : taken[taken.Count - 1].ElapsedHours;

            Trend trend = Trend.Fit(
                floor.Select(s => (double)s.Cycles).ToList(),
                floor.Select(s => (double)s.CollectedManagedBytes).ToList());

            var findings = new List<SoakFinding>
            {
                DroveEnoughCycles(cycles, floor.Count),
                CyclesSpanned(floor),
                DoesNotGrowPerCycle(trend),
            };

            RetentionConclusion conclusion = Conclude(findings, trend, out string reasoning);

            return new RetentionReport(
                cycles,
                hours,
                floor.Count,
                trend,
                HypothesisBytesPerCycle,
                conclusion,
                reasoning,
                findings);
        }

        private static SoakFinding DroveEnoughCycles(int cycles, int floorSamples)
        {
            const string Claim = "The run drove enough cycles to resolve a per-cycle rate";

            if (cycles < MinimumCycles)
            {
                return new SoakFinding(
                    Claim, "REQ-TST-009", SoakVerdict.Inconclusive,
                    cycles + " cycles, fewer than the " + MinimumCycles + " this needs");
            }

            if (floorSamples < MinimumSamples)
            {
                return new SoakFinding(
                    Claim, "REQ-TST-009", SoakVerdict.Inconclusive,
                    cycles + " cycles but only " + floorSamples +
                    " collected samples after the first " + WarmUpCycles + ", fewer than the " +
                    MinimumSamples + " a scatter needs");
            }

            return new SoakFinding(
                Claim, "REQ-TST-009", SoakVerdict.Passed,
                cycles + " cycles over " + floorSamples +
                " collected samples after the first " + WarmUpCycles);
        }

        /// <summary>
        /// That the samples are spread across the cycles rather than bunched at one end.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A line's slope is only as good as its spread in x. Twenty samples all taken in the last
        /// two hundred cycles of two thousand would fit a slope with a small standard error and
        /// describe nothing about the other 1,800 — a bound computed from that would refute the
        /// hypothesis by never having looked at most of where it applies. That is what a run whose
        /// early samples faulted leaves behind.
        /// </para>
        /// <para>
        /// <strong>What it cannot see, said plainly:</strong> samples that stop early. The run's
        /// cycle count is read from its last sample, so a log that stopped sampling at cycle 200 of
        /// 2,000 is indistinguishable here from a 200-cycle run — and is judged as one, which is at
        /// least not wrong. Only the host knows how many cycles it was asked for, and it records
        /// that in the log's preamble for a reader rather than for this rule.
        /// </para>
        /// </remarks>
        private static SoakFinding CyclesSpanned(List<SoakSample> floor)
        {
            const string Claim = "The samples span the cycles they are fitted over";

            if (floor.Count < 2)
            {
                return new SoakFinding(
                    Claim, "REQ-TST-009", SoakVerdict.Inconclusive,
                    "fewer than two collected samples, so they span nothing");
            }

            int first = floor.Min(s => s.Cycles);
            int last = floor.Max(s => s.Cycles);
            int span = last - first;
            int total = last;

            string figures =
                "sampled from cycle " + first + " to cycle " + last + ", spanning " + span +
                " of " + total;

            // Half the run, which is what "spread across it" has to mean for a bound to describe the
            // whole. Below that the fit speaks for a window rather than for the run.
            return total > 0 && span * 2 >= total
                ? new SoakFinding(Claim, "REQ-TST-009", SoakVerdict.Passed, figures)
                : new SoakFinding(Claim, "REQ-TST-009", SoakVerdict.Inconclusive, figures);
        }

        private SoakFinding DoesNotGrowPerCycle(Trend trend)
        {
            const string Claim = "Managed memory does not grow with each create-and-destroy cycle";

            if (!trend.IsDetermined)
            {
                return new SoakFinding(
                    Claim, "REQ-TST-009", SoakVerdict.Inconclusive,
                    "only " + trend.Count + " collected samples, which cannot determine a slope");
            }

            double bound = trend.Slope + (Sigma * trend.StandardError);

            string figures =
                "measured " + Bytes(trend.Slope) + "/cycle ±" + Bytes(trend.StandardError) +
                " over " + trend.Count + " samples after the first " + WarmUpCycles +
                " cycles, from " + Megabytes(trend.First) + " to " +
                Megabytes(trend.Last) + "; " + Sigma.ToString("0", CultureInfo.InvariantCulture) +
                "σ upper bound " + Bytes(bound) + "/cycle, against the hypothesis of " +
                Bytes(HypothesisBytesPerCycle) + "/cycle";

            return trend.RisesSignificantly(Sigma)
                ? new SoakFinding(Claim, "REQ-TST-009", SoakVerdict.Failed, figures)
                : new SoakFinding(Claim, "REQ-TST-009", SoakVerdict.Passed, figures);
        }

        private RetentionConclusion Conclude(
            List<SoakFinding> findings, Trend trend, out string reasoning)
        {
            SoakFinding blocking = findings.FirstOrDefault(
                f => f.Verdict == SoakVerdict.Inconclusive);

            if (blocking != null)
            {
                reasoning = "the run cannot answer: " + blocking.Detail;
                return RetentionConclusion.Undecided;
            }

            double bound = trend.Slope + (Sigma * trend.StandardError);
            double separation = trend.StandardError > 0.0
                ? (HypothesisBytesPerCycle - trend.Slope) / trend.StandardError
                : double.PositiveInfinity;

            if (bound < HypothesisBytesPerCycle)
            {
                reasoning =
                    "the per-cycle explanation is refuted — " + Bytes(HypothesisBytesPerCycle) +
                    "/cycle is " + separation.ToString("0.0", CultureInfo.InvariantCulture) +
                    "σ above what was measured, and the " +
                    Sigma.ToString("0", CultureInfo.InvariantCulture) + "σ upper bound of " +
                    Bytes(bound) + "/cycle excludes it. Whatever rose over eight hours, the cycles " +
                    "did not cause it";

                return RetentionConclusion.NotPerCycle;
            }

            if (trend.RisesSignificantly(Sigma))
            {
                reasoning =
                    "memory grows per cycle — " + Bytes(trend.Slope) + "/cycle ±" +
                    Bytes(trend.StandardError) + ", which is where the eight-hour rise comes from";

                return RetentionConclusion.PerCycle;
            }

            // No significant rise, but the bound is too loose to exclude the hypothesis either. That
            // is the honest middle, and it is reported rather than rounded to whichever side is
            // convenient.
            reasoning =
                "no rise was measured (" + Bytes(trend.Slope) + "/cycle ±" +
                Bytes(trend.StandardError) + ") but the " +
                Sigma.ToString("0", CultureInfo.InvariantCulture) + "σ upper bound of " +
                Bytes(bound) + "/cycle does not exclude " + Bytes(HypothesisBytesPerCycle) +
                "/cycle either. More cycles are needed, not a different rule";

            return RetentionConclusion.Undecided;
        }

        private static string Bytes(double bytes) =>
            bytes.ToString("0.0", CultureInfo.InvariantCulture) + " bytes";

        private static string Megabytes(double bytes) =>
            (bytes / (1024.0 * 1024.0)).ToString("0.000", CultureInfo.InvariantCulture) + " MiB";
    }
}
