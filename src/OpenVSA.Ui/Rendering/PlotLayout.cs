using System;

namespace OpenVSA.Ui.Rendering
{
    /// <summary>
    /// The geometry of a plot: where the graticule sits and how values map to pixels.
    /// </summary>
    /// <remarks>
    /// The three zones of <c>REQ-UI-010</c> follow from this. The graticule rectangle is the inner
    /// zone; everything outside it, inside the surface, is the annotation band. Keeping the
    /// division here rather than in the rasteriser is what lets a test assert that a sampled pixel
    /// belongs to the zone it claims to.
    /// </remarks>
    public sealed class PlotLayout
    {
        /// <summary>
        /// Graticule divisions on each axis unless told otherwise (<c>REQ-UI-012</c>).
        /// </summary>
        /// <remarks>
        /// Ten, and <c>REQ-UI-012</c> is candid that no vendor source states a count. The strongest
        /// evidence is arithmetic: the reference product's predecessor published 401 display points
        /// per trace — 400 intervals, which is exactly a ten-division grid at 40 pixels a division.
        /// That is an inference from a published number rather than a quotation, which is why the
        /// count is a setting rather than a constant. The requirement carries the model this is
        /// inferred from; this assembly may not, per <c>REQ-HAL-002</c>.
        /// </remarks>
        public const int DefaultDivisions = 10;

        /// <summary>Creates a layout.</summary>
        /// <param name="width">Surface width in pixels; must be positive.</param>
        /// <param name="height">Surface height in pixels; must be positive.</param>
        /// <param name="margin">Annotation band thickness on every side, in pixels.</param>
        /// <param name="topValue">Value at the top of the graticule, in the trace's units.</param>
        /// <param name="bottomValue">Value at the bottom of the graticule.</param>
        /// <param name="horizontalDivisions">Graticule columns; must be positive.</param>
        /// <param name="verticalDivisions">Graticule rows; must be positive.</param>
        /// <param name="yReferencePercent">Where the reference line sits, 0 at the bottom of the grid through 100 at the top (<c>REQ-UI-013</c>).</param>
        /// <param name="xReferencePercent">Where the reference column sits, 0 at the left edge through 100 at the right.</param>
        /// <exception cref="ArgumentOutOfRangeException">A dimension or division count is not positive, a reference position is outside 0 to 100, or the margins leave no graticule.</exception>
        /// <exception cref="ArgumentException">The value range is empty or not finite.</exception>
        public PlotLayout(
            int width,
            int height,
            int margin = 48,
            double topValue = 0.0,
            double bottomValue = -100.0,
            int horizontalDivisions = DefaultDivisions,
            int verticalDivisions = DefaultDivisions,
            int yReferencePercent = ReferencePosition.TopPercent,
            int xReferencePercent = ReferencePosition.DefaultXPercent)
        {
            if (width <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(width), width, "Width must be positive.");
            }

            if (height <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(height), height, "Height must be positive.");
            }

            if (margin < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(margin), margin, "Margin cannot be negative.");
            }

            if (horizontalDivisions <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(horizontalDivisions), horizontalDivisions, "Divisions must be positive.");
            }

            if (verticalDivisions <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(verticalDivisions), verticalDivisions, "Divisions must be positive.");
            }

            int graticuleWidth = width - margin * 2;
            int graticuleHeight = height - margin * 2;

            if (graticuleWidth <= 0 || graticuleHeight <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(margin), margin, "Margins leave no room for a graticule.");
            }

            if (double.IsNaN(topValue) || double.IsInfinity(topValue) ||
                double.IsNaN(bottomValue) || double.IsInfinity(bottomValue))
            {
                throw new ArgumentException("Axis limits must be finite.", nameof(topValue));
            }

            if (topValue <= bottomValue)
            {
                throw new ArgumentException(
                    "The top of the axis must be above the bottom.", nameof(topValue));
            }

            Width = width;
            Height = height;
            YReferencePercent = ReferencePosition.Validate(yReferencePercent, nameof(yReferencePercent));
            XReferencePercent = ReferencePosition.Validate(xReferencePercent, nameof(xReferencePercent));
            Graticule = new PixelRect(margin, margin, graticuleWidth, graticuleHeight);
            TopValue = topValue;
            BottomValue = bottomValue;
            HorizontalDivisions = horizontalDivisions;
            VerticalDivisions = verticalDivisions;
        }

        /// <summary>Surface width in pixels.</summary>
        public int Width { get; }

        /// <summary>Surface height in pixels.</summary>
        public int Height { get; }

        /// <summary>The graticule rectangle: the inner zone of <c>REQ-UI-010</c>.</summary>
        public PixelRect Graticule { get; }

        /// <summary>Value at the top of the graticule.</summary>
        public double TopValue { get; }

        /// <summary>Value at the bottom of the graticule.</summary>
        public double BottomValue { get; }

        /// <summary>Number of graticule columns.</summary>
        public int HorizontalDivisions { get; }

        /// <summary>Number of graticule rows.</summary>
        public int VerticalDivisions { get; }

        /// <summary>
        /// Where the reference line sits, 0 at the bottom of the grid through 100 at the top
        /// (<c>REQ-UI-013</c>).
        /// </summary>
        public int YReferencePercent { get; }

        /// <summary>Where the reference column sits, 0 at the left edge through 100 at the right.</summary>
        public int XReferencePercent { get; }

        /// <summary>
        /// The row the reference line falls on.
        /// </summary>
        /// <remarks>
        /// Measured from the bottom, because the percentage is: 100 % is the top of the grid and
        /// 0 % the bottom. At 100 % this is the topmost row of the graticule and so coincides with
        /// the topmost graticule line, which is the sense in which "the reference line at the top of
        /// the grid" is a statement about layout and not about an extra line being drawn.
        /// </remarks>
        public int ReferenceLineY() =>
            Graticule.Bottom - 1 -
            (int)Math.Round(YReferencePercent / 100.0 * (Graticule.Height - 1));

        /// <summary>The column the reference position falls on, 0 % at the left edge.</summary>
        public int ReferenceLineX() =>
            Graticule.X + (int)Math.Round(XReferencePercent / 100.0 * (Graticule.Width - 1));

        /// <summary>Whether a pixel lies in the annotation band — inside the surface, outside the graticule.</summary>
        /// <param name="x">Horizontal coordinate.</param>
        /// <param name="y">Vertical coordinate.</param>
        public bool IsInAnnotationBand(int x, int y) =>
            x >= 0 && x < Width && y >= 0 && y < Height && !Graticule.Contains(x, y);

        /// <summary>
        /// Maps a trace value to a row, clamped to the graticule.
        /// </summary>
        /// <param name="value">The value, in the trace's units.</param>
        /// <returns>A row within the graticule, or −1 for a blanked (<see cref="float.NaN"/>) value.</returns>
        /// <remarks>
        /// Clamping rather than discarding: a trace running off the top of the screen should be
        /// drawn pinned to the edge, which is what an analyser does and what tells the user the
        /// signal is off-scale. Discarding it would look like no signal at all.
        /// </remarks>
        public int ValueToY(double value)
        {
            if (double.IsNaN(value))
            {
                return -1;
            }

            double fraction = (TopValue - value) / (TopValue - BottomValue);
            double row = Graticule.Y + fraction * (Graticule.Height - 1);

            if (row < Graticule.Y)
            {
                return Graticule.Y;
            }

            if (row > Graticule.Bottom - 1)
            {
                return Graticule.Bottom - 1;
            }

            return (int)Math.Round(row);
        }

        /// <summary>Row of a horizontal graticule line.</summary>
        /// <param name="division">Division index, 0 at the top through <see cref="VerticalDivisions"/> at the bottom.</param>
        /// <exception cref="ArgumentOutOfRangeException">The index is outside the division range.</exception>
        public int HorizontalGridLineY(int division)
        {
            if (division < 0 || division > VerticalDivisions)
            {
                throw new ArgumentOutOfRangeException(nameof(division), division, "Outside the graticule.");
            }

            return Graticule.Y + (int)Math.Round(
                division * (Graticule.Height - 1.0) / VerticalDivisions);
        }

        /// <summary>Column of a vertical graticule line.</summary>
        /// <param name="division">Division index, 0 at the left through <see cref="HorizontalDivisions"/> at the right.</param>
        /// <exception cref="ArgumentOutOfRangeException">The index is outside the division range.</exception>
        public int VerticalGridLineX(int division)
        {
            if (division < 0 || division > HorizontalDivisions)
            {
                throw new ArgumentOutOfRangeException(nameof(division), division, "Outside the graticule.");
            }

            return Graticule.X + (int)Math.Round(
                division * (Graticule.Width - 1.0) / HorizontalDivisions);
        }
    }
}
