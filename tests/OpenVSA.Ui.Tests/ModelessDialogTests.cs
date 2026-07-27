using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using OpenVSA.Ui.Dialogs;
using OpenVSA.Ui.Dialogs.Pages;
using OpenVSA.Ui.HotSpots;
using OpenVSA.Ui.Rendering;
using Xunit;

namespace OpenVSA.Ui.Tests
{
    /// <summary>
    /// <c>REQ-UI-070</c>: setting dialogs are tabbed, modeless and live.
    /// </summary>
    public class ModelessDialogTests
    {
        [Fact]
        public void ASettingDialogRefusesToBeShownModally()
        {
            // The requirement's first criterion is that no setting dialog is modal. A modal one
            // stops the measurement updating behind it and puts the hot spots out of reach, so this
            // fails at the call rather than leaving the mistake to be noticed on a bench.
            Sta.Run(() =>
            {
                var dialog = new SettingsDialog("Settings", new DialogFrameworkOptions());
                dialog.AddPage("Page", new Border { Width = 80.0, Height = 80.0 });

                InvalidOperationException refused =
                    Assert.Throws<InvalidOperationException>(() => dialog.ShowDialog());

                Assert.Contains("modeless", refused.Message, StringComparison.OrdinalIgnoreCase);
            });
        }

        [Fact]
        public void DisplayPreferencesHasNoOkApplyOrCancelButton()
        {
            // "a test asserts ... that no such button exists on the dialog". Over the real dialog
            // with all five of its pages built, because the button that would creep in is one a
            // page brought with it.
            Sta.Run(() => Assert.Equal(0, Preferences().CommitButtonCount));
        }

        [Fact]
        public void TheButtonCounterWouldNoticeOneIfItWereThere()
        {
            // The discriminator. A counter that always returned zero would pass the test above,
            // and the requirement would be asserted by a method that cannot fail.
            Sta.Run(() =>
            {
                var page = new StackPanel();
                page.Children.Add(new Button { Content = "Apply" });

                var dialog = new SettingsDialog("Settings", new DialogFrameworkOptions());
                dialog.AddPage("Page", page);

                Assert.Equal(1, dialog.CommitButtonCount);
            });
        }

        [Fact]
        public void AHotSpotAndItsDialogDriveOnePieceOfState()
        {
            // The third criterion, both ways round: "each surface reflects a change made from the
            // other without needing to be reopened".
            Sta.Run(() =>
            {
                var value = NumericHotSpotValue.Frequency(1e9, 1e6);
                var spot = new HotSpot { Label = "Center ", Value = value };
                var dialog = new ValueEntryDialog("Center ", value);

                // Typed in the dialog; read on the trace.
                dialog.EntryText = "1.5 GHz";

                Assert.Equal(1.5e9, value.Value, 0);
                Assert.Equal("Center " + value.Text, spot.Text);

                // Nudged on the trace; read in the dialog.
                Assert.True(spot.Adjust(3));

                Assert.Equal(1.503e9, value.Value, 0);
                Assert.Equal(value.Text, dialog.EntryText);
            });
        }

        [Fact]
        public void TheEntryAppliesWithNoOkInvoked()
        {
            // Nothing is clicked here and nothing is committed: the value has already moved by the
            // time the text has been typed.
            Sta.Run(() =>
            {
                var value = NumericHotSpotValue.Decibels(-10.0);
                var dialog = new ValueEntryDialog("Ref ", value);

                dialog.EntryText = "-20 dBm";

                Assert.Equal(-20.0, value.Value, 3);
                Assert.Equal(string.Empty, dialog.Note);
                Assert.Equal(0, CommitButtons(dialog));
            });
        }

        [Fact]
        public void AnEntryThatIsNotUnderstoodSaysSoAndChangesNothing()
        {
            Sta.Run(() =>
            {
                var value = NumericHotSpotValue.Decibels(-10.0);
                var dialog = new ValueEntryDialog("Ref ", value);

                dialog.EntryText = "somewhere";

                Assert.Equal(-10.0, value.Value, 3);
                Assert.Contains("not a value", dialog.Note);
            });
        }

        [Fact]
        public void RetypingTheValueItAlreadyHoldsIsNotAnError()
        {
            // TrySet answers two questions at once — understood, and changed — and reporting "no
            // change" as "not understood" would put an error under a perfectly good entry.
            Sta.Run(() =>
            {
                var value = NumericHotSpotValue.Decibels(-10.0);
                var dialog = new ValueEntryDialog("Ref ", value);

                dialog.EntryText = "-10.00 dBm";

                Assert.Equal(string.Empty, dialog.Note);
            });
        }

        [Fact]
        public void TheTraceTabAndTheDisplayMenuDriveOneSetting()
        {
            // The same criterion again, on a setting whose two surfaces are a tab and a menu item.
            // The tab is checked here; the shell's menu follows the same object's Changed event.
            Sta.Run(() =>
            {
                var options = new TraceDisplayOptions();
                var page = new TracePage(options);

                CheckBox failures = CheckBoxWith(page, "Indicate limit failures");

                Assert.True(failures.IsChecked);

                // Changed elsewhere: the tab follows without being rebuilt.
                options.IndicateLimitFailures = false;
                Assert.False(failures.IsChecked);

                // Changed on the tab: the setting follows.
                failures.IsChecked = true;
                Assert.True(options.IndicateLimitFailures);
            });
        }

        [Fact]
        public void ChangingAColourOnTheTabIsAppliedAtOnce()
        {
            Sta.Run(() =>
            {
                var colours = new ColourPreferences();
                DisplayPreferencesDialog dialog = Preferences(colours);

                int announced = 0;
                dialog.ColoursChanged += (sender, e) => announced++;

                Assert.True(dialog.Colour.Select("OpenVSA.Grid"));

                PlotColor was = colours.ColourOf("Grid");
                Slider red = SliderIn(dialog.Colour);

                red.Value = was.R == 200 ? 40.0 : 200.0;

                Assert.True(announced > 0, "The change was not announced as it was made.");
                Assert.NotEqual(was, colours.ColourOf("Grid"));
            });
        }

        private static DisplayPreferencesDialog Preferences(ColourPreferences colours = null) =>
            new DisplayPreferencesDialog(
                new DialogFrameworkOptions(),
                colours ?? new ColourPreferences(),
                new FontPreferences(),
                new TraceDisplayOptions(),
                SpectrogramColourMap.Default);

        private static int CommitButtons(DependencyObject root)
        {
            int found = 0;

            var button = root as Button;
            var caption = button == null ? null : button.Content as string;

            if (caption != null &&
                (caption == "OK" || caption == "Apply" || caption == "Cancel"))
            {
                found++;
            }

            foreach (object child in LogicalTreeHelper.GetChildren(root))
            {
                var node = child as DependencyObject;

                if (node != null)
                {
                    found += CommitButtons(node);
                }
            }

            return found;
        }

        private static CheckBox CheckBoxWith(DependencyObject root, string caption)
        {
            foreach (CheckBox box in Descendants<CheckBox>(root))
            {
                var content = box.Content as string;

                if (content != null &&
                    content.IndexOf(caption, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return box;
                }
            }

            throw new InvalidOperationException("No check box reading '" + caption + "'.");
        }

        private static Slider SliderIn(DependencyObject root)
        {
            foreach (Slider slider in Descendants<Slider>(root))
            {
                return slider;
            }

            throw new InvalidOperationException("The page has no slider.");
        }

        private static IEnumerable<T> Descendants<T>(DependencyObject root)
            where T : DependencyObject
        {
            var match = root as T;

            if (match != null)
            {
                yield return match;
            }

            foreach (object child in LogicalTreeHelper.GetChildren(root))
            {
                var node = child as DependencyObject;

                if (node == null)
                {
                    continue;
                }

                foreach (T found in Descendants<T>(node))
                {
                    yield return found;
                }
            }
        }
    }
}
