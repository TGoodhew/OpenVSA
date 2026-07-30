using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls.Primitives;
using OpenVSA.Core;
using OpenVSA.Measurement;
using OpenVSA.Dsp.Spectrum;
using OpenVSA.Dsp.Windowing;
using OpenVSA.Ui.Rendering;
using OpenVSA.Ui.Toolbars;
using Xunit;

namespace OpenVSA.Ui.Tests
{
    /// <summary>
    /// <c>REQ-UI-054</c> at the control: a plot set to Spectrogram draws a map with two markers.
    /// </summary>
    public class SpectrogramPlotTests
    {
        private const int Points = 401;

        [Fact]
        public void APlotSetToSpectrogramDrawsAMapRatherThanATrace()
        {
            OnStaThread(() =>
            {
                TracePlot plot = Laid();

                Assert.False(plot.IsShowingSpectrogram);
                Assert.Equal(0, plot.SpectrogramCellsDrawn);

                plot.History = Swept(24);
                plot.Accumulator = TraceAccumulator.Spectrogram;

                Assert.True(plot.IsShowingSpectrogram);
                Assert.True(
                    plot.SpectrogramCellsDrawn > 0,
                    "The plot is in spectrogram mode and painted nothing.");
            });
        }

        [Fact]
        public void TheOtherTwoAccumulatorsDrawNoMapYet()
        {
            // Digital Persistence and Cumulative History are declared by REQ-TRC-001a and have no
            // store of their own; selecting one accumulates nothing rather than quietly drawing a
            // spectrogram under another mode's name. Asserted so that a later implementation
            // changes this test deliberately rather than inheriting it.
            OnStaThread(() =>
            {
                TracePlot plot = Laid();

                plot.History = Swept(12);

                foreach (TraceAccumulator mode in
                    new[] { TraceAccumulator.DigitalPersistence, TraceAccumulator.CumulativeHistory })
                {
                    plot.Accumulator = mode;

                    Assert.False(plot.IsShowingSpectrogram);
                }
            });
        }

        [Fact]
        public void RaisingTheThresholdRemovesCellsFromWhatThePlotDraws()
        {
            OnStaThread(() =>
            {
                TracePlot plot = Laid();

                plot.History = Swept(24);
                plot.Accumulator = TraceAccumulator.Spectrogram;

                int everything = plot.SpectrogramCellsDrawn;

                plot.SpectrogramThresholdBelowTopDb = 40.0;

                Assert.True(
                    plot.SpectrogramCellsDrawn < everything,
                    "The threshold drew " + plot.SpectrogramCellsDrawn + " of " + everything +
                    " cells; it removed none.");

                plot.SpectrogramThresholdBelowTopDb = double.NaN;

                Assert.Equal(everything, plot.SpectrogramCellsDrawn);
                Assert.Equal(SpectrogramLevels.NoThresholdDbm, plot.SpectrogramThresholdDbm);
            });
        }

        [Fact]
        public void TheThresholdMeansTheSameThingWhetherEnhanceIsOnOrOff()
        {
            // The defect the screenshot found. The ladder is "so many decibels below the loudest
            // cell"; measuring it from the window instead makes it a no-op the moment Enhance
            // narrows that window — on a flat floor to about a decibel, so every entry on the
            // ladder hides nothing at all.
            OnStaThread(() =>
            {
                TracePlot plot = Laid();

                plot.History = Swept(24);
                plot.Accumulator = TraceAccumulator.Spectrogram;
                plot.SpectrogramThresholdBelowTopDb = 40.0;

                double cut = plot.SpectrogramThresholdDbm;
                int drawn = plot.SpectrogramCellsDrawn;

                Assert.True(drawn > 0, "The threshold removed everything.");

                plot.SpectrogramEnhance = true;

                // The cut is where it was, and exactly the same cells survive it. That Enhance
                // does narrow the window is EnhanceNarrowsTheWindowThePlotDrawsWith's job; asserting
                // it again here is not possible in the same breath, because a threshold this high
                // leaves mostly carrier and the busiest levels are then the loudest ones.
                Assert.Equal(cut, plot.SpectrogramThresholdDbm, 9);
                Assert.Equal(drawn, plot.SpectrogramCellsDrawn);
            });
        }

        [Fact]
        public void EnhanceNarrowsTheWindowThePlotDrawsWith()
        {
            OnStaThread(() =>
            {
                TracePlot plot = Laid();

                plot.History = Swept(24);
                plot.Accumulator = TraceAccumulator.Spectrogram;

                double wide = plot.SpectrogramLevels.RangeDb;

                plot.SpectrogramEnhance = true;

                Assert.True(
                    plot.SpectrogramLevels.RangeDb < wide,
                    "Enhance left the window at " + plot.SpectrogramLevels + ".");
            });
        }

        [Fact]
        public void TheColourMapReachesThePixels()
        {
            OnStaThread(() =>
            {
                TracePlot plot = Laid();

                plot.History = Swept(24);
                plot.Accumulator = TraceAccumulator.Spectrogram;

                plot.SpectrogramMap = SpectrogramColourMap.ColorNormal();
                string colour = Signature(plot);

                plot.SpectrogramMap = SpectrogramColourMap.GreyNormal();
                string grey = Signature(plot);

                Assert.NotEqual(colour, grey);
            });
        }

        [Fact]
        public void AClickMovesOneMarkerAndLeavesTheOtherWhereItWas()
        {
            // The criterion at the control, through the gesture rather than the model: a click is a
            // point, and each marker must take only its own coordinate from it.
            OnStaThread(() =>
            {
                TracePlot plot = Laid();

                plot.History = Swept(24);
                plot.Accumulator = TraceAccumulator.Spectrogram;

                SpectrogramMarkers markers = plot.SpectrogramMarkers;

                Assert.True(markers != null, "A plot with a history has no markers.");

                plot.MoveSpectrogramMarker(SpectrogramMarkerKind.Spectrogram, new Point(150.0, 150.0));
                plot.MoveSpectrogramMarker(SpectrogramMarkerKind.TraceSelect, new Point(150.0, 150.0));

                int bin = markers.BinIndex;
                int row = markers.RowIndex;

                // A click a long way away in both directions.
                plot.MoveSpectrogramMarker(SpectrogramMarkerKind.Spectrogram, new Point(600.0, 480.0));

                Assert.NotEqual(bin, markers.BinIndex);
                Assert.Equal(row, markers.RowIndex);

                int moved = markers.BinIndex;

                // Top and bottom of the plot rather than two points a few pixels apart: how many
                // screen rows a history row occupies depends on the graticule's height and on the
                // display's scaling, and a test that assumed a band height would be asserting the
                // geometry of the machine it ran on.
                plot.MoveSpectrogramMarker(SpectrogramMarkerKind.TraceSelect, new Point(400.0, 8.0));
                int atTop = markers.RowIndex;

                plot.MoveSpectrogramMarker(SpectrogramMarkerKind.TraceSelect, new Point(400.0, 592.0));
                int atBottom = markers.RowIndex;

                Assert.NotEqual(atTop, atBottom);

                // Newest at the top, so the higher row index is the one nearer the top.
                Assert.True(atTop > atBottom, "The time axis runs the wrong way.");

                // Two trace-select clicks that moved the row a long way, and the frequency marker
                // stayed exactly where the previous gesture left it.
                Assert.Equal(moved, markers.BinIndex);
            });
        }

        [Fact]
        public void MovingTheTraceSelectMarkerSurfacesThatRowsOwnData()
        {
            // REQ-MKR-007's second half at the control: "moving the trace-select marker to a history
            // row makes the spectrum trace show that row's data, matching the data captured at that
            // time". The marker moved and the map redrew; nothing surfaced the row, so the trace that
            // is meant to show it had nothing to show.
            OnStaThread(() =>
            {
                TracePlot plot = Laid();

                plot.History = Swept(24);
                plot.Accumulator = TraceAccumulator.Spectrogram;

                int announced = 0;
                plot.SelectedHistoryRowChanged += (sender, e) => announced++;

                // Bottom first, because the marker starts on the newest row at the top: moving it
                // there would be a move that changes nothing, and this needs a change to observe.
                plot.MoveSpectrogramMarker(SpectrogramMarkerKind.TraceSelect, new Point(400.0, 592.0));

                int bottom = plot.SelectedHistoryRow;

                Assert.True(bottom >= 0, "The trace-select marker is on no row.");
                Assert.Equal(1, announced);

                // That row's OWN frame, taken from the history rather than from the drawn map: a
                // spectrum built from colour-mapped cells would be a picture of a picture, and wrong
                // in every format but log magnitude.
                Assert.Same(plot.History.Row(bottom), plot.SelectedHistoryFrame);

                plot.MoveSpectrogramMarker(SpectrogramMarkerKind.TraceSelect, new Point(400.0, 8.0));

                int top = plot.SelectedHistoryRow;

                Assert.NotEqual(top, bottom);
                Assert.Equal(2, announced);
                Assert.Same(plot.History.Row(top), plot.SelectedHistoryFrame);

                // A move that lands on the row it was already on announces nothing: a trace that
                // rebuilt itself on every mouse move during a drag would spend the frame budget
                // redrawing the same spectrum.
                plot.MoveSpectrogramMarker(SpectrogramMarkerKind.TraceSelect, new Point(410.0, 8.0));

                Assert.Equal(2, announced);
                Assert.Equal(top, plot.SelectedHistoryRow);
            });
        }

        [Fact]
        public void AMarkerIsRefusedWhenThereIsNoSpectrogramToMarkerOn()
        {
            OnStaThread(() =>
            {
                TracePlot plot = Laid();

                Assert.False(
                    plot.MoveSpectrogramMarker(
                        SpectrogramMarkerKind.Spectrogram, new Point(100.0, 100.0)));

                plot.History = Swept(4);

                // A history the plot is not drawing is still not something to put a marker on.
                Assert.False(
                    plot.MoveSpectrogramMarker(
                        SpectrogramMarkerKind.Spectrogram, new Point(100.0, 100.0)));
            });
        }

        [Fact]
        public void ANullColourMapIsRefused()
        {
            OnStaThread(() => Assert.Throws<ArgumentNullException>(() => Laid().SpectrogramMap = null));
        }

        // ---- Helpers -----------------------------------------------------------------------------

        private static TracePlot Laid()
        {
            var plot = new TracePlot();

            plot.Measure(new Size(800.0, 600.0));
            plot.Arrange(new Rect(0.0, 0.0, 800.0, 600.0));

            return plot;
        }

        /// <summary>
        /// A history of a tone stepping up in frequency, one row per step.
        /// </summary>
        /// <remarks>
        /// <strong>Computed from time-domain blocks rather than built with
        /// <c>SpectrumFrame.FromLevels</c>, because the rows need distinct timestamps.</strong>
        /// <c>FromLevels</c> stamps every frame with <c>DateTime.UtcNow</c>, whose resolution on
        /// Windows is coarser than the time it takes to build twenty-four of them — so every row
        /// carries the same instant and the trace-select marker, which resolves an instant to the
        /// nearest row, always answers row 0. That passed when this test ran alone and failed in
        /// the full suite, which is exactly the shape of flake worth writing down.
        /// </remarks>
        private static Spectrogram Swept(int rows)
        {
            var history = new Spectrogram(rows);
            var start = new DateTime(2026, 7, 27, 12, 0, 0, DateTimeKind.Utc);

            for (int row = 0; row < rows; row++)
            {
                double offsetHz = -3e6 + row * (6e6 / Math.Max(1, rows - 1));

                history.Add(Tone(offsetHz, start.AddMilliseconds(row * 10)));
            }

            return history;
        }

        private static SpectrumFrame Tone(double offsetHz, DateTime acquiredUtc)
        {
            const double RateHz = 15e6;
            const int Samples = 1021;

            IqBlock block = IqBlock.Rent(new IqBlockMetadata(
                sampleCount: Samples,
                sampleRateHz: RateHz,
                centerFrequencyHz: 1e9,
                isBaseband: false,
                fullScaleVolts: 1.0,
                referenceLevelDbm: 0.0,
                sequenceNumber: 1,
                acquiredUtc: acquiredUtc,
                triggerOffsetSeconds: 0.0,
                triggerCorrectionsApplied: false,
                source: new FrontEndId("test"),
                extended: null));

            using (block)
            {
                Span<float> data = block.GetSamples();
                double cycles = offsetHz / RateHz;

                for (int n = 0; n < Samples; n++)
                {
                    double angle = 2.0 * Math.PI * cycles * n;

                    data[n * 2] = (float)Math.Cos(angle);
                    data[n * 2 + 1] = (float)Math.Sin(angle);
                }

                return new SpectrumComputer(WindowType.FlatTop, null, null).Compute(block);
            }
        }

        /// <summary>A sample of what the plot's bitmap actually holds.</summary>
        private static string Signature(TracePlot plot)
        {
            var bitmap = (System.Windows.Media.Imaging.WriteableBitmap)ImageOf(plot);

            int stride = bitmap.PixelWidth * 4;
            var pixels = new byte[stride * bitmap.PixelHeight];

            bitmap.CopyPixels(pixels, stride, 0);

            var text = new System.Text.StringBuilder();

            for (int i = 0; i < pixels.Length; i += 997)
            {
                text.Append(pixels[i]).Append(',');
            }

            return text.ToString();
        }

        private static System.Windows.Media.ImageSource ImageOf(TracePlot plot)
        {
            foreach (UIElement child in plot.Children)
            {
                var image = child as Image;

                if (image != null)
                {
                    return image.Source;
                }
            }

            throw new InvalidOperationException("The plot has no image to draw into.");
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

    /// <summary>
    /// <c>REQ-UI-054</c> in the shell: the three modes on one toolbar, and its three controls.
    /// </summary>
    [Collection("Shell")]
    public class SpectrogramToolbarTests
    {
        private readonly ShellHost _host;

        /// <summary>Takes the shared shell thread.</summary>
        /// <param name="host">The thread every shell in this collection is built on.</param>
        public SpectrogramToolbarTests(ShellHost host)
        {
            _host = host;
        }

        [Fact]
        public void TheThreeAccumulatingModesAppearTogetherOnOneDedicatedToolbar()
        {
            // "The three accumulating modes appear together on one dedicated toolbar per
            // REQ-UI-063" — one toolbar, all three on it, and no fourth toolbar carrying any.
            var carrying = new List<string>();

            foreach (ShellToolbar bar in ShellToolbars.All)
            {
                foreach (ToolbarControl control in bar.Controls)
                {
                    if (control.Name == "Spectrogram" ||
                        control.Name == "Digital Persistence" ||
                        control.Name == "Cumulative History")
                    {
                        carrying.Add(bar.Name);
                    }
                }
            }

            Assert.Equal(3, carrying.Count);
            Assert.Single(carrying.Distinct());
            Assert.Equal("Spectrogram / Colour Map", carrying[0]);
        }

        [Fact]
        public void TheThreeModesAreOneSettingRatherThanThreeIndependentToggles()
        {
            // REQ-TRC-001a: they are values of one accumulator, so choosing one clears the others.
            // A group, declared as such, so the builder couples them rather than the shell.
            foreach (string name in
                new[] { "Spectrogram", "Digital Persistence", "Cumulative History" })
            {
                ToolbarControl control = ShellToolbars.ControlAt(
                    ShellToolbars.PathOf("Spectrogram / Colour Map", name));

                Assert.Equal(ToolbarControlKind.Toggle, control.Kind);
                Assert.Equal(ShellToolbars.AccumulatorGroup, control.Group);
            }
        }

        [Fact]
        public void EnhanceThresholdAndMapColourSchemeArePresentAndLive()
        {
            // "Enhance, Threshold and Map Colour Scheme are present and each visibly changes the
            // rendering" — present and enabled here; that they change it is the render tests.
            _host.Run(() =>
            {
                var shell = new ShellWindow { PersistPreferences = false, Interactive = false };

                foreach (string name in new[] { "Enhance", "Threshold", "Map Colour Scheme" })
                {
                    FrameworkElement made = Control(shell, "Spectrogram / Colour Map > " + name);

                    Assert.True(made.IsEnabled, name + " is on the toolbar and disabled.");
                }
            });
        }

        [Fact]
        public void TheThresholdDropdownOffersAOffEntryAndALadderBelowTheTop()
        {
            _host.Run(() =>
            {
                var shell = new ShellWindow { PersistPreferences = false, Interactive = false };

                var box = (ComboBox)Control(shell, "Spectrogram / Colour Map > Threshold");

                Assert.Equal(
                    1 + ShellWindow.ThresholdStepsDb.Length, box.Items.Count);

                Assert.Equal(ShellWindow.ThresholdOff, box.Items[0]);
                Assert.Equal(0, box.SelectedIndex);
                Assert.True(double.IsNaN(shell.SpectrogramThresholdBelowTopDb));

                // Zero is not offered: a threshold at the top of the map hides everything.
                Assert.DoesNotContain(0.0, ShellWindow.ThresholdStepsDb);

                box.SelectedIndex = 3;

                Assert.Equal(ShellWindow.ThresholdStepsDb[2], shell.SpectrogramThresholdBelowTopDb);
                Assert.Equal("Spectrogram / Colour Map > Threshold", shell.LastToolbarCommand);
            });
        }

        [Fact]
        public void EnhanceIsAToggleTheShellFollowsBothWays()
        {
            _host.Run(() =>
            {
                var shell = new ShellWindow { PersistPreferences = false, Interactive = false };

                var toggle = (ToggleButton)Control(shell, "Spectrogram / Colour Map > Enhance");

                Assert.False(shell.SpectrogramEnhance);
                Assert.False(toggle.IsChecked == true);

                toggle.IsChecked = true;
                toggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

                Assert.True(shell.SpectrogramEnhance);

                // And the other way: set on the shell, shown on the toolbar.
                shell.SpectrogramEnhance = false;

                Assert.False(toggle.IsChecked == true);
            });
        }

        [Fact]
        public void EveryToolbarToggleWorksThroughItsAutomationPeer()
        {
            // How a screen reader, UI Automation and every other non-mouse client operates a
            // toggle: IToggleProvider.Toggle, which WPF implements by setting IsChecked. It raises
            // Checked and Unchecked and NEVER raises Click — so a toggle bound to Click lights up
            // and does nothing. Every toggle on REQ-UI-063's toolbars was bound that way, and only
            // a screenshot of the running application showed it: the Spectrogram button lit with
            // the accumulator still at None.
            _host.Run(() =>
            {
                var shell = new ShellWindow { PersistPreferences = false, Interactive = false };

                Toggle(shell, "Spectrogram / Colour Map > Spectrogram");
                Assert.Equal(
                    TraceAccumulator.Spectrogram,
                    shell.DocumentArea.PlotOf('A').Accumulator);

                Toggle(shell, "Spectrogram / Colour Map > Enhance");
                Assert.True(shell.SpectrogramEnhance);

                Toggle(shell, "Control > Single Sweep");
                Assert.Equal(SweepMode.Single, shell.Sweep.Mode);

                Toggle(shell, "Marker Tools > Band Power");
                Assert.Equal(MouseMode.BandPower, shell.MouseMode);

                // And off again, through the same route.
                Toggle(shell, "Spectrogram / Colour Map > Spectrogram");
                Assert.Equal(
                    TraceAccumulator.None, shell.DocumentArea.PlotOf('A').Accumulator);

                Toggle(shell, "Control > Single Sweep");
                Assert.Equal(SweepMode.Continuous, shell.Sweep.Mode);
            });
        }

        [Fact]
        public void ChoosingOneAccumulatorThroughAutomationDoesNotCancelItself()
        {
            // The group is coupled by unchecking the others, and each of those raises Unchecked.
            // Without a guard the mode chosen would be set and then immediately cleared by its own
            // neighbours — a gesture that lights the button and leaves the setting at None.
            _host.Run(() =>
            {
                var shell = new ShellWindow { PersistPreferences = false, Interactive = false };

                Toggle(shell, "Spectrogram / Colour Map > Spectrogram");
                Toggle(shell, "Spectrogram / Colour Map > Cumulative History");

                Assert.Equal(
                    TraceAccumulator.CumulativeHistory,
                    shell.DocumentArea.PlotOf('A').Accumulator);

                var spectrogram = (ToggleButton)Control(
                    shell, "Spectrogram / Colour Map > Spectrogram");

                Assert.False(spectrogram.IsChecked == true, "Two accumulators are in at once.");
            });
        }

        /// <summary>Operates a toggle the way something that is not a mouse would.</summary>
        private static void Toggle(ShellWindow shell, string path)
        {
            var button = (ToggleButton)Control(shell, path);

            var provider = (IToggleProvider)UIElementAutomationPeer
                .CreatePeerForElement(button)
                .GetPattern(PatternInterface.Toggle);

            Assert.True(provider != null, path + " offers no toggle pattern to automation.");

            provider.Toggle();
        }

        [Fact]
        public void TheAccumulatorToggleReachesEveryTraceWindow()
        {
            // The wiring between the toolbar and the display. The toggle sets one measurement
            // setting; every open trace window has to be told, or the control lights up and the
            // picture does not change — which is what the first screenshot of this work showed.
            _host.Run(() =>
            {
                var shell = new ShellWindow { PersistPreferences = false, Interactive = false };

                var toggle = (ToggleButton)Control(shell, "Spectrogram / Colour Map > Spectrogram");

                toggle.IsChecked = true;
                toggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

                foreach (char letter in shell.DocumentArea.Traces)
                {
                    TracePlot plot = shell.DocumentArea.PlotOf(letter);

                    Assert.Equal(TraceAccumulator.Spectrogram, plot.Accumulator);
                    Assert.True(plot.History != null, "Trace " + letter + " was given no history.");
                }
            });
        }

        private static FrameworkElement Control(ShellWindow shell, string path)
        {
            string[] steps = path.Split(new[] { " > " }, StringSplitOptions.None);

            foreach (ToolBar bar in shell.ToolbarTray.ToolBars)
            {
                if (!string.Equals(bar.Tag as string, steps[0], StringComparison.Ordinal))
                {
                    continue;
                }

                foreach (object child in bar.Items)
                {
                    var made = child as FrameworkElement;

                    if (made != null &&
                        string.Equals(made.Tag as string, steps[1], StringComparison.Ordinal))
                    {
                        return made;
                    }
                }
            }

            throw new InvalidOperationException("'" + path + "' is not on any toolbar.");
        }
    }
}
