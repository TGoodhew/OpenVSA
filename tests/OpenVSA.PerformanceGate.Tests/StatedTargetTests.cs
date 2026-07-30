using System.Linq;
using OpenVSA.PerformanceGate;
using Xunit;

namespace OpenVSA.PerformanceGate.Tests
{
    /// <summary>
    /// A target can be stable and still be too slow. The gate compares against a baseline; the
    /// requirement states a figure; those are different questions and both get asked.
    /// </summary>
    public class StatedTargetTests
    {
        private static readonly MachineClass Machine = new MachineClass("Test CPU", 8, 32);

        [Fact]
        public void AStableMeasurementBelowTheStatedFigureIsStillReported()
        {
            // This is not hypothetical: REQ-NFR-021 measured 9.49 updates/s against its stated 10
            // on the reference machine the first time the harness ran. Compared against a baseline
            // taken from that same run it passes for ever.
            var store = new BaselineStore();
            store.Set(new BaselineEntry(
                Machine, "Spectrum1MRenderedDecimated", 9.49, 0.01,
                new System.DateTime(2026, 7, 28, 0, 0, 0, System.DateTimeKind.Utc), "x"));

            GateReport report = new RegressionGate(store).Judge(
                Machine,
                new[] { new TargetMeasurement("Spectrum1MRenderedDecimated", 9.49, 0.02, 6) });

            TargetVerdict v = report.Verdicts.Single(x => x.Target.Requirement == "REQ-NFR-021");

            // No regression against the baseline...
            Assert.Equal(PerformanceGate.Verdict.Passed, v.Verdict);

            // ...and still short of what the requirement asks for.
            Assert.True(v.MissesStatedTarget);
            Assert.Contains("BELOW THE STATED TARGET", report.Render());
            Assert.Contains(v, report.BelowStatedTarget);
        }

        [Fact]
        public void TheStatedFigureIsCheckedTheRightWayRoundForBothDirections()
        {
            // Higher-is-better: below the figure is a miss. Lower-is-better: above it is.
            var store = new BaselineStore();

            GateReport slow = Judge(store, "ColdStartToFirstTrace", 4.0);
            GateReport quick = Judge(store, "ColdStartToFirstTrace", 2.0);

            Assert.True(For(slow, "REQ-NFR-025").MissesStatedTarget);
            Assert.False(For(quick, "REQ-NFR-025").MissesStatedTarget);

            Assert.True(For(Judge(store, "Spectrum8192Rendered", 45.0), "REQ-NFR-020").MissesStatedTarget);
            Assert.False(For(Judge(store, "Spectrum8192Rendered", 90.0), "REQ-NFR-020").MissesStatedTarget);
        }

        [Fact]
        public void AColdStartIsJudgedOnItsColdFigureAndNotOnTheWarmMean()
        {
            // The defect this exists to stop. REQ-NFR-025 states a COLD start of 3 s, but only the
            // first launch of a session is cold: the reproducible figure a 15 % regression gate needs
            // is the warm mean over the launches after it. Reported alone, that warm mean was then
            // compared against the cold requirement -- 1.36 s against 3 s, comfortably inside, while
            // the cold start it was standing in for was 3.29 s and over.
            var store = new BaselineStore();
            store.Set(new BaselineEntry(
                Machine, "ColdStartToFirstTrace", 1.36, 0.02,
                new System.DateTime(2026, 7, 29, 0, 0, 0, System.DateTimeKind.Utc), "s"));

            GateReport report = new RegressionGate(store).Judge(
                Machine,
                new[]
                {
                    new TargetMeasurement(
                        "ColdStartToFirstTrace", 1.36, 0.02, 4, againstStated: 3.29),
                });

            TargetVerdict v = For(report, "REQ-NFR-025");

            // The warm mean has not regressed against the warm baseline, which is the gate's job...
            Assert.Equal(PerformanceGate.Verdict.Passed, v.Verdict);

            // ...and the requirement is missed all the same, which is the other question.
            Assert.True(
                v.MissesStatedTarget,
                "3.29 s cold against a stated 3 s has to be reported as a miss.");
            Assert.Contains("BELOW THE STATED TARGET", report.Render());
        }

        [Fact]
        public void AColdStartInsideTheStatedFigureIsNotReportedAsAMiss()
        {
            // The other side of it: the distinction must not turn into a gate that always complains.
            GateReport report = new RegressionGate(new BaselineStore()).Judge(
                Machine,
                new[]
                {
                    new TargetMeasurement(
                        "ColdStartToFirstTrace", 1.36, 0.02, 4, againstStated: 2.80),
                });

            Assert.False(For(report, "REQ-NFR-025").MissesStatedTarget);
        }

        [Fact]
        public void AMeasurementThatNamesNoSeparateFigureIsJudgedOnItsMean()
        {
            // Every other target asks one question of one population, and must keep behaving as it
            // did: AgainstStated defaults to the mean rather than to nothing.
            var measurement = new TargetMeasurement("Spectrum8192Rendered", 45.0, 1.0, 10);

            Assert.Equal(45.0, measurement.AgainstStated);
            Assert.True(For(Judge(new BaselineStore(), "Spectrum8192Rendered", 45.0), "REQ-NFR-020")
                .MissesStatedTarget);
        }

        [Fact]
        public void ASeparateFigureSurvivesTheHandOffToTheGate()
        {
            // The two halves are separate processes, so the figure has to cross a file. One that was
            // dropped in writing would put the old defect back with no test failing.
            var written = new[]
            {
                new TargetMeasurement("ColdStartToFirstTrace", 1.36, 0.02, 4, againstStated: 3.29),
            };

            TargetMeasurement read = MeasurementFile.Read(MeasurementFile.Write(written)).Single();

            Assert.Equal(1.36, read.Mean, 6);
            Assert.Equal(3.29, read.AgainstStated, 6);
        }

        [Fact]
        public void AFileFromBeforeTheDistinctionExistedStillReads()
        {
            // Four columns, as the format was. The mean stands for both figures, which is what it
            // meant then -- refusing the file would make an old measurement unreadable rather than
            // merely less informative.
            TargetMeasurement read = MeasurementFile.Read(
                "# REQ-TST-007 run measurements.\n" +
                "benchmark\tmean\tstddev\tsamples\n" +
                "ColdStartToFirstTrace\t1.36\t0.02\t4\n").Single();

            Assert.Equal(1.36, read.Mean, 6);
            Assert.Equal(1.36, read.AgainstStated, 6);
        }

        [Fact]
        public void ANonsensicalSeparateFigureIsRefused()
        {
            Assert.Throws<System.ArgumentOutOfRangeException>(() =>
                new TargetMeasurement("ColdStartToFirstTrace", 1.36, 0.02, 4, againstStated: 0.0));
            Assert.Throws<System.ArgumentOutOfRangeException>(() =>
                new TargetMeasurement("ColdStartToFirstTrace", 1.36, 0.02, 4, againstStated: -1.0));
            Assert.Throws<System.ArgumentOutOfRangeException>(() =>
                new TargetMeasurement(
                    "ColdStartToFirstTrace", 1.36, 0.02, 4,
                    againstStated: double.PositiveInfinity));
        }

        [Fact]
        public void ATargetWithNoMeasurementCannotMissItsFigure()
        {
            GateReport report = new RegressionGate(new BaselineStore())
                .Judge(Machine, new TargetMeasurement[0]);

            Assert.Empty(report.BelowStatedTarget);
        }

        private static GateReport Judge(BaselineStore store, string name, double measured) =>
            new RegressionGate(store).Judge(
                Machine, new[] { new TargetMeasurement(name, measured, measured * 0.01, 10) });

        private static TargetVerdict For(GateReport report, string requirement) =>
            report.Verdicts.Single(v => v.Target.Requirement == requirement);
    }
}
