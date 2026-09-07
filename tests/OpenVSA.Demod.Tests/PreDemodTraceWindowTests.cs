using System;
using System.Collections.Generic;
using System.Linq;
using OpenVSA.Demod.Chain;
using OpenVSA.Demod.Results;
using OpenVSA.Demod.Tests.Signals;
using Xunit;
using Xunit.Abstractions;

namespace OpenVSA.Demod.Tests
{
    /// <summary>
    /// <c>REQ-DEM-032</c>: the pre-demodulation traces use a window 20 % larger than the Result
    /// Length, the result is unaffected by them, and a burst's transitions show in the wider window
    /// and not in the result.
    /// </summary>
    /// <remarks>
    /// The criterion asks for three separate things and each is a test below. The factor is checked
    /// across several Result Lengths rather than one, because a fixed padding and a proportional one
    /// are indistinguishable at a single length — which is the criterion's own reason for asking.
    /// </remarks>
    public class PreDemodTraceWindowTests
    {
        private readonly ITestOutputHelper _output;

        public PreDemodTraceWindowTests(ITestOutputHelper output)
        {
            _output = output;
        }

        /// <summary>Result Lengths the factor is checked at.</summary>
        /// <remarks>
        /// Spread over a factor of six, and none of them a multiple of another, so a padding that
        /// happened to equal 0.2 x one of them cannot pass at the rest.
        /// </remarks>
        public static IEnumerable<object[]> ResultLengths => new List<object[]>
        {
            new object[] { 64 },
            new object[] { 100 },
            new object[] { 150 },
            new object[] { 256 },
            new object[] { 384 },
        };

        [Theory]
        [MemberData(nameof(ResultLengths))]
        public void EachPreDemodulationTraceSpansTwentyPercentMoreThanTheResultLength(int length)
        {
            DemodResult result = Demodulate(Settings(length), Source(), length * 3);

            PreDemodWaveform window = result.PreDemod;

            Assert.NotNull(window);
            Assert.Equal(length, window.ResultSymbolCount);

            _output.WriteLine(
                "Result Length " + length + ": " + window);

            // The tolerance is one sample, which is what a window cut on a sample grid can
            // actually promise, rather than a number chosen to make this pass. Expressed in
            // symbols so it does not silently loosen when points per symbol changes.
            double sample = 1.0 / result.Trace.SamplesPerSymbol;

            Assert.InRange(
                window.SymbolSpan, (1.2 * length) - sample, (1.2 * length) + sample);

            // And the factor itself, which is what the criterion actually names.
            Assert.InRange(window.SpanFactor, 1.2 - (sample / length), 1.2 + (sample / length));

            // All three traces, not just the one the window belongs to: the criterion says "for
            // each of the three pre-demodulation traces".
            foreach (PreDemodTrace trace in PreDemodTraces.All)
            {
                Assert.True(
                    PreDemodTraces.IsAvailable(result, trace), trace + " was not available.");

                PreDemodTraceData data = PreDemodTraces.Take(result, trace);

                Assert.Same(window, data.Window);
                Assert.True(data.Count > 0, trace + " produced nothing.");

                _output.WriteLine("  " + data);
            }
        }

        [Fact]
        public void TheFactorIsProportionalRatherThanAFixedPadding()
        {
            // The distinguishing test, stated as the criterion states it. A fixed padding gives a
            // constant DIFFERENCE across Result Lengths; a proportional one gives a constant
            // RATIO. Both are measured here and the two are compared, so the test would fail
            // against an implementation that added a constant -- which passing at one length
            // cannot detect.
            var spans = new List<double>();
            var extras = new List<double>();

            int shortest = int.MaxValue;
            int perSymbol = 0;

            foreach (object[] row in ResultLengths)
            {
                var length = (int)row[0];

                DemodResult result = Demodulate(Settings(length), Source(), length * 3);

                spans.Add(result.PreDemod.SpanFactor);
                extras.Add(result.PreDemod.SymbolSpan - length);

                shortest = Math.Min(shortest, length);
                perSymbol = result.PreDemod.SamplesPerSymbol;

                _output.WriteLine(
                    length + ": factor " + result.PreDemod.SpanFactor.ToString("F4") +
                    ", extra " + extras[extras.Count - 1].ToString("F2") + " symbols");
            }

            double factorSpread = spans.Max() - spans.Min();
            double extraSpread = extras.Max() - extras.Min();

            // DERIVED, NOT CHOSEN. The window is a whole number of samples, so the factor can only
            // land on multiples of one sample in a Result Length -- coarsest at the SHORTEST length
            // in the set, which is what bounds the spread. Picking a tolerance that happened to
            // pass would make this test agree with whatever the code did.
            double grid = 1.0 / (shortest * perSymbol);

            _output.WriteLine(
                "factor spread " + factorSpread.ToString("F4") +
                ", extra spread " + extraSpread.ToString("F2") + " symbols; the sample grid " +
                "allows " + grid.ToString("F4") + " at " + shortest + " symbols");

            // The ratio is the same number at every Result Length, to that grid; the difference is
            // not remotely constant, growing with the Result Length as a proportion must. The
            // clamped implementation this replaced spread by 0.072, twenty times the grid.
            Assert.True(
                factorSpread <= grid,
                "The factor varied by " + factorSpread + " across the Result Lengths, which is " +
                "more than the " + grid + " the sample grid accounts for.");

            Assert.True(
                extraSpread > 20.0,
                "The extra was near-constant at " + extraSpread +
                " symbols of spread, which is a fixed padding rather than a proportion.");
        }

        [Fact]
        public void TheResultIsIdenticalWhetherOrNotAPreDemodulationTraceIsTaken()
        {
            // The criterion: "EVM computed over the Result Length is identical whether or not a
            // pre-demodulation trace is displayed."
            //
            // It is identical BY CONSTRUCTION, and that is the point worth testing rather than a
            // tolerance. Step 7 cuts the wider window unconditionally and no later step reads it,
            // so there is no flag for a display to set and no path for it to change. What this
            // asserts is that taking every trace -- the whole of what a display does -- leaves the
            // result bit-identical, not merely close.
            DemodResult result = Demodulate(Settings(256), Source(), 800);

            double before = result.EvmPercent;
            double[] symbolsBefore = result.Trace.Measured
                .SelectMany(point => new[] { point.I, point.Q })
                .ToArray();

            foreach (PreDemodTrace trace in PreDemodTraces.All)
            {
                PreDemodTraceData data = PreDemodTraces.Take(result, trace);

                Assert.True(data.Count > 0);
            }

            double after = result.EvmPercent;
            double[] symbolsAfter = result.Trace.Measured
                .SelectMany(point => new[] { point.I, point.Q })
                .ToArray();

            _output.WriteLine("EVM " + before.ToString("F6") + " then " + after.ToString("F6"));

            Assert.Equal(before, after);
            Assert.Equal(symbolsBefore, symbolsAfter);

            // And the same demodulation run twice gives the same EVM, so the equality above is a
            // statement about the traces rather than about a measurement that never varies anyway.
            DemodResult again = Demodulate(Settings(256), Source(), 800);

            Assert.Equal(before, again.EvmPercent);
        }

        [Fact]
        public void ABurstsTransitionsAreInTheWiderWindowAndNotInTheResult()
        {
            // The criterion's third clause, and the reason for the whole requirement. The signal
            // is on through the filter's transient and the whole Result Length, then switches off
            // shortly after it -- so the result window sees nothing but full-amplitude signal and
            // the 20 % extension reaches past the edge.
            const int Length = 100;
            const int PerSymbol = 4;

            DemodSettings settings = Settings(Length);

            QpskSource source = Source();
            float[] samples = source.Generate(Length * 4);

            // Where the result window ends, in samples of the acquisition. The chain starts it at
            // the measurement filter's transient when there is no sync and no burst search, which
            // is FilterSymbolSpan symbols in.
            int resultEndSymbol = settings.FilterSymbolSpan + Length;

            // Three symbols beyond the result's last, so the edge is outside the Result Length and
            // inside the extension: 0.1 x 100 = 10 symbols of extension at that end.
            int offSymbol = resultEndSymbol + 3;

            Silence(samples, offSymbol, source.SampleRateHz / source.SymbolRateHz);

            DemodResult result = Demodulate(settings, source, samples);

            PreDemodWaveform window = result.PreDemod;

            Assert.NotNull(window);

            double reference = Rms(result.Trace.Samples, 0, result.Trace.SampleCount);

            double quietestInResult = Quietest(
                result.Trace.Samples, 0, result.Trace.SampleCount, PerSymbol);

            // Only the part of the wider window that lies BEYOND the result: the shared part is
            // the same samples and would say nothing about the extension.
            int beyond = window.ResultOffsetSamples + (Length * window.SamplesPerSymbol);

            double quietestBeyond = beyond >= window.SampleCount
                ? double.NaN
                : Quietest(window.Samples, beyond, window.SampleCount, PerSymbol);

            _output.WriteLine(
                "rms " + reference.ToString("F4") +
                "; quietest in the result " + quietestInResult.ToString("F4") +
                "; quietest beyond it " + quietestBeyond.ToString("F4"));

            Assert.True(
                beyond < window.SampleCount,
                "The wider window did not extend past the Result Length at all.");

            // The result window is signal throughout: nothing in it drops near the floor.
            Assert.True(
                quietestInResult > 0.4 * reference,
                "The result window contained a sample at " + quietestInResult +
                " against an rms of " + reference + ", so the transition is inside it.");

            // The extension reaches the off region, which the result never sees.
            Assert.True(
                quietestBeyond < 0.1 * reference,
                "The wider window's extension did not reach the burst edge: its quietest sample " +
                "was " + quietestBeyond + " against an rms of " + reference + ".");
        }

        [Fact]
        public void TheTwoSpectraShareAWindowAndDifferOnlyByTheAveraging()
        {
            DemodResult result = Demodulate(Settings(256), Source(), 800);

            PreDemodTraceData averaged = PreDemodTraces.Take(result, PreDemodTrace.Spectrum);
            PreDemodTraceData instant =
                PreDemodTraces.Take(result, PreDemodTrace.InstantaneousSpectrum);

            Assert.Equal(ResultTraceDomain.Frequency, averaged.Domain);
            Assert.Equal(ResultTraceDomain.Frequency, instant.Domain);
            Assert.Equal("dB", averaged.Unit);
            Assert.Same(averaged.Window, instant.Window);

            // Both span the same frequency axis -- the same sample rate, the same window -- which
            // is what makes them comparable at all. The instantaneous one transforms the whole
            // window, so it resolves it more finely.
            Assert.Equal(averaged.XStart, instant.XStart, 6);
            Assert.True(
                instant.Count > averaged.Count,
                "The instantaneous spectrum should resolve the window more finely than four " +
                "segments of it: " + instant.Count + " bins against " + averaged.Count + ".");

            Assert.True(instant.XStep < averaged.XStep);

            // And they agree about where the signal is. Both are baseband spectra of the same
            // modulated carrier, so the occupied band sits about zero in each.
            _output.WriteLine(
                "averaged " + averaged.Count + " bins of " + averaged.XStep.ToString("F0") +
                " Hz; instantaneous " + instant.Count + " bins of " +
                instant.XStep.ToString("F0") + " Hz");

            Assert.InRange(Centroid(averaged), -0.15, 0.15);
            Assert.InRange(Centroid(instant), -0.15, 0.15);

            // The averaged estimate is the steadier one, which is the reason it exists. Measured
            // as the bin-to-bin variation of the trace rather than asserted.
            double averagedRoughness = Roughness(averaged);
            double instantRoughness = Roughness(instant);

            _output.WriteLine(
                "roughness: averaged " + averagedRoughness.ToString("F3") +
                " dB, instantaneous " + instantRoughness.ToString("F3") + " dB");

            Assert.True(
                averagedRoughness < instantRoughness,
                "Averaging four segments should steady the estimate: " + averagedRoughness +
                " dB against " + instantRoughness + " dB.");
        }

        [Fact]
        public void APreDemodulationTraceIsRefusedByNameWhenThereIsNoWindow()
        {
            var result = (DemodResult)System.Runtime.Serialization.FormatterServices
                .GetUninitializedObject(typeof(DemodResult));

            Assert.False(PreDemodTraces.IsAvailable(result, PreDemodTrace.Time));

            InvalidOperationException refused = Assert.Throws<InvalidOperationException>(
                () => PreDemodTraces.Take(result, PreDemodTrace.Time));

            Assert.Contains("pre-demodulation window", refused.Message, StringComparison.Ordinal);
        }

        /// <summary>The centre of mass of a spectrum, as a fraction of its span.</summary>
        private static double Centroid(PreDemodTraceData data)
        {
            // Weighted by power rather than by the decibels themselves: a decibel value is a
            // logarithm and can be negative, so weighting by it would let the noise floor pull
            // the centroid about.
            double weight = 0.0;
            double moment = 0.0;

            for (int bin = 0; bin < data.Count; bin++)
            {
                double power = Math.Pow(10.0, data.Values[bin] / 10.0);
                double where = (bin / (double)(data.Count - 1)) - 0.5;

                weight += power;
                moment += power * where;
            }

            return weight <= 0.0 ? 0.0 : moment / weight;
        }

        /// <summary>Mean absolute bin-to-bin change, in decibels.</summary>
        private static double Roughness(PreDemodTraceData data)
        {
            double sum = 0.0;

            for (int bin = 1; bin < data.Count; bin++)
            {
                sum += Math.Abs(data.Values[bin] - data.Values[bin - 1]);
            }

            return data.Count < 2 ? 0.0 : sum / (data.Count - 1);
        }

        /// <summary>The rms magnitude of an interleaved waveform over a range of samples.</summary>
        private static double Rms(ReadOnlySpan<float> samples, int from, int to)
        {
            double sum = 0.0;

            for (int sample = from; sample < to; sample++)
            {
                double i = samples[2 * sample];
                double q = samples[(2 * sample) + 1];

                sum += (i * i) + (q * q);
            }

            return to <= from ? 0.0 : Math.Sqrt(sum / (to - from));
        }

        /// <summary>
        /// The quietest symbol-length stretch of an interleaved waveform, by rms.
        /// </summary>
        /// <remarks>
        /// Over a symbol rather than at a sample, because a shaped modulation passes through zero
        /// between symbols on its own — a single-sample minimum finds that and says nothing about
        /// whether the carrier was on. A symbol's worth of it cannot be quiet unless the burst is.
        /// </remarks>
        private static double Quietest(
            ReadOnlySpan<float> samples, int from, int to, int perSymbol)
        {
            double quietest = double.MaxValue;

            for (int start = from; start + perSymbol <= to; start += perSymbol)
            {
                quietest = Math.Min(quietest, Rms(samples, start, start + perSymbol));
            }

            return quietest == double.MaxValue ? double.NaN : quietest;
        }

        /// <summary>Switches the waveform off from a symbol onward, with a short ramp.</summary>
        /// <param name="samples">The interleaved waveform, modified in place.</param>
        /// <param name="atSymbol">The symbol the ramp starts at.</param>
        /// <param name="samplesPerSymbol">Samples per symbol in the acquisition.</param>
        /// <remarks>
        /// A ramp of one symbol rather than a step: a step is a discontinuity whose spectrum is
        /// everywhere, and the transition region this exists to make visible is a real
        /// transmitter's ramp rather than an edge no amplifier could produce.
        /// </remarks>
        private static void Silence(float[] samples, int atSymbol, double samplesPerSymbol)
        {
            int at = (int)Math.Round(atSymbol * samplesPerSymbol);
            int ramp = Math.Max(1, (int)Math.Round(samplesPerSymbol));
            int count = samples.Length / 2;

            for (int sample = at; sample < count; sample++)
            {
                double envelope = sample >= at + ramp
                    ? 0.0
                    : 1.0 - ((sample - at) / (double)ramp);

                samples[2 * sample] = (float)(samples[2 * sample] * envelope);
                samples[(2 * sample) + 1] = (float)(samples[(2 * sample) + 1] * envelope);
            }
        }

        private static DemodSettings Settings(int resultLength) =>
            new DemodSettings
            {
                SymbolRateHz = 1e6,
                ResultLengthSymbols = resultLength,
            };

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

        private static DemodResult Demodulate(
            DemodSettings settings, QpskSource source, float[] samples)
        {
            settings.SymbolRateHz = source.SymbolRateHz;

            return new Demodulator().Run(samples, source.SampleRateHz, settings);
        }
    }
}
