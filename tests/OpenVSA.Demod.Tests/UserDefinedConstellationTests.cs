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
    /// <c>REQ-DEM-011</c>: a constellation a user defined, and the labelling they chose for it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The geometry is measurable and the labelling is not.</strong> Where the points sit
    /// shows up in the EVM; which bits each one carries does not, because every labelling of the
    /// same points demodulates identically and reports the same error vector. So the geometry is
    /// tested by demodulating, and the labelling is tested by comparing bits with what was sent —
    /// two different kinds of evidence, and the second is the only one that can catch a wrong
    /// mapping.
    /// </para>
    /// <para>
    /// The measured case is on the bench, not here: an E4438C's Gray-labelled formats were recovered
    /// symbol for symbol only after this requirement existed. <c>evidence/req-dem-011/</c>.
    /// </para>
    /// </remarks>
    public class UserDefinedConstellationTests
    {
        private const int Symbols = 4000;
        private const double SampleRateHz = 16e6;
        private const double SymbolRateHz = 1e6;
        private const int PulseSpan = 20;

        private readonly ITestOutputHelper _output;

        public UserDefinedConstellationTests(ITestOutputHelper output)
        {
            _output = output;
        }

        /// <summary>
        /// The 4/12/16 ring structure <c>REQ-DEM-011</c>'s acceptance criterion names.
        /// </summary>
        /// <remarks>
        /// The radii are DVB-S2's 32-APSK at its middle code rate — 1 : 2.64 : 4.64 — and the outer
        /// two rings are turned so their points fall between the inner ring's rather than on top of
        /// them. <strong>The requirement gives the ring populations and not the radii</strong>,
        /// which is the point of it being user-defined: a caller states the geometry, and stating it
        /// here rather than burying it in a factory is what makes the test's subject visible.
        /// </remarks>
        private static Constellation Apsk32() =>
            Constellation.Apsk(
                "32APSK",
                new List<Constellation.ApskRing>
                {
                    new Constellation.ApskRing(1.0, 4, Math.PI / 4.0),
                    new Constellation.ApskRing(2.64, 12, Math.PI / 12.0),
                    new Constellation.ApskRing(4.64, 16, Math.PI / 16.0),
                });

        [Fact]
        public void AUserDefined32ApskDemodulatesFromTheSimulator()
        {
            // REQ-DEM-011's acceptance criterion, word for word: "A user-defined 32-APSK (4/12/16
            // ring structure) demodulates correctly from the simulator."
            Constellation apsk = Apsk32();

            Assert.Equal(32, apsk.Count);
            Assert.Equal(5, apsk.BitsPerSymbol);

            ContinuousModulatedSource source = SourceFor(apsk);
            float[] samples = Generate(source);

            DemodResult result = Demodulate(samples, source, apsk);

            var sent = new int[Symbols];

            for (int symbol = 0; symbol < Symbols; symbol++)
            {
                sent[symbol] = source.SymbolAt(symbol);
            }

            // Printed before either assertion, because which of them fails says different things:
            // the symbols right and the EVM wrong is a reference that does not match the
            // measurement, and both wrong is an estimate that did not converge on the signal.
            _output.WriteLine(
                "32-APSK (4/12/16, radii 1 : 2.64 : 4.64): " + result.Trace.SymbolCount +
                " symbols, EVM " + result.EvmPercent.ToString("F6", CultureInfo.InvariantCulture) +
                " %rms, " + (result.Converged ? "converged" : "NOT CONVERGED") + ", carrier error " +
                result.CarrierFrequencyErrorHz.ToString("F2", CultureInfo.InvariantCulture) +
                " Hz, " + LongestAgreement(result.Symbols, sent) + " of " + result.Symbols.Count +
                " symbols recovered");

            Assert.True(
                result.EvmPercent < 0.1,
                "32-APSK demodulated at " + result.EvmPercent + " %rms.");

            Assert.Equal(result.Symbols.Count, LongestAgreement(result.Symbols, sent));
        }

        [Fact]
        public void TheRingsAreWhereTheDefinitionPutThem()
        {
            // The geometry is what a user stated, so it is worth reading back rather than trusting:
            // four points at one radius, twelve at 2.64 of it, sixteen at 4.64, each ring turned by
            // half its own spacing. Scaled to unit mean power, so the ratios survive and the
            // absolute radii do not.
            Constellation apsk = Apsk32();

            var counts = new Dictionary<long, int>();

            foreach (ConstellationPoint point in apsk.Points)
            {
                long radius = (long)Math.Round(
                    1e6 * Math.Sqrt((point.I * point.I) + (point.Q * point.Q)));

                counts[radius] = 1 + (counts.ContainsKey(radius) ? counts[radius] : 0);
            }

            Assert.Equal(3, counts.Count);

            var radii = new List<long>(counts.Keys);

            radii.Sort();

            Assert.Equal(4, counts[radii[0]]);
            Assert.Equal(12, counts[radii[1]]);
            Assert.Equal(16, counts[radii[2]]);

            Assert.Equal(2.64, radii[1] / (double)radii[0], 3);
            Assert.Equal(4.64, radii[2] / (double)radii[0], 3);

            _output.WriteLine(
                "rings at " + string.Join(
                    " : ",
                    new[]
                    {
                        "1.000",
                        (radii[1] / (double)radii[0]).ToString("F3", CultureInfo.InvariantCulture),
                        (radii[2] / (double)radii[0]).ToString("F3", CultureInfo.InvariantCulture),
                    }) + " carrying 4, 12, 16 points");
        }

        [Theory]
        [InlineData("QPSK")]
        [InlineData("8PSK")]
        [InlineData("16PSK")]
        public void GrayOnARingIsTheCodeOfTheIndex(string name)
        {
            // On a ring, neighbouring points are neighbouring indices, so Gray is the reflected
            // binary code of the index. Asserted against the code itself rather than against a
            // table, and then against the property the code exists for.
            Constellation gray = Constellation.ByName(name).WithMapping(BitMapping.Gray);

            Assert.Equal(BitMapping.Gray, gray.Mapping);

            for (int symbol = 0; symbol < gray.Count; symbol++)
            {
                Assert.Equal(symbol ^ (symbol >> 1), gray.CarriedBy(symbol));
            }

            // The property: going once round the ring changes one bit at every step, including the
            // step from the last point back to the first.
            for (int symbol = 0; symbol < gray.Count; symbol++)
            {
                int here = gray.CarriedBy(symbol);
                int next = gray.CarriedBy((symbol + 1) % gray.Count);

                Assert.Equal(1, Bits(here ^ next));
            }

            _output.WriteLine(name + " Gray: " + Rendered(gray));
        }

        [Theory]
        [InlineData("16QAM")]
        [InlineData("64QAM")]
        [InlineData("256QAM")]
        public void GrayOnASquareGridIsPerAxis(string name)
        {
            // A point on a grid has neighbours on both axes, so the code is applied to each axis's
            // level separately. This is the case that would be silently wrong if the ring's version
            // were used: it would still be a permutation, still demodulate, and still leave
            // touching points differing in several bits -- which is the whole property.
            Constellation gray = Constellation.ByName(name).WithMapping(BitMapping.Gray);
            int side = gray.LevelsPerAxis;

            Assert.Equal(side * side, gray.Count);

            int checkedPairs = 0;

            for (int i = 0; i < side; i++)
            {
                for (int q = 0; q < side; q++)
                {
                    int here = gray.CarriedBy((i * side) + q);

                    if (q + 1 < side)
                    {
                        Assert.Equal(1, Bits(here ^ gray.CarriedBy((i * side) + q + 1)));
                        checkedPairs++;
                    }

                    if (i + 1 < side)
                    {
                        Assert.Equal(1, Bits(here ^ gray.CarriedBy(((i + 1) * side) + q)));
                        checkedPairs++;
                    }
                }
            }

            _output.WriteLine(
                name + ": " + checkedPairs + " neighbouring pairs, every one differing in one bit");
        }

        [Theory]
        [InlineData("32QAM")]
        [InlineData("16STARQAM")]
        public void GrayIsRefusedWhereThereIsNoOneGrayCode(string name)
        {
            // A cross QAM's points do not form a grid and a star's rings are not one cycle.
            // Returning some permutation anyway would be inventing a standard, so the request is
            // refused and the message says what to do instead.
            //
            // 🔴 OOK left this list on 31 August 2026, when the frequency-keyed formats taught the
            // catalogue what a LEVEL LADDER is. It had been refused for being neither a ring nor a
            // grid, which was true and was not the question: its two points are ordered along an
            // axis, and an ordered set has a Gray code. On two symbols that code is the identity,
            // so what OOK now accepts is the labelling it already had -- see
            // AGrayCodeOnTwoLevelsIsTheOneItAlreadyHad.
            InvalidOperationException refused = Assert.Throws<InvalidOperationException>(
                () => Constellation.ByName(name).WithMapping(BitMapping.Gray));

            Assert.Contains("table", refused.Message, StringComparison.Ordinal);
            _output.WriteLine(name + ": " + refused.Message);
        }

        [Fact]
        public void AGrayCodeOnTwoLevelsIsTheOneItAlreadyHad()
        {
            // What "Gray" means on an ordered set of two: neighbouring symbols differ in one bit,
            // and with one bit each there is only one way for that to be true. Asserted rather than
            // assumed, because the alternative to refusing a request is answering it correctly.
            Constellation gray = Constellation.ByName("OOK").WithMapping(BitMapping.Gray);
            Constellation natural = Constellation.ByName("OOK");

            for (int symbol = 0; symbol < natural.Count; symbol++)
            {
                Assert.Equal(natural.CarriedBy(symbol), gray.CarriedBy(symbol));
            }

            _output.WriteLine(
                "OOK's Gray labelling is its natural one: " +
                gray.CarriedBy(0) + ", " + gray.CarriedBy(1));
        }

        [Fact]
        public void AnExplicitTableIsUsedExactlyAsGiven()
        {
            Constellation mine = Constellation.ByName("QPSK").WithMapping(new[] { 2, 0, 3, 1 });

            Assert.Equal(BitMapping.Explicit, mine.Mapping);
            Assert.Equal(2, mine.CarriedBy(0));
            Assert.Equal(0, mine.CarriedBy(1));
            Assert.Equal(3, mine.CarriedBy(2));
            Assert.Equal(1, mine.CarriedBy(3));

            // The bits follow the value the point carries, not the point.
            Assert.Equal(new[] { 1, 0 }, mine.BitsOf(0));
            Assert.Equal(new[] { 0, 0 }, mine.BitsOf(1));
        }

        [Fact]
        public void ATableThatIsNotAPermutationIsRefused()
        {
            // Two points carrying one value could not be told apart in the bit stream while
            // remaining perfectly distinguishable on the constellation -- a defect that shows up as
            // a bit error rate and in nothing else.
            ArgumentException refused = Assert.Throws<ArgumentException>(
                () => Constellation.ByName("QPSK").WithMapping(new[] { 0, 1, 1, 3 }));

            Assert.Contains("permutation", refused.Message, StringComparison.Ordinal);
            _output.WriteLine(refused.Message);

            Assert.Throws<ArgumentException>(
                () => Constellation.ByName("QPSK").WithMapping(new[] { 0, 1, 2 }));

            Assert.Throws<ArgumentException>(
                () => Constellation.ByName("QPSK").WithMapping(new[] { 0, 1, 2, 4 }));
        }

        [Theory]
        [InlineData(BitMapping.Natural)]
        [InlineData(BitMapping.Gray)]
        public void TheLabellingChangesTheBitsAndNotTheMeasurement(BitMapping mapping)
        {
            // The claim that makes a wrong mapping dangerous, stated as a test: one signal, two
            // labellings, the same EVM to the last digit and different bits. Nothing in a
            // measurement can tell them apart -- only a comparison with what was transmitted.
            Constellation qpsk = Constellation.ByName("QPSK");
            ContinuousModulatedSource source = SourceFor(qpsk);
            float[] samples = Generate(source);

            Constellation labelled = qpsk.WithMapping(mapping);
            DemodResult result = Demodulate(samples, source, labelled);

            var carried = new int[Symbols];

            for (int symbol = 0; symbol < Symbols; symbol++)
            {
                carried[symbol] = labelled.CarriedBy(source.SymbolAt(symbol));
            }

            _output.WriteLine(
                mapping + ": EVM " +
                result.EvmPercent.ToString("F6", CultureInfo.InvariantCulture) + " %rms, " +
                LongestAgreement(result.DataSymbols, carried) + " of " + result.DataSymbols.Count +
                " data symbols recovered");

            Assert.True(result.EvmPercent < 0.1);
            Assert.Equal(
                result.DataSymbols.Count, LongestAgreement(result.DataSymbols, carried));

            // The points are the same points whichever labelling is chosen.
            for (int symbol = 0; symbol < qpsk.Count; symbol++)
            {
                Assert.Equal(qpsk.Points[symbol].I, labelled.Points[symbol].I, 12);
                Assert.Equal(qpsk.Points[symbol].Q, labelled.Points[symbol].Q, 12);
            }
        }

        [Fact]
        public void ADifferentialFormatLabelsTheChangeAndNotTheSymbol()
        {
            // The order of the two operations, and the bench is what settles it: the difference is
            // taken between POINTS, because it is a change of phase, and the labelling is applied to
            // that difference, because that is what the signal carried. Subtracting two Gray codes
            // would be neither.
            Constellation dqpsk = Constellation.ByName("DQPSK").WithMapping(BitMapping.Gray);
            ContinuousModulatedSource source = SourceFor(dqpsk);
            float[] samples = Generate(source);

            DemodResult result = Demodulate(samples, source, dqpsk);

            var carried = new int[Symbols - 1];

            for (int symbol = 1; symbol < Symbols; symbol++)
            {
                carried[symbol - 1] = dqpsk.CarriedBy(source.DataSymbolAt(symbol));
            }

            _output.WriteLine(
                "Gray-labelled DQPSK: EVM " +
                result.EvmPercent.ToString("F6", CultureInfo.InvariantCulture) + " %rms, " +
                LongestAgreement(result.DataSymbols, carried) + " of " +
                result.DataSymbols.Count + " recovered");

            Assert.Equal(
                result.DataSymbols.Count, LongestAgreement(result.DataSymbols, carried));
        }

        [Fact]
        public void StepThreeDeclinesRatherThanReportALineThatIsNotTheCarrier()
        {
            // The defect this requirement's acceptance criterion uncovered, and the fix, both as a
            // measurement. Raising a 4/12/16 APSK to the fourth power leaves 0.0005 of it standing
            // -- twelve points land on three angles that cancel, sixteen on four that cancel, and
            // only the inner four survive at the smallest radius. The tallest line in that spectrum
            // is therefore not the carrier's, and step 3 used to report it: 64 481 Hz of offset on a
            // signal that had none, followed by a demodulation that recovered 43 symbols of 512 and
            // said it had converged.
            Constellation apsk = Apsk32();

            Assert.True(
                apsk.StrippingQuality < 0.001,
                "32-APSK now strips to " + apsk.StrippingQuality + ", which would change what this " +
                "test is about.");

            ContinuousModulatedSource source = SourceFor(apsk);
            DemodResult result = Demodulate(Generate(source), source, apsk);

            string declined = null;

            foreach (string notice in result.Notices)
            {
                if (notice.IndexOf("Step 3 did not estimate", StringComparison.Ordinal) >= 0)
                {
                    declined = notice;
                }
            }

            Assert.NotNull(declined);
            _output.WriteLine(declined);

            // Declining is only worth anything if what follows is right. A millihertz on a megabaud
            // signal is step 8's own residual and not an offset: what is being asserted is that
            // nothing was invented, against the 64 481 Hz that used to be.
            Assert.True(
                Math.Abs(result.CarrierFrequencyErrorHz) < 1.0,
                "The carrier error came back as " + result.CarrierFrequencyErrorHz + " Hz.");

            Assert.True(result.EvmPercent < 0.1);
        }

        [Theory]
        [InlineData("QPSK", 1.0)]
        [InlineData("8PSK", 1.0)]
        [InlineData("16STARQAM", 1.0)]
        [InlineData("16QAM", 0.515152)]
        [InlineData("64QAM", 0.448276)]
        [InlineData("32QAM", 0.145038)]
        [InlineData("2048QAM", 0.132156)]
        public void EveryCatalogueFormatStripsWellEnoughForStepThree(string name, double expected)
        {
            // The threshold is a reading of the formats rather than a number that made a test pass,
            // so the readings are pinned. The weakest in the catalogue is a cross QAM at 0.132; the
            // strongest that fails is a 16-APSK at 0.006; the threshold is the geometric middle of
            // that gap and nothing lies inside it.
            Constellation constellation = Constellation.ByName(name);

            _output.WriteLine(
                name + " (symmetry " + constellation.RotationalSymmetry + "): " +
                constellation.StrippingQuality.ToString("F6", CultureInfo.InvariantCulture));

            Assert.Equal(expected, constellation.StrippingQuality, 5);
            Assert.True(constellation.StrippingQuality > 0.03);
        }

        private static int Bits(int value)
        {
            int count = 0;

            while (value != 0)
            {
                count += value & 1;
                value >>= 1;
            }

            return count;
        }

        private static string Rendered(Constellation constellation)
        {
            var parts = new List<string>(constellation.Count);

            for (int symbol = 0; symbol < constellation.Count; symbol++)
            {
                parts.Add(symbol + "→" + constellation.CarriedBy(symbol));
            }

            return string.Join(", ", parts);
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

        private static DemodResult Demodulate(
            float[] samples, ContinuousModulatedSource source, Constellation constellation) =>
            new Demodulator().Run(
                samples,
                source.SampleRateHz,
                new DemodSettings
                {
                    Constellation = constellation,
                    SymbolRateHz = source.SymbolRateHz,
                    ResultLengthSymbols = 512,
                    FilterSymbolSpan = source.PulseSpanSymbols,
                    MeasurementFilter = PulseFilterType.RootRaisedCosine,
                    MeasurementFilterAlpha = source.RollOff,
                    ReferenceFilterAlpha = source.RollOff,
                });

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
