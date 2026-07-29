using System;
using System.Collections.Generic;
using OpenVSA.Core;
using OpenVSA.Hal;
using Xunit;
using Xunit.Abstractions;

namespace OpenVSA.Hal.Tests
{
    /// <summary>
    /// <c>REQ-NFR-027</c>: the gap-free judgement and the duty cycle come from a measured rate,
    /// not a bus headline.
    /// </summary>
    /// <remarks>
    /// The failure this guards against is a plan that says gap-free because the arithmetic used the
    /// figure GPIB can do in principle. This instrument has been measured at around 2 300 samples a
    /// second through an HP-IB extender — 18 kB/s against a nominal megabyte. A plan judged against
    /// the headline is not slightly optimistic; it is wrong by nearly two orders of magnitude, and
    /// the user finds out by watching the trace fall behind.
    /// </remarks>
    public class ThroughputHonestyTests
    {
        private readonly ITestOutputHelper _output;

        /// <summary>Takes xunit's output sink.</summary>
        /// <param name="output">Where the duty cycles are written.</param>
        public ThroughputHonestyTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void TheDutyCycleIsComputedFromTheMeasuredRate()
        {
            // 8 192 samples at 2 MS/s is 4.1 ms of signal and 65 536 bytes to move. At 18 kB/s that
            // takes 3.6 s — a duty cycle near 900, which is not "slightly behind".
            AcquisitionPlan plan = Plan(measuredBytesPerSecond: 18400.0);

            _output.WriteLine(
                "measured 18.4 kB/s: duty cycle " + plan.DutyCycle.ToString("F1"));

            Assert.True(plan.DutyCycle > 100.0);
            Assert.False(double.IsNaN(plan.DutyCycle));
        }

        [Fact]
        public void ADutyCycleBelowOneMeansTheLinkKeepsUp()
        {
            AcquisitionPlan plan = Plan(measuredBytesPerSecond: 50.0e6);

            _output.WriteLine("measured 50 MB/s: duty cycle " + plan.DutyCycle.ToString("F3"));

            Assert.True(plan.DutyCycle < 1.0);
        }

        [Fact]
        public void AnUnmeasuredRateReportsNotANumberRatherThanAGuess()
        {
            // The distinction the requirement is about. A duty cycle of zero would read as "keeps
            // up easily" and would be a fabrication; NaN says "not measured", which is the truth
            // before the instrument has been probed.
            AcquisitionPlan plan = Plan(measuredBytesPerSecond: 0.0);

            Assert.True(double.IsNaN(plan.DutyCycle));
            Assert.Equal(0.0, plan.MeasuredBytesPerSecond);
        }

        [Fact]
        public void TheDutyCycleAndTheGapFreeFlagAgree()
        {
            // The flag alone loses the magnitude: "0.98" and "12.4" are both not-gap-free and mean
            // very different things to somebody deciding what to change.
            AcquisitionPlan fast = Plan(50.0e6, gapFree: true);
            AcquisitionPlan slow = Plan(18400.0, gapFree: false);

            Assert.True(fast.SupportsGapFreeStreaming && fast.DutyCycle < 1.0);
            Assert.True(!slow.SupportsGapFreeStreaming && slow.DutyCycle > 1.0);
        }

        private static AcquisitionPlan Plan(
            double measuredBytesPerSecond, bool gapFree = false)
        {
            return new AcquisitionPlan(
                centerFrequencyHz: 1.0e9,
                spanHz: 2.0e6,
                sampleRateHz: 2.0e6,
                samplesPerBlock: 8192,
                referenceLevelDbm: 0.0,
                supportsGapFreeStreaming: gapFree,
                coercions: new List<ParameterCoercion>(),
                path: AnalysisPath.ComplexZoom,
                measuredBytesPerSecond: measuredBytesPerSecond);
        }
    }
}
