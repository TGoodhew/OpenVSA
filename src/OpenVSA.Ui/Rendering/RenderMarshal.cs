using System;
using System.Threading;
using OpenVSA.Core.Threading;
using OpenVSA.Dsp.Spectrum;

namespace OpenVSA.Ui.Rendering
{
    /// <summary>
    /// A computed spectrum together with its decimated envelope, ready to rasterise.
    /// </summary>
    /// <remarks>
    /// Both halves are needed and neither replaces the other: the envelope is what gets drawn
    /// (<c>REQ-NFR-006</c>), and the full-resolution frame is what a marker reads, because a peak
    /// search against decimated data returns the position of a pixel column rather than of a signal.
    /// Immutable, per <c>REQ-NFR-011</c>.
    /// </remarks>
    public sealed class TraceSnapshot
    {
        private readonly float[] _minMax;

        internal TraceSnapshot(SpectrumFrame spectrum, float[] minMax, int columns)
        {
            Spectrum = spectrum;
            _minMax = minMax;
            Columns = columns;
        }

        /// <summary>The full-resolution spectrum.</summary>
        public SpectrumFrame Spectrum { get; }

        /// <summary>Number of pixel columns the envelope was reduced to.</summary>
        public int Columns { get; }

        /// <summary>The envelope: <c>Columns × 2</c> values as (minimum, maximum) pairs.</summary>
        public ReadOnlySpan<float> MinMax => new ReadOnlySpan<float>(_minMax);
    }

    /// <summary>
    /// The "render marshal" of <c>REQ-NFR-010</c>: turns frames into render primitives off the UI
    /// thread, and holds the newest one for the UI to collect.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>One slot, not a queue.</strong> <c>REQ-NFR-012</c> prohibits unbounded buffering and
    /// requires deliberate dropping with a visible counter. A display can only ever show the newest
    /// frame, so a frame that arrives while another is waiting replaces it and increments
    /// <see cref="FramesDropped"/>. Queueing them instead would trade memory for latency and end up
    /// showing the user a spectrum that is seconds old.
    /// </para>
    /// <para>
    /// <see cref="Offer"/> runs on the pump thread and <see cref="TakeForRender"/> on the UI thread;
    /// the hand-off is one <see cref="Interlocked.Exchange(ref object, object)"/> of a reference to
    /// an immutable object, so there is nothing for the two threads to tear.
    /// </para>
    /// </remarks>
    public sealed class RenderMarshal
    {
        /// <summary>
        /// Maximum render callbacks allowed to be outstanding on the dispatcher at once.
        /// </summary>
        /// <remarks>
        /// The bound of <c>REQ-NFR-012</c> applied to the dispatcher queue rather than to memory.
        /// More than a couple in flight means the UI is not keeping up, and posting further ones
        /// only makes the backlog it has to work through longer.
        /// </remarks>
        public const int MaximumOutstandingPosts = 4;

        private readonly object _slot = new object();

        private TraceSnapshot _pending;
        private long _framesDropped;
        private int _outstandingPosts;

        /// <summary>Frames discarded because the display had not collected the previous one.</summary>
        public long FramesDropped => Interlocked.Read(ref _framesDropped);

        /// <summary>
        /// Pixel columns to decimate to. Set by the view when its graticule changes width.
        /// </summary>
        /// <remarks>
        /// Read once per frame on the pump thread and written from the UI thread, so a resize
        /// during a frame produces a snapshot at the old width, which
        /// <see cref="TracePlot.Show"/> discards. That is a dropped frame during a drag, not a
        /// mismatch drawn at the wrong scale.
        /// </remarks>
        public int Columns { get; set; }

        /// <summary>
        /// Decimates a frame and makes it the pending one, on the pump thread.
        /// </summary>
        /// <param name="frame">The frame to publish.</param>
        /// <returns>
        /// <c>true</c> if the caller should post a render callback; <c>false</c> if enough are
        /// already outstanding, in which case one of those will collect this frame.
        /// </returns>
        /// <exception cref="ArgumentNullException"><paramref name="frame"/> is null.</exception>
        public bool Offer(SpectrumFrame frame)
        {
            if (frame == null)
            {
                throw new ArgumentNullException(nameof(frame));
            }

            ThreadAffinity.AssertNotOnUiThread("Render marshalling");

            int columns = Columns;

            if (columns <= 0)
            {
                // No graticule yet: the view has not been laid out. Nothing to decimate to.
                return false;
            }

            // A fresh array per frame rather than a reused scratch, for the reason SpectrumFrame
            // gives: it is published to another thread and must never be written again. At two
            // floats per pixel column it is a few kilobytes, not a frame buffer.
            var envelope = new float[columns * 2];
            TraceDecimator.Decimate(frame.LevelsDbm, columns, new Span<float>(envelope));

            lock (_slot)
            {
                if (_pending != null)
                {
                    Interlocked.Increment(ref _framesDropped);
                }

                _pending = new TraceSnapshot(frame, envelope, columns);
            }

            if (Interlocked.Increment(ref _outstandingPosts) <= MaximumOutstandingPosts)
            {
                return true;
            }

            Interlocked.Decrement(ref _outstandingPosts);
            return false;
        }

        /// <summary>
        /// Collects the newest snapshot, on the UI thread.
        /// </summary>
        /// <returns>The snapshot, or <c>null</c> if an earlier callback already took it.</returns>
        /// <remarks>
        /// Every posted callback must call this exactly once, including when it draws nothing:
        /// the outstanding-post count is decremented here, and a callback that returned early
        /// without collecting would leak a slot and eventually stop the display.
        /// </remarks>
        public TraceSnapshot TakeForRender()
        {
            TraceSnapshot snapshot;

            lock (_slot)
            {
                snapshot = _pending;
                _pending = null;
            }

            // Clamped rather than decremented blindly: Reset zeroes the count while callbacks are
            // still in flight, and each of those will arrive here expecting a slot to give back. A
            // count left negative would let the pump post more than the bound allows.
            int outstanding = Interlocked.Decrement(ref _outstandingPosts);
            if (outstanding < 0)
            {
                Interlocked.CompareExchange(ref _outstandingPosts, 0, outstanding);
            }

            return snapshot;
        }

        /// <summary>Discards any pending frame and resets the post count. For stopping a measurement.</summary>
        public void Reset()
        {
            lock (_slot)
            {
                _pending = null;
            }

            Interlocked.Exchange(ref _outstandingPosts, 0);
        }
    }
}
