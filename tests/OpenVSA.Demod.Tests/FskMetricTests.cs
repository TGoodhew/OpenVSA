using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using OpenVSA.Demod.Chain;
using OpenVSA.Demod.Results;
using OpenVSA.Demod.Signal;
using Xunit;
using Xunit.Abstractions;

namespace OpenVSA.Demod.Tests
{
    /// <summary>
    /// <c>REQ-DEM-070</c>'s frequency-keyed pair: FSK deviation and FSK error, against a generated
    /// FSK signal of known deviation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The signal is built here from the definition of FSK — a phase that advances at a rate set by
    /// the symbol's level — rather than through the pulse-shaped generator, because the criterion
    /// asks for a KNOWN deviation and the value has to be exact for the comparison to mean anything.
    /// A shaped transition between levels would make the deviation a thing to be defined rather than
    /// a thing to be assumed.
    /// </para>
    /// <para>
    /// <strong>FSK error's normalisation is a reading, not a specification.</strong>
    /// <c>REQ-DEM-070</c> names the metric and does not say what it is a percentage of. Peak
    /// deviation is the choice, because that is the quantity beside it in the same requirement.
    /// The test below therefore checks it two ways that do not depend on the convention: that a
    /// clean signal reads near zero, and that a signal with a known deviation error reads the size
    /// the injection implies.
    /// </para>
    /// </remarks>
    public class FskMetricTests
    {
        private const double SymbolRateHz = 1e6;
        private const double SampleRateHz = 16e6;
        private const int PerSymbol = 8;

        private readonly ITestOutputHelper _output;

        public FskMetricTests(ITestOutputHelper output)
        {
            _output = output;
        }

        public static IEnumerable<object[]> Cases => new List<object[]>
        {
            new object[] { 2, 250e3 },
            new object[] { 2, 500e3 },
            new object[] { 4, 300e3 },
            new object[] { 4, 750e3 },
            new object[] { 8, 700e3 },
        };

        [Theory]
        [MemberData(nameof(Cases))]
        public void AKnownDeviationIsRecovered(int order, double deviationHz)
        {
            DemodResult result = Demodulate(order, deviationHz, 0.0);

            double reported = Row(result, "FSK Dev").Rms;
            double error = Math.Abs(reported - deviationHz) / deviationHz;

            _output.WriteLine(
                order + "FSK, injected " + (deviationHz / 1e3).ToString("F1") +
                " kHz peak deviation, reported " + (reported / 1e3).ToString("F3") +
                " kHz (" + (error * 100.0).ToString("F3") + " %)");

            Assert.True(
                error < 0.01,
                "injected " + deviationHz + " Hz and read " + reported + " Hz.");
        }

        [Fact]
        public void FskErrorIsNearZeroOnACleanSignalAndFollowsAnInjectedLadderError()
        {
            // A near-zero reading on its own proves nothing -- a metric hard-wired to zero would
            // pass. So the ladder is stretched by a known fraction and the error is required to
            // grow with it.
            DemodResult clean = Demodulate(4, 500e3, 0.0);

            double cleanError = Row(clean, "FSK Err").Rms;

            _output.WriteLine("clean 4FSK: FSK error " + cleanError.ToString("F4") + " %");

            Assert.True(cleanError < 1.0, "A clean signal read " + cleanError + " % FSK error.");

            // A ladder whose outer levels are 10 % wide of where they should be. The demodulator
            // fits ONE scale to the whole ladder, so it lands between the inner and outer levels
            // and both end up wrong -- which is what a real transmitter with a non-linear
            // modulator looks like, and what the metric exists to show.
            DemodResult stretched = Demodulate(4, 500e3, 0.10);

            double stretchedError = Row(stretched, "FSK Err").Rms;

            _output.WriteLine(
                "4FSK with the outer levels 10 % wide: FSK error " +
                stretchedError.ToString("F4") + " %");

            Assert.True(
                stretchedError > 10.0 * Math.Max(cleanError, 0.01),
                "The injected ladder error did not move FSK error: " + cleanError +
                " % clean against " + stretchedError + " % stretched.");
        }

        [Fact]
        public void TheRowsAppearForFskAndForNothingElse()
        {
            // REQ-DEM-070 scopes these to FSK formats, and REQ-DEM-071 wants the table's rows to
            // follow the format.
            IReadOnlyList<string> fsk = MetricApplicability.LabelsFor(ModulationFamily.Fsk, false);

            Assert.Contains("FSK Dev", fsk);
            Assert.Contains("FSK Err", fsk);

            foreach (ModulationFamily family in new[]
            {
                ModulationFamily.Psk,
                ModulationFamily.Qam,
                ModulationFamily.Apsk,
                ModulationFamily.Msk,
                ModulationFamily.Vsb,
            })
            {
                IReadOnlyList<string> rows = MetricApplicability.LabelsFor(family, false);

                Assert.DoesNotContain("FSK Dev", rows);
                Assert.DoesNotContain("FSK Err", rows);
            }

            // Units, because REQ-DEM-071 asks for the table to carry them.
            Assert.Equal("Hz", MetricApplicability.UnitOf("FSK Dev"));
            Assert.Equal("%", MetricApplicability.UnitOf("FSK Err"));
        }

        [Fact]
        public void ANonFskFormatReportsNoDeviationRatherThanZero()
        {
            // The distinction the summary draws everywhere else: not measured is NaN, and zero
            // would be a measurement result.
            DemodResult result = DemodulateQpsk();

            Assert.DoesNotContain(result.Summary.Metrics, metric => metric.Label == "FSK Dev");
        }

        private static ErrorMetric Row(DemodResult result, string label) =>
            result.Summary.Metrics.Single(metric => metric.Label == label);

        /// <summary>
        /// A continuous-phase FSK signal at a known peak deviation.
        /// </summary>
        /// <param name="order">How many levels.</param>
        /// <param name="deviationHz">The outermost level's deviation from the carrier.</param>
        /// <param name="outerStretch">
        /// A fraction by which the outer levels are widened, for injecting a ladder error; zero for
        /// a clean signal.
        /// </param>
        /// <remarks>
        /// Continuous phase, because a phase that jumped at every symbol boundary would be a
        /// different modulation with a far wider spectrum, and the discriminator would be reading
        /// the jumps. The phase is accumulated in double and only the sample written is narrowed.
        /// </remarks>
        private static float[] Fsk(
            int order, double deviationHz, double outerStretch, int symbols, int seed)
        {
            var random = new Random(seed);
            var samples = new float[2 * symbols * PerSymbol];

            int outermost = order - 1;
            double phase = 0.0;

            for (int symbol = 0; symbol < symbols; symbol++)
            {
                int level = random.Next(order);
                double ladder = (2 * level) - (order - 1);

                // Stretch only the outer levels, so the ladder stops being evenly spaced. A uniform
                // stretch would simply be a different deviation and the fit would absorb it.
                if (outerStretch != 0.0 && Math.Abs(ladder) == outermost)
                {
                    ladder *= 1.0 + outerStretch;
                }

                double hz = ladder / outermost * deviationHz;
                double perSample = 2.0 * Math.PI * hz / (SymbolRateHz * PerSymbol);

                for (int sample = 0; sample < PerSymbol; sample++)
                {
                    int at = (symbol * PerSymbol) + sample;

                    samples[2 * at] = (float)Math.Cos(phase);
                    samples[(2 * at) + 1] = (float)Math.Sin(phase);

                    phase += perSample;
                }
            }

            return samples;
        }

        private static DemodResult Demodulate(int order, double deviationHz, double outerStretch)
        {
            const int Symbols = 2000;

            float[] samples = Fsk(order, deviationHz, outerStretch, Symbols, 20260907);

            var settings = new DemodSettings
            {
                Constellation = Constellation.Fsk(order),
                SymbolRateHz = SymbolRateHz,
                PointsPerSymbol = PerSymbol,
                ResultLengthSymbols = 1024,
                FilterSymbolSpan = 8,
                MeasurementFilter = PulseFilterType.None,
                ReferenceFilter = PulseFilterType.None,
            };

            // The record is generated at the internal rate already, so the acquisition rate is the
            // same number: there is nothing here for step 4 to resample and no shaping to undo.
            return new Demodulator().Run(
                samples, SymbolRateHz * PerSymbol, settings);
        }

        private static DemodResult DemodulateQpsk()
        {
            var source = new OpenVSA.Synthesis.ContinuousModulatedSource
            {
                Scheme = OpenVSA.Synthesis.ModulationScheme.FromPoints(
                    "QPSK",
                    Constellation.Qpsk().Points
                        .Select(p => new OpenVSA.Synthesis.SymbolPoint(p.I, p.Q)).ToList(),
                    false,
                    0.0),
                SymbolRateHz = SymbolRateHz,
                SampleRateHz = SampleRateHz,
                RollOff = 0.35,
                PulseSpanSymbols = 12,
                Seed = 20260907,
            };

            var samples = new float[2 * 2000 * 16];

            source.Restart();
            source.Fill(samples);

            var settings = new DemodSettings
            {
                Constellation = Constellation.Qpsk(),
                SymbolRateHz = SymbolRateHz,
                ResultLengthSymbols = 512,
                FilterSymbolSpan = 12,
                MeasurementFilter = PulseFilterType.RootRaisedCosine,
                MeasurementFilterAlpha = 0.35,
                ReferenceFilterAlpha = 0.35,
            };

            return new Demodulator().Run(samples, SampleRateHz, settings);
        }
    }
}
