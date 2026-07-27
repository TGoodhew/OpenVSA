using System;
using System.Collections.Generic;
using OpenVSA.Dsp.Spectrum;
using Xunit;

namespace OpenVSA.Dsp.Tests
{
    /// <summary>
    /// <c>REQ-UI-072</c>'s Detectors: how points sharing a pixel column are reduced.
    /// </summary>
    public class TraceDetectorTests
    {
        private static readonly float[] Column = { -10.0f, -30.0f, -20.0f, -40.0f };

        [Fact]
        public void NormalKeepsBothExtrema()
        {
            // The default, and the one REQ-NFR-006's guarantee depends on: a one-bin spur cannot be
            // decimated away if both extrema of every column survive.
            float minimum;
            float maximum;

            Detect(TraceDetector.Normal, out minimum, out maximum);

            Assert.Equal(-40.0f, minimum, 4);
            Assert.Equal(-10.0f, maximum, 4);
        }

        [Fact]
        public void ThePeakDetectorsKeepOneEndEach()
        {
            float minimum;
            float maximum;

            Detect(TraceDetector.Peak, out minimum, out maximum);

            Assert.Equal(-10.0f, minimum, 4);
            Assert.Equal(-10.0f, maximum, 4);

            Detect(TraceDetector.NegativePeak, out minimum, out maximum);

            Assert.Equal(-40.0f, minimum, 4);
            Assert.Equal(-40.0f, maximum, 4);
        }

        [Fact]
        public void SampleTakesTheFirstPointOfTheColumn()
        {
            float minimum;
            float maximum;

            Detect(TraceDetector.Sample, out minimum, out maximum);

            Assert.Equal(Column[0], minimum, 4);
            Assert.Equal(Column[0], maximum, 4);
        }

        [Fact]
        public void TheAverageIsTakenInPowerAndNotInDecibels()
        {
            // The whole point of this test. Two points at 0 and −20 dBm are 1 mW and 0.01 mW; their
            // mean power is 0.505 mW, which is −2.97 dBm. The mean of the decibels is −10 dBm — a
            // 7 dB error, and one that reads low exactly where this detector is most used.
            var values = new[] { 0.0f, -20.0f };

            float minimum;
            float maximum;

            TraceDetection.Detect(
                values, 0, values.Length, TraceDetector.Average,
                valuesAreDecibels: true, minimum: out minimum, maximum: out maximum);

            Assert.Equal(10.0 * Math.Log10(0.505), minimum, 4);
            Assert.Equal(minimum, maximum, 6);

            Assert.True(
                Math.Abs(minimum - (-10.0)) > 6.0,
                "The average came out at the mean of the decibels, which is the classic error.");
        }

        [Fact]
        public void ALinearAverageIsTakenLinearly()
        {
            // The same detector over values that are not decibels — volts, or a real part. Applying
            // the power conversion to those would be the same error in the other direction.
            var values = new[] { 1.0f, 3.0f };

            float minimum;
            float maximum;

            TraceDetection.Detect(
                values, 0, values.Length, TraceDetector.Average,
                valuesAreDecibels: false, minimum: out minimum, maximum: out maximum);

            Assert.Equal(2.0f, minimum, 5);
        }

        [Fact]
        public void EveryDetectorExcludesBlankedPoints()
        {
            var values = new[] { float.NaN, -30.0f, float.NaN, -10.0f };

            foreach (TraceDetector detector in TraceDetection.All)
            {
                float minimum;
                float maximum;

                TraceDetection.Detect(
                    values, 0, values.Length, detector,
                    valuesAreDecibels: true, minimum: out minimum, maximum: out maximum);

                if (detector == TraceDetector.Sample)
                {
                    // Sample takes the first point, and the first point is blanked. That is the
                    // detector working, not failing: it draws what the instrument sampled.
                    Assert.True(float.IsNaN(minimum));
                    continue;
                }

                Assert.False(
                    float.IsNaN(minimum),
                    TraceDetection.NameOf(detector) + " was defeated by a blanked point.");
            }
        }

        [Fact]
        public void AColumnOfNothingButBlanksStaysBlank()
        {
            var values = new[] { float.NaN, float.NaN };

            foreach (TraceDetector detector in TraceDetection.All)
            {
                float minimum;
                float maximum;

                TraceDetection.Detect(
                    values, 0, values.Length, detector,
                    valuesAreDecibels: true, minimum: out minimum, maximum: out maximum);

                Assert.True(
                    float.IsNaN(minimum) && float.IsNaN(maximum),
                    TraceDetection.NameOf(detector) + " invented a value for an empty column.");
            }
        }

        [Fact]
        public void OnlyNormalDrawsASpan()
        {
            foreach (TraceDetector detector in TraceDetection.All)
            {
                Assert.Equal(
                    detector != TraceDetector.Normal, TraceDetection.IsSingleValued(detector));
            }
        }

        [Fact]
        public void EveryDetectorIsNamedAndDescribed()
        {
            var names = new List<string>();

            foreach (TraceDetector detector in TraceDetection.All)
            {
                string name = TraceDetection.NameOf(detector);

                Assert.False(string.IsNullOrEmpty(name));
                Assert.False(string.IsNullOrEmpty(TraceDetection.Describe(detector)));
                Assert.DoesNotContain(name, names);

                names.Add(name);
            }

            Assert.Equal(Enum.GetValues(typeof(TraceDetector)).Length, names.Count);
        }

        [Fact]
        public void ADetectorThatIsNotOneIsRefused()
        {
            float minimum;
            float maximum;

            Assert.Throws<ArgumentOutOfRangeException>(
                () => TraceDetection.Detect(
                    Column, 0, Column.Length, (TraceDetector)99,
                    valuesAreDecibels: true, minimum: out minimum, maximum: out maximum));

            Assert.Throws<ArgumentOutOfRangeException>(
                () => TraceDetection.NameOf((TraceDetector)99));

            Assert.Throws<ArgumentOutOfRangeException>(
                () => TraceDetection.Describe((TraceDetector)99));
        }

        private static void Detect(TraceDetector detector, out float minimum, out float maximum) =>
            TraceDetection.Detect(
                Column, 0, Column.Length, detector,
                valuesAreDecibels: true, minimum: out minimum, maximum: out maximum);
    }
}
