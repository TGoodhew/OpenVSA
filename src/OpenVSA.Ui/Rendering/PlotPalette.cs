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
            : this(
                traceBackground, grid, annotation, annotationBackground, trace, selectedMarker,
                notSelectedMarker, annotation)
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
        /// <param name="indicator">Trace indicator messages inside the graticule.</param>
        public PlotPalette(
            PlotColor traceBackground,
            PlotColor grid,
            PlotColor annotation,
            PlotColor annotationBackground,
            PlotColor trace,
            PlotColor selectedMarker,
            PlotColor notSelectedMarker,
            PlotColor indicator)
        {
            Indicator = indicator;
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

        /// <summary>
        /// Colour for the trace indicator messages (<c>REQ-UI-040</c>, <c>REQ-UI-041</c>).
        /// </summary>
        /// <remarks>
        /// Its own colour, and the reason is positional: the indicators are the only annotation
        /// drawn <em>inside</em> the graticule, over the trace background rather than the
        /// annotation background, and a colour chosen to read against the one is not guaranteed to
        /// read against the other.
        /// </remarks>
        public PlotColor Indicator { get; }

        /// <summary>The dark theme, and the default.</summary>
        public static PlotPalette Dark { get; } = new PlotPalette(
            traceBackground: PlotColor.FromArgb(0xFF101014),
            grid: PlotColor.FromArgb(0xFF3C3C46),
            annotation: PlotColor.FromArgb(0xFFE0E0E6),
            annotationBackground: PlotColor.FromArgb(0xFF1E1E24),
            trace: PlotColor.FromArgb(0xFFFFD200),
            selectedMarker: PlotColor.FromArgb(0xFFFFFFFF),
            notSelectedMarker: PlotColor.FromArgb(0xFF9090A0),
            indicator: PlotColor.FromArgb(0xFFFF6A3C));

        /// <summary>The light theme.</summary>
        public static PlotPalette Light { get; } = new PlotPalette(
            traceBackground: PlotColor.FromArgb(0xFFFFFFFF),
            grid: PlotColor.FromArgb(0xFFC8C8D0),
            annotation: PlotColor.FromArgb(0xFF1A1A1F),
            annotationBackground: PlotColor.FromArgb(0xFFF0F0F3),
            trace: PlotColor.FromArgb(0xFF0050C8),
            selectedMarker: PlotColor.FromArgb(0xFF101010),
            notSelectedMarker: PlotColor.FromArgb(0xFF707080),
            indicator: PlotColor.FromArgb(0xFFC02000));

        /// <summary>Returns a copy with a different trace background.</summary>
        /// <param name="value">The new colour.</param>
        public PlotPalette WithTraceBackground(PlotColor value) =>
            new PlotPalette(value, Grid, Annotation, AnnotationBackground, Trace, SelectedMarker, NotSelectedMarker, Indicator);

        /// <summary>Returns a copy with a different grid colour.</summary>
        /// <param name="value">The new colour.</param>
        public PlotPalette WithGrid(PlotColor value) =>
            new PlotPalette(TraceBackground, value, Annotation, AnnotationBackground, Trace, SelectedMarker, NotSelectedMarker, Indicator);

        /// <summary>Returns a copy with a different annotation colour.</summary>
        /// <param name="value">The new colour.</param>
        public PlotPalette WithAnnotation(PlotColor value) =>
            new PlotPalette(TraceBackground, Grid, value, AnnotationBackground, Trace, SelectedMarker, NotSelectedMarker, Indicator);

        /// <summary>Returns a copy with a different annotation background.</summary>
        /// <param name="value">The new colour.</param>
        public PlotPalette WithAnnotationBackground(PlotColor value) =>
            new PlotPalette(TraceBackground, Grid, Annotation, value, Trace, SelectedMarker, NotSelectedMarker, Indicator);

        /// <summary>Returns a copy with a different trace colour.</summary>
        /// <param name="value">The new colour.</param>
        public PlotPalette WithTrace(PlotColor value) =>
            new PlotPalette(TraceBackground, Grid, Annotation, AnnotationBackground, value, SelectedMarker, NotSelectedMarker, Indicator);

        /// <summary>Returns a copy with a different indicator colour.</summary>
        /// <param name="value">The new colour.</param>
        public PlotPalette WithIndicator(PlotColor value) =>
            new PlotPalette(TraceBackground, Grid, Annotation, AnnotationBackground, Trace, SelectedMarker, NotSelectedMarker, value);

        /// <summary>
        /// The relative luminance below which a colour is treated as dark, from 0 to 1.
        /// </summary>
        /// <remarks>
        /// Half. A colour above it would be invisible on white and is darkened by
        /// <see cref="ForPrinting"/>; one below it is already legible and is left exactly as it is,
        /// so a printed trace keeps whatever colour the user chose wherever that is possible.
        /// </remarks>
        public const double PrintDarkeningThreshold = 0.5;

        /// <summary>
        /// The luminance a darkened colour is taken to, from 0 to 1.
        /// </summary>
        /// <remarks>
        /// Below <see cref="PrintDarkeningThreshold"/>, not equal to it. The threshold decides
        /// <em>whether</em> a colour needs darkening; this decides <em>how far</em>, and darkening
        /// exactly to the threshold leaves every printed colour sitting on the boundary of being
        /// legible — which is not the same as being legible, and is what the first version did.
        /// </remarks>
        public const double PrintTargetLuminance = 0.34;

        /// <summary>
        /// This palette with a white trace background, for the <em>Force white background</em>
        /// print option (<c>REQ-UI-015</c>).
        /// </summary>
        /// <remarks>
        /// <para>
        /// The reference product offers this because "large areas of black do not print well on
        /// inkjet-style printers", and its own note adds that "very light colors will print black
        /// so they can be seen". That second clause is the part worth implementing carefully:
        /// simply whitening the background would leave an amber trace as a pale smear and a white
        /// marker as nothing at all.
        /// </para>
        /// <para>
        /// <strong>Light colours are darkened, not replaced.</strong> Each is scaled towards black
        /// until it clears the contrast the requirement's own reasoning implies, which keeps four
        /// traces distinguishable from one another on paper — replacing them all with black, as the
        /// note's literal wording suggests, would make a four-trace overlay unreadable in exactly
        /// the case someone prints one.
        /// </para>
        /// <para>
        /// Colours already dark enough to read on white are left untouched, so a user who chose a
        /// dark trace colour gets it printed rather than a darkened version of it.
        /// </para>
        /// </remarks>
        public PlotPalette ForPrinting() =>
            new PlotPalette(
                PlotColor.FromArgb(0xFFFFFFFF),
                Darkened(Grid),
                Darkened(Annotation),
                PlotColor.FromArgb(0xFFFFFFFF),
                Darkened(Trace),
                Darkened(SelectedMarker),
                Darkened(NotSelectedMarker),
                Darkened(Indicator));

        /// <summary>
        /// Whether a colour would be legible on white.
        /// </summary>
        /// <param name="colour">The colour.</param>
        public static bool IsLegibleOnWhite(PlotColor colour) =>
            Luminance(colour) < PrintDarkeningThreshold;

        /// <summary>
        /// A colour dark enough to read on white, keeping its hue.
        /// </summary>
        /// <param name="colour">The colour.</param>
        /// <returns>The colour unchanged when it is already dark enough.</returns>
        public static PlotColor Darkened(PlotColor colour)
        {
            double luminance = Luminance(colour);

            if (luminance < PrintDarkeningThreshold)
            {
                return colour;
            }

            // Scaled rather than clamped: multiplying all three channels by the same factor holds
            // the hue, where clamping each to a ceiling would drag every light colour towards grey
            // and lose the distinction between traces that is the point of colouring them.
            double scale = PrintTargetLuminance / Math.Max(luminance, 1e-6);

            return new PlotColor(
                (byte)Math.Round(colour.R * scale),
                (byte)Math.Round(colour.G * scale),
                (byte)Math.Round(colour.B * scale),
                colour.A);
        }

        /// <summary>
        /// Relative luminance, from 0 for black to 1 for white.
        /// </summary>
        /// <param name="colour">The colour.</param>
        /// <remarks>
        /// The Rec. 709 coefficients, on the channel values as stored. Not the gamma-corrected form
        /// of <c>REQ-UI-090</c>'s contrast ratio: this decides whether ink will show on paper, a
        /// coarser question than whether two colours meet a contrast floor, and using the stricter
        /// measure here would darken colours that print perfectly well.
        /// </remarks>
        public static double Luminance(PlotColor colour) =>
            (0.2126 * colour.R + 0.7152 * colour.G + 0.0722 * colour.B) / 255.0;
    }
}
