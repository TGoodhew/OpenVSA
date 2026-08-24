using System;
using System.Collections.Generic;

namespace OpenVSA.Synthesis
{
    /// <summary>
    /// The pseudo-random bit sequences a signal generator transmits, generated here so that a
    /// demodulator's output can be compared with something the instrument did not supply.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Why this is worth having.</strong> Every other check on a demodulator compares it
    /// with itself: EVM says the symbols are near the constellation, and a round trip through
    /// OpenVSA's own generator says the two agree. Neither would notice a bit mapping that was
    /// consistently wrong. A PN sequence is defined by a polynomial in a standard, so it can be
    /// generated from that definition on this side and compared bit for bit with what came out of
    /// the instrument — and PN9 repeats only every 511 bits, so an alignment over a few hundred bits
    /// cannot happen by chance.
    /// </para>
    /// <para>
    /// <strong>The polynomials are ITU-T O.150's, which is what the generator's manual cites.</strong>
    /// What is <em>not</em> settled by the standard, and is deliberately not assumed here, is the
    /// shift direction, which tap is the output, and the initial state — instruments differ, and
    /// those choices only change the sequence's phase or its complement. So this produces one
    /// convention and the comparison searches over the rest; what the search finds is then recorded
    /// as a fact about the instrument rather than guessed at in advance.
    /// </para>
    /// </remarks>
    public static class PnSequence
    {
        /// <summary>The sequences this can generate, by the name the generator uses.</summary>
        public static IReadOnlyList<string> Names =>
            new List<string> { "PN9", "PN11", "PN15", "PN20", "PN23" };

        /// <summary>How long a sequence runs before it repeats.</summary>
        /// <param name="name">The sequence's name.</param>
        /// <returns>The period, in bits.</returns>
        /// <exception cref="ArgumentException">No such sequence.</exception>
        public static int PeriodOf(string name)
        {
            return (1 << DegreeOf(name)) - 1;
        }

        /// <summary>
        /// Generates a sequence.
        /// </summary>
        /// <param name="name">The sequence's name, as the generator names it.</param>
        /// <param name="bits">How many bits to generate; may exceed the period, which repeats.</param>
        /// <returns>The bits, each zero or one.</returns>
        /// <exception cref="ArgumentException">No such sequence.</exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="bits"/> is negative.</exception>
        /// <remarks>
        /// A Fibonacci shift register from an all-ones state, taking the bit that falls out of the
        /// end as the output. That is one of several conventions the standard leaves open; see the
        /// class remarks for why that is not a problem here.
        /// </remarks>
        public static int[] Generate(string name, int bits)
        {
            if (bits < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(bits), bits, "A sequence has a whole number of bits.");
            }

            int degree = DegreeOf(name);
            int tap = TapOf(name);

            // All ones: the state a shift register cannot reach on its own is the one every
            // instrument starts it in, because the all-zero state is a fixed point.
            var register = new int[degree];

            for (int cell = 0; cell < degree; cell++)
            {
                register[cell] = 1;
            }

            var sequence = new int[bits];

            for (int index = 0; index < bits; index++)
            {
                int output = register[degree - 1];
                int feedback = output ^ register[tap - 1];

                for (int cell = degree - 1; cell > 0; cell--)
                {
                    register[cell] = register[cell - 1];
                }

                register[0] = feedback;
                sequence[index] = output;
            }

            return sequence;
        }

        private static int DegreeOf(string name)
        {
            switch ((name ?? string.Empty).Trim().ToUpperInvariant())
            {
                case "PN9":
                    return 9;

                case "PN11":
                    return 11;

                case "PN15":
                    return 15;

                case "PN20":
                    return 20;

                case "PN23":
                    return 23;

                default:
                    throw new ArgumentException(
                        "No sequence called \"" + (name ?? "(none)") + "\" is known. Known: PN9, " +
                        "PN11, PN15, PN20, PN23.",
                        nameof(name));
            }
        }

        /// <summary>The second tap of the sequence's polynomial, counting from one.</summary>
        /// <remarks>
        /// ITU-T O.150's polynomials: x^9 + x^5 + 1, x^11 + x^9 + 1, x^15 + x^14 + 1,
        /// x^20 + x^3 + 1 and x^23 + x^18 + 1. The leading term is the register's length and the
        /// constant is the feedback itself, so the only thing left to state is the middle one.
        /// </remarks>
        private static int TapOf(string name)
        {
            switch (DegreeOf(name))
            {
                case 9:
                    return 5;

                case 11:
                    return 9;

                case 15:
                    return 14;

                case 20:
                    return 3;

                default:
                    return 18;
            }
        }
    }
}
