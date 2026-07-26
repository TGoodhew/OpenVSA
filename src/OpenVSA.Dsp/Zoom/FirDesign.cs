using System;

namespace OpenVSA.Dsp.Zoom
{
    /// <summary>
    /// Designs the linear-phase low-pass filters the downconverter decimates with
    /// (<c>REQ-DSP-023a</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Kaiser-windowed sinc, not an equiripple design.</strong> Parks–McClellan reaches a
    /// given stopband in fewer taps, but it is an iterative solver whose failure mode is a filter
    /// that is subtly wrong rather than one that does not exist, and every zoomed measurement and
    /// every demodulation result passes through this. The Kaiser design is closed form: the same
    /// arguments give the same taps, every time, and the achieved attenuation follows from the
    /// formula rather than from a search that converged. Where it costs taps it costs them in a
    /// polyphase structure, which spends them at one per output sample per phase.
    /// </para>
    /// <para>
    /// <strong>Odd length, symmetric.</strong> That is what makes the phase exactly linear with an
    /// integer group delay of <c>(N−1)/2</c> samples — so the time alignment of a zoomed record can
    /// be corrected exactly rather than to the nearest sample, and there is no fractional-delay
    /// interpolation to go wrong.
    /// </para>
    /// </remarks>
    public static class FirDesign
    {
        /// <summary>
        /// Designs a low-pass filter by the Kaiser method.
        /// </summary>
        /// <param name="cutoff">
        /// −6 dB cutoff, in cycles per sample; must be in (0, 0.5). The midpoint of the passband
        /// and stopband edges.
        /// </param>
        /// <param name="transitionWidth">
        /// Width from passband edge to stopband edge, in cycles per sample; must be positive.
        /// </param>
        /// <param name="stopbandDb">Wanted stopband attenuation, in dB; must be positive.</param>
        /// <returns>The taps, symmetric and of odd length, normalised to unity gain at DC.</returns>
        /// <exception cref="ArgumentOutOfRangeException">A value is out of range.</exception>
        /// <remarks>
        /// Normalised to unity at DC rather than left as the analytic sinc. The window truncates
        /// the sinc, which moves the DC gain by a few parts in a million; normalising removes that
        /// exactly, and a zoom that changed the amplitude of what it was zooming into by even
        /// 0.001 dB would put the whole amplitude chain of <c>REQ-AMP-001</c> in doubt.
        /// </remarks>
        public static double[] LowPass(double cutoff, double transitionWidth, double stopbandDb)
        {
            if (!(cutoff > 0.0) || cutoff >= 0.5)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(cutoff), cutoff,
                    "A cutoff must lie between 0 and half the sample rate, in cycles per sample.");
            }

            if (!(transitionWidth > 0.0) || double.IsInfinity(transitionWidth))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(transitionWidth), transitionWidth,
                    "A transition width must be positive and finite.");
            }

            if (!(stopbandDb > 0.0) || double.IsInfinity(stopbandDb))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(stopbandDb), stopbandDb, "A stopband attenuation must be positive.");
            }

            int length = TapCountFor(transitionWidth, stopbandDb);
            double beta = Beta(stopbandDb + MarginDb);
            int half = (length - 1) / 2;

            var taps = new double[length];
            double sum = 0.0;

            for (int n = 0; n < length; n++)
            {
                double offset = n - half;
                double ideal = 2.0 * cutoff * Sinc(2.0 * cutoff * offset);
                double window = Kaiser(offset / half, beta);

                taps[n] = ideal * window;
                sum += taps[n];
            }

            for (int n = 0; n < length; n++)
            {
                taps[n] /= sum;
            }

            return taps;
        }

        /// <summary>
        /// The margin, in dB, that a design is over-specified by so that it meets what was asked.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <strong>Measured, not chosen.</strong> Kaiser's <c>β</c> and length formulas are
        /// empirical fits, and a filter designed straight from them lands about 0.9 dB short of
        /// its stopband across the whole useful range of transition widths — with excursions to
        /// 2.7 dB at short lengths, where forcing the tap count to an odd integer is itself worth
        /// more than a decibel. Sweeping transition widths from 0.1 down to 0.00078 cycles per
        /// sample and attenuations from 60 to 130 dB puts the worst case inside 3 dB, so designing
        /// for <c>A + 3</c> is what makes <see cref="LowPass"/>'s promise of <c>A</c> true. Over
        /// that same sweep the corrected design lands between 0.3 dB and 4.4 dB <em>past</em> what
        /// was asked — a margin, not a comfortable one at the short end, and not one to spend.
        /// </para>
        /// <para>
        /// The alternative — leaving the shortfall and widening every caller's target to cover it
        /// — spreads one filter's calibration across every requirement that uses a filter.
        /// <c>REQ-DSP-023a</c> asks for 100 dB of alias rejection; the downconverter should be
        /// able to ask for 100 dB and get it.
        /// </para>
        /// </remarks>
        public const double MarginDb = 3.0;

        /// <summary>
        /// The tap count <see cref="LowPass"/> uses, always odd, <see cref="MarginDb"/> included.
        /// </summary>
        /// <param name="transitionWidth">Transition width, in cycles per sample.</param>
        /// <param name="stopbandDb">Wanted stopband attenuation, in dB.</param>
        /// <returns>An odd tap count of at least 3.</returns>
        /// <exception cref="ArgumentOutOfRangeException">A value is out of range.</exception>
        /// <remarks>
        /// Public so that a caller can find out what a design will cost before paying for it: tap
        /// count rises in proportion to the decimation factor, and a downconverter asked for an
        /// impossible span should say so rather than try to allocate the answer.
        /// </remarks>
        public static int TapCountFor(double transitionWidth, double stopbandDb)
        {
            if (!(stopbandDb > 0.0) || double.IsInfinity(stopbandDb))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(stopbandDb), stopbandDb, "A stopband attenuation must be positive.");
            }

            return LengthFor(transitionWidth, stopbandDb + MarginDb);
        }

        /// <summary>
        /// Kaiser's own length estimate, <c>N ≈ (A − 8) / (2.285 · Δω) + 1</c>, rounded up and
        /// forced odd.
        /// </summary>
        /// <param name="transitionWidth">Transition width, in cycles per sample.</param>
        /// <param name="stopbandDb">Stopband attenuation to estimate for, in dB.</param>
        /// <returns>An odd tap count of at least 3.</returns>
        /// <exception cref="ArgumentOutOfRangeException">A value is out of range.</exception>
        /// <remarks>
        /// The published fit, with no margin applied — it is the estimate, not the answer. Kept
        /// separate from <see cref="TapCountFor"/> so that <see cref="MarginDb"/> is visible as a
        /// correction to a known formula rather than buried inside a constant that no longer
        /// matches anything in the literature. The <c>+1</c> turns Kaiser's <em>order</em> into a
        /// length; forcing the result odd is what gives the filter a whole-sample group delay.
        /// </remarks>
        public static int LengthFor(double transitionWidth, double stopbandDb)
        {
            if (!(transitionWidth > 0.0) || double.IsInfinity(transitionWidth))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(transitionWidth), transitionWidth,
                    "A transition width must be positive and finite.");
            }

            if (!(stopbandDb > 0.0) || double.IsInfinity(stopbandDb))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(stopbandDb), stopbandDb, "A stopband attenuation must be positive.");
            }

            double order = (stopbandDb - 8.0) / (2.285 * 2.0 * Math.PI * transitionWidth);

            // A transition width small enough to ask for more taps than an array can hold is a
            // caller's units mistake, not a design. Saturating rather than wrapping keeps that
            // mistake visible to whoever checks the count against a limit of their own.
            if (order >= int.MaxValue - 2)
            {
                return int.MaxValue;
            }

            int length = (int)Math.Ceiling(order) + 1;

            if (length % 2 == 0)
            {
                length++;
            }

            return Math.Max(3, length);
        }

        /// <summary>
        /// The Kaiser shape parameter for a stopband attenuation.
        /// </summary>
        /// <param name="stopbandDb">Wanted attenuation, in dB.</param>
        /// <remarks>Kaiser's own three-part empirical fit, which is where these constants are from.</remarks>
        public static double Beta(double stopbandDb)
        {
            if (stopbandDb > 50.0)
            {
                return 0.1102 * (stopbandDb - 8.7);
            }

            if (stopbandDb >= 21.0)
            {
                return 0.5842 * Math.Pow(stopbandDb - 21.0, 0.4) + 0.07886 * (stopbandDb - 21.0);
            }

            return 0.0;
        }

        /// <summary>The Kaiser window at a position in [−1, 1].</summary>
        /// <param name="position">Position across the window, −1 at one end and +1 at the other.</param>
        /// <param name="beta">Shape parameter.</param>
        public static double Kaiser(double position, double beta)
        {
            double inside = 1.0 - position * position;

            if (inside <= 0.0)
            {
                return 0.0;
            }

            return BesselI0(beta * Math.Sqrt(inside)) / BesselI0(beta);
        }

        /// <summary>
        /// The modified Bessel function of the first kind, order zero.
        /// </summary>
        /// <param name="x">Argument.</param>
        /// <remarks>
        /// Summed from its power series until a term stops changing the total. At the arguments a
        /// Kaiser window uses — <c>β</c> is about 11 at 110 dB — the series converges in a few
        /// dozen terms, and a table would be one more thing to be wrong.
        /// </remarks>
        public static double BesselI0(double x)
        {
            double half = x / 2.0;
            double term = 1.0;
            double sum = 1.0;

            for (int k = 1; k < 100; k++)
            {
                term *= half / k;
                double contribution = term * term;
                sum += contribution;

                if (contribution < sum * 1e-17)
                {
                    break;
                }
            }

            return sum;
        }

        private static double Sinc(double x)
        {
            if (Math.Abs(x) < 1e-12)
            {
                return 1.0;
            }

            double piX = Math.PI * x;

            return Math.Sin(piX) / piX;
        }
    }
}
