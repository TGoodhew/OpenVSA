using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;

namespace OpenVSA.Ui.Layout
{
    /// <summary>
    /// The trace layout presets of <c>REQ-UI-005</c>.
    /// </summary>
    public enum TraceLayoutKind
    {
        /// <summary>All visible traces in a single tab group.</summary>
        Single = 0,

        /// <summary><c>N</c> evenly spaced trace windows stacked vertically.</summary>
        Stack,

        /// <summary>A grid of trace windows, <c>N</c> rows by <c>M</c> columns.</summary>
        Grid,

        /// <summary>A user-defined arrangement.</summary>
        Custom,

        /// <summary>
        /// Auto-layout over all open traces, each in its own space.
        /// </summary>
        /// <remarks>
        /// The clause that distinguishes it from <see cref="Single"/>: traces currently hidden as
        /// tabs are <em>promoted</em> to their own space rather than left sharing one.
        /// </remarks>
        TileVisible,

        /// <summary>Revert to the arrangement in force before the last change.</summary>
        Previous,
    }

    /// <summary>
    /// A rectangle on the document area and the traces shown in it.
    /// </summary>
    /// <remarks>
    /// A slot holding more than one trace is a tab group: they share the space and one is on top.
    /// That is what makes <c>Single</c> one slot of everything and <c>Tile Visible</c> one slot
    /// each, without the two needing different result types.
    /// </remarks>
    public sealed class TraceSlot
    {
        internal TraceSlot(int left, int top, int width, int height, IReadOnlyList<char> traces)
        {
            Left = left;
            Top = top;
            Width = width;
            Height = height;
            Traces = traces;
        }

        /// <summary>Left edge, in pixels from the document area's left.</summary>
        public int Left { get; }

        /// <summary>Top edge, in pixels from the document area's top.</summary>
        public int Top { get; }

        /// <summary>Width in pixels.</summary>
        public int Width { get; }

        /// <summary>Height in pixels.</summary>
        public int Height { get; }

        /// <summary>Right edge, exclusive.</summary>
        public int Right => Left + Width;

        /// <summary>Bottom edge, exclusive.</summary>
        public int Bottom => Top + Height;

        /// <summary>The trace letters in this slot; more than one means a tab group.</summary>
        public IReadOnlyList<char> Traces { get; }

        /// <summary>The trace on top, which is the first.</summary>
        public char Active => Traces.Count > 0 ? Traces[0] : '\0';

        /// <inheritdoc />
        public override string ToString() =>
            "[" + new string(AsArray()) + "] " + Left + "," + Top + " " + Width + "×" + Height;

        private char[] AsArray()
        {
            var letters = new char[Traces.Count];

            for (int i = 0; i < Traces.Count; i++)
            {
                letters[i] = Traces[i];
            }

            return letters;
        }
    }

    /// <summary>
    /// One entry of the layout menu (<c>REQ-UI-005</c>).
    /// </summary>
    /// <remarks>
    /// <strong>Parameterised, not a fixed list.</strong> The requirement's criterion says so
    /// explicitly: <c>Stack N</c> and <c>Grid N×M</c> take arbitrary <c>N</c> and <c>N×M</c>
    /// within the space available. The Agilent-era product shipped five fixed presets — Single,
    /// Stack 2, Grid 2x2, Quad 4, Grid 6 — and enumerating those would be reproducing a
    /// limitation rather than a behaviour.
    /// </remarks>
    public sealed class TraceLayoutPreset
    {
        private TraceLayoutPreset(TraceLayoutKind kind, int rows, int columns)
        {
            Kind = kind;
            Rows = rows;
            Columns = columns;
        }

        /// <summary>Which preset this is.</summary>
        public TraceLayoutKind Kind { get; }

        /// <summary>Rows, for <see cref="TraceLayoutKind.Stack"/> and <see cref="TraceLayoutKind.Grid"/>.</summary>
        public int Rows { get; }

        /// <summary>Columns, for <see cref="TraceLayoutKind.Grid"/>.</summary>
        public int Columns { get; }

        /// <summary>
        /// The name as the layout menu writes it.
        /// </summary>
        /// <remarks>
        /// <c>REQ-UI-005</c>'s criterion is that all six appear "under exactly these names", so the
        /// parameterised two are written the way the requirement writes them — <c>Stack 3</c> and
        /// <c>Grid 2×3</c>, with a multiplication sign rather than a letter x.
        /// </remarks>
        public string Name
        {
            get
            {
                switch (Kind)
                {
                    case TraceLayoutKind.Single: return "Single";
                    case TraceLayoutKind.Custom: return "Custom";
                    case TraceLayoutKind.TileVisible: return "Tile Visible";
                    case TraceLayoutKind.Previous: return "Previous Layout";

                    case TraceLayoutKind.Stack:
                        return "Stack " + Rows.ToString(CultureInfo.CurrentCulture);

                    default:
                        return "Grid " + Rows.ToString(CultureInfo.CurrentCulture) + "×" +
                            Columns.ToString(CultureInfo.CurrentCulture);
                }
            }
        }

        /// <summary>All visible traces in a single tab group.</summary>
        public static TraceLayoutPreset Single() =>
            new TraceLayoutPreset(TraceLayoutKind.Single, 1, 1);

        /// <summary>Evenly spaced trace windows stacked vertically.</summary>
        /// <param name="rows">How many; at least 1.</param>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="rows"/> is less than 1.</exception>
        public static TraceLayoutPreset Stack(int rows)
        {
            RequireAtLeastOne(rows, nameof(rows));

            return new TraceLayoutPreset(TraceLayoutKind.Stack, rows, 1);
        }

        /// <summary>A grid of trace windows.</summary>
        /// <param name="rows">Rows; at least 1.</param>
        /// <param name="columns">Columns; at least 1.</param>
        /// <exception cref="ArgumentOutOfRangeException">A count is less than 1.</exception>
        public static TraceLayoutPreset Grid(int rows, int columns)
        {
            RequireAtLeastOne(rows, nameof(rows));
            RequireAtLeastOne(columns, nameof(columns));

            return new TraceLayoutPreset(TraceLayoutKind.Grid, rows, columns);
        }

        /// <summary>A user-defined arrangement.</summary>
        public static TraceLayoutPreset Custom() =>
            new TraceLayoutPreset(TraceLayoutKind.Custom, 1, 1);

        /// <summary>Auto-layout, every open trace in its own space.</summary>
        public static TraceLayoutPreset TileVisible() =>
            new TraceLayoutPreset(TraceLayoutKind.TileVisible, 1, 1);

        /// <summary>Revert to the arrangement in force before the last change.</summary>
        public static TraceLayoutPreset Previous() =>
            new TraceLayoutPreset(TraceLayoutKind.Previous, 1, 1);

        /// <summary>The six entries the layout menu offers, in the requirement's order.</summary>
        /// <param name="stackRows">The <c>N</c> the Stack entry is currently set to.</param>
        /// <param name="gridRows">The <c>N</c> the Grid entry is set to.</param>
        /// <param name="gridColumns">The <c>M</c> the Grid entry is set to.</param>
        /// <remarks>
        /// The menu shows the parameterised entries at whatever they are currently set to, so a
        /// user who chose <c>Grid 3×2</c> sees that rather than a generic label.
        /// </remarks>
        public static IReadOnlyList<TraceLayoutPreset> Menu(
            int stackRows = 2, int gridRows = 2, int gridColumns = 2) =>
            new ReadOnlyCollection<TraceLayoutPreset>(new[]
            {
                Single(),
                Stack(stackRows),
                Grid(gridRows, gridColumns),
                Custom(),
                TileVisible(),
                Previous(),
            });

        /// <inheritdoc />
        public override string ToString() => Name;

        private static void RequireAtLeastOne(int value, string name)
        {
            if (value < 1)
            {
                throw new ArgumentOutOfRangeException(
                    name, value, name + " must be at least 1.");
            }
        }
    }

    /// <summary>
    /// Arranges trace windows over the document area (<c>REQ-UI-005</c>, <c>REQ-UI-004</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Cells tile exactly and differ by at most a pixel.</strong> Edges are computed by
    /// rounding <c>i·H/N</c> rather than by rounding a cell height and multiplying, so the last
    /// row reaches the bottom of the area exactly and no two cells differ by more than one pixel —
    /// which is what <c>REQ-UI-004</c>'s <em>Resize Traces</em> criterion asks for. Rounding a
    /// height first leaves a strip of unpainted area at the end whose width depends on the number
    /// of rows, and it is a strip nobody notices until it is several pixels.
    /// </para>
    /// <para>
    /// <strong>More cells than traces is not an error.</strong> Asking for <c>Grid 3×3</c> with
    /// four traces is a perfectly ordinary thing to do: four cells are filled and five are empty.
    /// Refusing it, or silently reducing the grid, would both be answers to a question the user
    /// did not ask.
    /// </para>
    /// <para>
    /// <strong>More traces than cells fills the last cell.</strong> The alternative — dropping the
    /// surplus — hides traces the user has open, which is exactly the failure <em>Tile Visible</em>
    /// exists to undo.
    /// </para>
    /// </remarks>
    public static class TraceLayoutEngine
    {
        /// <summary>
        /// Arranges traces over an area.
        /// </summary>
        /// <param name="preset">The layout to apply.</param>
        /// <param name="traces">The visible traces, in order.</param>
        /// <param name="width">Document area width in pixels; must be positive.</param>
        /// <param name="height">Document area height in pixels; must be positive.</param>
        /// <returns>The slots, left to right then top to bottom. Empty when there are no traces.</returns>
        /// <exception cref="ArgumentNullException">An argument is null.</exception>
        /// <exception cref="ArgumentException">The preset is <see cref="TraceLayoutKind.Previous"/>.</exception>
        /// <exception cref="ArgumentOutOfRangeException">A dimension is not positive.</exception>
        public static IReadOnlyList<TraceSlot> Arrange(
            TraceLayoutPreset preset, IReadOnlyList<char> traces, int width, int height)
        {
            if (preset == null)
            {
                throw new ArgumentNullException(nameof(preset));
            }

            if (traces == null)
            {
                throw new ArgumentNullException(nameof(traces));
            }

            if (preset.Kind == TraceLayoutKind.Previous)
            {
                throw new ArgumentException(
                    "Previous Layout is a command, not an arrangement: it names whatever was in " +
                    "force before, which only the layout history knows.",
                    nameof(preset));
            }

            RequirePositive(width, nameof(width));
            RequirePositive(height, nameof(height));

            if (traces.Count == 0)
            {
                return new ReadOnlyCollection<TraceSlot>(new TraceSlot[0]);
            }

            int rows;
            int columns;

            switch (preset.Kind)
            {
                case TraceLayoutKind.Single:
                case TraceLayoutKind.Custom:
                    // One space holding everything. Custom is the user's own arrangement; until
                    // one has been made, it is a single group rather than a guess at what they
                    // would have chosen.
                    return new ReadOnlyCollection<TraceSlot>(new[]
                    {
                        new TraceSlot(0, 0, width, height, Copy(traces)),
                    });

                case TraceLayoutKind.TileVisible:
                    // Every trace in its own space, so the grid follows the count rather than the
                    // other way about. This is the clause that makes Tile Visible different from
                    // Single: a trace hidden as a tab is promoted rather than left sharing.
                    Balance(traces.Count, width, height, out rows, out columns);
                    break;

                case TraceLayoutKind.Stack:
                    rows = preset.Rows;
                    columns = 1;
                    break;

                default:
                    rows = preset.Rows;
                    columns = preset.Columns;
                    break;
            }

            return Tile(traces, width, height, rows, columns);
        }

        /// <summary>
        /// The shape a trace window wants to be: wider than tall, as 16 is to 9.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A frequency axis is long and an amplitude axis is short, so a trace has a natural
        /// proportion and it is not square. This is the figure <see cref="Balance"/> aims cells at.
        /// </para>
        /// <para>
        /// <strong>Two more obvious targets are both wrong.</strong> Aiming for square puts four
        /// traces on a 16:9 area into a 3×2 with one cell empty, because a 533×450 cell is nearer
        /// square than an 800×450 one — measured, and it is not what anybody wants. Aiming for the
        /// <em>area's</em> aspect gets that case right and then puts four traces on a 400×1600
        /// window into a 2×2 of 200×800 cells, each of which is a trace drawn in a chimney.
        /// </para>
        /// </remarks>
        public const double PreferredTraceAspect = 16.0 / 9.0;

        /// <summary>
        /// The rows and columns <em>Tile Visible</em> uses for a trace count.
        /// </summary>
        /// <param name="count">How many traces; must be positive.</param>
        /// <param name="width">Area width.</param>
        /// <param name="height">Area height.</param>
        /// <param name="rows">Receives the row count.</param>
        /// <param name="columns">Receives the column count.</param>
        /// <exception cref="ArgumentOutOfRangeException">A value is not positive.</exception>
        /// <remarks>
        /// "As evenly as possible" read as: cells as near as the area allows to the shape a trace
        /// wants to be, <see cref="PreferredTraceAspect"/>. On a wide document area that puts four
        /// traces in a 2×2; on a tall narrow one, in a single column; on a very wide one, in a
        /// single row. A fixed rule would be right for one shape of window.
        /// </remarks>
        public static void Balance(
            int count, int width, int height, out int rows, out int columns)
        {
            RequirePositive(count, nameof(count));
            RequirePositive(width, nameof(width));
            RequirePositive(height, nameof(height));

            int bestColumns = 1;
            double bestPenalty = double.PositiveInfinity;

            for (int candidate = 1; candidate <= count; candidate++)
            {
                int candidateRows = (count + candidate - 1) / candidate;

                double cellWidth = (double)width / candidate;
                double cellHeight = (double)height / candidateRows;

                // How far the cell's proportion is from a trace's natural one, measured in logs so
                // that twice-too-wide and twice-too-tall are penalised equally - which they are
                // not on a plain ratio.
                double aspect = Math.Log(cellWidth / cellHeight / PreferredTraceAspect);

                // Plus a little for each cell left empty, which breaks ties towards the
                // arrangement that wastes fewer of them.
                double waste = candidateRows * candidate - count;
                double penalty = aspect * aspect + 0.05 * waste;

                if (penalty < bestPenalty)
                {
                    bestPenalty = penalty;
                    bestColumns = candidate;
                }
            }

            columns = bestColumns;
            rows = (count + bestColumns - 1) / bestColumns;
        }

        /// <summary>
        /// Whether an arrangement's cells are all within a pixel of the same size
        /// (<c>REQ-UI-004</c>'s <em>Resize Traces</em>).
        /// </summary>
        /// <param name="slots">The slots to check.</param>
        /// <exception cref="ArgumentNullException"><paramref name="slots"/> is null.</exception>
        public static bool AreEvenlySized(IReadOnlyList<TraceSlot> slots)
        {
            if (slots == null)
            {
                throw new ArgumentNullException(nameof(slots));
            }

            if (slots.Count < 2)
            {
                return true;
            }

            int minWidth = int.MaxValue;
            int maxWidth = int.MinValue;
            int minHeight = int.MaxValue;
            int maxHeight = int.MinValue;

            foreach (TraceSlot slot in slots)
            {
                minWidth = Math.Min(minWidth, slot.Width);
                maxWidth = Math.Max(maxWidth, slot.Width);
                minHeight = Math.Min(minHeight, slot.Height);
                maxHeight = Math.Max(maxHeight, slot.Height);
            }

            return maxWidth - minWidth <= 1 && maxHeight - minHeight <= 1;
        }

        /// <summary>The smallest a trace window may be dragged to, in pixels.</summary>
        /// <remarks>
        /// Below this a trace is a sliver with no graticule and no annotation, and getting it back
        /// means finding a boundary a few pixels wide. A floor is kinder than the freedom to make
        /// a window unusable, and it is the only thing here that stops a drag from doing so.
        /// </remarks>
        public const int MinimumSlotSize = 48;

        /// <summary>
        /// Moves the boundary between two stacked slots, resizing those two and nothing else
        /// (<c>REQ-UI-004</c>).
        /// </summary>
        /// <param name="slots">The current arrangement.</param>
        /// <param name="aboveIndex">Index of the slot above the boundary.</param>
        /// <param name="deltaPixels">How far to move it; positive grows the upper slot.</param>
        /// <returns>A new arrangement; every other slot is unchanged.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="slots"/> is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="aboveIndex"/> is out of range.</exception>
        /// <exception cref="ArgumentException">The two slots do not share a boundary.</exception>
        /// <remarks>
        /// <para>
        /// <strong>Only the two adjacent windows move</strong> — the requirement's criterion, and
        /// the behaviour anyone expects of a splitter. The alternative, redistributing the change
        /// across every row, moves windows the user was not touching and is how a careful
        /// arrangement comes apart under one drag.
        /// </para>
        /// <para>
        /// The drag is clamped so neither slot falls below <see cref="MinimumSlotSize"/>, and the
        /// clamp is silent: a splitter that stopped moving is self-explanatory, where a refusal
        /// mid-drag would not be.
        /// </para>
        /// </remarks>
        public static IReadOnlyList<TraceSlot> DragBoundary(
            IReadOnlyList<TraceSlot> slots, int aboveIndex, int deltaPixels)
        {
            if (slots == null)
            {
                throw new ArgumentNullException(nameof(slots));
            }

            if (aboveIndex < 0 || aboveIndex >= slots.Count - 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(aboveIndex), aboveIndex,
                    "A boundary lies between two slots; this arrangement has " + slots.Count + ".");
            }

            TraceSlot above = slots[aboveIndex];
            TraceSlot below = slots[aboveIndex + 1];

            if (above.Bottom != below.Top || above.Left != below.Left || above.Width != below.Width)
            {
                throw new ArgumentException(
                    "Slots " + aboveIndex + " and " + (aboveIndex + 1) +
                    " do not share a horizontal boundary, so there is nothing between them to drag.",
                    nameof(aboveIndex));
            }

            // Clamped so neither end falls below the floor. Both bounds, because a large negative
            // delta must not shrink the upper slot past it either.
            int lowest = MinimumSlotSize - above.Height;
            int highest = below.Height - MinimumSlotSize;
            int moved = Math.Max(lowest, Math.Min(highest, deltaPixels));

            var adjusted = new List<TraceSlot>(slots.Count);

            for (int i = 0; i < slots.Count; i++)
            {
                if (i == aboveIndex)
                {
                    adjusted.Add(new TraceSlot(
                        above.Left, above.Top, above.Width, above.Height + moved, above.Traces));
                }
                else if (i == aboveIndex + 1)
                {
                    adjusted.Add(new TraceSlot(
                        below.Left, below.Top + moved, below.Width, below.Height - moved,
                        below.Traces));
                }
                else
                {
                    adjusted.Add(slots[i]);
                }
            }

            return new ReadOnlyCollection<TraceSlot>(adjusted);
        }

        private static IReadOnlyList<TraceSlot> Tile(
            IReadOnlyList<char> traces, int width, int height, int rows, int columns)
        {
            var slots = new List<TraceSlot>(rows * columns);
            int cells = rows * columns;
            int next = 0;

            for (int row = 0; row < rows; row++)
            {
                // Rounded from the fraction of the whole, so the edges tile exactly and the last
                // row ends at the bottom rather than short of it.
                int top = (int)Math.Round((double)row * height / rows);
                int bottom = (int)Math.Round((double)(row + 1) * height / rows);

                for (int column = 0; column < columns; column++)
                {
                    int left = (int)Math.Round((double)column * width / columns);
                    int right = (int)Math.Round((double)(column + 1) * width / columns);

                    int index = row * columns + column;
                    var here = new List<char>();

                    if (next < traces.Count)
                    {
                        here.Add(traces[next]);
                        next++;

                        // The last cell takes whatever is left over, rather than the surplus
                        // traces being dropped out of sight.
                        if (index == cells - 1)
                        {
                            while (next < traces.Count)
                            {
                                here.Add(traces[next]);
                                next++;
                            }
                        }
                    }

                    slots.Add(
                        new TraceSlot(
                            left, top, right - left, bottom - top,
                            new ReadOnlyCollection<char>(here)));
                }
            }

            return new ReadOnlyCollection<TraceSlot>(slots);
        }

        private static IReadOnlyList<char> Copy(IReadOnlyList<char> traces)
        {
            var copy = new List<char>(traces.Count);

            foreach (char trace in traces)
            {
                copy.Add(trace);
            }

            return new ReadOnlyCollection<char>(copy);
        }

        private static void RequirePositive(int value, string name)
        {
            if (value < 1)
            {
                throw new ArgumentOutOfRangeException(
                    name, value, name + " must be positive.");
            }
        }
    }
}
