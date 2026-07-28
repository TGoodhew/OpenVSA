using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OpenVSA.Api;
using OpenVSA.Dsp.Spectrum;
using OpenVSA.Dsp.Windowing;
using OpenVSA.Measurement.Limits;
using Xunit;
using Xunit.Abstractions;

namespace OpenVSA.Measurement.Tests
{
    /// <summary>
    /// <c>REQ-LIM-003</c>: limit count and results through the automation API.
    /// </summary>
    /// <remarks>
    /// The criterion's substance is that the API is not reporting "a stale or separately computed
    /// verdict", that it answers while a measurement is running, and that what it answers is a
    /// completed evaluation rather than a partial one. Those are properties of the evaluator, so
    /// they are tested here rather than through a window.
    /// </remarks>
    public class LimitAutomationTests
    {
        private readonly ITestOutputHelper _output;

        /// <summary>Takes xunit's output sink.</summary>
        /// <param name="output">Where measured figures are written.</param>
        public LimitAutomationTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void CountAndResultAreQueryableThroughTheApi()
        {
            var application = new VsaApplication();
            VsaMeasurement measurement = application.Measurements[0];

            // Nothing set: no lines, and no verdict — which is not the same as a pass.
            Assert.Equal(0, measurement.LimitTests.Count);
            Assert.Null(measurement.LimitTests.Passed);
            Assert.Null(measurement.LimitTests.Result);

            measurement.Evaluator.Test = Test(-40.0);

            Assert.Equal(1, measurement.LimitTests.Count);

            // Still no verdict: a test that has never seen a frame has not passed.
            Assert.Null(measurement.LimitTests.Passed);

            measurement.Evaluator.Offer(Trace(-60.0));

            Assert.True(measurement.LimitTests.Passed);
            Assert.NotNull(measurement.LimitTests.Result);
            Assert.Single(measurement.LimitTests.Lines);
        }

        [Fact]
        public void TheQueriedResultMatchesTheEvaluationIncludingTheFailingCase()
        {
            // "including the failing case, so the API is not reporting a stale or separately
            // computed verdict". A pass-only test passes against an API hard-wired to true.
            var application = new VsaApplication();
            VsaMeasurement measurement = application.Measurements[0];

            measurement.Evaluator.Test = Test(-40.0);

            foreach (double level in new[] { -60.0, -20.0, -50.0, -10.0 })
            {
                LimitTestResult evaluated = measurement.Evaluator.Offer(Trace(level));

                bool expected = level < -40.0;

                _output.WriteLine(
                    "trace at " + level + " dBm against a -40 dBm upper limit: API says " +
                    measurement.LimitTests.Passed + ", evaluation says " + evaluated.Passed);

                Assert.Equal(expected, evaluated.Passed);

                // The API's answer and the evaluation are the same object, not two that agree.
                Assert.Equal(expected, measurement.LimitTests.Passed);
                Assert.Same(evaluated, measurement.LimitTests.Result);
            }
        }

        [Fact]
        public void ReplacingTheTestDiscardsTheStandingVerdict()
        {
            // A verdict that outlived the test it was computed against is exactly the stale answer
            // the requirement forbids.
            var application = new VsaApplication();
            VsaMeasurement measurement = application.Measurements[0];

            measurement.Evaluator.Test = Test(-40.0);
            measurement.Evaluator.Offer(Trace(-60.0));

            Assert.True(measurement.LimitTests.Passed);

            measurement.Evaluator.Test = Test(-80.0);

            Assert.Null(measurement.LimitTests.Passed);
            Assert.Equal(1, measurement.LimitTests.Count);

            // And Clear does the same without changing the test, which is what a restart calls.
            measurement.Evaluator.Offer(Trace(-90.0));
            Assert.True(measurement.LimitTests.Passed);

            measurement.Evaluator.Clear();
            Assert.Null(measurement.LimitTests.Passed);
            Assert.Equal(0L, measurement.LimitTests.EvaluationCount);
        }

        [Fact]
        public async Task TheQueryIsAnswerableWhileAMeasurementIsRunning()
        {
            // "The query is answerable while a measurement is running, and reports the state of a
            // completed evaluation rather than a partially updated one."
            //
            // A writer offering frames as fast as it can while a reader queries throughout: every
            // answer the reader gets is a whole verdict, and the reader is never blocked out.
            var application = new VsaApplication();
            VsaMeasurement measurement = application.Measurements[0];

            measurement.Evaluator.Test = Test(-40.0);

            var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(400.0));

            SpectrumFrame passing = Trace(-60.0);
            SpectrumFrame failing = Trace(-20.0);

            Task writer = Task.Run(() =>
            {
                long n = 0;

                while (!stop.IsCancellationRequested)
                {
                    measurement.Evaluator.Offer((n++ & 1) == 0 ? passing : failing);
                }
            });

            int answers = 0;
            int wholeVerdicts = 0;

            while (!stop.IsCancellationRequested)
            {
                LimitTestResult result = measurement.LimitTests.Result;

                answers++;

                // Yield to the writer. Without this the reader is a tight loop that never blocks,
                // and on a machine with few cores it starves the writer completely — a CI runner
                // with two cores gave "115265217 queries during 0 evaluations", so the test failed
                // for the one reason it was not testing. The point here is that reading is never
                // blocked out, and that is still shown: yielding lets the scheduler run the writer
                // and the reader still gets orders of magnitude more turns than it needs.
                Thread.Yield();

                if (result == null)
                {
                    continue;
                }

                // A whole verdict: the line list is populated and its own pass/fail agrees with
                // the test's. A half-written result would show a line count of zero, or lines that
                // disagree with the aggregate.
                bool whole =
                    result.Lines.Count == 1 &&
                    result.Passed == result.Lines.All(l => l.Passed);

                if (whole)
                {
                    wholeVerdicts++;
                }
                else
                {
                    Assert.Fail("A partially updated evaluation was observed.");
                }
            }

            await writer.ConfigureAwait(false);

            _output.WriteLine(
                answers + " queries during " + measurement.LimitTests.EvaluationCount +
                " evaluations; " + wholeVerdicts + " whole verdicts, 0 partial");

            Assert.True(answers > 100, "The reader was answered only " + answers + " times.");
            Assert.True(
                measurement.LimitTests.EvaluationCount > 0,
                "The writer completed no evaluations, so nothing about concurrent reading was shown.");
            Assert.True(wholeVerdicts > 0, "No verdict was ever observed.");
        }

        [Fact]
        public void TheWorstMarginFollowsTheVerdictsSign()
        {
            var application = new VsaApplication();
            VsaMeasurement measurement = application.Measurements[0];

            measurement.Evaluator.Test = Test(-40.0);

            Assert.True(double.IsNaN(measurement.LimitTests.WorstMarginDb));

            measurement.Evaluator.Offer(Trace(-60.0));
            Assert.True(measurement.LimitTests.WorstMarginDb > 0.0);

            measurement.Evaluator.Offer(Trace(-20.0));
            Assert.True(measurement.LimitTests.WorstMarginDb < 0.0);
        }

        [Fact]
        public void TheApiIsReachableWithNoUiLoaded()
        {
            // REQ-API-002's shape, checked as an assembly dependency: OpenVSA.Api must never
            // reference the WPF shell, or "usable with no UI loaded" is not a property it has.
            string[] referenced = typeof(VsaApplication).Assembly
                .GetReferencedAssemblies()
                .Select(a => a.Name)
                .ToArray();

            Assert.DoesNotContain("OpenVSA.Ui", referenced);
            Assert.DoesNotContain("PresentationFramework", referenced);
            Assert.DoesNotContain("PresentationCore", referenced);
        }

        [Fact]
        public void MissingArgumentsAreRefused()
        {
            Assert.Throws<ArgumentException>(() => new VsaApplication(new string[0]));
            Assert.Throws<ArgumentException>(() => new VsaApplication("  "));
            Assert.Throws<ArgumentNullException>(() => new LimitEvaluator().Offer(null));

            Assert.Null(new VsaApplication().Measurement("Nowhere"));
            Assert.NotNull(new VsaApplication().Measurement("measurement 1"));
        }

        // ---- Helpers -----------------------------------------------------------------------------

        /// <summary>An upper limit line across the whole span at a level.</summary>
        private static LimitTest Test(double levelDbm)
        {
            var line = new LimitLine("Upper", LimitSide.Upper);

            line.Add(new LimitPoint(0.999e9, levelDbm));
            line.Add(new LimitPoint(1.001e9, levelDbm));

            var test = new LimitTest("Automation");

            test.Add(line);
            return test;
        }

        /// <summary>A flat trace at a level, across the line's span.</summary>
        private static SpectrumFrame Trace(double levelDbm)
        {
            var levels = new float[401];

            for (int i = 0; i < levels.Length; i++)
            {
                levels[i] = (float)levelDbm;
            }

            return SpectrumFrame.FromLevels(levels, 0.999e9, 5e3, WindowType.FlatTop, 3.8194);
        }
    }
}
