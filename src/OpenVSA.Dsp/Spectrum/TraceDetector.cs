using System;
using System.Collections.Generic;

namespace OpenVSA.Dsp.Spectrum
{
    /// <summary>
    /// How several trace points are reduced to the one pixel column they share
    /// (<c>REQ-UI-072</c>'s Detectors tab).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>A detector is a display decision, not an acquisition one.</strong> Every point is
    /// still computed and every point is still what a marker reads; the detector says only what to
    /// draw when a column covers more points than it has pixels. That is why it lives beside the
    /// formats rather than in the acquisition plan, and why changing it costs a redraw rather than
    /// a re-acquisition.
    /// </para>
    /// <para>
    /// <strong><see cref="Normal"/> is the default and is not a compromise.</strong> Keeping both
    /// extrema of a column is what makes <c>REQ-NFR-006</c>'s guarantee — that a one-bin spur
    /// cannot be decimated away — hold. The single-valued detectors each discard something on
    /// purpose, which is the reason to offer them: Peak for finding spurs, Negative Peak for
    /// finding notches, Sample for seeing the trace as the instrument sampled it, and Average for
    /// reading a noise floor without its scatter.
    /// </para>
    /// </remarks>
    public enum TraceDetector
    {
        /// <summary>Both extrema of the column, drawn as a vertical span.</summary>
        Normal = 0,

        /// <summary>The largest value in the column.</summary>
        Peak,

        /// <summary>The smallest value in the column.</summary>
        NegativePeak,

        /// <summary>The first value in the column, as the instrument sampled it.</summary>
        Sample,

        /// <summary>The mean of the column — in power, when the values are decibels.</summary>
        Average,
    }

    /// <summary>
    /// Reduces a column's worth of trace points to what the detector says should be drawn.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The average is taken in power, never in decibels.</strong> Averaging dB values is a
    /// geometric mean of powers, and it reads low by an amount that grows with the scatter — which
    /// is largest exactly where the detector is most used, on a noise floor. This is the same trap
    /// that <c>REQ-DSP-024</c>'s noise correction has, stated once in each place because getting it
    /// wrong looks plausible in both.
    /// </para>
    /// <para>
    /// <see cref="float.NaN"/> marks a blanked point and is excluded from every detector. A column
    /// of nothing but blanks stays blank rather than becoming a zero, which would draw a line
    /// across a gap the trace does not have.
    /// </para>
    /// </remarks>
    public static class TraceDetection
    {
        /// <summary>Every detector, in the order a menu should list them.</summary>
        public static IReadOnlyList<TraceDetector> All { get; } = new[]
        {
            TraceDetector.Normal,
            TraceDetector.Peak,
            TraceDetector.NegativePeak,
            TraceDetector.Sample,
            TraceDetector.Average,
        };

        /// <summary>The detector's name, as the Detectors tab writes it.</summary>
        /// <param name="detector">The detector.</param>
        /// <exception cref="ArgumentOutOfRangeException">Not a detector.</exception>
        public static string NameOf(TraceDetector detector)
        {
            switch (detector)
            {
                case TraceDetector.Normal: return "Normal";
                case TraceDetector.Peak: return "Peak";
                case TraceDetector.NegativePeak: return "Negative peak";
                case TraceDetector.Sample: return "Sample";
                case TraceDetector.Average: return "Average";
            }

            throw new ArgumentOutOfRangeException(
                nameof(detector), detector, "There is no such trace detector.");
        }

        /// <summary>What the detector does, for the tab to say beside it.</summary>
        /// <param name="detector">The detector.</param>
        /// <exception cref="ArgumentOutOfRangeException">Not a detector.</exception>
        public static string Describe(TraceDetector detector)
        {
            switch (detector)
            {
                case TraceDetector.Normal:
                    return "Both extrema of every column. A one-bin spur cannot be lost.";

                case TraceDetector.Peak:
                    return "The largest value in each column. Finds spurs; hides notches.";

                case TraceDetector.NegativePeak:
                    return "The smallest value in each column. Finds notches; hides spurs.";

                case TraceDetector.Sample:
                    return "The first value in each column, as the instrument sampled it.";

                case TraceDetector.Average:
                    return "The mean of each column, taken in power. Reads a noise floor without " +
                           "its scatter, and reads a spur low.";
            }

            throw new ArgumentOutOfRangeException(
                nameof(detector), detector, "There is no such trace detector.");
        }

        /// <summary>Whether a detector produces one value rather than a span.</summary>
        /// <param name="detector">The detector.</param>
        public static bool IsSingleValued(TraceDetector detector) => detector != TraceDetector.Normal;

        /// <summary>
        /// Reduces one column's points.
        /// </summary>
        /// <param name="values">The whole trace.</param>
        /// <param name="start">First point of the column.</param>
        /// <param name="end">One past the last point of the column.</param>
        /// <param name="detector">The detector.</param>
        /// <param name="valuesAreDecibels">Whether the values are logarithmic, for the average.</param>
        /// <param name="minimum">Receives the bottom of the drawn span.</param>
        /// <param name="maximum">Receives the top of the drawn span.</param>
        /// <exception cref="ArgumentOutOfRangeException">Not a detector.</exception>
        /// <remarks>
        /// Both outputs are written even for a single-valued detector, and they are equal. The
        /// rasteriser draws a vertical span from one to the other, so equal ends draw one pixel —
        /// which means no drawing code has to know which detector produced the column.
        /// </remarks>
        public static void Detect(
            ReadOnlySpan<float> values,
            int start,
            int end,
            TraceDetector detector,
            bool valuesAreDecibels,
            out float minimum,
            out float maximum)
        {
            switch (detector)
            {
                case TraceDetector.Normal:
                    Extrema(values, start, end, out minimum, out maximum);
                    return;

                case TraceDetector.Peak:
                    Extrema(values, start, end, out _, out maximum);
                    minimum = maximum;
                    return;

                case TraceDetector.NegativePeak:
                    Extrema(values, start, end, out minimum, out _);
                    maximum = minimum;
                    return;

                case TraceDetector.Sample:
                    minimum = maximum = start < end && start < values.Length
                        ? values[start]
                        : float.NaN;
                    return;

                case TraceDetector.Average:
                    minimum = maximum = Mean(values, start, end, valuesAreDecibels);
                    return;
            }

            throw new ArgumentOutOfRangeException(
                nameof(detector), detector, "There is no such trace detector.");
        }

        private static void Extrema(
            ReadOnlySpan<float> values, int start, int end, out float minimum, out float maximum)
        {
            float smallest = float.PositiveInfinity;
            float largest = float.NegativeInfinity;

            for (int i = start; i < end; i++)
            {
                float value = values[i];

                // NaN fails both comparisons, so a blanked point is excluded without a separate
                // test - and a column of nothing but blanks stays empty.
                if (value < smallest)
                {
                    smallest = value;
                }

                if (value > largest)
                {
                    largest = value;
                }
            }

            bool empty = float.IsInfinity(smallest);

            minimum = empty ? float.NaN : smallest;
            maximum = empty ? float.NaN : largest;
        }

        private static float Mean(
            ReadOnlySpan<float> values, int start, int end, bool valuesAreDecibels)
        {
            double total = 0.0;
            int counted = 0;

            for (int i = start; i < end; i++)
            {
                float value = values[i];

                if (float.IsNaN(value))
                {
                    continue;
                }

                // In power. Ten decibel values averaged as decibels give the geometric mean of the
                // powers they stand for, which is lower than the arithmetic one by an amount that
                // grows with the scatter.
                total += valuesAreDecibels ? Math.Pow(10.0, value / 10.0) : value;
                counted++;
            }

            if (counted == 0)
            {
                return float.NaN;
            }

            double mean = total / counted;

            return valuesAreDecibels
                ? (float)(10.0 * Math.Log10(mean))
                : (float)mean;
        }
    }
}
