using System;
using OpenVSA.Dsp.Fft;
using Xunit;

namespace OpenVSA.Dsp.Tests
{
    /// <summary>
    /// <c>REQ-NFR-004a</c>: the precision each provider is held to, and the rule governing how
    /// providers are compared with one another.
    /// </summary>
    public class FftPrecisionTests
    {
        private const int MillionPoint = 1 << 20;

        [Fact]
        public void DoubleProvider_SatisfiesParsevalTo1e12_OnAMillionPoints()
        {
            // The headline figure of REQ-NFR-004a, at the size it specifies. A million points is
            // where a naive twiddle recurrence would have drifted far enough to fail, so the size
            // is doing real work here rather than just being large.
            double error = ParsevalError(new ManagedFftProvider(), MillionPoint);

            Assert.True(error < 1e-12,
                "Parseval error " + error.ToString("E3") + " at 2^20 points exceeds 1e-12.");
        }

        [Fact]
        public void SingleProvider_SatisfiesParsevalTo1e5_OnAMillionPoints()
        {
            double error = ParsevalError(new ManagedSingleFftProvider(), MillionPoint);

            Assert.True(error < 1e-5,
                "Parseval error " + error.ToString("E3") + " at 2^20 points exceeds 1e-5.");
        }

        [Fact]
        public void CrossProviderAgreement_IsAssertedAtTheLessPreciseTolerance()
        {
            // REQ-NFR-004a is explicit that this comparison must not be made at 1e-6: accumulated
            // single-precision error at 2^20 points is already about 5e-7, so a 1e-6 bound would
            // be measuring luck. The tolerance is derived from the providers' declared precision.
            IFftProvider precise = new ManagedFftProvider();
            IFftProvider coarse = new ManagedSingleFftProvider();

            const int length = 1 << 16;
            double[] a = Signal(length);
            var b = (double[])a.Clone();

            precise.Forward(a);
            coarse.Forward(b);

            double tolerance = ToleranceFor(Math.Min(precise.SignificandBits, coarse.SignificandBits));
            Assert.True(tolerance > 1e-6,
                "The cross-provider tolerance must not be tighter than single precision can support.");

            double reference = 0.0;
            for (int k = 0; k < length; k++)
            {
                double magnitude = Math.Sqrt(a[k * 2] * a[k * 2] + a[k * 2 + 1] * a[k * 2 + 1]);
                if (magnitude > reference)
                {
                    reference = magnitude;
                }
            }

            double worst = 0.0;
            for (int i = 0; i < a.Length; i++)
            {
                double difference = Math.Abs(a[i] - b[i]) / reference;
                if (difference > worst)
                {
                    worst = difference;
                }
            }

            Assert.True(worst < tolerance,
                "Providers disagree by " + worst.ToString("E3") + " relative to the peak, " +
                "tolerance " + tolerance.ToString("E1") + ".");
        }

        [Fact]
        public void SingleProviderIsMeasurablyLessAccurate_SoTheComparisonIsNotVacuous()
        {
            // If both providers were double underneath, every cross-provider test above would pass
            // for the wrong reason. This asserts the single provider really does lose precision.
            const int length = 1 << 16;

            double preciseError = ParsevalError(new ManagedFftProvider(), length);
            double coarseError = ParsevalError(new ManagedSingleFftProvider(), length);

            Assert.True(coarseError > preciseError * 1000.0,
                "The single-precision provider (" + coarseError.ToString("E3") + ") is not " +
                "measurably less accurate than the double one (" + preciseError.ToString("E3") +
                "), so it is not exercising a single-precision path.");
        }

        [Fact]
        public void ProvidersDeclareTheirPrecision()
        {
            Assert.Equal(53, new ManagedFftProvider().SignificandBits);
            Assert.Equal(24, new ManagedSingleFftProvider().SignificandBits);
        }

        private static double ToleranceFor(int significandBits) =>
            significandBits >= 53 ? 1e-12 : 1e-5;

        private static double ParsevalError(IFftProvider provider, int length)
        {
            double[] data = Signal(length);
            double timePower = SumOfSquares(data);

            provider.Forward(data);

            double frequencyPower = SumOfSquares(data) / length;

            return Math.Abs(frequencyPower - timePower) / timePower;
        }

        /// <summary>Compensated (Kahan) sum of the squared magnitudes.</summary>
        /// <remarks>
        /// Naive summation of 2²⁰ positive terms accumulates a relative error of order 1e-13,
        /// which is within a factor of ten of the 1e-12 bound being asserted. The measurement
        /// would then be reporting the test harness's arithmetic as much as the transform's, so
        /// the harness compensates and the bound is about the FFT.
        /// </remarks>
        private static double SumOfSquares(double[] interleaved)
        {
            double sum = 0.0;
            double compensation = 0.0;

            for (int i = 0; i < interleaved.Length; i += 2)
            {
                double term = interleaved[i] * interleaved[i] +
                              interleaved[i + 1] * interleaved[i + 1];

                double adjusted = term - compensation;
                double next = sum + adjusted;
                compensation = (next - sum) - adjusted;
                sum = next;
            }

            return sum;
        }

        private static double[] Signal(int length)
        {
            // Broadband and deterministic. A single tone would concentrate all the energy in one
            // bin and let a provider with poor cancellation elsewhere pass.
            var data = new double[length * 2];
            for (int n = 0; n < length; n++)
            {
                data[n * 2] = Math.Sin(0.001 * n) + 0.5 * Math.Sin(0.31 * n + 0.2);
                data[n * 2 + 1] = Math.Cos(0.007 * n) - 0.25 * Math.Cos(1.11 * n + 0.7);
            }

            return data;
        }
    }
}
