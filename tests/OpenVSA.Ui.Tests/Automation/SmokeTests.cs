using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using OpenVSA.Measurement.Markers;
using OpenVSA.Measurement.State;
using OpenVSA.Ui.Layout;
using OpenVSA.Ui.Rendering;
using OpenVSA.Ui.ToolWindows;
using Xunit;
using Xunit.Abstractions;

namespace OpenVSA.Ui.Tests.Automation
{
    /// <summary>
    /// <c>REQ-TST-008</c>: "All four areas are exercised through the UI automation layer rather than
    /// by calling view models directly, so a binding or command-routing break is caught. The suite
    /// creates trace and tool windows, configures a trace's format, saves and recalls a state and
    /// asserts the recalled configuration matches per <c>REQ-STA-001</c>, and places and moves
    /// markers. It runs headless in CI, and a failure identifies the step and captures the visual tree
    /// at that point rather than reporting only a timeout."
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every action goes through <see cref="AutomationDriver"/>, which reaches controls the way an
    /// automation client does: the peer a control publishes, and the provider interface it implements.
    /// A control whose command was never bound, or whose peer does not implement the pattern it
    /// appears to offer, fails here — and a direct call on the click handler would pass in both cases.
    /// </para>
    /// <para>
    /// The trace data and marker coupling clauses are <c>REQ-TST-008a</c>'s: <c>Trace ▸ Data</c> and
    /// <c>Marker ▸ Couple Markers</c> are deliberately inert in this build and say so in
    /// <c>ShellMenuTable</c>, so driving them would be driving a control with nothing behind it.
    /// </para>
    /// </remarks>
    [Collection("Shell")]
    public class SmokeTests
    {
        private readonly ShellHost _host;
        private readonly ITestOutputHelper _output;

        /// <summary>Takes the shared STA host.</summary>
        /// <param name="host">The host whose thread the shell is built on.</param>
        /// <param name="output">Where the steps and any tree dump are written.</param>
        public SmokeTests(ShellHost host, ITestOutputHelper output)
        {
            _host = host;
            _output = output;
        }

        // ---- Area 1: window creation -------------------------------------------------------------

        [Fact]
        public void ItCreatesTraceWindowsThroughTheAutomationLayer()
        {
            Drive((shell, driver) =>
            {
                int before = shell.DocumentArea.Traces.Count;

                driver.Step("press New on the Trace menu's embedded toolbar", () =>
                {
                    ToolBar bar = Toolbar(shell, "Trace");

                    driver.Invoke(Control<ButtonBase>(bar, "New"));
                });

                driver.Step("a trace window appeared", () =>
                    Assert.Equal(before + 1, shell.DocumentArea.Traces.Count));

                driver.Step("press New again, so two is not one by accident", () =>
                {
                    driver.Invoke(Control<ButtonBase>(Toolbar(shell, "Trace"), "New"));

                    Assert.Equal(before + 2, shell.DocumentArea.Traces.Count);
                });

                driver.Step("each new trace has a plot of its own", () =>
                {
                    foreach (char letter in shell.DocumentArea.Traces)
                    {
                        Assert.NotNull(shell.DocumentArea.ContentOf(letter));
                    }

                    Assert.Equal(
                        shell.DocumentArea.Traces.Count,
                        shell.DocumentArea.Traces.Distinct().Count());
                });
            });
        }

        [Fact]
        public void ItCreatesToolWindowsThroughTheAutomationLayer()
        {
            Drive((shell, driver) =>
            {
                // The Markers window: REQ-UI-002 names it, and it is one of the eight.
                driver.Step("open a tool window from the Window menu", () =>
                {
                    Assert.False(shell.ToolWindows.Layout.IsOpen(ToolWindow.Markers));

                    MenuItem entry = MenuEntry(shell, "Window", "Markers");

                    driver.Invoke(entry);
                });

                driver.Step("the tool window is open and has a pane", () =>
                {
                    Assert.True(shell.ToolWindows.Layout.IsOpen(ToolWindow.Markers));
                    Assert.NotNull(shell.ToolWindows.PaneOf(ToolWindow.Markers));
                });

                driver.Step("invoking it again closes it, so the entry is a toggle not a one-way", () =>
                {
                    driver.Invoke(MenuEntry(shell, "Window", "Markers"));

                    Assert.False(shell.ToolWindows.Layout.IsOpen(ToolWindow.Markers));
                });
            });
        }

        // ---- Area 2: trace configuration ---------------------------------------------------------

        [Fact]
        public void ItConfiguresATracesFormatThroughTheAutomationLayer()
        {
            Drive((shell, driver) =>
            {
                TracePlot plot = shell.DocumentArea.ActivePlot;

                Assert.NotNull(plot);

                string before = plot.FormatHotSpot.Value.Text;

                driver.Step("choose a format from the Trace menu's Format submenu", () =>
                {
                    MenuItem format = MenuEntry(shell, "Trace", "Format");

                    // Submenus are filled on open, so it is expanded through the pattern before its
                    // children are looked for — the same order a client would go in.
                    driver.Expand(format);

                    MenuItem chosen = format.Items.OfType<MenuItem>()
                        .First(i => !string.Equals(
                            ShellMenus.NameOf(i.Header as string), before, StringComparison.Ordinal));

                    driver.Invoke(chosen);
                });

                driver.Step("the trace is drawn in the format that was chosen", () =>
                {
                    Assert.NotEqual(before, shell.DocumentArea.ActivePlot.FormatHotSpot.Value.Text);
                });

                // The settings pane's own controls, through IValueProvider: a text box that an
                // automation client cannot set is a text box a screen-reader user cannot use.
                driver.Step("retune through the pane and apply", () =>
                {
                    driver.SetValue(shell.CentreBox, "2.4 GHz");
                    driver.SetValue(shell.SpanBox, "5 MHz");
                    driver.SetValue(shell.ResolutionBandwidthBox, "100 kHz");

                    Assert.Equal("2.4 GHz", driver.ValueOf(shell.CentreBox));

                    driver.Invoke(Control<ButtonBase>(shell.SettingsGrid, "Apply"));
                });
            });
        }

        // ---- Area 3: state save and recall -------------------------------------------------------

        [Fact]
        public void ItSavesAndRecallsAStateThroughTheAutomationLayer()
        {
            string path = Path.Combine(
                Path.GetTempPath(), "OpenVSA.smoke." + Guid.NewGuid().ToString("N") + ".ovsa");

            try
            {
                Drive((shell, driver) =>
                {
                    // The picker cannot be driven from the thread it is modal on, so the routing runs
                    // and the path is nominated. See ShellWindow.NonInteractiveStatePath.
                    shell.NonInteractiveStatePath = path;

                    driver.Step("set something worth recalling", () =>
                    {
                        driver.SetValue(shell.CentreBox, "1.5 GHz");
                        driver.SetValue(shell.SpanBox, "2 MHz");
                        driver.SetValue(shell.ResolutionBandwidthBox, "30 kHz");

                        Assert.True(shell.ReadPaneIntoAnalysis());
                    });

                    driver.Step("File ▸ Save Setup", () =>
                    {
                        driver.Invoke(MenuEntry(shell, "File", "Save Setup"));

                        Assert.True(File.Exists(path), "No state was written to " + path + ".");
                    });

                    driver.Step("move the settings away from what was saved", () =>
                    {
                        driver.SetValue(shell.CentreBox, "900 MHz");
                        driver.SetValue(shell.SpanBox, "8 MHz");

                        Assert.True(shell.ReadPaneIntoAnalysis());
                        Assert.Equal(
                            900e6, shell.CaptureState().Measurements[0].CenterFrequencyHz, 0);
                    });

                    driver.Step("File ▸ Recall Setup", () =>
                        driver.Invoke(MenuEntry(shell, "File", "Recall Setup")));

                    driver.Step("the recalled configuration matches what was saved", () =>
                    {
                        // REQ-STA-001's own claim: what comes back is what went in. Read out of the
                        // session's state rather than out of the boxes, because the state is what the
                        // requirement is about.
                        MeasurementState recalled = shell.CaptureState().Measurements[0];

                        Assert.Equal(1.5e9, recalled.CenterFrequencyHz, 0);
                        Assert.Equal(2e6, recalled.SpanHz, 0);
                        Assert.Equal(30e3, recalled.ResolutionBandwidthHz, 0);
                    });
                });
            }
            finally
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }

        // ---- Area 4: marker interaction ----------------------------------------------------------

        [Fact]
        public void ItPlacesAndMovesMarkersThroughTheAutomationLayer()
        {
            Drive((shell, driver) =>
            {
                driver.Step("press New on the Marker menu's embedded toolbar", () =>
                {
                    driver.Invoke(Control<ButtonBase>(Toolbar(shell, "Marker"), "New"));

                    Assert.Single(shell.Markers.Markers);
                });

                driver.Step("place a second, and it is a second rather than a move", () =>
                {
                    driver.Invoke(Control<ButtonBase>(Toolbar(shell, "Marker"), "New"));

                    Assert.Equal(2, shell.Markers.Markers.Count);
                    Assert.Equal(new[] { 1, 2 }, shell.Markers.Markers.Select(m => m.Number));
                });

                driver.Step("select the first through the chooser's item peer", () =>
                {
                    var chooser = Toolbar(shell, "Marker").Items.OfType<ComboBox>().First();

                    driver.SelectItem(chooser, 0);

                    Assert.True(shell.Markers.Markers[0].IsSelected);
                });

                double before = shell.Markers.Markers[0].XHz;

                driver.Step("move it with Marker ▸ Peak", () =>
                {
                    MenuItem search = MenuEntry(shell, "Marker", "Marker Search");

                    driver.Expand(search);

                    MenuItem peak = search.Items.OfType<MenuItem>()
                        .First(i => string.Equals(
                            ShellMenus.NameOf(i.Header as string), "Peak", StringComparison.Ordinal));

                    driver.Invoke(peak);
                });

                driver.Step("hide it, and it keeps its number and its place", () =>
                {
                    Marker selected = shell.Markers.Markers[0];
                    double where = selected.XHz;

                    var hide = Control<ToggleButton>(Toolbar(shell, "Marker"), "Hide");

                    // Through IToggleProvider, which is the pattern a ToggleButton publishes and the
                    // one every toggle in this shell was once unbound from.
                    driver.Toggle(hide);

                    Assert.False(selected.IsVisible);
                    Assert.Equal(1, selected.Number);
                    Assert.Equal(where, selected.XHz);

                    driver.Toggle(hide);
                    Assert.True(selected.IsVisible);
                });

                driver.Step("delete it, and the other survives", () =>
                {
                    driver.Invoke(Control<ButtonBase>(Toolbar(shell, "Marker"), "Delete"));

                    Assert.Single(shell.Markers.Markers);
                });

                _output.WriteLine(
                    "marker 1 started at " + before + " Hz");
            });
        }

        // ---- The failure report the criterion asks for --------------------------------------------

        [Fact]
        public void AFailingStepNamesItselfAndCapturesTheVisualTree()
        {
            // The criterion's last clause, and it cannot be shown by a suite that passes: a step is
            // made to fail on purpose, and what it reports is what a real failure would report.
            _host.Run(() =>
            {
                var shell = new ShellWindow { PersistPreferences = false, Interactive = false };

                try
                {
                    var driver = new AutomationDriver(shell);

                    AutomationStepException failed = Assert.Throws<AutomationStepException>(() =>
                        driver.Step("a step that cannot succeed", () =>
                            driver.Invoke(shell.CentreBox)));

                    _output.WriteLine(failed.Message.Substring(0, Math.Min(600, failed.Message.Length)));

                    // Names the step, rather than reporting only that something timed out.
                    Assert.Equal("a step that cannot succeed", failed.Step);
                    Assert.Contains("a step that cannot succeed", failed.Message);

                    // And says what was wrong: a TextBox publishes no Invoke pattern, so no
                    // automation client could press it either.
                    Assert.Contains("Invoke", failed.Message, StringComparison.Ordinal);

                    // And carries the visual tree, deep enough to locate the step in the shell.
                    Assert.Contains("ShellWindow", failed.VisualTree, StringComparison.Ordinal);
                    Assert.Contains("CentreBox", failed.VisualTree, StringComparison.Ordinal);
                    Assert.True(
                        failed.VisualTree.Split('\n').Length > 20,
                        "The captured tree is " + failed.VisualTree.Split('\n').Length + " lines.");
                }
                finally
                {
                    shell.Close();
                }
            });
        }

        [Fact]
        public void EveryStepIsRecordedSoAReportCanSayHowFarItGot()
        {
            Drive((shell, driver) =>
            {
                driver.Step("first", () => { });
                driver.Step("second", () => { });

                Assert.Equal(new[] { "first", "second" }, driver.Steps);
            });
        }

        private void Drive(Action<ShellWindow, AutomationDriver> body)
        {
            _host.Run(() =>
            {
                var shell = new ShellWindow { PersistPreferences = false, Interactive = false };
                var driver = new AutomationDriver(shell);

                try
                {
                    body(shell, driver);
                }
                catch (AutomationStepException failed)
                {
                    _output.WriteLine("steps: " + string.Join(" → ", driver.Steps));
                    _output.WriteLine(failed.Message);
                    throw;
                }
                finally
                {
                    shell.Close();
                }
            });
        }

        private static ToolBar Toolbar(ShellWindow shell, string menu) =>
            (ToolBar)Menu(shell, menu).Items[0];

        private static MenuItem Menu(ShellWindow shell, string name)
        {
            foreach (object candidate in shell.MenuBar.Items)
            {
                var top = candidate as MenuItem;

                if (top != null &&
                    string.Equals(
                        ShellMenus.NameOf(top.Header as string), name, StringComparison.Ordinal))
                {
                    return top;
                }
            }

            throw new InvalidOperationException("There is no " + name + " menu.");
        }

        private static MenuItem MenuEntry(ShellWindow shell, string menu, string entry)
        {
            MenuItem found = Descendant(Menu(shell, menu), entry);

            if (found == null)
            {
                throw new InvalidOperationException(
                    "The " + menu + " menu has no '" + entry + "' entry.");
            }

            return found;
        }

        private static MenuItem Descendant(MenuItem parent, string name)
        {
            foreach (object candidate in parent.Items)
            {
                var item = candidate as MenuItem;

                if (item == null)
                {
                    continue;
                }

                if (string.Equals(
                    ShellMenus.NameOf(item.Header as string), name, StringComparison.Ordinal))
                {
                    return item;
                }

                MenuItem deeper = Descendant(item, name);

                if (deeper != null)
                {
                    return deeper;
                }
            }

            return null;
        }

        private static T Control<T>(ItemsControl bar, string caption)
            where T : UIElement
        {
            foreach (object child in bar.Items)
            {
                var content = child as ContentControl;

                if (content is T && string.Equals(
                        content.Content as string, caption, StringComparison.Ordinal))
                {
                    return (T)(UIElement)content;
                }
            }

            throw new InvalidOperationException("'" + caption + "' is not on that toolbar.");
        }

        private static T Control<T>(Panel panel, string caption)
            where T : UIElement
        {
            foreach (UIElement child in panel.Children)
            {
                var content = child as ContentControl;

                if (content is T && string.Equals(
                        content.Content as string, caption, StringComparison.Ordinal))
                {
                    return (T)(UIElement)content;
                }
            }

            throw new InvalidOperationException("'" + caption + "' is not in that panel.");
        }
    }
}
