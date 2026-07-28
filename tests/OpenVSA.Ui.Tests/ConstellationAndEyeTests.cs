using System;
using System.Collections.Generic;
using System.Windows;
using System.Linq;
using OpenVSA.Demod.Results;
using OpenVSA.TestHarness.Synthesis;
using OpenVSA.Ui.Rendering;
using Xunit;
using Xunit.Abstractions;

namespace OpenVSA.Ui.Tests
{
    /// <summary>
    /// <c>REQ-UI-050</c>: the constellation, and how it differs from the IQ/vector format.
    /// </summary>
    public class ConstellationTests
    {
        private readonly ITestOutputHelper _output;

        /// <summary>Takes xunit's output sink.</summary>
        /// <param name="output">Where measured figures are written.</param>
        public ConstellationTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void ExactlyOnePointIsDrawnPerSymbolAndNoLinesJoinThem()
        {
            // "a test asserts the rendered primitive count equals the symbol count and that no line
            // segments join them".
            SymbolTrace trace = Result(ModulationScheme.Qam16(), 240);

            var surface = new PixelSurface(320, 320);
            var area = new PixelRect(10, 10, 300, 300);

            ConstellationRender drawn = ConstellationRasterizer.Render(
                surface, area, trace, new ConstellationColours(),
                IdealStateOverlay.Crosshair, connect: false);

            Assert.Equal(trace.SymbolCount, drawn.SymbolsDrawn);
            Assert.Equal(0, drawn.SegmentsDrawn);
        }

        [Fact]
        public void TheSameDataInVectorFormatDrawsTheConnectingTrajectory()
        {
            // "The same data in IQ/Vector format draws the connecting trajectory, which is the
            // difference between the two." One argument, and the whole difference.
            SymbolTrace trace = Result(ModulationScheme.Qpsk(), 80);

            var area = new PixelRect(0, 0, 240, 240);

            var constellation = new PixelSurface(240, 240);
            var vector = new PixelSurface(240, 240);

            ConstellationRender points = ConstellationRasterizer.Render(
                constellation, area, trace, new ConstellationColours(),
                IdealStateOverlay.None, connect: false);

            ConstellationRender joined = ConstellationRasterizer.Render(
                vector, area, trace, new ConstellationColours(),
                IdealStateOverlay.None, connect: true);

            Assert.Equal(0, points.SegmentsDrawn);
            Assert.Equal(trace.SymbolCount - 1, joined.SegmentsDrawn);

            // And it shows: the vector format inks far more of the display than the points alone.
            int pointInk = Inked(constellation, area);
            int vectorInk = Inked(vector, area);

            _output.WriteLine(
                "constellation " + pointInk + " pixels, vector " + vectorInk + " pixels");

            Assert.True(
                vectorInk > pointInk * 2,
                "The vector format drew " + vectorInk + " against " + pointInk +
                "; the trajectory is missing.");
        }

        [Fact]
        public void IdealStatesOverlayAsCrosshairsOrCirclesAndNeverAsFilledDots()
        {
            // "Ideal states overlay as crosshairs or circles, user-selectable, and never as filled
            // dots" — a filled dot is what a measured symbol is, so an overlay drawn that way is
            // confusable with the data.
            SymbolTrace trace = Result(ModulationScheme.Qam16(), 160);

            var area = new PixelRect(0, 0, 300, 300);
            var colours = new ConstellationColours
            {
                IdealState = new PlotColor(0x00, 0xFF, 0x00),
                Symbol = new PlotColor(0xFF, 0x00, 0x00),
            };

            foreach (IdealStateOverlay overlay in
                new[] { IdealStateOverlay.Crosshair, IdealStateOverlay.Circle })
            {
                var surface = new PixelSurface(300, 300);

                ConstellationRender drawn = ConstellationRasterizer.Render(
                    surface, area, trace, colours, overlay, connect: false);

                // 16QAM has sixteen ideal states and every one is used by 160 random symbols.
                Assert.Equal(16, drawn.OverlaysDrawn);

                // The overlay is open in the middle: the centre of each ideal state is not inked by
                // it, which is what a filled dot would do and what the requirement forbids.
                double extent = ConstellationRasterizer.Extent(trace);

                foreach (ConstellationPoint ideal in trace.Ideal.Distinct().Take(4))
                {
                    int x = ConstellationRasterizer.XFor(ideal.I, extent, area);
                    int y = ConstellationRasterizer.YFor(ideal.Q, extent, area);

                    Assert.NotEqual(colours.IdealState, surface.GetPixel(x, y));
                }
            }

            // And None draws none, so the choice is a real one.
            var bare = new PixelSurface(300, 300);

            Assert.Equal(
                0,
                ConstellationRasterizer.Render(
                    bare, area, trace, colours, IdealStateOverlay.None, false).OverlaysDrawn);
        }

        [Fact]
        public void SymbolPointsUseTheSymbolColourNotTheTraceLines()
        {
            // REQ-UI-022 lists Symbol as a per-trace element in its own right and REQ-UI-050 says
            // the points use it.
            SymbolTrace trace = Result(ModulationScheme.Qpsk(), 60);

            var area = new PixelRect(0, 0, 200, 200);
            var surface = new PixelSurface(200, 200);

            var colours = new ConstellationColours
            {
                Symbol = new PlotColor(0x11, 0x22, 0x33),
                Trajectory = new PlotColor(0x99, 0x88, 0x77),
            };

            ConstellationRasterizer.Render(
                surface, area, trace, colours, IdealStateOverlay.None, connect: false);

            double extent = ConstellationRasterizer.Extent(trace);

            int x = ConstellationRasterizer.XFor(trace.Measured[0].I, extent, area);
            int y = ConstellationRasterizer.YFor(trace.Measured[0].Q, extent, area);

            Assert.Equal(colours.Symbol, surface.GetPixel(x, y));
            Assert.NotEqual(colours.Trajectory, surface.GetPixel(x, y));
        }

        [Fact]
        public void AMixedModulationSignalColoursSymbolsByModulationType()
        {
            // "a mixed-modulation signal colours symbols by modulation type via the Mod Type N
            // entries".
            var source = new SyntheticSymbolSource { Scheme = ModulationScheme.Qpsk() };
            SyntheticBurst burst = source.Generate(40);

            // Alternate types, so a display that used one colour for everything is visibly wrong.
            var types = Enumerable.Range(0, 40).Select(i => i % 2).ToList();
            SymbolTrace trace = burst.ToSymbolTrace(types);

            Assert.True(trace.IsMixedModulation);

            var colours = new ConstellationColours
            {
                Symbol = new PlotColor(0xFF, 0xFF, 0xFF),
                ModulationTypes = new[]
                {
                    new PlotColor(0xFF, 0x00, 0x00),
                    new PlotColor(0x00, 0x00, 0xFF),
                },
            };

            Assert.Equal(new PlotColor(0xFF, 0x00, 0x00), colours.For(trace, 0));
            Assert.Equal(new PlotColor(0x00, 0x00, 0xFF), colours.For(trace, 1));

            // A result with one modulation falls back to the Symbol colour, so "every symbol is
            // type 0" and "there is one modulation" stay different things.
            SymbolTrace single = burst.ToSymbolTrace();

            Assert.False(single.IsMixedModulation);
            Assert.Equal(colours.Symbol, colours.For(single, 0));
        }

        [Fact]
        public void MissingArgumentsAreRefused()
        {
            var surface = new PixelSurface(8, 8);
            var area = new PixelRect(0, 0, 8, 8);
            SymbolTrace trace = Result(ModulationScheme.Qpsk(), 4);

            Assert.Throws<ArgumentNullException>(() => ConstellationRasterizer.Render(
                null, area, trace, new ConstellationColours(), IdealStateOverlay.None, false));

            Assert.Throws<ArgumentNullException>(() => ConstellationRasterizer.Render(
                surface, area, null, new ConstellationColours(), IdealStateOverlay.None, false));

            Assert.Throws<ArgumentNullException>(() => ConstellationRasterizer.Render(
                surface, area, trace, null, IdealStateOverlay.None, false));

            Assert.Throws<ArgumentNullException>(() => ConstellationRasterizer.Extent(null));
        }

        internal static SymbolTrace Result(ModulationScheme scheme, int symbols) =>
            new SyntheticSymbolSource { Scheme = scheme, SignalToNoiseDb = 28.0 }
                .Generate(symbols)
                .ToSymbolTrace();

        internal static int Inked(PixelSurface surface, PixelRect area)
        {
            int inked = 0;

            for (int y = area.Y; y < area.Bottom; y++)
            {
                for (int x = area.X; x < area.Right; x++)
                {
                    PlotColor colour = surface.GetPixel(x, y);

                    if (colour.R != 0 || colour.G != 0 || colour.B != 0)
                    {
                        inked++;
                    }
                }
            }

            return inked;
        }
    }

    /// <summary>
    /// <c>REQ-UI-051</c>: the eye diagram's centring, reference lines and accumulation.
    /// </summary>
    public class EyeDiagramTests
    {
        private readonly ITestOutputHelper _output;

        /// <summary>Takes xunit's output sink.</summary>
        /// <param name="output">Where measured figures are written.</param>
        public EyeDiagramTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void AOneSymbolEyeSpansMinusHalfToPlusHalfAboutTheDisplayCentre()
        {
            // "a one-symbol eye spans -1/2 to +1/2 symbol about the display centre, measured from
            // the rendered frame".
            var area = new PixelRect(0, 0, 401, 300);

            Assert.Equal(area.X, EyeRasterizer.XForSymbolOffset(-0.5, 1.0, area));
            Assert.Equal(area.Right - 1, EyeRasterizer.XForSymbolOffset(0.5, 1.0, area));

            int centre = EyeRasterizer.XForSymbolOffset(0.0, 1.0, area);

            Assert.Equal(area.X + (area.Width - 1) / 2, centre);

            // And it stays centred at every allowed length.
            foreach (double length in new[] { 0.1, 0.5, 2.0, 5.5, 10.0 })
            {
                Assert.Equal(centre, EyeRasterizer.XForSymbolOffset(0.0, length, area));
                Assert.Equal(area.X, EyeRasterizer.XForSymbolOffset(-length / 2.0, length, area));
            }
        }

        [Fact]
        public void ReferenceLinesFallAtTheSymbolPositions()
        {
            // "Vertical reference lines fall at the symbol positions, coinciding with the points of
            // maximum eye opening for a clean signal — checked against the generated signal's known
            // symbol clock, so a half-symbol offset fails."
            var area = new PixelRect(0, 0, 401, 300);

            Assert.Equal(new[] { 0.0 }, EyeRasterizer.ReferenceOffsets(1.0).ToArray());
            Assert.Equal(new[] { -1.0, 0.0, 1.0 }, EyeRasterizer.ReferenceOffsets(2.0).ToArray());

            Assert.Equal(
                new[] { -2.0, -1.0, 0.0, 1.0, 2.0 },
                EyeRasterizer.ReferenceOffsets(4.0).ToArray());

            // The centre line is the symbol instant, not a half-symbol out.
            int centre = EyeRasterizer.XForSymbolOffset(0.0, 2.0, area);
            int half = EyeRasterizer.XForSymbolOffset(0.5, 2.0, area);

            Assert.NotEqual(centre, half);
            Assert.Equal(area.X + (area.Width - 1) / 2, centre);
        }

        [Fact]
        public void TheEyeIsWidestAtTheReferenceLinesForACleanSignal()
        {
            // The physical check the requirement asks for: the reference lines coincide with the
            // maximum eye opening. Measured from the waveform rather than from the rendering, so a
            // half-symbol error in the fold shows up as the eye being narrowest where the lines are.
            SymbolTrace trace = ConstellationTests.Result(ModulationScheme.Bpsk(), 200);

            double atSymbol = Opening(trace, 0.0);
            double atHalf = Opening(trace, 0.5);

            _output.WriteLine(
                "opening at the symbol instant " + atSymbol.ToString("0.000") +
                ", half a symbol away " + atHalf.ToString("0.000"));

            Assert.True(
                atSymbol > atHalf * 2.0,
                "The eye is not widest at the symbol instant: " + atSymbol + " against " + atHalf + ".");
        }

        [Fact]
        public void EveryFoldIsOverlaidAndTheReferenceLinesAreDrawn()
        {
            SymbolTrace trace = ConstellationTests.Result(ModulationScheme.Qpsk(), 120);

            var surface = new PixelSurface(320, 240);
            var area = new PixelRect(10, 10, 300, 220);

            EyeRender drawn = EyeRasterizer.Render(
                surface, area, trace, EyeComponent.InPhase, 2.0, new EyeColours());

            // One fold per symbol instant that had samples, and REQ-UI-051's three lines at -1, 0
            // and +1 symbols for a two-symbol eye.
            Assert.Equal(trace.SymbolCount, drawn.Folds);
            Assert.Equal(3, drawn.ReferenceLines);
        }

        [Fact]
        public void RenderingIsAccumulativeRatherThanReplacing()
        {
            // "Rendering is accumulative: successive acquisitions overlay rather than replace."
            // The surface is not cleared, so a second result drawn onto it adds ink.
            var area = new PixelRect(0, 0, 300, 220);
            var surface = new PixelSurface(300, 220);

            SymbolTrace first = ConstellationTests.Result(ModulationScheme.Qpsk(), 40);

            EyeRasterizer.Render(surface, area, first, EyeComponent.InPhase, 2.0, new EyeColours());

            int afterOne = ConstellationTests.Inked(surface, area);

            // A different result, so it inks pixels the first did not.
            SymbolTrace second = new SyntheticSymbolSource
            {
                Scheme = ModulationScheme.Qpsk(),
                Seed = 4242,
                SignalToNoiseDb = 18.0,
            }.Generate(40).ToSymbolTrace();

            EyeRasterizer.Render(surface, area, second, EyeComponent.InPhase, 2.0, new EyeColours());

            int afterTwo = ConstellationTests.Inked(surface, area);

            _output.WriteLine(afterOne + " pixels after one acquisition, " + afterTwo + " after two");

            Assert.True(
                afterTwo > afterOne,
                "The second acquisition replaced the first instead of overlaying it.");
        }

        [Fact]
        public void AnMLevelModulationShowsMMinusOneEyes()
        {
            // "An m-level modulation shows m-1 eyes stacked vertically, counted for at least two
            // values of m." Counted from the waveform's own levels at the decision instants rather
            // than from the declaration, so the two have to agree.
            foreach (ModulationScheme scheme in
                new[] { ModulationScheme.Bpsk(), ModulationScheme.Qam16(), ModulationScheme.Qam64() })
            {
                SymbolTrace trace = ConstellationTests.Result(scheme, 600);

                var levels = new HashSet<double>();

                for (int symbol = 0; symbol < trace.SymbolCount; symbol++)
                {
                    levels.Add(Math.Round(trace.Ideal[symbol].I, 4));
                }

                _output.WriteLine(
                    scheme.Name + ": " + levels.Count + " levels on I, " + trace.EyeOpenings +
                    " eyes declared");

                Assert.Equal(levels.Count, trace.LevelsPerAxis);
                Assert.Equal(levels.Count - 1, trace.EyeOpenings);
            }
        }

        [Fact]
        public void AnEyeLengthOutsideTheAllowedRangeIsRejected()
        {
            // "Eye length is settable over 0.1 to 10 symbols with values outside rejected."
            Assert.True(EyeRasterizer.IsLengthAllowed(0.1));
            Assert.True(EyeRasterizer.IsLengthAllowed(10.0));
            Assert.True(EyeRasterizer.IsLengthAllowed(2.0));

            Assert.False(EyeRasterizer.IsLengthAllowed(0.09));
            Assert.False(EyeRasterizer.IsLengthAllowed(10.01));
            Assert.False(EyeRasterizer.IsLengthAllowed(0.0));
            Assert.False(EyeRasterizer.IsLengthAllowed(double.NaN));

            SymbolTrace trace = ConstellationTests.Result(ModulationScheme.Qpsk(), 8);

            var surface = new PixelSurface(64, 64);
            var area = new PixelRect(0, 0, 64, 64);

            Assert.Throws<ArgumentOutOfRangeException>(() => EyeRasterizer.Render(
                surface, area, trace, EyeComponent.InPhase, 12.0, new EyeColours()));

            Assert.Throws<ArgumentOutOfRangeException>(() => EyeRasterizer.Render(
                surface, area, trace, EyeComponent.InPhase, 0.05, new EyeColours()));
        }

        [Fact]
        public void MissingArgumentsAreRefused()
        {
            var surface = new PixelSurface(8, 8);
            var area = new PixelRect(0, 0, 8, 8);
            SymbolTrace trace = ConstellationTests.Result(ModulationScheme.Qpsk(), 4);

            Assert.Throws<ArgumentNullException>(() => EyeRasterizer.Render(
                null, area, trace, EyeComponent.InPhase, 2.0, new EyeColours()));

            Assert.Throws<ArgumentNullException>(() => EyeRasterizer.Render(
                surface, area, null, EyeComponent.InPhase, 2.0, new EyeColours()));

            Assert.Throws<ArgumentNullException>(() => EyeRasterizer.Render(
                surface, area, trace, EyeComponent.InPhase, 2.0, null));

            Assert.Throws<ArgumentNullException>(
                () => EyeRasterizer.Extent(null, EyeComponent.InPhase));
        }

        /// <summary>
        /// How far apart the closest pair of levels is, a given offset from the symbol instant.
        /// </summary>
        /// <remarks>
        /// A crude measure of eye opening, and enough: for a two-level signal it is the gap between
        /// the highest negative excursion and the lowest positive one, which is widest at the
        /// decision instant and closes between them.
        /// </remarks>
        private static double Opening(SymbolTrace trace, double offsetSymbols)
        {
            int offset = (int)Math.Round(offsetSymbols * trace.SamplesPerSymbol);

            double lowestPositive = double.MaxValue;
            double highestNegative = double.MinValue;

            foreach (int centre in trace.DecisionSampleIndices)
            {
                int at = centre + offset;

                if (at < 0 || at >= trace.SampleCount)
                {
                    continue;
                }

                double value = trace.SampleAt(at).I;

                if (value > 0.0)
                {
                    lowestPositive = Math.Min(lowestPositive, value);
                }
                else
                {
                    highestNegative = Math.Max(highestNegative, value);
                }
            }

            return lowestPositive == double.MaxValue || highestNegative == double.MinValue
                ? 0.0
                : lowestPositive - highestNegative;
        }
    }

    /// <summary>
    /// <c>REQ-UI-052</c>: the symbol table and error summary are one trace, split top and bottom.
    /// </summary>
    /// <remarks>
    /// The requirement calls this "a structural point, not a styling one", and says that getting it
    /// wrong "means building two traces where the product has one". So these assert the structure.
    /// </remarks>
    public class SymbolTableTraceTests
    {
        private readonly ITestOutputHelper _output;

        /// <summary>Takes xunit's output sink.</summary>
        /// <param name="output">Where the rendered portions are written.</param>
        public SymbolTableTraceTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void ItIsOneTraceWithTwoPortionsRatherThanTwoTraces()
        {
            OnStaThread(() =>
            {
                var panel = new SymbolTablePanel
                {
                    Result = ConstellationTests.Result(ModulationScheme.Qam16(), 64),
                };

                // One element in the trace window, two portions inside it. A second trace would be
                // a second element in the document area, which is what this shape prevents.
                Assert.Equal(2, panel.PortionCount);
                Assert.NotNull(panel.SummaryPortion);
                Assert.NotNull(panel.StreamPortion);
                Assert.Same(panel, panel.SummaryPortion.Parent);
                Assert.Same(panel, panel.StreamPortion.Parent);

                // Both portions are filled from the one result, so selecting the trace selects both.
                Assert.Contains("EVM", panel.SummaryPortion.Text);
                Assert.Contains("0", panel.StreamPortion.Text);

                _output.WriteLine(panel.SummaryPortion.Text);
                _output.WriteLine(panel.StreamPortion.Text.Split('\n')[0]);
            });
        }

        [Fact]
        public void OneResultFillsBothPortionsAndOneFontSetsThem()
        {
            // REQ-UI-052: both portions render in the fixed-width Tabular slot of REQ-UI-080. One
            // call, because a summary in one face above a stream in another would be the two-trace
            // mistake showing through the styling.
            OnStaThread(() =>
            {
                var panel = new SymbolTablePanel
                {
                    Result = ConstellationTests.Result(ModulationScheme.Qam16(), 32),
                };

                panel.ApplyFont(new System.Windows.Media.FontFamily("Courier New"), 11.0);

                Assert.Equal("Courier New", panel.SummaryPortion.FontFamily.Source);
                Assert.Equal("Courier New", panel.StreamPortion.FontFamily.Source);
                Assert.Equal(panel.SummaryPortion.FontSize, panel.StreamPortion.FontSize);
            });
        }

        [Fact]
        public void HexIsOfferedOnlyWhenTheSymbolsAreWideEnough()
        {
            OnStaThread(() =>
            {
                var narrow = new SymbolTablePanel
                {
                    Result = ConstellationTests.Result(ModulationScheme.Qpsk(), 16),
                };

                Assert.False(narrow.IsHexAvailable);
                Assert.Throws<InvalidOperationException>(
                    () => narrow.Format = SymbolTableFormat.Hexadecimal);

                var wide = new SymbolTablePanel
                {
                    Result = ConstellationTests.Result(ModulationScheme.Qam16(), 16),
                };

                Assert.True(wide.IsHexAvailable);

                wide.Format = SymbolTableFormat.Hexadecimal;

                Assert.Equal(SymbolTableFormat.Hexadecimal, wide.Format);

                // And a result too narrow for the format in force falls back rather than throwing:
                // changing trace is not the user asking for hex.
                wide.Result = ConstellationTests.Result(ModulationScheme.Qpsk(), 16);

                Assert.Equal(SymbolTableFormat.Binary, wide.Format);
            });
        }

        [Fact]
        public void AnEmptyPanelSaysSoRatherThanShowingNothing()
        {
            OnStaThread(() =>
            {
                var panel = new SymbolTablePanel();

                Assert.Contains("No demodulated result", panel.SummaryPortion.Text);
                Assert.Equal(string.Empty, panel.StreamPortion.Text);
            });
        }

        [Fact]
        public void APlotDrawsAResultInsteadOfASpectrum()
        {
            OnStaThread(() =>
            {
                var plot = new TracePlot();

                plot.Measure(new Size(800.0, 600.0));
                plot.Arrange(new Rect(0.0, 0.0, 800.0, 600.0));

                Assert.False(plot.IsShowingResult);

                SymbolTrace trace = ConstellationTests.Result(ModulationScheme.Qam16(), 200);

                plot.Result = trace;
                plot.ResultKind = ResultTraceKind.Constellation;

                Assert.True(plot.IsShowingResult);
                Assert.Equal(trace.SymbolCount, plot.LastConstellationRender.SymbolsDrawn);
                Assert.Equal(0, plot.LastConstellationRender.SegmentsDrawn);

                plot.ResultKind = ResultTraceKind.IqVector;

                Assert.Equal(trace.SymbolCount - 1, plot.LastConstellationRender.SegmentsDrawn);

                plot.ResultKind = ResultTraceKind.Eye;

                Assert.Equal(trace.SymbolCount, plot.LastEyeRender.Folds);
                Assert.Equal(3, plot.LastEyeRender.ReferenceLines);

                // And the eye length is held to REQ-UI-051's range at the control too.
                Assert.Throws<ArgumentOutOfRangeException>(() => plot.EyeLengthSymbols = 11.0);

                plot.EyeLengthSymbols = 4.0;

                Assert.Equal(5, plot.LastEyeRender.ReferenceLines);
            });
        }

        private static void OnStaThread(Action action)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo failure = null;

            var thread = new System.Threading.Thread(() =>
            {
                try
                {
                    action();
                }
                catch (Exception e)
                {
                    failure = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e);
                }
            });

            thread.SetApartmentState(System.Threading.ApartmentState.STA);
            thread.Start();
            thread.Join();

            if (failure != null)
            {
                failure.Throw();
            }
        }
    }
}
