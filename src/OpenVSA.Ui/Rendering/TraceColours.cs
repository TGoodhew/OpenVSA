using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;

namespace OpenVSA.Ui.Rendering
{
    /// <summary>
    /// Trace identifiers and the indexed colour table they draw from (<c>REQ-UI-020</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Letters, never numbers.</strong> Trace <em>numbers</em> would collide with marker
    /// numbering — "3" would mean a trace to one part of the interface and a marker to another,
    /// and the delta-marker label of <c>REQ-UI-031</c> would become unreadable. Lettering is why
    /// the reference product does it this way, and the criterion fails any identifier that renders
    /// as a bare number.
    /// </para>
    /// <para>
    /// <strong>Twenty colours, and the twenty-first trace reuses the first.</strong> Not an
    /// extended table and not a failure: a user with twenty-one traces open has long since stopped
    /// telling them apart by colour, and refusing the twenty-first would be refusing a measurement
    /// over a display detail.
    /// </para>
    /// </remarks>
    public static class TraceColours
    {
        /// <summary>Entries in the colour table (<c>REQ-UI-020</c>).</summary>
        public const int TableSize = 20;

        /// <summary>
        /// The twenty trace colours, in index order.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Ordered so that consecutive traces are far apart in hue: a two-trace overlay is the
        /// common case and yellow against cyan is legible where yellow against amber is not. The
        /// first is the amber the single-trace display has always used, so opening a second trace
        /// does not recolour the first.
        /// </para>
        /// <para>
        /// All are light against a dark background, which is what <c>REQ-UI-015</c> makes the
        /// default. <c>REQ-UI-090</c>'s contrast floors govern the palette as a whole; these are
        /// chosen inside them, and <see cref="PlotPalette.ForPrinting"/> darkens the light ones
        /// when the background is forced white.
        /// </para>
        /// </remarks>
        public static IReadOnlyList<PlotColor> Table { get; } =
            new ReadOnlyCollection<PlotColor>(new[]
            {
                new PlotColor(0xFF, 0xD2, 0x00),   //  0  amber
                new PlotColor(0x4F, 0xD8, 0xFF),   //  1  cyan
                new PlotColor(0x8C, 0xE8, 0x6B),   //  2  green
                new PlotColor(0xFF, 0x8A, 0xC0),   //  3  pink
                new PlotColor(0xB9, 0x9C, 0xFF),   //  4  violet
                new PlotColor(0xFF, 0xA5, 0x4F),   //  5  orange
                new PlotColor(0x7F, 0xE3, 0xC8),   //  6  teal
                new PlotColor(0xE8, 0xE8, 0x9A),   //  7  straw
                new PlotColor(0x9C, 0xC4, 0xFF),   //  8  periwinkle
                new PlotColor(0xFF, 0xB8, 0xB8),   //  9  salmon
                new PlotColor(0xC7, 0xE8, 0x4F),   // 10  lime
                new PlotColor(0x6F, 0xC4, 0xE8),   // 11  sky
                new PlotColor(0xE8, 0xC4, 0x6F),   // 12  sand
                new PlotColor(0xD0, 0x9C, 0xE8),   // 13  orchid
                new PlotColor(0x6F, 0xE8, 0x9C),   // 14  mint
                new PlotColor(0xE8, 0x9C, 0x6F),   // 15  clay
                new PlotColor(0x9C, 0xE8, 0xE8),   // 16  ice
                new PlotColor(0xE8, 0x6F, 0x9C),   // 17  rose
                new PlotColor(0xC4, 0xC4, 0xE8),   // 18  lilac
                new PlotColor(0xB8, 0xE8, 0xB8),   // 19  sage
            });

        /// <summary>
        /// The colour at a table index, wrapping round rather than running out.
        /// </summary>
        /// <param name="index">A trace's index, from zero.</param>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is negative.</exception>
        public static PlotColor ForIndex(int index)
        {
            if (index < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(index), index, "A trace index counts from zero.");
            }

            return Table[index % TableSize];
        }

        /// <summary>
        /// A trace's identifier: <c>A</c>…<c>Z</c>, then <c>AA</c>, <c>AB</c>, and so on.
        /// </summary>
        /// <param name="index">A trace's index, from zero.</param>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is negative.</exception>
        /// <remarks>
        /// <para>
        /// The criterion asks for lettering to continue past Z "in a defined way rather than
        /// colliding or truncating". Spreadsheet columns are the definition every user already
        /// knows: after Z comes AA, not AAA and not a wrap back to A.
        /// </para>
        /// <para>
        /// Bijective base-26, so there is no zero digit and every index maps to exactly one
        /// identifier. The obvious base-26 conversion produces <c>A</c> for both 0 and 26 — a
        /// collision at exactly the boundary the criterion names.
        /// </para>
        /// </remarks>
        public static string LetterAt(int index)
        {
            if (index < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(index), index, "A trace index counts from zero.");
            }

            var letters = new Stack<char>();
            int remaining = index + 1;

            while (remaining > 0)
            {
                int digit = (remaining - 1) % 26;

                letters.Push((char)('A' + digit));
                remaining = (remaining - 1 - digit) / 26;
            }

            return new string(letters.ToArray());
        }

        /// <summary>
        /// The index a trace identifier stands for; the inverse of <see cref="LetterAt"/>.
        /// </summary>
        /// <param name="letters">The identifier, such as <c>C</c> or <c>AB</c>.</param>
        /// <returns>The index, or −1 if it is not an identifier.</returns>
        public static int IndexOf(string letters)
        {
            if (string.IsNullOrEmpty(letters))
            {
                return -1;
            }

            int index = 0;

            foreach (char letter in letters)
            {
                if (letter < 'A' || letter > 'Z')
                {
                    return -1;
                }

                index = index * 26 + (letter - 'A' + 1);
            }

            return index - 1;
        }

        /// <summary>The colour a trace identifier draws in.</summary>
        /// <param name="letters">The identifier, such as <c>C</c> or <c>AB</c>.</param>
        /// <exception cref="ArgumentException"><paramref name="letters"/> is not an identifier.</exception>
        public static PlotColor ForTrace(string letters)
        {
            int index = IndexOf(letters);

            if (index < 0)
            {
                throw new ArgumentException(
                    "'" + letters + "' is not a trace identifier; traces are lettered A, B, … Z, " +
                    "AA, AB (REQ-UI-020).",
                    nameof(letters));
            }

            return ForIndex(index);
        }

        /// <summary>The colour a single-letter trace draws in.</summary>
        /// <param name="letter">The trace letter.</param>
        /// <exception cref="ArgumentException"><paramref name="letter"/> is not A to Z.</exception>
        public static PlotColor ForTrace(char letter) =>
            ForTrace(letter.ToString(CultureInfo.InvariantCulture));
    }
}
