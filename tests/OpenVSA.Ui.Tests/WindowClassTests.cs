using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using OpenVSA.Ui.Layout;
using OpenVSA.Ui.Menus;
using OpenVSA.Ui.Rendering;
using OpenVSA.Ui.ToolWindows;
using Syncfusion.Windows.Tools.Controls;
using Xunit;
using Xunit.Abstractions;

namespace OpenVSA.Ui.Tests
{
    /// <summary>
    /// <c>REQ-UI-001</c>: two window classes, and the things that must not be there.
    /// </summary>
    [Collection("Shell")]
    public class WindowClassTests
    {
        private readonly ShellHost _host;
        private readonly ITestOutputHelper _output;

        /// <summary>Takes the shared shell thread.</summary>
        /// <param name="host">The thread every shell in this collection is built on.</param>
        /// <param name="output">Where the walked tree is written.</param>
        public WindowClassTests(ShellHost host, ITestOutputHelper output)
        {
            _host = host;
            _output = output;
        }

        [Fact]
        public void ToolWindowsAreRefusedTheDocumentArea()
        {
            // "Attempting to dock a tool window into the document area ... is refused rather than
            // accepted — the two classes are distinct, not merely conventionally used differently."
            _host.Run(() =>
            {
                var shell = Built();

                foreach (ToolWindow window in OpenVSA.Ui.ToolWindows.ToolWindows.All)
                {
                    ContentControl pane = shell.ToolWindows.PaneOf(window);

                    Assert.True(pane != null, window + " has no pane.");

                    Assert.False(
                        DockingManager.GetCanDocument(pane),
                        window + " may be docked into the document area, and it is a tool window.");
                }
            });
        }

        [Fact]
        public void TraceWindowsAreDocumentsAndCarryATabRatherThanATitleBar()
        {
            // A trace window appears as a tab in a tab group; a tool window carries a title bar.
            _host.Run(() =>
            {
                var shell = Built();

                Assert.NotEmpty(shell.DocumentArea.TabStrips);

                foreach (TraceTabStrip strip in shell.DocumentArea.TabStrips)
                {
                    Assert.NotEmpty(strip.Traces);
                }

                // And every trace is on exactly one strip: a trace docked to an edge would be one
                // that appeared on none.
                var onStrips = shell.DocumentArea.TabStrips
                    .SelectMany(s => s.Traces)
                    .ToList();

                foreach (char trace in shell.DocumentArea.VisibleTraces)
                {
                    Assert.Single(onStrips.Where(t => t == trace));
                }
            });
        }

        [Fact]
        public void ThereIsNoRightHandSoftkeyColumnAnywhereInTheShell()
        {
            // "asserted by a test over the shell's visual tree so it cannot be reintroduced
            // unnoticed". The reference SOFTWARE has none; that belongs to the hardware this
            // product line grew out of, and adding one would be a retro affectation.
            _host.Run(() =>
            {
                ShellWindow shell = Shown();

                try
                {
                    shell.UpdateLayout();

                    double shellRight = shell.ActualWidth;
                    var offenders = new List<string>();

                    foreach (FrameworkElement element in Visuals(shell))
                    {
                        // A softkey column is a tall, narrow stack of buttons pinned to the right
                        // edge. Described by shape rather than by name, because a test looking for
                        // a control called "SoftkeyPanel" is one a differently named class walks
                        // straight past.
                        if (!IsButtonStack(element))
                        {
                            continue;
                        }

                        Point at = element.TranslatePoint(new Point(0.0, 0.0), shell);

                        bool againstTheRightEdge = at.X > shellRight * 0.75;
                        bool tallAndNarrow =
                            element.ActualHeight > shell.ActualHeight * 0.5 &&
                            element.ActualWidth < shellRight * 0.2;

                        if (againstTheRightEdge && tallAndNarrow)
                        {
                            offenders.Add(
                                element.GetType().Name + " at " + at.X + " sized " +
                                element.ActualWidth + "×" + element.ActualHeight);
                        }
                    }

                    Assert.True(
                        offenders.Count == 0,
                        "REQ-UI-001 forbids a right-hand softkey column:" + Environment.NewLine +
                        string.Join(Environment.NewLine, offenders));
                }
                finally
                {
                    shell.Close();
                }
            });
        }

        [Fact]
        public void TheShellsTopLevelChildrenAreInTheStatedOrder()
        {
            // "title bar → menu bar → toolbar band → document area → status bar". The title bar is
            // the window's own, so what is asserted is the four inside it, top to bottom.
            _host.Run(() =>
            {
                var shell = Built();

                var root = shell.Content as DockPanel;

                Assert.True(root != null, "The shell's content is not the docked root.");

                var order = new List<string>();

                foreach (UIElement child in root.Children)
                {
                    Dock dock = DockPanel.GetDock(child);
                    string name = (child as FrameworkElement)?.Name ?? child.GetType().Name;

                    order.Add(name + ":" + dock);
                }

                _output.WriteLine(string.Join("  ", order));

                // The menu bar and the toolbar band dock to the top, in that order; the status bar
                // to the bottom; the document area fills what is left and is last.
                Assert.Equal("MainMenu", ((FrameworkElement)root.Children[0]).Name);
                Assert.Equal(Dock.Top, DockPanel.GetDock(root.Children[0]));

                Assert.Equal("Toolbars", ((FrameworkElement)root.Children[1]).Name);
                Assert.Equal(Dock.Top, DockPanel.GetDock(root.Children[1]));

                Assert.Equal(Dock.Bottom, DockPanel.GetDock(root.Children[2]));
                Assert.IsType<StatusBar>(root.Children[2]);

                Assert.IsType<DockingManager>(root.Children[root.Children.Count - 1]);
            });
        }

        [Fact]
        public void TheSoftkeyCheckWouldCatchOneIfItAppeared()
        {
            // The check above passes by finding nothing, which is exactly how a shape-matching test
            // comes to mean nothing. So: a column of the shape it looks for is recognised, and the
            // shell's own furniture is not.
            _host.Run(() =>
            {
                var softkeys = new StackPanel();

                for (int i = 0; i < 6; i++)
                {
                    softkeys.Children.Add(new Button { Content = "F" + (i + 1) });
                }

                Assert.True(IsButtonStack(softkeys), "A column of six buttons is not recognised.");

                // A toolbar is a row of buttons, not a column of them, and must not be matched by
                // shape alone — which is why the geometry test also asks where it is and how tall.
                var toolbar = new StackPanel { Orientation = Orientation.Horizontal };

                toolbar.Children.Add(new Button());
                toolbar.Children.Add(new Button());

                Assert.False(IsButtonStack(toolbar), "Two buttons count as a softkey column.");

                // And the walker reaches a real shell rather than stopping at its root.
                ShellWindow shell = Shown();

                try
                {
                    shell.UpdateLayout();

                    Assert.True(
                        Visuals(shell).Count() > 50,
                        "The visual walk found only " + Visuals(shell).Count() + " elements.");
                }
                finally
                {
                    shell.Close();
                }
            });
        }

        private static bool IsButtonStack(FrameworkElement element)
        {
            var panel = element as Panel;

            if (panel == null || panel.Children.Count < 4)
            {
                return false;
            }

            int buttons = panel.Children.OfType<ButtonBase>().Count();

            return buttons >= 4 && buttons == panel.Children.Count;
        }

        private static IEnumerable<FrameworkElement> Visuals(DependencyObject root)
        {
            int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);

            for (int i = 0; i < count; i++)
            {
                DependencyObject child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);

                var element = child as FrameworkElement;

                if (element != null)
                {
                    yield return element;
                }

                foreach (FrameworkElement deeper in Visuals(child))
                {
                    yield return deeper;
                }
            }
        }

        private static ShellWindow Built() =>
            new ShellWindow { PersistPreferences = false, Interactive = false };

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
    }

    /// <summary>
    /// <c>REQ-UI-003</c>: a detached trace is a full secondary window.
    /// </summary>
    [Collection("Shell")]
    public class DetachedTraceWindowTests
    {
        private readonly ShellHost _host;

        /// <summary>Takes the shared shell thread.</summary>
        /// <param name="host">The thread every shell in this collection is built on.</param>
        public DetachedTraceWindowTests(ShellHost host)
        {
            _host = host;
        }

        [Fact]
        public void ADetachedWindowHasItsOwnWorkingMenuBarAndToolbar()
        {
            // "A detached trace window has its own working menu bar and toolbar" — its own, not the
            // main window's reparented, and working rather than a row of empty headers.
            _host.Run(() =>
            {
                var shell = Built();
                var plot = new TracePlot();

                var window = new TraceWindow('B', plot);

                Assert.Equal(TraceWindow.MenuNames.Count, window.MenuBar.Items.Count);
                Assert.Equal(TraceWindow.ToolbarNames.Count, window.ToolbarTray.ToolBars.Count);

                Assert.True(window.IsWorking, "The detached window's bars are empty.");

                // Its own: not the same objects the main window uses.
                Assert.NotSame(shell.MenuBar, window.MenuBar);
                Assert.NotSame(shell.ToolbarTray, window.ToolbarTray);
            });
        }

        [Fact]
        public void ItCarriesASubsetOfTheMenuBarRatherThanAllOfIt()
        {
            // The requirement says "a subset of the menu items", and the way this gets
            // over-delivered is by cloning the whole bar and calling it one.
            Assert.True(
                TraceWindow.MenuNames.Count < ShellMenus.Names.Count,
                "The detached window carries every menu the main bar does.");

            foreach (string name in TraceWindow.MenuNames)
            {
                Assert.Contains(name, ShellMenus.Names);
            }

            // The two absences that matter: a detached trace connects no instruments and saves no
            // layouts.
            Assert.DoesNotContain("Hardware", TraceWindow.MenuNames);
            Assert.DoesNotContain("File", TraceWindow.MenuNames);

            // And the trace-specific ones the requirement names are all there.
            foreach (string wanted in new[] { "Trace", "Marker", "Acquisition" })
            {
                Assert.Contains(wanted, TraceWindow.MenuNames);
            }
        }

        [Fact]
        public void DetachingMovesTheTraceOutOfTheDocumentArea()
        {
            // One trace in one place: a detached window holding a second plot fed from the same
            // snapshot would be two traces that agree until one of them is scaled.
            _host.Run(() =>
            {
                var shell = Built();

                shell.DocumentArea.AddTrace('B');

                TracePlot plot = shell.DocumentArea.PlotOf('B');

                TraceWindow window = shell.DetachTrace('B');

                Assert.True(window != null, "The trace did not detach.");
                Assert.Same(plot, window.Content2);
                Assert.DoesNotContain('B', shell.DocumentArea.Traces);
                Assert.Contains('B', shell.DetachedTraces);
            });
        }

        [Fact]
        public void TheLastTraceStaysInTheMainWindow()
        {
            // The same rule that makes the last trace uncloseable: a document area with nothing in
            // it is a grey rectangle with no way back to a measurement.
            _host.Run(() =>
            {
                var shell = Built();

                Assert.Single(shell.DocumentArea.Traces);
                Assert.Null(shell.DetachTrace(shell.DocumentArea.ActiveTrace));
                Assert.Single(shell.DocumentArea.Traces);
            });
        }

        [Fact]
        public void ADetachedWindowIsCapturedInTheSavedLayout()
        {
            // "and is captured in saved layout state" — the criterion's third clause. A layout that
            // remembered the docked panes and forgot a window on the second monitor would put the
            // user back with one screen's worth of what they left.
            _host.Run(() =>
            {
                var shell = Built();

                shell.DocumentArea.AddTrace('B');

                TraceWindow window = shell.DetachTrace('B');

                window.PlaceAt(new Rect(1920.0, 100.0, 900.0, 600.0));

                List<OpenVSA.Measurement.State.DetachedTraceState> saved =
                    shell.DetachedTraceStates();

                Assert.Single(saved);
                Assert.Equal("B", saved[0].Trace);
                Assert.Equal(1920.0, saved[0].Left);
                Assert.Equal(100.0, saved[0].Top);
                Assert.Equal(900.0, saved[0].Width);
                Assert.Equal(600.0, saved[0].Height);

                // And it round-trips through the sidecar.
                string json = OpenVSA.Measurement.State.SidecarFile.Write(
                    new OpenVSA.Measurement.State.DisplayPreferencesState
                    {
                        DetachedTraces = saved,
                    });

                var read = OpenVSA.Measurement.State.SidecarFile
                    .Read<OpenVSA.Measurement.State.DisplayPreferencesState>(json);

                Assert.Single(read.DetachedTraces);
                Assert.Equal("B", read.DetachedTraces[0].Trace);
                Assert.Equal(1920.0, read.DetachedTraces[0].Left);
            });
        }

        [Fact]
        public void MissingArgumentsAreRefused()
        {
            _host.Run(() => Assert.Throws<ArgumentNullException>(() => new TraceWindow('A', null)));
        }

        private static ShellWindow Built() =>
            new ShellWindow { PersistPreferences = false, Interactive = false };
    }

    /// <summary>
    /// <c>REQ-UI-052</c>'s remaining clause: the symbol table is one entry in the trace list.
    /// </summary>
    [Collection("Shell")]
    public class SymbolTableDocumentTests
    {
        private readonly ShellHost _host;

        /// <summary>Takes the shared shell thread.</summary>
        /// <param name="host">The thread every shell in this collection is built on.</param>
        public SymbolTableDocumentTests(ShellHost host)
        {
            _host = host;
        }

        [Fact]
        public void TheTraceListGainsExactlyOneEntryForBothPortions()
        {
            // "asserted structurally: the trace list contains a single entry, and selecting it
            // selects both portions". The requirement's own warning is that getting this wrong
            // means building two traces where the product has one.
            _host.Run(() =>
            {
                var shell = Built();

                int before = shell.DocumentArea.Traces.Count;

                SymbolTablePanel panel = shell.DocumentArea.AddSymbolTable('S');

                Assert.Equal(before + 1, shell.DocumentArea.Traces.Count);
                Assert.Contains('S', shell.DocumentArea.Traces);

                // One document, two portions inside it.
                Assert.Same(panel, shell.DocumentArea.ContentOf('S'));
                Assert.Equal(2, panel.PortionCount);

                // It is not a plot, and the document area does not pretend it is.
                Assert.Null(shell.DocumentArea.PlotOf('S'));
            });
        }

        [Fact]
        public void SelectingItSelectsBothPortions()
        {
            _host.Run(() =>
            {
                var shell = Built();

                SymbolTablePanel panel = shell.DocumentArea.AddSymbolTable('S');

                shell.DocumentArea.ActiveTrace = 'S';

                Assert.Equal('S', shell.DocumentArea.ActiveTrace);

                // Selecting the trace selects the one element that carries both portions — there is
                // nothing else to select, which is the structural point.
                Assert.Same(panel, shell.DocumentArea.ContentOf(shell.DocumentArea.ActiveTrace));
                Assert.Same(panel, panel.SummaryPortion.Parent);
                Assert.Same(panel, panel.StreamPortion.Parent);
            });
        }

        private static ShellWindow Built() =>
            new ShellWindow { PersistPreferences = false, Interactive = false };
    }
}
