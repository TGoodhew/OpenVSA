using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Syncfusion.Windows.Tools.Controls;

namespace OpenVSA.Ui.Theming
{
    /// <summary>
    /// Colours the document tab strip from the chrome theme (<c>REQ-UI-083</c>; see #420).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Everything else the docking manager draws is reachable from XAML; this is not.</strong>
    /// The pane captions, splitters and side panels take their colours from
    /// <c>DockingManager</c>'s own brush properties, bound in <c>ShellWindow.xaml</c>. The document
    /// tabs are drawn by a <see cref="DocumentTabControl"/> the manager creates for itself, and
    /// neither <c>DockingManager.TabItemBackgroundSelected</c> nor a <c>TabControlStyle</c> reaches
    /// it — measured both ways: the tab strip went dark and the selected tab stayed Aero blue
    /// (<c>#F1F7FC</c> over a <c>LinearGradientBrush</c>), because the manager applies
    /// <c>TabControlStyle</c> to the docked tabbed host rather than to the document container.
    /// </para>
    /// <para>
    /// <strong>So the instance is bound directly, and with resource references rather than brushes.</strong>
    /// <see cref="FrameworkElement.SetResourceReference"/> is the code-behind spelling of
    /// <c>DynamicResource</c>: the properties go on following the chrome keys afterwards, so a theme
    /// chosen while the shell is running still reaches the tabs with no restart and nothing to
    /// re-apply. Assigning brushes here instead would freeze the tabs at whichever theme happened to
    /// be in force when the window was built.
    /// </para>
    /// <para>
    /// <strong>Setters, never a template.</strong> Re-templating the docking manager is what loses
    /// the hosted documents — see <c>SkinnedShellTests</c>. Nothing here replaces a template.
    /// </para>
    /// </remarks>
    public static class DockingChrome
    {
        /// <summary>
        /// The resource key of the tab-header style the shell supplies, if it supplies one.
        /// </summary>
        /// <remarks>
        /// Looked up rather than referenced, so a host that defines no such style simply keeps the
        /// stock tab headers instead of failing.
        /// </remarks>
        public const string TabItemStyleKey = "OpenVSA.DocumentTabItem";

        /// <summary>
        /// Points every document tab strip under <paramref name="root"/> at the chrome keys.
        /// </summary>
        /// <param name="root">The shell window, or any element above the docking manager.</param>
        /// <returns>How many tab strips were bound; zero before the manager has built one.</returns>
        /// <remarks>
        /// Returns a count rather than nothing so a test can assert it found something. A version of
        /// this that silently bound nothing would leave the tabs stock and look exactly like a
        /// theme that had not been written yet.
        /// </remarks>
        public static int FollowTheme(DependencyObject root)
        {
            if (root == null)
            {
                return 0;
            }

            // Before any Syncfusion control is touched: an unlicensed one raises a MODAL trial
            // dialog as it is constructed, and on a dispatcher thread that stops the dispatcher
            // pumping. Idempotent, and enforced by NoUnregisteredSyncfusionHostsTests.
            SyncfusionLicense.Register();

            int bound = 0;

            foreach (TabControlExt tabs in Descendants(root))
            {
                tabs.SetResourceReference(
                    TabControlExt.TabPanelBackgroundProperty, ChromeKeys.WindowBackground);

                tabs.SetResourceReference(
                    TabControlExt.TabItemSelectedBackgroundProperty, ChromeKeys.SurfaceBackground);
                tabs.SetResourceReference(
                    TabControlExt.TabItemSelectedForegroundProperty, ChromeKeys.WindowForeground);
                tabs.SetResourceReference(
                    TabControlExt.TabItemSelectedBorderBrushProperty, ChromeKeys.Accent);

                tabs.SetResourceReference(
                    TabControlExt.TabItemHoverBackgroundProperty, ChromeKeys.SurfaceBackground);
                tabs.SetResourceReference(
                    TabControlExt.TabItemHoverForegroundProperty, ChromeKeys.WindowForeground);
                tabs.SetResourceReference(
                    TabControlExt.TabItemHoverBorderBrushProperty, ChromeKeys.Border);

                tabs.SetResourceReference(Control.BackgroundProperty, ChromeKeys.WindowBackground);
                tabs.SetResourceReference(Control.ForegroundProperty, ChromeKeys.MutedForeground);
                tabs.SetResourceReference(Control.BorderBrushProperty, ChromeKeys.Border);

                // Every brush above binds and the tab still paints its own gradient, because
                // TabItemExt hard-codes the selected fill inside its template. This is the style
                // that replaces it, and ItemContainerStyle is how it reaches the tab items -
                // DockingManager's TabItemStyle does not, for the same reason TabControlStyle does
                // not reach the document tab control itself.
                if (tabs.TryFindResource(TabItemStyleKey) is Style style)
                {
                    tabs.ItemContainerStyle = style;
                }

                bound++;
            }

            return bound;
        }

        private static IEnumerable<TabControlExt> Descendants(DependencyObject root)
        {
            int children = VisualTreeHelper.GetChildrenCount(root);

            for (int index = 0; index < children; index++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(root, index);

                if (child is TabControlExt tabs)
                {
                    yield return tabs;
                }

                foreach (TabControlExt nested in Descendants(child))
                {
                    yield return nested;
                }
            }
        }
    }
}
