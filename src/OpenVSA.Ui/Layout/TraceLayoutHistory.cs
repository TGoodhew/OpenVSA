using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace OpenVSA.Ui.Layout
{
    /// <summary>
    /// The layout in force and the one before it, so <em>Previous Layout</em> has something to
    /// revert to (<c>REQ-UI-005</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>One step, and it is a toggle.</strong> The requirement asks for "the arrangement in
    /// force before the last layout change", not an undo stack — so applying Previous Layout twice
    /// returns to where you started. That is the behaviour a single menu entry implies, and it is
    /// the one that makes Previous Layout useful for comparing two arrangements.
    /// </para>
    /// <para>
    /// <strong>A Custom arrangement is remembered as its slots, not as its name.</strong> The
    /// criterion says Previous Layout restores a Custom one, and "Custom" names nothing on its own
    /// — reverting to it has to bring back where the user actually put things. So the history
    /// holds the arrangement, and the preset beside it for the menu's benefit.
    /// </para>
    /// <para>
    /// <strong>Re-applying the same preset is not a change.</strong> Choosing Grid 2×2 while
    /// already in Grid 2×2 must not make the previous layout Grid 2×2 as well — that would leave
    /// Previous Layout doing nothing, and the user with no way back to what they had.
    /// </para>
    /// </remarks>
    public sealed class TraceLayoutHistory
    {
        private IReadOnlyList<TraceSlot> _currentSlots;
        private IReadOnlyList<TraceSlot> _previousSlots;

        /// <summary>Creates a history starting at Single with nothing arranged.</summary>
        public TraceLayoutHistory()
        {
            Current = TraceLayoutPreset.Single();
            _currentSlots = new ReadOnlyCollection<TraceSlot>(new TraceSlot[0]);
        }

        /// <summary>The preset in force.</summary>
        public TraceLayoutPreset Current { get; private set; }

        /// <summary>The preset before the last change, or <c>null</c> if there has not been one.</summary>
        public TraceLayoutPreset PreviousPreset { get; private set; }

        /// <summary>The arrangement in force.</summary>
        public IReadOnlyList<TraceSlot> CurrentSlots => _currentSlots;

        /// <summary>Whether there is a layout to go back to.</summary>
        public bool CanRevert => PreviousPreset != null;

        /// <summary>
        /// Applies a layout, remembering what it replaced.
        /// </summary>
        /// <param name="preset">The layout to apply; <see cref="TraceLayoutKind.Previous"/> reverts.</param>
        /// <param name="traces">The visible traces.</param>
        /// <param name="width">Document area width in pixels.</param>
        /// <param name="height">Document area height in pixels.</param>
        /// <returns>The arrangement now in force.</returns>
        /// <exception cref="ArgumentNullException">An argument is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException">A dimension is not positive.</exception>
        public IReadOnlyList<TraceSlot> Apply(
            TraceLayoutPreset preset, IReadOnlyList<char> traces, int width, int height)
        {
            if (preset == null)
            {
                throw new ArgumentNullException(nameof(preset));
            }

            if (preset.Kind == TraceLayoutKind.Previous)
            {
                return Revert(traces, width, height);
            }

            IReadOnlyList<TraceSlot> arranged =
                TraceLayoutEngine.Arrange(preset, traces, width, height);

            // Choosing the layout you are already in is not a change, so it does not consume the
            // way back.
            if (Current == null || !SamePreset(Current, preset))
            {
                PreviousPreset = Current;
                _previousSlots = _currentSlots;
            }

            Current = preset;
            _currentSlots = arranged;

            return arranged;
        }

        /// <summary>
        /// Records an arrangement the user made by hand, as <see cref="TraceLayoutKind.Custom"/>.
        /// </summary>
        /// <param name="slots">Where the user put things.</param>
        /// <exception cref="ArgumentNullException"><paramref name="slots"/> is null.</exception>
        /// <remarks>
        /// Dragging a boundary is a layout change like any other, so it displaces the previous
        /// layout — otherwise Previous Layout would step back past everything the user had done
        /// by hand since the last menu choice.
        /// </remarks>
        public void RecordCustom(IReadOnlyList<TraceSlot> slots)
        {
            if (slots == null)
            {
                throw new ArgumentNullException(nameof(slots));
            }

            PreviousPreset = Current;
            _previousSlots = _currentSlots;

            Current = TraceLayoutPreset.Custom();
            _currentSlots = slots;
        }

        /// <summary>
        /// Reverts to the layout before the last change, and makes the current one revertible.
        /// </summary>
        /// <param name="traces">The visible traces.</param>
        /// <param name="width">Document area width.</param>
        /// <param name="height">Document area height.</param>
        /// <returns>The arrangement now in force, unchanged when there is nothing to revert to.</returns>
        /// <remarks>
        /// A Custom layout comes back as the slots that were saved, not as a re-arrangement — the
        /// point of reverting to one is to get back exactly where things were. Everything else is
        /// re-arranged for the current area, so reverting after the window is resized gives a
        /// layout that fits rather than one drawn for the old size.
        /// </remarks>
        public IReadOnlyList<TraceSlot> Revert(
            IReadOnlyList<char> traces, int width, int height)
        {
            if (!CanRevert)
            {
                return _currentSlots;
            }

            TraceLayoutPreset goingTo = PreviousPreset;
            IReadOnlyList<TraceSlot> goingToSlots = _previousSlots;

            PreviousPreset = Current;
            _previousSlots = _currentSlots;

            Current = goingTo;

            _currentSlots = goingTo.Kind == TraceLayoutKind.Custom
                ? goingToSlots
                : TraceLayoutEngine.Arrange(goingTo, traces, width, height);

            return _currentSlots;
        }

        private static bool SamePreset(TraceLayoutPreset left, TraceLayoutPreset right) =>
            left.Kind == right.Kind &&
            left.Rows == right.Rows &&
            left.Columns == right.Columns;
    }
}
