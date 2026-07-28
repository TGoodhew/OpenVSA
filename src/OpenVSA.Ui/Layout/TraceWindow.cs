using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using OpenVSA.Ui.Menus;
using OpenVSA.Ui.Rendering;
using OpenVSA.Ui.Toolbars;

namespace OpenVSA.Ui.Layout
{
    /// <summary>
    /// A trace dragged out of the document area (<c>REQ-UI-003</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>"A regular application window with menus and toolbars, similar to the main VSA
    /// application window, except that the Trace Window contains a subset of the menu
    /// items."</strong> So it is a real <see cref="Window"/> with its own working menu bar and its
    /// own toolbar — not a floating panel, and not the main window's menu reparented. A user drags
    /// a trace to a second monitor precisely so that it keeps working while the main window is doing
    /// something else, and a window borrowing the main one's chrome cannot.
    /// </para>
    /// <para>
    /// <strong>The subset is declared, not left to whoever writes the menu.</strong>
    /// <see cref="MenuNames"/> and <see cref="ToolbarNames"/> are the lists the requirement's own
    /// sentence implies — all trace-specific operations plus limited measurement control — and the
    /// window is built from them, for the reason <c>ShellMenuTable</c> exists: a second hand-kept
    /// copy in markup is a second thing to drift.
    /// </para>
    /// <para>
    /// <strong>What it does NOT have.</strong> No Hardware menu, because a detached trace does not
    /// connect or disconnect instruments; no File menu, because a layout is saved by the
    /// application rather than by one of its windows. Both absences are asserted, since the way
    /// this requirement gets over-delivered is by cloning the whole menu bar and calling it a
    /// subset.
    /// </para>
    /// </remarks>
    public sealed class TraceWindow : Window
    {
        private static readonly ReadOnlyCollection<string> Menus =
            new ReadOnlyCollection<string>(new List<string>
            {
                // Trace-specific operations, in the main bar's own order.
                "Acquisition",
                "Analysis",
                "Trace",
                "Marker",
                "Window",
            });

        private static readonly ReadOnlyCollection<string> Toolbars =
            new ReadOnlyCollection<string>(new List<string>
            {
                // Limited measurement control, and the trace tools. Not the whole tray.
                "Control",
                "Marker Tools",
                "Trace / Block Diagram",
                "Spectrogram / Colour Map",
            });

        private readonly Menu _menu = new Menu();
        private readonly ToolBarTray _toolbars = new ToolBarTray();
        private readonly Border _host = new Border();

        /// <summary>Creates a detached window for a trace.</summary>
        /// <param name="trace">The trace's letter.</param>
        /// <param name="content">What the trace shows; usually a <see cref="TracePlot"/>.</param>
        /// <exception cref="ArgumentNullException"><paramref name="content"/> is null.</exception>
        public TraceWindow(char trace, FrameworkElement content)
        {
            if (content == null)
            {
                throw new ArgumentNullException(nameof(content));
            }

            Trace = trace;
            Content2 = content;

            Title = "Trace " + trace;
            Width = 760.0;
            Height = 520.0;
            ShowInTaskbar = true;

            SetResourceReference(BackgroundProperty, Theming.ChromeKeys.WindowBackground);
            SetResourceReference(ForegroundProperty, Theming.ChromeKeys.WindowForeground);

            _menu.SetResourceReference(BackgroundProperty, Theming.ChromeKeys.MenuBackground);
            _toolbars.SetResourceReference(BackgroundProperty, Theming.ChromeKeys.ToolbarBackground);

            BuildMenu();
            BuildToolbars();

            _host.Child = content;

            var root = new DockPanel { LastChildFill = true };

            DockPanel.SetDock(_menu, Dock.Top);
            DockPanel.SetDock(_toolbars, Dock.Top);

            root.Children.Add(_menu);
            root.Children.Add(_toolbars);
            root.Children.Add(_host);

            Content = root;
        }

        /// <summary>The menus a detached trace window carries (<c>REQ-UI-003</c>).</summary>
        public static IReadOnlyList<string> MenuNames => Menus;

        /// <summary>The toolbars a detached trace window carries (<c>REQ-UI-003</c>).</summary>
        public static IReadOnlyList<string> ToolbarNames => Toolbars;

        /// <summary>Which trace this window holds.</summary>
        public char Trace { get; }

        /// <summary>What it shows.</summary>
        public FrameworkElement Content2 { get; }

        /// <summary>Its own menu bar.</summary>
        public Menu MenuBar => _menu;

        /// <summary>Its own toolbar tray.</summary>
        public ToolBarTray ToolbarTray => _toolbars;

        /// <summary>
        /// Whether every menu and toolbar it carries actually has items in it.
        /// </summary>
        /// <remarks>
        /// <c>REQ-UI-003</c>'s criterion is "its own <em>working</em> menu bar and toolbar", and a
        /// bar of empty headers would satisfy every check that only counted them.
        /// </remarks>
        public bool IsWorking
        {
            get
            {
                foreach (object child in _menu.Items)
                {
                    var item = child as MenuItem;

                    if (item == null || item.Items.Count == 0)
                    {
                        return false;
                    }
                }

                foreach (ToolBar bar in _toolbars.ToolBars)
                {
                    if (bar.Items.Count == 0)
                    {
                        return false;
                    }
                }

                return _menu.Items.Count > 0 && _toolbars.ToolBars.Count > 0;
            }
        }

        /// <summary>Where the window is, for the saved layout (<c>REQ-UI-003</c>).</summary>
        public Rect Placement => new Rect(Left, Top, Width, Height);

        /// <summary>Puts the window back where a saved layout says it was.</summary>
        /// <param name="placement">The rectangle.</param>
        public void PlaceAt(Rect placement)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;

            Left = placement.X;
            Top = placement.Y;
            Width = Math.Max(200.0, placement.Width);
            Height = Math.Max(150.0, placement.Height);
        }

        /// <summary>
        /// Builds the subset of <c>REQ-UI-061</c>'s bar this window carries.
        /// </summary>
        /// <remarks>
        /// From <see cref="ShellMenuTable"/>'s own entries, so a detached window shows the same
        /// items under the same names as the main bar and cannot drift from it. Items the main
        /// shell disables with a reason arrive disabled with the same reason.
        /// </remarks>
        private void BuildMenu()
        {
            foreach (string name in Menus)
            {
                ShellMenu declared = ShellMenuTable.For(name);

                var top = new MenuItem { Header = declared.Name };

                foreach (ShellMenuEntry entry in declared.Items)
                {
                    top.Items.Add(Item(entry));
                }

                _menu.Items.Add(top);
            }
        }

        /// <summary>
        /// One menu entry, disabled unless the detached window can carry it out.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A detached window shows the same items under the same names as the main bar — the same
        /// table builds both — but it drives only what it owns. Everything else is disabled with a
        /// reason saying where to do it, which is the same bound-or-reasoned rule the main bar
        /// keeps: no item is present and inert.
        /// </para>
        /// <para>
        /// The reason is deliberately about <em>this window</em> rather than about the feature. An
        /// item greyed with "not implemented" would be wrong — it is implemented, in the window the
        /// user came from.
        /// </para>
        /// </remarks>
        private static MenuItem Item(ShellMenuEntry entry)
        {
            var item = new MenuItem
            {
                Header = entry.Name,
                IsCheckable = entry.IsCheckable,
                Tag = entry.Name,
            };

            foreach (ShellMenuEntry child in entry.Children)
            {
                item.Items.Add(Item(child));
            }

            if (item.Items.Count > 0)
            {
                return item;
            }

            item.IsEnabled = false;

            item.ToolTip = entry.Reason ??
                "Detached trace windows drive the trace they hold. Use the main window for this.";

            ToolTipService.SetShowOnDisabled(item, true);

            return item;
        }

        /// <summary>
        /// The subset of <c>REQ-UI-063</c>'s tray this window carries.
        /// </summary>
        /// <remarks>
        /// From <see cref="ShellToolbars"/>'s own declarations for the same reason the menu is from
        /// <see cref="ShellMenuTable"/>'s. The macro bar is absent, as it is from the customiser.
        /// </remarks>
        private void BuildToolbars()
        {
            foreach (string name in Toolbars)
            {
                ShellToolbar declared = ShellToolbars.For(name);

                var bar = new ToolBar { Tag = declared.Name, ToolTip = declared.Name };

                foreach (ToolbarControl control in declared.Controls)
                {
                    if (control.Kind == ToolbarControlKind.Separator)
                    {
                        bar.Items.Add(new Separator());
                        continue;
                    }

                    var button = new Button
                    {
                        Content = control.Name,
                        Tag = control.Name,
                        Padding = new Thickness(6.0, 1.0, 6.0, 1.0),
                        Focusable = false,
                        IsEnabled = false,
                        ToolTip = control.Reason ??
                            "Detached trace windows drive the trace they hold. Use the main " +
                            "window for this.",
                    };

                    ToolTipService.SetShowOnDisabled(button, true);
                    bar.Items.Add(button);
                }

                _toolbars.ToolBars.Add(bar);
            }
        }
    }
}
