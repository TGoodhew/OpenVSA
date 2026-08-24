using System;
using System.Collections.Generic;
using System.Globalization;
using OpenVSA.Synthesis;

namespace OpenVSA.TestHarness
{
    /// <summary>
    /// How a demodulated symbol stream lined up with the sequence the generator was transmitting.
    /// </summary>
    public sealed class BitStreamMatch
    {
        internal BitStreamMatch(
            bool found,
            int rotation,
            bool lsbFirst,
            bool inverted,
            int offset,
            int matched,
            int compared,
            double baseline,
            int ties)
        {
            Found = found;
            Rotation = rotation;
            LsbFirst = lsbFirst;
            Inverted = inverted;
            Offset = offset;
            Matched = matched;
            Compared = compared;
            Baseline = baseline;
            Ties = ties;
        }

        /// <summary>Whether one reading of the stream matched well enough to be the answer.</summary>
        public bool Found { get; }

        /// <summary>How many quarter turns the constellation was rotated by.</summary>
        /// <remarks>
        /// Not a defect: without a sync word or differential decoding, every PSK demodulator finds
        /// the constellation at one of its rotations and all of them are correct. What matters is
        /// that one rotation explains the whole stream.
        /// </remarks>
        public int Rotation { get; }

        /// <summary>Whether the least significant bit of a symbol came first in the stream.</summary>
        public bool LsbFirst { get; }

        /// <summary>Whether every bit was inverted.</summary>
        public bool Inverted { get; }

        /// <summary>Where in the sequence's period the stream started.</summary>
        public int Offset { get; }

        /// <summary>How many bits agreed.</summary>
        public int Matched { get; }

        /// <summary>How many bits were compared.</summary>
        public int Compared { get; }

        /// <summary>
        /// What a typical reading of the stream scores: the median rate across every candidate.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The number that makes the answer trustworthy rather than lucky. A wrong reading is a coin
        /// toss per bit, so the median sits near a half, and the winner scoring near one against
        /// that is the evidence that the stream really is the sequence.
        /// </para>
        /// <para>
        /// <strong>The median rather than the runner-up, because whether the winner is unique
        /// depends on the mapping.</strong> Two readings can be the same relabelling and then score
        /// identically. Whether that happens turns on how bits are assigned to points: inverting
        /// both bits of a two-bit symbol takes <em>s</em> to 3−<em>s</em>, while a half-turn of the
        /// constellation takes it to <em>s</em>+2. Those are different permutations for the natural
        /// mapping OpenVSA uses — measured: the winner comes out unique — and the same permutation
        /// for a Gray-coded one, where antipodal points carry complementary labels. A criterion
        /// demanding that the winner beat its runner-up would therefore pass on one mapping and
        /// reject a perfect match on the other, which is no way to judge evidence.
        /// <see cref="Ties"/> reports how many readings tied, so the ambiguity is visible when it is
        /// there rather than assumed either way.
        /// </para>
        /// </remarks>
        public double Baseline { get; }

        /// <summary>How many candidate readings scored exactly what the winner scored.</summary>
        /// <remarks>
        /// One for a mapping whose readings are all distinct, which the natural mapping's are. More
        /// than one is not a defect: it means the stream is the sequence under any of several
        /// equivalent readings, and which of them the demodulator "really" used is not a question
        /// the bits can answer. See <see cref="Baseline"/>.
        /// </remarks>
        public int Ties { get; }

        /// <summary>The winning reading's rate, as a fraction.</summary>
        public double Rate => Compared == 0 ? 0.0 : Matched / (double)Compared;

        /// <inheritdoc />
        public override string ToString()
        {
            if (!Found)
            {
                return "no reading explained the stream; best " +
                    Rate.ToString("P2", CultureInfo.InvariantCulture) + " against a typical " +
                    Baseline.ToString("P2", CultureInfo.InvariantCulture);
            }

            return "rotation " + Rotation + ", " + (LsbFirst ? "LSB" : "MSB") + " first" +
                (Inverted ? ", inverted" : string.Empty) + ", offset " + Offset + ": " +
                Matched + " of " + Compared + " bits (" +
                Rate.ToString("P2", CultureInfo.InvariantCulture) + ") against a typical " +
                Baseline.ToString("P2", CultureInfo.InvariantCulture) +
                (Ties > 1 ? ", " + Ties + " equivalent readings" : string.Empty);
        }
    }

    /// <summary>
    /// Finds which reading makes a demodulated symbol stream agree with a generator's PN sequence
    /// (<c>REQ-E44-007</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The point is to measure the convention, not to assume it.</strong> Four things are
    /// unknown when a demodulator first meets a real transmitter: which of the constellation's
    /// rotations it locked to, whether the instrument sends the most or least significant bit of a
    /// symbol first, whether the sequence or its complement is transmitted, and where in the
    /// sequence's period the analysed block happened to start. None is a defect and any of them
    /// would make a bit-for-bit comparison fail. So all four are searched, and what comes out is
    /// recorded as a fact about the instrument and OpenVSA together.
    /// </para>
    /// <para>
    /// <strong>Why the answer is trustworthy.</strong> PN9 repeats every 511 bits, so a wrong
    /// reading is a coin toss per bit and lands near a half. A stream that really carries the
    /// sequence matches one reading almost perfectly. The distance between the winner and a typical
    /// candidate is therefore the evidence, and it is reported alongside the winner rather than left
    /// out — a match of 99 % means nothing if everything scored 98 %.
    /// </para>
    /// <para>
    /// <strong>What it may not be able to tell apart, and says so.</strong> Depending on the bit
    /// mapping, two readings can be the same relabelling and tie — see <c>BitStreamMatch.Baseline</c>
    /// for when. Ties are reported rather than broken. What is proved is that the stream <em>is</em>
    /// the sequence and under which reading or family of readings, not which member of that family
    /// the demodulator had in mind, which the bits cannot say.
    /// </para>
    /// </remarks>
    public static class BitStreamAlignment
    {
        /// <summary>How much of the stream must agree for a reading to be the answer.</summary>
        /// <remarks>
        /// Not 100 %, because a real signal off a real instrument may carry a symbol error and a
        /// check demanding perfection would fail on a signal that was plainly right. Not much below,
        /// because a demodulator with a systematically wrong mapping does not score 95 %.
        /// </remarks>
        public const double MinimumRate = 0.98;

        /// <summary>The most a typical reading may score for the winner to mean anything.</summary>
        /// <remarks>
        /// A wrong reading is a coin toss per bit, so the median lands near a half. Anything much
        /// above says the comparison is not discriminating and the winner proves nothing.
        /// </remarks>
        public const double MaximumBaseline = 0.6;

        /// <summary>
        /// Searches for the reading under which a symbol stream is a generator's PN sequence.
        /// </summary>
        /// <param name="symbols">The demodulated symbol values.</param>
        /// <param name="bitsPerSymbol">How many bits each symbol carries.</param>
        /// <param name="statesPerSymbol">
        /// How many states the constellation has, which is how many rotations to try.
        /// </param>
        /// <param name="sequence">The generator's sequence, by name — <c>PN9</c> and friends.</param>
        /// <returns>What matched, and how convincingly.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="symbols"/> is null.</exception>
        /// <exception cref="ArgumentException">The sequence is not one this knows.</exception>
        /// <param name="carried">
        /// What each symbol value carries, when the constellation is labelled with something other
        /// than the natural mapping (<c>REQ-DEM-011</c>); <c>null</c> when a symbol carries itself.
        /// </param>
        /// <param name="rotations">
        /// How many rotations to search; zero or less for all of them, and one for a stream in which
        /// the rotation has already been divided out.
        /// </param>
        /// <remarks>
        /// <strong>How many states there are and how many rotations to try are two different
        /// numbers, and conflating them is not a hypothetical mistake.</strong> A differential
        /// stream needs one rotation searched and still has all of its states; asking for that by
        /// passing a state count of one instead collapsed every symbol to zero — a stream of nothing
        /// but zeroes, which scores about half against any sequence and produced a confident-looking
        /// "best 50.10 % against a typical 50.10 %" for three bench cases that were in fact perfect.
        /// The two numbers being equal is what gives it away: every candidate reading scored the
        /// same because every candidate reading was the same.
        /// </remarks>
        public static BitStreamMatch Find(
            IReadOnlyList<int> symbols,
            int bitsPerSymbol,
            int statesPerSymbol,
            string sequence,
            IReadOnlyList<int> carried = null,
            int rotations = 0)
        {
            if (symbols == null)
            {
                throw new ArgumentNullException(nameof(symbols));
            }

            int period = PnSequence.PeriodOf(sequence);
            int[] reference = PnSequence.Generate(sequence, period);

            int bits = symbols.Count * bitsPerSymbol;

            if (bits < period)
            {
                // Fewer bits than the sequence's period cannot rule out a coincidence, and saying so
                // is better than reporting a match nobody should rely on.
                return new BitStreamMatch(false, 0, false, false, 0, 0, bits, 0.0, 0);
            }

            BitStreamMatch best = null;
            var rates = new List<double>();

            foreach (bool lsbFirst in new[] { false, true })
            {
                int tried = rotations > 0
                    ? Math.Min(rotations, Math.Max(1, statesPerSymbol))
                    : Math.Max(1, statesPerSymbol);

                for (int rotation = 0; rotation < tried; rotation++)
                {
                    int[] stream = Stream(
                        symbols, bitsPerSymbol, statesPerSymbol, rotation, lsbFirst, carried);

                    foreach (bool inverted in new[] { false, true })
                    {
                        for (int offset = 0; offset < period; offset++)
                        {
                            int matched = Agreement(stream, reference, offset, inverted);
                            double rate = matched / (double)stream.Length;

                            rates.Add(rate);

                            if (best != null && rate <= best.Rate)
                            {
                                continue;
                            }

                            best = new BitStreamMatch(
                                false,
                                rotation,
                                lsbFirst,
                                inverted,
                                offset,
                                matched,
                                stream.Length,
                                0.0,
                                0);
                        }
                    }
                }
            }

            if (best == null)
            {
                return new BitStreamMatch(false, 0, false, false, 0, 0, bits, 0.0, 0);
            }

            rates.Sort();

            double baseline = rates[rates.Count / 2];
            int ties = 0;

            foreach (double rate in rates)
            {
                if (Math.Abs(rate - best.Rate) < 1e-12)
                {
                    ties++;
                }
            }

            bool found = best.Rate >= MinimumRate && baseline <= MaximumBaseline;

            return new BitStreamMatch(
                found,
                best.Rotation,
                best.LsbFirst,
                best.Inverted,
                best.Offset,
                best.Matched,
                best.Compared,
                baseline,
                ties);
        }

        /// <remarks>
        /// <strong>The rotation is applied to the point and the labelling afterwards, in that
        /// order.</strong> A turned constellation moves each point to its neighbour's place, and
        /// what that point then carries is whatever the labelling says. Adding the rotation to the
        /// carried value instead would be right only for the natural mapping, where a point and its
        /// value are the same number — and silently wrong for every other, which is exactly the kind
        /// of coincidence that hides a defect until somebody changes the mapping.
        /// </remarks>
        private static int[] Stream(
            IReadOnlyList<int> symbols,
            int bitsPerSymbol,
            int statesPerSymbol,
            int rotation,
            bool lsbFirst,
            IReadOnlyList<int> carried)
        {
            var stream = new int[symbols.Count * bitsPerSymbol];
            int states = Math.Max(1, statesPerSymbol);

            for (int symbol = 0; symbol < symbols.Count; symbol++)
            {
                int value = (((symbols[symbol] + rotation) % states) + states) % states;

                if (carried != null)
                {
                    value = carried[value];
                }

                for (int bit = 0; bit < bitsPerSymbol; bit++)
                {
                    int shift = lsbFirst ? bit : bitsPerSymbol - 1 - bit;

                    stream[(symbol * bitsPerSymbol) + bit] = (value >> shift) & 1;
                }
            }

            return stream;
        }

        private static int Agreement(int[] stream, int[] reference, int offset, bool inverted)
        {
            int matched = 0;
            int period = reference.Length;

            for (int index = 0; index < stream.Length; index++)
            {
                int want = reference[(offset + index) % period];

                if (inverted)
                {
                    want ^= 1;
                }

                if (stream[index] == want)
                {
                    matched++;
                }
            }

            return matched;
        }
    }
}
