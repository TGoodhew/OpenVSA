using System;
using System.Collections.Generic;
using OpenVSA.Dsp.Spectrum;

namespace OpenVSA.Ui
{
    /// <summary>
    /// Names the trace data formats of <c>REQ-DSP-041</c> as they are shown and selected.
    /// </summary>
    /// <remarks>
    /// The enum spells them <c>LogMagnitude</c> and <c>GroupDelay</c>; a display has room for a
    /// word or two and a user looking through a list expects "Log Mag". The split happens here so
    /// the enum stays the identifier it is and the display name stays in one place.
    /// </remarks>
    public static class TraceFormatText
    {
        private static readonly TraceFormat[] Order =
        {
            TraceFormat.LogMagnitude,
            TraceFormat.LinearMagnitude,
            TraceFormat.WrappedPhase,
            TraceFormat.UnwrappedPhase,
            TraceFormat.GroupDelay,
            TraceFormat.Real,
            TraceFormat.Imaginary,
            TraceFormat.IQ,
        };

        /// <summary>The formats, in the order a selector steps through them.</summary>
        public static IReadOnlyList<TraceFormat> Formats => Order;

        /// <summary>The display names, in the same order as <see cref="Formats"/>.</summary>
        public static IReadOnlyList<string> Names
        {
            get
            {
                var names = new string[Order.Length];

                for (int i = 0; i < Order.Length; i++)
                {
                    names[i] = Describe(Order[i]);
                }

                return names;
            }
        }

        /// <summary>The display name of a format.</summary>
        /// <param name="format">The format.</param>
        /// <exception cref="ArgumentOutOfRangeException">Not a known format.</exception>
        public static string Describe(TraceFormat format)
        {
            switch (format)
            {
                case TraceFormat.LogMagnitude: return "Log Mag";
                case TraceFormat.LinearMagnitude: return "Lin Mag";
                case TraceFormat.WrappedPhase: return "Phase";
                case TraceFormat.UnwrappedPhase: return "Unwrap Phase";
                case TraceFormat.GroupDelay: return "Group Delay";
                case TraceFormat.Real: return "Real";
                case TraceFormat.Imaginary: return "Imag";
                case TraceFormat.IQ: return "IQ";

                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(format), format, "Not a known trace format.");
            }
        }

        /// <summary>
        /// The format a display name refers to.
        /// </summary>
        /// <param name="name">The display name; case-insensitive.</param>
        /// <param name="format">Receives the format.</param>
        /// <returns><c>true</c> if the name was recognised.</returns>
        public static bool TryParse(string name, out TraceFormat format)
        {
            format = TraceFormat.LogMagnitude;

            if (string.IsNullOrEmpty(name))
            {
                return false;
            }

            string trimmed = name.Trim();

            foreach (TraceFormat candidate in Order)
            {
                if (string.Equals(Describe(candidate), trimmed, StringComparison.OrdinalIgnoreCase))
                {
                    format = candidate;
                    return true;
                }
            }

            return false;
        }
    }
}
