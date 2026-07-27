using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using OpenVSA.Measurement.State;

namespace OpenVSA.Ui.ToolWindows
{
    /// <summary>
    /// Where the eight tool windows sit, and how that survives a restart (<c>REQ-UI-002</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Every window always has a placement.</strong> A layout read from a file that names
    /// only three of the eight still answers for all eight — the missing five get their defaults.
    /// The alternative, returning nothing for a window nobody has moved, makes every caller write
    /// the same null check and get it wrong once.
    /// </para>
    /// <para>
    /// <strong>Unknown names in the file are dropped, not carried.</strong> A placement for a
    /// window that no longer exists is not a setting anyone can act on, and keeping it would grow
    /// the file for the life of the product. This is display preference, not a measurement — the
    /// forward-compatibility rule that makes <c>REQ-STA-003</c> preserve unknown members applies
    /// to states, not to window geometry.
    /// </para>
    /// </remarks>
    public sealed class ToolWindowLayout
    {
        private readonly Dictionary<ToolWindow, ToolWindowPlacement> _placements =
            new Dictionary<ToolWindow, ToolWindowPlacement>();

        /// <summary>Creates a layout with every window at its default.</summary>
        public ToolWindowLayout()
        {
            foreach (ToolWindow window in ToolWindows.All)
            {
                _placements[window] = Default(window);
            }
        }

        /// <summary>Default width for a left- or right-docked window, in device-independent pixels.</summary>
        public const double DefaultWidth = 280.0;

        /// <summary>Default height for a bottom-docked window, in device-independent pixels.</summary>
        public const double DefaultHeight = 180.0;

        /// <summary>
        /// One window's placement.
        /// </summary>
        /// <param name="window">The window.</param>
        /// <exception cref="ArgumentOutOfRangeException">Not a known tool window.</exception>
        public ToolWindowPlacement this[ToolWindow window]
        {
            get
            {
                ToolWindowPlacement placement;

                if (!_placements.TryGetValue(window, out placement))
                {
                    // Reached only for a value outside the enumeration; NameOf reports it.
                    ToolWindows.NameOf(window);
                    placement = Default(window);
                    _placements[window] = placement;
                }

                return placement;
            }
        }

        /// <summary>Whether a window is open.</summary>
        /// <param name="window">The window.</param>
        public bool IsOpen(ToolWindow window) => this[window].IsOpen;

        /// <summary>Opens or closes a window.</summary>
        /// <param name="window">The window.</param>
        /// <param name="open">Whether it is open.</param>
        public void SetOpen(ToolWindow window, bool open) => this[window].IsOpen = open;

        /// <summary>Records where a window has been docked and how big it is.</summary>
        /// <param name="window">The window.</param>
        /// <param name="side">Which edge it is docked to.</param>
        /// <param name="width">Docked width, in device-independent pixels.</param>
        /// <param name="height">Docked height.</param>
        /// <exception cref="ArgumentOutOfRangeException">A size is not positive and finite.</exception>
        public void SetPlacement(
            ToolWindow window, ToolWindowSide side, double width, double height)
        {
            RequireSize(width, nameof(width));
            RequireSize(height, nameof(height));

            ToolWindowPlacement placement = this[window];

            placement.Side = side.ToString();
            placement.Width = width;
            placement.Height = height;
        }

        /// <summary>The side a window is docked to, falling back to its default if the file said something else.</summary>
        /// <param name="window">The window.</param>
        /// <remarks>
        /// A hand-edited file can say <c>Top</c>, or anything at all. Falling back to the default
        /// is the behaviour that opens; refusing to start over a window edge would not be.
        /// </remarks>
        public ToolWindowSide SideOf(ToolWindow window)
        {
            ToolWindowSide side;

            return Enum.TryParse(this[window].Side, out side) &&
                   Enum.IsDefined(typeof(ToolWindowSide), side)
                ? side
                : ToolWindows.DefaultSide(window);
        }

        /// <summary>
        /// Captures the layout as saveable display preference.
        /// </summary>
        /// <returns>One placement per window, in enumeration order.</returns>
        public List<ToolWindowPlacement> ToState()
        {
            var state = new List<ToolWindowPlacement>(ToolWindows.All.Count);

            foreach (ToolWindow window in ToolWindows.All)
            {
                ToolWindowPlacement placement = this[window];

                state.Add(new ToolWindowPlacement
                {
                    Name = ToolWindows.NameOf(window),
                    Side = placement.Side,
                    Width = placement.Width,
                    Height = placement.Height,
                    IsOpen = placement.IsOpen,
                });
            }

            return state;
        }

        /// <summary>
        /// Rebuilds a layout from saved display preference.
        /// </summary>
        /// <param name="state">The saved placements; may be null, short or contain unknown names.</param>
        /// <returns>A layout answering for all eight windows.</returns>
        /// <remarks>
        /// A size of zero or less is replaced by the default rather than honoured. A window
        /// restored to a width of nothing is open, present in the layout and invisible, which is
        /// the hardest kind of missing to diagnose.
        /// </remarks>
        public static ToolWindowLayout FromState(IEnumerable<ToolWindowPlacement> state)
        {
            var layout = new ToolWindowLayout();

            if (state == null)
            {
                return layout;
            }

            foreach (ToolWindowPlacement saved in state)
            {
                if (saved == null)
                {
                    continue;
                }

                ToolWindow? window = ToolWindows.ByName(saved.Name);

                if (window == null)
                {
                    continue;
                }

                ToolWindowPlacement placement = layout[window.Value];

                placement.Side = saved.Side;
                placement.Width = Sane(saved.Width, DefaultWidth);
                placement.Height = Sane(saved.Height, DefaultHeight);
                placement.IsOpen = saved.IsOpen;
            }

            return layout;
        }

        /// <summary>The windows currently open, in enumeration order.</summary>
        public IReadOnlyList<ToolWindow> OpenWindows()
        {
            var open = new List<ToolWindow>();

            foreach (ToolWindow window in ToolWindows.All)
            {
                if (IsOpen(window))
                {
                    open.Add(window);
                }
            }

            return new ReadOnlyCollection<ToolWindow>(open);
        }

        private static ToolWindowPlacement Default(ToolWindow window) =>
            new ToolWindowPlacement
            {
                Name = ToolWindows.NameOf(window),
                Side = ToolWindows.DefaultSide(window).ToString(),
                Width = DefaultWidth,
                Height = DefaultHeight,
                IsOpen = ToolWindows.IsOpenByDefault(window),
            };

        private static double Sane(double value, double fallback) =>
            value > 0.0 && !double.IsInfinity(value) ? value : fallback;

        private static void RequireSize(double value, string name)
        {
            if (!(value > 0.0) || double.IsInfinity(value))
            {
                throw new ArgumentOutOfRangeException(
                    name, value, name + " must be positive and finite.");
            }
        }
    }
}
