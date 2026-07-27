using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace OpenVSA.Ui.Menus
{
    /// <summary>What one line of a menu is.</summary>
    public enum ShellMenuEntryKind
    {
        /// <summary>An item: something to click, or a submenu to open.</summary>
        Item = 0,

        /// <summary>A rule between groups. Not an item, and not part of the requirement's list.</summary>
        Separator,

        /// <summary>
        /// A toolbar embedded in the menu, which <c>REQ-UI-061</c> lists as an entry of its own on
        /// the Trace and Marker menus.
        /// </summary>
        EmbeddedToolbar,
    }

    /// <summary>
    /// One entry of <c>REQ-UI-061</c>'s menu contents.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Either an action or a reason, never neither.</strong> The requirement's criterion is
    /// that each item is "either enabled and functional or disabled with a reason — none is present
    /// and inert", so an entry carries <see cref="Reason"/> exactly when the shell is not expected
    /// to bind it. Building the bar throws if the two disagree in either direction: an unbound entry
    /// with no reason, or a reason for something the shell went and implemented anyway.
    /// </para>
    /// <para>
    /// <strong><see cref="Spec"/> is what the specification calls the entry</strong>, which is
    /// usually what the menu calls it and occasionally is not — <em>Help (F1)</em> is one item and a
    /// gesture, not an item with brackets in its name. Keeping the two apart lets the test that
    /// parses the specification compare like with like.
    /// </para>
    /// </remarks>
    public sealed class ShellMenuEntry
    {
        private static readonly ReadOnlyCollection<ShellMenuEntry> NoChildren =
            new ReadOnlyCollection<ShellMenuEntry>(new List<ShellMenuEntry>());

        internal ShellMenuEntry(
            string name,
            ShellMenuEntryKind kind,
            string reason,
            bool isCheckable,
            bool isDynamic,
            string spec,
            string gesture,
            IList<ShellMenuEntry> children)
        {
            Name = name ?? string.Empty;
            Kind = kind;
            Reason = reason;
            IsCheckable = isCheckable;
            IsDynamic = isDynamic;
            Spec = spec ?? name ?? string.Empty;
            Gesture = gesture ?? string.Empty;

            Children = children == null
                ? NoChildren
                : new ReadOnlyCollection<ShellMenuEntry>(children);
        }

        /// <summary>The name the menu shows, without an access-key marker.</summary>
        public string Name { get; }

        /// <summary>What kind of entry this is.</summary>
        public ShellMenuEntryKind Kind { get; }

        /// <summary>
        /// Why the item is disabled, or <c>null</c> when the shell binds it.
        /// </summary>
        /// <remarks>
        /// Shown as the item's tooltip, because a disabled item with no explanation is
        /// indistinguishable from a broken one — and the user's next move, hunting for the setting
        /// somewhere else, is wasted either way.
        /// </remarks>
        public string Reason { get; }

        /// <summary>Whether the item carries a tick.</summary>
        public bool IsCheckable { get; }

        /// <summary>
        /// Whether the shell adds children to this item beyond the ones listed here.
        /// </summary>
        /// <remarks>
        /// The instrument list, the user's presets, the open traces and the trace formats are all
        /// discovered rather than declared. The tree walk stops comparing at the end of
        /// <see cref="Children"/> for these, and instead asserts that opening the submenu fills it.
        /// </remarks>
        public bool IsDynamic { get; }

        /// <summary>How the specification's list writes this entry.</summary>
        public string Spec { get; }

        /// <summary>The keyboard gesture shown beside the item, or empty.</summary>
        public string Gesture { get; }

        /// <summary>The entries beneath this one.</summary>
        public IReadOnlyList<ShellMenuEntry> Children { get; }

        /// <summary>Whether the shell is expected to bind this entry.</summary>
        public bool IsImplemented => Reason == null;

        /// <summary>
        /// Whether clicking this entry should do something, as opposed to opening a submenu.
        /// </summary>
        public bool IsAction =>
            Kind == ShellMenuEntryKind.Item && Children.Count == 0 && !IsDynamic;

        /// <inheritdoc />
        public override string ToString() =>
            Kind == ShellMenuEntryKind.Item ? Name : "<" + Kind + ">";
    }

    /// <summary>One top-level menu and everything on it.</summary>
    public sealed class ShellMenu
    {
        internal ShellMenu(string name, IList<ShellMenuEntry> items)
        {
            if (string.IsNullOrEmpty(name))
            {
                throw new ArgumentNullException(nameof(name));
            }

            Name = name;
            Items = new ReadOnlyCollection<ShellMenuEntry>(items ?? new List<ShellMenuEntry>());
        }

        /// <summary>The menu's name, as <c>REQ-UI-060</c> writes it.</summary>
        public string Name { get; }

        /// <summary>What is on it, in order.</summary>
        public IReadOnlyList<ShellMenuEntry> Items { get; }

        /// <inheritdoc />
        public override string ToString() => Name + " (" + Items.Count + " entries)";
    }
}
