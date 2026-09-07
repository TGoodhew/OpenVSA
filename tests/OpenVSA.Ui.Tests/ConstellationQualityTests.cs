using System;
using System.Collections.Generic;
using System.Diagnostics;
using OpenVSA.Demod.Results;
using OpenVSA.Synthesis;
using OpenVSA.TestHarness.Synthesis;
using OpenVSA.Ui.Rendering;
using Xunit;
using Xunit.Abstractions;

namespace OpenVSA.Ui.Tests
{
    /// <summary>
    /// <c>REQ-DEM-082</c>: the overlays and rendering a constellation offers beyond the points.
    /// </summary>
    /// <remarks>
    /// The requirement lists five things — ideal-point overlay, colouring by error magnitude,
    /// decision boundaries, persistence with configurable decay, and density rendering — and one
    /// figure: a hundred thousand symbols at ten frames a second with persistence on. The overlay
    /// is <c>REQ-UI-050</c>'s and is asserted there; these are the other four and the figure.
    /// </remarks>
    public class ConstellationQualityTests
    {
        private readonly ITestOutputHelper _output;

        /// <summary>Takes xunit's output sink.</summary>
        /// <param name="output">Where measured figures are written.</param>
        public ConstellationQualityTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void ColouringByErrorMagnitudeSeparatesTheWorstSymbolFromTheRest()
        {
            // The check has to be against a symbol whose error is known, or "coloured by error"
            // passes for "coloured by anything that varies". One symbol is displaced deliberately,
            // and it is the one that must come out at the top of the map.
            const int Displaced = 37;

            SymbolTrace trace = new SyntheticSymbolSource
            {
                Scheme = ModulationScheme.Qpsk(),
                SignalToNoiseDb = 30.0,
                DisplacedSymbolIndex = Displaced,
                Displacement = 0.4,
            }.Generate(200).ToSymbolTrace();

            double worst = ConstellationRasterizer.WorstError(trace);
            double displaced = ConstellationRasterizer.Error(trace, Displaced);

            _output.WriteLine(
                "worst error " + worst.ToString("0.0000") + ", the displaced symbol's " +
                displaced.ToString("0.0000"));

            Assert.Equal(worst, displaced, 9);

            var colours = new ConstellationColours();

            PlotColor top = colours.ForError(displaced, worst);
            PlotColor bottom = colours.ForError(0.0, worst);

            Assert.Equal(colours.ErrorMap.Maximum, top);
            Assert.Equal(colours.ErrorMap.Minimum, bottom);
            Assert.NotEqual(top, bottom);

            // And the rendered frame carries the top colour exactly once per displaced symbol -
            // the point is identifiable on the display, not merely in the arithmetic.
            var surface = new PixelSurface(320, 320);
            var area = new PixelRect(10, 10, 300, 300);

            ConstellationRasterizer.Render(
                surface, area, trace, colours,
                new ConstellationOptions { Colouring = SymbolColouring.ByErrorMagnitude });

            Assert.True(
                Counted(surface, area, top) > 0,
                "No cell took the top of the error map, so the worst symbol is not on the display.");
        }

        [Fact]
        public void ColouringByErrorMagnitudeChangesTheColoursAndNotThePositions()
        {
            // A constellation coloured by error is the same constellation. If the positions moved,
            // the colouring would be telling you about the display rather than about the signal.
            SymbolTrace trace = Result(ModulationScheme.Qam16(), 400);

            var area = new PixelRect(10, 10, 300, 300);
            var colours = new ConstellationColours();

            var flat = new PixelSurface(320, 320);
            var byError = new PixelSurface(320, 320);

            ConstellationRender first = ConstellationRasterizer.Render(
                flat, area, trace, colours,
                new ConstellationOptions { Colouring = SymbolColouring.Fixed, Scale = 2.0 });

            ConstellationRender second = ConstellationRasterizer.Render(
                byError, area, trace, colours,
                new ConstellationOptions
                {
                    Colouring = SymbolColouring.ByErrorMagnitude,
                    Scale = 2.0,
                });

            Assert.Equal(first.SymbolsDrawn, second.SymbolsDrawn);

            int differed = 0;

            for (int y = area.Y; y < area.Bottom; y++)
            {
                for (int x = area.X; x < area.Right; x++)
                {
                    PlotColor a = flat.GetPixel(x, y);
                    PlotColor b = byError.GetPixel(x, y);

                    Assert.True(
                        Lit(a) == Lit(b),
                        "The colouring moved a symbol at " + x + "," + y + ".");

                    if (Lit(a) && !a.Equals(b))
                    {
                        differed++;
                    }
                }
            }

            _output.WriteLine(differed + " cells took a different colour");

            Assert.True(differed > 0, "The colouring changed nothing, so it is not wired up.");
        }

        [Fact]
        public void DecisionBoundariesFallWhereTheNearestIdealStateChanges()
        {
            // The boundary is not "a grid": it is the locus where the minimum-distance decision
            // changes its answer. For QPSK that is the axes, and it is a fact about the decision
            // rather than about the modulation — so it is measured by classifying the cells either
            // side of a drawn boundary cell and finding they disagree.
            SymbolTrace trace = Result(ModulationScheme.Qpsk(), 200);

            var surface = new PixelSurface(201, 201);
            var area = new PixelRect(0, 0, 201, 201);
            var boundaries = new DecisionBoundaries();
            var colours = new ConstellationColours();

            int cells = boundaries.Draw(surface, area, trace, 2.0, colours.DecisionBoundary);

            _output.WriteLine(
                cells + " boundary cells over " + boundaries.States + " states, " +
                boundaries.Computations + " computations");

            Assert.Equal(4, boundaries.States);
            Assert.True(cells > 0, "No boundary was drawn between four states.");

            // QPSK's states are at the four diagonals, so the boundaries are the two axes: the
            // centre row and the centre column carry them and the far corners do not.
            Assert.Equal(colours.DecisionBoundary, surface.GetPixel(100, 100));
            Assert.Equal(colours.DecisionBoundary, surface.GetPixel(40, 100));
            Assert.Equal(colours.DecisionBoundary, surface.GetPixel(100, 160));

            Assert.NotEqual(colours.DecisionBoundary, surface.GetPixel(40, 40));
            Assert.NotEqual(colours.DecisionBoundary, surface.GetPixel(160, 160));
        }

        [Fact]
        public void TheBoundaryMaskIsComputedOnceForAFixedDisplay()
        {
            // Classifying every cell against every state is the display's largest sum, and it is
            // the same sum every frame. A cache that recomputes each time looks identical on the
            // screen, so the count of computations is the only thing that can tell them apart.
            SymbolTrace trace = Result(ModulationScheme.Qam16(), 200);

            var surface = new PixelSurface(160, 160);
            var area = new PixelRect(0, 0, 160, 160);
            var boundaries = new DecisionBoundaries();
            var colours = new ConstellationColours();

            for (int frame = 0; frame < 10; frame++)
            {
                boundaries.Draw(surface, area, trace, 2.0, colours.DecisionBoundary);
            }

            Assert.Equal(1, boundaries.Computations);

            // A change of scale is a change of boundary, so it must not be reused.
            boundaries.Draw(surface, area, trace, 3.0, colours.DecisionBoundary);

            Assert.Equal(2, boundaries.Computations);

            // And a change of constellation likewise.
            boundaries.Draw(
                surface, area, Result(ModulationScheme.Qpsk(), 200), 3.0, colours.DecisionBoundary);

            Assert.Equal(3, boundaries.Computations);
        }

        [Fact]
        public void ASingleStateHasNoBoundaryAndNoneIsInvented()
        {
            // A constellation with one ideal state has no interior edge. Drawing a grid anyway
            // would be an assertion about a signal that does not support it.
            var points = new List<ConstellationPoint>();
            var symbols = new List<int>();
            var decisions = new List<int>();

            for (int symbol = 0; symbol < 16; symbol++)
            {
                symbols.Add(0);
                points.Add(new ConstellationPoint(1.0, 0.0));
                decisions.Add(symbol * 4);
            }

            var trace = new SymbolTrace(
                "one state", 1, 2, symbols, points, points, decisions,
                new float[128], 4, 1.0e6);

            var surface = new PixelSurface(64, 64);
            var boundaries = new DecisionBoundaries();

            int cells = boundaries.Draw(
                surface, new PixelRect(0, 0, 64, 64), trace, 2.0, PlotColor.White);

            Assert.Equal(0, cells);
            Assert.Equal(1, boundaries.States);
        }

        [Fact]
        public void PersistenceFadesTheOldAcquisitionAndTheDecayIsConfigurable()
        {
            // "Digital persistence with configurable decay." A cell that took a symbol and then
            // takes none must hold less afterwards, by the configured fraction and not by some
            // other one.
            var area = new PixelRect(0, 0, 64, 64);

            var density = new ConstellationDensity { Decay = 0.5 };

            SymbolTrace first = Result(ModulationScheme.Qpsk(), 100);

            density.Accumulate(first, 2.0, area);

            double peak = density.Peak;

            Assert.True(peak > 0.0, "Nothing was accumulated.");

            // The same result again: every cell it lit is lit again, so a cell holds half of what
            // it held plus what it just took.
            density.Accumulate(first, 2.0, area);

            _output.WriteLine(
                "peak " + peak.ToString("0.00") + " then " + density.Peak.ToString("0.00") +
                " after a second identical acquisition at decay 0.5");

            Assert.Equal((peak * 0.5) + peak, density.Peak, 4);

            // And the decay is what does it: at zero, the second acquisition stands alone.
            var undecayed = new ConstellationDensity { Decay = 0.0 };

            undecayed.Accumulate(first, 2.0, area);
            undecayed.Accumulate(first, 2.0, area);

            Assert.Equal(peak, undecayed.Peak, 4);

            // A decay of one never forgets, so the display would fill in without limit.
            Assert.Throws<ArgumentOutOfRangeException>(() => density.Decay = 1.0);
            Assert.Throws<ArgumentOutOfRangeException>(() => density.Decay = -0.1);
        }

        [Fact]
        public void APersistedCellStopsBeingDrawnRatherThanFadingForEver()
        {
            // A geometric decay never reaches zero. Without a floor, every cell the signal has ever
            // touched stays lit at a value too small to see and large enough to count as ink, and
            // the display slowly fills in.
            var area = new PixelRect(0, 0, 64, 64);
            var density = new ConstellationDensity { Decay = 0.5 };

            density.Accumulate(Result(ModulationScheme.Qpsk(), 100), 2.0, area);

            int lit = density.OccupiedCells;

            Assert.True(lit > 0, "Nothing was accumulated.");

            // Nothing further arrives: an empty result, acquisition after acquisition.
            SymbolTrace empty = Offscreen();

            for (int acquisition = 0; acquisition < 40; acquisition++)
            {
                density.Accumulate(empty, 2.0, area);
            }

            _output.WriteLine(
                lit + " cells lit, " + density.OccupiedCells + " after forty empty acquisitions");

            Assert.Equal(0, density.OccupiedCells);
        }

        [Fact]
        public void TheHeatMapSaysHowManySymbolsACellTookAndAFlatColourCannot()
        {
            // "Optional density (heat-map) rendering for large symbol counts." The claim being
            // tested is that the heat map carries information the point rendering does not: a cell
            // that took four thousand symbols and one that took one are the same colour as points
            // and different colours through the map.
            var area = new PixelRect(0, 0, 128, 128);

            SymbolTrace trace = Result(ModulationScheme.Qam16(), 20000);

            var density = new ConstellationDensity
            {
                Decay = 0.0,
                Rendering = DensityRendering.HeatMap,
            };

            var surface = new PixelSurface(128, 128);
            var colours = new ConstellationColours();

            ConstellationRender drawn = ConstellationRasterizer.Render(
                surface, area, trace, colours,
                new ConstellationOptions { Density = density, Scale = 2.0 });

            _output.WriteLine(drawn.ToString());
            _output.WriteLine("peak " + density.Peak + " symbols in one cell");

            // Every symbol is still drawn — counted into a cell rather than painted into one.
            Assert.Equal(trace.SymbolCount, drawn.SymbolsDrawn);

            Assert.True(
                density.Peak > 1.0,
                "No cell took more than one symbol, so there is no density to map.");

            Assert.True(
                drawn.DensityCells < drawn.SymbolsDrawn,
                "The density rendering painted a cell per symbol, so it has saved nothing: " +
                drawn.DensityCells + " cells for " + drawn.SymbolsDrawn + " symbols.");

            var used = new HashSet<string>(StringComparer.Ordinal);

            for (int y = area.Y; y < area.Bottom; y++)
            {
                for (int x = area.X; x < area.Right; x++)
                {
                    PlotColor colour = surface.GetPixel(x, y);

                    if (Lit(colour))
                    {
                        used.Add(colour.ToString());
                    }
                }
            }

            _output.WriteLine(used.Count + " distinct colours on the frame");

            Assert.True(
                used.Count > 8,
                "The heat map used " + used.Count + " colours, which is not a density scale.");
        }

        [Fact]
        public void TheHeatMapRampNeverGoesBackwardsDownTheColourMap()
        {
            // The intensity ramp is checked below on luminance, which the heat map's colours do not
            // order — REQ-UI-024's map runs blue to red and blue is not dimmer than red. What is
            // ordered is the map index, so that is what has to be monotonic: more in a cell is
            // never further down the map.
            var area = new PixelRect(0, 0, 64, 64);

            var density = new ConstellationDensity { Rendering = DensityRendering.HeatMap };

            density.Accumulate(Result(ModulationScheme.Qam16(), 20000), 2.0, area);

            Assert.True(density.Peak > 10.0, "Too little density to test the ramp over.");

            IReadOnlyList<PlotColor> entries = density.Map.Entries;

            int previous = -1;
            var reached = new HashSet<int>();

            for (double held = 1.0; held <= density.Peak; held += 1.0)
            {
                int index = IndexOf(entries, density.ColourFor(held, PlotColor.White));

                Assert.True(
                    index >= previous,
                    held + " in a cell maps lower than " + (held - 1.0) + ".");

                previous = index;
                reached.Add(index);
            }

            _output.WriteLine(
                "peak " + density.Peak + " in a cell, reaching " + reached.Count + " of the map's " +
                entries.Count + " entries");

            Assert.True(
                reached.Count > 4,
                "The density reached " + reached.Count + " map entries, which is not a ramp.");

            Assert.Equal(entries.Count - 1, IndexOf(entries, density.ColourFor(density.Peak, PlotColor.White)));
        }

        [Fact]
        public void TheIntensityRampIsMonotonicInWhatACellHolds()
        {
            var area = new PixelRect(0, 0, 64, 64);
            var density = new ConstellationDensity { Rendering = DensityRendering.Intensity };

            density.Accumulate(Result(ModulationScheme.Qam16(), 20000), 2.0, area);

            Assert.True(density.Peak > 10.0, "Too little density to test the ramp over.");

            var symbol = new PlotColor(0xFF, 0xD2, 0x00);

            double previous = -1.0;

            for (double held = 1.0; held <= density.Peak; held += 1.0)
            {
                double luminance = Luminance(density.ColourFor(held, symbol));

                Assert.True(
                    luminance >= previous,
                    held + " in a cell is dimmer than " + (held - 1.0) + ".");

                previous = luminance;
            }

            // Dim, but present: a cell one symbol reached is rare, not absent.
            Assert.True(Luminance(density.ColourFor(1.0, symbol)) > 0.0);
            Assert.True(
                Luminance(density.ColourFor(1.0, symbol)) <
                Luminance(density.ColourFor(density.Peak, symbol)));
        }

        [Fact]
        public void AHundredThousandSymbolsRenderWithPersistenceInsideAHundredMilliseconds()
        {
            // "100 000 symbols render at >=10 fps with persistence enabled." Ten frames a second is
            // a hundred milliseconds a frame, and the frame measured here is the whole one: the
            // decision boundaries under it, the ideal states over them, and the symbols through the
            // persistence accumulator.
            SymbolTrace trace = Result(ModulationScheme.Qam16(), 100000);

            Assert.Equal(100000, trace.SymbolCount);

            var surface = new PixelSurface(800, 600);
            var area = new PixelRect(0, 0, 800, 600);
            var colours = new ConstellationColours();

            var options = new ConstellationOptions
            {
                IdealStates = IdealStateOverlay.Crosshair,
                Scale = 2.0,
                DecisionBoundaries = new DecisionBoundaries(),
                Density = new ConstellationDensity
                {
                    Decay = 0.7,
                    Rendering = DensityRendering.Intensity,
                },
            };

            // One frame outside the measurement: the first pays for the boundary mask and for the
            // buffers, and a rate is about the frames after the first.
            ConstellationRasterizer.Render(surface, area, trace, colours, options);

            const int Frames = 10;

            var clock = Stopwatch.StartNew();

            for (int frame = 0; frame < Frames; frame++)
            {
                ConstellationRasterizer.Render(surface, area, trace, colours, options);
            }

            clock.Stop();

            double perFrame = clock.Elapsed.TotalMilliseconds / Frames;

            _output.WriteLine(
                "100 000 symbols, 800x600, persistence on: " + perFrame.ToString("0.0") +
                " ms a frame = " + (1000.0 / perFrame).ToString("0") + " fps");

            Assert.Equal(1, options.DecisionBoundaries.Computations);

            Assert.True(
                perFrame < 100.0,
                "REQ-DEM-082 asks for 10 fps with persistence enabled and this frame took " +
                perFrame.ToString("0.0") + " ms.");
        }

        /// <summary>A result whose symbols all land outside the drawn area.</summary>
        private static SymbolTrace Offscreen()
        {
            var points = new List<ConstellationPoint>();
            var symbols = new List<int>();
            var decisions = new List<int>();

            for (int symbol = 0; symbol < 4; symbol++)
            {
                symbols.Add(symbol);
                points.Add(new ConstellationPoint(1000.0, 1000.0));
                decisions.Add(symbol * 4);
            }

            return new SymbolTrace(
                "offscreen", 2, 2, symbols, points, points, decisions, new float[64], 4, 1.0e6);
        }

        private static SymbolTrace Result(ModulationScheme scheme, int symbols) =>
            new SyntheticSymbolSource { Scheme = scheme, SignalToNoiseDb = 22.0 }
                .Generate(symbols)
                .ToSymbolTrace();

        private static bool Lit(PlotColor colour) =>
            colour.R != 0 || colour.G != 0 || colour.B != 0;

        private static int Counted(PixelSurface surface, PixelRect area, PlotColor wanted)
        {
            int found = 0;

            for (int y = area.Y; y < area.Bottom; y++)
            {
                for (int x = area.X; x < area.Right; x++)
                {
                    if (surface.GetPixel(x, y).Equals(wanted))
                    {
                        found++;
                    }
                }
            }

            return found;
        }

        private static int IndexOf(IReadOnlyList<PlotColor> entries, PlotColor colour)
        {
            for (int entry = 0; entry < entries.Count; entry++)
            {
                if (entries[entry].Equals(colour))
                {
                    return entry;
                }
            }

            throw new InvalidOperationException(
                "The density produced " + colour + ", which is not one of the map's colours.");
        }

        private static double Luminance(PlotColor colour) =>
            (0.2126 * colour.R) + (0.7152 * colour.G) + (0.0722 * colour.B);
    }
}
