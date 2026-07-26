using System;
using System.Linq;
using OpenVSA.Core;
using OpenVSA.Dsp.Spectrum;
using OpenVSA.Dsp.Windowing;
using Xunit;
using Xunit.Abstractions;

namespace OpenVSA.Dsp.Tests
{
    /// <summary>
    /// <c>REQ-MKR-003</c>: band power, occupied bandwidth and adjacent-channel power.
    /// </summary>
    public class BandMeasurementTests
    {
        private readonly ITestOutputHelper _output;

        public BandMeasurementTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Theory]
        [InlineData(WindowType.FlatTop)]
        [InlineData(WindowType.Hann)]
        [InlineData(WindowType.BlackmanHarris)]
        [InlineData(WindowType.Uniform)]
        public void BandPowerOverACwToneEqualsTheTonesPower(WindowType window)
        {
            // REQ-MKR-003's first criterion, to within 0.05 dB. It holds under every window
            // because the ENBW correction is exact for a bin-centred tone rather than an
            // approximation tuned to one window.
            const int length = 4096;
            const double sampleRate = 12.8e6;
            const double centre = 1e9;
            const double amplitude = 0.5;

            using (IqBlock block = Tone(length, sampleRate, centre, 1000, amplitude))
            {
                var computer = new SpectrumComputer(window, null, null);
                SpectrumFrame frame = computer.Compute(block);

                // A band wide enough to hold the whole mainlobe, however wide the window makes it.
                double toneHz = centre + 1000 * sampleRate / length;
                double half = 40.0 * frame.BinWidthHz;

                BandPower measured = BandMeasurements.Power(frame, toneHz - half, toneHz + half);
                double expected = ExpectedDbm(amplitude);

                _output.WriteLine(
                    window + ": band " + measured.TotalDbm.ToString("F4") +
                    " dBm against tone " + expected.ToString("F4") + " dBm");

                Assert.True(
                    Math.Abs(measured.TotalDbm - expected) <= 0.05,
                    window + ": band power read " + measured.TotalDbm.ToString("F4") +
                    " dBm against a tone of " + expected.ToString("F4") + " dBm.");
            }
        }

        [Fact]
        public void BandPowerAgreesWithTheMarkerReadingOfTheSameTone()
        {
            // Two routes to one number: integrating the band, and reading the peak bin. They must
            // agree, or one of them is wrong about what a windowed tone means.
            const int length = 4096;

            using (IqBlock block = Tone(length, 12.8e6, 1e9, 1000, 0.5))
            {
                SpectrumFrame frame = new SpectrumComputer().Compute(block);

                int peak = frame.IndexOfPeak();
                double marker = frame.LevelsDbm[peak];

                double half = 40.0 * frame.BinWidthHz;
                BandPower band = BandMeasurements.Power(
                    frame, frame.FrequencyAt(peak) - half, frame.FrequencyAt(peak) + half);

                Assert.Equal(marker, band.TotalDbm, 2);
            }
        }

        [Fact]
        public void TheNinetyNinePercentBandwidthOfRootRaisedCosineIsOnePointOneSixSeven()
        {
            // REQ-MKR-003's second criterion. The spectrum is built directly from the raised-cosine
            // shape - which is the power spectrum of a root-raised-cosine-shaped signal - so the
            // expected answer is analytic rather than simulated.
            const double alpha = 0.35;
            const double symbolRate = 1.0e6;

            SpectrumFrame frame = RaisedCosineSpectrum(alpha, symbolRate);
            OccupiedBandwidth occupied = BandMeasurements.Occupied(frame, 0.99);

            double ratio = occupied.BandwidthHz / symbolRate;

            _output.WriteLine(
                "99% OBW = " + occupied.BandwidthHz.ToString("G6") + " Hz = " +
                ratio.ToString("F4") + " x Rsym (expected 1.167)");

            Assert.True(
                Math.Abs(ratio - 1.167) / 1.167 <= 0.02,
                "99 % OBW came to " + ratio.ToString("F4") + " x Rsym against 1.167.");
        }

        [Fact]
        public void TheNullToNullBandwidthIsNotTheNinetyNinePercentFigure()
        {
            // The trap the requirement names: (1+alpha)*Rsym is the absolute, null-to-null
            // bandwidth and is wrong by 11-16 % as a 99 % figure. Pinned so a change that
            // substituted it fails rather than looking plausible.
            const double alpha = 0.35;
            const double symbolRate = 1.0e6;

            SpectrumFrame frame = RaisedCosineSpectrum(alpha, symbolRate);
            double measured = BandMeasurements.Occupied(frame, 0.99).BandwidthHz / symbolRate;
            const double nullToNull = 1.0 + alpha;

            Assert.True(
                Math.Abs(measured - nullToNull) / nullToNull > 0.10,
                "The measured 99 % OBW of " + measured.ToString("F4") +
                " is within 10 % of the null-to-null figure " + nullToNull.ToString("F4") +
                ", so the two are not being distinguished.");
        }

        [Fact]
        public void OccupiedBandwidthGrowsWithTheFractionAsked()
        {
            SpectrumFrame frame = RaisedCosineSpectrum(0.35, 1.0e6);

            double ninety = BandMeasurements.Occupied(frame, 0.90).BandwidthHz;
            double ninetyNine = BandMeasurements.Occupied(frame, 0.99).BandwidthHz;

            Assert.True(ninetyNine > ninety);
        }

        [Fact]
        public void AdjacentChannelPowerIsMeasuredOnBothSidesOfTheCarrier()
        {
            // A carrier with one sideband deliberately stronger, so a measurement that folded the
            // two sides together, or reported only one, would be caught.
            const int points = 2001;
            const double binWidth = 1e3;
            const double centre = 1e9;

            var complex = new float[points * 2];

            for (int i = 0; i < points; i++)
            {
                double offset = (i - points / 2) * binWidth;
                double amplitude = 1e-6;

                if (Math.Abs(offset) < 50e3)
                {
                    amplitude = 1e-2;
                }
                else if (offset > 150e3 && offset < 250e3)
                {
                    amplitude = 1e-3;
                }
                else if (offset < -150e3 && offset > -250e3)
                {
                    amplitude = 1e-4;
                }

                complex[i * 2] = (float)amplitude;
                complex[i * 2 + 1] = 0.0f;
            }

            SpectrumFrame frame = SpectrumFrame.FromComplex(
                complex, centre - points / 2 * binWidth, binWidth, WindowType.Uniform, 1.0);

            AdjacentChannelPower acp = BandMeasurements.Adjacent(
                frame, centre, 100e3, new[] { 200e3 });

            Assert.Equal(2, acp.Channels.Count);

            AdjacentChannel upper = acp.Channels.Single(c => c.OffsetHz > 0.0);
            AdjacentChannel lower = acp.Channels.Single(c => c.OffsetHz < 0.0);

            _output.WriteLine("upper " + upper.RatioDb.ToString("F1") + " dBc, lower " + lower.RatioDb.ToString("F1") + " dBc");

            Assert.True(upper.RatioDb < 0.0 && lower.RatioDb < 0.0);
            Assert.True(
                lower.RatioDb < upper.RatioDb - 10.0,
                "The two sidebands differ by 20 dB and must not be reported as the same.");
        }

        [Theory]
        [InlineData(3.0)]
        [InlineData(6.0)]
        [InlineData(20.0)]
        public void TheXDecibelsDownWidthOfAGaussianMatchesItsClosedForm(double decibelsDown)
        {
            // REQ-CHM-002: a shape whose width at that level is known analytically. For a Gaussian
            // power spectrum exp(-f^2/2s^2), the level falls x dB at f = s*sqrt(2*ln(10^(x/10))),
            // so the width is twice that.
            const double sigmaHz = 50e3;

            SpectrumFrame frame = GaussianSpectrum(sigmaHz);
            OccupiedBandwidth measured = BandMeasurements.XDecibelsDown(frame, decibelsDown);

            double expected = 2.0 * sigmaHz *
                Math.Sqrt(2.0 * Math.Log(Math.Pow(10.0, decibelsDown / 10.0)));

            _output.WriteLine(
                decibelsDown + " dB down: " + measured.BandwidthHz.ToString("G6") +
                " Hz against " + expected.ToString("G6") + " Hz");

            Assert.True(
                Math.Abs(measured.BandwidthHz - expected) <= frame.BinWidthHz,
                decibelsDown + " dB width came to " + measured.BandwidthHz.ToString("G6") +
                " Hz against " + expected.ToString("G6") + " Hz, more than one bin out.");
        }

        [Fact]
        public void ThePercentageAndXDecibelCriteriaGiveDifferentAnswers()
        {
            // The check that both are genuinely implemented rather than one aliased onto the
            // other. For a Gaussian the 99 % width is about 5.15 sigma and the 3 dB width about
            // 2.35 sigma, so they are nowhere near each other.
            const double sigmaHz = 50e3;
            SpectrumFrame frame = GaussianSpectrum(sigmaHz);

            double percentage = BandMeasurements.Occupied(frame, 0.99).BandwidthHz;
            double threeDb = BandMeasurements.XDecibelsDown(frame, 3.0).BandwidthHz;

            _output.WriteLine(
                "99% = " + (percentage / sigmaHz).ToString("F3") + " sigma, 3 dB = " +
                (threeDb / sigmaHz).ToString("F3") + " sigma");

            Assert.True(percentage > threeDb * 1.5);
        }

        [Fact]
        public void TheDefaultPercentageIsNinetyNine()
        {
            SpectrumFrame frame = RaisedCosineSpectrum(0.35, 1.0e6);

            Assert.Equal(
                BandMeasurements.Occupied(frame, 0.99).BandwidthHz,
                BandMeasurements.Occupied(frame).BandwidthHz,
                6);
        }

        [Fact]
        public void AnEmptyBandReportsTheFloorRatherThanThrowing()
        {
            SpectrumFrame frame = RaisedCosineSpectrum(0.35, 1.0e6);
            BandPower power = BandMeasurements.Power(frame, 1e12, 1.1e12);

            Assert.Equal(0, power.BinCount);
            Assert.Equal(AmplitudeScale.FloorDbm, power.TotalDbm);
        }

        [Fact]
        public void TheArgumentsAreChecked()
        {
            SpectrumFrame frame = RaisedCosineSpectrum(0.35, 1.0e6);

            Assert.Throws<ArgumentNullException>(() => BandMeasurements.Power(null, 0.0, 1.0));
            Assert.Throws<ArgumentException>(() => BandMeasurements.Power(frame, 2.0, 1.0));
            Assert.Throws<ArgumentOutOfRangeException>(() => BandMeasurements.Occupied(frame, 1.0));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => BandMeasurements.Adjacent(frame, 1e9, 0.0, new[] { 1e3 }));
        }

        /// <summary>
        /// The power spectrum of a root-raised-cosine-shaped signal, built analytically.
        /// </summary>
        /// <remarks>
        /// An RRC-shaped signal has a raised-cosine power spectrum, so this is the shape whose
        /// 99 % bandwidth is the 1.167·R_sym the requirement quotes. Built rather than simulated,
        /// so the test measures the algorithm and not a random realisation.
        /// </remarks>
        private static SpectrumFrame RaisedCosineSpectrum(double alpha, double symbolRate)
        {
            const int points = 4001;
            double binWidth = symbolRate * 4.0 / points;

            var complex = new float[points * 2];
            double transitionStart = (1.0 - alpha) * symbolRate / 2.0;
            double transitionStop = (1.0 + alpha) * symbolRate / 2.0;

            for (int i = 0; i < points; i++)
            {
                double f = Math.Abs((i - points / 2) * binWidth);
                double powerShape;

                if (f <= transitionStart)
                {
                    powerShape = 1.0;
                }
                else if (f <= transitionStop)
                {
                    // Normalised by alpha x Rsym, so the cosine spans exactly the transition
                    // band: 1 at its lower edge and 0 at its upper one.
                    powerShape = 0.5 * (1.0 + Math.Cos(
                        Math.PI * (f - transitionStart) / (alpha * symbolRate)));
                }
                else
                {
                    powerShape = 0.0;
                }

                // The stored value is a voltage, so the square root of the power shape.
                complex[i * 2] = (float)Math.Sqrt(powerShape);
                complex[i * 2 + 1] = 0.0f;
            }

            return SpectrumFrame.FromComplex(
                complex, -(points / 2) * binWidth, binWidth, WindowType.Uniform, 1.0);
        }

        /// <summary>A Gaussian power spectrum, whose width at any level is known in closed form.</summary>
        private static SpectrumFrame GaussianSpectrum(double sigmaHz)
        {
            const int points = 2001;
            double binWidth = sigmaHz * 12.0 / points;

            var complex = new float[points * 2];

            for (int i = 0; i < points; i++)
            {
                double f = (i - points / 2) * binWidth;
                double power = Math.Exp(-(f * f) / (2.0 * sigmaHz * sigmaHz));

                complex[i * 2] = (float)Math.Sqrt(power);
                complex[i * 2 + 1] = 0.0f;
            }

            return SpectrumFrame.FromComplex(
                complex, -(points / 2) * binWidth, binWidth, WindowType.Uniform, 1.0);
        }

        private static double ExpectedDbm(double voltsPeak) =>
            10.0 * Math.Log10(voltsPeak * voltsPeak / (2.0 * 50.0) * 1000.0);

        private static IqBlock Tone(
            int count, double sampleRateHz, double centreHz, int bin, double amplitude)
        {
            var metadata = new IqBlockMetadata(
                sampleCount: count,
                sampleRateHz: sampleRateHz,
                centerFrequencyHz: centreHz,
                isBaseband: false,
                fullScaleVolts: 1.0,
                referenceLevelDbm: 0.0,
                sequenceNumber: 0,
                acquiredUtc: DateTime.UtcNow,
                triggerOffsetSeconds: 0.0,
                triggerCorrectionsApplied: true,
                source: new FrontEndId("test"),
                extended: null);

            IqBlock block = IqBlock.Rent(metadata);
            Span<float> samples = block.GetSamples();

            for (int n = 0; n < count; n++)
            {
                double phase = 2.0 * Math.PI * bin * n / count;
                samples[n * 2] = (float)(amplitude * Math.Cos(phase));
                samples[n * 2 + 1] = (float)(amplitude * Math.Sin(phase));
            }

            return block;
        }
    }
}
