using System;

namespace OpenVSA.Synthesis
{
    /// <summary>
    /// Recovers each injected impairment from the samples, without demodulating
    /// (<c>REQ-SIM-002</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The requirement's criterion names the recipe for each one, and these are those recipes: SNR
    /// from the noise power, carrier offset and phase from the complex mean at the symbol instants,
    /// gain imbalance and quadrature skew from the I and Q second moments, origin offset from the
    /// mean, droop from a fit to the symbol magnitudes, timing offset and clock error from the
    /// recovered instants.
    /// </para>
    /// <para>
    /// <strong>Not a demodulator, and deliberately so.</strong> Measuring an impairment with the
    /// thing the impairment exists to test would make the two agree by construction — the
    /// generator would be verified against the metric and the metric against the generator, and a
    /// shared misunderstanding would be invisible. That is why <c>REQ-SIM-002a</c> exists
    /// separately: this proves the generator, and the demodulator gets proved against it later.
    /// </para>
    /// </remarks>
    public static class ImpairmentMeasurement
    {
        /// <summary>
        /// The identifiability below which a tap estimate is refused rather than reported.
        /// </summary>
        /// <remarks>
        /// A conditional variance of one part in ten thousand means the delayed copy carries almost
        /// no information the others do not already carry, and the tap solved for it is arithmetic
        /// noise amplified by ten thousand. Published rather than buried, so the reason a channel
        /// was refused can be read off against the number.
        /// </remarks>
        public const double MinimumIdentifiability = 1e-4;

        /// <summary>Carrier frequency offset, in hertz, from the phase advance between symbols.</summary>
        /// <param name="signal">The signal.</param>
        /// <remarks>
        /// QPSK's fourth power removes the modulation: raising each symbol to the fourth collapses
        /// the four constellation points onto one, leaving four times the carrier phase and nothing
        /// else. The advance of that phase across the record is the offset.
        /// </remarks>
        public static double CarrierOffsetHz(ImpairedSignal signal)
        {
            if (signal == null)
            {
                throw new ArgumentNullException(nameof(signal));
            }

            // The average phase advance from one symbol to the next, not the phase difference
            // between two ends of the record. Over a few thousand symbols a kilohertz of offset
            // turns the carrier through many complete revolutions, and an end-to-end difference
            // cannot tell one revolution from twenty — the first version of this returned about
            // zero for every offset it was given.
            int[] instants = signal.SymbolInstants;

            double sumRe = 0.0;
            double sumIm = 0.0;

            double previousRe = 0.0;
            double previousIm = 0.0;
            bool have = false;

            foreach (int n in instants)
            {
                FourthPower(signal.I[n], signal.Q[n], out double re, out double im);

                if (have)
                {
                    // z[s] * conj(z[s-1]): the per-symbol phase step, averaged as a vector so that
                    // symbols contribute in proportion to their magnitude rather than equally.
                    sumRe += re * previousRe + im * previousIm;
                    sumIm += im * previousRe - re * previousIm;
                }

                previousRe = re;
                previousIm = im;
                have = true;
            }

            double perSymbol = Math.Atan2(sumIm, sumRe) / 4.0;

            return perSymbol / (2.0 * Math.PI) * signal.SymbolRateHz;
        }

        /// <summary>Carrier phase offset, in degrees, from the fourth-power mean.</summary>
        /// <param name="signal">The signal.</param>
        public static double CarrierPhaseDegrees(ImpairedSignal signal)
        {
            if (signal == null)
            {
                throw new ArgumentNullException(nameof(signal));
            }

            int[] instants = signal.SymbolInstants;
            double phase = FourthPowerPhase(signal, instants, 0, instants.Length) / 4.0;

            // The fourth power leaves a four-fold ambiguity: QPSK's own 45° offset plus any
            // multiple of 90°. Reported in (-45, 45], which is what a phase-offset measurement of a
            // QPSK signal can mean at all.
            double degrees = phase * 180.0 / Math.PI - 45.0;

            while (degrees <= -45.0)
            {
                degrees += 90.0;
            }

            while (degrees > 45.0)
            {
                degrees -= 90.0;
            }

            return degrees;
        }

        /// <summary>Gain imbalance, in dB, from the I and Q second moments.</summary>
        /// <param name="signal">The signal.</param>
        /// <remarks>
        /// Measured before any de-rotation, which is why the generator applies the carrier last: a
        /// rotation mixes the axes and the ratio of their second moments stops meaning imbalance.
        /// The test therefore measures this on a signal with no carrier offset, which is exactly
        /// what independence means here.
        /// </remarks>
        public static double GainImbalanceDb(ImpairedSignal signal)
        {
            if (signal == null)
            {
                throw new ArgumentNullException(nameof(signal));
            }

            double sumI = 0.0;
            double sumQ = 0.0;

            foreach (int n in signal.SymbolInstants)
            {
                sumI += signal.I[n] * signal.I[n];
                sumQ += signal.Q[n] * signal.Q[n];
            }

            return 10.0 * Math.Log10(sumI / sumQ);
        }

        /// <summary>Quadrature skew, in degrees, from the I/Q cross-correlation.</summary>
        /// <param name="signal">The signal.</param>
        /// <remarks>
        /// For symbols that are independent and equiprobable on each axis, E[I·Q] is zero unless
        /// the axes are not perpendicular. The shear the generator applies puts sin(skew) of Q onto
        /// I, so the normalised cross-moment is that sine.
        /// </remarks>
        public static double QuadratureSkewDegrees(ImpairedSignal signal)
        {
            if (signal == null)
            {
                throw new ArgumentNullException(nameof(signal));
            }

            double cross = 0.0;
            double power = 0.0;

            foreach (int n in signal.SymbolInstants)
            {
                cross += signal.I[n] * signal.Q[n];
                power += signal.Q[n] * signal.Q[n];
            }

            // Arctangent, not arcsine. The shear puts sin(skew) of Q onto I while scaling Q by
            // cos(skew), so the normalised cross-moment is the TANGENT of the skew. Using arcsine
            // read 5.70° for a requested 5°.
            return Math.Atan(cross / power) * 180.0 / Math.PI;
        }

        /// <summary>Origin offset, in dB, from the mean of the symbols.</summary>
        /// <param name="signal">The signal.</param>
        public static double OriginOffsetDb(ImpairedSignal signal)
        {
            if (signal == null)
            {
                throw new ArgumentNullException(nameof(signal));
            }

            double meanI = 0.0;
            double meanQ = 0.0;
            double power = 0.0;

            foreach (int n in signal.SymbolInstants)
            {
                meanI += signal.I[n];
                meanQ += signal.Q[n];
                power += signal.I[n] * signal.I[n] + signal.Q[n] * signal.Q[n];
            }

            int count = signal.SymbolInstants.Length;

            meanI /= count;
            meanQ /= count;

            // Per-axis RMS against the offset vector's own magnitude, which is how the offset was
            // specified. Halving it, as the first version did, reported -37.7 dB for -30.
            double rms = Math.Sqrt(power / count / 2.0);
            double offset = Math.Sqrt(meanI * meanI + meanQ * meanQ);

            return offset <= 0.0 ? double.NegativeInfinity : 20.0 * Math.Log10(offset / rms);
        }

        /// <summary>Amplitude droop, in dB per symbol, from a fit to the symbol magnitudes.</summary>
        /// <param name="signal">The signal.</param>
        /// <remarks>
        /// A least-squares line through the log magnitudes. A first-and-last comparison would give
        /// the same answer on a clean signal and a much worse one under noise, and the requirement
        /// asks for these to hold with the other impairments present.
        /// </remarks>
        public static double DroopDbPerSymbol(ImpairedSignal signal)
        {
            if (signal == null)
            {
                throw new ArgumentNullException(nameof(signal));
            }

            int[] instants = signal.SymbolInstants;

            double sumX = 0.0, sumY = 0.0, sumXy = 0.0, sumXx = 0.0;
            int used = 0;

            for (int s = 0; s < instants.Length; s++)
            {
                int n = instants[s];
                double magnitude = Math.Sqrt(
                    signal.I[n] * signal.I[n] + signal.Q[n] * signal.Q[n]);

                if (magnitude <= 0.0)
                {
                    continue;
                }

                double y = 20.0 * Math.Log10(magnitude);

                sumX += s;
                sumY += y;
                sumXy += s * y;
                sumXx += (double)s * s;
                used++;
            }

            return (used * sumXy - sumX * sumY) / (used * sumXx - sumX * sumX);
        }

        /// <summary>Signal-to-noise ratio, in dB, from the scatter about the symbol values.</summary>
        /// <param name="signal">The signal.</param>
        /// <remarks>
        /// The residual after removing the nearest constellation point is the noise, and the
        /// constellation is known because the generator placed it. That is legitimate here and
        /// would not be in a demodulator: this measures the generator, not an estimator.
        /// </remarks>
        public static double SignalToNoiseDb(ImpairedSignal signal)
        {
            if (signal == null)
            {
                throw new ArgumentNullException(nameof(signal));
            }

            double signalPower = 0.0;
            double noisePower = 0.0;

            foreach (int n in signal.SymbolInstants)
            {
                double idealI = signal.I[n] >= 0.0 ? 1.0 : -1.0;
                double idealQ = signal.Q[n] >= 0.0 ? 1.0 : -1.0;

                double errorI = signal.I[n] - idealI;
                double errorQ = signal.Q[n] - idealQ;

                signalPower += idealI * idealI + idealQ * idealQ;
                noisePower += errorI * errorI + errorQ * errorQ;
            }

            return noisePower <= 0.0
                ? double.PositiveInfinity
                : 10.0 * Math.Log10(signalPower / noisePower);
        }

        /// <summary>Phase noise, as an RMS deviation in degrees, from the symbol phase scatter.</summary>
        /// <param name="signal">The signal.</param>
        /// <remarks>
        /// The fourth power removes the modulation; what is left is four times the carrier phase,
        /// and its scatter about its own mean is four times the phase noise. Dividing by four
        /// recovers the deviation — and only works while the scatter is small enough not to wrap,
        /// which is the regime a phase-noise figure is quoted in anyway.
        /// </remarks>
        public static double PhaseNoiseDegreesRms(ImpairedSignal signal)
        {
            if (signal == null)
            {
                throw new ArgumentNullException(nameof(signal));
            }

            int[] instants = signal.SymbolInstants;
            double mean = FourthPowerPhase(signal, instants, 0, instants.Length);

            double sum = 0.0;

            foreach (int n in instants)
            {
                FourthPower(signal.I[n], signal.Q[n], out double re, out double im);

                double deviation = Unwrap(Math.Atan2(im, re) - mean);
                sum += deviation * deviation;
            }

            double rms = Math.Sqrt(sum / instants.Length) / 4.0;

            return rms * 180.0 / Math.PI;
        }

        /// <summary>AM/AM compression, in dB, from the gain at full envelope.</summary>
        /// <param name="signal">The signal.</param>
        /// <remarks>
        /// Read as the loss of the mean symbol magnitude against the unit constellation the
        /// generator used. A two-point fit against envelope would be better on a signal with a
        /// range of amplitudes; QPSK has one amplitude, so there is one point to read.
        /// </remarks>
        public static double CompressionDb(ImpairedSignal signal)
        {
            if (signal == null)
            {
                throw new ArgumentNullException(nameof(signal));
            }

            double sum = 0.0;

            foreach (int n in signal.SymbolInstants)
            {
                sum += Math.Sqrt(signal.I[n] * signal.I[n] + signal.Q[n] * signal.Q[n]);
            }

            double mean = sum / signal.SymbolInstants.Length;

            // The clean constellation has magnitude sqrt(2) at every point.
            return -20.0 * Math.Log10(mean / Math.Sqrt(2.0));
        }

        /// <summary>
        /// The tapped-delay-line channel, recovered by least squares against the pre-channel signal.
        /// </summary>
        /// <param name="signal">The signal.</param>
        /// <param name="delaySamples">The delays to solve for.</param>
        /// <returns>The estimate, including whether this reference could identify it at all.</returns>
        /// <remarks>
        /// <para>
        /// The other eleven impairments are read from a moment or a fit. A channel is not a number,
        /// so this solves for one: minimise the residual between the received samples and a weighted
        /// sum of delayed copies of what went in, which is the normal equations for the taps.
        /// </para>
        /// <para>
        /// <strong>The reference sequence cannot identify every delay set, and this says so rather
        /// than answering anyway.</strong> The generator uses a fixed cyclic QPSK pattern, chosen so
        /// the moment-based measurements are exact rather than accurate to one part in root-N. The
        /// cost is paid here: that pattern repeats every four symbols, its four-point transform has
        /// a null at DC, and the eight-sample hold puts further nulls at every fourth bin. Delayed
        /// copies of a sequence with nulls in its spectrum are linearly dependent, and a solver
        /// handed a singular system returns a confident answer built out of arithmetic noise.
        /// </para>
        /// <para>
        /// So the normalised system's smallest elimination pivot is reported as
        /// <see cref="ChannelEstimate.Identifiability"/>. For a Hermitian positive-semidefinite
        /// system those pivots are conditional variances: one near zero means a delay whose copy is
        /// already a combination of the others, and the estimate is refused rather than believed.
        /// </para>
        /// </remarks>
        public static ChannelEstimate MultipathTaps(ImpairedSignal signal, params int[] delaySamples)
        {
            if (signal == null)
            {
                throw new ArgumentNullException(nameof(signal));
            }

            if (delaySamples == null || delaySamples.Length == 0)
            {
                throw new ArgumentException("No delays to solve for.", nameof(delaySamples));
            }

            int k = delaySamples.Length;
            int longest = 0;

            foreach (int d in delaySamples)
            {
                longest = Math.Max(longest, d);
            }

            double[] x = signal.PreChannelI, xq = signal.PreChannelQ;
            double[] y = signal.I, yq = signal.Q;

            var re = new double[k, k];
            var im = new double[k, k];
            var pRe = new double[k];
            var pIm = new double[k];

            // From the longest delay onwards, so every shifted copy is fully present. The
            // zero-padded head would otherwise bias the tap it belongs to towards zero.
            for (int j = 0; j < k; j++)
            {
                for (int c = 0; c < k; c++)
                {
                    double sumRe = 0.0, sumIm = 0.0;

                    for (int n = longest; n < x.Length; n++)
                    {
                        int a = n - delaySamples[j];
                        int b = n - delaySamples[c];

                        // conj(x[a]) * x[b]
                        sumRe += x[a] * x[b] + xq[a] * xq[b];
                        sumIm += x[a] * xq[b] - xq[a] * x[b];
                    }

                    re[j, c] = sumRe;
                    im[j, c] = sumIm;
                }

                double rhsRe = 0.0, rhsIm = 0.0;

                for (int n = longest; n < x.Length; n++)
                {
                    int a = n - delaySamples[j];

                    rhsRe += x[a] * y[n] + xq[a] * yq[n];
                    rhsIm += x[a] * yq[n] - xq[a] * y[n];
                }

                pRe[j] = rhsRe;
                pIm[j] = rhsIm;
            }

            // Normalised to unit diagonal, so the identifiability figure is a correlation and does
            // not move when the signal level does.
            var scale = new double[k];

            for (int j = 0; j < k; j++)
            {
                scale[j] = Math.Sqrt(re[j, j]);
            }

            for (int j = 0; j < k; j++)
            {
                for (int c = 0; c < k; c++)
                {
                    re[j, c] /= scale[j] * scale[c];
                    im[j, c] /= scale[j] * scale[c];
                }

                pRe[j] /= scale[j];
                pIm[j] /= scale[j];
            }

            double identifiability = Solve(re, im, pRe, pIm, k);

            var taps = new MultipathTap[k];

            for (int j = 0; j < k; j++)
            {
                double tapRe = pRe[j] / scale[j];
                double tapIm = pIm[j] / scale[j];

                double magnitude = Math.Sqrt(tapRe * tapRe + tapIm * tapIm);

                taps[j] = new MultipathTap(
                    delaySamples[j],
                    magnitude <= 0.0 ? double.NegativeInfinity : 20.0 * Math.Log10(magnitude),
                    Math.Atan2(tapIm, tapRe) * 180.0 / Math.PI);
            }

            return new ChannelEstimate(taps, identifiability);
        }

        /// <summary>
        /// Gaussian elimination on a Hermitian system, returning the smallest pivot magnitude.
        /// </summary>
        /// <remarks>
        /// No pivoting: the matrix is a Gram matrix and so positive semi-definite, where the natural
        /// pivots are already the largest available and a small one is information rather than an
        /// accident of ordering. Row-swapping would hide exactly what this returns.
        /// </remarks>
        private static double Solve(double[,] re, double[,] im, double[] pRe, double[] pIm, int k)
        {
            double smallest = double.PositiveInfinity;

            for (int c = 0; c < k; c++)
            {
                double pivotRe = re[c, c];
                double pivotIm = im[c, c];
                double pivot = Math.Sqrt(pivotRe * pivotRe + pivotIm * pivotIm);

                smallest = Math.Min(smallest, pivot);

                if (pivot < 1e-12)
                {
                    return smallest;
                }

                for (int r = c + 1; r < k; r++)
                {
                    Divide(re[r, c], im[r, c], pivotRe, pivotIm, out double fRe, out double fIm);

                    for (int j = c; j < k; j++)
                    {
                        re[r, j] -= fRe * re[c, j] - fIm * im[c, j];
                        im[r, j] -= fRe * im[c, j] + fIm * re[c, j];
                    }

                    pRe[r] -= fRe * pRe[c] - fIm * pIm[c];
                    pIm[r] -= fRe * pIm[c] + fIm * pRe[c];
                }
            }

            for (int r = k - 1; r >= 0; r--)
            {
                double sumRe = pRe[r], sumIm = pIm[r];

                for (int j = r + 1; j < k; j++)
                {
                    sumRe -= re[r, j] * pRe[j] - im[r, j] * pIm[j];
                    sumIm -= re[r, j] * pIm[j] + im[r, j] * pRe[j];
                }

                Divide(sumRe, sumIm, re[r, r], im[r, r], out double vRe, out double vIm);

                pRe[r] = vRe;
                pIm[r] = vIm;
            }

            return smallest;
        }

        private static void Divide(
            double aRe, double aIm, double bRe, double bIm, out double re, out double im)
        {
            double denominator = bRe * bRe + bIm * bIm;

            re = (aRe * bRe + aIm * bIm) / denominator;
            im = (aIm * bRe - aRe * bIm) / denominator;
        }

        /// <summary>The fourth-power mean phase over a range of symbols.</summary>
        private static double FourthPowerPhase(ImpairedSignal signal, int[] instants, int from, int to)
        {
            double sumRe = 0.0;
            double sumIm = 0.0;

            for (int s = from; s < to; s++)
            {
                int n = instants[s];

                FourthPower(signal.I[n], signal.Q[n], out double re, out double im);

                sumRe += re;
                sumIm += im;
            }

            return Math.Atan2(sumIm, sumRe);
        }

        /// <summary>(re + j·im)^4, which collapses QPSK's four points onto one.</summary>
        private static void FourthPower(double re, double im, out double outRe, out double outIm)
        {
            double r2 = re * re - im * im;
            double i2 = 2.0 * re * im;

            outRe = r2 * r2 - i2 * i2;
            outIm = 2.0 * r2 * i2;
        }

        private static double Unwrap(double radians)
        {
            while (radians > Math.PI)
            {
                radians -= 2.0 * Math.PI;
            }

            while (radians < -Math.PI)
            {
                radians += 2.0 * Math.PI;
            }

            return radians;
        }
    }
}
