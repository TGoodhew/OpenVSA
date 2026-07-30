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

// The class and its namespace share a name, so the class needs saying explicitly.
using ToolWindowNames = OpenVSA.Ui.ToolWindows.ToolWindows;
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
                    ToolBar bar = Toolbar(shell, driver, "Trace");

                    driver.Invoke(Control<ButtonBase>(bar, "New"));
                });

                driver.Step("a trace window appeared", () =>
                    Assert.Equal(before + 1, shell.DocumentArea.Traces.Count));

                driver.Step("press New again, so two is not one by accident", () =>
                {
                    driver.Invoke(Control<ButtonBase>(Toolbar(shell, driver, "Trace"), "New"));

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
                // Two of REQ-UI-002's eight, from the two menus they live on: six are on Window and
                // the Markers window is on Marker, so one from each proves the whole arrangement
                // rather than one builder.
                OpensAndCloses(shell, driver, ToolWindow.Output, "Window", "Output");
                OpensAndCloses(shell, driver, ToolWindow.Markers, "Marker", "Markers Window");
            });
        }

        /// <summary>
        /// Opens a tool window from its menu entry and closes it again, both through automation.
        /// </summary>
        /// <remarks>
        /// The starting state is normalised rather than assumed. <c>PersistPreferences</c> stops a
        /// test <em>writing</em> the layout, but the shell still <em>reads</em> the one in
        /// <c>%APPDATA%</c> — so a developer who once opened the Output window has a shell where it
        /// starts open, and an assertion that it starts closed would pass on a fresh CI runner and
        /// fail on their machine. Closing it first, through the same entry, is what makes the test
        /// say the same thing everywhere.
        /// </remarks>
        private static void OpensAndCloses(
            ShellWindow shell, AutomationDriver driver, ToolWindow window, params string[] path)
        {
            string name = ToolWindowNames.NameOf(window);

            driver.Step("close " + name + " if a persisted layout had it open", () =>
            {
                if (shell.ToolWindows.Layout.IsOpen(window))
                {
                    driver.Invoke(driver.MenuPath(path));
                }

                Assert.False(shell.ToolWindows.Layout.IsOpen(window));
            });

            driver.Step("open " + name + " from " + string.Join(" ▸ ", path), () =>
            {
                driver.Invoke(driver.MenuPath(path));

                Assert.True(shell.ToolWindows.Layout.IsOpen(window));
                Assert.NotNull(shell.ToolWindows.PaneOf(window));
            });

            driver.Step("invoke it again, so the entry is a toggle rather than a one-way", () =>
            {
                driver.Invoke(driver.MenuPath(path));

                Assert.False(shell.ToolWindows.Layout.IsOpen(window));
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
                    MenuItem format = driver.MenuPath("Trace", "Format");

                    // Submenus are filled on open, so it is expanded through the pattern before its
                    // children are looked for — the same order a client would go in.
                    driver.Expand(format);

                    MenuItem chosen = format.Items.OfType<MenuItem>()
                        .First(i => i.IsEnabled && !string.Equals(
                            ShellMenus.NameOf(i.Header as string), before, StringComparison.Ordinal));

                    driver.Invoke(chosen);
                });

                driver.Step("the trace is drawn in the format that was chosen", () =>
                {
                    Assert.NotEqual(before, shell.DocumentArea.ActivePlot.FormatHotSpot.Value.Text);
                });

                SelectTheSimulatedSource(shell, driver);

                // The settings pane's own controls, through IValueProvider: a text box that an
                // automation client cannot set is a text box a screen-reader user cannot use.
                driver.Step("retune through the pane and apply", () =>
                {
                    driver.SetValue(shell.CentreBox, "2.4 GHz");
                    driver.SetValue(shell.SpanBox, "5 MHz");
                    driver.SetValue(shell.ResolutionBandwidthBox, "100 kHz");

                    Assert.Equal("2.4 GHz", driver.ValueOf(shell.CentreBox));

                    driver.Invoke(driver.ById<ButtonBase>("Apply"));
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

                    SelectTheSimulatedSource(shell, driver);

                    driver.Step("set something worth recalling", () =>
                    {
                        driver.SetValue(shell.CentreBox, "1.5 GHz");
                        driver.SetValue(shell.SpanBox, "2 MHz");
                        driver.SetValue(shell.ResolutionBandwidthBox, "30 kHz");

                        Assert.True(shell.ReadPaneIntoAnalysis());
                    });

                    driver.Step("File ▸ Save ▸ Setup", () =>
                    {
                        driver.Invoke(driver.MenuPath("File", "Save", "Setup"));

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

                    driver.Step("File ▸ Recall ▸ Setup", () =>
                        driver.Invoke(driver.MenuPath("File", "Recall", "Setup")));

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
                    driver.Invoke(Control<ButtonBase>(Toolbar(shell, driver, "Marker"), "New"));

                    Assert.Single(shell.Markers.Markers);
                });

                driver.Step("place a second, and it is a second rather than a move", () =>
                {
                    driver.Invoke(Control<ButtonBase>(Toolbar(shell, driver, "Marker"), "New"));

                    Assert.Equal(2, shell.Markers.Markers.Count);
                    Assert.Equal(new[] { 1, 2 }, shell.Markers.Markers.Select(m => m.Number));
                });

                driver.Step("select the first through the chooser's item peer", () =>
                {
                    var chooser = Toolbar(shell, driver, "Marker").Items.OfType<ComboBox>().First();

                    driver.SelectItem(chooser, 0);

                    Assert.True(shell.Markers.Markers[0].IsSelected);
                });

                double before = shell.Markers.Markers[0].XHz;

                driver.Step("move it with Marker ▸ Peak Search ▸ Peak", () =>
                    driver.Invoke(driver.MenuPath("Marker", "Peak Search", "Peak")));

                driver.Step("hide it, and it keeps its number and its place", () =>
                {
                    Marker selected = shell.Markers.Markers[0];
                    double where = selected.XHz;

                    var hide = Control<ToggleButton>(Toolbar(shell, driver, "Marker"), "Hide");

                    // Through IToggleProvider, which is the pattern a ToggleButton publishes and the
                    // one every toggle in this shell was once unbound from.
                    driver.Toggle(hide);

                    Assert.False(
                        selected.IsVisible,
                        "Hide reads IsChecked=" + hide.IsChecked + " and the selected marker is " +
                        (shell.Markers.Selected == null
                            ? "none"
                            : shell.Markers.Selected.Number.ToString()) +
                        ", but marker " + selected.Number + " is still drawn.");
                    Assert.Equal(1, selected.Number);
                    Assert.Equal(where, selected.XHz);

                    driver.Toggle(hide);
                    Assert.True(selected.IsVisible);
                });

                driver.Step("delete it, and the other survives", () =>
                {
                    driver.Invoke(Control<ButtonBase>(Toolbar(shell, driver, "Marker"), "Delete"));

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
                ShellWindow shell = Shown();
                var driver = new AutomationDriver(shell);

                try
                {
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
                    // Before Close, and on the failure path too: see AutomationDriver.CloseMenus.
                    driver.CloseMenus();
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

        /// <summary>
        /// A shell that has been shown, off-screen and out of the taskbar.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <strong>Shown, because an unshown control has no template.</strong> A
        /// <c>ComboBox</c> that has never been laid out has generated no item containers, so it
        /// publishes no item peers and there is nothing for <c>ISelectionItemProvider</c> to select —
        /// the list an automation client sees is empty. Everything about the trace and marker
        /// choosers that this suite exists to check is invisible until the shell is realised.
        /// </para>
        /// <para>
        /// Off-screen at a negative position and out of the taskbar, which is what
        /// <c>EmbeddedToolbarTests</c> already does and what keeps "runs headless in CI" true: the
        /// window is real and pumps messages, but it does not appear in front of anyone, and nothing
        /// here needs input focus or a visible desktop.
        /// </para>
        /// </remarks>
        private static ShellWindow Shown()
        {
            var shell = new ShellWindow
            {
                PersistPreferences = false,
                Interactive = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -4000.0,
                Top = -4000.0,
                ShowInTaskbar = false,
            };

            shell.Show();
            return shell;
        }

        private void Drive(Action<ShellWindow, AutomationDriver> body)
        {
            _host.Run(() =>
            {
                ShellWindow shell = Shown();
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
                    // Before Close, and on the failure path too: see AutomationDriver.CloseMenus.
                    driver.CloseMenus();
                    shell.Close();
                }
            });
        }

        /// <summary>
        /// Connects the simulated source, so that the settings pane is live.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <strong>Why this is needed at all.</strong> <c>SettingsGrid</c> starts
        /// <c>IsEnabled="False"</c> and is enabled by <c>RangeSettingsFor</c> once a front end has
        /// declared its capabilities — <c>REQ-HAL-002</c>, which ranges every control from the
        /// instrument rather than from a table. An automation client is refused a disabled element by
        /// design (<c>ElementNotEnabledException</c>), so a suite that drove the pane without
        /// connecting anything would be driving controls the application does not offer yet.
        /// </para>
        /// <para>
        /// <strong>Through the menu, not by calling the shell.</strong> Selecting the source is
        /// arrangement rather than one of the criterion's four areas, so it could have been set up
        /// directly — but <c>Hardware ▸ Instruments…</c> is a submenu built on open from what
        /// discovery found, and driving it is what proves the entry is bound at all.
        /// </para>
        /// <para>
        /// <strong>Named, not the first one offered.</strong> This bench has instruments a VISA front
        /// end can reach, and a test that invoked whichever source happened to be listed first could
        /// connect the suite to real hardware.
        /// </para>
        /// </remarks>
        private static void SelectTheSimulatedSource(ShellWindow shell, AutomationDriver driver)
        {
            driver.Step("connect the simulated source from Hardware ▸ Instruments…", () =>
            {
                driver.Invoke(driver.MenuPath("Hardware", "Instruments", SimulatedSourceName));

                // Connecting is awaited on a worker, so the pane comes live a moment after the
                // entry is invoked rather than inside it.
                driver.SettleUntil(
                    () => shell.SettingsGrid.IsEnabled,
                    "the settings pane to come live after connecting the simulated source");
            });

            // Res BW is derived from the point count unless the count is Auto, and the pane says so
            // by disabling the box. Choosing Auto through the list's item peer is how a client would
            // make it settable — and forcing the box enabled instead would be testing a state the
            // application never offers.
            driver.Step("choose Auto points, so the resolution bandwidth is ours to set", () =>
            {
                driver.SelectItem(shell.PointsBox, 0);

                Assert.True(
                    shell.ResolutionBandwidthBox.IsEnabled,
                    "Auto points was selected and Res BW is still disabled.");
            });
        }

        /// <summary>
        /// The simulated front end's name, as its <c>FrontEndProviderAttribute</c> declares it.
        /// </summary>
        /// <remarks>
        /// Spelled here rather than referenced: <c>REQ-ARC-001</c> keeps <c>OpenVSA.Hal.Sim</c> off
        /// every compile-time reference from L3 up, and this test project deploys it into
        /// <c>FrontEnds\</c> the same way the shell does instead of referencing it. A name that stops
        /// matching fails the step with the list of what the menu did offer, which is the failure
        /// this suite is meant to produce.
        /// </remarks>
        private const string SimulatedSourceName = "Simulated source";

        /// <summary>
        /// The toolbar embedded at the head of a menu, with the menu opened first.
        /// </summary>
        /// <remarks>
        /// Opened through <c>IExpandCollapseProvider</c>, because the toolbar lives in the menu's
        /// popup and nothing in a popup that has never been shown has a template — so its marker
        /// chooser has generated no item containers and publishes no item peers to select. Opening
        /// the menu is what a user does before pressing anything on it, and it is what makes the
        /// controls inside real.
        /// </remarks>
        private static ToolBar Toolbar(ShellWindow shell, AutomationDriver driver, string menu)
        {
            MenuItem top = TopLevel(shell, menu);

            driver.Expand(top);

            return (ToolBar)top.Items[0];
        }

        private static MenuItem TopLevel(ShellWindow shell, string name)
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
