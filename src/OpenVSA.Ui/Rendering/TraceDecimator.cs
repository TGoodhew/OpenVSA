using System;

namespace OpenVSA.Ui.Rendering
{
    /// <summary>
    /// Min/max envelope decimation, one column per horizontal pixel (<c>REQ-NFR-006</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Never point-skipping.</strong> Taking every <c>N/W</c>th point is faster and hides
    /// narrow spectral peaks and transients — precisely the features an analyser exists to reveal.
    /// A one-bin spur has a 1-in-655 chance of surviving a skip at 524 288 points across 800
    /// pixels; retaining both extrema of every column makes it certain.
    /// </para>
    /// <para>
    /// Every source point belongs to exactly one column and no point is skipped, which is the
    /// property that makes the guarantee hold. Columns are half-open ranges
    /// <c>[⌊cN/W⌋, ⌊(c+1)N/W⌋)</c>, so the partition is exact for any N and W.
    /// </para>
    /// </remarks>
    public static class TraceDecimator
    {
        /// <summary>
        /// Reduces <paramref name="values"/> to one minimum and one maximum per column.
        /// </summary>
        /// <param name="values">Source trace values. <see cref="float.NaN"/> marks a blanked point and is excluded from the extrema.</param>
        /// <param name="columns">Number of pixel columns; must be positive.</param>
        /// <param name="minMax">
        /// Receives <c>columns × 2</c> values as (minimum, maximum) pairs. A column with no
        /// contributing sample receives <see cref="float.NaN"/> in both slots.
        /// </param>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="columns"/> is not positive.</exception>
        /// <exception cref="ArgumentException"><paramref name="minMax"/> is not exactly <c>columns × 2</c> long.</exception>
        public static void Decimate(ReadOnlySpan<float> values, int columns, Span<float> minMax)
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

            int count = values.Length;

            for (int column = 0; column < columns; column++)
            {
                // Computed with long arithmetic: at 2^20 points a 4 096-pixel width overflows
                // int at the multiply, and the result would silently be a negative index.
                int start = (int)((long)column * count / columns);
                int end = (int)(((long)column + 1) * count / columns);

                float minimum = float.PositiveInfinity;
                float maximum = float.NegativeInfinity;

                for (int i = start; i < end; i++)
                {
                    float value = values[i];

                    // NaN fails both comparisons, so a blanked point is excluded without a
                    // separate test — and a column of nothing but blanks stays empty.
                    if (value < minimum)
                    {
                        minimum = value;
                    }

                    if (value > maximum)
                    {
                        maximum = value;
                    }
                }

                bool empty = float.IsInfinity(minimum);
                minMax[column * 2] = empty ? float.NaN : minimum;
                minMax[column * 2 + 1] = empty ? float.NaN : maximum;
            }
        }

        /// <summary>
        /// Whether a trace of <paramref name="pointCount"/> points needs decimating at
        /// <paramref name="columns"/> pixels wide.
        /// </summary>
        /// <param name="pointCount">Points in the trace.</param>
        /// <param name="columns">Available horizontal pixels.</param>
        public static bool IsRequired(int pointCount, int columns) => pointCount > columns;
    }
}
