using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace OpenVSA.Ui.ToolWindows
{
    /// <summary>
    /// The eight tool windows <c>REQ-UI-002</c> names.
    /// </summary>
    /// <remarks>
    /// The requirement lists them by name and its criterion is that "all eight exist under exactly
    /// these names". The names therefore live in <see cref="ToolWindows.NameOf"/> as literals and
    /// are asserted as exact strings — <c>SCPI Log</c>, not <c>ScpiLog</c>, and
    /// <c>Block Diagram</c>, not <c>Block diagram</c>. An enumeration member's own spelling is a C#
    /// identifier and cannot carry a space, which is exactly why the two are kept apart rather than
    /// derived from one another.
    /// </remarks>
    public enum ToolWindow
    {
        /// <summary>Every marker on every trace (<c>REQ-MKR-006</c>).</summary>
        Markers = 0,

        /// <summary>Measurement results and messages.</summary>
        Output,

        /// <summary>Recording playback transport (<c>REQ-REC-002</c>).</summary>
        Player,

        /// <summary>Traffic to and from the instrument.</summary>
        ScpiLog,

        /// <summary>Application events, warnings and errors.</summary>
        EventLog,

        /// <summary>The measurement contexts in the session (<c>REQ-STA-004</c>).</summary>
        Contexts,

        /// <summary>The signal path from input to trace.</summary>
        BlockDiagram,

        /// <summary>Saved command sequences.</summary>
        Macros,
    }

    /// <summary>Which menu a tool window is opened from (<c>REQ-UI-061</c>).</summary>
    public enum ToolWindowMenu
    {
        /// <summary>The Window menu.</summary>
        Window = 0,

        /// <summary>The Marker menu, where the requirement puts the Markers window.</summary>
        Marker,
    }

    /// <summary>Which edge of the document area a tool window docks to by default.</summary>
    public enum ToolWindowSide
    {
        /// <summary>Left of the document area.</summary>
        Left = 0,

        /// <summary>Right of it.</summary>
        Right,

        /// <summary>Below it.</summary>
        Bottom,
    }

    /// <summary>
    /// The names, menus and default placement of <c>REQ-UI-002</c>'s tool windows.
    /// </summary>
    /// <remarks>
    /// Kept apart from the shell's XAML so the requirement's criterion can be asserted without a
    /// visual tree — the same reason <c>TraceIndicators</c> is not inside the control that draws
    /// it. A test over these answers "do all eight exist, under exactly these names, on the right
    /// menu"; the XAML then has to agree with it, and a test asserts that too.
    /// </remarks>
    public static class ToolWindows
    {
        /// <summary>Every tool window, in the order the Window menu lists them.</summary>
        public static IReadOnlyList<ToolWindow> All { get; } =
            new ReadOnlyCollection<ToolWindow>((ToolWindow[])Enum.GetValues(typeof(ToolWindow)));

        /// <summary>
        /// The window's title, exactly as <c>REQ-UI-002</c> writes it.
        /// </summary>
        /// <param name="window">The window.</param>
        /// <exception cref="ArgumentOutOfRangeException">Not a known tool window.</exception>
        public static string NameOf(ToolWindow window)
        {
            switch (window)
            {
                case ToolWindow.Markers: return "Markers";
                case ToolWindow.Output: return "Output";
                case ToolWindow.Player: return "Player";
                case ToolWindow.ScpiLog: return "SCPI Log";
                case ToolWindow.EventLog: return "Event Log";
                case ToolWindow.Contexts: return "Contexts";
                case ToolWindow.BlockDiagram: return "Block Diagram";
                case ToolWindow.Macros: return "Macros";

                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(window), window, "Not a known tool window.");
            }
        }

        /// <summary>
        /// Which menu opens the window (<c>REQ-UI-002</c>: "the Window or Marker menu").
        /// </summary>
        /// <param name="window">The window.</param>
        /// <exception cref="ArgumentOutOfRangeException">Not a known tool window.</exception>
        public static ToolWindowMenu MenuOf(ToolWindow window)
        {
            // Called for its argument check, so an unknown window fails here rather than being
            // silently filed under Window.
            NameOf(window);

            return window == ToolWindow.Markers ? ToolWindowMenu.Marker : ToolWindowMenu.Window;
        }

        /// <summary>
        /// Where the window docks before a user has moved it.
        /// </summary>
        /// <param name="window">The window.</param>
        /// <exception cref="ArgumentOutOfRangeException">Not a known tool window.</exception>
        /// <remarks>
        /// The logs and the player go along the bottom, where a wide short pane suits a stream of
        /// lines; the lists and the diagram go to the right, where a tall narrow one does. A
        /// default that put all eight on one edge would be arranged in the sense that nothing had
        /// been left to chance, and unusable.
        /// </remarks>
        public static ToolWindowSide DefaultSide(ToolWindow window)
        {
            switch (window)
            {
                case ToolWindow.Output:
                case ToolWindow.ScpiLog:
                case ToolWindow.EventLog:
                case ToolWindow.Player:
                    return ToolWindowSide.Bottom;

                default:
                    NameOf(window);
                    return ToolWindowSide.Right;
            }
        }

        /// <summary>
        /// Whether the window is open when the application has never been run before.
        /// </summary>
        /// <param name="window">The window.</param>
        /// <returns>Always <c>false</c>; every one of the eight starts closed.</returns>
        /// <exception cref="ArgumentOutOfRangeException">Not a known tool window.</exception>
        /// <remarks>
        /// <para>
        /// <strong>Measured, not assumed.</strong> The shell already docks Measurement and Hardware
        /// around the document area. Opening even one of these eight as well leaves the trace a
        /// vertical strip on a 1280-wide window — which is what it did, and what a first-run user
        /// would see. Eight would leave no trace at all.
        /// </para>
        /// <para>
        /// They are one menu item away, the Window menu lists seven of them and the Marker menu the
        /// eighth, and from the first time one is opened its state persists. Nothing is hidden;
        /// nothing is in the way either.
        /// </para>
        /// </remarks>
        public static bool IsOpenByDefault(ToolWindow window)
        {
            NameOf(window);

            return false;
        }

        /// <summary>The window with a given name, or <c>null</c> if the name is not one of the eight.</summary>
        /// <param name="name">The name to look up; compared exactly.</param>
        public static ToolWindow? ByName(string name)
        {
            foreach (ToolWindow window in All)
            {
                if (string.Equals(NameOf(window), name, StringComparison.Ordinal))
                {
                    return window;
                }
            }

            return null;
        }
    }
}
