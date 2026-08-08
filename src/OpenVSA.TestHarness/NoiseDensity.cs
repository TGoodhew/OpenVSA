using System;
using System.Collections.Generic;

namespace OpenVSA.TestHarness
{
    /// <summary>
    /// Measures power spectral density from a spectrum (issue #393, <c>REQ-DSP-011</c>).
    /// </summary>
    /// <remarks>
    /// A pure function, separate from the runner, for the reason <see cref="ToneSearch"/> is: it is
    /// the part of a noise scenario that can be wrong quietly, and the only part that can be checked
    /// without a generator.
    /// </remarks>
    public static class NoiseDensity
    {
        /// <summary>Bins either side of centre excluded from the average.</summary>
        /// <remarks>
        /// A residual local-oscillator spur sits at the centre of the analysis on a real receiver.
        /// It is a tone, so it is far above the noise around it, and averaging it in raises the
        /// density by an amount that depends on the span rather than on anything being measured.
        /// </remarks>
        public const int CentreGuardBins = 3;

        /// <summary>
        /// Fraction of the requested span, either side of centre, that is averaged.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <strong>Of the SPAN, and that distinction was worth 1.3 dB.</strong> This started as a
        /// fraction of the frame's bin count, which is not the same thing: the front end digitises
        /// at 1.5× its information bandwidth, so the frame reaches well beyond the span that was
        /// asked for, and a fixed fraction of the frame lands on the analysis filter's roll-off.
        /// Averaging that in reported the roll-off as a density error — measured 2.58 dB low, of
        /// which 1.29 dB came back simply by widening the guard, which is how the cause was found.
        /// </para>
        /// <para>
        /// 0.35 of the span each way keeps the average inside the flat part with room to spare. The
        /// remaining departure is real measurement, not geometry.
        /// </para>
        /// </remarks>
        public const double UsableSpanFraction = 0.35;

        /// <summary>
        /// Mean power spectral density across the usable bins, in dBm per hertz.
        /// </summary>
        /// <param name="levelsDbm">The measured spectrum, in dBm per bin.</param>
        /// <param name="startFrequencyHz">Frequency of the first bin, in hertz.</param>
        /// <param name="binWidthHz">Frequency step between bins, in hertz.</param>
        /// <param name="centreFrequencyHz">Centre of the analysis, in hertz.</param>
        /// <param name="spanHz">The span that was asked for, in hertz.</param>
        /// <param name="equivalentNoiseBandwidthBins">
        /// The analysis window's equivalent noise bandwidth, in bins — 1.0 for rectangular,
        /// 3.8194 for the flat top this product defaults to.
        /// </param>
        /// <param name="binsUsed">How many bins the mean was taken over.</param>
        /// <returns>Mean density in dBm/Hz, or <c>NaN</c> if there was nothing usable to average.</returns>
        /// <remarks>
        /// <para>
        /// <strong>The average is taken in POWER, never in decibels.</strong> The mean of the
        /// logarithms is the logarithm of the geometric mean, which for noise sits well below the
        /// arithmetic mean — about 2.5 dB low for Rayleigh-distributed magnitudes. A harness that
        /// averaged in dB would report a density error of roughly that size on a perfectly correct
        /// analyser, and the number is stable enough to look like a real calibration offset.
        /// </para>
        /// <para>
        /// <strong>The noise bandwidth of one bin is <c>binWidth × ENBW</c>, not <c>binWidth</c>.
        /// </strong> That product is the whole of <c>REQ-DSP-011</c>: the window spreads each bin's
        /// response over more than one bin's spacing, and dividing by the spacing alone overstates
        /// the density by 10·log10(ENBW) — 5.82 dB for the flat top.
        /// </para>
        /// </remarks>
        public static double MeasureDbmPerHz(
            IReadOnlyList<float> levelsDbm,
            double startFrequencyHz,
            double binWidthHz,
            double centreFrequencyHz,
            double spanHz,
            double equivalentNoiseBandwidthBins,
            out int binsUsed)
        {
            binsUsed = 0;

            if (levelsDbm == null || levelsDbm.Count == 0 || !(binWidthHz > 0.0) ||
                !(spanHz > 0.0) || !(equivalentNoiseBandwidthBins > 0.0))
            {
                return double.NaN;
            }

            double reach = spanHz * UsableSpanFraction;
            double guard = CentreGuardBins * binWidthHz;
            double total = 0.0;

            for (int index = 0; index < levelsDbm.Count; index++)
            {
                double offset = startFrequencyHz + (index * binWidthHz) - centreFrequencyHz;
                double distance = Math.Abs(offset);

                if (distance > reach || distance <= guard)
                {
                    continue;
                }

                float level = levelsDbm[index];

                if (float.IsNaN(level) || float.IsInfinity(level))
                {
                    continue;
                }

                total += Math.Pow(10.0, level / 10.0);
                binsUsed++;
            }

            if (binsUsed == 0)
            {
                return double.NaN;
            }

            double meanBinDbm = 10.0 * Math.Log10(total / binsUsed);

            return meanBinDbm - (10.0 * Math.Log10(binWidthHz * equivalentNoiseBandwidthBins));
        }
    }
}
