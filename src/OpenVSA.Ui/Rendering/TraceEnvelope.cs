using System;

namespace OpenVSA.Ui.Rendering
{
    /// <summary>
    /// Reduces — or stretches — a trace to one (minimum, maximum) pair per pixel column.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two directions, and only one of them is <c>REQ-NFR-006</c>. With more points than columns
    /// the trace is decimated by min/max envelope, which is what that requirement is about and what
    /// <see cref="TraceDecimator"/> does. With <em>fewer</em> points than columns — 401 displayed
    /// points on an 800-pixel graticule, a perfectly ordinary setting under <c>REQ-DSP-022</c> —
    /// decimation leaves every other column with no contributing sample, and a column with no
    /// sample is blanked. The trace would draw as a dotted line.
    /// </para>
    /// <para>
    /// The fix is not to blank fewer columns but to interpolate: a spectrum is a sampled function,
    /// and the pixels between two of its points are on the line between them. The endpoints are
    /// anchored — column 0 is the first point and the last column is the last point — so the trace
    /// spans exactly the axis the annotation claims rather than stopping short of the right-hand
    /// graticule line.
    /// </para>
    /// </remarks>
    public static class TraceEnvelope
    {
        /// <summary>
        /// Builds the per-column envelope, decimating or interpolating as the point count requires.
        /// </summary>
        /// <param name="values">Trace values. <see cref="float.NaN"/> marks a blanked point.</param>
        /// <param name="columns">Pixel columns; must be positive.</param>
        /// <param name="minMax">Receives <c>columns × 2</c> values as (minimum, maximum) pairs.</param>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="columns"/> is not positive.</exception>
        /// <exception cref="ArgumentException"><paramref name="minMax"/> is not exactly <c>columns × 2</c> long.</exception>
        public static void Build(ReadOnlySpan<float> values, int columns, Span<float> minMax)
        {
            if (columns <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(columns), columns, "Column count must be positive.");
            }

            if (minMax.Length != columns * 2)
            {
                throw new ArgumentException(
                    "Expected " + (columns * 2) + " output values for " + columns +
                    " columns, got " + minMax.Length + ".",
                    nameof(minMax));
            }

            if (TraceDecimator.IsRequired(values.Length, columns))
            {
                TraceDecimator.Decimate(values, columns, minMax);
                return;
            }

            Interpolate(values, columns, minMax);
        }

        /// <summary>
        /// Whether a trace of this length will be interpolated rather than decimated.
        /// </summary>
        /// <param name="pointCount">Points in the trace.</param>
        /// <param name="columns">Available pixel columns.</param>
        public static bool IsInterpolated(int pointCount, int columns) =>
            !TraceDecimator.IsRequired(pointCount, columns);

        /// <summary>
        /// The pixel column a trace point is drawn in.
        /// </summary>
        /// <param name="index">Point index.</param>
        /// <param name="pointCount">Points in the trace.</param>
        /// <param name="columns">Pixel columns.</param>
        /// <returns>A column within the graticule, clamped to it.</returns>
        /// <remarks>
        /// Must use the same mapping <see cref="Build"/> did, or a marker glyph lands beside the
        /// feature it marks rather than on it — by a pixel at 800 points and by rather more at 51.
        /// The two directions differ because the mappings do: decimation partitions the points
        /// across columns, interpolation stretches them between the first and last.
        /// </remarks>
        public static int ColumnFor(int index, int pointCount, int columns)
        {
            if (columns <= 0 || pointCount <= 0)
            {
                return 0;
            }

            long column = IsInterpolated(pointCount, columns)
                ? (pointCount == 1 ? 0 : (long)Math.Round((double)index * (columns - 1) / (pointCount - 1)))
                : (long)index * columns / pointCount;

            if (column < 0)
            {
                return 0;
            }

            return column > columns - 1 ? columns - 1 : (int)column;
        }

        /// <summary>
        /// The trace point a pixel column corresponds to: the inverse of <see cref="ColumnFor"/>.
        /// </summary>
        /// <param name="column">Pixel column.</param>
        /// <param name="pointCount">Points in the trace.</param>
        /// <param name="columns">Pixel columns.</param>
        /// <returns>A point index, clamped to the trace.</returns>
        /// <remarks>
        /// What a click on the plot means. Under decimation a column covers several points and this
        /// returns the first of them, which is the convention <see cref="TraceDecimator"/> uses for
        /// the column's own range.
        /// </remarks>
        public static int IndexFor(int column, int pointCount, int columns)
        {
            if (columns <= 0 || pointCount <= 0)
            {
                return 0;
            }

            long index = IsInterpolated(pointCount, columns)
                ? (columns == 1 ? 0 : (long)Math.Round((double)column * (pointCount - 1) / (columns - 1)))
                : (long)column * pointCount / columns;

            if (index < 0)
            {
                return 0;
            }

            return index > pointCount - 1 ? pointCount - 1 : (int)index;
        }

        private static void Interpolate(ReadOnlySpan<float> values, int columns, Span<float> minMax)
        {
            int count = values.Length;

            if (count == 0)
            {
                for (int i = 0; i < minMax.Length; i++)
                {
                    minMax[i] = float.NaN;
                }

                return;
            }

            for (int column = 0; column < columns; column++)
            {
                // Anchored at both ends: column 0 is point 0, and the last column is the last
                // point. Using count rather than count-1 here would leave the trace short of the
                // right-hand graticule line by one column's worth of span.
                double position = columns == 1
                    ? 0.0
                    : (double)column * (count - 1) / (columns - 1);

                int lower = (int)position;
                int upper = lower + 1 < count ? lower + 1 : lower;
                double fraction = position - lower;

                float first = values[lower];
                float second = values[upper];

                // A column landing exactly on a point takes that point, whatever its neighbour is:
                // otherwise a single blanked point would blank the column showing the good point
                // beside it. Between two points, NaN in either blanks the column - a blanked point
                // has no value to interpolate towards, and inventing one draws a line into a gap.
                float value;

                if (fraction <= 0.0)
                {
                    value = first;
                }
                else if (float.IsNaN(first) || float.IsNaN(second))
                {
                    value = float.NaN;
                }
                else
                {
                    value = (float)(first + (second - first) * fraction);
                }

                minMax[column * 2] = value;
                minMax[column * 2 + 1] = value;
            }
        }
    }
}
