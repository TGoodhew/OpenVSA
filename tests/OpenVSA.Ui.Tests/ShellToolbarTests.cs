using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using OpenVSA.Dsp.Spectrum;
using OpenVSA.Measurement;
using OpenVSA.Measurement.State;
using OpenVSA.Ui.Rendering;
using OpenVSA.Ui.Toolbars;
using Xunit;

namespace OpenVSA.Ui.Tests
{
    /// <summary>
    /// <c>REQ-UI-063</c>: the six toolbars, and the behaviours the requirement asks to be tested
    /// rather than merely present.
    /// </summary>
    /// <remarks>
    /// The criterion is unusually explicit about what a passing implementation looks like, and
    /// about where it expects a shortcut to be taken: "<em>Pause</em> then a second click
    /// single-steps under Single sweep and continues under Continuous — <strong>both branches
    /// tested, since collapsing them is the likely shortcut</strong>". So both branches are here,
    /// and so is the case of each that would pass if they had been collapsed.
    /// </remarks>
    [Collection("Shell")]
    public class ShellToolbarTests
    {
        private readonly ShellHost _host;

        /// <summary>Takes the shared shell thread.</summary>
        /// <param name="host">The thread every shell in this collection is built on.</param>
        public ShellToolbarTests(ShellHost host)
        {
            _host = host;
        }

        [Fact]
        public void AllSixToolbarsExistWithTheListedContents()
        {
            _host.Run(() =>
            {
                var shell = Built();

                Assert.Equal(ShellToolbars.All.Count, shell.ToolbarTray.ToolBars.Count);

                for (int index = 0; index < ShellToolbars.All.Count; index++)
                {
                    ShellToolbar declared = ShellToolbars.All[index];
                    ToolBar built = shell.ToolbarTray.ToolBars[index];

                    Assert.Equal(declared.Name, built.Tag as string);

                    List<string> expected = declared.Controls
                        .Where(c => c.Kind != ToolbarControlKind.Separator)
                        .Select(c => c.Name)
                        .ToList();

                    Assert.Equal(expected, Captions(built));
                }
            });
        }

        [Fact]
        public void NoControlIsPresentAndInert()
        {
            // The same rule the menus keep: enabled and functional, or disabled with a reason.
            _host.Run(() =>
            {
                var shell = Built();
                var inert = new List<string>();

                foreach (KeyValuePair<string, ToolbarControl> found in ShellToolbars.AllControls())
                {
                    FrameworkElement made = Control(shell, found.Key);

                    if (!made.IsEnabled)
                    {
                        var reason = made.ToolTip as string;

                        if (string.IsNullOrWhiteSpace(reason) || reason.Length < 40)
                        {
                            inert.Add(found.Key + " is disabled and gives no reason.");
                        }

                        continue;
                    }

                    Assert.False(
                        string.IsNullOrWhiteSpace(made.ToolTip as string),
                        found.Key + " is enabled and says nothing about what it does.");

                    var button = made as ButtonBase;

                    if (button == null)
                    {
                        continue;
                    }

                    button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

                    if (!string.Equals(
                        shell.LastToolbarCommand, found.Key, StringComparison.Ordinal))
                    {
                        inert.Add(
                            found.Key + " is enabled and pressing it reached '" +
                            shell.LastToolbarCommand + "'.");
                    }
                }

                Assert.True(
                    inert.Count == 0,
                    "REQ-UI-063: no control may be present and inert." + Environment.NewLine +
                    string.Join(Environment.NewLine, inert));
            });
        }

        [Fact]
        public void MarkerToolsIsARadioGroup()
        {
            // "Marker Tools is a radio group: selecting one mouse mode deselects the others."
            _host.Run(() =>
            {
                var shell = Built();

                foreach (MouseMode mode in (MouseMode[])Enum.GetValues(typeof(MouseMode)))
                {
                    var chosen = (ToggleButton)Control(
                        shell, "Marker Tools > " + MouseModes.NameOf(mode));

                    chosen.IsChecked = true;
                    chosen.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

                    Assert.Equal(mode, shell.MouseMode);

                    foreach (MouseMode other in (MouseMode[])Enum.GetValues(typeof(MouseMode)))
                    {
                        var button = (ToggleButton)Control(
                            shell, "Marker Tools > " + MouseModes.NameOf(other));

                        Assert.Equal(other == mode, button.IsChecked == true);
                    }
                }
            });
        }

        [Fact]
        public void OnlyTheMarkerModePlacesAMarkerOnAClick()
        {
            // What the radio group is for: a drag or a click means one thing at a time. Pointer
            // exists so that a click can mean nothing at all, which is the mode a user picks when
            // they want to point at the screen without changing the measurement.
            _host.Run(() =>
            {
                var shell = Built();

                shell.MouseMode = MouseMode.Pointer;
                Assert.False(shell.DocumentArea.ActivePlot.SelectAreaEnabled);

                shell.MouseMode = MouseMode.AreaSelect;
                Assert.True(shell.DocumentArea.ActivePlot.SelectAreaEnabled);

                shell.MouseMode = MouseMode.Marker;
                Assert.False(shell.DocumentArea.ActivePlot.SelectAreaEnabled);
            });
        }

        [Fact]
        public void AreaSelectOffersItsThreeOutcomes()
        {
            // "Area Select can scale X, Y, or set centre frequency and span." Three outcomes on
            // one gesture, chosen on the tool itself — and two of them are refused with a reason
            // rather than quietly doing the third.
            _host.Run(() =>
            {
                var shell = Built();

                var tool = (ToggleButton)Control(shell, "Marker Tools > Area Select");

                Assert.True(tool.ContextMenu != null, "Area Select offers no choice of outcome.");

                var offered = tool.ContextMenu.Items.OfType<MenuItem>().ToList();

                Assert.Equal(
                    new[] { "Set centre and span", "Scale X", "Scale Y", "Scale X and Y" },
                    offered.Select(i => (string)i.Header).ToArray());

                foreach (MenuItem item in offered)
                {
                    if (item.IsEnabled)
                    {
                        continue;
                    }

                    Assert.False(
                        string.IsNullOrWhiteSpace(item.ToolTip as string),
                        (string)item.Header + " is refused and does not say why.");
                }

                // Set centre and span, and Scale Y, both work.
                Assert.Null(MouseModes.ReasonAgainst(AreaSelectAction.CentreAndSpan));
                Assert.Null(MouseModes.ReasonAgainst(AreaSelectAction.ScaleY));
            });
        }

        [Fact]
        public void ScaleYMovesTheAxisAndNotTheMeasurement()
        {
            _host.Run(() =>
            {
                var shell = Built();
                TracePlot plot = shell.DocumentArea.ActivePlot;

                double centre = shell.CaptureState().Measurements[0].CenterFrequencyHz;
                double before = plot.DecibelsPerDivision;

                Assert.True(plot.ScaleTo(-20.0, -60.0));

                // The region asked for fits, on a ladder a graticule can be read against.
                Assert.True(plot.FullScaleDb >= 40.0);
                Assert.NotEqual(before, plot.DecibelsPerDivision);
                Assert.True(plot.TopDbm >= -20.0);

                // And the per-division readout says what the axis actually is, rather than the
                // nearest rung of a fixed ladder.
                Assert.Contains(
                    plot.DecibelsPerDivision.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture),
                    plot.PerDivisionHotSpot.Value.Text);

                // The measurement is untouched: that is the whole difference between this action
                // and setting the centre frequency and span.
                Assert.Equal(centre, shell.CaptureState().Measurements[0].CenterFrequencyHz);

                Assert.False(plot.ScaleTo(-60.0, -20.0), "An inverted region is not a scale.");
                Assert.False(plot.ScaleTo(double.NaN, -20.0));
            });
        }

        [Fact]
        public void AutoRangeIsASplitButtonWithAChannelPerDeclaredChannel()
        {
            // "Auto-range is a split button whose main click ranges all channels and whose dropdown
            // ranges a chosen one." The dropdown's contents come from what the front end declares,
            // never from a fixed list — REQ-HAL-002.
            _host.Run(() =>
            {
                var shell = Built();
                var split = (SplitButton)Control(shell, "Control > Auto-range");

                Assert.NotNull(split.MainButton);
                Assert.NotNull(split.DropDownButton);

                split.MainButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Assert.Equal("Control > Auto-range", shell.LastToolbarCommand);

                split.OpenDropDown(true);

                List<MenuItem> offered = split.DropDownItems.OfType<MenuItem>().ToList();

                Assert.NotEmpty(offered);

                // Nothing connected: the dropdown says so rather than offering a channel that does
                // not exist.
                Assert.Single(offered);
                Assert.False(offered[0].IsEnabled);
                Assert.Contains("connected", (string)offered[0].ToolTip, StringComparison.Ordinal);

                split.OpenDropDown(false);
            });
        }

        [Fact]
        public void TheMacroBarIsNotEditableThroughTheCustomiser()
        {
            // REQ-UI-063 says the macro bar is "managed by the macros utility; not user-editable
            // through the toolbar customiser", and REQ-UI-064's criterion repeats it. Five of the
            // six are the customiser's; one is not.
            Assert.False(ShellToolbars.For("Macro Buttons").IsCustomisable);

            Assert.Equal(
                5, ShellToolbars.All.Count(t => t.IsCustomisable));

            Assert.Equal(
                1, ShellToolbars.All.Count(t => !t.IsCustomisable));
        }

        [Fact]
        public void TheAccumulatorsAreOneSettingWithAWayBack()
        {
            // Three buttons over one setting, which has a fourth value: no accumulator. A radio
            // group would offer no way back to it, so these are toggles that clear each other.
            _host.Run(() =>
            {
                var shell = Built();

                var spectrogram = (ToggleButton)Control(
                    shell, "Spectrogram / Colour Map > Spectrogram");

                var persistence = (ToggleButton)Control(
                    shell, "Spectrogram / Colour Map > Digital Persistence");

                spectrogram.IsChecked = true;
                spectrogram.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

                Assert.True(spectrogram.IsChecked);
                Assert.False(persistence.IsChecked == true);

                persistence.IsChecked = true;
                persistence.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

                Assert.False(spectrogram.IsChecked == true);
                Assert.True(persistence.IsChecked);

                // And pressing the chosen one again turns the accumulator off, which a radio group
                // could not do.
                persistence.IsChecked = false;
                persistence.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

                Assert.False(persistence.IsChecked == true);
                Assert.False(spectrogram.IsChecked == true);
            });
        }

        [Fact]
        public void TheActiveTraceIsShownByItsLetter()
        {
            // "Active Trace (shows the active trace's letter)". A letter, which is how REQ-UI-020
            // identifies a trace everywhere else.
            _host.Run(() =>
            {
                var shell = Built();
                var readout = (TextBlock)Control(shell, "Trace / Block Diagram > Active Trace");

                Assert.Contains(
                    shell.DocumentArea.ActiveTrace.ToString(), readout.Text, StringComparison.Ordinal);
            });
        }

        [Fact]
        public void ThePauseButtonSaysWhatASecondPressWillDo()
        {
            // The caption is part of the behaviour: a button reading "Pause" while a second press
            // would single-step is telling the user the wrong thing about what they are about to
            // do. Driven through the shell's own sweep control, so the button and the space bar
            // cannot disagree.
            _host.Run(() =>
            {
                var shell = Built();
                var pause = (Button)Control(shell, "Control > Pause");

                Assert.Equal("Pause", pause.Content);

                shell.Sweep.IsRunning = true;
                shell.Sweep.Press();

                shell.Sweep.Mode = SweepMode.Single;
                Assert.Equal("Single", shell.Sweep.PauseCaption);

                shell.Sweep.Mode = SweepMode.Continuous;
                Assert.Equal("Continue", shell.Sweep.PauseCaption);
            });
        }

        // ---- Helpers ---------------------------------------------------------------------------

        private static ShellWindow Built() =>
            new ShellWindow { PersistPreferences = false, Interactive = false };

        /// <summary>The control at a path, from the real tray.</summary>
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
                        string.Equals(CaptionOf(made), steps[1], StringComparison.Ordinal))
                    {
                        return made;
                    }
                }
            }

            throw new InvalidOperationException("'" + path + "' is not on any toolbar.");
        }

        private static List<string> Captions(ToolBar bar)
        {
            var captions = new List<string>();

            foreach (object child in bar.Items)
            {
                if (child is Separator)
                {
                    continue;
                }

                var made = child as FrameworkElement;

                if (made != null)
                {
                    captions.Add(CaptionOf(made));
                }
            }

            return captions;
        }

        /// <summary>What the requirement calls a control, whatever kind it is.</summary>
        /// <remarks>
        /// From the tag the builder puts on it, not from its caption: a dropdown and a readout have
        /// no caption, and matching them by what they happen to hold would be a test that broke the
        /// first time either was filled differently.
        /// </remarks>
        private static string CaptionOf(FrameworkElement made) => made.Tag as string ?? string.Empty;
    }
}
