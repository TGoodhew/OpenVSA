using System;
using System.Numerics;

namespace OpenVSA.Dsp
{
    /// <summary>
    /// The two inner loops of a spectrum frame, in a scalar and a vector form
    /// (<c>REQ-NFR-003</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both forms are kept and both are public, because the requirement asks for the vector one to
    /// be <em>measured against</em> the scalar one rather than merely to exist. A kernel with no
    /// reference to compare against cannot be shown to be worth its complexity, and these are the
    /// two loops where the complexity would otherwise be argued about on instinct.
    /// </para>
    /// <para>
    /// <strong>Raw arrays, not <c>Span&lt;T&gt;</c>.</strong> On .NET Framework the portable span
    /// has no JIT intrinsic — <c>DEPENDENCIES.md</c> records this against <c>System.Memory</c> —
    /// so indexing one costs a bounds check the JIT will not elide and a vector loop built on it
    /// would measure the span rather than the arithmetic.
    /// </para>
    /// <para>
    /// <c>Vector&lt;float&gt;</c> rather than an explicit width: the same source compiles to SSE on
    /// a machine with four lanes and AVX2 on one with eight, and the requirement's own escape
    /// clause is about what <c>Vector&lt;float&gt;.Count</c> turns out to be on the target.
    /// </para>
    /// </remarks>
    public static class Kernels
    {
        /// <summary>Lanes the runtime gives a <see cref="Vector{T}"/> of <see cref="float"/>.</summary>
        /// <remarks>
        /// Reported rather than assumed. <c>REQ-NFR-003</c>'s alternative branch turns on this
        /// being four, so a measurement that did not state it could not be read against the
        /// requirement at all.
        /// </remarks>
        public static int Lanes => Vector<float>.Count;

        /// <summary>Whether the runtime accelerates vectors at all.</summary>
        public static bool IsAccelerated => Vector.IsHardwareAccelerated;

        /// <summary>Multiplies an interleaved I/Q buffer by a window, one coefficient per sample.</summary>
        /// <param name="interleaved">Interleaved I/Q, modified in place.</param>
        /// <param name="window">Window coefficients; half the length of <paramref name="interleaved"/>.</param>
        /// <exception cref="ArgumentNullException">An argument is null.</exception>
        /// <exception cref="ArgumentException">The lengths do not correspond.</exception>
        public static void WindowMultiplyScalar(float[] interleaved, float[] window)
        {
            Check(interleaved, window);

            for (int n = 0; n < window.Length; n++)
            {
                float w = window[n];

                interleaved[n * 2] *= w;
                interleaved[n * 2 + 1] *= w;
            }
        }

        /// <summary>The vector form of <see cref="WindowMultiplyScalar"/>.</summary>
        /// <param name="interleaved">Interleaved I/Q, modified in place.</param>
        /// <param name="window">Window coefficients.</param>
        /// <exception cref="ArgumentNullException">An argument is null.</exception>
        /// <exception cref="ArgumentException">The lengths do not correspond.</exception>
        /// <remarks>
        /// Each window coefficient applies to two consecutive floats, so the window is widened into
        /// the interleaved layout as it goes rather than the buffer being de-interleaved first. A
        /// de-interleave would be two extra passes over the whole buffer and would lose more than
        /// the vectorisation gains.
        /// </remarks>
        public static void WindowMultiplyVector(float[] interleaved, float[] window)
        {
            Check(interleaved, window);

            int lanes = Vector<float>.Count;
            int pairs = lanes / 2;
            var widened = new float[lanes];

            int n = 0;

            for (; n + pairs <= window.Length; n += pairs)
            {
                for (int k = 0; k < pairs; k++)
                {
                    float w = window[n + k];

                    widened[k * 2] = w;
                    widened[k * 2 + 1] = w;
                }

                var coefficients = new Vector<float>(widened);
                var samples = new Vector<float>(interleaved, n * 2);

                (samples * coefficients).CopyTo(interleaved, n * 2);
            }

            for (; n < window.Length; n++)
            {
                float w = window[n];

                interleaved[n * 2] *= w;
                interleaved[n * 2 + 1] *= w;
            }
        }

        /// <summary>Squared magnitude of each interleaved complex sample.</summary>
        /// <param name="interleaved">Interleaved I/Q.</param>
        /// <param name="magnitudes">Receives one value per complex sample.</param>
        /// <exception cref="ArgumentNullException">An argument is null.</exception>
        /// <exception cref="ArgumentException">The lengths do not correspond.</exception>
        public static void MagnitudeSquaredScalar(float[] interleaved, float[] magnitudes)
        {
            Check(interleaved, magnitudes);

            for (int n = 0; n < magnitudes.Length; n++)
            {
                float i = interleaved[n * 2];
                float q = interleaved[n * 2 + 1];

                magnitudes[n] = i * i + q * q;
            }
        }

        /// <summary>The vector form of <see cref="MagnitudeSquaredScalar"/>.</summary>
        /// <param name="interleaved">Interleaved I/Q.</param>
        /// <param name="magnitudes">Receives one value per complex sample.</param>
        /// <exception cref="ArgumentNullException">An argument is null.</exception>
        /// <exception cref="ArgumentException">The lengths do not correspond.</exception>
        /// <remarks>
        /// Two loaded vectors of interleaved data are squared and then folded, which costs a
        /// gather that a de-interleaved layout would not — the price of <c>REQ-DAT-003</c>'s
        /// interleaved buffer, paid here rather than in a conversion pass.
        /// </remarks>
        public static void MagnitudeSquaredVector(float[] interleaved, float[] magnitudes)
        {
            Check(interleaved, magnitudes);

            int lanes = Vector<float>.Count;
            var squared = new float[lanes];

            int n = 0;

            for (; n + lanes <= magnitudes.Length; n += lanes)
            {
                // Square the whole interleaved run: two vectors cover 2 × lanes floats, which is
                // exactly the I and Q of `lanes` complex samples.
                var first = new Vector<float>(interleaved, n * 2);
                var second = new Vector<float>(interleaved, n * 2 + lanes);

                (first * first).CopyTo(squared);

                for (int k = 0; k < lanes / 2; k++)
                {
                    magnitudes[n + k] = squared[k * 2] + squared[k * 2 + 1];
                }

                (second * second).CopyTo(squared);

                for (int k = 0; k < lanes / 2; k++)
                {
                    magnitudes[n + lanes / 2 + k] = squared[k * 2] + squared[k * 2 + 1];
                }
            }

            for (; n < magnitudes.Length; n++)
            {
                float i = interleaved[n * 2];
                float q = interleaved[n * 2 + 1];

                magnitudes[n] = i * i + q * q;
            }
        }

        private static void Check(float[] interleaved, float[] perSample)
        {
            if (interleaved == null)
            {
                throw new ArgumentNullException(nameof(interleaved));
            }

            if (perSample == null)
            {
                throw new ArgumentNullException(nameof(perSample));
            }

            if (interleaved.Length != perSample.Length * 2)
            {
                throw new ArgumentException(
                    "Expected " + (perSample.Length * 2) + " interleaved values, got " +
                    interleaved.Length + ".",
                    nameof(interleaved));
            }
        }
    }
}
