using System;
using System.Collections.Generic;
using System.Linq;
using OpenVSA.Dsp.Spectrum;
using OpenVSA.Dsp.Windowing;
using OpenVSA.Ui.Rendering;
using Xunit;
using Xunit.Abstractions;

namespace OpenVSA.Ui.Tests
{
    /// <summary>
    /// <c>REQ-UI-054</c>: what a spectrogram looks like, and that its three controls each change it.
    /// </summary>
    /// <remarks>
    /// The rendering half. <c>SpectrogramScalingTests</c> asserts what Threshold and Enhance mean;
    /// this asserts that the meaning reaches the pixels — a threshold honoured in the scaling and
    /// ignored in the rasteriser would pass the first and fail the criterion.
    /// </remarks>
    public class SpectrogramRenderTests
    {
        private const int Points = 401;

        private readonly ITestOutputHelper _output;

        /// <summary>Takes xunit's output sink.</summary>
        /// <param name="output">Where measured figures are written.</param>
        public SpectrogramRenderTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void TheNewestSweepIsDrawnAtTheTop()
        {
            // REQ-UI-063's own description of the control is "draw each sweep as a row, oldest at
            // the bottom", so the screen runs backwards through the history. A spectrogram drawn
            // the other way up reads every event as happening in reverse.
            const int rows = 20;
            const int height = 200;

            Assert.Equal(rows - 1, SpectrogramRasterizer.RowForY(0, height, rows));
            Assert.Equal(0, SpectrogramRasterizer.RowForY(height - 1, height, rows));

            // A row is drawn in the middle of the band it occupies, so the newest is in the topmost
            // band rather than on the topmost pixel.
            Assert.InRange(
                SpectrogramRasterizer.YForRow(rows - 1, height, rows), 0, height / rows);

            Assert.True(SpectrogramRasterizer.YForRow(0, height, rows) > height / 2);
        }

        [Fact]
        public void ARowAndItsScreenRowAgreeInBothDirections()
        {
            // A marker is drawn at YForRow and a click is read at RowForY. If they disagree, a
            // trace-select marker sits beside the row it claims to have selected.
            foreach (int rows in new[] { 3, 17, 64, 199 })
            {
                foreach (int height in new[] { 200, 307, 512 })
                {
                    if (height < rows)
                    {
                        continue;
                    }

                    for (int row = 0; row < rows; row++)
                    {
                        int y = SpectrogramRasterizer.YForRow(row, height, rows);

                        Assert.InRange(y, 0, height - 1);

                        Assert.Equal(
                            row,
                            SpectrogramRasterizer.RowForY(y, height, rows));
                    }

                    // And every bin, the same way: a spectrogram marker is drawn at ColumnForBin
                    // and a click is read back through the paint's own mapping.
                    for (int bin = 0; bin < Points; bin++)
                    {
                        int column = SpectrogramRasterizer.ColumnForBin(bin, 300, Points);

                        Assert.InRange(column, 0, 299);
                        Assert.InRange(bin, TraceEnvelope.IndexFor(column, Points, 300), Points - 1);
                    }
                }
            }
        }

        [Fact]
        public void RaisingTheThresholdRemovesCellsFromTheRendering()
        {
            // The criterion, counted in pixels rather than in model cells.
            Spectrogram history = Swept(24);
            var surface = new PixelSurface(320, 240);
            var area = new PixelRect(10, 10, 300, 220);

            SpectrogramLevels window = SpectrogramScaling.Window(
                history, SpectrogramLevels.NoThresholdDbm, false, Fallback);

            int everything = Paint(surface, area, history, window, SpectrogramLevels.NoThresholdDbm);
            int previous = everything;

            foreach (double threshold in new[] { -110.0, -100.0, -90.0, -80.0 })
            {
                int painted = Paint(surface, area, history, window, threshold);

                _output.WriteLine(
                    "threshold " + threshold.ToString("0") + " dBm paints " + painted + " of " +
                    everything + " pixels");

                Assert.True(
                    painted <= previous,
                    "Raising the threshold to " + threshold + " dBm painted more, not less.");

                previous = painted;
            }

            Assert.True(previous < everything, "No threshold removed anything from the rendering.");
        }

        [Fact]
        public void AThresholdAboveEverythingLeavesTheBackground()
        {
            Spectrogram history = Swept(8);
            var surface = new PixelSurface(120, 90);
            var area = new PixelRect(0, 0, 120, 90);
            var background = new PlotColor(1, 2, 3);

            int painted = SpectrogramRasterizer.Render(
                surface, area, history, SpectrogramColourMap.ColorNormal(), Fallback, 60.0, background);

            Assert.Equal(0, painted);
            Assert.Equal(background, surface.GetPixel(60, 45));
        }

        [Fact]
        public void MapColourSchemeSwitchesBetweenTheMaps()
        {
            // "Map Colour Scheme switches between the REQ-UI-024 maps" — asserted by rendering the
            // same history through each and comparing the pixels, not by reading back the setting.
            Spectrogram history = Swept(24);
            var area = new PixelRect(0, 0, 200, 160);

            SpectrogramLevels window = SpectrogramScaling.Window(
                history, SpectrogramLevels.NoThresholdDbm, false, Fallback);

            var drawn = new Dictionary<SpectrogramColourMapKind, string>();

            foreach (SpectrogramColourMapKind kind in
                (SpectrogramColourMapKind[])Enum.GetValues(typeof(SpectrogramColourMapKind)))
            {
                if (kind == SpectrogramColourMapKind.UserDefined)
                {
                    continue;
                }

                var surface = new PixelSurface(200, 160);

                SpectrogramRasterizer.Render(
                    surface,
                    area,
                    history,
                    SpectrogramColourMap.Of(kind),
                    window,
                    SpectrogramLevels.NoThresholdDbm,
                    PlotColor.Black);

                drawn[kind] = Signature(surface, area);
            }

            // Four maps, four different pictures. Colour Normal and Colour Reverse are each other's
            // inverse and Grey Normal and Grey Reverse likewise, so a rendering that ignored the map
            // would collapse all four onto one signature.
            Assert.Equal(drawn.Count, drawn.Values.Distinct().Count());
        }

        [Fact]
        public void EnhanceChangesTheColoursWithoutChangingWhichCellsAreDrawn()
        {
            // The two controls are independent: Threshold decides what is drawn, Enhance decides
            // what the drawn ones are coloured by. Asserted by the pixel count staying equal while
            // the picture changes.
            Spectrogram history = Swept(24);
            var area = new PixelRect(0, 0, 200, 160);

            var plain = new PixelSurface(200, 160);
            var enhanced = new PixelSurface(200, 160);

            SpectrogramLevels wide = SpectrogramScaling.Window(
                history, SpectrogramLevels.NoThresholdDbm, enhance: false, fallback: Fallback);

            SpectrogramLevels narrow = SpectrogramScaling.Window(
                history, SpectrogramLevels.NoThresholdDbm, enhance: true, fallback: Fallback);

            int plainCells = SpectrogramRasterizer.Render(
                plain, area, history, SpectrogramColourMap.ColorNormal(), wide,
                SpectrogramLevels.NoThresholdDbm, PlotColor.Black);

            int enhancedCells = SpectrogramRasterizer.Render(
                enhanced, area, history, SpectrogramColourMap.ColorNormal(), narrow,
                SpectrogramLevels.NoThresholdDbm, PlotColor.Black);

            Assert.Equal(plainCells, enhancedCells);

            Assert.NotEqual(
                Signature(plain, area),
                Signature(enhanced, area));

            // And it is an enhancement rather than merely a difference: the drawn cells use more of
            // the map than they did, which is the whole point of the control.
            Assert.True(
                DistinctColours(enhanced, area) > DistinctColours(plain, area),
                "Enhance used " + DistinctColours(enhanced, area) + " colours against " +
                DistinctColours(plain, area) + " without it.");
        }

        [Fact]
        public void ACarrierSurvivesDecimationIntoAColumn()
        {
            // A column covers many bins and takes the largest, because a spectrogram is read to find
            // signals. Averaging would bury a single-bin carrier in the noise either side of it.
            var levels = new float[Points];

            for (int i = 0; i < Points; i++)
            {
                levels[i] = -100.0f;
            }

            levels[200] = -10.0f;

            var history = new Spectrogram(1);
            history.Add(SpectrumFrame.FromLevels(levels, 1e9, 1e3, WindowType.FlatTop, 3.8194));

            var surface = new PixelSurface(64, 8);
            var area = new PixelRect(0, 0, 64, 8);

            SpectrogramRasterizer.Render(
                surface, area, history, SpectrogramColourMap.GreyNormal(),
                new SpectrogramLevels(-100.0, -10.0), SpectrogramLevels.NoThresholdDbm,
                PlotColor.Black);

            int column = SpectrogramRasterizer.ColumnForBin(200, 64, Points);

            // The carrier's column is at the top of the map; a neighbour is at the bottom.
            PlotColor top = SpectrogramColourMap.GreyNormal().Maximum;

            Assert.Equal(top, surface.GetPixel(column, 4));
            Assert.NotEqual(top, surface.GetPixel(column + 2, 4));

            // And the mapping is the one the paint used, which TraceEnvelope.ColumnFor is not: it
            // answers 31 for this bin while the column the peak was painted into is 32.
            Assert.NotEqual(TraceEnvelope.ColumnFor(200, Points, 64), column);
        }

        [Fact]
        public void TheTwoMarkerRulesArePerpendicularAndLandOnTheirOwnCoordinate()
        {
            // Drawn as lines rather than glyphs, and crossing at the cell the pair names.
            var surface = new PixelSurface(80, 60);
            var area = new PixelRect(5, 5, 70, 50);
            var colour = new PlotColor(255, 0, 0);

            Assert.True(MarkerGlyph.DrawVerticalRule(surface, 30, area, colour));
            Assert.True(MarkerGlyph.DrawHorizontalRule(surface, 20, area, colour));

            // The vertical rule occupies one column over the area's height and no other.
            Assert.True(Painted(surface, colour).All(p => p.Item1 == 30 || p.Item2 == 20));
            Assert.Contains(Painted(surface, colour), p => p.Item1 == 30 && p.Item2 != 20);
            Assert.Contains(Painted(surface, colour), p => p.Item2 == 20 && p.Item1 != 30);

            // Dashed, so the cells under a rule can still be read.
            Assert.True(
                Painted(surface, colour).Count(p => p.Item1 == 30) < area.Height,
                "The vertical rule is solid; it hides every cell in its column.");

            // Outside the area, nothing is drawn rather than something being clamped into it.
            Assert.False(MarkerGlyph.DrawVerticalRule(surface, 2, area, colour));
            Assert.False(MarkerGlyph.DrawHorizontalRule(surface, 400, area, colour));
        }

        [Fact]
        public void TheThreeAccumulatingModesAreNotFormats()
        {
            // "reached through TraceAccumulator rather than the format list, per REQ-TRC-001a".
            // The set of format names must not contain any of them, in either direction.
            var accumulators = new List<string>();

            foreach (TraceAccumulator mode in
                (TraceAccumulator[])Enum.GetValues(typeof(TraceAccumulator)))
            {
                if (mode != TraceAccumulator.None)
                {
                    accumulators.Add(mode.ToString());
                }
            }

            Assert.Equal(
                new[] { "Spectrogram", "DigitalPersistence", "CumulativeHistory" },
                accumulators.ToArray());

            foreach (TraceFormat format in (TraceFormat[])Enum.GetValues(typeof(TraceFormat)))
            {
                Assert.DoesNotContain(format.ToString(), accumulators);
            }
        }

        [Fact]
        public void AFullDepthMapRendersInsideAFramePeriod()
        {
            // REQ-NFR-006 and REQ-NFR-010: the map is drawn on the UI thread, once per acquisition,
            // and a full-depth history over a full-width graticule is the worst case. The first
            // implementation took 59 ms a frame — the shell stopped answering UI Automation
            // altogether, which is what the screenshot found; it is 6 ms now.
            //
            // The bound is deliberately loose. This runs in a Debug build alongside the rest of a
            // parallel suite, so the number it measures is not the number the application sees, and
            // a tight bound here would be a flake rather than a guarantee. What it is for is
            // catching a return to the order of magnitude above, which is the regression that
            // actually happened.
            var history = new Spectrogram(Spectrogram.DefaultDepth);

            for (int row = 0; row < Spectrogram.DefaultDepth; row++)
            {
                var levels = new float[801];

                for (int i = 0; i < levels.Length; i++)
                {
                    levels[i] = (float)(-108.0 + 6.0 * Math.Sin((i + row) * 0.05));
                }

                history.Add(SpectrumFrame.FromLevels(levels, 1e9, 1e3, WindowType.FlatTop, 3.8194));
            }

            var surface = new PixelSurface(1024, 640);
            var area = new PixelRect(48, 48, 928, 544);
            SpectrogramLevels window = SpectrogramScaling.Window(
                history, SpectrogramLevels.NoThresholdDbm, false, Fallback);

            // Once to warm the code paths, then measured.
            Paint(surface, area, history, window, SpectrogramLevels.NoThresholdDbm);

            var clock = System.Diagnostics.Stopwatch.StartNew();
            const int Frames = 20;

            for (int frame = 0; frame < Frames; frame++)
            {
                Paint(surface, area, history, window, SpectrogramLevels.NoThresholdDbm);
            }

            clock.Stop();

            double perFrameMs = clock.Elapsed.TotalMilliseconds / Frames;

            _output.WriteLine(
                area.Width + "×" + area.Height + " over " + history.RowCount + " rows of " +
                history.Newest.PointCount + " points: " +
                perFrameMs.ToString("0.00") + " ms per frame");

            Assert.True(
                perFrameMs < 25.0,
                "A full-depth spectrogram took " + perFrameMs.ToString("0.0") +
                " ms to draw; it was 6 ms when this was written, and at anything like this cost it " +
                "owns the frame instead of fitting in it.");
        }

        [Fact]
        public void TheFastColourPathAgreesWithTheColourMapItself()
        {
            // The rasteriser hoists SpectrogramColourMap.At's division out of its innermost loop.
            // Two ways of choosing a colour is exactly the duplication that drifts, so they are
            // compared across the whole range and past both ends.
            SpectrogramColourMap map = SpectrogramColourMap.ColorNormal();
            var window = new SpectrogramLevels(-113.0, -7.0);

            var surface = new PixelSurface(1, 1);
            var area = new PixelRect(0, 0, 1, 1);

            for (double level = -130.0; level <= 10.0; level += 0.37)
            {
                var history = new Spectrogram(1);
                var levels = new float[1];

                levels[0] = (float)level;
                history.Add(SpectrumFrame.FromLevels(levels, 1e9, 1e3, WindowType.FlatTop, 3.8194));

                SpectrogramRasterizer.Render(
                    surface, area, history, map, window, SpectrogramLevels.NoThresholdDbm,
                    PlotColor.Black);

                Assert.Equal(map.At(window.FractionOf(level)), surface.GetPixel(0, 0));
            }
        }

        [Fact]
        public void MissingArgumentsAreRefused()
        {
            var surface = new PixelSurface(8, 8);
            var area = new PixelRect(0, 0, 8, 8);

            Assert.Throws<ArgumentNullException>(() => SpectrogramRasterizer.Render(
                null, area, new Spectrogram(2), SpectrogramColourMap.Default, Fallback, 0.0,
                PlotColor.Black));

            Assert.Throws<ArgumentNullException>(() => SpectrogramRasterizer.Render(
                surface, area, null, SpectrogramColourMap.Default, Fallback, 0.0, PlotColor.Black));

            Assert.Throws<ArgumentNullException>(() => SpectrogramRasterizer.Render(
                surface, area, new Spectrogram(2), null, Fallback, 0.0, PlotColor.Black));

            Assert.Throws<ArgumentNullException>(
                () => MarkerGlyph.DrawVerticalRule(null, 0, area, PlotColor.Black));
        }

        // ---- Helpers -----------------------------------------------------------------------------

        private static SpectrogramLevels Fallback => new SpectrogramLevels(-120.0, 0.0);

        private static int Paint(
            PixelSurface surface,
            PixelRect area,
            Spectrogram history,
            SpectrogramLevels window,
            double thresholdDbm) =>
            SpectrogramRasterizer.Render(
                surface, area, history, SpectrogramColourMap.ColorNormal(), window, thresholdDbm,
                PlotColor.Black);

        /// <summary>A history of a carrier stepping up in frequency over a sloping floor.</summary>
        /// <remarks>
        /// A floor that slopes, because a flat one would give Enhance nothing to narrow onto and a
        /// map that ignored its window would still look right.
        /// </remarks>
        private static Spectrogram Swept(int rows)
        {
            var history = new Spectrogram(rows);

            for (int row = 0; row < rows; row++)
            {
                var levels = new float[Points];

                for (int i = 0; i < Points; i++)
                {
                    levels[i] = (float)(-108.0 + 6.0 * Math.Sin(i * 0.05));
                }

                int carrier = 30 + row * (340 / Math.Max(1, rows - 1));

                levels[Math.Min(Points - 1, carrier)] = -12.0f;

                history.Add(SpectrumFrame.FromLevels(levels, 1e9, 1e3, WindowType.FlatTop, 3.8194));
            }

            return history;
        }

        private static string Signature(PixelSurface surface, PixelRect area)
        {
            var text = new System.Text.StringBuilder();

            for (int y = area.Y; y < area.Bottom; y += 7)
            {
                for (int x = area.X; x < area.Right; x += 11)
                {
                    PlotColor colour = surface.GetPixel(x, y);

                    text.Append(colour.R).Append(',').Append(colour.G).Append(',')
                        .Append(colour.B).Append(';');
                }
            }

            return text.ToString();
        }

        private static int DistinctColours(PixelSurface surface, PixelRect area)
        {
            var seen = new HashSet<int>();

            for (int y = area.Y; y < area.Bottom; y++)
            {
                for (int x = area.X; x < area.Right; x++)
                {
                    PlotColor colour = surface.GetPixel(x, y);

                    seen.Add((colour.R << 16) | (colour.G << 8) | colour.B);
                }
            }

            return seen.Count;
        }

        private static List<Tuple<int, int>> Painted(PixelSurface surface, PlotColor colour)
        {
            var found = new List<Tuple<int, int>>();

            for (int y = 0; y < surface.Height; y++)
            {
                for (int x = 0; x < surface.Width; x++)
                {
                    if (surface.GetPixel(x, y).Equals(colour))
                    {
                        found.Add(Tuple.Create(x, y));
                    }
                }
            }

            return found;
        }
    }
}
