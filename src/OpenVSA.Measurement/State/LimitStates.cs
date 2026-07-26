using System;
using System.Collections.Generic;
using OpenVSA.Measurement.Limits;

namespace OpenVSA.Measurement.State
{
    /// <summary>
    /// Converts limit tests between the runtime hierarchy and the saved one
    /// (<c>REQ-LIM-001</c>, <c>REQ-STA-001</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Two shapes, deliberately.</strong> The runtime <see cref="LimitTest"/> is built for
    /// evaluating — its points are immutable, its lines are constructed with a name and a side that
    /// cannot then change — while the state model is built for reading, editing and writing as
    /// JSON, so every member is settable. Keeping them apart means the saved format does not
    /// dictate the runtime's immutability, and a change to either does not silently change the
    /// other's file format.
    /// </para>
    /// <para>
    /// The conversion is here rather than on either type so that neither has to know about the
    /// other's concerns, and so that a round-trip test has one obvious pair of functions to
    /// exercise.
    /// </para>
    /// </remarks>
    public static class LimitStates
    {
        /// <summary>
        /// Captures a runtime limit test as saveable state.
        /// </summary>
        /// <param name="test">The test to capture.</param>
        /// <exception cref="ArgumentNullException"><paramref name="test"/> is null.</exception>
        public static LimitTestState ToState(LimitTest test)
        {
            if (test == null)
            {
                throw new ArgumentNullException(nameof(test));
            }

            var state = new LimitTestState
            {
                Name = test.Name,
                IsEnabled = test.IsEnabled,
                Lines = new List<LimitLineState>(test.Lines.Count),
            };

            foreach (LimitLine line in test.Lines)
            {
                var saved = new LimitLineState
                {
                    Name = line.Name,
                    Side = line.Side,
                    MarginDb = line.MarginDb,
                    Points = new List<LimitPointState>(line.Points.Count),
                };

                foreach (LimitPoint point in line.Points)
                {
                    saved.Points.Add(new LimitPointState
                    {
                        XHz = point.XHz,
                        YDbm = point.YDbm,
                        ConnectToPrevious = point.ConnectToPrevious,
                    });
                }

                state.Lines.Add(saved);
            }

            return state;
        }

        /// <summary>
        /// Rebuilds a runtime limit test from saved state.
        /// </summary>
        /// <param name="state">The saved test.</param>
        /// <exception cref="ArgumentNullException"><paramref name="state"/> is null.</exception>
        /// <exception cref="ArgumentException">A name is missing.</exception>
        /// <remarks>
        /// A missing name is refused rather than replaced with a generated one. The requirement
        /// makes all three levels user-named so that a failure can be reported as "which test,
        /// which line, where"; a state file that lost a name would come back as a test reporting
        /// failures against something the user cannot identify, and inventing "Limit 3" for it
        /// would hide the loss rather than report it.
        /// </remarks>
        public static LimitTest ToLimitTest(LimitTestState state)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            var test = new LimitTest(state.Name) { IsEnabled = state.IsEnabled };

            if (state.Lines == null)
            {
                return test;
            }

            foreach (LimitLineState saved in state.Lines)
            {
                var line = new LimitLine(saved.Name, saved.Side) { MarginDb = saved.MarginDb };

                if (saved.Points != null)
                {
                    foreach (LimitPointState point in saved.Points)
                    {
                        line.Add(point.XHz, point.YDbm, point.ConnectToPrevious);
                    }
                }

                test.Add(line);
            }

            return test;
        }

        /// <summary>Captures several tests.</summary>
        /// <param name="tests">The tests to capture; may be empty.</param>
        /// <exception cref="ArgumentNullException"><paramref name="tests"/> is null.</exception>
        public static List<LimitTestState> ToState(IEnumerable<LimitTest> tests)
        {
            if (tests == null)
            {
                throw new ArgumentNullException(nameof(tests));
            }

            var states = new List<LimitTestState>();

            foreach (LimitTest test in tests)
            {
                states.Add(ToState(test));
            }

            return states;
        }

        /// <summary>Rebuilds several tests.</summary>
        /// <param name="states">The saved tests; may be empty.</param>
        /// <exception cref="ArgumentNullException"><paramref name="states"/> is null.</exception>
        public static List<LimitTest> ToLimitTests(IEnumerable<LimitTestState> states)
        {
            if (states == null)
            {
                throw new ArgumentNullException(nameof(states));
            }

            var tests = new List<LimitTest>();

            foreach (LimitTestState state in states)
            {
                tests.Add(ToLimitTest(state));
            }

            return tests;
        }
    }
}
