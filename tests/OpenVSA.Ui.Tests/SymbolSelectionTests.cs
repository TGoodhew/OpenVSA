using System;
using System.Collections.Generic;
using System.Windows;
using OpenVSA.Demod.Results;
using OpenVSA.Synthesis;
using OpenVSA.TestHarness.Synthesis;
using OpenVSA.Ui.Rendering;
using Xunit;
using Xunit.Abstractions;

namespace OpenVSA.Ui.Tests
{
    /// <summary>
    /// <c>REQ-DEM-083</c>: selecting a symbol in one display selects it in the others.
    /// </summary>
    /// <remarks>
    /// The criterion insists on a signal "in which one symbol is displaced so the correct point is
    /// identifiable, which an off-by-one selection fails", so the fixture throughout is a burst
    /// with one deliberately displaced symbol. A test that selected a symbol in a clean signal
    /// would pass for every index.
    /// </remarks>
    public class SymbolSelectionTests
    {
        /// <summary>The symbol displaced in every fixture here.</summary>
        private const int Displaced = 37;

        private readonly ITestOutputHelper _output;

        /// <summary>Takes xunit's output sink.</summary>
        /// <param name="output">Where measured figures are written.</param>
        public SymbolSelectionTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void TheConstellationRingsTheSelectedSymbolAndNotItsNeighbour()
        {
            // "Selecting symbol k in the table highlights the constellation point ... for symbol k
            // specifically ... which an off-by-one selection fails." The displaced symbol is the
            // one that can be told apart, so the ring must land on it and not on 36 or 38.
            SymbolTrace trace = Displacement();

            var area = new PixelRect(0, 0, 301, 301);
            var colours = new ConstellationColours();

            ConstellationPoint chosen = trace.Measured[Displaced];

            int x = ConstellationRasterizer.XFor(chosen.I, 2.0, area);
            int y = ConstellationRasterizer.YFor(chosen.Q, 2.0, area);

            var surface = new PixelSurface(301, 301);

            ConstellationRender drawn = ConstellationRasterizer.Render(
                surface, area, trace, colours,
                new ConstellationOptions { Scale = 2.0, Selected = Displaced });

            _output.WriteLine(
                "symbol " + Displaced + " drawn at " + x + "," + y + "; " + drawn);

            Assert.True(drawn.SelectionDrawn);

            // The ring is centred on the symbol: its own cell is not the ring, and the cell a
            // radius away is.
            Assert.Equal(
                colours.Selection,
                surface.GetPixel(x + ConstellationRasterizer.SelectionRadius, y));

            Assert.NotEqual(colours.Selection, surface.GetPixel(x, y));

            // And selecting the neighbour puts the ring somewhere else, which is what makes this
            // an off-by-one check rather than a "something was drawn" check.
            var neighbouring = new PixelSurface(301, 301);

            ConstellationRasterizer.Render(
                neighbouring, area, trace, colours,
                new ConstellationOptions { Scale = 2.0, Selected = Displaced - 1 });

            Assert.NotEqual(
                colours.Selection,
                neighbouring.GetPixel(x + ConstellationRasterizer.SelectionRadius, y));
        }

        [Fact]
        public void PointingAtTheDisplacedSymbolSelectsItAndNotItsNeighbour()
        {
            // The other direction: "and vice versa". Pointing where the displaced symbol was drawn
            // must name that symbol, which only a signal with a distinguishable symbol can test.
            SymbolTrace trace = Displacement();

            var area = new PixelRect(0, 0, 301, 301);

            ConstellationPoint chosen = trace.Measured[Displaced];

            int x = ConstellationRasterizer.XFor(chosen.I, 2.0, area);
            int y = ConstellationRasterizer.YFor(chosen.Q, 2.0, area);

            int found = ConstellationRasterizer.SymbolNear(trace, area, x, y, 2.0);

            _output.WriteLine("pointing at " + x + "," + y + " found symbol " + found);

            Assert.Equal(Displaced, found);

            // Pointing at nothing selects nothing rather than the nearest thing anywhere.
            Assert.Equal(
                SymbolSelection.None,
                ConstellationRasterizer.SymbolNear(trace, area, 0, 0, 2.0));
        }

        [Fact]
        public void TheEyeDrawsTheSelectedSymbolsOwnFold()
        {
            SymbolTrace trace = Displacement();

            var area = new PixelRect(0, 0, 300, 220);
            var colours = new EyeColours();

            var surface = new PixelSurface(300, 220);

            EyeRender drawn = EyeRasterizer.Render(
                surface, area, trace, EyeComponent.InPhase, 2.0, colours, 0.0, false, Displaced);

            _output.WriteLine(drawn.ToString());

            Assert.True(drawn.SelectionDrawn);

            // The highlighted fold passes through the selected symbol's own value at the centre of
            // the display, which is where its decision instant folds to. A fold one symbol out
            // would cross the centre line somewhere else.
            int centre = EyeRasterizer.XForSymbolOffset(0.0, 2.0, area);
            double extent = EyeRasterizer.Extent(trace, EyeComponent.InPhase);

            int at = EyeRasterizer.YForValue(
                trace.SampleAt(trace.DecisionSampleIndices[Displaced]).I, extent, area);

            int wrong = EyeRasterizer.YForValue(
                trace.SampleAt(trace.DecisionSampleIndices[Displaced - 1]).I, extent, area);

            _output.WriteLine(
                "centre column " + centre + ", selected symbol at row " + at + ", its neighbour " +
                wrong);

            Assert.Equal(colours.Selection, surface.GetPixel(centre, at));

            // The neighbour's row on the centre column is not highlighted -- unless the two happen
            // to fold to the same row, in which case this comparison says nothing and is skipped
            // rather than asserted.
            if (wrong != at)
            {
                Assert.NotEqual(colours.Selection, surface.GetPixel(centre, wrong));
            }
        }

        [Fact]
        public void TheTableFindsTheSelectedSymbolsOwnCharacters()
        {
            // The table's equivalent of "the corresponding point": which characters spell symbol k.
            SymbolTrace trace = Displacement();

            SymbolPosition position = SymbolTable.Locate(
                trace.Symbols, Displaced, trace.BitsPerSymbol, SymbolTableFormat.Binary, 32);

            _output.WriteLine("symbol " + Displaced + " is at " + position);

            Assert.True(position.IsFound);
            Assert.Equal(trace.BitsPerSymbol, position.Length);
            Assert.Equal(Displaced * trace.BitsPerSymbol, position.StartCharacter);

            // And the round trip closes: the character it names belongs to that symbol and to no
            // other, which is the off-by-one check the criterion asks for.
            Assert.Equal(
                Displaced,
                SymbolTable.SymbolAt(
                    trace.Symbols, position.Row, position.Column, trace.BitsPerSymbol,
                    SymbolTableFormat.Binary, 32));

            SymbolPosition neighbour = SymbolTable.Locate(
                trace.Symbols, Displaced - 1, trace.BitsPerSymbol, SymbolTableFormat.Binary, 32);

            Assert.NotEqual(neighbour.StartCharacter, position.StartCharacter);
        }

        [Fact]
        public void EveryCharacterOfTheTableRoundTripsToItsOwnSymbol()
        {
            // The two directions have to agree everywhere, not at one index. Walked over every
            // symbol of a result, in both formats, including the group spaces and the gutter -
            // which belong to no symbol and must say so.
            foreach (SymbolTableFormat format in
                new[] { SymbolTableFormat.Binary, SymbolTableFormat.Hexadecimal })
            {
                SymbolTrace trace = Displacement(ModulationScheme.Qam16());

                for (int symbol = 0; symbol < trace.SymbolCount; symbol++)
                {
                    SymbolPosition at = SymbolTable.Locate(
                        trace.Symbols, symbol, trace.BitsPerSymbol, format, 32);

                    Assert.True(at.IsFound, format + ": symbol " + symbol + " was not located.");

                    Assert.Equal(
                        symbol,
                        SymbolTable.SymbolAt(
                            trace.Symbols, at.Row, at.Column, trace.BitsPerSymbol, format, 32));
                }

                // The gutter and the separator belong to no symbol.
                Assert.Equal(
                    SymbolSelection.None,
                    SymbolTable.SymbolAt(trace.Symbols, 0, 0, trace.BitsPerSymbol, format, 32));

                Assert.Equal(
                    SymbolSelection.None,
                    SymbolTable.SymbolAt(
                        trace.Symbols, 0, SymbolTable.GutterWidth, trace.BitsPerSymbol, format, 32));

                // And so does the space between two groups of eight.
                Assert.Equal(
                    SymbolSelection.None,
                    SymbolTable.SymbolAt(
                        trace.Symbols, 0, SymbolTable.GutterWidth + 1 + SymbolTable.GroupSize,
                        trace.BitsPerSymbol, format, 32));
            }
        }

        [Fact]
        public void AHexSymbolWiderThanOneDigitStillLocatesItself()
        {
            // The case that breaks a "one character per symbol" assumption. SymbolTable.Spell
            // writes a symbol with ToString("X"), so a six-bit symbol is one digit up to 15 and two
            // above it: symbol k does not start at character k, and where it does start depends on
            // the values of the symbols before it. 16-QAM cannot show this — four bits always spell
            // one digit — so 64-QAM is the fixture.
            SymbolTrace trace = Displacement(ModulationScheme.Qam64());

            Assert.Equal(6, trace.BitsPerSymbol);

            string stream = SymbolTable.Spell(
                trace.Symbols, trace.BitsPerSymbol, SymbolTableFormat.Hexadecimal);

            _output.WriteLine(
                trace.SymbolCount + " symbols spell " + stream.Length + " hex characters");

            Assert.True(
                stream.Length > trace.SymbolCount,
                "Every symbol spelled to one digit, so this fixture does not exercise the case.");

            int wide = 0;

            for (int symbol = 0; symbol < trace.SymbolCount; symbol++)
            {
                SymbolPosition at = SymbolTable.Locate(
                    trace.Symbols, symbol, trace.BitsPerSymbol, SymbolTableFormat.Hexadecimal, 32);

                Assert.True(at.IsFound);

                if (at.Length > 1)
                {
                    wide++;
                }

                // Every character of the symbol names that symbol, not only its first.
                for (int character = 0; character < at.Length; character++)
                {
                    int offset = at.StartCharacter + character;

                    Assert.Equal(
                        symbol,
                        SymbolTable.SymbolAt(
                            trace.Symbols,
                            offset / 32,
                            (SymbolTable.GutterWidth + 1) + (offset % 32) +
                                ((offset % 32) / SymbolTable.GroupSize),
                            trace.BitsPerSymbol,
                            SymbolTableFormat.Hexadecimal,
                            32));
                }
            }

            _output.WriteLine(wide + " symbols spelled to two digits");

            Assert.True(wide > 0, "No symbol was wide, so the case is not exercised.");
        }

        [Fact]
        public void ASelectionRoundTripBetweenTwoSurfacesSettles()
        {
            // "Selection propagates in both directions and settles: selecting in the constellation
            // highlights the table row without re-triggering a further selection." Two subscribers
            // that echo what they are told are the arrangement that would loop for ever if
            // selecting the current symbol raised an event.
            var selection = new SymbolSelection();

            int notified = 0;

            selection.Changed += (sender, e) =>
            {
                notified++;
                selection.Select(selection.Selected);
            };

            selection.Changed += (sender, e) => selection.Select(selection.Selected);

            selection.Select(Displaced);

            _output.WriteLine(
                notified + " notifications, " + selection.Changes + " changes, selected " +
                selection.Selected);

            Assert.Equal(Displaced, selection.Selected);
            Assert.Equal(1, notified);
            Assert.Equal(1, selection.Changes);

            // Selecting it again is not a change at all.
            selection.Select(Displaced);

            Assert.Equal(1, selection.Changes);
        }

        [Fact]
        public void ASelectionSurvivesAMeasurementUpdateAndClearsWhenItCannot()
        {
            // "Selection survives a measurement update if the symbol still exists, and clears
            // cleanly if it does not."
            var selection = new SymbolSelection();

            selection.Select(Displaced);

            int changes = selection.Changes;

            Assert.True(selection.Update(Displacement()));
            Assert.Equal(Displaced, selection.Selected);
            Assert.Equal(changes, selection.Changes);

            // A shorter result: symbol 37 is gone, so the selection goes with it — once, and
            // reported as a change, because something did change.
            Assert.False(selection.Update(Result(ModulationScheme.Qpsk(), 20)));

            Assert.Equal(SymbolSelection.None, selection.Selected);
            Assert.False(selection.HasSelection);
            Assert.Equal(changes + 1, selection.Changes);

            // And no result at all clears it too.
            selection.Select(3);

            Assert.False(selection.Update(null));
            Assert.Equal(SymbolSelection.None, selection.Selected);
        }

        [Fact]
        public void ThePlotAndThePanelShowOneSelectionBetweenThem()
        {
            OnStaThread(() =>
            {
                var selection = new SymbolSelection();
                SymbolTrace trace = Displacement();

                var plot = new TracePlot();

                plot.Measure(new Size(800.0, 600.0));
                plot.Arrange(new Rect(0.0, 0.0, 800.0, 600.0));

                plot.Selection = selection;
                plot.Result = trace;
                plot.ResultKind = ResultTraceKind.Constellation;

                var panel = new SymbolTablePanel { Selection = selection, Result = trace };

                Assert.False(panel.SelectionPosition.IsFound);
                Assert.False(plot.LastConstellationRender.SelectionDrawn);

                // Selecting on the plot reaches the panel, and does so once.
                int found = plot.SelectAt(
                    ConstellationRasterizer.XFor(
                        trace.Measured[Displaced].I,
                        ConstellationRasterizer.Extent(trace),
                        plot.GraticuleArea),
                    ConstellationRasterizer.YFor(
                        trace.Measured[Displaced].Q,
                        ConstellationRasterizer.Extent(trace),
                        plot.GraticuleArea));

                Assert.Equal(Displaced, found);
                Assert.Equal(Displaced, selection.Selected);
                Assert.Equal(1, selection.Changes);

                Assert.True(plot.LastConstellationRender.SelectionDrawn);
                Assert.True(panel.SelectionPosition.IsFound);
                Assert.Equal(Displaced * trace.BitsPerSymbol, panel.SelectionPosition.StartCharacter);

                // And the panel's own direction reaches the plot.
                SymbolPosition other = SymbolTable.Locate(
                    trace.Symbols, 12, trace.BitsPerSymbol, SymbolTableFormat.Binary, 32);

                Assert.Equal(12, panel.SelectAt(other.Row, other.Column));
                Assert.Equal(12, selection.Selected);
                Assert.Equal(2, selection.Changes);

                // A shorter measurement clears it on both.
                plot.Result = Result(ModulationScheme.Qpsk(), 8);

                Assert.False(selection.HasSelection);
                Assert.False(plot.LastConstellationRender.SelectionDrawn);
            });
        }

        /// <summary>A burst with one symbol displaced so that it can be told from its neighbours.</summary>
        private static SymbolTrace Displacement(ModulationScheme scheme = null) =>
            new SyntheticSymbolSource
            {
                Scheme = scheme ?? ModulationScheme.Qpsk(),
                SignalToNoiseDb = 30.0,
                DisplacedSymbolIndex = Displaced,
                Displacement = 0.4,
            }.Generate(120).ToSymbolTrace();

        private static SymbolTrace Result(ModulationScheme scheme, int symbols) =>
            new SyntheticSymbolSource { Scheme = scheme, SignalToNoiseDb = 28.0 }
                .Generate(symbols)
                .ToSymbolTrace();

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
