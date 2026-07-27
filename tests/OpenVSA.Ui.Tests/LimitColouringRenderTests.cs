using System;
using System.Collections.Generic;
using OpenVSA.Dsp.Spectrum;
using OpenVSA.Dsp.Windowing;
using OpenVSA.Measurement.Limits;
using OpenVSA.Ui.Rendering;
using Xunit;

namespace OpenVSA.Ui.Tests
{
    /// <summary>
    /// <c>REQ-UI-023</c> at the pixels: what the rendered frame actually contains.
    /// </summary>
    /// <remarks>
    /// The model tests prove the classification; these prove it reaches the surface. A correct
    /// classification that the rasteriser then ignores would pass every test in
    /// <c>LimitColouringTests</c> and paint a uniform trace.
    /// </remarks>
    public sealed class LimitColouringRenderTests
    {
        private static readonly PlotColor TraceColour = new PlotColor(0, 255, 0);
        private static readonly PlotColor LimitColour = new PlotColor(10, 20, 30);
        private static readonly PlotColor FailLimitColour = new PlotColor(255, 0, 255);
        private static readonly PlotColor FailMarginColour = new PlotColor(0, 255, 255);

        [Fact]
        public void TheRenderedFrameRecoloursTheTraceOverTheFailingSpanOnly()
        {
            // A trace at −60 dBm that rises to 0 dBm over the middle fifth, against a limit at
            // −30 dBm: the middle fifth of the trace must be drawn in Fail Limit and the rest in
            // the trace's own colour.
            SpectrumFrame trace = Trace();
            LimitTest test = Failing();

            var surface = new PixelSurface(320, 240);
            var layout = new PlotLayout(320, 240, 40, 20.0, -80.0);

            var minMax = new float[layout.Graticule.Width * 2];
            TraceDecimator.Decimate(trace.LevelsDbm, layout.Graticule.Width, minMax);

            PlotColor[] columns = LimitShading.ShadeTrace(
                LimitShading.ToColumns(
                    LimitShading.Classify(trace, test), layout.Graticule.Width),
                Colours(),
                TraceColour);

            PlotRasterizer.Render(surface, layout, Palette(), minMax, columns);

            IReadOnlyDictionary<PlotColor, int> counted = CountInside(surface, layout);

            Assert.True(
                Count(counted, FailLimitColour) > 0,
                "The failing span was not painted in the fail colour at all.");
            Assert.True(
                Count(counted, TraceColour) > 0,
                "The whole trace was painted the fail colour, not just the failing span.");
        }

        [Fact]
        public void TheLimitColourItselfAppearsNowhereOnTheSurface()
        {
            // The inverted implementation the requirement warns about would recolour the limit line
            // and leave the trace alone. There is no path by which the Limit colour can reach the
            // rasteriser — the limit line is not among the things it draws — so this asserts that
            // nothing has quietly grown one.
            SpectrumFrame trace = Trace();
            LimitTest test = Failing();

            var surface = new PixelSurface(320, 240);
            var layout = new PlotLayout(320, 240, 40, 20.0, -80.0);

            var minMax = new float[layout.Graticule.Width * 2];
            TraceDecimator.Decimate(trace.LevelsDbm, layout.Graticule.Width, minMax);

            PlotColor[] columns = LimitShading.ShadeTrace(
                LimitShading.ToColumns(
                    LimitShading.Classify(trace, test), layout.Graticule.Width),
                Colours(),
                TraceColour);

            PlotRasterizer.Render(surface, layout, Palette(), minMax, columns);

            Assert.Equal(0, Count(CountInside(surface, layout), LimitColour));
        }

        [Fact]
        public void APassingTraceRendersInOneColour()
        {
            SpectrumFrame trace = Trace();

            var passing = new LimitTest("generous");
            passing.Add(Line(20.0));

            var surface = new PixelSurface(320, 240);
            var layout = new PlotLayout(320, 240, 40, 20.0, -80.0);

            var minMax = new float[layout.Graticule.Width * 2];
            TraceDecimator.Decimate(trace.LevelsDbm, layout.Graticule.Width, minMax);

            PlotColor[] columns = LimitShading.ShadeTrace(
                LimitShading.ToColumns(
                    LimitShading.Classify(trace, passing), layout.Graticule.Width),
                Colours(),
                TraceColour);

            PlotRasterizer.Render(surface, layout, Palette(), minMax, columns);

            IReadOnlyDictionary<PlotColor, int> counted = CountInside(surface, layout);

            Assert.True(Count(counted, TraceColour) > 0);
            Assert.Equal(0, Count(counted, FailLimitColour));
            Assert.Equal(0, Count(counted, FailMarginColour));
        }

        [Fact]
        public void ColumnColoursMustMatchTheGraticuleWidth()
        {
            var surface = new PixelSurface(320, 240);
            var layout = new PlotLayout(320, 240, 40, 20.0, -80.0);
            var minMax = new float[layout.Graticule.Width * 2];

            Assert.Throws<ArgumentException>(() => PlotRasterizer.Render(
                surface, layout, Palette(), minMax, new PlotColor[3]));
        }

        [Fact]
        public void PassingNoColumnColoursDrawsTheWholeTraceInThePalettesTraceColour()
        {
            // The old four-argument call must keep working exactly as it did: everything that does
            // not have a limit test still renders a single-coloured trace.
            SpectrumFrame trace = Trace();

            var surface = new PixelSurface(320, 240);
            var layout = new PlotLayout(320, 240, 40, 20.0, -80.0);

            var minMax = new float[layout.Graticule.Width * 2];
            TraceDecimator.Decimate(trace.LevelsDbm, layout.Graticule.Width, minMax);

            PlotRasterizer.Render(surface, layout, Palette(), minMax);

            IReadOnlyDictionary<PlotColor, int> counted = CountInside(surface, layout);

            Assert.True(Count(counted, TraceColour) > 0);
            Assert.Equal(0, Count(counted, FailLimitColour));
        }

        private static PlotPalette Palette() => new PlotPalette(
            new PlotColor(0, 0, 0),
            new PlotColor(60, 60, 60),
            new PlotColor(200, 200, 200),
            new PlotColor(30, 30, 30),
            TraceColour);

        private static LimitColours Colours() => new LimitColours
        {
            Limit = LimitColour,
            Margin = new PlotColor(11, 22, 33),
            FailLimit = FailLimitColour,
            FailMargin = FailMarginColour,
        };

        private static LimitTest Failing()
        {
            var test = new LimitTest("mask");
            test.Add(Line(-30.0));
            return test;
        }

        private static LimitLine Line(double limitDbm)
        {
            var line = new LimitLine("mask", LimitSide.Upper);
            line.Add(0.9e9, limitDbm).Add(1.1e9, limitDbm);
            return line;
        }

        private static SpectrumFrame Trace()
        {
            var levels = new float[500];

            for (int i = 0; i < levels.Length; i++)
            {
                levels[i] = i >= 200 && i < 300 ? 0.0f : -60.0f;
            }

            return SpectrumFrame.FromLevels(levels, 1.0e9, 10e3, WindowType.FlatTop, 3.8194);
        }

        /// <summary>Counts the colours drawn inside the graticule.</summary>
        private static IReadOnlyDictionary<PlotColor, int> CountInside(
            PixelSurface surface, PlotLayout layout)
        {
            var counted = new Dictionary<PlotColor, int>();
            PixelRect graticule = layout.Graticule;

            for (int y = graticule.Y; y < graticule.Bottom; y++)
            {
                for (int x = graticule.X; x < graticule.Right; x++)
                {
                    PlotColor colour = surface.GetPixel(x, y);

                    int count;
                    counted.TryGetValue(colour, out count);
                    counted[colour] = count + 1;
                }
            }

            return counted;
        }

        private static int Count(IReadOnlyDictionary<PlotColor, int> counted, PlotColor colour)
        {
            int count;
            return counted.TryGetValue(colour, out count) ? count : 0;
        }
    }
}
