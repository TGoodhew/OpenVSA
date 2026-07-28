using System;
using System.Windows;
using System.Windows.Media.Imaging;
using OpenVSA.Ui.Rendering;
using Xunit;

namespace OpenVSA.Ui.Tests
{
    /// <summary>
    /// <c>REQ-UI-011</c>: the annotation band reflows, and the grid lines go independently.
    /// </summary>
    public class AnnotationReflowTests
    {
        [Fact]
        public void TurningAnnotationOffChangesThePlotRectangle()
        {
            // The criterion in one line: "Toggling Show Annotation changes the plot rectangle's
            // size, not merely text visibility." A implementation that only collapsed the text
            // would leave both of these unchanged.
            Sta.Run(() =>
            {
                TracePlot plot = Laid();

                Rect annotated = plot.GraticuleBounds;
                int annotatedColumns = plot.GraticuleColumns;

                plot.ShowAnnotation = false;

                Rect bare = plot.GraticuleBounds;

                Assert.True(
                    bare.Width > annotated.Width,
                    "The graticule did not widen: " + annotated + " then " + bare);

                Assert.True(
                    bare.Height > annotated.Height,
                    "The graticule did not deepen: " + annotated + " then " + bare);

                Assert.True(
                    plot.GraticuleColumns > annotatedColumns,
                    "The graticule gained no columns to decimate to.");
            });
        }

        [Fact]
        public void TheReclaimedSpaceIsTheWholeBandOnEverySide()
        {
            // Both bands and both margins, not just the top one - which is the shape of mistake a
            // reflow that only zeroed one row would make.
            Sta.Run(() =>
            {
                TracePlot plot = Laid();

                plot.ShowAnnotation = false;
                Rect bare = plot.GraticuleBounds;

                Assert.Equal(0.0, bare.X, 3);
                Assert.Equal(0.0, bare.Y, 3);
                Assert.Equal(800.0, bare.Width, 3);
                Assert.Equal(600.0, bare.Height, 3);
            });
        }

        [Fact]
        public void TurningItBackOnRestoresTheBand()
        {
            Sta.Run(() =>
            {
                TracePlot plot = Laid();

                Rect annotated = plot.GraticuleBounds;

                plot.ShowAnnotation = false;
                plot.ShowAnnotation = true;

                Assert.Equal(annotated.X, plot.GraticuleBounds.X, 3);
                Assert.Equal(annotated.Width, plot.GraticuleBounds.Width, 3);
                Assert.Equal(annotated.Height, plot.GraticuleBounds.Height, 3);
            });
        }

        [Fact]
        public void TheAnnotationItselfGoesWithTheBand()
        {
            // The band being reclaimed is the point, but annotation left visible over an expanded
            // graticule would be worse than either.
            //
            // The fault indicators are the one exception and this test used to assert the opposite.
            // REQ-UI-007 requires the conditions that invalidate a measurement to be shown on the
            // trace and not buried; hiding them because a user turned annotation off is a worse
            // burial than the event log that requirement already forbids. See
            // FaultIndicatorTests.TheIndicatorSurvivesShowAnnotationBeingTurnedOff.
            Sta.Run(() =>
            {
                TracePlot plot = Laid();

                plot.ShowAnnotation = false;

                foreach (FrameworkElement element in plot.AnnotationElements)
                {
                    Assert.Equal(Visibility.Collapsed, element.Visibility);
                }

                Assert.Equal(Visibility.Visible, plot.IndicatorElement.Visibility);

                plot.ShowAnnotation = true;

                foreach (FrameworkElement element in plot.AnnotationElements)
                {
                    Assert.Equal(Visibility.Visible, element.Visibility);
                }
            });
        }

        [Fact]
        public void ShowGridLinesIsIndependentOfShowAnnotation()
        {
            // The requirement states the independence outright, so it is tested outright: turning
            // the lines off must not move the plot rectangle, and turning the annotation off must
            // not take the lines with it.
            Sta.Run(() =>
            {
                TracePlot plot = Laid();

                Rect before = plot.GraticuleBounds;

                plot.ShowGraticuleLines = false;

                Assert.Equal(before.Width, plot.GraticuleBounds.Width, 3);
                Assert.Equal(before.Height, plot.GraticuleBounds.Height, 3);
                Assert.True(plot.ShowAnnotation);

                plot.ShowGraticuleLines = true;
                plot.ShowAnnotation = false;

                Assert.True(plot.ShowGraticuleLines);
            });
        }

        [Fact]
        public void WithGridLinesOffNoGridColourIsDrawn()
        {
            // Counted from the rendered frame rather than from the flag, because the flag being
            // read is the easy half.
            Sta.Run(() =>
            {
                var surface = new PixelSurface(400, 300);
                var layout = new PlotLayout(400, 300, 40);
                PlotPalette palette = PlotPalette.Dark;

                PlotRasterizer.Render(
                    surface, layout, palette, ReadOnlySpan<float>.Empty,
                    ReadOnlySpan<PlotColor>.Empty, drawGridLines: true);

                Assert.True(Contains(surface, palette.Grid), "The graticule was not drawn at all.");

                PlotRasterizer.Render(
                    surface, layout, palette, ReadOnlySpan<float>.Empty,
                    ReadOnlySpan<PlotColor>.Empty, drawGridLines: false);

                Assert.False(Contains(surface, palette.Grid), "Grid colour survived Show Grid Lines off.");
            });
        }

        [Fact]
        public void TheGraticuleKeepsItsBackgroundWithTheLinesOff()
        {
            // Show Grid Lines removes the lines and nothing else. If it also took the trace
            // background the plot area would vanish into the annotation band, and the two settings
            // would stop being independent in the way that matters on screen.
            Sta.Run(() =>
            {
                var surface = new PixelSurface(400, 300);
                var layout = new PlotLayout(400, 300, 40);
                PlotPalette palette = PlotPalette.Dark;

                PlotRasterizer.Render(
                    surface, layout, palette, ReadOnlySpan<float>.Empty,
                    ReadOnlySpan<PlotColor>.Empty, drawGridLines: false);

                Assert.Equal(
                    palette.TraceBackground,
                    surface.GetPixel(layout.Graticule.X + 5, layout.Graticule.Y + 5));

                Assert.Equal(palette.AnnotationBackground, surface.GetPixel(2, 2));
            });
        }

        [Fact]
        public void ThePlotFollowsTheSharedDisplayOptions()
        {
            // The Trace tab and the Display menu both write TraceDisplayOptions; the plot reads it.
            Sta.Run(() =>
            {
                TracePlot plot = Laid();
                var options = new TraceDisplayOptions { ShowAnnotation = false, ShowGridLines = false };

                plot.ApplyDisplayOptions(options);

                Assert.False(plot.ShowAnnotation);
                Assert.False(plot.ShowGraticuleLines);
                Assert.Equal(0.0, plot.GraticuleBounds.X, 3);
            });
        }

        [Fact]
        public void APlotNeedsOptionsToFollow()
        {
            Sta.Run(() =>
                Assert.Throws<ArgumentNullException>(() => new TracePlot().ApplyDisplayOptions(null)));
        }

        private static bool Contains(PixelSurface surface, PlotColor colour)
        {
            for (int y = 0; y < surface.Height; y++)
            {
                for (int x = 0; x < surface.Width; x++)
                {
                    if (surface.GetPixel(x, y).Equals(colour))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static TracePlot Laid()
        {
            var plot = new TracePlot();

            plot.Measure(new Size(800.0, 600.0));
            plot.Arrange(new Rect(0.0, 0.0, 800.0, 600.0));

            return plot;
        }
    }
}
