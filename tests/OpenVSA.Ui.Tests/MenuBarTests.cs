using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Controls;
using Xunit;

namespace OpenVSA.Ui.Tests
{
    /// <summary>
    /// <c>REQ-UI-060</c>: the menu bar, and the Agilent-era names it must not have.
    /// </summary>
    /// <remarks>
    /// Walked on a real shell rather than asserted against a second list. The criterion is about
    /// what the application shows, and one list checked against another proves only that somebody
    /// wrote the same thing twice.
    /// </remarks>
    [Collection("Shell")]
    public class MenuBarTests
    {
        private readonly ShellHost _host;

        /// <summary>Takes the shared shell thread.</summary>
        /// <param name="host">The thread every shell in this collection is built on.</param>
        public MenuBarTests(ShellHost host)
        {
            _host = host;
        }

        [Fact]
        public void TheBarIsExactlyTheTenMenusInOrder()
        {
            _host.Run(() => Assert.Equal(
                new List<string>(ShellMenus.Names), Headers(new ShellWindow { PersistPreferences = false })));
        }

        [Fact]
        public void TheSupersededNamesAreAbsent()
        {
            // The half that catches the mistake. A test that only checked for the ten would pass a
            // bar of eleven with Display among them — which is what this shell had until
            // REQ-UI-060 was implemented, because Display is where a colour picker naturally goes.
            _host.Run(() =>
            {
                foreach (string name in Headers(new ShellWindow { PersistPreferences = false }))
                {
                    Assert.False(
                        ShellMenus.IsSuperseded(name),
                        "REQ-UI-060 renamed or demoted '" + name + "'; it must not be a top-level " +
                        "menu. Input became Acquisition, MeasSetup became Analysis, Markers became " +
                        "Marker, Control became a submenu of Acquisition, and Display was removed " +
                        "with its functions moving under Window and Trace.");
                }
            });
        }

        [Fact]
        public void TheListWouldCatchAnOldNameIfOneAppeared()
        {
            // A test that can only pass is not a test.
            foreach (string old in new[] { "Input", "MeasSetup", "Display", "Control", "_Display" })
            {
                Assert.True(ShellMenus.IsSuperseded(old), old + " would not have been caught.");
            }

            foreach (string current in ShellMenus.Names)
            {
                Assert.False(
                    ShellMenus.IsSuperseded(current),
                    current + " is a current menu and must not be rejected.");
            }
        }

        [Fact]
        public void TheAccessKeyMarkerIsNotPartOfTheName()
        {
            // WPF writes the access key as a leading underscore and UI Automation reports the name
            // without it. Comparing raw headers would fail on the underscore rather than on the
            // name — a test failing for the wrong reason.
            Assert.Equal("Analysis", ShellMenus.NameOf("Ana_lysis"));
            Assert.Equal("File", ShellMenus.NameOf("_File"));
            Assert.Equal(string.Empty, ShellMenus.NameOf(null));
        }

        [Fact]
        public void WhatTheDisplayMenuHeldIsStillReachable()
        {
            // Removing a menu must not remove what was on it, and neither must REQ-UI-061's exact
            // list. Display Preferences is on Utilities where the requirement puts it; the map is
            // Trace > Spectrogram / Colour Map; the limit indications are under Trace > Limit
            // Tests…, which is a listed item and the right home for them.
            _host.Run(() =>
            {
                var shell = new ShellWindow { PersistPreferences = false };

                Assert.NotNull(Find(shell, "Utilities", "Display Preferences…"));
                Assert.NotNull(Find(shell, "Trace", "Spectrogram / Colour Map"));

                MenuItem limits = Find(shell, "Trace", "Limit Tests…");

                Assert.NotNull(Under(limits, "Indicate limit failures"));
                Assert.NotNull(Under(limits, "Indicate margin warnings"));
            });
        }

        [Fact]
        public void ShowAnnotationAndGridLinesAreOnTheTraceTabInstead()
        {
            // These two were menu items until REQ-UI-061's list was applied, and the list does not
            // have them. They are not gone: the Trace tab of Display Preferences has always held
            // them, both surfaces wrote one TraceDisplayOptions, and now the tab is the only way in.
            // Asserted on the dialog rather than on the options object, because "still reachable"
            // is a claim about what a user can get at.
            _host.Run(() =>
            {
                var options = new OpenVSA.Ui.Rendering.TraceDisplayOptions();
                var page = new OpenVSA.Ui.Dialogs.Pages.TracePage(options);

                var captions = new List<string>();

                foreach (object child in page.Children)
                {
                    var box = child as CheckBox;

                    if (box != null)
                    {
                        captions.Add((string)box.Content);
                    }
                }

                Assert.Contains(captions, c => c.StartsWith("Show annotation", StringComparison.Ordinal));
                Assert.Contains(captions, c => c.StartsWith("Show grid lines", StringComparison.Ordinal));
            });
        }

        private static MenuItem Under(MenuItem parent, string name)
        {
            foreach (object child in parent.Items)
            {
                var entry = child as MenuItem;

                if (entry != null &&
                    string.Equals(
                        ShellMenus.NameOf(entry.Header as string), name, StringComparison.Ordinal))
                {
                    return entry;
                }
            }

            throw new InvalidOperationException(
                "'" + parent.Header + " > " + name + "' is not in the menu bar.");
        }

        private static List<string> Headers(ShellWindow shell)
        {
            var names = new List<string>();

            foreach (object item in shell.MenuBar.Items)
            {
                var menu = item as MenuItem;

                if (menu != null)
                {
                    names.Add(ShellMenus.NameOf(menu.Header as string));
                }
            }

            return names;
        }

        private static MenuItem Find(ShellWindow shell, string menu, string item)
        {
            foreach (object candidate in shell.MenuBar.Items)
            {
                var top = candidate as MenuItem;

                if (top == null ||
                    !string.Equals(ShellMenus.NameOf(top.Header as string), menu, StringComparison.Ordinal))
                {
                    continue;
                }

                foreach (object child in top.Items)
                {
                    var entry = child as MenuItem;

                    if (entry != null &&
                        string.Equals(
                            ShellMenus.NameOf(entry.Header as string), item, StringComparison.Ordinal))
                    {
                        return entry;
                    }
                }
            }

            throw new InvalidOperationException("'" + menu + " > " + item + "' is not in the menu bar.");
        }
    }
}
