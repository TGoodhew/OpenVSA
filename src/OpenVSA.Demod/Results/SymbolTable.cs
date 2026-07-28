using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace OpenVSA.Demod.Results
{
    /// <summary>How the detected symbols are spelled (<c>REQ-UI-052</c>).</summary>
    public enum SymbolTableFormat
    {
        /// <summary>One character per bit; the gutter counts bits.</summary>
        Binary = 0,

        /// <summary>One character per symbol; the gutter counts symbols.</summary>
        Hexadecimal,
    }

    /// <summary>
    /// The bottom portion of <c>REQ-UI-052</c>'s trace: the detected symbol or bit stream.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The left gutter counts different things in the two formats, and that is the
    /// requirement.</strong> In binary "the number to the left of each row indicates the bit offset
    /// of the first bit in the row"; in hex it is the symbol offset. Getting that wrong gives a
    /// gutter that is right in one format and out by a factor of the bits per symbol in the other
    /// — which looks like a rounding problem rather than the wrong quantity.
    /// </para>
    /// <para>
    /// <strong>Hex requires at least four bits per symbol.</strong> Below that a symbol does not
    /// fill a hex digit, and a table that rendered QPSK in hex would be spelling two bits in a
    /// character that can hold four — every digit 0 to 3 and three quarters of the alphabet
    /// unused. <see cref="IsAvailable"/> answers it so a display can offer the format or not, and
    /// <see cref="Render"/> refuses rather than producing the misleading thing.
    /// </para>
    /// <para>
    /// <strong>Characters group in eights followed by a space</strong> — the requirement's own
    /// words. The grouping is of <em>characters</em>, not of symbols, so in binary a group is eight
    /// bits and in hex eight symbols; that difference is what makes the groups line up with
    /// something meaningful in each format.
    /// </para>
    /// </remarks>
    public static class SymbolTable
    {
        /// <summary>How many characters make a group (<c>REQ-UI-052</c>).</summary>
        public const int GroupSize = 8;

        /// <summary>The fewest bits per symbol the hexadecimal format can spell.</summary>
        public const int MinimumBitsForHex = 4;

        /// <summary>Characters in the left gutter, before the separator.</summary>
        public const int GutterWidth = 6;

        /// <summary>
        /// Whether a format can spell symbols of a given width.
        /// </summary>
        /// <param name="format">The format.</param>
        /// <param name="bitsPerSymbol">Bits one symbol carries.</param>
        public static bool IsAvailable(SymbolTableFormat format, int bitsPerSymbol) =>
            format != SymbolTableFormat.Hexadecimal || bitsPerSymbol >= MinimumBitsForHex;

        /// <summary>
        /// Why a format is unavailable, or <c>null</c> when it is available.
        /// </summary>
        /// <param name="format">The format.</param>
        /// <param name="bitsPerSymbol">Bits one symbol carries.</param>
        /// <remarks>
        /// A reason rather than a bare refusal, so a display can grey the entry and say why —
        /// the same rule the menus and toolbars keep.
        /// </remarks>
        public static string ReasonAgainst(SymbolTableFormat format, int bitsPerSymbol) =>
            IsAvailable(format, bitsPerSymbol)
                ? null
                : "Hexadecimal needs at least " + MinimumBitsForHex + " bits per symbol; this " +
                  "modulation carries " + bitsPerSymbol +
                  ", so a hex digit would spell fewer values than it can hold.";

        /// <summary>
        /// The symbol stream as rows of text.
        /// </summary>
        /// <param name="symbols">The decided symbol values.</param>
        /// <param name="bitsPerSymbol">Bits one symbol carries.</param>
        /// <param name="format">How to spell them.</param>
        /// <param name="charactersPerRow">
        /// How many characters of stream to a row, not counting the gutter or the group spaces.
        /// </param>
        /// <exception cref="ArgumentNullException"><paramref name="symbols"/> is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException">A count is outside its range.</exception>
        /// <exception cref="InvalidOperationException">The format cannot spell these symbols.</exception>
        public static IReadOnlyList<string> Render(
            IReadOnlyList<int> symbols,
            int bitsPerSymbol,
            SymbolTableFormat format,
            int charactersPerRow = 32)
        {
            if (symbols == null)
            {
                throw new ArgumentNullException(nameof(symbols));
            }

            if (bitsPerSymbol < 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(bitsPerSymbol), bitsPerSymbol, "A symbol carries at least one bit.");
            }

            if (charactersPerRow < GroupSize || charactersPerRow % GroupSize != 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(charactersPerRow), charactersPerRow,
                    "A row holds a whole number of groups of " + GroupSize + " characters.");
            }

            string refusal = ReasonAgainst(format, bitsPerSymbol);

            if (refusal != null)
            {
                throw new InvalidOperationException(refusal);
            }

            string stream = Spell(symbols, bitsPerSymbol, format);
            var rows = new List<string>();

            // The offset into the stream is the gutter's value in both formats: a character is a
            // bit in binary and a symbol in hex, and the gutter counts whichever it is.
            for (int at = 0; at < stream.Length; at += charactersPerRow)
            {
                int take = Math.Min(charactersPerRow, stream.Length - at);

                var row = new StringBuilder();

                row.Append(at.ToString(CultureInfo.InvariantCulture).PadLeft(GutterWidth));
                row.Append(' ');

                for (int i = 0; i < take; i++)
                {
                    if (i > 0 && i % GroupSize == 0)
                    {
                        row.Append(' ');
                    }

                    row.Append(stream[at + i]);
                }

                rows.Add(row.ToString());
            }

            return rows;
        }

        /// <summary>
        /// The gutter value a row starts at (<c>REQ-UI-052</c>).
        /// </summary>
        /// <param name="row">Which row, from zero.</param>
        /// <param name="charactersPerRow">Characters of stream to a row.</param>
        /// <remarks>
        /// <para>
        /// <strong>The same number in both formats, and that is not a coincidence.</strong> In
        /// binary a character is a bit and the gutter counts bits; in hex a character is a symbol
        /// and the gutter counts symbols. Both come out as "characters so far", which is why this
        /// takes no format — and why an implementation that converted between bits and symbols here
        /// would be right in one format and wrong in the other.
        /// </para>
        /// </remarks>
        public static int GutterValue(int row, int charactersPerRow) => row * charactersPerRow;

        /// <summary>
        /// The whole stream as one string, without gutters, grouping or rows.
        /// </summary>
        /// <param name="symbols">The decided symbol values.</param>
        /// <param name="bitsPerSymbol">Bits one symbol carries.</param>
        /// <param name="format">How to spell them.</param>
        /// <exception cref="ArgumentNullException"><paramref name="symbols"/> is null.</exception>
        public static string Spell(
            IReadOnlyList<int> symbols, int bitsPerSymbol, SymbolTableFormat format)
        {
            if (symbols == null)
            {
                throw new ArgumentNullException(nameof(symbols));
            }

            var text = new StringBuilder();

            foreach (int symbol in symbols)
            {
                if (format == SymbolTableFormat.Hexadecimal)
                {
                    text.Append(symbol.ToString("X", CultureInfo.InvariantCulture));
                    continue;
                }

                for (int bit = bitsPerSymbol - 1; bit >= 0; bit--)
                {
                    text.Append(((symbol >> bit) & 1) == 1 ? '1' : '0');
                }
            }

            return text.ToString();
        }
    }
}
