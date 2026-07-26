using System;
using System.Linq;
using OpenVSA.Dsp.Spectrum;
using OpenVSA.Dsp.Windowing;
using OpenVSA.Measurement.Limits;
using Xunit;

namespace OpenVSA.Measurement.Tests
{
    /// <summary>
    /// <c>REQ-LIM-001</c> and <c>REQ-LIM-002</c>: the limit hierarchy and its verdicts.
    /// </summary>
    public class LimitTestTests
    {
        private const double StartHz = 1.0e9;
        private const double BinWidthHz = 1.0e3;
        private const int Points = 101;

        [Fact]
        public void AnUpperLineFailsATraceAboveItAndPassesOneBelow()
        {
            // The requirement calls an inverted comparison "the defect most easily shipped here",
            // so both directions are asserted against the same line.
            LimitLine upper = new LimitLine("upper", LimitSide.Upper)
                .Add(StartHz, -30.0)
                .Add(StartHz + 100 * BinWidthHz, -30.0);

            Assert.False(upper.Evaluate(Flat(-20.0)).Passed);
            Assert.True(upper.Evaluate(Flat(-40.0)).Passed);
        }

        [Fact]
        public void ALowerLineFailsATraceBelowItAndPassesOneAbove()
        {
            LimitLine lower = new LimitLine("lower", LimitSide.Lower)
                .Add(StartHz, -30.0)
                .Add(StartHz + 100 * BinWidthHz, -30.0);

            Assert.False(lower.Evaluate(Flat(-40.0)).Passed);
            Assert.True(lower.Evaluate(Flat(-20.0)).Passed);
        }

        [Fact]
        public void MarginIsAppliedOnThePassSideSoItTightensTheTest()
        {
            // REQ-LIM-001: "Margin is applied on the pass side of the limit, never the fail side."
            // A margin is a safety factor, so it must make a passing trace harder to pass - not an
            // allowance that lets a failing one through.
            LimitLine upper = new LimitLine("upper", LimitSide.Upper)
                .Add(StartHz, -30.0)
                .Add(StartHz + 100 * BinWidthHz, -30.0);

            SpectrumFrame trace = Flat(-33.0);

            Assert.True(upper.Evaluate(trace).Passed);

            upper.MarginDb = 5.0;
            Assert.False(upper.Evaluate(trace).Passed);

            // And the same on a lower line, where the margin moves the other way.
            LimitLine lower = new LimitLine("lower", LimitSide.Lower)
                .Add(StartHz, -30.0)
                .Add(StartHz + 100 * BinWidthHz, -30.0);

            SpectrumFrame above = Flat(-27.0);
            Assert.True(lower.Evaluate(above).Passed);

            lower.MarginDb = 5.0;
            Assert.False(lower.Evaluate(above).Passed);
        }

        [Fact]
        public void MarginIsSignedTheSameWayForBothSides()
        {
            // "More margin is better" has to hold without the reader remembering which side the
            // line is, or a status bar showing a margin is unreadable.
            LimitLine upper = new LimitLine("upper", LimitSide.Upper)
                .Add(StartHz, -30.0).Add(StartHz + 100 * BinWidthHz, -30.0);
            LimitLine lower = new LimitLine("lower", LimitSide.Lower)
                .Add(StartHz, -30.0).Add(StartHz + 100 * BinWidthHz, -30.0);

            Assert.Equal(10.0, upper.Evaluate(Flat(-40.0)).WorstMarginDb, 3);
            Assert.Equal(10.0, lower.Evaluate(Flat(-20.0)).WorstMarginDb, 3);

            Assert.Equal(-10.0, upper.Evaluate(Flat(-20.0)).WorstMarginDb, 3);
            Assert.Equal(-10.0, lower.Evaluate(Flat(-40.0)).WorstMarginDb, 3);
        }

        [Fact]
        public void AGapBetweenSegmentsIsNotTested()
        {
            // REQ-LIM-001's criterion: a point with connect-to-previous clear starts a new
            // segment, and a trace passing through the gap is not tested there - verified by a
            // trace that would fail were the segments joined.
            var line = new LimitLine("two segments", LimitSide.Upper);
            line.Add(StartHz, -30.0);
            line.Add(StartHz + 20 * BinWidthHz, -30.0);
            line.Add(StartHz + 80 * BinWidthHz, -30.0, connectToPrevious: false);
            line.Add(StartHz + 100 * BinWidthHz, -30.0);

            // A trace that breaches only inside the gap.
            SpectrumFrame trace = Flat(-40.0, spikeIndex: 50, spikeDbm: -10.0);

            LimitLineResult result = line.Evaluate(trace);

            Assert.True(result.Passed, "The breach lay in an untested gap and must not fail.");

            // Joined, the same trace fails - which is what makes the test above meaningful.
            var joined = new LimitLine("one segment", LimitSide.Upper);
            joined.Add(StartHz, -30.0);
            joined.Add(StartHz + 100 * BinWidthHz, -30.0);

            Assert.False(joined.Evaluate(trace).Passed);
        }

        [Fact]
        public void ASlopedSegmentIsInterpolatedBetweenItsPoints()
        {
            var line = new LimitLine("ramp", LimitSide.Upper);
            line.Add(StartHz, -50.0);
            line.Add(StartHz + 100 * BinWidthHz, -30.0);

            Assert.Equal(-50.0, line.LimitAt(StartHz), 6);
            Assert.Equal(-40.0, line.LimitAt(StartHz + 50 * BinWidthHz), 6);
            Assert.Equal(-30.0, line.LimitAt(StartHz + 100 * BinWidthHz), 6);

            // Outside every segment there is no limit at all, not an extrapolated one.
            Assert.True(double.IsNaN(line.LimitAt(StartHz - BinWidthHz)));
        }

        [Fact]
        public void AFailureReportsTheWorstMarginAndWhereItIs()
        {
            // REQ-LIM-002's criterion: the correct worst-case margin and its X location.
            var line = new LimitLine("upper", LimitSide.Upper);
            line.Add(StartHz, -30.0);
            line.Add(StartHz + 100 * BinWidthHz, -30.0);

            SpectrumFrame trace = Flat(-40.0, spikeIndex: 37, spikeDbm: -18.0);
            LimitLineResult result = line.Evaluate(trace);

            Assert.False(result.Passed);
            Assert.Equal(-12.0, result.WorstMarginDb, 2);
            Assert.Equal(StartHz + 37 * BinWidthHz, result.WorstXHz, 3);
        }

        [Fact]
        public void ATraceExactlyOnTheLimitPasses()
        {
            // The documented boundary convention: the limit is inclusive, so a margin of exactly
            // zero passes. Stated and tested rather than left to whichever operator was typed.
            var line = new LimitLine("upper", LimitSide.Upper);
            line.Add(StartHz, -30.0);
            line.Add(StartHz + 100 * BinWidthHz, -30.0);

            LimitLineResult result = line.Evaluate(Flat(-30.0));

            Assert.True(result.Passed);
            Assert.Equal(0.0, result.WorstMarginDb, 3);
        }

        [Fact]
        public void ATestPassesOnlyIfEveryLinePasses()
        {
            var test = new LimitTest("mask")
                .Add(new LimitLine("ceiling", LimitSide.Upper)
                    .Add(StartHz, -20.0).Add(StartHz + 100 * BinWidthHz, -20.0))
                .Add(new LimitLine("floor", LimitSide.Lower)
                    .Add(StartHz, -50.0).Add(StartHz + 100 * BinWidthHz, -50.0));

            Assert.True(test.Evaluate(Flat(-35.0)).Passed);

            // Above the ceiling: one line fails, so the test fails.
            LimitTestResult tooHigh = test.Evaluate(Flat(-10.0));
            Assert.False(tooHigh.Passed);
            Assert.Equal("ceiling", tooHigh.Worst.Line.Name);

            // Below the floor: the other line fails, and the report names that one instead.
            LimitTestResult tooLow = test.Evaluate(Flat(-60.0));
            Assert.False(tooLow.Passed);
            Assert.Equal("floor", tooLow.Worst.Line.Name);
        }

        [Fact]
        public void EachLevelOfTheHierarchyIsNamed()
        {
            // REQ-LIM-001: test, line and point, each user-named.
            var test = new LimitTest("emissions");
            var line = new LimitLine("in band", LimitSide.Upper);
            line.Add(new LimitPoint(StartHz, -30.0));
            test.Add(line);

            Assert.Equal("emissions", test.Name);
            Assert.Equal("in band", test.Lines.Single().Name);
            Assert.Single(test.Lines.Single().Points);
        }

        [Fact]
        public void ALineCoveringNothingReportsThatNothingWasTested()
        {
            var line = new LimitLine("elsewhere", LimitSide.Upper);
            line.Add(2.0e9, -30.0);
            line.Add(2.1e9, -30.0);

            LimitLineResult result = line.Evaluate(Flat(-10.0));

            Assert.Equal(0, result.TestedPoints);
            Assert.True(result.Passed);
            Assert.True(double.IsNaN(result.WorstMarginDb));
        }

        [Fact]
        public void TheArgumentsAreChecked()
        {
            Assert.Throws<ArgumentException>(() => new LimitTest(string.Empty));
            Assert.Throws<ArgumentException>(() => new LimitLine(null, LimitSide.Upper));
            Assert.Throws<ArgumentNullException>(() => new LimitTest("t").Add((LimitLine)null));
            Assert.Throws<ArgumentNullException>(
                () => new LimitLine("l", LimitSide.Upper).Evaluate(null));
        }

        /// <summary>A flat trace at a level, optionally with one bin raised.</summary>
        private static SpectrumFrame Flat(double levelDbm, int spikeIndex = -1, double spikeDbm = 0.0)
        {
            var levels = new float[Points];

            for (int i = 0; i < Points; i++)
            {
                levels[i] = (float)levelDbm;
            }

            if (spikeIndex >= 0)
            {
                levels[spikeIndex] = (float)spikeDbm;
            }

            return SpectrumFrame.FromLevels(levels, StartHz, BinWidthHz, WindowType.Uniform, 1.0);
        }
    }
}
