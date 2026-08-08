using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace OpenVSA.Ui.Theming
{
    /// <summary>
    /// The resource keys a chrome theme defines (<c>REQ-UI-083</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Chrome only.</strong> <c>REQ-UI-081</c> separates the two: a theme styles the
    /// window, the menus, the toolbars and the panes, and the graticule and traces are governed
    /// entirely by the colour settings of <c>REQ-UI-022</c> — which live in
    /// <see cref="Rendering.ThemeElements"/> and are not here. Nothing in this list reaches the
    /// plot surface, and a test samples every plot colour across a theme change to say so.
    /// </para>
    /// <para>
    /// <strong>The list is what makes "a theme cannot be partially defined" checkable.</strong>
    /// Every shipped dictionary is asserted to define every key, and a third dictionary supplied at
    /// runtime is held to the same list. Without an enumerable set, a theme missing a brush would
    /// simply fall through to whatever WPF had lying about, which is the failure that looks like a
    /// styling bug and is a missing key.
    /// </para>
    /// </remarks>
    public static class ChromeKeys
    {
        /// <summary>The prefix every chrome key carries.</summary>
        /// <remarks>
        /// Distinct from the <c>OpenVSA.</c> prefix the plot elements use, so that the two sets
        /// cannot collide in one merged dictionary and so that a reader of a resource key can tell
        /// at a glance which side of <c>REQ-UI-081</c>'s separation it is on.
        /// </remarks>
        public const string Prefix = "OpenVSA.Chrome.";

        /// <summary>Behind the application window as a whole.</summary>
        public const string WindowBackground = Prefix + "WindowBackground";

        /// <summary>Text on the window background.</summary>
        public const string WindowForeground = Prefix + "WindowForeground";

        /// <summary>Behind a pane, a dialog page or any raised surface.</summary>
        public const string SurfaceBackground = Prefix + "SurfaceBackground";

        /// <summary>Text on a raised surface.</summary>
        public const string SurfaceForeground = Prefix + "SurfaceForeground";

        /// <summary>Explanatory text that must read as secondary.</summary>
        public const string MutedForeground = Prefix + "MutedForeground";

        /// <summary>Text of a control that cannot be used.</summary>
        public const string DisabledForeground = Prefix + "DisabledForeground";

        /// <summary>Lines separating one region from another.</summary>
        public const string Border = Prefix + "Border";

        /// <summary>Behind the menu bar.</summary>
        public const string MenuBackground = Prefix + "MenuBackground";

        /// <summary>Text in the menu bar.</summary>
        public const string MenuForeground = Prefix + "MenuForeground";

        /// <summary>Behind the toolbar tray.</summary>
        public const string ToolbarBackground = Prefix + "ToolbarBackground";

        /// <summary>Behind the status bar.</summary>
        public const string StatusBarBackground = Prefix + "StatusBarBackground";

        /// <summary>Text in the status bar.</summary>
        public const string StatusBarForeground = Prefix + "StatusBarForeground";

        /// <summary>Behind a selected row, tab or menu item.</summary>
        public const string SelectionBackground = Prefix + "SelectionBackground";

        /// <summary>Text on a selection.</summary>
        public const string SelectionForeground = Prefix + "SelectionForeground";

        /// <summary>Behind a control that is switched on, such as a toolbar toggle.</summary>
        /// <remarks>
        /// <strong>Not <see cref="SelectionBackground"/>, and the difference is the point.</strong>
        /// A selection is the one row or item the user is acting on now, and it is drawn in the
        /// solid accent. A toggle that is on is a setting that stays on while the user works on
        /// something else — half a toolbar can be in that state at once, and painting each of them
        /// in the solid accent turns the band into a row of shouting blue blocks. VS Code draws its
        /// active options in a translucent accent over whatever is behind them, which reads as "on"
        /// without competing with the thing being worked on.
        /// </remarks>
        public const string ActiveBackground = Prefix + "ActiveBackground";

        /// <summary>The colour that marks the thing currently in force.</summary>
        public const string Accent = Prefix + "Accent";

        /// <summary>Behind the guidance overlay drawn over a trace window.</summary>
        public const string OverlayBackground = Prefix + "OverlayBackground";

        /// <summary>Text of the guidance overlay.</summary>
        public const string OverlayForeground = Prefix + "OverlayForeground";

        private static readonly ReadOnlyCollection<string> Keys =
            new ReadOnlyCollection<string>(new List<string>
            {
                WindowBackground,
                WindowForeground,
                SurfaceBackground,
                SurfaceForeground,
                MutedForeground,
                DisabledForeground,
                Border,
                MenuBackground,
                MenuForeground,
                ToolbarBackground,
                StatusBarBackground,
                StatusBarForeground,
                SelectionBackground,
                SelectionForeground,
                ActiveBackground,
                Accent,
                OverlayBackground,
                OverlayForeground,
            });

        /// <summary>Every key a chrome theme must define, in a stable order.</summary>
        public static IReadOnlyList<string> All => Keys;

        /// <summary>
        /// Whether a resource key belongs to the chrome.
        /// </summary>
        /// <param name="key">The key.</param>
        /// <remarks>
        /// By prefix rather than by membership of <see cref="All"/>: a dictionary carrying a chrome
        /// key this build does not know is a dictionary written for a later version, and reporting
        /// it as unknown is more useful than not recognising it as chrome at all.
        /// </remarks>
        public static bool IsChromeKey(string key) =>
            key != null && key.StartsWith(Prefix, StringComparison.Ordinal);
    }
}
