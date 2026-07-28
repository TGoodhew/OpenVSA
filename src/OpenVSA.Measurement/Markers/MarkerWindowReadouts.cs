using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;

namespace OpenVSA.Measurement.Markers
{
    /// <summary>
    /// How a two-dimensional IQ reading is spelled in the Markers window (<c>REQ-UI-032</c>).
    /// </summary>
    public enum IqReadoutPair
    {
        /// <summary>Magnitude and phase — the default the requirement names.</summary>
        MagnitudeAndPhase = 0,

        /// <summary>Real and imaginary parts.</summary>
        RealAndImaginary,
    }

    /// <summary>
    /// The Markers window's readout labels, fields and value formatting (<c>REQ-UI-032</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The spellings are the requirement.</strong> Its criterion is that "every listed
    /// readout label and field appears with exactly the spelling given", so they are constants
    /// asserted as literals: <c>ACP Ref</c> with the space, <c>Mkr NΔTR</c> with the delta, and
    /// <c>Sym</c> rather than "Symbol". Writing them out at the point of use is how one of them
    /// quietly becomes "ACP Reference".
    /// </para>
    /// <para>
    /// <strong><c>NAN</c> and <c>INF</c> are literals too, and that is the point.</strong> A
    /// framework's own <c>double.NaN.ToString()</c> gives "NaN" and its infinity gives "∞" — both
    /// are wrong here, and a blank is worse than either because it reads as "no marker" rather than
    /// "this marker has no answer". <see cref="Value"/> is the only place a number becomes text in
    /// this window.
    /// </para>
    /// <para>
    /// Here rather than in the UI so that the harness and the unit suite can hold the spellings to
    /// the requirement without a window, which is the split the display group settled on.
    /// </para>
    /// </remarks>
    public static class MarkerWindowReadouts
    {
        /// <summary>What an invalid value renders as (<c>REQ-UI-032</c>).</summary>
        public const string NotANumber = "NAN";

        /// <summary>What an overflowing value renders as (<c>REQ-UI-032</c>).</summary>
        public const string Overflow = "INF";

        /// <summary>A marker's X, Y and Z values; <c>N</c> is the marker number.</summary>
        public const string MarkerLabel = "Mkr N";

        /// <summary>A marker's difference from the trace reference.</summary>
        public const string MarkerDeltaTraceLabel = "Mkr NΔTR";

        /// <summary>A marker's frequency-counter reading.</summary>
        public const string FrequencyCounterLabel = "Freq N";

        /// <summary>Occupied bandwidth.</summary>
        public const string OccupiedBandwidthLabel = "OBW";

        /// <summary>x-dB bandwidth.</summary>
        public const string BandwidthLabel = "BW";

        /// <summary>The adjacent-channel power reference.</summary>
        public const string AcpReferenceLabel = "ACP Ref";

        /// <summary>Band power.</summary>
        public const string PowerLabel = "Power";

        /// <summary>Band-power density.</summary>
        public const string DensityLabel = "Density";

        /// <summary>The limit-test reading.</summary>
        public const string LimitLabel = "Limit";

        /// <summary>The carrier field.</summary>
        public const string CarrierField = "Carrier";

        /// <summary>The channel-type field.</summary>
        public const string ChannelTypeField = "Channel Type";

        /// <summary>The layer field.</summary>
        public const string LayerField = "Layer";

        /// <summary>The symbol field; terse, as the requirement writes it.</summary>
        public const string SymbolField = "Sym";

        private static readonly ReadOnlyCollection<string> AllLabels =
            new ReadOnlyCollection<string>(new List<string>
            {
                MarkerLabel,
                MarkerDeltaTraceLabel,
                FrequencyCounterLabel,
                OccupiedBandwidthLabel,
                BandwidthLabel,
                AcpReferenceLabel,
                PowerLabel,
                DensityLabel,
                LimitLabel,
            });

        private static readonly ReadOnlyCollection<string> AllFields =
            new ReadOnlyCollection<string>(new List<string>
            {
                CarrierField,
                ChannelTypeField,
                LayerField,
                SymbolField,
            });

        /// <summary>The readout labels, in the order the requirement lists them.</summary>
        public static IReadOnlyList<string> Labels => AllLabels;

        /// <summary>The fields, in the order the requirement lists them.</summary>
        public static IReadOnlyList<string> Fields => AllFields;

        /// <summary>
        /// A label with its marker number substituted.
        /// </summary>
        /// <param name="label">One of <see cref="Labels"/>.</param>
        /// <param name="number">The marker number.</param>
        /// <remarks>
        /// The <c>N</c> in <c>Mkr N</c> and <c>Freq N</c> is the marker number, and in
        /// <c>Mkr NΔTR</c> it is the same number before the delta. Substituted here so the spelling
        /// stays in one place — the alternative is a format string per site, and the delta one is
        /// where a reader would get it wrong.
        /// </remarks>
        public static string Numbered(string label, int number)
        {
            if (label == null)
            {
                throw new ArgumentNullException(nameof(label));
            }

            return label.Replace("N", number.ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>
        /// A value as the Markers window spells it (<c>REQ-UI-032</c>).
        /// </summary>
        /// <param name="value">The value.</param>
        /// <param name="unit">The unit, or an empty string.</param>
        /// <param name="decimals">Digits after the point.</param>
        /// <remarks>
        /// <para>
        /// Invalid gives the literal <c>NAN</c> and overflow the literal <c>INF</c>, with the sign
        /// kept for a negative infinity — <c>-INF</c> is a different statement from <c>INF</c> and
        /// a reader of a level cares which.
        /// </para>
        /// <para>
        /// The unit is dropped from both. "NAN dBm" claims a unit for a number that does not exist;
        /// the point of <c>NAN</c> is that there is nothing to put a unit on.
        /// </para>
        /// </remarks>
        public static string Value(double value, string unit = "", int decimals = 2)
        {
            if (double.IsNaN(value))
            {
                return NotANumber;
            }

            if (double.IsPositiveInfinity(value))
            {
                return Overflow;
            }

            if (double.IsNegativeInfinity(value))
            {
                return "-" + Overflow;
            }

            string text = value.ToString(
                "F" + decimals.ToString(CultureInfo.InvariantCulture), CultureInfo.CurrentCulture);

            return string.IsNullOrEmpty(unit) ? text : text + " " + unit;
        }

        /// <summary>
        /// The two components of a two-dimensional IQ reading (<c>REQ-UI-032</c>).
        /// </summary>
        /// <param name="pair">Which pair to show.</param>
        /// <param name="real">The real part.</param>
        /// <param name="imaginary">The imaginary part.</param>
        /// <returns>The two values, already spelled.</returns>
        /// <remarks>
        /// Both pairs are offered and Mag &amp; Phase is the default, which the requirement states
        /// and <see cref="DefaultIqPair"/> holds in one place. A magnitude of zero has no defined
        /// phase, and this reports it as <c>NAN</c> rather than as zero degrees — zero is a
        /// direction and there is not one.
        /// </remarks>
        public static string[] IqComponents(IqReadoutPair pair, double real, double imaginary)
        {
            if (pair == IqReadoutPair.RealAndImaginary)
            {
                return new[] { Value(real, "V", 6), Value(imaginary, "V", 6) };
            }

            double magnitude = Math.Sqrt(real * real + imaginary * imaginary);

            double phase = magnitude < 1e-15
                ? double.NaN
                : Math.Atan2(imaginary, real) * 180.0 / Math.PI;

            return new[] { Value(magnitude, "V", 6), Value(phase, "deg", 3) };
        }

        /// <summary>
        /// The readout pair a two-dimensional IQ format starts in (<c>REQ-UI-032</c>).
        /// </summary>
        public static IqReadoutPair DefaultIqPair => IqReadoutPair.MagnitudeAndPhase;

        /// <summary>The name a chooser shows for a readout pair.</summary>
        /// <param name="pair">The pair.</param>
        /// <exception cref="ArgumentOutOfRangeException">Not a known pair.</exception>
        public static string NameOf(IqReadoutPair pair)
        {
            switch (pair)
            {
                case IqReadoutPair.MagnitudeAndPhase: return "Mag & Phase";
                case IqReadoutPair.RealAndImaginary: return "Real & Imag";

                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(pair), pair, "Not a known IQ readout pair.");
            }
        }

        /// <summary>
        /// A row of the window: a label padded to a column, then its values.
        /// </summary>
        /// <param name="label">The label, already numbered.</param>
        /// <param name="values">The values, already spelled.</param>
        /// <exception cref="ArgumentNullException">An argument is null.</exception>
        /// <remarks>
        /// <strong>The columns are why <c>REQ-UI-033</c> asks for a fixed-width face.</strong> The
        /// label is padded to a fixed width and each value to another, so rows of differing digit
        /// content line up — which is the property the fixed-width face exists to provide and what
        /// that requirement's second criterion measures.
        /// </remarks>
        public static string Row(string label, params string[] values)
        {
            if (label == null)
            {
                throw new ArgumentNullException(nameof(label));
            }

            if (values == null)
            {
                throw new ArgumentNullException(nameof(values));
            }

            var row = new System.Text.StringBuilder(label.PadRight(LabelColumnWidth));

            foreach (string value in values)
            {
                row.Append((value ?? string.Empty).PadLeft(ValueColumnWidth));
            }

            return row.ToString().TrimEnd();
        }

        /// <summary>How wide the label column is, in characters.</summary>
        /// <remarks>
        /// The longest label — <c>Channel Type</c> at twelve — plus two, so no row's label runs into
        /// its first value.
        /// </remarks>
        public const int LabelColumnWidth = 14;

        /// <summary>How wide each value column is, in characters.</summary>
        public const int ValueColumnWidth = 16;
    }
}
