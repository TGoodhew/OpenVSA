using System;
using OpenVSA.Ui.Rendering;
using Xunit;

namespace OpenVSA.Ui.Tests
{
    /// <summary>
    /// <c>REQ-UI-010</c>: the three independently coloured zones, verified by rendering a frame
    /// and sampling it.
    /// </summary>
    /// <remarks>
    /// Runs headlessly because the render core has no WPF types. The fourth zone colour,
    /// <c>Annotation</c>, is asserted on the palette here but not on glyphs — annotation text is
    /// drawn by the WPF layer, where <c>REQ-UI-042</c>'s hot spots need real elements to hit-test
    /// against. That clause of the criterion is covered with the WPF surface.
    /// </remarks>
    public class PlotRasterizerTests
    {
        private const int Width = 400;
        private const int Height = 300;
        private const int Margin = 40;

        /// <summary>Four unmistakable, mutually distinct colours — no two channels shared.</summary>
        private static readonly PlotPalette Distinct = new PlotPalette(
            traceBackground: new PlotColor(10, 20, 30),
            grid: new PlotColor(40, 50, 60),
            annotation: new PlotColor(70, 80, 90),
            annotationBackground: new PlotColor(100, 110, 120),
            trace: new PlotColor(200, 210, 220));

        private static PlotLayout Layout() => new PlotLayout(Width, Height, Margin);

        private static PixelSurface Render(PlotPalette palette)
        {
            var surface = new PixelSurface(Width, Height);
            PlotRasterizer.Render(surface, Layout(), palette, ReadOnlySpan<float>.Empty);
            return surface;
        }

        /// <summary>A point inside the graticule that is on neither a grid line nor the trace.</summary>
        private static void PlainInteriorPoint(PlotLayout layout, out int x, out int y)
        {
            // Offset from a division boundary by a few pixels so the sample cannot land on a
            // graticule line by accident.
            x = layout.VerticalGridLineX(3) + 7;
            y = layout.HorizontalGridLineY(3) + 7;
        }

        // ---- The zones exist and are distinct ---------------------------------------------------

        [Fact]
        public void EachZoneRendersInItsOwnColour()
        {
            PlotLayout layout = Layout();
            PixelSurface surface = Render(Distinct);

            int x, y;
            PlainInteriorPoint(layout, out x, out y);
            Assert.Equal(Distinct.TraceBackground, surface.GetPixel(x, y));

            Assert.Equal(
                Distinct.Grid,
                surface.GetPixel(layout.VerticalGridLineX(3), layout.HorizontalGridLineY(3) + 7));

            Assert.Equal(Distinct.AnnotationBackground, surface.GetPixel(2, 2));
            Assert.Equal(Distinct.AnnotationBackground, surface.GetPixel(Width - 3, Height - 3));
        }

        [Fact]
        public void TheAnnotationBandSurroundsTheGraticuleOnAllFourSides()
        {
            PlotLayout layout = Layout();
            PixelSurface surface = Render(Distinct);

            int midX = Width / 2;
            int midY = Height / 2;

            Assert.Equal(Distinct.AnnotationBackground, surface.GetPixel(midX, Margin / 2));
            Assert.Equal(Distinct.AnnotationBackground, surface.GetPixel(midX, Height - Margin / 2));
            Assert.Equal(Distinct.AnnotationBackground, surface.GetPixel(Margin / 2, midY));
            Assert.Equal(Distinct.AnnotationBackground, surface.GetPixel(Width - Margin / 2, midY));

            Assert.True(layout.IsInAnnotationBand(midX, Margin / 2));
            Assert.False(layout.IsInAnnotationBand(midX, midY));
        }

        // ---- Independence: the load-bearing half of the criterion --------------------------------

        [Fact]
        public void ChangingTheTraceBackgroundLeavesTheOtherZonesUntouched()
        {
            AssertOnlyTheIntendedZoneChanges(
                Distinct.WithTraceBackground(new PlotColor(1, 2, 3)), changedZone: Zone.TraceBackground);
        }

        [Fact]
        public void ChangingTheGridColourLeavesTheOtherZonesUntouched()
        {
            AssertOnlyTheIntendedZoneChanges(
                Distinct.WithGrid(new PlotColor(4, 5, 6)), changedZone: Zone.Grid);
        }

        [Fact]
        public void ChangingTheAnnotationBackgroundLeavesTheOtherZonesUntouched()
        {
            AssertOnlyTheIntendedZoneChanges(
                Distinct.WithAnnotationBackground(new PlotColor(7, 8, 9)),
                changedZone: Zone.AnnotationBackground);
        }

        [Fact]
        public void ChangingTheAnnotationColourChangesNoRenderedZone()
        {
            // Annotation is text, drawn by the WPF layer. Changing it must not disturb anything
            // the rasteriser owns - which is what "four independent colours" means from this
            // side of the boundary.
            AssertOnlyTheIntendedZoneChanges(
                Distinct.WithAnnotation(new PlotColor(11, 12, 13)), changedZone: Zone.None);
        }

        private enum Zone
        {
            None,
            TraceBackground,
            Grid,
            AnnotationBackground,
        }

        private static void AssertOnlyTheIntendedZoneChanges(PlotPalette changed, Zone changedZone)
        {
            PlotLayout layout = Layout();
            PixelSurface before = Render(Distinct);
            PixelSurface after = Render(changed);

            int interiorX, interiorY;
            PlainInteriorPoint(layout, out interiorX, out interiorY);

            int gridX = layout.VerticalGridLineX(3);
            int gridY = layout.HorizontalGridLineY(3) + 7;

            AssertZone(before, after, interiorX, interiorY,
                changedZone == Zone.TraceBackground, "trace background");
            AssertZone(before, after, gridX, gridY,
                changedZone == Zone.Grid, "grid line");
            AssertZone(before, after, 2, 2,
                changedZone == Zone.AnnotationBackground, "annotation band");
        }

        private static void AssertZone(
            PixelSurface before, PixelSurface after, int x, int y, bool shouldChange, string zone)
        {
            PlotColor was = before.GetPixel(x, y);
            PlotColor now = after.GetPixel(x, y);

            if (shouldChange)
            {
                Assert.True(was != now, "The " + zone + " should have changed and did not.");
            }
            else
            {
                Assert.True(was == now,
                    "The " + zone + " changed from " + was + " to " + now +
                    ", so the zones are not independent.");
            }
        }

        // ---- The graticule ------------------------------------------------------------------------

        [Fact]
        public void TheGraticuleHasTheExpectedNumberOfLines()
        {
            PlotLayout layout = Layout();
            PixelSurface surface = Render(Distinct);

            int horizontal = 0;
            int scanX = layout.VerticalGridLineX(3) + 7;
            for (int y = layout.Graticule.Y; y < layout.Graticule.Bottom; y++)
            {
                if (surface.GetPixel(scanX, y) == Distinct.Grid)
                {
                    horizontal++;
                }
            }

            int vertical = 0;
            int scanY = layout.HorizontalGridLineY(3) + 7;
            for (int x = layout.Graticule.X; x < layout.Graticule.Right; x++)
            {
                if (surface.GetPixel(x, scanY) == Distinct.Grid)
                {
                    vertical++;
                }
            }

            // Ten divisions means eleven lines, borders included.
            Assert.Equal(layout.VerticalDivisions + 1, horizontal);
            Assert.Equal(layout.HorizontalDivisions + 1, vertical);
        }

        [Fact]
        public void TheGraticuleStaysInsideItsRectangle()
        {
            PlotLayout layout = Layout();
            PixelSurface surface = Render(Distinct);

            for (int y = 0; y < Height; y++)
            {
                for (int x = 0; x < Width; x++)
                {
                    if (surface.GetPixel(x, y) == Distinct.Grid)
                    {
                        Assert.True(
                            layout.Graticule.Contains(x, y),
                            "A grid pixel escaped the graticule at " + x + "," + y + ".");
                    }
                }
            }
        }

        // ---- The trace ------------------------------------------------------------------------------

        [Fact]
        public void TheTraceIsDrawnInsideTheGraticule()
        {
            PlotLayout layout = Layout();
            var surface = new PixelSurface(Width, Height);

            var minMax = new float[layout.Graticule.Width * 2];
            for (int column = 0; column < layout.Graticule.Width; column++)
            {
                float value = (float)(-50.0 + 20.0 * Math.Sin(column * 0.05));
                minMax[column * 2] = value;
                minMax[column * 2 + 1] = value;
            }

            PlotRasterizer.Render(surface, layout, Distinct, minMax);

            int traced = 0;
            for (int y = 0; y < Height; y++)
            {
                for (int x = 0; x < Width; x++)
                {
                    if (surface.GetPixel(x, y) == Distinct.Trace)
                    {
                        Assert.True(
                            layout.Graticule.Contains(x, y),
                            "A trace pixel escaped the graticule at " + x + "," + y + ".");
                        traced++;
                    }
                }
            }

            Assert.True(traced >= layout.Graticule.Width,
                "Expected at least one trace pixel per column, got " + traced + ".");
        }

        [Fact]
        public void TheTraceIsContinuousAcrossASteepEdge()
        {
            // A vertical step. Drawing each column's span independently would leave a gap the
            // height of the step; the trace would be correct at every column and visibly broken.
            PlotLayout layout = Layout();
            var surface = new PixelSurface(Width, Height);

            var minMax = new float[layout.Graticule.Width * 2];
            int half = layout.Graticule.Width / 2;
            for (int column = 0; column < layout.Graticule.Width; column++)
            {
                float value = column < half ? -90.0f : -10.0f;
                minMax[column * 2] = value;
                minMax[column * 2 + 1] = value;
            }

            PlotRasterizer.Render(surface, layout, Distinct, minMax);

            // Walk the column at the step and confirm an unbroken run of trace pixels spanning it.
            int stepX = layout.Graticule.X + half;
            int top = layout.ValueToY(-10.0);
            int bottom = layout.ValueToY(-90.0);

            for (int y = top; y <= bottom; y++)
            {
                Assert.True(
                    surface.GetPixel(stepX, y) == Distinct.Trace,
                    "Gap in the trace at row " + y + " of the step column.");
            }
        }

        [Fact]
        public void BlankedColumnsDrawNothing()
        {
            PlotLayout layout = Layout();
            var surface = new PixelSurface(Width, Height);

            var minMax = new float[layout.Graticule.Width * 2];
            for (int i = 0; i < minMax.Length; i++)
            {
                minMax[i] = float.NaN;
            }

            PlotRasterizer.Render(surface, layout, Distinct, minMax);

            for (int y = 0; y < Height; y++)
            {
                for (int x = 0; x < Width; x++)
                {
                    Assert.True(surface.GetPixel(x, y) != Distinct.Trace,
                        "A blanked trace drew a pixel at " + x + "," + y + ".");
                }
            }
        }

        [Fact]
        public void AnOffScaleTraceIsPinnedToTheEdgeRatherThanDiscarded()
        {
            // Pinning tells the user the signal is off-scale; discarding looks like no signal.
            PlotLayout layout = Layout();
            var surface = new PixelSurface(Width, Height);

            var minMax = new float[layout.Graticule.Width * 2];
            for (int column = 0; column < layout.Graticule.Width; column++)
            {
                minMax[column * 2] = 50.0f;
                minMax[column * 2 + 1] = 50.0f;
            }

            PlotRasterizer.Render(surface, layout, Distinct, minMax);

            Assert.Equal(
                Distinct.Trace,
                surface.GetPixel(layout.Graticule.X + 5, layout.Graticule.Y));
        }

        // ---- Contract -------------------------------------------------------------------------------

        [Fact]
        public void RejectsASurfaceThatDoesNotMatchTheLayout()
        {
            Assert.Throws<ArgumentException>(() => PlotRasterizer.Render(
                new PixelSurface(100, 100), Layout(), Distinct, ReadOnlySpan<float>.Empty));
        }

        [Fact]
        public void RejectsATraceOfTheWrongLength()
        {
            Assert.Throws<ArgumentException>(() => PlotRasterizer.Render(
                new PixelSurface(Width, Height), Layout(), Distinct, new float[7]));
        }

        [Fact]
        public void RejectsNullArguments()
        {
            var surface = new PixelSurface(Width, Height);

            Assert.Throws<ArgumentNullException>(() => PlotRasterizer.Render(
                null, Layout(), Distinct, ReadOnlySpan<float>.Empty));
            Assert.Throws<ArgumentNullException>(() => PlotRasterizer.Render(
                surface, null, Distinct, ReadOnlySpan<float>.Empty));
            Assert.Throws<ArgumentNullException>(() => PlotRasterizer.Render(
                surface, Layout(), null, ReadOnlySpan<float>.Empty));
        }
    }
}
