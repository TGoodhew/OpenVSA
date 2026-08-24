using System;
using System.Collections.Generic;
using OpenVSA.Synthesis;
using OpenVSA.TestHarness;
using Xunit;
using Xunit.Abstractions;

namespace OpenVSA.TestHarness.Tests
{
    /// <summary>
    /// <see cref="SymbolRelabelling"/>: what to do with the number a failed bit check leaves behind.
    /// </summary>
    /// <remarks>
    /// The cases that matter are the two it has to tell apart — a stream whose geometry is right and
    /// whose labels are somebody else's, and a stream that is nobody's sequence. The first must be
    /// explained and named; the second must not be explained at all, because a relabelling has as
    /// many free choices as the constellation has points and will happily fit noise if it is let to.
    /// </remarks>
    public class SymbolRelabellingTests
    {
        private const string Pattern = "PN9";

        private readonly ITestOutputHelper _output;

        public SymbolRelabellingTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Theory]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        public void AGrayLabelledStreamIsExplainedAndNamed(int bitsPerSymbol)
        {
            // The case this exists for, made on purpose: the transmitted symbols, relabelled by a
            // Gray code, which is what an instrument's built-in I/Q map is likely to carry.
            int states = 1 << bitsPerSymbol;
            int[] transmitted = Symbols(bitsPerSymbol, 512, offset: 0);
            var recovered = new List<int>(transmitted.Length);

            foreach (int symbol in transmitted)
            {
                recovered.Add(Gray(symbol));
            }

            SymbolRelabellingMatch explained =
                SymbolRelabelling.Explain(recovered, bitsPerSymbol, states, Pattern);

            _output.WriteLine(bitsPerSymbol + " bits/symbol: " + explained);

            Assert.True(explained.Found);
            Assert.Contains("Gray", explained.Description, StringComparison.Ordinal);
            Assert.Equal(explained.Compared, explained.Matched);
        }

        [Fact]
        public void TheOffsetIsFoundWhereverTheStreamStarted()
        {
            const int Offset = 137;

            int[] transmitted = Symbols(2, 512, Offset);

            SymbolRelabellingMatch explained =
                SymbolRelabelling.Explain(transmitted, 2, 4, Pattern);

            _output.WriteLine("started at " + Offset + ", found " + explained);

            Assert.True(explained.Found);
            Assert.Equal(Offset, explained.Offset);

            // Started where it started and relabelled by nothing: every symbol stands for itself.
            for (int symbol = 0; symbol < 4; symbol++)
            {
                Assert.Equal(symbol, explained.Mapping[symbol]);
            }
        }

        [Fact]
        public void AStreamThatIsNobodysSequenceIsNotExplained()
        {
            // The guard that makes the rest of it mean anything. A relabelling is fitted rather
            // than assumed, so on a stream with no structure the best of them still beats chance --
            // and it must not beat it enough to be called an answer.
            var random = new Random(20260824);
            var noise = new List<int>(512);

            for (int symbol = 0; symbol < 512; symbol++)
            {
                noise.Add(random.Next(4));
            }

            SymbolRelabellingMatch explained =
                SymbolRelabelling.Explain(noise, 2, 4, Pattern);

            _output.WriteLine("noise: " + explained);

            Assert.False(explained.Found);
        }

        [Fact]
        public void AStreamWithTwoSymbolsCollapsedIntoOneIsNotExplained()
        {
            // Not every wrong answer is a relabelling, and the ones that are not must not be
            // dressed up as one. A demodulator that could not separate two of its states sends both
            // to the same value, which no bijection undoes -- and the mapping it produces says so by
            // sending two demodulated values to one transmitted value.
            int[] transmitted = Symbols(2, 512, offset: 0);
            var collapsed = new List<int>(transmitted.Length);

            foreach (int symbol in transmitted)
            {
                collapsed.Add(symbol == 3 ? 2 : symbol);
            }

            SymbolRelabellingMatch explained =
                SymbolRelabelling.Explain(collapsed, 2, 4, Pattern);

            _output.WriteLine("two states collapsed: " + explained);

            Assert.False(explained.Found);
        }

        /// <summary>The symbols a PN sequence carries, most significant bit first.</summary>
        private static int[] Symbols(int bitsPerSymbol, int count, int offset)
        {
            int period = PnSequence.PeriodOf(Pattern);
            int[] bits = PnSequence.Generate(Pattern, period);

            var symbols = new int[count];

            for (int symbol = 0; symbol < count; symbol++)
            {
                int value = 0;

                for (int bit = 0; bit < bitsPerSymbol; bit++)
                {
                    value = (value << 1) |
                        bits[(offset + (symbol * bitsPerSymbol) + bit) % period];
                }

                symbols[symbol] = value;
            }

            return symbols;
        }

        private static int Gray(int value) => value ^ (value >> 1);
    }
}
