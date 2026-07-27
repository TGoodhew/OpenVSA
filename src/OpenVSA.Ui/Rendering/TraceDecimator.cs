using System;
using OpenVSA.Dsp.Spectrum;

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
        public static void Decimate(ReadOnlySpan<float> values, int columns, Span<float> minMax) =>
            Decimate(values, columns, minMax, TraceDetector.Normal, valuesAreDecibels: true);

        /// <summary>
        /// Reduces <paramref name="values"/> to one column each, by a chosen detector
        /// (<c>REQ-UI-072</c>).
        /// </summary>
        /// <param name="values">Source trace values; <see cref="float.NaN"/> marks a blanked point.</param>
        /// <param name="columns">Number of pixel columns; must be positive.</param>
        /// <param name="minMax">Receives <c>columns × 2</c> values as (minimum, maximum) pairs.</param>
        /// <param name="detector">How the points of a column are reduced.</param>
        /// <param name="valuesAreDecibels">Whether the values are logarithmic, for the average.</param>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="columns"/> is not positive.</exception>
        /// <exception cref="ArgumentException"><paramref name="minMax"/> is not exactly <c>columns × 2</c> long.</exception>
        /// <remarks>
        /// The column partition is the detector's business only in that every detector uses the
        /// same one. Which points belong to a column is a property of the decimation and must not
        /// change with the detector, or a peak and an average would be reading different data.
        /// </remarks>
        public static void Decimate(
            ReadOnlySpan<float> values,
            int columns,
            Span<float> minMax,
            TraceDetector detector,
            bool valuesAreDecibels)
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

                float minimum;
                float maximum;

                TraceDetection.Detect(
                    values, start, end, detector, valuesAreDecibels, out minimum, out maximum);

                minMax[column * 2] = minimum;
                minMax[column * 2 + 1] = maximum;
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
