using System;
using System.Linq;
using OpenVSA.Dsp.Spectrum;
using OpenVSA.Dsp.Windowing;
using OpenVSA.Measurement.Channels;
using OpenVSA.Measurement.Limits;
using Xunit;
using Xunit.Abstractions;

namespace OpenVSA.Measurement.Tests
{
    /// <summary>
    /// <c>REQ-CHM-003</c>: a mask of several segments, each evaluated against its own limit, by the
    /// <c>REQ-LIM-001</c> engine rather than by a second implementation of it.
    /// </summary>
    public class EmissionMaskTests
    {
        private const double CarrierHz = 1e9;
        private const double BinHz = 20e3;

        private readonly ITestOutputHelper _output;

        public EmissionMaskTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void EachSegmentIsEvaluatedAgainstItsOwnLimit()
        {
            // The criterion: a signal that passes in one segment and fails in the adjacent one.
            // The shoulder at -35 dBc clears the near segment's -30 and breaches the far
            // segment's -50, so a mask that applied one limit everywhere fails whichever way it
            // picked.
            EmissionMask mask = Mask();

            // The carrier stops where the mask starts. A bin exactly on a segment's inner edge is
            // inside that segment and tested against it, so a synthetic carrier that ran up to and
            // including 2 MHz would be measuring itself against the mask - which is a property of
            // the trace, not of the measurement.
            SpectrumFrame trace = Trace(offset =>
            {
                double f = Math.Abs(offset);

                if (f < 2e6)
                {
                    return -20.0;
                }

                if (f < 5e6)
                {
                    return -55.0;    // -35 dBc against a -20 dBm carrier bin: inside -30 dBc.
                }

                return -60.0;        // -40 dBc: outside the far segment's -50 dBc.
            });

            EmissionMaskResult result = mask.Evaluate(trace, CarrierHz, -20.0);

            _output.WriteLine(result.ToString());

            Assert.False(result.Passed);
            Assert.Contains("Far", result.OffendingSegment);

            // And the near segment passed on its own terms.
            LimitLineResult near = result.LimitResult.Lines
                .Single(l => l.Line.Name == "Near upper");

            Assert.True(near.Passed);
            Assert.True(near.TestedPoints > 0);
        }

        [Fact]
        public void TheFailureNamesTheSegmentAndTheSide()
        {
            // "The reported failure naming the offending segment." Each segment is its own limit
            // line for exactly this reason: a mask built as one line with gaps would evaluate to
            // the same verdict and be able to report only that the mask failed.
            EmissionMask mask = Mask();

            // A breach on the upper side only, so a report that named the wrong side would fail.
            SpectrumFrame trace = Trace(offset =>
            {
                if (Math.Abs(offset) < 2e6)
                {
                    return -20.0;
                }

                return offset > 5e6 && offset <= 9e6 ? -55.0 : -100.0;
            });

            EmissionMaskResult result = mask.Evaluate(trace, CarrierHz, -20.0);

            Assert.False(result.Passed);
            Assert.Equal("Far upper", result.OffendingSegment);
            Assert.True(result.WorstHz > CarrierHz);

            // The lower side of the same segment is fine, which is what says the two are
            // independent rather than mirrored after the fact.
            Assert.True(
                result.LimitResult.Lines.Single(l => l.Line.Name == "Far lower").Passed);
        }

        [Fact]
        public void TheMeasurementRunsThroughTheLimitEngineAndNotAroundIt()
        {
            // The criterion asks for the shared code path to be asserted, "since a second
            // implementation is where the Upper/Lower inversion returns". Two assertions here: the
            // mask hands back the engine's own result type, and building the same test by hand and
            // running it through the engine gives an identical verdict line for line.
            EmissionMask mask = Mask();
            SpectrumFrame trace = Trace(offset => Math.Abs(offset) <= 2e6 ? -20.0 : -70.0);

            EmissionMaskResult viaMask = mask.Evaluate(trace, CarrierHz, -20.0);

            LimitTest built = mask.ToLimitTest(CarrierHz, -20.0);
            LimitTestResult viaEngine = built.Evaluate(trace);

            Assert.Equal(viaEngine.Passed, viaMask.LimitResult.Passed);
            Assert.Equal(viaEngine.Lines.Count, viaMask.LimitResult.Lines.Count);

            for (int i = 0; i < viaEngine.Lines.Count; i++)
            {
                Assert.Equal(viaEngine.Lines[i].Line.Name, viaMask.LimitResult.Lines[i].Line.Name);
                Assert.Equal(viaEngine.Lines[i].Passed, viaMask.LimitResult.Lines[i].Passed);
                Assert.Equal(
                    viaEngine.Lines[i].WorstMarginDb, viaMask.LimitResult.Lines[i].WorstMarginDb, 9);
                Assert.Equal(
                    viaEngine.Lines[i].TestedPoints, viaMask.LimitResult.Lines[i].TestedPoints);
            }
        }

        [Fact]
        public void EverySegmentBecomesTwoNamedLimitLines()
        {
            LimitTest built = Mask().ToLimitTest(CarrierHz, -20.0);

            Assert.Equal(4, built.Lines.Count);
            Assert.Equal(
                new[] { "Near lower", "Near upper", "Far lower", "Far upper" },
                built.Lines.Select(l => l.Name));

            // Every one is an Upper-side line: a mask says "stay below", on both sides.
            Assert.All(built.Lines, l => Assert.Equal(LimitSide.Upper, l.Side));
        }

        [Fact]
        public void RelativeSegmentsFollowTheCarrierPower()
        {
            // A mask written in dBc is a statement about spectral regrowth, so raising the carrier
            // raises the whole mask with it and the same shoulder still passes.
            EmissionMask mask = Mask();

            LimitTest low = mask.ToLimitTest(CarrierHz, -20.0);
            LimitTest high = mask.ToLimitTest(CarrierHz, -10.0);

            Assert.Equal(-50.0, low.Lines[0].LimitAt(CarrierHz - 3e6), 9);
            Assert.Equal(-40.0, high.Lines[0].LimitAt(CarrierHz - 3e6), 9);
        }

        [Fact]
        public void AnAbsoluteSegmentIgnoresTheCarrierPower()
        {
            var mask = new EmissionMask("Absolute")
                .Add(new EmissionMaskSegment("Floor", 1e6, 5e6, -70.0, MaskReference.Absolute));

            Assert.Equal(
                -70.0, mask.ToLimitTest(CarrierHz, -20.0).Lines[0].LimitAt(CarrierHz - 3e6), 9);
            Assert.Equal(
                -70.0, mask.ToLimitTest(CarrierHz, 10.0).Lines[0].LimitAt(CarrierHz - 3e6), 9);
        }

        [Fact]
        public void ASlopingSegmentInterpolatesBetweenItsEdges()
        {
            var mask = new EmissionMask("Sloped")
                .Add(new EmissionMaskSegment("Skirt", 1e6, 3e6, -30.0, -50.0));

            LimitTest built = mask.ToLimitTest(CarrierHz, 0.0);
            LimitLine upper = built.Lines.Single(l => l.Name == "Skirt upper");

            Assert.Equal(-30.0, upper.LimitAt(CarrierHz + 1e6), 6);
            Assert.Equal(-40.0, upper.LimitAt(CarrierHz + 2e6), 6);
            Assert.Equal(-50.0, upper.LimitAt(CarrierHz + 3e6), 6);

            // And the lower side is the mirror image, not a copy.
            LimitLine lower = built.Lines.Single(l => l.Name == "Skirt lower");

            Assert.Equal(-30.0, lower.LimitAt(CarrierHz - 1e6), 6);
            Assert.Equal(-50.0, lower.LimitAt(CarrierHz - 3e6), 6);
        }

        [Fact]
        public void NothingIsTestedBetweenSegments()
        {
            // The gap between the near segment's outer edge and the far segment's inner edge is
            // not covered by either, so a spur there is not a mask failure. That is a property of
            // the mask as written rather than an oversight, and it comes free from the segments
            // being separate lines.
            var mask = new EmissionMask("Gapped")
                .Add(new EmissionMaskSegment("Near", 1e6, 2e6, -30.0))
                .Add(new EmissionMaskSegment("Far", 4e6, 6e6, -50.0));

            SpectrumFrame trace = Trace(offset =>
                Math.Abs(offset - 3e6) < 1e5 ? -10.0 : -120.0);

            Assert.True(mask.Evaluate(trace, CarrierHz, 0.0).Passed);
        }

        [Fact]
        public void AMarginTightensTheMaskRatherThanLooseningIt()
        {
            // REQ-LIM-001's rule, inherited rather than restated: the margin is applied on the
            // pass side, so it makes the mask harder to pass.
            EmissionMask mask = Mask();

            SpectrumFrame trace = Trace(offset =>
            {
                if (Math.Abs(offset) < 2e6)
                {
                    return -20.0;
                }

                // Two dB inside the far segment's -50 dBc limit.
                return Math.Abs(offset) > 5e6 ? -72.0 : -100.0;
            });

            Assert.True(mask.Evaluate(trace, CarrierHz, -20.0).Passed);

            mask.MarginDb = 5.0;

            EmissionMaskResult tightened = mask.Evaluate(trace, CarrierHz, -20.0);

            Assert.False(tightened.Passed);
            Assert.Contains("Far", tightened.OffendingSegment);
        }

        [Fact]
        public void TheCarrierPowerCanBeMeasuredFromTheSameTrace()
        {
            // The usual case: a mask's relative limits refer to the carrier power of the very
            // signal being tested, so measuring it separately is a chance for the two to be of
            // different things.
            SpectrumFrame trace = Trace(offset => Math.Abs(offset) < 2e6 ? -50.0 : -120.0);

            ChannelDefinition carrier = ChannelDefinition.Rectangular("Carrier", 0.0, 4e6);

            EmissionMaskResult measured = Mask().Evaluate(trace, carrier, CarrierHz);

            double expected = BandMeasurements
                .Power(trace, CarrierHz - 2e6, CarrierHz + 2e6).TotalDbm;

            Assert.Equal(expected, measured.CarrierPowerDbm, 9);
            Assert.True(measured.Passed);
        }

        [Fact]
        public void AnEmptyMaskTestsNothingAndSaysSo()
        {
            EmissionMaskResult result =
                new EmissionMask("Empty").Evaluate(Trace(offset => -50.0), CarrierHz, -20.0);

            Assert.True(result.Passed);
            Assert.Null(result.OffendingSegment);
            Assert.Contains("nothing tested", result.ToString());
        }

        [Fact]
        public void ASegmentNeedsANameBecauseAFailureIsReportedAgainstIt()
        {
            ArgumentException error = Assert.Throws<ArgumentException>(
                () => new EmissionMaskSegment(string.Empty, 1e6, 2e6, -30.0));

            Assert.Contains("reported against", error.Message);
        }

        [Theory]
        [InlineData(-1e6, 2e6)]
        [InlineData(2e6, 2e6)]
        [InlineData(3e6, 2e6)]
        [InlineData(double.NaN, 2e6)]
        public void ASegmentWithNoInteriorIsRefused(double startHz, double stopHz)
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => new EmissionMaskSegment("S", startHz, stopHz, -30.0));
        }

        [Fact]
        public void ACarrierPowerThatIsNotFiniteIsRefusedRatherThanReferencedTo()
        {
            ArgumentOutOfRangeException error = Assert.Throws<ArgumentOutOfRangeException>(
                () => Mask().ToLimitTest(CarrierHz, double.NaN));

            Assert.Contains("nothing to reference", error.Message);
        }

        [Fact]
        public void MissingArgumentsAreRefused()
        {
            Assert.Throws<ArgumentException>(() => new EmissionMask(null));
            Assert.Throws<ArgumentNullException>(() => new EmissionMask("M").Add(null));
            Assert.Throws<ArgumentNullException>(
                () => Mask().Evaluate(null, CarrierHz, -20.0));
            Assert.Throws<ArgumentNullException>(
                () => Mask().Evaluate(Trace(o => -50.0), null, CarrierHz));
        }

        /// <summary>
        /// A two-segment mask: −30 dBc from 2 to 5 MHz out, −50 dBc from 5 to 9 MHz.
        /// </summary>
        private static EmissionMask Mask() =>
            new EmissionMask("Emission mask")
                .Add(new EmissionMaskSegment("Near", 2e6, 5e6, -30.0))
                .Add(new EmissionMaskSegment("Far", 5e6, 9e6, -50.0));

        private static SpectrumFrame Trace(Func<double, double> levelAtOffset)
        {
            const int points = 1201;
            var levels = new float[points];
            double startHz = CarrierHz - (points - 1) / 2.0 * BinHz;

            for (int i = 0; i < points; i++)
            {
                levels[i] = (float)levelAtOffset(startHz + i * BinHz - CarrierHz);
            }

            return SpectrumFrame.FromLevels(levels, startHz, BinHz, WindowType.Uniform, 1.0);
        }
    }
}
