using System;
using System.Collections.Generic;
using OpenVSA.Synthesis;
using OpenVSA.TestHarness;
using Xunit;
using Xunit.Abstractions;

namespace OpenVSA.TestHarness.Tests
{
    /// <summary>
    /// The bit-stream comparison that turns "the constellation looks right" into "these are the bits
    /// the generator sent" (<c>REQ-E44-007</c>).
    /// </summary>
    /// <remarks>
    /// Tested here against streams built from the sequence on purpose, because a search that cannot
    /// find a convention it was handed will not find one on the bench either — and the failure there
    /// would be indistinguishable from a demodulator with a wrong mapping, which is the thing the
    /// search exists to detect.
    /// </remarks>
    public class BitStreamAlignmentTests
    {
        private readonly ITestOutputHelper _output;

        public BitStreamAlignmentTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void ThePn9SequenceHasThePeriodItsPolynomialGives()
        {
            Assert.Equal(511, PnSequence.PeriodOf("PN9"));
            Assert.Equal(2047, PnSequence.PeriodOf("PN11"));
            Assert.Equal(32767, PnSequence.PeriodOf("PN15"));

            int[] sequence = PnSequence.Generate("PN9", 1022);

            // It repeats, and only after its period: a register that fell into a shorter cycle
            // would be a wrong tap, and the sequence would look plausible until it was compared.
            for (int index = 0; index < 511; index++)
            {
                Assert.Equal(sequence[index], sequence[index + 511]);
            }

            Assert.NotEqual(
                Slice(sequence, 0, 100), Slice(sequence, 1, 100));
        }

        [Fact]
        public void TheSequenceIsBalancedAsAMaximalLengthRegisterIs()
        {
            // 256 ones and 255 zeros over a period: not decoration, it is what says the register is
            // maximal-length rather than merely long.
            int[] sequence = PnSequence.Generate("PN9", 511);

            int ones = 0;

            foreach (int bit in sequence)
            {
                ones += bit;
            }

            Assert.Equal(256, ones);
        }

        [Fact]
        public void AnUnknownSequenceIsRefusedByName()
        {
            ArgumentException refused =
                Assert.Throws<ArgumentException>(() => PnSequence.Generate("PN7", 10));

            Assert.Contains("PN7", refused.Message, StringComparison.Ordinal);
        }

        [Theory]
        [InlineData(0, false, false)]
        [InlineData(1, false, false)]
        [InlineData(2, true, false)]
        [InlineData(3, false, true)]
        [InlineData(1, true, true)]
        public void TheSearchFindsTheConventionItWasGiven(int rotation, bool lsbFirst, bool inverted)
        {
            const int Symbols = 600;

            int[] wanted = PnSequence.Generate("PN9", Symbols * 2);
            IReadOnlyList<int> symbols = Encode(wanted, rotation, lsbFirst, inverted);

            BitStreamMatch match = BitStreamAlignment.Find(symbols, 2, 4, "PN9");

            _output.WriteLine(match.ToString());

            Assert.True(match.Found);
            Assert.Equal(1.0, match.Rate);

            // The gap is the evidence: a wrong reading is a coin toss per bit, so a typical one
            // scores near a half.
            Assert.True(match.Baseline < 0.6, "A typical reading scored " + match.Baseline + ".");

            // Unique, for this mapping. Inverting both bits of a symbol takes s to 3 - s and a
            // half-turn takes it to s + 2, which are different permutations under the natural
            // mapping and the same one under a Gray-coded mapping. Asserted rather than assumed,
            // because the first version of this search demanded a unique winner as its evidence and
            // that is only sound for some mappings.
            Assert.Equal(1, match.Ties);
        }

        [Fact]
        public void AStreamThatIsNotTheSequenceIsNotClaimedToBe()
        {
            // The half of this that matters: a demodulator with a systematically wrong mapping, or
            // a signal that was never the sequence, must not produce a match.
            var random = new Random(7);
            var symbols = new List<int>();

            for (int symbol = 0; symbol < 600; symbol++)
            {
                symbols.Add(random.Next(4));
            }

            BitStreamMatch match = BitStreamAlignment.Find(symbols, 2, 4, "PN9");

            _output.WriteLine(match.ToString());

            Assert.False(match.Found);
        }

        [Fact]
        public void OneSymbolErrorDoesNotLoseTheAlignment()
        {
            // A real signal off a real instrument may carry a symbol error, and a check that
            // demanded perfection would fail on a signal that was plainly right.
            int[] wanted = PnSequence.Generate("PN9", 1200);
            var symbols = new List<int>(Encode(wanted, 0, false, false));

            symbols[137] = (symbols[137] + 1) % 4;

            BitStreamMatch match = BitStreamAlignment.Find(symbols, 2, 4, "PN9");

            _output.WriteLine(match.ToString());

            Assert.True(match.Found);
            Assert.True(match.Rate < 1.0);
        }

        [Fact]
        public void TooFewBitsToRuleOutACoincidenceIsNotAMatch()
        {
            // Fewer bits than the sequence's period cannot exclude luck, and saying so is better
            // than reporting a match nobody should rely on.
            int[] wanted = PnSequence.Generate("PN9", 200);
            IReadOnlyList<int> symbols = Encode(wanted, 0, false, false);

            BitStreamMatch match = BitStreamAlignment.Find(symbols, 2, 4, "PN9");

            Assert.False(match.Found);
        }

        /// <summary>Builds the symbol stream a generator using a given convention would produce.</summary>
        private static IReadOnlyList<int> Encode(
            int[] bits, int rotation, bool lsbFirst, bool inverted)
        {
            var symbols = new List<int>(bits.Length / 2);

            for (int symbol = 0; symbol * 2 + 1 < bits.Length; symbol++)
            {
                int first = bits[symbol * 2] ^ (inverted ? 1 : 0);
                int second = bits[(symbol * 2) + 1] ^ (inverted ? 1 : 0);

                int value = lsbFirst ? (second << 1) | first : (first << 1) | second;

                // The rotation the search will have to undo.
                symbols.Add(((value - rotation) % 4 + 4) % 4);
            }

            return symbols;
        }

        private static string Slice(int[] sequence, int from, int count)
        {
            var text = new char[count];

            for (int index = 0; index < count; index++)
            {
                text[index] = sequence[from + index] == 0 ? '0' : '1';
            }

            return new string(text);
        }
    }
}
