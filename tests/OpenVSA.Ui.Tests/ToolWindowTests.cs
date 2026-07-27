using System;
using System.Collections.Generic;
using System.Linq;
using OpenVSA.Dsp.Spectrum;
using OpenVSA.Dsp.Windowing;
using OpenVSA.Measurement.Markers;
using OpenVSA.Measurement.State;
using OpenVSA.Ui.ToolWindows;
using Xunit;
using Xunit.Abstractions;

namespace OpenVSA.Ui.Tests
{
    /// <summary>
    /// <c>REQ-UI-002</c>: all eight tool windows exist under exactly these names, open from the
    /// right menu, and keep their placement across a restart.
    /// </summary>
    public class ToolWindowTests
    {
        private readonly ITestOutputHelper _output;

        public ToolWindowTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void AllEightExistUnderExactlyTheseNames()
        {
            // The criterion is "exactly these names", so they are asserted as exact strings and in
            // the requirement's own order. SCPI Log, not ScpiLog; Block Diagram, not Block diagram.
            Assert.Equal(
                new[]
                {
                    "Markers", "Output", "Player", "SCPI Log",
                    "Event Log", "Contexts", "Block Diagram", "Macros",
                },
                Ui.ToolWindows.ToolWindows.All.Select(Ui.ToolWindows.ToolWindows.NameOf));

            Assert.Equal(8, Ui.ToolWindows.ToolWindows.All.Count);
        }

        [Fact]
        public void EachNameResolvesBackToItsWindowAndNothingElseDoes()
        {
            foreach (ToolWindow window in Ui.ToolWindows.ToolWindows.All)
            {
                Assert.Equal(
                    window,
                    Ui.ToolWindows.ToolWindows.ByName(Ui.ToolWindows.ToolWindows.NameOf(window)));
            }

            // Exact, not lenient: a lookup that accepted "scpi log" would let the menu and the pane
            // disagree about capitalisation and still line up.
            Assert.Null(Ui.ToolWindows.ToolWindows.ByName("scpi log"));
            Assert.Null(Ui.ToolWindows.ToolWindows.ByName("ScpiLog"));
            Assert.Null(Ui.ToolWindows.ToolWindows.ByName("Markers "));
        }

        [Fact]
        public void MarkersOpensFromTheMarkerMenuAndTheRestFromWindow()
        {
            // REQ-UI-002: "openable from the Window or Marker menu".
            Assert.Equal(
                ToolWindowMenu.Marker,
                Ui.ToolWindows.ToolWindows.MenuOf(ToolWindow.Markers));

            foreach (ToolWindow window in Ui.ToolWindows.ToolWindows.All)
            {
                if (window == ToolWindow.Markers)
                {
                    continue;
                }

                Assert.Equal(ToolWindowMenu.Window, Ui.ToolWindows.ToolWindows.MenuOf(window));
            }
        }

        [Fact]
        public void NoneAreOpenBeforeAnyoneHasChosen()
        {
            // The shell already docks Measurement and Hardware around the document area. Opening
            // even one of these eight as well leaves the trace a vertical strip on a 1280-wide
            // window - measured, by opening one and looking at it.
            Assert.Empty(new ToolWindowLayout().OpenWindows());
        }

        [Fact]
        public void OpeningOneRecordsItAndTheOthersStayShut()
        {
            var layout = new ToolWindowLayout();

            layout.SetOpen(ToolWindow.Markers, true);

            Assert.Equal(new[] { ToolWindow.Markers }, layout.OpenWindows());
        }

        [Fact]
        public void EveryWindowHasAPlacementEvenWhenNothingWasSaved()
        {
            var layout = new ToolWindowLayout();

            foreach (ToolWindow window in Ui.ToolWindows.ToolWindows.All)
            {
                Assert.NotNull(layout[window]);
                Assert.True(layout[window].Width > 0.0);
                Assert.True(layout[window].Height > 0.0);
                Assert.Equal(
                    Ui.ToolWindows.ToolWindows.NameOf(window), layout[window].Name);
            }
        }

        [Fact]
        public void PlacementAndOpenStateSurviveARestart()
        {
            // The criterion: "each one's docked position, size and open/closed state persist across
            // a restart". Through the real sidecar serialiser, not through an in-memory copy.
            var before = new ToolWindowLayout();

            before.SetOpen(ToolWindow.ScpiLog, true);
            before.SetOpen(ToolWindow.Markers, false);
            before.SetPlacement(ToolWindow.ScpiLog, ToolWindowSide.Bottom, 640.0, 210.0);
            before.SetPlacement(ToolWindow.Contexts, ToolWindowSide.Left, 315.0, 400.0);

            var preferences = new DisplayPreferencesState { ToolWindows = before.ToState() };
            string json = SidecarFile.Write(preferences);

            _output.WriteLine(json.Substring(0, Math.Min(300, json.Length)));

            ToolWindowLayout after = ToolWindowLayout.FromState(
                SidecarFile.Read<DisplayPreferencesState>(json).ToolWindows);

            Assert.True(after.IsOpen(ToolWindow.ScpiLog));
            Assert.False(after.IsOpen(ToolWindow.Markers));

            Assert.Equal(ToolWindowSide.Bottom, after.SideOf(ToolWindow.ScpiLog));
            Assert.Equal(640.0, after[ToolWindow.ScpiLog].Width, 6);
            Assert.Equal(210.0, after[ToolWindow.ScpiLog].Height, 6);

            Assert.Equal(ToolWindowSide.Left, after.SideOf(ToolWindow.Contexts));
            Assert.Equal(315.0, after[ToolWindow.Contexts].Width, 6);
        }

        [Fact]
        public void AFileNamingOnlySomeWindowsStillAnswersForAllEight()
        {
            ToolWindowLayout layout = ToolWindowLayout.FromState(new[]
            {
                new ToolWindowPlacement { Name = "Macros", IsOpen = true, Width = 400.0 },
            });

            Assert.True(layout.IsOpen(ToolWindow.Macros));
            Assert.Equal(400.0, layout[ToolWindow.Macros].Width, 6);

            // And the other seven are at their defaults rather than missing.
            foreach (ToolWindow window in Ui.ToolWindows.ToolWindows.All)
            {
                Assert.True(layout[window].Width > 0.0);
            }
        }

        [Fact]
        public void ASavedSizeOfNothingIsReplacedRatherThanHonoured()
        {
            // A window restored to a width of zero is open, present in the layout, and invisible -
            // the hardest kind of missing to diagnose.
            ToolWindowLayout layout = ToolWindowLayout.FromState(new[]
            {
                new ToolWindowPlacement { Name = "Output", IsOpen = true, Width = 0.0, Height = -5.0 },
            });

            Assert.Equal(ToolWindowLayout.DefaultWidth, layout[ToolWindow.Output].Width, 6);
            Assert.Equal(ToolWindowLayout.DefaultHeight, layout[ToolWindow.Output].Height, 6);
        }

        [Fact]
        public void AnUnknownWindowNameInTheFileIsDropped()
        {
            ToolWindowLayout layout = ToolWindowLayout.FromState(new[]
            {
                new ToolWindowPlacement { Name = "Spectrogram", IsOpen = true },
                new ToolWindowPlacement { Name = "Output", IsOpen = true },
            });

            Assert.True(layout.IsOpen(ToolWindow.Output));
            Assert.Equal(8, layout.ToState().Count);
        }

        [Fact]
        public void AnUnreadableSideFallsBackToTheDefault()
        {
            // A hand-edited file can say anything. Falling back opens; refusing to start over a
            // window edge would not.
            ToolWindowLayout layout = ToolWindowLayout.FromState(new[]
            {
                new ToolWindowPlacement { Name = "Output", Side = "Sideways" },
            });

            Assert.Equal(
                Ui.ToolWindows.ToolWindows.DefaultSide(ToolWindow.Output),
                layout.SideOf(ToolWindow.Output));
        }

        [Fact]
        public void ANullStateGivesTheDefaultLayout()
        {
            Assert.Empty(ToolWindowLayout.FromState(null).OpenWindows());
            Assert.Equal(8, ToolWindowLayout.FromState(null).ToState().Count);
        }

        // ---- Sources ---------------------------------------------------------------------------

        [Fact]
        public void EveryWindowWithoutALiveSourceSaysSoInItsFirstLine()
        {
            // The rule that stops a Player position or seeded SCPI traffic being read as real.
            foreach (ToolWindow window in Ui.ToolWindows.ToolWindows.All)
            {
                IToolWindowSource source = ToolWindowDemonstrations.For(window);

                if (source == null)
                {
                    continue;
                }

                Assert.False(source.IsLive);
                Assert.Equal(ToolWindowSource.DemonstrationNotice, source.Lines[0]);
            }
        }

        [Fact]
        public void ALogIsDemonstrationUntilSomethingRealIsWrittenToIt()
        {
            var log = new ToolWindowLog(ToolWindow.ScpiLog);

            foreach (string line in ToolWindowDemonstrations.ScpiLog())
            {
                log.Seed(line);
            }

            Assert.False(log.IsLive);
            Assert.Equal(ToolWindowSource.DemonstrationNotice, log.Lines[0]);
            Assert.Contains("*IDN?", string.Join("|", log.Lines));

            log.Append("→ :FREQ:CENT 2.4E9");

            // Live now - and the seeded lines are gone, not sitting above the real traffic where
            // someone reading the log to find out what happened would take them for facts.
            Assert.True(log.IsLive);
            Assert.DoesNotContain(ToolWindowSource.DemonstrationNotice, log.Lines);
            Assert.DoesNotContain("*IDN?", string.Join("|", log.Lines));
            Assert.Single(log.Lines);
        }

        [Fact]
        public void SeedingALiveLogIsRefused()
        {
            var log = new ToolWindowLog(ToolWindow.EventLog);

            log.Append("Front end selected.");

            InvalidOperationException error =
                Assert.Throws<InvalidOperationException>(() => log.Seed("pretend"));

            Assert.Contains("invented traffic", error.Message);
        }

        [Fact]
        public void ALogIsBoundedAndSaysHowMuchItDropped()
        {
            // An unbounded list behind a UI control is a leak that shows up only after a long
            // session on a chatty instrument.
            var log = new ToolWindowLog(ToolWindow.ScpiLog, capacity: 10);

            for (int i = 0; i < 25; i++)
            {
                log.Append("line " + i);
            }

            Assert.Equal(15, log.DroppedCount);
            Assert.Equal(11, log.Lines.Count);
            Assert.Contains("15 earlier line(s) dropped", log.Lines[0]);
            Assert.Equal("line 24", log.Lines[log.Lines.Count - 1]);
        }

        [Fact]
        public void ClearingALogEmptiesItRatherThanRestoringTheExamples()
        {
            // Putting demonstration traffic back into a log somebody has just cleared is the same
            // misreading as showing it beside real traffic, arrived at from the other direction.
            var log = new ToolWindowLog(ToolWindow.Output);

            log.Seed("Peak  1.000000 GHz  −20.00 dBm");
            log.Append("real");

            Assert.True(log.IsLive);

            log.Clear();

            Assert.False(log.IsLive);
            Assert.DoesNotContain("Peak", string.Join("|", log.Lines));

            // And seeding is permitted again, for a caller that does want the examples back.
            log.Seed("Peak  1.000000 GHz  −20.00 dBm");

            Assert.Contains("Peak", string.Join("|", log.Lines));
        }

        [Fact]
        public void TheMarkersWindowShowsTheSameTextAsTheAboveGridReadout()
        {
            // REQ-MKR-006 through the window: one readout, two surfaces. The row is the readout's
            // own Text, so they cannot drift.
            var markers = new MarkerCollection();
            var levels = new float[801];

            for (int i = 0; i < levels.Length; i++)
            {
                levels[i] = -90.0f;
            }

            levels[200] = -20.0f;

            markers.Update('A', SpectrumFrame.FromLevels(levels, 1e9, 12.5e3, WindowType.Uniform, 1.0));

            Marker marker = markers.ForTrace('A').AddNormal(1e9 + 200 * 12.5e3);
            markers.ActiveTrace = 'A';

            var source = new MarkerWindowSource(markers);
            source.Refresh();

            Assert.True(source.IsLive);
            Assert.Contains(markers.ActiveReadout.Text, source.Lines[0]);
            Assert.StartsWith("▶", source.Lines[0]);
        }

        [Fact]
        public void TheMarkersWindowSaysWhatToDoWhenThereAreNoMarkers()
        {
            var source = new MarkerWindowSource(new MarkerCollection());

            Assert.True(source.IsLive);
            Assert.Contains("No markers", source.Lines[0]);
        }

        [Fact]
        public void TheContextsWindowIsLiveOnceContextsExist()
        {
            var contexts = new ContextWindowSource();

            Assert.False(contexts.IsLive);
            Assert.Equal(ToolWindowSource.DemonstrationNotice, contexts.Lines[0]);

            contexts.Set(new[] { "Bench", "Spare" }, "Spare");

            Assert.True(contexts.IsLive);
            Assert.Equal("  Bench", contexts.Lines[0]);
            Assert.Equal("▶ Spare", contexts.Lines[1]);
        }

        [Fact]
        public void TheDemonstrationContentSaysWhatEachWindowIsWaitingFor()
        {
            // A worked example is more use than an empty pane for a window that cannot yet do its
            // job - but only if it says why it cannot.
            Assert.Contains(
                "REQ-REC-002",
                string.Join(" ", ToolWindowDemonstrations.For(ToolWindow.Player).Lines));
            Assert.Contains(
                "REQ-API",
                string.Join(" ", ToolWindowDemonstrations.For(ToolWindow.Macros).Lines));
            Assert.Contains(
                "REQ-TRC-003",
                string.Join(" ", ToolWindowDemonstrations.For(ToolWindow.BlockDiagram).Lines));
        }

        [Fact]
        public void TheBlockDiagramNamesTheStagesTheCompositionOrderDeclares()
        {
            // Not a stale drawing: every stage in CompositionOrder appears in it, so a stage added
            // to the pipeline and not to the diagram fails here.
            string diagram = string.Join(
                " ", ToolWindowDemonstrations.For(ToolWindow.BlockDiagram).Lines);

            foreach (AnalysisStage stage in CompositionOrder.Stages)
            {
                string name = stage == AnalysisStage.Transform ? "FFT" : stage.ToString();

                Assert.True(
                    diagram.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0,
                    "the block diagram does not mention " + stage + ".");
            }
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        public void ALogNeedsRoomForAtLeastOneLine(int capacity)
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => new ToolWindowLog(ToolWindow.Output, capacity));
        }

        [Fact]
        public void AnUnknownWindowIsRefusedRatherThanNamed()
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => Ui.ToolWindows.ToolWindows.NameOf((ToolWindow)99));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => Ui.ToolWindows.ToolWindows.MenuOf((ToolWindow)99));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => Ui.ToolWindows.ToolWindows.DefaultSide((ToolWindow)99));
        }

        [Fact]
        public void MissingArgumentsAreRefused()
        {
            Assert.Throws<ArgumentNullException>(() => new MarkerWindowSource(null));
            Assert.Throws<ArgumentNullException>(() => new ContextWindowSource().Set(null, ""));
            Assert.Throws<ArgumentNullException>(
                () => new ToolWindowLog(ToolWindow.Output).Append(null));
            Assert.Throws<ArgumentNullException>(
                () => new DemonstrationSource(ToolWindow.Player, null));
        }
    }
}
