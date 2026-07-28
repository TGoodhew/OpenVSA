using System;

namespace OpenVSA.Ui.Rendering
{
    /// <summary>
    /// What a click or a drag on a trace means (<c>REQ-UI-063</c>'s Marker Tools).
    /// </summary>
    /// <remarks>
    /// A mode rather than a set of gestures to remember, and one mode at a time: the requirement
    /// calls this a radio group and says "selecting one mouse mode deselects the others". The
    /// reason is that these gestures overlap — a drag across a trace could reasonably zoom, set a
    /// band or gate a record, and guessing which the user meant is how an imprecise click changes
    /// a measurement.
    /// </remarks>
    public enum MouseMode
    {
        /// <summary>Clicks and drags do nothing to the measurement.</summary>
        Pointer = 0,

        /// <summary>Drag a rectangle to examine that region (<c>REQ-DSP-023</c>).</summary>
        AreaSelect,

        /// <summary>Click to place a marker (<c>REQ-MKR-001</c>).</summary>
        Marker,

        /// <summary>Drag the band limits to integrate between them (<c>REQ-MKR-003</c>).</summary>
        BandPower,

        /// <summary>Drag to isolate a portion of the time record (<c>REQ-TRG-020</c>).</summary>
        TimeGate,
    }

    /// <summary>
    /// What dragging a rectangle does, when the mouse mode is
    /// <see cref="MouseMode.AreaSelect"/>.
    /// </summary>
    /// <remarks>
    /// <c>REQ-UI-063</c> says Area Select "can scale X and/or Y, <strong>or set centre frequency
    /// and span</strong>". Those are different operations on the same gesture: the first two change
    /// what part of the trace is on screen, the third changes what is being measured. Conflating
    /// them would mean a drag meant to magnify the display quietly re-planning the acquisition.
    /// </remarks>
    public enum AreaSelectAction
    {
        /// <summary>Set the analysis centre frequency and span from the region.</summary>
        CentreAndSpan = 0,

        /// <summary>Scale the X axis to the region, leaving the measurement alone.</summary>
        ScaleX,

        /// <summary>Scale the Y axis to the region.</summary>
        ScaleY,

        /// <summary>Scale both axes to the region.</summary>
        ScaleBoth,
    }

    /// <summary>Names for the mouse modes and area actions, as the requirement writes them.</summary>
    public static class MouseModes
    {
        /// <summary>The mode's name.</summary>
        /// <param name="mode">The mode.</param>
        /// <exception cref="ArgumentOutOfRangeException">Not a known mode.</exception>
        public static string NameOf(MouseMode mode)
        {
            switch (mode)
            {
                case MouseMode.Pointer: return "Pointer";
                case MouseMode.AreaSelect: return "Area Select";
                case MouseMode.Marker: return "Marker";
                case MouseMode.BandPower: return "Band Power";
                case MouseMode.TimeGate: return "Time Gate";

                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(mode), mode, "Not a known mouse mode.");
            }
        }

        /// <summary>The action's name, as the Area Select dropdown offers it.</summary>
        /// <param name="action">The action.</param>
        /// <exception cref="ArgumentOutOfRangeException">Not a known action.</exception>
        public static string NameOf(AreaSelectAction action)
        {
            switch (action)
            {
                case AreaSelectAction.CentreAndSpan: return "Set centre and span";
                case AreaSelectAction.ScaleX: return "Scale X";
                case AreaSelectAction.ScaleY: return "Scale Y";
                case AreaSelectAction.ScaleBoth: return "Scale X and Y";

                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(action), action, "Not a known area-select action.");
            }
        }

        /// <summary>
        /// Why an area action cannot be offered, or <c>null</c> when it can.
        /// </summary>
        /// <param name="action">The action.</param>
        /// <remarks>
        /// <para>
        /// <strong>All four are offered now.</strong> Scale X and Scale X and Y were refused for as
        /// long as there was no way to say what band was actually drawn: the centre and span
        /// readouts are editable hot spots that set the measurement, so a display magnified behind
        /// them would make both of them lie. <c>TracePlot</c>'s display range and its "Disp"
        /// annotation are that way (#397), so the refusal is gone.
        /// </para>
        /// <para>
        /// <strong>Setting the centre and span is still a different operation.</strong> OpenVSA's
        /// zoom re-analyses the block it already has — no re-tune, no re-arm — so it gives more
        /// resolution over the dragged band rather than the same points magnified. Scale X
        /// magnifies what was already computed. Both are useful and they are not substitutes.
        /// </para>
        /// <para>
        /// Kept rather than deleted: the shape is how a later action arrives disabled with a reason
        /// instead of present and inert, which is the rule the menus and toolbars keep.
        /// </para>
        /// </remarks>
        public static string ReasonAgainst(AreaSelectAction action) => null;

        /// <summary>The mode with a given name, or <c>null</c>.</summary>
        /// <param name="name">The name, compared exactly.</param>
        public static MouseMode? ByName(string name)
        {
            foreach (MouseMode mode in (MouseMode[])Enum.GetValues(typeof(MouseMode)))
            {
                if (string.Equals(NameOf(mode), name, StringComparison.Ordinal))
                {
                    return mode;
                }
            }

            return null;
        }
    }
}
