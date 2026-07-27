using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using OpenVSA.Dsp.Spectrum;
using OpenVSA.Dsp.Windowing;
using OpenVSA.Ui.Rendering;
using Xunit;
using Xunit.Abstractions;

namespace OpenVSA.Ui.Tests
{
    /// <summary>
    /// <c>REQ-UI-015</c> (default background and the print option), <c>REQ-UI-020</c> (lettering
    /// and the twenty-entry colour table), <c>REQ-UI-021</c> (a trace and its annotation share one
    /// colour) and <c>REQ-UI-022</c> (the themeable element set).
    /// </summary>
    public class TraceColourTests
    {
        private readonly ITestOutputHelper _output;

        public TraceColourTests(ITestOutputHelper output)
        {
            _output = output;
        }

        // ---- REQ-UI-020: lettering and the colour table ----------------------------------------

        [Fact]
        public void TheColourTableHoldsExactlyTwenty()
        {
            Assert.Equal(20, TraceColours.TableSize);
            Assert.Equal(20, TraceColours.Table.Count);
        }

        [Fact]
        public void TheTwentyFirstTraceReusesIndexZero()
        {
            // Not an extended table and not a failure: refusing the twenty-first trace would be
            // refusing a measurement over a display detail.
            Assert.Equal(TraceColours.ForIndex(0), TraceColours.ForIndex(20));
            Assert.Equal(TraceColours.ForIndex(3), TraceColours.ForIndex(23));
            Assert.Equal(TraceColours.ForIndex(19), TraceColours.ForIndex(39));
        }

        [Fact]
        public void EveryColourInTheTableIsDistinct()
        {
            // Twenty entries of which two are the same is nineteen entries and a bug.
            var seen = new HashSet<uint>();

            foreach (PlotColor colour in TraceColours.Table)
            {
                uint packed = (uint)((colour.R << 16) | (colour.G << 8) | colour.B);

                Assert.True(seen.Add(packed), "the table repeats a colour.");
            }
        }

        [Fact]
        public void ConsecutiveTracesAreNotNeighbouringHues()
        {
            // A two-trace overlay is the common case, and yellow against amber is not legible.
            for (int i = 0; i + 1 < TraceColours.TableSize; i++)
            {
                PlotColor a = TraceColours.ForIndex(i);
                PlotColor b = TraceColours.ForIndex(i + 1);

                int distance = Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B);

                Assert.True(
                    distance > 80,
                    "traces " + i + " and " + (i + 1) + " differ by only " + distance + ".");
            }
        }

        [Fact]
        public void TracesAreLetteredAndTheLetteringContinuesPastZ()
        {
            // "In a defined way rather than colliding or truncating." Spreadsheet columns are the
            // definition every user already knows.
            Assert.Equal("A", TraceColours.LetterAt(0));
            Assert.Equal("Z", TraceColours.LetterAt(25));
            Assert.Equal("AA", TraceColours.LetterAt(26));
            Assert.Equal("AB", TraceColours.LetterAt(27));
            Assert.Equal("AZ", TraceColours.LetterAt(51));
            Assert.Equal("BA", TraceColours.LetterAt(52));
            Assert.Equal("ZZ", TraceColours.LetterAt(701));
            Assert.Equal("AAA", TraceColours.LetterAt(702));
        }

        [Fact]
        public void NoTwoIndicesShareAnIdentifier()
        {
            // The plain base-26 conversion gives "A" for both 0 and 26 - a collision at exactly the
            // boundary the criterion names.
            var seen = new HashSet<string>();

            for (int i = 0; i < 1000; i++)
            {
                Assert.True(seen.Add(TraceColours.LetterAt(i)), "index " + i + " repeats a letter.");
            }
        }

        [Fact]
        public void AnIdentifierRoundTripsBackToItsIndex()
        {
            for (int i = 0; i < 800; i++)
            {
                Assert.Equal(i, TraceColours.IndexOf(TraceColours.LetterAt(i)));
            }
        }

        [Fact]
        public void NoTraceIdentifierIsABareNumber()
        {
            // The criterion: "a test that fails if any trace identifier renders as a bare number".
            // Trace numbers would collide with marker numbering, which is why they are lettered.
            for (int i = 0; i < 200; i++)
            {
                string letters = TraceColours.LetterAt(i);

                Assert.False(string.IsNullOrEmpty(letters));

                foreach (char letter in letters)
                {
                    Assert.False(char.IsDigit(letter), "'" + letters + "' contains a digit.");
                    Assert.True(letter >= 'A' && letter <= 'Z');
                }
            }
        }

        [Fact]
        public void SomethingThatIsNotAnIdentifierIsRefused()
        {
            Assert.Equal(-1, TraceColours.IndexOf("3"));
            Assert.Equal(-1, TraceColours.IndexOf("a"));
            Assert.Equal(-1, TraceColours.IndexOf(string.Empty));
            Assert.Equal(-1, TraceColours.IndexOf(null));

            Assert.Throws<ArgumentException>(() => TraceColours.ForTrace("3"));
            Assert.Throws<ArgumentOutOfRangeException>(() => TraceColours.ForIndex(-1));
            Assert.Throws<ArgumentOutOfRangeException>(() => TraceColours.LetterAt(-1));
        }

        // ---- REQ-UI-021: one colour drives the line and its annotation --------------------------

        [Fact]
        public void ATraceAndItsAnnotationShareOneColour()
        {
            OnStaThread(() =>
            {
                TracePlot plot = Drawn();

                foreach (char letter in new[] { 'A', 'B', 'C', 'D' })
                {
                    PlotColor colour = TraceColours.ForTrace(letter);

                    plot.Palette = plot.Palette.WithTrace(colour);
                    plot.UpdateLayout();

                    AssertAnnotationMatchesTrace(plot, colour);
                }
            });
        }

        [Fact]
        public void TheyStayEqualAfterThePresetThatHistoricallyBrokeIt()
        {
            // The 89400 defect this requirement quotes is exactly the post-preset case: "Trace A
            // shows wrong annotation color after preset."
            OnStaThread(() =>
            {
                TracePlot plot = Drawn();

                plot.Palette = plot.Palette.WithTrace(TraceColours.ForTrace('C'));
                plot.UpdateLayout();

                // Preset: back to the default palette.
                plot.Palette = PlotPalette.Dark;
                plot.UpdateLayout();

                AssertAnnotationMatchesTrace(plot, PlotPalette.Dark.Trace);

                // And a colour chosen after the preset still takes both.
                plot.Palette = plot.Palette.WithTrace(TraceColours.ForTrace('E'));
                plot.UpdateLayout();

                AssertAnnotationMatchesTrace(plot, TraceColours.ForTrace('E'));
            });
        }

        [Fact]
        public void TheyStayEqualAcrossAThemeChange()
        {
            OnStaThread(() =>
            {
                TracePlot plot = Drawn();

                plot.Palette = PlotPalette.Light;
                plot.UpdateLayout();

                AssertAnnotationMatchesTrace(plot, PlotPalette.Light.Trace);

                plot.Palette = PlotPalette.Dark;
                plot.UpdateLayout();

                AssertAnnotationMatchesTrace(plot, PlotPalette.Dark.Trace);
            });
        }

        // ---- REQ-UI-015: default background, and the print option -------------------------------

        [Fact]
        public void TheDefaultTraceBackgroundIsVeryDark()
        {
            // The requirement infers black from the Print dialog's "Force white background" option,
            // offered because "large areas of black do not print well".
            Assert.True(
                PlotPalette.Luminance(PlotPalette.Dark.TraceBackground) < 0.1,
                "the default background has a luminance of " +
                PlotPalette.Luminance(PlotPalette.Dark.TraceBackground) + ".");
        }

        [Fact]
        public void ForPrintingWhitensTheBackgroundAndKeepsEveryColourLegible()
        {
            // "Very light colors will print black so they can be seen" - the half a plain
            // background swap misses.
            PlotPalette printed = PlotPalette.Dark.ForPrinting();

            Assert.Equal(0xFF, printed.TraceBackground.R);
            Assert.Equal(0xFF, printed.TraceBackground.G);
            Assert.Equal(0xFF, printed.TraceBackground.B);

            foreach (PlotColor ink in new[]
            {
                printed.Trace, printed.Annotation, printed.Grid, printed.Indicator,
                printed.SelectedMarker, printed.NotSelectedMarker,
            })
            {
                Assert.True(
                    PlotPalette.IsLegibleOnWhite(ink),
                    "an ink of luminance " + PlotPalette.Luminance(ink) + " is invisible on white.");
            }
        }

        [Fact]
        public void EveryTraceColourStaysDistinguishableWhenPrinted()
        {
            // Replacing every light colour with black, as the reference product's own note
            // literally says, would make a four-trace overlay unreadable in exactly the case
            // somebody prints one.
            var printed = new List<PlotColor>();

            for (int i = 0; i < TraceColours.TableSize; i++)
            {
                printed.Add(PlotPalette.Darkened(TraceColours.ForIndex(i)));
            }

            var seen = new HashSet<uint>();

            foreach (PlotColor colour in printed)
            {
                Assert.True(PlotPalette.IsLegibleOnWhite(colour));
                Assert.True(
                    seen.Add((uint)((colour.R << 16) | (colour.G << 8) | colour.B)),
                    "two trace colours print the same.");
            }
        }

        [Fact]
        public void AColourAlreadyDarkEnoughIsLeftExactlyAsItIs()
        {
            // A user who chose a dark trace colour gets it printed, not a darkened version of it.
            var navy = new PlotColor(0x00, 0x20, 0x80);

            Assert.Equal(navy, PlotPalette.Darkened(navy));
            Assert.Equal(
                PlotPalette.Light.Trace, PlotPalette.Light.ForPrinting().Trace);
        }

        [Fact]
        public void DarkeningKeepsTheHue()
        {
            // Scaled, not clamped: clamping each channel to a ceiling drags every light colour
            // towards grey and loses the distinction between traces.
            PlotColor amber = TraceColours.ForIndex(0);
            PlotColor printed = PlotPalette.Darkened(amber);

            Assert.True(printed.R > printed.G);
            Assert.True(printed.G > printed.B);
            Assert.True(PlotPalette.IsLegibleOnWhite(printed));
        }

        // ---- REQ-UI-022: the themeable element set ----------------------------------------------

        [Fact]
        public void EveryNamedElementFromTheRequirementIsPresent()
        {
            // The requirement's own list. A missing or misspelled entry fails here rather than
            // silently falling back to a default brush.
            string[] named =
            {
                "ACP", "ACP annotation", "Annotation", "Annotation Background", "Grid",
                "Indicator", "Limit", "Fail Limit", "Margin", "Fail Margin",
                "Marker Window Background", "Selected Marker", "Not Selected Marker",
                "OBW", "OBW annotation", "Slot Annotation", "Slot Data", "Slot MAC",
                "Slot Midamble", "Slot Pilot Downlink", "Slot Pilot Uplink", "Slot Preamble",
                "Slot Selected", "Trace Background",
                "Trace", "Symbol", "Average", "Pilot", "Spectrogram Marker", "Trace Select",
            };

            foreach (string name in named)
            {
                Assert.True(
                    ThemeElements.ByName(name) != null,
                    "the theme set has no element named '" + name + "'.");
            }
        }

        [Fact]
        public void TheEmitterAndGroupRangesAreCompleteAtBothEnds()
        {
            // 80 entries typed by hand are 80 chances to skip a number, and the ends are where
            // such a mistake lands.
            Assert.NotNull(ThemeElements.ByName("Emitter 1"));
            Assert.NotNull(ThemeElements.ByName("Emitter 32"));
            Assert.Null(ThemeElements.ByName("Emitter 0"));
            Assert.Null(ThemeElements.ByName("Emitter 33"));

            Assert.NotNull(ThemeElements.ByName("Group 1"));
            Assert.NotNull(ThemeElements.ByName("Group 48"));
            Assert.Null(ThemeElements.ByName("Group 0"));
            Assert.Null(ThemeElements.ByName("Group 49"));

            Assert.NotNull(ThemeElements.ByName("Mod Type 1"));
            Assert.NotNull(
                ThemeElements.ByName("Mod Type " + ThemeElements.ModulationTypeCount));
        }

        [Fact]
        public void EveryElementResolvesToADistinctKey()
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);

            foreach (ThemeElement element in ThemeElements.All)
            {
                Assert.False(string.IsNullOrEmpty(element.Key));
                Assert.StartsWith("OpenVSA.", element.Key);
                Assert.DoesNotContain(" ", element.Key);
                Assert.True(keys.Add(element.Key), "two elements share the key " + element.Key + ".");
            }

            _output.WriteLine(ThemeElements.All.Count + " themeable elements");
        }

        [Fact]
        public void GlobalAndPerTraceAreDistinguished()
        {
            Assert.Equal(ThemeScope.Global, ThemeElements.ByName("Grid").Scope);
            Assert.Equal(ThemeScope.PerTrace, ThemeElements.ByName("Trace").Scope);

            Assert.Equal(
                ThemeElements.All.Count,
                ThemeElements.Global.Count + ThemeElements.PerTrace.Count);
        }

        [Fact]
        public void PerTraceKeysAreIndexedByTraceAndGlobalOnesAreNot()
        {
            ThemeElement perTrace = ThemeElements.ByName("Trace");
            ThemeElement global = ThemeElements.ByName("Grid");

            Assert.Equal("OpenVSA.Trace.A", perTrace.KeyForTrace(0));
            Assert.Equal("OpenVSA.Trace.C", perTrace.KeyForTrace(2));
            Assert.Equal("OpenVSA.Trace.AA", perTrace.KeyForTrace(26));

            Assert.Throws<InvalidOperationException>(() => global.KeyForTrace(0));
            Assert.Throws<ArgumentOutOfRangeException>(() => perTrace.KeyForTrace(-1));
        }

        [Fact]
        public void TheKeySetForSeveralTracesHasOneEntryPerTracePerPerTraceElement()
        {
            IReadOnlyList<string> keys = ThemeElements.KeysFor(4);

            Assert.Equal(
                ThemeElements.Global.Count + ThemeElements.PerTrace.Count * 4, keys.Count);
            Assert.Equal(keys.Count, keys.Distinct().Count());

            Assert.Throws<ArgumentOutOfRangeException>(() => ThemeElements.KeysFor(0));
        }

        // ---- Helpers ---------------------------------------------------------------------------

        private static void AssertAnnotationMatchesTrace(TracePlot plot, PlotColor expected)
        {
            var brush = plot.TraceAnnotationBrush as SolidColorBrush;

            Assert.NotNull(brush);
            Assert.Equal(expected.R, brush.Color.R);
            Assert.Equal(expected.G, brush.Color.G);
            Assert.Equal(expected.B, brush.Color.B);
            Assert.Equal(expected, plot.Palette.Trace);
        }

        private static TracePlot Drawn()
        {
            var plot = new TracePlot();

            plot.Measure(new Size(900.0, 500.0));
            plot.Arrange(new Rect(0.0, 0.0, 900.0, 500.0));
            plot.UpdateLayout();

            var levels = new float[401];

            for (int i = 0; i < levels.Length; i++)
            {
                levels[i] = -90.0f;
            }

            levels[200] = -20.0f;

            var marshal = new RenderMarshal { Columns = plot.GraticuleColumns };

            marshal.Offer(
                SpectrumFrame.FromLevels(levels, 1e9, 25e3, WindowType.Uniform, 1.0));
            plot.Show(marshal.TakeForRender());

            return plot;
        }

        private static void OnStaThread(Action action)
        {
            ExceptionDispatchInfo failure = null;

            var thread = new Thread(() =>
            {
                try
                {
                    action();
                }
                catch (Exception e)
                {
                    failure = ExceptionDispatchInfo.Capture(e);
                }
            });

            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();

            if (failure != null)
            {
                failure.Throw();
            }
        }
    }
}
