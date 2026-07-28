using System;
using System.Collections.Generic;
using System.Linq;
using OpenVSA.Core;
using OpenVSA.Dsp.Spectrum;
using OpenVSA.Dsp.Windowing;
using OpenVSA.TestHarness.Synthesis;
using Xunit;
using Xunit.Abstractions;

namespace OpenVSA.TestHarness.Tests
{
    /// <summary>
    /// The synthetic modulated source, and the ground truth the display group will rest on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>These come first because everything else will trust them.</strong>
    /// <c>REQ-UI-050</c>, <c>REQ-UI-051</c>, <c>REQ-UI-052</c> and <c>REQ-DEM-083</c> are all
    /// worded against a signal whose symbols and symbol clock are known, so a generator that was
    /// quietly wrong would make every one of those criteria pass against nothing. The checks here
    /// are the generator against itself: the symbols come back out, the pulse is zero at the
    /// neighbouring decision instants, and the occupied bandwidth is the symbol rate the caller
    /// asked for — measured through the real spectrum path, not asserted about the code.
    /// </para>
    /// </remarks>
    public class SyntheticSymbolSourceTests
    {
        private readonly ITestOutputHelper _output;

        /// <summary>Takes xunit's output sink.</summary>
        /// <param name="output">Where measured figures are written.</param>
        public SyntheticSymbolSourceTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void EveryConstellationHasUnitAveragePower()
        {
            // EVM is referenced to the average power of the ideal points, so every scheme has to
            // agree on what that is or the metric means something different per modulation.
            foreach (ModulationScheme scheme in ModulationScheme.All)
            {
                double power = scheme.IdealPoints.Sum(p => p.I * p.I + p.Q * p.Q) / scheme.Order;

                Assert.Equal(1.0, power, 9);
            }
        }

        [Fact]
        public void EachSchemeKnowsHowManyEyesItShows()
        {
            // REQ-UI-051: "an m-level modulation shows m-1 eyes stacked vertically", where m is the
            // number of levels on the axis and not the number of constellation points. Counted from
            // the constellation rather than declared, so the two cannot disagree.
            foreach (ModulationScheme scheme in ModulationScheme.All)
            {
                var levels = new HashSet<double>(
                    scheme.IdealPoints.Select(p => Math.Round(p.I, 6)));

                _output.WriteLine(
                    scheme.Name + ": " + scheme.Order + " points, " + levels.Count +
                    " I levels, " + scheme.EyeOpenings + " eyes");

                Assert.Equal(levels.Count, scheme.LevelsPerAxis);
                Assert.Equal(levels.Count - 1, scheme.EyeOpenings);
            }
        }

        [Fact]
        public void ABitPatternIsProducedForEverySymbol()
        {
            // REQ-UI-052's bottom portion is the detected symbol/bit stream, and its gutter numbers
            // rows by bit — so a scheme has to be able to spell a symbol in bits.
            foreach (ModulationScheme scheme in ModulationScheme.All)
            {
                var seen = new HashSet<string>();

                for (int symbol = 0; symbol < scheme.Order; symbol++)
                {
                    string bits = scheme.BitsOf(symbol);

                    Assert.Equal(scheme.BitsPerSymbol, bits.Length);
                    Assert.True(seen.Add(bits), scheme.Name + " spells two symbols the same way.");
                }

                Assert.Throws<ArgumentOutOfRangeException>(() => scheme.BitsOf(scheme.Order));
            }
        }

        [Fact]
        public void ACleanBurstReturnsExactlyTheSymbolsItWasGiven()
        {
            // The generator checked against its own truth. A pulse shape or a clock that is wrong
            // makes this fall short, and every criterion resting on "the known symbols" would then
            // be resting on nothing.
            foreach (ModulationScheme scheme in ModulationScheme.All)
            {
                var source = new SyntheticSymbolSource { Scheme = scheme };
                SyntheticBurst burst = source.Generate(200);

                Assert.Equal(200, burst.Symbols.Count);
                Assert.Equal(200, burst.DecisionSampleIndices.Count);
                Assert.Equal(200, burst.CorrectlyDecided());

                _output.WriteLine(
                    scheme.Name + ": EVM " +
                    (burst.ErrorVectorMagnitude() * 100.0).ToString("0.0000") + " %");

                // Clean means clean: the residual is the pulse's own truncation and nothing else.
                Assert.True(
                    burst.ErrorVectorMagnitude() < 0.01,
                    scheme.Name + " has an EVM of " + burst.ErrorVectorMagnitude() +
                    " with no noise added.");
            }
        }

        [Fact]
        public void ThePulseIsZeroAtTheNeighbouringDecisionInstants()
        {
            // What makes the symbols recoverable exactly: a raised cosine passes through zero at
            // every symbol instant but its own. A root-raised cosine does not, and using one here
            // without a matched filter is the mistake that makes every EVM figure wrong by the
            // inter-symbol interference.
            var source = new SyntheticSymbolSource
            {
                Scheme = ModulationScheme.Bpsk(),
                SamplesPerSymbol = 8,
            };

            // One symbol alone, so anything at a neighbouring instant is the pulse's own tail.
            SyntheticBurst burst = source.Generate(1);

            int centre = burst.DecisionSampleIndices[0];
            ReadOnlySpan<float> samples = burst.Samples;

            for (int neighbour = -4; neighbour <= 4; neighbour++)
            {
                if (neighbour == 0)
                {
                    continue;
                }

                int at = (centre + neighbour * burst.SamplesPerSymbol) * 2;

                if (at < 0 || at + 1 >= samples.Length)
                {
                    continue;
                }

                Assert.True(
                    Math.Abs(samples[at]) < 1e-5,
                    "The pulse is " + samples[at] + " at symbol instant " + neighbour + ".");
            }
        }

        [Fact]
        public void ADisplacedSymbolIsWhereItWasPutAndNowhereElse()
        {
            // REQ-DEM-083: "verified against a signal in which one symbol is displaced so the
            // correct point is identifiable, which an off-by-one selection fails". Without the
            // displacement every symbol of the same value sits in the same place and selecting
            // k ± 1 looks right.
            var source = new SyntheticSymbolSource
            {
                Scheme = ModulationScheme.Qpsk(),
                DisplacedSymbolIndex = 37,
                Displacement = 0.4,
            };

            SyntheticBurst burst = source.Generate(120);

            Assert.Equal(37, burst.DisplacedSymbolIndex);

            var errors = new List<double>();

            for (int symbol = 0; symbol < burst.Symbols.Count; symbol++)
            {
                SymbolPoint measured = burst.MeasuredAt(symbol);
                SymbolPoint ideal = burst.Scheme.IdealPoints[burst.Symbols[symbol]];

                errors.Add(measured.DistanceTo(ideal));
            }

            double worstOther = errors.Where((e, i) => i != 37).Max();

            _output.WriteLine(
                "displaced symbol error " + errors[37].ToString("0.0000") +
                ", worst of the others " + worstOther.ToString("0.0000"));

            Assert.True(
                errors[37] > worstOther * 10.0,
                "The displaced symbol is not distinguishable from its neighbours.");

            // And it is genuinely the one asked for: symbol 36 and 38 are where they should be.
            Assert.True(errors[36] < 1e-3);
            Assert.True(errors[38] < 1e-3);
        }

        [Fact]
        public void TheDecisionInstantsAreOneSymbolApart()
        {
            // REQ-UI-051's vertical reference lines fall at the symbol positions. A display folding
            // half a symbol out disagrees with this list, which is the failure the criterion names.
            var source = new SyntheticSymbolSource { SamplesPerSymbol = 10 };
            SyntheticBurst burst = source.Generate(50);

            for (int symbol = 1; symbol < burst.DecisionSampleIndices.Count; symbol++)
            {
                Assert.Equal(
                    10,
                    burst.DecisionSampleIndices[symbol] - burst.DecisionSampleIndices[symbol - 1]);
            }

            Assert.Equal(burst.SampleRateHz / 10.0, burst.SymbolRateHz, 6);
        }

        [Fact]
        public void TheOccupiedBandwidthIsTheSymbolRateTheCallerAskedFor()
        {
            // Measured through the real spectrum path rather than asserted about the generator: a
            // root-raised-cosine shaped signal occupies (1 + rolloff) times its symbol rate, and a
            // burst whose spectrum said otherwise would not be a signal any of these displays
            // could be checked against.
            var source = new SyntheticSymbolSource
            {
                Scheme = ModulationScheme.Qam16(),
                SampleRateHz = 12.8e6,
                SamplesPerSymbol = 8,
                RollOff = 0.35,
            };

            SyntheticBurst burst = source.Generate(512);

            using (IqBlock block = burst.ToBlock(1e9, DateTime.UtcNow))
            {
                SpectrumFrame frame = new SpectrumComputer(WindowType.Hann, null, null).Compute(block);

                ReadOnlySpan<float> levels = frame.LevelsDbm;

                double peak = double.MinValue;

                for (int i = 0; i < levels.Length; i++)
                {
                    peak = Math.Max(peak, levels[i]);
                }

                // The width at 20 dB below the peak, which is inside the roll-off skirt.
                int first = -1;
                int last = -1;

                for (int i = 0; i < levels.Length; i++)
                {
                    if (levels[i] > peak - 20.0)
                    {
                        if (first < 0)
                        {
                            first = i;
                        }

                        last = i;
                    }
                }

                double measuredHz = (last - first) * frame.BinWidthHz;
                double expectedHz = burst.SymbolRateHz * (1.0 + source.RollOff);

                _output.WriteLine(
                    "symbol rate " + (burst.SymbolRateHz / 1e6).ToString("0.000") +
                    " MHz, occupied " + (measuredHz / 1e6).ToString("0.000") +
                    " MHz, expected about " + (expectedHz / 1e6).ToString("0.000") + " MHz");

                Assert.InRange(measuredHz, expectedHz * 0.75, expectedHz * 1.25);
            }
        }

        [Fact]
        public void NoiseRaisesTheErrorVectorMagnitudeByTheAmountAsked()
        {
            // The generator's noise is referenced to unit average symbol power, so an SNR of
            // 20 dB should give an EVM near 10 per cent. Loose bounds — the point is that the
            // figure asked for is the figure measured, not that it is exact on 400 symbols.
            foreach (double snr in new[] { 30.0, 20.0, 14.0 })
            {
                var source = new SyntheticSymbolSource
                {
                    Scheme = ModulationScheme.Qpsk(),
                    SignalToNoiseDb = snr,
                };

                SyntheticBurst burst = source.Generate(400);

                double expected = Math.Pow(10.0, -snr / 20.0);
                double measured = burst.ErrorVectorMagnitude();

                _output.WriteLine(
                    snr.ToString("0") + " dB SNR: EVM " + (measured * 100.0).ToString("0.00") +
                    " % against " + (expected * 100.0).ToString("0.00") + " % expected");

                Assert.InRange(measured, expected * 0.6, expected * 1.6);
            }
        }

        [Fact]
        public void TheSymbolStreamIsLaidOutInRows()
        {
            // REQ-UI-052's bottom portion, in both the forms it offers.
            var source = new SyntheticSymbolSource { Scheme = ModulationScheme.Qam16() };
            SyntheticBurst burst = source.Generate(20);

            IReadOnlyList<string> binary = burst.SymbolStream(binary: true, perRow: 8);

            Assert.Equal(3, binary.Count);
            Assert.Equal(8, binary[0].Split(' ').Length);
            Assert.Equal(4, binary[2].Split(' ').Length);
            Assert.All(binary[0].Split(' '), bits => Assert.Equal(4, bits.Length));

            IReadOnlyList<string> values = burst.SymbolStream(binary: false, perRow: 20);

            Assert.Single(values);
            Assert.Equal(
                string.Join(" ", burst.Symbols.Select(s => s.ToString())),
                values[0]);
        }

        [Fact]
        public void TheSameSeedGivesTheSameBurst()
        {
            // A display test that failed once and passed on the next run would be worse than none.
            var first = new SyntheticSymbolSource { Seed = 99 }.Generate(64);
            var second = new SyntheticSymbolSource { Seed = 99 }.Generate(64);
            var other = new SyntheticSymbolSource { Seed = 100 }.Generate(64);

            Assert.Equal(first.Symbols, second.Symbols);
            Assert.NotEqual(first.Symbols, other.Symbols);
        }

        [Fact]
        public void ImpossibleSettingsAreRefused()
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => new SyntheticSymbolSource().Generate(0));

            Assert.Throws<ArgumentOutOfRangeException>(
                () => new SyntheticSymbolSource { SamplesPerSymbol = 1 }.Generate(4));

            Assert.Throws<ArgumentOutOfRangeException>(
                () => new SyntheticSymbolSource { RollOff = 1.5 }.Generate(4));

            Assert.Throws<ArgumentNullException>(
                () => new SyntheticSymbolSource { Scheme = null });

            Assert.Throws<ArgumentException>(
                () => new SyntheticSymbolSource().Generate(4).ToBlock(1e9, DateTime.Now));
        }
    }
}
