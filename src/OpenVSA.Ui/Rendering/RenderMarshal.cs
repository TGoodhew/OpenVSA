using System;
using System.Collections.Generic;
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
        private readonly Dictionary<TraceFormat, float[]> _envelopes;

        private readonly Dictionary<TraceFormat, ulong> _seals;

        internal TraceSnapshot(
            SpectrumFrame spectrum, Dictionary<TraceFormat, float[]> envelopes, int columns)
        {
            Spectrum = spectrum;
            _envelopes = envelopes;
            Columns = columns;

            if (ContentSeal.Enabled)
            {
                _seals = new Dictionary<TraceFormat, ulong>(envelopes.Count);

                foreach (KeyValuePair<TraceFormat, float[]> envelope in envelopes)
                {
                    _seals[envelope.Key] = ContentSeal.Of(new ReadOnlySpan<float>(envelope.Value));
                }
            }
        }

        /// <summary>
        /// Whether every envelope still holds what it held when this snapshot was published
        /// (<c>REQ-NFR-011</c>).
        /// </summary>
        /// <remarks>
        /// Always true when <see cref="ContentSeal.Enabled"/> is false, which it is outside the
        /// soak — there is nothing to compare against. False means something wrote to a buffer it
        /// had already handed to the UI, which is the torn frame the requirement forbids.
        /// </remarks>
        public bool SealIntact
        {
            get
            {
                if (_seals == null)
                {
                    return true;
                }

                foreach (KeyValuePair<TraceFormat, float[]> envelope in _envelopes)
                {
                    if (ContentSeal.Of(new ReadOnlySpan<float>(envelope.Value)) !=
                        _seals[envelope.Key])
                    {
                        return false;
                    }
                }

                return true;
            }
        }

        /// <summary>The full-resolution spectrum.</summary>
        public SpectrumFrame Spectrum { get; }

        /// <summary>
        /// Claims a share of the spectrum this snapshot carries (<c>REQ-NFR-002</c>).
        /// </summary>
        /// <remarks>
        /// A snapshot outlives the pump callback that produced it — that is its whole purpose —
        /// so it holds a reference to the frame rather than borrowing one. Anything that stores
        /// the snapshot beyond the call it was handed to must take its own share, and give it back
        /// with <see cref="Release"/>. No-ops on an unpooled frame.
        /// </remarks>
        public void Retain() => Spectrum?.Retain();

        /// <summary>Gives up a share of the spectrum this snapshot carries.</summary>
        public void Release() => Spectrum?.Release();

        /// <summary>Number of pixel columns the envelope was reduced to.</summary>
        public int Columns { get; }

        /// <summary>The formats this snapshot carries an envelope for.</summary>
        public IEnumerable<TraceFormat> Formats => _envelopes.Keys;

        /// <summary>
        /// The log-magnitude envelope: <c>Columns × 2</c> values as (minimum, maximum) pairs.
        /// </summary>
        public ReadOnlySpan<float> MinMax => MinMaxFor(TraceFormat.LogMagnitude);

        /// <summary>
        /// The envelope for a format, or an empty span if this snapshot does not carry one.
        /// </summary>
        /// <param name="format">The format.</param>
        /// <remarks>
        /// <strong>Empty is a real answer.</strong> A plot whose format was not among those asked
        /// for when this frame was decimated — because it changed format between the pump thread
        /// reading the list and the frame arriving — must draw nothing rather than draw another
        /// format's geometry, which is the defect this whole mechanism exists to fix. The next
        /// frame will carry it.
        /// </remarks>
        public ReadOnlySpan<float> MinMaxFor(TraceFormat format)
        {
            float[] envelope;

            return _envelopes.TryGetValue(format, out envelope)
                ? new ReadOnlySpan<float>(envelope)
                : ReadOnlySpan<float>.Empty;
        }
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

        private TraceFormat[] _formats = { TraceFormat.LogMagnitude };
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
        /// How points sharing a pixel column are reduced (<c>REQ-UI-072</c>).
        /// </summary>
        /// <remarks>
        /// Read once per frame on the pump thread, as <see cref="Columns"/> is, and written from
        /// the UI thread when the Detectors tab changes it. A detector changed mid-frame produces
        /// one frame drawn with the previous one, which is a frame, not a fault.
        /// </remarks>
        public TraceDetector Detector { get; set; } = TraceDetector.Normal;

        /// <summary>
        /// The formats currently on screen, each of which gets its own envelope.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <strong>Per format, and off the UI thread.</strong> Four windows showing four formats of
        /// one acquisition is <c>REQ-TRC-001</c>'s whole point, and four windows showing one
        /// format's geometry under four labels is what happens if the envelope is built once from
        /// the log magnitude. Building them here rather than in each plot keeps the per-point work
        /// off the dispatcher, which is <c>REQ-NFR-010</c>.
        /// </para>
        /// <para>
        /// Set from the UI thread when a trace window opens, closes or changes format. Read once
        /// per frame on the pump thread, so a format changed mid-frame costs one frame drawn
        /// without it — a dropped frame, not a mismatch.
        /// </para>
        /// </remarks>
        public IReadOnlyList<TraceFormat> Formats
        {
            get { return _formats; }

            set
            {
                var wanted = new List<TraceFormat>();

                if (value != null)
                {
                    foreach (TraceFormat format in value)
                    {
                        // Only the line formats. IQ is two values per point and a constellation,
                        // not a curve; an envelope of its interleaved pairs would mean nothing.
                        if (TraceAxis.IsLineTrace(format) && !wanted.Contains(format))
                        {
                            wanted.Add(format);
                        }
                    }
                }

                if (wanted.Count == 0)
                {
                    wanted.Add(TraceFormat.LogMagnitude);
                }

                // Replaced wholesale rather than mutated, so the pump thread reads a complete list
                // or the previous one and never a half-built one.
                Interlocked.Exchange(ref _formats, wanted.ToArray());
            }
        }

        /// <summary>
        /// The format options phase and group delay are computed with (<c>REQ-DSP-044</c>,
        /// <c>REQ-DSP-045</c>).
        /// </summary>
        public TraceFormatOptions FormatOptions { get; set; } = TraceFormatOptions.Default;

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

            TraceFormat[] formats = _formats;
            TraceDetector detector = Detector;
            TraceFormatOptions options = FormatOptions;

            TraceSnapshot snapshot = Decimate(frame, columns, formats, detector, options);

            // REQ-NFR-002: the snapshot outlives this callback and crosses to the UI thread, so it
            // takes its own share of the frame rather than borrowing the pump's -- which the pump
            // gives up the moment every handler returns.
            snapshot.Retain();

            TraceSnapshot superseded;

            lock (_slot)
            {
                superseded = _pending;

                if (superseded != null)
                {
                    Interlocked.Increment(ref _framesDropped);
                }

                _pending = snapshot;
            }

            // Released outside the lock: a coalesced frame is one nothing will ever draw, and its
            // buffer has to go back or the pool drains at exactly the rate frames are dropped.
            superseded?.Release();

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
            TraceSnapshot discarded;

            lock (_slot)
            {
                discarded = _pending;
                _pending = null;
            }

            // Stopping a measurement must not strand the last frame's buffer in a snapshot nobody
            // will collect.
            discarded?.Release();

            Interlocked.Exchange(ref _outstandingPosts, 0);
        }
        /// <summary>
        /// Decimates a frame to a width, producing a snapshot ready to draw.
        /// </summary>
        /// <param name="frame">The full-resolution spectrum.</param>
        /// <param name="columns">Pixel columns to reduce to; at least one.</param>
        /// <param name="formats">The formats to build envelopes for.</param>
        /// <param name="detector">The detector to reduce with.</param>
        /// <param name="options">Phase and group-delay options for the derived formats.</param>
        /// <exception cref="ArgumentNullException">An argument is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="columns"/> is not positive.</exception>
        /// <remarks>
        /// <para>
        /// Public and static because a plot has to be able to do this for itself when it is
        /// resized. The marshal decimates to the width it was told about; a plot that has just
        /// changed width holds a snapshot built for the old one, and
        /// <c>TracePlot.Show</c> rightly refuses it — so with a measurement running the next frame
        /// repairs the display and with nothing running there is no next frame and the trace
        /// vanishes. That was the defect.
        /// </para>
        /// <para>
        /// The same arithmetic the pump thread runs, called from the UI thread on a resize. That is
        /// acceptable exactly because a resize is not a per-frame event: the cost this method
        /// carries is why <c>REQ-NFR-010</c> keeps it off the dispatcher sixty times a second, not
        /// why it may never be called there at all.
        /// </para>
        /// </remarks>
        public static TraceSnapshot Decimate(
            SpectrumFrame frame,
            int columns,
            TraceFormat[] formats,
            TraceDetector detector,
            TraceFormatOptions options)
        {
            if (frame == null)
            {
                throw new ArgumentNullException(nameof(frame));
            }

            if (formats == null)
            {
                throw new ArgumentNullException(nameof(formats));
            }

            if (columns < 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(columns), columns, "A snapshot is decimated to at least one column.");
            }

            // A fresh array per frame rather than a reused scratch, for the reason SpectrumFrame
            // gives: it is published to another thread and must never be written again. At two
            // floats per pixel column it is a few kilobytes per format, not a frame buffer.
            var envelopes = new Dictionary<TraceFormat, float[]>(formats.Length);
            var formatted = new float[frame.PointCount];

            foreach (TraceFormat format in formats)
            {
                var envelope = new float[columns * 2];

                if (format == TraceFormat.LogMagnitude)
                {
                    // The one the frame already holds; formatting it again would be the same
                    // arithmetic twice.
                    TraceEnvelope.Build(
                        frame.LevelsDbm, columns, new Span<float>(envelope), detector,
                        valuesAreDecibels: true);
                }
                else
                {
                    frame.Format(format, new Span<float>(formatted), options);

                    // Whether the average detector converts to power is a property of the format,
                    // not a constant. Averaging volts as though they were decibels — or the other
                    // way round — is the same class of error either way.
                    TraceEnvelope.Build(
                        formatted, columns, new Span<float>(envelope), detector,
                        valuesAreDecibels: false);
                }

                envelopes[format] = envelope;
            }

            return new TraceSnapshot(frame, envelopes, columns);
        }

    }
}
