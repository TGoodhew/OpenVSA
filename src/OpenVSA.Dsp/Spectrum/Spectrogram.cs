using System;
using System.Globalization;

namespace OpenVSA.Dsp.Spectrum
{
    /// <summary>
    /// A scrolling time–frequency history of spectra (<c>REQ-DSP-043</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Whole frames, not extracted levels.</strong> A row is the
    /// <see cref="SpectrumFrame"/> that was computed at that moment, kept as it stands — so
    /// selecting a row hands the spectrum trace back exactly the data it would have shown live,
    /// in whatever format is asked for, with its own axis and its own timestamp. Storing levels
    /// instead would mean rebuilding a frame from a rendering, and every format but the one that
    /// was stored would be lost. Frames are immutable and their arrays are not pooled
    /// (<c>REQ-NFR-011</c>), so keeping a reference costs nothing but the memory it names.
    /// </para>
    /// <para>
    /// <strong>Depth is a memory setting as much as a display one.</strong> A row is
    /// <c>PointCount</c> floats: 200 rows of an 801-point spectrum is 640 kB and unremarkable,
    /// while 200 rows of a 2²⁰-point one is 800 MB. The depth is the only control over that, which
    /// is why it is settable rather than fixed, and why reducing it takes effect at once instead of
    /// waiting for rows to age out.
    /// </para>
    /// <para>
    /// <strong>Row 0 is the oldest.</strong> Time ascends with the index, so a caller drawing a
    /// time axis does not have to reverse anything, and the arithmetic relating a row to an instant
    /// is a straight subtraction. Which edge of the display the newest row is drawn at is a
    /// question for the renderer, and a model that had already answered it would have to be argued
    /// with.
    /// </para>
    /// </remarks>
    public sealed class Spectrogram
    {
        /// <summary>
        /// The history depth a spectrogram starts at, in rows.
        /// </summary>
        /// <remarks>
        /// The same 200 that <c>MeasurementState</c> saves, so a new spectrogram and a recalled one
        /// with nothing set agree.
        /// </remarks>
        public const int DefaultDepth = 200;

        /// <summary>The deepest history that may be asked for, in rows.</summary>
        /// <remarks>
        /// Not a display limit — it is a guard on the multiplication. Depth times point count is
        /// the memory, and a depth arriving from a mis-scaled control should be reported rather
        /// than allocated.
        /// </remarks>
        public const int MaximumDepth = 100000;

        private SpectrumFrame[] _rows;
        private int _count;
        private int _next;

        /// <summary>Creates a spectrogram at the default depth.</summary>
        public Spectrogram()
            : this(DefaultDepth)
        {
        }

        /// <summary>Creates a spectrogram.</summary>
        /// <param name="depth">History depth in rows; from 1 to <see cref="MaximumDepth"/>.</param>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="depth"/> is out of range.</exception>
        public Spectrogram(int depth)
        {
            RequireDepth(depth);
            _rows = new SpectrumFrame[depth];
        }

        /// <summary>
        /// History depth, in rows.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">The value is out of range.</exception>
        /// <remarks>
        /// Reducing it discards the oldest rows immediately, which is the same rule that applies
        /// when a full history takes a new row. Keeping them until they aged out would leave the
        /// memory the setting was changed to reclaim still held.
        /// </remarks>
        public int Depth
        {
            get { return _rows.Length; }

            set
            {
                RequireDepth(value);

                if (value == _rows.Length)
                {
                    return;
                }

                int keep = _count < value ? _count : value;
                var resized = new SpectrumFrame[value];

                // The oldest rows that no longer fit give their share back before they are lost:
                // shrinking the depth must not strand pooled buffers (REQ-NFR-002).
                for (int i = 0; i < _count - keep; i++)
                {
                    Row(i)?.Release();
                }

                // Oldest first into the new store, dropping any that no longer fit.
                for (int i = 0; i < keep; i++)
                {
                    resized[i] = Row(_count - keep + i);
                }

                _rows = resized;
                _count = keep;
                _next = keep == value ? 0 : keep;
            }
        }

        /// <summary>Rows currently held, never more than <see cref="Depth"/>.</summary>
        public int RowCount => _count;

        /// <summary>Whether any spectrum has been added.</summary>
        public bool IsEmpty => _count == 0;

        /// <summary>Total spectra added since the last <see cref="Clear"/>, including discarded ones.</summary>
        /// <remarks>
        /// So a caller can tell a history that has scrolled from one that has merely filled — the
        /// two look identical from <see cref="RowCount"/> alone.
        /// </remarks>
        public long AddedCount { get; private set; }

        /// <summary>The most recent row, or <c>null</c> when empty.</summary>
        public SpectrumFrame Newest => _count == 0 ? null : Row(_count - 1);

        /// <summary>The oldest row still held, or <c>null</c> when empty.</summary>
        public SpectrumFrame Oldest => _count == 0 ? null : Row(0);

        /// <summary>
        /// Adds a spectrum as the newest row, discarding the oldest when the history is full.
        /// </summary>
        /// <param name="frame">The spectrum; retained by reference.</param>
        /// <exception cref="ArgumentNullException"><paramref name="frame"/> is null.</exception>
        public void Add(SpectrumFrame frame)
        {
            if (frame == null)
            {
                throw new ArgumentNullException(nameof(frame));
            }

            // REQ-NFR-002: the history keeps a frame for as long as it is on screen, which is far
            // beyond the callback that produced it, so it takes its own share of the buffer. The
            // row being overwritten is one nothing can reach any more, and its share goes back --
            // a ring that only ever retained would hold `depth` buffers out of the pool for ever.
            frame.Retain();

            SpectrumFrame evicted = _rows[_next];

            _rows[_next] = frame;
            _next = (_next + 1) % _rows.Length;

            evicted?.Release();

            if (_count < _rows.Length)
            {
                _count++;
            }

            AddedCount++;
        }

        /// <summary>
        /// A row by index, oldest first.
        /// </summary>
        /// <param name="index">Row index, 0 for the oldest and <see cref="RowCount"/> − 1 for the newest.</param>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is outside the history.</exception>
        public SpectrumFrame Row(int index)
        {
            if (index < 0 || index >= _count)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(index), index,
                    _count == 0
                        ? "The spectrogram is empty."
                        : "The spectrogram holds rows 0 to " +
                          (_count - 1).ToString(CultureInfo.InvariantCulture) + ".");
            }

            // The oldest sits one past the write cursor once the ring has wrapped.
            int oldest = _count == _rows.Length ? _next : 0;

            return _rows[(oldest + index) % _rows.Length];
        }

        /// <summary>Discards the whole history.</summary>
        public void Clear()
        {
            // Released, not merely forgotten: a stale reference to a full-depth history of
            // 2^20-point frames is most of a gigabyte the collector cannot reach. And with
            // REQ-NFR-002's pooling, each row also holds a share of a pooled buffer that has to go
            // back -- forgetting them would drain the pool one clear at a time.
            for (int i = 0; i < _rows.Length; i++)
            {
                _rows[i]?.Release();
            }

            Array.Clear(_rows, 0, _rows.Length);

            _count = 0;
            _next = 0;
            AddedCount = 0;
        }

        /// <summary>
        /// The row nearest an instant — what a trace-select marker resolves to
        /// (<c>REQ-DSP-043</c>).
        /// </summary>
        /// <param name="utc">The instant to select; must be UTC.</param>
        /// <returns>A row index, clamped to the history that exists.</returns>
        /// <exception cref="InvalidOperationException">The spectrogram is empty.</exception>
        /// <exception cref="ArgumentException"><paramref name="utc"/> is not UTC.</exception>
        /// <remarks>
        /// Nearest, and clamped rather than refused. A marker dragged past the end of the history
        /// is a gesture with an obvious meaning — the oldest or newest row — and a marker that
        /// stopped selecting anything at the edge of the display would look like a broken drag.
        /// Rows are searched linearly: a history is a few hundred rows and the search happens once
        /// per gesture, where a binary search would be a second assumption about the timestamps
        /// being ordered.
        /// </remarks>
        public int RowIndexAt(DateTime utc)
        {
            if (utc.Kind != DateTimeKind.Utc)
            {
                throw new ArgumentException("An instant must be UTC.", nameof(utc));
            }

            if (_count == 0)
            {
                throw new InvalidOperationException(
                    "An empty spectrogram has no row to select.");
            }

            int nearest = 0;
            long best = long.MaxValue;

            for (int i = 0; i < _count; i++)
            {
                long distance = Math.Abs((Row(i).AcquiredUtc - utc).Ticks);

                if (distance < best)
                {
                    best = distance;
                    nearest = i;
                }
            }

            return nearest;
        }

        /// <summary>
        /// How far before the newest row a row was acquired, in seconds.
        /// </summary>
        /// <param name="index">Row index, oldest first.</param>
        /// <returns>Zero for the newest row and a positive number for older ones.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is outside the history.</exception>
        /// <remarks>
        /// The time axis a display draws: a spectrogram's vertical scale is age, not wall clock,
        /// and deriving it here keeps the sign in one place.
        /// </remarks>
        public double SecondsBeforeNewest(int index) =>
            (Newest.AcquiredUtc - Row(index).AcquiredUtc).TotalSeconds;

        /// <summary>The span of time the history covers, in seconds.</summary>
        public double HistorySeconds =>
            _count < 2 ? 0.0 : (Newest.AcquiredUtc - Oldest.AcquiredUtc).TotalSeconds;

        private static void RequireDepth(int depth)
        {
            if (depth < 1 || depth > MaximumDepth)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(depth), depth,
                    "A spectrogram depth must be from 1 to " +
                    MaximumDepth.ToString(CultureInfo.InvariantCulture) +
                    " rows; depth multiplied by point count is the memory it holds.");
            }
        }
    }
}
