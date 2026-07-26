using System;
using System.Diagnostics;
using OpenVSA.Core;
using Xunit;
using Xunit.Abstractions;

namespace OpenVSA.Core.Tests
{
    /// <summary>
    /// <c>REQ-ACQ-010</c>: where a timestamp comes from, and what it refers to.
    /// </summary>
    public class AcquisitionClockTests
    {
        private readonly ITestOutputHelper _output;

        public AcquisitionClockTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void TheClockResolvesFinerThanTheSystemTimerTick()
        {
            // The whole reason for not using DateTime.UtcNow: its granularity is the system timer
            // tick, between about 1 ms and 15.6 ms, and a 1024-sample block at 25.6 MS/s lasts
            // 40 us. Timestamping with it would give hundreds of consecutive blocks the same time.
            _output.WriteLine(
                "resolution " + (AcquisitionClock.ResolutionSeconds * 1e9).ToString("F1") +
                " ns, high resolution: " + AcquisitionClock.IsHighResolution);

            Assert.True(AcquisitionClock.IsHighResolution);
            Assert.True(
                AcquisitionClock.ResolutionSeconds < 1e-6,
                "The clock resolves to " + AcquisitionClock.ResolutionSeconds +
                " s, which is coarser than a single block.");
        }

        [Fact]
        public void TheClockNeverGoesBackwards()
        {
            // Monotonic is the property being bought. A wall clock adjusted mid-run - a time sync,
            // a time-zone change - would otherwise make one block appear to precede the one before.
            DateTime previous = AcquisitionClock.UtcNow;

            for (int i = 0; i < 10000; i++)
            {
                DateTime now = AcquisitionClock.UtcNow;

                Assert.True(now >= previous, "The clock went backwards at iteration " + i + ".");
                previous = now;
            }
        }

        [Fact]
        public void TheClockAgreesWithTheWallClockToWithinItsOwnDrift()
        {
            // Monotonic, but still UTC: it is disciplined to the wall clock at session start, so
            // the absolute time is right and only the drift between two oscillators separates them.
            TimeSpan difference = AcquisitionClock.UtcNow - DateTime.UtcNow;

            _output.WriteLine(
                "clock differs from the wall clock by " +
                difference.TotalMilliseconds.ToString("F3") + " ms");

            Assert.True(
                Math.Abs(difference.TotalSeconds) < 1.0,
                "The acquisition clock is " + difference + " from the wall clock.");
        }

        [Fact]
        public void SuccessiveBlocksAdvanceByExactlyTheirOwnDuration()
        {
            // REQ-ACQ-010's criterion. A gap-free stream has an exact sample-count relationship
            // between one block and the next; reading the clock per block would add the
            // scheduler's jitter to every timestamp and lose that exactness for nothing.
            const int samples = 1024;
            const double rateHz = 25.6e6;

            var timeline = new BlockTimeline();
            DateTime first = timeline.Next(samples, rateHz);

            double expectedSeconds = samples / rateHz;

            // Measured from the first block throughout, not from the previous one: an error that
            // cancelled between neighbours but accumulated over a thousand blocks would pass a
            // pairwise check and is exactly what "advance by exactly SampleCount / SampleRateHz"
            // is about.
            for (int block = 1; block <= 1000; block++)
            {
                double elapsed = (timeline.Next(samples, rateHz) - first).TotalSeconds;

                Assert.Equal(block * expectedSeconds, elapsed, 9);
            }

            Assert.Equal(1001, timeline.BlockCount);
        }

        [Theory]
        [InlineData(1024, 25.6e6)]   // 40 us exactly: 400 clock ticks, no rounding anywhere
        [InlineData(1021, 15.0e6)]   // 68.0667 us: 680.667 ticks, and the rounding has to go somewhere
        [InlineData(801, 12.8e6)]
        [InlineData(4096, 51.2e6)]
        public void TheTimestampDoesNotDriftEvenWhenABlockIsNotAWholeNumberOfTicks(
            int samples, double rateHz)
        {
            // The bench found this one. A DateTime advanced by a rounded block duration each time
            // feeds the rounding back in, and it accumulates linearly: 1021 samples at 15 MS/s is
            // 680.667 ticks, rounds to 681, and gains a third of a tick per block - 3.3 us per
            // hundred blocks, milliseconds over a run.
            //
            // The tick-aligned case at the top of this list is the one the original test used, and
            // it cannot show the defect, which is why the awkward ones are here beside it.
            var timeline = new BlockTimeline(new DateTime(2026, 7, 26, 0, 0, 0, DateTimeKind.Utc));

            DateTime first = timeline.Next(samples, rateHz);
            double expected = samples / rateHz;
            double worst = 0.0;

            for (int block = 1; block <= 10000; block++)
            {
                double elapsed = (timeline.Next(samples, rateHz) - first).TotalSeconds;
                worst = Math.Max(worst, Math.Abs(elapsed - block * expected));
            }

            _output.WriteLine(
                samples + " samples at " + (rateHz / 1e6).ToString("F1") +
                " MS/s: worst departure over 10 000 blocks " + worst.ToString("G3") + " s");

            // One clock tick. Two timestamps are involved and each is rounded independently, so
            // half a tick each way is the bound - and it is a *bound*, not a budget that grows:
            // the accumulating version reached 3.3 us in a hundred blocks, thirty times this over
            // a hundredth of the run.
            const double oneTickSeconds = 100e-9;

            Assert.True(
                worst <= oneTickSeconds,
                "Over 10 000 blocks the timeline drifted by " + worst.ToString("G3") +
                " s, which is more than the " + oneTickSeconds + " s two roundings can account for.");
        }

        [Fact]
        public void ABlockOfADifferentLengthAdvancesByItsOwnLength()
        {
            var timeline = new BlockTimeline(new DateTime(2026, 7, 26, 12, 0, 0, DateTimeKind.Utc));

            DateTime first = timeline.Next(1000, 1e6);      // 1 ms
            DateTime second = timeline.Next(4000, 1e6);     // 4 ms
            DateTime third = timeline.Next(1000, 1e6);

            Assert.Equal(1.0, (second - first).TotalMilliseconds, 6);
            Assert.Equal(4.0, (third - second).TotalMilliseconds, 6);
        }

        [Fact]
        public void TheTimestampRefersToTheFirstSampleAndTheTriggerLiesAfterIt()
        {
            // The relationship REQ-ACQ-010 requires be defined explicitly. Stated as code so that
            // a caller reasoning about it cannot get the sign the wrong way round without this
            // failing.
            var firstSample = new DateTime(2026, 7, 26, 12, 0, 0, DateTimeKind.Utc);

            // Positive: the trigger is inside the record, which is the pre-trigger case.
            Assert.Equal(
                firstSample.AddMilliseconds(10.0),
                BlockTimeline.TriggerInstant(firstSample, 10e-3));

            // Negative: the trigger happened before the record started, which is a delayed one.
            Assert.Equal(
                firstSample.AddMilliseconds(-5.0),
                BlockTimeline.TriggerInstant(firstSample, -5e-3));

            Assert.Equal(firstSample, BlockTimeline.TriggerInstant(firstSample, 0.0));
        }

        [Fact]
        public void RestartingPlacesTheNextBlockByTheClockAgain()
        {
            // For a break in the stream. Carrying on across a gap would claim a continuity the
            // acquisition did not have, which is a worse error than a step in the absolute time.
            var timeline = new BlockTimeline(new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc));

            timeline.Next(1000, 1e6);
            Assert.Equal(1, timeline.BlockCount);

            timeline.Restart();
            Assert.Equal(0, timeline.BlockCount);

            DateTime afterRestart = timeline.Next(1000, 1e6);

            Assert.True(
                afterRestart.Year > 2000,
                "After a restart the timeline continued from the old epoch rather than the clock.");
        }

        [Fact]
        public void TheArgumentsAreChecked()
        {
            var timeline = new BlockTimeline();

            Assert.Throws<ArgumentOutOfRangeException>(() => timeline.Next(0, 1e6));
            Assert.Throws<ArgumentOutOfRangeException>(() => timeline.Next(-1, 1e6));
            Assert.Throws<ArgumentOutOfRangeException>(() => timeline.Next(1000, 0.0));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => timeline.Next(1000, double.PositiveInfinity));
        }
    }
}
