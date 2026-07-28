using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OpenVSA.Ui.Dialogs;
using OpenVSA.Ui.Dialogs.Pages;
using OpenVSA.Ui.Rendering;
using OpenVSA.Ui.Theming;
using Xunit;

namespace OpenVSA.Ui.Tests
{
    /// <summary>
    /// <c>REQ-UI-083</c>: two themes ship, and a third costs a dictionary.
    /// </summary>
    /// <remarks>
    /// The requirement's own words about how this is got wrong: "Shipping two themes is easy to do
    /// in a way that makes a third expensive: hard-coded brushes, colours resolved through a
    /// <c>switch</c> on a two-valued enum, or a bool <c>IsDarkMode</c> threaded through view models
    /// all satisfy 'light and dark' today and have to be unpicked later." So the test that matters
    /// is the one that actually does the deferred thing —
    /// <see cref="AThirdThemeSuppliedAtRuntimeRendersWithNoProductCodeChanged"/>.
    /// </remarks>
    [Collection("Shell")]
    public class ThemingTests
    {
        private readonly ShellHost _host;

        /// <summary>Takes the shared shell thread.</summary>
        /// <param name="host">The thread every shell in this collection is built on.</param>
        public ThemingTests(ShellHost host)
        {
            _host = host;
        }

        [Fact]
        public void ExactlyTwoThemesShipAndTheyAreNamedLightAndDark()
        {
            _host.Run(() =>
            {
                ThemeCatalogue catalogue = ThemeCatalogue.Shipped();

                Assert.Equal(new[] { "Light", "Dark" }, catalogue.Names.ToArray());
            });
        }

        [Fact]
        public void BothShippedThemesDefineEveryKey()
        {
            // "Every key in the shipped dictionaries is present in both, so a theme cannot be
            // partially defined." A missing key does not fail where it is missing: WPF resolves the
            // reference to nothing and the control keeps its default brush, which reads as a
            // styling mistake rather than as an incomplete theme.
            _host.Run(() =>
            {
                foreach (ChromeTheme theme in ThemeCatalogue.Shipped().Themes)
                {
                    Assert.True(
                        theme.IsComplete,
                        theme.Name + " does not define: " + string.Join(", ", theme.MissingKeys));

                    foreach (string key in ChromeKeys.All)
                    {
                        Assert.IsAssignableFrom<Brush>(theme.Resources[key]);
                    }
                }
            });
        }

        [Fact]
        public void BothAreSelectableWithNoRestart()
        {
            // A live application, a live swap, and the chrome actually resolving to the new values.
            _host.Run(() =>
            {
                var shell = Built();

                shell.ThemeName = "Light";
                Brush light = ChromeBrush(shell, ChromeKeys.WindowBackground);

                shell.ThemeName = "Dark";
                Brush dark = ChromeBrush(shell, ChromeKeys.WindowBackground);

                Assert.NotEqual(Colour(light), Colour(dark));

                // And back, without anything being rebuilt.
                shell.ThemeName = "Light";
                Assert.Equal(Colour(light), Colour(ChromeBrush(shell, ChromeKeys.WindowBackground)));
            });
        }

        [Fact]
        public void AThirdThemeSuppliedAtRuntimeRendersWithNoProductCodeChanged()
        {
            // The criterion, done rather than described: "a test supplies a third resource
            // dictionary at runtime and asserts the application renders with it, with no product
            // code changed — the only honest test of 'a later custom theme is not made harder',
            // since every weaker check passes on an implementation that has hard-coded the two."
            //
            // Nothing below names Light or Dark, and nothing in the product was touched to make it
            // work: the dictionary is built here, registered here, and chosen by name.
            _host.Run(() =>
            {
                var shell = Built();

                var invented = new ResourceDictionary();
                var signature = Color.FromArgb(0xFF, 0x11, 0x22, 0x33);

                foreach (string key in ChromeKeys.All)
                {
                    invented[key] = new SolidColorBrush(signature);
                }

                shell.Themes.Add(new ChromeTheme("Sepia", invented));

                Assert.Contains("Sepia", shell.Themes.Names);

                shell.ThemeName = "Sepia";

                Assert.Equal("Sepia", shell.Themes.CurrentName);

                // Every chrome key now resolves to the third theme's brush, through the same
                // DynamicResource lookups the shipped themes use.
                foreach (string key in ChromeKeys.All)
                {
                    Assert.Equal(signature, Colour(ChromeBrush(shell, key)));
                }

                // And the window is drawn with it, not merely holding it in a dictionary.
                Assert.Equal(signature, Colour(shell.Background));

                // The chooser offers it, because it is filled from the catalogue rather than from a
                // list written in the page.
                var page = new WindowPage(new DialogFrameworkOptions(), shell.Themes);

                Assert.Contains("Sepia", page.ThemeBox.Items.Cast<string>());
            });
        }

        [Fact]
        public void ASettingsDialogFollowsTheChromeTheme()
        {
            // A settings dialog is chrome. Left unthemed it is a white window against a dark shell,
            // which the screenshot showed — and it matters most here, because the chooser that
            // changes the theme is on one of these pages.
            _host.Run(() =>
            {
                var dialog = new SettingsDialog("Sample", new DialogFrameworkOptions());

                var surface = new SolidColorBrush(Color.FromArgb(0xFF, 0x0A, 0x0B, 0x0C));
                var text = new SolidColorBrush(Color.FromArgb(0xFF, 0xF0, 0xF1, 0xF2));

                // Into the dialog's own resources: a window resolves its dynamic references from
                // itself before the application, so this exercises the reference without needing
                // an Application to exist.
                dialog.Resources[ChromeKeys.SurfaceBackground] = surface;
                dialog.Resources[ChromeKeys.SurfaceForeground] = text;

                Assert.Equal(Colour(surface), Colour(dialog.Background));
                Assert.Equal(Colour(text), Colour(dialog.Foreground));
            });
        }

        [Fact]
        public void AThemeThisBuildDoesNotHaveIsRefusedRatherThanApplied()
        {
            _host.Run(() =>
            {
                var shell = Built();
                string was = shell.ThemeName;

                shell.ThemeName = "Office2007Blue";

                Assert.Equal(was, shell.ThemeName);
            });
        }

        [Fact]
        public void AnIncompleteThemeReportsWhatItLacksRatherThanBeingRefused()
        {
            // A theme written against a later version should be partly usable and say what it is
            // missing, not be rejected outright.
            var thin = new ResourceDictionary
            {
                [ChromeKeys.WindowBackground] = new SolidColorBrush(Colors.Black),
            };

            var theme = new ChromeTheme("Thin", thin);

            Assert.False(theme.IsComplete);
            Assert.Equal(ChromeKeys.All.Count - 1, theme.MissingKeys.Count);
            Assert.DoesNotContain(ChromeKeys.WindowBackground, theme.MissingKeys);
        }

        [Fact]
        public void ADuplicateThemeNameIsRefused()
        {
            _host.Run(() =>
            {
                ThemeCatalogue catalogue = ThemeCatalogue.Shipped();

                Assert.Throws<ArgumentException>(
                    () => catalogue.Add(new ChromeTheme("light", new ResourceDictionary())));
            });
        }

        [Fact]
        public void TheChosenThemeSurvivesARestart()
        {
            _host.Run(() =>
            {
                var shell = Built();

                shell.ThemeName = "Light";

                var state = new OpenVSA.Measurement.State.DisplayPreferencesState
                {
                    ChromeTheme = shell.ThemeName,
                };

                string json = OpenVSA.Measurement.State.SidecarFile.Write(state);

                var read = OpenVSA.Measurement.State.SidecarFile
                    .Read<OpenVSA.Measurement.State.DisplayPreferencesState>(json);

                Assert.Equal("Light", read.ChromeTheme);
            });
        }

        [Fact]
        public void MissingArgumentsAreRefused()
        {
            Assert.Throws<ArgumentNullException>(() => new ChromeTheme("X", null));
            Assert.Throws<ArgumentException>(() => new ChromeTheme("  ", new ResourceDictionary()));

            _host.Run(() =>
            {
                Assert.Throws<ArgumentNullException>(
                    () => ThemeCatalogue.Shipped().Apply("Light", null));

                Assert.Throws<ArgumentNullException>(() => ThemeCatalogue.Shipped().Add(null));
            });
        }

        // ---- REQ-UI-081: chrome only ---------------------------------------------------------

        [Fact]
        public void SwitchingThemeChangesChromeOnly()
        {
            // REQ-UI-081's testable half: "switching theme changes chrome only, and every colour of
            // REQ-UI-022 — graticule, traces, annotation, backgrounds — samples identically before
            // and after a theme change. A theme that alters a plot-surface colour fails."
            //
            // It holds by construction — the plot colours come from ColourPreferences and the
            // chrome from a resource dictionary, and nothing joins them — but "by construction" is
            // what this test exists to stop being quietly untrue.
            _host.Run(() =>
            {
                var shell = Built();

                shell.ThemeName = "Dark";

                Dictionary<string, PlotColor> before = PlotColours(shell);
                PlotPalette paletteBefore = shell.DocumentArea.PlotOf('A').Palette;

                shell.ThemeName = "Light";

                Dictionary<string, PlotColor> after = PlotColours(shell);
                PlotPalette paletteAfter = shell.DocumentArea.PlotOf('A').Palette;

                Assert.Equal(before.Count, after.Count);

                foreach (KeyValuePair<string, PlotColor> entry in before)
                {
                    Assert.Equal(entry.Value, after[entry.Key]);
                }

                // And the palette the rasteriser actually draws with, not only the preferences
                // behind it — a theme reaching the plot would most plausibly do it here.
                Assert.Equal(paletteBefore.TraceBackground, paletteAfter.TraceBackground);
                Assert.Equal(paletteBefore.Grid, paletteAfter.Grid);
                Assert.Equal(paletteBefore.Annotation, paletteAfter.Annotation);
                Assert.Equal(paletteBefore.AnnotationBackground, paletteAfter.AnnotationBackground);
                Assert.Equal(paletteBefore.Trace, paletteAfter.Trace);
            });
        }

        [Fact]
        public void AThirdThemeCannotReachThePlotEither()
        {
            // "the REQ-UI-081 separation test passes for the third dictionary too" — including one
            // that deliberately tries, by defining keys with the plot elements' own names.
            _host.Run(() =>
            {
                var shell = Built();

                Dictionary<string, PlotColor> before = PlotColours(shell);

                var hostile = new ResourceDictionary();

                foreach (string key in ChromeKeys.All)
                {
                    hostile[key] = new SolidColorBrush(Colors.Magenta);
                }

                // The plot elements' keys, in the same dictionary, which is the mistake a theme
                // author would most plausibly make.
                foreach (string key in ThemeElements.KeysFor(4))
                {
                    hostile[key] = new SolidColorBrush(Colors.Magenta);
                }

                shell.Themes.Add(new ChromeTheme("Hostile", hostile));
                shell.ThemeName = "Hostile";

                Dictionary<string, PlotColor> after = PlotColours(shell);

                foreach (KeyValuePair<string, PlotColor> entry in before)
                {
                    Assert.Equal(entry.Value, after[entry.Key]);
                }
            });
        }

        [Fact]
        public void NoChromeKeyIsAlsoAPlotKey()
        {
            // The two sets are addressed by different prefixes so that one merged dictionary cannot
            // confuse them, which is what makes the separation above cheap to keep.
            var plot = new HashSet<string>(ThemeElements.KeysFor(20));

            foreach (string key in ChromeKeys.All)
            {
                Assert.True(ChromeKeys.IsChromeKey(key));
                Assert.DoesNotContain(key, plot);
            }

            foreach (string key in plot)
            {
                Assert.False(ChromeKeys.IsChromeKey(key), key + " reads as a chrome key.");
            }
        }

        // ---- REQ-UI-082: not the Office-2007 chrome ------------------------------------------

        [Fact]
        public void NoneOfTheReferenceProductsThemesShip()
        {
            // REQ-UI-082: "The chrome is not Office-2007: no theme reproducing that skin ships, and
            // the shipped theme set is the Light/Dark pair of REQ-UI-083." REQ-UI-081 quotes the
            // reference product's list as evidence of its visual era; none of it is ours.
            string[] referenceThemes =
            {
                "AeroNormalColor", "Classic", "HighContrast", "LunaHomestead", "LunaMetallic",
                "LunaNormalColor", "Office2007Black", "Office2007Blue", "Office2007Silver",
                "Office2010Black", "Office2010Blue", "Office2010Silver", "RoyaleNormalColor",
            };

            _host.Run(() =>
            {
                ThemeCatalogue catalogue = ThemeCatalogue.Shipped();

                foreach (string name in referenceThemes)
                {
                    Assert.Null(catalogue.Find(name));
                }

                Assert.Equal(2, catalogue.Names.Count);
            });
        }

        // ---- Helpers -----------------------------------------------------------------------------

        private static ShellWindow Built() =>
            new ShellWindow { PersistPreferences = false, Interactive = false };

        /// <summary>What a chrome key resolves to for a shell, through the real lookup chain.</summary>
        private static Brush ChromeBrush(ShellWindow shell, string key)
        {
            object found = shell.TryFindResource(key);

            Assert.True(found != null, "'" + key + "' resolves to nothing.");

            return (Brush)found;
        }

        private static Color Colour(Brush brush) => ((SolidColorBrush)brush).Color;

        /// <summary>
        /// Every colour of <c>REQ-UI-022</c>, as this shell's own live preferences resolve them.
        /// </summary>
        /// <remarks>
        /// The shell's, not a fresh set: a comparison of two freshly built preference objects would
        /// be equal whatever a theme had done, which is a test that cannot fail.
        /// </remarks>
        private static Dictionary<string, PlotColor> PlotColours(ShellWindow shell)
        {
            var sampled = new Dictionary<string, PlotColor>(StringComparer.Ordinal);
            ColourPreferences preferences = shell.Colours;

            foreach (ColourEntry entry in preferences.Entries)
            {
                sampled[entry.Key] = preferences.Colour(entry.Key);
            }

            Assert.True(sampled.Count > 100, "Only " + sampled.Count + " plot colours were sampled.");

            return sampled;
        }
    }
}
