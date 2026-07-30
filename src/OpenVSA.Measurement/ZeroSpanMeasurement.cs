using System;
using OpenVSA.Dsp.Spectrum;
using OpenVSA.Dsp.Windowing;

namespace OpenVSA.Measurement
{
    /// <summary>
    /// Zero-span/power-spectrum operation: one channel's power, through a channel filter
    /// (<c>REQ-DSP-012</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Zero span is an analysis mode, not a span of zero hertz.</strong> A digital vector
    /// analyser does not stop acquiring a band in order to report one channel: it acquires the
    /// narrowest band the front end allows and reports the power in a single channel against time.
    /// Putting the mode in the acquisition request would meet <c>REQ-HAL-002</c>'s declared
    /// <c>MinSpanHz</c>, which correctly refuses a span of zero — so the mode lives in the analysis
    /// state and the negotiated plan is untouched.
    /// </para>
    /// <para>
    /// <strong>This is what makes the Channel Filter Shape control mean something.</strong> A setting
    /// that is recorded and never applied is a setting that lies. The reading here is taken through
    /// the selected shape, so choosing Gaussian rather than None changes the number — by nothing at
    /// all on a centred tone, and on noise by the ratio of the two noise bandwidths, which for
    /// <see cref="ChannelFilterType.None"/> is the whole analysed span rather than the channel. Both
    /// are asserted, because the first is what says the filter is centred and the second is what says
    /// it is shaped.
    /// </para>
    /// <para>
    /// Integrated from the spectrum rather than by filtering the samples, because the two are the
    /// same calculation and this one reuses <see cref="BandMeasurements.WeightedPower"/> — the
    /// integrator an adjacent-channel measurement and a band-power marker already share. A second
    /// implementation would be a second set of edge conventions to keep in agreement.
    /// </para>
    /// </remarks>
    public static class ZeroSpanMeasurement
    {
        /// <summary>
        /// The power in the channel, through the selected filter.
        /// </summary>
        /// <param name="frame">The spectrum of the acquired band.</param>
        /// <param name="filter">The channel filter shape.</param>
        /// <param name="bandwidthHz">
        /// The channel's 3 dB bandwidth, in hertz — the resolution bandwidth in zero span. Ignored
        /// for <see cref="ChannelFilterType.None"/>, which takes the whole analysed band.
        /// </param>
        /// <returns>The channel's total power and its density.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="frame"/> is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="bandwidthHz"/> is invalid.</exception>
        public static BandPower Power(
            SpectrumFrame frame, ChannelFilterType filter, double bandwidthHz)
        {
            if (frame == null)
            {
                throw new ArgumentNullException(nameof(frame));
            }

            if (!(bandwidthHz > 0.0) || double.IsInfinity(bandwidthHz))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(bandwidthHz), bandwidthHz,
                    "A channel bandwidth must be positive and finite.");
            }

            double centreHz = frame.CenterFrequencyHz;

            if (filter == ChannelFilterType.None)
            {
                // Everything that arrived, counted equally: the anti-alias filter already decided
                // what that is, and the frame covers exactly the analysed span.
                return BandMeasurements.Power(frame, frame.StartFrequencyHz, frame.StopFrequencyHz);
            }

            double half = ChannelFilters.GaussianIntegrationBandwidths * bandwidthHz;

            return BandMeasurements.WeightedPower(
                frame,
                centreHz - half,
                centreHz + half,
                f => ChannelFilters.PowerResponseAt(filter, f - centreHz, bandwidthHz));
        }

        /// <summary>
        /// The noise bandwidth the reading was taken through, in hertz.
        /// </summary>
        /// <param name="frame">The spectrum of the acquired band.</param>
        /// <param name="filter">The channel filter shape.</param>
        /// <param name="bandwidthHz">The channel's 3 dB bandwidth, in hertz.</param>
        /// <returns>The noise bandwidth.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="frame"/> is null.</exception>
        /// <remarks>
        /// Reported alongside the power because it is the figure that makes a noise reading
        /// comparable with one taken elsewhere. For <see cref="ChannelFilterType.None"/> it is the
        /// analysed span, which is a property of the acquisition rather than of the setting — hence
        /// the frame.
        /// </remarks>
        public static double NoiseBandwidthHz(
            SpectrumFrame frame, ChannelFilterType filter, double bandwidthHz)
        {
            if (frame == null)
            {
                throw new ArgumentNullException(nameof(frame));
            }

            return filter == ChannelFilterType.None
                ? ChannelFilters.NoiseBandwidthHz(
                    filter, Math.Max(frame.BinWidthHz, frame.StopFrequencyHz - frame.StartFrequencyHz))
                : ChannelFilters.NoiseBandwidthHz(filter, bandwidthHz);
        }
    }
}
