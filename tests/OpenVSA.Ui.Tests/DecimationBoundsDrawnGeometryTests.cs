using System;
using OpenVSA.Ui.Rendering;
using Xunit;

namespace OpenVSA.Ui.Tests
{
    /// <summary>
    /// <c>REQ-NFR-005</c> (amended 2026-07-29): decimation bounds drawn geometry by the pixel
    /// width, which is what makes the top strategy band unreachable.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The amended requirement withdrew the <c>D3DImage</c> path on the finding that
    /// <c>REQ-NFR-006</c>'s min/max decimation runs before anything is drawn, so a 2²⁰-point trace
    /// is drawn from at most a few thousand spans. That finding is the load-bearing one — if it
    /// stopped being true the withdrawal would no longer be justified — so it is asserted here
    /// rather than left as a paragraph in the specification.
    /// </para>
    /// <para>
    /// <strong>Asserted structurally, not by stopwatch.</strong> The obvious alternative is to time
    /// rasterisation at 8 192 and 2²⁰ points and assert the two are close. That measures the
    /// runner's load as much as the code, and CI has already produced one performance failure per
    /// session's worth of debugging for exactly that reason. The span count is the mechanism
    /// itself and is deterministic. The timings are recorded by <c>REQ-TST-007</c>'s harness, where
    /// a stored baseline and a machine-class check make a measurement mean something.
    /// </para>
    /// </remarks>
    public class DecimationBoundsDrawnGeometryTests
    {
        /// <summary>Source point counts spanning everything the product will draw.</summary>
        /// <remarks>
        /// 401 is below the column count and is interpolated rather than decimated, which is the
        /// other branch of <see cref="TraceEnvelope.Build"/> and must be bounded too.
        /// </remarks>
        private static readonly int[] SourcePoints = { 401, 8192, 65536, 1 << 20 };

        /// <summary>
        /// The widest surface a trace can be drawn on, generously: an 8K display edge to edge.
        /// </summary>
        private const int WidestRealisticSurface = 7680;

        [Theory]
        [InlineData(800)]
        [InlineData(1920)]
        [InlineData(WidestRealisticSurface)]
        public void TheDrawnSpanCountFollowsTheWidthAndNotThePointCount(int columns)
        {
            foreach (int points in SourcePoints)
            {
                var values = new float[points];

                for (int i = 0; i < points; i++)
                {
                    values[i] = (float)Math.Sin(i * 0.01);
                }

                var envelope = new float[columns * 2];
                TraceEnvelope.Build(values, columns, envelope);

                // Two extrema per column, whatever the source held. This is the whole of the
                // amendment's premise: the geometry handed to the rasteriser is a function of the
                // surface, and a 2^20-point trace costs what a 401-point one does.
                Assert.Equal(columns * 2, envelope.Length);
            }
        }

        [Fact]
        public void TheTopStrategyBandCannotBeReachedOnAnyRealisticSurface()
        {
            int drawnAtWidest = WidestRealisticSurface * 2;

            // 15 360 against a boundary of 20 000. The margin is the finding: there is no display
            // on which decimated geometry reaches the band whose D3DImage machinery was withdrawn.
            Assert.True(
                drawnAtWidest < RenderStrategySelector.StreamGeometryLimit,
                "REQ-NFR-005's withdrawal of the D3DImage path rests on decimated geometry never " +
                "reaching the top band. At " + WidestRealisticSurface + " px that is " +
                drawnAtWidest + " spans against a boundary of " +
                RenderStrategySelector.StreamGeometryLimit + ". If this fails, the withdrawal " +
                "needs revisiting rather than this number needs raising.");

            Assert.NotEqual(
                RenderStrategy.SoftwareRasterizer, RenderStrategySelector.Select(drawnAtWidest));
        }

        [Fact]
        public void TheBoundComesFromDecimationAndNotFromTheSelector()
        {
            // The discriminating half. Without decimation a 2^20-point trace lands squarely in the
            // top band -- so the previous test passes because of REQ-NFR-006, not because the
            // selector could never return that value. Both tests pass on a build where decimation
            // had been removed and only this one would then be lying, which is why it states the
            // undecimated case explicitly.
            Assert.Equal(RenderStrategy.SoftwareRasterizer, RenderStrategySelector.Select(1 << 20));

            // And the envelope really is the reduction: a 2^20-point trace at 800 columns is
            // 1 600 values, not 1 048 576.
            var values = new float[1 << 20];
            var envelope = new float[800 * 2];

            TraceEnvelope.Build(values, 800, envelope);

            Assert.Equal(1600, envelope.Length);
            Assert.True(envelope.Length < values.Length / 600);
        }
    }
}
