using System;
using System.Globalization;
using OpenVSA.Dsp.Spectrum;

namespace OpenVSA.Measurement.Markers
{
    /// <summary>The marker types of <c>REQ-MKR-001</c>.</summary>
    public enum MarkerType
    {
        /// <summary>Reads the trace at its own X position. Diamond glyph.</summary>
        Normal = 0,

        /// <summary>Reads the difference from another marker. Diamond glyph, <c>3Δ1</c> label.</summary>
        Delta,

        /// <summary>Position-locked: reads what it read when it was placed. "X" glyph.</summary>
        Fixed,
    }

    /// <summary>What a marker reads against a particular frame.</summary>
    public readonly struct MarkerReading
    {
        /// <summary>An invalid reading: the marker is not over the trace.</summary>
        public static readonly MarkerReading Invalid =
            new MarkerReading(double.NaN, double.NaN, -1, false);

        /// <summary>Creates a reading.</summary>
        /// <param name="xHz">X value, in hertz — or an X difference, for a delta marker.</param>
        /// <param name="yDbm">Y value, in dBm — or a Y difference, for a delta marker.</param>
        /// <param name="index">Point index the reading came from, or −1.</param>
        /// <param name="isValid">Whether the reading is meaningful.</param>
        public MarkerReading(double xHz, double yDbm, int index, bool isValid)
        {
            XHz = xHz;
            YDbm = yDbm;
            Index = index;
            IsValid = isValid;
        }

        /// <summary>X value in hertz, or an X difference for a delta marker.</summary>
        public double XHz { get; }

        /// <summary>Y value in dBm, or a Y difference in dB for a delta marker.</summary>
        public double YDbm { get; }

        /// <summary>Point index the reading came from, or −1 when there is none.</summary>
        public int Index { get; }

        /// <summary>Whether the reading is meaningful.</summary>
        public bool IsValid { get; }
    }

    /// <summary>
    /// A marker on a trace (<c>REQ-MKR-001</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The three types differ in what they are anchored to, which is the whole of the distinction
    /// the requirement draws:
    /// </para>
    /// <list type="bullet">
    /// <item><description><see cref="MarkerType.Normal"/> is anchored to an X value. Its Y is read
    /// from whichever frame is current, so the readout tracks the data as the trace updates.</description></item>
    /// <item><description><see cref="MarkerType.Fixed"/> is anchored to an X <em>and</em> a Y,
    /// captured when it was placed. Neither moves when the trace updates — which is what makes it
    /// useful as a reference to measure against.</description></item>
    /// <item><description><see cref="MarkerType.Delta"/> is anchored like a Normal marker but reads
    /// the difference from its <see cref="Reference"/>.</description></item>
    /// </list>
    /// <para>
    /// <strong>Anchored to a frequency, not to a bin index.</strong> A marker at 1.001 GHz must
    /// still be at 1.001 GHz after the point count changes, and an index would put it somewhere
    /// else entirely. Reading resolves the nearest bin each time instead.
    /// </para>
    /// </remarks>
    public sealed class Marker
    {
        internal Marker(int number, MarkerType type, double xHz, char traceLetter)
        {
            Number = number;
            Type = type;
            XHz = xHz;
            TraceLetter = traceLetter;
        }

        /// <summary>Marker number, 1 upwards, unique within its trace.</summary>
        public int Number { get; }

        /// <summary>Which kind of marker this is.</summary>
        public MarkerType Type { get; internal set; }

        /// <summary>The letter of the trace this marker is on (<c>REQ-UI-020</c>).</summary>
        public char TraceLetter { get; }

        /// <summary>X position, in hertz.</summary>
        public double XHz { get; internal set; }

        /// <summary>
        /// Y value in dBm for a <see cref="MarkerType.Fixed"/> marker; <see cref="double.NaN"/> otherwise.
        /// </summary>
        public double FixedYDbm { get; internal set; } = double.NaN;

        /// <summary>The marker this one is measured from, for a <see cref="MarkerType.Delta"/> marker.</summary>
        public Marker Reference { get; internal set; }

        /// <summary>Whether this is the selected marker; drawn filled (<c>REQ-UI-030</c>).</summary>
        public bool IsSelected { get; internal set; }

        /// <summary>
        /// The marker's label (<c>REQ-UI-031</c>).
        /// </summary>
        /// <remarks>
        /// <c>XΔTR</c> for a delta marker, where the reference trace letter appears
        /// <strong>only when the reference is on a different trace</strong>: <c>3Δ1</c> same-trace,
        /// <c>2ΔB1</c> cross-trace. Emitting the letter unconditionally is the obvious
        /// implementation and is wrong for the same-trace case, which is why the requirement
        /// asserts both as exact strings.
        /// </remarks>
        public string Label
        {
            get
            {
                if (Type != MarkerType.Delta || Reference == null)
                {
                    return Number.ToString(CultureInfo.CurrentCulture);
                }

                string trace = Reference.TraceLetter == TraceLetter
                    ? string.Empty
                    : Reference.TraceLetter.ToString();

                return Number.ToString(CultureInfo.CurrentCulture) + "Δ" + trace +
                    Reference.Number.ToString(CultureInfo.CurrentCulture);
            }
        }

        /// <summary>The label as the Markers Window writes it: <c>Mkr N</c> or <c>Mkr NΔTR</c>.</summary>
        public string WindowLabel => "Mkr " + Label;

        /// <summary>
        /// Reads this marker against a frame.
        /// </summary>
        /// <param name="frame">The current spectrum, or <c>null</c>.</param>
        /// <returns>The reading, or <see cref="MarkerReading.Invalid"/> if there is nothing to read.</returns>
        public MarkerReading Read(SpectrumFrame frame)
        {
            if (Type == MarkerType.Fixed)
            {
                // Position-locked: the frame is not consulted at all, which is what makes the
                // reading stable across acquisitions.
                return new MarkerReading(XHz, FixedYDbm, IndexIn(frame), !double.IsNaN(FixedYDbm));
            }

            if (frame == null)
            {
                return MarkerReading.Invalid;
            }

            int index = IndexIn(frame);

            if (index < 0)
            {
                return MarkerReading.Invalid;
            }

            double x = frame.FrequencyAt(index);
            double y = frame.LevelsDbm[index];

            if (Type != MarkerType.Delta)
            {
                return new MarkerReading(x, y, index, true);
            }

            if (Reference == null)
            {
                return MarkerReading.Invalid;
            }

            MarkerReading reference = Reference.Read(frame);

            return reference.IsValid
                ? new MarkerReading(x - reference.XHz, y - reference.YDbm, index, true)
                : MarkerReading.Invalid;
        }

        /// <summary>
        /// The point index this marker sits on in a frame, or −1 if it is outside it.
        /// </summary>
        /// <param name="frame">The frame, or <c>null</c>.</param>
        public int IndexIn(SpectrumFrame frame)
        {
            if (frame == null || frame.PointCount == 0 || !(frame.BinWidthHz > 0.0))
            {
                return -1;
            }

            double offset = (XHz - frame.StartFrequencyHz) / frame.BinWidthHz;
            int index = (int)Math.Round(offset);

            return index < 0 || index >= frame.PointCount ? -1 : index;
        }
    }
}
