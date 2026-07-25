using System;
using OpenVSA.Dsp.Windowing;

namespace OpenVSA.Dsp.Spectrum
{
    /// <summary>
    /// The resolution-bandwidth / time-record coupling, in both directions (<c>REQ-DSP-020</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// One relation, <c>RBW = ENBW / T_rec</c>, and which side is independent depends on the
    /// measurement class: a spectrum measurement takes RBW as the setting and derives the record
    /// length, a demodulation measurement takes Main Time Length as the setting and derives the
    /// RBW. Both are the same equation, so they cannot disagree — which is the point of putting it
    /// here rather than solving it separately in each measurement.
    /// </para>
    /// <para>
    /// <strong>ENBW is in bins, not hertz.</strong> It is the window's noise bandwidth expressed as
    /// a multiple of the bin spacing (1.5 for Hann, 3.8194 for Flat Top), which is what makes the
    /// relation independent of span and transform size. Substituting a bandwidth in hertz gives an
    /// answer that looks plausible and is wrong by the bin width.
    /// </para>
    /// <para>
    /// <strong>RBW is not the bin spacing.</strong> Under the default Flat Top window the
    /// resolution bandwidth is nearly four times the spacing between displayed points, and treating
    /// the two as the same is how a measurement comes to claim resolution it does not have.
    /// </para>
    /// </remarks>
    public static class ResolutionBandwidth
    {
        /// <summary>
        /// The RBW a record length gives: <c>ENBW / T_rec</c>.
        /// </summary>
        /// <param name="equivalentNoiseBandwidthBins">The window's ENBW, in bins.</param>
        /// <param name="recordSeconds">Time-record length, in seconds.</param>
        /// <returns>Resolution bandwidth in hertz.</returns>
        /// <exception cref="ArgumentOutOfRangeException">A value is not positive and finite.</exception>
        public static double ForRecordLength(double equivalentNoiseBandwidthBins, double recordSeconds)
        {
            RequirePositive(equivalentNoiseBandwidthBins, nameof(equivalentNoiseBandwidthBins));
            RequirePositive(recordSeconds, nameof(recordSeconds));

            return equivalentNoiseBandwidthBins / recordSeconds;
        }

        /// <summary>
        /// The record length an RBW needs: <c>ENBW / RBW</c>.
        /// </summary>
        /// <param name="equivalentNoiseBandwidthBins">The window's ENBW, in bins.</param>
        /// <param name="resolutionBandwidthHz">Resolution bandwidth, in hertz.</param>
        /// <returns>Time-record length in seconds.</returns>
        /// <exception cref="ArgumentOutOfRangeException">A value is not positive and finite.</exception>
        public static double RecordLengthFor(
            double equivalentNoiseBandwidthBins, double resolutionBandwidthHz)
        {
            RequirePositive(equivalentNoiseBandwidthBins, nameof(equivalentNoiseBandwidthBins));
            RequirePositive(resolutionBandwidthHz, nameof(resolutionBandwidthHz));

            return equivalentNoiseBandwidthBins / resolutionBandwidthHz;
        }

        /// <summary>The RBW a window gives over a record length.</summary>
        /// <param name="window">The analysis window.</param>
        /// <param name="recordSeconds">Time-record length, in seconds.</param>
        /// <exception cref="ArgumentNullException"><paramref name="window"/> is null.</exception>
        public static double ForRecordLength(Window window, double recordSeconds)
        {
            if (window == null)
            {
                throw new ArgumentNullException(nameof(window));
            }

            return ForRecordLength(window.Enbw, recordSeconds);
        }

        /// <summary>The record length a window needs to reach an RBW.</summary>
        /// <param name="window">The analysis window.</param>
        /// <param name="resolutionBandwidthHz">Resolution bandwidth, in hertz.</param>
        /// <exception cref="ArgumentNullException"><paramref name="window"/> is null.</exception>
        public static double RecordLengthFor(Window window, double resolutionBandwidthHz)
        {
            if (window == null)
            {
                throw new ArgumentNullException(nameof(window));
            }

            return RecordLengthFor(window.Enbw, resolutionBandwidthHz);
        }

        /// <summary>
        /// The RBW a displayed spectrum has: <c>ENBW × Span / (N_f − 1)</c>.
        /// </summary>
        /// <param name="equivalentNoiseBandwidthBins">The window's ENBW, in bins.</param>
        /// <param name="spanHz">Analysis span, in hertz.</param>
        /// <param name="frequencyPoints">Displayed frequency points.</param>
        /// <remarks>
        /// The same relation seen from the display: <c>T_rec = (N_f − 1) / Span</c> is
        /// <c>REQ-ACQ-001</c>'s <c>T_max</c>, so this is <c>ENBW / T_rec</c> with the substitution
        /// made. The two forms agreeing is what keeps the axis and the RBW annotation consistent.
        /// </remarks>
        /// <exception cref="ArgumentOutOfRangeException">A value is out of range.</exception>
        public static double ForSpan(
            double equivalentNoiseBandwidthBins, double spanHz, int frequencyPoints)
        {
            if (frequencyPoints < 2)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(frequencyPoints), frequencyPoints, "A spectrum needs at least two points.");
            }

            RequirePositive(spanHz, nameof(spanHz));

            return ForRecordLength(
                equivalentNoiseBandwidthBins, (frequencyPoints - 1) / spanHz);
        }

        /// <summary>
        /// The <c>N_f − 1</c> a given RBW needs over a span, before snapping to an available count.
        /// </summary>
        /// <param name="equivalentNoiseBandwidthBins">The window's ENBW, in bins.</param>
        /// <param name="spanHz">Analysis span, in hertz.</param>
        /// <param name="resolutionBandwidthHz">Wanted resolution bandwidth, in hertz.</param>
        /// <returns>The required number of intervals, which will not generally be a whole number.</returns>
        /// <remarks>
        /// Returned unrounded, because the caller must round <em>up</em> to the next available
        /// point count: more points means a longer record and so a finer RBW. Rounding to nearest
        /// here would hand back a measurement whose resolution is coarser than the one asked for,
        /// which is the direction that matters — a user who asks for 100 Hz and gets 110 Hz has
        /// been given a different measurement without being told.
        /// </remarks>
        /// <exception cref="ArgumentOutOfRangeException">A value is not positive and finite.</exception>
        public static double RequiredIntervals(
            double equivalentNoiseBandwidthBins, double spanHz, double resolutionBandwidthHz)
        {
            RequirePositive(spanHz, nameof(spanHz));

            return RecordLengthFor(equivalentNoiseBandwidthBins, resolutionBandwidthHz) * spanHz;
        }

        private static void RequirePositive(double value, string name)
        {
            if (!(value > 0.0) || double.IsInfinity(value))
            {
                throw new ArgumentOutOfRangeException(name, value, name + " must be positive and finite.");
            }
        }
    }
}
