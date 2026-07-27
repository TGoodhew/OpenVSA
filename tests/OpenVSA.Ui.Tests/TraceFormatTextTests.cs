using System;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using OpenVSA.Dsp.Spectrum;
using OpenVSA.Ui;
using OpenVSA.Ui.HotSpots;
using Xunit;

namespace OpenVSA.Ui.Tests
{
    /// <summary>
    /// The display names of the trace formats, and the data-entry dialog behind a hot spot.
    /// </summary>
    public class TraceFormatTextTests
    {
        [Theory]
        [InlineData(TraceFormat.LogMagnitude, "Log Mag")]
        [InlineData(TraceFormat.LinearMagnitude, "Lin Mag")]
        [InlineData(TraceFormat.WrappedPhase, "Phase")]
        [InlineData(TraceFormat.UnwrappedPhase, "Unwrap Phase")]
        [InlineData(TraceFormat.GroupDelay, "Group Delay")]
        [InlineData(TraceFormat.IQ, "IQ")]
        public void EachFormatHasTheNameADisplayHasRoomFor(TraceFormat format, string expected)
        {
            Assert.Equal(expected, TraceFormatText.Describe(format));
        }

        [Fact]
        public void EveryFormatIsOfferedAndEveryNameParsesBack()
        {
            // A format missing from the list would be one no hot spot could select, which is the
            // sort of gap that shows up only when someone goes looking for it.
            Assert.Equal(
                Enum.GetValues(typeof(TraceFormat)).Length,
                TraceFormatText.Formats.Count);

            foreach (TraceFormat format in TraceFormatText.Formats)
            {
                TraceFormat parsed;

                Assert.True(TraceFormatText.TryParse(TraceFormatText.Describe(format), out parsed));
                Assert.Equal(format, parsed);
            }

            Assert.Equal(
                TraceFormatText.Formats.Select(TraceFormatText.Describe).ToArray(),
                TraceFormatText.Names.ToArray());
        }

        [Fact]
        public void AnUnknownNameIsRefusedRatherThanGuessedAt()
        {
            TraceFormat parsed;

            Assert.False(TraceFormatText.TryParse("Magnitude Squared", out parsed));
            Assert.False(TraceFormatText.TryParse(null, out parsed));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => TraceFormatText.Describe((TraceFormat)99));
        }

        [Fact]
        public void TheDataEntryDialogSetsTheValueFromItsField()
        {
            // The dialog is the slow path of REQ-UI-042, for a value being read off a note rather
            // than nudged - so it takes the value and its units in one go. It applies as it is
            // typed rather than on an OK, which is REQ-UI-070: a second Apply afterwards therefore
            // reports no change, because the change has already been made.
            OnStaThread(() =>
            {
                var value = NumericHotSpotValue.Frequency(1e9, 1e3);
                var dialog = new ValueEntryDialog("Center ", value);

                Assert.Equal("1.000000 GHz", dialog.EntryText);

                dialog.EntryText = "2.4 GHz";

                Assert.Equal(2.4e9, value.Value, 3);
                Assert.False(dialog.Apply());
                Assert.Equal(2.4e9, value.Value, 3);
            });
        }

        [Fact]
        public void ADialogEntryThatIsNotUnderstoodChangesNothing()
        {
            OnStaThread(() =>
            {
                var value = NumericHotSpotValue.Frequency(1e9, 1e3);
                var dialog = new ValueEntryDialog("Center ", value) { EntryText = "somewhere" };

                Assert.False(dialog.Apply());
                Assert.Equal(1e9, value.Value, 3);
            });
        }

        [Fact]
        public void TheDialogRefusesAValueOfNull()
        {
            OnStaThread(() =>
            {
                Assert.Throws<ArgumentNullException>(() => new ValueEntryDialog("x", null));
                Assert.Throws<ArgumentNullException>(() => ValueEntryDialog.Prompt(null, null));

                // A hot spot with nothing behind it has nothing to prompt for, and says so rather
                // than putting an empty dialog on screen.
                Assert.Null(ValueEntryDialog.Prompt(null, new HotSpot()));
            });
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
