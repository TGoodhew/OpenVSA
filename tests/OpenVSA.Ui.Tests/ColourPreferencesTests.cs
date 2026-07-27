using System;
using System.Collections.Generic;
using System.Linq;
using OpenVSA.Measurement.State;
using OpenVSA.Ui.Rendering;
using Xunit;

namespace OpenVSA.Ui.Tests
{
    /// <summary>
    /// <c>REQ-UI-014</c>: colour configuration and persistence.
    /// </summary>
    public sealed class ColourPreferencesTests
    {
        [Fact]
        public void EveryThemeableElementIsReachableFromThePicker()
        {
            // The requirement's criterion, stated as "an element added without a picker entry
            // fails". It holds because the list is generated from the element set rather than
            // written out beside it — this test is what proves the two cannot be separated.
            var preferences = new ColourPreferences(traceCount: 4);
            var listed = new HashSet<string>(preferences.Entries.Select(e => e.Key), StringComparer.Ordinal);

            foreach (string key in ThemeElements.KeysFor(4))
            {
                Assert.True(listed.Contains(key), "No picker entry for " + key + ".");
            }
        }

        [Fact]
        public void ThePickerListsNothingThatIsNotAThemeableElement()
        {
            var preferences = new ColourPreferences(traceCount: 4);
            var known = new HashSet<string>(ThemeElements.KeysFor(4), StringComparer.Ordinal);

            foreach (ColourEntry entry in preferences.Entries)
            {
                Assert.True(known.Contains(entry.Key), entry.Key + " is not a themeable element.");
            }
        }

        [Fact]
        public void EveryPickerEntryHasItsOwnKey()
        {
            var preferences = new ColourPreferences(traceCount: 4);

            Assert.Equal(
                preferences.Entries.Count,
                preferences.Entries.Select(e => e.Key).Distinct(StringComparer.Ordinal).Count());
        }

        [Fact]
        public void PerTraceEntriesAreNamedWithTheirTraceLetter()
        {
            var preferences = new ColourPreferences(traceCount: 3);

            ColourEntry entry = preferences.Find("OpenVSA.Trace.C");

            Assert.NotNull(entry);
            Assert.Equal("Trace C", entry.DisplayName);
            Assert.Equal(2, entry.TraceIndex);
        }

        [Fact]
        public void GlobalEntriesAreNamedWithoutATraceLetter()
        {
            var preferences = new ColourPreferences();

            ColourEntry entry = preferences.Find("OpenVSA.Grid");

            Assert.NotNull(entry);
            Assert.Equal("Grid", entry.DisplayName);
            Assert.Equal(-1, entry.TraceIndex);
        }

        [Fact]
        public void TheDefaultsMatchWhatIsAlreadyOnScreen()
        {
            // The picker opens showing the colours in use, not a guess at them.
            var preferences = new ColourPreferences();

            Assert.Equal(PlotPalette.Dark.Grid, preferences.ColourOf("Grid"));
            Assert.Equal(PlotPalette.Dark.TraceBackground, preferences.ColourOf("Trace Background"));
            Assert.Equal(PlotPalette.Dark.Annotation, preferences.ColourOf("Annotation"));
            Assert.Equal(PlotPalette.Dark.Indicator, preferences.ColourOf("Indicator"));
        }

        [Fact]
        public void TheLimitDefaultsAreTheOnesRequUi023States()
        {
            var preferences = new ColourPreferences();

            Assert.Equal(new PlotColor(255, 0, 0), preferences.ColourOf("Limit"));
            Assert.Equal(new PlotColor(255, 255, 0), preferences.ColourOf("Margin"));
        }

        [Fact]
        public void EachTracesDefaultIsItsOwnColourFromTheTraceTable()
        {
            var preferences = new ColourPreferences(traceCount: 5);

            for (int trace = 0; trace < 5; trace++)
            {
                Assert.Equal(
                    TraceColours.ForIndex(trace),
                    preferences.Colour("OpenVSA.Trace." + TraceColours.LetterAt(trace)));
            }
        }

        [Fact]
        public void NothingIsChangedToStartWith()
        {
            var preferences = new ColourPreferences();

            Assert.Equal(0, preferences.ChangedCount);
            Assert.False(preferences.IsChanged("OpenVSA.Grid"));
        }

        [Fact]
        public void AChangedColourIsWhatIsReturned()
        {
            var preferences = new ColourPreferences();
            preferences.Set("OpenVSA.Grid", new PlotColor(1, 2, 3));

            Assert.Equal(new PlotColor(1, 2, 3), preferences.Colour("OpenVSA.Grid"));
            Assert.True(preferences.IsChanged("OpenVSA.Grid"));
            Assert.Equal(1, preferences.ChangedCount);
        }

        [Fact]
        public void ChangingOneColourLeavesTheOthersAlone()
        {
            var preferences = new ColourPreferences();
            preferences.Set("OpenVSA.Grid", new PlotColor(1, 2, 3));

            Assert.Equal(PlotPalette.Dark.Annotation, preferences.ColourOf("Annotation"));
        }

        [Fact]
        public void SettingAColourBackToItsDefaultDropsTheChange()
        {
            // A user who opens the picker, tries a colour and changes their mind leaves no trace in
            // the file — and ChangedCount answers "how much have I altered" truthfully.
            var preferences = new ColourPreferences();

            preferences.Set("OpenVSA.Grid", new PlotColor(1, 2, 3));
            preferences.Set("OpenVSA.Grid", PlotPalette.Dark.Grid);

            Assert.Equal(0, preferences.ChangedCount);
            Assert.False(preferences.IsChanged("OpenVSA.Grid"));
        }

        [Fact]
        public void ResettingPutsAColourBack()
        {
            var preferences = new ColourPreferences();
            preferences.Set("OpenVSA.Grid", new PlotColor(1, 2, 3));

            Assert.True(preferences.Reset("OpenVSA.Grid"));
            Assert.Equal(PlotPalette.Dark.Grid, preferences.ColourOf("Grid"));
            Assert.False(preferences.Reset("OpenVSA.Grid"));
        }

        [Fact]
        public void ResetAllPutsEverythingBack()
        {
            var preferences = new ColourPreferences();
            preferences.Set("OpenVSA.Grid", new PlotColor(1, 2, 3));
            preferences.Set("OpenVSA.Trace.A", new PlotColor(4, 5, 6));

            preferences.ResetAll();

            Assert.Equal(0, preferences.ChangedCount);
            Assert.Equal(PlotPalette.Dark.Grid, preferences.ColourOf("Grid"));
        }

        [Fact]
        public void AnUnknownKeyThrowsRatherThanPaintingAFallback()
        {
            // REQ-UI-022's reason: a misspelled key that silently paints grey is a bug nobody
            // notices until a customer photographs the screen.
            var preferences = new ColourPreferences();

            Assert.Throws<ArgumentException>(() => preferences.Colour("OpenVSA.Griddle"));
            Assert.Throws<ArgumentException>(
                () => preferences.Set("OpenVSA.Griddle", new PlotColor(1, 2, 3)));
        }

        [Fact]
        public void ChangedColoursSurviveASaveAndLoad()
        {
            // The requirement's second criterion: changed colours survive a restart. The sidecar
            // file is what outlives a session.
            var saved = new ColourPreferences();
            saved.Set("OpenVSA.Grid", new PlotColor(1, 2, 3));
            saved.Set("OpenVSA.Trace.B", new PlotColor(4, 5, 6));
            saved.Set("OpenVSA.FailLimit", new PlotColor(7, 8, 9));

            var state = new DisplayPreferencesState();
            saved.SaveInto(state);

            string json = SidecarFile.Write(state);
            var reloaded = SidecarFile.Read<DisplayPreferencesState>(json);

            var restored = new ColourPreferences();
            IReadOnlyList<string> unknown = restored.LoadFrom(reloaded);

            Assert.Empty(unknown);
            Assert.Equal(3, restored.ChangedCount);
            Assert.Equal(new PlotColor(1, 2, 3), restored.Colour("OpenVSA.Grid"));
            Assert.Equal(new PlotColor(4, 5, 6), restored.Colour("OpenVSA.Trace.B"));
            Assert.Equal(new PlotColor(7, 8, 9), restored.Colour("OpenVSA.FailLimit"));
        }

        [Fact]
        public void OnlyTheChangesAreWritten()
        {
            // An element left alone follows the default theme, including after the default changes.
            // Writing all several hundred out would freeze today's defaults into a user's file the
            // first time they changed one colour.
            var preferences = new ColourPreferences();
            preferences.Set("OpenVSA.Grid", new PlotColor(1, 2, 3));

            var state = new DisplayPreferencesState();
            preferences.SaveInto(state);

            Assert.Single(state.Colours);
            Assert.Equal("OpenVSA.Grid", state.Colours[0].Element);
        }

        [Fact]
        public void ThePreferencesFileIsWrittenInAStableOrder()
        {
            // Two saves of the same colours produce the same file, so a diff shows only what
            // actually differs.
            var first = new ColourPreferences();
            first.Set("OpenVSA.Trace.B", new PlotColor(4, 5, 6));
            first.Set("OpenVSA.Grid", new PlotColor(1, 2, 3));

            var second = new ColourPreferences();
            second.Set("OpenVSA.Grid", new PlotColor(1, 2, 3));
            second.Set("OpenVSA.Trace.B", new PlotColor(4, 5, 6));

            var a = new DisplayPreferencesState();
            var b = new DisplayPreferencesState();
            first.SaveInto(a);
            second.SaveInto(b);

            Assert.Equal(SidecarFile.Write(a), SidecarFile.Write(b));
        }

        [Fact]
        public void LoadingReplacesWhateverWasThereBefore()
        {
            var preferences = new ColourPreferences();
            preferences.Set("OpenVSA.Annotation", new PlotColor(9, 9, 9));

            var state = new DisplayPreferencesState();
            state.Colours.Add(new ElementColourState
            {
                Element = "OpenVSA.Grid",
                Argb = ColourPreferences.Pack(new PlotColor(1, 2, 3)),
            });

            preferences.LoadFrom(state);

            Assert.Equal(1, preferences.ChangedCount);
            Assert.Equal(PlotPalette.Dark.Annotation, preferences.ColourOf("Annotation"));
        }

        [Fact]
        public void AnUnknownKeyInTheFileIsReportedRatherThanThrownOn()
        {
            // A file written by a later version, or by a build covering more traces, will name
            // elements this one has never heard of. Discarding the user's other colours over one of
            // them would be the worse failure.
            var state = new DisplayPreferencesState();
            state.Colours.Add(new ElementColourState
            {
                Element = "OpenVSA.SomethingLater",
                Argb = 0xFF010203,
            });
            state.Colours.Add(new ElementColourState
            {
                Element = "OpenVSA.Grid",
                Argb = ColourPreferences.Pack(new PlotColor(1, 2, 3)),
            });

            var preferences = new ColourPreferences();
            IReadOnlyList<string> unknown = preferences.LoadFrom(state);

            Assert.Equal(new[] { "OpenVSA.SomethingLater" }, unknown);
            Assert.Equal(new PlotColor(1, 2, 3), preferences.ColourOf("Grid"));
        }

        [Fact]
        public void ColoursRoundTripThroughThePackedForm()
        {
            var colour = new PlotColor(0x12, 0x34, 0x56);

            Assert.Equal(0xFF123456u, ColourPreferences.Pack(colour));
            Assert.Equal(colour, ColourPreferences.Unpack(ColourPreferences.Pack(colour)));
        }

        [Fact]
        public void DisplayPreferencesAreSeparateFromASavedState()
        {
            // REQ-STA-002: recalling a colleague's setup must not repaint your display. The colours
            // live in the display sidecar, which has its own extension and its own file.
            Assert.Equal(".ovsa-display.json", SidecarState.PreferencesExtension);
            Assert.NotEqual(StateFile.Extension, SidecarState.PreferencesExtension);
        }

        [Fact]
        public void EveryDefaultColourIsARealChoiceRatherThanAPlaceholder()
        {
            // A single placeholder grey for the families nothing draws yet would make the picker's
            // own list unreadable and would hide the day a real default went missing.
            var preferences = new ColourPreferences(traceCount: 2);
            var seen = new Dictionary<PlotColor, int>();

            foreach (ColourEntry entry in preferences.Entries)
            {
                int count;
                seen.TryGetValue(entry.Default, out count);
                seen[entry.Default] = count + 1;
            }

            // No colour is shared by more than a handful of entries. Genuine sharing exists — Trace
            // and Trace Select start the same, as do the two limit pairs — but a placeholder would
            // show up here as one colour used by hundreds.
            Assert.True(seen.Values.Max() <= 4, "A default colour is used by " + seen.Values.Max() + " entries.");
        }

        [Fact]
        public void TheColoursThePaletteIsBuiltFromAreAllGlobal()
        {
            // ColourOf takes global elements only, and Trace is per trace. Asking it for "Trace"
            // throws — which is what it should do, and which took the shell down on its first
            // launch after the picker was wired in. The palette's trace colour comes from the
            // per-trace key instead; these are the seven that are genuinely global.
            var preferences = new ColourPreferences();

            foreach (string name in new[]
            {
                "Trace Background", "Grid", "Annotation", "Annotation Background",
                "Selected Marker", "Not Selected Marker", "Indicator",
                "Limit", "Margin", "Fail Limit", "Fail Margin",
            })
            {
                ThemeElement element = ThemeElements.ByName(name);

                Assert.NotNull(element);
                Assert.Equal(ThemeScope.Global, element.Scope);

                // Throws if it is not global, which is the failure this guards.
                preferences.ColourOf(name);
            }
        }

        [Fact]
        public void APerTraceElementIsNotReachableAsAGlobalOne()
        {
            var preferences = new ColourPreferences();

            Assert.Throws<ArgumentException>(() => preferences.ColourOf("Trace"));
        }

        [Fact]
        public void APickerCoversAtLeastOneTrace()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new ColourPreferences(0));
        }

        [Fact]
        public void NullSidecarsAreRejected()
        {
            var preferences = new ColourPreferences();

            Assert.Throws<ArgumentNullException>(() => preferences.SaveInto(null));
            Assert.Throws<ArgumentNullException>(() => preferences.LoadFrom(null));
        }
    }
}
