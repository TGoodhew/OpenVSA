using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace OpenVSA.SoakGate.Tests
{
    /// <summary>
    /// The log format and the line fit, which everything else is built on.
    /// </summary>
    public class SoakLogAndTrendTests
    {
        [Fact]
        public void ASampleSurvivesTheRoundTrip()
        {
            var written = new SoakSample(
                3661.5, 123456789L, 98765432L, 987654321L, 712, 344, 209,
                123456L, 789L, 26, 50331648L, 4, 17);

            SoakSample read = SoakLog.Read(SoakLog.Write(new[] { written }, "a note")).Single();

            Assert.Equal(written.ElapsedSeconds, read.ElapsedSeconds, 3);
            Assert.Equal(written.ManagedBytes, read.ManagedBytes);
            Assert.Equal(written.CollectedManagedBytes, read.CollectedManagedBytes);
            Assert.Equal(written.PrivateBytes, read.PrivateBytes);
            Assert.Equal(written.Handles, read.Handles);
            Assert.Equal(written.GdiObjects, read.GdiObjects);
            Assert.Equal(written.UserObjects, read.UserObjects);
            Assert.Equal(written.FramesDrawn, read.FramesDrawn);
            Assert.Equal(written.FramesDropped, read.FramesDropped);
            Assert.Equal(written.PooledBuffers, read.PooledBuffers);
            Assert.Equal(written.PooledBytes, read.PooledBytes);
            Assert.Equal(written.TracesOpen, read.TracesOpen);
            Assert.Equal(written.Cycles, read.Cycles);
        }

        [Fact]
        public void ALogTruncatedMidLineStillReadsEverythingBeforeIt()
        {
            // A run killed at hour seven leaves a half-written line, and refusing the file would
            // throw away the night over its last three seconds. The host appends and flushes as it
            // goes precisely so that this case is recoverable.
            string whole = SoakLog.Write(new[]
            {
                new SoakSample(0.0, 1L, 2L, 3L, 4, 5, 6, 7L, 8L, 9, 10L, 11, 12),
                new SoakSample(60.0, 1L, 2L, 3L, 4, 5, 6, 7L, 8L, 9, 10L, 11, 12),
            });

            string truncated = whole.Substring(0, whole.Length - 12);

            IList<SoakSample> read = SoakLog.Read(truncated);

            Assert.Single(read);
            Assert.Equal(0.0, read[0].ElapsedSeconds);
        }

        [Fact]
        public void AMalformedLineInTheMiddleIsAnError()
        {
            // The forgiveness above is for the LAST line only. A short line anywhere else means the
            // file is not what it claims to be, and reading past it would judge a log with holes.
            string text =
                SoakLog.Preamble("test") +
                "0\t1\t2\t3\t4\t5\t6\t7\t8\t9\t10\t11\t12\n" +
                "60\t1\t2\n" +
                "120\t1\t2\t3\t4\t5\t6\t7\t8\t9\t10\t11\t12\n";

            Assert.Throws<FormatException>(() => SoakLog.Read(text));
        }

        [Fact]
        public void AnEmptyLogReadsAsNoSamplesRatherThanThrowing()
        {
            Assert.Empty(SoakLog.Read(null));
            Assert.Empty(SoakLog.Read(string.Empty));
            Assert.Empty(SoakLog.Read(SoakLog.Preamble("nothing yet")));
        }

        [Fact]
        public void ASampleCannotPredateTheRun()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new SoakSample(-1.0, 0L, 0L, 0L, 0, 0, 0, 0L, 0L, 0, 0L, 0, 0));
        }

        // ---- The line fit ------------------------------------------------------------------------

        [Fact]
        public void AStraightLineIsRecoveredExactly()
        {
            var xs = new List<double>();
            var ys = new List<double>();

            for (int i = 0; i < 20; i++)
            {
                xs.Add(i * 0.5);
                ys.Add(100.0 + (7.0 * i * 0.5));
            }

            Trend trend = Trend.Fit(xs, ys);

            Assert.Equal(7.0, trend.Slope, 9);
            Assert.Equal(100.0, trend.Intercept, 9);
            Assert.True(trend.IsDetermined);

            // No residual, so no uncertainty -- and therefore a rise it is certain about.
            Assert.Equal(0.0, trend.StandardError, 9);
            Assert.True(trend.RisesSignificantly());
        }

        [Fact]
        public void NoiseWithNoTrendIsNotARise()
        {
            var xs = new List<double>();
            var ys = new List<double>();

            // Deterministic, alternating, and centred: a real series with no slope in it. Using a
            // random generator here would make the test's own verdict a matter of luck.
            for (int i = 0; i < 60; i++)
            {
                xs.Add(i);
                ys.Add(500.0 + (i % 2 == 0 ? 90.0 : -90.0));
            }

            Trend trend = Trend.Fit(xs, ys);

            Assert.True(trend.IsDetermined);
            Assert.False(trend.RisesSignificantly());
        }

        [Fact]
        public void ARiseBuriedInLargerNoiseIsStillFound()
        {
            var xs = new List<double>();
            var ys = new List<double>();

            // Noise of ±90 either side, and a rise of 4 per step: the swing dwarfs the step, but
            // over 60 points the slope is many times its own standard error. This is the case the
            // gate depends on -- a leak smaller than the sawtooth it hides in.
            //
            // The pattern has period four rather than two, so that it carries no correlation with x
            // of its own: an alternating ±90 leans on the fit and moves the recovered slope, which
            // would make this test's own arithmetic the thing under examination.
            for (int i = 0; i < 60; i++)
            {
                xs.Add(i);
                ys.Add(500.0 + (4.0 * i) + (i % 4 == 0 || i % 4 == 3 ? 90.0 : -90.0));
            }

            Trend trend = Trend.Fit(xs, ys);

            Assert.Equal(4.0, trend.Slope, 1);
            Assert.True(
                trend.RisesSignificantly(),
                "slope " + trend.Slope + " ± " + trend.StandardError);
        }

        [Fact]
        public void TwoPointsAreNotATrend()
        {
            // Two points fit a line exactly, with no residual and so no uncertainty. Reporting a
            // perfectly determined slope from two readings is how a run that measured nothing comes
            // to look conclusive.
            Trend trend = Trend.Fit(new[] { 0.0, 1.0 }, new[] { 100.0, 900.0 });

            Assert.False(trend.IsDetermined);
            Assert.False(trend.RisesSignificantly());
            Assert.Equal(100.0, trend.First);
            Assert.Equal(900.0, trend.Last);
        }

        [Fact]
        public void EverySampleAtTheSameInstantIsNotATrendEither()
        {
            Trend trend = Trend.Fit(
                new[] { 4.0, 4.0, 4.0, 4.0 }, new[] { 1.0, 2.0, 3.0, 900.0 });

            Assert.False(trend.IsDetermined);
        }

        [Fact]
        public void AFallingSeriesDoesNotCountAsARise()
        {
            var xs = new List<double>();
            var ys = new List<double>();

            for (int i = 0; i < 30; i++)
            {
                xs.Add(i);
                ys.Add(1000.0 - (10.0 * i));
            }

            Trend trend = Trend.Fit(xs, ys);

            Assert.Equal(-10.0, trend.Slope, 9);
            Assert.False(trend.RisesSignificantly());
        }

        [Fact]
        public void MismatchedSeriesAreRefused()
        {
            Assert.Throws<ArgumentException>(() => Trend.Fit(new[] { 1.0, 2.0 }, new[] { 1.0 }));
            Assert.Throws<ArgumentNullException>(() => Trend.Fit(null, new[] { 1.0 }));
            Assert.Throws<ArgumentNullException>(() => Trend.Fit(new[] { 1.0 }, null));
        }
    }
}
