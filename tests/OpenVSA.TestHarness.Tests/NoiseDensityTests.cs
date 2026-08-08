using System;
using OpenVSA.TestHarness;
using Xunit;

namespace OpenVSA.TestHarness.Tests
{
    /// <summary>
    /// Power spectral density from a spectrum (issue #393, <c>REQ-DSP-011</c>).
    /// </summary>
    /// <remarks>
    /// No hardware. These pin the two things the bench cannot tell apart from a wrong analyser: the
    /// ENBW divisor and where the average is taken.
    /// </remarks>
    public class NoiseDensityTests
    {
        private const double CentreHz = 1.0e9;
        private const double SpanHz = 2.0e6;
        private const double BinHz = 2500.0;

        [Fact]
        public void AFlatSpectrumGivesItsOwnDensity()
        {
            // 801 bins of -50 dBm, 2.5 kHz apart, rectangular window. With ENBW 1 the density is
            // simply the bin level less 10*log10(binWidth).
            float[] flat = Flat(801, -50.0f);
            int used;

            double density = NoiseDensity.MeasureDbmPerHz(
                flat, Start(801), BinHz, CentreHz, SpanHz, 1.0, out used);

            Assert.Equal(-50.0 - (10.0 * Math.Log10(BinHz)), density, 6);
            Assert.True(used > 0);
        }

        [Fact]
        public void TheEnbwDivisorIsApplied()
        {
            // THE point of the scenario. A flat top spreads each bin over 3.8194 bins' width, so
            // omitting it overstates the density by 10*log10(3.8194) = 5.82 dB — and every tone in
            // the product still reads correctly while it does.
            float[] flat = Flat(801, -50.0f);
            int used;

            double withEnbw = NoiseDensity.MeasureDbmPerHz(
                flat, Start(801), BinHz, CentreHz, SpanHz, 3.8194, out used);

            double without = NoiseDensity.MeasureDbmPerHz(
                flat, Start(801), BinHz, CentreHz, SpanHz, 1.0, out used);

            Assert.Equal(10.0 * Math.Log10(3.8194), without - withEnbw, 6);
            Assert.Equal(5.8195, without - withEnbw, 3);
        }

        [Fact]
        public void TheAverageIsTakenInPowerNotInDecibels()
        {
            // Two bins, 20 dB apart. The power mean is 10*log10((1 + 0.01)/2) = -2.98 dBm; the
            // mean of the decibels is -10 dBm. A harness averaging in dB would be 7 dB low here
            // and about 2.5 dB low on real noise — stable enough to look like a calibration offset.
            var spectrum = new float[801];

            for (int index = 0; index < spectrum.Length; index++)
            {
                spectrum[index] = (index % 2 == 0) ? 0.0f : -20.0f;
            }

            int used;

            double density = NoiseDensity.MeasureDbmPerHz(
                spectrum, Start(801), BinHz, CentreHz, SpanHz, 1.0, out used);

            double powerAnswer = (10.0 * Math.Log10((1.0 + 0.01) / 2.0)) - (10.0 * Math.Log10(BinHz));
            double decibelAnswer = -10.0 - (10.0 * Math.Log10(BinHz));

            // Asserted as "far nearer one than the other" rather than to n decimal places: the
            // usable window need not hold exactly equal numbers of the two levels, and pinning the
            // exact figure would only re-implement the function in the test. The two candidate
            // answers are 7 dB apart, so the distinction is not delicate.
            Assert.True(
                Math.Abs(density - powerAnswer) < 0.5,
                "Density " + density + " is not the power mean " + powerAnswer + ".");

            Assert.True(
                Math.Abs(density - decibelAnswer) > 5.0,
                "Density " + density + " is the mean of the decibels, which is 2.5 dB low on real " +
                "noise and looks like a calibration offset.");
        }

        [Fact]
        public void BinsBeyondTheRequestedSpanAreNotAveragedIn()
        {
            // The defect this was measured to have. The front end digitises at 1.5x its
            // information bandwidth, so the frame reaches past the span that was asked for and the
            // outer bins sit on the analysis filter's roll-off. Averaging them in reported the
            // roll-off as a density error, 2.58 dB low on the bench.
            //
            // Here the outer third of the frame is 30 dB down, as a roll-off would be. The answer
            // must be unchanged by it.
            int count = 1201;
            float[] withSkirts = Flat(count, -50.0f);
            double start = CentreHz - (count / 2 * BinHz);

            for (int index = 0; index < count; index++)
            {
                double offset = Math.Abs(start + (index * BinHz) - CentreHz);

                if (offset > SpanHz * 0.45)
                {
                    withSkirts[index] = -80.0f;
                }
            }

            int used;

            double density = NoiseDensity.MeasureDbmPerHz(
                withSkirts, start, BinHz, CentreHz, SpanHz, 1.0, out used);

            Assert.Equal(-50.0 - (10.0 * Math.Log10(BinHz)), density, 6);
        }

        [Fact]
        public void TheCentreSpurIsExcluded()
        {
            // A residual local-oscillator spur sits at the centre of a real receiver's analysis.
            // It is a tone, so it is far above the noise, and averaging it in raises the density by
            // an amount that depends on the span rather than on anything being measured.
            float[] withSpur = Flat(801, -50.0f);
            withSpur[400] = 0.0f;
            withSpur[399] = -10.0f;
            withSpur[401] = -10.0f;

            int used;

            double density = NoiseDensity.MeasureDbmPerHz(
                withSpur, Start(801), BinHz, CentreHz, SpanHz, 1.0, out used);

            Assert.Equal(-50.0 - (10.0 * Math.Log10(BinHz)), density, 6);
        }

        [Fact]
        public void NothingUsableGivesNotANumberRatherThanAPlausibleAnswer()
        {
            int used;

            Assert.True(double.IsNaN(NoiseDensity.MeasureDbmPerHz(
                null, 0.0, BinHz, CentreHz, SpanHz, 1.0, out used)));

            Assert.True(double.IsNaN(NoiseDensity.MeasureDbmPerHz(
                Flat(801, -50.0f), Start(801), 0.0, CentreHz, SpanHz, 1.0, out used)));

            // A span of zero leaves no bin inside the reach, and NaN is the honest answer: a
            // density computed from no bins would be reported as a measurement.
            Assert.True(double.IsNaN(NoiseDensity.MeasureDbmPerHz(
                Flat(801, -50.0f), Start(801), BinHz, CentreHz, 0.0, 1.0, out used)));

            Assert.Equal(0, used);
        }

        [Fact]
        public void ANotANumberBinIsSkippedRatherThanPoisoningTheMean()
        {
            float[] spectrum = Flat(801, -50.0f);
            spectrum[380] = float.NaN;
            spectrum[420] = float.NegativeInfinity;

            int used;

            double density = NoiseDensity.MeasureDbmPerHz(
                spectrum, Start(801), BinHz, CentreHz, SpanHz, 1.0, out used);

            Assert.Equal(-50.0 - (10.0 * Math.Log10(BinHz)), density, 6);
        }

        private static double Start(int count) => CentreHz - (count / 2 * BinHz);

        private static float[] Flat(int count, float levelDbm)
        {
            var spectrum = new float[count];

            for (int index = 0; index < count; index++)
            {
                spectrum[index] = levelDbm;
            }

            return spectrum;
        }
    }
}
