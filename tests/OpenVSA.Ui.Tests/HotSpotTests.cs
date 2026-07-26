using System;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows.Input;
using OpenVSA.Ui.HotSpots;
using Xunit;

namespace OpenVSA.Ui.Tests
{
    /// <summary>
    /// <c>REQ-UI-042</c>: the hot spot, which the requirement calls the signature interaction.
    /// </summary>
    /// <remarks>
    /// The four interactions are exercised through the methods the input handlers call, because
    /// synthesising real mouse and keyboard input needs a window and a message pump that a unit
    /// test has neither of. What is tested is therefore the same code the mouse reaches, not a
    /// parallel path.
    /// </remarks>
    public class HotSpotTests
    {
        [Fact]
        public void HoveringUnderlinesTheValueAndChangesTheCursorToAHand()
        {
            // Both, because the requirement names both and they do different jobs: the underline
            // says "this is a control" across the whole display at a glance, the cursor says it for
            // the one under the pointer.
            OnStaThread(() =>
            {
                HotSpot spot = Level();

                Assert.False(IsUnderlined(spot));
                Assert.False(spot.IsHovered);

                spot.Enter();

                Assert.True(spot.IsHovered);
                Assert.True(IsUnderlined(spot));
                Assert.Same(Cursors.Hand, spot.Cursor);

                spot.Leave();

                Assert.False(spot.IsHovered);
                Assert.False(IsUnderlined(spot));
                Assert.Null(spot.Cursor);
            });
        }

        [Fact]
        public void ASingleClickArmsLiveAdjustmentWithoutOpeningADialog()
        {
            OnStaThread(() =>
            {
                HotSpot spot = Level();
                bool dialog = false;
                spot.DialogRequested += (sender, e) => dialog = true;

                spot.BeginEdit();

                Assert.True(spot.IsEditing);
                Assert.Same(Cursors.ScrollNS, spot.Cursor);
                Assert.False(dialog);
            });
        }

        [Fact]
        public void ADoubleClickAsksForTheDataEntryDialog()
        {
            OnStaThread(() =>
            {
                HotSpot spot = Level();
                int asked = 0;
                spot.DialogRequested += (sender, e) => asked++;

                spot.RequestDialog();

                Assert.Equal(1, asked);
            });
        }

        [Fact]
        public void TheWheelAndArrowKeysAdjustTheHoveredValueWithoutADialog()
        {
            // The requirement is explicit that this happens on the hovered value, so it must not
            // require a click first - that would put back the round-trip the feature removes.
            OnStaThread(() =>
            {
                HotSpot spot = Level();
                int changes = 0;
                spot.ValueChanged += (sender, e) => changes++;

                spot.Enter();

                Assert.True(spot.Adjust(1));
                Assert.False(spot.IsEditing);
                Assert.Equal(-9.0, ((NumericHotSpotValue)spot.Value).Value, 9);

                Assert.True(spot.Adjust(-3));
                Assert.Equal(-12.0, ((NumericHotSpotValue)spot.Value).Value, 9);

                Assert.Equal(2, changes);
                Assert.Equal("Ref -12.00 dBm", spot.Text);
            });
        }

        [Fact]
        public void TypingReplacesTheValueAndShowsWhatHasBeenTypedMeanwhile()
        {
            // Otherwise there is nothing on screen to say an entry is in progress, and the user is
            // typing into a control that looks unchanged.
            OnStaThread(() =>
            {
                HotSpot spot = Level();

                spot.Type('-');
                spot.Type('2');
                spot.Type('5');

                Assert.Equal("-25", spot.TypedText);
                Assert.Equal("Ref -25", spot.Text);

                Assert.True(spot.CommitTyped());
                Assert.Equal(-25.0, ((NumericHotSpotValue)spot.Value).Value, 9);
                Assert.Equal("Ref -25.00 dBm", spot.Text);
                Assert.Equal(string.Empty, spot.TypedText);
            });
        }

        [Fact]
        public void AnEntryThatIsNotUnderstoodIsDiscardedRatherThanLeftOnScreen()
        {
            // A rejected entry that stayed on the display would look like a setting that took
            // effect, which is worse than not accepting it at all.
            OnStaThread(() =>
            {
                HotSpot spot = Level();

                spot.Type('f');
                spot.Type('o');
                spot.Type('o');

                Assert.False(spot.CommitTyped());
                Assert.Equal(-10.0, ((NumericHotSpotValue)spot.Value).Value, 9);
                Assert.Equal("Ref -10.00 dBm", spot.Text);
            });
        }

        [Fact]
        public void BackspaceRemovesTheLastCharacterTyped()
        {
            OnStaThread(() =>
            {
                HotSpot spot = Level();

                spot.Type('1');
                spot.Type('2');

                Assert.True(spot.Backspace());
                Assert.Equal("1", spot.TypedText);

                Assert.True(spot.Backspace());
                Assert.False(spot.Backspace());
            });
        }

        [Fact]
        public void EndingAnEditRestoresTheDisplayedValue()
        {
            OnStaThread(() =>
            {
                HotSpot spot = Level();

                spot.BeginEdit();
                spot.Type('9');
                spot.EndEdit(commit: false);

                Assert.False(spot.IsEditing);
                Assert.Equal(-10.0, ((NumericHotSpotValue)spot.Value).Value, 9);
                Assert.Equal("Ref -10.00 dBm", spot.Text);
            });
        }

        [Fact]
        public void ARightClickOffersCopyAndPaste()
        {
            OnStaThread(() =>
            {
                HotSpot spot = Level();

                Assert.NotNull(spot.ContextMenu);
                Assert.Equal(2, spot.ContextMenu.Items.Count);

                var copy = (System.Windows.Controls.MenuItem)spot.ContextMenu.Items[0];
                var paste = (System.Windows.Controls.MenuItem)spot.ContextMenu.Items[1];

                Assert.Equal("Copy", copy.Header);
                Assert.Equal("Paste", paste.Header);
            });
        }

        [Fact]
        public void AHotSpotWithNothingBehindItDoesNothingRatherThanThrowing()
        {
            // A hot spot exists from the moment the plot is constructed, before any measurement has
            // given it a value to show.
            OnStaThread(() =>
            {
                var spot = new HotSpot { Label = "Ref " };

                spot.BeginEdit();

                Assert.False(spot.IsEditing);
                Assert.False(spot.Adjust(1));
                Assert.False(spot.Copy());
                Assert.False(spot.Paste());
                Assert.False(spot.CommitTyped());
                Assert.Equal("Ref", spot.Text);
            });
        }

        [Fact]
        public void ANumericValueRespectsItsLimits()
        {
            var value = NumericHotSpotValue.Decibels(-10.0)
                ;
            value.Minimum = -12.0;
            value.Maximum = -9.0;

            Assert.True(value.TryAdjust(-5));
            Assert.Equal(-12.0, value.Value, 9);

            // Already at the limit: nothing moves, and it says so rather than reporting a change
            // that did not happen.
            Assert.False(value.TryAdjust(-1));

            Assert.True(value.TryAdjust(10));
            Assert.Equal(-9.0, value.Value, 9);
        }

        [Fact]
        public void AProportionalStepScalesWithTheValue()
        {
            // A resolution bandwidth runs from millihertz to megahertz, where a fixed increment is
            // either uselessly small at one end or uselessly coarse at the other.
            var value = NumericHotSpotValue.Frequency(1000.0, 1.0);
            value.ProportionalStep = 0.1;

            Assert.True(value.TryAdjust(1));
            Assert.Equal(1100.0, value.Value, 6);

            Assert.True(value.TryAdjust(-1));
            Assert.Equal(1000.0, value.Value, 6);
        }

        [Theory]
        [InlineData("1.5 GHz", 1.5e9)]
        [InlineData("250M", 250e6)]
        [InlineData("12345", 12345.0)]
        public void AFrequencyValueAcceptsEngineeringNotation(string typed, double expected)
        {
            var value = NumericHotSpotValue.Frequency(1e9, 1e3);

            Assert.True(value.TrySet(typed));
            Assert.Equal(expected, value.Value, 3);
        }

        [Theory]
        [InlineData("80us", 80e-6)]
        [InlineData("1.5 ms", 1.5e-3)]
        [InlineData("2", 2.0)]
        public void ATimeValueAcceptsEngineeringNotationWithOrWithoutTheUnit(
            string typed, double expected)
        {
            var value = NumericHotSpotValue.Time(1e-3, 1e-6);

            Assert.True(value.TrySet(typed));
            Assert.Equal(expected, value.Value, 12);
        }

        [Fact]
        public void AChoiceStepsThroughItsOptionsAndWraps()
        {
            // A list of formats has no natural first or last, and a wheel that stopped dead at the
            // end would read as a stuck control rather than as a boundary.
            var value = new ChoiceHotSpotValue(new[] { "Log Mag", "Lin Mag", "Phase" });

            Assert.Equal("Log Mag", value.Text);

            Assert.True(value.TryAdjust(1));
            Assert.Equal("Lin Mag", value.Text);

            Assert.True(value.TryAdjust(2));
            Assert.Equal("Log Mag", value.Text);

            Assert.True(value.TryAdjust(-1));
            Assert.Equal("Phase", value.Text);
        }

        [Fact]
        public void AChoiceCanBeSetByName()
        {
            var value = new ChoiceHotSpotValue(new[] { "Ch 1", "Ch 2", "Ext" });

            Assert.True(value.TrySet("ext"));
            Assert.Equal("Ext", value.Text);

            Assert.False(value.TrySet("Ch 9"));
            Assert.Equal("Ext", value.Text);

            // Setting it to what it already is is not a change.
            Assert.False(value.TrySet("Ext"));
        }

        [Fact]
        public void TheArgumentsAreChecked()
        {
            Assert.Throws<ArgumentNullException>(
                () => new NumericHotSpotValue(0.0, 1.0, null, t => 0.0));
            Assert.Throws<ArgumentNullException>(
                () => new NumericHotSpotValue(0.0, 1.0, v => string.Empty, null));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => new NumericHotSpotValue(0.0, 0.0, v => string.Empty, t => 0.0));

            Assert.Throws<ArgumentNullException>(() => new ChoiceHotSpotValue(null));
            Assert.Throws<ArgumentException>(() => new ChoiceHotSpotValue(new string[0]));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => new ChoiceHotSpotValue(new[] { "one" }, 1));
        }

        /// <summary>
        /// Whether the value is underlined.
        /// </summary>
        /// <remarks>
        /// Not a null check: WPF's default for the property is an empty collection rather than
        /// null, so "no decorations" and "not set" are two different values that mean the same
        /// thing.
        /// </remarks>
        private static bool IsUnderlined(HotSpot spot) =>
            spot.TextDecorations != null && spot.TextDecorations.Count > 0;

        private static HotSpot Level()
        {
            var spot = new HotSpot { Label = "Ref " };
            spot.Value = NumericHotSpotValue.Decibels(-10.0);
            return spot;
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
