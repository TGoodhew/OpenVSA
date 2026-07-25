using System;
using System.Collections.Generic;
using OpenVSA.Dsp.Fft;
using OpenVSA.Dsp.Windowing;
using Xunit;

namespace OpenVSA.Dsp.Tests
{
    /// <summary>
    /// <c>REQ-DSP-011</c>: coherent gain corrects a discrete tone's amplitude, ENBW corrects a
    /// noise density, and each is right for its own kind of signal and wrong for the other.
    /// </summary>
    /// <remarks>
    /// This covers the two corrections themselves. Selecting between them automatically by trace
    /// data type is the remaining part of the requirement and arrives with the spectrum
    /// measurement, which is where trace data types first exist.
    /// </remarks>
    public class WindowCorrectionTests
    {
        private static readonly IFftProvider Fft = new ManagedFftProvider();

        public static IEnumerable<object[]> AllWindows() => WindowTests.AllWindows();

        // ---- Coherent gain: discrete-tone amplitude --------------------------------------------

        [Theory]
        [MemberData(nameof(AllWindows))]
        public void CwTone_ReadsItsCorrectAmplitudeUnderEveryWindow(WindowType type)
        {
            // REQ-DSP-011 AC: within 0.05 dB, under every window.
            //
            // Closed form: for x[n] = A*e^(2*pi*i*k0*n/N) windowed by w, X[k0] = A * sum(w)
            // exactly, so A = |X[k0]| / (N * coherent gain). The window's own transform has nulls
            // at every other integer bin, so nothing leaks into the peak.
            const int length = 4096;
            const int bin = 137;
            const double amplitude = 0.75;

            Window window = Window.Get(type, length);
            double[] spectrum = TransformTone(window, length, bin, amplitude);

            double peak = Magnitude(spectrum, bin);
            double measured = peak / (length * window.CoherentGain);

            double errorDb = 20.0 * Math.Log10(measured / amplitude);
            Assert.True(Math.Abs(errorDb) <= 0.05,
                type + ": amplitude read " + measured.ToString("F6") + " against " + amplitude +
                " — " + errorDb.ToString("F4") + " dB error, tolerance 0.05 dB.");
        }

        [Fact]
        public void WithoutCoherentGainCorrection_TheReadingIsWrongByTheWindowsGain()
        {
            // The correction has to be doing something, or the test above would pass on a stub
            // that returned the raw peak. Flat Top's coherent gain is about 0.213, which is a
            // 13 dB error if it is not applied.
            const int length = 4096;
            Window window = Window.Get(WindowType.FlatTop, length);
            double[] spectrum = TransformTone(window, length, 137, 0.75);

            double uncorrected = Magnitude(spectrum, 137) / length;
            double errorDb = 20.0 * Math.Log10(uncorrected / 0.75);

            Assert.True(errorDb < -10.0,
                "Omitting the correction should be a large error, not a subtle one; got " +
                errorDb.ToString("F2") + " dB.");
        }

        [Fact]
        public void OffBinTone_IsWhyFlatTopIsTheDefault()
        {
            // The AC above puts the tone at a bin centre, where every window is exact. The reason
            // the reference product defaults to Flat Top shows up half a bin away: Flat Top holds
            // its amplitude to hundredths of a dB, Hann loses over a dB. This is the behaviour
            // being cloned, so it is worth asserting rather than assuming.
            const int length = 4096;
            const double amplitude = 0.75;
            const double offset = 0.5;

            double flatTopError = OffBinAmplitudeErrorDb(WindowType.FlatTop, length, offset, amplitude);
            double hannError = OffBinAmplitudeErrorDb(WindowType.Hann, length, offset, amplitude);

            Assert.True(Math.Abs(flatTopError) < 0.05,
                "Flat Top should stay within 0.05 dB half a bin off centre; got " +
                flatTopError.ToString("F4") + " dB.");

            Assert.True(hannError < -1.0,
                "Hann should lose more than 1 dB half a bin off centre, or the comparison is not " +
                "showing what Flat Top is for; got " + hannError.ToString("F4") + " dB.");
        }

        // ---- ENBW: noise density ----------------------------------------------------------------

        [Theory]
        [MemberData(nameof(AllWindows))]
        public void WhiteNoise_ReadsItsCorrectPowerDensityUnderEveryWindow(WindowType type)
        {
            // REQ-DSP-011 AC: within 0.1 dB, under every window.
            //
            // Closed form: for white noise of total power P, E|X[k]|^2 = P * sum(w^2). Dividing by
            // (sum w)^2 * ENBW gives P/N — the noise power in one bin's worth of bandwidth, which
            // is what a density readout normalised to bin width means. Both corrections appear, so
            // a wrong ENBW cannot be absorbed by a compensating error in the coherent gain.
            const int length = 16384;
            const int trials = 8;
            const double totalPower = 2.0;

            Window window = Window.Get(type, length);
            var random = new Random(20260725 + (int)type);

            double accumulated = 0.0;
            for (int trial = 0; trial < trials; trial++)
            {
                double[] data = WhiteNoise(random, length, totalPower);
                window.ApplyTo(new Span<double>(data));
                Fft.Forward(data);

                for (int k = 0; k < length; k++)
                {
                    accumulated += data[k * 2] * data[k * 2] + data[k * 2 + 1] * data[k * 2 + 1];
                }
            }

            double meanBinPower = accumulated / (trials * (double)length);
            double coherentSum = length * window.CoherentGain;
            double measured = meanBinPower / (coherentSum * coherentSum * window.Enbw);

            double expected = totalPower / length;
            double errorDb = 10.0 * Math.Log10(measured / expected);

            Assert.True(Math.Abs(errorDb) <= 0.1,
                type + ": noise density read " + errorDb.ToString("F4") +
                " dB from the analytic value, tolerance 0.1 dB.");
        }

        [Fact]
        public void UsingCoherentGainForNoise_IsWrongByTheEnbw()
        {
            // The two corrections are not interchangeable, which is the whole point of selecting
            // between them by trace data type. Applying the tone correction to noise is wrong by
            // exactly the ENBW — 5.8 dB for Flat Top.
            Window window = Window.Get(WindowType.FlatTop, 1024);

            double wrongByDb = 10.0 * Math.Log10(window.Enbw);

            Assert.True(wrongByDb > 5.0,
                "Flat Top's ENBW should make the wrong correction a large error; got " +
                wrongByDb.ToString("F2") + " dB.");
        }

        [Theory]
        [MemberData(nameof(AllWindows))]
        public void NoisePowerGain_IsConsistentWithEnbwAndCoherentGain(WindowType type)
        {
            // ENBW = N * sum(w^2) / (sum w)^2 = NoisePowerGain / CoherentGain^2. Asserting the
            // identity catches a normalisation slip in any one of the three.
            Window window = Window.Get(type, 2048);

            double derived = window.NoisePowerGain / (window.CoherentGain * window.CoherentGain);

            Assert.Equal(window.Enbw, derived, 9);
        }

        // ---- Helpers ------------------------------------------------------------------------------

        private static double[] TransformTone(Window window, int length, double bin, double amplitude)
        {
            var data = new double[length * 2];
            for (int n = 0; n < length; n++)
            {
                double angle = 2.0 * Math.PI * bin * n / length;
                data[n * 2] = amplitude * Math.Cos(angle);
                data[n * 2 + 1] = amplitude * Math.Sin(angle);
            }

            window.ApplyTo(new Span<double>(data));
            Fft.Forward(data);
            return data;
        }

        private static double OffBinAmplitudeErrorDb(
            WindowType type, int length, double offset, double amplitude)
        {
            Window window = Window.Get(type, length);
            double[] spectrum = TransformTone(window, length, 137 + offset, amplitude);

            double peak = 0.0;
            for (int k = 0; k < length; k++)
            {
                double magnitude = Magnitude(spectrum, k);
                if (magnitude > peak)
                {
                    peak = magnitude;
                }
            }

            double measured = peak / (length * window.CoherentGain);
            return 20.0 * Math.Log10(measured / amplitude);
        }

        private static double[] WhiteNoise(Random random, int length, double totalPower)
        {
            // Box-Muller. System.Random is used only for its statistical properties here, never
            // for reproducibility across runtimes — that is REQ-SIM-003's concern and it has its
            // own generator.
            double perComponent = Math.Sqrt(totalPower / 2.0);
            var data = new double[length * 2];

            for (int i = 0; i < data.Length; i += 2)
            {
                double u1 = 1.0 - random.NextDouble();
                double u2 = random.NextDouble();
                double magnitude = Math.Sqrt(-2.0 * Math.Log(u1));

                data[i] = perComponent * magnitude * Math.Cos(2.0 * Math.PI * u2);
                data[i + 1] = perComponent * magnitude * Math.Sin(2.0 * Math.PI * u2);
            }

            return data;
        }

        private static double Magnitude(double[] interleaved, int index) =>
            Math.Sqrt(interleaved[index * 2] * interleaved[index * 2] +
                      interleaved[index * 2 + 1] * interleaved[index * 2 + 1]);
    }
}
