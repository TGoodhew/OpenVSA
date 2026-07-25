using System;
using OpenVSA.Core;
using Xunit;

namespace OpenVSA.Core.Tests
{
    /// <summary>
    /// <c>REQ-ACQ-001</c>: the span/sample-rate/FFT-size relationships, on both paths.
    /// </summary>
    public class AcquisitionLawTests
    {
        [Fact]
        public void TheWorkedExample_HoldsOnTheComplexPath()
        {
            // The requirement's own worked example, which matches Keysight's exactly.
            const double span = 10e6;
            const int points = 801;

            Assert.Equal(12.8e6, AcquisitionLaw.SampleRateFor(span, AnalysisPath.ComplexZoom), 3);
            Assert.Equal(1024, AcquisitionLaw.TransformLengthFor(points, AnalysisPath.ComplexZoom));
            Assert.Equal(80e-6, AcquisitionLaw.MaxTimeSeconds(points, span), 12);
        }

        [Fact]
        public void TheWorkedExample_HoldsOnTheRealPath_WithTheSameMaximumTime()
        {
            // The discriminating half. Fs and N_FFT both double, so T_max does not move - and using
            // the complex FFT factor on this path, which the requirement calls out as a defect
            // rather than a simplification, breaks exactly that identity.
            const double span = 10e6;
            const int points = 801;

            Assert.Equal(25.6e6, AcquisitionLaw.SampleRateFor(span, AnalysisPath.RealBaseband), 3);
            Assert.Equal(2048, AcquisitionLaw.TransformLengthFor(points, AnalysisPath.RealBaseband));
            Assert.Equal(80e-6, AcquisitionLaw.MaxTimeSeconds(points, span), 12);
        }

        [Fact]
        public void MaximumTimeIsTheSameOnBothPaths_AtEveryAvailablePointCount()
        {
            foreach (int points in FrequencyPoints.Supported)
            {
                double complex = AcquisitionLaw.MaxTimeSeconds(points, 10e6);
                double real = AcquisitionLaw.MaxTimeSeconds(points, 10e6);

                Assert.Equal(complex, real, 15);

                // And the identity that makes it so: T_max = N_FFT / Fs on either path.
                foreach (AnalysisPath path in new[] { AnalysisPath.ComplexZoom, AnalysisPath.RealBaseband })
                {
                    double fromFft =
                        AcquisitionLaw.TransformLengthFor(points, path) /
                        AcquisitionLaw.SampleRateFor(10e6, path);

                    Assert.Equal(complex, fromFft, 15);
                }
            }
        }

        [Fact]
        public void EveryAvailablePointCountGivesAPowerOfTwoTransform()
        {
            // REQ-DSP-022's reason for existing: the 50 x 2^k constraint is there so that this
            // holds. If it ever fails, the ladder and the law have drifted apart.
            foreach (int points in FrequencyPoints.Supported)
            {
                foreach (AnalysisPath path in new[] { AnalysisPath.ComplexZoom, AnalysisPath.RealBaseband })
                {
                    int length = AcquisitionLaw.TransformLengthFor(points, path);

                    Assert.True(
                        length > 0 && (length & (length - 1)) == 0,
                        points + " points on " + path + " gives N_FFT = " + length +
                        ", which is not a power of two.");
                }
            }
        }

        [Fact]
        public void TransformLengthAndPointCountAreInverses()
        {
            foreach (int points in FrequencyPoints.Supported)
            {
                foreach (AnalysisPath path in new[] { AnalysisPath.ComplexZoom, AnalysisPath.RealBaseband })
                {
                    int length = AcquisitionLaw.TransformLengthFor(points, path);
                    Assert.Equal(points, AcquisitionLaw.PointsForTransformLength(length, path));
                }
            }
        }

        [Theory]
        [InlineData(1, AnalysisPath.ComplexZoom)]
        [InlineData(2, AnalysisPath.ComplexZoom)]
        [InlineData(32, AnalysisPath.ComplexZoom)]
        [InlineData(64, AnalysisPath.RealBaseband)]
        public void ATransformLengthThatIsNotAWholePointCountIsRefused(int length, AnalysisPath path)
        {
            // 32 complex points would be 25 steps, i.e. 26 displayed points - below the minimum -
            // and 64 on the real path likewise. Returning 0 rather than a plausible-looking number
            // is what lets a caller show the whole band instead of an impossible point count.
            int points = AcquisitionLaw.PointsForTransformLength(length, path);
            Assert.False(FrequencyPoints.IsValid(points));
        }

        [Fact]
        public void SpanAndSampleRateAreInverses()
        {
            foreach (AnalysisPath path in new[] { AnalysisPath.ComplexZoom, AnalysisPath.RealBaseband })
            {
                double rate = AcquisitionLaw.SampleRateFor(10e6, path);
                Assert.Equal(10e6, AcquisitionLaw.SpanFor(rate, path), 6);
            }
        }

        [Fact]
        public void TheFactorsAreExact_NotFloatingPointApproximations()
        {
            // Computed as 32/25 and 64/25 in integer arithmetic. Written as 1.28, the round trip
            // 1024 / 1.28 does not return exactly 800, and a point count derived from an FFT size
            // would be one out at some sizes and not others.
            Assert.Equal(524288, AcquisitionLaw.TransformLengthFor(409601, AnalysisPath.ComplexZoom));
            Assert.Equal(409601, AcquisitionLaw.PointsForTransformLength(524288, AnalysisPath.ComplexZoom));
        }

        [Fact]
        public void APointCountBelowTwoIsRefused()
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => AcquisitionLaw.TransformLengthFor(1, AnalysisPath.ComplexZoom));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => AcquisitionLaw.MaxTimeSeconds(1, 10e6));
        }

        [Fact]
        public void ASpanThatIsNotPositiveIsRefused()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => AcquisitionLaw.MaxTimeSeconds(801, 0.0));
            Assert.Throws<ArgumentOutOfRangeException>(() => AcquisitionLaw.MaxTimeSeconds(801, double.NaN));
        }
    }
}
