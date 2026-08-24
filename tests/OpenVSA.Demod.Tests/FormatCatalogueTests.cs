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
    /// <strong>The formats absent from these tests are absent from the product.</strong> The offset,
    /// differential, frequency-keyed and vestigial-sideband rows of the requirement are not point
    /// lists and the chain cannot demodulate them yet; they arrive with <c>REQ-DEM-012</c> and
    /// <c>REQ-DEM-021</c>. A test that skipped them quietly would leave the catalogue looking
    /// complete, so <see cref="TheCatalogueSaysWhichRowsOfTheRequirementItDoesNotYetCover"/> names
    /// them instead.
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
                Assert.Throws<ArgumentException>(() => Constellation.ByName("GMSK"));

            Assert.Contains("GMSK", refused.Message, StringComparison.Ordinal);
            Assert.Contains("REQ-DEM-012", refused.Message, StringComparison.Ordinal);
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
                "OQPSK", "SOQPSK", "DQPSK", "PI4DQPSK", "D8PSK",
                "MSK", "GMSK", "2FSK", "4FSK", "8FSK", "16FSK", "8VSB", "16VSB", "DVBQAM",
            };

            foreach (string format in notYet)
            {
                Assert.Throws<ArgumentException>(() => Constellation.ByName(format));
            }

            _output.WriteLine(
                "Still owed by REQ-DEM-010: " + string.Join(", ", notYet) +
                " -- offset and differential handling is REQ-DEM-012, EDGE's pulse REQ-DEM-021.");
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
        private static ModulationScheme SchemeFor(Constellation constellation)
        {
            var points = new List<SymbolPoint>(constellation.Count);

            foreach (ConstellationPoint point in constellation.Points)
            {
                points.Add(new SymbolPoint(point.I, point.Q));
            }

            return ModulationScheme.FromPoints(constellation.Name, points);
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
