using System;
using OpenVSA.Core;
using OpenVSA.Dsp.Spectrum;
using OpenVSA.Dsp.Windowing;
using Xunit;

namespace OpenVSA.Dsp.Tests
{
    /// <summary>
    /// <c>REQ-NFR-002</c>: a pooled frame reuses its buffer, and reading one past its lease throws
    /// instead of returning another frame's spectrum.
    /// </summary>
    public class PooledFrameLifetimeTests
    {
        private const int Points = 4096;

        [Fact]
        public void APooledFrameReusesItsBuffer()
        {
            SampleBufferPool.Instance.Clear();

            var computer = new SpectrumComputer(WindowType.FlatTop, null, null) { PoolFrames = true };

            using (IqBlock block = Block())
            {
                SpectrumFrame first = computer.Compute(block);
                first.Release();

                SpectrumFrame second = computer.Compute(block);

                // The whole point: the second frame is drawn from the pool rather than the heap.
                // Without this assertion the test passes against an implementation that allocates
                // every time and merely calls the pool's methods.
                Assert.True(SampleBufferPool.Instance.Hits >= 1);

                second.Release();
            }
        }

        [Fact]
        public void ReadingAReleasedFrameThrowsRatherThanReturningAnotherFramesSpectrum()
        {
            var computer = new SpectrumComputer(WindowType.FlatTop, null, null) { PoolFrames = true };

            using (IqBlock block = Block())
            {
                SpectrumFrame frame = computer.Compute(block);
                frame.Release();

                // This is the property that makes pooling safe to adopt at all. A consumer that
                // forgot to Retain gets an exception at its own first read, with a stack trace
                // pointing at it -- not a plausible spectrum belonging to a later frame, which
                // nothing would ever detect.
                Assert.Throws<ObjectDisposedException>(() =>
                {
                    ReadOnlySpan<float> complex = frame.Complex;
                    _ = complex.Length;
                });

                Assert.Throws<ObjectDisposedException>(() =>
                {
                    ReadOnlySpan<float> levels = frame.LevelsDbm;
                    _ = levels.Length;
                });
            }
        }

        [Fact]
        public void ARetainedFrameSurvivesTheProducersRelease()
        {
            var computer = new SpectrumComputer(WindowType.FlatTop, null, null) { PoolFrames = true };

            using (IqBlock block = Block())
            {
                SpectrumFrame frame = computer.Compute(block);

                frame.Retain();
                frame.Release();

                // Still readable: the consumer holds a share. This is the case the protocol exists
                // to serve, and a design that threw here would be unusable.
                Assert.Equal(Points, frame.LevelsDbm.Length);

                frame.Release();
                Assert.Throws<ObjectDisposedException>(() => _ = frame.Complex.Length);
            }
        }

        [Fact]
        public void APooledFrameReportsItsOwnPointsAndNotTheBucketSize()
        {
            var computer = new SpectrumComputer(WindowType.FlatTop, null, null) { PoolFrames = true };

            using (IqBlock block = Block())
            {
                SpectrumFrame pooled = computer.Compute(block);

                var plain = new SpectrumComputer(WindowType.FlatTop, null, null);
                SpectrumFrame unpooled = plain.Compute(block);

                // A pooled buffer is rounded up to a power-of-two bucket, so deriving the point
                // count from the array length -- which is what the frame used to do -- would report
                // thousands of extra bins holding the previous tenant's data.
                Assert.Equal(unpooled.PointCount, pooled.PointCount);
                Assert.Equal(unpooled.Complex.Length, pooled.Complex.Length);
                Assert.Equal(unpooled.LevelsDbm.Length, pooled.LevelsDbm.Length);

                for (int i = 0; i < unpooled.PointCount; i++)
                {
                    Assert.Equal(unpooled.LevelsDbm[i], pooled.LevelsDbm[i], 5);
                }

                pooled.Release();
            }
        }

        [Fact]
        public void AnUnpooledFrameIgnoresTheProtocol()
        {
            var computer = new SpectrumComputer(WindowType.FlatTop, null, null);

            using (IqBlock block = Block())
            {
                SpectrumFrame frame = computer.Compute(block);

                Assert.False(frame.IsPooled);

                // Release and Retain are no-ops on a frame that owns its array, so a consumer that
                // honours the protocol works with either kind and never has to ask which it has.
                frame.Release();
                frame.Release();

                Assert.Equal(Points, frame.LevelsDbm.Length);
            }
        }

        private static IqBlock Block()
        {
            var metadata = new IqBlockMetadata(
                Points, 2.0e6, 1.0e9, false, 1.0, 0.0, 1L,
                new DateTime(2026, 7, 29, 0, 0, 0, DateTimeKind.Utc), 0.0, false,
                new FrontEndId("test"), null);

            IqBlock block = IqBlock.Rent(metadata);
            Span<float> samples = block.GetSamples();

            for (int n = 0; n < Points; n++)
            {
                samples[n * 2] = (float)Math.Cos(0.125 * 2.0 * Math.PI * n);
                samples[n * 2 + 1] = (float)Math.Sin(0.125 * 2.0 * Math.PI * n);
            }

            return block;
        }
    }
}
