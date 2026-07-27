using System;
using System.Collections.Generic;
using System.Windows.Controls;

namespace OpenVSA.Ui.Menus
{
    /// <summary>
    /// What the shell supplies to <see cref="ShellMenuBuilder"/> for each entry of
    /// <see cref="ShellMenuTable"/>.
    /// </summary>
    public interface IShellMenuBinding
    {
        /// <summary>
        /// The item for an entry, ready to use, or <c>null</c> if the shell does not implement it.
        /// </summary>
        /// <param name="path">The entry's path, such as <c>File &gt; Preset &gt; Setup</c>.</param>
        /// <param name="entry">The entry.</param>
        /// <remarks>
        /// Returning <c>null</c> is how an entry ends up disabled with its reason as a tooltip.
        /// Returning an item for an entry that has a reason is a build failure, and so is returning
        /// <c>null</c> for one that has none — see <see cref="ShellMenuBuilder.Build"/>.
        /// </remarks>
        MenuItem Bind(string path, ShellMenuEntry entry);

        /// <summary>
        /// The toolbar embedded at an entry of kind
        /// <see cref="ShellMenuEntryKind.EmbeddedToolbar"/>.
        /// </summary>
        /// <param name="path">The entry's path.</param>
        /// <param name="entry">The entry.</param>
        ToolBar Toolbar(string path, ShellMenuEntry entry);

        /// <summary>
        /// Called when an item is clicked, before the shell's own handler.
        /// </summary>
        /// <param name="path">The entry's path.</param>
        /// <remarks>
        /// So that a test can drive the whole bar and see which action each item reached, without
        /// every one of a hundred handlers having to remember to record itself.
        /// </remarks>
        void Ran(string path);
    }

    /// <summary>
    /// Builds the menu bar from <see cref="ShellMenuTable"/> (<c>REQ-UI-061</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Built from the table rather than written in XAML.</strong> A hundred items across ten
    /// menus, whose exact contents are the requirement, is precisely the case where a second
    /// hand-maintained copy in markup drifts — and the drift is invisible, because both halves look
    /// right on their own. Building from the table means the tree cannot disagree with it.
    /// </para>
    /// <para>
    /// <strong>The "present and inert" check happens here, at construction.</strong> An entry the
    /// shell did not bind and that carries no reason throws while the window is being built, rather
    /// than presenting a user with an item that does nothing. So does the opposite: an entry the
    /// shell implemented while the table still claims it is unavailable, which is how a stale reason
    /// would otherwise sit in a tooltip for a year.
    /// </para>
    /// </remarks>
    public static class ShellMenuBuilder
    {
        /// <summary>
        /// Fills a menu bar with the table's ten menus.
        /// </summary>
        /// <param name="bar">The bar to fill; emptied first.</param>
        /// <param name="binding">What the shell supplies for each entry.</param>
        /// <exception cref="ArgumentNullException">An argument is null.</exception>
        /// <exception cref="InvalidOperationException">
        /// An entry has neither an action nor a reason, or has both.
        /// </exception>
        public static void Build(Menu bar, IShellMenuBinding binding)
        {
            if (bar == null)
            {
                throw new ArgumentNullException(nameof(bar));
            }

            if (binding == null)
            {
                throw new ArgumentNullException(nameof(binding));
            }

            bar.Items.Clear();

            var used = new HashSet<char>();

            foreach (ShellMenu menu in ShellMenuTable.Menus)
            {
                var top = new MenuItem { Header = WithAccessKey(menu.Name, used) };

                Fill(top, menu.Name, menu.Items, binding);
                bar.Items.Add(top);
            }
        }

        private static void Fill(
            MenuItem parent,
            string parentPath,
            IReadOnlyList<ShellMenuEntry> entries,
            IShellMenuBinding binding)
        {
            var used = new HashSet<char>();

            foreach (ShellMenuEntry entry in entries)
            {
                if (entry.Kind == ShellMenuEntryKind.Separator)
                {
                    parent.Items.Add(new Separator());
                    continue;
                }

                string path = ShellMenuTable.PathOf(parentPath, entry.Name);

                if (entry.Kind == ShellMenuEntryKind.EmbeddedToolbar)
                {
                    ToolBar toolbar = binding.Toolbar(path, entry);

                    if (toolbar == null)
                    {
                        throw new InvalidOperationException(
                            "REQ-UI-061 lists an embedded toolbar at '" + path + "' and the shell " +
                            "supplied none.");
                    }

                    // Added to the menu's items directly. A menu is an ItemsControl and will host
                    // one; wrapping it in an item of its own would put a name in the tree that the
                    // requirement's list does not have, which is the failure the exactness check
                    // exists to catch.
                    parent.Items.Add(toolbar);
                    continue;
                }

                parent.Items.Add(Make(path, entry, binding, used));
            }
        }

        private static MenuItem Make(
            string path, ShellMenuEntry entry, IShellMenuBinding binding, HashSet<char> used)
        {
            MenuItem item = binding.Bind(path, entry);

            if (item != null && !entry.IsImplemented)
            {
                throw new InvalidOperationException(
                    "The shell binds '" + path + "', but REQ-UI-061's table still gives a reason " +
                    "why it is unavailable: \"" + entry.Reason + "\" Remove the reason.");
            }

            bool needsAction = entry.IsAction || entry.IsDynamic;

            if (item == null && entry.IsImplemented && needsAction)
            {
                throw new InvalidOperationException(
                    "'" + path + "' is in REQ-UI-061's list, the shell does not bind it, and the " +
                    "table gives no reason why it is unavailable. Every item must be either " +
                    "enabled and functional or disabled with a reason.");
            }

            if (item == null)
            {
                item = new MenuItem();

                if (!entry.IsImplemented)
                {
                    item.IsEnabled = false;
                    item.ToolTip = entry.Reason;

                    // Without this the tooltip never appears, because WPF suppresses tooltips on
                    // disabled elements by default - which would leave the reason written down
                    // where nobody could read it.
                    ToolTipService.SetShowOnDisabled(item, true);
                }
            }

            item.Header = WithAccessKey(entry.Name, used);
            item.IsCheckable = entry.IsCheckable;

            if (entry.Gesture.Length > 0)
            {
                item.InputGestureText = entry.Gesture;
            }

            if (entry.IsAction && entry.IsImplemented)
            {
                string captured = path;
                item.Click += (sender, e) => binding.Ran(captured);
            }

            Fill(item, path, entry.Children, binding);

            return item;
        }

        /// <summary>
        /// Marks a letter of the name as its access key, avoiding the ones already taken.
        /// </summary>
        /// <param name="name">The name.</param>
        /// <param name="used">Letters already used at this level; added to.</param>
        /// <returns>The header, with an underscore before the access key.</returns>
        /// <remarks>
        /// <para>
        /// Assigned rather than written down. A hundred items across ten menus is enough that a
        /// hand-picked set would collide somewhere, and a collision does not fail loudly: the
        /// keystroke cycles between the two items instead of invoking one, which reads as a menu
        /// that has stopped working.
        /// </para>
        /// <para>
        /// Word beginnings first, because <c>F</c> for <em>Force white background</em> is guessable
        /// and <c>c</c> for it is not; then any letter, so an item still gets one when its initials
        /// are taken.
        /// </para>
        /// </remarks>
        public static string WithAccessKey(string name, HashSet<char> used)
        {
            if (string.IsNullOrEmpty(name))
            {
                return name ?? string.Empty;
            }

            if (used == null)
            {
                throw new ArgumentNullException(nameof(used));
            }

            int chosen = Choose(name, used, wordStartsOnly: true);

            if (chosen < 0)
            {
                chosen = Choose(name, used, wordStartsOnly: false);
            }

            if (chosen < 0)
            {
                return name;
            }

            used.Add(char.ToUpperInvariant(name[chosen]));

            return name.Substring(0, chosen) + "_" + name.Substring(chosen);
        }

        private static int Choose(string name, HashSet<char> used, bool wordStartsOnly)
        {
            for (int index = 0; index < name.Length; index++)
            {
                char letter = name[index];

                if (!char.IsLetterOrDigit(letter))
                {
                    continue;
                }

                if (wordStartsOnly && index > 0 && char.IsLetterOrDigit(name[index - 1]))
                {
                    continue;
                }

                if (!used.Contains(char.ToUpperInvariant(letter)))
                {
                    return index;
                }
            }

            return -1;
        }
    }
}
