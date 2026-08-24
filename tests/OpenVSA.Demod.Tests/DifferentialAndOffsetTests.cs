using System;
using System.Collections.Generic;
using System.Globalization;
using OpenVSA.Demod.Chain;
using OpenVSA.Demod.Results;
using OpenVSA.Demod.Signal;
using OpenVSA.Synthesis;
using Xunit;
using Xunit.Abstractions;

namespace OpenVSA.Demod.Tests
{
    /// <summary>
    /// <c>REQ-DEM-012</c>: the two things a point list alone does not say — when each axis is read,
    /// and what a symbol's bits are read against.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Both halves of this requirement fail quietly, which is what the tests are shaped
    /// around.</strong> An offset signal demodulated as though it were not still converges and still
    /// reports an EVM; a differentially encoded signal decoded absolutely still produces a
    /// well-formed bit stream. Neither throws, neither warns, and both are wrong. So each test here
    /// measures the wrong answer as well as the right one, and says what the wrong one is — an EVM
    /// figure for the first, and a predicted bit stream for the second.
    /// </para>
    /// <para>
    /// <strong>What these prove and what they do not.</strong> The generator and the demodulator
    /// share one point list and one definition of the difference, so a pass says the chain reads the
    /// stagger, takes the rotation out and applies the reference — and says nothing about whether
    /// those conventions are anybody else's. The E4438C offers <c>OQPSK</c>, <c>P4DQPSK</c> and
    /// <c>D8PSK</c>, and settling that is what <c>evidence/req-e44-007/</c> is for.
    /// </para>
    /// </remarks>
    public class DifferentialAndOffsetTests
    {
        /// <summary>Symbols generated for a round trip.</summary>
        private const int Symbols = 4000;

        /// <summary>Samples per symbol out of the generator; a whole multiple of the chain's rate.</summary>
        private const double SampleRateHz = 16e6;

        /// <summary>The symbol rate everything here runs at.</summary>
        private const double SymbolRateHz = 1e6;

        /// <summary>
        /// The transmit pulse's span, matched at both ends. Twenty rather than six for the reason
        /// <c>FormatCatalogueTests</c> gives: a truncated root raised cosine puts real intersymbol
        /// interference in the signal, and 0.1 % cannot be measured with an instrument that injects
        /// 0.29 %.
        /// </summary>
        private const int PulseSpan = 20;

        private readonly ITestOutputHelper _output;

        public DifferentialAndOffsetTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void AnOffsetSignalDemodulatesToNearZeroEvm()
        {
            // REQ-DEM-012's acceptance criterion: "An OQPSK signal generated with a known
            // half-symbol I/Q offset demodulates to near-zero EVM, which it cannot do if the offset
            // is mishandled." The generator staggers its Q axis by half a symbol; nothing else about
            // the signal differs from QPSK.
            Constellation oqpsk = Constellation.ByName("OQPSK");

            Assert.True(oqpsk.IsOffset);

            ContinuousModulatedSource source = SourceFor(oqpsk);
            float[] samples = Generate(source);

            DemodResult result = Demodulate(samples, source, oqpsk);

            _output.WriteLine(
                "OQPSK: " + result.Trace.SymbolCount + " symbols, EVM " +
                result.EvmPercent.ToString("F6", CultureInfo.InvariantCulture) + " %rms, " +
                (result.Converged ? "converged" : "NOT CONVERGED"));

            Assert.True(
                result.EvmPercent < 0.1,
                "OQPSK demodulated at " + result.EvmPercent + " %rms, which is not under 0.1 %.");
        }

        [Fact]
        public void TheSameSignalReadAtOneInstantPerSymbolIsPlausibleAndWrong()
        {
            // The failure REQ-DEM-012 names in as many words: "processing OQPSK at 1 point per
            // symbol yields plausible-looking but wrong EVM". One signal, two constellations that
            // differ only in whether they stagger, and the difference between the two numbers is
            // the whole of what this requirement buys.
            ContinuousModulatedSource source = SourceFor(Constellation.ByName("OQPSK"));
            float[] samples = Generate(source);

            DemodResult staggered =
                Demodulate(samples, source, Constellation.ByName("OQPSK"));

            DemodResult straight =
                Demodulate(samples, source, Constellation.ByName("QPSK"));

            _output.WriteLine(
                "one OQPSK signal: read at two instants a symbol, EVM " +
                staggered.EvmPercent.ToString("F4", CultureInfo.InvariantCulture) +
                " %rms; read at one, EVM " +
                straight.EvmPercent.ToString("F4", CultureInfo.InvariantCulture) + " %rms" +
                (straight.Converged ? ", and it converged" : ", and it did not converge"));

            Assert.True(
                straight.EvmPercent > 20.0 * staggered.EvmPercent,
                "Reading an offset signal at one instant per symbol gave " +
                straight.EvmPercent + " %rms against the staggered reading's " +
                staggered.EvmPercent + " %rms. If those are close, the stagger is not being used.");

            // Plausible, not absurd: it is a number in the region a real signal with a real
            // impairment produces, which is precisely why nothing downstream would question it.
            Assert.True(
                straight.EvmPercent < 100.0,
                "The wrong reading was " + straight.EvmPercent + " %rms, which is not the " +
                "plausible-looking failure the requirement describes.");
        }

        [Theory]
        [InlineData(2)]
        [InlineData(4)]
        [InlineData(8)]
        public void AnOffsetFormatIsReadAtTwoInstantsPerSymbolAtEveryInternalRate(int perSymbol)
        {
            // "Offset formats are processed internally at 2 points per symbol regardless of the
            // display points-per-symbol of REQ-DEM-034; a test asserts the internal rate." Two
            // instants a symbol is a property of the format, so it holds at the requirement's own
            // rate of two points per symbol and at every rate above it.
            Constellation oqpsk = Constellation.ByName("OQPSK");
            ContinuousModulatedSource source = SourceFor(oqpsk);
            float[] samples = Generate(source);

            var settings = SettingsFor(oqpsk, source);

            settings.PointsPerSymbol = perSymbol;

            Assert.Equal(2, settings.InstantsPerSymbol);

            DemodResult result = new Demodulator().Run(samples, source.SampleRateHz, settings);

            _output.WriteLine(
                perSymbol + " points/symbol: EVM " +
                result.EvmPercent.ToString("F6", CultureInfo.InvariantCulture) + " %rms");

            Assert.True(
                result.EvmPercent < 0.5,
                "OQPSK at " + perSymbol + " points per symbol read " + result.EvmPercent + " %rms.");
        }

        [Fact]
        public void AFormatThatDoesNotStaggerIsReadAtOneInstantPerSymbol()
        {
            // The other half of the same statement, and the reason it is a property rather than a
            // setting: nothing but an offset format is read twice.
            var settings = new DemodSettings
            {
                Constellation = Constellation.ByName("QPSK"),
                SymbolRateHz = SymbolRateHz,
            };

            Assert.Equal(1, settings.InstantsPerSymbol);
        }

        [Fact]
        public void AnOddInternalRateIsRefusedForAnOffsetFormat()
        {
            // Half of an odd number of samples is not a sample. The interpolator would resolve it,
            // since the timing estimate is fractional anyway — but the requirement's rate is two
            // points per symbol precisely because that is where the second instant lands on the
            // grid, and an internal rate that quietly did not is worth refusing.
            var settings = new DemodSettings
            {
                Constellation = Constellation.ByName("OQPSK"),
                SymbolRateHz = SymbolRateHz,
                PointsPerSymbol = 5,
            };

            ArgumentException refused = Assert.Throws<ArgumentException>(() => settings.Validate());

            Assert.Contains("REQ-DEM-012", refused.Message, StringComparison.Ordinal);
            _output.WriteLine(refused.Message);
        }

        [Fact]
        public void HalfASymbolAndAQuarterTurnTogetherAreAFreeParameter()
        {
            // Not a defect, and not resolvable here: reading an offset signal half a symbol late and
            // turning it by 90° pairs the Q of one symbol with the I of the next, and every one of
            // those pairs is an exact constellation point. So the two alignments demodulate equally
            // well and carry different bits, and only a sync pattern (REQ-DEM-040) says which is the
            // transmitter's. This measures that rather than asserting it, because an EVM that came
            // back near zero on both is the evidence — and the warning.
            Constellation oqpsk = Constellation.ByName("OQPSK");
            ContinuousModulatedSource source = SourceFor(oqpsk);
            float[] samples = Generate(source);

            // The same signal read half a symbol later: the record is trimmed by half a symbol's
            // worth of samples, so what the chain calls symbol zero is what the transmitter called
            // the Q half of symbol zero.
            int shift = (int)Math.Round(source.SamplesPerSymbol / 2.0);
            var shifted = new float[samples.Length - (2 * shift)];

            Array.Copy(samples, 2 * shift, shifted, 0, shifted.Length);

            DemodResult aligned = Demodulate(samples, source, oqpsk);
            DemodResult late = Demodulate(shifted, source, oqpsk);

            _output.WriteLine(
                "aligned EVM " + aligned.EvmPercent.ToString("F6", CultureInfo.InvariantCulture) +
                " %rms; half a symbol late EVM " +
                late.EvmPercent.ToString("F6", CultureInfo.InvariantCulture) + " %rms");

            Assert.True(late.EvmPercent < 0.1, "The late alignment read " + late.EvmPercent + " %rms.");

            var sent = new int[Symbols];

            for (int symbol = 0; symbol < Symbols; symbol++)
            {
                sent[symbol] = source.SymbolAt(symbol);
            }

            // Both demodulations recover the symbols exactly — each under one of the two pairings,
            // and neither under the other. That is the ambiguity stated as a measurement: the EVM
            // cannot tell them apart and the bits are different.
            string alignedAs = Recovered(aligned.Symbols, sent, oqpsk);
            string lateAs = Recovered(late.Symbols, sent, oqpsk);

            _output.WriteLine("aligned recovered " + alignedAs);
            _output.WriteLine("half a symbol late recovered " + lateAs);

            Assert.NotNull(alignedAs);
            Assert.NotNull(lateAs);

            Assert.True(
                alignedAs.StartsWith("the symbols sent", StringComparison.Ordinal) !=
                lateAs.StartsWith("the symbols sent", StringComparison.Ordinal),
                "Both alignments recovered the same pairing, so shifting the record by half a " +
                "symbol changed nothing — and the arithmetic says it changes which axis is read " +
                "with which.");
        }

        [Theory]
        [InlineData("DQPSK")]
        [InlineData("D8PSK")]
        [InlineData("PI4DQPSK")]
        public void ADifferentialSignalDemodulatesToTheTransmittedBits(string name)
        {
            // The first half of REQ-DEM-012's criterion: "A differentially encoded signal
            // demodulates to the transmitted bits with the correct reference selected."
            Constellation constellation = Constellation.ByName(name);

            Assert.True(constellation.IsDifferential);

            ContinuousModulatedSource source = SourceFor(constellation);
            float[] samples = Generate(source);

            DemodResult result = Demodulate(samples, source, constellation);

            _output.WriteLine(
                name + ": " + result.Trace.SymbolCount + " symbols, EVM " +
                result.EvmPercent.ToString("F6", CultureInfo.InvariantCulture) + " %rms, carrier " +
                "error " + result.CarrierFrequencyErrorHz.ToString(
                    "F3", CultureInfo.InvariantCulture) + " Hz");

            Assert.True(
                result.EvmPercent < 0.1,
                name + " demodulated at " + result.EvmPercent + " %rms.");

            // One symbol shorter than the window, because the first symbol is the reference.
            Assert.Equal(result.Symbols.Count - 1, result.DataSymbols.Count);
            Assert.Equal(
                result.DataSymbols.Count * constellation.BitsPerSymbol, result.Bits.Count);

            var carried = new int[Symbols - 1];

            for (int symbol = 1; symbol < Symbols; symbol++)
            {
                carried[symbol - 1] = source.DataSymbolAt(symbol);
            }

            Assert.Equal(
                result.DataSymbols.Count, LongestAgreement(result.DataSymbols, carried));
        }

        [Fact]
        public void TheWrongReferenceGivesThePredictedWrongBitsRatherThanAWarning()
        {
            // The second half, and the one that makes the selection demonstrably effective: "and to
            // a predictably wrong bit stream with the wrong one". Predictably is the word that
            // matters — the test computes what the wrong selection must return and demands exactly
            // that, so an implementation that ignored the setting could not pass by returning
            // something merely different.
            Constellation dqpsk = Constellation.ByName("DQPSK");
            ContinuousModulatedSource source = SourceFor(dqpsk);
            float[] samples = Generate(source);

            DemodSettings settings = SettingsFor(dqpsk, source);

            settings.DifferentialReference = DifferentialReference.None;

            Assert.False(settings.DecodesDifferentially);

            DemodResult wrong = new Demodulator().Run(samples, source.SampleRateHz, settings);

            // With no differential decode the bits are the encoded symbols themselves — the running
            // accumulation the transmitter sent, not the data it was carrying.
            var encoded = new int[Symbols];

            for (int symbol = 0; symbol < Symbols; symbol++)
            {
                encoded[symbol] = source.SymbolAt(symbol);
            }

            var carried = new int[Symbols - 1];

            for (int symbol = 1; symbol < Symbols; symbol++)
            {
                carried[symbol - 1] = source.DataSymbolAt(symbol);
            }

            int asEncoded = LongestAgreement(wrong.DataSymbols, encoded);
            int asData = LongestAgreement(wrong.DataSymbols, carried);

            _output.WriteLine(
                "DQPSK decoded with the reference forced to None: " + asEncoded + " of " +
                wrong.DataSymbols.Count + " symbols are the ENCODED stream, and " + asData +
                " are the data. EVM " +
                wrong.EvmPercent.ToString("F6", CultureInfo.InvariantCulture) + " %rms.");

            Assert.Equal(wrong.DataSymbols.Count, asEncoded);

            // A quarter of them agree with the data by chance, which is what makes this failure
            // invisible without the prediction: the stream is well-formed, the constellation is
            // clean, and the EVM is as good as the right answer's.
            Assert.True(
                asData < wrong.DataSymbols.Count,
                "The wrong reference returned the data, so the selection is not being applied.");

            Assert.True(
                wrong.EvmPercent < 0.1,
                "The wrong reference should demodulate exactly as well — it is the same waveform " +
                "and the same decisions. It read " + wrong.EvmPercent + " %rms.");
        }

        [Fact]
        public void ForcingADifferentialDecodeOnAFormatThatIsNotOneIsAlsoSelectable()
        {
            // The selection works in both directions, which is what "selectable" means: a plain
            // QPSK signal decoded differentially returns the differences of its symbols. Predicted
            // and demanded, for the same reason as above.
            Constellation qpsk = Constellation.ByName("QPSK");
            ContinuousModulatedSource source = SourceFor(qpsk);
            float[] samples = Generate(source);

            DemodSettings settings = SettingsFor(qpsk, source);

            settings.DifferentialReference = DifferentialReference.PreviousSymbol;

            Assert.True(settings.DecodesDifferentially);

            DemodResult result = new Demodulator().Run(samples, source.SampleRateHz, settings);

            var differences = new int[Symbols - 1];

            for (int symbol = 1; symbol < Symbols; symbol++)
            {
                differences[symbol - 1] = source.DataSymbolAt(symbol);
            }

            _output.WriteLine(
                "QPSK decoded differentially: " +
                LongestAgreement(result.DataSymbols, differences) + " of " +
                result.DataSymbols.Count + " symbols are the differences.");

            Assert.Equal(
                result.DataSymbols.Count, LongestAgreement(result.DataSymbols, differences));
        }

        [Fact]
        public void ADifferentialDecodeIsRefusedOnAConstellationThatIsNotARing()
        {
            // A difference of symbol values is a change of phase only when the values run around a
            // ring. 16QAM's run along rows, so subtracting two of them would give a well-formed bit
            // stream that meant nothing — refused rather than returned.
            var settings = new DemodSettings
            {
                Constellation = Constellation.ByName("16QAM"),
                SymbolRateHz = SymbolRateHz,
                DifferentialReference = DifferentialReference.PreviousSymbol,
            };

            ArgumentException refused = Assert.Throws<ArgumentException>(() => settings.Validate());

            Assert.Contains("ring", refused.Message, StringComparison.Ordinal);
            _output.WriteLine(refused.Message);
        }

        [Fact]
        public void ATurningFormatIsStrippedByThePowerOfItsPositionsNotItsPoints()
        {
            // π/4-DQPSK is four points and eight positions. Step 3 strips the modulation by raising
            // the signal to the power of the constellation's symmetry, and raising this one to the
            // fourth leaves the alternation as a line half a symbol rate from the carrier — which
            // step 3 would then report as the carrier offset, confidently. Eight annihilates both.
            Constellation pi4 = Constellation.ByName("PI4DQPSK");
            ContinuousModulatedSource source = SourceFor(pi4);
            float[] samples = Generate(source);

            DemodResult result = Demodulate(samples, source, pi4);

            _output.WriteLine(
                "PI4DQPSK on a signal with no carrier offset: carrier error " +
                result.CarrierFrequencyErrorHz.ToString("F3", CultureInfo.InvariantCulture) +
                " Hz, against the Rs/8 of " + (SymbolRateHz / 8.0).ToString(
                    "F0", CultureInfo.InvariantCulture) + " Hz that the rotation would look like.");

            // A tenth of a hertz on a megabaud signal: the rotation is out, not fitted as frequency.
            Assert.True(
                Math.Abs(result.CarrierFrequencyErrorHz) < 1.0,
                "The rotation was reported as " + result.CarrierFrequencyErrorHz +
                " Hz of carrier error.");
        }

        [Fact]
        public void ADifferentialStreamSurvivesATurnedConstellation()
        {
            // Why differential encoding exists, and a property worth having a test for: adding the
            // same index to both symbols leaves their difference alone. So the rotation ambiguity
            // that evidence/req-e44-007/ has to search over for QPSK does not arise for DQPSK, and
            // a bit check against a transmitter needs one freedom fewer.
            Constellation dqpsk = Constellation.ByName("DQPSK");
            ContinuousModulatedSource source = SourceFor(dqpsk);

            source.PhaseRadians = Math.PI / 2.0;

            float[] samples = Generate(source);

            DemodResult result = Demodulate(samples, source, dqpsk);

            var carried = new int[Symbols - 1];

            for (int symbol = 1; symbol < Symbols; symbol++)
            {
                carried[symbol - 1] = source.DataSymbolAt(symbol);
            }

            _output.WriteLine(
                "DQPSK transmitted a quarter-turn out: " +
                LongestAgreement(result.DataSymbols, carried) + " of " + result.DataSymbols.Count +
                " symbols recovered, EVM " +
                result.EvmPercent.ToString("F6", CultureInfo.InvariantCulture) + " %rms");

            Assert.Equal(result.DataSymbols.Count, LongestAgreement(result.DataSymbols, carried));
        }

        /// <summary>
        /// Which of an offset format's eight equally-good readings the chain came back with.
        /// </summary>
        /// <param name="recovered">The symbols the chain decided.</param>
        /// <param name="sent">The symbols the generator transmitted.</param>
        /// <param name="constellation">The format, for its geometry.</param>
        /// <returns>A description of the reading, or <c>null</c> if it was none of them.</returns>
        /// <remarks>
        /// <para>
        /// Two freedoms, and they are not independent: a quarter-turn of the constellation (four
        /// values, and the ordinary one every phase-keyed format has) and the half-symbol pairing
        /// (two values, and this format's own). Turned by a quarter and read half a symbol late, an
        /// OQPSK signal gives the pair <c>(Q of symbol k, −I of symbol k+1)</c> — every one an exact
        /// constellation point, at the same EVM, carrying different bits.
        /// </para>
        /// <para>
        /// So a test that demanded the transmitted symbols exactly would be demanding that the
        /// chain resolve something no estimator can, and would pass or fail on where step 7 happened
        /// to put its window. What can be demanded is that the reading is one of the eight, and
        /// which one it was is worth printing rather than swallowing.
        /// </para>
        /// </remarks>
        private static string Recovered(
            IReadOnlyList<int> recovered, int[] sent, Constellation constellation)
        {
            int order = constellation.Count;

            for (int turn = 0; turn < order; turn++)
            {
                var straight = new int[sent.Length];
                var late = new int[sent.Length];

                for (int symbol = 0; symbol < sent.Length; symbol++)
                {
                    int next = sent[(symbol + 1) % sent.Length];

                    ConstellationPoint here = constellation.Points[sent[symbol]];
                    ConstellationPoint after = constellation.Points[next];

                    straight[symbol] = (sent[symbol] + turn) % order;
                    late[symbol] = (constellation.Decide(here.Q, -after.I) + turn) % order;
                }

                if (LongestAgreement(recovered, straight) == recovered.Count)
                {
                    return "the symbols sent, turned by " + (90 * turn) + "°";
                }

                if (LongestAgreement(recovered, late) == recovered.Count)
                {
                    return "the half-symbol pairing (Q of k with I of k+1), turned by " +
                        (90 * turn) + "°";
                }
            }

            return null;
        }

        private static ContinuousModulatedSource SourceFor(Constellation constellation)
        {
            var points = new List<SymbolPoint>(constellation.Count);

            foreach (ConstellationPoint point in constellation.Points)
            {
                points.Add(new SymbolPoint(point.I, point.Q));
            }

            return new ContinuousModulatedSource
            {
                Scheme = ModulationScheme.FromPoints(
                    constellation.Name,
                    points,
                    constellation.IsOffset,
                    constellation.RotationPerSymbolRadians),
                SymbolRateHz = SymbolRateHz,
                SampleRateHz = SampleRateHz,
                RollOff = 0.35,
                PulseSpanSymbols = PulseSpan,
                Seed = 20260824,
            };
        }

        private static float[] Generate(ContinuousModulatedSource source)
        {
            var samples = new float[2 * (int)Math.Ceiling(Symbols * source.SamplesPerSymbol)];

            source.Restart();
            source.Fill(samples);

            return samples;
        }

        private static DemodSettings SettingsFor(
            Constellation constellation, ContinuousModulatedSource source) =>
            new DemodSettings
            {
                Constellation = constellation,
                SymbolRateHz = source.SymbolRateHz,
                ResultLengthSymbols = 512,
                FilterSymbolSpan = source.PulseSpanSymbols,
                MeasurementFilter = PulseFilterType.RootRaisedCosine,
                MeasurementFilterAlpha = source.RollOff,
                ReferenceFilterAlpha = source.RollOff,
            };

        private static DemodResult Demodulate(
            float[] samples, ContinuousModulatedSource source, Constellation constellation) =>
            new Demodulator().Run(
                samples, source.SampleRateHz, SettingsFor(constellation, source));

        /// <summary>
        /// How many of the recovered symbols agree with the transmitted ones, at the best alignment.
        /// </summary>
        /// <remarks>
        /// The chain returns a window from somewhere inside the record and does not say where it
        /// started, so the offset is searched — the same helper, and the same reasoning, as
        /// <c>FormatCatalogueTests</c>.
        /// </remarks>
        private static int LongestAgreement(IReadOnlyList<int> recovered, int[] sent)
        {
            int best = 0;

            for (int offset = 0; offset < sent.Length; offset++)
            {
                int matched = 0;

                for (int index = 0; index < recovered.Count; index++)
                {
                    if (recovered[index] == sent[(offset + index) % sent.Length])
                    {
                        matched++;
                    }
                }

                if (matched > best)
                {
                    best = matched;
                }

                if (best == recovered.Count)
                {
                    break;
                }
            }

            return best;
        }
    }
}
