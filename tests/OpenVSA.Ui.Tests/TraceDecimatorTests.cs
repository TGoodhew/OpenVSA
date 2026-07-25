using System;
using OpenVSA.Ui.Rendering;
using Xunit;

namespace OpenVSA.Ui.Tests
{
    /// <summary>
    /// <c>REQ-NFR-006</c>: min/max envelope decimation per pixel column, never point-skipping.
    /// </summary>
    public class TraceDecimatorTests
    {
        // ---- The acceptance criterion, in the shape it is stated ------------------------------

        [Fact]
        public void SingleBinSpur_SurvivesDecimationAtItsCorrectAmplitude()
        {
            // REQ-NFR-006 AC verbatim: a synthetic spectrum containing a single one-bin -60 dBc
            // spur, rendered at 800 px from 524 288 points, displays the spur at its correct
            // amplitude.
            const int points = 524288;
            const int columns = 800;
            const float noiseFloorDb = -120.0f;
            const float spurDb = -60.0f;
            const int spurBin = 314159;

            var spectrum = new float[points];
            for (int i = 0; i < points; i++)
            {
                spectrum[i] = noiseFloorDb;
            }

            spectrum[spurBin] = spurDb;

            var minMax = new float[columns * 2];
            TraceDecimator.Decimate(spectrum, columns, minMax);

            int spurColumn = -1;
            for (int column = 0; column < columns; column++)
            {
                if (Math.Abs(minMax[column * 2 + 1] - spurDb) < 1e-6)
                {
                    spurColumn = column;
                }
            }

            Assert.True(spurColumn >= 0, "The spur was lost entirely.");

            // At its correct amplitude, not merely present: an implementation that averaged the
            // column would report about -117 dB here and still "show a spur".
            Assert.Equal(spurDb, minMax[spurColumn * 2 + 1], 5);

            // And in the right place, to within a column.
            int expectedColumn = (int)((long)spurBin * columns / points);
            Assert.InRange(spurColumn, expectedColumn - 1, expectedColumn + 1);
        }

        [Fact]
        public void PointSkipping_DemonstrablyLosesTheSameSpur()
        {
            // The other half of the criterion. Without this the test above only shows that
            // min/max works, not that the cheaper approach the requirement forbids actually
            // fails - which is the reason the requirement exists.
            const int points = 524288;
            const int columns = 800;
            const int spurBin = 314159;

            var spectrum = new float[points];
            for (int i = 0; i < points; i++)
            {
                spectrum[i] = -120.0f;
            }

            spectrum[spurBin] = -60.0f;

            bool found = false;
            for (int column = 0; column < columns; column++)
            {
                int index = (int)((long)column * points / columns);
                if (Math.Abs(spectrum[index] - (-60.0f)) < 1e-6)
                {
                    found = true;
                }
            }

            Assert.False(found, "Point-skipping happened to sample the spur; choose a bin it misses.");
        }

        // ---- Partitioning ----------------------------------------------------------------------

        [Fact]
        public void EverySourcePointBelongsToExactlyOneColumn()
        {
            // The guarantee rests entirely on the partition being exact. If columns overlapped or
            // left gaps, a spur could fall through and no amount of min/max would catch it.
            const int points = 100003;
            const int columns = 797;

            var counted = new bool[points];
            int total = 0;

            for (int column = 0; column < columns; column++)
            {
                int start = (int)((long)column * points / columns);
                int end = (int)(((long)column + 1) * points / columns);

                for (int i = start; i < end; i++)
                {
                    Assert.False(counted[i], "Point " + i + " is claimed by more than one column.");
                    counted[i] = true;
                    total++;
                }
            }

            Assert.Equal(points, total);
        }

        [Fact]
        public void ExtremaAreTheTrueExtremaOfTheirColumn()
        {
            var values = new float[1000];
            var random = new Random(4242);
            for (int i = 0; i < values.Length; i++)
            {
                values[i] = (float)(random.NextDouble() * 200.0 - 150.0);
            }

            const int columns = 37;
            var minMax = new float[columns * 2];
            TraceDecimator.Decimate(values, columns, minMax);

            for (int column = 0; column < columns; column++)
            {
                int start = (int)((long)column * values.Length / columns);
                int end = (int)(((long)column + 1) * values.Length / columns);

                float expectedMin = float.MaxValue;
                float expectedMax = float.MinValue;
                for (int i = start; i < end; i++)
                {
                    expectedMin = Math.Min(expectedMin, values[i]);
                    expectedMax = Math.Max(expectedMax, values[i]);
                }

                Assert.Equal(expectedMin, minMax[column * 2], 5);
                Assert.Equal(expectedMax, minMax[column * 2 + 1], 5);
            }
        }

        [Fact]
        public void LargeInputDoesNotOverflowTheColumnArithmetic()
        {
            // 2^20 points at 4096 columns overflows int at the multiply if the index is computed
            // in 32 bits, and the result is a negative index rather than a wrong-but-plausible
            // one. Cheap to guard, expensive to diagnose.
            const int points = 1 << 20;
            const int columns = 4096;

            var values = new float[points];
            values[points - 1] = 1.0f;

            var minMax = new float[columns * 2];
            TraceDecimator.Decimate(values, columns, minMax);

            Assert.Equal(1.0f, minMax[(columns - 1) * 2 + 1], 5);
        }

        // ---- Blanked points and empty columns ----------------------------------------------------

        [Fact]
        public void BlankedPointsAreExcludedFromTheExtrema()
        {
            var values = new float[] { -10.0f, float.NaN, -30.0f, float.NaN };

            var minMax = new float[2];
            TraceDecimator.Decimate(values, 1, minMax);

            Assert.Equal(-30.0f, minMax[0], 5);
            Assert.Equal(-10.0f, minMax[1], 5);
        }

        [Fact]
        public void AColumnOfNothingButBlanksStaysBlank()
        {
            var values = new float[] { float.NaN, float.NaN };

            var minMax = new float[2];
            TraceDecimator.Decimate(values, 1, minMax);

            Assert.True(float.IsNaN(minMax[0]));
            Assert.True(float.IsNaN(minMax[1]));
        }

        [Fact]
        public void ColumnsWithNoSourcePointsAreBlank()
        {
            // Fewer points than columns: some columns have nothing to show, and must say so
            // rather than repeating a neighbour's value.
            var values = new float[] { 1.0f, 2.0f };

            var minMax = new float[10 * 2];
            TraceDecimator.Decimate(values, 10, minMax);

            int populated = 0;
            for (int column = 0; column < 10; column++)
            {
                if (!float.IsNaN(minMax[column * 2]))
                {
                    populated++;
                }
            }

            Assert.Equal(2, populated);
        }

        [Fact]
        public void EmptyInputProducesAnEntirelyBlankResult()
        {
            var minMax = new float[6];
            TraceDecimator.Decimate(ReadOnlySpan<float>.Empty, 3, minMax);

            for (int i = 0; i < minMax.Length; i++)
            {
                Assert.True(float.IsNaN(minMax[i]));
            }
        }

        // ---- Contract -----------------------------------------------------------------------------

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        public void RejectsNonPositiveColumnCount(int columns)
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => TraceDecimator.Decimate(new float[10], columns, new float[2]));
        }

        [Fact]
        public void RejectsAMismatchedOutputLength()
        {
            Assert.Throws<ArgumentException>(
                () => TraceDecimator.Decimate(new float[10], 5, new float[8]));
        }

        [Theory]
        [InlineData(1000, 800, true)]
        [InlineData(800, 800, false)]
        [InlineData(400, 800, false)]
        public void IsRequired_WhenThereAreMorePointsThanPixels(int points, int columns, bool expected)
        {
            Assert.Equal(expected, TraceDecimator.IsRequired(points, columns));
        }
    }
}
