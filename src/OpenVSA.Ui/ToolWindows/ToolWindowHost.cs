using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using Syncfusion.Windows.Tools.Controls;

using OpenVSA.Ui.Rendering;

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
            // This type manipulates Syncfusion controls, so it registers too rather than relying on
            // whoever built the DockingManager having done it. Idempotent; see SyncfusionLicense.
            SyncfusionLicense.Register();

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


        /// <summary>
        /// Applies the Marker font slot to the Markers window (<c>REQ-UI-033</c>,
        /// <c>REQ-UI-080</c>).
        /// </summary>
        /// <param name="fonts">The font slots in force.</param>
        /// <exception cref="ArgumentNullException"><paramref name="fonts"/> is null.</exception>
        /// <remarks>
        /// The Markers window only. The other seven panes are also columns of figures and also draw
        /// fixed-width, but <c>REQ-UI-080</c> gives the Marker slot one surface and the Tabular slot
        /// two others; letting one slot quietly restyle all eight would make "setting one leaves the
        /// other two unchanged" untestable on screen.
        /// </remarks>
        public void ApplyFonts(FontPreferences fonts)
        {
            if (fonts == null)
            {
                throw new ArgumentNullException(nameof(fonts));
            }

            TextBlock markers;

            if (_text.TryGetValue(ToolWindow.Markers, out markers))
            {
                fonts.ApplyTo(FontSlot.Marker, markers);
            }
        }

        /// <summary>
        /// Gives the Markers window its own background colour (<c>REQ-UI-032</c>).
        /// </summary>
        /// <param name="colours">The themed colours.</param>
        /// <exception cref="ArgumentNullException"><paramref name="colours"/> is null.</exception>
        /// <remarks>
        /// <para>
        /// <strong>"A dedicated tool window with its own background colour and its own font
        /// slot."</strong> <c>Marker Window Background</c> is a themeable element of
        /// <c>REQ-UI-022</c> in its own right, and this is the surface it exists for; without this
        /// the entry appeared in the colour picker and changed nothing on screen.
        /// </para>
        /// <para>
        /// The Markers window alone, for the reason <see cref="ApplyFonts"/> gives about the Marker
        /// slot: an element that quietly restyled all eight panes would make "its own" untestable.
        /// </para>
        /// </remarks>
        public void ApplyColours(ColourPreferences colours)
        {
            if (colours == null)
            {
                throw new ArgumentNullException(nameof(colours));
            }

            ContentControl pane;

            if (!_panes.TryGetValue(ToolWindow.Markers, out pane))
            {
                return;
            }

            PlotColor colour = colours.ColourOf("Marker Window Background");

            pane.Background = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromArgb(colour.A, colour.R, colour.G, colour.B));
        }

        /// <summary>The Markers window's pane, for a test to sample what it is drawn on.</summary>
        public ContentControl MarkersPane
        {
            get
            {
                ContentControl pane;

                return _panes.TryGetValue(ToolWindow.Markers, out pane) ? pane : null;
            }
        }

        /// <summary>The Markers window's text block, for a test to read what it shows.</summary>
        public TextBlock MarkersText
        {
            get
            {
                TextBlock text;

                return _text.TryGetValue(ToolWindow.Markers, out text) ? text : null;
            }
        }

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
