using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using OpenVSA.Demod.Chain;
using OpenVSA.Demod.Results;
using OpenVSA.Demod.Signal;
using OpenVSA.Synthesis;
using Xunit;
using Xunit.Abstractions;

namespace OpenVSA.Demod.Tests
{
    /// <summary>
    /// <c>REQ-DEM-050</c> and <c>REQ-DEM-053</c>: what the equaliser corrects, and the channel it
    /// reports having corrected.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The equaliser is told nothing about the channel.</strong> That is the criterion's own
    /// emphasis — "coefficients derive from the measured signal, not from the injected channel: the
    /// test supplies no knowledge of the impairment" — and it is what makes these tests worth
    /// running. Each impairment is applied to the generated waveform and the demodulator is handed
    /// the samples and nothing else.
    /// </para>
    /// <para>
    /// <strong>What is here is REQ-DEM-053's mechanism, not REQ-DEM-050's acceptance.</strong> The
    /// three impairment classes were measured and the equaliser corrects 30 dB of a 52 dB
    /// impairment, which is not the "within 1 dB of the unimpaired value" the criterion asks for.
    /// That is a property of how the equaliser is fitted rather than of this trace, and #159 carries
    /// the measurements and the root cause; the tests for it belong with the work that closes it.
    /// </para>
    /// </remarks>
    public class EqualiserChannelTests
    {
        private const double SymbolRateHz = 1e6;
        private const double SampleRateHz = 16e6;
        private const int Symbols = 4000;

        private readonly ITestOutputHelper _output;

        public EqualiserChannelTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void AnUnimpairedSignalIsNotMadeWorseByTurningTheEqualiserOn()
        {
            // "On an unimpaired signal the equaliser leaves EVM unchanged to within 0.1 dB rather
            // than degrading it." An equaliser that always helps a bad signal and always hurts a
            // good one is not a feature.
            DemodResult off = Demodulate(null, equalise: false);
            DemodResult on = Demodulate(null, equalise: true);

            double change = 20.0 * Math.Log10(on.EvmPercent / off.EvmPercent);

            _output.WriteLine(
                "unimpaired: " + off.EvmPercent.ToString("F6", CultureInfo.InvariantCulture) +
                " %rms off, " + on.EvmPercent.ToString("F6", CultureInfo.InvariantCulture) +
                " %rms on, a change of " +
                change.ToString("F4", CultureInfo.InvariantCulture) + " dB");

            Assert.True(
                Math.Abs(change) < 0.1,
                "turning the equaliser on moved a clean signal's EVM by " + change + " dB");
        }

        [Fact]
        public void TheChannelResponseIsAbsentWhenTheEqualiserDidNotRun()
        {
            // Null rather than empty, for the same reason the coefficients are: a trace that does
            // not exist is a different thing from one with no data in it (REQ-DEM-080).
            DemodResult result = Demodulate(null, equalise: false);

            Assert.Null(result.ChannelResponse);
            Assert.Null(result.EqualiserCoefficients);
        }

        [Fact]
        public void TheRegularisationIsAnnotatedOnTheTrace()
        {
            // "the regularisation shall be documented and annotated on the trace." A reader has to
            // be able to tell a bounded band edge from a measured one.
            DemodResult result = Demodulate(TwoRay(0.4, 1.5), equalise: true);

            string annotation = result.ChannelResponse.Regularisation;

            _output.WriteLine(annotation);

            Assert.Contains("W*/(|W|^2 + e)", annotation, StringComparison.Ordinal);
            Assert.Contains("signal-to-noise", annotation, StringComparison.Ordinal);
            Assert.True(result.ChannelResponse.Epsilon > 0.0);
        }

        /// <summary>
        /// The edge of the band the signal occupies, which is what an impairment's size is quoted
        /// against.
        /// </summary>
        /// <remarks>
        /// 🔴 Not the Nyquist frequency. A root raised cosine at 0.35 occupies 1.35 times the symbol
        /// rate — 1.35 MHz of the 16 MHz this record is sampled at — so an impairment defined
        /// across the whole span puts less than a tenth of itself where the signal is. A first
        /// version of these fixtures did exactly that, and a "6 dB tilt" was 0.5 dB across the
        /// signal; the numbers looked reasonable and were measuring almost nothing.
        /// </remarks>
        private const double BandEdgeHz = SymbolRateHz * 1.35 / 2.0;

        /// <summary>A linear tilt across the occupied band, as a frequency-domain multiply.</summary>
        private static Action<float[]> Tilt(double db) =>
            samples => Filter(
                samples,
                hertz => FromPolar(Math.Pow(10.0, db * (hertz / BandEdgeHz) / 40.0), 0.0));

        /// <summary>A phase that varies with the square of frequency: a linear group delay.</summary>
        private static Action<float[]> QuadraticPhase(double radiansAtEdge) =>
            samples => Filter(
                samples,
                hertz => FromPolar(
                    1.0,
                    radiansAtEdge * (hertz / BandEdgeHz) * (hertz / BandEdgeHz)));

        /// <summary>A second ray, delayed and attenuated.</summary>
        private static Action<float[]> TwoRay(double amplitude, double delaySymbols)
        {
            return samples =>
            {
                int delay = (int)Math.Round(delaySymbols * SampleRateHz / SymbolRateHz);
                var original = (float[])samples.Clone();

                for (int sample = delay; sample < samples.Length / 2; sample++)
                {
                    samples[2 * sample] += (float)(amplitude * original[2 * (sample - delay)]);
                    samples[(2 * sample) + 1] +=
                        (float)(amplitude * original[(2 * (sample - delay)) + 1]);
                }
            };
        }

        private static Iq FromPolar(double magnitude, double radians) =>
            new Iq(magnitude * Math.Cos(radians), magnitude * Math.Sin(radians));

        /// <summary>Applies a frequency response to a waveform, by transform.</summary>
        /// <remarks>
        /// The whole record at once rather than block by block: these are test impairments and the
        /// record is a few hundred thousand samples, so the simplest thing that is exactly right is
        /// the right thing. A block-wise version would have edge effects of its own to explain.
        /// </remarks>
        private static void Filter(float[] samples, Func<double, Iq> response)
        {
            int count = samples.Length / 2;
            int length = 1;

            while (length < count)
            {
                length *= 2;
            }

            var spectrum = new double[2 * length];

            for (int sample = 0; sample < count; sample++)
            {
                Iq.Set(spectrum, sample, new Iq(samples[2 * sample], samples[(2 * sample) + 1]));
            }

            Dsp.Fft.IFftProvider fft = Dsp.Fft.FftProviders.Active;

            fft.Forward(new Span<double>(spectrum));

            for (int bin = 0; bin < length; bin++)
            {
                double hertz = (bin <= length / 2 ? bin : bin - length) * SampleRateHz / length;

                Iq value = Iq.At(spectrum, bin);
                Iq gain = response(hertz);

                Iq.Set(spectrum, bin, value * gain);
            }

            fft.Inverse(new Span<double>(spectrum));

            for (int sample = 0; sample < count; sample++)
            {
                Iq value = Iq.At(spectrum, sample);

                samples[2 * sample] = (float)(value.I / length);
                samples[(2 * sample) + 1] = (float)(value.Q / length);
            }
        }

        private static DemodResult Demodulate(Action<float[]> impair, bool equalise)
        {
            var source = new ContinuousModulatedSource
            {
                Scheme = ModulationScheme.Qpsk(),
                SymbolRateHz = SymbolRateHz,
                SampleRateHz = SampleRateHz,
                RollOff = 0.35,
                PulseSpanSymbols = 20,
                Seed = 20260824,
            };

            var samples = new float[2 * Symbols * 16];

            source.Restart();
            source.Fill(samples);

            if (impair != null)
            {
                impair(samples);
            }

            var settings = new DemodSettings
            {
                Constellation = Constellation.Qpsk(),
                SymbolRateHz = SymbolRateHz,
                ResultLengthSymbols = 512,
                FilterSymbolSpan = 20,
                MeasurementFilter = PulseFilterType.RootRaisedCosine,
                MeasurementFilterAlpha = 0.35,
                ReferenceFilterAlpha = 0.35,
                EqualiserEnabled = equalise,
                EqualiserTaps = 41,
            };

            return new Demodulator().Run(samples, SampleRateHz, settings);
        }
    }
}
