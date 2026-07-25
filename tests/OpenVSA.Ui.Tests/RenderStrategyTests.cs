using System;
using OpenVSA.Ui.Rendering;
using Xunit;

namespace OpenVSA.Ui.Tests
{
    /// <summary>
    /// <c>REQ-NFR-005</c>: the strategy chosen for a trace is observable and tested at each band
    /// boundary.
    /// </summary>
    public class RenderStrategyTests
    {
        [Theory]
        [InlineData(0, RenderStrategy.PolylineGeometry)]
        [InlineData(1, RenderStrategy.PolylineGeometry)]
        [InlineData(1999, RenderStrategy.PolylineGeometry)]
        [InlineData(2000, RenderStrategy.PolylineGeometry)]
        [InlineData(2001, RenderStrategy.StreamGeometry)]
        [InlineData(19999, RenderStrategy.StreamGeometry)]
        [InlineData(20000, RenderStrategy.StreamGeometry)]
        [InlineData(20001, RenderStrategy.SoftwareRasterizer)]
        [InlineData(1 << 20, RenderStrategy.SoftwareRasterizer)]
        public void StrategyIsSelectedAtEachBandBoundary(int pointCount, RenderStrategy expected)
        {
            Assert.Equal(expected, RenderStrategySelector.Select(pointCount));
        }

        [Fact]
        public void PerPointGeometryAboveTwoThousandPointsIsNotPermitted()
        {
            // The prohibition stated as a prohibition, which is how REQ-NFR-005 words it.
            Assert.False(RenderStrategySelector.IsPermitted(RenderStrategy.PolylineGeometry, 2001));
            Assert.False(RenderStrategySelector.IsPermitted(RenderStrategy.PolylineGeometry, 500000));
            Assert.True(RenderStrategySelector.IsPermitted(RenderStrategy.PolylineGeometry, 2000));
        }

        [Fact]
        public void StreamGeometryIsNotPermittedInTheTopBand()
        {
            // StreamGeometry removes the per-element overhead and then meets the same MilCore
            // tessellator, so it earns the middle band and not the top one.
            Assert.False(RenderStrategySelector.IsPermitted(RenderStrategy.StreamGeometry, 20001));
            Assert.True(RenderStrategySelector.IsPermitted(RenderStrategy.StreamGeometry, 20000));
        }

        [Fact]
        public void FallingBackToTheRasterizerIsAlwaysPermitted()
        {
            // REQ-NFR-005's RDP clause: with the shared-surface bridge unavailable the surface
            // drops to the WriteableBitmap rasteriser and still renders correctly. Dropping to a
            // more expensive strategy is legitimate at any size; dropping up to per-point
            // geometry never is.
            Assert.True(RenderStrategySelector.IsPermitted(RenderStrategy.SoftwareRasterizer, 10));
            Assert.True(RenderStrategySelector.IsPermitted(RenderStrategy.SoftwareRasterizer, 1 << 20));
            Assert.True(RenderStrategySelector.IsPermitted(RenderStrategy.StreamGeometry, 10));
        }

        [Fact]
        public void TheBandBoundariesAreTheOnesTheRequirementStates()
        {
            Assert.Equal(2000, RenderStrategySelector.PolylineLimit);
            Assert.Equal(20000, RenderStrategySelector.StreamGeometryLimit);
        }

        [Fact]
        public void RejectsANegativePointCount()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => RenderStrategySelector.Select(-1));
        }
    }
}
