using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using Xunit.Abstractions;

namespace OpenVSA.SoakGate.Tests
{
    /// <summary>
    /// The retention gate's rules, each driven from both sides.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>What this gate is for.</strong> The eight-hour soak found a managed floor rising
    /// 0.0106 MiB/hour and could not say whether the cause scales with elapsed time or with the
    /// create-and-destroy cycles the run drove at a constant rate. <see cref="RetentionGate"/> judges
    /// a run that breaks the tie by driving cycles far faster, so that only one of the two
    /// explanations predicts a visible rise.
    /// </para>
    /// <para>
    /// <strong>The load-bearing test is <see
    /// cref="ARunTooNoisyToPlaceTheHypothesisIsUndecidedRatherThanRefuting"/>.</strong> A refutation
    /// here is an upper bound, and a bad enough run has a very loose one — so without that rule, the
    /// way to "prove" there is no leak would be to measure badly. Every other test in this file would
    /// pass on a gate that had that hole.
    /// </para>
    /// </remarks>
    public class RetentionGateTests
    {
        private const double Mib = 1024.0 * 1024.0;

        /// <summary>The floor a healthy shell settles at, from the eight-hour run's own log.</summary>
        private const double FloorBytes = 17.0 * Mib;

        private readonly ITestOutputHelper _output;

        /// <summary>Takes the output helper, so a conclusion's figures are visible.</summary>
        /// <param name="output">Where the report is written.</param>
        public RetentionGateTests(ITestOutputHelper output)
        {
            _output = output;
        }

        // ---- The two explanations, each recognised ------------------------------------------------

        [Fact]
        public void ARunWhoseCyclesRetainMemoryNamesTheRateItRetainsAt()
        {
            RetentionReport report = Judge(Run(
                cycles: 2000,
                hours: 0.20,
                bytesPerCycle: RetentionGate.EightHourBytesPerCycle,
                bytesPerHour: 0.0,
                scatterBytes: 20 * 1024.0));

            _output.WriteLine(report.Render());

            Assert.Equal(RetentionConclusion.PerCycle, report.Conclusion);

            // The rate it names has to be the rate that was planted, or the gate has detected
            // something without measuring it.
            Assert.InRange(report.Trend.Slope, 900.0, 965.0);
        }

        [Fact]
        public void ARunWhoseGrowthIsPerHourRefutesThePerCycleExplanation()
        {
            // This is the real scenario. The eight-hour log fits 0.0106 MiB/hour equally well as
            // 0.91 KiB/cycle, because that run drove 12 cycles an hour throughout. At 10,000 cycles
            // an hour the per-hour rise contributes about two kilobytes across the whole run, and
            // the per-cycle explanation would have predicted 1.78 MiB.
            RetentionReport report = Judge(Run(
                cycles: 2000,
                hours: 0.20,
                bytesPerCycle: 0.0,
                bytesPerHour: 0.0106 * Mib,
                scatterBytes: 20 * 1024.0));

            _output.WriteLine(report.Render());

            Assert.Equal(RetentionConclusion.NotPerCycle, report.Conclusion);

            // Refuted by an upper bound, not by a null result: the bound has to exclude the
            // hypothesis, and be stated.
            double bound = report.Trend.Slope + (RetentionGate.Sigma * report.Trend.StandardError);

            Assert.True(
                bound < RetentionGate.EightHourBytesPerCycle,
                "The bound of " + bound + " bytes/cycle does not exclude the hypothesis.");

            Assert.Contains("upper bound", report.Reasoning, StringComparison.Ordinal);
        }

        // ---- Warm-up is not a leak, and discarding it does not hide one ----------------------------

        [Fact]
        public void TheOpeningStepAsTheWindowPathIsJittedIsNotReadAsRetention()
        {
            // Measured, not invented: the first rehearsal of the retention mode read 16.29 MiB at
            // cycle zero and 17.14 MiB by cycle five, then flat. Fitted from cycle zero that step
            // alone is 33.6 KiB per cycle — thirty-six times the rate under test — so without the
            // warm-up discard this run would CONFIRM the hypothesis out of start-up costs.
            List<SoakSample> stepped = Run(
                cycles: 2000,
                hours: 0.20,
                bytesPerCycle: 0.0,
                bytesPerHour: 0.0,
                scatterBytes: 20 * 1024.0,
                openingStepBytes: 0.85 * Mib,
                stepCompleteByCycle: 5);

            RetentionReport report = Judge(stepped);

            _output.WriteLine(report.Render());

            Assert.Equal(RetentionConclusion.NotPerCycle, report.Conclusion);
        }

        [Fact]
        public void DiscardingTheWarmUpDoesNotDiscardARealLeakWithOneInFrontOfIt()
        {
            // The other direction, and the one that keeps the discard honest: the same opening step,
            // with a genuine per-cycle rate underneath it. Excluding the opening cycles must not
            // excuse the run — a warm-up window wide enough to swallow the leak would make every
            // conclusion of this gate meaningless.
            RetentionReport report = Judge(Run(
                cycles: 2000,
                hours: 0.20,
                bytesPerCycle: RetentionGate.EightHourBytesPerCycle,
                bytesPerHour: 0.0,
                scatterBytes: 20 * 1024.0,
                openingStepBytes: 0.85 * Mib,
                stepCompleteByCycle: 5));

            _output.WriteLine(report.Render());

            Assert.Equal(RetentionConclusion.PerCycle, report.Conclusion);
            Assert.InRange(report.Trend.Slope, 900.0, 965.0);
        }

        [Fact]
        public void TheWarmUpWindowIsASmallPartOfAnyRunThisGateWillJudge()
        {
            // The discard is only defensible while it stays small. If either constant moves so that
            // a tenth of the shortest judgeable run is no longer the ceiling, this fails and the
            // reasoning in WarmUpCycles has to be rewritten rather than quietly outgrown.
            Assert.True(
                RetentionGate.WarmUpCycles * 10 <= RetentionGate.MinimumCycles,
                "The warm-up discard is " + RetentionGate.WarmUpCycles + " of a minimum " +
                RetentionGate.MinimumCycles + " cycles, which is more than a tenth of the run.");
        }

        // ---- A run that cannot answer says so ------------------------------------------------------

        [Fact]
        public void ARunTooNoisyToPlaceTheHypothesisIsUndecidedRatherThanRefuting()
        {
            // No rise, and no ability to see one: three megabytes of scatter, a thousand times the
            // rate under test. The slope is not significantly positive, so a gate that concluded
            // "not per cycle" from that alone would let a bad measurement refute a real leak — and
            // measuring badly would become the cheapest way to close #356. It must not.
            RetentionReport report = Judge(Run(
                cycles: 800,
                hours: 0.08,
                bytesPerCycle: 0.0,
                bytesPerHour: 0.0,
                scatterBytes: 3.0 * Mib,
                samples: 16));

            _output.WriteLine(report.Render());

            Assert.False(report.Trend.RisesSignificantly(RetentionGate.Sigma));
            Assert.Equal(RetentionConclusion.Undecided, report.Conclusion);
            Assert.Contains("does not exclude", report.Reasoning, StringComparison.Ordinal);
        }

        [Fact]
        public void ARunWithFewerCyclesThanTheSoakItselfCannotImproveOnIt()
        {
            RetentionReport report = Judge(Run(
                cycles: 96,
                hours: 0.01,
                bytesPerCycle: 0.0,
                bytesPerHour: 0.0,
                scatterBytes: 20 * 1024.0));

            _output.WriteLine(report.Render());

            Assert.Equal(RetentionConclusion.Undecided, report.Conclusion);
            Assert.Equal(SoakVerdict.Inconclusive, Finding(report, "enough cycles").Verdict);
        }

        [Fact]
        public void SamplesTakenOnlyNearTheEndCannotSpeakForTheWholeRun()
        {
            // Twenty samples, all inside the last 200 cycles of 2,000 — what a run whose early
            // samples faulted leaves behind. The line through them has a small standard error and
            // describes the first 1,800 cycles not at all, so a bound computed from it would refute
            // the hypothesis without ever having looked at most of where it applies.
            RetentionReport report = Judge(Run(
                cycles: 2000,
                hours: 0.20,
                bytesPerCycle: 0.0,
                bytesPerHour: 0.0,
                scatterBytes: 20 * 1024.0,
                samples: 20,
                firstSampledCycle: 1800));

            _output.WriteLine(report.Render());

            Assert.Equal(SoakVerdict.Inconclusive, Finding(report, "span the cycles").Verdict);
            Assert.Equal(RetentionConclusion.Undecided, report.Conclusion);
        }

        [Fact]
        public void SamplesSpanningTheWholeRunSatisfyTheSpreadRule()
        {
            // The other side of it: without this, a rule that always answered Inconclusive would
            // pass the test above and make every real run undecidable.
            RetentionReport report = Judge(Run(
                cycles: 2000,
                hours: 0.20,
                bytesPerCycle: 0.0,
                bytesPerHour: 0.0,
                scatterBytes: 20 * 1024.0));

            Assert.Equal(SoakVerdict.Passed, Finding(report, "span the cycles").Verdict);
        }

        [Fact]
        public void ARunWhoseSamplesNeverForcedACollectionHasNoFloorToFit()
        {
            List<SoakSample> uncollected = Run(
                cycles: 2000,
                hours: 0.20,
                bytesPerCycle: 0.0,
                bytesPerHour: 0.0,
                scatterBytes: 20 * 1024.0)
                .Select(s => new SoakSample(
                    s.ElapsedSeconds, s.ManagedBytes, 0L, s.PrivateBytes, s.Handles, s.GdiObjects,
                    s.UserObjects, s.FramesDrawn, s.FramesDropped, s.PooledBuffers, s.PooledBytes,
                    s.TracesOpen, s.Cycles))
                .ToList();

            RetentionReport report = Judge(uncollected);

            _output.WriteLine(report.Render());

            Assert.Equal(RetentionConclusion.Undecided, report.Conclusion);
            Assert.Equal(0, report.Samples);
        }

        // ---- The gate's own edges ------------------------------------------------------------------

        [Fact]
        public void AHypothesisThatPredictsNoGrowthIsNotAHypothesis()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new RetentionGate(0.0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new RetentionGate(-1.0));
        }

        [Fact]
        public void JudgingNothingIsRefused()
        {
            Assert.Throws<ArgumentNullException>(() => new RetentionGate().Judge(null));
        }

        [Fact]
        public void AnEmptyLogIsUndecidedRatherThanRefuting()
        {
            RetentionReport report = Judge(new List<SoakSample>());

            Assert.Equal(RetentionConclusion.Undecided, report.Conclusion);
            Assert.False(report.Decided);
        }

        [Fact]
        public void TheRateUnderTestIsTheFittedRiseAndNotTheFirstToLastDifference()
        {
            // The eight-hour run reports two figures, and they are 23 % apart. The fitted slope,
            // 0.0106 MiB/hour, is the one with an uncertainty attached and so the one a bound can be
            // compared against; the first-to-last difference of 0.104 MiB carries the scatter of two
            // readings out of forty-eight. Taking the larger would make the hypothesis easier to
            // refute by inflating it — which is the failure this test exists to prevent, and it
            // caught the constant being written that way.
            double fitted = 0.0106 * Mib * 8.0 / 96.0;
            double firstToLast = 0.104 * Mib / 96.0;

            Assert.InRange(RetentionGate.EightHourBytesPerCycle, fitted * 0.99, fitted * 1.01);
            Assert.True(
                firstToLast > RetentionGate.EightHourBytesPerCycle * 1.15,
                "The two figures are supposed to differ; if they no longer do, this test is vacuous.");
        }

        // ---- Fixtures ------------------------------------------------------------------------------

        private static RetentionReport Judge(IEnumerable<SoakSample> samples) =>
            new RetentionGate().Judge(samples);

        private static SoakFinding Finding(RetentionReport report, string claimContains) =>
            report.Findings.Single(f => f.Claim.IndexOf(claimContains, StringComparison.Ordinal) >= 0);

        /// <summary>
        /// A retention run whose managed floor grows at a stated rate per cycle, a stated rate per
        /// hour, or neither, with deterministic scatter on top.
        /// </summary>
        /// <param name="cycles">Cycles the run drove.</param>
        /// <param name="hours">How long it took.</param>
        /// <param name="bytesPerCycle">Growth attributable to each cycle.</param>
        /// <param name="bytesPerHour">Growth attributable to elapsed time.</param>
        /// <param name="scatterBytes">Peak scatter about the line.</param>
        /// <param name="samples">How many samples to take.</param>
        /// <param name="firstSampledCycle">
        /// The cycle the first sample is taken at, for building a log whose samples are bunched into
        /// the tail of the run. Defaults to zero.
        /// </param>
        /// <param name="openingStepBytes">A one-off rise over the opening cycles: warm-up.</param>
        /// <param name="stepCompleteByCycle">The cycle by which that rise has finished.</param>
        /// <remarks>
        /// The scatter is a fixed irrational-stride sequence rather than a random one: a fixture
        /// that fails once in fifty runs is worse than no fixture, because the failure arrives
        /// months later attached to whatever was being changed at the time.
        /// </remarks>
        private static List<SoakSample> Run(
            int cycles,
            double hours,
            double bytesPerCycle,
            double bytesPerHour,
            double scatterBytes,
            int samples = 40,
            int firstSampledCycle = 0,
            double openingStepBytes = 0.0,
            int stepCompleteByCycle = 5)
        {
            var log = new List<SoakSample>();

            for (int i = 0; i < samples; i++)
            {
                double share = samples == 1 ? 0.0 : i / (double)(samples - 1);
                int atCycle = firstSampledCycle +
                    (int)Math.Round(share * (cycles - firstSampledCycle));
                double atHours = hours * (cycles == 0 ? 0.0 : atCycle / (double)cycles);

                // Sin of an irrational stride: bounded, deterministic, and not in step with the
                // sampling interval, so it does not alias into a slope of its own.
                double scatter = scatterBytes * Math.Sin(i * 2.399963229728653);

                // A one-off rise that finishes early and then stays: what jitting the window path
                // costs. It is a step and not a slope, which is exactly why fitting from cycle zero
                // misreads it.
                double warmUp = stepCompleteByCycle <= 0
                    ? openingStepBytes
                    : openingStepBytes * Math.Min(1.0, atCycle / (double)stepCompleteByCycle);

                double floor = FloorBytes + warmUp +
                    (bytesPerCycle * atCycle) + (bytesPerHour * atHours) + scatter;

                log.Add(new SoakSample(
                    atHours * 3600.0,
                    (long)(floor * 1.4),
                    (long)floor,
                    (long)(130.0 * Mib),
                    640,
                    22,
                    30,
                    (long)(atHours * 3600.0 * 60.0),
                    0L,
                    4,
                    (long)(0.02 * Mib),
                    1,
                    atCycle));
            }

            return log;
        }
    }
}
