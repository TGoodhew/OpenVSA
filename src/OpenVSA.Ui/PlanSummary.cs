using System;
using System.Globalization;
using System.Text;
using OpenVSA.Hal;

namespace OpenVSA.Ui
{
    /// <summary>
    /// Renders a negotiated <see cref="AcquisitionPlan"/> for display.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Separate from the shell window so that <c>REQ-HAL-001</c>'s "the UI surfaces the coercion"
    /// clause can be asserted rather than looked at. A plan that quietly differs from the request
    /// is the failure that requirement exists to prevent, and a test that only checks the plan
    /// object would pass while the window showed nothing.
    /// </para>
    /// <para>
    /// Every figure comes from the plan. Nothing here knows what kind of front end produced it,
    /// which is <c>REQ-HAL-002</c>.
    /// </para>
    /// </remarks>
    public static class PlanSummary
    {
        /// <summary>
        /// Describes a plan and every coercion it carries.
        /// </summary>
        /// <param name="plan">The negotiated plan.</param>
        /// <returns>Display text naming each honoured value, and each coerced one with its reason.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="plan"/> is null.</exception>
        public static string Describe(AcquisitionPlan plan)
        {
            if (plan == null)
            {
                throw new ArgumentNullException(nameof(plan));
            }

            var text = new StringBuilder();
            text.AppendLine("Negotiated plan:");
            text.AppendLine();
            Append(text, "Centre frequency", Frequency(plan.CenterFrequencyHz));
            Append(text, "Span", Frequency(plan.SpanHz));
            Append(text, "Sample rate", Frequency(plan.SampleRateHz));
            Append(text, "Block size", plan.SamplesPerBlock.ToString(CultureInfo.CurrentCulture) + " samples");
            Append(text, "Reference level", plan.ReferenceLevelDbm.ToString("0.##", CultureInfo.CurrentCulture) + " dBm");
            Append(text, "Gap-free", plan.SupportsGapFreeStreaming ? "yes" : "no");

            text.AppendLine();

            if (!plan.Coerced)
            {
                text.AppendLine("Every requested value was honoured.");
                return text.ToString();
            }

            text.AppendLine(plan.Coercions.Count == 1
                ? "One request was coerced:"
                : plan.Coercions.Count + " requests were coerced:");

            foreach (ParameterCoercion coercion in plan.Coercions)
            {
                // The reason is carried through verbatim. Paraphrasing it here would put the
                // explanation of a coercion in two places, and the front end is the only one of
                // them that knows why.
                text.AppendLine(
                    "  " + coercion.Parameter + ": asked " +
                    Plain(coercion.Requested) + ", got " + Plain(coercion.Honoured) +
                    " — " + coercion.Reason);
            }

            return text.ToString();
        }

        private static void Append(StringBuilder text, string label, string value) =>
            text.AppendLine("  " + label.PadRight(22) + value);

        /// <summary>
        /// A coerced value in full, without an exponent.
        /// </summary>
        /// <remarks>
        /// A coercion's units are not known here — it may be hertz, dBm or a sample count — so
        /// there is no engineering prefix to apply, only a general format. It must not be one that
        /// produces an exponent: "asked 5E+07, got 4E+07" is a worse answer to "what did it change
        /// my span to?" than the raw digits.
        /// </remarks>
        private static string Plain(double value) =>
            value.ToString("#,##0.######", CultureInfo.CurrentCulture);

        /// <summary>Engineering-notation frequency, as the hardware pane shows it.</summary>
        internal static string Frequency(double hertz)
        {
            if (hertz >= 1e9)
            {
                return (hertz / 1e9).ToString("0.###", CultureInfo.CurrentCulture) + " GHz";
            }

            if (hertz >= 1e6)
            {
                return (hertz / 1e6).ToString("0.###", CultureInfo.CurrentCulture) + " MHz";
            }

            if (hertz >= 1e3)
            {
                return (hertz / 1e3).ToString("0.###", CultureInfo.CurrentCulture) + " kHz";
            }

            return hertz.ToString("0.###", CultureInfo.CurrentCulture) + " Hz";
        }
    }
}
