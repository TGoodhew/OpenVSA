using System;
using System.Collections.Generic;
using System.Linq;
using OpenVSA.Core;
using OpenVSA.Dsp.Spectrum;
using OpenVSA.Dsp.Windowing;
using Xunit;
using Xunit.Abstractions;

namespace OpenVSA.Dsp.Tests
{
    /// <summary>
    /// <c>REQ-DSP-030</c>: the six averaging types, and the test that they are not conflated.
    /// </summary>
    /// <remarks>
    /// The requirement is explicit that the definitive check is the <em>difference</em> between
    /// coherent and incoherent averaging: coherent improves signal-to-noise by 10·log10(N),
    /// incoherent leaves it alone and instead reduces the variance of the estimate by N. Both make
    /// a trace look smoother, which is why conflating them is easy and why a test that only
    /// measured smoothness would pass either way.
    /// </remarks>
    public class TraceAveragerTests
    {
        private const int Points = 64;
        private const int ToneBin = 20;

        private readonly ITestOutputHelper _output;

        public TraceAveragerTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void CoherentAveragingImprovesSignalToNoiseByTenLogN()
        {
            const int averages = 16;

            double single = SignalToNoiseDb(AveragingType.Off, 1, seed: 1);
            double averaged = SignalToNoiseDb(AveragingType.Time, averages, seed: 1);

            double improvement = averaged - single;
            double expected = 10.0 * Math.Log10(averages);

            _output.WriteLine(
                "coherent: " + single.ToString("F2") + " dB -> " + averaged.ToString("F2") +
                " dB, improvement " + improvement.ToString("F2") + " dB against " +
                expected.ToString("F2"));

            // Within 2 dB: the noise realisation is finite, so the improvement scatters about its
            // expectation. The point is that it is close to 12 dB and nowhere near zero.
            Assert.InRange(improvement, expected - 2.0, expected + 2.0);
        }

        [Fact]
        public void IncoherentAveragingLeavesSignalToNoiseAlone()
        {
            const int averages = 16;

            double single = SignalToNoiseDb(AveragingType.Off, 1, seed: 1);
            double averaged = SignalToNoiseDb(AveragingType.RmsVideo, averages, seed: 1);

            double change = averaged - single;

            _output.WriteLine(
                "incoherent: " + single.ToString("F2") + " dB -> " + averaged.ToString("F2") +
                " dB, change " + change.ToString("F2") + " dB");

            // The mean noise power is unchanged by averaging power, so the ratio stays put. A
            // conflated implementation that averaged the complex values would show ~12 dB here.
            Assert.InRange(change, -2.0, 2.0);
        }

        [Fact]
        public void IncoherentAveragingReducesTheVarianceOfTheEstimate()
        {
            // The other half of the requirement's criterion. Averaging power cannot improve the
            // signal-to-noise ratio, but it does make the noise floor estimate steadier - standard
            // deviation falling as the square root of the count.
            const int averages = 16;

            double single = NoiseFloorSpreadDb(AveragingType.Off, 1, seed: 7);
            double averaged = NoiseFloorSpreadDb(AveragingType.RmsVideo, averages, seed: 7);

            _output.WriteLine(
                "noise-floor spread: " + single.ToString("F2") + " dB -> " +
                averaged.ToString("F2") + " dB");

            Assert.True(
                averaged < single * 0.6,
                "Averaging " + averages + " times reduced the spread only from " +
                single.ToString("F2") + " dB to " + averaged.ToString("F2") + " dB.");
        }

        [Fact]
        public void PeakHoldKeepsTheLargestValueEachBinEverSaw()
        {
            var averager = new TraceAverager(AveragingType.PeakHold, 4);

            SpectrumFrame result = null;

            foreach (double amplitude in new[] { 0.2, 0.9, 0.4 })
            {
                result = averager.Accumulate(Frame(amplitude, noiseSigma: 0.0, seed: 1));
            }

            var magnitude = new float[result.PointCount];
            result.Format(TraceFormat.LinearMagnitude, magnitude);

            // The bin holding the tone must remember the 0.9, not the 0.4 that came after it.
            SpectrumFrame biggest = Frame(0.9, noiseSigma: 0.0, seed: 1);
            var expected = new float[biggest.PointCount];
            biggest.Format(TraceFormat.LinearMagnitude, expected);

            Assert.Equal(expected[ToneBin], magnitude[ToneBin], 6);
        }

        [Fact]
        public void ALinearAverageStopsAtItsCount()
        {
            var averager = new TraceAverager(AveragingType.RmsVideo, 3);

            for (int i = 0; i < 3; i++)
            {
                averager.Accumulate(Frame(0.5, 0.0, 1));
            }

            Assert.True(averager.IsComplete);
            Assert.Equal(3, averager.Completed);

            // Held, not advanced: a finished measurement stays put while it is read.
            averager.Accumulate(Frame(0.5, 0.0, 1));
            Assert.Equal(3, averager.Completed);
        }

        [Fact]
        public void RepeatAverageRestartsInsteadOfHolding()
        {
            var averager = new TraceAverager(AveragingType.RmsVideo, 3) { RepeatAverage = true };

            for (int i = 0; i < 3; i++)
            {
                averager.Accumulate(Frame(0.5, 0.0, 1));
            }

            Assert.True(averager.IsComplete);

            averager.Accumulate(Frame(0.5, 0.0, 1));
            Assert.Equal(1, averager.Completed);
            Assert.False(averager.IsComplete);
        }

        [Fact]
        public void ExponentialTypesNeverComplete()
        {
            var averager = new TraceAverager(AveragingType.RmsVideoExponential, 2);

            for (int i = 0; i < 10; i++)
            {
                averager.Accumulate(Frame(0.5, 0.0, 1));
            }

            Assert.False(averager.IsComplete);
            Assert.Equal(10, averager.Completed);
        }

        [Fact]
        public void APowerAveragedFrameReportsThatItHasNoPhase()
        {
            var coherent = new TraceAverager(AveragingType.Time, 4);
            var incoherent = new TraceAverager(AveragingType.RmsVideo, 4);

            SpectrumFrame fromCoherent = coherent.Accumulate(Frame(0.5, 0.0, 1));
            SpectrumFrame fromIncoherent = incoherent.Accumulate(Frame(0.5, 0.0, 1));

            Assert.True(fromCoherent.HasPhase);
            Assert.False(fromIncoherent.HasPhase);
        }

        [Fact]
        public void ThePhaseFormatsAreInvalidForAPowerAveragedTrace()
        {
            // REQ-TRC-002: validity is a function of the averaging type as well as the data
            // source, and invalid combinations are unselectable rather than erroring afterwards.
            foreach (TraceFormat format in
                new[] { TraceFormat.WrappedPhase, TraceFormat.UnwrappedPhase, TraceFormat.GroupDelay, TraceFormat.IQ })
            {
                Assert.False(TraceValidity.IsValid(format, AveragingType.RmsVideo));
                Assert.True(TraceValidity.IsValid(format, AveragingType.Time));
                Assert.True(TraceValidity.IsValid(format, AveragingType.Off));

                Assert.Contains("phase", TraceValidity.Explain(format, AveragingType.RmsVideo));
            }

            // Magnitude formats survive any averaging.
            Assert.True(TraceValidity.IsValid(TraceFormat.LogMagnitude, AveragingType.RmsVideo));
            Assert.DoesNotContain(
                TraceFormat.WrappedPhase, TraceValidity.ValidFormats(AveragingType.RmsVideo));
            Assert.Contains(
                TraceFormat.WrappedPhase, TraceValidity.ValidFormats(AveragingType.Time));
        }

        [Fact]
        public void AveragingOffPassesTheFrameStraightThrough()
        {
            var averager = new TraceAverager(AveragingType.Off);
            SpectrumFrame frame = Frame(0.5, 0.0, 1);

            Assert.Same(frame, averager.Accumulate(frame));
        }

        [Fact]
        public void AnAveragerNeedsAPositiveCountAndAFrame()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new TraceAverager(AveragingType.RmsVideo, 0));
            Assert.Throws<ArgumentNullException>(
                () => new TraceAverager(AveragingType.RmsVideo).Accumulate(null));
        }

        /// <summary>Signal-to-noise of the averaged spectrum, in dB.</summary>
        private static double SignalToNoiseDb(AveragingType type, int averages, int seed)
        {
            var averager = new TraceAverager(type, averages);
            SpectrumFrame result = null;
            var random = new Deterministic(seed);

            for (int i = 0; i < averages; i++)
            {
                result = averager.Accumulate(Frame(0.5, 0.05, random));
            }

            var levels = new float[result.PointCount];
            result.Format(TraceFormat.LogMagnitude, levels);

            double tone = levels[ToneBin];
            double floor = NoiseBins(levels).Average();

            return tone - floor;
        }

        /// <summary>Standard deviation of the noise floor, in dB — the scatter of the estimate.</summary>
        private static double NoiseFloorSpreadDb(AveragingType type, int averages, int seed)
        {
            var averager = new TraceAverager(type, averages);
            SpectrumFrame result = null;
            var random = new Deterministic(seed);

            for (int i = 0; i < averages; i++)
            {
                result = averager.Accumulate(Frame(0.0, 0.05, random));
            }

            var levels = new float[result.PointCount];
            result.Format(TraceFormat.LogMagnitude, levels);

            List<double> floor = NoiseBins(levels).ToList();
            double mean = floor.Average();

            return Math.Sqrt(floor.Sum(v => (v - mean) * (v - mean)) / floor.Count);
        }

        private static IEnumerable<double> NoiseBins(float[] levels) =>
            Enumerable.Range(0, levels.Length)
                .Where(i => Math.Abs(i - ToneBin) > 3)
                .Select(i => (double)levels[i]);

        private static SpectrumFrame Frame(double amplitude, double noiseSigma, int seed) =>
            Frame(amplitude, noiseSigma, new Deterministic(seed));

        /// <summary>
        /// A spectrum of a tone in noise, built directly rather than transformed.
        /// </summary>
        /// <remarks>
        /// Built in the frequency domain so the tone sits in exactly one bin and the noise is
        /// independent bin to bin — which is what makes the coherent and incoherent behaviours
        /// analytically clean rather than entangled with window leakage.
        /// </remarks>
        private static SpectrumFrame Frame(double amplitude, double noiseSigma, Deterministic random)
        {
            var complex = new float[Points * 2];

            for (int i = 0; i < Points; i++)
            {
                double re = noiseSigma * random.NextGaussian();
                double im = noiseSigma * random.NextGaussian();

                if (i == ToneBin)
                {
                    // A fixed phase, as a triggered acquisition would give: that is what lets
                    // coherent averaging accumulate the tone while the noise cancels.
                    re += amplitude;
                }

                complex[i * 2] = (float)re;
                complex[i * 2 + 1] = (float)im;
            }

            return SpectrumFrame.FromComplex(complex, 1e9, 10e3, WindowType.Uniform, 1.0);
        }

        /// <summary>A reproducible Gaussian source, so a failure can be investigated.</summary>
        private sealed class Deterministic
        {
            private readonly Random _random;

            public Deterministic(int seed)
            {
                _random = new Random(seed);
            }

            public double NextGaussian()
            {
                double u1 = 1.0 - _random.NextDouble();
                double u2 = _random.NextDouble();
                return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
            }
        }
    }
}
