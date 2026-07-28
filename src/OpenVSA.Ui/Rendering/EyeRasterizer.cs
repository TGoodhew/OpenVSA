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

            if (!IsLengthAllowed(lengthSymbols))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(lengthSymbols), lengthSymbols,
                    "REQ-UI-051 allows an eye of " + MinimumLengthSymbols + " to " +
                    MaximumLengthSymbols + " symbols.");
            }

            if (area.Width <= 1 || area.Height <= 1)
            {
                return new EyeRender(0, 0);
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
            int half = (int)Math.Round(lengthSymbols * trace.SamplesPerSymbol / 2.0);
            int folds = 0;

            foreach (int centre in trace.DecisionSampleIndices)
            {
                int previousX = int.MinValue;
                int previousY = 0;
                bool drew = false;

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
                        DrawLine(surface, previousX, previousY, x, y, area, colours.Trace);
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

            return new EyeRender(folds, lines);
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
    }

    /// <summary>What an eye render actually drew.</summary>
    public readonly struct EyeRender
    {
        internal EyeRender(int folds, int referenceLines)
        {
            Folds = folds;
            ReferenceLines = referenceLines;
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

        /// <inheritdoc />
        public override string ToString() =>
            Folds + " folds, " + ReferenceLines + " reference lines";
    }

    /// <summary>The colours an eye draws with.</summary>
    public sealed class EyeColours
    {
        /// <summary>The waveform.</summary>
        public PlotColor Trace { get; set; } = new PlotColor(0xFF, 0xD2, 0x00);

        /// <summary>The vertical reference lines at the symbol positions.</summary>
        public PlotColor ReferenceLine { get; set; } = new PlotColor(0x50, 0x50, 0x5C);
    }
}
