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
    /// <c>REQ-ACQ-003</c> overlap processing, and <c>REQ-DSP-031</c>'s effective average count.
    /// </summary>
    public class FrameExtractionTests
    {
        private const double SampleRateHz = 12.8e6;

        private readonly ITestOutputHelper _output;

        public FrameExtractionTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void FiftyPercentOverlapYieldsTwiceAsManyFrames()
        {
            // REQ-ACQ-003's acceptance criterion, stated as it is written: within one frame, half
            // overlap doubles the count.
            const int record = 1024;
            const int available = 32768;

            int none = FrameExtraction.FrameCount(available, record, 0.0);
            int half = FrameExtraction.FrameCount(available, record, 0.5);

            _output.WriteLine(none + " frames at 0 %, " + half + " at 50 %");

            Assert.True(
                Math.Abs(half - 2 * none) <= 1,
                half + " frames at 50 % overlap against " + none + " at 0 %.");
        }

        [Fact]
        public void AdvanceFollowsTheRecordLengthRatherThanTheTransformLength()
        {
            // The distinction the requirement calls out. A 1000-sample record transforms at 512
            // points, so an advance taken from the transform length would be 256 and the count
            // would come out at 12 rather than 7. The two only agree when the record happens to be
            // a power of two, which is exactly why a test that used one would not catch this.
            const int record = 1000;
            const int available = 4000;

            Assert.Equal(512, SpectrumComputer.TransformLengthFor(record));

            Assert.Equal(500, FrameExtraction.Advance(record, 0.5));
            Assert.Equal(7, FrameExtraction.FrameCount(available, record, 0.5));
        }

        [Theory]
        [InlineData(0.0, 1000)]
        [InlineData(0.5, 500)]
        [InlineData(0.75, 250)]
        [InlineData(0.9, 100)]
        [InlineData(0.99, 10)]
        [InlineData(0.999, 1)]
        [InlineData(0.9999, 1)]
        public void TheAdvanceIsTheFlooredRemainderOfTheRecord(double overlap, int expected)
        {
            // Floored, and never less than a sample: at 99.99 % of a 1000-sample record the exact
            // advance is a tenth of a sample, and a zero advance would extract the same frame for
            // ever.
            Assert.Equal(expected, FrameExtraction.Advance(1000, overlap));
        }

        [Fact]
        public void AFramesSamplesAreTheOnesItsPositionNamesThem()
        {
            // A ramp makes every sample identifiable, so the placement is checked rather than
            // assumed.
            using (IqBlock block = Ramp(1000))
            {
                List<IqBlock> frames = FrameExtraction.Extract(block, 200, 0.5).ToList();

                try
                {
                    Assert.Equal(9, frames.Count);

                    for (int f = 0; f < frames.Count; f++)
                    {
                        Assert.Equal(200, frames[f].SampleCount);
                        Assert.Equal(f * 100.0f, frames[f].GetSample(0).I, 3);
                        Assert.Equal(f * 100.0f + 199.0f, frames[f].GetSample(199).I, 3);
                    }
                }
                finally
                {
                    frames.ForEach(f => f.Dispose());
                }
            }
        }

        [Fact]
        public void EachFrameCarriesItsOwnTriggerOffsetAndSequenceNumber()
        {
            // Every frame is a separate analysis downstream, so each has to say where it sits in
            // the sequence and how far back the trigger now is - REQ-DAT-002's relationship, kept
            // through the split.
            using (IqBlock block = Ramp(1000))
            {
                List<IqBlock> frames = FrameExtraction.Extract(block, 500, 0.5).ToList();

                try
                {
                    Assert.Equal(3, frames.Count);

                    for (int f = 0; f < frames.Count; f++)
                    {
                        Assert.Equal(block.SequenceNumber + f, frames[f].SequenceNumber);
                        Assert.Equal(
                            block.TriggerOffsetSeconds - f * 250.0 / SampleRateHz,
                            frames[f].TriggerOffsetSeconds,
                            12);
                    }
                }
                finally
                {
                    frames.ForEach(f => f.Dispose());
                }
            }
        }

        [Fact]
        public void EveryFrameAgreesOnWhenTheTriggerHappened()
        {
            // The frames are cuts of one acquisition, so they cannot disagree about when its
            // trigger was. The timestamp advances and the offset shrinks by the same amount: move
            // one without the other and the trigger appears to walk backwards frame by frame.
            using (IqBlock block = Ramp(1000))
            {
                List<IqBlock> frames = FrameExtraction.Extract(block, 500, 0.5).ToList();

                try
                {
                    DateTime expected = BlockTimeline.TriggerInstant(
                        block.AcquiredUtc, block.TriggerOffsetSeconds);

                    for (int f = 0; f < frames.Count; f++)
                    {
                        Assert.True(
                            frames[f].AcquiredUtc >= block.AcquiredUtc,
                            "frame " + f + " starts before the block it was cut from.");
                        DateTime actual = BlockTimeline.TriggerInstant(
                            frames[f].AcquiredUtc, frames[f].TriggerOffsetSeconds);

                        // To the tick, which is the resolution the two halves are rounded at -
                        // the defect this guards against moves the instant by 195 of them.
                        Assert.True(
                            Math.Abs((actual - expected).Ticks) <= 1,
                            "frame " + f + " puts the trigger " + (actual - expected).Ticks +
                            " ticks from where the block does.");
                    }
                }
                finally
                {
                    frames.ForEach(f => f.Dispose());
                }
            }
        }

        [Fact]
        public void ARecordShorterThanOneFrameYieldsNothing()
        {
            Assert.Equal(0, FrameExtraction.FrameCount(99, 100, 0.5));

            using (IqBlock block = Ramp(99))
            {
                Assert.Empty(FrameExtraction.Extract(block, 100, 0.5));
            }
        }

        [Fact]
        public void TheArgumentsAreChecked()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => FrameExtraction.Advance(0, 0.5));
            Assert.Throws<ArgumentOutOfRangeException>(() => FrameExtraction.Advance(100, -0.1));
            Assert.Throws<ArgumentOutOfRangeException>(() => FrameExtraction.Advance(100, 1.0));
            Assert.Throws<ArgumentOutOfRangeException>(() => FrameExtraction.Advance(100, double.NaN));
            Assert.Throws<ArgumentNullException>(
                () => FrameExtraction.Extract(null, 100, 0.5).ToList());
            Assert.Throws<ArgumentOutOfRangeException>(
                () => EffectiveAverages.Compute(0, 100, 0.0, WindowType.Hann));
        }

        [Theory]
        [InlineData(WindowType.Uniform)]
        [InlineData(WindowType.Hann)]
        [InlineData(WindowType.FlatTop)]
        public void WithoutOverlapTheEffectiveCountIsTheFrameCount(WindowType window)
        {
            // Frames that share no samples are independent, whatever the window, so the effective
            // count is the plain one exactly.
            Assert.Equal(20.0, EffectiveAverages.Compute(20, 1024, 0.0, window), 10);
        }

        [Theory]
        [InlineData(0.5)]
        [InlineData(0.75)]
        [InlineData(0.9)]
        public void OverlapMakesTheEffectiveCountStrictlyLessThanTheFrameCount(double overlap)
        {
            // REQ-DSP-031: reporting the raw count at 75 % overlap is the failure the requirement
            // names, so the figure has to move.
            double effective = EffectiveAverages.Compute(40, 1024, overlap, WindowType.Hann);

            _output.WriteLine(
                "40 frames at " + (overlap * 100.0).ToString("F0") + " % overlap are worth " +
                effective.ToString("F2"));

            Assert.True(effective < 40.0);
            Assert.True(effective > 0.0);
        }

        [Fact]
        public void ATaperedWindowGivesUpLessToOverlapThanAUniformOne()
        {
            // Which is why the figure depends on the window and not on the overlap alone: Hann has
            // already weighted the shared samples towards zero, so half-overlapped Hann frames are
            // far less correlated than half-overlapped uniform ones.
            double uniform = EffectiveAverages.Compute(32, 1024, 0.5, WindowType.Uniform);
            double hann = EffectiveAverages.Compute(32, 1024, 0.5, WindowType.Hann);

            _output.WriteLine(
                "32 frames at 50 % overlap: uniform " + uniform.ToString("F2") + ", Hann " +
                hann.ToString("F2"));

            Assert.True(
                hann > uniform,
                "Hann came out at " + hann.ToString("F2") + " against uniform's " +
                uniform.ToString("F2") + ", so the window is not being taken into account.");
        }

        [Theory]
        [InlineData(0.0)]
        [InlineData(0.5)]
        [InlineData(0.75)]
        public void TheEffectiveCountPredictsTheVarianceThatIsActuallyObserved(double overlap)
        {
            // REQ-DSP-031's acceptance criterion, and the only test here that could tell a correct
            // formula from a plausible one. Averaging n independent estimates of a noise power
            // divides the variance by n, so the effective count is recoverable from the scatter of
            // the result: for exponentially distributed bin powers, mean squared over variance.
            //
            // Uniform window and complex Gaussian noise, because that combination makes the bins
            // of one frame exactly independent complex Gaussians - so the only correlation left in
            // the measurement is the one the overlap introduced, which is the thing being checked.
            const int record = 128;
            const int frames = 16;
            const int trials = 120;

            int advance = FrameExtraction.Advance(record, overlap);
            int available = record + (frames - 1) * advance;

            var computer = new SpectrumComputer(WindowType.Uniform, null, null);
            var averaged = new List<double>(trials * record);

            for (int t = 0; t < trials; t++)
            {
                using (IqBlock noise = WhiteNoise(available, 0.01, seed: 7000 + t))
                {
                    var sum = new double[record];
                    int counted = 0;

                    foreach (IqBlock frame in FrameExtraction.Extract(noise, record, overlap))
                    {
                        using (frame)
                        {
                            SpectrumFrame spectrum = computer.Compute(frame);
                            ReadOnlySpan<float> complex = spectrum.Complex;

                            for (int i = 0; i < record; i++)
                            {
                                double re = complex[i * 2];
                                double im = complex[i * 2 + 1];
                                sum[i] += re * re + im * im;
                            }

                            counted++;
                        }
                    }

                    Assert.Equal(frames, counted);

                    for (int i = 0; i < record; i++)
                    {
                        averaged.Add(sum[i] / counted);
                    }
                }
            }

            double mean = averaged.Average();
            double variance = averaged.Sum(v => (v - mean) * (v - mean)) / (averaged.Count - 1);

            double observed = mean * mean / variance;
            double predicted = EffectiveAverages.Compute(frames, record, overlap, WindowType.Uniform);

            _output.WriteLine(
                (overlap * 100.0).ToString("F0") + " % overlap: predicted " +
                predicted.ToString("F2") + " effective averages, observed " +
                observed.ToString("F2") + " from " + averaged.Count + " bin estimates");

            Assert.True(
                Math.Abs(observed - predicted) / predicted <= 0.1,
                "At " + (overlap * 100.0).ToString("F0") + " % overlap the effective count was " +
                "predicted as " + predicted.ToString("F2") + " but the observed variance " +
                "reduction was " + observed.ToString("F2") + ".");
        }

        [Fact]
        public void ReportingTheFrameCountWouldFailThatCriterionAtSeventyFivePercent()
        {
            // What makes the test above discriminating: at 75 % overlap the raw frame count is
            // more than 10 % away from the truth, so an implementation that reported it would be
            // caught rather than merely unprincipled.
            double effective = EffectiveAverages.Compute(16, 128, 0.75, WindowType.Uniform);

            _output.WriteLine(
                "16 frames at 75 % overlap are worth " + effective.ToString("F2"));

            Assert.True(
                (16.0 - effective) / effective > 0.1,
                "The raw count of 16 is within 10 % of the effective " + effective.ToString("F2") +
                ", so this criterion would not distinguish them.");
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

        private static double Gaussian(Random random)
        {
            double u1 = 1.0 - random.NextDouble();
            double u2 = random.NextDouble();
            return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
        }

        private static IqBlock Ramp(int count)
        {
            IqBlock block = Rent(count);
            Span<float> samples = block.GetSamples();

            for (int n = 0; n < count; n++)
            {
                samples[n * 2] = n;
                samples[n * 2 + 1] = -n;
            }

            return block;
        }

        private static IqBlock Rent(int count) =>
            IqBlock.Rent(new IqBlockMetadata(
                sampleCount: count,
                sampleRateHz: SampleRateHz,
                centerFrequencyHz: 1e9,
                isBaseband: false,
                fullScaleVolts: 1.0,
                referenceLevelDbm: 0.0,
                sequenceNumber: 17,
                acquiredUtc: DateTime.UtcNow,
                triggerOffsetSeconds: 1e-3,
                triggerCorrectionsApplied: true,
                source: new FrontEndId("test"),
                extended: null));
    }
}
