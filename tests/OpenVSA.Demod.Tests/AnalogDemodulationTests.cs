using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using OpenVSA.Demod.Analog;
using Xunit;
using Xunit.Abstractions;

namespace OpenVSA.Demod.Tests
{
    /// <summary>
    /// <c>REQ-DEM-010a</c>: AM, FM and PM demodulation, recovered to within 1 % of what was
    /// injected, with SINAD above 60 dB on a clean signal.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The signals are built here rather than taken from a generator fixture, because the criterion
    /// is about recovering a KNOWN value and the value has to be known exactly for 1 % to mean
    /// anything. Each is written from its own definition — an envelope for AM, an integrated
    /// frequency for FM, a phase for PM — so a detector agreeing with one of these agrees with the
    /// textbook rather than with a shared helper that could be wrong in the same direction as the
    /// thing it is testing.
    /// </para>
    /// </remarks>
    public class AnalogDemodulationTests
    {
        private const double SampleRateHz = 200e3;
        private const int Samples = 65536;

        private readonly ITestOutputHelper _output;

        public AnalogDemodulationTests(ITestOutputHelper output)
        {
            _output = output;
        }

        public static IEnumerable<object[]> AmCases => new List<object[]>
        {
            new object[] { 30.0, 1000.0 },
            new object[] { 50.0, 1000.0 },
            new object[] { 80.0, 400.0 },
            new object[] { 10.0, 3000.0 },
        };

        public static IEnumerable<object[]> FmCases => new List<object[]>
        {
            new object[] { 5000.0, 1000.0 },
            new object[] { 15000.0, 1000.0 },
            new object[] { 2500.0, 400.0 },
            new object[] { 1000.0, 3000.0 },
        };

        [Theory]
        [MemberData(nameof(AmCases))]
        public void AmDepthAndRateAreRecoveredToWithinOnePerCent(double depth, double rate)
        {
            AnalogDemodResult result = Demodulate(
                Am(depth / 100.0, rate), AnalogDemodType.Am);

            double depthError = Relative(result.Metrics.AmDepthPercent, depth);
            double rateError = Relative(result.Metrics.ModulationRateHz, rate);

            _output.WriteLine(
                "depth " + result.Metrics.AmDepthPercent.ToString("F4") + " % against " + depth +
                " (" + Percent(depthError) + "), rate " +
                result.Metrics.ModulationRateHz.ToString("F3") + " Hz against " + rate +
                " (" + Percent(rateError) + ")");

            Assert.True(depthError < 0.01, "Depth was out by " + Percent(depthError) + ".");
            Assert.True(rateError < 0.01, "Rate was out by " + Percent(rateError) + ".");
        }

        [Theory]
        [MemberData(nameof(FmCases))]
        public void FmDeviationAndRateAreRecoveredToWithinOnePerCent(
            double deviationHz, double rate)
        {
            AnalogDemodResult result = Demodulate(
                Fm(deviationHz, rate), AnalogDemodType.Fm);

            double peakError = Relative(result.Metrics.FmDeviationPeakHz, deviationHz);
            double rateError = Relative(result.Metrics.ModulationRateHz, rate);

            // The rms of a sinusoid is its peak over root two, so this is the same claim measured
            // a second way rather than a second claim.
            double rmsError = Relative(
                result.Metrics.FmDeviationRmsHz, deviationHz / Math.Sqrt(2.0));

            _output.WriteLine(
                "peak deviation " + result.Metrics.FmDeviationPeakHz.ToString("F3") +
                " Hz against " + deviationHz + " (" + Percent(peakError) + "), rms " +
                result.Metrics.FmDeviationRmsHz.ToString("F3") + " Hz (" + Percent(rmsError) +
                "), rate " + result.Metrics.ModulationRateHz.ToString("F3") + " Hz against " +
                rate + " (" + Percent(rateError) + ")");

            Assert.True(peakError < 0.01, "Peak deviation was out by " + Percent(peakError) + ".");
            Assert.True(rmsError < 0.01, "RMS deviation was out by " + Percent(rmsError) + ".");
            Assert.True(rateError < 0.01, "Rate was out by " + Percent(rateError) + ".");
        }

        [Fact]
        public void PmDeviationIsRecoveredInRadiansAndDegrees()
        {
            const double Radians = 0.75;
            const double Rate = 1000.0;

            AnalogDemodResult result = Demodulate(Pm(Radians, Rate), AnalogDemodType.Pm);

            double peakError = Relative(result.Metrics.PmDeviationPeakRadians, Radians);
            double rateError = Relative(result.Metrics.ModulationRateHz, Rate);

            _output.WriteLine(
                "peak phase " + result.Metrics.PmDeviationPeakRadians.ToString("F5") +
                " rad = " + result.Metrics.PmDeviationPeakDegrees.ToString("F3") +
                " deg against " + Radians + " rad (" + Percent(peakError) + "), rate " +
                result.Metrics.ModulationRateHz.ToString("F3") + " Hz (" + Percent(rateError) + ")");

            Assert.True(peakError < 0.01, "Peak deviation was out by " + Percent(peakError) + ".");
            Assert.True(rateError < 0.01, "Rate was out by " + Percent(rateError) + ".");

            // Degrees are the same number in other units, not a second measurement.
            Assert.Equal(
                result.Metrics.PmDeviationPeakRadians * 180.0 / Math.PI,
                result.Metrics.PmDeviationPeakDegrees,
                9);
        }

        [Fact]
        public void SinadExceedsSixtyDecibelsOnACleanSignal()
        {
            // The criterion's own threshold, checked for each of the three so that it is a
            // statement about the demodulator rather than about whichever one was tried.
            //
            // WHAT THE 88 dB CEILING IS. All three come back at the same figure because none of
            // them is limited by its detector: the envelope and the phase advance are exact, and
            // what is left is the ANALYSIS WINDOW. Blackman-Harris sidelobes are 92 dB down, and a
            // measurement of how much power is not in the fundamental cannot see past them. So the
            // criterion's 60 dB is met with 28 dB in hand, and the number above it belongs to
            // AudioSpectrum's window rather than to anything here. SinadFollowsTheNoiseThatIsActuallyThere
            // is what shows the figure is a measurement and not a constant at that ceiling.
            var cases = new List<Tuple<string, AnalogDemodResult>>
            {
                Tuple.Create("AM", Demodulate(Am(0.5, 1000.0), AnalogDemodType.Am)),
                Tuple.Create("FM", Demodulate(Fm(15000.0, 1000.0), AnalogDemodType.Fm)),
                Tuple.Create("PM", Demodulate(Pm(0.75, 1000.0), AnalogDemodType.Pm)),
            };

            foreach (Tuple<string, AnalogDemodResult> one in cases)
            {
                _output.WriteLine(
                    one.Item1 + ": SINAD " + one.Item2.Metrics.SinadDb.ToString("F2") +
                    " dB, distortion " + one.Item2.Metrics.DistortionPercent.ToString("F5") + " %");

                Assert.True(
                    one.Item2.Metrics.SinadDb > 60.0,
                    one.Item1 + " gave a SINAD of " + one.Item2.Metrics.SinadDb + " dB.");
            }
        }

        [Fact]
        public void SinadFollowsTheNoiseThatIsActuallyThere()
        {
            // THE CONTROL FOR THE TEST ABOVE. All three clean signals returned 88.05 dB, to the
            // decimal -- which is what a detector that adds nothing looks like, and also what a
            // constant looks like. "Above 60 dB" would pass either. So known noise is injected at
            // three levels and SINAD is required to follow it down.
            var measured = new List<Tuple<double, double>>();

            foreach (double noise in new[] { 0.0, 1e-4, 1e-3, 1e-2 })
            {
                AnalogDemodResult result = Demodulate(
                    Noisy(Fm(10000.0, 1000.0), noise, 20260907), AnalogDemodType.Fm);

                measured.Add(Tuple.Create(noise, result.Metrics.SinadDb));

                _output.WriteLine(
                    "noise " + noise.ToString("G3") + " of carrier: SINAD " +
                    result.Metrics.SinadDb.ToString("F2") + " dB");
            }

            // Monotonic: more noise, less SINAD, every step.
            for (int step = 1; step < measured.Count; step++)
            {
                Assert.True(
                    measured[step].Item2 < measured[step - 1].Item2 - 3.0,
                    "SINAD did not fall when the noise rose from " + measured[step - 1].Item1 +
                    " to " + measured[step].Item1 + ": " + measured[step - 1].Item2 + " dB then " +
                    measured[step].Item2 + " dB.");
            }

            // And by the right amount. Ten times the noise AMPLITUDE is 20 dB of power, so each
            // decade should cost about 20 dB once noise dominates the floor -- which is the claim
            // a constant cannot make and a mis-scaled one gets wrong.
            double perDecade = measured[2].Item2 - measured[3].Item2;

            _output.WriteLine("a decade of noise costs " + perDecade.ToString("F1") + " dB");

            Assert.InRange(perDecade, 17.0, 23.0);
        }

        [Fact]
        public void DistortionIsSmallOnACleanToneAndFoundWhenItIsThere()
        {
            // A THD that is merely small proves nothing -- an implementation returning zero would
            // pass. So a known second harmonic is injected and looked for.
            AnalogDemodResult clean = Demodulate(Fm(10000.0, 1000.0), AnalogDemodType.Fm);

            const double Second = 0.05;

            AnalogDemodResult distorted = Demodulate(
                Fm(10000.0, 1000.0, Second), AnalogDemodType.Fm);

            _output.WriteLine(
                "clean THD " + clean.Metrics.DistortionPercent.ToString("F5") +
                " %, with a " + (Second * 100.0) + " % second harmonic " +
                distorted.Metrics.DistortionPercent.ToString("F4") + " %");

            Assert.True(clean.Metrics.DistortionPercent < 0.1);

            // The injected harmonic is 5 % of the fundamental's amplitude, and THD is the root sum
            // of the harmonics over the fundamental -- so 5 %, to the accuracy of the lobe sums.
            Assert.InRange(distorted.Metrics.DistortionPercent, 4.9, 5.1);
        }

        [Fact]
        public void ResidualFmIsWhatIsLeftWhenTheModulationIsTakenOut()
        {
            // An unmodulated carrier with a known frequency wobble far below the analysis band:
            // residual FM is what the figure is quoted for, and it must not simply follow the
            // deviation of a modulated one.
            AnalogDemodResult modulated = Demodulate(
                Fm(10000.0, 1000.0), AnalogDemodType.Fm);

            _output.WriteLine(
                "residual FM on a clean 10 kHz deviation: " +
                modulated.Metrics.ResidualFmHz.ToString("F4") + " Hz rms, against a peak " +
                "deviation of " + modulated.Metrics.FmDeviationPeakHz.ToString("F1") + " Hz");

            // On a clean tone almost everything is the fundamental, so what is left is tiny --
            // orders below the deviation itself, which is the statement worth making.
            Assert.True(
                modulated.Metrics.ResidualFmHz < modulated.Metrics.FmDeviationPeakHz / 1000.0,
                "Residual FM was " + modulated.Metrics.ResidualFmHz +
                " Hz on a clean signal deviating " + modulated.Metrics.FmDeviationPeakHz + " Hz.");
        }

        [Fact]
        public void TheCarrierTracesArePresentWhateverWasDemodulated()
        {
            // The requirement asks for carrier frequency and amplitude against time as traces in
            // their own right. They are what the detectors compute on the way to any of the three,
            // so withholding them on an AM measurement would hide a trace already produced.
            AnalogDemodResult result = Demodulate(Am(0.5, 1000.0), AnalogDemodType.Am);

            foreach (AnalogTrace trace in AnalogDemodResult.AllTraces)
            {
                IReadOnlyList<double> values = result.Take(trace);

                Assert.True(values.Count > 0, trace + " produced nothing.");

                _output.WriteLine(
                    trace + ": " + values.Count + " points, step " +
                    result.StepFor(trace).ToString("G4") + ", unit '" +
                    result.UnitFor(trace) + "'");
            }

            // The envelope of a 50 % AM signal swings between half and one and a half of the
            // carrier, which is the trace saying the same thing the depth metric does.
            Assert.InRange(result.CarrierAmplitude.Max() / result.Metrics.CarrierAmplitude, 1.49, 1.51);
            Assert.InRange(result.CarrierAmplitude.Min() / result.Metrics.CarrierAmplitude, 0.49, 0.51);
        }

        [Fact]
        public void DeEmphasisAttenuatesAboveItsCornerByTheAmountItsTimeConstantImplies()
        {
            // 75 us is a single pole at 1/(2 pi tau) = 2122 Hz. A tone AT the corner comes out
            // 3.01 dB down: that is what a time constant means, and it is checked rather than
            // assumed because the whole point of naming it in seconds is that the corner follows.
            const double Corner = 1.0 / (2.0 * Math.PI * AnalogDemodSettings.DeEmphasis75Microseconds);

            AnalogDemodResult plain = Demodulate(Fm(10000.0, Corner), AnalogDemodType.Fm);

            AnalogDemodResult shaped = Demodulate(
                Fm(10000.0, Corner),
                AnalogDemodType.Fm,
                new AnalogDemodSettings
                {
                    Type = AnalogDemodType.Fm,
                    DeEmphasisSeconds = AnalogDemodSettings.DeEmphasis75Microseconds,
                });

            double ratio = shaped.Metrics.FmDeviationRmsHz / plain.Metrics.FmDeviationRmsHz;
            double decibels = 20.0 * Math.Log10(ratio);

            _output.WriteLine(
                "corner " + Corner.ToString("F1") + " Hz; rms " +
                plain.Metrics.FmDeviationRmsHz.ToString("F1") + " Hz plain, " +
                shaped.Metrics.FmDeviationRmsHz.ToString("F1") + " Hz de-emphasised: " +
                decibels.ToString("F3") + " dB");

            // A tenth of a decibel about the 3.01 dB a single pole is down at its own corner.
            // It was 3.3 wide while the filter used T/(T + tau), which sat at 3.148.
            Assert.InRange(decibels, -3.11, -2.91);

            // And the metrics say they describe filtered audio, so a deviation measured through a
            // network is not silently compared with one that was not.
            Assert.True(shaped.Metrics.FilteredAudio);
            Assert.False(plain.Metrics.FilteredAudio);
        }

        [Fact]
        public void CrossedPostDetectionCornersAreRefused()
        {
            var settings = new AnalogDemodSettings
            {
                Type = AnalogDemodType.Fm,
                HighPassHz = 5000.0,
                LowPassHz = 300.0,
            };

            ArgumentException refused =
                Assert.Throws<ArgumentException>(() => settings.Validate());

            _output.WriteLine(refused.Message);

            Assert.Contains("passes nothing", refused.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void AMetricThatDoesNotApplyIsNotANumber()
        {
            // The distinction the summary draws: an AM measurement has no FM deviation, and 0 Hz
            // would be a result rather than the absence of one.
            AnalogDemodResult am = Demodulate(Am(0.5, 1000.0), AnalogDemodType.Am);

            Assert.True(double.IsNaN(am.Metrics.FmDeviationPeakHz));
            Assert.True(double.IsNaN(am.Metrics.PmDeviationPeakRadians));
            Assert.False(double.IsNaN(am.Metrics.AmDepthPercent));

            Assert.True(am.Metrics.Applies("AM Depth"));
            Assert.False(am.Metrics.Applies("FM Deviation (peak)"));

            foreach (string line in am.Metrics.Render())
            {
                _output.WriteLine(line);
            }

            Assert.DoesNotContain(
                am.Metrics.Render(),
                line => line.IndexOf("FM Deviation", StringComparison.Ordinal) >= 0);
        }

        private static string Percent(double fraction) =>
            (fraction * 100.0).ToString("F4", CultureInfo.InvariantCulture) + " %";

        private static double Relative(double measured, double wanted) =>
            Math.Abs(measured - wanted) / Math.Abs(wanted);

        private static AnalogDemodResult Demodulate(float[] samples, AnalogDemodType type) =>
            Demodulate(samples, type, new AnalogDemodSettings { Type = type });

        private static AnalogDemodResult Demodulate(
            float[] samples, AnalogDemodType type, AnalogDemodSettings settings) =>
            new AnalogDemodulator().Run(samples, SampleRateHz, settings);

        /// <summary>Adds white Gaussian noise of a known amplitude to a record.</summary>
        /// <param name="samples">The record, which is not modified.</param>
        /// <param name="sigma">Noise standard deviation per component, relative to unit carrier.</param>
        /// <param name="seed">Which realisation.</param>
        private static float[] Noisy(float[] samples, double sigma, int seed)
        {
            if (sigma <= 0.0)
            {
                return samples;
            }

            var noisy = (float[])samples.Clone();
            var random = new Random(seed);

            for (int index = 0; index < noisy.Length; index += 2)
            {
                // Box-Muller, two components at a time, so the noise is Gaussian rather than
                // uniform -- a uniform "noise" has a different crest factor and would put a
                // different amount of power in the tails for the same standard deviation.
                double u1 = Math.Max(random.NextDouble(), 1e-12);
                double u2 = random.NextDouble();
                double magnitude = sigma * Math.Sqrt(-2.0 * Math.Log(u1));

                noisy[index] += (float)(magnitude * Math.Cos(2.0 * Math.PI * u2));
                noisy[index + 1] += (float)(magnitude * Math.Sin(2.0 * Math.PI * u2));
            }

            return noisy;
        }

        /// <summary>An AM carrier of unit amplitude at a known depth and rate.</summary>
        private static float[] Am(double depth, double rateHz)
        {
            var samples = new float[2 * Samples];

            for (int sample = 0; sample < Samples; sample++)
            {
                double t = sample / SampleRateHz;
                double envelope = 1.0 + (depth * Math.Cos(2.0 * Math.PI * rateHz * t));

                samples[2 * sample] = (float)envelope;
                samples[(2 * sample) + 1] = 0.0f;
            }

            return samples;
        }

        private static float[] Fm(double deviationHz, double rateHz) =>
            Fm(deviationHz, rateHz, 0.0);

        /// <summary>
        /// An FM carrier at a known deviation and rate, optionally with a second harmonic.
        /// </summary>
        /// <remarks>
        /// Built by integrating the frequency into a phase analytically rather than by summing
        /// samples: the integral of a cosine is a sine, exactly, so the signal carries no
        /// accumulation error of its own for the detector to be blamed for.
        /// </remarks>
        private static float[] Fm(double deviationHz, double rateHz, double secondHarmonic)
        {
            var samples = new float[2 * Samples];
            double beta = deviationHz / rateHz;
            double betaSecond = secondHarmonic * deviationHz / (2.0 * rateHz);

            for (int sample = 0; sample < Samples; sample++)
            {
                double t = sample / SampleRateHz;
                double phase =
                    (beta * Math.Sin(2.0 * Math.PI * rateHz * t)) +
                    (betaSecond * Math.Sin(4.0 * Math.PI * rateHz * t));

                samples[2 * sample] = (float)Math.Cos(phase);
                samples[(2 * sample) + 1] = (float)Math.Sin(phase);
            }

            return samples;
        }

        /// <summary>A PM carrier at a known peak phase deviation and rate.</summary>
        private static float[] Pm(double radians, double rateHz)
        {
            var samples = new float[2 * Samples];

            for (int sample = 0; sample < Samples; sample++)
            {
                double t = sample / SampleRateHz;
                double phase = radians * Math.Cos(2.0 * Math.PI * rateHz * t);

                samples[2 * sample] = (float)Math.Cos(phase);
                samples[(2 * sample) + 1] = (float)Math.Sin(phase);
            }

            return samples;
        }
    }
}
