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
    /// <c>REQ-DEM-031</c>, <c>REQ-DEM-034</c> and <c>REQ-DEM-034a</c>: how long a result is, and how
    /// finely it is drawn.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two settings that sound like one. The <em>internal</em> rate is what the chain resamples to
    /// and filters at; the <em>display</em> rate is how finely the traces are drawn. Coupling them
    /// would make one point per symbol a demodulation of something else — an RRC-shaped signal
    /// occupies more than a symbol rate of bandwidth, so at one sample a symbol the matched filter
    /// cannot be applied without aliasing, and <c>REQ-DEM-034a</c> exists to say so.
    /// </para>
    /// <para>
    /// <strong>The strongest claim here is an equality, not a tolerance.</strong> Every
    /// symbol-instant metric must be <em>bit-identical</em> across display settings, because any
    /// difference at all would mean a metric had been computed somewhere other than a decision
    /// instant.
    /// </para>
    /// </remarks>
    public class DisplayRateAndResultLengthTests
    {
        private const int Symbols = 4000;

        private readonly ITestOutputHelper _output;

        public DisplayRateAndResultLengthTests(ITestOutputHelper output)
        {
            _output = output;
        }

        public static IEnumerable<object[]> TypicalRates()
        {
            foreach (int rate in new[] { 1, 2, 4, 5, 10, 20 })
            {
                yield return new object[] { rate };
            }
        }

        [Theory]
        [MemberData(nameof(TypicalRates))]
        public void EveryTypicalDisplayRateIsSettableAndChangesTheTracePointCount(int drawn)
        {
            // "All of 1, 2, 4, 5, 10 and 20 points per symbol are settable, and each changes the
            // point count of the IQ Measured Time and trajectory traces accordingly."
            DemodResult result = Demodulate(Signal(), drawn);

            Assert.Equal(drawn, result.Trace.SamplesPerSymbol);

            int expected = ((result.Trace.SymbolCount - 1) * drawn) + 1;

            _output.WriteLine(
                drawn + " points/symbol: " + result.Trace.SymbolCount + " symbols drawn over " +
                result.Trace.Samples.Length / 2 + " points");

            Assert.Equal(expected, result.Trace.Samples.Length / 2);

            // And the decisions land on whole points of that grid, which is what lets a
            // constellation point lie on the waveform it came from.
            for (int symbol = 0; symbol < result.Trace.SymbolCount; symbol++)
            {
                Assert.Equal(symbol * drawn, result.Trace.DecisionSampleIndices[symbol]);
            }
        }

        [Fact]
        public void EverySymbolInstantMetricIsBitIdenticalAcrossDisplayRates()
        {
            // "EVM, magnitude error, phase error and every other symbol-instant metric are
            // bit-identical across all six settings for the same input — asserted as exact equality
            // rather than a tolerance, because any difference means metrics are being evaluated
            // somewhere other than the decision instants."
            float[] samples = Signal();

            DemodResult first = Demodulate(samples, 1);

            foreach (int drawn in new[] { 2, 4, 5, 10, 20 })
            {
                DemodResult other = Demodulate(samples, drawn);

                Assert.Equal(first.EvmPercent, other.EvmPercent);
                Assert.Equal(
                    first.CarrierFrequencyErrorHz, other.CarrierFrequencyErrorHz);

                Assert.Equal(first.Symbols.Count, other.Symbols.Count);

                for (int symbol = 0; symbol < first.Symbols.Count; symbol++)
                {
                    Assert.Equal(first.Symbols[symbol], other.Symbols[symbol]);
                }

                // The whole summary, row by row, exactly.
                Assert.Equal(first.Summary.Metrics.Count, other.Summary.Metrics.Count);

                for (int row = 0; row < first.Summary.Metrics.Count; row++)
                {
                    ErrorMetric mine = first.Summary.Metrics[row];
                    ErrorMetric theirs = other.Summary.Metrics[row];

                    Assert.Equal(mine.Label, theirs.Label);
                    Assert.Equal(mine.Rms, theirs.Rms);
                    Assert.Equal(mine.Peak, theirs.Peak);
                }
            }

            _output.WriteLine(
                "EVM " + first.EvmPercent.ToString("R", CultureInfo.InvariantCulture) +
                " %rms, identical to the last bit at 1, 2, 4, 5, 10 and 20 points a symbol");
        }

        [Fact]
        public void TheInternalRateDoesNotFollowTheDisplaySetting()
        {
            // REQ-DEM-034a's own criterion: "For a fixed input and a non-offset format, EVM computed
            // with display points/symbol of 1, 4 and 20 is identical to within 1e-9, the internal
            // rate being fixed at ≥4 sps in all three cases." Identical to 1e-9 is the requirement's
            // wording; it is identical exactly, which the test above asserts. What this one asserts
            // is the other half — that the internal rate really did not move.
            float[] samples = Signal();

            foreach (int drawn in new[] { 1, 4, 20 })
            {
                var settings = Settings(drawn);

                Assert.True(
                    settings.PointsPerSymbol >= 4,
                    "The internal rate fell to " + settings.PointsPerSymbol +
                    " when the display asked for " + drawn + ".");

                Assert.Equal(DemodSettings.DefaultPointsPerSymbol, settings.PointsPerSymbol);
            }

            Assert.Equal(2, DemodSettings.MinimumPointsPerSymbol);
        }

        [Fact]
        public void AnInternalRateBelowTheAbsoluteMinimumIsRefused()
        {
            var settings = Settings(4);

            settings.PointsPerSymbol = 1;

            ArgumentException refused = Assert.Throws<ArgumentException>(() => settings.Validate());

            Assert.Contains("REQ-DEM-034a", refused.Message, StringComparison.Ordinal);
            _output.WriteLine(refused.Message);
        }

        [Fact]
        public void AnOffsetFormatKeepsItsTwoInstantsWhateverTheDisplayAsksFor()
        {
            // "Offset formats retain their internal 2 points per symbol per REQ-DEM-012 regardless
            // of this display setting."
            var settings = new DemodSettings
            {
                Constellation = Constellation.ByName("OQPSK"),
                SymbolRateHz = 1e6,
                DisplayPointsPerSymbol = 1,
            };

            settings.Validate();

            Assert.Equal(2, settings.InstantsPerSymbol);
            Assert.True(settings.PointsPerSymbol >= 2);
        }

        [Fact]
        public void AtOnePointPerSymbolTheEyeIsUnavailableRatherThanEmpty()
        {
            // An eye is the waveform folded on the symbol clock, and at REQ-DEM-034's lowest
            // setting there is nothing between the decisions to fold. The honest answer is that the
            // trace does not exist -- the same answer REQ-DEM-080 already asks for when the
            // equaliser is off -- rather than a fold of one point per symbol drawn as if it were an
            // eye.
            DemodResult drawnFinely = Demodulate(Signal(), 4);
            DemodResult drawnAtSymbols = Demodulate(Signal(), 1);

            foreach (ResultTrace trace in
                new[] { ResultTrace.EyeI, ResultTrace.EyeQ, ResultTrace.Trellis })
            {
                Assert.True(ResultTraces.IsAvailable(drawnFinely, trace));
                Assert.False(ResultTraces.IsAvailable(drawnAtSymbols, trace));

                string reason = ResultTraces.ReasonUnavailable(drawnAtSymbols, trace);

                Assert.Contains("one point per symbol", reason, StringComparison.Ordinal);
                Assert.Contains("REQ-DEM-034", reason, StringComparison.Ordinal);

                _output.WriteLine(trace + ": " + reason);
            }

            // Everything that is not a fold is still there, including the constellation -- which is
            // what a trace at one point a symbol IS.
            foreach (ResultTrace trace in
                new[] { ResultTrace.Constellation, ResultTrace.IqMeasuredTime, ResultTrace.ErrorVectorTime })
            {
                Assert.True(ResultTraces.IsAvailable(drawnAtSymbols, trace));
            }
        }

        // ---- REQ-DEM-031: Result Length ------------------------------------------------------

        [Theory]
        [InlineData("QPSK", 50)]
        [InlineData("16QAM", 50)]
        [InlineData("64QAM", 64)]
        [InlineData("1024QAM", 1024)]
        [InlineData("2048QAM", 2048)]
        [InlineData("4096QAM", 4096)]
        public void TheRecommendedResultLengthRisesWithTheModulationOrder(string name, int expected)
        {
            // "Minimum viable values scale with modulation order — approximately 50 symbols for
            // QPSK/16-QAM rising to about 4 000 symbols for 2048/4096-QAM."
            Constellation constellation = Constellation.ByName(name);

            _output.WriteLine(
                name + " (" + constellation.Count + " points): " +
                constellation.RecommendedResultLengthSymbols + " symbols recommended");

            Assert.Equal(expected, constellation.RecommendedResultLengthSymbols);
        }

        [Fact]
        public void AResultLengthBelowTheRecommendationIsWarnedAboutBySpecificNumber()
        {
            // The criterion, word for word: "Selecting 1024-QAM with Result Length 50 produces a
            // visible, specific warning naming the recommended minimum."
            var settings = new DemodSettings
            {
                Constellation = Constellation.ByName("1024QAM"),
                SymbolRateHz = 1e6,
                ResultLengthSymbols = 50,
            };

            string advice = settings.ResultLengthAdvice;

            _output.WriteLine(advice);

            Assert.NotNull(advice);
            Assert.Contains("1024", advice, StringComparison.Ordinal);
            Assert.Contains("50", advice, StringComparison.Ordinal);
            Assert.Contains("REQ-DEM-031", advice, StringComparison.Ordinal);

            // Visible: it reaches the caller on the result rather than staying in the settings.
            DemodResult result = Demodulate(Signal(), 4, tune: measurement =>
            {
                measurement.Constellation = Constellation.ByName("1024QAM");
                measurement.ResultLengthSymbols = 50;
            });

            bool said = false;

            foreach (string notice in result.Notices)
            {
                if (notice.IndexOf("below the 1024 recommended", StringComparison.Ordinal) >= 0)
                {
                    said = true;
                }
            }

            Assert.True(said, "The chain demodulated a short block for 1024-QAM without saying so.");
        }

        [Fact]
        public void AResultLengthAtOrAboveTheRecommendationSaysNothing()
        {
            // A warning that is always there is not a warning.
            var settings = new DemodSettings
            {
                Constellation = Constellation.ByName("QPSK"),
                SymbolRateHz = 1e6,
                ResultLengthSymbols = 50,
            };

            Assert.Null(settings.ResultLengthAdvice);

            settings.ResultLengthSymbols = 49;

            Assert.NotNull(settings.ResultLengthAdvice);
        }

        private static float[] Signal()
        {
            var source = Source();
            var samples = new float[2 * (int)Math.Ceiling(Symbols * source.SamplesPerSymbol)];

            source.Fill(samples);

            return samples;
        }

        private static ContinuousModulatedSource Source() =>
            new ContinuousModulatedSource
            {
                Scheme = ModulationScheme.Qpsk(),
                SymbolRateHz = 1e6,
                SampleRateHz = 16e6,
                RollOff = 0.35,
                PulseSpanSymbols = 20,
                Seed = 20260824,
            };

        private static DemodSettings Settings(int drawn) =>
            new DemodSettings
            {
                Constellation = Constellation.Qpsk(),
                SymbolRateHz = 1e6,
                ResultLengthSymbols = 256,
                DisplayPointsPerSymbol = drawn,
                FilterSymbolSpan = 20,
                MeasurementFilter = PulseFilterType.RootRaisedCosine,
                MeasurementFilterAlpha = 0.35,
                ReferenceFilterAlpha = 0.35,
            };

        private static DemodResult Demodulate(
            float[] samples, int drawn, Action<DemodSettings> tune = null)
        {
            DemodSettings settings = Settings(drawn);

            if (tune != null)
            {
                tune(settings);
            }

            return new Demodulator().Run(samples, 16e6, settings);
        }
    }
}
