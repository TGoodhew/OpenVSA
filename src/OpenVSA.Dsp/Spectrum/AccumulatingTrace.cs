using System;

namespace OpenVSA.Dsp.Spectrum
{
    /// <summary>
    /// A trace's format and its accumulator, which are two independent settings
    /// (<c>REQ-TRC-001a</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The separation exists so that these two behave differently, and this is where the
    /// difference lives.</strong> Changing <see cref="Format"/> recomputes nothing and discards
    /// nothing — <c>REQ-TRC-001</c>'s rule — so a spectrogram built over a minute of acquisitions
    /// survives a switch from log magnitude to phase. Changing <see cref="Accumulator"/> discards
    /// the history, because the history is of the accumulator: rows of spectra are not rows of a
    /// persistence map, and pretending otherwise would present one mode's data under another
    /// mode's name.
    /// </para>
    /// <para>
    /// Had the three accumulating modes been left in <see cref="TraceFormat"/>, as the obvious
    /// reading of a product's menu would suggest, this distinction would have had nowhere to live:
    /// every format change would have had to decide whether it was also an accumulator change, and
    /// the rule that a format change recomputes nothing would have had an exception in it.
    /// </para>
    /// <para>
    /// Only <see cref="TraceAccumulator.Spectrogram"/> has a store here yet. Digital Persistence
    /// and Cumulative History are declared by <c>REQ-TRC-001a</c> and drawn by <c>REQ-UI-054</c>;
    /// they accumulate differently and get their own stores when that requirement lands. Selecting
    /// one now is accepted and starts nothing, which is the honest behaviour for a mode whose
    /// display does not exist — rather than quietly accumulating into a spectrogram and drawing it
    /// under the wrong name.
    /// </para>
    /// </remarks>
    public sealed class AccumulatingTrace
    {
        private readonly Spectrogram _spectrogram;
        private TraceAccumulator _accumulator;

        /// <summary>Creates a trace with no accumulation, at the default spectrogram depth.</summary>
        public AccumulatingTrace()
            : this(Spectrogram.DefaultDepth)
        {
        }

        /// <summary>Creates a trace with no accumulation.</summary>
        /// <param name="spectrogramDepth">History depth for the spectrogram, in rows.</param>
        /// <exception cref="ArgumentOutOfRangeException">The depth is out of range.</exception>
        public AccumulatingTrace(int spectrogramDepth)
        {
            _spectrogram = new Spectrogram(spectrogramDepth);
        }

        /// <summary>
        /// The display format (<c>REQ-DSP-041</c>). Changing it disturbs no accumulation.
        /// </summary>
        public TraceFormat Format { get; set; }

        /// <summary>
        /// The accumulating mode (<c>REQ-TRC-001a</c>). Changing it discards what was accumulated.
        /// </summary>
        /// <remarks>
        /// Setting it to the value it already holds is not a change and discards nothing — a
        /// control that reasserts its own state on every repaint would otherwise erase the history
        /// it was drawing.
        /// </remarks>
        public TraceAccumulator Accumulator
        {
            get { return _accumulator; }

            set
            {
                if (value == _accumulator)
                {
                    return;
                }

                _accumulator = value;
                _spectrogram.Clear();
            }
        }

        /// <summary>The time–frequency history, empty unless the spectrogram is selected.</summary>
        public Spectrogram Spectrogram => _spectrogram;

        /// <summary>The most recent spectrum added, or <c>null</c> before the first.</summary>
        /// <remarks>
        /// Kept whatever the accumulator is, because a trace always has a current acquisition to
        /// draw; the accumulator governs what is drawn <em>as well</em>, not instead.
        /// </remarks>
        public SpectrumFrame Latest { get; private set; }

        /// <summary>
        /// Takes one acquisition, accumulating it where the mode calls for it.
        /// </summary>
        /// <param name="frame">The computed spectrum.</param>
        /// <exception cref="ArgumentNullException"><paramref name="frame"/> is null.</exception>
        public void Add(SpectrumFrame frame)
        {
            if (frame == null)
            {
                throw new ArgumentNullException(nameof(frame));
            }

            Latest = frame;

            if (_accumulator == TraceAccumulator.Spectrogram)
            {
                _spectrogram.Add(frame);
            }
        }

        /// <summary>
        /// The spectrum a trace-select marker at an instant resolves to
        /// (<c>REQ-DSP-043</c>).
        /// </summary>
        /// <param name="utc">The instant the marker sits at; must be UTC.</param>
        /// <returns>The history row's own frame, exactly as it was computed.</returns>
        /// <exception cref="InvalidOperationException">No spectrogram history has been accumulated.</exception>
        /// <exception cref="ArgumentException"><paramref name="utc"/> is not UTC.</exception>
        public SpectrumFrame SelectRowAt(DateTime utc)
        {
            if (_spectrogram.IsEmpty)
            {
                throw new InvalidOperationException(
                    _accumulator == TraceAccumulator.Spectrogram
                        ? "No acquisitions have been accumulated yet."
                        : "The trace-select marker needs a spectrogram; this trace's accumulator " +
                          "is " + _accumulator + ".");
            }

            return _spectrogram.Row(_spectrogram.RowIndexAt(utc));
        }
    }
}
