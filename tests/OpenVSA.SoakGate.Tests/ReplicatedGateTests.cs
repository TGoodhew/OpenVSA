using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using Xunit.Abstractions;

namespace OpenVSA.SoakGate.Tests
{
    /// <summary>
    /// The replicated managed-memory rule, driven from both sides.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This gate exists because of a real mistake, and the fixture reproduces it.</strong>
    /// The collected managed floor is a staircase that moves both ways — flat for ten minutes, then
    /// a four-to-eight kibibyte step. Two runs of one identical configuration fitted 0.06 ±0.70 and
    /// 54.96 ±2.06 KiB/hour, and a conclusion was published from the first before the second was
    /// taken.
    /// </para>
    /// <para>
    /// The load-bearing test is <see cref="RunsThatEachLookCertainButDisagreeDoNotEstablishALeak"/>.
    /// Every other test here would pass on a gate that simply averaged the runs and kept using the
    /// within-run error.
    /// </para>
    /// </remarks>
    public class ReplicatedGateTests
    {
        private const double Kib = 1024.0;
        private const double Mib = 1024.0 * 1024.0;

        private readonly ITestOutputHelper _output;

        /// <summary>Takes the output helper, so the figures behind a verdict are visible.</summary>
        /// <param name="output">Where the report is written.</param>
        public ReplicatedGateTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void RunsThatEachLookCertainButDisagreeDoNotEstablishALeak()
        {
            // The exact shape of the mistake this gate was written for. Three staircases whose steps
            // fall differently, so each run fits its own line tightly and the three slopes are
            // nothing like each other. Judged one at a time, the middle one "proves" a leak at many
            // times its own error bar. Judged together, they prove only that the quantity wanders.
            ReplicatedReport report = Judge(
                Staircase(stepKib: 6.0, upEvery: 40, downEvery: 41, seed: 1),
                Staircase(stepKib: 6.0, upEvery: 9, downEvery: 40, seed: 2),
                Staircase(stepKib: 6.0, upEvery: 40, downEvery: 9, seed: 3));

            _output.WriteLine(report.Render());

            // Each run on its own looks decisive...
            foreach (double slope in report.Slopes)
            {
                _output.WriteLine("run slope " + (slope / Kib) + " KiB/hour");
            }

            Assert.True(
                report.Slopes.Max() - report.Slopes.Min() > 20.0 * Kib,
                "The fixture's runs agree too closely to exercise the rule.");

            // ...and together they say nothing.
            Assert.Equal(SoakVerdict.Passed, Finding(report, "bounded across repeated runs").Verdict);
            Assert.True(report.Passed, "A wandering floor was reported as a leak.");
        }

        [Fact]
        public void ALeakThatEveryRunAgreesOnStillFails()
        {
            // The other side, and the one that keeps the gate worth having: the same staircase noise
            // with a real 3 MiB/hour rise under all three. Averaging must not launder that away.
            ReplicatedReport report = Judge(
                Staircase(6.0, 40, 41, 1, leakBytesPerHour: 3.0 * Mib),
                Staircase(6.0, 9, 40, 2, leakBytesPerHour: 3.0 * Mib),
                Staircase(6.0, 40, 9, 3, leakBytesPerHour: 3.0 * Mib));

            _output.WriteLine(report.Render());

            Assert.Equal(SoakVerdict.Failed, Finding(report, "bounded across repeated runs").Verdict);
        }

        [Fact]
        public void ASmallLeakEveryRunAgreesOnIsStillFoundWhenTheRunsAreConsistent()
        {
            // Consistency is what buys sensitivity. Three runs that agree closely on a modest rise
            // fail, even though the same figure inside one noisy run would not be resolvable --
            // which is the point of replicating rather than simply running longer.
            ReplicatedReport report = Judge(
                Staircase(0.5, 20, 21, 4, leakBytesPerHour: 40.0 * Kib),
                Staircase(0.5, 20, 21, 5, leakBytesPerHour: 40.0 * Kib),
                Staircase(0.5, 20, 21, 6, leakBytesPerHour: 40.0 * Kib));

            _output.WriteLine(report.Render());

            Assert.Equal(SoakVerdict.Failed, Finding(report, "bounded across repeated runs").Verdict);
        }

        [Fact]
        public void TwoRunsCannotSayWhetherADifferenceIsTypical()
        {
            ReplicatedReport report = Judge(
                Staircase(6.0, 40, 41, 1),
                Staircase(6.0, 9, 40, 2));

            _output.WriteLine(report.Render());

            Assert.Equal(SoakVerdict.Inconclusive, Finding(report, "Enough runs").Verdict);
            Assert.False(report.Passed);
        }

        [Fact]
        public void TheUnderstatementOfASingleRunsErrorBarIsReported()
        {
            // The number that decides whether anybody may quote one run. Reported rather than
            // judged: a large ratio is a fact about the quantity, not a fault in the run.
            ReplicatedReport report = Judge(
                Staircase(6.0, 40, 41, 1),
                Staircase(6.0, 9, 40, 2),
                Staircase(6.0, 40, 9, 3));

            _output.WriteLine(report.Render());

            Assert.True(
                report.Understatement > 2.0,
                "The fixture should show a single run badly understating its own uncertainty; it " +
                "reported a factor of " + report.Understatement + ".");

            Assert.Contains(
                "no single run's slope may be quoted",
                Finding(report, "understates").Detail,
                StringComparison.Ordinal);
        }

        [Fact]
        public void JudgingNothingIsRefused()
        {
            Assert.Throws<ArgumentNullException>(() => new ReplicatedGate().Judge(null));
        }

        [Fact]
        public void RunsTooShortToFitAreNotCountedAsRuns()
        {
            // A run whose floor cannot be fitted contributes nothing, and must not be silently
            // counted towards the minimum -- three logs of which two are unusable is one run.
            var empty = new List<SoakSample>();

            ReplicatedReport report = new ReplicatedGate().Judge(
                new IReadOnlyList<SoakSample>[] { Staircase(6.0, 40, 41, 1), empty, empty });

            _output.WriteLine(report.Render());

            Assert.Equal(SoakVerdict.Inconclusive, Finding(report, "Enough runs").Verdict);
            Assert.Contains("1 of 3", Finding(report, "Enough runs").Detail, StringComparison.Ordinal);
        }

        // ---- Fixtures ------------------------------------------------------------------------------

        private static ReplicatedReport Judge(params IReadOnlyList<SoakSample>[] runs) =>
            new ReplicatedGate().Judge(runs);

        private static SoakFinding Finding(ReplicatedReport report, string claimContains) =>
            report.Findings.First(
                f => f.Claim.IndexOf(claimContains, StringComparison.OrdinalIgnoreCase) >= 0);

        /// <summary>
        /// An eight-hour run whose collected floor is a staircase moving in both directions.
        /// </summary>
        /// <param name="stepKib">How large each step is.</param>
        /// <param name="upEvery">A step up every this many samples.</param>
        /// <param name="downEvery">A step down every this many samples.</param>
        /// <param name="seed">Shifts where the steps fall, so runs differ as real ones do.</param>
        /// <param name="leakBytesPerHour">A genuine rise underneath, or zero.</param>
        /// <remarks>
        /// A staircase and not a noisy line, because the distinction is the whole point: a staircase
        /// sits tightly about a straight line, so each run reports a small standard error while the
        /// runs disagree wildly. Gaussian noise would give large within-run errors and would not
        /// reproduce the failure at all.
        /// </remarks>
        private static IReadOnlyList<SoakSample> Staircase(
            double stepKib,
            int upEvery,
            int downEvery,
            int seed,
            double leakBytesPerHour = 0.0)
        {
            var samples = new List<SoakSample>();
            double floor = 17.0 * Mib;
            long frames = 0L;
            const int Total = (8 * 60) + 1;

            for (int i = 0; i < Total; i++)
            {
                double hours = i / 60.0;

                frames += 1800L;

                if (i > 0 && (i + seed) % upEvery == 0)
                {
                    floor += stepKib * Kib;
                }

                if (i > 0 && (i + seed) % downEvery == 0)
                {
                    floor -= stepKib * Kib;
                }

                samples.Add(new SoakSample(
                    hours * 3600.0,
                    (long)(floor * 1.3),
                    (long)(floor + (leakBytesPerHour * hours)),
                    (long)(420.0 * Mib),
                    700 + (i % 11),
                    300 + (i % 7),
                    200 + (i % 5),
                    frames,
                    12L * i,
                    24,
                    (long)(48.0 * Mib),
                    1,
                    i / 15));
            }

            return samples;
        }
    }
}
