using System;
using System.Collections.Generic;
using OpenVSA.Demod.Results;

namespace OpenVSA.Ui.Rendering
{
    /// <summary>Which component an eye diagram shows.</summary>
    public enum EyeComponent
    {
        /// <summary>The in-phase component — the requirement's <c>I - Eye</c>.</summary>
        InPhase = 0,

        /// <summary>The quadrature component.</summary>
        Quadrature,
    }

    /// <summary>
    /// Draws an eye diagram (<c>REQ-UI-051</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>X in symbols, centred, and the centring is the criterion.</strong> "A one-symbol eye
    /// spans −½ to +½ symbol about the display centre, measured from the rendered frame." So the
    /// mapping from a position in symbols to a column is the load-bearing arithmetic here, and it
    /// is exposed as <see cref="XForSymbolOffset"/> so a test and the reference lines use the same
    /// one rather than two that agree by inspection.
    /// </para>
    /// <para>
    /// <strong>Vertical reference lines at the symbol positions.</strong> They fall where the
    /// maximum eye opening should be for a clean signal, which for a raised-cosine shaped signal is
    /// exactly the decision instants. Drawing them from the same fold arithmetic as the traces is
    /// what makes "a half-symbol offset fails" a real check rather than two independent guesses
    /// that happen to line up.
    /// </para>
    /// <para>
    /// <strong>Accumulative, and that is a property of the surface rather than of this method.</strong>
    /// "The VSA draws the first trace, then overlays the second trace, the third trace, and so on."
    /// So <see cref="Render"/> does not clear what it draws on; the caller clears when it wants a
    /// fresh eye, and successive acquisitions drawn onto the same surface overlay.
    /// </para>
    /// </remarks>
    public static class EyeRasterizer
    {
        /// <summary>The shortest eye <c>REQ-UI-051</c> allows, in symbols.</summary>
        public const double MinimumLengthSymbols = 0.1;

        /// <summary>The longest eye <c>REQ-UI-051</c> allows, in symbols.</summary>
        public const double MaximumLengthSymbols = 10.0;

        /// <summary>The eye length a display starts at, in symbols.</summary>
        /// <remarks><c>REQ-DEM-081</c>'s default, and the length the axis annotation assumes.</remarks>
        public const double DefaultLengthSymbols = 2.0;

        /// <summary>
        /// Whether an eye length is one <c>REQ-UI-051</c> allows.
        /// </summary>
        /// <param name="lengthSymbols">The length, in symbols.</param>
        public static bool IsLengthAllowed(double lengthSymbols) =>
            !double.IsNaN(lengthSymbols) &&
            lengthSymbols >= MinimumLengthSymbols &&
            lengthSymbols <= MaximumLengthSymbols;

        /// <summary>
        /// Where a position in symbols lands, measured from the centre of the display.
        /// </summary>
        /// <param name="offsetSymbols">Position in symbols; zero is the centre.</param>
        /// <param name="lengthSymbols">How many symbols the display spans.</param>
        /// <param name="area">The area drawn in.</param>
        /// <returns>A column, which may be outside the area.</returns>
        /// <remarks>
        /// The centre of the display is symbol offset zero, so a one-symbol eye runs from −½ to +½
        /// and the middle column is the symbol instant. Every other piece of geometry here is
        /// derived from this, including the reference lines.
        /// </remarks>
        public static int XForSymbolOffset(double offsetSymbols, double lengthSymbols, PixelRect area)
        {
            double fraction = offsetSymbols / lengthSymbols + 0.5;

            return area.X + (int)Math.Round(fraction * (area.Width - 1));
        }

        /// <summary>
        /// The symbol offsets that carry a reference line, for a given eye length.
        /// </summary>
        /// <param name="lengthSymbols">How many symbols the display spans.</param>
        /// <returns>Offsets in symbols, ascending, all within the display.</returns>
        /// <remarks>
        /// Whole symbols, including zero — the symbol positions, where the maximum eye opening
        /// should occur. An eye shorter than one symbol shows only the centre line, which is the
        /// honest answer rather than none.
        /// </remarks>
        public static IReadOnlyList<double> ReferenceOffsets(double lengthSymbols)
        {
            var offsets = new List<double>();

            int reach = (int)Math.Floor(lengthSymbols / 2.0);

            for (int symbol = -reach; symbol <= reach; symbol++)
            {
                offsets.Add(symbol);
            }

            return offsets;
        }

        /// <summary>
        /// How many folds a result implies — one per symbol instant in the Result Length.
        /// </summary>
        /// <param name="trace">The demodulated result.</param>
        /// <exception cref="ArgumentNullException"><paramref name="trace"/> is null.</exception>
        /// <remarks>
        /// <para>
        /// <c>REQ-DEM-081</c> asks that the eye be built "across the Result Length", and its
        /// criterion is that "the trace count equals the number of folds the Result Length and eye
        /// length imply, so a partial build fails". This is that number, computed from the result
        /// alone, so <see cref="EyeRender.Folds"/> can be checked against something other than
        /// itself.
        /// </para>
        /// <para>
        /// <strong>Eye length does not change it.</strong> A longer eye makes each fold wider, not
        /// the folds fewer: every symbol instant still contributes one. What a long eye does change
        /// is how many folds run off the end of the captured samples, and that is
        /// <see cref="EyeRender.TruncatedFolds"/> — reported rather than deducted, because a fold
        /// clipped by the capture is a fold that was built, and quietly dropping it is the partial
        /// build the criterion is looking for.
        /// </para>
        /// </remarks>
        public static int ExpectedFolds(SymbolTrace trace)
        {
            if (trace == null)
            {
                throw new ArgumentNullException(nameof(trace));
            }

            return trace.DecisionSampleIndices.Count;
        }

        /// <summary>
        /// Draws the eye, overlaying whatever is already on the surface.
        /// </summary>
        /// <param name="surface">The surface to draw on; not cleared.</param>
        /// <param name="area">The rectangle to draw in.</param>
        /// <param name="trace">The demodulated result.</param>
        /// <param name="component">Which component to show.</param>
        /// <param name="lengthSymbols">How many symbols the display spans.</param>
        /// <param name="colours">What to draw with.</param>
        /// <param name="scale">
        /// The value at the top of the display; a non-positive value takes it from the result.
        /// </param>
        /// <exception cref="ArgumentNullException">An argument is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException">
        /// The eye length is outside <c>REQ-UI-051</c>'s range.
        /// </exception>
        public static EyeRender Render(
            PixelSurface surface,
            PixelRect area,
            SymbolTrace trace,
            EyeComponent component,
            double lengthSymbols,
            EyeColours colours,
            double scale = 0.0,
            bool persistence = false,
            int selected = -1)
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

            if (!IsLengthAllowed(lengthSymbols))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(lengthSymbols), lengthSymbols,
                    "REQ-UI-051 allows an eye of " + MinimumLengthSymbols + " to " +
                    MaximumLengthSymbols + " symbols.");
            }

            if (area.Width <= 1 || area.Height <= 1)
            {
                return new EyeRender(0, 0, 0, 0);
            }

            double extent = scale > 0.0 ? scale : Extent(trace, component);

            // The reference lines first, so the geometry is drawn over them: they are a reference
            // and the eye is the subject.
            int lines = 0;

            foreach (double offset in ReferenceOffsets(lengthSymbols))
            {
                int x = XForSymbolOffset(offset, lengthSymbols, area);

                if (x < area.X || x >= area.Right)
                {
                    continue;
                }

                for (int y = area.Y; y < area.Bottom; y++)
                {
                    surface.SetPixel(x, y, colours.ReferenceLine);
                }

                lines++;
            }

            // One fold per symbol instant, each spanning the whole display width about it.
            //
            // The geometry is counted into a traversal buffer first and painted afterwards, rather
            // than written to the surface as the walk goes. That is what makes REQ-DEM-081's
            // "turning it off leaves the eye's geometry unchanged" true by construction instead of
            // true by inspection: persistence changes only the colour chosen for a cell, never
            // which cells the walk visits, so the two settings cannot drift apart.
            int half = (int)Math.Round(lengthSymbols * trace.SamplesPerSymbol / 2.0);
            int folds = 0;
            int truncated = 0;

            var traversals = new int[area.Width * area.Height];

            foreach (int centre in trace.DecisionSampleIndices)
            {
                int previousX = int.MinValue;
                int previousY = 0;
                bool drew = false;

                if (centre - half < 0 || centre + half >= trace.SampleCount)
                {
                    truncated++;
                }

                for (int at = centre - half; at <= centre + half; at++)
                {
                    if (at < 0 || at >= trace.SampleCount)
                    {
                        continue;
                    }

                    double offsetSymbols = (at - centre) / (double)trace.SamplesPerSymbol;

                    int x = XForSymbolOffset(offsetSymbols, lengthSymbols, area);
                    int y = YForValue(Value(trace, at, component), extent, area);

                    if (previousX != int.MinValue)
                    {
                        CountLine(traversals, area, previousX, previousY, x, y);
                    }

                    previousX = x;
                    previousY = y;
                    drew = true;
                }

                if (drew)
                {
                    folds++;
                }
            }

            int peak = Paint(surface, area, traversals, colours, persistence);

            // The selected symbol's own fold, over the top of the rest (REQ-DEM-083). Drawn from
            // the same walk as the eye it sits in — a second piece of fold arithmetic here is a
            // second chance to be half a symbol out, and it would be out only for the highlight,
            // which is the hardest kind of disagreement to notice.
            bool selectionDrawn = false;

            if (selected >= 0 && selected < trace.DecisionSampleIndices.Count)
            {
                int centre = trace.DecisionSampleIndices[selected];
                int previousX = int.MinValue;
                int previousY = 0;

                for (int at = centre - half; at <= centre + half; at++)
                {
                    if (at < 0 || at >= trace.SampleCount)
                    {
                        continue;
                    }

                    double offsetSymbols = (at - centre) / (double)trace.SamplesPerSymbol;

                    int x = XForSymbolOffset(offsetSymbols, lengthSymbols, area);
                    int y = YForValue(Value(trace, at, component), extent, area);

                    if (previousX != int.MinValue)
                    {
                        DrawLine(surface, area, previousX, previousY, x, y, colours.Selection);
                    }

                    previousX = x;
                    previousY = y;
                    selectionDrawn = true;
                }
            }

            return new EyeRender(folds, lines, truncated, peak, selectionDrawn);
        }

        /// <summary>
        /// Writes the walked geometry onto the surface, shaded by traversal count when asked.
        /// </summary>
        /// <returns>The most traversals any one cell took.</returns>
        private static int Paint(
            PixelSurface surface,
            PixelRect area,
            int[] traversals,
            EyeColours colours,
            bool persistence)
        {
            int peak = 0;

            foreach (int count in traversals)
            {
                if (count > peak)
                {
                    peak = count;
                }
            }

            if (peak == 0)
            {
                return 0;
            }

            for (int row = 0; row < area.Height; row++)
            {
                for (int column = 0; column < area.Width; column++)
                {
                    int count = traversals[(row * area.Width) + column];

                    if (count == 0)
                    {
                        continue;
                    }

                    surface.SetPixel(
                        area.X + column,
                        area.Y + row,
                        persistence ? colours.ForTraversals(count, peak) : colours.Trace);
                }
            }

            return peak;
        }

        /// <summary>Where a value lands vertically.</summary>
        /// <param name="value">The value.</param>
        /// <param name="extent">The value at the top of the display.</param>
        /// <param name="area">The area.</param>
        public static int YForValue(double value, double extent, PixelRect area) =>
            area.Y + (area.Height - 1) -
            (int)Math.Round((value / extent + 1.0) * 0.5 * (area.Height - 1));

        /// <summary>
        /// The value at the top of the display for a result.
        /// </summary>
        /// <param name="trace">The result.</param>
        /// <param name="component">Which component.</param>
        /// <exception cref="ArgumentNullException"><paramref name="trace"/> is null.</exception>
        public static double Extent(SymbolTrace trace, EyeComponent component)
        {
            if (trace == null)
            {
                throw new ArgumentNullException(nameof(trace));
            }

            double furthest = 0.0;

            for (int at = 0; at < trace.SampleCount; at++)
            {
                furthest = Math.Max(furthest, Math.Abs(Value(trace, at, component)));
            }

            return furthest < 1e-9 ? 1.0 : furthest * 1.1;
        }

        private static double Value(SymbolTrace trace, int at, EyeComponent component)
        {
            ConstellationPoint sample = trace.SampleAt(at);

            return component == EyeComponent.InPhase ? sample.I : sample.Q;
        }

        /// <summary>
        /// Walks a segment, counting one traversal for each cell it passes through.
        /// </summary>
        /// <remarks>
        /// A cell the same fold crosses twice counts twice, which is what makes a path the
        /// waveform doubles back along read as denser than one it passes once. Bresenham, so the
        /// cells counted are the ones a drawn line would have inked and no others.
        /// </remarks>
        /// <summary>Walks a segment, inking each cell it passes through.</summary>
        /// <remarks>
        /// The same walk as <see cref="CountLine"/>, writing colour instead of counting, so the
        /// selected fold lands on exactly the cells its own fold contributed to the eye.
        /// </remarks>
        private static void DrawLine(
            PixelSurface surface, PixelRect area, int x0, int y0, int x1, int y1, PlotColor colour)
        {
            int dx = Math.Abs(x1 - x0);
            int dy = -Math.Abs(y1 - y0);
            int sx = x0 < x1 ? 1 : -1;
            int sy = y0 < y1 ? 1 : -1;
            int error = dx + dy;

            while (true)
            {
                if (area.Contains(x0, y0))
                {
                    surface.SetPixel(x0, y0, colour);
                }

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

        private static void CountLine(
            int[] traversals, PixelRect area, int x0, int y0, int x1, int y1)
        {
            int dx = Math.Abs(x1 - x0);
            int dy = -Math.Abs(y1 - y0);
            int sx = x0 < x1 ? 1 : -1;
            int sy = y0 < y1 ? 1 : -1;
            int error = dx + dy;

            while (true)
            {
                if (area.Contains(x0, y0))
                {
                    traversals[((y0 - area.Y) * area.Width) + (x0 - area.X)]++;
                }

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
    }

    /// <summary>What an eye render actually drew.</summary>
    public readonly struct EyeRender
    {
        internal EyeRender(
            int folds, int referenceLines, int truncatedFolds, int peakTraversals,
            bool selectionDrawn = false)
        {
            Folds = folds;
            ReferenceLines = referenceLines;
            TruncatedFolds = truncatedFolds;
            PeakTraversals = peakTraversals;
            SelectionDrawn = selectionDrawn;
        }

        /// <summary>
        /// How many folds were overlaid — one per symbol instant that had samples to draw.
        /// </summary>
        /// <remarks>
        /// <c>REQ-DEM-081</c>'s "the trace count equals the number of folds the Result Length and
        /// eye length imply, so a partial build fails".
        /// </remarks>
        public int Folds { get; }

        /// <summary>How many vertical reference lines were drawn.</summary>
        public int ReferenceLines { get; }

        /// <summary>
        /// How many of those folds ran off the end of the captured samples and were clipped.
        /// </summary>
        /// <remarks>
        /// A fold centred within half an eye length of either end of the capture has no samples to
        /// draw on one side of the symbol instant. It is still one of <see cref="Folds"/>, because
        /// the symbol is in the Result Length and the eye was built from it; this says how many
        /// carry less than a full eye's width of waveform, which is a property of the capture
        /// rather than of the build.
        /// </remarks>
        public int TruncatedFolds { get; }

        /// <summary>The most times any one cell of the display was traversed.</summary>
        /// <remarks>
        /// The denominator of the persistence shading, and zero when nothing was drawn. Reported so
        /// a display can say how deep the shading goes, and so a test can tell a flat eye from a
        /// shaded one without reading pixels.
        /// </remarks>
        public int PeakTraversals { get; }

        /// <summary>Whether the selected symbol's fold was drawn over the eye.</summary>
        public bool SelectionDrawn { get; }

        /// <inheritdoc />
        public override string ToString() =>
            Folds + " folds (" + TruncatedFolds + " truncated), " + ReferenceLines +
            " reference lines, peak " + PeakTraversals +
            (SelectionDrawn ? ", one selected" : string.Empty);
    }

    /// <summary>The colours an eye draws with.</summary>
    public sealed class EyeColours
    {
        /// <summary>The waveform.</summary>
        public PlotColor Trace { get; set; } = new PlotColor(0xFF, 0xD2, 0x00);

        /// <summary>The vertical reference lines at the symbol positions.</summary>
        public PlotColor ReferenceLine { get; set; } = new PlotColor(0x50, 0x50, 0x5C);

        /// <summary>The selected symbol's fold (<c>REQ-DEM-083</c>).</summary>
        /// <remarks>
        /// White, matching the constellation's selection ring: one selection shown on two displays
        /// should look like one selection.
        /// </remarks>
        public PlotColor Selection { get; set; } = PlotColor.White;

        /// <summary>
        /// How bright a cell the waveform crossed just once is, as a fraction of
        /// <see cref="Trace"/>.
        /// </summary>
        /// <remarks>
        /// A fifth. Not zero: a path taken once is rare, not absent, and an eye whose rare paths
        /// are invisible has thrown away the outliers that are the reason to look at one. Expressed
        /// as a fraction of the trace colour rather than as a colour of its own, so recolouring the
        /// eye (<c>REQ-UI-022</c>) does not leave the shading a different hue from the trace it
        /// shades.
        /// </remarks>
        public double PersistenceFloorFraction { get; set; } = 0.2;

        /// <summary>
        /// The colour a cell takes for its share of the traversals.
        /// </summary>
        /// <param name="traversals">How many times the waveform crossed this cell.</param>
        /// <param name="peak">The most any cell was crossed.</param>
        /// <remarks>
        /// <para>
        /// <strong>Square-rooted, and that is the point of the shading.</strong> An eye's centre
        /// rail is crossed by every fold and its rarest excursion by one, so on a linear ramp the
        /// excursion sits at a thousandth of full brightness and is indistinguishable from the
        /// floor — the shading would then separate nothing over the range that matters. The square
        /// root spreads the low counts, which is where an eye's information is.
        /// </para>
        /// <para>
        /// Monotonic in <paramref name="traversals"/>, which is <c>REQ-DEM-081</c>'s criterion:
        /// more traversals is never a dimmer cell.
        /// </para>
        /// </remarks>
        public PlotColor ForTraversals(int traversals, int peak)
        {
            if (traversals <= 0 || peak <= 0)
            {
                return Trace;
            }

            double floor = PersistenceFloorFraction;

            if (floor < 0.0)
            {
                floor = 0.0;
            }
            else if (floor > 1.0)
            {
                floor = 1.0;
            }

            double share = Math.Sqrt(Math.Min(traversals, peak) / (double)peak);
            double weight = floor + ((1.0 - floor) * share);

            return new PlotColor(
                Channel(Trace.R, weight), Channel(Trace.G, weight), Channel(Trace.B, weight),
                Trace.A);
        }

        private static byte Channel(byte value, double weight)
        {
            int scaled = (int)Math.Round(value * weight);

            return (byte)(scaled < 0 ? 0 : scaled > 255 ? 255 : scaled);
        }
    }
}
