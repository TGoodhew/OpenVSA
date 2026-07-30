using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using OpenVSA.Measurement.Markers;
using OpenVSA.Ui.Layout;
using OpenVSA.Ui.Menus;
using OpenVSA.Ui.Theming;
using Xunit;

namespace OpenVSA.Ui.Tests
{
    /// <summary>
    /// <c>REQ-UI-062</c>: the toolbars embedded at the top of the Trace and Marker menus.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The requirement calls this "a distinctive touch worth keeping: it puts the most frequent
    /// actions one click inside the menu that owns them, rather than requiring a separate toolbar
    /// hunt". Its criterion is specific about the two ways it could be got wrong — "a toolbar as
    /// their topmost element, <strong>not a list of commands standing in for one</strong>", and
    /// acting on it "takes effect <strong>without first dismissing the menu</strong>".
    /// </para>
    /// <para>
    /// Both of those are tested here, because both are what makes the touch worth having: a
    /// submenu of commands would look similar and cost a second click each time, and a toolbar that
    /// closed the menu on every press would be worse than the submenu.
    /// </para>
    /// </remarks>
    [Collection("Shell")]
    public class EmbeddedToolbarTests
    {
        private readonly ShellHost _host;

        /// <summary>Takes the shared shell thread.</summary>
        /// <param name="host">The thread every shell in this collection is built on.</param>
        public EmbeddedToolbarTests(ShellHost host)
        {
            _host = host;
        }

        [Fact]
        public void TheToolbarIsTheTopmostElementOfBothMenus()
        {
            _host.Run(() =>
            {
                var shell = Built();

                foreach (string name in new[] { "Trace", "Marker" })
                {
                    MenuItem menu = Menu(shell, name);

                    Assert.True(
                        menu.Items.Count > 0, "The " + name + " menu is empty.");

                    Assert.True(
                        menu.Items[0] is ToolBar,
                        "REQ-UI-062: the " + name + " menu's topmost element is " +
                        menu.Items[0].GetType().Name + ", not a toolbar.");
                }
            });
        }

        [Fact]
        public void ItIsAToolbarAndNotAListOfCommandsStandingInForOne()
        {
            // The criterion says so in as many words. A submenu of items would satisfy a test that
            // only looked for the commands, and would cost a second click every time.
            _host.Run(() =>
            {
                var shell = Built();

                foreach (string name in new[] { "Trace", "Marker" })
                {
                    var bar = (ToolBar)Menu(shell, name).Items[0];

                    Assert.NotEmpty(bar.Items.OfType<ButtonBase>());
                    Assert.Empty(bar.Items.OfType<MenuItem>());
                }
            });
        }

        [Fact]
        public void TheTraceToolbarSelectsAddsRemovesAndHides()
        {
            // "The trace toolbar selects the active trace and adds, removes and hides traces."
            _host.Run(() =>
            {
                var shell = Built();
                var bar = (ToolBar)Menu(shell, "Trace").Items[0];

                var chooser = bar.Items.OfType<ComboBox>().FirstOrDefault();

                Assert.True(chooser != null, "The trace toolbar cannot select a trace.");
                Assert.Single(shell.DocumentArea.Traces);

                // Adds.
                Press(bar, "New");
                Assert.Equal(2, shell.DocumentArea.Traces.Count);
                Assert.Equal(2, chooser.Items.Count);

                // Selects: the chooser drives the active trace, and follows it back.
                chooser.SelectedIndex = 0;
                Assert.Equal(shell.DocumentArea.Traces[0], shell.DocumentArea.ActiveTrace);

                shell.DocumentArea.ActiveTrace = shell.DocumentArea.Traces[1];
                Assert.Equal(1, chooser.SelectedIndex);

                // Hides — which is not removing: the trace stays open, and stays in the chooser.
                //
                // Setting IsChecked is the whole press: the button acts on Checked and Unchecked, so
                // that it works for an automation client as well as a mouse. It used to be raised as
                // a Click as well, which did nothing and hid the fact that Click was the only thing
                // driving it. See ShellMenuBinding.ShowActiveTrace.
                char hidden = shell.DocumentArea.ActiveTrace;
                var hide = (ToggleButton)Control(bar, "Hide");

                hide.IsChecked = true;

                Assert.False(shell.DocumentArea.IsVisible(hidden));
                Assert.Contains(hidden, shell.DocumentArea.Traces);
                Assert.Equal(2, chooser.Items.Count);
                Assert.Contains(
                    chooser.Items.Cast<string>(), i => i.Contains("hidden"));

                // And back again.
                shell.DocumentArea.ActiveTrace = hidden;
                hide.IsChecked = false;

                Assert.True(shell.DocumentArea.IsVisible(hidden));

                // Removes.
                Press(bar, "Close");
                Assert.Single(shell.DocumentArea.Traces);
            });
        }

        [Fact]
        public void TheLastVisibleTraceCannotBeHidden()
        {
            // An empty document area is a state with no way out of it from the document area.
            _host.Run(() =>
            {
                var shell = Built();
                var bar = (ToolBar)Menu(shell, "Trace").Items[0];
                var hide = (ToggleButton)Control(bar, "Hide");

                Assert.Single(shell.DocumentArea.Traces);

                hide.IsChecked = true;

                Assert.True(shell.DocumentArea.IsVisible(shell.DocumentArea.ActiveTrace));
                Assert.False(hide.IsChecked == true);
            });
        }

        [Fact]
        public void TheMarkerToolbarSelectsAddsRemovesAndHides()
        {
            // "The markers toolbar does the equivalent for markers."
            _host.Run(() =>
            {
                var shell = Built();
                var bar = (ToolBar)Menu(shell, "Marker").Items[0];

                var chooser = bar.Items.OfType<ComboBox>().FirstOrDefault();

                Assert.True(chooser != null, "The markers toolbar cannot select a marker.");

                // Adds. Two, so that selecting between them means something.
                Press(bar, "New");
                Press(bar, "New");

                Assert.Equal(2, shell.Markers.Markers.Count);
                Assert.Equal(2, chooser.Items.Count);

                // Selects.
                chooser.SelectedIndex = 0;
                Assert.True(shell.Markers.Markers[0].IsSelected);

                // Hides — the marker keeps its number and its position.
                var hide = (ToggleButton)Control(bar, "Hide");
                double where = shell.Markers.Markers[0].XHz;

                hide.IsChecked = true;

                Assert.False(shell.Markers.Markers[0].IsVisible);
                Assert.Equal(2, shell.Markers.Markers.Count);
                Assert.Equal(where, shell.Markers.Markers[0].XHz);

                hide.IsChecked = false;

                Assert.True(shell.Markers.Markers[0].IsVisible);

                // Removes.
                Press(bar, "Delete");
                Assert.Single(shell.Markers.Markers);
            });
        }

        [Fact]
        public void ActingOnTheToolbarDoesNotDismissTheMenu()
        {
            // The criterion's last clause, and the one that decides whether embedding a toolbar was
            // worth doing: "acting on the embedded toolbar takes effect without first dismissing
            // the menu". Nothing on these toolbars is a MenuItem, which is what keeps it open.
            _host.Run(() =>
            {
                ShellWindow shell = Shown();

                try
                {
                    MenuItem menu = Menu(shell, "Trace");
                    var bar = (ToolBar)menu.Items[0];

                    menu.IsSubmenuOpen = true;
                    Assert.True(menu.IsSubmenuOpen);

                    Press(bar, "New");

                    Assert.True(
                        menu.IsSubmenuOpen,
                        "Adding a trace from the embedded toolbar closed the Trace menu.");

                    Assert.Equal(2, shell.DocumentArea.Traces.Count);

                    Press(bar, "Close");

                    Assert.True(
                        menu.IsSubmenuOpen,
                        "Closing a trace from the embedded toolbar closed the Trace menu.");

                    menu.IsSubmenuOpen = false;
                }
                finally
                {
                    shell.Close();
                }
            });
        }

        [Fact]
        public void HidingATraceLeavesItMeasuringAndOffTheArrangement()
        {
            // The distinction the requirement is asking for: hidden is not closed. The layout is
            // over the visible traces, and the hidden one still has a plot being fed.
            _host.Run(() =>
            {
                var shell = Built();

                shell.DocumentArea.AddTrace('B');
                shell.DocumentArea.ActiveTrace = 'B';

                Assert.Equal(2, shell.DocumentArea.VisibleTraces.Count);

                Assert.True(shell.DocumentArea.SetVisible('B', false));

                Assert.Equal(2, shell.DocumentArea.Traces.Count);
                Assert.Equal(1, shell.DocumentArea.VisibleTraces.Count);
                Assert.NotNull(shell.DocumentArea.PlotOf('B'));

                // Hiding the active trace moves the selection to one that is still on screen: a
                // command aimed at an invisible trace looks like a command that does nothing.
                Assert.Equal('A', shell.DocumentArea.ActiveTrace);

                Assert.True(shell.DocumentArea.SetVisible('B', true));
                Assert.Equal(2, shell.DocumentArea.VisibleTraces.Count);
            });
        }

        [Fact]
        public void TheEmbeddedToolbarsAreStillEntriesOfTheirMenus()
        {
            // REQ-UI-061 lists "(embedded trace toolbar)" and "(embedded markers toolbar)" as
            // entries in their own right. Moving them to the top does not remove them from that
            // list — and the list is exact, so an extra item would fail elsewhere.
            foreach (string menu in new[] { "Trace", "Marker" })
            {
                IReadOnlyList<ShellMenuEntry> items = ShellMenuTable.For(menu).Items;

                Assert.Equal(ShellMenuEntryKind.EmbeddedToolbar, items[0].Kind);

                Assert.Single(items.Where(e => e.Kind == ShellMenuEntryKind.EmbeddedToolbar));
            }
        }

        // ---- Helpers ---------------------------------------------------------------------------

        [Fact]
        public void RedrawingDoesNotRebuildTheMarkerChooserWhenNothingAboutItChanged()
        {
            // RefreshMarkers runs on every drawn frame, so this ran sixty times a second -- and with
            // no markers placed, which is the ordinary state, every pass cleared the chooser, put
            // "No markers" back and set SelectedIndex to 0 again. Each pass drives a -1 -> 0
            // selection transition and raises SelectionChanged twice, to arrive at what was already
            // displayed.
            //
            // Counting SelectionChanged is what makes this discriminate: an assertion on the items
            // would pass either way, because the rebuild put back exactly what it removed.
            _host.Run(() =>
            {
                var shell = Built();
                var bar = (ToolBar)Menu(shell, "Marker").Items[0];
                var chooser = bar.Items.OfType<ComboBox>().First();

                shell.RefreshMarkers();

                int changes = 0;
                chooser.SelectionChanged += (sender, e) => changes++;

                for (int frame = 0; frame < 20; frame++)
                {
                    shell.RefreshMarkers();
                }

                Assert.Equal(0, changes);

                // And it is still showing the right thing, rather than having been skipped into
                // being empty.
                Assert.Equal(1, chooser.Items.Count);
                Assert.Equal(0, chooser.SelectedIndex);
            });
        }

        [Fact]
        public void TheMarkerChooserStillFollowsAMarkerBeingAddedAndHidden()
        {
            // The other half. A guard that never rebuilds would pass the test above and leave the
            // chooser permanently reading "No markers" -- so each thing the entries depend on has
            // to be shown to still reach them.
            _host.Run(() =>
            {
                var shell = Built();
                var bar = (ToolBar)Menu(shell, "Marker").Items[0];
                var chooser = bar.Items.OfType<ComboBox>().First();

                shell.RefreshMarkers();
                Assert.Equal("No markers", chooser.Items[0]);

                Marker marker = shell.Markers.AddNormal(1.0e9);
                shell.RefreshMarkers();

                Assert.Equal(1, chooser.Items.Count);
                Assert.Equal("Mkr 1", chooser.Items[0]);
                Assert.Equal(0, chooser.SelectedIndex);

                // Visibility is set on the marker itself rather than through the set, which is
                // exactly what a version counter on the set would have missed.
                marker.IsVisible = false;
                shell.RefreshMarkers();

                Assert.Equal("Mkr 1 (hidden)", chooser.Items[0]);

                shell.Markers.Remove(marker);
                shell.RefreshMarkers();

                Assert.Equal("No markers", chooser.Items[0]);
            });
        }

        [Fact]
        public void TheActiveTraceWindowIsFramedInTheAccentColour()
        {
            // REQ-UI-083's Accent key is "the colour that marks the thing currently in force", and
            // with several windows tiled the thing in force is the one the shell drives. It had no
            // consumer anywhere in the tree until this, so the key was complete in both shipped
            // themes and reached nothing on screen -- see #408.
            //
            // The second half is the part that would have been missed: selecting a trace does not
            // rebuild the arrangement, so a frame coloured only where the cell is built keeps the
            // accent on whichever window was active when the layout was last laid out.
            _host.Run(() =>
            {
                var shell = Built();

                TraceDocumentArea area = shell.DocumentArea;

                area.AddTrace('B');

                // Each trace in its own cell. Left in the default arrangement the two share one tab
                // group, so they share one frame — and a frame round a cell holding the active
                // trace is accented whichever tab is on top, which is right but says nothing about
                // the accent moving.
                area.ApplyLayout(TraceLayoutPreset.Stack(2));

                area.ActiveTrace = 'A';

                Assert.Equal(ChromeKeys.Accent, area.FrameKeyFor('A'));
                Assert.Equal(ChromeKeys.Border, area.FrameKeyFor('B'));

                area.ActiveTrace = 'B';

                Assert.Equal(ChromeKeys.Border, area.FrameKeyFor('A'));
                Assert.Equal(ChromeKeys.Accent, area.FrameKeyFor('B'));
            });
        }

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

            throw new InvalidOperationException("There is no '" + name + "' menu.");
        }

        private static FrameworkElement Control(ToolBar bar, string caption)
        {
            foreach (object child in bar.Items)
            {
                var content = child as ContentControl;

                if (content != null &&
                    string.Equals(content.Content as string, caption, StringComparison.Ordinal))
                {
                    return content;
                }
            }

            throw new InvalidOperationException("'" + caption + "' is not on the toolbar.");
        }

        private static void Press(ToolBar bar, string caption) =>
            ((ButtonBase)Control(bar, caption))
                .RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
    }
}
