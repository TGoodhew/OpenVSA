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
    /// <c>REQ-DSP-040</c>: power spectral density and autocorrelation, against closed forms.
    /// </summary>
    public class SpectrumDerivationTests
    {
        private const double SampleRateHz = 12.8e6;
        private const int Length = 16384;

        private readonly ITestOutputHelper _output;

        public SpectrumDerivationTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Theory]
        [InlineData(WindowType.Uniform)]
        [InlineData(WindowType.Hann)]
        [InlineData(WindowType.FlatTop)]
        [InlineData(WindowType.BlackmanHarris)]
        public void ThePsdOfWhiteNoiseReadsItsKnownDensity(WindowType window)
        {
            // REQ-DSP-040's criterion, to within 0.1 dB. The discriminating part is that it holds
            // under every window: the noise bandwidth of a bin is ENBW x bin width, so a density
            // computed with the bin width alone is out by the window's ENBW - 5.8 dB under Flat
            // Top, which is large enough to matter and small enough to look like a calibration
            // problem.
            const double sigma = 0.01;
            const int realisations = 8;

            // Averaged, because a single frame's per-bin estimate is chi-squared with two degrees
            // of freedom and scatters by several dB. Averaging is not a device for passing the
            // test - it is what a density measurement of noise actually is, and the requirement's
            // 0.1 dB is only meaningful against an estimate that has converged. Tapered windows
            // correlate neighbouring bins, so they need it more than Uniform does.
            var computer = new SpectrumComputer(window, null, null);
            var averager = new TraceAverager(AveragingType.RmsVideo, realisations);
            SpectrumFrame frame = null;

            for (int r = 0; r < realisations; r++)
            {
                using (IqBlock block = WhiteNoise(Length, sigma, seed: 4242 + r))
                {
                    frame = averager.Accumulate(computer.Compute(block));
                }
            }

            {
                var density = new float[frame.PointCount];
                PowerSpectralDensity.Compute(frame, density);

                // Averaged in the power domain, because the mean of a log is not the log of a
                // mean and a per-bin noise estimate scatters by several dB.
                double meanWatts = density
                    .Select(d => Math.Pow(10.0, d / 10.0))
                    .Average();

                double measured = 10.0 * Math.Log10(meanWatts);

                // Complex noise of sigma per component has mean power 2*sigma^2 volts squared,
                // spread across the whole sample rate.
                double expected = 10.0 * Math.Log10(
                    2.0 * sigma * sigma / (2.0 * 50.0) / SampleRateHz * 1000.0);

                _output.WriteLine(
                    window + ": " + measured.ToString("F3") + " dBm/Hz against " +
                    expected.ToString("F3"));

                Assert.True(
                    Math.Abs(measured - expected) <= 0.1,
                    window + ": density read " + measured.ToString("F3") +
                    " dBm/Hz against a known " + expected.ToString("F3") + " dBm/Hz.");
            }
        }

        [Fact]
        public void ThePsdWouldBeWrongByTheWindowsNoiseBandwidthWithoutTheCorrection()
        {
            // The correction has to be doing something, or the test above would pass on a version
            // that divided by the bin width alone. Flat Top's ENBW is 3.8194, which is 5.8 dB.
            using (IqBlock block = WhiteNoise(Length, 0.01, seed: 1))
            {
                var computer = new SpectrumComputer(WindowType.FlatTop, null, null);
                SpectrumFrame frame = computer.Compute(block);

                double corrected = PowerSpectralDensity.NoiseBandwidthHz(frame);
                double uncorrected = frame.BinWidthHz;

                Assert.Equal(
                    10.0 * Math.Log10(3.8194),
                    10.0 * Math.Log10(corrected / uncorrected),
                    2);
            }
        }

        [Fact]
        public void TheAutocorrelationOfWhiteNoiseIsAnImpulseAtZeroLag()
        {
            // REQ-DSP-040's criterion. White noise is uncorrelated with itself at every non-zero
            // lag, so everything away from lag zero must collapse.
            using (IqBlock block = WhiteNoise(Length, 0.01, seed: 99))
            {
                SpectrumFrame frame = new SpectrumComputer(WindowType.Uniform, null, null)
                    .Compute(block);

                var correlation = new float[1024];
                double lagSeconds = Autocorrelation.Compute(frame, null, correlation);

                Assert.Equal(1.0, correlation[0], 6);
                Assert.True(lagSeconds > 0.0);

                double largestAway = Enumerable.Range(1, correlation.Length - 1)
                    .Max(i => Math.Abs(correlation[i]));

                _output.WriteLine(
                    "largest correlation away from zero lag: " + largestAway.ToString("F4"));

                Assert.True(
                    largestAway < 0.2,
                    "White noise correlated with itself at " + largestAway.ToString("F4") +
                    " away from zero lag, so it is not being treated as uncorrelated.");
            }
        }

        [Fact]
        public void TheAutocorrelationOfAToneDoesNotCollapse()
        {
            // The opposite case, which makes the test above meaningful: a CW tone is perfectly
            // correlated with itself at every lag, so its autocorrelation stays large. A
            // computation that produced an impulse for everything would pass the noise test.
            using (IqBlock block = Tone(Length, 512, 0.5))
            {
                SpectrumFrame frame = new SpectrumComputer(WindowType.Uniform, null, null)
                    .Compute(block);

                var correlation = new float[1024];
                Autocorrelation.Compute(frame, null, correlation);

                double largestAway = Enumerable.Range(1, correlation.Length - 1)
                    .Max(i => Math.Abs(correlation[i]));

                _output.WriteLine("tone: largest away from zero lag " + largestAway.ToString("F4"));

                Assert.True(
                    largestAway > 0.5,
                    "A CW tone's autocorrelation collapsed to " + largestAway.ToString("F4") +
                    ", which would mean it was being treated as uncorrelated with itself.");
            }
        }

        [Fact]
        public void TheArgumentsAreChecked()
        {
            using (IqBlock block = WhiteNoise(1024, 0.01, seed: 2))
            {
                SpectrumFrame frame = new SpectrumComputer().Compute(block);

                Assert.Throws<ArgumentNullException>(
                    () => PowerSpectralDensity.Compute(null, new float[1]));
                Assert.Throws<ArgumentException>(
                    () => PowerSpectralDensity.Compute(frame, new float[3]));
                Assert.Throws<ArgumentNullException>(
                    () => Autocorrelation.Compute(null, null, new float[16]));

                // Not a power of two, and larger than the frame: both unusable.
                Assert.Throws<ArgumentException>(
                    () => Autocorrelation.Compute(frame, null, new float[100]));
                Assert.Throws<ArgumentException>(
                    () => Autocorrelation.Compute(frame, null, new float[1 << 20]));
            }
        }

        private static IqBlock WhiteNoise(int count, double sigma, int seed)
        {
            IqBlock block = Rent(count);
            Span<float> samples = block.GetSamples();
            var random = new Random(seed);

            for (int n = 0; n < count; n++)
            {
                samples[n * 2] = (float)(sigma * Gaussian(random));
                samples[n * 2 + 1] = (float)(sigma * Gaussian(random));
            }

            return block;
        }

        private static IqBlock Tone(int count, int bin, double amplitude)
        {
            IqBlock block = Rent(count);
            Span<float> samples = block.GetSamples();

            for (int n = 0; n < count; n++)
            {
                double phase = 2.0 * Math.PI * bin * n / count;
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
                sampleRateHz: SampleRateHz,
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
