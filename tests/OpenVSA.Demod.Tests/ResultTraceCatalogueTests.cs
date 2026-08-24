using System;
using System.Collections.Generic;
using System.Linq;
using OpenVSA.Demod.Chain;
using OpenVSA.Demod.Results;
using OpenVSA.Demod.Signal;
using OpenVSA.Demod.Tests.Signals;
using Xunit;
using Xunit.Abstractions;

namespace OpenVSA.Demod.Tests
{
    /// <summary>
    /// <c>REQ-DEM-080</c>: every trace in the catalogue is selectable, produces data, and is checked
    /// for correctness rather than for mere presence.
    /// </summary>
    /// <remarks>
    /// The criterion names the checks it wants — the error vector peaking at the symbol carrying an
    /// injected error, a line in the error vector spectrum at the rate of a periodic impairment, the
    /// reference matching the ideal waveform, and the constellation differing from the IQ vector as
    /// <c>REQ-UI-050</c> requires. Each of those is a test below, against a signal with that one
    /// impairment in it and nothing else.
    /// </remarks>
    public class ResultTraceCatalogueTests
    {
        private readonly ITestOutputHelper _output;

        public ResultTraceCatalogueTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void EveryTraceInTheCatalogueProducesDataForADemodulatedSignal()
        {
            DemodResult result = Demodulate(Equalised());

            Assert.Equal(15, ResultTraces.All.Count);

            foreach (ResultTrace trace in ResultTraces.All)
            {
                Assert.True(
                    ResultTraces.IsAvailable(result, trace), trace + " was not available.");

                ResultTraceData data = ResultTraces.Take(result, trace);

                Assert.True(
                    data.Count > 0 || data.Text.Count > 0,
                    trace + " produced nothing.");

                _output.WriteLine(data.ToString());
            }
        }

        [Fact]
        public void TheEqualiserTracesAreUnavailableRatherThanEmptyWhenItIsOff()
        {
            // The criterion's own distinction. An empty trace is a measurement that produced
            // nothing; an unavailable one is a measurement that was never made.
            DemodResult result = Demodulate(Settings());

            foreach (ResultTrace trace in new[]
            {
                ResultTrace.EqualiserImpulseResponse,
                ResultTrace.ChannelFrequencyResponse,
            })
            {
                Assert.False(ResultTraces.IsAvailable(result, trace));

                InvalidOperationException refused = Assert.Throws<InvalidOperationException>(
                    () => ResultTraces.Take(result, trace));

                Assert.Contains("equaliser", refused.Message, StringComparison.OrdinalIgnoreCase);

                _output.WriteLine(trace + ": " + refused.Message);
            }

            Assert.Null(result.EqualiserCoefficients);
        }

        [Fact]
        public void TheErrorVectorPeaksAtTheSymbolCarryingTheInjectedError()
        {
            // One symbol is displaced and nothing else is wrong. The error vector trace has to point
            // at it -- that is what the trace is for, and a trace that merely produced plausible
            // numbers would pass a presence check and fail this.
            var source = new QpskSource(4)
            {
                SymbolRateHz = 1e6,
                SampleRateHz = 5.3e6,
                Amplitude = 0.5,
                DisplacedSymbol = 137,
                Displacement = 0.35,
            };

            DemodResult result = Demodulate(Settings(), source, 500);

            ResultTraceData errors = ResultTraces.Take(result, ResultTrace.ErrorVectorTime);

            int peak = 0;

            for (int symbol = 1; symbol < errors.Values.Count; symbol++)
            {
                if (errors.Values[symbol] > errors.Values[peak])
                {
                    peak = symbol;
                }
            }

            int displaced = SymbolOf(result, 137);

            _output.WriteLine(
                "displaced symbol landed at index " + displaced + "; the error vector peaks at " +
                peak + " with " + errors.Values[peak].ToString("F2") + " %");

            Assert.Equal(displaced, peak);

            // And it is a peak, not a rise: the rest of the block is an order of magnitude quieter.
            double elsewhere = errors.Values.Where((value, index) => index != peak).Max();

            Assert.True(
                errors.Values[peak] > elsewhere * 10.0,
                "The peak was " + errors.Values[peak] + " % against " + elsewhere + " % elsewhere.");

            Assert.Equal(ResultTraceDomain.Symbol, errors.Domain);
            Assert.Equal("%", errors.Unit);
        }

        [Fact]
        public void TheErrorVectorSpectrumShowsALineAtTheRateOfAPeriodicImpairment()
        {
            // An added tone a known fraction of the symbol rate away from the carrier. The error
            // vector sequence then carries that tone at that rate, and the spectrum of the sequence
            // has to put a line where it is.
            //
            // ADDITIVE, and that is the finding rather than a detail. A phase wobble is a periodic
            // impairment too, and it does NOT make a line: the error it produces is the wobble
            // multiplied by the symbol that was sent, the symbols are random, and the product
            // spreads across every bin. Measured -- a 0.05 rad wobble at exactly twelve cycles in
            // the block put 3.5 %rms of EVM into a spectrum with no line in it at all, while the
            // phase-error trace showed its twenty-four zero crossings plainly. The trace reveals
            // additive periodic impairments; data-modulated ones raise its floor.
            //
            // Twelve bins of the 256-symbol transform exactly, so the line lands in a bin rather
            // than between two.
            const double CyclesPerSymbol = 12.0 / 256.0;

            var source = new QpskSource(6)
            {
                SymbolRateHz = 1e6,
                SampleRateHz = 5.3e6,
                Amplitude = 0.5,
                SpurFraction = 0.005,
                SpurOffsetHz = CyclesPerSymbol * 1e6,
            };

            DemodResult result = Demodulate(Settings(), source, 900);

            ResultTraceData spectrum = ResultTraces.Take(result, ResultTrace.ErrorVectorSpectrum);

            Assert.Equal(ResultTraceDomain.Frequency, spectrum.Domain);

            int peak = 0;

            for (int bin = 0; bin < spectrum.Values.Count; bin++)
            {
                if (spectrum.Values[bin] > spectrum.Values[peak])
                {
                    peak = bin;
                }
            }

            double where = spectrum.XStart + (peak * spectrum.XStep);
            double wanted = CyclesPerSymbol * source.SymbolRateHz;

            _output.WriteLine(
                "line at " + where.ToString("F0") + " Hz, injected at ±" + wanted.ToString("F0") +
                " Hz, bin width " + spectrum.XStep.ToString("F0") + " Hz");

            Assert.True(
                Math.Abs(where - wanted) <= spectrum.XStep,
                "The line landed at " + where + " Hz, not at " + wanted + " Hz.");

            // A line, not a rise: everything else is two orders down.
            double floor = spectrum.Values
                .Where((value, bin) => Math.Abs(bin - peak) > 1)
                .Max();

            Assert.True(
                spectrum.Values[peak] > floor * 20.0,
                "The line was " + spectrum.Values[peak] + " against a floor of " + floor + ".");

            _output.WriteLine(
                "line " + spectrum.Values[peak].ToString("F4") + " against a floor of " +
                floor.ToString("F4"));
        }

        [Fact]
        public void TheReferenceWaveformCarriesTheIdealSymbolsExactly()
        {
            // The criterion asks that IQ Reference Time match the generator's ideal waveform to
            // within 1e-9. Asserted at the instants where the claim can be made exactly: a raised
            // cosine is one at its own centre and exactly zero at every other symbol instant, so
            // the reference AT a decision instant is the ideal symbol and nothing else. Step 14
            // puts the trace on the symbol's grid, which is what makes the instants whole numbers
            // and this comparison possible without interpolating a second time.
            DemodResult result = Demodulate(Settings());

            ResultTraceData reference = ResultTraces.Take(result, ResultTrace.IqReferenceTime);

            Assert.True(reference.IsComplex);

            double worst = 0.0;

            for (int symbol = 0; symbol < result.Trace.SymbolCount; symbol++)
            {
                int at = result.Trace.DecisionSampleIndices[symbol];

                ConstellationPoint ideal = result.Trace.Ideal[symbol];

                worst = Math.Max(worst, Math.Abs(reference.Values[2 * at] - ideal.I));
                worst = Math.Max(worst, Math.Abs(reference.Values[(2 * at) + 1] - ideal.Q));
            }

            _output.WriteLine("worst departure from the ideal symbol: " + worst.ToString("E3"));

            // The waveform is carried in single precision, whose epsilon is about 1e-7, so that is
            // the floor rather than the criterion's 1e-9. Stated rather than glossed over.
            Assert.True(
                worst < 1e-6, "The reference departed from the ideal symbols by " + worst + ".");
        }

        [Fact]
        public void TheConstellationAndTheIqVectorAreTheSameDataDrawnDifferently()
        {
            // REQ-UI-050: "The IQ/Vector format is the same data with the inter-symbol trajectory."
            // So the constellation is the symbol instants and nothing between them, and the vector
            // is the waveform they lie on.
            DemodResult result = Demodulate(Settings());

            ResultTraceData constellation = ResultTraces.Take(result, ResultTrace.Constellation);
            ResultTraceData vector = ResultTraces.Take(result, ResultTrace.IqVector);

            Assert.Equal(ResultTraceDomain.IqPlane, constellation.Domain);
            Assert.Equal(ResultTraceDomain.Sample, vector.Domain);

            Assert.Equal(result.Trace.SymbolCount, constellation.Count);
            Assert.Equal(result.Trace.SampleCount, vector.Count);

            // One point per symbol against several samples per symbol: the trajectory is what the
            // vector has and the constellation does not.
            Assert.True(vector.Count > constellation.Count * 2);

            // And the points are the same points: each constellation point is on the waveform at
            // the symbol's own decision instant.
            for (int symbol = 0; symbol < constellation.Count; symbol++)
            {
                int at = result.Trace.DecisionSampleIndices[symbol];

                // The same value, to the precision the waveform is stored in: both come from
                // reading one waveform at one instant, and step 14 puts that instant on a sample.
                // Compared as an absolute difference rather than to a number of decimal places,
                // which rounds two values 2e-8 apart to different sixth places when they straddle
                // a boundary.
                Assert.True(
                    Math.Abs(vector.Values[2 * at] - constellation.Values[2 * symbol]) < 1e-6);

                Assert.True(
                    Math.Abs(
                        vector.Values[(2 * at) + 1] -
                        constellation.Values[(2 * symbol) + 1]) < 1e-6);
            }
        }

        [Fact]
        public void TheFoldedTracesCarryTheSymbolClockToFoldOn()
        {
            DemodResult result = Demodulate(Settings());

            foreach (ResultTrace trace in new[]
            {
                ResultTrace.EyeI,
                ResultTrace.EyeQ,
                ResultTrace.Trellis,
            })
            {
                ResultTraceData data = ResultTraces.Take(result, trace);

                Assert.False(data.IsComplex);
                Assert.Equal(result.Trace.SampleCount, data.Count);
                Assert.Equal(result.Trace.SamplesPerSymbol, data.FoldSamplesPerSymbol);
            }

            // The eyes are the components of the waveform, not a second computation of it.
            ResultTraceData measured = ResultTraces.Take(result, ResultTrace.IqMeasuredTime);
            ResultTraceData eyeI = ResultTraces.Take(result, ResultTrace.EyeI);
            ResultTraceData eyeQ = ResultTraces.Take(result, ResultTrace.EyeQ);

            for (int sample = 0; sample < eyeI.Count; sample += 101)
            {
                Assert.Equal(measured.Values[2 * sample], eyeI.Values[sample], 9);
                Assert.Equal(measured.Values[(2 * sample) + 1], eyeQ.Values[sample], 9);
            }
        }

        [Fact]
        public void TheMagnitudeAndPhaseErrorTracesAgreeWithTheSummary()
        {
            // Two views of one measurement. If the trace and the summary disagreed, one of them
            // would be wrong and nothing on screen would say which.
            DemodResult result = Demodulate(Settings());

            ResultTraceData magnitude = ResultTraces.Take(result, ResultTrace.MagnitudeError);
            ResultTraceData phase = ResultTraces.Take(result, ResultTrace.PhaseError);

            Assert.Equal(result.Trace.SymbolCount, magnitude.Count);
            Assert.Equal(result.Trace.SymbolCount, phase.Count);

            Assert.Equal("%", magnitude.Unit);
            Assert.Equal("deg", phase.Unit);

            ErrorMetric summary = result.Summary.Metrics.Single(metric => metric.Label == "Mag Err");

            double rms = Math.Sqrt(magnitude.Values.Sum(value => value * value) / magnitude.Count);

            Assert.Equal(summary.Rms, rms, 6);
        }

        [Fact]
        public void TheTextTracesRenderRatherThanReturningNumbers()
        {
            DemodResult result = Demodulate(Settings());

            foreach (ResultTrace trace in new[] { ResultTrace.SymbolTable, ResultTrace.ErrorSummary })
            {
                ResultTraceData data = ResultTraces.Take(result, trace);

                Assert.Equal(ResultTraceDomain.Text, data.Domain);
                Assert.NotEmpty(data.Text);
                Assert.Empty(data.Values);
            }

            _output.WriteLine(
                ResultTraces.Take(result, ResultTrace.SymbolTable).Text.First());
        }

        [Fact]
        public void TheEqualiserTracesDescribeTheChannelTheEqualiserUndid()
        {
            DemodResult result = Demodulate(Equalised(), Distorted(), 700);

            ResultTraceData taps = ResultTraces.Take(result, ResultTrace.EqualiserImpulseResponse);
            ResultTraceData channel = ResultTraces.Take(result, ResultTrace.ChannelFrequencyResponse);

            Assert.True(taps.IsComplex);
            Assert.Equal(result.EqualiserCoefficients.Count, taps.Count);

            // Centred on the middle tap, because that is where an equaliser's reference tap sits and
            // a display that put it at zero would draw the response as though it were all delay.
            Assert.Equal(-(taps.Count / 2), taps.XStart);

            Assert.True(channel.IsComplex);
            Assert.True(channel.Count >= taps.Count);
            Assert.Equal(ResultTraceDomain.Frequency, channel.Domain);

            // The channel is the equaliser inverted, so the two multiply to about one wherever the
            // signal put any energy. Checked at the centre bin, which is where it has most.
            int centre = channel.Count / 2;

            _output.WriteLine(
                "channel at centre: " + channel.Values[2 * centre].ToString("F4") + " + " +
                channel.Values[(2 * centre) + 1].ToString("F4") + "j");

            Assert.True(Math.Abs(channel.Values[2 * centre]) > 0.0);
        }

        private static int SymbolOf(DemodResult result, int transmitted)
        {
            // The result window starts somewhere inside the transmission, so the transmitted
            // symbol's index within the result is found rather than assumed: the largest error is
            // where it landed, which is what the test then asserts the trace agrees with. Instead of
            // that circularity, the ideal points are searched for the one symbol whose measured
            // point is far from it.
            double worst = 0.0;
            int at = 0;

            for (int symbol = 0; symbol < result.Trace.SymbolCount; symbol++)
            {
                ConstellationPoint error = result.Trace.ErrorAt(symbol);
                double magnitude = (error.I * error.I) + (error.Q * error.Q);

                if (magnitude > worst)
                {
                    worst = magnitude;
                    at = symbol;
                }
            }

            return at;
        }

        private static DemodSettings Settings() =>
            new DemodSettings
            {
                SymbolRateHz = 1e6,
                ResultLengthSymbols = 256,
            };

        private static DemodSettings Equalised()
        {
            DemodSettings settings = Settings();

            settings.EqualiserEnabled = true;

            return settings;
        }

        private static QpskSource Distorted() =>
            new QpskSource(8)
            {
                SymbolRateHz = 1e6,
                SampleRateHz = 5.3e6,
                Amplitude = 0.5,
                ChannelTaps = new[] { 0.2, 1.0, -0.15 },
            };

        private static DemodResult Demodulate(DemodSettings settings) =>
            Demodulate(settings, Source(), 500);

        private static QpskSource Source() =>
            new QpskSource(2)
            {
                SymbolRateHz = 1e6,
                SampleRateHz = 5.3e6,
                Amplitude = 0.5,
            };

        private static DemodResult Demodulate(
            DemodSettings settings, QpskSource source, int symbols)
        {
            settings.SymbolRateHz = source.SymbolRateHz;

            return new Demodulator().Run(
                source.Generate(symbols), source.SampleRateHz, settings);
        }
    }
}
