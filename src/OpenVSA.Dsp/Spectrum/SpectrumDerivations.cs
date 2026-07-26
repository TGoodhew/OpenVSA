using System;
using OpenVSA.Dsp.Fft;

namespace OpenVSA.Dsp.Spectrum
{
    /// <summary>
    /// Power spectral density, as a trace data type (<c>REQ-DSP-040</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The noise bandwidth of a bin is <c>ENBW × bin width</c>, not the bin width.</strong>
    /// That single factor is the whole of the difference between a density that reads correctly
    /// and one that is out by the window's noise bandwidth — 5.8 dB under the default Flat Top,
    /// which is large enough to matter and small enough to be mistaken for a calibration problem.
    /// </para>
    /// <para>
    /// Density is the right reading for noise and the wrong one for a discrete tone, which is the
    /// distinction <c>REQ-DSP-011</c> draws between the ENBW and coherent-gain corrections. This
    /// applies the noise correction unconditionally, because a trace's data type is what declares
    /// which of the two the user asked for.
    /// </para>
    /// </remarks>
    public static class PowerSpectralDensity
    {
        /// <summary>
        /// Computes power spectral density, in dBm per hertz.
        /// </summary>
        /// <param name="frame">The spectrum.</param>
        /// <param name="destination">Receives <c>frame.PointCount</c> values.</param>
        /// <exception cref="ArgumentNullException"><paramref name="frame"/> is null.</exception>
        /// <exception cref="ArgumentException">The destination is the wrong length.</exception>
        public static void Compute(SpectrumFrame frame, Span<float> destination)
        {
            if (frame == null)
            {
                throw new ArgumentNullException(nameof(frame));
            }

            if (destination.Length != frame.PointCount)
            {
                throw new ArgumentException(
                    "Expected " + frame.PointCount + " values, got " + destination.Length + ".",
                    nameof(destination));
            }

            double noiseBandwidth = NoiseBandwidthHz(frame);

            if (!(noiseBandwidth > 0.0))
            {
                for (int i = 0; i < destination.Length; i++)
                {
                    destination[i] = float.NaN;
                }

                return;
            }

            ReadOnlySpan<float> complex = frame.Complex;

            for (int i = 0; i < frame.PointCount; i++)
            {
                double re = complex[i * 2];
                double im = complex[i * 2 + 1];

                if (double.IsNaN(re) || double.IsNaN(im))
                {
                    destination[i] = float.NaN;
                    continue;
                }

                destination[i] = (float)frame.Scale.VoltsSquaredToDbm(
                    (re * re + im * im) / noiseBandwidth);
            }
        }

        /// <summary>The noise bandwidth of one bin, in hertz: <c>ENBW × bin width</c>.</summary>
        /// <param name="frame">The spectrum.</param>
        /// <exception cref="ArgumentNullException"><paramref name="frame"/> is null.</exception>
        public static double NoiseBandwidthHz(SpectrumFrame frame)
        {
            if (frame == null)
            {
                throw new ArgumentNullException(nameof(frame));
            }

            double enbw = frame.EquivalentNoiseBandwidthBins > 0.0
                ? frame.EquivalentNoiseBandwidthBins
                : 1.0;

            return enbw * frame.BinWidthHz;
        }
    }

    /// <summary>
    /// Autocorrelation, as a trace data type (<c>REQ-DSP-040</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Computed as the inverse transform of the power spectrum, by the Wiener–Khinchin theorem,
    /// rather than by correlating the time record with shifted copies of itself. The transform
    /// route is <c>N log N</c> where the direct one is <c>N²</c>, and at the point counts this
    /// product works with that is the difference between a trace and a wait.
    /// </para>
    /// <para>
    /// Normalised so that zero lag is 1, which is what makes the result a correlation coefficient
    /// and lets the white-noise criterion — a unit impulse at zero lag — be stated without
    /// reference to level.
    /// </para>
    /// </remarks>
    public static class Autocorrelation
    {
        /// <summary>
        /// Computes the normalised autocorrelation of a spectrum.
        /// </summary>
        /// <param name="frame">The spectrum.</param>
        /// <param name="provider">FFT provider, or <c>null</c> for the configured one.</param>
        /// <param name="destination">
        /// Receives one value per lag. Its length must be a power of two and no greater than the
        /// frame's point count; lag 0 is at index 0.
        /// </param>
        /// <returns>The lag spacing, in seconds.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="frame"/> is null.</exception>
        /// <exception cref="ArgumentException">The destination length is unusable.</exception>
        public static double Compute(
            SpectrumFrame frame, IFftProvider provider, Span<float> destination)
        {
            if (frame == null)
            {
                throw new ArgumentNullException(nameof(frame));
            }

            IFftProvider fft = provider ?? FftProviders.Active;
            int lags = destination.Length;

            if (lags < 2 || !fft.SupportsLength(lags) || lags > frame.PointCount)
            {
                throw new ArgumentException(
                    "The destination must be a power of two between 2 and the frame's " +
                    frame.PointCount + " points; got " + lags + ".",
                    nameof(destination));
            }

            // The power spectrum, resampled onto the transform length. Real and even, so its
            // inverse transform is real and even too - any imaginary part that comes back is
            // arithmetic noise rather than signal.
            var scratch = new double[lags * 2];
            ReadOnlySpan<float> complex = frame.Complex;

            for (int i = 0; i < lags; i++)
            {
                int source = (int)((long)i * frame.PointCount / lags);
                double re = complex[source * 2];
                double im = complex[source * 2 + 1];

                scratch[i * 2] = double.IsNaN(re) || double.IsNaN(im) ? 0.0 : re * re + im * im;
                scratch[i * 2 + 1] = 0.0;
            }

            fft.Inverse(new Span<double>(scratch));

            double zeroLag = scratch[0];

            if (!(Math.Abs(zeroLag) > 0.0))
            {
                for (int i = 0; i < lags; i++)
                {
                    destination[i] = 0.0f;
                }

                return 0.0;
            }

            for (int i = 0; i < lags; i++)
            {
                destination[i] = (float)(scratch[i * 2] / zeroLag);
            }

            // Lag spacing is the reciprocal of the frequency span the transform covered.
            double spanHz = frame.BinWidthHz * lags;
            return spanHz > 0.0 ? 1.0 / spanHz : 0.0;
        }
    }
}
