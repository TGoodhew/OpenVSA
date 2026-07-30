using System;
using OpenVSA.Dsp.Spectrum;
using OpenVSA.Dsp.Windowing;
using OpenVSA.Measurement;
using Xunit;

namespace OpenVSA.Measurement.Tests
{
    /// <summary>
    /// Zero-span operation: one channel's power, through the selected channel filter
    /// (<c>REQ-DSP-012</c>).
    /// </summary>
    /// <remarks>
    /// These are what stop the Channel Filter Shape control being a setting that is recorded and
    /// never applied. Two things have to be true of the reading: it must be centred on the channel,
    /// which a tone shows, and it must be shaped, which only a noise-like input shows.
    /// </remarks>
    public class ZeroSpanMeasurementTests
    {
        private const int Points = 801;
        private const double BinWidthHz = 1e3;
        private const double CentreHz = 1e9;
        private const double ChannelBandwidthHz = 50e3;

        [Fact]
        public void ACentredToneReadsItsLevelThroughEitherFilter()
        {
            SpectrumFrame frame = Frame(floorDbm: -140.0, toneDbm: -20.0, toneOffsetHz: 0.0);

            BandPower gaussian = ZeroSpanMeasurement.Power(
                frame, ChannelFilterType.Gaussian, ChannelBandwidthHz);
            BandPower unshaped = ZeroSpanMeasurement.Power(
                frame, ChannelFilterType.None, ChannelBandwidthHz);

            // A filter centred on the channel passes a tone at the channel centre untouched, so the
            // shape cannot change the answer. If it did, the filter would be off centre.
            Assert.Equal(-20.0, gaussian.TotalDbm, 2);
            Assert.Equal(-20.0, unshaped.TotalDbm, 2);
        }

        [Fact]
        public void AToneOutsideTheChannelIsRejectedByTheGaussianAndNotByNone()
        {
            // Four bandwidths out: past where the Gaussian is integrated at all, and well inside the
            // analysed span, which is what makes the two answers differ.
            SpectrumFrame frame = Frame(
                floorDbm: -140.0, toneDbm: -20.0, toneOffsetHz: 4.0 * ChannelBandwidthHz);

            BandPower gaussian = ZeroSpanMeasurement.Power(
                frame, ChannelFilterType.Gaussian, ChannelBandwidthHz);
            BandPower unshaped = ZeroSpanMeasurement.Power(
                frame, ChannelFilterType.None, ChannelBandwidthHz);

            // Unshaped counts everything that arrived, so it still reads the tone.
            Assert.Equal(-20.0, unshaped.TotalDbm, 2);

            // The Gaussian does not: this is the shape doing its job, and it is the discriminating
            // half of the pair. A reading that ignored the filter would give -20 dBm here too.
            Assert.True(
                gaussian.TotalDbm < -100.0,
                "The Gaussian channel read " + gaussian.TotalDbm +
                " dBm for a tone four bandwidths outside it.");
        }

        [Fact]
        public void NoiseReadsHigherUnshapedByTheRatioOfTheNoiseBandwidths()
        {
            // A flat floor and no tone: the only thing that can differ between the two readings is
            // how much bandwidth each filter accumulated.
            SpectrumFrame frame = Frame(floorDbm: -120.0, toneDbm: double.NaN, toneOffsetHz: 0.0);

            BandPower gaussian = ZeroSpanMeasurement.Power(
                frame, ChannelFilterType.Gaussian, ChannelBandwidthHz);
            BandPower unshaped = ZeroSpanMeasurement.Power(
                frame, ChannelFilterType.None, ChannelBandwidthHz);

            double gaussianNoiseBandwidth = ZeroSpanMeasurement.NoiseBandwidthHz(
                frame, ChannelFilterType.Gaussian, ChannelBandwidthHz);
            double unshapedNoiseBandwidth = ZeroSpanMeasurement.NoiseBandwidthHz(
                frame, ChannelFilterType.None, ChannelBandwidthHz);

            // Unshaped takes the whole analysed span; the Gaussian takes its own noise bandwidth.
            Assert.Equal(
                Math.Sqrt(Math.PI / (4.0 * Math.Log(2.0))) * ChannelBandwidthHz,
                gaussianNoiseBandwidth,
                6);
            Assert.Equal(BinWidthHz * (Points - 1), unshapedNoiseBandwidth, 0);

            double expectedDifferenceDb =
                10.0 * Math.Log10(unshapedNoiseBandwidth / gaussianNoiseBandwidth);

            Assert.Equal(
                expectedDifferenceDb, unshaped.TotalDbm - gaussian.TotalDbm, 1);
        }

        [Fact]
        public void TheGaussianReadingFollowsItsBandwidth()
        {
            SpectrumFrame frame = Frame(floorDbm: -120.0, toneDbm: double.NaN, toneOffsetHz: 0.0);

            BandPower narrow = ZeroSpanMeasurement.Power(
                frame, ChannelFilterType.Gaussian, ChannelBandwidthHz);
            BandPower wide = ZeroSpanMeasurement.Power(
                frame, ChannelFilterType.Gaussian, 4.0 * ChannelBandwidthHz);

            // Four times the bandwidth over a flat floor is 6 dB more noise power. A reading that
            // ignored the bandwidth would give the same answer twice.
            Assert.Equal(6.02, wide.TotalDbm - narrow.TotalDbm, 1);
        }

        [Fact]
        public void NothingIsMeasuredWithoutAFrameOrABandwidth()
        {
            SpectrumFrame frame = Frame(floorDbm: -120.0, toneDbm: -20.0, toneOffsetHz: 0.0);

            Assert.Throws<ArgumentNullException>(
                () => ZeroSpanMeasurement.Power(null, ChannelFilterType.Gaussian, ChannelBandwidthHz));

            foreach (double bad in new[] { 0.0, -1.0 })
            {
                Assert.Throws<ArgumentOutOfRangeException>(
                    () => ZeroSpanMeasurement.Power(frame, ChannelFilterType.Gaussian, bad));
            }
        }

        /// <summary>
        /// A frame with a flat floor and, optionally, a tone at an offset from the centre.
        /// </summary>
        private static SpectrumFrame Frame(double floorDbm, double toneDbm, double toneOffsetHz)
        {
            var levels = new float[Points];

            for (int i = 0; i < Points; i++)
            {
                levels[i] = (float)floorDbm;
            }

            if (!double.IsNaN(toneDbm))
            {
                int bin = (Points - 1) / 2 + (int)Math.Round(toneOffsetHz / BinWidthHz);

                Assert.InRange(bin, 0, Points - 1);

                levels[bin] = (float)toneDbm;
            }

            double startHz = CentreHz - BinWidthHz * (Points - 1) / 2.0;

            // Uniform, with an equivalent noise bandwidth of one bin. A band-power integration
            // divides by the window's noise bandwidth, because that is what turns bin power into the
            // power of a signal a real window has spread over several bins -- and a synthetic tone
            // that occupies exactly one bin is what a uniform window gives for an on-bin tone. Using
            // a flat-top ENBW here would make every reading 10·log10(3.8194) = 5.82 dB low, which is
            // an artefact of the test's own signal rather than anything the measurement does.
            return SpectrumFrame.FromLevels(
                levels, startHz, BinWidthHz, WindowType.Uniform, 1.0);
        }
    }
}
