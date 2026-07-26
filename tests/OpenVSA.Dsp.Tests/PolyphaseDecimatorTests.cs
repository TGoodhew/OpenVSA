using System;
using OpenVSA.Dsp.Zoom;
using Xunit;

namespace OpenVSA.Dsp.Tests
{
    /// <summary>
    /// <c>REQ-DSP-023a</c>: the decimating convolution, checked against the definition it is a
    /// rearrangement of.
    /// </summary>
    /// <remarks>
    /// The lengths and rates here are deliberately awkward — 1021 samples, 37 taps, decimation by
    /// 7 — because a decimator whose alignment arithmetic is out by one is exactly right whenever
    /// the tap count happens to be a multiple of the decimation factor.
    /// </remarks>
    public class PolyphaseDecimatorTests
    {
        [Fact]
        public void GroupDelayIsHalfTheTapsAndAlignmentIsTheFirstMultipleOfMBeyondIt()
        {
            double[] taps = Ramp(37);
            var decimator = PolyphaseDecimator.WithRealTaps(taps, 7);

            Assert.Equal(18, decimator.GroupDelaySamples);

            // 18 rounded up to a multiple of 7 is 21, not 18 and not 14.
            Assert.Equal(21, decimator.AlignmentOffsetSamples);
            Assert.Equal(40, decimator.MinimumInputSamples);
        }

        [Fact]
        public void OutputCountsFollowTheSupportedWindow()
        {
            var decimator = PolyphaseDecimator.WithRealTaps(Ramp(37), 7);

            Assert.Equal(0, decimator.OutputCountFor(0));
            Assert.Equal(0, decimator.OutputCountFor(39));
            Assert.Equal(1, decimator.OutputCountFor(40));
            Assert.Equal(1, decimator.OutputCountFor(46));
            Assert.Equal(2, decimator.OutputCountFor(47));
            Assert.Equal(141, decimator.OutputCountFor(1021));
        }

        [Fact]
        public void RealTapsAgreeWithFilterThenThrowAway()
        {
            // The definition the polyphase form is a rearrangement of: convolve everything, then
            // keep the samples on the output grid. Same answer, M times the work.
            double[] taps = FirDesign.LowPass(0.5 / 7.0, 0.2 / 7.0, 90.0);
            var decimator = PolyphaseDecimator.WithRealTaps(taps, 7);

            float[] input = Chirp(1021, 0.0131, 0.29);
            var output = new float[decimator.OutputCountFor(1021) * 2];

            int count = decimator.Decimate(input, output);

            for (int j = 0; j < count; j++)
            {
                int centre = decimator.AlignmentOffsetSamples + j * 7;
                double expectedI, expectedQ;

                Reference(input, taps, null, centre + decimator.GroupDelaySamples,
                    out expectedI, out expectedQ);

                AssertClose(expectedI, output[j * 2]);
                AssertClose(expectedQ, output[j * 2 + 1]);
            }
        }

        [Fact]
        public void ComplexTapsAgreeWithFilterThenThrowAway()
        {
            double[] real = FirDesign.LowPass(0.5 / 5.0, 0.2 / 5.0, 90.0);
            var tapsI = new double[real.Length];
            var tapsQ = new double[real.Length];

            for (int k = 0; k < real.Length; k++)
            {
                double angle = 2.0 * Math.PI * 0.0873 * k;

                tapsI[k] = real[k] * Math.Cos(angle);
                tapsQ[k] = real[k] * Math.Sin(angle);
            }

            var decimator = PolyphaseDecimator.WithComplexTaps(tapsI, tapsQ, 5);

            Assert.True(decimator.HasComplexTaps);

            float[] input = Chirp(997, 0.021, 0.31);
            var output = new float[decimator.OutputCountFor(997) * 2];

            int count = decimator.Decimate(input, output);

            for (int j = 0; j < count; j++)
            {
                int centre = decimator.AlignmentOffsetSamples + j * 5;
                double expectedI, expectedQ;

                Reference(input, tapsI, tapsQ, centre + decimator.GroupDelaySamples,
                    out expectedI, out expectedQ);

                AssertClose(expectedI, output[j * 2]);
                AssertClose(expectedQ, output[j * 2 + 1]);
            }
        }

        [Fact]
        public void TheTapDirectionIsNotSymmetricForComplexTaps()
        {
            // For the symmetric real low-pass, reversing the kernel changes nothing and a
            // wrong-way-round loop passes every test. For the downconverter's shifted taps it
            // conjugates the frequency shift, so this asserts the direction directly: a filter
            // whose taps are the reverse of another's must give a different answer.
            var forwardI = new double[] { 1.0, 0.5, 0.25 };
            var forwardQ = new double[] { 0.0, 0.5, 0.75 };
            var reversedQ = new double[] { 0.75, 0.5, 0.0 };

            float[] input = Chirp(64, 0.037, 0.21);

            var a = new float[PolyphaseDecimator.WithComplexTaps(forwardI, forwardQ, 2)
                .OutputCountFor(64) * 2];
            var b = new float[a.Length];

            PolyphaseDecimator.WithComplexTaps(forwardI, forwardQ, 2).Decimate(input, a);
            PolyphaseDecimator.WithComplexTaps(forwardI, reversedQ, 2).Decimate(input, b);

            Assert.NotEqual(a[0], b[0]);
        }

        [Fact]
        public void AConstantInputComesThroughAtTheFiltersDcGain()
        {
            double[] taps = FirDesign.LowPass(0.5 / 4.0, 0.2 / 4.0, 100.0);
            var decimator = PolyphaseDecimator.WithRealTaps(taps, 4);

            var input = new float[2048 * 2];

            for (int n = 0; n < 2048; n++)
            {
                input[n * 2] = 0.75f;
                input[n * 2 + 1] = -0.25f;
            }

            var output = new float[decimator.OutputCountFor(2048) * 2];
            int count = decimator.Decimate(input, output);

            Assert.True(count > 0);

            for (int j = 0; j < count; j++)
            {
                Assert.Equal(0.75, output[j * 2], 6);
                Assert.Equal(-0.25, output[j * 2 + 1], 6);
            }
        }

        [Fact]
        public void ARecordTooShortToSupportAnOutputProducesNone()
        {
            var decimator = PolyphaseDecimator.WithRealTaps(Ramp(37), 7);
            var output = new float[16];

            Assert.Equal(0, decimator.Decimate(new float[39 * 2], output));
        }

        [Fact]
        public void ADecimationOfOnePassesEverythingThroughTheFilter()
        {
            // Legal here even though the downconverter refuses it: a filter with no decimation is
            // still a filter, and forbidding it in the engine would make the engine the place a
            // product rule lives.
            var decimator = PolyphaseDecimator.WithRealTaps(Ramp(11), 1);

            Assert.Equal(5, decimator.AlignmentOffsetSamples);
            Assert.Equal(90, decimator.OutputCountFor(100));
        }

        [Fact]
        public void ATooSmallOutputSpanIsRefusedRatherThanTruncated()
        {
            var decimator = PolyphaseDecimator.WithRealTaps(Ramp(37), 7);
            var input = new float[1021 * 2];

            ArgumentException error = Assert.Throws<ArgumentException>(
                () => decimator.Decimate(input, new float[10]));

            Assert.Contains("141", error.Message);
        }

        [Fact]
        public void AnOddNumberOfFloatsIsNotInterleavedIq()
        {
            var decimator = PolyphaseDecimator.WithRealTaps(Ramp(37), 7);

            Assert.Throws<ArgumentException>(
                () => decimator.Decimate(new float[2047], new float[4096]));
        }

        [Fact]
        public void EvenTapCountsAreRefused()
        {
            // An even-length symmetric filter has a half-sample group delay, and a half-sample
            // offset is not something this class can express or the caller can correct.
            Assert.Throws<ArgumentException>(
                () => PolyphaseDecimator.WithRealTaps(Ramp(36), 4));
            Assert.Throws<ArgumentException>(
                () => PolyphaseDecimator.WithComplexTaps(Ramp(36), Ramp(36), 4));
        }

        [Fact]
        public void MismatchedComplexTapLengthsAreRefused()
        {
            Assert.Throws<ArgumentException>(
                () => PolyphaseDecimator.WithComplexTaps(Ramp(37), Ramp(35), 4));
        }

        [Fact]
        public void MissingTapsAreRefused()
        {
            Assert.Throws<ArgumentNullException>(() => PolyphaseDecimator.WithRealTaps(null, 4));
            Assert.Throws<ArgumentNullException>(
                () => PolyphaseDecimator.WithComplexTaps(Ramp(37), null, 4));
            Assert.Throws<ArgumentException>(
                () => PolyphaseDecimator.WithRealTaps(new double[0], 4));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-3)]
        public void ADecimationBelowOneIsRefused(int decimation)
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => PolyphaseDecimator.WithRealTaps(Ramp(37), decimation));
        }

        [Fact]
        public void ANegativeSampleCountIsRefused()
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => PolyphaseDecimator.WithRealTaps(Ramp(37), 7).OutputCountFor(-1));
        }

        /// <summary>
        /// Compares against the reference at the precision the output is stored in.
        /// </summary>
        /// <remarks>
        /// The accumulator is <see cref="double"/> and the output is <see cref="float"/>, so the
        /// two forms can only agree to within the last rounding — a fixed number of decimal places
        /// would be a test of where a value happens to sit relative to a rounding boundary.
        /// </remarks>
        private static void AssertClose(double expected, float actual)
        {
            Assert.True(
                Math.Abs(expected - actual) <= 1e-6 * (1.0 + Math.Abs(expected)),
                "expected " + expected + ", got " + actual + ".");
        }

        /// <summary>
        /// The convolution written out at one output instant: <c>Σ_k g[k]·x[n−k]</c>.
        /// </summary>
        private static void Reference(
            float[] input, double[] tapsI, double[] tapsQ, int instant,
            out double resultI, out double resultQ)
        {
            resultI = 0.0;
            resultQ = 0.0;

            for (int k = 0; k < tapsI.Length; k++)
            {
                int i = instant - k;

                if (i < 0 || i * 2 + 1 >= input.Length)
                {
                    continue;
                }

                double sampleI = input[i * 2];
                double sampleQ = input[i * 2 + 1];

                if (tapsQ == null)
                {
                    resultI += tapsI[k] * sampleI;
                    resultQ += tapsI[k] * sampleQ;
                }
                else
                {
                    resultI += tapsI[k] * sampleI - tapsQ[k] * sampleQ;
                    resultQ += tapsI[k] * sampleQ + tapsQ[k] * sampleI;
                }
            }
        }

        private static double[] Ramp(int length)
        {
            var taps = new double[length];

            for (int n = 0; n < length; n++)
            {
                taps[n] = (n + 1) / (double)length;
            }

            return taps;
        }

        /// <summary>A complex sweep: broadband, deterministic, and not symmetric in time.</summary>
        private static float[] Chirp(int samples, double startCycles, double endCycles)
        {
            var data = new float[samples * 2];
            double phase = 0.0;

            for (int n = 0; n < samples; n++)
            {
                data[n * 2] = (float)Math.Cos(phase);
                data[n * 2 + 1] = (float)Math.Sin(phase);

                double f = startCycles + (endCycles - startCycles) * n / samples;
                phase += 2.0 * Math.PI * f;
            }

            return data;
        }
    }
}
