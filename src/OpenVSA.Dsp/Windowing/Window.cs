using System;
using System.Collections.Concurrent;

namespace OpenVSA.Dsp.Windowing
{
    /// <summary>
    /// A window function evaluated at a given length, with the gain figures needed to correct a
    /// spectrum for it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Periodic (DFT-even) throughout</strong>, per <c>REQ-DSP-010a</c>: coefficients are
    /// <c>w[n] = f(n/N)</c> for <c>n = 0 … N-1</c>, never <c>f(n/(N-1))</c>. This is not a matter
    /// of taste. The ENBW figures of <c>REQ-DSP-010</c> are the periodic values; symmetric Hann
    /// gives <c>1.5 N/(N-1)</c>, which is a 0.03 % error at N = 4096 but <strong>1.6 % at
    /// N = 64</strong> — and N = 64 is a supported FFT size, so the symmetric form fails the
    /// acceptance criterion at the small end of the range.
    /// </para>
    /// <para>
    /// <see cref="Enbw"/> and <see cref="CoherentGain"/> are computed from the coefficients that
    /// were actually generated, never read from the table. That is what makes the test comparing
    /// them against the table a real check rather than a restatement of a constant.
    /// </para>
    /// <para>
    /// Instances are immutable and cached per (type, length). Windows are applied per frame and
    /// Kaiser-Bessel in particular is expensive to build, so rebuilding one per acquisition would
    /// show up against <c>REQ-NFR-003</c>.
    /// </para>
    /// </remarks>
    public sealed class Window
    {
        private static readonly ConcurrentDictionary<long, Window> Cache =
            new ConcurrentDictionary<long, Window>();

        private readonly double[] _coefficients;

        private Window(WindowType type, double[] coefficients)
        {
            _coefficients = coefficients;
            Type = type;

            // Accumulated in double per REQ-DSP-002. At N = 2^20 a single-precision sum of
            // squares would lose roughly five significant figures, which is larger than the
            // 0.1 % tolerance these figures are held to.
            double sum = 0.0;
            double sumOfSquares = 0.0;
            for (int n = 0; n < coefficients.Length; n++)
            {
                double w = coefficients[n];
                sum += w;
                sumOfSquares += w * w;
            }

            int length = coefficients.Length;
            CoherentGain = sum / length;
            Enbw = length * sumOfSquares / (sum * sum);
            NoisePowerGain = sumOfSquares / length;
        }

        /// <summary>The default window, <see cref="WindowType.FlatTop"/> (<c>REQ-DSP-010</c>).</summary>
        public const WindowType Default = WindowType.FlatTop;

        /// <summary>Which window this is.</summary>
        public WindowType Type { get; }

        /// <summary>Number of coefficients.</summary>
        public int Length => _coefficients.Length;

        /// <summary>
        /// Coherent gain, <c>Σw/N</c> — the amplitude correction for a discrete tone
        /// (<c>REQ-DSP-011</c>).
        /// </summary>
        public double CoherentGain { get; }

        /// <summary>
        /// Normalised equivalent noise bandwidth, <c>N·Σw²/(Σw)²</c>, in bins — the noise-density
        /// correction (<c>REQ-DSP-011</c>) and the RBW coupling factor (<c>REQ-DSP-020</c>).
        /// </summary>
        public double Enbw { get; }

        /// <summary>Mean square of the coefficients, <c>Σw²/N</c>. Incoherent (noise) power gain.</summary>
        public double NoisePowerGain { get; }

        /// <summary>The coefficients, in a view that cannot be written through.</summary>
        public ReadOnlySpan<double> Coefficients => new ReadOnlySpan<double>(_coefficients);

        /// <summary>
        /// Gets the window of the given type and length, building it on first use.
        /// </summary>
        /// <param name="type">Which window.</param>
        /// <param name="length">Number of coefficients; must be positive.</param>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="length"/> is not positive, or <paramref name="type"/> is not a known window.</exception>
        public static Window Get(WindowType type, int length)
        {
            if (length <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(length), length, "Window length must be positive.");
            }

            long key = ((long)type << 40) | (uint)length;
            return Cache.GetOrAdd(key, _ => new Window(type, Build(type, length)));
        }

        /// <summary>
        /// Multiplies interleaved I/Q samples in place by this window.
        /// </summary>
        /// <param name="interleaved">Interleaved I/Q, of length <c>2 × <see cref="Length"/></c>.</param>
        /// <exception cref="ArgumentException">The span is not twice the window length.</exception>
        public void ApplyTo(Span<float> interleaved)
        {
            if (interleaved.Length != _coefficients.Length * 2)
            {
                throw new ArgumentException(
                    "Expected " + (_coefficients.Length * 2) + " interleaved values for a window of length " +
                    _coefficients.Length + ", got " + interleaved.Length + ".",
                    nameof(interleaved));
            }

            double[] w = _coefficients;
            for (int n = 0; n < w.Length; n++)
            {
                float scale = (float)w[n];
                interleaved[n * 2] *= scale;
                interleaved[n * 2 + 1] *= scale;
            }
        }

        /// <summary>
        /// Multiplies interleaved I/Q samples in place by this window.
        /// </summary>
        /// <param name="interleaved">Interleaved I/Q, of length <c>2 × <see cref="Length"/></c>.</param>
        /// <exception cref="ArgumentException">The span is not twice the window length.</exception>
        public void ApplyTo(Span<double> interleaved)
        {
            if (interleaved.Length != _coefficients.Length * 2)
            {
                throw new ArgumentException(
                    "Expected " + (_coefficients.Length * 2) + " interleaved values for a window of length " +
                    _coefficients.Length + ", got " + interleaved.Length + ".",
                    nameof(interleaved));
            }

            double[] w = _coefficients;
            for (int n = 0; n < w.Length; n++)
            {
                double scale = w[n];
                interleaved[n * 2] *= scale;
                interleaved[n * 2 + 1] *= scale;
            }
        }

        // ---- Definitions ----------------------------------------------------------------------

        /// <summary>
        /// Cosine-sum coefficients, normalised in <see cref="CosineSum"/> so the window peaks at 1.
        /// </summary>
        /// <remarks>
        /// Signs are carried in the coefficients: <c>w[n] = Σ c[k]·cos(2πkn/N)</c>.
        /// </remarks>
        private static readonly double[] UniformCoefficients = { 1.0 };

        private static readonly double[] HannCoefficients = { 0.5, -0.5 };

        /// <summary>Blackman-Harris, 4-term minimum. The classic set; ENBW 2.004353, sidelobe −92.0 dB.</summary>
        private static readonly double[] BlackmanHarrisCoefficients =
            { 0.35875, -0.48829, 0.14128, -0.01168 };

        /// <summary>
        /// Flat Top, chosen to give the ENBW of 3.8194 that <c>REQ-DSP-010</c> tabulates.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <strong>Provenance, because this is the one window here that is not simply a published
        /// set.</strong> The reference product documents ENBW 3.8194 but not its coefficients, and
        /// no published flat top has that ENBW: the nearest are Heinzel's HFT95 (3.8112) and
        /// HFT90D (3.8832), and the widely used SRS/MATLAB flat top is 3.7702 — all outside the
        /// 0.1 % tolerance the acceptance criterion sets. These coefficients are the convex blend
        /// of HFT95 and HFT90D at t = 0.115777, solved so the ENBW is 3.819400 exactly.
        /// </para>
        /// <para>
        /// The result is a sound flat top in its own right and not a fudge to pass a test: peak
        /// sidelobe −94.1 dB and amplitude flatness 0.0039 dB over a bin, both between its two
        /// parents. ENBW is the behaviourally visible quantity — it sets RBW through
        /// <c>REQ-DSP-020</c> — so matching it exactly is what "behaviourally equivalent" means
        /// here.
        /// </para>
        /// </remarks>
        private static readonly double[] FlatTopCoefficients =
            { 1.0, -1.938831818206, 1.308664778222, -0.407224690897, 0.035996250862 };

        /// <summary>Kaiser-Bessel shape parameter. The specification's "πα = 11.9" is β directly.</summary>
        /// <remarks>
        /// Verified rather than assumed: β = 11.9 reproduces <em>both</em> tabulated figures —
        /// ENBW 2.001266 against 2.0013, and peak sidelobe −89.09 dB against −89.1 dB. Reading it
        /// as α = 11.9/π instead gives ENBW 1.22, which is not close. The two independent figures
        /// agreeing is what settles it.
        /// </remarks>
        private const double KaiserBeta = 11.9;

        /// <summary>Gaussian σ, as the specification states it (α = 3.58 ⇒ σ = 0.5/α = 0.1397).</summary>
        /// <remarks>
        /// Taken as given rather than solved: σ = 0.1397 yields ENBW 2.020682 against the
        /// tabulated 2.0212 (0.026 % low, within tolerance) and sidelobe −73.46 dB against −73.5.
        /// </remarks>
        private const double GaussianSigma = 0.1397;

        /// <summary>Gaussian Top σ, solved for the tabulated ENBW of 2.2153.</summary>
        /// <remarks>
        /// The reference product does not document what "Gaussian Top" is beyond the ENBW figure
        /// and "high dynamic range". A Gaussian narrower in time than <see cref="GaussianSigma"/>
        /// fits both: this σ gives ENBW 2.215300 and a peak sidelobe of −85.2 dB, appreciably
        /// better than the −73.5 dB of the plain Gaussian, which is the claim being made about it.
        /// No sidelobe figure is tabulated for this window, so only the ENBW is binding.
        /// </remarks>
        private const double GaussianTopSigma = 0.127361329;

        private static double[] Build(WindowType type, int length)
        {
            switch (type)
            {
                case WindowType.Uniform: return CosineSum(UniformCoefficients, length);
                case WindowType.Hann: return CosineSum(HannCoefficients, length);
                case WindowType.GaussianTop: return GaussianWindow(length, GaussianTopSigma);
                case WindowType.FlatTop: return CosineSum(FlatTopCoefficients, length);
                case WindowType.BlackmanHarris: return CosineSum(BlackmanHarrisCoefficients, length);
                case WindowType.KaiserBessel: return KaiserWindow(length, KaiserBeta);
                case WindowType.Gaussian: return GaussianWindow(length, GaussianSigma);
                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(type), type, "Not a known window function.");
            }
        }

        /// <summary>
        /// Evaluates <c>w[n] = Σ c[k]·cos(2πkn/N)</c>, normalised to a peak of 1.
        /// </summary>
        /// <remarks>
        /// The peak is at <c>n = N/2</c>, where <c>cos(πk) = (−1)^k</c>; since the coefficients
        /// alternate in sign, every term there contributes <c>|c[k]|</c> and the peak is
        /// <c>Σ|c[k]|</c>. Normalising by that is a no-op for the published sets, whose
        /// coefficients already sum to 1, and is what scales the flat top — quoted in the
        /// literature with <c>c0 = 1</c> — onto the same footing as the rest.
        /// </remarks>
        private static double[] CosineSum(double[] coefficients, int length)
        {
            double peak = 0.0;
            for (int k = 0; k < coefficients.Length; k++)
            {
                peak += Math.Abs(coefficients[k]);
            }

            var w = new double[length];
            for (int n = 0; n < length; n++)
            {
                double sum = 0.0;
                for (int k = 0; k < coefficients.Length; k++)
                {
                    // The angle is formed from n and k directly rather than by accumulating a
                    // phase increment; accumulation drifts by ~N·eps and would show up in the
                    // ENBW at N = 2^20.
                    sum += coefficients[k] * Math.Cos(2.0 * Math.PI * k * n / length);
                }

                w[n] = sum / peak;
            }

            return w;
        }

        /// <summary>Periodic Gaussian of normalised standard deviation <paramref name="sigma"/>, peaking at 1.</summary>
        private static double[] GaussianWindow(int length, double sigma)
        {
            var w = new double[length];
            double half = length / 2.0;
            double denominator = sigma * length;

            for (int n = 0; n < length; n++)
            {
                double x = (n - half) / denominator;
                w[n] = Math.Exp(-0.5 * x * x);
            }

            return w;
        }

        /// <summary>Periodic Kaiser-Bessel of shape <paramref name="beta"/>, peaking at 1.</summary>
        private static double[] KaiserWindow(int length, double beta)
        {
            var w = new double[length];
            double half = length / 2.0;
            double normalisation = BesselI0(beta);

            for (int n = 0; n < length; n++)
            {
                double x = (n - half) / half;
                double radicand = 1.0 - x * x;

                // n = 0 gives x = -1 exactly, so the radicand is 0; clamping guards only against
                // a negative from rounding, which would make the square root NaN.
                w[n] = BesselI0(beta * Math.Sqrt(radicand > 0.0 ? radicand : 0.0)) / normalisation;
            }

            return w;
        }

        /// <summary>Modified Bessel function of the first kind, order zero.</summary>
        /// <remarks>
        /// The ascending series, which converges quickly and to full double precision for the
        /// arguments a window needs (0 … β). Terms are formed by ratio from their predecessor
        /// rather than as factorials, which would overflow long before the series terminates.
        /// </remarks>
        private static double BesselI0(double x)
        {
            double sum = 1.0;
            double term = 1.0;
            double halfXSquared = 0.25 * x * x;

            for (int k = 1; k < 100; k++)
            {
                term *= halfXSquared / (k * (double)k);
                sum += term;

                if (term < 1e-18 * sum)
                {
                    break;
                }
            }

            return sum;
        }
    }
}
