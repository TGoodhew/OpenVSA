using System;
using System.Collections.Generic;
using OpenVSA.Demod.Results;

namespace OpenVSA.Ui.Rendering
{
    /// <summary>
    /// How the ideal states are overlaid on a constellation (<c>REQ-UI-050</c>).
    /// </summary>
    /// <remarks>
    /// <strong>Never a filled dot, and the requirement says so twice.</strong> A filled dot is what
    /// a measured symbol is drawn as, so an ideal state drawn the same way is confusable with the
    /// data — which is the one thing a constellation must not be. Both offered shapes are open in
    /// the middle so a symbol sitting on its ideal state is still visible.
    /// </remarks>
    public enum IdealStateOverlay
    {
        /// <summary>No overlay.</summary>
        None = 0,

        /// <summary>A small cross centred on the ideal point.</summary>
        Crosshair,

        /// <summary>A ring around the ideal point.</summary>
        Circle,
    }

    /// <summary>
    /// Draws a constellation or an IQ/vector trace (<c>REQ-UI-050</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The two are the same data and differ by one thing: the lines.</strong>
    /// <c>REQ-UI-050</c> is explicit — a constellation is "an IQ diagram but information is shown
    /// only at specified time intervals", "similar to the IQ trace format but without the lines
    /// that connect the points". So both live here and
    /// <see cref="Render"/>'s <c>connect</c> argument is the whole difference, which is what lets a
    /// test assert that one draws connecting geometry and the other draws none.
    /// </para>
    /// <para>
    /// <strong>Points, not a line, and counted.</strong> The criterion is that "the rendered
    /// primitive count equals the symbol count and that no line segments join them", so
    /// <see cref="ConstellationRender.SymbolsDrawn"/> is returned rather than inferred from pixels:
    /// a symbol whose point lands off the display is still a symbol that was drawn, and counting
    /// ink would make the assertion depend on the scaling.
    /// </para>
    /// </remarks>
    public static class ConstellationRasterizer
    {
        /// <summary>Half the width of a drawn symbol, in pixels.</summary>
        public const int SymbolRadius = 1;

        /// <summary>Half the width of an ideal-state overlay, in pixels.</summary>
        public const int OverlayRadius = 4;

        /// <summary>
        /// Draws a result.
        /// </summary>
        /// <param name="surface">The surface to draw on.</param>
        /// <param name="area">The rectangle to draw in; usually the graticule.</param>
        /// <param name="trace">The demodulated result.</param>
        /// <param name="colours">What to draw each part with.</param>
        /// <param name="overlay">How to show the ideal states.</param>
        /// <param name="connect">
        /// Whether to join the symbols in time order — the IQ/vector format. A constellation never
        /// does (<c>REQ-UI-050</c>).
        /// </param>
        /// <param name="scale">
        /// The value drawn at the edge of the area, in constellation units; a non-positive value
        /// takes it from the result.
        /// </param>
        /// <exception cref="ArgumentNullException">An argument is null.</exception>
        public static ConstellationRender Render(
            PixelSurface surface,
            PixelRect area,
            SymbolTrace trace,
            ConstellationColours colours,
            IdealStateOverlay overlay,
            bool connect,
            double scale = 0.0)
        {
            if (surface == null)
            {
                throw new ArgumentNullException(nameof(surface));
            }

            if (trace == null)
            {
                throw new ArgumentNullException(nameof(trace));
            }

            if (colours == null)
            {
                throw new ArgumentNullException(nameof(colours));
            }

            if (area.Width <= 0 || area.Height <= 0)
            {
                return new ConstellationRender(0, 0, 0);
            }

            double extent = scale > 0.0 ? scale : Extent(trace);

            // The ideal states first, so a measured symbol sitting on one is drawn over it rather
            // than under it — the overlay is a reference and the data is the subject.
            int overlays = 0;

            if (overlay != IdealStateOverlay.None)
            {
                foreach (ConstellationPoint ideal in DistinctIdeals(trace))
                {
                    int x = XFor(ideal.I, extent, area);
                    int y = YFor(ideal.Q, extent, area);

                    if (!area.Contains(x, y))
                    {
                        continue;
                    }

                    if (overlay == IdealStateOverlay.Crosshair)
                    {
                        DrawCrosshair(surface, x, y, area, colours.IdealState);
                    }
                    else
                    {
                        DrawCircle(surface, x, y, area, colours.IdealState);
                    }

                    overlays++;
                }
            }

            int segments = 0;

            if (connect)
            {
                for (int symbol = 1; symbol < trace.SymbolCount; symbol++)
                {
                    ConstellationPoint from = trace.Measured[symbol - 1];
                    ConstellationPoint to = trace.Measured[symbol];

                    DrawLine(
                        surface,
                        XFor(from.I, extent, area), YFor(from.Q, extent, area),
                        XFor(to.I, extent, area), YFor(to.Q, extent, area),
                        area,
                        colours.Trajectory);

                    segments++;
                }
            }

            for (int symbol = 0; symbol < trace.SymbolCount; symbol++)
            {
                ConstellationPoint measured = trace.Measured[symbol];

                int x = XFor(measured.I, extent, area);
                int y = YFor(measured.Q, extent, area);

                DrawSymbol(surface, x, y, area, colours.For(trace, symbol));
            }

            return new ConstellationRender(trace.SymbolCount, segments, overlays);
        }

        /// <summary>Where a value on the I axis lands.</summary>
        /// <param name="i">The in-phase value.</param>
        /// <param name="extent">The value at the edge of the area.</param>
        /// <param name="area">The area.</param>
        public static int XFor(double i, double extent, PixelRect area) =>
            area.X + (int)Math.Round((i / extent + 1.0) * 0.5 * (area.Width - 1));

        /// <summary>Where a value on the Q axis lands.</summary>
        /// <param name="q">The quadrature value.</param>
        /// <param name="extent">The value at the edge of the area.</param>
        /// <param name="area">The area.</param>
        /// <remarks>Q increases upwards, so the row decreases as the value rises.</remarks>
        public static int YFor(double q, double extent, PixelRect area) =>
            area.Y + (area.Height - 1) - (int)Math.Round((q / extent + 1.0) * 0.5 * (area.Height - 1));

        /// <summary>
        /// The value at the edge of the display for a result.
        /// </summary>
        /// <param name="trace">The result.</param>
        /// <exception cref="ArgumentNullException"><paramref name="trace"/> is null.</exception>
        /// <remarks>
        /// The furthest measured or ideal point, with a fifth again so that a symbol on the outside
        /// of the constellation is not drawn on the border. Taken from both, so an impairment that
        /// pushes a symbol outward stays visible instead of being clipped to the ideal grid.
        /// </remarks>
        public static double Extent(SymbolTrace trace)
        {
            if (trace == null)
            {
                throw new ArgumentNullException(nameof(trace));
            }

            double furthest = 0.0;

            for (int symbol = 0; symbol < trace.SymbolCount; symbol++)
            {
                furthest = Math.Max(furthest, Reach(trace.Measured[symbol]));
                furthest = Math.Max(furthest, Reach(trace.Ideal[symbol]));
            }

            return furthest < 1e-9 ? 1.0 : furthest * 1.2;
        }

        private static double Reach(ConstellationPoint point) =>
            Math.Max(Math.Abs(point.I), Math.Abs(point.Q));

        /// <summary>The ideal states actually used, each once.</summary>
        private static IEnumerable<ConstellationPoint> DistinctIdeals(SymbolTrace trace)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);

            for (int symbol = 0; symbol < trace.SymbolCount; symbol++)
            {
                ConstellationPoint ideal = trace.Ideal[symbol];
                string key = ideal.ToString();

                if (seen.Add(key))
                {
                    yield return ideal;
                }
            }
        }

        private static void DrawSymbol(
            PixelSurface surface, int x, int y, PixelRect area, PlotColor colour)
        {
            for (int dy = -SymbolRadius; dy <= SymbolRadius; dy++)
            {
                for (int dx = -SymbolRadius; dx <= SymbolRadius; dx++)
                {
                    if (area.Contains(x + dx, y + dy))
                    {
                        surface.SetPixel(x + dx, y + dy, colour);
                    }
                }
            }
        }

        private static void DrawCrosshair(
            PixelSurface surface, int x, int y, PixelRect area, PlotColor colour)
        {
            // Open in the middle: a symbol sitting exactly on its ideal state has to stay visible,
            // which a filled marker would prevent — the reason REQ-UI-050 forbids filled dots.
            for (int offset = 2; offset <= OverlayRadius; offset++)
            {
                Plot(surface, x + offset, y, area, colour);
                Plot(surface, x - offset, y, area, colour);
                Plot(surface, x, y + offset, area, colour);
                Plot(surface, x, y - offset, area, colour);
            }
        }

        private static void DrawCircle(
            PixelSurface surface, int x, int y, PixelRect area, PlotColor colour)
        {
            const int Steps = 24;

            for (int step = 0; step < Steps; step++)
            {
                double angle = 2.0 * Math.PI * step / Steps;

                Plot(
                    surface,
                    x + (int)Math.Round(OverlayRadius * Math.Cos(angle)),
                    y + (int)Math.Round(OverlayRadius * Math.Sin(angle)),
                    area,
                    colour);
            }
        }

        private static void DrawLine(
            PixelSurface surface, int x0, int y0, int x1, int y1, PixelRect area, PlotColor colour)
        {
            int dx = Math.Abs(x1 - x0);
            int dy = -Math.Abs(y1 - y0);
            int sx = x0 < x1 ? 1 : -1;
            int sy = y0 < y1 ? 1 : -1;
            int error = dx + dy;

            while (true)
            {
                Plot(surface, x0, y0, area, colour);

                if (x0 == x1 && y0 == y1)
                {
                    return;
                }

                int twice = 2 * error;

                if (twice >= dy)
                {
                    error += dy;
                    x0 += sx;
                }

                if (twice <= dx)
                {
                    error += dx;
                    y0 += sy;
                }
            }
        }

        private static void Plot(
            PixelSurface surface, int x, int y, PixelRect area, PlotColor colour)
        {
            if (area.Contains(x, y))
            {
                surface.SetPixel(x, y, colour);
            }
        }
    }

    /// <summary>What a constellation render actually drew.</summary>
    /// <remarks>
    /// <c>REQ-UI-050</c>'s criterion counts primitives: one point per symbol and no line segments
    /// joining them. Reported rather than sampled from the pixels, because a symbol drawn off the
    /// edge of the display is still a symbol that was drawn.
    /// </remarks>
    public readonly struct ConstellationRender
    {
        internal ConstellationRender(int symbols, int segments, int overlays)
        {
            SymbolsDrawn = symbols;
            SegmentsDrawn = segments;
            OverlaysDrawn = overlays;
        }

        /// <summary>How many symbol points were drawn.</summary>
        public int SymbolsDrawn { get; }

        /// <summary>How many connecting segments were drawn; zero for a constellation.</summary>
        public int SegmentsDrawn { get; }

        /// <summary>How many ideal states were overlaid.</summary>
        public int OverlaysDrawn { get; }

        /// <inheritdoc />
        public override string ToString() =>
            SymbolsDrawn + " symbols, " + SegmentsDrawn + " segments, " + OverlaysDrawn + " ideals";
    }

    /// <summary>
    /// The colours a constellation draws with (<c>REQ-UI-050</c>, <c>REQ-UI-022</c>).
    /// </summary>
    /// <remarks>
    /// <strong>Symbol points carry their own colour, separate from the trace line's.</strong>
    /// <c>REQ-UI-022</c> lists <c>Symbol</c> as a per-trace element in its own right, and
    /// <c>REQ-UI-050</c> says the points use it. A mixed-modulation result colours its symbols from
    /// <see cref="ModulationTypes"/> — the <c>Mod Type N</c> entries — instead.
    /// </remarks>
    public sealed class ConstellationColours
    {
        /// <summary>The colour of a measured symbol.</summary>
        public PlotColor Symbol { get; set; } = new PlotColor(0xFF, 0xD2, 0x00);

        /// <summary>The colour of an ideal-state overlay.</summary>
        public PlotColor IdealState { get; set; } = new PlotColor(0x80, 0x80, 0x90);

        /// <summary>The colour of the inter-symbol trajectory, in the IQ/vector format.</summary>
        public PlotColor Trajectory { get; set; } = new PlotColor(0x40, 0x80, 0xC0);

        /// <summary>
        /// The <c>Mod Type N</c> colours, for a mixed-modulation result.
        /// </summary>
        public IReadOnlyList<PlotColor> ModulationTypes { get; set; }

        /// <summary>
        /// The colour one symbol is drawn in.
        /// </summary>
        /// <param name="trace">The result.</param>
        /// <param name="symbol">Which symbol.</param>
        /// <remarks>
        /// A result carrying one modulation uses <see cref="Symbol"/>; one carrying several uses
        /// the <c>Mod Type N</c> entry for that symbol's type, wrapping if there are more types
        /// than entries — the same rule the trace colour table keeps for a twenty-first trace.
        /// </remarks>
        public PlotColor For(SymbolTrace trace, int symbol)
        {
            IReadOnlyList<int> types = trace?.ModulationTypes;
            IReadOnlyList<PlotColor> palette = ModulationTypes;

            if (types == null || palette == null || palette.Count == 0 ||
                symbol < 0 || symbol >= types.Count)
            {
                return Symbol;
            }

            int type = types[symbol];

            return palette[((type % palette.Count) + palette.Count) % palette.Count];
        }
    }
}
