using System;
using System.Collections.Generic;
using System.Windows;
using OpenVSA.Measurement.State;
using OpenVSA.Ui.Rendering;
using Xunit;

namespace OpenVSA.Ui.Tests
{
    /// <summary>
    /// <c>REQ-UI-012</c>: ten by ten divisions by default, configurable, evenly spaced, with the
    /// outermost lines on the boundary.
    /// </summary>
    public class GraticuleGeometryTests
    {
        [Fact]
        public void TenByTenIsTheDefault()
        {
            var layout = new PlotLayout(400, 300, 40);

            Assert.Equal(10, layout.HorizontalDivisions);
            Assert.Equal(10, layout.VerticalDivisions);
        }

        [Fact]
        public void TenByTenIsCountedFromTheRenderedFrame()
        {
            // The criterion says "counted from the rendered frame", not read off the layout - so
            // the lines are counted in the pixels, which is also what catches a rasteriser that
            // draws one line twice or skips the last.
            var surface = new PixelSurface(400, 300);
            var layout = new PlotLayout(400, 300, 40);

            PlotRasterizer.Render(surface, layout, PlotPalette.Dark, ReadOnlySpan<float>.Empty);

            Assert.Equal(11, HorizontalLines(surface, layout).Count);
            Assert.Equal(11, VerticalLines(surface, layout).Count);
        }

        [Fact]
        public void ANonDefaultCountIsRenderedAsAsked()
        {
            // The discriminating half: a rasteriser that always drew eleven lines would pass the
            // test above and ignore the setting entirely.
            var surface = new PixelSurface(400, 300);
            var layout = new PlotLayout(400, 300, 40, 0.0, -100.0, 8, 5);

            PlotRasterizer.Render(surface, layout, PlotPalette.Dark, ReadOnlySpan<float>.Empty);

            Assert.Equal(6, HorizontalLines(surface, layout).Count);
            Assert.Equal(9, VerticalLines(surface, layout).Count);
        }

        [Fact]
        public void DivisionLinesAreEvenlySpacedToWithinOnePixel()
        {
            // Over a graticule whose height is not a multiple of the division count, which is where
            // an implementation that truncates instead of rounding drifts.
            var layout = new PlotLayout(407, 303, 40, 0.0, -100.0, 10, 10);

            AssertEvenlySpaced(RowsOf(layout), "horizontal");
            AssertEvenlySpaced(ColumnsOf(layout), "vertical");
        }

        [Fact]
        public void TheOutermostLinesAreTheGraticuleBoundary()
        {
            // "the outermost lines coincide with the grid boundary rather than falling inside or
            // outside it" - one pixel out either way and the trace is drawn over its own frame.
            var layout = new PlotLayout(407, 303, 40, 0.0, -100.0, 7, 13);

            Assert.Equal(layout.Graticule.Y, layout.HorizontalGridLineY(0));
            Assert.Equal(layout.Graticule.Bottom - 1, layout.HorizontalGridLineY(13));

            Assert.Equal(layout.Graticule.X, layout.VerticalGridLineX(0));
            Assert.Equal(layout.Graticule.Right - 1, layout.VerticalGridLineX(7));
        }

        [Fact]
        public void EveryCountFromTwoToTwentySpacesEvenlyAndReachesBothEdges()
        {
            // Swept rather than sampled, because the awkward cases are the ones where the division
            // count and the pixel height share no factor, and picking two counts by hand misses them.
            for (int divisions = TraceDisplayOptions.MinimumDivisions;
                 divisions <= TraceDisplayOptions.MaximumDivisions;
                 divisions++)
            {
                var layout = new PlotLayout(401, 307, 37, 0.0, -100.0, divisions, divisions);

                AssertEvenlySpaced(RowsOf(layout), divisions + " rows");
                AssertEvenlySpaced(ColumnsOf(layout), divisions + " columns");

                Assert.Equal(layout.Graticule.Y, layout.HorizontalGridLineY(0));
                Assert.Equal(layout.Graticule.Bottom - 1, layout.HorizontalGridLineY(divisions));
            }
        }

        [Fact]
        public void TheCountTakesEffectWithoutARestart()
        {
            // A live plot, already laid out, told to change its graticule. "A setting that takes
            // effect without restart" is the criterion, and this is the only way to assert it.
            Sta.Run(() =>
            {
                var plot = new TracePlot();
                plot.Measure(new Size(800.0, 600.0));
                plot.Arrange(new Rect(0.0, 0.0, 800.0, 600.0));

                Assert.Equal(10, plot.VerticalDivisions);
                Assert.Equal(10, plot.HorizontalDivisions);

                double fullScale = plot.FullScaleDb;

                plot.ApplyDisplayOptions(
                    new TraceDisplayOptions { HorizontalDivisions = 8, VerticalDivisions = 5 });

                Assert.Equal(8, plot.HorizontalDivisions);
                Assert.Equal(5, plot.VerticalDivisions);

                // The scale is per division, so halving the divisions halves the range the axis
                // spans - the bottom of the graticule moves, and the per-division reading does not.
                Assert.Equal(fullScale / 2.0, plot.FullScaleDb, 6);
                Assert.Equal(plot.TopDbm - plot.FullScaleDb, plot.BottomDbm, 6);
            });
        }

        [Fact]
        public void ADivisionCountOutsideTheRangeIsRefused()
        {
            var options = new TraceDisplayOptions();

            Assert.Throws<ArgumentOutOfRangeException>(() => options.VerticalDivisions = 1);
            Assert.Throws<ArgumentOutOfRangeException>(() => options.HorizontalDivisions = 21);

            // And a layout refuses a count of zero outright, since it would divide by it.
            Assert.Throws<ArgumentOutOfRangeException>(
                () => new PlotLayout(400, 300, 40, 0.0, -100.0, 0, 10));
        }

        [Fact]
        public void ANonsenseCountInTheFileIsClampedRatherThanThrownOn()
        {
            // A preferences file is a thing a user can edit. A bad division count should cost them
            // a graticule they recognise, not a shell that will not start.
            var state = new DisplayPreferencesState
            {
                HorizontalDivisions = 0,
                VerticalDivisions = 5000,
            };

            var options = new TraceDisplayOptions();
            options.LoadFrom(state);

            Assert.Equal(TraceDisplayOptions.MinimumDivisions, options.HorizontalDivisions);
            Assert.Equal(TraceDisplayOptions.MaximumDivisions, options.VerticalDivisions);
        }

        [Fact]
        public void TheGeometrySurvivesTheSidecar()
        {
            var before = new TraceDisplayOptions
            {
                HorizontalDivisions = 12,
                VerticalDivisions = 6,
                ShowAnnotation = false,
                ShowGridLines = false,
                XReferencePercent = 25,
            };

            var state = new DisplayPreferencesState();
            before.SaveInto(state);

            var after = new TraceDisplayOptions();
            after.LoadFrom(state);

            Assert.Equal(12, after.HorizontalDivisions);
            Assert.Equal(6, after.VerticalDivisions);
            Assert.False(after.ShowAnnotation);
            Assert.False(after.ShowGridLines);
            Assert.Equal(25, after.XReferencePercent);
        }

        private static void AssertEvenlySpaced(IReadOnlyList<int> positions, string what)
        {
            var gaps = new List<int>();

            for (int i = 1; i < positions.Count; i++)
            {
                gaps.Add(positions[i] - positions[i - 1]);
            }

            int smallest = int.MaxValue;
            int largest = int.MinValue;

            foreach (int gap in gaps)
            {
                smallest = Math.Min(smallest, gap);
                largest = Math.Max(largest, gap);
            }

            Assert.True(
                largest - smallest <= 1,
                what + " divisions are unevenly spaced: gaps ran " + smallest + " to " + largest +
                " pixels.");
        }

        private static IReadOnlyList<int> RowsOf(PlotLayout layout)
        {
            var rows = new List<int>();

            for (int division = 0; division <= layout.VerticalDivisions; division++)
            {
                rows.Add(layout.HorizontalGridLineY(division));
            }

            return rows;
        }

        private static IReadOnlyList<int> ColumnsOf(PlotLayout layout)
        {
            var columns = new List<int>();

            for (int division = 0; division <= layout.HorizontalDivisions; division++)
            {
                columns.Add(layout.VerticalGridLineX(division));
            }

            return columns;
        }

        /// <summary>Rows drawn entirely in the grid colour, read back out of the pixels.</summary>
        private static IReadOnlyList<int> HorizontalLines(PixelSurface surface, PlotLayout layout)
        {
            var rows = new List<int>();
            PlotColor grid = PlotPalette.Dark.Grid;

            for (int y = layout.Graticule.Y; y < layout.Graticule.Bottom; y++)
            {
                // Sampled a few pixels in from each end, so a vertical line's own pixels cannot
                // make a row look like a horizontal one.
                if (surface.GetPixel(layout.Graticule.X + 3, y).Equals(grid) &&
                    surface.GetPixel(layout.Graticule.Right - 4, y).Equals(grid))
                {
                    rows.Add(y);
                }
            }

            return rows;
        }

        private static IReadOnlyList<int> VerticalLines(PixelSurface surface, PlotLayout layout)
        {
            var columns = new List<int>();
            PlotColor grid = PlotPalette.Dark.Grid;

            for (int x = layout.Graticule.X; x < layout.Graticule.Right; x++)
            {
                if (surface.GetPixel(x, layout.Graticule.Y + 3).Equals(grid) &&
                    surface.GetPixel(x, layout.Graticule.Bottom - 4).Equals(grid))
                {
                    columns.Add(x);
                }
            }

            return columns;
        }
    }
}
