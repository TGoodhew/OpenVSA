using System;
using System.Collections.Generic;
using System.Linq;
using OpenVSA.PerformanceGate;
using Xunit;
using Xunit.Abstractions;

namespace OpenVSA.PerformanceGate.Tests
{
    /// <summary>
    /// <c>REQ-TST-007</c>, clause by clause: "a deliberately introduced 20 % slowdown fails the
    /// build while a 5 % one does not — the threshold is tested, not merely configured".
    /// </summary>
    /// <remarks>
    /// The threshold is driven from both sides rather than asserted to be 0.15. A gate whose
    /// constant is right and whose comparison is backwards passes a configuration check and fails
    /// every real regression, which is the failure worth testing for.
    /// </remarks>
    public class RegressionGateTests
    {
        private static readonly MachineClass Reference =
            new MachineClass("AMD Ryzen 9 7950X", 32, 64);

        private readonly ITestOutputHelper _output;

        /// <summary>Takes xunit's output sink.</summary>
        /// <param name="output">Where rendered reports are written.</param>
        public RegressionGateTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Theory]
        [InlineData(0.20, Verdict.Regressed)]
        [InlineData(0.16, Verdict.Regressed)]
        [InlineData(0.05, Verdict.Passed)]
        [InlineData(0.00, Verdict.Passed)]
        [InlineData(-0.30, Verdict.Passed)]
        public void TheThresholdIsTestedFromBothSides(double slowdown, Verdict expected)
        {
            // A quiet run, so the noise term cannot be what decides.
            GateReport report = Judge("Spectrum1MRenderedDecimated", 12.0, Worsen(12.0, Better.Higher, slowdown), 0.002);

            TargetVerdict v = Only(report, "REQ-NFR-021");

            _output.WriteLine(
                slowdown.ToString("+0.##;-0.##;0") + " applied -> " + v.Verdict +
                " (" + (v.RelativeChange * 100.0).ToString("F1") + "% worse)");

            Assert.Equal(expected, v.Verdict);
            Assert.Equal(expected == Verdict.Regressed, v.Fails);
        }

        [Fact]
        public void ARegressionIsCaughtWhicheverDirectionCountsAsBetter()
        {
            // REQ-NFR-021 is updates/s (higher is better); REQ-NFR-025 is seconds (lower is
            // better). Getting one of the two backwards would let a halving of the update rate
            // read as an improvement, and no threshold tuning would catch that.
            GateReport faster = Judge("ColdStartToFirstTrace", 2.0, 2.6, 0.002);
            GateReport slower = Judge("Spectrum1MRenderedDecimated", 12.0, 9.0, 0.002);

            Assert.Equal(Verdict.Regressed, Only(faster, "REQ-NFR-025").Verdict);
            Assert.Equal(Verdict.Regressed, Only(slower, "REQ-NFR-021").Verdict);

            // And an improvement in each direction is not a regression.
            Assert.Equal(Verdict.Passed, Only(Judge("ColdStartToFirstTrace", 2.0, 1.5, 0.002), "REQ-NFR-025").Verdict);
            Assert.Equal(Verdict.Passed, Only(Judge("Spectrum1MRenderedDecimated", 12.0, 15.0, 0.002), "REQ-NFR-021").Verdict);
        }

        [Fact]
        public void ARunTooNoisyToResolveTheThresholdIsInconclusiveRatherThanPassed()
        {
            // "Measurements report variance, and a run too noisy to distinguish 15 % is reported
            // as inconclusive rather than passed." The measured mean here is identical to the
            // baseline — the naive answer is a pass, and it would be a pass that showed nothing.
            GateReport report = Judge("Spectrum1MRenderedDecimated", 12.0, 12.0, 0.40);

            TargetVerdict v = Only(report, "REQ-NFR-021");

            _output.WriteLine("resolution " + (v.Measurement.RelativeResolution * 100.0).ToString("F1") + "% -> " + v.Verdict);

            Assert.True(v.Measurement.RelativeResolution > 0.15);
            Assert.Equal(Verdict.Inconclusive, v.Verdict);

            // Inconclusive does not fail the build. It is a statement that nothing was shown, and
            // failing on it would make every busy machine a red build.
            Assert.False(v.Fails);
        }

        [Fact]
        public void ANoisyRunStillReportsARegressionItCanSee()
        {
            // Noise makes a small change unknowable, not every change. A 60 % slowdown on a run
            // that resolves 20 % is still a regression, and calling it inconclusive would let a
            // real one hide behind a busy machine.
            GateReport report = Judge("Spectrum1MRenderedDecimated", 12.0, 12.0 * 0.4, 0.20);

            TargetVerdict v = Only(report, "REQ-NFR-021");

            Assert.True(v.Measurement.RelativeResolution > 0.15);
            Assert.Equal(Verdict.Regressed, v.Verdict);
        }

        [Fact]
        public void AnUnrecognisedMachineIsReportedRatherThanComparedAgainstTheReference()
        {
            // "A run on unrecognised hardware reports that rather than comparing against an
            // inapplicable baseline." A CI runner is not the reference machine.
            var store = new BaselineStore();
            store.Set(Entry(Reference, "Spectrum1MRenderedDecimated", 12.0));

            var runner = new MachineClass("Intel Xeon Platinum 8370C", 4, 16);

            GateReport report = new RegressionGate(store).Judge(
                runner, FullRun("Spectrum1MRenderedDecimated", 3.0, 0.002));

            _output.WriteLine(report.Render());

            Assert.False(report.MachineRecognised);

            TargetVerdict v = Only(report, "REQ-NFR-021");

            // Measured at a quarter of the reference figure — and not called a regression.
            Assert.Equal(Verdict.NoBaseline, v.Verdict);
            Assert.False(report.Failed);
            Assert.Contains("NO BASELINE", report.Render());
        }

        [Fact]
        public void ATargetWhoseFeatureExistsAndProducedNoNumberFailsTheRun()
        {
            // The half of REQ-TST-007 that is easy to miss: the harness may not quietly shrink to
            // the targets that happen to be implemented.
            var store = new BaselineStore();
            store.Set(Entry(Reference, "Spectrum1MRenderedDecimated", 12.0));

            GateReport report = new RegressionGate(store).Judge(Reference, new TargetMeasurement[0]);

            TargetVerdict missing = Only(report, "REQ-NFR-021");

            Assert.Equal(Verdict.Missing, missing.Verdict);
            Assert.True(report.Failed);
            Assert.Equal(1, report.ExitCode);
            Assert.Contains("may not", report.Render());
        }

        [Fact]
        public void ATargetWaitingOnALaterPhaseIsReportedNotYetAndDoesNotFail()
        {
            // REQ-NFR-022 and -023 need the Phase 2 demodulator; REQ-NFR-026 needs Phase 3
            // recording. Absent is the correct state for those, and it is distinguishable from a
            // skip because the target declares which phase it waits on.
            var store = new BaselineStore();
            GateReport report = new RegressionGate(store).Judge(Reference, new TargetMeasurement[0]);

            foreach (string requirement in new[] { "REQ-NFR-022", "REQ-NFR-023", "REQ-NFR-026" })
            {
                TargetVerdict v = Only(report, requirement);

                Assert.Equal(Verdict.AwaitingPhase, v.Verdict);
                Assert.False(v.Fails);
                Assert.NotNull(v.Target.AwaitingPhase);
            }

            _output.WriteLine(report.Render());
        }

        [Fact]
        public void EverySevenTargetsAreJudgedEveryRun()
        {
            // The seven share one acceptance criterion, so a run that judged six of them would
            // satisfy no requirement fully.
            GateReport report = new RegressionGate(new BaselineStore())
                .Judge(Reference, new TargetMeasurement[0]);

            Assert.Equal(7, report.Verdicts.Count);
            Assert.Equal(7, TargetCatalogue.All.Count);

            string[] expected =
            {
                "REQ-NFR-020", "REQ-NFR-021", "REQ-NFR-022",
                "REQ-NFR-023", "REQ-NFR-024", "REQ-NFR-025", "REQ-NFR-026",
            };

            Assert.Equal(expected, report.Verdicts.Select(v => v.Target.Requirement).ToArray());
        }

        [Fact]
        public void AMeasurementWithNoSpreadIsRefused()
        {
            // A mean with no spread beside it cannot answer the inconclusive question, so it is
            // not accepted at all rather than assumed quiet.
            Assert.Throws<ArgumentOutOfRangeException>(
                () => new TargetMeasurement("x", 1.0, 0.1, 1));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => new TargetMeasurement("x", 0.0, 0.1, 10));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => new TargetMeasurement("x", 1.0, -0.1, 10));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => new RegressionGate(new BaselineStore(), 0.0));
        }

        // ---- Helpers -----------------------------------------------------------------------------

        private const int Samples = 30;

        /// <summary>
        /// A run judging one target against a baseline, with every other built target measured
        /// exactly at its own baseline so it drops out of the answer.
        /// </summary>
        /// <remarks>
        /// The whole run is supplied rather than the single target under test, because a target
        /// left out is a <see cref="Verdict.Missing"/> and would fail the run — correctly, and for
        /// a reason that has nothing to do with what these tests are about. That the first draft
        /// of these helpers tripped over it is the behaviour working.
        /// </remarks>
        private static GateReport Judge(string name, double baseline, double measured, double relativeSpread)
        {
            var store = new BaselineStore();

            foreach (PerformanceTarget target in Built())
            {
                store.Set(Entry(Reference, target.Name, target.Name == name ? baseline : target.Stated));
            }

            return new RegressionGate(store).Judge(Reference, FullRun(name, measured, relativeSpread));
        }

        /// <summary>
        /// Measurements for every built target: the named one as asked, the rest at their stated
        /// figure and quiet.
        /// </summary>
        private static IEnumerable<TargetMeasurement> FullRun(string name, double measured, double relativeSpread)
        {
            foreach (PerformanceTarget target in Built())
            {
                bool subject = target.Name == name;
                double mean = subject ? measured : target.Stated;
                double spread = subject ? relativeSpread : 0.002;

                // Choose the deviation so the run's resolving power is the spread asked for:
                // resolution = 1.96 * sd / sqrt(n) / mean.
                yield return new TargetMeasurement(
                    target.Name, mean, spread * mean * Math.Sqrt(Samples) / 1.96, Samples);
            }
        }

        /// <summary>The targets whose feature exists, so absence from a run is a skip.</summary>
        private static IEnumerable<PerformanceTarget> Built() =>
            TargetCatalogue.All.Where(t => !t.AwaitingPhase.HasValue);

        private static BaselineEntry Entry(MachineClass machine, string name, double mean) =>
            new BaselineEntry(machine, name, mean, 0.01, new DateTime(2026, 7, 28, 0, 0, 0, DateTimeKind.Utc), "abc1234");

        /// <summary>The verdict for one requirement.</summary>
        private static TargetVerdict Only(GateReport report, string requirement) =>
            report.Verdicts.Single(v => v.Target.Requirement == requirement);

        /// <summary>A value that is <paramref name="fraction"/> worse than <paramref name="from"/>.</summary>
        private static double Worsen(double from, Better better, double fraction) =>
            better == Better.Higher ? from * (1.0 - fraction) : from * (1.0 + fraction);
    }
}
