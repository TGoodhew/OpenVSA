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
