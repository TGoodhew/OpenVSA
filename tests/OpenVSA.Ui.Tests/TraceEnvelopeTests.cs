using System;
using OpenVSA.Ui.Rendering;
using Xunit;

namespace OpenVSA.Ui.Tests
{
    /// <summary>
    /// The envelope builder, in both directions: decimating a long trace and interpolating a short
    /// one.
    /// </summary>
    /// <remarks>
    /// The second direction is the one with a defect waiting in it. Point counts under
    /// <c>REQ-DSP-022</c> start at 51, and a graticule is several hundred pixels wide, so a trace
    /// with fewer points than columns is an ordinary setting rather than an edge case — and
    /// decimating it leaves most columns empty, which draws as a dotted line.
    /// </remarks>
    public class TraceEnvelopeTests
    {
        [Fact]
        public void MorePointsThanColumns_AreDecimatedByEnvelope()
        {
            var values = new float[1000];
            for (int i = 0; i < values.Length; i++)
            {
                values[i] = -100.0f;
            }

            values[555] = -3.0f;

            var minMax = new float[100 * 2];
            TraceEnvelope.Build(values, 100, minMax);

            Assert.False(TraceEnvelope.IsInterpolated(1000, 100));
            Assert.Equal(-3.0f, minMax[55 * 2 + 1]);
            Assert.Equal(-100.0f, minMax[55 * 2]);
        }

        [Fact]
        public void FewerPointsThanColumns_LeaveNoColumnBlank()
        {
            // The defect this exists to prevent: min/max decimation of 51 points across 800 columns
            // gives 749 columns with no contributing sample, every one of them blanked.
            var values = new float[51];
            for (int i = 0; i < values.Length; i++)
            {
                values[i] = -50.0f + i;
            }

            var minMax = new float[800 * 2];
            TraceEnvelope.Build(values, 800, minMax);

            Assert.True(TraceEnvelope.IsInterpolated(51, 800));

            for (int column = 0; column < 800; column++)
            {
                Assert.False(
                    float.IsNaN(minMax[column * 2]),
                    "Column " + column + " was blanked, so the trace would draw with a gap in it.");
            }
        }

        [Fact]
        public void TheInterpolatedTraceIsAnchoredAtBothEnds()
        {
            // Otherwise the trace stops short of the right-hand graticule line, and the axis
            // annotation claims a stop frequency the trace never reaches.
            var values = new[] { -10.0f, 0.0f, 10.0f };
            var minMax = new float[9 * 2];

            TraceEnvelope.Build(values, 9, minMax);

            Assert.Equal(-10.0f, minMax[0]);
            Assert.Equal(10.0f, minMax[8 * 2]);
        }

        [Fact]
        public void InterpolationIsLinearBetweenNeighbouringPoints()
        {
            var values = new[] { 0.0f, 10.0f };
            var minMax = new float[11 * 2];

            TraceEnvelope.Build(values, 11, minMax);

            for (int column = 0; column < 11; column++)
            {
                Assert.Equal(column, minMax[column * 2], 4);
                Assert.Equal(minMax[column * 2], minMax[column * 2 + 1]);
            }
        }

        [Fact]
        public void ABlankedPointBlanksTheColumnsAroundIt_RatherThanBeingDrawnThrough()
        {
            var values = new[] { 0.0f, float.NaN, 10.0f };
            var minMax = new float[5 * 2];

            TraceEnvelope.Build(values, 5, minMax);

            Assert.Equal(0.0f, minMax[0]);
            Assert.True(float.IsNaN(minMax[2 * 2]));
            Assert.Equal(10.0f, minMax[4 * 2]);
        }

        [Fact]
        public void OneColumnTakesTheFirstPoint()
        {
            var values = new[] { -7.0f, 3.0f };
            var minMax = new float[2];

            TraceEnvelope.Build(values, 1, minMax);

            Assert.Equal(-7.0f, minMax[0]);
        }

        [Fact]
        public void AnEmptyTraceBlanksEveryColumn()
        {
            var minMax = new float[4 * 2];
            TraceEnvelope.Build(ReadOnlySpan<float>.Empty, 4, minMax);

            for (int i = 0; i < minMax.Length; i++)
            {
                Assert.True(float.IsNaN(minMax[i]));
            }
        }

        [Fact]
        public void ItRefusesAMismatchedOutputBuffer()
        {
            Assert.Throws<ArgumentException>(
                () => TraceEnvelope.Build(new float[10], 4, new float[6]));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => TraceEnvelope.Build(new float[10], 0, new float[0]));
        }
    }
}
