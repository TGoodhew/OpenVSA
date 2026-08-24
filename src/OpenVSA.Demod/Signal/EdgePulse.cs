using System;

namespace OpenVSA.Demod.Signal
{
    /// <summary>
    /// The EDGE transmit pulse: the linearised-GMSK main pulse <em>c₀(t)</em> of
    /// 3GPP TS 45.004 subclause 3.5 (<c>REQ-DEM-021</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>It is not a Gaussian, and the requirement says so in a box of its own.</strong> It is
    /// the principal component of the Laurent decomposition of GMSK — the pulse that, driven by
    /// Dirac impulses, reproduces most of what a GMSK modulator does, and the one EDGE actually
    /// transmits its 3π/8-rotated 8PSK symbols through. A Gaussian at a fitted BT looks near enough
    /// on a plot to be believed; measured against this it is nowhere near, and a test says by how
    /// much.
    /// </para>
    /// <para>
    /// <strong>The standard's definition, verbatim in structure:</strong>
    /// </para>
    /// <code>
    /// c₀(t) = ∏(i = 0..3) S(t + iT),                       0 ≤ t ≤ 5T,  else 0
    ///
    /// S(t)  = sin(π ∫₀ᵗ g(t′) dt′),                        0 ≤ t ≤ 4T
    ///       = sin(π/2 − π ∫₀^(t−4T) g(t′) dt′),           4T ≤ t ≤ 8T
    ///       = 0                                            else
    ///
    /// g(t)  = 1/2T [ Q(2π·0.3 (t − 5T/2) / (T √ln2)) − Q(2π·0.3 (t − 3T/2) / (T √ln2)) ]
    ///
    /// Q(t)  = 1/√2π ∫ₜ^∞ e^(−τ²/2) dτ
    /// </code>
    /// <para>
    /// <strong>No quadrature: the integral of g has a closed form.</strong> g is a difference of two
    /// Q functions, and ∫Q(x)dx = xQ(x) − φ(x) with φ the standard normal density. The divergent
    /// parts of the two cancel, leaving <see cref="Phase"/> exact to the last bit — which matters
    /// because the pulse is a product of four of these and any error in one is multiplied into all
    /// of them.
    /// </para>
    /// <para>
    /// <strong>🔴 The lower limit of that integral is a reading of the standard, not a detail.</strong>
    /// The text writes ∫₀ᵗ, and g is not quite zero at t = 0 — it is 1.7e-4 — so taking the limit
    /// literally makes the pulse asymmetric by 1.2e-4, while the mathematics says c₀ is exactly
    /// symmetric about 5T/2 (g is symmetric about 2T, so S is symmetric about 4T, and the product of
    /// the four reflects onto itself). This implementation integrates from far enough below zero
    /// that g has genuinely vanished, which gives symmetry to 2.7e-14. <strong>The two readings
    /// differ by up to 6.1e-5 — larger than the 1e-6 the acceptance criterion is stated at — so the
    /// choice had to be made deliberately rather than fallen into.</strong> It is recorded on the
    /// issue and measured in <c>evidence/req-dem-021/</c>.
    /// </para>
    /// </remarks>
    internal static class EdgePulse
    {
        /// <summary>The bandwidth–time product in the standard's own definition of g.</summary>
        public const double BandwidthTime = 0.3;

        /// <summary>How far the pulse reaches either side of its centre, in symbols.</summary>
        /// <remarks>
        /// Two and a half. The standard defines c₀ on <c>0 ≤ t ≤ 5T</c> and applies it delayed by
        /// 2T; centred on its own axis of symmetry that is ±5T/2, and a filter span shorter than
        /// that truncates a pulse the transmitter did not truncate.
        /// </remarks>
        public const double ReachSymbols = 2.5;

        /// <summary>
        /// Where the phase integral is started, in symbols before zero.
        /// </summary>
        /// <remarks>
        /// Six symbols below the standard's own lower limit, where g has fallen to about 1e-30 — far
        /// past anything a double can tell from zero, so moving it further changes nothing. See the
        /// note on the class about why this is not simply zero.
        /// </remarks>
        private const double IntegralStartSymbols = -6.0;

        /// <summary>The pulse, centred on its own axis of symmetry.</summary>
        /// <param name="symbols">How far from the centre, in symbol periods.</param>
        /// <returns>The value of c₀(t + 5T/2); zero beyond ±5T/2.</returns>
        public static double At(double symbols)
        {
            if (Math.Abs(symbols) > ReachSymbols)
            {
                return 0.0;
            }

            return Causal(symbols + ReachSymbols);
        }

        /// <summary>The pulse in the standard's own time reference, non-zero on 0 ≤ t ≤ 5T.</summary>
        /// <param name="t">Time, in symbol periods from the start of the pulse.</param>
        internal static double Causal(double t)
        {
            if (t < 0.0 || t > 5.0)
            {
                return 0.0;
            }

            double product = 1.0;

            for (int i = 0; i < 4; i++)
            {
                product *= S(t + i);
            }

            return product;
        }

        /// <summary>The standard's S(t), in symbol periods.</summary>
        internal static double S(double t)
        {
            if (t < 0.0 || t > 8.0)
            {
                return 0.0;
            }

            if (t <= 4.0)
            {
                return Math.Sin(Math.PI * Phase(t));
            }

            return Math.Sin((Math.PI / 2.0) - (Math.PI * Phase(t - 4.0)));
        }

        /// <summary>
        /// The integral of g up to <paramref name="t"/>, in closed form.
        /// </summary>
        /// <param name="t">The upper limit, in symbol periods.</param>
        /// <returns>The phase integral, which runs from 0 to a half.</returns>
        /// <remarks>
        /// <para>
        /// g is <c>(Q(a(t − 5/2)) − Q(a(t − 3/2))) / 2</c> in symbol periods, and
        /// <c>∫Q(x)dx = xQ(x) − φ(x)</c>. Written for the difference, the parts that diverge at −∞
        /// cancel and what is left is exact:
        /// </para>
        /// <code>
        /// ∫ g = ( [x₂Q(x₂) − φ(x₂)] − [x₁Q(x₁) − φ(x₁)] ) / (2a) + 1/2   evaluated at the limits
        /// </code>
        /// <para>
        /// The constant is what the lower limit contributes: as t falls, both Q approach one and the
        /// bracket approaches <c>x₂ − x₁ = −a</c>, so the whole approaches −1/2 and the added half
        /// takes it to zero. That the result reaches exactly 1/2 as t grows is what makes
        /// <c>S(4T) = sin(π/2) = 1</c>, which is the property the two halves of S join on.
        /// </para>
        /// </remarks>
        internal static double Phase(double t)
        {
            double a = 2.0 * Math.PI * BandwidthTime / Math.Sqrt(Math.Log(2.0));

            return Antiderivative(a * (t - 2.5), a * (t - 1.5)) -
                Antiderivative(a * (IntegralStartSymbols - 2.5), a * (IntegralStartSymbols - 1.5));
        }

        /// <summary>The antiderivative of the difference of the two Q terms, at one point.</summary>
        private static double Antiderivative(double upper, double lower)
        {
            double a = 2.0 * Math.PI * BandwidthTime / Math.Sqrt(Math.Log(2.0));

            double first = (upper * Q(upper)) - Density(upper);
            double second = (lower * Q(lower)) - Density(lower);

            return (first - second) / (2.0 * a);
        }

        /// <summary>The standard normal density.</summary>
        private static double Density(double x) =>
            Math.Exp(-0.5 * x * x) / Math.Sqrt(2.0 * Math.PI);

        /// <summary>The upper-tail Gaussian, <c>Q(x) = erfc(x/√2)/2</c>.</summary>
        internal static double Q(double x) => 0.5 * Erfc(x / Math.Sqrt(2.0));

        /// <summary>
        /// The complementary error function, to about a part in 1e-15.
        /// </summary>
        /// <param name="x">The argument.</param>
        /// <returns><c>erfc(x)</c>.</returns>
        /// <remarks>
        /// <para>
        /// .NET Framework has no <c>erfc</c>, so it is here — a series near the origin where it
        /// converges quickly, and a continued fraction in the tail where the series does not. Both
        /// are the textbook forms, and the join at 2 is where each is comfortably inside its own
        /// range rather than at the edge of it.
        /// </para>
        /// <para>
        /// Accuracy matters more here than it looks: the pulse is a product of four values of S, so
        /// an error in one appears four times over, and the test that compares this filter with an
        /// independent evaluation of the standard is stated at 1e-6.
        /// </para>
        /// </remarks>
        internal static double Erfc(double x)
        {
            if (x < 0.0)
            {
                return 2.0 - Erfc(-x);
            }

            if (x < 2.0)
            {
                // erf(x) = 2/√π · e^(−x²) · Σ 2ⁿx^(2n+1) / (1·3·5···(2n+1)).
                double term = x;
                double sum = x;

                for (int n = 1; n < 200; n++)
                {
                    term *= 2.0 * x * x / ((2 * n) + 1);
                    sum += term;

                    if (Math.Abs(term) < Math.Abs(sum) * 1e-17)
                    {
                        break;
                    }
                }

                return 1.0 - (2.0 / Math.Sqrt(Math.PI) * Math.Exp(-x * x) * sum);
            }

            // erfc(x) = e^(−x²)/(x√π) · 1/(1 + 1/(2x²)/(1 + 2/(2x²)/(1 + …))), evaluated from the
            // bottom up. Sixty levels is far more than enough by x = 2, where the fraction's terms
            // are already shrinking quickly.
            double fraction = 0.0;

            for (int level = 60; level >= 1; level--)
            {
                fraction = level / 2.0 / (x + fraction);
            }

            return Math.Exp(-x * x) / (Math.Sqrt(Math.PI) * (x + fraction));
        }
    }
}
