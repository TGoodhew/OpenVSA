using System;
using System.Collections.Generic;
using System.Linq;
using OpenVSA.Core;
using OpenVSA.Hal;
using OpenVSA.Measurement;
using Xunit;
using Xunit.Abstractions;

namespace OpenVSA.Measurement.Tests
{
    /// <summary>
    /// <c>REQ-ACQ-002</c>: a clamped main time length names both remedies and their numbers.
    /// </summary>
    public class MainTimeClampingTests
    {
        private readonly ITestOutputHelper _output;

        public MainTimeClampingTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void RequestingAMillisecondWhereEightyMicrosecondsArePossibleClampsAndExplains()
        {
            // The requirement's criterion, in its own numbers. REQ-ACQ-001 makes main time
            // (N_f - 1)/Span, so 80 us over a 10 MHz span is 801 points - the default count - and
            // a front end that can deliver no more than that limits the record to 80 us.
            var capabilities = new Capabilities { Samples = 1024 };

            PlannedAcquisition plan = AcquisitionPlanner.PlanForMainTime(
                capabilities, 1e9, 10e6, 1e-3, 0.0, AnalysisPath.ComplexZoom);

            ParameterCoercion clamped = plan.Coercions
                .FirstOrDefault(c => c.Parameter == "MainTimeLength");

            Assert.NotNull(clamped);

            _output.WriteLine(clamped.Reason);

            Assert.Equal(1e-3, clamped.Requested, 9);
            Assert.Equal(80e-6, clamped.Honoured, 9);
            Assert.Equal(80e-6, plan.MaxTimeSeconds, 9);

            // Both remedies, each with the number it would take.
            Assert.Contains("reduce the span", clamped.Reason);
            Assert.Contains("increase the frequency points", clamped.Reason);

            // (801 - 1) / 1 ms = 800 kHz.
            Assert.Contains("800 kHz", clamped.Reason);

            // 1 ms x 10 MHz needs 10 000 intervals, and the REQ-DSP-022 ladder is 50*2^k + 1, so
            // the next count that covers it is 12801 rather than a round 10001 - which is the
            // point of naming a count the user can actually select.
            Assert.Contains("12801", clamped.Reason);
        }

        [Fact]
        public void ARequestThatFitsIsNotClampedAndCarriesNoCoercion()
        {
            // The other side of it: a request the front end can honour must pass through silently,
            // or the message becomes noise nobody reads.
            var capabilities = new Capabilities { Samples = 1 << 22 };

            PlannedAcquisition plan = AcquisitionPlanner.PlanForMainTime(
                capabilities, 1e9, 10e6, 80e-6, 0.0, AnalysisPath.ComplexZoom);

            Assert.DoesNotContain(plan.Coercions, c => c.Parameter == "MainTimeLength");
            Assert.True(plan.MaxTimeSeconds >= 80e-6);
        }

        [Fact]
        public void TheRemediesAreArithmeticallyRightRatherThanJustPresent()
        {
            // A message naming both remedies is worth nothing if the numbers in it do not work.
            // Both are checked by using them: the stated span, and the stated point count, each
            // deliver the main time that was asked for.
            const double wanted = 1e-3;
            const double span = 10e6;
            const int available = 801;

            string remedies = AcquisitionPlanner.Remedies(wanted, span, available);

            _output.WriteLine(remedies);

            // Remedy one: at the narrower span, the available points give the wanted time.
            double neededSpan = (available - 1) / wanted;
            Assert.Equal(wanted, (available - 1) / neededSpan, 12);

            // Remedy two: at the stated span, the larger count gives at least the wanted time.
            int neededPoints = FrequencyPoints.Supported.First(p => p - 1 >= wanted * span);
            Assert.True((neededPoints - 1) / span >= wanted);

            Assert.Contains("800 kHz", remedies);
            Assert.Contains(neededPoints.ToString(), remedies);
        }

        [Fact]
        public void ARequestBeyondTheLadderItselfSaysSoRatherThanNamingAnUnreachableCount()
        {
            // A remedy the user cannot select is not a remedy. Beyond the point ladder's maximum
            // the message says that instead of printing a number no setting could reach.
            string remedies = AcquisitionPlanner.Remedies(1.0, 10e6, 801);

            _output.WriteLine(remedies);

            Assert.Contains("more than the maximum", remedies);
            Assert.Contains("reduce the span", remedies);
        }

        [Fact]
        public void TheArgumentsAreChecked()
        {
            var capabilities = new Capabilities();

            Assert.Throws<ArgumentNullException>(
                () => AcquisitionPlanner.PlanForMainTime(
                    null, 1e9, 10e6, 1e-3, 0.0, AnalysisPath.ComplexZoom));

            Assert.Throws<ArgumentOutOfRangeException>(
                () => AcquisitionPlanner.PlanForMainTime(
                    capabilities, 1e9, 10e6, 0.0, 0.0, AnalysisPath.ComplexZoom));

            Assert.Throws<ArgumentOutOfRangeException>(() => AcquisitionPlanner.Remedies(0.0, 10e6, 801));
            Assert.Throws<ArgumentOutOfRangeException>(() => AcquisitionPlanner.Remedies(1e-3, 0.0, 801));
            Assert.Throws<ArgumentOutOfRangeException>(() => AcquisitionPlanner.Remedies(1e-3, 10e6, 1));
        }

        /// <summary>Capabilities whose block size is what a test wants to vary.</summary>
        private sealed class Capabilities : IFrontEndCapabilities
        {
            public int Samples { get; set; } = 1 << 20;

            public FrequencyRange CenterFrequencyRange => new FrequencyRange(0.0, 26.5e9);
            public double MaxSpanHz => 40e6;
            public double MinSpanHz => 1.0;
            public double MaxSampleRateHz => 51.2e6;
            public int MaxSamplesPerBlock => Samples;
            public long MaxCaptureSamples => Samples;
            public bool SupportsBasebandIq => true;
            public int ChannelCount => 1;
            public bool SupportsPhaseCoherentChannels => false;
            public IReadOnlyList<TriggerStyle> TriggerStyles => new[] { TriggerStyle.Immediate };
            public AmplitudeRange ReferenceLevelRange => new AmplitudeRange(-100.0, 30.0);
            public bool SupportsExternalRef => false;
            public bool SupportsInputRangeControl => true;
            public bool SupportsRealTimeAnalysis => false;
            public long MaxPreTriggerSamples => 0L;
        }
    }
}
