using System;
using System.Windows;

namespace OpenVSA.Ui.Dialogs
{
    /// <summary>
    /// One page of a settings dialog — a tab, or an expander, depending on the mode
    /// (<c>REQ-UI-071</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// A page is a name and one element, and deliberately nothing else. The four modes of
    /// <c>REQ-UI-071</c> have to render "the same content", and they can only be guaranteed to if
    /// there is exactly one thing to render: a page that carried, say, its own buttons for the
    /// tabbed case would be the mechanism by which a control became reachable in one mode and not
    /// another.
    /// </para>
    /// <para>
    /// The element is moved between presentations rather than rebuilt for each, so a page keeps its
    /// state — a filter typed into the colour list, a scroll position — across a mode change. That
    /// is also why <see cref="SettingsDialog"/> detaches before it rebuilds: one element cannot
    /// have two parents.
    /// </para>
    /// </remarks>
    public sealed class SettingsPage
    {
        /// <summary>Creates a page.</summary>
        /// <param name="name">The page's name, as the tab or expander header shows it.</param>
        /// <param name="content">The page's one element.</param>
        /// <exception cref="ArgumentNullException"><paramref name="content"/> is null.</exception>
        /// <exception cref="ArgumentException"><paramref name="name"/> is null or blank.</exception>
        public SettingsPage(string name, FrameworkElement content)
        {
            if (string.IsNullOrEmpty(name) || name.Trim().Length == 0)
            {
                throw new ArgumentException("A page needs a name.", nameof(name));
            }

            if (content == null)
            {
                throw new ArgumentNullException(nameof(content));
            }

            Name = name.Trim();
            Content = content;
        }

        /// <summary>The page's name.</summary>
        public string Name { get; }

        /// <summary>
        /// The page's one element.
        /// </summary>
        /// <remarks>
        /// A <see cref="FrameworkElement"/> rather than a bare <see cref="UIElement"/> because
        /// Fixed Size works by giving every page the same minimum, and a minimum is something only
        /// a framework element has.
        /// </remarks>
        public FrameworkElement Content { get; }

        /// <summary>
        /// The name shortened to its initial, for a collapsed tab strip (<c>REQ-UI-071</c>).
        /// </summary>
        /// <remarks>
        /// The initial, with the full name kept as the tooltip. A collapsed strip that showed
        /// nothing at all would leave a user counting tab positions to find the page they wanted.
        /// </remarks>
        public string Initial => Name.Substring(0, 1);

        /// <inheritdoc />
        public override string ToString() => Name;
    }
}
