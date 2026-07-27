using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using OpenVSA.Ui.Layout;
using Xunit;
using Xunit.Abstractions;

namespace OpenVSA.Ui.Tests
{
    /// <summary>
    /// <c>REQ-UI-004</c>'s tab group conventions, asserted on rendered geometry.
    /// </summary>
    /// <remarks>
    /// The requirement is explicit that the close button's position is "the detail a developer will
    /// 'fix' into the conventional right-hand position", and that the criterion is therefore about
    /// bounds rather than about a style name. So the strip is measured and arranged on an STA
    /// thread and the answers come from <c>TransformToAncestor</c> — a <c>Dock</c> property can be
    /// Left while the thing on screen is not.
    /// </remarks>
    public class TraceTabStripTests
    {
        private readonly ITestOutputHelper _output;

        public TraceTabStripTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void TheCloseButtonSitsToTheLeftOfEveryTab()
        {
            OnStaThread(() =>
            {
                TraceTabStrip strip = Arranged("ABCD", 'A');

                Rect close = strip.CloseButtonBounds;

                _output.WriteLine("close " + close + "; A " + strip.TabBounds('A'));

                Assert.False(close.IsEmpty);

                foreach (char trace in strip.Traces)
                {
                    Rect tab = strip.TabBounds(trace);

                    Assert.False(tab.IsEmpty);
                    Assert.True(
                        close.Right <= tab.Left,
                        "the close button's right edge (" + close.Right +
                        ") is not left of tab " + trace + "'s left edge (" + tab.Left + ").");
                }
            });
        }

        [Fact]
        public void TheCloseButtonIsNotAtTheRightEitherByAccident()
        {
            // The failure this guards against is a button that ends up on the right. Asserting the
            // left-of-every-tab relation alone would pass for a strip with no tabs at all.
            OnStaThread(() =>
            {
                TraceTabStrip strip = Arranged("ABCD", 'A');

                Rect close = strip.CloseButtonBounds;
                double rightmost = strip.Traces.Max(t => strip.TabBounds(t).Right);

                Assert.True(close.Left < rightmost);
                Assert.True(close.Left < 40.0, "the close button starts at " + close.Left + ".");
            });
        }

        [Fact]
        public void ThereIsNoPerTabCloseButton()
        {
            // The requirement's negative criterion, answered by walking the visual tree of the
            // tabs rather than asserted by inspection.
            OnStaThread(() =>
            {
                TraceTabStrip strip = Arranged("ABCDE", 'C');

                Assert.Empty(strip.ButtonsInsideTabs());
            });
        }

        [Fact]
        public void TheActiveTabIsTheOnlyBoldOne()
        {
            OnStaThread(() =>
            {
                TraceTabStrip strip = Arranged("ABCD", 'B');

                Assert.True(strip.IsBold('B'));
                Assert.False(strip.IsBold('A'));
                Assert.False(strip.IsBold('C'));
                Assert.False(strip.IsBold('D'));
            });
        }

        [Fact]
        public void ChangingTheActiveTraceMovesTheBold()
        {
            OnStaThread(() =>
            {
                TraceTabStrip strip = Arranged("ABCD", 'A');

                Assert.True(strip.IsBold('A'));

                strip.ActiveTrace = 'D';

                Assert.True(strip.IsBold('D'));
                Assert.Equal(
                    1, strip.Traces.Count(t => strip.IsBold(t)));
            });
        }

        [Fact]
        public void SelectingATabMakesItActiveAndSaysSo()
        {
            OnStaThread(() =>
            {
                TraceTabStrip strip = Arranged("ABC", 'A');

                char reported = '\0';
                strip.TraceSelected += (sender, trace) => reported = trace;

                strip.Select('C');

                Assert.Equal('C', reported);
                Assert.Equal('C', strip.ActiveTrace);
                Assert.True(strip.IsBold('C'));
            });
        }

        [Fact]
        public void TheCloseButtonClosesTheActiveTrace()
        {
            // One close button, and it acts on the active trace. That is the whole model, and it
            // is why a per-tab button would make the left-hand one ambiguous.
            OnStaThread(() =>
            {
                TraceTabStrip strip = Arranged("ABC", 'B');

                char asked = '\0';
                strip.CloseRequested += (sender, trace) => asked = trace;

                ((System.Windows.Controls.Button)strip.CloseButton)
                    .RaiseEvent(new RoutedEventArgs(
                        System.Windows.Controls.Primitives.ButtonBase.ClickEvent));

                Assert.Equal('B', asked);
            });
        }

        [Fact]
        public void AGroupThatDoesNotHoldTheActiveTraceBoldsNothing()
        {
            // The active trace is one trace, not one per group. Every group has a tab on top, so
            // bolding each group's own makes four tiled traces look active at once - which is what
            // it did, and it reads as though the application had lost track of the selection.
            OnStaThread(() =>
            {
                TraceTabStrip strip = Arranged("CD", 'C');

                Assert.True(strip.IsBold('C'));

                strip.HighlightsActive = false;

                Assert.False(strip.IsBold('C'));
                Assert.False(strip.IsBold('D'));

                strip.HighlightsActive = true;

                Assert.True(strip.IsBold('C'));
            });
        }

        [Fact]
        public void ATraceNotInTheGroupIsRefused()
        {
            OnStaThread(() =>
            {
                TraceTabStrip strip = Arranged("AB", 'A');

                Assert.Throws<ArgumentException>(() => strip.ActiveTrace = 'Z');
                Assert.Throws<ArgumentException>(() => strip.TabBounds('Z'));
                Assert.Throws<ArgumentException>(() => strip.IsBold('Z'));
            });
        }

        [Fact]
        public void AnActiveTraceOutsideTheGroupFallsBackToTheFirst()
        {
            OnStaThread(() =>
            {
                TraceTabStrip strip = Arranged("ABC", 'Z');

                Assert.Equal('A', strip.ActiveTrace);
                Assert.True(strip.IsBold('A'));
            });
        }

        [Fact]
        public void MissingTracesAreRefused()
        {
            OnStaThread(() => Assert.Throws<ArgumentNullException>(
                () => new TraceTabStrip().SetTraces(null, 'A')));
        }

        // ---- Boundary dragging and Resize Traces -----------------------------------------------

        [Fact]
        public void DraggingABoundaryResizesTheAdjacentWindowsOnly()
        {
            // The criterion. Redistributing the change across every row moves windows the user was
            // not touching, and is how a careful arrangement comes apart under one drag.
            IReadOnlyList<TraceSlot> before = TraceLayoutEngine.Arrange(
                TraceLayoutPreset.Stack(4), "ABCD".ToCharArray(), 1000, 800);

            IReadOnlyList<TraceSlot> after = TraceLayoutEngine.DragBoundary(before, 1, 60);

            // The two either side of the boundary moved...
            Assert.Equal(before[1].Height + 60, after[1].Height);
            Assert.Equal(before[2].Height - 60, after[2].Height);
            Assert.Equal(before[1].Bottom + 60, after[2].Top);

            // ...and nothing else did.
            Assert.Equal(before[0].Top, after[0].Top);
            Assert.Equal(before[0].Height, after[0].Height);
            Assert.Equal(before[3].Top, after[3].Top);
            Assert.Equal(before[3].Height, after[3].Height);

            // The stack still tiles exactly.
            Assert.Equal(800, after[after.Count - 1].Bottom);
        }

        [Fact]
        public void ADragIsClampedSoNeitherWindowVanishes()
        {
            // Silently: a splitter that stops moving explains itself, where a refusal mid-drag
            // would not.
            IReadOnlyList<TraceSlot> before = TraceLayoutEngine.Arrange(
                TraceLayoutPreset.Stack(3), "ABC".ToCharArray(), 1000, 600);

            IReadOnlyList<TraceSlot> pushedDown = TraceLayoutEngine.DragBoundary(before, 0, 10000);

            Assert.Equal(TraceLayoutEngine.MinimumSlotSize, pushedDown[1].Height);
            Assert.Equal(600, pushedDown[pushedDown.Count - 1].Bottom);

            IReadOnlyList<TraceSlot> pushedUp = TraceLayoutEngine.DragBoundary(before, 0, -10000);

            Assert.Equal(TraceLayoutEngine.MinimumSlotSize, pushedUp[0].Height);
            Assert.Equal(0, pushedUp[0].Top);
        }

        [Fact]
        public void ADragAcrossSlotsThatShareNoBoundaryIsRefused()
        {
            IReadOnlyList<TraceSlot> grid = TraceLayoutEngine.Arrange(
                TraceLayoutPreset.Grid(2, 2), "ABCD".ToCharArray(), 1000, 800);

            // Slots 0 and 1 are side by side, not stacked.
            ArgumentException error = Assert.Throws<ArgumentException>(
                () => TraceLayoutEngine.DragBoundary(grid, 0, 20));

            Assert.Contains("nothing between them to drag", error.Message);
        }

        [Fact]
        public void ResizeTracesLeavesEveryWindowWithinOnePixelOfEqual()
        {
            // The criterion, after a drag has made them uneven.
            var history = new TraceLayoutHistory();
            IReadOnlyList<char> traces = "ABCDE".ToCharArray();

            history.Apply(TraceLayoutPreset.Stack(5), traces, 1000, 777);

            IReadOnlyList<TraceSlot> dragged =
                TraceLayoutEngine.DragBoundary(history.CurrentSlots, 2, 90);

            Assert.False(TraceLayoutEngine.AreEvenlySized(dragged));

            history.RecordCustom(dragged);

            IReadOnlyList<TraceSlot> evened = history.ResizeTraces(traces, 1000, 777);

            Assert.True(TraceLayoutEngine.AreEvenlySized(evened));
            Assert.Equal(5, evened.Count(s => s.Traces.Count > 0));
            Assert.Equal(777, evened.Max(s => s.Bottom));
        }

        [Fact]
        public void ResizeTracesKeepsThePresetItIsIn()
        {
            var history = new TraceLayoutHistory();
            IReadOnlyList<char> traces = "ABCD".ToCharArray();

            history.Apply(TraceLayoutPreset.Grid(2, 2), traces, 1000, 800);

            IReadOnlyList<TraceSlot> evened = history.ResizeTraces(traces, 1000, 800);

            Assert.Equal(4, evened.Count);
            Assert.True(TraceLayoutEngine.AreEvenlySized(evened));
            Assert.Equal("Grid 2×2", history.Current.Name);
        }

        [Fact]
        public void ResizeTracesAfterAHandArrangementKeepsTheWindowCount()
        {
            // Custom names no shape, so the cell count the user built is what survives - evenly
            // sized, which is what the command is for.
            var history = new TraceLayoutHistory();
            IReadOnlyList<char> traces = "ABC".ToCharArray();

            history.RecordCustom(
                TraceLayoutEngine.Arrange(TraceLayoutPreset.Stack(3), traces, 900, 600));

            IReadOnlyList<TraceSlot> evened = history.ResizeTraces(traces, 900, 600);

            Assert.Equal(3, evened.Count(s => s.Traces.Count > 0));
            Assert.True(TraceLayoutEngine.AreEvenlySized(evened));
        }

        [Fact]
        public void MissingArgumentsAreRefusedByTheDragAndResize()
        {
            Assert.Throws<ArgumentNullException>(
                () => TraceLayoutEngine.DragBoundary(null, 0, 10));
            Assert.Throws<ArgumentNullException>(
                () => new TraceLayoutHistory().ResizeTraces(null, 100, 100));

            IReadOnlyList<TraceSlot> slots = TraceLayoutEngine.Arrange(
                TraceLayoutPreset.Stack(2), "AB".ToCharArray(), 100, 200);

            Assert.Throws<ArgumentOutOfRangeException>(
                () => TraceLayoutEngine.DragBoundary(slots, -1, 10));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => TraceLayoutEngine.DragBoundary(slots, 1, 10));
        }

        private static TraceTabStrip Arranged(string traces, char active)
        {
            var strip = new TraceTabStrip();

            strip.SetTraces(traces.ToCharArray(), active);

            // A real measure and arrange, so the bounds below are the rendered ones.
            strip.Measure(new Size(900.0, 40.0));
            strip.Arrange(new Rect(0.0, 0.0, 900.0, 40.0));
            strip.UpdateLayout();

            return strip;
        }

        private static void OnStaThread(Action action)
        {
            ExceptionDispatchInfo failure = null;

            var thread = new Thread(() =>
            {
                try
                {
                    action();
                }
                catch (Exception e)
                {
                    failure = ExceptionDispatchInfo.Capture(e);
                }
            });

            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();

            if (failure != null)
            {
                failure.Throw();
            }
        }
    }
}
