using System;
using OpenVSA.Core;
using OpenVSA.Dsp.Spectrum;
using OpenVSA.Dsp.Windowing;
using Xunit;
using Xunit.Abstractions;

namespace OpenVSA.Dsp.Tests
{
    /// <summary>
    /// <c>REQ-DSP-050</c>: gating to a sub-interval, with RBW following the gate.
    /// </summary>
    public class TimeGateTests
    {
        private const double SampleRateHz = 10.24e6;
        private const int Length = 8192;
        private const int FirstToneBin = 500;
        private const int SecondToneBin = 2000;

        private readonly ITestOutputHelper _output;

        public TimeGateTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void GatingToEachIntervalRevealsOnlyTheToneThatIsThere()
        {
            // REQ-DSP-050's criterion exactly: two tones present in disjoint time intervals, and
            // gating to each reveals only the corresponding one.
            using (IqBlock block = TwoTonesInSequence())
            {
                var computer = new SpectrumComputer(WindowType.Hann, null, null);

                double halfRecord = Length / 2.0 / SampleRateHz;

                using (IqBlock firstHalf = new TimeGate(0.0, halfRecord).Apply(block))
                using (IqBlock secondHalf = new TimeGate(halfRecord, halfRecord).Apply(block))
                {
                    SpectrumFrame early = computer.Compute(firstHalf);
                    SpectrumFrame late = computer.Compute(secondHalf);

                    double earlyPeak = early.FrequencyAt(early.IndexOfPeak());
                    double latePeak = late.FrequencyAt(late.IndexOfPeak());

                    double firstToneHz = ToneFrequency(FirstToneBin);
                    double secondToneHz = ToneFrequency(SecondToneBin);

                    _output.WriteLine(
                        "first gate peak " + earlyPeak.ToString("G8") + " Hz (tone " +
                        firstToneHz.ToString("G8") + "), second gate peak " +
                        latePeak.ToString("G8") + " Hz (tone " + secondToneHz.ToString("G8") + ")");

                    // Each gate must find its own tone, within a bin of the shortened record.
                    Assert.True(Math.Abs(earlyPeak - firstToneHz) <= 2.0 * early.BinWidthHz);
                    Assert.True(Math.Abs(latePeak - secondToneHz) <= 2.0 * late.BinWidthHz);

                    // And they must be different tones, or the gate is not selecting anything.
                    Assert.True(Math.Abs(earlyPeak - latePeak) > 10.0 * early.BinWidthHz);
                }
            }
        }

        [Fact]
        public void TheUngatedRecordShowsBothTones()
        {
            // What makes the test above meaningful: without gating, both tones are present, so
            // finding one of them is a consequence of the gate rather than of the signal.
            using (IqBlock block = TwoTonesInSequence())
            {
                SpectrumFrame frame = new SpectrumComputer(WindowType.Hann, null, null)
                    .Compute(block);

                double firstLevel = LevelAt(frame, ToneFrequency(FirstToneBin));
                double secondLevel = LevelAt(frame, ToneFrequency(SecondToneBin));
                double floor = LevelAt(frame, ToneFrequency(3500));

                Assert.True(firstLevel > floor + 20.0);
                Assert.True(secondLevel > floor + 20.0);
            }
        }

        [Fact]
        public void ResolutionBandwidthTracksGateLengthRatherThanRecordLength()
        {
            // The annotation half of the requirement. A gate shortens the record, so the RBW must
            // coarsen - a gated measurement that kept the ungated RBW would be claiming a
            // resolution the analysis no longer has.
            const double enbw = 1.5;
            double recordSeconds = Length / SampleRateHz;

            var quarter = new TimeGate(0.0, recordSeconds / 4.0);

            double ungated = ResolutionBandwidth.ForRecordLength(enbw, recordSeconds);
            double gated = quarter.ResolutionBandwidthHz(enbw);

            _output.WriteLine(
                "ungated RBW " + ungated.ToString("G6") + " Hz, gated to a quarter " +
                gated.ToString("G6") + " Hz");

            Assert.Equal(4.0, gated / ungated, 6);
        }

        [Fact]
        public void TheGatedFrameCarriesTheCoarserResolutionBandwidth()
        {
            // End to end: the frame produced from a gated block reports the RBW of the gate,
            // because the gate really did shorten the record it was computed from.
            using (IqBlock block = TwoTonesInSequence())
            {
                var computer = new SpectrumComputer(WindowType.Hann, null, null);
                SpectrumFrame ungated = computer.Compute(block);

                double quarter = Length / 4.0 / SampleRateHz;

                using (IqBlock gated = new TimeGate(0.0, quarter).Apply(block))
                {
                    SpectrumFrame frame = computer.Compute(gated);

                    Assert.Equal(4.0, frame.ResolutionBandwidthHz / ungated.ResolutionBandwidthHz, 3);
                }
            }
        }

        [Fact]
        public void AGateSelectsTheSamplesItSaysItDoes()
        {
            using (IqBlock block = Ramp(1000))
            {
                var gate = new TimeGate(100.0 / SampleRateHz, 250.0 / SampleRateHz);

                using (IqBlock gated = gate.Apply(block))
                {
                    Assert.Equal(250, gated.SampleCount);

                    // The ramp makes each sample identifiable, so the offset is checked rather
                    // than assumed.
                    Assert.Equal(100.0f, gated.GetSample(0).I, 3);
                    Assert.Equal(349.0f, gated.GetSample(249).I, 3);
                }
            }
        }

        [Fact]
        public void AGateRunningPastTheEndIsTruncatedRatherThanRefused()
        {
            // "From here to the end" is a legitimate thing to ask for, and the annotation then
            // reports the length actually analysed.
            using (IqBlock block = Ramp(1000))
            {
                var gate = new TimeGate(900.0 / SampleRateHz, 500.0 / SampleRateHz);

                using (IqBlock gated = gate.Apply(block))
                {
                    Assert.Equal(100, gated.SampleCount);
                }
            }
        }

        [Fact]
        public void TheGateMovesTheTriggerOffsetWithIt()
        {
            // The first sample of the gated record is later than the first sample of the original,
            // so the trigger is that much further back. REQ-DAT-002 wants the relationship kept.
            using (IqBlock block = Ramp(1000))
            {
                double delay = 100.0 / SampleRateHz;

                using (IqBlock gated = new TimeGate(delay, 100.0 / SampleRateHz).Apply(block))
                {
                    Assert.Equal(block.TriggerOffsetSeconds - delay, gated.TriggerOffsetSeconds, 12);
                }
            }
        }

        [Fact]
        public void AGateOutsideTheRecordSaysSo()
        {
            using (IqBlock block = Ramp(100))
            {
                var gate = new TimeGate(1.0, 0.1);

                ArgumentException failure = Assert.Throws<ArgumentException>(() => gate.Apply(block));
                Assert.Contains("selects nothing", failure.Message);
            }
        }

        [Fact]
        public void TheArgumentsAreChecked()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new TimeGate(-1.0, 1.0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new TimeGate(0.0, 0.0));
            Assert.Throws<ArgumentNullException>(() => new TimeGate(0.0, 1.0).Apply(null));
        }

        private static double ToneFrequency(int bin) => 1e9 + bin * SampleRateHz / Length;

        private static double LevelAt(SpectrumFrame frame, double hertz)
        {
            int index = (int)Math.Round((hertz - frame.StartFrequencyHz) / frame.BinWidthHz);
            index = Math.Max(0, Math.Min(frame.PointCount - 1, index));
            return frame.LevelsDbm[index];
        }

        /// <summary>One tone in the first half of the record and a different one in the second.</summary>
        private static IqBlock TwoTonesInSequence()
        {
            IqBlock block = Rent(Length);
            Span<float> samples = block.GetSamples();

            for (int n = 0; n < Length; n++)
            {
                int bin = n < Length / 2 ? FirstToneBin : SecondToneBin;
                double phase = 2.0 * Math.PI * bin * n / Length;

                samples[n * 2] = (float)(0.5 * Math.Cos(phase));
                samples[n * 2 + 1] = (float)(0.5 * Math.Sin(phase));
            }

            return block;
        }

        /// <summary>A ramp, so each sample is identifiable by value.</summary>
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
                sequenceNumber: 0,
                acquiredUtc: DateTime.UtcNow,
                triggerOffsetSeconds: 1e-3,
                triggerCorrectionsApplied: true,
                source: new FrontEndId("test"),
                extended: null));
    }
}
