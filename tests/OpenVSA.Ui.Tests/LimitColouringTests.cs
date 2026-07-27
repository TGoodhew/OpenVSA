using System;
using System.Collections.Generic;
using OpenVSA.Dsp.Spectrum;
using OpenVSA.Dsp.Windowing;
using OpenVSA.Measurement.Limits;
using OpenVSA.Ui.Rendering;
using Xunit;

namespace OpenVSA.Ui.Tests
{
    /// <summary>
    /// <c>REQ-UI-023</c>: limit and margin colouring.
    /// </summary>
    /// <remarks>
    /// Every test here sets all four colours to distinct values of its own rather than using the
    /// defaults. The defaults deliberately match the line each refers to — red for both limit
    /// entries, yellow for both margin ones — so a test left on them could not tell a correct
    /// implementation from the inverted one, which is precisely the failure the requirement warns
    /// about.
    /// </remarks>
    public sealed class LimitColouringTests
    {
        private static readonly PlotColor TraceColour = new PlotColor(0, 255, 0);
        private static readonly PlotColor LimitColour = new PlotColor(10, 20, 30);
        private static readonly PlotColor MarginColour = new PlotColor(40, 50, 60);
        private static readonly PlotColor FailLimitColour = new PlotColor(70, 80, 90);
        private static readonly PlotColor FailMarginColour = new PlotColor(100, 110, 120);

        [Fact]
        public void TheFourColoursDefaultToRedLimitsAndYellowMargins()
        {
            var colours = new LimitColours();

            Assert.Equal(new PlotColor(255, 0, 0), colours.Limit);
            Assert.Equal(new PlotColor(255, 255, 0), colours.Margin);
        }

        [Fact]
        public void FailingPointsRecolourTheTraceItself()
        {
            // The requirement's central statement, and the one it warns is often implemented
            // backwards: the *trace* takes the fail colour where it fails. The limit line does not
            // change colour — a recoloured line says which line failed but not where, which on a
            // display with three limit lines is the only thing worth knowing.
            SpectrumFrame trace = Trace(breachAt: 40);
            LimitLine line = Upper(limitDbm: -30.0, marginDb: 0.0);

            PlotColor[] shaded = LimitShading.ShadeTrace(trace, line, Colours(), TraceColour);

            Assert.Equal(FailLimitColour, shaded[40]);
        }

        [Fact]
        public void PassingPointsKeepTheTracesOwnColour()
        {
            SpectrumFrame trace = Trace(breachAt: 40);
            LimitLine line = Upper(limitDbm: -30.0, marginDb: 0.0);

            PlotColor[] shaded = LimitShading.ShadeTrace(trace, line, Colours(), TraceColour);

            Assert.Equal(TraceColour, shaded[0]);
            Assert.Equal(TraceColour, shaded[39]);
            Assert.Equal(TraceColour, shaded[41]);
        }

        [Fact]
        public void TheLimitColourNeverAppearsOnTheTrace()
        {
            // The inverted implementation — recolour the line, leave the trace alone — would have
            // to paint the trace with the *limit* colours to be caught anywhere else. It cannot
            // appear here whether the test passes or fails.
            SpectrumFrame trace = Trace(breachAt: 40);
            LimitLine line = Upper(limitDbm: -30.0, marginDb: 6.0);

            PlotColor[] shaded = LimitShading.ShadeTrace(trace, line, Colours(), TraceColour);

            Assert.DoesNotContain(LimitColour, shaded);
            Assert.DoesNotContain(MarginColour, shaded);
        }

        [Fact]
        public void APointInsideTheMarginTakesTheFailMarginColourNotTheFailLimitOne()
        {
            // The trace sits at −33 dBm: past the margin line at −36 but well clear of the limit at
            // −30. That is a warning, not a failure, and the two must not be conflated — a display
            // that painted this the failure colour would report a passing measurement as failed.
            SpectrumFrame trace = Trace(breachAt: 40, breachLevel: -33.0f);
            LimitLine line = Upper(limitDbm: -30.0, marginDb: 6.0);

            PlotColor[] shaded = LimitShading.ShadeTrace(trace, line, Colours(), TraceColour);

            Assert.Equal(FailMarginColour, shaded[40]);
        }

        [Fact]
        public void ThePointClassificationAgreesWithTheLimitTestsOwnVerdict()
        {
            // The colouring and the PASS/FAIL annotation must never disagree: a trace with a red
            // stretch and a PASS beside it is a bug report waiting to happen. Sharing the
            // comparison is not enough — this checks the two actually agree.
            SpectrumFrame trace = Trace(breachAt: 40);
            LimitLine line = Upper(limitDbm: -30.0, marginDb: 6.0);

            LimitLineResult verdict = line.Evaluate(trace);
            LimitStanding[] standings = LimitShading.Classify(trace, line);

            bool anyBeyond = Array.IndexOf(standings, LimitStanding.BeyondLimit) >= 0;

            Assert.False(verdict.Passed);
            Assert.True(anyBeyond);
        }

        [Fact]
        public void APassingTraceHasNoPointClassifiedBeyondTheLimit()
        {
            SpectrumFrame trace = Trace(breachAt: null);
            LimitLine line = Upper(limitDbm: -30.0, marginDb: 0.0);

            Assert.True(line.Evaluate(trace).Passed);
            Assert.DoesNotContain(LimitStanding.BeyondLimit, LimitShading.Classify(trace, line));
        }

        [Fact]
        public void APointExactlyOnTheLimitIsClear()
        {
            // The same inclusive boundary LimitLine.Evaluate uses. Two different boundary
            // conventions between the verdict and the colouring is how a trace ends up painted red
            // beside a PASS.
            SpectrumFrame trace = Trace(breachAt: 40, breachLevel: -30.0f);
            LimitLine line = Upper(limitDbm: -30.0, marginDb: 0.0);

            Assert.Equal(LimitStanding.Clear, LimitShading.Classify(trace, line)[40]);
            Assert.True(line.Evaluate(trace).Passed);
        }

        [Fact]
        public void ALowerLimitFailsBelowItselfRatherThanAbove()
        {
            // The upper/lower inversion, checked on the colouring as well as on the verdict.
            SpectrumFrame trace = Trace(breachAt: 40, breachLevel: -90.0f);
            var line = new LimitLine("floor", LimitSide.Lower);
            line.Add(0.995e9, -80.0).Add(1.005e9, -80.0);

            LimitStanding[] standings = LimitShading.Classify(trace, line);

            Assert.Equal(LimitStanding.BeyondLimit, standings[40]);
            Assert.Equal(LimitStanding.Clear, standings[0]);
        }

        [Fact]
        public void FrequenciesInAGapBetweenSegmentsAreLeftAlone()
        {
            // Untested is not failed. Evaluate declines to count them; the colouring must decline
            // to paint them, or a limit line covering half the span would appear to condemn the
            // other half.
            // The segment covers the first half of the trace only. Every point is at 0 dBm, so
            // every covered point breaches; the uncovered ones must stay the trace's own colour.
            SpectrumFrame trace = Trace(breachAt: null, level: 0.0f);
            var line = new LimitLine("partial", LimitSide.Upper);
            line.Add(0.999e9, -30.0).Add(1.0003e9, -30.0);

            PlotColor[] shaded = LimitShading.ShadeTrace(trace, line, Colours(), TraceColour);

            Assert.Equal(FailLimitColour, shaded[0]);
            Assert.Equal(TraceColour, shaded[trace.LevelsDbm.Length - 1]);
        }

        [Fact]
        public void PointsWithNoLevelAreLeftAlone()
        {
            var levels = new float[64];

            for (int i = 0; i < levels.Length; i++)
            {
                levels[i] = float.NaN;
            }

            SpectrumFrame trace =
                SpectrumFrame.FromLevels(levels, 1.0e9, 10e3, WindowType.FlatTop, 3.8194);
            LimitLine line = Upper(limitDbm: -30.0, marginDb: 0.0);

            Assert.DoesNotContain(LimitStanding.BeyondLimit, LimitShading.Classify(trace, line));
        }

        [Fact]
        public void MarginIndicationCanBeTurnedOffWithoutTurningOffFailures()
        {
            // A generous margin would otherwise paint most of a passing trace. The two switches are
            // separate because a margin is a warning and a limit is a verdict.
            SpectrumFrame trace = Trace(breachAt: 40, breachLevel: -33.0f);
            LimitLine line = Upper(limitDbm: -30.0, marginDb: 6.0);

            LimitColours colours = Colours();
            colours.IndicateMargin = false;

            PlotColor[] shaded = LimitShading.ShadeTrace(trace, line, colours, TraceColour);

            Assert.Equal(TraceColour, shaded[40]);

            SpectrumFrame failing = Trace(breachAt: 40);
            PlotColor[] stillFails =
                LimitShading.ShadeTrace(failing, line, colours, TraceColour);

            Assert.Equal(FailLimitColour, stillFails[40]);
        }

        [Fact]
        public void TurningOffFailureIndicationLeavesTheTraceOneColour()
        {
            SpectrumFrame trace = Trace(breachAt: 40);
            LimitLine line = Upper(limitDbm: -30.0, marginDb: 6.0);

            LimitColours colours = Colours();
            colours.IndicateFailures = false;

            foreach (PlotColor colour in
                LimitShading.ShadeTrace(trace, line, colours, TraceColour))
            {
                Assert.Equal(TraceColour, colour);
            }
        }

        [Fact]
        public void AZeroMarginNeverProducesAMarginWarning()
        {
            SpectrumFrame trace = Trace(breachAt: 40, breachLevel: -30.0f);
            LimitLine line = Upper(limitDbm: -30.0, marginDb: 0.0);

            Assert.DoesNotContain(LimitStanding.InsideMargin, LimitShading.Classify(trace, line));
        }

        [Fact]
        public void AcrossAWholeTestTheWorstStandingWins()
        {
            // A point breaking one line and merely nearing another is coloured for the break;
            // otherwise a lenient line would paint over a failure.
            SpectrumFrame trace = Trace(breachAt: 40);

            var test = new LimitTest("both");
            test.Add(Upper(limitDbm: -30.0, marginDb: 0.0));
            test.Add(Upper(limitDbm: 20.0, marginDb: 40.0));

            PlotColor[] shaded = LimitShading.ShadeTrace(trace, test, Colours(), TraceColour);

            Assert.Equal(FailLimitColour, shaded[40]);
        }

        [Fact]
        public void ADisabledTestColoursNothing()
        {
            SpectrumFrame trace = Trace(breachAt: 40);

            var test = new LimitTest("off") { IsEnabled = false };
            test.Add(Upper(limitDbm: -30.0, marginDb: 0.0));

            foreach (PlotColor colour in
                LimitShading.ShadeTrace(trace, test, Colours(), TraceColour))
            {
                Assert.Equal(TraceColour, colour);
            }
        }

        [Fact]
        public void SettingAColourBackChangesWhatIsPainted()
        {
            // The four entries are independently settable, which is the whole point of their being
            // four entries rather than one "fail" colour.
            SpectrumFrame trace = Trace(breachAt: 40);
            LimitLine line = Upper(limitDbm: -30.0, marginDb: 0.0);

            LimitColours colours = Colours();
            colours.FailLimit = new PlotColor(1, 2, 3);

            Assert.Equal(
                new PlotColor(1, 2, 3),
                LimitShading.ShadeTrace(trace, line, colours, TraceColour)[40]);
        }

        [Fact]
        public void RunsCoverEveryPointOnceAndInOrder()
        {
            SpectrumFrame trace = Trace(breachAt: 40);
            LimitLine line = Upper(limitDbm: -30.0, marginDb: 0.0);

            LimitStanding[] standings = LimitShading.Classify(trace, line);
            IReadOnlyList<LimitRun> runs = LimitShading.Runs(standings);

            int next = 0;
            int total = 0;

            foreach (LimitRun run in runs)
            {
                Assert.Equal(next, run.Start);
                Assert.True(run.Length > 0);

                for (int i = run.Start; i < run.Start + run.Length; i++)
                {
                    Assert.Equal(run.Standing, standings[i]);
                }

                next = run.Start + run.Length;
                total += run.Length;
            }

            Assert.Equal(standings.Length, total);
        }

        [Fact]
        public void OneBreachingPointBecomesItsOwnRun()
        {
            SpectrumFrame trace = Trace(breachAt: 40);
            LimitLine line = Upper(limitDbm: -30.0, marginDb: 0.0);

            IReadOnlyList<LimitRun> runs =
                LimitShading.Runs(LimitShading.Classify(trace, line));

            Assert.Equal(3, runs.Count);
            Assert.Equal(new LimitRun(40, 1, LimitStanding.BeyondLimit), runs[1]);
        }

        [Fact]
        public void NullArgumentsAreRejected()
        {
            SpectrumFrame trace = Trace(breachAt: null);
            LimitLine line = Upper(limitDbm: -30.0, marginDb: 0.0);

            Assert.Throws<ArgumentNullException>(
                () => LimitShading.Classify(null, line));
            Assert.Throws<ArgumentNullException>(
                () => LimitShading.Classify(trace, (LimitLine)null));
            Assert.Throws<ArgumentNullException>(
                () => LimitShading.Classify(trace, (LimitTest)null));
            Assert.Throws<ArgumentNullException>(
                () => LimitShading.ShadeTrace(null, Colours(), TraceColour));
            Assert.Throws<ArgumentNullException>(
                () => LimitShading.ShadeTrace(trace, line, null, TraceColour));
            Assert.Throws<ArgumentNullException>(() => LimitShading.Runs(null));
        }

        private static LimitColours Colours() => new LimitColours
        {
            Limit = LimitColour,
            Margin = MarginColour,
            FailLimit = FailLimitColour,
            FailMargin = FailMarginColour,
        };

        private static LimitLine Upper(double limitDbm, double marginDb)
        {
            var line = new LimitLine("mask", LimitSide.Upper) { MarginDb = marginDb };
            line.Add(0.995e9, limitDbm).Add(1.005e9, limitDbm);
            return line;
        }

        private static SpectrumFrame Trace(int? breachAt, float breachLevel = 0.0f, float level = -60.0f)
        {
            var levels = new float[64];

            for (int i = 0; i < levels.Length; i++)
            {
                levels[i] = level;
            }

            if (breachAt.HasValue)
            {
                levels[breachAt.Value] = breachLevel;
            }

            // Centred on 1 GHz with 10 kHz bins, so the whole trace lies inside the ±5 MHz limit
            // segments above.
            return SpectrumFrame.FromLevels(levels, 1.0e9, 10e3, WindowType.FlatTop, 3.8194);
        }
    }
}
