using System;
using System.Buffers;
using System.Collections.Concurrent;

namespace OpenVSA.Dsp.Fft
{
    /// <summary>
    /// A managed single-precision FFT, offered under <c>REQ-NFR-004a</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The arithmetic is genuinely <c>float</c> — twiddles, butterflies and all. That is the point:
    /// it is the provider against which the cross-provider tolerance rule of <c>REQ-NFR-004a</c>
    /// has any meaning. Accumulated single-precision error at 2²⁰ points is already about 5e-7, so
    /// agreement with the double provider must be asserted at the single provider's tolerance and
    /// never at 1e-6.
    /// </para>
    /// <para>
    /// <strong>Its throughput advantage is currently unrealised, and saying so matters.</strong>
    /// <see cref="IFftProvider"/> exchanges <c>double</c> so providers are interchangeable, which
    /// means this one converts at both boundaries; the conversion costs roughly what the reduced
    /// precision saves. It becomes a throughput option only once a single-precision path runs
    /// through the measurement chain end to end. Until then, treat it as the second provider that
    /// makes the abstraction testable rather than as a performance setting.
    /// </para>
    /// </remarks>
    [FftProvider("ManagedSingle")]
    public sealed class ManagedSingleFftProvider : IFftProvider
    {
        private static readonly ConcurrentDictionary<int, float[]> Twiddles =
            new ConcurrentDictionary<int, float[]>();

        /// <inheritdoc />
        public string Name => "ManagedSingle";

        /// <inheritdoc />
        public bool IsNativeAccelerated => false;

        /// <inheritdoc />
        public int SignificandBits => 24;

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

            // Pooled rather than allocated, for the same reason IqBlock pools: a 2^20 transform
            // needs an 8 MB scratch buffer, and one per frame on the large object heap would
            // fragment it within minutes of running.
            float[] scratch = ArrayPool<float>.Shared.Rent(interleaved.Length);
            try
            {
                for (int i = 0; i < interleaved.Length; i++)
                {
                    scratch[i] = (float)interleaved[i];
                }

                var data = new Span<float>(scratch, 0, interleaved.Length);

                if (inverse)
                {
                    Conjugate(data, n);
                }

                BitReverse(data, n);
                Butterflies(data, n);

                if (inverse)
                {
                    Conjugate(data, n);
                    float scale = 1.0f / n;
                    for (int i = 0; i < data.Length; i++)
                    {
                        data[i] *= scale;
                    }
                }

                for (int i = 0; i < interleaved.Length; i++)
                {
                    interleaved[i] = scratch[i];
                }
            }
            finally
            {
                ArrayPool<float>.Shared.Return(scratch);
            }
        }

        private static void Conjugate(Span<float> data, int n)
        {
            for (int i = 0; i < n; i++)
            {
                data[i * 2 + 1] = -data[i * 2 + 1];
            }
        }

        private static void BitReverse(Span<float> data, int n)
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

                    float tempRe = data[a];
                    float tempIm = data[a + 1];
                    data[a] = data[b];
                    data[a + 1] = data[b + 1];
                    data[b] = tempRe;
                    data[b + 1] = tempIm;
                }
            }
        }

        private static void Butterflies(Span<float> data, int n)
        {
            float[] twiddles = GetTwiddles(n);

            for (int length = 2; length <= n; length <<= 1)
            {
                int half = length >> 1;
                int stride = n / length;

                for (int start = 0; start < n; start += length)
                {
                    for (int j = 0; j < half; j++)
                    {
                        int t = (j * stride) * 2;
                        float wRe = twiddles[t];
                        float wIm = twiddles[t + 1];

                        int upper = (start + j) * 2;
                        int lower = (start + j + half) * 2;

                        float lowerRe = data[lower];
                        float lowerIm = data[lower + 1];

                        float productRe = lowerRe * wRe - lowerIm * wIm;
                        float productIm = lowerRe * wIm + lowerIm * wRe;

                        float upperRe = data[upper];
                        float upperIm = data[upper + 1];

                        data[upper] = upperRe + productRe;
                        data[upper + 1] = upperIm + productIm;
                        data[lower] = upperRe - productRe;
                        data[lower + 1] = upperIm - productIm;
                    }
                }
            }
        }

        /// <summary>Interleaved <c>e^(−2πik/N)</c>, rounded to single precision.</summary>
        /// <remarks>
        /// The angle and its cosine are computed in double and rounded once. Computing them in
        /// single throughout would add an avoidable error to the table itself, which is a
        /// different and larger effect than the single-precision accumulation this provider is
        /// meant to exhibit.
        /// </remarks>
        private static float[] GetTwiddles(int n)
        {
            return Twiddles.GetOrAdd(n, length =>
            {
                var table = new float[length];
                for (int k = 0; k < length / 2; k++)
                {
                    double angle = -2.0 * Math.PI * k / length;
                    table[k * 2] = (float)Math.Cos(angle);
                    table[k * 2 + 1] = (float)Math.Sin(angle);
                }

                return table;
            });
        }
    }
}
