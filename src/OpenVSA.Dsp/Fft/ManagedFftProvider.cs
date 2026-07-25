using System;
using System.Collections.Concurrent;

namespace OpenVSA.Dsp.Fft
{
    /// <summary>
    /// The shipped default: a fully managed, double-precision radix-2 FFT.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the reference provider of <c>REQ-NFR-004a</c>, held to Parseval within 1e-12
    /// relative on a 2²⁰-point transform. Being managed, it carries no copyleft obligation and no
    /// native dependency, so it is what runs in CI and what ships when no native provider is
    /// configured.
    /// </para>
    /// <para>
    /// Power-of-two lengths only. Arbitrary lengths would need Bluestein's algorithm; nothing in
    /// the specification asks for one, and a provider that silently zero-padded to the next power
    /// of two would change the resolution bandwidth without saying so.
    /// </para>
    /// </remarks>
    [FftProvider("Managed")]
    public sealed class ManagedFftProvider : IFftProvider
    {
        /// <summary>Twiddle factors per transform length, built on first use.</summary>
        /// <remarks>
        /// Cached because a running measurement transforms the same length over and over, and
        /// rebuilding a 2²⁰ table per frame would cost more than the transform. A measurement uses
        /// one or two lengths at a time, so the cache does not grow without bound in practice.
        /// </remarks>
        private static readonly ConcurrentDictionary<int, double[]> Twiddles =
            new ConcurrentDictionary<int, double[]>();

        /// <inheritdoc />
        public string Name => "Managed";

        /// <inheritdoc />
        public bool IsNativeAccelerated => false;

        /// <inheritdoc />
        public int SignificandBits => 53;

        /// <inheritdoc />
        public bool SupportsLength(int length) => length > 0 && (length & (length - 1)) == 0;

        /// <inheritdoc />
        public void Forward(Span<double> interleaved)
        {
            Transform(interleaved, inverse: false);
        }

        /// <inheritdoc />
        public void Inverse(Span<double> interleaved)
        {
            Transform(interleaved, inverse: true);
        }

        private void Transform(Span<double> interleaved, bool inverse)
        {
            int n = interleaved.Length / 2;

            if (interleaved.Length % 2 != 0 || !SupportsLength(n))
            {
                throw new ArgumentException(
                    "Length must be twice a positive power of two; got " + interleaved.Length +
                    " values (" + n + " complex points).",
                    nameof(interleaved));
            }

            if (n == 1)
            {
                return;
            }

            // The inverse is the forward transform between two conjugations, then scaled. Deriving
            // it this way rather than writing a second kernel means the two directions cannot
            // drift apart, which is the usual source of a sign error that only shows up as a
            // spectrum mirrored about DC.
            if (inverse)
            {
                Conjugate(interleaved, n);
            }

            BitReverse(interleaved, n);
            Butterflies(interleaved, n);

            if (inverse)
            {
                Conjugate(interleaved, n);
                double scale = 1.0 / n;
                for (int i = 0; i < interleaved.Length; i++)
                {
                    interleaved[i] *= scale;
                }
            }
        }

        private static void Conjugate(Span<double> data, int n)
        {
            for (int i = 0; i < n; i++)
            {
                data[i * 2 + 1] = -data[i * 2 + 1];
            }
        }

        private static void BitReverse(Span<double> data, int n)
        {
            for (int i = 1, j = 0; i < n; i++)
            {
                int bit = n >> 1;
                for (; (j & bit) != 0; bit >>= 1)
                {
                    j ^= bit;
                }

                j ^= bit;

                if (i < j)
                {
                    int a = i * 2;
                    int b = j * 2;

                    double tempRe = data[a];
                    double tempIm = data[a + 1];
                    data[a] = data[b];
                    data[a + 1] = data[b + 1];
                    data[b] = tempRe;
                    data[b + 1] = tempIm;
                }
            }
        }

        private static void Butterflies(Span<double> data, int n)
        {
            double[] twiddles = GetTwiddles(n);

            for (int length = 2; length <= n; length <<= 1)
            {
                int half = length >> 1;
                int stride = n / length;

                for (int start = 0; start < n; start += length)
                {
                    for (int j = 0; j < half; j++)
                    {
                        int t = (j * stride) * 2;
                        double wRe = twiddles[t];
                        double wIm = twiddles[t + 1];

                        int upper = (start + j) * 2;
                        int lower = (start + j + half) * 2;

                        double lowerRe = data[lower];
                        double lowerIm = data[lower + 1];

                        double productRe = lowerRe * wRe - lowerIm * wIm;
                        double productIm = lowerRe * wIm + lowerIm * wRe;

                        double upperRe = data[upper];
                        double upperIm = data[upper + 1];

                        data[upper] = upperRe + productRe;
                        data[upper + 1] = upperIm + productIm;
                        data[lower] = upperRe - productRe;
                        data[lower + 1] = upperIm - productIm;
                    }
                }
            }
        }

        /// <summary>Interleaved <c>e^(−2πik/N)</c> for <c>k = 0 … N/2-1</c>.</summary>
        /// <remarks>
        /// Each factor is evaluated from its own angle. Generating them by repeated multiplication
        /// by a fixed root is faster and accumulates error proportional to N, which at N = 2²⁰
        /// costs several digits — enough to miss the 1e-12 Parseval bound this provider exists to
        /// meet.
        /// </remarks>
        private static double[] GetTwiddles(int n)
        {
            return Twiddles.GetOrAdd(n, length =>
            {
                var table = new double[length];
                for (int k = 0; k < length / 2; k++)
                {
                    double angle = -2.0 * Math.PI * k / length;
                    table[k * 2] = Math.Cos(angle);
                    table[k * 2 + 1] = Math.Sin(angle);
                }

                return table;
            });
        }
    }
}
