using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace OpenVSA.Dsp.Windowing
{
    /// <summary>
    /// The channel filter that replaces the analysis window in zero-span operation
    /// (<c>REQ-DSP-012</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Named to mirror <see cref="WindowType"/>, because it replaces it.</strong> In
    /// zero-span/power-spectrum operation there is no band being resolved into bins, so a window has
    /// nothing to do: what shapes the measurement is the filter defining the channel. Offering both
    /// controls at once would offer a setting that does nothing, which is why <c>REQ-DSP-012</c>
    /// asks for a replacement rather than an addition.
    /// </para>
    /// <para>
    /// <strong>Not to be confused with <c>OpenVSA.Measurement.Channels.ChannelFilterShape</c>.</strong>
    /// That one is <c>REQ-CHM-001</c>'s <em>measurement</em> filter — rectangular or
    /// root-raised-cosine — through which an adjacent-channel-power integration is taken, and it
    /// applies whatever the span. This one is the zero-span RBW-shaping filter and applies only in
    /// that mode. The reference product calls both "channel filter"; they are two settings and
    /// conflating them would make an ACP measurement depend on a display mode.
    /// </para>
    /// </remarks>
    public enum ChannelFilterType
    {
        /// <summary>
        /// A Gaussian channel filter of the stated 3 dB bandwidth.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The swept spectrum analyser's own filter shape, which is why zero-span readings taken
        /// through it are comparable with one. Its skirts fall monotonically and it has no sidelobes
        /// at all, so a strong neighbour leaks in predictably rather than through a ripple whose
        /// depth depends on exactly where it sits.
        /// </para>
        /// <para>
        /// <strong>There is also a <see cref="WindowType.Gaussian"/>, and they are different
        /// things.</strong> That one is an analysis window applied to a time record before a
        /// transform; this one is a channel filter shaping a band. They share a name because both are
        /// Gaussian in shape, and the collision is worth knowing about: a check that looks for a
        /// window-type control by the names it offers will find this control unless it excludes the
        /// names the two have in common.
        /// </para>
        /// </remarks>
        Gaussian = 0,

        /// <summary>
        /// No channel filter: only the front end's own anti-alias filter shapes the band.
        /// </summary>
        /// <remarks>
        /// Everything the acquisition delivered, counted equally. Wider noise bandwidth than any
        /// shaped filter of the same 3 dB width, and the honest choice when the question is "how
        /// much power arrived", not "how much power arrived in this channel".
        /// </remarks>
        None,
    }

    /// <summary>
    /// The channel filter shapes' responses and noise bandwidths (<c>REQ-DSP-012</c>).
    /// </summary>
    /// <remarks>
    /// Beside the enumeration and below the UI, for the same reason <c>TraceDataTypes</c> is: the
    /// response is a property of the filter, not of the control that selects it, and a UI that had
    /// to know it would be a UI that could get it wrong.
    /// </remarks>
    public static class ChannelFilters
    {
        /// <summary>
        /// A Gaussian filter's equivalent noise bandwidth as a multiple of its 3 dB bandwidth.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <c>∫|H(f)|² df = B · √(π / (4 ln 2)) = 1.0644...·B</c> for the power response
        /// <c>exp(−ln2 · (2f/B)²)</c>. This is the same 1.065 figure a swept spectrum analyser's
        /// documentation quotes for converting a Gaussian resolution bandwidth to a noise bandwidth,
        /// which is what makes a zero-span noise reading through this filter comparable with one
        /// taken on such an instrument.
        /// </para>
        /// <para>
        /// Stated as a constant computed from the closed form rather than typed as 1.0645, so the
        /// figure and the response cannot drift apart.
        /// </para>
        /// </remarks>
        public static readonly double GaussianNoiseBandwidthFactor =
            Math.Sqrt(Math.PI / (4.0 * Math.Log(2.0)));

        /// <summary>
        /// How far out, in multiples of the 3 dB bandwidth, a Gaussian filter is integrated.
        /// </summary>
        /// <remarks>
        /// The Gaussian never reaches zero, so an integration has to stop somewhere. At three
        /// bandwidths from the centre the power response is <c>2^-36</c> — about −108 dB — so the
        /// energy discarded is far below anything a measurement can resolve, and the bound is stated
        /// here rather than chosen afresh by each caller.
        /// </remarks>
        public const double GaussianIntegrationBandwidths = 3.0;

        private static readonly ReadOnlyCollection<ChannelFilterType> Shapes =
            new ReadOnlyCollection<ChannelFilterType>(
                new List<ChannelFilterType>
                {
                    ChannelFilterType.Gaussian,
                    ChannelFilterType.None,
                });

        /// <summary>
        /// Every shape the control offers, in the order it offers them.
        /// </summary>
        /// <remarks>
        /// A list rather than <c>Enum.GetValues</c>, because <c>REQ-DSP-012</c>'s criterion names
        /// the two shapes and their order: a member added to the enumeration should have to be added
        /// here deliberately rather than appearing in the UI on its own.
        /// </remarks>
        public static IReadOnlyList<ChannelFilterType> All => Shapes;

        /// <summary>
        /// How a shape is spelled in the UI.
        /// </summary>
        /// <param name="filter">The shape.</param>
        /// <returns>Its name.</returns>
        /// <remarks>
        /// "None (anti-alias only)" rather than "None", because the band is not unfiltered — the
        /// front end's anti-alias filter is still there and still decides what arrived. A bare
        /// "None" would claim otherwise.
        /// </remarks>
        public static string Describe(ChannelFilterType filter)
        {
            switch (filter)
            {
                case ChannelFilterType.Gaussian: return "Gaussian";
                case ChannelFilterType.None: return "None (anti-alias only)";
                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(filter), filter, "There is no such channel filter shape.");
            }
        }

        /// <summary>
        /// Parses a spelling produced by <see cref="Describe"/>.
        /// </summary>
        /// <param name="text">The text.</param>
        /// <param name="filter">Receives the shape.</param>
        /// <returns>Whether the text named a shape.</returns>
        public static bool TryParse(string text, out ChannelFilterType filter)
        {
            filter = ChannelFilterType.Gaussian;

            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            string trimmed = text.Trim();

            foreach (ChannelFilterType candidate in Shapes)
            {
                if (string.Equals(
                        Describe(candidate), trimmed, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(
                        candidate.ToString(), trimmed, StringComparison.OrdinalIgnoreCase))
                {
                    filter = candidate;
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// The filter's power response <c>|H(f)|²</c> at an offset from the channel centre.
        /// </summary>
        /// <param name="filter">The shape.</param>
        /// <param name="offsetHz">Distance from the channel centre, in hertz; sign is ignored.</param>
        /// <param name="bandwidthHz">The filter's 3 dB bandwidth, in hertz; must be positive.</param>
        /// <returns>A weight from 0 to 1.</returns>
        /// <exception cref="ArgumentOutOfRangeException">
        /// <paramref name="bandwidthHz"/> is not positive and finite, or the shape is unknown.
        /// </exception>
        /// <remarks>
        /// In power, because that is what multiplies a bin — the same convention
        /// <c>ChannelDefinition.PowerResponseAt</c> uses, so the two can be handed to the same
        /// integrator. At <c>f = B/2</c> the Gaussian returns exactly one half, which is what makes
        /// <paramref name="bandwidthHz"/> the 3 dB bandwidth by construction rather than by a
        /// separately maintained constant.
        /// </remarks>
        public static double PowerResponseAt(
            ChannelFilterType filter, double offsetHz, double bandwidthHz)
        {
            if (!(bandwidthHz > 0.0) || double.IsInfinity(bandwidthHz))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(bandwidthHz), bandwidthHz,
                    "A channel filter's bandwidth must be positive and finite.");
            }

            double f = Math.Abs(offsetHz);

            switch (filter)
            {
                case ChannelFilterType.None:
                    // Anti-alias only: flat over the band that arrived, and this function is not
                    // told what that band is -- the caller integrates over the analysed span.
                    return 1.0;

                case ChannelFilterType.Gaussian:
                    double normalised = 2.0 * f / bandwidthHz;

                    return Math.Exp(-Math.Log(2.0) * normalised * normalised);

                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(filter), filter, "There is no such channel filter shape.");
            }
        }

        /// <summary>
        /// The filter's equivalent noise bandwidth, in hertz.
        /// </summary>
        /// <param name="filter">The shape.</param>
        /// <param name="bandwidthHz">
        /// The 3 dB bandwidth for <see cref="ChannelFilterType.Gaussian"/>, or the analysed
        /// bandwidth for <see cref="ChannelFilterType.None"/>.
        /// </param>
        /// <returns>The noise bandwidth, in hertz.</returns>
        /// <exception cref="ArgumentOutOfRangeException">The bandwidth or the shape is invalid.</exception>
        /// <remarks>
        /// The figure that says what switching shape does to a noise-like signal, which is the only
        /// way the choice is visible on anything but a pure tone. Unshaped is wider than Gaussian of
        /// the same nominal width, by about 0.27 dB.
        /// </remarks>
        public static double NoiseBandwidthHz(ChannelFilterType filter, double bandwidthHz)
        {
            if (!(bandwidthHz > 0.0) || double.IsInfinity(bandwidthHz))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(bandwidthHz), bandwidthHz,
                    "A channel filter's bandwidth must be positive and finite.");
            }

            switch (filter)
            {
                case ChannelFilterType.None:
                    return bandwidthHz;

                case ChannelFilterType.Gaussian:
                    return GaussianNoiseBandwidthFactor * bandwidthHz;

                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(filter), filter, "There is no such channel filter shape.");
            }
        }
    }
}
