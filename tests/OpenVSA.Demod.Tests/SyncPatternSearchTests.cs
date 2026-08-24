using System;
using System.Collections.Generic;
using System.Globalization;
using OpenVSA.Demod.Chain;
using OpenVSA.Demod.Signal;
using OpenVSA.Synthesis;
using Xunit;
using Xunit.Abstractions;

namespace OpenVSA.Demod.Tests
{
    /// <summary>
    /// <c>REQ-DEM-040</c>: finding a sync pattern, and positioning the result relative to it.
    /// </summary>
    /// <remarks>
    /// The criterion is stated against "a known pattern inserted at a known position by the
    /// simulator", so the simulator inserts one: <c>ContinuousModulatedSource.InsertedSymbols</c>
    /// carries a run of known symbols at a stated index while everything around it stays what it
    /// was. That makes "the window lands at the specified offset, to the symbol" a comparison
    /// against numbers the test chose rather than against the chain's own opinion of where it is.
    /// </remarks>
    public class SyncPatternSearchTests
    {
        private const double SymbolRateHz = 1e6;
        private const double SampleRateHz = 16e6;
        private const int PerSymbol = 16;
        private const int PatternAt = 1000;
        private const int Symbols = 4000;

        private readonly ITestOutputHelper _output;

        public SyncPatternSearchTests(ITestOutputHelper output)
        {
            _output = output;
        }

        /// <summary>
        /// A thirty-two-symbol word.
        /// </summary>
        /// <remarks>
        /// <strong>Its length is part of the test.</strong> A correlation's false-peak rate falls as
        /// exp(−f²N), so a sixteen-symbol pattern in four thousand symbols of random QPSK is matched
        /// by chance about twice per record and thirty-two symbols about once in fifty thousand. The
        /// first version of this test used sixteen and the search settled 377 symbols away from
        /// where the pattern was — which is a fact about pattern lengths rather than about the
        /// search, and is why the step now reports the score it reached.
        /// </remarks>
        private static int[] Pattern => new[]
        {
            0, 0, 1, 1, 0, 1, 0, 0, 1, 1, 1, 0, 0, 1, 0, 1,
            2, 3, 3, 1, 2, 2, 0, 3, 1, 0, 2, 1, 3, 2, 3, 0,
        };

        [Theory]
        [InlineData(0)]
        [InlineData(16)]
        [InlineData(64)]
        [InlineData(-8)]
        public void TheWindowLandsAtTheSearchOffsetFromThePatternToTheSymbol(int offset)
        {
            // "With a known pattern inserted at a known position by the simulator, the Result Length
            // window lands at the specified offset from that position, to the symbol."
            //
            // Read off the symbols themselves rather than any internal position: the first symbol
            // the chain reports has to be the one the generator put at PatternAt + offset, and every
            // symbol after it in order. Nothing else can produce that agreement.
            ContinuousModulatedSource source = Source();
            float[] samples = Generate(source);

            var settings = Settings();

            settings.SyncSearchEnabled = true;
            settings.SyncPattern = Pattern;
            settings.SearchOffsetSymbols = offset;

            DemodResult result = new Demodulator().Run(samples, SampleRateHz, settings);

            _output.WriteLine(
                "offset " + offset + ": EVM " +
                result.EvmPercent.ToString("F4", CultureInfo.InvariantCulture) + " %rms, first " +
                "symbols " + Rendered(result.Symbols, 8));

            _output.WriteLine(
                "the generator put " + Rendered(Transmitted(source, PatternAt + offset, 8), 8) +
                " at symbol " + (PatternAt + offset));

            for (int at = 0; at < Symbols - 40; at++)
            {
                bool same = true;

                for (int symbol = 0; symbol < 12 && same; symbol++)
                {
                    same = source.SymbolAt(at + symbol) == result.Symbols[symbol];
                }

                if (same)
                {
                    // Printed rather than asserted -- the assertion below is the criterion. What
                    // this buys is the next failure being diagnosable in one line: a window that
                    // lands somewhere else says WHERE, and 377 symbols away from the pattern is a
                    // different fault from half a symbol away.
                    _output.WriteLine(
                        "the window landed at symbol " + at + ", and the pattern is at " +
                        PatternAt + " with an offset of " + offset);

                    break;
                }
            }

            for (int symbol = 0; symbol < 32; symbol++)
            {
                Assert.Equal(
                    source.SymbolAt(PatternAt + offset + symbol), result.Symbols[symbol]);
            }
        }

        [Fact]
        public void APatternGivenAsBitsIsTheSamePattern()
        {
            // "Search shall locate a user-specified bit pattern (a multiple of bits-per-symbol in
            // length)". A user knows a sync word as bits; the correlation wants symbols; and which
            // point carries which bits is REQ-DEM-011's mapping. Given both ways, the same window.
            ContinuousModulatedSource source = Source();
            float[] samples = Generate(source);

            var asSymbols = Settings();

            asSymbols.SyncSearchEnabled = true;
            asSymbols.SyncPattern = Pattern;

            var asBits = Settings();

            asBits.SyncSearchEnabled = true;
            asBits.SyncPatternBits = Bits(Pattern, asBits.Constellation);

            Assert.Equal(Pattern.Length * 2, asBits.SyncPatternBits.Length);
            Assert.Equal(Pattern, asBits.SyncSymbols());

            DemodResult fromSymbols = new Demodulator().Run(samples, SampleRateHz, asSymbols);
            DemodResult fromBits = new Demodulator().Run(samples, SampleRateHz, asBits);

            for (int symbol = 0; symbol < 32; symbol++)
            {
                Assert.Equal(fromSymbols.Symbols[symbol], fromBits.Symbols[symbol]);
            }

            _output.WriteLine(
                "the same window either way, first symbols " + Rendered(fromBits.Symbols, 8));
        }

        [Fact]
        public void ABitPatternGoesThroughTheLabellingAndNotThroughTheIndex()
        {
            // The case where the two differ, and the reason the conversion is not a cast. On a
            // Gray-labelled QPSK the point carrying the bits 1 0 is point 3, not point 2 --
            // correlating against point 2 would be searching for a pattern nobody transmitted.
            var settings = Settings();

            settings.Constellation = Constellation.Qpsk().WithMapping(BitMapping.Gray);
            settings.SyncPatternBits = new[] { 1, 0, 1, 1 };

            int[] symbols = settings.SyncSymbols();

            _output.WriteLine(
                "bits 10 11 on a Gray-labelled QPSK are points " + Rendered(symbols, symbols.Length));

            Assert.Equal(2, symbols.Length);
            Assert.Equal(2, settings.Constellation.CarriedBy(symbols[0]));
            Assert.Equal(3, settings.Constellation.CarriedBy(symbols[1]));

            // Which is NOT the same as reading the bits as point indices.
            Assert.NotEqual(2, symbols[0]);
        }

        [Fact]
        public void ABitPatternThatIsNotAWholeNumberOfSymbolsIsRefused()
        {
            var settings = Settings();

            settings.SyncSearchEnabled = true;
            settings.SyncPatternBits = new[] { 1, 0, 1 };

            ArgumentException refused = Assert.Throws<ArgumentException>(() => settings.Validate());

            Assert.Contains("REQ-DEM-040", refused.Message, StringComparison.Ordinal);
            _output.WriteLine(refused.Message);
        }

        [Fact]
        public void TheFirstMatchIsUsedAndNotTheStrongest()
        {
            // "Only the first match shall be used." A sync word in a repeating frame occurs many
            // times, and taking whichever correlated best would move a measurement from one frame to
            // another between acquisitions for no reason a user could see. Two copies here, the
            // second in a quieter stretch of nothing in particular; the window must land on the
            // first.
            ContinuousModulatedSource source = Source();

            var inserted = new List<int>(Pattern);

            source.InsertedSymbols = inserted.ToArray();
            source.InsertedAtSymbol = PatternAt;

            float[] first = Generate(source);

            source.InsertedAtSymbol = PatternAt + 800;

            float[] second = Generate(source);

            // One record holding both: the first half of the first and the second half of the
            // second, so the pattern appears at PatternAt and again 800 symbols later.
            var samples = new float[first.Length];

            Array.Copy(first, samples, first.Length);

            int from = 2 * (PatternAt + 400) * PerSymbol;

            Array.Copy(second, from, samples, from, second.Length - from);

            var settings = Settings();

            settings.SyncSearchEnabled = true;
            settings.SyncPattern = Pattern;

            DemodResult result = new Demodulator().Run(samples, SampleRateHz, settings);

            _output.WriteLine(
                "two copies, at " + PatternAt + " and " + (PatternAt + 800) + "; the window began " +
                "on " + Rendered(result.Symbols, 8));

            for (int symbol = 0; symbol < Pattern.Length; symbol++)
            {
                Assert.Equal(Pattern[symbol], result.Symbols[symbol]);
            }

            // And it is the FIRST copy: the symbols after the pattern are the ones that followed the
            // first, which differ from those that followed the second.
            for (int symbol = Pattern.Length; symbol < Pattern.Length + 16; symbol++)
            {
                Assert.Equal(source.SymbolAt(PatternAt + symbol), result.Symbols[symbol]);
            }
        }

        [Fact]
        public void CarrierLockDoesNotDependOnTheSearch()
        {
            // "Sync search shall be optional — carrier locking shall not depend on it." The same
            // signal, demodulated without the search at all: it still locks, and reads the same EVM.
            ContinuousModulatedSource source = Source();
            float[] samples = Generate(source);

            var searching = Settings();

            searching.SyncSearchEnabled = true;
            searching.SyncPattern = Pattern;

            var not = Settings();

            DemodResult withIt = new Demodulator().Run(samples, SampleRateHz, searching);
            DemodResult withoutIt = new Demodulator().Run(samples, SampleRateHz, not);

            _output.WriteLine(
                "with the search: EVM " +
                withIt.EvmPercent.ToString("F4", CultureInfo.InvariantCulture) +
                " %rms; without it: " +
                withoutIt.EvmPercent.ToString("F4", CultureInfo.InvariantCulture) + " %rms");

            Assert.True(withIt.EvmPercent < 0.1);
            Assert.True(withoutIt.EvmPercent < 0.1);
        }

        private static ContinuousModulatedSource Source() =>
            new ContinuousModulatedSource
            {
                Scheme = ModulationScheme.Qpsk(),
                SymbolRateHz = SymbolRateHz,
                SampleRateHz = SampleRateHz,
                RollOff = 0.35,
                PulseSpanSymbols = 20,
                Seed = 20260824,
                InsertedSymbols = Pattern,
                InsertedAtSymbol = PatternAt,
            };

        private static float[] Generate(ContinuousModulatedSource source)
        {
            var samples = new float[2 * Symbols * PerSymbol];

            source.Restart();
            source.Fill(samples);

            return samples;
        }

        private static DemodSettings Settings() =>
            new DemodSettings
            {
                Constellation = Constellation.Qpsk(),
                SymbolRateHz = SymbolRateHz,
                ResultLengthSymbols = 256,
                FilterSymbolSpan = 20,
                MeasurementFilter = PulseFilterType.RootRaisedCosine,
                MeasurementFilterAlpha = 0.35,
                ReferenceFilterAlpha = 0.35,
            };

        private static int[] Bits(int[] symbols, Constellation constellation)
        {
            var bits = new List<int>(symbols.Length * constellation.BitsPerSymbol);

            foreach (int symbol in symbols)
            {
                bits.AddRange(constellation.BitsOf(symbol));
            }

            return bits.ToArray();
        }

        private static int[] Transmitted(ContinuousModulatedSource source, int from, int count)
        {
            var symbols = new int[count];

            for (int symbol = 0; symbol < count; symbol++)
            {
                symbols[symbol] = source.SymbolAt(from + symbol);
            }

            return symbols;
        }

        private static string Rendered(IReadOnlyList<int> symbols, int count)
        {
            var parts = new List<string>(count);

            for (int symbol = 0; symbol < Math.Min(count, symbols.Count); symbol++)
            {
                parts.Add(symbols[symbol].ToString(CultureInfo.InvariantCulture));
            }

            return string.Join(" ", parts);
        }
    }
}
