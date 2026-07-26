using System;
using OpenVSA.Core;
using OpenVSA.Dsp.Spectrum;
using OpenVSA.Dsp.Windowing;
using Xunit;

namespace OpenVSA.Dsp.Tests
{
    /// <summary>
    /// <c>REQ-DSP-024</c>'s <em>Max FFT Size</em>: it defaults to 2²⁰, it is configurable, and a
    /// measurement that wants more is bounded rather than failed — visibly.
    /// </summary>
    public class MaxTransformSizeTests
    {
        [Fact]
        public void TheCeilingDefaultsToTwoToTheTwenty()
        {
            // The requirement's stated default, since the reference product's own figure was never
            // obtained.
            Assert.Equal(1048576, SpectrumComputer.DefaultMaxTransformLength);
            Assert.Equal(1 << 20, new SpectrumComputer().MaxTransformLength);
        }

        [Fact]
        public void TheCeilingIsConfigurable()
        {
            var computer = new SpectrumComputer { MaxTransformLength = 4096 };

            Assert.Equal(4096, computer.MaxTransformLength);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(-4096)]
        [InlineData(1000000)]
        [InlineData(3)]
        public void ACeilingThatIsNotAPowerOfTwoIsRefused(int length)
        {
            // A million is the interesting one: silently behaving as 524288 would leave a user
            // wondering why the setting they typed did nothing.
            var computer = new SpectrumComputer();

            ArgumentOutOfRangeException error = Assert.Throws<ArgumentOutOfRangeException>(
                () => computer.MaxTransformLength = length);

            Assert.Contains("power of two", error.Message);
            Assert.Equal(SpectrumComputer.DefaultMaxTransformLength, computer.MaxTransformLength);
        }

        [Theory]
        [InlineData(1021, 2048, 512)]
        [InlineData(1021, 256, 256)]
        [InlineData(4096, 1024, 1024)]
        [InlineData(4096, 8192, 4096)]
        public void TheBoundedLengthIsTheSmallerOfTheRecordAndTheCeiling(
            int samples, int ceiling, int expected)
        {
            Assert.Equal(expected, SpectrumComputer.TransformLengthFor(samples, ceiling));
        }

        [Fact]
        public void AMeasurementPastTheCeilingIsBoundedRatherThanFailed()
        {
            // The criterion: bounded, not refused. A block that wants a bigger transform than the
            // ceiling allows is still a measurement, just a coarser one.
            var computer = new SpectrumComputer(WindowType.FlatTop, null, null)
            {
                MaxTransformLength = 512,
            };

            using (IqBlock block = Tone(4099, 2.5e6))
            {
                SpectrumFrame frame = computer.Compute(block);

                Assert.Equal(512, frame.TransformLength);
                Assert.True(frame.TransformWasCapped);

                // And it is a measurement, not a shape: the tone is still where it was put.
                int peak = frame.IndexOfPeak();

                Assert.True(peak >= 0);
                Assert.True(Math.Abs(frame.FrequencyAt(peak) - (1e9 + 2.5e6)) <= 2.0 * frame.BinWidthHz);
            }
        }

        [Fact]
        public void TheCapCostsResolutionAndTheFrameSaysSo()
        {
            // What the annotation is for. The two frames are the same measurement of the same
            // block; only one of them can resolve what the other cannot, and nothing on the trace
            // itself distinguishes them.
            using (IqBlock block = Tone(4099, 2.5e6))
            {
                SpectrumFrame full = new SpectrumComputer(WindowType.FlatTop, null, null)
                    .Compute(block);
                SpectrumFrame capped =
                    new SpectrumComputer(WindowType.FlatTop, null, null) { MaxTransformLength = 512 }
                        .Compute(block);

                Assert.False(full.TransformWasCapped);
                Assert.True(capped.TransformWasCapped);

                Assert.Equal(4096, full.TransformLength);
                Assert.Equal(512, capped.TransformLength);

                // Eight times the transform is eight times the resolution.
                Assert.Equal(8.0, capped.ResolutionBandwidthHz / full.ResolutionBandwidthHz, 6);
            }
        }

        [Fact]
        public void ARecordShorterThanTheCeilingIsNotMarkedAsCapped()
        {
            var computer = new SpectrumComputer(WindowType.FlatTop, null, null)
            {
                MaxTransformLength = 65536,
            };

            using (IqBlock block = Tone(1021, 2.5e6))
            {
                SpectrumFrame frame = computer.Compute(block);

                Assert.Equal(512, frame.TransformLength);
                Assert.False(frame.TransformWasCapped);
            }
        }

        [Fact]
        public void ACeilingEqualToTheNaturalLengthDoesNotCount()
        {
            // A boundary worth pinning: the ceiling bound nothing, so nothing was lost and nothing
            // should be announced.
            var computer = new SpectrumComputer(WindowType.FlatTop, null, null)
            {
                MaxTransformLength = 512,
            };

            using (IqBlock block = Tone(1021, 2.5e6))
            {
                Assert.False(computer.Compute(block).TransformWasCapped);
            }
        }

        [Fact]
        public void ADerivedTraceRemembersThatItWasCapped()
        {
            // Provenance survives arithmetic. A difference of two capped traces is still a trace
            // measured at a resolution the samples did not have to be limited to.
            var computer = new SpectrumComputer(WindowType.FlatTop, null, null)
            {
                MaxTransformLength = 512,
            };

            using (IqBlock block = Tone(4099, 2.5e6))
            {
                SpectrumFrame frame = computer.Compute(block);
                SpectrumFrame difference = TraceMath.Apply("Subtract", frame, frame);

                Assert.True(difference.TransformWasCapped);
                Assert.Equal(512, difference.TransformLength);
            }
        }

        [Fact]
        public void ANegativeOrZeroSampleCountIsStillRefused()
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => SpectrumComputer.TransformLengthFor(0, 1024));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => SpectrumComputer.TransformLengthFor(1024, 0));
        }

        private static IqBlock Tone(int samples, double offsetHz)
        {
            const double rateHz = 15e6;

            IqBlock block = IqBlock.Rent(new IqBlockMetadata(
                sampleCount: samples,
                sampleRateHz: rateHz,
                centerFrequencyHz: 1e9,
                isBaseband: false,
                fullScaleVolts: 1.0,
                referenceLevelDbm: 0.0,
                sequenceNumber: 1,
                acquiredUtc: new DateTime(2026, 7, 26, 0, 0, 0, DateTimeKind.Utc),
                triggerOffsetSeconds: 0.0,
                triggerCorrectionsApplied: false,
                source: new FrontEndId("test"),
                extended: null));

            Span<float> data = block.GetSamples();
            double cycles = offsetHz / rateHz;

            for (int n = 0; n < samples; n++)
            {
                double angle = 2.0 * Math.PI * cycles * n;

                data[n * 2] = (float)Math.Cos(angle);
                data[n * 2 + 1] = (float)Math.Sin(angle);
            }

            return block;
        }
    }
}
