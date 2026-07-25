using System;
using OpenVSA.Dsp.Fft;
using Xunit;

namespace OpenVSA.Dsp.Tests
{
    /// <summary>
    /// The provider-parametrised suite of <c>REQ-NFR-004</c>: the same forward/inverse round-trip,
    /// Parseval and known-transform-pair checks run against every registered
    /// <see cref="IFftProvider"/>.
    /// </summary>
    /// <remarks>
    /// Derive one fixture per provider. Every expectation is a closed-form transform pair, per
    /// <c>REQ-TST-001</c> — nothing is compared against a stored output of a previous run.
    /// </remarks>
    public abstract class FftProviderConformanceTests
    {
        /// <summary>The provider under test.</summary>
        protected abstract IFftProvider Provider { get; }

        /// <summary>
        /// Relative tolerance for this provider, derived from its declared precision.
        /// </summary>
        /// <remarks>
        /// <c>REQ-NFR-004a</c> forbids asserting a single-precision path at a double-precision
        /// tolerance, so the bound is computed from <see cref="IFftProvider.SignificandBits"/>
        /// rather than written in per fixture. The margin over the raw epsilon covers accumulation
        /// across the transform's log N stages.
        /// </remarks>
        protected double Tolerance => Provider.SignificandBits >= 53 ? 1e-12 : 1e-5;

        private const int Length = 1024;

        // ---- Known transform pairs -------------------------------------------------------------

        [Fact]
        public void UnitImpulseAtOrigin_TransformsToAConstant()
        {
            // delta[n] -> X[k] = 1 for all k. The most basic pair there is, and it catches a
            // wrong bit-reversal outright.
            var data = new double[Length * 2];
            data[0] = 1.0;

            Provider.Forward(data);

            for (int k = 0; k < Length; k++)
            {
                Assert.True(Math.Abs(data[k * 2] - 1.0) < Tolerance * 10.0,
                    "Re X[" + k + "] = " + data[k * 2] + ", expected 1.");
                Assert.True(Math.Abs(data[k * 2 + 1]) < Tolerance * 10.0,
                    "Im X[" + k + "] = " + data[k * 2 + 1] + ", expected 0.");
            }
        }

        [Fact]
        public void Constant_TransformsToAnImpulseOfAmplitudeN()
        {
            // x[n] = 1 -> X[0] = N, X[k != 0] = 0.
            //
            // The amplitude here is the check that matters: it pins the normalisation convention.
            // A provider that split the scaling as 1/sqrt(N) across both directions would still
            // round-trip perfectly and would put every absolute amplitude readout out by sqrt(N).
            var data = new double[Length * 2];
            for (int n = 0; n < Length; n++)
            {
                data[n * 2] = 1.0;
            }

            Provider.Forward(data);

            Assert.True(Math.Abs(data[0] - Length) < Tolerance * Length,
                "X[0] = " + data[0] + ", expected N = " + Length + " for an unnormalised forward transform.");
            Assert.True(Math.Abs(data[1]) < Tolerance * Length);

            for (int k = 1; k < Length; k++)
            {
                double magnitude = Math.Sqrt(
                    data[k * 2] * data[k * 2] + data[k * 2 + 1] * data[k * 2 + 1]);
                Assert.True(magnitude < Tolerance * Length,
                    "|X[" + k + "]| = " + magnitude + ", expected 0.");
            }
        }

        [Theory]
        [InlineData(1)]
        [InlineData(7)]
        [InlineData(511)]
        [InlineData(1023)]
        public void ComplexExponential_TransformsToASingleBin(int bin)
        {
            // x[n] = e^(+2*pi*i*k0*n/N) -> X[k0] = N, everything else 0, under the sign convention
            // X[k] = sum x[n] e^(-2*pi*i*k*n/N). A sign error puts the energy in bin N-k0, which
            // is why this uses asymmetric bins rather than N/2.
            var data = new double[Length * 2];
            for (int n = 0; n < Length; n++)
            {
                double angle = 2.0 * Math.PI * bin * n / Length;
                data[n * 2] = Math.Cos(angle);
                data[n * 2 + 1] = Math.Sin(angle);
            }

            Provider.Forward(data);

            double peak = Math.Sqrt(
                data[bin * 2] * data[bin * 2] + data[bin * 2 + 1] * data[bin * 2 + 1]);

            Assert.True(Math.Abs(peak - Length) < Tolerance * Length,
                "|X[" + bin + "]| = " + peak + ", expected N = " + Length + ".");

            for (int k = 0; k < Length; k++)
            {
                if (k == bin)
                {
                    continue;
                }

                double magnitude = Math.Sqrt(
                    data[k * 2] * data[k * 2] + data[k * 2 + 1] * data[k * 2 + 1]);
                Assert.True(magnitude < Tolerance * Length,
                    "Energy leaked into bin " + k + " (|X| = " + magnitude + ") from a tone at bin " + bin + ".");
            }
        }

        [Fact]
        public void Linearity_Holds()
        {
            double[] a = Ramp(Length, 1.0);
            double[] b = Ramp(Length, -0.3);

            var combined = new double[Length * 2];
            for (int i = 0; i < combined.Length; i++)
            {
                combined[i] = 2.0 * a[i] + 5.0 * b[i];
            }

            Provider.Forward(a);
            Provider.Forward(b);
            Provider.Forward(combined);

            for (int i = 0; i < combined.Length; i++)
            {
                double expected = 2.0 * a[i] + 5.0 * b[i];
                Assert.True(Math.Abs(combined[i] - expected) < Tolerance * Length,
                    "Linearity failed at index " + i + ".");
            }
        }

        // ---- Round trip and Parseval -----------------------------------------------------------

        [Theory]
        [InlineData(64)]
        [InlineData(256)]
        [InlineData(4096)]
        public void ForwardThenInverse_IsTheIdentity(int length)
        {
            double[] original = Ramp(length, 1.0);
            var data = (double[])original.Clone();

            Provider.Forward(data);
            Provider.Inverse(data);

            for (int i = 0; i < data.Length; i++)
            {
                Assert.True(Math.Abs(data[i] - original[i]) < Tolerance * Length,
                    "Round trip changed index " + i + ": " + original[i] + " became " + data[i] + ".");
            }
        }

        [Theory]
        [InlineData(64)]
        [InlineData(4096)]
        public void Parseval_Holds(int length)
        {
            // sum |x[n]|^2 == (1/N) sum |X[k]|^2, which follows from the unnormalised forward
            // transform this interface specifies.
            double[] data = Ramp(length, 1.0);

            double timePower = 0.0;
            for (int n = 0; n < length; n++)
            {
                timePower += data[n * 2] * data[n * 2] + data[n * 2 + 1] * data[n * 2 + 1];
            }

            Provider.Forward(data);

            double frequencyPower = 0.0;
            for (int k = 0; k < length; k++)
            {
                frequencyPower += data[k * 2] * data[k * 2] + data[k * 2 + 1] * data[k * 2 + 1];
            }

            frequencyPower /= length;

            double error = Math.Abs(frequencyPower - timePower) / timePower;
            Assert.True(error < Tolerance,
                "Parseval error " + error.ToString("E3") + " at N=" + length +
                " exceeds this provider's tolerance of " + Tolerance.ToString("E1") + ".");
        }

        // ---- Contract ---------------------------------------------------------------------------

        [Fact]
        public void SupportsPowersOfTwoOnly()
        {
            Assert.True(Provider.SupportsLength(64));
            Assert.True(Provider.SupportsLength(1 << 20));
            Assert.False(Provider.SupportsLength(0));
            Assert.False(Provider.SupportsLength(-8));
            Assert.False(Provider.SupportsLength(100));
        }

        [Fact]
        public void RejectsAnUnsupportedLength()
        {
            // Rejected rather than zero-padded to the next power of two. Padding would silently
            // change the resolution bandwidth, and the resulting spectrum would look plausible.
            Assert.Throws<ArgumentException>(() => Provider.Forward(new double[100 * 2]));
            Assert.Throws<ArgumentException>(() => Provider.Inverse(new double[100 * 2]));
            Assert.Throws<ArgumentException>(() => Provider.Forward(new double[7]));
        }

        [Fact]
        public void SingleSampleIsTheIdentity()
        {
            var data = new double[] { 3.0, -4.0 };
            Provider.Forward(data);

            Assert.Equal(3.0, data[0], 12);
            Assert.Equal(-4.0, data[1], 12);
        }

        [Fact]
        public void DeclaresItsNameAndPrecision()
        {
            Assert.False(string.IsNullOrEmpty(Provider.Name));
            Assert.True(Provider.SignificandBits > 0, "A provider must declare its precision.");
        }

        private static double[] Ramp(int length, double scale)
        {
            // Deterministic, non-symmetric, and with both components populated, so a provider that
            // mishandled the imaginary part could not pass by accident.
            var data = new double[length * 2];
            for (int n = 0; n < length; n++)
            {
                data[n * 2] = scale * Math.Sin(0.1 * n + 0.3);
                data[n * 2 + 1] = scale * Math.Cos(0.037 * n * n + 1.1);
            }

            return data;
        }
    }

    /// <summary>Runs the suite against the managed double-precision provider.</summary>
    public sealed class ManagedFftProviderConformanceTests : FftProviderConformanceTests
    {
        /// <inheritdoc />
        protected override IFftProvider Provider { get; } = new ManagedFftProvider();
    }

    /// <summary>Runs the suite against the managed single-precision provider.</summary>
    public sealed class ManagedSingleFftProviderConformanceTests : FftProviderConformanceTests
    {
        /// <inheritdoc />
        protected override IFftProvider Provider { get; } = new ManagedSingleFftProvider();
    }
}
