using System;
using System.Collections.Generic;
using System.Linq;
using OpenVSA.Ui.Layout;
using Xunit;
using Xunit.Abstractions;

namespace OpenVSA.Ui.Tests
{
    /// <summary>
    /// <c>REQ-UI-005</c>'s six layout presets, and <c>REQ-UI-004</c>'s even redistribution.
    /// </summary>
    public class TraceLayoutTests
    {
        private const int Width = 1200;
        private const int Height = 800;

        private readonly ITestOutputHelper _output;

        public TraceLayoutTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void AllSixEntriesAppearUnderExactlyTheseNames()
        {
            Assert.Equal(
                new[] { "Single", "Stack 2", "Grid 2×2", "Custom", "Tile Visible", "Previous Layout" },
                TraceLayoutPreset.Menu().Select(p => p.Name));
        }

        [Fact]
        public void StackAndGridAreParameterisedNotAFixedList()
        {
            // The criterion says so explicitly. The Agilent-era product shipped Single, Stack 2,
            // Grid 2x2, Quad 4 and Grid 6; enumerating those would reproduce a limitation.
            Assert.Equal("Stack 7", TraceLayoutPreset.Stack(7).Name);
            Assert.Equal("Grid 3×5", TraceLayoutPreset.Grid(3, 5).Name);
            Assert.Equal("Stack 11", TraceLayoutPreset.Menu(stackRows: 11)[1].Name);
            Assert.Equal("Grid 4×3", TraceLayoutPreset.Menu(gridRows: 4, gridColumns: 3)[2].Name);
        }

        [Fact]
        public void SinglePutsEveryTraceInOneTabGroup()
        {
            IReadOnlyList<TraceSlot> slots = TraceLayoutEngine.Arrange(
                TraceLayoutPreset.Single(), Traces("ABCD"), Width, Height);

            TraceSlot only = Assert.Single(slots);

            Assert.Equal(new[] { 'A', 'B', 'C', 'D' }, only.Traces);
            Assert.Equal(0, only.Left);
            Assert.Equal(0, only.Top);
            Assert.Equal(Width, only.Width);
            Assert.Equal(Height, only.Height);
        }

        [Fact]
        public void StackNGivesNRowsOfFullWidth()
        {
            IReadOnlyList<TraceSlot> slots = TraceLayoutEngine.Arrange(
                TraceLayoutPreset.Stack(3), Traces("ABC"), Width, Height);

            Assert.Equal(3, slots.Count);

            foreach (TraceSlot slot in slots)
            {
                Assert.Equal(0, slot.Left);
                Assert.Equal(Width, slot.Width);
            }

            Assert.Equal(new[] { 'A' }, slots[0].Traces);
            Assert.Equal(new[] { 'B' }, slots[1].Traces);
            Assert.Equal(new[] { 'C' }, slots[2].Traces);
        }

        [Fact]
        public void GridNByMGivesNRowsAndMColumnsInReadingOrder()
        {
            IReadOnlyList<TraceSlot> slots = TraceLayoutEngine.Arrange(
                TraceLayoutPreset.Grid(2, 3), Traces("ABCDEF"), Width, Height);

            Assert.Equal(6, slots.Count);

            // Left to right, then top to bottom - so the letters land where a reader expects.
            Assert.Equal(new[] { 'A', 'B', 'C', 'D', 'E', 'F' }, slots.Select(s => s.Active));

            Assert.Equal(0, slots[0].Left);
            Assert.Equal(0, slots[0].Top);
            Assert.Equal(slots[0].Right, slots[1].Left);
            Assert.Equal(slots[0].Top, slots[2].Top);
            Assert.Equal(slots[0].Bottom, slots[3].Top);
        }

        [Theory]
        [InlineData(3)]
        [InlineData(7)]
        [InlineData(13)]
        public void CellsTileTheAreaExactlyAndDifferByAtMostOnePixel(int rows)
        {
            // REQ-UI-004's Resize Traces criterion: "leaves all trace windows within one pixel of
            // equal size". Rounding a cell height and multiplying leaves a strip of unpainted area
            // at the end whose size depends on the row count; rounding i*H/N does not.
            IReadOnlyList<TraceSlot> slots = TraceLayoutEngine.Arrange(
                TraceLayoutPreset.Stack(rows), Traces(new string('A', rows)), Width, Height);

            Assert.True(TraceLayoutEngine.AreEvenlySized(slots));

            Assert.Equal(0, slots[0].Top);
            Assert.Equal(Height, slots[slots.Count - 1].Bottom);

            for (int i = 1; i < slots.Count; i++)
            {
                Assert.Equal(slots[i - 1].Bottom, slots[i].Top);
            }
        }

        [Fact]
        public void AGridTilesExactlyInBothDirections()
        {
            IReadOnlyList<TraceSlot> slots = TraceLayoutEngine.Arrange(
                TraceLayoutPreset.Grid(3, 7), Traces("ABCDEFGHIJKLMNOPQRSTU"), 1000, 700);

            Assert.True(TraceLayoutEngine.AreEvenlySized(slots));
            Assert.Equal(1000, slots.Max(s => s.Right));
            Assert.Equal(700, slots.Max(s => s.Bottom));
            Assert.Equal(0, slots.Min(s => s.Left));
            Assert.Equal(0, slots.Min(s => s.Top));
        }

        [Fact]
        public void TileVisiblePromotesTracesThatWereHiddenAsTabs()
        {
            // The clause that distinguishes Tile Visible from Single, and the criterion tests it
            // exactly this way: start from a tab group of several and assert each becomes
            // separately visible.
            IReadOnlyList<char> traces = Traces("ABCDE");

            TraceSlot grouped = Assert.Single(
                TraceLayoutEngine.Arrange(TraceLayoutPreset.Single(), traces, Width, Height));

            Assert.Equal(5, grouped.Traces.Count);

            IReadOnlyList<TraceSlot> tiled = TraceLayoutEngine.Arrange(
                TraceLayoutPreset.TileVisible(), traces, Width, Height);

            _output.WriteLine(string.Join("  ", tiled.Select(s => s.ToString())));

            // Every trace has a space of its own, and no slot holds two.
            IEnumerable<char> placed = tiled.SelectMany(s => s.Traces);

            Assert.Equal(traces, placed.ToArray());
            Assert.All(tiled.Where(s => s.Traces.Count > 0), s => Assert.Single(s.Traces));
            Assert.Equal(5, tiled.Count(s => s.Traces.Count == 1));
        }

        [Fact]
        public void TileVisibleFavoursCellsShapedLikeATrace()
        {
            // "Allocating space as evenly as possible", read as cells as near as the area allows
            // to the shape a trace wants: wider than tall, because a frequency axis is long and an
            // amplitude axis is short.
            //
            // Aiming for square instead puts these four into a 3×2 with a cell empty, because a
            // 533×450 cell is nearer square than an 800×450 one. Measured, and not what anyone
            // wants - which is why the target is a trace's proportion and not squareness.
            int rows;
            int columns;

            TraceLayoutEngine.Balance(4, 1600, 900, out rows, out columns);
            Assert.Equal(2, rows);
            Assert.Equal(2, columns);

            // A tall narrow area stacks them instead.
            TraceLayoutEngine.Balance(4, 400, 1600, out rows, out columns);
            Assert.True(rows > columns, rows + " rows by " + columns + " columns on a tall area.");

            // A very wide one puts them in a row.
            TraceLayoutEngine.Balance(4, 4000, 300, out rows, out columns);
            Assert.True(columns > rows, rows + " rows by " + columns + " columns on a wide area.");
        }

        [Fact]
        public void OneTraceTiledIsTheWholeArea()
        {
            TraceSlot only = Assert.Single(
                TraceLayoutEngine.Arrange(
                    TraceLayoutPreset.TileVisible(), Traces("A"), Width, Height));

            Assert.Equal(Width, only.Width);
            Assert.Equal(Height, only.Height);
        }

        [Fact]
        public void MoreCellsThanTracesLeavesTheSurplusEmpty()
        {
            // Asking for Grid 3×3 with four traces is an ordinary thing to do. Refusing it, or
            // silently shrinking the grid, both answer a question the user did not ask.
            IReadOnlyList<TraceSlot> slots = TraceLayoutEngine.Arrange(
                TraceLayoutPreset.Grid(3, 3), Traces("ABCD"), Width, Height);

            Assert.Equal(9, slots.Count);
            Assert.Equal(4, slots.Count(s => s.Traces.Count > 0));
            Assert.Equal(5, slots.Count(s => s.Traces.Count == 0));
        }

        [Fact]
        public void MoreTracesThanCellsFillsTheLastCellRatherThanDroppingThem()
        {
            // Dropping the surplus hides traces the user has open, which is the failure Tile
            // Visible exists to undo.
            IReadOnlyList<TraceSlot> slots = TraceLayoutEngine.Arrange(
                TraceLayoutPreset.Stack(2), Traces("ABCDE"), Width, Height);

            Assert.Equal(2, slots.Count);
            Assert.Equal(new[] { 'A' }, slots[0].Traces);
            Assert.Equal(new[] { 'B', 'C', 'D', 'E' }, slots[1].Traces);
        }

        [Fact]
        public void NoTracesGivesNoSlots()
        {
            Assert.Empty(
                TraceLayoutEngine.Arrange(
                    TraceLayoutPreset.Grid(2, 2), Traces(string.Empty), Width, Height));
        }

        // ---- Previous Layout -------------------------------------------------------------------

        [Fact]
        public void PreviousLayoutRestoresTheArrangementBeforeTheLastChange()
        {
            var history = new TraceLayoutHistory();
            IReadOnlyList<char> traces = Traces("ABCD");

            history.Apply(TraceLayoutPreset.Grid(2, 2), traces, Width, Height);
            history.Apply(TraceLayoutPreset.Stack(4), traces, Width, Height);

            Assert.Equal("Stack 4", history.Current.Name);

            history.Apply(TraceLayoutPreset.Previous(), traces, Width, Height);

            Assert.Equal("Grid 2×2", history.Current.Name);
            Assert.Equal(4, history.CurrentSlots.Count);
        }

        [Fact]
        public void PreviousLayoutIsAToggleRatherThanAnUndoStack()
        {
            // The requirement asks for "the arrangement in force before the last layout change",
            // which one menu entry implies - and it is what makes the entry useful for comparing
            // two arrangements.
            var history = new TraceLayoutHistory();
            IReadOnlyList<char> traces = Traces("ABC");

            history.Apply(TraceLayoutPreset.Single(), traces, Width, Height);
            history.Apply(TraceLayoutPreset.Stack(3), traces, Width, Height);

            history.Apply(TraceLayoutPreset.Previous(), traces, Width, Height);
            Assert.Equal("Single", history.Current.Name);

            history.Apply(TraceLayoutPreset.Previous(), traces, Width, Height);
            Assert.Equal("Stack 3", history.Current.Name);
        }

        [Fact]
        public void ReApplyingTheSameLayoutDoesNotConsumeTheWayBack()
        {
            // Otherwise choosing Grid 2×2 while already in it would make the previous layout
            // Grid 2×2 as well, and the user would have no way back to what they had.
            var history = new TraceLayoutHistory();
            IReadOnlyList<char> traces = Traces("ABCD");

            history.Apply(TraceLayoutPreset.Single(), traces, Width, Height);
            history.Apply(TraceLayoutPreset.Grid(2, 2), traces, Width, Height);
            history.Apply(TraceLayoutPreset.Grid(2, 2), traces, Width, Height);

            history.Apply(TraceLayoutPreset.Previous(), traces, Width, Height);

            Assert.Equal("Single", history.Current.Name);
        }

        [Fact]
        public void PreviousLayoutRestoresACustomArrangementExactly()
        {
            // The criterion names the Custom case, and "Custom" describes nothing on its own -
            // reverting to it has to bring back where the user actually put things.
            var history = new TraceLayoutHistory();
            IReadOnlyList<char> traces = Traces("AB");

            IReadOnlyList<TraceSlot> hand = TraceLayoutEngine.Arrange(
                TraceLayoutPreset.Grid(1, 2), traces, 900, 600);

            history.RecordCustom(hand);

            Assert.Equal("Custom", history.Current.Name);

            history.Apply(TraceLayoutPreset.Stack(2), traces, Width, Height);
            Assert.Equal("Stack 2", history.Current.Name);

            history.Apply(TraceLayoutPreset.Previous(), traces, Width, Height);

            Assert.Equal("Custom", history.Current.Name);
            Assert.Equal(hand.Count, history.CurrentSlots.Count);

            for (int i = 0; i < hand.Count; i++)
            {
                Assert.Equal(hand[i].Left, history.CurrentSlots[i].Left);
                Assert.Equal(hand[i].Width, history.CurrentSlots[i].Width);
                Assert.Equal(hand[i].Height, history.CurrentSlots[i].Height);
            }
        }

        [Fact]
        public void ADragByHandDisplacesThePreviousLayout()
        {
            // Dragging a boundary is a layout change like any other. If it did not count, Previous
            // Layout would step back past everything done by hand since the last menu choice.
            var history = new TraceLayoutHistory();
            IReadOnlyList<char> traces = Traces("AB");

            history.Apply(TraceLayoutPreset.Stack(2), traces, Width, Height);
            history.RecordCustom(
                TraceLayoutEngine.Arrange(TraceLayoutPreset.Grid(1, 2), traces, Width, Height));

            history.Apply(TraceLayoutPreset.Previous(), traces, Width, Height);

            Assert.Equal("Stack 2", history.Current.Name);
        }

        [Fact]
        public void RevertingAfterAResizeFitsTheNewArea()
        {
            // A preset is re-arranged for the current area rather than replayed at the old size,
            // so reverting after the window changed gives a layout that fits.
            var history = new TraceLayoutHistory();
            IReadOnlyList<char> traces = Traces("ABCD");

            history.Apply(TraceLayoutPreset.Grid(2, 2), traces, 1000, 800);
            history.Apply(TraceLayoutPreset.Single(), traces, 1000, 800);

            IReadOnlyList<TraceSlot> back =
                history.Apply(TraceLayoutPreset.Previous(), traces, 1600, 400);

            Assert.Equal(1600, back.Max(s => s.Right));
            Assert.Equal(400, back.Max(s => s.Bottom));
        }

        [Fact]
        public void ThereIsNothingToRevertToAtTheStart()
        {
            var history = new TraceLayoutHistory();

            Assert.False(history.CanRevert);
            Assert.Empty(history.Apply(
                TraceLayoutPreset.Previous(), Traces("A"), Width, Height));
        }

        // ---- Refusals --------------------------------------------------------------------------

        [Fact]
        public void ArrangingPreviousDirectlyIsRefusedBecauseItNamesNoArrangement()
        {
            ArgumentException error = Assert.Throws<ArgumentException>(
                () => TraceLayoutEngine.Arrange(
                    TraceLayoutPreset.Previous(), Traces("A"), Width, Height));

            Assert.Contains("layout history", error.Message);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-3)]
        public void ARowOrColumnCountBelowOneIsRefused(int count)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => TraceLayoutPreset.Stack(count));
            Assert.Throws<ArgumentOutOfRangeException>(() => TraceLayoutPreset.Grid(count, 2));
            Assert.Throws<ArgumentOutOfRangeException>(() => TraceLayoutPreset.Grid(2, count));
        }

        [Theory]
        [InlineData(0, 800)]
        [InlineData(1200, 0)]
        [InlineData(-1, 800)]
        public void AnAreaWithNoSizeIsRefused(int width, int height)
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => TraceLayoutEngine.Arrange(
                    TraceLayoutPreset.Single(), Traces("A"), width, height));
        }

        [Fact]
        public void MissingArgumentsAreRefused()
        {
            Assert.Throws<ArgumentNullException>(
                () => TraceLayoutEngine.Arrange(null, Traces("A"), Width, Height));
            Assert.Throws<ArgumentNullException>(
                () => TraceLayoutEngine.Arrange(TraceLayoutPreset.Single(), null, Width, Height));
            Assert.Throws<ArgumentNullException>(() => TraceLayoutEngine.AreEvenlySized(null));
            Assert.Throws<ArgumentNullException>(
                () => new TraceLayoutHistory().Apply(null, Traces("A"), Width, Height));
            Assert.Throws<ArgumentNullException>(() => new TraceLayoutHistory().RecordCustom(null));
        }

        private static IReadOnlyList<char> Traces(string letters) => letters.ToCharArray();
    }
}
