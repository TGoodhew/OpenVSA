using System;
using System.Collections.Generic;

namespace OpenVSA.Dsp.Spectrum
{
    /// <summary>
    /// Which formats are valid for a trace, given how it was averaged (<c>REQ-TRC-002</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Validity depends on the averaging type, not only on the data source.</strong> An
    /// RMS-averaged spectrum has no phase — squaring discarded it — so wrapped phase, unwrapped
    /// phase, group delay and IQ are meaningless for it, while being perfectly valid for the same
    /// data source under coherent averaging or none at all.
    /// </para>
    /// <para>
    /// The requirement asks for invalid combinations to be <em>unselectable</em> rather than
    /// erroring after the fact, which is why this answers a question rather than throwing: a UI
    /// asks what is valid and offers only that.
    /// </para>
    /// </remarks>
    public static class TraceValidity
    {
        /// <summary>Formats that need phase, and so are invalid for a power-averaged trace.</summary>
        private static readonly TraceFormat[] NeedPhase =
        {
            TraceFormat.WrappedPhase,
            TraceFormat.UnwrappedPhase,
            TraceFormat.GroupDelay,
            TraceFormat.IQ,
        };

        /// <summary>Whether a format needs phase to mean anything.</summary>
        /// <param name="format">The format.</param>
        public static bool RequiresPhase(TraceFormat format) =>
            Array.IndexOf(NeedPhase, format) >= 0;

        /// <summary>Whether an averaging type leaves phase intact.</summary>
        /// <param name="averaging">The averaging type.</param>
        public static bool PreservesPhase(AveragingType averaging) =>
            averaging == AveragingType.Off ||
            averaging == AveragingType.Time ||
            averaging == AveragingType.TimeExponential;

        /// <summary>Whether a format may be selected for a trace averaged this way.</summary>
        /// <param name="format">The format.</param>
        /// <param name="averaging">The averaging type.</param>
        public static bool IsValid(TraceFormat format, AveragingType averaging) =>
            !RequiresPhase(format) || PreservesPhase(averaging);

        /// <summary>The formats selectable for a trace averaged this way.</summary>
        /// <param name="averaging">The averaging type.</param>
        public static IReadOnlyList<TraceFormat> ValidFormats(AveragingType averaging)
        {
            var valid = new List<TraceFormat>();

            foreach (TraceFormat format in Enum.GetValues(typeof(TraceFormat)))
            {
                if (IsValid(format, averaging))
                {
                    valid.Add(format);
                }
            }

            return valid;
        }

        /// <summary>
        /// Why a format is unavailable, for the explanatory tooltip the requirement asks for.
        /// </summary>
        /// <param name="format">The format.</param>
        /// <param name="averaging">The averaging type.</param>
        /// <returns>The reason, or an empty string when the format is valid.</returns>
        public static string Explain(TraceFormat format, AveragingType averaging)
        {
            if (IsValid(format, averaging))
            {
                return string.Empty;
            }

            return format + " needs phase, and " + averaging +
                " averages power — which discards it. Use Time averaging, or none, to see phase.";
        }
    }
}
