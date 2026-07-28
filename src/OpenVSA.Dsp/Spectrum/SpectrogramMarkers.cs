using System;

namespace OpenVSA.Dsp.Spectrum
{
    /// <summary>
    /// Which of a spectrogram's two markers (<c>REQ-UI-054</c>).
    /// </summary>
    public enum SpectrogramMarkerKind
    {
        /// <summary>The spectrogram marker: a vertical line, positioned along the frequency axis.</summary>
        Spectrogram = 0,

        /// <summary>The trace-select marker: a horizontal line, positioned along the time axis.</summary>
        TraceSelect,
    }

    /// <summary>
    /// A spectrogram's two markers, on perpendicular axes (<c>REQ-UI-054</c>, <c>REQ-MKR-007</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Each marker moves only along its own axis, and that is enforced here rather than
    /// asserted about the handlers.</strong> <see cref="MoveTo"/> takes a whole position — a bin and
    /// a row — and applies only the coordinate belonging to the marker being moved. A drag is a
    /// two-dimensional gesture whatever the marker is, so a design in which each handler is trusted
    /// to ignore one coordinate is one where a copied line of code breaks the criterion silently.
    /// Passing both and discarding one makes a test able to drag diagonally and prove the other
    /// coordinate did not follow.
    /// </para>
    /// <para>
    /// <strong>Each holds a physical coordinate, not an index.</strong> The spectrogram marker holds
    /// a frequency and the trace-select marker an instant, because both indices move under them: a
    /// re-plan changes how many bins a row has, and every new sweep shifts every row index down by
    /// one once the history is full. A marker stored as row 40 would slide to a different moment
    /// once a second while sitting still, which is the opposite of what a user pinning a marker to
    /// an event wants.
    /// </para>
    /// <para>
    /// <strong>The trace-select marker's job is to select a row, not to read a level.</strong>
    /// <see cref="SelectedRow"/> hands back the whole frame — <c>REQ-MKR-007</c>'s "makes the
    /// spectrum trace show that row's data" — so the spectrum trace shows exactly what it would
    /// have shown live, in whatever format is asked for, rather than a rendering of it.
    /// </para>
    /// </remarks>
    public sealed class SpectrogramMarkers
    {
        private readonly Spectrogram _history;

        private double _frequencyHz = double.NaN;
        private DateTime _selectedUtc = DateTime.MinValue;

        /// <summary>Creates the pair over a history.</summary>
        /// <param name="history">The history they mark; held by reference and read as it grows.</param>
        /// <exception cref="ArgumentNullException"><paramref name="history"/> is null.</exception>
        public SpectrogramMarkers(Spectrogram history)
        {
            if (history == null)
            {
                throw new ArgumentNullException(nameof(history));
            }

            _history = history;
        }

        /// <summary>Whether the history holds anything for the markers to point at.</summary>
        public bool HasRows => _history.RowCount > 0;

        /// <summary>
        /// The spectrogram marker's frequency, in hertz, or NaN before it has been placed.
        /// </summary>
        public double FrequencyHz => HasRows ? Reference.StartFrequencyHz + BinIndex * Reference.BinWidthHz : _frequencyHz;

        /// <summary>
        /// The bin the spectrogram marker sits on, or −1 when there is nothing to sit on.
        /// </summary>
        /// <remarks>
        /// Resolved against the newest row, which is the row whose frequency axis the display is
        /// drawn to. A marker placed before any sweep arrived resolves to the middle of the first
        /// row rather than to bin zero: the centre of the span is where a user who has not yet
        /// aimed at anything would expect to find it, and bin zero is the far left edge.
        /// </remarks>
        public int BinIndex
        {
            get
            {
                if (!HasRows)
                {
                    return -1;
                }

                SpectrumFrame reference = Reference;

                if (double.IsNaN(_frequencyHz))
                {
                    return reference.PointCount / 2;
                }

                int bin = (int)Math.Round(
                    (_frequencyHz - reference.StartFrequencyHz) / reference.BinWidthHz);

                return Clamp(bin, 0, reference.PointCount - 1);
            }
        }

        /// <summary>
        /// The row the trace-select marker sits on, or −1 when the history is empty.
        /// </summary>
        /// <remarks>
        /// Nearest to the instant it holds, by <see cref="Spectrogram.RowIndexAt"/> — so a marker
        /// whose row has aged out of the history lands on the oldest row that remains rather than
        /// disappearing. Before it has been placed it selects the newest row, which is the row the
        /// spectrum trace is already showing.
        /// </remarks>
        public int RowIndex
        {
            get
            {
                if (!HasRows)
                {
                    return -1;
                }

                return _selectedUtc == DateTime.MinValue
                    ? _history.RowCount - 1
                    : _history.RowIndexAt(_selectedUtc);
            }
        }

        /// <summary>
        /// The spectrum the trace-select marker has selected, or <c>null</c> when there is none.
        /// </summary>
        public SpectrumFrame SelectedRow
        {
            get
            {
                int row = RowIndex;

                return row < 0 ? null : _history.Row(row);
            }
        }

        /// <summary>
        /// How far before the newest row the trace-select marker sits, in seconds.
        /// </summary>
        /// <remarks>
        /// Zero on the newest row and positive going back, which is the axis a display draws — the
        /// same sign convention as <see cref="Spectrogram.SecondsBeforeNewest"/>, taken from it
        /// rather than restated.
        /// </remarks>
        public double SecondsBeforeNewest
        {
            get
            {
                int row = RowIndex;

                return row < 0 ? 0.0 : _history.SecondsBeforeNewest(row);
            }
        }

        /// <summary>
        /// Whether a marker is drawn as a vertical line (<c>REQ-UI-054</c>).
        /// </summary>
        /// <param name="which">The marker.</param>
        /// <remarks>
        /// The requirement fixes both orientations and they are perpendicular by construction, not
        /// by two independent decisions that happen to differ: this answers one and
        /// <see cref="IsHorizontal"/> is its negation.
        /// </remarks>
        public static bool IsVertical(SpectrogramMarkerKind which) =>
            which == SpectrogramMarkerKind.Spectrogram;

        /// <summary>Whether a marker is drawn as a horizontal line (<c>REQ-UI-054</c>).</summary>
        /// <param name="which">The marker.</param>
        public static bool IsHorizontal(SpectrogramMarkerKind which) => !IsVertical(which);

        /// <summary>
        /// Moves one marker, applying only the coordinate on its own axis (<c>REQ-UI-054</c>).
        /// </summary>
        /// <param name="which">The marker to move.</param>
        /// <param name="binIndex">Where the gesture landed along the frequency axis.</param>
        /// <param name="rowIndex">Where the gesture landed along the time axis.</param>
        /// <returns>Whether the marker moved.</returns>
        /// <remarks>
        /// Both coordinates are taken and one is discarded. See the type's remarks: this signature
        /// is the criterion, and a pair of single-coordinate setters would put "only along its own
        /// axis" in the caller's hands.
        /// </remarks>
        public bool MoveTo(SpectrogramMarkerKind which, int binIndex, int rowIndex)
        {
            if (!HasRows)
            {
                return false;
            }

            if (which == SpectrogramMarkerKind.Spectrogram)
            {
                SpectrumFrame reference = Reference;

                int bin = Clamp(binIndex, 0, reference.PointCount - 1);
                double frequency = reference.StartFrequencyHz + bin * reference.BinWidthHz;

                if (bin == BinIndex && !double.IsNaN(_frequencyHz))
                {
                    return false;
                }

                _frequencyHz = frequency;
                return true;
            }

            int row = Clamp(rowIndex, 0, _history.RowCount - 1);

            if (row == RowIndex && _selectedUtc != DateTime.MinValue)
            {
                return false;
            }

            _selectedUtc = _history.Row(row).AcquiredUtc;
            return true;
        }

        /// <summary>Puts both markers back where an unplaced pair sits.</summary>
        /// <remarks>
        /// What a change of accumulator calls: the history the markers point into has been
        /// discarded, and a frequency held from the last one would place the marker against an axis
        /// that no longer exists.
        /// </remarks>
        public void Clear()
        {
            _frequencyHz = double.NaN;
            _selectedUtc = DateTime.MinValue;
        }

        /// <summary>The row whose frequency axis the markers are resolved against.</summary>
        private SpectrumFrame Reference => _history.Newest;

        private static int Clamp(int value, int low, int high) =>
            value < low ? low : (value > high ? high : value);
    }
}
