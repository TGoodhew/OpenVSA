using System;
using System.Collections.Generic;
using OpenVSA.Measurement.State;
using OpenVSA.Ui.Dialogs;
using OpenVSA.Ui.Rendering;
using Xunit;

namespace OpenVSA.Ui.Tests
{
    /// <summary>
    /// <c>REQ-UI-073</c>: the Display Preferences tab set.
    /// </summary>
    public class DisplayPreferencesDialogTests
    {
        [Fact]
        public void TheDialogHasExactlyTheFiveTabs()
        {
            Sta.Run(() => Assert.Equal(
                new[] { "Trace", "Colour", "User Map Colour", "Font", "Window" },
                new List<string>(Preferences().PageNames)));
        }

        [Fact]
        public void ThereIsNoGeneralThemeOrAppearanceTab()
        {
            // The requirement's criterion names these three because adding one is the natural
            // instinct: it would split theming away from Colour and Window, which is where this
            // specification deliberately puts it.
            Sta.Run(() =>
            {
                foreach (string name in Preferences().PageNames)
                {
                    Assert.False(
                        string.Equals(name, "General", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(name, "Theme", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(name, "Appearance", StringComparison.OrdinalIgnoreCase),
                        "REQ-UI-073 forbids a tab named '" + name + "'.");
                }
            });
        }

        [Fact]
        public void TheTabListIsWhatTheDialogIsBuiltFrom()
        {
            // Not two lists that agree today. A sixth tab added to the dialog without being added
            // to the published list would fail this, which is the drift the criterion is about.
            Sta.Run(() => Assert.Equal(
                new List<string>(DisplayPreferencesDialog.TabNames),
                new List<string>(Preferences().PageNames)));
        }

        [Fact]
        public void ColourExposesTheWholeElementSet()
        {
            Sta.Run(() =>
            {
                var colours = new ColourPreferences();
                DisplayPreferencesDialog dialog = Preferences(colours);

                Assert.Equal(colours.Entries.Count, dialog.Colour.ListedCount);
            });
        }

        [Fact]
        public void UserMapColourExposesTheUserMap()
        {
            Sta.Run(() =>
            {
                DisplayPreferencesDialog dialog = Preferences();

                // Seeded from the map in force, so the tab opens on the colouring being looked at.
                Assert.Equal(SpectrogramColourMap.StandardEntryCount, dialog.UserMap.Map.Count);
                Assert.Equal(SpectrogramColourMapKind.UserDefined, dialog.UserMap.Map.Kind);

                // And the count discards from the top, which is REQ-UI-024's surprising direction:
                // the minimum keeps its colour however few entries are kept.
                PlotColor minimum = dialog.UserMap.Map.Minimum;

                dialog.UserMap.Count = 8;

                Assert.Equal(8, dialog.UserMap.Map.Count);
                Assert.Equal(minimum, dialog.UserMap.Map.Minimum);
            });
        }

        [Fact]
        public void FontExposesTheThreeSlots()
        {
            Sta.Run(() =>
            {
                var fonts = new FontPreferences();
                DisplayPreferencesDialog dialog = Preferences(fonts: fonts);

                foreach (FontSlot slot in FontPreferences.Slots)
                {
                    Assert.False(string.IsNullOrEmpty(dialog.Font.DescriptionOf(slot)));
                }

                dialog.Font.ChooseSize(FontSlot.Marker, 14.0);

                Assert.Equal(14.0, fonts[FontSlot.Marker].SizePoints, 3);
                Assert.Equal(9.0, fonts[FontSlot.Annotation].SizePoints, 3);
                Assert.Equal(9.0, fonts[FontSlot.Tabular].SizePoints, 3);
            });
        }

        [Fact]
        public void WindowExposesTheFrameworkOptions()
        {
            Sta.Run(() =>
            {
                var options = new DialogFrameworkOptions();
                DisplayPreferencesDialog dialog = Preferences(options: options);

                Assert.Same(options, dialog.Window.Options);

                // Tabs Collapsed by Default says where it applies, under every mode.
                options.DefaultMode = DialogMode.ExpandersVertical;
                Assert.Contains("Tabs on left", dialog.Window.CollapsedNote);

                options.DefaultMode = DialogMode.TabsOnLeft;
                Assert.Contains("left", dialog.Window.CollapsedNote);
            });
        }

        [Fact]
        public void ChangesMadeHerePersistPerReqUi014()
        {
            Sta.Run(() =>
            {
                var colours = new ColourPreferences();
                var fonts = new FontPreferences();

                DisplayPreferencesDialog dialog = Preferences(colours, fonts);

                Assert.True(dialog.Colour.Select("OpenVSA.Grid"));
                colours.Set("OpenVSA.Grid", new PlotColor(1, 2, 3));
                dialog.Font.ChooseSize(FontSlot.Tabular, 12.0);

                var state = new DisplayPreferencesState();
                colours.SaveInto(state);
                fonts.SaveInto(state);

                var restoredColours = new ColourPreferences();
                var restoredFonts = new FontPreferences();

                Assert.Empty(restoredColours.LoadFrom(state));
                Assert.Empty(restoredFonts.LoadFrom(state));

                Assert.Equal(new PlotColor(1, 2, 3), restoredColours.Colour("OpenVSA.Grid"));
                Assert.Equal(12.0, restoredFonts[FontSlot.Tabular].SizePoints, 3);
            });
        }

        [Fact]
        public void TheDialogNeedsEveryPreferenceItEdits()
        {
            Sta.Run(() =>
            {
                var options = new DialogFrameworkOptions();
                var colours = new ColourPreferences();
                var fonts = new FontPreferences();
                var traces = new TraceDisplayOptions();
                SpectrogramColourMap map = SpectrogramColourMap.Default;

                Assert.Throws<ArgumentNullException>(
                    () => new DisplayPreferencesDialog(options, null, fonts, traces, map));

                Assert.Throws<ArgumentNullException>(
                    () => new DisplayPreferencesDialog(options, colours, null, traces, map));

                Assert.Throws<ArgumentNullException>(
                    () => new DisplayPreferencesDialog(options, colours, fonts, null, map));

                Assert.Throws<ArgumentNullException>(
                    () => new DisplayPreferencesDialog(options, colours, fonts, traces, null));
            });
        }

        private static DisplayPreferencesDialog Preferences(
            ColourPreferences colours = null,
            FontPreferences fonts = null,
            DialogFrameworkOptions options = null) =>
            new DisplayPreferencesDialog(
                options ?? new DialogFrameworkOptions(),
                colours ?? new ColourPreferences(),
                fonts ?? new FontPreferences(),
                new TraceDisplayOptions(),
                SpectrogramColourMap.Default);
    }
}
