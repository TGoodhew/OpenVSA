using System;
using OpenVSA.Core;
using Xunit;

namespace OpenVSA.Core.Tests
{
    /// <summary>
    /// <c>REQ-DSP-022</c>: which displayed point counts exist, and what happens to one that does not.
    /// </summary>
    public class FrequencyPointsTests
    {
        [Theory]
        [InlineData(51)]
        [InlineData(101)]
        [InlineData(801)]
        [InlineData(6401)]
        [InlineData(409601)]
        public void TheAvailableCountsAreAccepted(int points)
        {
            Assert.True(FrequencyPoints.IsValid(points));
        }

        [Theory]
        [InlineData(50)]
        [InlineData(52)]
        [InlineData(500)]
        [InlineData(409602)]
        [InlineData(524288)]
        [InlineData(0)]
        [InlineData(-1)]
        public void EverythingElseIsRejected(int points)
        {
            Assert.False(FrequencyPoints.IsValid(points));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => FrequencyPoints.Validate(points, "points"));
        }

        [Fact]
        public void TheRejectionMessageNamesTheNearestAvailableCounts()
        {
            // REQ-DSP-022's criterion asks for a clear message. "500 is not available" is not one;
            // the two counts either side of it are what makes it actionable.
            string message = FrequencyPoints.Explain(500);

            Assert.Contains("401", message);
            Assert.Contains("801", message);
        }

        [Fact]
        public void TheMessageForTooManyPointsExplainsTheDatasheetFigure()
        {
            // 524 288 is the maximum FFT size, not a point count - the documented inconsistency in
            // the reference product's datasheet. A user who typed it got it from somewhere.
            string message = FrequencyPoints.Explain(524288);

            Assert.Contains("409601", message.Replace(",", string.Empty).Replace(" ", string.Empty));
            Assert.Contains("FFT size", message);
        }

        [Fact]
        public void TheLadderRunsFromTheMinimumToTheMaximumWithNoGaps()
        {
            Assert.Equal(FrequencyPoints.Minimum, FrequencyPoints.Supported[0]);
            Assert.Equal(FrequencyPoints.Maximum, FrequencyPoints.Supported[FrequencyPoints.Supported.Count - 1]);

            for (int i = 1; i < FrequencyPoints.Supported.Count; i++)
            {
                int previous = FrequencyPoints.Supported[i - 1];
                Assert.Equal((previous - 1) * 2 + 1, FrequencyPoints.Supported[i]);
            }
        }

        [Theory]
        [InlineData(801, 801)]
        [InlineData(800, 401)]
        [InlineData(1600, 801)]
        [InlineData(50, 0)]
        [InlineData(1000000, 409601)]
        public void SnapDownTakesTheLargestCountThatFits(int requested, int expected)
        {
            Assert.Equal(expected, FrequencyPoints.SnapDown(requested));
        }

        [Theory]
        [InlineData(801, 801)]
        [InlineData(1200, 1601)]
        [InlineData(900, 801)]
        [InlineData(10, 51)]
        public void NearestChoosesByRatio_BecauseTheLadderIsGeometric(int requested, int expected)
        {
            // 1200 is 1.5x 801 and 0.75x 1601, so 1601 is nearer in the only sense that matches how
            // the counts are spaced. By plain difference it would be 801, which is the wrong answer
            // on a geometric ladder.
            Assert.Equal(expected, FrequencyPoints.Nearest(requested));
        }
    }
}
