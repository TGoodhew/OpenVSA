using System;
using System.Collections.Generic;
using System.Linq;
using OpenVSA.Demod.Results;
using OpenVSA.TestHarness.Synthesis;
using Xunit;
using Xunit.Abstractions;

namespace OpenVSA.TestHarness.Tests
{
    /// <summary>
    /// <c>REQ-UI-052</c>'s bottom portion: the detected symbol stream, its gutter and its grouping.
    /// </summary>
    public class SymbolTableTests
    {
        private readonly ITestOutputHelper _output;

        /// <summary>Takes xunit's output sink.</summary>
        /// <param name="output">Where the rendered rows are written.</param>
        public SymbolTableTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void InBinaryTheGutterIsTheBitOffsetOfTheRowsFirstBit()
        {
            // "the number to the left of each row indicates the bit offset of the first bit in the
            // row". Checked against a known stream: 16QAM at four bits a symbol, 32 characters to
            // a row, so each row starts 32 bits after the last.
            var symbols = Enumerable.Range(0, 64).Select(i => i % 16).ToList();

            IReadOnlyList<string> rows = SymbolTable.Render(
                symbols, bitsPerSymbol: 4, format: SymbolTableFormat.Binary, charactersPerRow: 32);

            foreach (string row in rows.Take(4))
            {
                _output.WriteLine(row);
            }

            // 64 symbols x 4 bits = 256 bits, 32 to a row.
            Assert.Equal(8, rows.Count);

            for (int row = 0; row < rows.Count; row++)
            {
                int gutter = int.Parse(rows[row].Substring(0, SymbolTable.GutterWidth).Trim());

                Assert.Equal(row * 32, gutter);
            }

            // And the bits themselves are the stream, in order.
            string expected = SymbolTable.Spell(symbols, 4, SymbolTableFormat.Binary);

            Assert.Equal(
                expected,
                string.Concat(rows.Select(r => r.Substring(SymbolTable.GutterWidth + 1)
                    .Replace(" ", string.Empty))));
        }

        [Fact]
        public void InHexTheGutterIsTheSymbolOffset()
        {
            // "in hex format it is the symbol offset" — the same number the binary gutter shows
            // only because a character is a symbol here and a bit there. An implementation that
            // converted between the two would be right in one format and wrong in the other.
            var symbols = Enumerable.Range(0, 64).Select(i => i % 16).ToList();

            IReadOnlyList<string> rows = SymbolTable.Render(
                symbols, bitsPerSymbol: 4, format: SymbolTableFormat.Hexadecimal,
                charactersPerRow: 16);

            foreach (string row in rows.Take(4))
            {
                _output.WriteLine(row);
            }

            Assert.Equal(4, rows.Count);

            for (int row = 0; row < rows.Count; row++)
            {
                int gutter = int.Parse(rows[row].Substring(0, SymbolTable.GutterWidth).Trim());

                // Symbols, not bits: row 1 starts at symbol 16, which in binary would be bit 64.
                Assert.Equal(row * 16, gutter);
            }

            Assert.Equal(16, SymbolTable.GutterValue(1, 16));
        }

        [Fact]
        public void CharactersGroupInEightsSeparatedByASpace()
        {
            // The requirement's own words. The grouping is of characters, so a group is eight bits
            // in binary and eight symbols in hex.
            var symbols = Enumerable.Range(0, 32).Select(i => i % 16).ToList();

            IReadOnlyList<string> binary = SymbolTable.Render(
                symbols, 4, SymbolTableFormat.Binary, 32);

            string body = binary[0].Substring(SymbolTable.GutterWidth + 1);

            Assert.Equal(new[] { 8, 8, 8, 8 }, body.Split(' ').Select(g => g.Length).ToArray());

            IReadOnlyList<string> hex = SymbolTable.Render(
                symbols, 4, SymbolTableFormat.Hexadecimal, 16);

            string hexBody = hex[0].Substring(SymbolTable.GutterWidth + 1);

            Assert.Equal(new[] { 8, 8 }, hexBody.Split(' ').Select(g => g.Length).ToArray());
        }

        [Fact]
        public void HexIsUnavailableBelowFourBitsPerSymbol()
        {
            // "hex is unavailable below 4 bits/symbol". A hex digit holds four bits; spelling QPSK
            // in it would use four of sixteen values and waste the rest.
            foreach (int bits in new[] { 1, 2, 3 })
            {
                Assert.False(SymbolTable.IsAvailable(SymbolTableFormat.Hexadecimal, bits));
                Assert.True(SymbolTable.IsAvailable(SymbolTableFormat.Binary, bits));

                string reason = SymbolTable.ReasonAgainst(SymbolTableFormat.Hexadecimal, bits);

                Assert.False(string.IsNullOrWhiteSpace(reason));
                Assert.Contains("4 bits per symbol", reason);

                Assert.Throws<InvalidOperationException>(
                    () => SymbolTable.Render(new[] { 0, 1 }, bits, SymbolTableFormat.Hexadecimal));
            }

            foreach (int bits in new[] { 4, 6, 8 })
            {
                Assert.True(SymbolTable.IsAvailable(SymbolTableFormat.Hexadecimal, bits));
                Assert.Null(SymbolTable.ReasonAgainst(SymbolTableFormat.Hexadecimal, bits));
            }
        }

        [Fact]
        public void EveryGeneratedModulationRendersItsOwnStream()
        {
            // Against the real generator, so the table is exercised on the streams the displays
            // will actually be given.
            foreach (ModulationScheme scheme in ModulationScheme.All)
            {
                SymbolTrace trace = new SyntheticSymbolSource { Scheme = scheme }
                    .Generate(64)
                    .ToSymbolTrace();

                IReadOnlyList<string> rows = SymbolTable.Render(
                    trace.Symbols, trace.BitsPerSymbol, SymbolTableFormat.Binary, 32);

                Assert.NotEmpty(rows);

                string spelled = SymbolTable.Spell(
                    trace.Symbols, trace.BitsPerSymbol, SymbolTableFormat.Binary);

                Assert.Equal(64 * scheme.BitsPerSymbol, spelled.Length);

                _output.WriteLine(
                    scheme.Name + " (" + scheme.BitsPerSymbol + " bits): " + rows.Count +
                    " rows, hex " +
                    (SymbolTable.IsAvailable(SymbolTableFormat.Hexadecimal, scheme.BitsPerSymbol)
                        ? "available"
                        : "unavailable"));
            }
        }

        [Fact]
        public void ARowMustHoldAWholeNumberOfGroups()
        {
            // A partial group at the end of every row would put the eights out of step with the
            // column they are meant to align in.
            Assert.Throws<ArgumentOutOfRangeException>(
                () => SymbolTable.Render(new[] { 0 }, 4, SymbolTableFormat.Binary, 30));

            Assert.Throws<ArgumentOutOfRangeException>(
                () => SymbolTable.Render(new[] { 0 }, 4, SymbolTableFormat.Binary, 4));
        }

        [Fact]
        public void MissingArgumentsAreRefused()
        {
            Assert.Throws<ArgumentNullException>(
                () => SymbolTable.Render(null, 4, SymbolTableFormat.Binary));

            Assert.Throws<ArgumentNullException>(
                () => SymbolTable.Spell(null, 4, SymbolTableFormat.Binary));

            Assert.Throws<ArgumentOutOfRangeException>(
                () => SymbolTable.Render(new[] { 0 }, 0, SymbolTableFormat.Binary));
        }
    }
}
