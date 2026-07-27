using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using Syncfusion.Windows.Tools.Controls;

namespace OpenVSA.Ui.ToolWindows
{
    /// <summary>
    /// Creates the eight tool windows, their menu items and their panes, and keeps the three in
    /// step (<c>REQ-UI-002</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Everything is built from <see cref="ToolWindows.All"/>.</strong> The panes, the
    /// Window menu and the Marker menu's one entry are all produced by walking the same list, so
    /// the criterion — all eight, under exactly these names, openable from the right menu — holds
    /// by construction. Three hand-written lists would agree on the day they were written.
    /// </para>
    /// <para>
    /// <strong>Docked, never a document.</strong> <c>REQ-UI-001</c> draws the distinction sharply:
    /// only trace-bearing windows may be document windows. Each pane is created with
    /// <c>DockingManager.SetState(pane, DockState.Dock)</c> and never with
    /// <c>DockState.Document</c>, and a test asserts the state of all eight rather than trusting
    /// the default.
    /// </para>
    /// <para>
    /// This class touches Syncfusion types and so cannot be exercised without a visual tree. The
    /// parts that carry the requirement's meaning — the names, the menus, the placement and its
    /// persistence — are in <see cref="ToolWindows"/> and <see cref="ToolWindowLayout"/>, which
    /// can, and are.
    /// </para>
    /// </remarks>
    public sealed class ToolWindowHost
    {
        private readonly Dictionary<ToolWindow, ContentControl> _panes =
            new Dictionary<ToolWindow, ContentControl>();

        private readonly Dictionary<ToolWindow, TextBlock> _text =
            new Dictionary<ToolWindow, TextBlock>();

        private readonly Dictionary<ToolWindow, IToolWindowSource> _sources =
            new Dictionary<ToolWindow, IToolWindowSource>();

        private readonly Dictionary<ToolWindow, MenuItem> _items =
            new Dictionary<ToolWindow, MenuItem>();

        private readonly DockingManager _docking;

        /// <summary>
        /// Builds the eight panes into a docking manager.
        /// </summary>
        /// <param name="docking">The docking manager to add them to.</param>
        /// <param name="layout">Where they go and whether they are open.</param>
        /// <exception cref="ArgumentNullException">An argument is null.</exception>
        public ToolWindowHost(DockingManager docking, ToolWindowLayout layout)
        {
            if (docking == null)
            {
                throw new ArgumentNullException(nameof(docking));
            }

            if (layout == null)
            {
                throw new ArgumentNullException(nameof(layout));
            }

            _docking = docking;
            Layout = layout;

            foreach (ToolWindow window in ToolWindows.All)
            {
                Create(window);
            }
        }

        /// <summary>The placement and open state of every window.</summary>
        public ToolWindowLayout Layout { get; }

        /// <summary>The panes, in enumeration order.</summary>
        public IReadOnlyList<ContentControl> Panes
        {
            get
            {
                var panes = new List<ContentControl>(ToolWindows.All.Count);

                foreach (ToolWindow window in ToolWindows.All)
                {
                    panes.Add(_panes[window]);
                }

                return new ReadOnlyCollection<ContentControl>(panes);
            }
        }

        /// <summary>One window's pane.</summary>
        /// <param name="window">The window.</param>
        public ContentControl PaneOf(ToolWindow window) => _panes[window];

        /// <summary>One window's menu item.</summary>
        /// <param name="window">The window.</param>
        public MenuItem MenuItemOf(ToolWindow window) => _items[window];

        /// <summary>
        /// Attaches a source to a window, replacing whatever fed it before.
        /// </summary>
        /// <param name="source">The source.</param>
        /// <exception cref="ArgumentNullException"><paramref name="source"/> is null.</exception>
        public void SetSource(IToolWindowSource source)
        {
            if (source == null)
            {
                throw new ArgumentNullException(nameof(source));
            }

            _sources[source.Window] = source;
            Refresh(source.Window);
        }

        /// <summary>The source feeding a window, or <c>null</c> if none has been attached.</summary>
        /// <param name="window">The window.</param>
        public IToolWindowSource SourceOf(ToolWindow window)
        {
            IToolWindowSource source;

            return _sources.TryGetValue(window, out source) ? source : null;
        }

        /// <summary>Re-reads one window's source and redraws it.</summary>
        /// <param name="window">The window.</param>
        public void Refresh(ToolWindow window)
        {
            IToolWindowSource source = SourceOf(window);

            if (source == null)
            {
                _text[window].Text = "Nothing is attached to this window.";
                return;
            }

            source.Refresh();
            _text[window].Text = string.Join(Environment.NewLine, source.Lines);
        }

        /// <summary>Re-reads every window.</summary>
        public void RefreshAll()
        {
            foreach (ToolWindow window in ToolWindows.All)
            {
                Refresh(window);
            }
        }

        /// <summary>
        /// Adds the menu items to the Window and Marker menus.
        /// </summary>
        /// <param name="windowMenu">The Window menu.</param>
        /// <param name="markerMenu">The item on the Marker menu that carries the Markers window.</param>
        /// <exception cref="ArgumentNullException">An argument is null.</exception>
        public void PopulateMenus(MenuItem windowMenu, MenuItem markerMenu)
        {
            if (windowMenu == null)
            {
                throw new ArgumentNullException(nameof(windowMenu));
            }

            if (markerMenu == null)
            {
                throw new ArgumentNullException(nameof(markerMenu));
            }

            markerMenu.Header = "_" + ToolWindows.NameOf(ToolWindow.Markers) + " window";

            foreach (ToolWindow window in ToolWindows.All)
            {
                MenuItem item = _items[window];

                if (ToolWindows.MenuOf(window) == ToolWindowMenu.Marker)
                {
                    // The Marker menu carries one entry, which is the item itself rather than a
                    // submenu holding it - REQ-UI-002 says the window is opened from that menu, not
                    // that the menu gains a Windows submenu.
                    markerMenu.IsCheckable = true;
                    markerMenu.IsChecked = item.IsChecked;
                    markerMenu.Click += (sender, e) => Toggle(window);
                    continue;
                }

                windowMenu.Items.Add(item);
            }

            _markerMenu = markerMenu;
        }

        /// <summary>Opens or closes a window and records the change.</summary>
        /// <param name="window">The window.</param>
        public void Toggle(ToolWindow window) => SetOpen(window, !Layout.IsOpen(window));

        /// <summary>Opens or closes a window.</summary>
        /// <param name="window">The window.</param>
        /// <param name="open">Whether it should be open.</param>
        public void SetOpen(ToolWindow window, bool open)
        {
            Layout.SetOpen(window, open);

            ContentControl pane = _panes[window];

            if (open)
            {
                // Added on demand rather than added-then-hidden. Eight panes handed to the docking
                // manager and seven immediately hidden left the eighth without a dock group to
                // live in - the closed ones had already claimed and then surrendered the layout.
                // Adding only what is open keeps the arrangement to what the user asked for.
                if (!_docking.Children.Contains(pane))
                {
                    _docking.Children.Add(pane);
                }

                ApplyPlacement(window, pane);
                DockingManager.SetState(pane, DockState.Dock);
            }
            else if (_docking.Children.Contains(pane))
            {
                DockingManager.SetState(pane, DockState.Hidden);
            }

            DockingManager.SetCanDocument(pane, false);

            _items[window].IsChecked = open;

            if (_markerMenu != null && ToolWindows.MenuOf(window) == ToolWindowMenu.Marker)
            {
                _markerMenu.IsChecked = open;
            }

            if (open)
            {
                Refresh(window);
            }
        }

        /// <summary>
        /// Reads the panes' current sizes back into the layout, ready to be saved.
        /// </summary>
        /// <remarks>
        /// Called before saving rather than on every drag: a docking manager reports sizes during a
        /// drag that are true only for that instant, and a preference file that recorded them would
        /// remember where a window passed through rather than where it was put.
        /// </remarks>
        public void CaptureSizes()
        {
            foreach (ToolWindow window in ToolWindows.All)
            {
                ContentControl pane = _panes[window];

                double width = pane.ActualWidth > 0.0
                    ? pane.ActualWidth
                    : Layout[window].Width;

                double height = pane.ActualHeight > 0.0
                    ? pane.ActualHeight
                    : Layout[window].Height;

                Layout.SetPlacement(window, Layout.SideOf(window), width, height);
            }
        }

        private MenuItem _markerMenu;

        private void Create(ToolWindow window)
        {
            var text = new TextBlock
            {
                Margin = new Thickness(10.0),
                TextWrapping = TextWrapping.NoWrap,

                // Fixed width, because seven of these eight are columns of figures or of arrows,
                // and REQ-UI-033 asks for it by name in the Markers window.
                FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                FontSize = 12.0,
            };

            var pane = new ContentControl
            {
                Name = "ToolWindow" + window,
                Content = new ScrollViewer
                {
                    Content = text,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                },
            };

            DockingManager.SetHeader(pane, ToolWindows.NameOf(window));

            // REQ-UI-001: a tool window is not a document window, and only trace-bearing windows
            // may be documents. Said explicitly rather than left to the default.
            DockingManager.SetCanDocument(pane, false);

            ApplyPlacement(window, pane);

            var item = new MenuItem
            {
                Header = ToolWindows.NameOf(window),
                IsCheckable = true,
                IsChecked = Layout.IsOpen(window),
            };

            ToolWindow captured = window;
            item.Click += (sender, e) => Toggle(captured);

            _panes[window] = pane;
            _text[window] = text;
            _items[window] = item;

            // Not added to the docking manager here: SetOpen does that for the ones that are open.
        }

        /// <summary>Puts a pane on the edge and at the size the layout says.</summary>
        private void ApplyPlacement(ToolWindow window, ContentControl pane)
        {
            ToolWindowSide side = Layout.SideOf(window);

            DockingManager.SetSideInDockedMode(
                pane,
                side == ToolWindowSide.Left
                    ? DockSide.Left
                    : side == ToolWindowSide.Bottom ? DockSide.Bottom : DockSide.Right);

            DockingManager.SetDesiredWidthInDockedMode(pane, Layout[window].Width);
            DockingManager.SetDesiredHeightInDockedMode(pane, Layout[window].Height);
        }
    }
}
