using System;
using OpenVSA.Core;
using OpenVSA.Dsp.Spectrum;
using OpenVSA.Dsp.Windowing;
using OpenVSA.Ui.Rendering;
using Xunit;

namespace OpenVSA.Ui.Tests
{
    /// <summary>
    /// <c>REQ-NFR-002</c> where it meets <c>REQ-NFR-012</c>: a frame the marshal coalesces away
    /// gives its pooled buffer back.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The marshal holds one slot, not a queue, and a frame arriving while another waits replaces
    /// it. That frame is one nothing will ever draw — and its buffer has to go back, because
    /// otherwise the pool drains at exactly the rate frames are dropped, which is highest when the
    /// display is already struggling. A leak that only appears under load, and only on the machine
    /// least able to afford it.
    /// </para>
    /// <para>
    /// Asserted on the lease rather than on a collection count, because a garbage-collection
    /// measurement in a unit test measures the runner.
    /// </para>
    /// </remarks>
    public class MarshalReleasesCoalescedFramesTests
    {
        private const int Points = 4096;

        [Fact]
        public void ACoalescedFrameReturnsItsBuffer()
        {
            var marshal = new RenderMarshal { Columns = 200 };
            var computer = new SpectrumComputer(WindowType.FlatTop, null, null) { PoolFrames = true };

            using (IqBlock block = Block())
            {
                SpectrumFrame first = computer.Compute(block);
                SpectrumFrame second = computer.Compute(block);

                marshal.Offer(first);

                // The pump's own share, given up as SpectrumEngine.Publish does.
                first.Release();

                // Still alive: the marshal holds it as the pending snapshot.
                Assert.Equal(Points, first.LevelsDbm.Length);

                marshal.Offer(second);
                second.Release();

                Assert.Equal(1, marshal.FramesDropped);

                // The coalesced frame is gone, and reading it says so rather than handing back the
                // buffer that now belongs to the frame which displaced it.
                Assert.Throws<ObjectDisposedException>(() => _ = first.Complex.Length);

                // The one that displaced it is intact and is what the display will collect.
                TraceSnapshot taken = marshal.TakeForRender();
                Assert.Same(second, taken.Spectrum);
                Assert.Equal(Points, taken.Spectrum.LevelsDbm.Length);

                taken.Release();
            }
        }

        [Fact]
        public void ResettingReleasesTheFrameNobodyWillCollect()
        {
            var marshal = new RenderMarshal { Columns = 200 };
            var computer = new SpectrumComputer(WindowType.FlatTop, null, null) { PoolFrames = true };

            using (IqBlock block = Block())
            {
                SpectrumFrame frame = computer.Compute(block);

                marshal.Offer(frame);
                frame.Release();

                // Stopping a measurement must not strand the last frame's buffer in a snapshot
                // nothing will ever take.
                marshal.Reset();

                Assert.Throws<ObjectDisposedException>(() => _ = frame.Complex.Length);
                Assert.Null(marshal.TakeForRender());
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
