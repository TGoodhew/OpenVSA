using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using OpenVSA.Core;
using OpenVSA.Hal;
using OpenVSA.Measurement;

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
        /// Describes a plan, the setup that produced it, and every coercion either made.
        /// </summary>
        /// <param name="plan">The negotiated plan.</param>
        /// <param name="planned">The planned acquisition, or <c>null</c> if there was none.</param>
        /// <param name="capabilities">
        /// The front end's capabilities, or <c>null</c>. Supplied so the point count can be shown
        /// against the most this instrument could have given, which is the difference between
        /// "801 points" and "801 of a possible 65 601".
        /// </param>
        /// <returns>Display text naming each honoured value, and each coerced one with its reason.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="plan"/> is null.</exception>
        public static string Describe(
            AcquisitionPlan plan,
            PlannedAcquisition planned = null,
            IFrontEndCapabilities capabilities = null)
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
            Append(text, "Path", plan.Path == AnalysisPath.RealBaseband ? "real baseband" : "complex zoom");
            Append(text, "Block size", plan.SamplesPerBlock.ToString(CultureInfo.CurrentCulture) + " samples");
            Append(text, "Reference level", plan.ReferenceLevelDbm.ToString("0.##", CultureInfo.CurrentCulture) + " dBm");
            // REQ-NFR-027: the measured rate and the duty cycle, beside the verdict they produced.
            // The verdict alone loses the magnitude — "0.98" and "12.4" are both "no" and mean very
            // different things to somebody deciding what to change — and it also hides whether the
            // judgement rests on a measurement at all.
            Append(text, "Gap-free", DescribeGapFree(plan));

            if (plan.MeasuredBytesPerSecond > 0.0)
            {
                Append(text, "Measured transfer", Rate(plan.MeasuredBytesPerSecond));
                Append(text, "Duty cycle", plan.DutyCycle.ToString("0.###", CultureInfo.CurrentCulture));
            }
            else
            {
                Append(text, "Measured transfer", "not measured");
            }

            if (planned != null)
            {
                Append(text, "Frequency points", DescribePoints(planned, capabilities, plan.Path));
                Append(text, "Max time record", Time(planned.MaxTimeSeconds));
            }

            text.AppendLine();

            int coerced = plan.Coercions.Count + (planned == null ? 0 : planned.Coercions.Count);

            if (coerced == 0)
            {
                text.AppendLine("Every requested value was honoured.");
                return text.ToString();
            }

            text.AppendLine(coerced == 1
                ? "One request was coerced:"
                : coerced + " requests were coerced:");

            if (planned != null)
            {
                AppendCoercions(text, planned.Coercions);
            }

            AppendCoercions(text, plan.Coercions);

            return text.ToString();
        }

        private static void AppendCoercions(
            StringBuilder text, IReadOnlyList<ParameterCoercion> coercions)
        {
            foreach (ParameterCoercion coercion in coercions)
            {
                // The reason is carried through verbatim. Paraphrasing it here would put the
                // explanation of a coercion in two places, and whoever made it is the only one of
                // them that knows why.
                text.AppendLine(
                    "  " + coercion.Parameter + ": asked " +
                    Plain(coercion.Requested) + ", got " + Plain(coercion.Honoured) +
                    " — " + coercion.Reason);
            }
        }

        /// <summary>
        /// The point count, against the most this front end could have delivered.
        /// </summary>
        private static string DescribePoints(
            PlannedAcquisition planned, IFrontEndCapabilities capabilities, AnalysisPath path)
        {
            string points = planned.FrequencyPoints.ToString(CultureInfo.CurrentCulture);

            if (capabilities == null)
            {
                return points;
            }

            int available = AcquisitionPlanner.MaximumPointsFor(capabilities, path);

            return available <= planned.FrequencyPoints
                ? points + " (this front end's maximum)"
                : points + " of a possible " + available.ToString(CultureInfo.CurrentCulture);
        }

        /// <summary>Engineering-notation time, for the maximum time record.</summary>
        private static string Time(double seconds)
        {
            if (seconds >= 1.0)
            {
                return seconds.ToString("0.###", CultureInfo.CurrentCulture) + " s";
            }

            if (seconds >= 1e-3)
            {
                return (seconds * 1e3).ToString("0.###", CultureInfo.CurrentCulture) + " ms";
            }

            if (seconds >= 1e-6)
            {
                return (seconds * 1e6).ToString("0.###", CultureInfo.CurrentCulture) + " us";
            }

            return (seconds * 1e9).ToString("0.###", CultureInfo.CurrentCulture) + " ns";
        }

        /// <summary>The gap-free verdict, saying whether it rests on a measurement.</summary>
        /// <param name="plan">The plan.</param>
        /// <remarks>
        /// <c>REQ-NFR-027</c>: "no UI affordance implies gap-free capture when the computed value
        /// is false". A bare "yes" against an unmeasured link is exactly such an affordance — it
        /// reads as a promise and rests on a nominal bus figure that this instrument, through an
        /// extender, misses by nearly two orders of magnitude.
        /// </remarks>
        private static string DescribeGapFree(AcquisitionPlan plan)
        {
            if (!plan.SupportsGapFreeStreaming)
            {
                return plan.MeasuredBytesPerSecond > 0.0
                    ? "no — the link cannot keep up with this plan"
                    : "no";
            }

            return plan.MeasuredBytesPerSecond > 0.0
                ? "yes"
                : "yes, estimated — the transfer rate has not been measured";
        }

        /// <summary>A byte rate in engineering units.</summary>
        private static string Rate(double bytesPerSecond)
        {
            if (bytesPerSecond >= 1.0e6)
            {
                return (bytesPerSecond / 1.0e6).ToString("0.##", CultureInfo.CurrentCulture) + " MB/s";
            }

            if (bytesPerSecond >= 1.0e3)
            {
                return (bytesPerSecond / 1.0e3).ToString("0.##", CultureInfo.CurrentCulture) + " kB/s";
            }

            return bytesPerSecond.ToString("0", CultureInfo.CurrentCulture) + " B/s";
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
