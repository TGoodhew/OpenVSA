using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using OpenVSA.Measurement.Contexts;
using OpenVSA.Measurement.State;
using OpenVSA.Ui.ToolWindows;
using Xunit;
using Xunit.Abstractions;

namespace OpenVSA.Ui.Tests
{
    /// <summary>
    /// <c>REQ-DAT-010</c>: "Two contexts (e.g. 'Spectrum' and 'QPSK demod') run concurrently against
    /// one capture session, each with its own trace windows and markers; both are saved and recalled
    /// by name; a state file whose context names do not match existing contexts raises a specific,
    /// actionable error rather than partially applying."
    /// </summary>
    /// <remarks>
    /// <para>
    /// The concurrency itself is proved in <c>OpenVSA.Measurement.Tests</c> against a running capture
    /// session — a spectrum is arithmetic and needs no window to be right. What is proved here is the
    /// half that does need one: that two contexts have their own trace windows and their own markers,
    /// and that switching between them shows one and hides the other rather than carrying anything
    /// across.
    /// </para>
    /// <para>
    /// Markers are read through <see cref="ShellWindow.Markers"/> and placed through the embedded
    /// toolbar rather than by reaching into the context model, because the claim is about the
    /// <em>shell</em> naming the right set. Writing into <c>Contexts.Active.Markers</c> and reading it
    /// back would pass whether or not the shell had noticed.
    /// </para>
    /// </remarks>
    [Collection("Shell")]
    public class MeasurementContextTests
    {
        private readonly ShellHost _host;
        private readonly ITestOutputHelper _output;

        /// <summary>Takes the shared STA host.</summary>
        /// <param name="host">The host whose thread the shell is built on.</param>
        /// <param name="output">Where the context listing is written.</param>
        public MeasurementContextTests(ShellHost host, ITestOutputHelper output)
        {
            _host = host;
            _output = output;
        }

        [Fact]
        public void ASessionStartsWithOneContextThatOwnsTheFirstTraceWindow()
        {
            WithShell(shell =>
            {
                Assert.Equal(1, shell.Contexts.Count);
                Assert.Equal("Measurement 1", shell.ActiveContext.Name);

                // The trace window belongs to the context, not to the shell.
                Assert.Equal(new[] { 'A' }, shell.ActiveContext.Traces.ToArray());
            });
        }

        [Fact]
        public void EachContextHasItsOwnTraceWindows()
        {
            WithShell(shell =>
            {
                MeasurementContext spectrum = shell.ActiveContext;
                MeasurementContext demod = shell.AddContext("QPSK demod");

                Assert.Equal(2, shell.Contexts.Count);

                char[] itsOwn = demod.Traces.ToArray();

                Assert.Single(itsOwn);
                Assert.DoesNotContain(itsOwn[0], spectrum.Traces);

                // Adding a context does not change what is on screen: the new context's window is
                // open and hidden until it is asked for.
                Assert.True(shell.DocumentArea.IsVisible(spectrum.Traces[0]));
                Assert.False(shell.DocumentArea.IsVisible(itsOwn[0]));

                Assert.True(shell.ActivateContext(demod));

                // And now the other way round: one arrangement, the active context's windows.
                Assert.True(shell.DocumentArea.IsVisible(itsOwn[0]));
                Assert.False(shell.DocumentArea.IsVisible(spectrum.Traces[0]));
                Assert.Same(demod, shell.ActiveContext);

                // Both windows are still open. A hidden trace is one that lost its place on screen,
                // not one that was closed (REQ-UI-062).
                Assert.Contains(spectrum.Traces[0], shell.DocumentArea.Traces);
                Assert.Contains(itsOwn[0], shell.DocumentArea.Traces);
            });
        }

        [Fact]
        public void EachContextHasItsOwnMarkers()
        {
            WithShell(shell =>
            {
                ToolBar bar = MarkerToolbar(shell);

                // Two on the first context, placed the way a user would.
                Press(bar, "New");
                Press(bar, "New");

                Assert.Equal(2, shell.Markers.Markers.Count);

                MeasurementContext first = shell.ActiveContext;
                MeasurementContext second = shell.AddContext("QPSK demod");

                Assert.True(shell.ActivateContext(second));

                // The second context's markers are its own, and it has none. If the shell were still
                // naming the first context's set, this would be 2.
                Assert.Empty(shell.Markers.Markers);

                Press(bar, "New");
                Assert.Single(shell.Markers.Markers);

                Assert.True(shell.ActivateContext(first));

                // Back to the first context's two, untouched: not rebuilt from whatever it was last
                // saved as, and not the second context's one.
                Assert.Equal(2, shell.Markers.Markers.Count);

                Assert.True(shell.ActivateContext(second));
                Assert.Single(shell.Markers.Markers);
            });
        }

        [Fact]
        public void ANewContextStartsFromTheBandBeingCaptured()
        {
            WithShell(shell =>
            {
                Tune(shell, "2.4 GHz");

                MeasurementContext added = shell.AddContext("Second");

                // One acquisition cannot be at two centre frequencies, so a new context inherits the
                // band rather than resetting to a default the session is not measuring.
                Assert.Equal(2.4e9, added.Setup.CenterFrequencyHz, 3);
            });
        }

        [Fact]
        public void SwitchingContextCarriesTheSetupBothWays()
        {
            WithShell(shell =>
            {
                Tune(shell, "1 GHz");

                MeasurementContext first = shell.ActiveContext;
                MeasurementContext second = shell.AddContext("Second");

                Assert.True(shell.ActivateContext(second));

                Tune(shell, "2.4 GHz");

                Assert.True(shell.ActivateContext(first));

                // What was on screen when the first context was left, not what it was last saved as:
                // the pane is read out before anything moves.
                Assert.Equal(1.0e9, shell.Contexts["Measurement 1"].Setup.CenterFrequencyHz, 3);

                // And the other context kept the change made while it was on screen.
                Assert.Equal(2.4e9, shell.Contexts["Second"].Setup.CenterFrequencyHz, 3);
            });
        }

        [Fact]
        public void BothContextsAreSavedAndRecalledByName()
        {
            WithShell(shell =>
            {
                Tune(shell, "1 GHz");
                shell.AddContext("QPSK demod");

                Assert.True(shell.ActivateContext("QPSK demod"));
                Tune(shell, "2.4 GHz");

                ApplicationState saved = shell.CaptureState();

                // Both, by name. A state carrying only the context on screen would recall as a
                // session with one measurement configured and one left as it happened to be.
                Assert.Equal(
                    new[] { "Measurement 1", "QPSK demod" }, saved.ContextNames().ToArray());
                Assert.Equal(1.0e9, saved.For("Measurement 1").CenterFrequencyHz, 3);
                Assert.Equal(2.4e9, saved.For("QPSK demod").CenterFrequencyHz, 3);

                // Change both, then put the state back.
                Tune(shell, "5.8 GHz");
                Assert.True(shell.ActivateContext("Measurement 1"));
                Tune(shell, "5.8 GHz");

                shell.Recall(saved);

                Assert.Equal(1.0e9, shell.Contexts["Measurement 1"].Setup.CenterFrequencyHz, 3);
                Assert.Equal(2.4e9, shell.Contexts["QPSK demod"].Setup.CenterFrequencyHz, 3);
            });
        }

        [Fact]
        public void AStateNamingAnUnknownContextIsRefusedWhole()
        {
            WithShell(shell =>
            {
                Tune(shell, "1 GHz");
                shell.AddContext("QPSK demod");

                var state = new ApplicationState();
                state.Measurements.Clear();
                state.Measurements.Add(new MeasurementState
                {
                    ContextName = "Measurement 1",
                    CenterFrequencyHz = 5.8e9,
                });
                state.Measurements.Add(new MeasurementState
                {
                    ContextName = "Pulse",
                    CenterFrequencyHz = 10.0e9,
                });

                shell.Recall(state);

                _output.WriteLine(shell.SettingsMessage.Text);

                // Specific and actionable: it names what did not match and what the session has.
                Assert.Contains("Pulse", shell.SettingsMessage.Text, StringComparison.Ordinal);
                Assert.Contains("QPSK demod", shell.SettingsMessage.Text, StringComparison.Ordinal);
                Assert.Equal("State not recalled", shell.StatusItem.Content);

                // And nothing applied, not even the context the state DID name.
                Assert.Equal(1.0e9, shell.Contexts["Measurement 1"].Setup.CenterFrequencyHz, 3);
            });
        }

        [Fact]
        public void APresetAppliesToWhicheverContextIsOnScreen()
        {
            WithShell(shell =>
            {
                Assert.True(shell.RenameContext(shell.ActiveContext, "Adjacent channel"));
                shell.AddContext("QPSK demod");

                // A preset is a measurement setup (REQ-STA-005), not a record of which contexts a
                // session had. Applying one must reset the context on screen and leave the other
                // alone, whatever either of them is called.
                Tune(shell, "2.4 GHz");

                MenuItem factory = FactoryPresetItem(shell);
                factory.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

                Assert.Equal("Preset: Factory Defaults", shell.StatusItem.Content);
                Assert.Equal(string.Empty, shell.SettingsMessage.Text);

                // Still two contexts, still named what the user called them: a preset does not
                // rewrite the session's shape.
                Assert.Equal(2, shell.Contexts.Count);
                Assert.Equal("Adjacent channel", shell.ActiveContext.Name);

                // And it reset the one on screen rather than the first in the list.
                Assert.NotEqual(2.4e9, shell.CaptureState().For("Adjacent channel").CenterFrequencyHz);
            });
        }

        [Fact]
        public void ASavedPresetIsAMeasurementRatherThanASession()
        {
            WithShell(shell =>
            {
                shell.AddContext("QPSK demod");

                // One measurement, named for the context on screen. A preset carrying both contexts
                // would be refused by REQ-STA-004's name matching in any session that did not happen
                // to have the same two.
                ApplicationState preset = shell.ActiveContextState();

                Assert.Single(preset.Measurements);
                Assert.Equal("Measurement 1", preset.Measurements[0].ContextName);

                // And applying one names it for whichever context is on screen, so a preset saved in
                // one session is usable in another. This is the one place a context name may be
                // rewritten -- doing it to a recalled STATE would defeat REQ-STA-004 by making every
                // state match.
                Assert.True(shell.ActivateContext("QPSK demod"));

                ApplicationState rewritten = shell.ForActiveContext(preset);

                Assert.Equal("QPSK demod", rewritten.Measurements[0].ContextName);

                // A two-context state is left alone, because that is a session and not a preset.
                ApplicationState session = shell.CaptureState();

                Assert.Equal(2, session.Measurements.Count);
                Assert.Same(session, shell.ForActiveContext(session));
                Assert.Equal(
                    new[] { "Measurement 1", "QPSK demod" }, session.ContextNames().ToArray());
            });
        }

        [Fact]
        public void TheContextsWindowListsThemAndMarksTheActiveOne()
        {
            WithShell(shell =>
            {
                shell.AddContext("QPSK demod");
                Assert.True(shell.ActivateContext("QPSK demod"));

                string shown = ContextsWindowText(shell);
                _output.WriteLine(shown);

                // REQ-UI-002 requires a window called Contexts; REQ-DAT-010 requires contexts to be
                // presented as objects in the UI. This is where the two meet, and it must be showing
                // the session rather than the worked example it shipped with.
                Assert.Contains("Measurement 1", shown, StringComparison.Ordinal);
                Assert.Contains("QPSK demod", shown, StringComparison.Ordinal);
                Assert.DoesNotContain("(spectrum, 2.4 GHz)", shown, StringComparison.Ordinal);

                // The active one is marked, and only it.
                string[] lines = shown.Split('\n');

                Assert.Single(lines.Where(l => l.IndexOf('▶') >= 0));
                Assert.Contains(
                    "QPSK demod",
                    lines.First(l => l.IndexOf('▶') >= 0),
                    StringComparison.Ordinal);
            });
        }

        [Fact]
        public void ANameCannotBeTakenTwiceAndTheRefusalIsReported()
        {
            WithShell(shell =>
            {
                shell.AddContext("QPSK demod");

                Assert.False(shell.RenameContext(shell.ActiveContext, "QPSK demod"));
                Assert.Equal("Context not renamed", shell.StatusItem.Content);
                Assert.Contains("QPSK demod", shell.SettingsMessage.Text, StringComparison.Ordinal);

                // The refusal changed nothing.
                Assert.Equal("Measurement 1", shell.ActiveContext.Name);
            });
        }

        [Fact]
        public void RemovingAContextClosesItsWindowsAndLeavesOneBehind()
        {
            WithShell(shell =>
            {
                MeasurementContext first = shell.ActiveContext;
                MeasurementContext second = shell.AddContext("Second");

                char itsWindow = second.Traces[0];

                Assert.True(shell.ActivateContext(second));
                Assert.True(shell.RemoveContext(second));

                Assert.Equal(1, shell.Contexts.Count);
                Assert.Same(first, shell.ActiveContext);
                Assert.DoesNotContain(itsWindow, shell.DocumentArea.Traces);
                Assert.True(shell.DocumentArea.IsVisible(first.Traces[0]));

                // The last context is not removable: a session with none has no measurement to
                // configure and no name to recall a state into.
                Assert.False(shell.RemoveContext(first));
            });
        }

        private void WithShell(Action<ShellWindow> body)
        {
            _host.Run(() =>
            {
                var shell = new ShellWindow { PersistPreferences = false, Interactive = false };

                try
                {
                    body(shell);
                }
                finally
                {
                    shell.Close();
                }
            });
        }

        /// <summary>
        /// Sets the centre frequency the way pressing Apply does.
        /// </summary>
        /// <remarks>
        /// Typing into the box is not the same as setting the measurement: the pane is read into the
        /// analysis settings, and those are the one place a measurement's definition lives
        /// (<c>REQ-UI-070</c>). A test that only wrote the text would be asserting about an edit the
        /// user had not committed.
        /// </remarks>
        private static void Tune(ShellWindow shell, string frequency)
        {
            shell.CentreBox.Text = frequency;

            // A shell that has never been shown has an unpopulated pane, and the read is all or
            // nothing: it refuses the lot if any box is not a frequency, which is the right
            // behaviour and not what this test is about.
            if (string.IsNullOrWhiteSpace(shell.SpanBox.Text))
            {
                shell.SpanBox.Text = "10 MHz";
            }

            if (string.IsNullOrWhiteSpace(shell.ResolutionBandwidthBox.Text))
            {
                shell.ResolutionBandwidthBox.Text = "100 kHz";
            }

            Assert.True(
                shell.ReadPaneIntoAnalysis(),
                "The settings pane refused '" + frequency + "': " + shell.SettingsMessage.Text);
        }

        private static ToolBar MarkerToolbar(ShellWindow shell)
        {
            foreach (object candidate in shell.MenuBar.Items)
            {
                var top = candidate as MenuItem;

                if (top != null &&
                    string.Equals(
                        ShellMenus.NameOf(top.Header as string), "Marker", StringComparison.Ordinal))
                {
                    return (ToolBar)top.Items[0];
                }
            }

            throw new InvalidOperationException("There is no Marker menu.");
        }

        private static MenuItem FactoryPresetItem(ShellWindow shell)
        {
            foreach (object candidate in shell.MenuBar.Items)
            {
                var top = candidate as MenuItem;
                MenuItem found = top == null ? null : Descendant(top, "Factory Defaults");

                if (found != null)
                {
                    return found;
                }
            }

            throw new InvalidOperationException("There is no Factory Preset entry.");
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

        private static string ContextsWindowText(ShellWindow shell)
        {
            IToolWindowSource source = shell.ToolWindows.SourceOf(ToolWindow.Contexts);

            Assert.NotNull(source);

            return string.Join("\n", source.Lines);
        }

        private static void Press(ToolBar bar, string caption)
        {
            foreach (object child in bar.Items)
            {
                var content = child as ContentControl;

                if (content != null &&
                    string.Equals(content.Content as string, caption, StringComparison.Ordinal))
                {
                    ((ButtonBase)content).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    return;
                }
            }

            throw new InvalidOperationException("'" + caption + "' is not on the toolbar.");
        }
    }
}
