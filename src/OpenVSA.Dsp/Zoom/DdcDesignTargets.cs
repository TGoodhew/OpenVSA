namespace OpenVSA.Dsp.Zoom
{
    /// <summary>
    /// The filter performance the downconverter is designed and tested against
    /// (<c>REQ-DSP-023a</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// These are the requirement's own table, written once as constants rather than repeated as
    /// literals in the designer and again in the tests. The reference product does not publish
    /// figures for its downconverter, so <c>REQ-DSP-023a</c> states them as design targets and
    /// <c>REQ-TST-001</c> requires measured SFDR against them. A target that lived only in a test
    /// assertion would be a number nobody could find from the code that has to meet it.
    /// </para>
    /// <para>
    /// <strong><see cref="UsableBandwidthFraction"/> and <see cref="FlatnessSpanFraction"/> are
    /// unrelated quantities that happen to be equal.</strong> The first says how much of the
    /// decimated sample rate the downconverter delivers alias-free; the second says which part of
    /// a span the tighter of the two amplitude targets applies over. Both are 0.8 by coincidence,
    /// and changing one because the other moved would be a real defect, so they are named and
    /// documented apart.
    /// </para>
    /// </remarks>
    public static class DdcDesignTargets
    {
        /// <summary>Peak-to-peak passband ripple allowed over the analysis span, in dB.</summary>
        public const double PassbandRippleDb = 0.05;

        /// <summary>
        /// Amplitude flatness allowed over the central <see cref="FlatnessSpanFraction"/> of the
        /// span, in dB. A bound on the deviation either way, so the peak-to-peak figure it implies
        /// is twice this.
        /// </summary>
        public const double PassbandFlatnessDb = 0.02;

        /// <summary>
        /// The fraction of the span, centred, that <see cref="PassbandFlatnessDb"/> applies over.
        /// </summary>
        public const double FlatnessSpanFraction = 0.80;

        /// <summary>Stopband and alias rejection required, in dB.</summary>
        public const double StopbandRejectionDb = 100.0;

        /// <summary>Spurious-free dynamic range required through the downconverter, in dBc.</summary>
        public const double SpuriousFreeDynamicRangeDbc = 100.0;

        /// <summary>
        /// The stopband the filter is actually designed for, in dB.
        /// </summary>
        /// <remarks>
        /// Ten dB above <see cref="StopbandRejectionDb"/>. Kaiser's length estimate errs slightly
        /// short at high attenuations, the achieved figure moves a little with the transition
        /// width, and the output is stored as <see cref="float"/>. Designing at exactly the
        /// requirement would leave a filter that passes the target on some decimation factors and
        /// misses it on others. The margin costs <c>(110 − 8) / (100 − 8)</c>, about 11 % more
        /// taps, which in a decimating structure is 11 % of a cost that is already divided by the
        /// decimation factor.
        /// </remarks>
        public const double DesignStopbandDb = 110.0;

        /// <summary>
        /// The fraction of the decimated sample rate the downconverter delivers alias-free.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Complex sampling makes the whole output rate available in principle; what is actually
        /// usable is set by where the decimation filter's transition band has to sit. Decimating by
        /// <c>M</c> folds everything at and above <c>Fs_out − f_pass</c> back into the passband, so
        /// with a passband edge at <c>α·Fs_out/2</c> the stopband must start at
        /// <c>Fs_out·(1 − α/2)</c> and the transition band is <c>Fs_out·(1 − α)</c> wide.
        /// </para>
        /// <para>
        /// The choice of <c>α</c> is a trade of usable bandwidth against tap count, and 0.8 is the
        /// point where the filter costs about 35 taps per phase — cheap — while still giving four
        /// fifths of the output rate. Pushing to 0.9 would double the taps for one eighth more
        /// span.
        /// </para>
        /// </remarks>
        public const double UsableBandwidthFraction = 0.80;
    }
}
