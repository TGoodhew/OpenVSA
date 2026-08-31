using System;
using System.Collections.Generic;
using System.Globalization;
using OpenVSA.Demod.Chain;
using OpenVSA.Demod.Results;
using OpenVSA.Demod.Signal;
using OpenVSA.Demod.Tests.Signals;
using OpenVSA.Synthesis;
using Xunit;
using Xunit.Abstractions;

namespace OpenVSA.Demod.Tests
{
    /// <summary>
    /// <c>REQ-DEM-010</c>'s catalogue: every format the chain can demodulate as a point list, and
    /// its acceptance criterion — generate, demodulate, and get the bits back.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>What a round trip here proves, and what it does not.</strong> The generator and the
    /// demodulator take their points from the same <see cref="Constellation"/>, so a pass says the
    /// chain's timing, carrier, gain and decisions recover what was sent for a constellation of that
    /// shape and size. It says nothing about whether the shape matches anybody's standard: both ends
    /// would be wrong together. That question was answered for QPSK against an E4438C on
    /// 24 August 2026 — its Gray-coded variant scored 75.10 % against this natural mapping and was
    /// correctly rejected — and it is answered for the rest of the catalogue only when the same is
    /// done for each. See <c>evidence/req-e44-007/</c>.
    /// </para>
    /// <para>
    /// <strong>The formats absent from these tests are absent from the product.</strong> The
    /// frequency-keyed, vestigial-sideband and shaped-offset rows of the requirement are not point
    /// lists and the chain cannot demodulate them yet; they arrive with <c>REQ-DEM-021</c>. A test
    /// that skipped them quietly would leave the catalogue looking complete, so
    /// <see cref="TheCatalogueSaysWhichRowsOfTheRequirementItDoesNotYetCover"/> names them instead.
    /// The offset and differential rows were among them until <c>REQ-DEM-012</c> arrived on
    /// 24 August 2026; <c>DifferentialAndOffsetTests</c> is where those are exercised, because what
    /// they need proving is not the point list.
    /// </para>
    /// </remarks>
    public class FormatCatalogueTests
    {
        /// <summary>
        /// Symbols in a round trip — enough for the estimators to have something to work with, and
        /// enough that a 4096-point constellation is visited more than once on average.
        /// </summary>
        private const int Symbols = 4000;

        private readonly ITestOutputHelper _output;

        public FormatCatalogueTests(ITestOutputHelper output)
        {
            _output = output;
        }

        public static IEnumerable<object[]> EveryName()
        {
            foreach (string name in Constellation.Names)
            {
                yield return new object[] { name };
            }
        }

        [Theory]
        [MemberData(nameof(EveryName))]
        public void EveryNameResolvesAndCallsItselfWhatItWasAskedFor(string name)
        {
            // The list a user interface offers and the factory a stored setup reaches are two
            // different pieces of code, and a name in one that the other does not answer is a
            // format that appears in a menu and throws when chosen.
            Constellation constellation = Constellation.ByName(name);

            Assert.Equal(name, constellation.Name);
            Assert.Equal(name, Constellation.ByName(name.ToLowerInvariant()).Name);
        }

        [Theory]
        [MemberData(nameof(EveryName))]
        public void EveryConstellationIsWellFormed(string name)
        {
            Constellation constellation = Constellation.ByName(name);

            Assert.Equal(1 << constellation.BitsPerSymbol, constellation.Count);

            // Unit mean power, which is the convention the joint refinement's gain parameter and
            // EVM's normalisation are both stated against.
            double power = 0.0;

            foreach (ConstellationPoint point in constellation.Points)
            {
                power += (point.I * point.I) + (point.Q * point.Q);
            }

            Assert.Equal(1.0, power / constellation.Count, 9);

            // Distinct points. Two symbols at one place cannot be told apart, and the decision would
            // silently always choose the lower one.
            var seen = new HashSet<string>();

            foreach (ConstellationPoint point in constellation.Points)
            {
                Assert.True(
                    seen.Add(
                        point.I.ToString("F9", CultureInfo.InvariantCulture) + "," +
                        point.Q.ToString("F9", CultureInfo.InvariantCulture)),
                    name + " has two symbols at the same point.");
            }

            // The eye count is derived from the geometry, never declared beside it.
            var levels = new HashSet<double>();

            foreach (ConstellationPoint point in constellation.Points)
            {
                levels.Add(Math.Round(point.I, 6));
            }

            Assert.Equal(levels.Count, constellation.LevelsPerAxis);
        }

        [Theory]
        [MemberData(nameof(EveryName))]
        public void EveryPointDecidesToItself(string name)
        {
            // The weakest possible statement about a decision rule, and the one that catches a
            // constellation whose points are too close to separate: handed a point exactly, the
            // decision must return that symbol.
            Constellation constellation = Constellation.ByName(name);

            for (int symbol = 0; symbol < constellation.Count; symbol++)
            {
                ConstellationPoint point = constellation.Points[symbol];

                Assert.Equal(symbol, constellation.Decide(point.I, point.Q));
            }
        }

        [Theory]
        // Every format in the catalogue, through the whole chain. The larger QAMs are slow rather
        // than difficult -- the decision is a linear scan -- so they are here in full.
        [InlineData("BPSK")]
        [InlineData("QPSK")]
        [InlineData("8PSK")]
        [InlineData("16PSK")]
        [InlineData("OOK")]
        [InlineData("16QAM")]
        [InlineData("32QAM")]
        [InlineData("64QAM")]
        [InlineData("128QAM")]
        [InlineData("256QAM")]
        [InlineData("512QAM")]
        [InlineData("1024QAM")]
        [InlineData("2048QAM")]
        [InlineData("4096QAM")]
        [InlineData("16STARQAM")]
        [InlineData("32STARQAM")]
        public void EachFormatRoundTripsThroughTheChain(string name)
        {
            // REQ-DEM-010's acceptance criterion, word for word: "Each format round-trips through
            // the simulator: generate -> demodulate -> recovered bits identical to transmitted bits
            // at high SNR, RMS EVM < 0.1 %."
            //
            // Against OpenVSA's own simulator, which is what "the simulator" means and what the
            // product ships. The test-only generator in Signals/ has a floor of about 0.26 %rms --
            // BlockEstimationTests documents 0.27 % as its clean figure -- because its transmit
            // pulse is truncated at eight symbols against the chain's six and the residual is
            // intersymbol interference, not noise. Measuring a 0.1 % criterion with a 0.26 %
            // instrument would only ever have one answer.
            Constellation constellation = Constellation.ByName(name);

            var source = new ContinuousModulatedSource
            {
                Scheme = SchemeFor(constellation),
                SymbolRateHz = 1e6,

                // Sixteen samples a symbol, a whole multiple of the chain's four points a symbol, so
                // no decision instant falls between samples. That is deliberate: the interpolation
                // needed when it does is a separate claim, tested separately at 5.3 samples a symbol.
                SampleRateHz = 16e6,
                RollOff = 0.35,

                // Twenty rather than the default six, because a root raised cosine cut off after six
                // symbols is no longer the filter whose matched pair is a Nyquist pulse, and the
                // residue is intersymbol interference IN THE SIGNAL. Measured on 24 August 2026: the
                // same chain reads 0.287 %rms at a span of six and 0.020 % at twenty. A criterion of
                // a tenth of a per cent cannot be measured with an instrument that injects two
                // tenths, and lowering the criterion to fit would have measured the generator.
                PulseSpanSymbols = 20,
                Seed = 20260824,
            };

            var samples = new float[2 * (int)Math.Ceiling(Symbols * source.SamplesPerSymbol)];

            source.Fill(samples);

            var settings = new DemodSettings
            {
                Constellation = constellation,
                SymbolRateHz = source.SymbolRateHz,
                ResultLengthSymbols = 512,

                // Matched to the transmit pulse. The shorter of the two sets the floor, so leaving
                // this at six would have thrown away the longer transmit pulse entirely.
                FilterSymbolSpan = source.PulseSpanSymbols,
                MeasurementFilter = PulseFilterType.RootRaisedCosine,
                MeasurementFilterAlpha = source.RollOff,
                ReferenceFilterAlpha = source.RollOff,
            };

            DemodResult result = new Demodulator().Run(samples, source.SampleRateHz, settings);

            _output.WriteLine(
                name + ": " + result.Trace.SymbolCount + " symbols, EVM " +
                result.EvmPercent.ToString("F6", CultureInfo.InvariantCulture) + " %rms, " +
                (result.Converged ? "converged" : "NOT CONVERGED") + " in " + result.Passes.Count +
                " pass(es)");

            Assert.True(
                result.EvmPercent < 0.1,
                name + " demodulated at " + result.EvmPercent + " %rms, which is not under 0.1 %.");

            // "Recovered bits identical to transmitted bits". The recovered block is a window
            // somewhere inside what was sent, and where is the demodulator's business, so the
            // comparison finds the alignment and then demands every symbol of it.
            var sent = new int[Symbols];

            for (int symbol = 0; symbol < Symbols; symbol++)
            {
                sent[symbol] = source.SymbolAt(symbol);
            }

            Assert.Equal(result.Symbols.Count, LongestAgreement(result.Symbols, sent));
        }

        [Theory]
        [InlineData("QPSK")]
        [InlineData("8PSK")]
        [InlineData("16QAM")]
        [InlineData("32QAM")]
        [InlineData("64QAM")]
        [InlineData("16STARQAM")]
        public void TheSymbolsSurviveANonIntegerSampleRate(string name)
        {
            // A separate claim from the criterion above, and the reason the test-only generator is
            // still worth running: it clocks 5.3 samples a symbol, so every decision instant falls
            // between samples and the chain has to interpolate to reach it. Its 0.26 %rms floor is
            // intersymbol interference from a pulse truncated at a different span, which is exactly
            // the thing that must NOT move a decision -- so what is asserted here is the symbols,
            // not the error vector.
            Constellation constellation = Constellation.ByName(name);

            var source = new QpskSource(20260824) { Constellation = constellation };
            float[] samples = source.Generate(Symbols);

            var settings = new DemodSettings
            {
                Constellation = constellation,
                SymbolRateHz = source.SymbolRateHz,
                ResultLengthSymbols = 512,
                MeasurementFilter = PulseFilterType.RootRaisedCosine,
                MeasurementFilterAlpha = source.Alpha,
                ReferenceFilterAlpha = source.Alpha,
            };

            DemodResult result = new Demodulator().Run(samples, source.SampleRateHz, settings);

            _output.WriteLine(
                name + " at " + (source.SampleRateHz / source.SymbolRateHz).ToString(
                    "F1", CultureInfo.InvariantCulture) + " samples/symbol: EVM " +
                result.EvmPercent.ToString("F5", CultureInfo.InvariantCulture) + " %rms");

            Assert.Equal(
                result.Symbols.Count, LongestAgreement(result.Symbols, source.Symbols));
        }

        [Fact]
        public void ARingCountAndPointCountBeyondTheRequirementAreRefusedRatherThanClamped()
        {
            // "up to 8 arbitrarily-spaced rings, 256 points". A constellation quietly reduced to fit
            // would demodulate, report an EVM, and be measuring something nobody asked for.
            var tooManyRings = new List<Constellation.ApskRing>();

            for (int ring = 0; ring < 9; ring++)
            {
                tooManyRings.Add(new Constellation.ApskRing(1.0 + ring, 4));
            }

            ArgumentException rings = Assert.Throws<ArgumentException>(
                () => Constellation.Apsk("9 rings", tooManyRings));

            Assert.Contains("8", rings.Message, StringComparison.Ordinal);

            var tooManyPoints = new List<Constellation.ApskRing>
            {
                new Constellation.ApskRing(1.0, 256),
                new Constellation.ApskRing(2.0, 256),
            };

            ArgumentException points = Assert.Throws<ArgumentException>(
                () => Constellation.Apsk("512 points", tooManyPoints));

            Assert.Contains("256", points.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void AnApskConstellationPutsItsPointsOnTheRingsItWasGiven()
        {
            // Radii in the ratio asked for, and the phase honoured: the geometry is the whole of
            // what a user defining rings is specifying, so it is checked rather than assumed.
            var rings = new List<Constellation.ApskRing>
            {
                new Constellation.ApskRing(1.0, 4, Math.PI / 4.0),
                new Constellation.ApskRing(2.5, 12),
            };

            Constellation constellation = Constellation.Apsk("16APSK", rings);

            Assert.Equal(16, constellation.Count);
            Assert.Equal(4, constellation.BitsPerSymbol);
            Assert.Equal(ModulationFamily.Apsk, constellation.Family);

            double inner = Radius(constellation.Points[0]);
            double outer = Radius(constellation.Points[4]);

            Assert.Equal(2.5, outer / inner, 6);

            // The first inner point was asked for at 45 degrees, so its two coordinates are equal.
            Assert.Equal(constellation.Points[0].I, constellation.Points[0].Q, 9);
        }

        [Theory]
        [InlineData(32, 6, 1)]
        [InlineData(128, 12, 2)]
        [InlineData(512, 24, 4)]
        [InlineData(2048, 48, 8)]
        public void ACrossQamIsASquareWithItsCornersRemoved(int order, int side, int corner)
        {
            // The construction the standards use, checked as a shape and not as a point list: on the
            // grid the square would have, every cell is occupied except the four corner blocks, and
            // those are empty. Recovering the grid from the points is the whole test -- a rectangle
            // or a lopsided cut would satisfy the point count and fail here.
            Constellation constellation = Constellation.Qam(order);

            Assert.Equal(order, constellation.Count);
            Assert.Equal(side, constellation.LevelsPerAxis);
            Assert.Equal(side * side - (4 * corner * corner), order);

            var axis = new List<double>();

            foreach (ConstellationPoint point in constellation.Points)
            {
                double level = Math.Round(point.I, 6);

                if (!axis.Contains(level))
                {
                    axis.Add(level);
                }
            }

            axis.Sort();

            Assert.Equal(side, axis.Count);

            // Evenly spaced, which is what makes it a QAM grid rather than an APSK ring set.
            double step = axis[1] - axis[0];

            for (int level = 1; level < axis.Count; level++)
            {
                double spacing = axis[level] - axis[level - 1];

                // An ABSOLUTE tolerance of twice the rounding quantum, not a relative one. The
                // levels were rounded to six decimals to deduplicate them, so each difference
                // carries up to 1e-6 of that rounding regardless of how big the step is -- and a
                // tolerance that scaled with the step was tighter than the rounding for every
                // constellation whose levels are close together, which is all of the large ones.
                Assert.True(
                    Math.Abs(spacing - step) <= 2e-6,
                    "Level " + level + " is " + spacing + " from the last, not " + step + ".");
            }

            var occupied = new HashSet<int>();

            foreach (ConstellationPoint point in constellation.Points)
            {
                int i = axis.IndexOf(Math.Round(point.I, 6));
                int q = axis.IndexOf(Math.Round(point.Q, 6));

                Assert.True(i >= 0 && q >= 0, "A point sits off the grid its own levels define.");
                Assert.True(occupied.Add((i * side) + q), "Two points share a cell.");
            }

            for (int i = 0; i < side; i++)
            {
                for (int q = 0; q < side; q++)
                {
                    bool inCorner =
                        (i < corner || i >= side - corner) && (q < corner || q >= side - corner);

                    Assert.Equal(!inCorner, occupied.Contains((i * side) + q));
                }
            }

            _output.WriteLine(
                order + "QAM: " + side + " by " + side + " less " + corner + " by " + corner +
                " from each corner, " + constellation.Count + " points on " + axis.Count + " levels");
        }

        [Fact]
        public void AQamOrderThatIsNotAPowerOfTwoIsRefused()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => Constellation.Qam(24));
            Assert.Throws<ArgumentOutOfRangeException>(() => Constellation.Psk(6));
        }

        [Fact]
        public void AnUnknownFormatIsRefusedByNameAndSaysWhatIsMissing()
        {
            ArgumentException refused =
                Assert.Throws<ArgumentException>(() => Constellation.ByName("8VSB"));

            Assert.Contains("8VSB", refused.Message, StringComparison.Ordinal);

            // What is still owed needs a discriminator or a vestigial-sideband path rather than a
            // point list, and the message says which. It named GMSK once, and then MSK, GMSK and
            // EDGE arrived: those turned out to want a pulse the catalogue already had and a
            // rotation the chain already did, which is why they are answered and these are not.
            Assert.Contains("frequency-keyed", refused.Message, StringComparison.Ordinal);
            Assert.Contains("vestigial-sideband", refused.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void TheCatalogueSaysWhichRowsOfTheRequirementItDoesNotYetCover()
        {
            // Not a test of behaviour: a test of honesty. REQ-DEM-010 lists formats this build
            // cannot demodulate, and the failure mode worth preventing is the catalogue quietly
            // looking finished. Each of these must still be refused, and when one starts being
            // answered this test is what makes somebody come back and update the requirement's
            // status rather than leaving it half true.
            string[] notYet =
            {
                "SOQPSK", "2FSK", "4FSK", "8FSK", "16FSK", "8VSB", "16VSB", "DVBQAM",
            };

            foreach (string format in notYet)
            {
                Assert.Throws<ArgumentException>(() => Constellation.ByName(format));
            }

            _output.WriteLine(
                "Still owed by REQ-DEM-010: " + string.Join(", ", notYet) +
                " -- the frequency-keyed ones want a discriminator rather than a decision, the " +
                "vestigial-sideband ones a single-sideband path, SOQPSK a continuous-phase " +
                "treatment, and DVB-QAM a quadrant-differential encoding that is not this " +
                "catalogue's whole-symbol one. The offset and differential rows left this list on " +
                "24 August 2026 with REQ-DEM-012; MSK, GMSK and EDGE left it on 31 August with " +
                "REQ-DEM-010 itself, because a rotation and a pulse were all they needed and the " +
                "chain already had both.");
        }

        [Theory]
        [InlineData("3PI8-8PSK", PulseFilterType.Edge, 8)]
        [InlineData("MSK1", PulseFilterType.Msk, 0)]
        [InlineData("MSK2", PulseFilterType.Msk, 0)]
        [InlineData("GMSK", PulseFilterType.Edge, 3)]
        public void AFormatWhoseTransmitPulseIsNotARootRaisedCosineRoundTripsToo(
            string name, PulseFilterType pulse, int equalisingPasses)
        {
            // REQ-DEM-010's criterion again, for the three formats whose transmit pulse is not a
            // root raised cosine: EDGE's linearised c0(t), MSK's half sine and GMSK's Gaussian.
            //
            // 🔴 THE PULSE IS SENT AS WELL AS EXPECTED, which is what makes this a round trip and
            // not a tautology. The generator is handed the same filter the analyser will match
            // against -- it cannot build one, since OpenVSA.Synthesis sits outside the analysis
            // stack and cannot reference the filter catalogue -- so the signal really carries that
            // pulse rather than a root raised cosine the demodulator is then told to expect.
            //
            // 🔴 AND THE FILTERS ARE THE SAME ON BOTH SIDES, which is not what a Nyquist pair does.
            // A root raised cosine is half of a Nyquist filter and the receiver applies the other
            // half; these pulses are the WHOLE shaping, so the measurement filter is None and the
            // reference is the transmit pulse itself. Matching a half-sine transmit pulse with a
            // half-sine receive filter would be applying the shaping twice.
            //
            // 🔴 TWO OF THESE NEED THE EQUALISER TO MEET THE CRITERION, AND THAT IS THE FORMAT
            // TALKING. MSK's half sine is zero at every symbol instant but its own, so it carries
            // no intersymbol interference and the round trip is exact without one. The linearised
            // GMSK pulse c0(t) spans about three symbols and is NOT a Nyquist pulse: it puts ISI in
            // the signal by construction, which is the price of a constant envelope, and a real
            // receiver for these formats equalises. Measured without it: EDGE 35.1 %rms.
            //
            // EDGE also needs more PASSES than the chain's default three -- 3.1 %rms at three,
            // 0.002 at six -- because the equaliser is decision-directed and eight points seen
            // through that ISI take more than one round of decisions to become trustworthy. GMSK's
            // two points are decided reliably from the start, so it converges in the default three.
            // The chain says so when it runs out: "the equaliser was still updating its
            // coefficients when the chain reached its bound".
            Constellation constellation = Constellation.ByName(name);

            const int PerSymbol = 4;
            const int Span = 12;

            PulseFilter shaping = pulse == PulseFilterType.Edge
                ? PulseFilter.Edge()
                : PulseFilter.Msk();

            double[] taps = shaping.Taps(PerSymbol, Span, FilterRole.Reference);

            var source = new ContinuousModulatedSource
            {
                Scheme = SchemeFor(constellation),
                SymbolRateHz = 1e6,
                SampleRateHz = 16e6,
                PulseSpanSymbols = Span,
                TransmitPulse = taps,
                TransmitPulseSamplesPerSymbol = PerSymbol,
                Seed = 20260831,
            };

            var samples = new float[2 * (int)Math.Ceiling(Symbols * source.SamplesPerSymbol)];

            source.Fill(samples);

            // 🔴 IS IT ACTUALLY THE FORMAT? MSK is a CONSTANT-ENVELOPE modulation, and that is not
            // a detail: it is why the format exists, since a transmitter with no envelope variation
            // can be run at saturation. A pulse that demodulates beautifully and leaves the
            // envelope reaching zero between symbols has produced something else, and the EVM
            // criterion below would pass on it happily. Measured: MSK's own two-symbol pulse holds
            // the envelope to 0.17 dB, and the one-symbol half sine it is easily confused with lets
            // it fall to zero -- 240 dB of variation, EVM 0.000000 %rms, and not MSK.
            //
            // EDGE is exempt because EDGE is not constant envelope and was never meant to be: it
            // spends that property to carry three bits a symbol instead of one, which is why its
            // transmitter needs a linear amplifier and GMSK's does not. Measured, its envelope
            // moves by 16.6 dB. Asserting flatness on it would have been asserting somebody else's
            // format.
            double flatness = EnvelopeVariationDb(samples);

            _output.WriteLine(
                name + ": envelope varies by " +
                flatness.ToString("F2", CultureInfo.InvariantCulture) + " dB");

            if (constellation.Family == ModulationFamily.Msk)
            {
                Assert.True(
                    flatness < 3.0,
                    name + "'s envelope varies by " +
                    flatness.ToString("F2", CultureInfo.InvariantCulture) +
                    " dB, so what was generated is not the constant-envelope modulation this " +
                    "format is, whatever it demodulates to.");
            }

            var settings = new DemodSettings
            {
                Constellation = constellation,
                SymbolRateHz = source.SymbolRateHz,
                ResultLengthSymbols = 512,
                FilterSymbolSpan = Span,
                MeasurementFilter = PulseFilterType.None,
                ReferenceFilter = pulse,
                EqualiserEnabled = equalisingPasses > 0,
                EqualiserLengthSymbols = 11,
                MaxPasses = Math.Max(DemodSettings.DefaultMaxPasses, equalisingPasses),
            };

            DemodResult result = new Demodulator().Run(samples, source.SampleRateHz, settings);

            _output.WriteLine(
                name + " through " + pulse + ": " + result.Trace.SymbolCount + " symbols, EVM " +
                result.EvmPercent.ToString("F6", CultureInfo.InvariantCulture) + " %rms, " +
                (result.Converged ? "converged" : "NOT CONVERGED") + ", " +
                (result.Lock.Locked ? "locked" : "NOT LOCKED"));

            foreach (string notice in result.Notices)
            {
                _output.WriteLine("    " + notice);
            }

            Assert.True(
                result.EvmPercent < 0.1,
                name + " round-tripped at " +
                result.EvmPercent.ToString("F4", CultureInfo.InvariantCulture) +
                " %rms, and REQ-DEM-010 asks for better than 0.1 %.");

            // "Recovered bits identical to transmitted bits". A differential format carries its
            // bits in the change, so what the generator sent as data is the change too --
            // DataSymbolAt, not SymbolAt, and the first symbol of a signal carries none.
            var sent = new int[Symbols];

            for (int symbol = 0; symbol < Symbols; symbol++)
            {
                sent[symbol] = constellation.IsDifferential && symbol > 0
                    ? source.DataSymbolAt(symbol)
                    : source.SymbolAt(symbol);
            }

            IReadOnlyList<int> recovered =
                constellation.IsDifferential ? result.DataSymbols : result.Symbols;

            // 🔴 UP TO ONE ROTATION OF THE CONSTELLATION, AND THAT IS PHYSICS RATHER THAN A
            // TOLERANCE. A signal carrying absolute phase contains nothing that says which of its
            // points is point zero: every rotation by a multiple of 2π/M fits the samples exactly
            // as well, so a demodulator handed nothing but the signal cannot choose between them.
            // EDGE shows it and QPSK does not only because this generator sends no carrier phase
            // offset, so the identity happens to be the rotation the estimator lands on; EDGE's own
            // turn moves the window's rotation origin away from the generator's by an amount that
            // depends on where the window fell.
            //
            // What resolves it in a real measurement is a known sequence -- REQ-DEM-040's sync
            // search, or the training sequence a real EDGE receiver correlates against. What this
            // test can honestly claim is that the symbols are the transmitted ones under ONE
            // rotation applied to all of them, and it says which.
            int rotation;
            int agreed = BestAgreement(recovered, sent, constellation.Count, out rotation);

            _output.WriteLine(
                "    " + agreed + " of " + recovered.Count + " symbols agree, at a constellation " +
                "rotation of " + rotation + " point(s)");

            Assert.Equal(recovered.Count, agreed);
        }

        [Fact]
        public void EdgeIsStrippedBySixteenBecauseItsTurnClosesAfterSixteenSymbols()
        {
            // 🔴 Three sixteenths of a turn closes after SIXTEEN symbols, not after the 16/3 that
            // one-over-the-turn gives -- and 16/3 is not a whole number, so the earlier form of the
            // calculation refused EDGE outright as a rotation no power could strip. The two
            // readings agree for every unit fraction, which is why π/4-DQPSK and MSK never showed
            // it up.
            Assert.Equal(16, Constellation.ByName("EDGE").RotationalSymmetry);

            // The unit fractions, unchanged: four points and eight positions, two points and four.
            Assert.Equal(8, Constellation.Pi4Dqpsk().RotationalSymmetry);
            Assert.Equal(4, Constellation.ByName("MSK2").RotationalSymmetry);
        }

        [Fact]
        public void MskAndGmskShowTheMetricsOfTheirFamilyAndNotOfTheirPoints()
        {
            // REQ-DEM-071 keys the error summary's rows on the family. MSK's points are BPSK's, so
            // a family inherited from the points would have offered the I/Q origin, imbalance and
            // quadrature rows -- which come from a linear fit of measured symbols against ideal
            // ones, and MSK is not a linear modulation of a constellation.
            foreach (string name in new[] { "MSK1", "MSK2", "GMSK" })
            {
                Constellation constellation = Constellation.ByName(name);

                Assert.Equal(ModulationFamily.Msk, constellation.Family);

                IReadOnlyList<string> rows =
                    MetricApplicability.LabelsFor(constellation.Family, constellation.IsOffset);

                Assert.Contains("Amp Droop", rows);
                Assert.DoesNotContain("IQ Offset", rows);
                Assert.DoesNotContain("IQ Quad. Error", rows);
            }

            // And EDGE is phase-shift keying, which shows all of them.
            Assert.Equal(ModulationFamily.Psk, Constellation.ByName("EDGE").Family);
        }

        /// <summary>How far a generated signal's envelope moves, in decibels.</summary>
        /// <param name="samples">The signal, interleaved.</param>
        /// <returns>The ratio of the largest magnitude to the smallest, in decibels.</returns>
        /// <remarks>
        /// Measured away from the ends, where the pulse train is still filling and every signal's
        /// envelope rises from nothing.
        /// </remarks>
        private static double EnvelopeVariationDb(float[] samples)
        {
            double least = double.MaxValue;
            double most = 0.0;

            for (int sample = 2000; sample < (samples.Length / 2) - 2000; sample++)
            {
                double i = samples[2 * sample];
                double q = samples[(2 * sample) + 1];
                double magnitude = Math.Sqrt((i * i) + (q * q));

                least = Math.Min(least, magnitude);
                most = Math.Max(most, magnitude);
            }

            return 20.0 * Math.Log10(most / Math.Max(least, 1e-12));
        }

        private static double Radius(ConstellationPoint point) =>
            Math.Sqrt((point.I * point.I) + (point.Q * point.Q));

        /// <summary>
        /// How many of the demodulated symbols agree with the transmitted ones, at the best
        /// alignment.
        /// </summary>
        /// <remarks>
        /// The chain returns a window from somewhere inside the record and is under no obligation to
        /// say where it started, so the offset is searched. Unlike the bench check there is no
        /// rotation to search: the generator applies none, so a constellation that came back turned
        /// would be a defect rather than a convention.
        /// </remarks>
        /// <summary>The generator's view of a constellation from the catalogue.</summary>
        /// <remarks>
        /// <c>OpenVSA.Synthesis</c> sits outside the analysis stack so a transport can use it, so it
        /// cannot reference <c>OpenVSA.Demod</c> and the points are carried across rather than looked
        /// up. Both ends then share one geometry, which is what makes this a test of the chain and
        /// not of the geometry — see the remarks on this class.
        /// </remarks>
        /// <summary>The generator's view of a constellation: its points, and how it is sent.</summary>
        /// <remarks>
        /// 🔴 <strong>The stagger and the turn are part of the signal, not of the point list.</strong>
        /// This helper used to hand over the points alone, which was harmless while every format it
        /// was asked for stood still — and silently wrong the moment one did not. MSK asks for a
        /// right angle every symbol; without it the generator sent plain BPSK, the analyser looked
        /// for a turning constellation, and the round trip came back at 119.8 %rms. The failure was
        /// in the fixture and it read exactly like a failure in the format.
        /// </remarks>
        private static ModulationScheme SchemeFor(Constellation constellation)
        {
            var points = new List<SymbolPoint>(constellation.Count);

            foreach (ConstellationPoint point in constellation.Points)
            {
                points.Add(new SymbolPoint(point.I, point.Q));
            }

            return ModulationScheme.FromPoints(
                constellation.Name,
                points,
                constellation.IsOffset,
                constellation.RotationPerSymbolRadians);
        }

        /// <summary>
        /// The best agreement over both alignments a demodulation is free in: where the window
        /// fell, and which point the constellation calls zero.
        /// </summary>
        /// <param name="recovered">What was demodulated.</param>
        /// <param name="sent">What was transmitted.</param>
        /// <param name="order">How many points the constellation has.</param>
        /// <param name="rotation">Receives the rotation, in points, that agreed best.</param>
        /// <returns>How many symbols agree at the best of the two.</returns>
        /// <remarks>
        /// <strong>Searching a freedom is not the same as excusing an error, and the difference is
        /// how much is left over.</strong> A rotation moves every symbol by the same amount, so a
        /// wrong constellation cannot hide in it: there are only <em>order</em> rotations, and one
        /// of them has to account for EVERY symbol. A demodulation that had genuinely mis-decided
        /// would agree with none of them beyond chance, which for eight points is one symbol in
        /// eight.
        /// </remarks>
        private static int BestAgreement(
            IReadOnlyList<int> recovered, int[] sent, int order, out int rotation)
        {
            int best = 0;

            rotation = 0;

            for (int turn = 0; turn < order; turn++)
            {
                var turned = new int[sent.Length];

                for (int symbol = 0; symbol < sent.Length; symbol++)
                {
                    turned[symbol] = (sent[symbol] + turn) % order;
                }

                int agreed = LongestAgreement(recovered, turned);

                if (agreed > best)
                {
                    best = agreed;
                    rotation = turn;
                }
            }

            return best;
        }

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
