using System;
using System.Collections.Generic;
using System.Linq;
using OpenVSA.Hal;

namespace OpenVSA.Capture.Triggering
{
    /// <summary>
    /// One trigger style, whether the connected front end offers it, and why not if it does not.
    /// </summary>
    public sealed class TriggerOption
    {
        /// <summary>Creates an option.</summary>
        /// <param name="style">The style.</param>
        /// <param name="isAvailable">Whether it can be selected.</param>
        /// <param name="explanation">Why it cannot, or empty when it can.</param>
        public TriggerOption(TriggerStyle style, bool isAvailable, string explanation)
        {
            Style = style;
            IsAvailable = isAvailable;
            Explanation = explanation ?? string.Empty;
        }

        /// <summary>The style.</summary>
        public TriggerStyle Style { get; }

        /// <summary>Whether the connected front end offers it.</summary>
        public bool IsAvailable { get; }

        /// <summary>
        /// Why it is unavailable, for the tooltip on the greyed entry; empty when available.
        /// </summary>
        /// <remarks>
        /// <c>REQ-TRG-001</c> requires the tooltip to be explanatory. A control greyed with no
        /// reason reads as a bug in the software rather than as a limit of the instrument, and the
        /// user's next move is to look for the setting that enables it — which does not exist.
        /// </remarks>
        public string Explanation { get; }

        /// <summary>The name this style is shown under.</summary>
        public string DisplayName => TriggerAvailability.NameOf(Style);

        /// <inheritdoc />
        public override string ToString() =>
            DisplayName + (IsAvailable ? string.Empty : " (unavailable: " + Explanation + ")");
    }

    /// <summary>
    /// Which trigger styles a front end offers, and why it does not offer the rest
    /// (<c>REQ-TRG-001</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Driven entirely by <see cref="IFrontEndCapabilities"/>.</strong>
    /// <c>REQ-HAL-002</c> forbids instrument-specific conditionals above the HAL, so the answer
    /// here comes from what the front end declares and from nothing else — no model names, no
    /// special cases.
    /// </para>
    /// <para>
    /// Every style is returned, available or not, rather than the available ones alone. A user
    /// looking for a frequency-mask trigger needs to see that it exists and that this instrument
    /// cannot do it; a list that simply omits it leaves them wondering whether the software has one.
    /// </para>
    /// </remarks>
    public static class TriggerAvailability
    {
        /// <summary>Every style, in the order a selector offers them.</summary>
        public static IReadOnlyList<TriggerStyle> AllStyles { get; } = new[]
        {
            TriggerStyle.Immediate,
            TriggerStyle.External,
            TriggerStyle.Level,
            TriggerStyle.Periodic,
            TriggerStyle.FrequencyMask,
        };

        /// <summary>
        /// The styles a front end offers, with an explanation attached to each it does not.
        /// </summary>
        /// <param name="capabilities">What the front end declares.</param>
        /// <exception cref="ArgumentNullException"><paramref name="capabilities"/> is null.</exception>
        public static IReadOnlyList<TriggerOption> For(IFrontEndCapabilities capabilities)
        {
            if (capabilities == null)
            {
                throw new ArgumentNullException(nameof(capabilities));
            }

            IReadOnlyList<TriggerStyle> declared =
                capabilities.TriggerStyles ?? new TriggerStyle[0];

            var offered = new HashSet<TriggerStyle>(declared);
            var options = new List<TriggerOption>(AllStyles.Count);

            foreach (TriggerStyle style in AllStyles)
            {
                options.Add(Option(style, offered, capabilities));
            }

            return options;
        }

        /// <summary>Whether a front end offers a style.</summary>
        /// <param name="capabilities">What the front end declares.</param>
        /// <param name="style">The style.</param>
        /// <exception cref="ArgumentNullException"><paramref name="capabilities"/> is null.</exception>
        public static bool Offers(IFrontEndCapabilities capabilities, TriggerStyle style) =>
            For(capabilities).First(o => o.Style == style).IsAvailable;

        /// <summary>The name a style is shown under.</summary>
        /// <param name="style">The style.</param>
        /// <exception cref="ArgumentOutOfRangeException">Not a known style.</exception>
        /// <remarks>
        /// "Free Run" rather than "Immediate": the enumerator is the code's name for it and this is
        /// the instrument world's, and the requirement lists the latter.
        /// </remarks>
        public static string NameOf(TriggerStyle style)
        {
            switch (style)
            {
                case TriggerStyle.Immediate: return "Free Run";
                case TriggerStyle.External: return "External";
                case TriggerStyle.Level: return "Channel level";
                case TriggerStyle.Periodic: return "Periodic";
                case TriggerStyle.FrequencyMask: return "Frequency mask";

                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(style), style, "Not a known trigger style.");
            }
        }

        private static TriggerOption Option(
            TriggerStyle style,
            HashSet<TriggerStyle> offered,
            IFrontEndCapabilities capabilities)
        {
            if (style == TriggerStyle.FrequencyMask)
            {
                // REQ-TRG-001: declared unsupported unless the front end reports real-time
                // capability, whatever its trigger list says. A mask tested against every other
                // transform is not a mask - it would miss precisely the transient it was drawn
                // to catch, and would do so silently.
                if (!capabilities.SupportsRealTimeAnalysis)
                {
                    return new TriggerOption(
                        style,
                        false,
                        "This source does not analyse gap-free in real time, so a frequency mask " +
                        "could not be tested against every transform and would miss transients.");
                }

                if (!offered.Contains(style))
                {
                    return new TriggerOption(
                        style, false, "This source does not offer a frequency-mask trigger.");
                }

                return new TriggerOption(style, true, string.Empty);
            }

            if (offered.Contains(style))
            {
                return new TriggerOption(style, true, string.Empty);
            }

            return new TriggerOption(
                style, false, "This source does not offer a " + NameOf(style).ToLowerInvariant() +
                " trigger.");
        }
    }
}
