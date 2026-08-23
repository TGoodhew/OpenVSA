using System;
using System.Globalization;

namespace OpenVSA.Demod.Signal
{
    /// <summary>
    /// One complex sample in double precision, for the estimators to work in.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Not <c>Complex32</c>, and not a replacement for it.</strong> <c>REQ-DAT-003</c> puts
    /// bulk sample data in interleaved <c>float</c> arrays for the vector loads, and that is what
    /// crosses this assembly's boundaries. What happens inside the joint refinement is different
    /// work: a few thousand symbols, summed into accumulators whose result is a frequency accurate
    /// to a small fraction of a hertz over a block that may be tens of milliseconds long. Single
    /// precision has 24 bits of mantissa, and a phase ramp accumulated in it loses the last figures
    /// of exactly the quantity being estimated.
    /// </para>
    /// <para>
    /// Internal because it is a working type, not a vocabulary word. The chain's inputs and results
    /// speak in interleaved arrays and in <c>ConstellationPoint</c>.
    /// </para>
    /// </remarks>
    internal readonly struct Iq
    {
        internal Iq(double i, double q)
        {
            I = i;
            Q = q;
        }

        /// <summary>The in-phase part.</summary>
        internal double I { get; }

        /// <summary>The quadrature part.</summary>
        internal double Q { get; }

        /// <summary>Zero.</summary>
        internal static Iq Zero => default(Iq);

        /// <summary>The squared magnitude, which avoids a square root where one is not needed.</summary>
        internal double MagnitudeSquared => (I * I) + (Q * Q);

        /// <summary>The magnitude.</summary>
        internal double Magnitude => Math.Sqrt(MagnitudeSquared);

        /// <summary>The argument, in radians.</summary>
        internal double Phase => Math.Atan2(Q, I);

        /// <summary>The unit vector at an angle: <c>e^{j\theta}</c>.</summary>
        /// <param name="radians">The angle.</param>
        internal static Iq FromPhase(double radians) =>
            new Iq(Math.Cos(radians), Math.Sin(radians));

        /// <summary>Sums two samples.</summary>
        /// <param name="a">One.</param>
        /// <param name="b">The other.</param>
        public static Iq operator +(Iq a, Iq b) => new Iq(a.I + b.I, a.Q + b.Q);

        /// <summary>Subtracts one sample from another.</summary>
        /// <param name="a">The sample subtracted from.</param>
        /// <param name="b">The sample subtracted.</param>
        public static Iq operator -(Iq a, Iq b) => new Iq(a.I - b.I, a.Q - b.Q);

        /// <summary>Multiplies two samples.</summary>
        /// <param name="a">One.</param>
        /// <param name="b">The other.</param>
        public static Iq operator *(Iq a, Iq b) =>
            new Iq((a.I * b.I) - (a.Q * b.Q), (a.I * b.Q) + (a.Q * b.I));

        /// <summary>Scales a sample.</summary>
        /// <param name="a">The sample.</param>
        /// <param name="scale">The scale.</param>
        public static Iq operator *(Iq a, double scale) => new Iq(a.I * scale, a.Q * scale);

        /// <summary>Divides a sample by a scale.</summary>
        /// <param name="a">The sample.</param>
        /// <param name="scale">The divisor.</param>
        public static Iq operator /(Iq a, double scale) => new Iq(a.I / scale, a.Q / scale);

        /// <summary>The complex conjugate.</summary>
        internal Iq Conjugate() => new Iq(I, -Q);

        /// <summary>Reads one sample out of an interleaved buffer.</summary>
        /// <param name="interleaved">The buffer, real and imaginary alternating.</param>
        /// <param name="index">The sample index, not the array index.</param>
        internal static Iq At(double[] interleaved, int index) =>
            new Iq(interleaved[2 * index], interleaved[(2 * index) + 1]);

        /// <summary>Writes one sample into an interleaved buffer.</summary>
        /// <param name="interleaved">The buffer, real and imaginary alternating.</param>
        /// <param name="index">The sample index, not the array index.</param>
        /// <param name="value">The sample.</param>
        internal static void Set(double[] interleaved, int index, Iq value)
        {
            interleaved[2 * index] = value.I;
            interleaved[(2 * index) + 1] = value.Q;
        }

        /// <summary>How many complex samples an interleaved buffer holds.</summary>
        /// <param name="interleaved">The buffer.</param>
        internal static int Count(double[] interleaved) => interleaved.Length / 2;

        /// <inheritdoc />
        public override string ToString() =>
            I.ToString("G6", CultureInfo.InvariantCulture) + " " +
            (Q < 0 ? "-" : "+") + " " +
            Math.Abs(Q).ToString("G6", CultureInfo.InvariantCulture) + "j";
    }
}
