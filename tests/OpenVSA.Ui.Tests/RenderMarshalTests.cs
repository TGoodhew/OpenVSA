using System;
using OpenVSA.Dsp.Spectrum;
using OpenVSA.Dsp.Windowing;
using OpenVSA.Ui.Rendering;
using Xunit;

namespace OpenVSA.Ui.Tests
{
    /// <summary>
    /// The render marshal: one slot, newest wins, and every discarded frame counted
    /// (<c>REQ-NFR-011</c>, <c>REQ-NFR-012</c>).
    /// </summary>
    /// <remarks>
    /// Headless, like the rasteriser tests: the marshal deals in arrays and counters and knows
    /// nothing about a dispatcher, which is what lets the dropping policy be asserted rather than
    /// observed by watching a window.
    /// </remarks>
    public class RenderMarshalTests
    {
        [Fact]
        public void ItDecimatesToTheColumnCountTheViewAsked()
        {
            var marshal = new RenderMarshal { Columns = 100 };

            Assert.True(marshal.Offer(Ramp(1000)));

            TraceSnapshot snapshot = marshal.TakeForRender();

            Assert.NotNull(snapshot);
            Assert.Equal(100, snapshot.Columns);
            Assert.Equal(200, snapshot.MinMax.Length);
            Assert.Equal(1000, snapshot.Spectrum.PointCount);
        }

        [Fact]
        public void TheEnvelopeKeepsBothExtremesOfEveryColumn()
        {
            // The property REQ-NFR-006 exists for, checked at the marshal rather than only at the
            // decimator: a one-point spike must survive the reduction to pixel columns.
            var marshal = new RenderMarshal { Columns = 10 };
            var levels = new float[1000];

            for (int i = 0; i < levels.Length; i++)
            {
                levels[i] = -100.0f;
            }

            levels[555] = -3.0f;

            marshal.Offer(SpectrumFrame.FromLevels(levels, 1e9, 1e3, WindowType.FlatTop, 3.8194));
            TraceSnapshot snapshot = marshal.TakeForRender();

            // To four decimals, not bit-exact: a frame stores calibrated volts as float, so a
            // level put in as decibels comes back through a square root and a logarithm. The
            // residue is around a hundred-thousandth of a dB, which is five orders of magnitude
            // inside anything the amplitude requirements care about.
            Assert.Equal(-3.0, snapshot.MinMax[5 * 2 + 1], 4);
            Assert.Equal(-100.0, snapshot.MinMax[5 * 2], 4);
        }

        [Fact]
        public void CollectingTwiceReturnsNothingTheSecondTime()
        {
            var marshal = new RenderMarshal { Columns = 32 };

            marshal.Offer(Ramp(256));

            Assert.NotNull(marshal.TakeForRender());
            Assert.Null(marshal.TakeForRender());
        }

        [Fact]
        public void AFrameArrivingBeforeTheLastWasDrawn_ReplacesItAndIsCounted()
        {
            var marshal = new RenderMarshal { Columns = 32 };

            marshal.Offer(Ramp(256, startFrequencyHz: 1e9));
            marshal.Offer(Ramp(256, startFrequencyHz: 2e9));
            marshal.Offer(Ramp(256, startFrequencyHz: 3e9));

            TraceSnapshot snapshot = marshal.TakeForRender();

            // Newest wins, and the two it displaced are counted rather than queued: a display can
            // only show the latest, and a queue would show the user a stale spectrum.
            Assert.Equal(3e9, snapshot.Spectrum.StartFrequencyHz);
            Assert.Equal(2, marshal.FramesDropped);
        }

        [Fact]
        public void ItStopsPostingOnceEnoughRenderCallbacksAreOutstanding()
        {
            // REQ-NFR-012 applied to the dispatcher queue: the pump may outrun the UI, but it may
            // not make the backlog the UI has to work through grow without bound.
            var marshal = new RenderMarshal { Columns = 32 };
            int posts = 0;

            for (int i = 0; i < 50; i++)
            {
                if (marshal.Offer(Ramp(256)))
                {
                    posts++;
                }
            }

            Assert.Equal(RenderMarshal.MaximumOutstandingPosts, posts);

            // Collecting frees a slot, so the display recovers rather than seizing up permanently.
            marshal.TakeForRender();
            Assert.True(marshal.Offer(Ramp(256)));
        }

        [Fact]
        public void BeforeTheViewIsLaidOut_ThereIsNothingToDecimateTo()
        {
            var marshal = new RenderMarshal();

            Assert.False(marshal.Offer(Ramp(256)));
            Assert.Null(marshal.TakeForRender());
        }

        [Fact]
        public void ResettingDiscardsThePendingFrame()
        {
            var marshal = new RenderMarshal { Columns = 32 };

            marshal.Offer(Ramp(256));
            marshal.Reset();

            Assert.Null(marshal.TakeForRender());
            Assert.True(marshal.Offer(Ramp(256)));
        }

        [Fact]
        public void ItRefusesAFrameOfNull()
        {
            var marshal = new RenderMarshal { Columns = 32 };
            Assert.Throws<ArgumentNullException>(() => marshal.Offer(null));
        }

        private static SpectrumFrame Ramp(int points, double startFrequencyHz = 1e9)
        {
            var levels = new float[points];

            for (int i = 0; i < points; i++)
            {
                levels[i] = -100.0f + i * 0.05f;
            }

            return SpectrumFrame.FromLevels(levels, startFrequencyHz, 1e3, WindowType.FlatTop, 3.8194);
        }
    }
}
