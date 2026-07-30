using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using OpenVSA.Dsp.Spectrum;

// Aliased because this type has a PeakSearch *method*, which would otherwise shadow the class.
using PeakFinder = OpenVSA.Dsp.Spectrum.PeakSearch;

namespace OpenVSA.Measurement.Markers
{
    /// <summary>
    /// The markers on one trace (<c>REQ-MKR-001</c>, <c>REQ-MKR-002</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Twenty per trace, and the twenty-first is refused with a named error rather than silently
    /// dropped. The limit is per trace: the trace count itself is bounded only by memory, so
    /// nothing here caps how many sets may exist.
    /// </para>
    /// <para>
    /// <strong>Reference integrity is enforced at deletion.</strong> A delta marker whose reference
    /// has been removed would read a difference from nothing, so removing a marker that another one
    /// references is refused and says which marker depends on it. The requirement allows re-homing
    /// the dependant instead; refusing is the choice that never silently changes what a delta
    /// marker means.
    /// </para>
    /// </remarks>
    public sealed class MarkerSet
    {
        /// <summary>Markers permitted on one trace (<c>REQ-MKR-002</c>).</summary>
        public const int MaximumPerTrace = 20;

        private readonly List<Marker> _markers = new List<Marker>();
        private readonly ReadOnlyCollection<Marker> _readOnly;

        /// <summary>Creates an empty set for a trace.</summary>
        /// <param name="traceLetter">The trace's letter (<c>REQ-UI-020</c>).</param>
        public MarkerSet(char traceLetter = 'A')
        {
            TraceLetter = traceLetter;
            _readOnly = new ReadOnlyCollection<Marker>(_markers);
        }

        /// <summary>The letter of the trace these markers are on.</summary>
        public char TraceLetter { get; }

        /// <summary>The markers, in creation order.</summary>
        /// <remarks>
        /// <para>
        /// One wrapper, made once. <see cref="ReadOnlyCollection{T}"/> is a live view over the list
        /// rather than a copy of it, so a single instance stays correct for the life of the set and
        /// there is nothing to invalidate.
        /// </para>
        /// <para>
        /// <strong>It used to be built on every read</strong>, and this is read from the drawing
        /// path: <c>ShellWindow.RefreshMarkers</c> and the chooser it fills touch it five times for
        /// every frame drawn, sixty times a second. That is garbage rather than a leak, but it is
        /// garbage created by a property that looks free at the call site — which is the kind that
        /// does not get noticed.
        /// </para>
        /// </remarks>
        public IReadOnlyList<Marker> Markers => _readOnly;

        /// <summary>The selected marker, or <c>null</c> if there is none.</summary>
        public Marker Selected => _markers.FirstOrDefault(m => m.IsSelected);

        /// <summary>
        /// Adds a normal marker at a frequency.
        /// </summary>
        /// <param name="xHz">Position, in hertz.</param>
        /// <returns>The new marker, which becomes the selected one.</returns>
        /// <exception cref="InvalidOperationException">The trace already has <see cref="MaximumPerTrace"/> markers.</exception>
        public Marker AddNormal(double xHz) => Add(MarkerType.Normal, xHz, double.NaN, null);

        /// <summary>
        /// Adds a fixed marker, locking the value it reads now.
        /// </summary>
        /// <param name="xHz">Position, in hertz.</param>
        /// <param name="yDbm">Level to lock, in dBm.</param>
        /// <returns>The new marker, which becomes the selected one.</returns>
        /// <exception cref="InvalidOperationException">The trace already has <see cref="MaximumPerTrace"/> markers.</exception>
        public Marker AddFixed(double xHz, double yDbm) =>
            Add(MarkerType.Fixed, xHz, yDbm, null);

        /// <summary>
        /// Adds a delta marker measured from another.
        /// </summary>
        /// <param name="xHz">Position, in hertz.</param>
        /// <param name="reference">The marker to measure from; must not be null.</param>
        /// <returns>The new marker, which becomes the selected one.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="reference"/> is null.</exception>
        /// <exception cref="InvalidOperationException">The trace already has <see cref="MaximumPerTrace"/> markers.</exception>
        public Marker AddDelta(double xHz, Marker reference)
        {
            if (reference == null)
            {
                throw new ArgumentNullException(
                    nameof(reference), "A delta marker must be measured from another marker.");
            }

            return Add(MarkerType.Delta, xHz, double.NaN, reference);
        }

        /// <summary>
        /// Removes a marker.
        /// </summary>
        /// <param name="marker">The marker to remove.</param>
        /// <exception cref="ArgumentNullException"><paramref name="marker"/> is null.</exception>
        /// <exception cref="InvalidOperationException">Another marker references this one.</exception>
        public void Remove(Marker marker)
        {
            if (marker == null)
            {
                throw new ArgumentNullException(nameof(marker));
            }

            Marker dependant = _markers.FirstOrDefault(m => ReferenceEquals(m.Reference, marker));

            if (dependant != null)
            {
                throw new InvalidOperationException(
                    "Marker " + marker.Number.ToString(CultureInfo.CurrentCulture) +
                    " cannot be deleted while marker " +
                    dependant.Number.ToString(CultureInfo.CurrentCulture) +
                    " is measured from it. Delete or re-home marker " +
                    dependant.Number.ToString(CultureInfo.CurrentCulture) + " first.");
            }

            bool wasSelected = marker.IsSelected;
            _markers.Remove(marker);

            if (wasSelected && _markers.Count > 0)
            {
                Select(_markers[_markers.Count - 1]);
            }
        }

        /// <summary>
        /// Points a delta marker at a different reference.
        /// </summary>
        /// <param name="delta">The delta marker to re-home.</param>
        /// <param name="reference">Its new reference.</param>
        /// <exception cref="ArgumentNullException">An argument is null.</exception>
        /// <exception cref="ArgumentException">The marker is not a delta marker, or the reference is the marker itself.</exception>
        /// <remarks>
        /// The operation <c>REQ-MKR-001</c> names as the alternative to refusing a deletion, and
        /// worth having in its own right: changing the reference is how a delta measurement is
        /// re-based without losing the marker's position.
        /// </remarks>
        public void Rehome(Marker delta, Marker reference)
        {
            if (delta == null)
            {
                throw new ArgumentNullException(nameof(delta));
            }

            if (reference == null)
            {
                throw new ArgumentNullException(nameof(reference));
            }

            if (delta.Type != MarkerType.Delta)
            {
                throw new ArgumentException(
                    "Only a delta marker measures from another.", nameof(delta));
            }

            if (ReferenceEquals(delta, reference))
            {
                throw new ArgumentException(
                    "A delta marker cannot measure from itself.", nameof(reference));
            }

            delta.Reference = reference;
        }

        /// <summary>Makes a marker the selected one; the others become unselected.</summary>
        /// <param name="marker">The marker to select.</param>
        /// <exception cref="ArgumentException">The marker is not in this set.</exception>
        public void Select(Marker marker)
        {
            if (marker != null && !_markers.Contains(marker))
            {
                throw new ArgumentException("That marker is not on this trace.", nameof(marker));
            }

            foreach (Marker other in _markers)
            {
                other.IsSelected = ReferenceEquals(other, marker);
            }
        }

        /// <summary>Moves the selected marker to the highest peak of a frame (<c>REQ-MKR-005</c>).</summary>
        /// <param name="frame">The current spectrum.</param>
        /// <returns>The marker that moved, or <c>null</c> if there was nothing to do.</returns>
        public Marker PeakSearch(SpectrumFrame frame) =>
            frame == null ? null : MoveSelectedTo(frame, PeakFinder.Highest(frame));

        /// <summary>Moves the selected marker to the next peak down (<c>REQ-MKR-005</c>).</summary>
        /// <param name="frame">The current spectrum.</param>
        /// <returns>The marker that moved, or <c>null</c> if there is no further peak.</returns>
        public Marker NextPeak(SpectrumFrame frame)
        {
            Marker marker = Selected;

            if (marker == null || frame == null)
            {
                return null;
            }

            return MoveSelectedTo(frame, PeakFinder.Next(frame, marker.IndexIn(frame)));
        }

        /// <summary>Moves the selected marker to the lowest point (<c>REQ-MKR-005</c>).</summary>
        /// <param name="frame">The current spectrum.</param>
        /// <returns>The marker that moved, or <c>null</c> if there was nothing to do.</returns>
        public Marker MinimumSearch(SpectrumFrame frame) =>
            frame == null ? null : MoveSelectedTo(frame, PeakFinder.Lowest(frame));

        /// <summary>
        /// Moves every peak-tracking marker onto the nearest peak of a new frame
        /// (<c>REQ-MKR-005</c>).
        /// </summary>
        /// <param name="frame">The new spectrum.</param>
        /// <returns>The markers that moved.</returns>
        /// <remarks>
        /// <para>
        /// Nearest peak, not highest. A marker put on the third tone of a comb must stay on the
        /// third tone when it drifts, and a search for the highest peak would move it to the first
        /// one on the very first acquisition.
        /// </para>
        /// <para>
        /// A fixed marker does not track even if asked to: locking the value it reads is what a
        /// fixed marker is, and a locked value that moved would be neither.
        /// </para>
        /// </remarks>
        public IReadOnlyList<Marker> RetrackPeaks(SpectrumFrame frame)
        {
            var moved = new List<Marker>();

            if (frame == null)
            {
                return new ReadOnlyCollection<Marker>(moved);
            }

            foreach (Marker marker in _markers)
            {
                if (!marker.TracksPeak || marker.Type == MarkerType.Fixed)
                {
                    continue;
                }

                int peak = PeakFinder.Nearest(frame, marker.IndexIn(frame));

                if (peak < 0)
                {
                    continue;
                }

                double landed = frame.FrequencyAt(peak);

                if (landed != marker.XHz)
                {
                    marker.XHz = landed;
                    moved.Add(marker);
                }
            }

            return new ReadOnlyCollection<Marker>(moved);
        }

        private Marker MoveSelectedTo(SpectrumFrame frame, int index)
        {
            Marker marker = Selected;

            if (marker == null || frame == null || index < 0)
            {
                return null;
            }

            marker.XHz = frame.FrequencyAt(index);

            // A fixed marker moved by a search takes the value it lands on: it is locked against
            // the trace updating, not against being deliberately moved.
            if (marker.Type == MarkerType.Fixed)
            {
                marker.FixedYDbm = frame.LevelsDbm[index];
            }

            return marker;
        }

        private Marker Add(MarkerType type, double xHz, double yDbm, Marker reference)
        {
            if (_markers.Count >= MaximumPerTrace)
            {
                throw new InvalidOperationException(
                    "A trace may carry " + MaximumPerTrace.ToString(CultureInfo.CurrentCulture) +
                    " markers (REQ-MKR-002); this trace already has that many. Delete one first.");
            }

            var marker = new Marker(NextNumber(), type, xHz, TraceLetter)
            {
                FixedYDbm = yDbm,
                Reference = reference,
            };

            _markers.Add(marker);
            Select(marker);

            return marker;
        }

        /// <summary>The lowest unused marker number, so a deleted number is reused rather than skipped.</summary>
        private int NextNumber()
        {
            for (int candidate = 1; candidate <= MaximumPerTrace; candidate++)
            {
                if (_markers.All(m => m.Number != candidate))
                {
                    return candidate;
                }
            }

            return _markers.Count + 1;
        }
    }
}
