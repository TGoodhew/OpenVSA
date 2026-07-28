using System;
using OpenVSA.Dsp;
using Xunit;
using Xunit.Abstractions;

namespace OpenVSA.Dsp.Tests
{
    /// <summary>
    /// <c>REQ-NFR-003</c>: the vector kernels agree with the scalar ones they replace.
    /// </summary>
    /// <remarks>
    /// Throughput is measured by the benchmark host; this is the half that has to be true first.
    /// A vector kernel that is faster and wrong is worse than no vector kernel, and the tail
    /// handling — the samples left over when the count is not a whole number of vectors — is where
    /// that goes wrong, so the sizes below deliberately straddle the lane count.
    /// </remarks>
    public class KernelTests
    {
        private readonly ITestOutputHelper _output;

        /// <summary>Takes xunit's output sink.</summary>
        /// <param name="output">Where the lane count is written.</param>
        public KernelTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void TheRuntimeAcceleratesVectors()
        {
            _output.WriteLine(
                "Vector<float>.Count = " + Kernels.Lanes +
                ", hardware accelerated = " + Kernels.IsAccelerated);

            Assert.True(
                Kernels.IsAccelerated,
                "Vector<float> is not hardware accelerated on this runtime, so the vector kernels " +
                "are scalar code wearing a vector's name and REQ-NFR-003 cannot be met at all.");

            Assert.True(Kernels.Lanes >= 4);
        }

        [Theory]
        [InlineData(1024)]
        [InlineData(1023)]
        [InlineData(7)]
        [InlineData(1)]
        public void TheVectorWindowMultiplyMatchesTheScalarOne(int samples)
        {
            float[] a = Interleaved(samples);
            float[] b = (float[])a.Clone();
            float[] window = Window(samples);

            Kernels.WindowMultiplyScalar(a, window);
            Kernels.WindowMultiplyVector(b, window);

            for (int n = 0; n < a.Length; n++)
            {
                Assert.Equal(a[n], b[n]);
            }
        }

        [Theory]
        [InlineData(1024)]
        [InlineData(1023)]
        [InlineData(7)]
        [InlineData(1)]
        public void TheVectorMagnitudeMatchesTheScalarOne(int samples)
        {
            float[] interleaved = Interleaved(samples);

            var scalar = new float[samples];
            var vector = new float[samples];

            Kernels.MagnitudeSquaredScalar(interleaved, scalar);
            Kernels.MagnitudeSquaredVector(interleaved, vector);

            for (int n = 0; n < samples; n++)
            {
                Assert.Equal(scalar[n], vector[n]);
            }
        }

        [Fact]
        public void MismatchedLengthsAreRefused()
        {
            Assert.Throws<ArgumentException>(
                () => Kernels.MagnitudeSquaredScalar(new float[10], new float[4]));
            Assert.Throws<ArgumentNullException>(
                () => Kernels.WindowMultiplyVector(null, new float[4]));
        }

        private static float[] Interleaved(int samples)
        {
            var data = new float[samples * 2];

            for (int n = 0; n < samples; n++)
            {
                data[n * 2] = (float)Math.Cos(0.1 * n);
                data[n * 2 + 1] = (float)Math.Sin(0.17 * n);
            }

            return data;
        }

        private static float[] Window(int samples)
        {
            var window = new float[samples];

            for (int n = 0; n < samples; n++)
            {
                window[n] = (float)(0.5 - 0.5 * Math.Cos(2.0 * Math.PI * n / samples));
            }

            return window;
        }
    }
}
