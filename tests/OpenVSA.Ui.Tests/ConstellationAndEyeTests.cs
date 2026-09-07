using System;
using System.Collections.Generic;
using System.Windows;
using System.Linq;
using OpenVSA.Demod.Results;
using OpenVSA.Synthesis;
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
    /// <c>REQ-DEM-081</c>: how the eye is built, rather than how it is laid out.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Why these live beside <c>REQ-UI-051</c>'s.</strong> <c>REQ-DEM-081</c>'s own
    /// criterion ends by deferring to it — "the rendered eye satisfies <c>REQ-UI-051</c>'s centring
    /// and reference-line criteria" — and <c>REQ-DEM-080</c> settled that the fold belongs to the
    /// display and what a result owes it is the period to fold on. So construction and layout are
    /// the same code, and these are the clauses layout does not cover: that every symbol in the
    /// Result Length contributes a fold, and what persistence shading may and may not change.
    /// </para>
    /// </remarks>
    public class EyeConstructionTests
    {
        private readonly ITestOutputHelper _output;

        /// <summary>Takes xunit's output sink.</summary>
        /// <param name="output">Where measured figures are written.</param>
        public EyeConstructionTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void EveryFoldTheResultLengthImpliesIsBuiltAtEveryEyeLength()
        {
            // "The eye is built from the measured waveform across the whole Result Length, folded
            // on the symbol clock — the trace count equals the number of folds the Result Length
            // and eye length imply, so a partial build fails."
            SymbolTrace trace = ConstellationTests.Result(ModulationScheme.Qam16(), 200);

            // The implied count comes from the result, not from the render, so the assertion below
            // is against something other than itself.
            Assert.Equal(trace.SymbolCount, EyeRasterizer.ExpectedFolds(trace));

            var area = new PixelRect(10, 10, 300, 220);

            foreach (double length in new[] { 0.1, 0.5, 2.0, 6.0, 10.0 })
            {
                var surface = new PixelSurface(320, 240);

                EyeRender drawn = EyeRasterizer.Render(
                    surface, area, trace, EyeComponent.InPhase, length, new EyeColours());

                _output.WriteLine(length.ToString("0.0") + " symbols: " + drawn);

                Assert.Equal(EyeRasterizer.ExpectedFolds(trace), drawn.Folds);
            }
        }

        [Fact]
        public void AFoldClippedByTheCaptureIsReportedRatherThanDropped()
        {
            // The way to pass the count above while still building a partial eye is to drop the
            // folds whose window runs off the end of the capture. So they are built, counted, and
            // said to be clipped separately.
            //
            // Built by hand rather than generated: SyntheticSymbolSource leads and trails every
            // burst by PulseSpanSymbols symbols, so its first decision instant has six symbols of
            // waveform in front of it and even a ten-symbol eye never reaches the edge. A capture
            // that begins at the first symbol — which is what a Result Length aligned to the burst
            // gives you — is the case this clause is about, and it has to be constructed.
            const int PerSymbol = 8;
            const int Symbols = 10;

            var samples = new float[Symbols * PerSymbol * 2];
            var decisions = new List<int>();
            var symbols = new List<int>();
            var points = new List<ConstellationPoint>();

            for (int symbol = 0; symbol < Symbols; symbol++)
            {
                decisions.Add(symbol * PerSymbol);
                symbols.Add(symbol % 2);
                points.Add(new ConstellationPoint(symbol % 2 == 0 ? 1.0 : -1.0, 0.0));

                for (int at = 0; at < PerSymbol; at++)
                {
                    samples[((symbol * PerSymbol) + at) * 2] = symbol % 2 == 0 ? 1.0f : -1.0f;
                }
            }

            var trace = new SymbolTrace(
                "BPSK", 1, 2, symbols, points, points, decisions, samples, PerSymbol, 1.0e6);

            var area = new PixelRect(10, 10, 300, 220);

            EyeRender shortEye = EyeRasterizer.Render(
                new PixelSurface(320, 240), area, trace, EyeComponent.InPhase, 0.5,
                new EyeColours());

            EyeRender longEye = EyeRasterizer.Render(
                new PixelSurface(320, 240), area, trace, EyeComponent.InPhase, 4.0,
                new EyeColours());

            _output.WriteLine("half a symbol: " + shortEye);
            _output.WriteLine("four symbols:  " + longEye);

            // Every symbol of the Result Length is a fold at both lengths — that is the clause
            // above, and it must not be bought back by dropping the clipped ones.
            Assert.Equal(EyeRasterizer.ExpectedFolds(trace), shortEye.Folds);
            Assert.Equal(EyeRasterizer.ExpectedFolds(trace), longEye.Folds);

            // The capture is not symmetric about the decisions and the counts show it: the first
            // instant is sample 0 with nothing in front of it, while the last is sample 72 with
            // seven samples behind it. So a half-symbol eye, reaching two samples either side,
            // clips only the leading fold. A four-symbol eye reaches two symbols, which is the
            // first two instants at the front and the last two at the back.
            Assert.Equal(1, shortEye.TruncatedFolds);
            Assert.Equal(4, longEye.TruncatedFolds);

            Assert.True(
                longEye.TruncatedFolds < longEye.Folds,
                "Every fold was clipped, so the capture is too short for this to say anything.");
        }

        [Fact]
        public void TheEyeLengthDefaultsToTwoSymbols()
        {
            // "Eye length defaults to 2 symbols and is configurable over the REQ-UI-051 range."
            // The range is REQ-UI-051's and asserted there; the default is this requirement's.
            Assert.Equal(2.0, EyeRasterizer.DefaultLengthSymbols);
            Assert.True(EyeRasterizer.IsLengthAllowed(EyeRasterizer.DefaultLengthSymbols));
        }

        [Fact]
        public void PersistenceMakesAFrequentlyTraversedPathDenserThanARareOne()
        {
            // "Persistence shading, when on, makes frequently traversed paths visibly denser than
            // rare ones." Read off the rendered frame: with the shading on the eye is drawn in a
            // spread of brightnesses, and with it off in exactly one.
            SymbolTrace trace = ConstellationTests.Result(ModulationScheme.Qam16(), 400);

            var area = new PixelRect(10, 10, 300, 220);

            var plain = new PixelSurface(320, 240);
            var shaded = new PixelSurface(320, 240);

            var colours = new EyeColours();

            EyeRasterizer.Render(plain, area, trace, EyeComponent.InPhase, 2.0, colours);

            EyeRender drawn = EyeRasterizer.Render(
                shaded, area, trace, EyeComponent.InPhase, 2.0, colours, 0.0, true);

            _output.WriteLine("shaded: " + drawn);

            Assert.True(
                drawn.PeakTraversals > 1,
                "No cell was crossed more than once, so there is no density to shade.");

            var plainLevels = new HashSet<double>();
            var shadedLevels = new HashSet<double>();

            double brightest = 0.0;
            double dimmest = double.MaxValue;

            foreach (Cell cell in Eye(plain, shaded, area, colours))
            {
                plainLevels.Add(cell.PlainLuminance);
                shadedLevels.Add(cell.ShadedLuminance);

                brightest = Math.Max(brightest, cell.ShadedLuminance);
                dimmest = Math.Min(dimmest, cell.ShadedLuminance);
            }

            _output.WriteLine(
                "unshaded brightnesses " + plainLevels.Count + ", shaded " + shadedLevels.Count +
                ", from " + dimmest.ToString("0.0") + " to " + brightest.ToString("0.0"));

            Assert.Single(plainLevels);

            Assert.True(
                shadedLevels.Count > 8,
                "The shading produced " + shadedLevels.Count + " brightnesses, which is not a " +
                "density scale.");

            Assert.True(
                brightest > dimmest * 2.0,
                "The densest path is not visibly denser than the rarest: " + brightest + " against " +
                dimmest + ".");
        }

        [Fact]
        public void MoreTraversalsIsNeverADimmerCell()
        {
            // The shading has to be monotonic or "denser" means nothing: a cell crossed more often
            // than another must never be drawn dimmer than it. Asserted on the mapping rather than
            // on a frame, because a frame only ever exercises the counts that signal happened to
            // produce.
            var colours = new EyeColours();

            double previous = -1.0;

            for (int traversals = 1; traversals <= 1000; traversals++)
            {
                double luminance = Luminance(colours.ForTraversals(traversals, 1000));

                Assert.True(
                    luminance >= previous,
                    traversals + " traversals is dimmer than " + (traversals - 1) + ": " +
                    luminance + " against " + previous + ".");

                previous = luminance;
            }

            // And a cell crossed once is dim but not absent — an eye whose rare paths are invisible
            // has thrown away the outliers that are the reason to look at one.
            PlotColor rare = colours.ForTraversals(1, 1000);

            Assert.True(
                Luminance(rare) > 0.0,
                "A path taken once was drawn as nothing, so the outliers are gone.");

            Assert.True(
                Luminance(rare) < Luminance(colours.Trace),
                "A path taken once is drawn as brightly as one taken a thousand times.");
        }

        [Fact]
        public void TurningPersistenceOffLeavesTheEyesGeometryUnchanged()
        {
            // "...and turning it off leaves the eye's geometry unchanged." Geometry is which cells
            // carry ink, so the two frames must ink exactly the same set and differ only in what
            // colour they put there.
            SymbolTrace trace = ConstellationTests.Result(ModulationScheme.Qam16(), 400);

            var area = new PixelRect(10, 10, 300, 220);

            var plain = new PixelSurface(320, 240);
            var shaded = new PixelSurface(320, 240);

            var colours = new EyeColours();

            EyeRender without = EyeRasterizer.Render(
                plain, area, trace, EyeComponent.InPhase, 2.0, colours, 0.0, false);

            EyeRender with = EyeRasterizer.Render(
                shaded, area, trace, EyeComponent.InPhase, 2.0, colours, 0.0, true);

            Assert.Equal(without.Folds, with.Folds);
            Assert.Equal(without.ReferenceLines, with.ReferenceLines);
            Assert.Equal(without.TruncatedFolds, with.TruncatedFolds);

            int inked = 0;
            int differed = 0;

            for (int y = area.Y; y < area.Bottom; y++)
            {
                for (int x = area.X; x < area.Right; x++)
                {
                    PlotColor a = plain.GetPixel(x, y);
                    PlotColor b = shaded.GetPixel(x, y);

                    bool aInked = a.R != 0 || a.G != 0 || a.B != 0;
                    bool bInked = b.R != 0 || b.G != 0 || b.B != 0;

                    Assert.True(
                        aInked == bInked,
                        "Persistence changed the geometry at " + x + "," + y + ": inked " + aInked +
                        " without it and " + bInked + " with it.");

                    if (!aInked)
                    {
                        continue;
                    }

                    inked++;

                    if (!a.Equals(b))
                    {
                        differed++;
                    }
                }
            }

            _output.WriteLine(
                inked + " inked cells, " + differed + " of them a different colour with the " +
                "shading on");

            Assert.True(inked > 0, "Nothing was drawn, so the comparison says nothing.");

            // The frames are the same shape, so the test has to show the shading did something at
            // all — otherwise "unchanged geometry" would pass for a persistence switch that is not
            // wired up.
            Assert.True(
                differed > 0,
                "The shading changed no colours, so it is not doing anything.");
        }

        private readonly struct Cell
        {
            internal Cell(double plain, double shaded)
            {
                PlainLuminance = plain;
                ShadedLuminance = shaded;
            }

            internal double PlainLuminance { get; }

            internal double ShadedLuminance { get; }
        }

        /// <summary>The cells the eye itself inked, excluding the reference lines under it.</summary>
        private static IEnumerable<Cell> Eye(
            PixelSurface plain, PixelSurface shaded, PixelRect area, EyeColours colours)
        {
            for (int y = area.Y; y < area.Bottom; y++)
            {
                for (int x = area.X; x < area.Right; x++)
                {
                    PlotColor unshaded = plain.GetPixel(x, y);

                    // The reference lines are drawn first and in their own colour, so a cell still
                    // carrying that colour is line rather than waveform.
                    if (!unshaded.Equals(colours.Trace))
                    {
                        continue;
                    }

                    yield return new Cell(Luminance(unshaded), Luminance(shaded.GetPixel(x, y)));
                }
            }
        }

        private static double Luminance(PlotColor colour) =>
            (0.2126 * colour.R) + (0.7152 * colour.G) + (0.0722 * colour.B);
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
