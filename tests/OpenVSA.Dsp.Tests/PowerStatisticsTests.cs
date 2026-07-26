using System;
using System.Linq;
using OpenVSA.Core;
using OpenVSA.Dsp.Spectrum;
using Xunit;
using Xunit.Abstractions;

namespace OpenVSA.Dsp.Tests
{
    /// <summary>
    /// <c>REQ-DSP-040</c> and <c>REQ-DSP-042</c>: the CCDF and the curve it is judged against.
    /// </summary>
    public class PowerStatisticsTests
    {
        private readonly ITestOutputHelper _output;

        public PowerStatisticsTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void TheCcdfOfGaussianNoiseMatchesTheAnalyticCurve()
        {
            // REQ-DSP-042's criterion, in its own terms: the measured curve must lie within
            // 0.2 dB *horizontally* of P(x) = exp(-10^(x/10)) over 0-10 dB. Horizontally matters -
            // "within 0.2 dB" of a probability is meaningless on a log axis.
            //
            // The sample count is part of the criterion too, and the requirement puts a number on
            // it: at least 10^7 independent samples. At x = 10 dB the true probability is e^-10,
            // about 4.5e-5, so 10^7 samples put roughly 450 of them in the tail being measured -
            // enough for the estimate there to have converged. Two million, which this test used
            // before the criterion was read closely, leaves about 90, and a curve fitted through
            // 90 samples is not what "within 0.2 dB" is asking about.
            const int samples = 10_000_000;

            using (IqBlock block = GaussianNoise(samples, seed: 20260725))
            {
                PowerDistribution distribution = PowerStatistics.Ccdf(block, bins: 400, rangeDb: 20.0);

                double worst = 0.0;
                double worstAt = 0.0;

                for (double x = 0.0; x <= 10.0; x += 0.5)
                {
                    double analytic = PowerStatistics.GaussianCcdf(x);
                    double measuredX = distribution.RatioAt(analytic);

                    if (double.IsNaN(measuredX))
                    {
                        continue;
                    }

                    double error = Math.Abs(measuredX - x);

                    if (error > worst)
                    {
                        worst = error;
                        worstAt = x;
                    }
                }

                _output.WriteLine(
                    "worst horizontal departure " + worst.ToString("F3") + " dB at " +
                    worstAt.ToString("F1") + " dB above average");

                Assert.True(
                    worst <= 0.2,
                    "The measured CCDF departs from the analytic curve by " +
                    worst.ToString("F3") + " dB at " + worstAt.ToString("F1") + " dB.");
            }
        }

        [Fact]
        public void GaussianNoiseHasThePeakToAverageRatioTheoryPredicts()
        {
            // Around 8-9 dB at the 0.01 % point for complex Gaussian noise: exp(-10^(x/10)) =
            // 1e-4 gives x = 10*log10(-ln(1e-4)) = 9.63 dB.
            using (IqBlock block = GaussianNoise(2_000_000, seed: 11))
            {
                PowerDistribution distribution = PowerStatistics.Ccdf(block, 400, 20.0);

                double analytic = 10.0 * Math.Log10(-Math.Log(1e-4));
                double measured = distribution.RatioAt(1e-4);

                _output.WriteLine(
                    "0.01 % point: " + measured.ToString("F3") + " dB against " +
                    analytic.ToString("F3"));

                Assert.Equal(analytic, measured, 1);
            }
        }

        [Fact]
        public void AConstantEnvelopeSignalHasAlmostNoPeakToAverageRatio()
        {
            // A CW tone has constant instantaneous power, so its CCDF collapses: everything sits
            // at 0 dB above average and nothing exceeds it. That is the opposite extreme from
            // noise and catches a CCDF that had been computed against an absolute level rather
            // than against the mean.
            using (IqBlock block = Tone(100_000, amplitude: 0.4))
            {
                PowerDistribution distribution = PowerStatistics.Ccdf(block, 200, 20.0);

                Assert.Equal(0.0, distribution.PeakToAverageDb, 3);

                // Above the average there is nothing at all. Asserted a bin up rather than at
                // zero: every sample of a constant envelope sits exactly on the 0 dB boundary, so
                // which side of it they land on is a rounding question and not a measurement.
                double step = 20.0 / (200 - 1);
                int oneDb = (int)Math.Ceiling(1.0 / step);

                Assert.Equal(0.0, distribution.Ccdf[oneDb], 9);
            }
        }

        [Fact]
        public void TheCdfIsOneMinusTheCcdf()
        {
            using (IqBlock block = GaussianNoise(50_000, seed: 3))
            {
                PowerDistribution distribution = PowerStatistics.Ccdf(block, 64, 20.0);

                var cdf = new double[distribution.PointCount];
                distribution.Cdf(cdf);

                for (int i = 0; i < distribution.PointCount; i++)
                {
                    Assert.Equal(1.0 - distribution.Ccdf[i], cdf[i], 12);
                }
            }
        }

        [Fact]
        public void ThePdfIsTheDifferenceOfTheCumulativeCurveAndSumsToTheWhole()
        {
            using (IqBlock block = GaussianNoise(50_000, seed: 5))
            {
                PowerDistribution distribution = PowerStatistics.Ccdf(block, 400, 40.0);

                var pdf = new double[distribution.PointCount];
                distribution.Pdf(pdf);

                Assert.All(pdf, p => Assert.True(p >= -1e-12));

                // Everything at or above 0 dB is accounted for, so the density sums to the
                // proportion that starts above the average.
                Assert.Equal(distribution.Ccdf[0], pdf.Sum(), 6);
            }
        }

        [Fact]
        public void TheCcdfFallsMonotonically()
        {
            using (IqBlock block = GaussianNoise(200_000, seed: 9))
            {
                PowerDistribution distribution = PowerStatistics.Ccdf(block, 200, 20.0);

                for (int i = 1; i < distribution.PointCount; i++)
                {
                    Assert.True(
                        distribution.Ccdf[i] <= distribution.Ccdf[i - 1] + 1e-12,
                        "The CCDF rose between " + i + " and " + (i - 1) + ".");
                }
            }
        }

        [Fact]
        public void TheArgumentsAreChecked()
        {
            using (IqBlock block = Tone(1000, 0.1))
            {
                Assert.Throws<ArgumentNullException>(() => PowerStatistics.Ccdf(null));
                Assert.Throws<ArgumentOutOfRangeException>(() => PowerStatistics.Ccdf(block, 1));
                Assert.Throws<ArgumentOutOfRangeException>(
                    () => PowerStatistics.Ccdf(block, 100, 0.0));

                PowerDistribution distribution = PowerStatistics.Ccdf(block, 16, 10.0);
                Assert.Throws<ArgumentException>(() => distribution.Cdf(new double[3]));
            }
        }

        /// <summary>Complex Gaussian noise, reproducible so a failure can be investigated.</summary>
        private static IqBlock GaussianNoise(int count, int seed)
        {
            IqBlock block = Rent(count);
            Span<float> samples = block.GetSamples();
            var random = new Random(seed);

            for (int n = 0; n < count; n++)
            {
                samples[n * 2] = (float)Gaussian(random);
                samples[n * 2 + 1] = (float)Gaussian(random);
            }

            return block;
        }

        private static IqBlock Tone(int count, double amplitude)
        {
            IqBlock block = Rent(count);
            Span<float> samples = block.GetSamples();

            for (int n = 0; n < count; n++)
            {
                double phase = 2.0 * Math.PI * 0.037 * n;
                samples[n * 2] = (float)(amplitude * Math.Cos(phase));
                samples[n * 2 + 1] = (float)(amplitude * Math.Sin(phase));
            }

            return block;
        }

        private static double Gaussian(Random random)
        {
            double u1 = 1.0 - random.NextDouble();
            double u2 = random.NextDouble();
            return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
        }

        private static IqBlock Rent(int count) =>
            IqBlock.Rent(new IqBlockMetadata(
                sampleCount: count,
                sampleRateHz: 12.8e6,
                centerFrequencyHz: 1e9,
                isBaseband: false,
                fullScaleVolts: 1.0,
                referenceLevelDbm: 0.0,
                sequenceNumber: 0,
                acquiredUtc: DateTime.UtcNow,
                triggerOffsetSeconds: 0.0,
                triggerCorrectionsApplied: true,
                source: new FrontEndId("test"),
                extended: null));
    }
}
