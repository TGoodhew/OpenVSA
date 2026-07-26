using System;
using OpenVSA.Dsp.Spectrum;
using OpenVSA.Dsp.Windowing;
using OpenVSA.Measurement.Channels;
using Xunit;
using Xunit.Abstractions;

namespace OpenVSA.Measurement.Tests
{
    /// <summary>
    /// <c>REQ-CHM-001</c>: adjacent channel power, per offset, upper and lower, through a
    /// configurable filter.
    /// </summary>
    /// <remarks>
    /// The traces here are built as flat power densities rather than as modulated signals, because
    /// every one of the requirement's criteria is a statement about integrated power over a stated
    /// bandwidth — and a flat density makes each of them a closed-form number rather than something
    /// to be estimated from a simulation.
    /// </remarks>
    public class AcpTests
    {
        private const double CarrierHz = 1e9;
        private const double BinHz = 10e3;

        private readonly ITestOutputHelper _output;

        public AcpTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void AKnownRatioIsReportedPerOffsetAndPerSide()
        {
            // The criterion, with asymmetric injection: -45 dBc below and -52 dBc above, so a
            // measurement that swapped the sides or averaged them fails rather than passing by
            // symmetry.
            const double carrierDensity = -60.0;
            const double lowerDensity = -105.0;
            const double upperDensity = -112.0;

            SpectrumFrame trace = Trace(density =>
            {
                double offset = density;

                if (Math.Abs(offset) <= 2e6)
                {
                    return carrierDensity;
                }

                if (offset >= -7e6 && offset <= -3e6)
                {
                    return lowerDensity;
                }

                if (offset >= 3e6 && offset <= 7e6)
                {
                    return upperDensity;
                }

                return -200.0;
            });

            var acp = new AcpMeasurement(ChannelDefinition.Rectangular("Carrier", 0.0, 4e6))
                .Add(ChannelDefinition.Rectangular("5 MHz", 5e6, 4e6));

            AcpResult result = acp.Measure(trace, CarrierHz);

            ChannelPower lower = result.Find("5 MHz", ChannelSide.Lower);
            ChannelPower upper = result.Find("5 MHz", ChannelSide.Upper);

            _output.WriteLine(
                "carrier " + result.Carrier.AbsoluteDbm.ToString("F3") + " dBm, lower " +
                lower.RelativeDb.ToString("F3") + " dBc, upper " +
                upper.RelativeDb.ToString("F3") + " dBc");

            // Equal bandwidths, so the ratio is the difference of the densities exactly.
            Assert.Equal(lowerDensity - carrierDensity, lower.RelativeDb, 1);
            Assert.Equal(upperDensity - carrierDensity, upper.RelativeDb, 1);

            Assert.True(
                Math.Abs(lower.RelativeDb - (lowerDensity - carrierDensity)) <= 0.2,
                "lower read " + lower.RelativeDb + " dBc.");
            Assert.True(
                Math.Abs(upper.RelativeDb - (upperDensity - carrierDensity)) <= 0.2,
                "upper read " + upper.RelativeDb + " dBc.");

            // And they are distinguished, not merely both present.
            Assert.True(lower.RelativeDb > upper.RelativeDb);
            Assert.Equal(CarrierHz - 5e6, lower.CentreHz, 3);
            Assert.Equal(CarrierHz + 5e6, upper.CentreHz, 3);
        }

        [Fact]
        public void AbsolutePowerAgreesWithTheBandPowerOverTheSameBandwidth()
        {
            // The criterion, to 0.1 dB. It holds because both go through the same integration in
            // the DSP layer - a second loop here would drift apart at the band edges, where which
            // bins are inside is a judgement call.
            SpectrumFrame trace = Trace(offset => Math.Abs(offset) <= 2e6 ? -60.0 : -200.0);

            var acp = new AcpMeasurement(ChannelDefinition.Rectangular("Carrier", 0.0, 4e6));
            AcpResult result = acp.Measure(trace, CarrierHz);

            BandPower band = BandMeasurements.Power(trace, CarrierHz - 2e6, CarrierHz + 2e6);

            Assert.Equal(band.TotalDbm, result.Carrier.AbsoluteDbm, 9);
            Assert.True(Math.Abs(band.TotalDbm - result.Carrier.AbsoluteDbm) <= 0.1);
        }

        [Fact]
        public void ARootRaisedCosineChannelReadsLowerByExactlyItsNoiseBandwidth()
        {
            // The last criterion: changing the filter shape "changes the result in the direction
            // and by the amount the filter's known noise bandwidth predicts".
            //
            // An RRC filter's noise bandwidth is exactly its symbol rate, whatever the roll-off -
            // that is the defining property of the raised-cosine family, since |H_rrc|^2 is the
            // raised-cosine response and its skirts are antisymmetric about the half-amplitude
            // point. Against a flat density it must therefore read 10*log10(Rs / B) relative to a
            // rectangular integration over the same span B = (1 + a) * Rs, which is -10*log10(1+a).
            const double symbolRate = 3.84e6;
            const double rollOff = 0.22;

            double spanHz = (1.0 + rollOff) * symbolRate;
            double predictedDb = -10.0 * Math.Log10(1.0 + rollOff);

            SpectrumFrame trace = Trace(offset => -60.0);

            ChannelDefinition shaped =
                ChannelDefinition.RootRaisedCosine("Carrier", 0.0, symbolRate, rollOff);
            ChannelDefinition flat =
                ChannelDefinition.Rectangular("Carrier", 0.0, spanHz);

            Assert.Equal(spanHz, shaped.IntegrationBandwidthHz, 3);
            Assert.Equal(symbolRate, shaped.NoiseBandwidthHz, 3);

            double shapedDbm = new AcpMeasurement(shaped).Measure(trace, CarrierHz)
                .Carrier.AbsoluteDbm;
            double flatDbm = new AcpMeasurement(flat).Measure(trace, CarrierHz)
                .Carrier.AbsoluteDbm;

            double measured = shapedDbm - flatDbm;

            _output.WriteLine(
                "RRC " + shapedDbm.ToString("F4") + " dBm, rectangular " + flatDbm.ToString("F4") +
                " dBm, difference " + measured.ToString("F4") + " dB against a predicted " +
                predictedDb.ToString("F4") + " dB");

            // In the direction predicted...
            Assert.True(measured < 0.0, "the shaped filter read higher, not lower.");

            // ...and by the amount. The bin grid is what stops this being exact: the filter is
            // sampled at 10 kHz spacing across a 4.68 MHz span.
            Assert.True(
                Math.Abs(measured - predictedDb) <= 0.02,
                "read " + measured + " dB against a predicted " + predictedDb + " dB.");
        }

        [Fact]
        public void TheNoiseBandwidthOfARootRaisedCosineIsTheSymbolRateAtEveryRollOff()
        {
            // Stated as a property of the definition, because it is the number every prediction
            // about this filter rests on, and it is the one that surprises people.
            foreach (double rollOff in new[] { 0.0, 0.1, 0.22, 0.35, 0.5, 1.0 })
            {
                ChannelDefinition channel =
                    ChannelDefinition.RootRaisedCosine("C", 0.0, 1e6, rollOff);

                Assert.Equal(1e6, channel.NoiseBandwidthHz, 6);
                Assert.Equal((1.0 + rollOff) * 1e6, channel.IntegrationBandwidthHz, 6);
            }
        }

        [Fact]
        public void TheShapeIntegratesToItsNoiseBandwidth()
        {
            // The claim behind the whole test above, checked directly against the response rather
            // than through a measurement: the area under |H(f)|^2 is the symbol rate.
            const double symbolRate = 1e6;

            foreach (double rollOff in new[] { 0.1, 0.22, 0.5, 1.0 })
            {
                ChannelDefinition channel =
                    ChannelDefinition.RootRaisedCosine("C", 0.0, symbolRate, rollOff);

                const int steps = 200001;
                double span = channel.IntegrationBandwidthHz;
                double step = span / (steps - 1);
                double area = 0.0;

                for (int i = 0; i < steps; i++)
                {
                    double f = -span / 2.0 + i * step;
                    double weight = i == 0 || i == steps - 1 ? 0.5 : 1.0;

                    area += weight * channel.PowerResponseAt(f) * step;
                }

                Assert.True(
                    Math.Abs(area - symbolRate) / symbolRate < 1e-4,
                    "roll-off " + rollOff + " integrated to " + area + ", not " + symbolRate + ".");
            }
        }

        [Fact]
        public void AZeroRollOffRootRaisedCosineIsARectangleOfTheSymbolRate()
        {
            // The degenerate case, worth pinning: with no skirt the filter is a brick wall one
            // symbol rate wide, so the two shapes must agree exactly.
            SpectrumFrame trace = Trace(offset => -60.0);

            double shaped = new AcpMeasurement(
                    ChannelDefinition.RootRaisedCosine("C", 0.0, 4e6, 0.0))
                .Measure(trace, CarrierHz).Carrier.AbsoluteDbm;

            double flat = new AcpMeasurement(ChannelDefinition.Rectangular("C", 0.0, 4e6))
                .Measure(trace, CarrierHz).Carrier.AbsoluteDbm;

            Assert.Equal(flat, shaped, 9);
        }

        [Fact]
        public void EachOffsetMayHaveItsOwnBandwidthAndShape()
        {
            // "Configurable carrier and offset channel definitions" - a standard that measures its
            // carrier through a matched filter often measures its adjacent channels through a
            // different one, and a single bandwidth applied to everything makes that unmeasurable.
            SpectrumFrame trace = Trace(offset => -60.0);

            var acp = new AcpMeasurement(
                    ChannelDefinition.RootRaisedCosine("Carrier", 0.0, 3.84e6, 0.22))
                .Add(ChannelDefinition.Rectangular("Near", 5e6, 1e6))
                .Add(ChannelDefinition.RootRaisedCosine("Far", 10e6, 3.84e6, 0.22));

            AcpResult result = acp.Measure(trace, CarrierHz);

            Assert.Equal(4, result.Offsets.Count);

            // A 1 MHz rectangular channel collects 10*log10(3.84) dB less than a 3.84 MHz RRC one
            // over the same flat density.
            double near = result.Find("Near", ChannelSide.Upper).AbsoluteDbm;
            double far = result.Find("Far", ChannelSide.Upper).AbsoluteDbm;

            Assert.True(
                Math.Abs((far - near) - 10.0 * Math.Log10(3.84)) < 0.05,
                "the two shapes differed by " + (far - near) + " dB.");
        }

        [Fact]
        public void TheCarrierIsZeroRelativeToItself()
        {
            SpectrumFrame trace = Trace(offset => -60.0);

            AcpResult result = new AcpMeasurement(
                ChannelDefinition.Rectangular("Carrier", 0.0, 4e6)).Measure(trace, CarrierHz);

            Assert.Equal(0.0, result.Carrier.RelativeDb, 12);
            Assert.Equal(ChannelSide.Carrier, result.Carrier.Side);
        }

        [Fact]
        public void OffsetsAreReportedLowerThenUpperInDefinitionOrder()
        {
            SpectrumFrame trace = Trace(offset => -60.0);

            AcpResult result = new AcpMeasurement(ChannelDefinition.Rectangular("C", 0.0, 1e6))
                .Add(ChannelDefinition.Rectangular("First", 2e6, 1e6))
                .Add(ChannelDefinition.Rectangular("Second", 4e6, 1e6))
                .Measure(trace, CarrierHz);

            Assert.Equal(ChannelSide.Lower, result.Offsets[0].Side);
            Assert.Equal("First", result.Offsets[0].Definition.Name);
            Assert.Equal(ChannelSide.Upper, result.Offsets[1].Side);
            Assert.Equal("Second", result.Offsets[2].Definition.Name);
        }

        [Fact]
        public void ASignOnAnOffsetIsIgnoredBecauseBothSidesAreMeasured()
        {
            ChannelDefinition negative = ChannelDefinition.Rectangular("C", -5e6, 1e6);

            Assert.Equal(5e6, negative.OffsetHz, 3);
        }

        [Fact]
        public void AnOffsetChannelAtZeroOffsetIsRefused()
        {
            ArgumentException error = Assert.Throws<ArgumentException>(
                () => new AcpMeasurement(ChannelDefinition.Rectangular("C", 0.0, 1e6))
                    .Add(ChannelDefinition.Rectangular("Nowhere", 0.0, 1e6)));

            Assert.Contains("carrier channel", error.Message);
        }

        [Theory]
        [InlineData(0.0)]
        [InlineData(-1e6)]
        [InlineData(double.NaN)]
        [InlineData(double.PositiveInfinity)]
        public void ABandwidthThatIsNotPositiveAndFiniteIsRefused(double bandwidthHz)
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => ChannelDefinition.Rectangular("C", 0.0, bandwidthHz));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => ChannelDefinition.RootRaisedCosine("C", 0.0, bandwidthHz, 0.22));
        }

        [Theory]
        [InlineData(-0.01)]
        [InlineData(1.01)]
        [InlineData(double.NaN)]
        public void ARollOffOutsideZeroToOneIsRefused(double rollOff)
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => ChannelDefinition.RootRaisedCosine("C", 0.0, 1e6, rollOff));
        }

        [Fact]
        public void AChannelWithoutANameIsRefused()
        {
            Assert.Throws<ArgumentException>(
                () => ChannelDefinition.Rectangular(string.Empty, 0.0, 1e6));
            Assert.Throws<ArgumentException>(
                () => ChannelDefinition.RootRaisedCosine(null, 0.0, 1e6, 0.22));
        }

        [Fact]
        public void MissingArgumentsAreRefused()
        {
            Assert.Throws<ArgumentNullException>(() => new AcpMeasurement(null));
            Assert.Throws<ArgumentNullException>(
                () => new AcpMeasurement(ChannelDefinition.Rectangular("C", 0.0, 1e6)).Add(null));
            Assert.Throws<ArgumentNullException>(
                () => new AcpMeasurement(ChannelDefinition.Rectangular("C", 0.0, 1e6))
                    .Measure(null, CarrierHz));
        }

        /// <summary>
        /// A spectrum whose density at each point is given by a function of the offset from the
        /// carrier, in dBm per bin.
        /// </summary>
        private static SpectrumFrame Trace(Func<double, double> densityAtOffset)
        {
            const int points = 3001;
            var levels = new float[points];
            double startHz = CarrierHz - (points - 1) / 2.0 * BinHz;

            for (int i = 0; i < points; i++)
            {
                levels[i] = (float)densityAtOffset(startHz + i * BinHz - CarrierHz);
            }

            // A uniform window, so its noise bandwidth is one bin and a level per bin integrates
            // to the sum of the bins with no correction to reason about.
            return SpectrumFrame.FromLevels(levels, startHz, BinHz, WindowType.Uniform, 1.0);
        }
    }
}
