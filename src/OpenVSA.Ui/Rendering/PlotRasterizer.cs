using System;

namespace OpenVSA.Ui.Rendering
{
    /// <summary>
    /// Draws a plot into a <see cref="PixelSurface"/> in software.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the <c>&gt; 20 000</c>-point strategy of <c>REQ-NFR-005</c>, and the reason it is
    /// the one that scales: it never builds geometry, so MilCore's anti-aliased tessellator and
    /// the per-<c>Point</c> managed→native marshalling — the actual costs at high point counts —
    /// are not on the path at all. Cost here is proportional to <em>pixels touched</em>, not to
    /// points in the trace, which is why a 2²⁰-point trace costs the same to draw as an
    /// 8 192-point one at the same width.
    /// </para>
    /// <para>
    /// It is also the RDP answer. <c>D3DImage</c> degrades to software rendering under RDP and
    /// without WDDM, so a surface built only on the shared-surface bridge has no path there.
    /// Making the rasteriser the primary top-band strategy rather than the fallback means the
    /// degraded case is the tested case.
    /// </para>
    /// <para>
    /// Annotation <em>text</em> is not drawn here. It belongs to the WPF layer, because
    /// <c>REQ-UI-042</c>'s hot spots need hit-testing, hover underlining and in-place editing —
    /// all far cheaper against real elements than against rasterised glyphs, and that requirement
    /// is explicit that retrofitting in-place editing later is the expensive path. What the
    /// rasteriser owns is the annotation <em>band</em> and its background colour.
    /// </para>
    /// </remarks>
    public static class PlotRasterizer
    {
        /// <summary>
        /// Renders a full frame: annotation band, trace background, graticule, then the trace.
        /// </summary>
        /// <param name="surface">Target surface; must match <paramref name="layout"/>'s dimensions.</param>
        /// <param name="layout">Plot geometry.</param>
        /// <param name="palette">Colours to draw with.</param>
        /// <param name="minMax">
        /// Decimated trace, as (minimum, maximum) pairs — one pair per graticule column, as
        /// produced by <see cref="TraceDecimator.Decimate"/>. Pass an empty span to draw the
        /// graticule with no trace.
        /// </param>
        /// <exception cref="ArgumentNullException">An argument is null.</exception>
        /// <exception cref="ArgumentException">The surface does not match the layout, or the trace length is wrong.</exception>
        public static void Render(
            PixelSurface surface,
            PlotLayout layout,
            PlotPalette palette,
            ReadOnlySpan<float> minMax) =>
            Render(surface, layout, palette, minMax, ReadOnlySpan<PlotColor>.Empty);

        /// <summary>
        /// Renders a full frame, colouring each trace column separately (<c>REQ-UI-023</c>).
        /// </summary>
        /// <param name="surface">Target surface; must match <paramref name="layout"/>'s dimensions.</param>
        /// <param name="layout">Plot geometry.</param>
        /// <param name="palette">Colours to draw with.</param>
        /// <param name="minMax">Decimated trace, as (minimum, maximum) pairs.</param>
        /// <param name="columnColours">
        /// One colour per graticule column, or empty to draw the whole trace in
        /// <see cref="PlotPalette.Trace"/>. This is where limit failures recolour the trace: the
        /// colours arrive per column, so a failing stretch is drawn in the fail colour and the rest
        /// of the same trace is not.
        /// </param>
        /// <exception cref="ArgumentNullException">An argument is null.</exception>
        /// <exception cref="ArgumentException">The surface does not match the layout, or a span is the wrong length.</exception>
        public static void Render(
            PixelSurface surface,
            PlotLayout layout,
            PlotPalette palette,
            ReadOnlySpan<float> minMax,
            ReadOnlySpan<PlotColor> columnColours) =>
            Render(surface, layout, palette, minMax, columnColours, drawGridLines: true);

        /// <summary>
        /// Renders a full frame, optionally without the graticule lines (<c>REQ-UI-011</c>).
        /// </summary>
        /// <param name="surface">Target surface; must match <paramref name="layout"/>'s dimensions.</param>
        /// <param name="layout">Plot geometry.</param>
        /// <param name="palette">Colours to draw with.</param>
        /// <param name="minMax">Decimated trace, as (minimum, maximum) pairs.</param>
        /// <param name="columnColours">One colour per graticule column, or empty for one colour.</param>
        /// <param name="drawGridLines">
        /// Whether the graticule lines are drawn. <em>Show Grid Lines</em> off removes the lines and
        /// nothing else: the graticule rectangle keeps its size and its own background colour, so
        /// the plot area is still visible as an area — which is what makes this independent of
        /// <em>Show Annotation</em> rather than a second way of spelling it.
        /// </param>
        /// <exception cref="ArgumentNullException">An argument is null.</exception>
        /// <exception cref="ArgumentException">The surface does not match the layout, or a span is the wrong length.</exception>
        public static void Render(
            PixelSurface surface,
            PlotLayout layout,
            PlotPalette palette,
            ReadOnlySpan<float> minMax,
            ReadOnlySpan<PlotColor> columnColours,
            bool drawGridLines)
        {
            if (surface == null)
            {
                throw new ArgumentNullException(nameof(surface));
            }

            if (layout == null)
            {
                throw new ArgumentNullException(nameof(layout));
            }

            if (palette == null)
            {
                throw new ArgumentNullException(nameof(palette));
            }

            if (surface.Width != layout.Width || surface.Height != layout.Height)
            {
                throw new ArgumentException(
                    "Surface is " + surface.Width + "x" + surface.Height + " but the layout is " +
                    layout.Width + "x" + layout.Height + ".",
                    nameof(surface));
            }

            if (minMax.Length != 0 && minMax.Length != layout.Graticule.Width * 2)
            {
                throw new ArgumentException(
                    "Expected " + (layout.Graticule.Width * 2) + " values for a graticule " +
                    layout.Graticule.Width + " columns wide, got " + minMax.Length + ".",
                    nameof(minMax));
            }

            if (columnColours.Length != 0 && columnColours.Length != layout.Graticule.Width)
            {
                throw new ArgumentException(
                    "Expected " + layout.Graticule.Width + " column colours for a graticule " +
                    layout.Graticule.Width + " columns wide, got " + columnColours.Length + ".",
                    nameof(columnColours));
            }

            // Zone order matters: the annotation background is laid down across the whole surface
            // and the trace background then covers the graticule. Painting only the band would
            // leave the two zones sharing whatever was there before, which is exactly the
            // "two colours with a shared background" that REQ-UI-010's criterion rules out.
            surface.Fill(surface.Bounds, palette.AnnotationBackground);
            surface.Fill(layout.Graticule, palette.TraceBackground);

            if (drawGridLines)
            {
                DrawGraticule(surface, layout, palette.Grid);
            }

            if (minMax.Length != 0)
            {
                DrawTrace(surface, layout, palette.Trace, minMax, columnColours);
            }
        }

        private static void DrawGraticule(PixelSurface surface, PlotLayout layout, PlotColor color)
        {
            PixelRect graticule = layout.Graticule;

            for (int division = 0; division <= layout.VerticalDivisions; division++)
            {
                surface.DrawHorizontal(
                    layout.HorizontalGridLineY(division), graticule.X, graticule.Right - 1, color);
            }

            for (int division = 0; division <= layout.HorizontalDivisions; division++)
            {
                surface.DrawVertical(
                    layout.VerticalGridLineX(division), graticule.Y, graticule.Bottom - 1, color);
            }
        }

        private static void DrawTrace(
            PixelSurface surface,
            PlotLayout layout,
            PlotColor color,
            ReadOnlySpan<float> minMax,
            ReadOnlySpan<PlotColor> columnColours)
        {
            PixelRect graticule = layout.Graticule;
            int previousTop = -1;
            int previousBottom = -1;

            for (int column = 0; column < graticule.Width; column++)
            {
                float minimum = minMax[column * 2];
                float maximum = minMax[column * 2 + 1];

                if (float.IsNaN(minimum) || float.IsNaN(maximum))
                {
                    previousTop = -1;
                    previousBottom = -1;
                    continue;
                }

                // The maximum is the higher value and so the *smaller* row: y grows downward.
                int top = layout.ValueToY(maximum);
                int bottom = layout.ValueToY(minimum);

                // Bridge to the previous column when the two spans do not overlap. Without this
                // a steep edge draws as a ladder of disconnected segments — the trace would be
                // technically correct at every column and visibly broken.
                if (previousTop >= 0)
                {
                    if (bottom < previousTop)
                    {
                        bottom = previousTop;
                    }
                    else if (top > previousBottom)
                    {
                        top = previousBottom;
                    }
                }

                // Per column, so a limit failure recolours the stretch of trace that failed and
                // nothing else. A bridging span between two columns takes the later column's
                // colour, which is the one whose points caused the jump.
                surface.DrawVertical(
                    graticule.X + column,
                    top,
                    bottom,
                    columnColours.Length == 0 ? color : columnColours[column]);

                previousTop = layout.ValueToY(maximum);
                previousBottom = layout.ValueToY(minimum);
            }
        }
    }
}
