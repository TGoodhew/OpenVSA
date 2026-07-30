using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using OpenVSA.Dsp.Spectrum;

namespace OpenVSA.Measurement.Markers
{
    /// <summary>
    /// What a trace's X axis measures (<c>REQ-MKR-004</c>).
    /// </summary>
    /// <remarks>
    /// Coupling needs this and cannot infer it. A spectrum's axis and a time record's axis are both
    /// numbers on the same kind of scale, and nothing in the numbers themselves says that moving a
    /// marker to 1.003 GHz on one has any meaning at all on the other.
    /// </remarks>
    public enum MarkerAxis
    {
        /// <summary>Frequency, in hertz.</summary>
        Frequency = 0,

        /// <summary>Time, in seconds.</summary>
        Time,
    }

    /// <summary>One marker's reading, ready to be displayed (<c>REQ-MKR-006</c>).</summary>
    public sealed class MarkerReadout
    {
        internal MarkerReadout(Marker marker, MarkerAxis axis, MarkerReading reading)
        {
            Marker = marker;
            Axis = axis;
            Reading = reading;
        }

        /// <summary>The marker.</summary>
        public Marker Marker { get; }

        /// <summary>What its trace's X axis measures.</summary>
        public MarkerAxis Axis { get; }

        /// <summary>The reading itself.</summary>
        public MarkerReading Reading { get; }

        /// <summary>The trace letter.</summary>
        public char TraceLetter => Marker.TraceLetter;

        /// <summary>Whether this marker is the active one.</summary>
        public bool IsActive { get; internal set; }

        /// <summary>
        /// The line a surface draws: label, X and Y.
        /// </summary>
        /// <remarks>
        /// <c>REQ-MKR-006</c> requires the Markers window row and the above-grid readout to agree,
        /// "since two independently computed readouts drifting apart is the failure this guards
        /// against". They agree because there is one of these and both surfaces render it —
        /// asserting agreement between two formatters would be testing that someone remembered to
        /// change both.
        /// </remarks>
        public string Text =>
            HasValue
                ? Label + "  " + XText + "  " + YText
                : Label + "  " + NoValue;

        /// <summary>Whether there is a reading to show.</summary>
        public bool HasValue => Reading.IsValid;

        /// <summary>What a surface shows in place of a value it does not have.</summary>
        /// <remarks>
        /// One spelling, because two surfaces inventing their own is how the above-grid readout came
        /// to say <c>NAN</c> where the window said <c>--</c>. A reader seeing both at once should not
        /// have to work out whether they mean the same thing.
        /// </remarks>
        public const string NoValue = "--";

        /// <summary>How the marker names itself: <c>Mkr 1</c>.</summary>
        public string Label => Marker.WindowLabel;

        /// <summary>
        /// The X reading, in the units of its trace's axis.
        /// </summary>
        /// <remarks>
        /// The three parts are exposed separately so that a surface can lay them out its own way
        /// without formatting them its own way. The above-grid readout puts the level on a second
        /// line, because on one it collides with the trace format and resolution bandwidth beside it
        /// — a layout difference, where formatting the numbers again was a <em>value</em> difference,
        /// and it is values that <c>REQ-MKR-006</c> requires to agree.
        /// </remarks>
        public string XText =>
            !HasValue
                ? NoValue
                : Axis == MarkerAxis.Time
                    ? (Reading.XHz * 1e6).ToString("0.###", CultureInfo.CurrentCulture) + " us"
                    : (Reading.XHz / 1e6).ToString("0.######", CultureInfo.CurrentCulture) + " MHz";

        /// <summary>The Y reading, in dBm, or dB for a delta marker.</summary>
        public string YText =>
            !HasValue
                ? NoValue
                : Reading.YDbm.ToString("0.00", CultureInfo.CurrentCulture) +
                  (Marker.Type == MarkerType.Delta ? " dB" : " dBm");

        /// <inheritdoc />
        public override string ToString() => Text;
    }

    /// <summary>
    /// Every marker on every trace, with the coupling and readouts that span them
    /// (<c>REQ-MKR-002</c>, <c>REQ-MKR-004</c>, <c>REQ-MKR-006</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Nothing here caps the number of traces.</strong> <c>REQ-MKR-002</c> puts the limit
    /// at twenty markers per trace and bounds the trace count "only by available memory", so sets
    /// are created on demand and held in a dictionary. A constant ceiling here — even a generous
    /// one — would be the fixed cap the requirement's criterion tests for.
    /// </para>
    /// <para>
    /// <strong>Coupling matches on number and on what the axis measures, never on index or
    /// pixel.</strong> Moving marker 3 to 1.003 GHz moves every other marker 3 to <em>that
    /// frequency</em>, which on a trace with a different point count is a different bin. Coupling
    /// by sample index is the implementation that passes a test where both traces happen to have
    /// the same length, and <c>REQ-MKR-004</c>'s criterion exists to catch it.
    /// </para>
    /// <para>
    /// <strong>Incommensurate traces are left alone, not coupled on a shared number.</strong> A
    /// time trace has no 1.003 GHz. Moving its marker 3 to the number 1.003×10⁹ would put it at a
    /// coordinate that is arithmetically valid and physically meaningless, which is worse than not
    /// moving it.
    /// </para>
    /// </remarks>
    public sealed class MarkerCollection
    {
        private readonly Dictionary<char, MarkerSet> _sets = new Dictionary<char, MarkerSet>();
        private readonly Dictionary<char, SpectrumFrame> _frames =
            new Dictionary<char, SpectrumFrame>();
        private readonly Dictionary<char, MarkerAxis> _axes = new Dictionary<char, MarkerAxis>();
        private readonly List<char> _order = new List<char>();

        private char _activeTrace;
        private bool _hasActiveTrace;

        /// <summary>
        /// Whether markers of the same number move together across commensurate traces
        /// (<c>REQ-MKR-004</c>).
        /// </summary>
        public bool Coupled { get; set; }

        /// <summary>The trace letters that have markers or frames, in the order first seen.</summary>
        public IReadOnlyList<char> TraceLetters => new ReadOnlyCollection<char>(_order);

        /// <summary>The number of traces being tracked.</summary>
        public int TraceCount => _order.Count;

        /// <summary>
        /// The markers on a trace, created on first use.
        /// </summary>
        /// <param name="traceLetter">The trace's letter.</param>
        /// <returns>That trace's marker set.</returns>
        public MarkerSet ForTrace(char traceLetter)
        {
            MarkerSet set;

            if (!_sets.TryGetValue(traceLetter, out set))
            {
                set = new MarkerSet(traceLetter);
                _sets[traceLetter] = set;
                Register(traceLetter);
            }

            if (!_hasActiveTrace)
            {
                _activeTrace = traceLetter;
                _hasActiveTrace = true;
            }

            return set;
        }

        /// <summary>Whether a trace has been seen.</summary>
        /// <param name="traceLetter">The trace's letter.</param>
        public bool HasTrace(char traceLetter) => _sets.ContainsKey(traceLetter);

        /// <summary>
        /// Supplies a trace's latest data, which is what markers read against.
        /// </summary>
        /// <param name="traceLetter">The trace's letter.</param>
        /// <param name="frame">The trace's current data.</param>
        /// <param name="axis">What its X axis measures.</param>
        /// <exception cref="ArgumentNullException"><paramref name="frame"/> is null.</exception>
        /// <remarks>
        /// Peak-tracking markers are re-searched here, which is the only place they can be: a
        /// marker that follows a drifting tone has to move when the tone does, and the tone moving
        /// is what a new frame <em>is</em> (<c>REQ-MKR-005</c>).
        /// </remarks>
        public void Update(char traceLetter, SpectrumFrame frame, MarkerAxis axis = MarkerAxis.Frequency)
        {
            if (frame == null)
            {
                throw new ArgumentNullException(nameof(frame));
            }

            // REQ-NFR-002: markers read the full-resolution frame long after it was published --
            // a peak search against decimated data returns a pixel column, not a signal -- so the
            // collection holds a share and the frame it replaces gives one back.
            frame?.Retain();

            SpectrumFrame replaced;
            _frames.TryGetValue(traceLetter, out replaced);

            _frames[traceLetter] = frame;

            if (!ReferenceEquals(replaced, frame))
            {
                replaced?.Release();
            }
            _axes[traceLetter] = axis;
            Register(traceLetter);

            MarkerSet set;

            if (_sets.TryGetValue(traceLetter, out set))
            {
                set.RetrackPeaks(frame);
            }
        }

        /// <summary>The frame a trace was last updated with, or <c>null</c>.</summary>
        /// <param name="traceLetter">The trace's letter.</param>
        public SpectrumFrame FrameOf(char traceLetter)
        {
            SpectrumFrame frame;
            return _frames.TryGetValue(traceLetter, out frame) ? frame : null;
        }

        /// <summary>What a trace's X axis measures; frequency unless said otherwise.</summary>
        /// <param name="traceLetter">The trace's letter.</param>
        public MarkerAxis AxisOf(char traceLetter)
        {
            MarkerAxis axis;
            return _axes.TryGetValue(traceLetter, out axis) ? axis : MarkerAxis.Frequency;
        }

        /// <summary>
        /// Moves a marker, taking its counterparts with it when coupling is on.
        /// </summary>
        /// <param name="marker">The marker to move.</param>
        /// <param name="x">Its new X position, in the units of its trace's axis.</param>
        /// <returns>Every marker that moved, the dragged one first.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="marker"/> is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="x"/> is not finite.</exception>
        public IReadOnlyList<Marker> MoveTo(Marker marker, double x)
        {
            if (marker == null)
            {
                throw new ArgumentNullException(nameof(marker));
            }

            if (double.IsNaN(x) || double.IsInfinity(x))
            {
                throw new ArgumentOutOfRangeException(nameof(x), x, "A position must be finite.");
            }

            var moved = new List<Marker> { marker };

            marker.XHz = x;

            if (!Coupled)
            {
                return new ReadOnlyCollection<Marker>(moved);
            }

            MarkerAxis sourceAxis = AxisOf(marker.TraceLetter);

            foreach (char letter in _order)
            {
                if (letter == marker.TraceLetter)
                {
                    continue;
                }

                if (AxisOf(letter) != sourceAxis || !Covers(letter, x))
                {
                    continue;
                }

                MarkerSet set;

                if (!_sets.TryGetValue(letter, out set))
                {
                    continue;
                }

                foreach (Marker other in set.Markers)
                {
                    if (other.Number != marker.Number)
                    {
                        continue;
                    }

                    other.XHz = x;
                    moved.Add(other);
                }
            }

            return new ReadOnlyCollection<Marker>(moved);
        }

        /// <summary>
        /// Whether a trace's axis reaches a position, so a coupled marker has somewhere to land.
        /// </summary>
        /// <param name="traceLetter">The trace's letter.</param>
        /// <param name="x">The position, in that axis's units.</param>
        /// <remarks>
        /// A trace zoomed into a narrower span than the one being dragged on simply does not cover
        /// the position. Its marker stays where it is rather than being clamped to an edge, because
        /// a marker parked at the end of the axis reads a real value at a place nobody put it.
        /// </remarks>
        public bool Covers(char traceLetter, double x)
        {
            SpectrumFrame frame = FrameOf(traceLetter);

            if (frame == null || frame.PointCount == 0)
            {
                return false;
            }

            double first = frame.FrequencyAt(0);
            double last = frame.FrequencyAt(frame.PointCount - 1);
            double low = Math.Min(first, last);
            double high = Math.Max(first, last);

            return x >= low && x <= high;
        }

        /// <summary>The trace whose markers the above-grid readout follows.</summary>
        /// <exception cref="ArgumentException">That trace has not been seen.</exception>
        public char ActiveTrace
        {
            get { return _activeTrace; }

            set
            {
                if (!_sets.ContainsKey(value) && !_frames.ContainsKey(value))
                {
                    throw new ArgumentException(
                        "Trace " + value + " has no markers and no data.", nameof(value));
                }

                _activeTrace = value;
                _hasActiveTrace = true;
            }
        }

        /// <summary>
        /// The active marker: the selected marker on the active trace, or <c>null</c>.
        /// </summary>
        public Marker Active
        {
            get
            {
                MarkerSet set;

                return _hasActiveTrace && _sets.TryGetValue(_activeTrace, out set)
                    ? set.Selected
                    : null;
            }
        }

        /// <summary>
        /// The readout above the grid, or <c>null</c> when no marker is active
        /// (<c>REQ-MKR-006</c>).
        /// </summary>
        public MarkerReadout ActiveReadout
        {
            get
            {
                Marker marker = Active;

                return marker == null ? null : ReadoutFor(marker, true);
            }
        }

        /// <summary>
        /// Every marker on every trace, for the Markers window (<c>REQ-MKR-006</c>).
        /// </summary>
        /// <returns>
        /// One readout per marker, ordered by trace then by marker number.
        /// </returns>
        /// <remarks>
        /// Every trace, not only the active one — which is what the requirement asks for, and the
        /// reason the window is useful at all: a marker on a trace you are not looking at is
        /// exactly the one you forget about.
        /// </remarks>
        public IReadOnlyList<MarkerReadout> Readouts()
        {
            var readouts = new List<MarkerReadout>();
            Marker active = Active;

            foreach (char letter in _order)
            {
                MarkerSet set;

                if (!_sets.TryGetValue(letter, out set))
                {
                    continue;
                }

                var numbered = new List<Marker>(set.Markers);
                numbered.Sort((a, b) => a.Number.CompareTo(b.Number));

                foreach (Marker marker in numbered)
                {
                    readouts.Add(ReadoutFor(marker, ReferenceEquals(marker, active)));
                }
            }

            return new ReadOnlyCollection<MarkerReadout>(readouts);
        }

        /// <summary>
        /// One marker's readout, computed the one way.
        /// </summary>
        /// <param name="marker">The marker.</param>
        /// <exception cref="ArgumentNullException"><paramref name="marker"/> is null.</exception>
        public MarkerReadout ReadoutFor(Marker marker)
        {
            if (marker == null)
            {
                throw new ArgumentNullException(nameof(marker));
            }

            return ReadoutFor(marker, ReferenceEquals(marker, Active));
        }

        private MarkerReadout ReadoutFor(Marker marker, bool isActive) =>
            new MarkerReadout(
                marker,
                AxisOf(marker.TraceLetter),
                marker.Read(FrameOf(marker.TraceLetter)))
            {
                IsActive = isActive,
            };

        private void Register(char traceLetter)
        {
            if (!_order.Contains(traceLetter))
            {
                _order.Add(traceLetter);
            }
        }
    }
}
