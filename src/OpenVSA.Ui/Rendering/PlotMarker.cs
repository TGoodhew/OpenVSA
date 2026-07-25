namespace OpenVSA.Ui.Rendering
{
    /// <summary>
    /// One marker as the plot needs it: where to draw the glyph, and which glyph.
    /// </summary>
    /// <remarks>
    /// A render primitive, not the marker itself. The plot has no business knowing what a delta
    /// marker's reference is or how its number was chosen — it needs a point, a level, a shape and
    /// a selection state. Keeping the boundary here is what lets the marker model live in the
    /// measurement layer and be tested without a window.
    /// </remarks>
    public readonly struct PlotMarker
    {
        /// <summary>Creates a marker primitive.</summary>
        /// <param name="pointIndex">Index of the trace point the marker sits on.</param>
        /// <param name="levelDbm">Level to draw the glyph at, in dBm.</param>
        /// <param name="isFixed">Whether to draw an X centred on the point rather than a diamond above it.</param>
        /// <param name="isSelected">Whether this is the selected marker, drawn filled.</param>
        public PlotMarker(int pointIndex, double levelDbm, bool isFixed, bool isSelected)
        {
            PointIndex = pointIndex;
            LevelDbm = levelDbm;
            IsFixed = isFixed;
            IsSelected = isSelected;
        }

        /// <summary>Index of the trace point the marker sits on.</summary>
        public int PointIndex { get; }

        /// <summary>Level to draw the glyph at, in dBm.</summary>
        public double LevelDbm { get; }

        /// <summary>Whether the glyph is an X centred on the point (<c>REQ-UI-030</c>, Fixed).</summary>
        public bool IsFixed { get; }

        /// <summary>Whether this is the selected marker, and so drawn filled.</summary>
        public bool IsSelected { get; }
    }
}
