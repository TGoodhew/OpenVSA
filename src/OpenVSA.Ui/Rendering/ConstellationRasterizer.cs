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

    /// <summary>What decides a measured symbol's colour (<c>REQ-DEM-082</c>).</summary>
    public enum SymbolColouring
    {
        /// <summary>The symbol colour, or the modulation type's (<c>REQ-UI-050</c>).</summary>
        Fixed = 0,

        /// <summary>
        /// The colour map, indexed by how far the symbol landed from its ideal state.
        /// </summary>
        /// <remarks>
        /// The point of it is that a constellation shows you <em>where</em> the symbols are and not
        /// <em>which</em> ones are wrong: a symbol that missed its state by a quarter of the
        /// spacing sits in the middle of its own cloud and looks like every other symbol in it.
        /// Colouring by error magnitude puts the answer on the display the error summary gives as a
        /// single number.
        /// </remarks>
        ByErrorMagnitude,
    }

    /// <summary>
    /// What to draw on a constellation besides the symbols (<c>REQ-UI-050</c>,
    /// <c>REQ-DEM-082</c>).
    /// </summary>
    /// <remarks>
    /// <strong>An object rather than more parameters.</strong> <c>REQ-DEM-082</c> adds four
    /// independent things to a call that already took three, and a seven-argument render is one
    /// where the call sites stop being readable and a transposed pair of booleans compiles.
    /// </remarks>
    public sealed class ConstellationOptions
    {
        /// <summary>How the ideal states are overlaid.</summary>
        public IdealStateOverlay IdealStates { get; set; } = IdealStateOverlay.None;

        /// <summary>Whether the symbols are joined in time order — the IQ/vector format.</summary>
        public bool Connect { get; set; }

        /// <summary>
        /// The value drawn at the edge of the area; a non-positive value takes it from the result.
        /// </summary>
        /// <remarks>
        /// Worth fixing when <see cref="DecisionBoundaries"/> is set: a scale that follows the data
        /// moves the boundary mask with it, and the mask is the expensive thing to recompute.
        /// </remarks>
        public double Scale { get; set; }

        /// <summary>What decides a measured symbol's colour.</summary>
        public SymbolColouring Colouring { get; set; } = SymbolColouring.Fixed;

        /// <summary>
        /// The error magnitude drawn at the top of the colour map; non-positive takes it from the
        /// result's own worst symbol.
        /// </summary>
        /// <remarks>
        /// Taken from the result by default so that a clean signal still shows its own structure
        /// rather than one flat colour at the bottom of the map. Set it to compare two measurements
        /// against each other, where a per-result scale would make the worse one look like the
        /// better one.
        /// </remarks>
        public double ErrorFullScale { get; set; }

        /// <summary>The decision-boundary overlay, or null to draw none.</summary>
        public DecisionBoundaries DecisionBoundaries { get; set; }

        /// <summary>
        /// The density accumulator the symbols are drawn through, or null to draw them as points.
        /// </summary>
        public ConstellationDensity Density { get; set; }
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
            double scale = 0.0) =>
            Render(
                surface, area, trace, colours,
                new ConstellationOptions
                {
                    IdealStates = overlay,
                    Connect = connect,
                    Scale = scale,
                });

        /// <summary>
        /// Draws a result, with the overlays and rendering of <c>REQ-DEM-082</c>.
        /// </summary>
        /// <param name="surface">The surface to draw on.</param>
        /// <param name="area">The rectangle to draw in; usually the graticule.</param>
        /// <param name="trace">The demodulated result.</param>
        /// <param name="colours">What to draw each part with.</param>
        /// <param name="options">What to draw besides the symbols.</param>
        /// <exception cref="ArgumentNullException">An argument is null.</exception>
        /// <remarks>
        /// The order is deepest reference first: decision boundaries, then ideal states, then the
        /// trajectory, then the symbols. Each layer is a reference for the one above it, so the
        /// data ends up on top of everything drawn to explain it.
        /// </remarks>
        public static ConstellationRender Render(
            PixelSurface surface,
            PixelRect area,
            SymbolTrace trace,
            ConstellationColours colours,
            ConstellationOptions options)
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

            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            if (area.Width <= 0 || area.Height <= 0)
            {
                return new ConstellationRender(0, 0, 0, 0, 0);
            }

            double extent = options.Scale > 0.0 ? options.Scale : Extent(trace);

            // Under everything: the regions the decision is made in.
            int boundaryCells = options.DecisionBoundaries == null
                ? 0
                : options.DecisionBoundaries.Draw(
                    surface, area, trace, extent, colours.DecisionBoundary);

            // The ideal states next, so a measured symbol sitting on one is drawn over it rather
            // than under it — the overlay is a reference and the data is the subject.
            IdealStateOverlay overlay = options.IdealStates;
            int overlays = 0;

            if (overlay != IdealStateOverlay.None)
            {
                foreach (ConstellationPoint ideal in IdealStates(trace))
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

            if (options.Connect)
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

            int densityCells = 0;

            if (options.Density != null)
            {
                // Through the accumulator rather than as points. Every symbol is still drawn — it
                // is counted into a cell instead of painted into one — so REQ-UI-050's "one point
                // per symbol" still holds and SymbolsDrawn still means what it meant.
                options.Density.Accumulate(trace, extent, area);

                densityCells = options.Density.Paint(surface, area, colours.Symbol);
            }
            else
            {
                double fullScale = options.Colouring == SymbolColouring.ByErrorMagnitude
                    ? (options.ErrorFullScale > 0.0 ? options.ErrorFullScale : WorstError(trace))
                    : 0.0;

                for (int symbol = 0; symbol < trace.SymbolCount; symbol++)
                {
                    ConstellationPoint measured = trace.Measured[symbol];

                    int x = XFor(measured.I, extent, area);
                    int y = YFor(measured.Q, extent, area);

                    PlotColor colour = options.Colouring == SymbolColouring.ByErrorMagnitude
                        ? colours.ForError(Error(trace, symbol), fullScale)
                        : colours.For(trace, symbol);

                    DrawSymbol(surface, x, y, area, colour);
                }
            }

            return new ConstellationRender(
                trace.SymbolCount, segments, overlays, boundaryCells, densityCells);
        }

        /// <summary>How far a symbol landed from its ideal state.</summary>
        /// <param name="trace">The result.</param>
        /// <param name="symbol">Which symbol.</param>
        public static double Error(SymbolTrace trace, int symbol)
        {
            if (trace == null)
            {
                throw new ArgumentNullException(nameof(trace));
            }

            ConstellationPoint error = trace.ErrorAt(symbol);

            return Math.Sqrt((error.I * error.I) + (error.Q * error.Q));
        }

        /// <summary>The largest error in a result.</summary>
        /// <param name="trace">The result.</param>
        /// <exception cref="ArgumentNullException"><paramref name="trace"/> is null.</exception>
        /// <remarks>
        /// The default top of the error colour map. A result with no error at all returns zero and
        /// the map is not used, because a ramp with no range to spread over would put an exact
        /// signal somewhere arbitrary on it.
        /// </remarks>
        public static double WorstError(SymbolTrace trace)
        {
            if (trace == null)
            {
                throw new ArgumentNullException(nameof(trace));
            }

            double worst = 0.0;

            for (int symbol = 0; symbol < trace.SymbolCount; symbol++)
            {
                worst = Math.Max(worst, Error(trace, symbol));
            }

            return worst;
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

        /// <summary>
        /// The ideal states a result actually uses, each once, ordered by I then Q.
        /// </summary>
        /// <param name="trace">The result.</param>
        /// <exception cref="ArgumentNullException"><paramref name="trace"/> is null.</exception>
        /// <remarks>
        /// <para>
        /// <strong>Bit-exact, and it used to be a formatted string.</strong> The states were found
        /// by keying a set on <c>ConstellationPoint.ToString()</c>, which formats two doubles to
        /// three places. That is two allocations and two number formats per symbol, on a scan run
        /// once for the ideal-state overlay and again for the decision boundaries — a hundred
        /// thousand symbols spent about 150 ms a frame in <c>double.ToString</c> and nothing else.
        /// It was also wrong at the top of the range: three decimal places merge two states of a
        /// 4096-QAM that are a thousandth apart, so the display would draw one overlay where the
        /// decision has two regions.
        /// </para>
        /// <para>
        /// Ordered so that a state's index is a property of the constellation rather than of the
        /// order the symbols happened to arrive in, which is what lets
        /// <see cref="DecisionBoundaries"/> compare one scan's states against the last one's.
        /// </para>
        /// </remarks>
        public static ConstellationPoint[] IdealStates(SymbolTrace trace)
        {
            if (trace == null)
            {
                throw new ArgumentNullException(nameof(trace));
            }

            var seen = new HashSet<long>();
            var states = new List<ConstellationPoint>();

            for (int symbol = 0; symbol < trace.SymbolCount; symbol++)
            {
                ConstellationPoint ideal = trace.Ideal[symbol];

                long key = BitConverter.DoubleToInt64Bits(ideal.I) ^
                    RotateLeft(BitConverter.DoubleToInt64Bits(ideal.Q));

                if (seen.Add(key))
                {
                    states.Add(ideal);
                }
            }

            states.Sort((left, right) =>
            {
                int byI = left.I.CompareTo(right.I);

                return byI != 0 ? byI : left.Q.CompareTo(right.Q);
            });

            return states.ToArray();
        }

        /// <summary>
        /// Turns the second half of a key so that <c>(a, b)</c> and <c>(b, a)</c> differ.
        /// </summary>
        /// <remarks>
        /// A plain exclusive-or of the two bit patterns collides for every point on the diagonal's
        /// mirror — <c>(1, −1)</c> and <c>(−1, 1)</c> hash alike — which for a QAM is half the
        /// constellation.
        /// </remarks>
        private static long RotateLeft(long bits) =>
            (long)(((ulong)bits << 32) | ((ulong)bits >> 32));

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
        internal ConstellationRender(
            int symbols, int segments, int overlays, int boundaryCells, int densityCells)
        {
            SymbolsDrawn = symbols;
            SegmentsDrawn = segments;
            OverlaysDrawn = overlays;
            BoundaryCells = boundaryCells;
            DensityCells = densityCells;
        }

        /// <summary>How many symbol points were drawn.</summary>
        public int SymbolsDrawn { get; }

        /// <summary>How many connecting segments were drawn; zero for a constellation.</summary>
        public int SegmentsDrawn { get; }

        /// <summary>How many ideal states were overlaid.</summary>
        public int OverlaysDrawn { get; }

        /// <summary>How many cells the decision-boundary overlay occupied; zero when off.</summary>
        public int BoundaryCells { get; }

        /// <summary>
        /// How many cells the density rendering painted; zero when the symbols were drawn as
        /// points.
        /// </summary>
        /// <remarks>
        /// Far fewer than <see cref="SymbolsDrawn"/> once the constellation is crowded, which is
        /// the whole reason <c>REQ-DEM-082</c> offers it for large symbol counts: the work stops
        /// scaling with the symbols and starts scaling with the display.
        /// </remarks>
        public int DensityCells { get; }

        /// <inheritdoc />
        public override string ToString() =>
            SymbolsDrawn + " symbols, " + SegmentsDrawn + " segments, " + OverlaysDrawn +
            " ideals, " + BoundaryCells + " boundary cells, " + DensityCells + " density cells";
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

        /// <summary>The colour of the decision-boundary overlay (<c>REQ-DEM-082</c>).</summary>
        /// <remarks>
        /// Dimmer than the ideal-state overlay, which is itself dimmer than the symbols. The
        /// boundary covers far more of the display than either, so at equal weight it would be the
        /// loudest thing on a constellation rather than the quietest.
        /// </remarks>
        public PlotColor DecisionBoundary { get; set; } = new PlotColor(0x38, 0x38, 0x44);

        /// <summary>
        /// The map a symbol's error magnitude is coloured through (<c>REQ-DEM-082</c>).
        /// </summary>
        /// <remarks>
        /// <c>REQ-UI-024</c>'s map, for the same reason the density heat map uses it: one idea, one
        /// map, and a user who has chosen how magnitude reads has chosen it here too.
        /// </remarks>
        public SpectrogramColourMap ErrorMap { get; set; } = SpectrogramColourMap.Default;

        /// <summary>
        /// The colour a symbol takes for its error magnitude.
        /// </summary>
        /// <param name="magnitude">How far the symbol landed from its ideal state.</param>
        /// <param name="fullScale">The magnitude at the top of the map.</param>
        /// <remarks>
        /// Linear, unlike the density ramps. A density spans three or four decades and has to be
        /// compressed to be read; error magnitudes span rather less than one, and compressing them
        /// would flatten the difference between a symbol at the decision boundary and one at its
        /// ideal state — which is the difference the colouring exists to show.
        /// </remarks>
        public PlotColor ForError(double magnitude, double fullScale)
        {
            SpectrogramColourMap map = ErrorMap;

            if (map == null || fullScale <= 0.0 || double.IsNaN(magnitude))
            {
                return Symbol;
            }

            double share = magnitude / fullScale;

            if (share < 0.0)
            {
                share = 0.0;
            }
            else if (share > 1.0)
            {
                share = 1.0;
            }

            return map.At(share);
        }

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
