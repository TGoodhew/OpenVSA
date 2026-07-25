using System;
using System.Runtime.InteropServices;

namespace OpenVSA.Core
{
    /// <summary>
    /// A single-precision complex sample: 8 bytes, laid out as I then Q.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Per <c>REQ-DAT-003</c> this type exists for <em>interpreted</em> access to sample data —
    /// reading one sample, writing a test vector, expressing an ideal constellation point. Bulk
    /// DSP does <em>not</em> use it: kernels take the raw interleaved <c>float[]</c> so the JIT can
    /// vectorise them. A public DSP kernel taking <c>Complex32[]</c> is a defect, and
    /// <c>REQ-DAT-003</c>'s acceptance criteria assert its absence.
    /// </para>
    /// <para>
    /// The layout is sequential and the size is exactly 8 bytes so that a <c>float[2N]</c> in
    /// I,Q,I,Q order and a <c>Complex32[N]</c> describe the same bytes in the same order. That
    /// correspondence is what makes the two views interchangeable rather than merely similar.
    /// </para>
    /// </remarks>
    [StructLayout(LayoutKind.Sequential, Pack = 4, Size = 8)]
    public readonly struct Complex32 : IEquatable<Complex32>
    {
        /// <summary>The in-phase component.</summary>
        public readonly float I;

        /// <summary>The quadrature component.</summary>
        public readonly float Q;

        /// <summary>Creates a complex sample from its components.</summary>
        /// <param name="i">In-phase component.</param>
        /// <param name="q">Quadrature component.</param>
        public Complex32(float i, float q)
        {
            I = i;
            Q = q;
        }

        /// <summary>The complex zero.</summary>
        public static Complex32 Zero => default(Complex32);

        /// <summary>
        /// Squared magnitude, <c>I² + Q²</c>, accumulated in <see cref="double"/>.
        /// </summary>
        /// <remarks>
        /// <c>REQ-DSP-002</c> requires accumulation in double precision even where storage is
        /// single. Returning <see cref="double"/> here keeps a caller summing many of these on the
        /// correct side of that rule by default.
        /// </remarks>
        public double MagnitudeSquared => (double)I * I + (double)Q * Q;

        /// <summary>Magnitude, <c>√(I² + Q²)</c>.</summary>
        public double Magnitude => Math.Sqrt(MagnitudeSquared);

        /// <summary>Phase in radians, in the principal range (−π, π].</summary>
        public double Phase => Math.Atan2(Q, I);

        /// <inheritdoc />
        public bool Equals(Complex32 other) =>
            I.Equals(other.I) && Q.Equals(other.Q);

        /// <inheritdoc />
        public override bool Equals(object obj) => obj is Complex32 other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode()
        {
            unchecked
            {
                return (I.GetHashCode() * 397) ^ Q.GetHashCode();
            }
        }

        /// <summary>Equality operator.</summary>
        /// <param name="left">Left operand.</param>
        /// <param name="right">Right operand.</param>
        public static bool operator ==(Complex32 left, Complex32 right) => left.Equals(right);

        /// <summary>Inequality operator.</summary>
        /// <param name="left">Left operand.</param>
        /// <param name="right">Right operand.</param>
        public static bool operator !=(Complex32 left, Complex32 right) => !left.Equals(right);

        /// <inheritdoc />
        public override string ToString() =>
            string.Format(System.Globalization.CultureInfo.InvariantCulture, "({0}, {1})", I, Q);
    }
}
