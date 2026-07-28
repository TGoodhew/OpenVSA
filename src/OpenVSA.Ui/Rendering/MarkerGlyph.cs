using System;

namespace OpenVSA.Ui.Rendering
{
    /// <summary>
    /// Draws marker glyphs into a <see cref="PixelSurface"/> (<c>REQ-UI-030</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two details the requirement calls out as easy to miss, and both are geometric rather than
    /// stylistic:
    /// </para>
    /// <list type="bullet">
    /// <item><description><strong>The diamond sits above the data point; the X is centred on
    /// it.</strong> A Normal or Delta marker's diamond has its bounds entirely above the point it
    /// marks, so it never hides the data. A Fixed marker's X is centred on the point, so its
    /// centroid and the point coincide.</description></item>
    /// <item><description><strong>Selection is conveyed by fill, not by colour index.</strong> The
    /// selected marker is solid and the rest hollow; colour comes from
    /// <see cref="PlotPalette.SelectedMarker"/> / <see cref="PlotPalette.NotSelectedMarker"/> by
    /// state. Two unselected markers of different numbers are the same colour — colouring by marker
    /// number is the obvious implementation and the requirement's test rejects it.</description></item>
    /// </list>
    /// <para>
    /// Glyphs are rasterised rather than drawn as WPF shapes, for the same reason as the trace:
    /// they belong to the pixel buffer the render marshal produces, and a marker per frame would
    /// otherwise add elements to the visual tree on the render path.
    /// </para>
    /// </remarks>
    public static class MarkerGlyph
    {
        /// <summary>Half-width of a glyph, in pixels; the full glyph is twice this plus one.</summary>
        public const int HalfSize = 4;

        /// <summary>Clear pixels between the diamond's lowest vertex and the data point.</summary>
        /// <remarks>
        /// One would satisfy "entirely above" arithmetically and look as though the glyph were
        /// resting on the trace; two reads as deliberate at every DPI the plot is drawn at.
        /// </remarks>
        public const int DiamondClearance = 2;

        /// <summary>
        /// Draws a diamond above a data point, for a Normal or Delta marker.
        /// </summary>
        /// <param name="surface">Target surface.</param>
        /// <param name="x">Data point's column.</param>
        /// <param name="y">Data point's row.</param>
        /// <param name="color">Glyph colour, by selection state.</param>
        /// <param name="filled">Whether this is the selected marker.</param>
        /// <exception cref="ArgumentNullException"><paramref name="surface"/> is null.</exception>
        public static void DrawDiamond(PixelSurface surface, int x, int y, PlotColor color, bool filled)
        {
            if (surface == null)
            {
                throw new ArgumentNullException(nameof(surface));
            }

            // The centre sits a full half-height plus the clearance above the point, which puts the
            // lowest vertex DiamondClearance pixels clear of it.
            int centreY = y - HalfSize - DiamondClearance;

            for (int row = -HalfSize; row <= HalfSize; row++)
            {
                int halfWidth = HalfSize - Math.Abs(row);

                if (filled || halfWidth == 0)
                {
                    surface.DrawHorizontal(centreY + row, x - halfWidth, x + halfWidth, color);
                }
                else
                {
                    surface.SetPixel(x - halfWidth, centreY + row, color);
                    surface.SetPixel(x + halfWidth, centreY + row, color);
                }
            }
        }

        /// <summary>
        /// Draws an X centred on a data point, for a Fixed marker.
        /// </summary>
        /// <param name="surface">Target surface.</param>
        /// <param name="x">Data point's column.</param>
        /// <param name="y">Data point's row.</param>
        /// <param name="color">Glyph colour, by selection state.</param>
        /// <param name="filled">Whether this is the selected marker; a selected X is drawn thicker.</param>
        /// <exception cref="ArgumentNullException"><paramref name="surface"/> is null.</exception>
        /// <remarks>
        /// An X has no interior to fill, so selection thickens the strokes instead. The centroid
        /// stays on the data point either way, which is what the requirement measures.
        /// </remarks>
        public static void DrawCross(PixelSurface surface, int x, int y, PlotColor color, bool filled)
        {
            if (surface == null)
            {
                throw new ArgumentNullException(nameof(surface));
            }

            for (int offset = -HalfSize; offset <= HalfSize; offset++)
            {
                surface.SetPixel(x + offset, y + offset, color);
                surface.SetPixel(x + offset, y - offset, color);

                if (filled)
                {
                    surface.SetPixel(x + offset + 1, y + offset, color);
                    surface.SetPixel(x + offset + 1, y - offset, color);
                }
            }
        }

        /// <summary>
        /// The rectangle a diamond occupies, for hit-testing and for asserting placement.
        /// </summary>
        /// <param name="x">Data point's column.</param>
        /// <param name="y">Data point's row.</param>
        public static PixelRect DiamondBounds(int x, int y)
        {
            int centreY = y - HalfSize - DiamondClearance;
            return new PixelRect(x - HalfSize, centreY - HalfSize, HalfSize * 2 + 1, HalfSize * 2 + 1);
        }

        /// <summary>The rectangle an X occupies, centred on the data point.</summary>
        /// <param name="x">Data point's column.</param>
        /// <param name="y">Data point's row.</param>
        public static PixelRect CrossBounds(int x, int y) =>
            new PixelRect(x - HalfSize, y - HalfSize, HalfSize * 2 + 1, HalfSize * 2 + 1);

        /// <summary>
        /// How many pixels of a marker rule are drawn out of every <see cref="RulePeriod"/>.
        /// </summary>
        /// <remarks>
        /// A spectrogram marker crosses the whole display, and every pixel it covers is a cell it
        /// hides. Dashed rather than solid so that the data under the line can still be read —
        /// which matters most exactly where a user has put the marker.
        /// </remarks>
        public const int RuleDashLength = 3;

        /// <summary>The repeat of a marker rule's dashes, in pixels.</summary>
        public const int RulePeriod = 5;

        /// <summary>
        /// Draws a spectrogram marker as a vertical rule (<c>REQ-UI-054</c>).
        /// </summary>
        /// <param name="surface">The surface to draw on.</param>
        /// <param name="x">The column to draw down.</param>
        /// <param name="area">The rectangle to stay inside.</param>
        /// <param name="color">The rule's colour.</param>
        /// <returns>Whether anything was drawn.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="surface"/> is null.</exception>
        public static bool DrawVerticalRule(PixelSurface surface, int x, PixelRect area, PlotColor color)
        {
            if (surface == null)
            {
                throw new ArgumentNullException(nameof(surface));
            }

            if (x < area.X || x >= area.Right)
            {
                return false;
            }

            for (int y = area.Y; y < area.Bottom; y++)
            {
                if ((y - area.Y) % RulePeriod < RuleDashLength)
                {
                    surface.SetPixel(x, y, color);
                }
            }

            return area.Height > 0;
        }

        /// <summary>
        /// Draws a trace-select marker as a horizontal rule (<c>REQ-UI-054</c>).
        /// </summary>
        /// <param name="surface">The surface to draw on.</param>
        /// <param name="y">The row to draw across.</param>
        /// <param name="area">The rectangle to stay inside.</param>
        /// <param name="color">The rule's colour.</param>
        /// <returns>Whether anything was drawn.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="surface"/> is null.</exception>
        public static bool DrawHorizontalRule(PixelSurface surface, int y, PixelRect area, PlotColor color)
        {
            if (surface == null)
            {
                throw new ArgumentNullException(nameof(surface));
            }

            if (y < area.Y || y >= area.Bottom)
            {
                return false;
            }

            for (int x = area.X; x < area.Right; x++)
            {
                if ((x - area.X) % RulePeriod < RuleDashLength)
                {
                    surface.SetPixel(x, y, color);
                }
            }

            return area.Width > 0;
        }
    }
}
