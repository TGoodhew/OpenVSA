using System;
using OpenVSA.Dsp.Spectrum;

namespace OpenVSA.Ui.Rendering
{
    /// <summary>
    /// The vertical axis a trace format needs: its range, its unit, and its per-division step.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>A format is not only a different curve, it is a different quantity.</strong> Log
    /// magnitude is decibels referred to a milliwatt; linear magnitude, real and imaginary are
    /// volts; phase is degrees; group delay is seconds. Drawing any of the last four on the
    /// decibel axis a spectrum uses puts the whole trace in the bottom pixel row — which looks like
    /// a measurement with no signal rather than like an axis with the wrong units, and is the
    /// reason this exists rather than the plot simply keeping its dBm scale.
    /// </para>
    /// <para>
    /// <strong>Only log magnitude has an axis the user set.</strong> Its top is the reference level
    /// and its step is the dB per division of <c>REQ-UI-013</c>; the rest are auto-ranged from the
    /// data, because there is no reference level in volts and no sensible fixed range for a group
    /// delay. Wrapped phase is the exception: it is bounded by definition, so it gets ±180° and
    /// stays still while the trace moves inside it.
    /// </para>
    /// <para>
    /// <strong>Auto-ranging steps in 1, 2 or 5.</strong> A range chosen as exactly the data's own
    /// extremes gives graticule lines at arbitrary values and a trace that touches the top and
    /// bottom of the grid on every frame; rounding out to a decimal step gives readable division
    /// labels and a little headroom, which is what an analyser does.
    /// </para>
    /// </remarks>
    public sealed class TraceAxis
    {
        private TraceAxis(double topValue, double bottomValue, double perDivision, string unit)
        {
            TopValue = topValue;
            BottomValue = bottomValue;
            PerDivision = perDivision;
            Unit = unit;
        }

        /// <summary>Value at the top of the graticule.</summary>
        public double TopValue { get; }

        /// <summary>Value at the bottom of the graticule.</summary>
        public double BottomValue { get; }

        /// <summary>Value per graticule division.</summary>
        public double PerDivision { get; }

        /// <summary>The unit the values are in: <c>dBm</c>, <c>V</c>, <c>deg</c> or <c>s</c>.</summary>
        public string Unit { get; }

        /// <summary>Whether this axis is in decibels, and so whether a power average applies.</summary>
        public bool IsDecibels => Unit == "dBm";

        /// <summary>The unit a format's values are in.</summary>
        /// <param name="format">The format.</param>
        /// <exception cref="ArgumentOutOfRangeException">Not a known format.</exception>
        public static string UnitFor(TraceFormat format)
        {
            switch (format)
            {
                case TraceFormat.LogMagnitude: return "dBm";

                case TraceFormat.LinearMagnitude:
                case TraceFormat.Real:
                case TraceFormat.Imaginary:
                case TraceFormat.IQ:
                    return "V";

                case TraceFormat.WrappedPhase:
                case TraceFormat.UnwrappedPhase:
                    return "deg";

                case TraceFormat.GroupDelay:
                    return "s";
            }

            throw new ArgumentOutOfRangeException(
                nameof(format), format, "There is no such trace format.");
        }

        /// <summary>
        /// Whether a format draws as a line against frequency at all.
        /// </summary>
        /// <param name="format">The format.</param>
        /// <remarks>
        /// <see cref="TraceFormat.IQ"/> is two values per point and is a constellation, not a
        /// curve. Drawing its interleaved pairs as a trace would produce a picture that means
        /// nothing, so the plot draws none — the constellation surface is <c>REQ-UI-050</c>.
        /// </remarks>
        public static bool IsLineTrace(TraceFormat format) => format != TraceFormat.IQ;

        /// <summary>
        /// The axis for a format, given the values it produced.
        /// </summary>
        /// <param name="format">The format.</param>
        /// <param name="values">The formatted trace, for the auto-ranged formats.</param>
        /// <param name="referenceDbm">The reference level, for log magnitude.</param>
        /// <param name="decibelsPerDivision">The dB per division, for log magnitude.</param>
        /// <param name="divisions">Vertical graticule divisions; must be positive.</param>
        /// <param name="yReferencePercent">Where the reference line sits (<c>REQ-UI-013</c>).</param>
        /// <exception cref="ArgumentOutOfRangeException">A division count or reference position is out of range.</exception>
        public static TraceAxis For(
            TraceFormat format,
            ReadOnlySpan<float> values,
            double referenceDbm,
            double decibelsPerDivision,
            int divisions,
            int yReferencePercent)
        {
            if (divisions <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(divisions), divisions, "Divisions must be positive.");
            }

            string unit = UnitFor(format);

            if (format == TraceFormat.LogMagnitude)
            {
                double fullScale = decibelsPerDivision * divisions;
                double top = ReferencePosition.TopFor(referenceDbm, fullScale, yReferencePercent);

                return new TraceAxis(top, top - fullScale, decibelsPerDivision, unit);
            }

            if (format == TraceFormat.WrappedPhase)
            {
                // Bounded by definition, so it is fixed: an auto-ranged wrapped phase would breathe
                // between ±180 and ±3 as the signal came and went, and the grid would stop meaning
                // anything from one frame to the next.
                return new TraceAxis(180.0, -180.0, 360.0 / divisions, unit);
            }

            return AutoRange(values, divisions, unit);
        }

        /// <summary>
        /// An axis rounded out from the data's own extremes.
        /// </summary>
        /// <param name="values">The formatted trace.</param>
        /// <param name="divisions">Vertical graticule divisions.</param>
        /// <param name="unit">The unit.</param>
        private static TraceAxis AutoRange(ReadOnlySpan<float> values, int divisions, string unit)
        {
            double smallest = double.PositiveInfinity;
            double largest = double.NegativeInfinity;

            for (int i = 0; i < values.Length; i++)
            {
                float value = values[i];

                if (float.IsNaN(value) || float.IsInfinity(value))
                {
                    continue;
                }

                if (value < smallest)
                {
                    smallest = value;
                }

                if (value > largest)
                {
                    largest = value;
                }
            }

            if (double.IsInfinity(smallest))
            {
                // Nothing to range against: an empty trace, or one that is all blanks. A symmetric
                // unit range keeps zero in the middle, which is where a signed quantity belongs.
                return new TraceAxis(1.0, -1.0, 2.0 / divisions, unit);
            }

            if (largest - smallest < double.Epsilon)
            {
                // A constant trace - a real part of exactly zero, say. Give it a division either
                // side so the line is visible rather than lying on the graticule's edge.
                double flat = NiceStep(Math.Max(Math.Abs(largest), 1e-12));

                return new TraceAxis(
                    largest + flat, largest - flat, 2.0 * flat / divisions, unit);
            }

            double step = NiceStep((largest - smallest) / divisions);
            double bottom = Math.Floor(smallest / step) * step;
            double top = bottom + step * divisions;

            // One more division if the rounding put the top below the data. Cheaper and clearer
            // than solving for the step that fits exactly, and it never clips the trace.
            while (top < largest)
            {
                step = NiceStep(step * 1.5);
                bottom = Math.Floor(smallest / step) * step;
                top = bottom + step * divisions;
            }

            return new TraceAxis(top, bottom, step, unit);
        }

        /// <summary>
        /// The next step of the form 1, 2 or 5 times a power of ten, at or above a value.
        /// </summary>
        /// <param name="wanted">The step the data would like.</param>
        /// <remarks>
        /// The 1-2-5 series is what makes division labels readable: a step of 0.037 volts gives
        /// graticule lines nobody can read off, and a step of 0.05 gives ten that anyone can.
        /// </remarks>
        public static double NiceStep(double wanted)
        {
            if (double.IsNaN(wanted) || double.IsInfinity(wanted) || wanted <= 0.0)
            {
                return 1.0;
            }

            double decade = Math.Pow(10.0, Math.Floor(Math.Log10(wanted)));
            double normalised = wanted / decade;

            if (normalised <= 1.0)
            {
                return decade;
            }

            if (normalised <= 2.0)
            {
                return 2.0 * decade;
            }

            return normalised <= 5.0 ? 5.0 * decade : 10.0 * decade;
        }

        /// <summary>Formats a value on this axis, with its unit.</summary>
        /// <param name="value">The value.</param>
        public string Format(double value) =>
            IsDecibels
                ? EngineeringText.Readout(value, "0.00") + " dBm"
                : EngineeringText.Quantity(value, Unit);

        /// <summary>Parses a value typed against this axis.</summary>
        /// <param name="text">The text.</param>
        /// <param name="value">The value.</param>
        /// <returns>Whether the text was understood.</returns>
        public bool TryParse(string text, out double value) =>
            IsDecibels
                ? EngineeringText.TryParseDecibels(text, out value)
                : EngineeringText.TryParse(text, Unit, out value);

        /// <inheritdoc />
        public override string ToString() =>
            Format(TopValue) + " to " + Format(BottomValue) + ", " +
            EngineeringText.Quantity(PerDivision, Unit) + "/div";
    }
}
