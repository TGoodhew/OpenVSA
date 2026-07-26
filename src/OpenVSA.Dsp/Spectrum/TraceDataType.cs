using System;
using System.Collections.Generic;
using System.Linq;

namespace OpenVSA.Dsp.Spectrum
{
    /// <summary>
    /// The base trace data types of <c>REQ-DSP-040</c> — everything available without
    /// demodulation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Data is not format.</strong> <c>REQ-TRC-001</c> makes these orthogonal: a data type
    /// says <em>what was measured</em> and a <see cref="TraceFormat"/> says <em>how it is drawn</em>.
    /// Spectrum in log magnitude and Spectrum in phase are one data type in two formats;
    /// Spectrum and PSD are two data types that may share a format.
    /// </para>
    /// <para>
    /// <strong>These are available unconditionally.</strong> The reference product gated them
    /// behind an option SKU; OpenVSA has no such gate (<c>REQ-LIC-010</c>), and what replaces the
    /// licence check is an assembly-dependency test asserting the DSP layer does not reference the
    /// demodulation one — which is stricter, because it fails the build rather than showing up as
    /// an unexpectedly licensed feature.
    /// </para>
    /// </remarks>
    public enum TraceDataType
    {
        /// <summary>The spectrum of the analysed record.</summary>
        Spectrum = 0,

        /// <summary>The time record as acquired, before any windowing.</summary>
        RawMainTime,

        /// <summary>The time record with the analysis window applied.</summary>
        InstantaneousMainTime,

        /// <summary>Power spectral density (<c>PowerSpectralDensity</c>).</summary>
        PowerSpectralDensity,

        /// <summary>Autocorrelation of the record (<c>Autocorrelation</c>).</summary>
        Autocorrelation,

        /// <summary>Complementary cumulative distribution of power (<c>REQ-DSP-042</c>).</summary>
        Ccdf,

        /// <summary>Cumulative distribution of power.</summary>
        Cdf,

        /// <summary>Probability density of power.</summary>
        Pdf,

        /// <summary>A correction trace: the response being applied, not a measurement.</summary>
        Correction,

        /// <summary>The result of a trace-math expression (<c>REQ-DSP-046</c>).</summary>
        Math,

        /// <summary>Nothing is assigned to this trace.</summary>
        NoData,
    }

    /// <summary>
    /// Names the base trace data types and says which formats each can be drawn in.
    /// </summary>
    /// <remarks>
    /// Kept beside the enumeration rather than in the UI, because the pairing is a property of the
    /// data and not of the display. A CCDF has no phase to show whatever surface it is drawn on,
    /// and a UI that had to know that would be a UI that could get it wrong.
    /// </remarks>
    public static class TraceDataTypes
    {
        /// <summary>Every base type, in the order a selector offers them.</summary>
        public static IReadOnlyList<TraceDataType> All { get; } =
            (TraceDataType[])Enum.GetValues(typeof(TraceDataType));

        /// <summary>The display name of a data type.</summary>
        /// <param name="type">The data type.</param>
        /// <exception cref="ArgumentOutOfRangeException">Not a known data type.</exception>
        /// <remarks>
        /// The reference product's own names, which are what a user of it will look for — "Raw
        /// Main Time", not "RawMainTime", and "PSD", not the expansion.
        /// </remarks>
        public static string Describe(TraceDataType type)
        {
            switch (type)
            {
                case TraceDataType.Spectrum: return "Spectrum";
                case TraceDataType.RawMainTime: return "Raw Main Time";
                case TraceDataType.InstantaneousMainTime: return "Instantaneous Main Time";
                case TraceDataType.PowerSpectralDensity: return "PSD";
                case TraceDataType.Autocorrelation: return "Autocorrelation";
                case TraceDataType.Ccdf: return "CCDF";
                case TraceDataType.Cdf: return "CDF";
                case TraceDataType.Pdf: return "PDF";
                case TraceDataType.Correction: return "Correction";
                case TraceDataType.Math: return "Math";
                case TraceDataType.NoData: return "No Data";

                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(type), type, "Not a known trace data type.");
            }
        }

        /// <summary>
        /// Whether a data type carries phase, and so can be drawn in the phase formats.
        /// </summary>
        /// <param name="type">The data type.</param>
        /// <remarks>
        /// The statistical types do not: a CCDF is a distribution of powers, and squaring discarded
        /// the phase before the distribution was formed. Neither does an autocorrelation computed
        /// from the power spectrum, nor a power spectral density. <c>REQ-TRC-002</c> uses this to
        /// make those formats unselectable rather than showing a phase of zero as though it had
        /// been measured.
        /// </remarks>
        public static bool CarriesPhase(TraceDataType type)
        {
            switch (type)
            {
                case TraceDataType.Spectrum:
                case TraceDataType.RawMainTime:
                case TraceDataType.InstantaneousMainTime:
                case TraceDataType.Correction:
                case TraceDataType.Math:
                    return true;

                default:
                    return false;
            }
        }

        /// <summary>
        /// The formats a data type can be drawn in.
        /// </summary>
        /// <param name="type">The data type.</param>
        /// <exception cref="ArgumentOutOfRangeException">Not a known data type.</exception>
        public static IReadOnlyList<TraceFormat> FormatsFor(TraceDataType type)
        {
            // Called for its argument check: an unknown type must fail here rather than return an
            // empty list, which would present as a data type nobody could pick a format for.
            Describe(type);

            if (type == TraceDataType.NoData)
            {
                return new TraceFormat[0];
            }

            var formats = (TraceFormat[])Enum.GetValues(typeof(TraceFormat));

            // The same rule REQ-TRC-002 applies after power averaging, applied here to the data
            // instead: what has no phase cannot be drawn in a format that needs one, and the
            // reason for the refusal is stated in one place rather than two.
            return CarriesPhase(type)
                ? formats.ToList()
                : formats.Where(f => !TraceValidity.RequiresPhase(f)).ToList();
        }

        /// <summary>Whether a data type is a measurement rather than a placeholder or an input.</summary>
        /// <param name="type">The data type.</param>
        /// <remarks>
        /// Correction is a response being applied and Math is the result of an expression; neither
        /// comes from an acquisition, and No Data comes from nothing at all. A caller wiring data
        /// types to the acquisition path needs to know which three to leave alone.
        /// </remarks>
        public static bool IsMeasured(TraceDataType type) =>
            type != TraceDataType.Correction &&
            type != TraceDataType.Math &&
            type != TraceDataType.NoData;
    }
}
