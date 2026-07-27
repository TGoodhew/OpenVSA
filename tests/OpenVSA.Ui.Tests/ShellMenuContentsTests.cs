using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using OpenVSA.Measurement.State;
using OpenVSA.Ui.Menus;
using Xunit;

namespace OpenVSA.Ui.Tests
{
    /// <summary>
    /// <c>REQ-UI-061</c>: the menu contents, walked on a real shell.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The criterion is exact in both directions — "every listed item is present under its listed
    /// menu … an item present in the tree but not in the list also fails" — so the comparison here
    /// is an equality over the whole tree rather than a series of lookups. A lookup-based test
    /// passes a menu that has quietly grown a dozen extra items, which is the accretion the
    /// requirement is written to prevent.
    /// </para>
    /// <para>
    /// The list itself is <see cref="ShellMenuTable"/>, and that it says what the specification says
    /// is <see cref="MenuSpecificationTests"/>'s job. Neither test is worth much without the other:
    /// this one alone would prove the shell agrees with a list somebody typed.
    /// </para>
    /// </remarks>
    [Collection("Shell")]
    public class ShellMenuContentsTests
    {
        private readonly ShellHost _host;

        /// <summary>Takes the shared shell thread.</summary>
        /// <param name="host">The thread every shell in this collection is built on.</param>
        public ShellMenuContentsTests(ShellHost host)
        {
            _host = host;
        }

        [Fact]
        public void TheTreeIsExactlyTheTable()
        {
            _host.Run(() =>
            {
                var shell = Built();
                var differences = new List<string>();

                Assert.Equal(ShellMenuTable.Menus.Count, shell.MenuBar.Items.Count);

                for (int index = 0; index < ShellMenuTable.Menus.Count; index++)
                {
                    ShellMenu menu = ShellMenuTable.Menus[index];
                    var top = (MenuItem)shell.MenuBar.Items[index];

                    Assert.Equal(menu.Name, ShellMenus.NameOf(top.Header as string));

                    Compare(menu.Name, menu.Items, top.Items, false, differences);
                }

                Assert.True(
                    differences.Count == 0,
                    "The menu bar and REQ-UI-061's list disagree:" + Environment.NewLine +
                    string.Join(Environment.NewLine, differences));
            });
        }

        [Fact]
        public void NoItemIsPresentAndInert()
        {
            // "Each item is either enabled and functional or disabled with a reason - none is
            // present and inert." Both halves, over every item in the bar: the enabled ones are
            // clicked and have to arrive somewhere, and the disabled ones have to say why.
            _host.Run(() =>
            {
                // Shown, off screen: several items open a modeless dialog, and a window cannot own
                // one until it has been shown itself. Unshown, the test would fail on the first
                // Analysis tab for a reason that has nothing to do with the menu.
                ShellWindow shell = Shown();
                var inert = new List<string>();

                foreach (KeyValuePair<string, MenuItem> found in Leaves(shell))
                {
                    MenuItem item = found.Value;

                    if (!item.IsEnabled)
                    {
                        var reason = item.ToolTip as string;

                        if (string.IsNullOrWhiteSpace(reason) || reason.Length < 30)
                        {
                            inert.Add(found.Key + " is disabled and gives no reason.");
                        }

                        continue;
                    }

                    item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

                    if (!string.Equals(shell.LastCommand, found.Key, StringComparison.Ordinal))
                    {
                        inert.Add(
                            found.Key + " is enabled and clicking it reached '" +
                            shell.LastCommand + "'.");
                    }
                }

                CloseAnythingOpened(shell);
                shell.Close();

                Assert.True(
                    inert.Count == 0,
                    "REQ-UI-061: no item may be present and inert." + Environment.NewLine +
                    string.Join(Environment.NewLine, inert));
            });
        }

        [Fact]
        public void EveryDisabledItemSaysSomethingUseful()
        {
            // A reason, not a restatement. "Not implemented" on forty items is a tooltip nobody
            // reads twice; what a user needs is what the item would do, and what has to exist
            // before it can.
            foreach (KeyValuePair<string, ShellMenuEntry> found in ShellMenuTable.All())
            {
                ShellMenuEntry entry = found.Value;

                if (entry.IsImplemented)
                {
                    continue;
                }

                Assert.True(
                    entry.Reason.Length >= 40,
                    found.Key + "'s reason is too short to be one: \"" + entry.Reason + "\"");

                Assert.False(
                    string.Equals(
                        entry.Reason.TrimEnd('.'), entry.Name, StringComparison.OrdinalIgnoreCase),
                    found.Key + "'s reason is its own name.");

                Assert.EndsWith(".", entry.Reason.Trim(), StringComparison.Ordinal);
            }
        }

        [Fact]
        public void TheAnalysisMenuIsInTheStatedOrder()
        {
            // Called out separately because the requirement calls it out: "with the Analysis menu
            // in the stated order". The seven dialogs in the middle are REQ-UI-072's tab set, and a
            // menu that offered them alphabetically would still contain every one of them.
            _host.Run(() =>
            {
                var shell = Built();

                Assert.Equal(
                    new[]
                    {
                        "Type", "Properties…", "Frequency…", "ResBW…", "Time…", "Detectors…",
                        "Conversion…", "Average…", "Heatmaps…", "Measurements…", "New Measurement",
                        "Duplicate Measurement",
                    },
                    Names(Menu(shell, "Analysis").Items).ToArray());
            });
        }

        [Fact]
        public void ThereIsNoLicensesItem()
        {
            // REQ-UI-061 points this one out itself: the reference product has a Licenses… item on
            // Utilities, OpenVSA has nothing to license (REQ-LIC-010), and this menu's exact-list
            // criterion means adding one fails the build. It is exactly the kind of item that
            // arrives by being copied from a screenshot of the product being cloned.
            _host.Run(() =>
            {
                var shell = Built();

                foreach (KeyValuePair<string, MenuItem> found in Leaves(shell))
                {
                    Assert.DoesNotContain("licen", found.Key, StringComparison.OrdinalIgnoreCase);
                }
            });
        }

        [Fact]
        public void EveryDynamicSubmenuFillsWhenItOpens()
        {
            // The instruments, the presets, the traces, the formats, the scale ladder, the layouts
            // and the colour maps are discovered rather than declared. An empty submenu does not
            // open at all, which reads as a broken item rather than as an empty list.
            _host.Run(() =>
            {
                var shell = Built();

                foreach (KeyValuePair<string, ShellMenuEntry> found in ShellMenuTable.All())
                {
                    if (!found.Value.IsDynamic)
                    {
                        continue;
                    }

                    MenuItem item = At(shell, found.Key);

                    item.RaiseEvent(new RoutedEventArgs(MenuItem.SubmenuOpenedEvent));

                    Assert.True(
                        item.Items.Count > 0,
                        found.Key + " is a dynamic submenu and opening it left it empty.");

                    foreach (object child in item.Items)
                    {
                        var entry = child as MenuItem;

                        if (entry != null)
                        {
                            Assert.False(
                                string.IsNullOrWhiteSpace(entry.Header as string),
                                found.Key + " filled itself with an item that has no name.");
                        }
                    }
                }
            });
        }

        [Fact]
        public void NoTwoItemsOnAMenuShareAnAccessKey()
        {
            // A collision does not fail loudly: the keystroke cycles between the two items instead
            // of invoking one, which reads as a menu that has stopped working. With a hundred items
            // assigned by hand there would be one somewhere.
            _host.Run(() => CheckAccessKeys("the menu bar", Built().MenuBar.Items));
        }

        [Fact]
        public void TheEmbeddedToolbarsAreToolbarsWithWorkingButtons()
        {
            // REQ-UI-061 lists "(embedded trace toolbar)" and "(embedded markers toolbar)" as
            // entries of the Trace and Marker menus. A submenu named "Trace tools" would be an item
            // the list does not have; a real ToolBar among the menu's own items is not an item.
            _host.Run(() =>
            {
                var shell = Built();

                foreach (string menu in new[] { "Trace", "Marker" })
                {
                    ToolBar bar = Menu(shell, menu).Items.OfType<ToolBar>().FirstOrDefault();

                    Assert.True(bar != null, menu + " has no embedded toolbar.");
                    Assert.NotEmpty(bar.Items.OfType<ButtonBase>());

                    foreach (ButtonBase button in bar.Items.OfType<ButtonBase>())
                    {
                        Assert.False(
                            string.IsNullOrWhiteSpace(button.Content as string),
                            "A button on the " + menu + " toolbar has no caption.");
                    }
                }

                // Select Area is REQ-DSP-023's trace tool and it is a mode: it has to stay pressed
                // while it is on, which is why it is a toggle rather than a button.
                ToolBar trace = Menu(shell, "Trace").Items.OfType<ToolBar>().First();
                ToggleButton select = trace.Items.OfType<ToggleButton>().FirstOrDefault();

                Assert.True(select != null, "Select Area is not a mode on the trace toolbar.");

                select.IsChecked = true;
                select.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

                Assert.True(shell.DocumentArea.ActivePlot.SelectAreaEnabled);

                select.IsChecked = false;
                select.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

                Assert.False(shell.DocumentArea.ActivePlot.SelectAreaEnabled);
            });
        }

        [Fact]
        public void EveryPresetVariantIsOnTheMenu()
        {
            // Nine of them, spelled as the requirement spells them, in its order. Two are disabled
            // with a reason — there is no standards library and there are no toolbars yet — and
            // NoItemIsPresentAndInert is what insists they say so.
            _host.Run(() =>
            {
                var shell = Built();

                Assert.Equal(
                    Presets.Variants.Select(Presets.NameOf).ToArray(),
                    Names(At(shell, "File > Preset").Items).ToArray());
            });
        }

        [Fact]
        public void PresetResetsTheMeasurementAndLeavesTheHardwareAlone()
        {
            // REQ-UI-061's separation, checked through the menu rather than through the model:
            // every enabled Preset item is clicked, the measurement settings the variant names have
            // to move, and what the Hardware menu offers has to be exactly what it offered before.
            // PresetScopeTests does the same over the state itself, where the frequency reference
            // and the source can be altered and watched.
            _host.Run(() =>
            {
                var shell = Built();

                MenuItem instruments = At(shell, "Hardware > Instruments…");
                MenuItem disconnect = At(shell, "Hardware > Disconnect");

                instruments.RaiseEvent(new RoutedEventArgs(MenuItem.SubmenuOpenedEvent));

                int discovered = instruments.Items.Count;
                bool connected = disconnect.IsEnabled;

                foreach (MenuItem item in At(shell, "File > Preset").Items.OfType<MenuItem>())
                {
                    if (!item.IsEnabled)
                    {
                        continue;
                    }

                    ApplicationState altered = shell.CaptureState();
                    altered.Measurements[0].CenterFrequencyHz = 2.4e9;
                    shell.ApplyState(altered.Measurements[0]);

                    Assert.Equal(2.4e9, shell.CaptureState().Measurements[0].CenterFrequencyHz, 0);

                    item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

                    string variant = ShellMenus.NameOf(item.Header as string);
                    double after = shell.CaptureState().Measurements[0].CenterFrequencyHz;

                    if (Resets(variant))
                    {
                        Assert.True(
                            Math.Abs(after - 2.4e9) > 1.0,
                            "Preset " + variant + " should have reset the centre frequency.");
                    }
                    else
                    {
                        Assert.Equal(2.4e9, after, 0);
                    }

                    instruments.RaiseEvent(new RoutedEventArgs(MenuItem.SubmenuOpenedEvent));

                    Assert.Equal(discovered, instruments.Items.Count);
                    Assert.Equal(connected, disconnect.IsEnabled);
                }
            });
        }

        /// <summary>Whether a preset variant is one that resets the measurement settings.</summary>
        private static bool Resets(string variantName)
        {
            foreach (PresetVariant variant in Presets.Variants)
            {
                if (string.Equals(Presets.NameOf(variant), variantName, StringComparison.Ordinal))
                {
                    return Presets.Has(
                        Presets.CategoriesOf(variant), PresetCategory.Measurement);
                }
            }

            throw new InvalidOperationException("'" + variantName + "' is not a preset variant.");
        }

        // ---- Helpers ---------------------------------------------------------------------------

        /// <summary>
        /// A shell that writes nothing outside itself.
        /// </summary>
        /// <remarks>
        /// <c>PersistPreferences</c> keeps the suite out of the user's real tool-window layout, and
        /// <c>Interactive</c> keeps it out of the user's clipboard and away from modal file
        /// pickers no test could answer. Neither changes how an item is reached.
        /// </remarks>
        private static ShellWindow Built() =>
            new ShellWindow { PersistPreferences = false, Interactive = false };

        /// <summary>The same shell, shown off screen so that it can own a dialog.</summary>
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

        private static void CloseAnythingOpened(ShellWindow shell)
        {
            // Several items open a modeless dialog, which is what they are for. Left open they
            // would outlive the test and be inherited by the next one on this thread.
            foreach (Window owned in shell.OwnedWindows.Cast<Window>().ToList())
            {
                owned.Close();
            }
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

        private static MenuItem At(ShellWindow shell, string path)
        {
            string[] steps = path.Split(new[] { " > " }, StringSplitOptions.None);
            MenuItem item = Menu(shell, steps[0]);

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

        private static IEnumerable<string> Names(ItemCollection items)
        {
            foreach (object child in items)
            {
                var item = child as MenuItem;

                if (item != null)
                {
                    yield return ShellMenus.NameOf(item.Header as string);
                }
            }
        }

        /// <summary>Every clickable item in the bar, by path, taken from the table.</summary>
        private static IEnumerable<KeyValuePair<string, MenuItem>> Leaves(ShellWindow shell)
        {
            foreach (KeyValuePair<string, ShellMenuEntry> found in ShellMenuTable.All())
            {
                if (found.Value.IsAction)
                {
                    yield return new KeyValuePair<string, MenuItem>(found.Key, At(shell, found.Key));
                }
            }
        }

        private static void CheckAccessKeys(string level, ItemCollection items)
        {
            var used = new Dictionary<char, string>();

            foreach (object child in items)
            {
                var item = child as MenuItem;

                if (item == null)
                {
                    continue;
                }

                var header = item.Header as string;
                int mark = header == null ? -1 : header.IndexOf('_');
                string name = ShellMenus.NameOf(header);

                if (mark >= 0 && mark + 1 < header.Length)
                {
                    char key = char.ToUpperInvariant(header[mark + 1]);

                    string other;

                    Assert.False(
                        used.TryGetValue(key, out other),
                        "On " + level + ", '" + name + "' and '" + other + "' share the access " +
                        "key " + key + ".");

                    used[key] = name;
                }

                CheckAccessKeys(level + " > " + name, item.Items);
            }
        }

        private static void Compare(
            string path,
            IReadOnlyList<ShellMenuEntry> expected,
            ItemCollection actual,
            bool grows,
            List<string> differences)
        {
            for (int index = 0; index < expected.Count; index++)
            {
                if (index >= actual.Count)
                {
                    differences.Add(path + " is missing '" + expected[index].Name + "'.");
                    continue;
                }

                object item = actual[index];
                ShellMenuEntry entry = expected[index];

                switch (entry.Kind)
                {
                    case ShellMenuEntryKind.Separator:
                        if (!(item is Separator))
                        {
                            differences.Add(path + " has " + Describe(item) + " where a rule goes.");
                        }

                        break;

                    case ShellMenuEntryKind.EmbeddedToolbar:
                        if (!(item is ToolBar))
                        {
                            differences.Add(
                                path + " has " + Describe(item) + " where the embedded toolbar goes.");
                        }

                        break;

                    default:
                        var menu = item as MenuItem;

                        if (menu == null)
                        {
                            differences.Add(
                                path + " has " + Describe(item) + " where '" + entry.Name + "' goes.");
                            break;
                        }

                        string name = ShellMenus.NameOf(menu.Header as string);

                        if (!string.Equals(name, entry.Name, StringComparison.Ordinal))
                        {
                            differences.Add(
                                path + " has '" + name + "' where '" + entry.Name + "' goes.");
                            break;
                        }

                        Compare(
                            ShellMenuTable.PathOf(path, entry.Name),
                            entry.Children,
                            menu.Items,
                            entry.IsDynamic,
                            differences);

                        break;
                }
            }

            if (!grows && actual.Count > expected.Count)
            {
                // The half the requirement is written for: "an item present in the tree but not in
                // the list also fails, so the menus stay as specified rather than accreting". Only
                // a submenu the table marks as dynamic is allowed to be longer than its list, and
                // then only past the end of it.
                differences.Add(
                    path + " has " + (actual.Count - expected.Count) + " entries more than the " +
                    "list, beginning with " + Describe(actual[expected.Count]) + ".");
            }
        }

        private static string Describe(object item)
        {
            var menu = item as MenuItem;

            if (menu != null)
            {
                return "'" + ShellMenus.NameOf(menu.Header as string) + "'";
            }

            return item is Separator ? "a rule" : item.GetType().Name;
        }
    }
}
