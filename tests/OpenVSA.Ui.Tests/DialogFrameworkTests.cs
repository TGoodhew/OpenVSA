using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using OpenVSA.Measurement.State;
using OpenVSA.Ui.Dialogs;
using Xunit;

namespace OpenVSA.Ui.Tests
{
    /// <summary>
    /// <c>REQ-UI-071</c>: the dialog framework's four modes and five options.
    /// </summary>
    public class DialogFrameworkTests
    {
        [Fact]
        public void TheFourModesAreNamedAsTheRequirementNamesThem()
        {
            Assert.Equal(
                new[] { "Tabs on top", "Tabs on left", "Expanders vertical", "Expanders horizontal" },
                new List<string>(Names()));
        }

        [Fact]
        public void EveryModeRendersEveryPage()
        {
            // The criterion: "All four Default Modes render the same content and every control
            // reachable in one is reachable in the others." The pages are the same objects in every
            // mode, so what this proves is that each one is actually attached — the failure it
            // catches is a rendering that silently drops a page, which looks fine until the page
            // someone needed is the missing one.
            Sta.Run(() =>
            {
                FrameworkElement[] pages = { Page(120.0, 80.0), Page(200.0, 60.0), Page(90.0, 150.0) };
                SettingsDialog dialog = DialogOver(new DialogFrameworkOptions(), pages);

                foreach (DialogMode mode in DialogModes.All)
                {
                    dialog.Mode = mode;

                    foreach (FrameworkElement page in pages)
                    {
                        Assert.True(
                            dialog.IsReachable(page),
                            "A page is not reachable under " + DialogModes.NameOf(mode) + ".");
                    }
                }
            });
        }

        [Fact]
        public void EveryControlOnAPageIsReachableInEveryMode()
        {
            // One level deeper than the page itself: a mode that attached the page but not its
            // contents would pass the test above.
            Sta.Run(() =>
            {
                var button = new Button { Content = "Deep" };
                var panel = new StackPanel();
                panel.Children.Add(button);

                SettingsDialog dialog = DialogOver(
                    new DialogFrameworkOptions(), new FrameworkElement[] { panel, Page(80.0, 80.0) });

                foreach (DialogMode mode in DialogModes.All)
                {
                    dialog.Mode = mode;

                    Assert.True(
                        dialog.IsReachable(button),
                        "A control inside a page is unreachable under " + DialogModes.NameOf(mode));
                }
            });
        }

        [Fact]
        public void FixedSizeIsTheUnionOfEveryPage()
        {
            // "the size equals the union of all tabs, so the largest tab is not clipped" - and the
            // union is genuinely a union: the widest page and the tallest page are different pages
            // here, which is the case a max-of-one-dimension implementation gets wrong.
            Sta.Run(() =>
            {
                var options = new DialogFrameworkOptions { FixedSize = true };

                SettingsDialog dialog = DialogOver(
                    options, new FrameworkElement[] { Page(300.0, 100.0), Page(120.0, 260.0) });

                Size union = dialog.FixedContentSize;

                Assert.True(union.Width >= 300.0, "The widest page would be clipped: " + union);
                Assert.True(union.Height >= 260.0, "The tallest page would be clipped: " + union);

                // Every page carries the union as its own minimum, so the presentation's chrome —
                // a tab strip is chrome — adds the same amount whichever page is in front.
                foreach (SettingsPage page in dialog.Pages)
                {
                    Assert.Equal(union.Width, page.Content.MinWidth, 3);
                    Assert.Equal(union.Height, page.Content.MinHeight, 3);
                }

                // And turning it off gives each page back the minimum its author set.
                options.FixedSize = false;

                foreach (SettingsPage page in dialog.Pages)
                {
                    Assert.Equal(0.0, page.Content.MinWidth, 3);
                    Assert.Equal(0.0, page.Content.MinHeight, 3);
                }
            });
        }

        [Fact]
        public void WithFixedSizeOnSwitchingTabsDoesNotResizeTheDialog()
        {
            // Laid out for real, on screen. An unshown WPF window has no template applied to its
            // tab control, so the selected page contributes nothing to a measure and both readings
            // would agree for the wrong reason - the test would pass on an implementation that
            // ignored Fixed Size entirely.
            Sta.Run(() =>
            {
                var options = new DialogFrameworkOptions { FixedSize = true };

                SettingsDialog dialog = DialogOver(
                    options, new FrameworkElement[] { Page(300.0, 100.0), Page(120.0, 260.0) });

                Rect first = Shown(dialog, 0);
                Rect second = Shown(dialog, 1);

                Assert.Equal(first.Width, second.Width, 0);
                Assert.Equal(first.Height, second.Height, 0);

                // And large enough for both, which is the half of the criterion that says the
                // largest tab is not clipped.
                Assert.True(first.Width >= 300.0, "The widest page is clipped: " + first);
                Assert.True(first.Height >= 260.0, "The tallest page is clipped: " + first);

                Close(dialog);
            });
        }

        [Fact]
        public void WithFixedSizeOffTheDialogFollowsThePageInFront()
        {
            // The discriminating half. Without this, a dialog that ignored Fixed Size entirely and
            // always sized to the union would pass the test above, and the option would be inert.
            Sta.Run(() =>
            {
                var options = new DialogFrameworkOptions { FixedSize = false };

                SettingsDialog dialog = DialogOver(
                    options, new FrameworkElement[] { Page(300.0, 100.0), Page(120.0, 260.0) });

                Rect first = Shown(dialog, 0);
                Rect second = Shown(dialog, 1);

                Assert.True(
                    Math.Abs(first.Width - second.Width) > 1.0 ||
                    Math.Abs(first.Height - second.Height) > 1.0,
                    "With Fixed Size off the dialog should follow the page in front; it did not. " +
                    first + " then " + second);

                Assert.Equal(Size.Empty, dialog.FixedContentSize);

                Close(dialog);
            });
        }

        [Fact]
        public void KeepOnTopGovernsWhetherADialogCanFallBehind()
        {
            // Both ways, as the criterion asks. Topmost is what Windows actually consults, so this
            // is the property to assert rather than a flag of our own that mirrors it.
            Sta.Run(() =>
            {
                var options = new DialogFrameworkOptions { KeepOnTop = true };
                SettingsDialog dialog = DialogOver(options, new[] { Page(80.0, 80.0) });

                Assert.True(dialog.Topmost);

                options.KeepOnTop = false;
                Assert.False(dialog.Topmost);

                options.KeepOnTop = true;
                Assert.True(dialog.Topmost);
            });
        }

        [Fact]
        public void PersistModeRestoresTheModeADialogWasClosedInAcrossARestart()
        {
            // Through a real file, because "across restarts, not merely within a session" is a
            // claim about what survives the process - and an in-memory round trip would prove
            // nothing about that.
            string path = Path.Combine(
                Path.GetTempPath(), "openvsa-dialogs-" + Guid.NewGuid().ToString("N") + ".json");

            Sta.Run(() =>
            {
                try
                {
                    var before = new DialogFrameworkOptions { PersistMode = true };

                    SettingsDialog dialog = DialogOver(before, new[] { Page(80.0, 80.0) });
                    dialog.Mode = DialogMode.ExpandersVertical;

                    var saved = new DisplayPreferencesState();
                    before.SaveInto(saved);
                    SidecarFile.Save(saved, path);

                    // The restart.
                    var after = new DialogFrameworkOptions();
                    after.LoadFrom(SidecarFile.Load<DisplayPreferencesState>(path));

                    SettingsDialog reopened = DialogOver(after, new[] { Page(80.0, 80.0) });

                    Assert.Equal(DialogMode.ExpandersVertical, reopened.Mode);
                }
                finally
                {
                    File.Delete(path);
                }
            });
        }

        [Fact]
        public void WithPersistModeOffADialogOpensInTheDefaultMode()
        {
            Sta.Run(() =>
            {
                var options = new DialogFrameworkOptions
                {
                    PersistMode = true,
                    DefaultMode = DialogMode.TabsOnTop,
                };

                SettingsDialog dialog = DialogOver(options, new[] { Page(80.0, 80.0) });
                dialog.Mode = DialogMode.ExpandersHorizontal;

                options.PersistMode = false;

                SettingsDialog reopened = DialogOver(options, new[] { Page(80.0, 80.0) });

                Assert.Equal(DialogMode.TabsOnTop, reopened.Mode);

                // Turning it off stops the memory being consulted; it does not erase it.
                options.PersistMode = true;

                SettingsDialog again = DialogOver(options, new[] { Page(80.0, 80.0) });
                Assert.Equal(DialogMode.ExpandersHorizontal, again.Mode);
            });
        }

        [Fact]
        public void TabsCollapsedByDefaultAppliesToTabsOnLeftAndIsInertElsewhere()
        {
            Sta.Run(() =>
            {
                var options = new DialogFrameworkOptions { TabsCollapsedByDefault = true };
                SettingsDialog dialog = DialogOver(options, new[] { Page(80.0, 80.0) });

                foreach (DialogMode mode in DialogModes.All)
                {
                    dialog.Mode = mode;

                    Assert.Equal(mode == DialogMode.TabsOnLeft, dialog.AreTabsCollapsed);
                }
            });
        }

        [Fact]
        public void ACollapsedTabStripStillReachesEveryPage()
        {
            Sta.Run(() =>
            {
                var options = new DialogFrameworkOptions
                {
                    TabsCollapsedByDefault = true,
                    DefaultMode = DialogMode.TabsOnLeft,
                };

                FrameworkElement[] pages = { Page(80.0, 80.0), Page(90.0, 70.0), Page(70.0, 90.0) };
                SettingsDialog dialog = DialogOver(options, pages);

                Assert.True(dialog.AreTabsCollapsed);

                foreach (FrameworkElement page in pages)
                {
                    Assert.True(dialog.IsReachable(page));
                }
            });
        }

        [Fact]
        public void WithTheOptionOffTheStripIsNotCollapsed()
        {
            Sta.Run(() =>
            {
                var options = new DialogFrameworkOptions
                {
                    TabsCollapsedByDefault = false,
                    DefaultMode = DialogMode.TabsOnLeft,
                };

                SettingsDialog dialog = DialogOver(options, new[] { Page(80.0, 80.0) });

                Assert.False(dialog.AreTabsCollapsed);
            });
        }

        [Fact]
        public void TheOptionsSurviveTheSidecar()
        {
            var before = new DialogFrameworkOptions
            {
                DefaultMode = DialogMode.ExpandersHorizontal,
                FixedSize = false,
                KeepOnTop = true,
                PersistMode = false,
                TabsCollapsedByDefault = true,
            };

            var state = new DisplayPreferencesState();
            before.SaveInto(state);

            var after = new DialogFrameworkOptions();
            Assert.Empty(after.LoadFrom(state));

            Assert.Equal(DialogMode.ExpandersHorizontal, after.DefaultMode);
            Assert.False(after.FixedSize);
            Assert.True(after.KeepOnTop);
            Assert.False(after.PersistMode);
            Assert.True(after.TabsCollapsedByDefault);
        }

        [Fact]
        public void AnUnknownModeNameCostsThatSettingAndNothingElse()
        {
            // A preferences file written by a later version names a mode this build has never heard
            // of. The user should lose their tab placement over that, not their whole file.
            var state = new DisplayPreferencesState
            {
                Dialogs = new DialogFrameworkState
                {
                    DefaultMode = "Tabs on the ceiling",
                    KeepOnTop = true,
                    Modes = new List<DialogModeState>
                    {
                        new DialogModeState { Dialog = "Display Preferences", Mode = "Expanders vertical" },
                        new DialogModeState { Dialog = "Analysis", Mode = "Diagonal" },
                    },
                },
            };

            var options = new DialogFrameworkOptions();
            IReadOnlyList<string> unknown = options.LoadFrom(state);

            Assert.Equal(new[] { "Tabs on the ceiling", "Diagonal" }, new List<string>(unknown));
            Assert.True(options.KeepOnTop);
            Assert.Equal(DialogMode.TabsOnTop, options.DefaultMode);
            Assert.Equal(DialogMode.ExpandersVertical, options.ModeFor("Display Preferences"));
        }

        [Fact]
        public void ADialogNeedsANameAndOptions()
        {
            Sta.Run(() =>
            {
                Assert.Throws<ArgumentException>(
                    () => new SettingsDialog("  ", new DialogFrameworkOptions()));

                Assert.Throws<ArgumentNullException>(() => new SettingsDialog("Settings", null));
            });
        }

        [Fact]
        public void TwoPagesCannotShareAName()
        {
            Sta.Run(() =>
            {
                var dialog = new SettingsDialog("Settings", new DialogFrameworkOptions());

                dialog.AddPage("Colour", Page(80.0, 80.0));

                Assert.Throws<ArgumentException>(
                    () => dialog.AddPage("colour", Page(80.0, 80.0)));
            });
        }

        private static IEnumerable<string> Names()
        {
            foreach (DialogMode mode in DialogModes.All)
            {
                yield return DialogModes.NameOf(mode);
            }
        }

        private static SettingsDialog DialogOver(
            DialogFrameworkOptions options, IReadOnlyList<FrameworkElement> pages)
        {
            var dialog = new SettingsDialog("Settings", options);

            for (int i = 0; i < pages.Count; i++)
            {
                dialog.AddPage("Page " + (i + 1), pages[i]);
            }

            return dialog;
        }

        /// <summary>A page of a known size, so a union can be asserted against a number.</summary>
        private static FrameworkElement Page(double width, double height) =>
            new Border { Width = width, Height = height };

        /// <summary>
        /// Shows the dialog if it is not already up, selects a page and lays it out for real.
        /// </summary>
        /// <remarks>
        /// Off screen — far enough that a test run does not flash windows over whatever is being
        /// worked on — but genuinely shown, because that is what applies the control templates that
        /// decide the size.
        /// </remarks>
        private static Rect Shown(SettingsDialog dialog, int page)
        {
            if (!dialog.IsVisible)
            {
                dialog.WindowStartupLocation = WindowStartupLocation.Manual;
                dialog.Left = -4000.0;
                dialog.Top = -4000.0;
                dialog.ShowInTaskbar = false;
                dialog.Show();
            }

            dialog.SelectedIndex = page;
            dialog.UpdateLayout();

            return new Rect(0.0, 0.0, dialog.ActualWidth, dialog.ActualHeight);
        }

        private static void Close(SettingsDialog dialog)
        {
            dialog.Close();
        }
    }
}
