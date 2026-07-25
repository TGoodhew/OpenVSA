using System;

namespace OpenVSA.Ui.Rendering
{
    /// <summary>
    /// The independently settable display colours of <c>REQ-UI-010</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The four zone colours are taken from the reference product's own display-colour
    /// enumeration, with its definitions:
    /// </para>
    /// <list type="bullet">
    /// <item><description><see cref="TraceBackground"/> — background of the trace data, behind the graticule.</description></item>
    /// <item><description><see cref="Grid"/> — the graticule lines.</description></item>
    /// <item><description><see cref="Annotation"/> — trace annotation text, outside the graticule.</description></item>
    /// <item><description><see cref="AnnotationBackground"/> — background of the area outside the graticule.</description></item>
    /// </list>
    /// <para>
    /// <strong>Immutable, with <c>With…</c> methods rather than settable properties.</strong> The
    /// acceptance criterion turns on changing one colour leaving the other three regions
    /// untouched, and a palette that can be mutated in place while a frame is being rasterised
    /// would make that true only most of the time. It is also what <c>REQ-NFR-011</c> asks of
    /// anything crossing into the render path.
    /// </para>
    /// <para>
    /// Custom themes are out of scope for now, but nothing here forecloses them: a theme is a
    /// palette, and every colour the surface draws with comes from one.
    /// </para>
    /// </remarks>
    public sealed class PlotPalette
    {
        /// <summary>Creates a palette, with the marker colours taken from the trace colour.</summary>
        /// <param name="traceBackground">Background behind the graticule.</param>
        /// <param name="grid">Graticule lines.</param>
        /// <param name="annotation">Annotation text outside the graticule.</param>
        /// <param name="annotationBackground">Background outside the graticule.</param>
        /// <param name="trace">Trace geometry.</param>
        public PlotPalette(
            PlotColor traceBackground,
            PlotColor grid,
            PlotColor annotation,
            PlotColor annotationBackground,
            PlotColor trace)
            : this(traceBackground, grid, annotation, annotationBackground, trace, annotation, trace)
        {
        }

        /// <summary>Creates a palette.</summary>
        /// <param name="traceBackground">Background behind the graticule.</param>
        /// <param name="grid">Graticule lines.</param>
        /// <param name="annotation">Annotation text outside the graticule.</param>
        /// <param name="annotationBackground">Background outside the graticule.</param>
        /// <param name="trace">Trace geometry.</param>
        /// <param name="selectedMarker">The selected marker's glyph.</param>
        /// <param name="notSelectedMarker">Every other marker's glyph.</param>
        public PlotPalette(
            PlotColor traceBackground,
            PlotColor grid,
            PlotColor annotation,
            PlotColor annotationBackground,
            PlotColor trace,
            PlotColor selectedMarker,
            PlotColor notSelectedMarker)
        {
            TraceBackground = traceBackground;
            Grid = grid;
            Annotation = annotation;
            AnnotationBackground = annotationBackground;
            Trace = trace;
            SelectedMarker = selectedMarker;
            NotSelectedMarker = notSelectedMarker;
        }

        /// <summary>Colour for the background of the trace data, behind the graticule.</summary>
        public PlotColor TraceBackground { get; }

        /// <summary>Colour for the graticule lines.</summary>
        public PlotColor Grid { get; }

        /// <summary>Colour for the trace annotation, the text outside the graticule.</summary>
        public PlotColor Annotation { get; }

        /// <summary>Colour for the background of the area outside the trace graticule.</summary>
        public PlotColor AnnotationBackground { get; }

        /// <summary>Colour for trace geometry.</summary>
        public PlotColor Trace { get; }

        /// <summary>
        /// Colour for the selected marker's glyph.
        /// </summary>
        /// <remarks>
        /// <c>REQ-UI-030</c>: marker colour is by <em>selection state</em>, not by marker index.
        /// Two unselected markers of different numbers render the same colour, and colouring by
        /// index fails the requirement's test.
        /// </remarks>
        public PlotColor SelectedMarker { get; }

        /// <summary>Colour for every marker that is not selected.</summary>
        public PlotColor NotSelectedMarker { get; }

        /// <summary>The dark theme, and the default.</summary>
        public static PlotPalette Dark { get; } = new PlotPalette(
            traceBackground: PlotColor.FromArgb(0xFF101014),
            grid: PlotColor.FromArgb(0xFF3C3C46),
            annotation: PlotColor.FromArgb(0xFFE0E0E6),
            annotationBackground: PlotColor.FromArgb(0xFF1E1E24),
            trace: PlotColor.FromArgb(0xFFFFD200),
            selectedMarker: PlotColor.FromArgb(0xFFFFFFFF),
            notSelectedMarker: PlotColor.FromArgb(0xFF9090A0));

        /// <summary>The light theme.</summary>
        public static PlotPalette Light { get; } = new PlotPalette(
            traceBackground: PlotColor.FromArgb(0xFFFFFFFF),
            grid: PlotColor.FromArgb(0xFFC8C8D0),
            annotation: PlotColor.FromArgb(0xFF1A1A1F),
            annotationBackground: PlotColor.FromArgb(0xFFF0F0F3),
            trace: PlotColor.FromArgb(0xFF0050C8),
            selectedMarker: PlotColor.FromArgb(0xFF101010),
            notSelectedMarker: PlotColor.FromArgb(0xFF707080));

        /// <summary>Returns a copy with a different trace background.</summary>
        /// <param name="value">The new colour.</param>
        public PlotPalette WithTraceBackground(PlotColor value) =>
            new PlotPalette(value, Grid, Annotation, AnnotationBackground, Trace, SelectedMarker, NotSelectedMarker);

        /// <summary>Returns a copy with a different grid colour.</summary>
        /// <param name="value">The new colour.</param>
        public PlotPalette WithGrid(PlotColor value) =>
            new PlotPalette(TraceBackground, value, Annotation, AnnotationBackground, Trace, SelectedMarker, NotSelectedMarker);

        /// <summary>Returns a copy with a different annotation colour.</summary>
        /// <param name="value">The new colour.</param>
        public PlotPalette WithAnnotation(PlotColor value) =>
            new PlotPalette(TraceBackground, Grid, value, AnnotationBackground, Trace, SelectedMarker, NotSelectedMarker);

        /// <summary>Returns a copy with a different annotation background.</summary>
        /// <param name="value">The new colour.</param>
        public PlotPalette WithAnnotationBackground(PlotColor value) =>
            new PlotPalette(TraceBackground, Grid, Annotation, value, Trace, SelectedMarker, NotSelectedMarker);

        /// <summary>Returns a copy with a different trace colour.</summary>
        /// <param name="value">The new colour.</param>
        public PlotPalette WithTrace(PlotColor value) =>
            new PlotPalette(TraceBackground, Grid, Annotation, AnnotationBackground, value, SelectedMarker, NotSelectedMarker);
    }
}
