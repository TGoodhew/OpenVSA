using System;
using System.Collections.Generic;
using OpenVSA.Core;
using OpenVSA.Hal;
using OpenVSA.Measurement;
using Xunit;

namespace OpenVSA.Measurement.Tests
{
    /// <summary>
    /// The planner: how many displayed points a <em>particular</em> instrument can support.
    /// </summary>
    /// <remarks>
    /// The relations are tested against closed form elsewhere. What matters here is that every
    /// instrument-dependent number comes from <see cref="IFrontEndCapabilities"/> — a front end
    /// that can only return a short block gets fewer points, one with a deep capture gets more, and
    /// no case is special-cased by model. The capabilities in these tests are deliberately unlike
    /// any real instrument's, so a planner that had learned a particular one's numbers would fail.
    /// </remarks>
    public class AcquisitionPlannerTests
    {
        [Fact]
        public void ThePointCountIsBoundedByWhatTheFrontEndCanCapture()
        {
            // 4 096 samples per block allows N_FFT = 4096, i.e. 3 201 points on the complex path.
            var caps = new Capabilities { MaxSamplesPerBlock = 4096 };

            Assert.Equal(3201, AcquisitionPlanner.MaximumPointsFor(caps, AnalysisPath.ComplexZoom));

            // The same instrument on the real path gets half as many, because each point costs
            // twice the samples there.
            Assert.Equal(1601, AcquisitionPlanner.MaximumPointsFor(caps, AnalysisPath.RealBaseband));
        }

        [Fact]
        public void ADeeperFrontEndGetsMorePoints_AndAShallowerOneFewer()
        {
            Assert.Equal(
                801,
                AcquisitionPlanner.MaximumPointsFor(
                    new Capabilities { MaxSamplesPerBlock = 1024 }, AnalysisPath.ComplexZoom));

            Assert.Equal(
                102401,
                AcquisitionPlanner.MaximumPointsFor(
                    new Capabilities { MaxSamplesPerBlock = 131072 }, AnalysisPath.ComplexZoom));
        }

        [Fact]
        public void TheCaptureDepthBoundsItToo_NotJustTheBlockSize()
        {
            // A front end willing to return a large block but unable to hold a deep capture is
            // bounded by the smaller of the two.
            var caps = new Capabilities { MaxSamplesPerBlock = 1 << 20, MaxCaptureSamples = 2048 };

            Assert.Equal(1601, AcquisitionPlanner.MaximumPointsFor(caps, AnalysisPath.ComplexZoom));
        }

        [Fact]
        public void TheRelationsCeilingAppliesToAnUnboundedFrontEnd()
        {
            // Nothing may exceed REQ-DSP-022's 409 601, however capable the instrument claims to be.
            var caps = new Capabilities
            {
                MaxSamplesPerBlock = int.MaxValue,
                MaxCaptureSamples = long.MaxValue,
            };

            Assert.Equal(
                FrequencyPoints.Maximum,
                AcquisitionPlanner.MaximumPointsFor(caps, AnalysisPath.ComplexZoom));
        }

        [Fact]
        public void AskingForMorePointsThanTheInstrumentCanCapture_IsCoercedAndSaidSo()
        {
            var caps = new Capabilities { MaxSamplesPerBlock = 1024 };

            PlannedAcquisition planned = AcquisitionPlanner.Plan(
                caps, 1e9, 10e6, 6401, 0.0, AnalysisPath.ComplexZoom);

            Assert.Equal(801, planned.FrequencyPoints);
            Assert.Equal(6401, planned.RequestedFrequencyPoints);
            Assert.True(planned.Coerced);

            ParameterCoercion coercion = Assert.Single(planned.Coercions);
            Assert.Equal("FrequencyPoints", coercion.Parameter);
            Assert.Equal(6401.0, coercion.Requested);
            Assert.Equal(801.0, coercion.Honoured);
            Assert.Contains("capture", coercion.Reason);
        }

        [Fact]
        public void AnAchievablePointCountIsPassedThroughUntouched()
        {
            PlannedAcquisition planned = AcquisitionPlanner.Plan(
                new Capabilities(), 1e9, 10e6, 801, 0.0, AnalysisPath.ComplexZoom);

            Assert.False(planned.Coerced);
            Assert.Equal(801, planned.FrequencyPoints);
            Assert.Equal(1024, planned.TransformLength);
            Assert.Equal(1024, planned.Request.SamplesPerBlock);
            Assert.Equal(80e-6, planned.MaxTimeSeconds, 12);
        }

        [Fact]
        public void TheRequestCarriesThePathSoTheFrontEndKnowsWhichRateLawToUse()
        {
            PlannedAcquisition planned = AcquisitionPlanner.Plan(
                new Capabilities(), 1e9, 10e6, 801, 0.0, AnalysisPath.RealBaseband);

            Assert.Equal(AnalysisPath.RealBaseband, planned.Request.Path);
            Assert.Equal(2048, planned.Request.SamplesPerBlock);

            // Same span, same points, same maximum time record - the identity of REQ-ACQ-001.
            Assert.Equal(80e-6, planned.MaxTimeSeconds, 12);
        }

        [Fact]
        public void AnUnavailablePointCountIsRejectedRatherThanSnapped()
        {
            // REQ-DSP-022: rejected with a clear message. Snapping would mean a user who asked for
            // 409 602 got 409 601 and never learned the number they believed in does not exist.
            ArgumentOutOfRangeException failure = Assert.Throws<ArgumentOutOfRangeException>(
                () => AcquisitionPlanner.Plan(
                    new Capabilities(), 1e9, 10e6, 409602, 0.0, AnalysisPath.ComplexZoom));

            Assert.Contains("REQ-DSP-022", failure.Message);
        }

        [Fact]
        public void AFrontEndTooSmallForEvenTheMinimumSaysSo()
        {
            // 51 points needs 64 samples on the complex path. A front end that cannot manage that
            // cannot produce a spectrum at all, and saying so beats planning an impossible one.
            var caps = new Capabilities { MaxSamplesPerBlock = 32 };

            ArgumentException failure = Assert.Throws<ArgumentOutOfRangeException>(
                () => AcquisitionPlanner.Plan(caps, 1e9, 10e6, 801, 0.0, AnalysisPath.ComplexZoom));

            Assert.Contains("64 samples", failure.Message);
            Assert.Equal(0, AcquisitionPlanner.MaximumPointsFor(caps, AnalysisPath.ComplexZoom));
        }

        [Fact]
        public void TheDefaultOverloadUsesTheDocumentedDefault_ReducedToFit()
        {
            PlannedAcquisition roomy = AcquisitionPlanner.Plan(new Capabilities(), 1e9, 10e6, 0.0);
            Assert.Equal(AcquisitionPlanner.DefaultFrequencyPoints, roomy.FrequencyPoints);

            PlannedAcquisition cramped = AcquisitionPlanner.Plan(
                new Capabilities { MaxSamplesPerBlock = 256 }, 1e9, 10e6, 0.0);
            Assert.Equal(201, cramped.FrequencyPoints);
            Assert.True(cramped.Coerced);
        }

        [Fact]
        public void ItRefusesCapabilitiesOfNull()
        {
            Assert.Throws<ArgumentNullException>(
                () => AcquisitionPlanner.MaximumPointsFor(null, AnalysisPath.ComplexZoom));
            Assert.Throws<ArgumentNullException>(
                () => AcquisitionPlanner.Plan(null, 1e9, 10e6, 801, 0.0, AnalysisPath.ComplexZoom));
        }

        /// <summary>Capabilities whose limits are set per test, resembling no particular instrument.</summary>
        private sealed class Capabilities : IFrontEndCapabilities
        {
            private static readonly IReadOnlyList<TriggerStyle> Styles =
                new List<TriggerStyle> { TriggerStyle.Immediate }.AsReadOnly();

            public int MaxSamplesPerBlock { get; set; } = 1 << 16;

            public long MaxCaptureSamples { get; set; } = 1L << 32;

            public FrequencyRange CenterFrequencyRange => new FrequencyRange(0.0, 26.5e9);
            public double MaxSpanHz => 40e6;
            public double MinSpanHz => 1.0;
            public double MaxSampleRateHz => 51.2e6;
            public bool SupportsBasebandIq => true;
            public int ChannelCount => 1;
            public bool SupportsPhaseCoherentChannels => false;
            public IReadOnlyList<TriggerStyle> TriggerStyles => Styles;
            public AmplitudeRange ReferenceLevelRange => new AmplitudeRange(-100.0, 30.0);
            public bool SupportsExternalRef => false;
        }
    }
}
