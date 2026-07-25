using System;
using System.Collections.Generic;
using OpenVSA.Hal;
using OpenVSA.Ui;
using Xunit;

namespace OpenVSA.Ui.Tests
{
    /// <summary>
    /// <c>REQ-HAL-001</c>'s third clause: the UI surfaces the coercion.
    /// </summary>
    /// <remarks>
    /// The other two clauses — a pure <c>Negotiate</c>, and a plan that records the honoured value
    /// and the reason — are asserted against the front ends themselves. This is the one that is
    /// about the display, and without it the requirement could be met everywhere except where the
    /// user would see it.
    /// </remarks>
    public class PlanSummaryTests
    {
        [Fact]
        public void ACoercedPlanNamesTheParameter_BothValues_AndTheReason()
        {
            AcquisitionPlan plan = Plan(new ParameterCoercion(
                "Span", 50e6, 10e6, "exceeds front-end maximum span"));

            string summary = PlanSummary.Describe(plan);

            Assert.Contains("Span", summary);
            Assert.Contains("50", summary);
            Assert.Contains("10", summary);
            Assert.Contains("exceeds front-end maximum span", summary);
            Assert.Contains("coerced", summary);
        }

        [Fact]
        public void EveryCoercionIsShown_NotJustTheFirst()
        {
            AcquisitionPlan plan = Plan(
                new ParameterCoercion("Span", 50e6, 10e6, "exceeds front-end maximum span"),
                new ParameterCoercion("ReferenceLevel", 50.0, 30.0, "outside the front-end's reference-level range"));

            string summary = PlanSummary.Describe(plan);

            Assert.Contains("2 requests were coerced", summary);
            Assert.Contains("exceeds front-end maximum span", summary);
            Assert.Contains("outside the front-end's reference-level range", summary);
        }

        [Fact]
        public void AnHonouredPlanSaysSo_RatherThanShowingNothing()
        {
            // Silence would be ambiguous: the user could not tell an honoured request from a
            // display that had failed to update.
            string summary = PlanSummary.Describe(Plan());

            Assert.Contains("Every requested value was honoured.", summary);
            Assert.DoesNotContain("coerced", summary);
        }

        [Fact]
        public void ThePlansOwnFiguresAreShown()
        {
            string summary = PlanSummary.Describe(Plan());

            Assert.Contains("1 GHz", summary);
            Assert.Contains("10 MHz", summary);
            Assert.Contains("12.8 MHz", summary);
            Assert.Contains("8192 samples", summary);
            Assert.Contains("20 dBm", summary);
        }

        [Fact]
        public void ItRefusesAPlanOfNull()
        {
            Assert.Throws<ArgumentNullException>(() => PlanSummary.Describe(null));
        }

        private static AcquisitionPlan Plan(params ParameterCoercion[] coercions) =>
            new AcquisitionPlan(
                centerFrequencyHz: 1e9,
                spanHz: 10e6,
                sampleRateHz: 12.8e6,
                samplesPerBlock: 8192,
                referenceLevelDbm: 20.0,
                supportsGapFreeStreaming: true,
                coercions: new List<ParameterCoercion>(coercions));
    }
}
