using System;
using System.Collections.Generic;
using System.Globalization;
using OpenVSA.Synthesis;

namespace OpenVSA.TestHarness
{
    /// <summary>
    /// What relabelling, if any, turns a demodulated symbol stream into a generator's sequence.
    /// </summary>
    public sealed class SymbolRelabellingMatch
    {
        private readonly int[] _mapping;

        internal SymbolRelabellingMatch(
            bool found, int offset, int[] mapping, int matched, int compared, double baseline)
        {
            Found = found;
            Offset = offset;
            _mapping = mapping ?? new int[0];
            Matched = matched;
            Compared = compared;
            Baseline = baseline;
        }

        /// <summary>Whether one relabelling explains the stream.</summary>
        public bool Found { get; }

        /// <summary>Where in the sequence's period the stream started.</summary>
        public int Offset { get; }

        /// <summary>Which transmitted symbol each demodulated one stands for.</summary>
        public IReadOnlyList<int> Mapping => _mapping;

        /// <summary>How many symbols the relabelling accounted for.</summary>
        public int Matched { get; }

        /// <summary>How many symbols were compared.</summary>
        public int Compared { get; }

        /// <summary>What a typical offset's best relabelling scores.</summary>
        /// <remarks>
        /// The same guard <c>BitStreamMatch.Baseline</c> exists for, and it matters more here: a
        /// relabelling has as many free choices as the constellation has points, so it can fit
        /// noise. On a stream that is nobody's sequence the best relabelling at a wrong offset
        /// still scores well above a coin toss, and this says what that looks like.
        /// </remarks>
        public double Baseline { get; }

        /// <summary>The winning relabelling's rate, as a fraction.</summary>
        public double Rate => Compared == 0 ? 0.0 : Matched / (double)Compared;

        /// <summary>What the relabelling is, in the words a person would use.</summary>
        /// <remarks>
        /// Gray is named because it is the answer that keeps coming up — a Gray-labelled
        /// constellation is what nearly every standard specifies and what an instrument's built-in
        /// I/Q map is likely to carry. Named at any rotation, because which rotation a demodulator
        /// locked to is a free parameter and not part of the labelling.
        /// </remarks>
        public string Description
        {
            get
            {
                if (!Found)
                {
                    return "no relabelling";
                }

                string named = Named();

                return named + " (" + string.Join(", ", Rendered()) + ")";
            }
        }

        /// <inheritdoc />
        public override string ToString()
        {
            if (!Found)
            {
                return "no relabelling explains the stream either; best " +
                    Rate.ToString("P2", CultureInfo.InvariantCulture) + " against a typical " +
                    Baseline.ToString("P2", CultureInfo.InvariantCulture);
            }

            return Description + " at offset " + Offset + ": " + Matched + " of " + Compared +
                " symbols (" + Rate.ToString("P2", CultureInfo.InvariantCulture) +
                ") against a typical " + Baseline.ToString("P2", CultureInfo.InvariantCulture);
        }

        private string[] Rendered()
        {
            var rendered = new string[_mapping.Length];

            for (int symbol = 0; symbol < _mapping.Length; symbol++)
            {
                rendered[symbol] = symbol + "→" + _mapping[symbol];
            }

            return rendered;
        }

        /// <summary>Whether the mapping is a Gray code, at any rotation, in either direction.</summary>
        private string Named()
        {
            int order = _mapping.Length;
            bool identity = true;

            for (int symbol = 0; symbol < order; symbol++)
            {
                identity &= _mapping[symbol] == symbol;
            }

            if (identity)
            {
                // Worth saying plainly rather than as a permutation nobody has a name for: the two
                // ends agree about every label, and what was searched here was only the offset.
                return "no relabelling at all -- the labels agree";
            }

            for (int rotation = 0; rotation < order; rotation++)
            {
                bool gray = true;
                bool inverse = true;

                for (int symbol = 0; symbol < order; symbol++)
                {
                    int turned = (symbol + rotation) % order;

                    gray &= _mapping[symbol] == (turned ^ (turned >> 1));
                    inverse &= (_mapping[symbol] ^ (_mapping[symbol] >> 1)) == turned;
                }

                if (gray)
                {
                    return rotation == 0
                        ? "a Gray labelling"
                        : "a Gray labelling, turned by " + rotation;
                }

                if (inverse)
                {
                    return rotation == 0
                        ? "an inverse Gray labelling"
                        : "an inverse Gray labelling, turned by " + rotation;
                }
            }

            return "a relabelling this does not have a name for";
        }
    }

    /// <summary>
    /// Asks whether a symbol stream that is not the sequence is a <em>relabelling</em> of it
    /// (<c>REQ-E44-007</c>, <c>REQ-DEM-011</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Why this exists.</strong> <see cref="BitStreamAlignment"/> answers "are these bits
    /// the sequence, under any of the readings that are free parameters" — and when the answer is
    /// no, it leaves a number like 76.66 % with nothing to do about it. That number is the
    /// interesting case: a demodulator whose geometry is right and whose <em>labels</em> are
    /// somebody else's scores well above chance and well below a match, and the difference between
    /// "our labelling is different from this instrument's" and "something is wrong" is the whole
    /// question. Root-causing it by hand means guessing a permutation; this finds it.
    /// </para>
    /// <para>
    /// <strong>How.</strong> For every start offset in the sequence's period, the transmitted
    /// symbols are known, so the demodulated ones can be tabulated against them and the best
    /// relabelling read straight off the table — each demodulated value stands for whichever
    /// transmitted value it most often coincides with. A real relabelling is a bijection and comes
    /// out as one; a stream that is nobody's sequence produces a table with no structure, and the
    /// baseline says so.
    /// </para>
    /// <para>
    /// <strong>It subsumes the rotations and bit orders searched elsewhere</strong>, because every
    /// one of those is itself a relabelling: turning a constellation permutes its labels, reversing
    /// the bits within a symbol permutes them, and inverting every bit permutes them. That is why
    /// this is a wider net and a weaker claim, and why <see cref="BitStreamAlignment"/> remains the
    /// thing that decides whether a check passed. A relabelling that explains a stream is evidence
    /// about a convention; it is not a demodulation that was right.
    /// </para>
    /// </remarks>
    public static class SymbolRelabelling
    {
        /// <summary>How much of the stream a relabelling must account for to be the answer.</summary>
        /// <remarks>
        /// The same figure <see cref="BitStreamAlignment.MinimumRate"/> uses, and for the same
        /// reason: a real signal off a real instrument may carry a symbol error, and demanding
        /// perfection would reject an answer that is plainly right.
        /// </remarks>
        public const double MinimumRate = 0.98;

        /// <summary>The most a typical offset may score for the winner to mean anything.</summary>
        /// <remarks>
        /// Higher than <see cref="BitStreamAlignment.MaximumBaseline"/>, and it has to be. A
        /// relabelling is fitted rather than assumed, so at a wrong offset the best of them still
        /// explains more than chance would — with <em>m</em> points and <em>n</em> symbols it picks
        /// the largest of <em>m</em> roughly equal counts in each row. What it cannot do at a wrong
        /// offset is explain nearly all of them.
        /// </remarks>
        public const double MaximumBaseline = 0.75;

        /// <summary>
        /// Searches for the relabelling under which a symbol stream is a generator's sequence.
        /// </summary>
        /// <param name="symbols">The demodulated symbol values.</param>
        /// <param name="bitsPerSymbol">How many bits each symbol carries.</param>
        /// <param name="statesPerSymbol">How many states the constellation has.</param>
        /// <param name="sequence">The generator's sequence, by name.</param>
        /// <returns>What explained the stream, and how convincingly.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="symbols"/> is null.</exception>
        /// <exception cref="ArgumentException">The sequence is not one this knows.</exception>
        public static SymbolRelabellingMatch Explain(
            IReadOnlyList<int> symbols, int bitsPerSymbol, int statesPerSymbol, string sequence)
        {
            if (symbols == null)
            {
                throw new ArgumentNullException(nameof(symbols));
            }

            int states = Math.Max(1, statesPerSymbol);
            int period = PnSequence.PeriodOf(sequence);
            int[] reference = PnSequence.Generate(sequence, period);

            if (symbols.Count * bitsPerSymbol < period || bitsPerSymbol < 1)
            {
                return new SymbolRelabellingMatch(false, 0, null, 0, symbols.Count, 0.0);
            }

            int[] bestMapping = null;
            int bestMatched = -1;
            int bestOffset = 0;

            var rates = new List<double>(period);

            for (int offset = 0; offset < period; offset++)
            {
                var table = new int[states, states];

                for (int symbol = 0; symbol < symbols.Count; symbol++)
                {
                    int demodulated = ((symbols[symbol] % states) + states) % states;
                    int transmitted = Transmitted(
                        reference, period, offset + (symbol * bitsPerSymbol), bitsPerSymbol);

                    table[demodulated, transmitted]++;
                }

                int[] mapping;
                int matched = Best(table, states, out mapping);

                rates.Add(matched / (double)symbols.Count);

                if (matched > bestMatched)
                {
                    bestMatched = matched;
                    bestMapping = mapping;
                    bestOffset = offset;
                }
            }

            rates.Sort();

            double baseline = rates[rates.Count / 2];
            double rate = bestMatched / (double)symbols.Count;

            return new SymbolRelabellingMatch(
                rate >= MinimumRate && baseline <= MaximumBaseline,
                bestOffset,
                bestMapping,
                bestMatched,
                symbols.Count,
                baseline);
        }

        /// <summary>The symbol the sequence's bits carry at a position, most significant first.</summary>
        private static int Transmitted(int[] reference, int period, int from, int bitsPerSymbol)
        {
            int value = 0;

            for (int bit = 0; bit < bitsPerSymbol; bit++)
            {
                value = (value << 1) | reference[(from + bit) % period];
            }

            return value;
        }

        /// <summary>
        /// The best relabelling a table of coincidences supports, and how much it accounts for.
        /// </summary>
        /// <remarks>
        /// Each demodulated value is assigned the transmitted value it coincided with most often.
        /// That is not forced to be a bijection, and it is not made into one: when the stream really
        /// is a relabelling the answer comes out bijective on its own, and when it does not, a
        /// mapping that sends two values to one is exactly the evidence that no relabelling explains
        /// the stream. Forcing a permutation would hide that behind a number.
        /// </remarks>
        private static int Best(int[,] table, int states, out int[] mapping)
        {
            mapping = new int[states];

            int total = 0;

            for (int demodulated = 0; demodulated < states; demodulated++)
            {
                int best = 0;
                int at = 0;

                for (int transmitted = 0; transmitted < states; transmitted++)
                {
                    if (table[demodulated, transmitted] > best)
                    {
                        best = table[demodulated, transmitted];
                        at = transmitted;
                    }
                }

                mapping[demodulated] = at;
                total += best;
            }

            return total;
        }
    }
}
