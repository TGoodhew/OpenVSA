using System;

namespace OpenVSA.Hal.Sim
{
    /// <summary>
    /// A seeded pseudo-random generator whose output stream is fixed by this implementation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>REQ-SIM-003</c> requires that two runs with identical seed and parameters produce
    /// bit-identical sample streams. <see cref="System.Random"/> cannot promise that: its algorithm
    /// is explicitly undocumented and was changed between .NET Framework and .NET Core, so a
    /// scenario recorded today could reproduce differently on a later runtime. That would quietly
    /// invalidate every stored expected value in <c>REQ-TST-005</c>'s corpus.
    /// </para>
    /// <para>
    /// This is xoshiro256** seeded through SplitMix64 — both fully specified, both public domain.
    /// The point is not statistical excellence but that the stream is a property of this code and
    /// nothing else.
    /// </para>
    /// </remarks>
    public sealed class DeterministicRandom
    {
        private ulong _s0, _s1, _s2, _s3;

        // Box-Muller produces two normal deviates at a time; the spare is kept for the next call
        // so the stream advances predictably rather than depending on call parity.
        private double _spareGaussian;
        private bool _hasSpareGaussian;

        /// <summary>Creates a generator from a seed.</summary>
        /// <param name="seed">Any value; the same seed always yields the same stream.</param>
        public DeterministicRandom(long seed)
        {
            Seed = seed;

            // SplitMix64 expansion: guarantees a well-distributed state even from seed 0, which a
            // naive fill would leave degenerate.
            ulong x = unchecked((ulong)seed);
            _s0 = SplitMix64(ref x);
            _s1 = SplitMix64(ref x);
            _s2 = SplitMix64(ref x);
            _s3 = SplitMix64(ref x);
        }

        /// <summary>The seed this generator was created with.</summary>
        public long Seed { get; }

        private static ulong SplitMix64(ref ulong x)
        {
            unchecked
            {
                x += 0x9E3779B97F4A7C15UL;
                ulong z = x;
                z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
                z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
                return z ^ (z >> 31);
            }
        }

        private static ulong RotateLeft(ulong x, int k) => (x << k) | (x >> (64 - k));

        /// <summary>Returns the next 64 random bits.</summary>
        public ulong NextUInt64()
        {
            unchecked
            {
                ulong result = RotateLeft(_s1 * 5UL, 7) * 9UL;
                ulong t = _s1 << 17;

                _s2 ^= _s0;
                _s3 ^= _s1;
                _s1 ^= _s2;
                _s0 ^= _s3;
                _s2 ^= t;
                _s3 = RotateLeft(_s3, 45);

                return result;
            }
        }

        /// <summary>Returns a uniform value in [0, 1).</summary>
        /// <remarks>
        /// Uses the top 53 bits, which is the most a <see cref="double"/> can represent without
        /// rounding two distinct draws onto the same value.
        /// </remarks>
        public double NextDouble() => (NextUInt64() >> 11) * (1.0 / 9007199254740992.0);

        /// <summary>Returns a normally distributed value with zero mean and unit variance.</summary>
        public double NextGaussian()
        {
            if (_hasSpareGaussian)
            {
                _hasSpareGaussian = false;
                return _spareGaussian;
            }

            // Marsaglia polar form. Rejection keeps the transform exact rather than approximating
            // the tails, and consumes a predictable number of draws for a given seed.
            double u, v, s;
            do
            {
                u = 2.0 * NextDouble() - 1.0;
                v = 2.0 * NextDouble() - 1.0;
                s = u * u + v * v;
            }
            while (s >= 1.0 || s == 0.0);

            double scale = Math.Sqrt(-2.0 * Math.Log(s) / s);
            _spareGaussian = v * scale;
            _hasSpareGaussian = true;
            return u * scale;
        }
    }
}
