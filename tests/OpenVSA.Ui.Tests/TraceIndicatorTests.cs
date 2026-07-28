using System;
using System.Collections.Generic;
using System.Linq;
using OpenVSA.Ui.Rendering;
using Xunit;

namespace OpenVSA.Ui.Tests
{
    /// <summary>
    /// <c>REQ-UI-041</c>: the trace indicator strings, verbatim, and in priority order.
    /// </summary>
    public class TraceIndicatorTests
    {
        [Theory]
        [InlineData(TraceIndicator.NoData, "NO DATA")]
        [InlineData(TraceIndicator.DataQuestionable, "DATA?")]
        [InlineData(TraceIndicator.CalibrationQuestionable, "CAL?")]
        [InlineData(TraceIndicator.PulseNotFound, "PULSE NOT FOUND")]
        [InlineData(TraceIndicator.SyncNotFound, "SYNC NOT FOUND")]
        [InlineData(TraceIndicator.CarrierLock, "CARRIER LOCK?")]
        [InlineData(TraceIndicator.Equaliser, "EQ")]
        [InlineData(TraceIndicator.Range, "RNG")]
        [InlineData(TraceIndicator.AllPoints, "ALL POINTS")]
        [InlineData(TraceIndicator.InactiveChannel, "INACTIVE CHAN")]
        [InlineData(TraceIndicator.MeasurementOffset, "MEAS OFFSET?")]
        [InlineData(TraceIndicator.PulseTooShort, "PULSE TOO SHORT")]
        [InlineData(TraceIndicator.IqCompensated, "IQ COMP")]
        public void EachStringRendersExactlyAsTheRequirementQuotesIt(
            TraceIndicator indicator, string expected)
        {
            // Exact literals, question marks included. "CAL?" means the calibration is doubtful;
            // "CAL" would read as a statement that it is fine, and a tidied-up wording would cost
            // the familiarity these terse strings exist for.
            Assert.Equal(expected, TraceIndicators.TextOf(indicator));
        }

        [Theory]
        [InlineData(1, "OV1")]
        [InlineData(2, "OV2")]
        [InlineData(4, "OV4")]
        public void AnOverloadCarriesTheChannelItAppliesTo(int channel, string expected)
        {
            Assert.Equal(expected, TraceIndicators.TextOf(TraceIndicator.Overload, channel));
        }

        [Fact]
        public void EveryIndicatorTheRequirementListsHasAString()
        {
            // A new enumerator without a string would otherwise show up only when the condition
            // occurred, which for several of these is rare.
            foreach (TraceIndicator indicator in Enum.GetValues(typeof(TraceIndicator)))
            {
                Assert.False(string.IsNullOrWhiteSpace(TraceIndicators.TextOf(indicator)));
            }

            Assert.Throws<ArgumentOutOfRangeException>(() => TraceIndicators.TextOf((TraceIndicator)999));
        }

        [Fact]
        public void SeveralConditionsAtOnceAreShownInThePriorityOrder()
        {
            // The requirement's criterion, in its own words: provoke overload and carrier-lock
            // failure together. Overload comes third in the list and carrier lock seventh, so the
            // order is fixed however they were reported.
            var indicators = new TraceIndicators();

            indicators.Set(TraceIndicator.CarrierLock);
            indicators.Set(TraceIndicator.Overload, 2);

            Assert.Equal("OV2" + Environment.NewLine + "CARRIER LOCK?", indicators.Text);
        }

        [Fact]
        public void TheOrderIsThePriorityListRatherThanTheOrderConditionsArrived()
        {
            var forwards = new TraceIndicators();
            var backwards = new TraceIndicators();

            TraceIndicator[] some =
            {
                TraceIndicator.IqCompensated,
                TraceIndicator.NoData,
                TraceIndicator.Range,
                TraceIndicator.DataQuestionable,
            };

            foreach (TraceIndicator indicator in some)
            {
                forwards.Set(indicator);
            }

            foreach (TraceIndicator indicator in some.Reverse())
            {
                backwards.Set(indicator);
            }

            Assert.Equal(forwards.Text, backwards.Text);

            Assert.Equal(
                new[] { "NO DATA", "DATA?", "RNG", "IQ COMP" },
                forwards.Active.Select(a => a.Text).ToArray());
        }

        [Fact]
        public void TwoChannelsCanBeOverloadedAtOnceAndBothAreShown()
        {
            // Two inputs can overload together and the user needs to know which, so these are
            // distinct conditions rather than one condition with a changing suffix.
            var indicators = new TraceIndicators();

            indicators.Set(TraceIndicator.Overload, 2);
            indicators.Set(TraceIndicator.Overload, 1);

            Assert.Equal("OV1" + Environment.NewLine + "OV2", indicators.Text);

            indicators.Clear(TraceIndicator.Overload, 1);

            Assert.Equal("OV2", indicators.Text);
            Assert.True(indicators.IsActive(TraceIndicator.Overload, 2));
            Assert.False(indicators.IsActive(TraceIndicator.Overload, 1));
        }

        [Fact]
        public void SettingTheSameConditionTwiceShowsItOnce()
        {
            var indicators = new TraceIndicators();

            indicators.Set(TraceIndicator.Range);
            indicators.Set(TraceIndicator.Range);

            Assert.Equal("RNG", indicators.Text);
            Assert.Single(indicators.Active);
        }

        [Fact]
        public void AnEmptySetShowsNothing()
        {
            var indicators = new TraceIndicators();

            Assert.True(indicators.IsEmpty);
            Assert.Equal(string.Empty, indicators.Text);

            indicators.Set(TraceIndicator.Equaliser);
            indicators.SetActive(TraceIndicator.AllPoints, true);

            Assert.False(indicators.IsEmpty);

            indicators.SetActive(TraceIndicator.AllPoints, false);
            Assert.Equal("EQ", indicators.Text);

            indicators.ClearAll();
            Assert.True(indicators.IsEmpty);
        }

        [Fact]
        public void TheDeclarationOrderIsThePriorityOrderTheRequirementLists()
        {
            // The priority is carried by the enum rather than by a table beside it, so this is what
            // pins the enum's order to the specification's list.
            var expected = new List<TraceIndicator>
            {
                TraceIndicator.NoData,
                TraceIndicator.DataQuestionable,
                TraceIndicator.Overload,
                TraceIndicator.CalibrationQuestionable,
                TraceIndicator.PulseNotFound,
                TraceIndicator.SyncNotFound,
                TraceIndicator.CarrierLock,
                TraceIndicator.Equaliser,
                TraceIndicator.Range,
                TraceIndicator.AllPoints,
                TraceIndicator.InactiveChannel,
                TraceIndicator.MeasurementOffset,
                TraceIndicator.PulseTooShort,
                TraceIndicator.IqCompensated,

                // REQ-UI-007's own list names two the reference product's does not, and they come
                // last because they are additions rather than reorderings: an unlocked reference
                // and dropped frames both invalidate the number on screen.
                TraceIndicator.ReferenceUnlocked,
                TraceIndicator.DroppedFrames,
            };

            for (int i = 0; i < expected.Count; i++)
            {
                Assert.Equal(i, (int)expected[i]);
            }

            Assert.Equal(expected.Count, Enum.GetValues(typeof(TraceIndicator)).Length);
        }
    }
}
