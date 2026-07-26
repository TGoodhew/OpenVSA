using System;
using System.Linq;
using OpenVSA.Dsp.Spectrum;
using OpenVSA.Dsp.Windowing;
using OpenVSA.Measurement.Limits;
using OpenVSA.Measurement.State;
using Xunit;

namespace OpenVSA.Measurement.Tests.State
{
    /// <summary>
    /// <c>REQ-LIM-001</c>'s "names round-trip through save and recall", and the rest of the
    /// hierarchy with them.
    /// </summary>
    /// <remarks>
    /// This was the one criterion of <c>REQ-LIM-001</c> the engine could not satisfy on its own:
    /// the three levels existed and were named, but <c>MeasurementState</c> carried no limit lines,
    /// so nothing survived a save.
    /// </remarks>
    public class LimitStateTests
    {
        [Fact]
        public void AllThreeNamesSurviveSaveAndRecall()
        {
            // Test, line and point are a three-level hierarchy so that a failure can be reported as
            // "which test, which line, where". Two of those three are names, and a state file that
            // lost them would report failures against something nobody can identify.
            ApplicationState configured = ApplicationState.Default("Bench");

            configured.Measurements[0].LimitTests.Add(
                LimitStates.ToState(Mask("Emission mask", "Upper segment", "Lower floor")));

            ApplicationState recalled = StateFile.Read(StateFile.Write(configured));

            LimitTestState test = Assert.Single(recalled.Measurements[0].LimitTests);

            Assert.Equal("Emission mask", test.Name);
            Assert.Equal(2, test.Lines.Count);
            Assert.Equal("Upper segment", test.Lines[0].Name);
            Assert.Equal("Lower floor", test.Lines[1].Name);
        }

        [Fact]
        public void SidesMarginsAndConnectFlagsSurviveToo()
        {
            // The connect flag is the one that would go unnoticed. A recalled mask that lost it
            // would test bands the original left unconstrained, and the line would look right on
            // screen apart from the gaps having quietly filled in.
            ApplicationState configured = ApplicationState.Default("Bench");

            configured.Measurements[0].LimitTests.Add(
                LimitStates.ToState(Mask("Mask", "Upper", "Lower")));

            ApplicationState recalled = StateFile.Read(StateFile.Write(configured));
            LimitTestState test = recalled.Measurements[0].LimitTests[0];

            Assert.Equal(LimitSide.Upper, test.Lines[0].Side);
            Assert.Equal(LimitSide.Lower, test.Lines[1].Side);
            Assert.Equal(2.5, test.Lines[0].MarginDb, 9);

            // The upper line's third point starts a new segment.
            Assert.True(test.Lines[0].Points[0].ConnectToPrevious);
            Assert.False(test.Lines[0].Points[2].ConnectToPrevious);
        }

        [Fact]
        public void ARecalledMaskEvaluatesIdenticallyToTheOneItWasSavedFrom()
        {
            // The assertion that matters: not that the fields came back, but that the measurement
            // they describe is the same measurement. A gap that closed, a side that inverted or a
            // margin that vanished all change the verdict, and all of them would pass a
            // field-by-field comparison of a subset.
            LimitTest before = Mask("Mask", "Upper", "Lower");

            ApplicationState configured = ApplicationState.Default("Bench");
            configured.Measurements[0].LimitTests.Add(LimitStates.ToState(before));

            ApplicationState recalled = StateFile.Read(StateFile.Write(configured));
            LimitTest after = LimitStates.ToLimitTest(recalled.Measurements[0].LimitTests[0]);

            SpectrumFrame trace = Breaching();

            LimitTestResult expected = before.Evaluate(trace);
            LimitTestResult actual = after.Evaluate(trace);

            // A verdict to agree about, not just a margin.
            Assert.False(expected.Passed);
            Assert.Equal(expected.Passed, actual.Passed);
            Assert.Equal(expected.WorstMarginDb, actual.WorstMarginDb, 9);
            Assert.Equal(expected.Worst.Line.Name, actual.Worst.Line.Name);

            for (int i = 0; i < expected.Lines.Count; i++)
            {
                Assert.Equal(expected.Lines[i].Passed, actual.Lines[i].Passed);
                Assert.Equal(expected.Lines[i].TestedPoints, actual.Lines[i].TestedPoints);
                Assert.Equal(expected.Lines[i].WorstMarginDb, actual.Lines[i].WorstMarginDb, 9);
            }
        }

        [Fact]
        public void TheGapIsWhatWouldBeLostAndTheTestSaysSo()
        {
            // Proves the previous test is testing something: joined up, this mask fails.
            LimitTest gapped = Mask("Mask", "Upper", "Lower");
            LimitTest joined = LimitStates.ToLimitTest(LimitStates.ToState(gapped));

            // Rebuild the upper line with its segments joined, which is what losing the flag does.
            var closed = new LimitTest("Mask");
            var line = new LimitLine("Upper", LimitSide.Upper) { MarginDb = 2.5 };

            foreach (LimitPoint point in joined.Lines[0].Points)
            {
                line.Add(point.XHz, point.YDbm);
            }

            closed.Add(line);

            SpectrumFrame trace = InTheGap();

            Assert.True(gapped.Lines[0].Evaluate(trace).Passed);
            Assert.False(closed.Lines[0].Evaluate(trace).Passed);

            // And it is the lump in the gap that does it, not something incidental.
            Assert.Equal(1.002e9, closed.Lines[0].Evaluate(trace).WorstXHz, 0);
        }

        [Fact]
        public void ADisabledTestStaysDisabled()
        {
            LimitTest disabled = Mask("Mask", "Upper", "Lower");
            disabled.IsEnabled = false;

            LimitTest recalled = LimitStates.ToLimitTest(LimitStates.ToState(disabled));

            Assert.False(recalled.IsEnabled);
        }

        [Fact]
        public void AMeasurementStartsWithNoLimits()
        {
            // Unlike a marker, where one is the useful starting point, a limit line is something
            // the user drew or imported.
            Assert.Empty(ApplicationState.Default("Bench").Measurements[0].LimitTests);
        }

        [Fact]
        public void SeveralTestsRoundTripInOrder()
        {
            LimitTest[] tests =
            {
                Mask("First", "A", "B"),
                Mask("Second", "C", "D"),
            };

            var recalled = LimitStates.ToLimitTests(LimitStates.ToState(tests));

            Assert.Equal(new[] { "First", "Second" }, recalled.Select(t => t.Name));
            Assert.Equal(new[] { "A", "C" }, recalled.Select(t => t.Lines[0].Name));
        }

        [Fact]
        public void AStateWithNoNameIsRefusedRatherThanNamedForTheUser()
        {
            // Inventing a name would hide the loss rather than report it.
            var nameless = new LimitTestState { Name = string.Empty };

            Assert.Throws<ArgumentException>(() => LimitStates.ToLimitTest(nameless));

            var namelessLine = new LimitTestState
            {
                Name = "Test",
                Lines = { new LimitLineState { Name = null } },
            };

            Assert.Throws<ArgumentException>(() => LimitStates.ToLimitTest(namelessLine));
        }

        [Fact]
        public void AStateWithNoLinesOrPointsIsAcceptedAsEmpty()
        {
            // Absent, not malformed: a test someone created and has not drawn yet.
            LimitTest empty = LimitStates.ToLimitTest(
                new LimitTestState { Name = "Blank", Lines = null });

            Assert.Empty(empty.Lines);

            LimitTest pointless = LimitStates.ToLimitTest(new LimitTestState
            {
                Name = "Blank",
                Lines = { new LimitLineState { Name = "Line", Points = null } },
            });

            Assert.Empty(pointless.Lines[0].Points);
        }

        [Fact]
        public void MissingArgumentsAreRefused()
        {
            Assert.Throws<ArgumentNullException>(() => LimitStates.ToState((LimitTest)null));
            Assert.Throws<ArgumentNullException>(() => LimitStates.ToLimitTest(null));
            Assert.Throws<ArgumentNullException>(
                () => LimitStates.ToState((System.Collections.Generic.IEnumerable<LimitTest>)null));
            Assert.Throws<ArgumentNullException>(() => LimitStates.ToLimitTests(null));
        }

        /// <summary>
        /// A two-line mask shaped like an emission mask: a high segment over the carrier, a low one
        /// further out, and a deliberate gap between them where nothing is tested.
        /// </summary>
        private static LimitTest Mask(string testName, string upperName, string lowerName)
        {
            var upper = new LimitLine(upperName, LimitSide.Upper) { MarginDb = 2.5 };

            upper.Add(1.000e9, -20.0);
            upper.Add(1.001e9, -20.0);

            // The gap: nothing is tested between 1.001 and 1.003 GHz. Joined up, the segment
            // between them would slope from -20 to -45 dBm.
            upper.Add(1.003e9, -45.0, connectToPrevious: false);
            upper.Add(1.004e9, -45.0);

            var lower = new LimitLine(lowerName, LimitSide.Lower) { MarginDb = 0.0 };

            lower.Add(1.000e9, -200.0);
            lower.Add(1.004e9, -200.0);

            return new LimitTest(testName).Add(upper).Add(lower);
        }

        /// <summary>
        /// A trace with one lump, and it sits in the gap: the mask passes it, and would not if the
        /// segments were joined.
        /// </summary>
        private static SpectrumFrame InTheGap() => Trace(null);

        /// <summary>
        /// The same, plus a breach inside the far segment — so the recalled test has a verdict and
        /// a location to agree about, not just a margin.
        /// </summary>
        private static SpectrumFrame Breaching() => Trace(350);

        private static SpectrumFrame Trace(int? breachIndex)
        {
            const int points = 401;
            var levels = new float[points];

            for (int i = 0; i < points; i++)
            {
                levels[i] = -80.0f;
            }

            // 1.002 GHz, squarely inside the untested gap. At -30 dBm it is 5 dB over what a joined
            // segment would allow there once the 2.5 dB margin is taken off.
            levels[200] = -30.0f;

            if (breachIndex.HasValue)
            {
                // 1.0035 GHz, inside the far segment, 7.5 dB over its limit with the margin on.
                levels[breachIndex.Value] = -40.0f;
            }

            return SpectrumFrame.FromLevels(levels, 1.000e9, 10e3, WindowType.FlatTop, 3.8194);
        }
    }
}
