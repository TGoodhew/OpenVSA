using System;
using OpenVSA.Dsp.Windowing;
using Xunit;

namespace OpenVSA.Dsp.Tests
{
    /// <summary>
    /// The channel filter shapes that replace the analysis window in zero span
    /// (<c>REQ-DSP-012</c>).
    /// </summary>
    /// <remarks>
    /// Against closed forms throughout, per <c>REQ-TST-001</c>: the 3 dB point is a definition, the
    /// noise bandwidth is an integral with an exact answer, and both are checked against the response
    /// itself rather than against a stored figure.
    /// </remarks>
    public class ChannelFilterTests
    {
        private const double BandwidthHz = 100e3;

        [Fact]
        public void TheGaussianIsThreeDecibelsDownAtHalfItsBandwidth()
        {
            // The definition of the stated bandwidth, and it must be exact rather than close: it is
            // what makes "3 dB bandwidth" the name of the parameter.
            double atEdge = ChannelFilters.PowerResponseAt(
                ChannelFilterType.Gaussian, BandwidthHz / 2.0, BandwidthHz);

            Assert.Equal(0.5, atEdge, 12);
            Assert.Equal(-3.0103, 10.0 * Math.Log10(atEdge), 4);

            // And unity at the centre, or the reading through it would not be the channel's power.
            Assert.Equal(
                1.0,
                ChannelFilters.PowerResponseAt(ChannelFilterType.Gaussian, 0.0, BandwidthHz),
                12);
        }

        [Fact]
        public void TheGaussianIsSymmetricAndFallsMonotonicallyWithNoSidelobes()
        {
            double previous = double.PositiveInfinity;

            for (int step = 0; step <= 400; step++)
            {
                double offset = step * BandwidthHz / 100.0;

                double above = ChannelFilters.PowerResponseAt(
                    ChannelFilterType.Gaussian, offset, BandwidthHz);
                double below = ChannelFilters.PowerResponseAt(
                    ChannelFilterType.Gaussian, -offset, BandwidthHz);

                Assert.Equal(above, below, 15);

                // No sidelobe anywhere: a Gaussian's skirt never turns back up, which is the
                // property that makes leakage from a strong neighbour predictable rather than
                // dependent on exactly where the neighbour sits.
                Assert.True(
                    above <= previous,
                    "The response rose at " + offset + " Hz: " + above + " after " + previous + ".");

                previous = above;
            }

            // Far out it is negligible rather than merely small: 2^-36 at three bandwidths is the
            // figure the integration bound is chosen from.
            Assert.True(
                ChannelFilters.PowerResponseAt(
                    ChannelFilterType.Gaussian,
                    ChannelFilters.GaussianIntegrationBandwidths * BandwidthHz,
                    BandwidthHz) < 1e-8);
        }

        [Fact]
        public void TheGaussianNoiseBandwidthIsTheIntegralOfItsOwnResponse()
        {
            // The strong form of the check: the stated factor is compared with a numerical
            // integration of the response the filter actually applies, so the constant and the
            // response cannot drift apart. A test against a typed 1.0645 would pass with a
            // response that had stopped matching it.
            double step = BandwidthHz / 2000.0;
            double integral = 0.0;

            for (double f = -6.0 * BandwidthHz; f <= 6.0 * BandwidthHz; f += step)
            {
                integral += ChannelFilters.PowerResponseAt(
                    ChannelFilterType.Gaussian, f, BandwidthHz) * step;
            }

            double stated = ChannelFilters.NoiseBandwidthHz(
                ChannelFilterType.Gaussian, BandwidthHz);

            Assert.Equal(integral / BandwidthHz, stated / BandwidthHz, 4);

            // And it is the figure a swept analyser's documentation quotes for a Gaussian
            // resolution bandwidth, which is what makes a reading through it comparable with one.
            Assert.Equal(1.0645, ChannelFilters.GaussianNoiseBandwidthFactor, 4);
        }

        [Fact]
        public void UnshapedIsWiderThanAGaussianOfTheSameNominalWidth()
        {
            double gaussian = ChannelFilters.NoiseBandwidthHz(
                ChannelFilterType.Gaussian, BandwidthHz);
            double unshaped = ChannelFilters.NoiseBandwidthHz(
                ChannelFilterType.None, BandwidthHz);

            Assert.Equal(BandwidthHz, unshaped, 6);
            Assert.True(gaussian > unshaped);

            // 0.27 dB, and it is the only way the choice of shape is visible on anything but a
            // pure tone.
            Assert.Equal(0.2713, 10.0 * Math.Log10(gaussian / unshaped), 4);
        }

        [Fact]
        public void AnUnfilteredChannelCountsEverythingEqually()
        {
            // Flat, and the caller decides what band that is over: the anti-alias filter already
            // decided what arrived, and this function is not told the span.
            foreach (double offset in new[] { 0.0, BandwidthHz, 1000.0 * BandwidthHz })
            {
                Assert.Equal(
                    1.0,
                    ChannelFilters.PowerResponseAt(ChannelFilterType.None, offset, BandwidthHz),
                    12);
            }
        }

        [Fact]
        public void EveryShapeIsOfferedNamedAndParsesBack()
        {
            // REQ-DSP-012 names two shapes, and the criterion is that the control offers them.
            Assert.Equal(
                new[] { ChannelFilterType.Gaussian, ChannelFilterType.None },
                ChannelFilters.All);

            foreach (ChannelFilterType filter in ChannelFilters.All)
            {
                string name = ChannelFilters.Describe(filter);

                Assert.False(string.IsNullOrWhiteSpace(name));

                ChannelFilterType parsed;

                Assert.True(ChannelFilters.TryParse(name, out parsed));
                Assert.Equal(filter, parsed);
            }

            // "None" alone would claim the band is unfiltered, and it is not: the front end's
            // anti-alias filter is still there and still decides what arrived.
            Assert.Contains("anti-alias", ChannelFilters.Describe(ChannelFilterType.None));
            Assert.Equal("Gaussian", ChannelFilters.Describe(ChannelFilterType.Gaussian));
        }

        [Fact]
        public void NothingElseIsAcceptedAsAShapeOrABandwidth()
        {
            var invented = (ChannelFilterType)99;

            Assert.Throws<ArgumentOutOfRangeException>(() => ChannelFilters.Describe(invented));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => ChannelFilters.PowerResponseAt(invented, 0.0, BandwidthHz));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => ChannelFilters.NoiseBandwidthHz(invented, BandwidthHz));

            // A zero or negative bandwidth is a filter with no shape at all, and returning
            // something for it would put a division by zero into a measurement.
            foreach (double bad in new[] { 0.0, -1.0, double.PositiveInfinity })
            {
                Assert.Throws<ArgumentOutOfRangeException>(
                    () => ChannelFilters.PowerResponseAt(
                        ChannelFilterType.Gaussian, 0.0, bad));
            }

            ChannelFilterType parsed;

            Assert.False(ChannelFilters.TryParse(null, out parsed));
            Assert.False(ChannelFilters.TryParse("   ", out parsed));
            Assert.False(ChannelFilters.TryParse("Hann", out parsed));
        }
    }
}
