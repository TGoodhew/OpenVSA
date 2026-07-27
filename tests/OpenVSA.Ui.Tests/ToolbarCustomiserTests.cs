using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using OpenVSA.Measurement;
using OpenVSA.Measurement.State;
using OpenVSA.Ui.Dialogs;
using OpenVSA.Ui.Dialogs.Pages;
using OpenVSA.Ui.Rendering;
using OpenVSA.Ui.Toolbars;
using Xunit;

namespace OpenVSA.Ui.Tests
{
    /// <summary>
    /// <c>REQ-UI-064</c>: the arrangement behind the toolbar customiser.
    /// </summary>
    /// <remarks>
    /// No shell here. The criterion — created, populated, reordered, deleted, and surviving a
    /// restart — is about an arrangement, and an arrangement that can only be exercised through a
    /// window is one whose rules are stated in event handlers.
    /// </remarks>
    public class ToolbarLayoutTests
    {
        private const string Pause = "Control > Pause";
        private const string Restart = "Control > Restart";
        private const string Marker = "Marker Tools > Marker";

        [Fact]
        public void TheDefaultArrangementIsWhatTheRequirementDeclares()
        {
            var layout = new ToolbarLayout();

            Assert.True(layout.IsDefault);
            Assert.Equal(ShellToolbars.All.Count, layout.Bars.Count);

            for (int index = 0; index < ShellToolbars.All.Count; index++)
            {
                ShellToolbar declared = ShellToolbars.All[index];
                ToolbarBar arranged = layout.Bars[index];

                Assert.Equal(declared.Name, arranged.Name);
                Assert.Equal(declared.Controls.Count, arranged.Controls.Count);
                Assert.False(arranged.IsCustom);
            }
        }

        [Fact]
        public void ACustomToolbarIsCreatedPopulatedReorderedAndDeleted()
        {
            // The criterion's first sentence, in the order it states it.
            var layout = new ToolbarLayout();

            ToolbarBar mine = layout.Create("Mine");

            Assert.True(mine.IsCustom);
            Assert.Empty(mine.Controls);
            Assert.False(layout.IsDefault);

            // Populated from what the picker offers, not from a path invented here.
            IReadOnlyList<string> offered = layout.Picker(mine);

            Assert.Contains(Pause, offered);
            Assert.Contains(Restart, offered);

            layout.Place(mine, Restart);
            layout.Place(mine, Pause);

            Assert.Equal(new[] { Restart, Pause }, mine.Controls.ToArray());

            // Reordered — the controls on it, and the toolbar among the others.
            Assert.True(layout.MoveControl(mine, 1, -1));
            Assert.Equal(new[] { Pause, Restart }, mine.Controls.ToArray());

            int was = layout.Bars.ToList().IndexOf(mine);

            Assert.True(layout.MoveBar(mine, -1));
            Assert.Equal(was - 1, layout.Bars.ToList().IndexOf(mine));

            // Deleted.
            layout.Delete(mine);

            Assert.DoesNotContain(mine, layout.Bars);
            Assert.Null(layout.Find("Mine"));
        }

        [Fact]
        public void TheArrangementSurvivesARestart()
        {
            // "and survives a restart" — which is the display sidecar, written and read back.
            var before = new ToolbarLayout();

            ToolbarBar mine = before.Create("Bench");

            before.Place(mine, Pause);
            before.Place(mine, Marker);
            before.Place(mine, ToolbarLayout.SeparatorPath);
            before.SetVisible(before.Find("Record"), false);

            string json = SidecarFile.Write(new DisplayPreferencesState { Toolbars = before.ToState() });

            var read = SidecarFile.Read<DisplayPreferencesState>(json);
            var after = new ToolbarLayout();

            Assert.Empty(after.LoadFrom(read.Toolbars));

            ToolbarBar restored = after.Find("Bench");

            Assert.True(restored != null, "The custom toolbar did not survive.");
            Assert.True(restored.IsCustom);
            Assert.Equal(new[] { Pause, Marker, ToolbarLayout.SeparatorPath }, restored.Controls.ToArray());

            Assert.False(after.Find("Record").IsVisible);
            Assert.DoesNotContain(Pause, after.Find("Control").Controls);
        }

        [Fact]
        public void AnUntouchedArrangementWritesNothing()
        {
            // The rule the colours keep: a user who has never customised anything should not have
            // today's defaults frozen into their preferences file.
            Assert.Empty(new ToolbarLayout().ToState());
        }

        [Fact]
        public void PresetToolbarsRestoresTheFiveAndRemovesTheCustomOnes()
        {
            // "File > Preset > Toolbars restores the five preconfigured toolbars of REQ-UI-063 to
            // their default contents and removes custom ones."
            var layout = new ToolbarLayout();
            var reference = new ToolbarLayout();

            ToolbarBar mine = layout.Create("Scratch");

            layout.Place(mine, Pause);
            layout.Take(layout.Find("Record"), 0);
            layout.MoveControl(layout.Find("Marker Tools"), 0, 2);
            layout.SetVisible(layout.Find("Control"), false);

            Assert.False(layout.IsDefault);

            layout.Reset();

            Assert.True(layout.IsDefault);
            Assert.Null(layout.Find("Scratch"));
            Assert.Equal(reference.Bars.Count, layout.Bars.Count);

            for (int index = 0; index < reference.Bars.Count; index++)
            {
                Assert.Equal(
                    reference.Bars[index].Controls.ToArray(),
                    layout.Bars[index].Controls.ToArray());
            }
        }

        [Fact]
        public void TheMacroBarIsAbsentFromTheCustomiser()
        {
            // Stated by REQ-UI-063 and repeated by REQ-UI-064, so it is asserted from all three
            // sides: the list, the picker, and an attempt to put something on it.
            var layout = new ToolbarLayout();
            ToolbarBar macros = layout.Find("Macro Buttons");

            Assert.True(macros != null, "The macro bar is not in the tray at all.");
            Assert.False(macros.IsCustomisable);
            Assert.DoesNotContain(macros, layout.Customisable);
            Assert.Equal(ShellToolbars.All.Count - 1, layout.Customisable.Count);

            foreach (string offered in layout.Picker(null))
            {
                Assert.False(
                    offered.StartsWith("Macro Buttons", StringComparison.Ordinal),
                    "The picker offers '" + offered + "', which is the macros utility's.");
            }

            Assert.Throws<ArgumentException>(() => layout.Place(macros, Pause));
            Assert.False(layout.Take(macros, 0));
        }

        [Fact]
        public void AControlIsOnOneToolbarAtATime()
        {
            var layout = new ToolbarLayout();
            ToolbarBar mine = layout.Create("Mine");

            Assert.Equal("Control", layout.BarOf(Pause).Name);

            layout.Place(mine, Pause);

            Assert.Same(mine, layout.BarOf(Pause));
            Assert.DoesNotContain(Pause, layout.Find("Control").Controls);
            Assert.DoesNotContain(Pause, layout.Picker(mine));
        }

        [Fact]
        public void DeletingACustomToolbarPutsWhatWasOnItBack()
        {
            var layout = new ToolbarLayout();
            ToolbarBar mine = layout.Create("Mine");

            layout.Place(mine, Pause);
            layout.Place(mine, Marker);
            layout.Delete(mine);

            Assert.Contains(Pause, layout.Find("Control").Controls);
            Assert.Contains(Marker, layout.Find("Marker Tools").Controls);
        }

        [Fact]
        public void APreconfiguredToolbarIsNotTheUsersToDelete()
        {
            var layout = new ToolbarLayout();

            InvalidOperationException refusal =
                Assert.Throws<InvalidOperationException>(() => layout.Delete(layout.Find("Control")));

            Assert.Contains("Preset", refusal.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void ANameIsRequiredAndMustBeNew()
        {
            var layout = new ToolbarLayout();

            layout.Create("Mine");

            Assert.Throws<ArgumentException>(() => layout.Create("   "));
            Assert.Throws<ArgumentException>(() => layout.Create("mine"));
            Assert.Throws<ArgumentException>(() => layout.Create("Control"));
        }

        [Fact]
        public void SeparatorsAreExemptFromTheOnePlaceRule()
        {
            // There is nothing behind a rule to be in two places at once, and a custom toolbar
            // wants as many groups as the user puts on it.
            var layout = new ToolbarLayout();
            ToolbarBar mine = layout.Create("Mine");

            layout.Place(mine, Pause);
            layout.Place(mine, ToolbarLayout.SeparatorPath);
            layout.Place(mine, Restart);
            layout.Place(mine, ToolbarLayout.SeparatorPath);

            Assert.Equal(2, mine.Controls.Count(c => c == ToolbarLayout.SeparatorPath));
            Assert.Contains(ToolbarLayout.SeparatorPath, layout.Picker(mine));
        }

        [Fact]
        public void AFileNamingAControlThisBuildLacksCostsThatControlOnly()
        {
            // The rule the colours keep, in both directions: a path this build does not know is
            // reported and dropped, and a control this build has that the file never mentions is
            // put back on the toolbar it belongs to rather than left unreachable.
            var saved = new List<ToolbarBarState>
            {
                new ToolbarBarState
                {
                    Name = "Control",
                    Controls = new List<string> { Restart, "Control > Time Machine" },
                },
            };

            var layout = new ToolbarLayout();
            IReadOnlyList<string> unknown = layout.LoadFrom(saved);

            Assert.Equal(new[] { "Control > Time Machine" }, unknown.ToArray());

            var placed = new List<string>();

            foreach (ToolbarBar bar in layout.Bars)
            {
                placed.AddRange(bar.Controls);
            }

            foreach (KeyValuePair<string, ToolbarControl> declared in ShellToolbars.AllControls())
            {
                Assert.Contains(declared.Key, placed);
            }

            Assert.DoesNotContain("Control > Time Machine", placed);
        }

        [Fact]
        public void AFileCannotTurnAPreconfiguredToolbarIntoADeletableOne()
        {
            var saved = new List<ToolbarBarState>
            {
                new ToolbarBarState { Name = "Control", IsCustom = true, Controls = new List<string> { Restart } },
            };

            var layout = new ToolbarLayout();

            layout.LoadFrom(saved);

            Assert.False(layout.Find("Control").IsCustom);
            Assert.Throws<InvalidOperationException>(() => layout.Delete(layout.Find("Control")));
        }
    }

    /// <summary>
    /// <c>REQ-UI-064</c>: the customiser itself, and the tray it edits.
    /// </summary>
    [Collection("Shell")]
    public class ToolbarCustomiserTests
    {
        private readonly ShellHost _host;

        /// <summary>Takes the shared shell thread.</summary>
        /// <param name="host">The thread every shell in this collection is built on.</param>
        public ToolbarCustomiserTests(ShellHost host)
        {
            _host = host;
        }

        [Fact]
        public void TheCustomiserOffersTheThreeSurfacesTheRequirementNames()
        {
            _host.Run(() =>
            {
                var layout = new ToolbarLayout();
                var page = new ToolbarsPage(layout);

                // A list of toolbars, a contents editor and a control picker — every toolbar but
                // the macro bar, and everything placeable on the one selected.
                Assert.Equal(
                    layout.Customisable.Select(b => b.Name).ToArray(),
                    page.ToolbarList.Items.Cast<ToolbarBar>().Select(b => b.Name).ToArray());

                Assert.True(page.Select("Control"));

                Assert.Equal(
                    layout.Find("Control").Controls.ToArray(),
                    page.ContentsList.Items.Cast<ToolbarEntry>().Select(e => e.Path).ToArray());

                Assert.Equal(
                    layout.Picker(layout.Find("Control")).ToArray(),
                    page.PickerList.Items.Cast<ToolbarEntry>().Select(e => e.Path).ToArray());
            });
        }

        [Fact]
        public void TheCustomiserCreatesPopulatesReordersAndDeletes()
        {
            // The whole criterion driven through the buttons a user presses, not through the model
            // the buttons call: a picker that offered the right paths and an Add button wired to
            // nothing would pass the model tests.
            _host.Run(() =>
            {
                var layout = new ToolbarLayout();
                var page = new ToolbarsPage(layout);

                page.NewToolbarName.Text = "Bench";
                Press(page.CreateButton);

                ToolbarBar mine = layout.Find("Bench");

                Assert.True(mine != null, "The New toolbar button created nothing.");
                Assert.Same(mine, page.SelectedToolbar);

                Pick(page, "Control > Restart");
                Press(page.AddButton);

                Pick(page, "Control > Pause");
                Press(page.AddButton);

                Assert.Equal(
                    new[] { "Control > Restart", "Control > Pause" }, mine.Controls.ToArray());

                page.ContentsList.SelectedIndex = 1;
                Press(page.ControlUpButton);

                Assert.Equal(
                    new[] { "Control > Pause", "Control > Restart" }, mine.Controls.ToArray());

                page.ContentsList.SelectedIndex = 0;
                Press(page.RemoveButton);

                Assert.Equal(new[] { "Control > Restart" }, mine.Controls.ToArray());

                Press(page.DeleteButton);

                Assert.Null(layout.Find("Bench"));
                Assert.Contains("Control > Restart", layout.Find("Control").Controls);
            });
        }

        [Fact]
        public void DeleteIsOfferedForACustomToolbarAndNotForTheOthers()
        {
            _host.Run(() =>
            {
                var layout = new ToolbarLayout();
                var page = new ToolbarsPage(layout);

                Assert.True(page.Select("Control"));
                Assert.False(page.DeleteButton.IsEnabled);

                page.NewToolbarName.Text = "Mine";
                Press(page.CreateButton);

                Assert.True(page.DeleteButton.IsEnabled);
            });
        }

        [Fact]
        public void ADuplicateNameIsRefusedInWordsRatherThanThrown()
        {
            _host.Run(() =>
            {
                var layout = new ToolbarLayout();
                var page = new ToolbarsPage(layout);

                page.NewToolbarName.Text = "Control";
                Press(page.CreateButton);

                Assert.Contains("already", page.Message, StringComparison.OrdinalIgnoreCase);
                Assert.Equal(ShellToolbars.All.Count, layout.Bars.Count);
            });
        }

        [Fact]
        public void TheCustomiserIsAModelessLiveDialogLikeEveryOther()
        {
            _host.Run(() =>
            {
                var dialog = new ToolbarCustomiserDialog(
                    new DialogFrameworkOptions(), new ToolbarLayout());

                // REQ-UI-070: nothing to commit, and no way to be modal.
                Assert.Equal(0, dialog.CommitButtonCount);
                Assert.Throws<InvalidOperationException>(() => dialog.ShowDialog());

                Assert.Equal(new[] { ToolbarCustomiserDialog.DialogTitle }, dialog.PageNames.ToArray());
            });
        }

        [Fact]
        public void TheTrayFollowsTheCustomiser()
        {
            // The point of the whole exercise: an edit in the dialog reaches the toolbar on screen,
            // and the control still does what it did.
            _host.Run(() =>
            {
                var shell = Built();

                ToolbarBar mine = shell.ToolbarArrangement.Create("Bench");

                shell.ToolbarArrangement.Place(mine, "Control > Pause");

                ToolBar built = BarNamed(shell, "Bench");

                Assert.True(built != null, "The tray has no toolbar for the one just created.");
                Assert.Equal(new[] { "Pause" }, Tags(built));
                Assert.DoesNotContain("Pause", Tags(BarNamed(shell, "Control")));

                // Bound by the path REQ-UI-063 declares, wherever the control has been put.
                var pause = (Button)built.Items.Cast<object>().First();

                pause.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

                Assert.Equal("Control > Pause", shell.LastToolbarCommand);
            });
        }

        [Fact]
        public void AHiddenToolbarIsNotInTheTray()
        {
            _host.Run(() =>
            {
                var shell = Built();
                int all = shell.ToolbarTray.ToolBars.Count;

                shell.ToolbarArrangement.SetVisible(shell.ToolbarArrangement.Find("Record"), false);

                Assert.Equal(all - 1, shell.ToolbarTray.ToolBars.Count);
                Assert.Null(BarNamed(shell, "Record"));

                shell.ToolbarArrangement.SetVisible(shell.ToolbarArrangement.Find("Record"), true);

                Assert.Equal(all, shell.ToolbarTray.ToolBars.Count);
            });
        }

        [Fact]
        public void ARebuiltTrayKeepsTheSettingsItsControlsShow()
        {
            // A rebuild makes new controls, and the shell holds each of the ones it has to keep in
            // step by reference. Getting this wrong leaves the mouse mode unchecked on a toolbar
            // that has just been rearranged, which is invisible to every test that never rebuilds.
            _host.Run(() =>
            {
                var shell = Built();

                shell.MouseMode = MouseMode.BandPower;
                shell.Sweep.Mode = SweepMode.Single;

                // Any edit at all; the rebuild is what is under test.
                shell.ToolbarArrangement.Create("Mine");

                var mode = (ToggleButton)Control(shell, "Marker Tools > Band Power");
                var single = (ToggleButton)Control(shell, "Control > Single Sweep");

                Assert.True(mode.IsChecked == true, "The rebuilt tray lost the mouse mode.");
                Assert.True(single.IsChecked == true, "The rebuilt tray lost the sweep mode.");

                // And the new controls are the ones the shell now follows.
                shell.MouseMode = MouseMode.Pointer;

                Assert.False(mode.IsChecked == true, "The shell is still driving the old buttons.");
            });
        }

        [Fact]
        public void PresetToolbarsRestoresTheDefaultsFromTheMenu()
        {
            _host.Run(() =>
            {
                var shell = Built();

                ToolbarBar mine = shell.ToolbarArrangement.Create("Scratch");

                shell.ToolbarArrangement.Place(mine, "Control > Pause");

                Assert.False(shell.ToolbarArrangement.IsDefault);

                At(shell, "File > Preset > Toolbars")
                    .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

                Assert.True(shell.ToolbarArrangement.IsDefault);
                Assert.Null(BarNamed(shell, "Scratch"));
                Assert.Contains("Pause", Tags(BarNamed(shell, "Control")));
            });
        }

        [Fact]
        public void FactoryDefaultsResetsTheToolbarsToo()
        {
            // Its scope names the toolbars, so it must — and by the same route, not a second one.
            _host.Run(() =>
            {
                var shell = Built();

                shell.ToolbarArrangement.Create("Scratch");

                At(shell, "File > Preset > Factory Defaults")
                    .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

                Assert.True(shell.ToolbarArrangement.IsDefault);
            });
        }

        [Fact]
        public void UtilitiesToolbarsOpensTheCustomiser()
        {
            _host.Run(() =>
            {
                ShellWindow shell = Shown();

                try
                {
                    At(shell, "Utilities > Toolbars…")
                        .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

                    ToolbarCustomiserDialog opened = shell.OwnedWindows
                        .Cast<Window>()
                        .OfType<ToolbarCustomiserDialog>()
                        .FirstOrDefault();

                    Assert.True(opened != null, "Utilities > Toolbars… opened no customiser.");

                    // The dialog edits the shell's own arrangement, not a copy of it.
                    opened.Page.NewToolbarName.Text = "From the menu";
                    Press(opened.Page.CreateButton);

                    Assert.True(shell.ToolbarArrangement.Find("From the menu") != null);
                    Assert.True(BarNamed(shell, "From the menu") != null);
                }
                finally
                {
                    foreach (Window owned in shell.OwnedWindows.Cast<Window>().ToList())
                    {
                        owned.Close();
                    }

                    shell.Close();
                }
            });
        }

        // ---- Helpers ---------------------------------------------------------------------------

        private static ShellWindow Built() =>
            new ShellWindow { PersistPreferences = false, Interactive = false };

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

        private static void Press(Button button) =>
            button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

        private static void Pick(ToolbarsPage page, string path)
        {
            foreach (object item in page.PickerList.Items)
            {
                var entry = item as ToolbarEntry;

                if (entry != null && string.Equals(entry.Path, path, StringComparison.Ordinal))
                {
                    page.PickerList.SelectedItem = entry;
                    return;
                }
            }

            throw new InvalidOperationException("The picker does not offer '" + path + "'.");
        }

        private static ToolBar BarNamed(ShellWindow shell, string name)
        {
            foreach (ToolBar bar in shell.ToolbarTray.ToolBars)
            {
                if (string.Equals(bar.Tag as string, name, StringComparison.Ordinal))
                {
                    return bar;
                }
            }

            return null;
        }

        private static string[] Tags(ToolBar bar) => bar.Items
            .Cast<object>()
            .OfType<FrameworkElement>()
            .Select(e => e.Tag as string)
            .Where(t => !string.IsNullOrEmpty(t))
            .ToArray();

        private static FrameworkElement Control(ShellWindow shell, string path)
        {
            string[] steps = path.Split(new[] { " > " }, StringSplitOptions.None);

            foreach (ToolBar bar in shell.ToolbarTray.ToolBars)
            {
                foreach (object child in bar.Items)
                {
                    var made = child as FrameworkElement;

                    if (made != null && string.Equals(made.Tag as string, steps[1], StringComparison.Ordinal))
                    {
                        return made;
                    }
                }
            }

            throw new InvalidOperationException("'" + path + "' is not on any toolbar.");
        }

        private static MenuItem At(ShellWindow shell, string path)
        {
            string[] steps = path.Split(new[] { " > " }, StringSplitOptions.None);
            MenuItem item = null;

            foreach (object candidate in shell.MenuBar.Items)
            {
                var top = candidate as MenuItem;

                if (top != null &&
                    string.Equals(ShellMenus.NameOf(top.Header as string), steps[0], StringComparison.Ordinal))
                {
                    item = top;
                    break;
                }
            }

            Assert.True(item != null, "There is no '" + steps[0] + "' menu.");

            for (int step = 1; step < steps.Length; step++)
            {
                MenuItem next = null;

                foreach (object child in item.Items)
                {
                    var entry = child as MenuItem;

                    if (entry != null &&
                        string.Equals(
                            ShellMenus.NameOf(entry.Header as string),
                            steps[step],
                            StringComparison.Ordinal))
                    {
                        next = entry;
                        break;
                    }
                }

                Assert.True(next != null, "'" + path + "' is not in the menu bar.");
                item = next;
            }

            return item;
        }
    }
}
