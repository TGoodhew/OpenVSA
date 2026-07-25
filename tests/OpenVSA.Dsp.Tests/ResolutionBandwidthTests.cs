using System;
using OpenVSA.Dsp.Spectrum;
using OpenVSA.Dsp.Windowing;
using Xunit;

namespace OpenVSA.Dsp.Tests
{
    /// <summary>
    /// <c>REQ-DSP-020</c>: <c>RBW = ENBW / T_rec</c>, enforced in both directions.
    /// </summary>
    public class ResolutionBandwidthTests
    {
        [Fact]
        public void TheWorkedExample_RbwToRecordLength()
        {
            // The requirement's own example: Hann, 100 kHz RBW -> 15 us record.
            Window hann = Window.Get(WindowType.Hann, 4096);

            Assert.Equal(15e-6, ResolutionBandwidth.RecordLengthFor(hann, 100e3), 12);
        }

        [Fact]
        public void TheWorkedExample_RecordLengthToRbw()
        {
            // And the other direction: Hann, 50 ms record -> 30 Hz RBW.
            Window hann = Window.Get(WindowType.Hann, 4096);

            Assert.Equal(30.0, ResolutionBandwidth.ForRecordLength(hann, 50e-3), 9);
        }

        [Fact]
        public void HannsNoiseBandwidthIsExactlyOneAndAHalfAtEveryLength()
        {
            // Both worked examples rest on this. ENBW is a property of the window's shape, not of
            // its length - which is why an Auto point count can be derived before the transform
            // size is known.
            foreach (int length in new[] { 64, 256, 4096, 65536 })
            {
                Assert.Equal(1.5, Window.Get(WindowType.Hann, length).Enbw, 12);
            }
        }

        [Fact]
        public void TheTwoDirectionsAreInverses()
        {
            foreach (WindowType type in Enum.GetValues(typeof(WindowType)))
            {
                Window window = Window.Get(type, 4096);
                double record = ResolutionBandwidth.RecordLengthFor(window, 12345.0);

                Assert.Equal(12345.0, ResolutionBandwidth.ForRecordLength(window, record), 6);
            }
        }

        [Fact]
        public void TheSpanFormAgreesWithTheRecordLengthForm()
        {
            // T_rec = (N_f - 1) / Span is REQ-ACQ-001's T_max, so the two forms are the same
            // relation. If they ever disagree, the axis and the RBW annotation are describing
            // different measurements.
            const double span = 10e6;
            const int points = 801;

            Window window = Window.Get(WindowType.FlatTop, 1024);
            double fromSpan = ResolutionBandwidth.ForSpan(window.Enbw, span, points);
            double fromRecord = ResolutionBandwidth.ForRecordLength(window, (points - 1) / span);

            Assert.Equal(fromRecord, fromSpan, 9);
        }

        [Fact]
        public void RbwIsNotTheBinSpacing()
        {
            // Under the default window it is nearly four times the spacing between displayed
            // points. Treating the two as the same is how a measurement claims resolution it does
            // not have.
            const double span = 10e6;
            const int points = 801;

            double binSpacing = span / (points - 1);
            double rbw = ResolutionBandwidth.ForSpan(
                Window.Get(WindowType.FlatTop, 1024).Enbw, span, points);

            Assert.Equal(3.8194 * binSpacing, rbw, 1);
            Assert.True(rbw > 3.0 * binSpacing);
        }

        [Fact]
        public void RequiredIntervalsIsTheInverseOfTheSpanForm()
        {
            const double span = 10e6;
            const double enbw = 1.5;

            double intervals = ResolutionBandwidth.RequiredIntervals(enbw, span, 30e3);

            Assert.Equal(
                30e3,
                ResolutionBandwidth.ForSpan(enbw, span, (int)Math.Round(intervals) + 1),
                6);
        }

        [Theory]
        [InlineData(0.0, 1.0)]
        [InlineData(-1.0, 1.0)]
        [InlineData(1.0, 0.0)]
        [InlineData(double.NaN, 1.0)]
        [InlineData(1.0, double.PositiveInfinity)]
        public void ValuesThatAreNotPositiveAndFiniteAreRefused(double enbw, double record)
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => ResolutionBandwidth.ForRecordLength(enbw, record));
        }

        [Fact]
        public void ItRefusesAWindowOfNull()
        {
            Assert.Throws<ArgumentNullException>(
                () => ResolutionBandwidth.ForRecordLength(null, 1e-3));
            Assert.Throws<ArgumentNullException>(
                () => ResolutionBandwidth.RecordLengthFor(null, 1e3));
        }
    }
}
