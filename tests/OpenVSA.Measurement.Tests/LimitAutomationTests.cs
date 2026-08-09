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
        public void TheQueryIsAnswerableWhileAMeasurementIsRunning()
        {
            // "The query is answerable while a measurement is running, and reports the state of a
            // completed evaluation rather than a partially updated one."
            //
            // A writer offering frames as fast as it can while a reader queries throughout: every
            // answer the reader gets is a whole verdict, and the reader is never blocked out.
            var application = new VsaApplication();
            VsaMeasurement measurement = application.Measurements[0];

            measurement.Evaluator.Test = Test(-40.0);

            var stop = new CancellationTokenSource();

            SpectrumFrame passing = Trace(-60.0);
            SpectrumFrame failing = Trace(-20.0);

            // **A dedicated thread, not Task.Run.** The pool hands out threads grudgingly — roughly
            // one new one per half-second once its initial count is busy — and on a two-core CI
            // runner with xUnit running other collections in parallel the writer simply never
            // started inside the window. That produced "22 queries during 0 evaluations": not the
            // race this test is about, just a writer that had not run yet.
            var running = new ManualResetEventSlim(false);

            var writer = new Thread(() =>
            {
                long n = 0;

                while (!stop.IsCancellationRequested)
                {
                    measurement.Evaluator.Offer((n++ & 1) == 0 ? passing : failing);
                    running.Set();
                }
            })
            {
                IsBackground = true,
            };

            writer.Start();

            // The timed window opens only once the writer has actually evaluated something.
            // Otherwise the test measures how long the scheduler took to start it, and asserts
            // about concurrency that never happened.
            Assert.True(
                running.Wait(TimeSpan.FromSeconds(10.0)),
                "The writer never completed an evaluation, so no concurrent read was ever attempted.");

            // **The window is bounded by WORK as well as by time, and that is the fix for #422.**
            //
            // It used to be 400 ms and nothing else, which made every count below a measurement of
            // how much processor the test happened to get rather than of anything about limits.
            // Measured over 23 full-solution runs — 8 unpinned, 3 pinned to two cores, 12 to one:
            //
            //     queries      3 … 1 026 777      against "at least one verdict seen"
            //     evaluations  3 274 … 84 177     against "more than a thousand"
            //     slowest read 0.000 … 2.615 ms   against 50 ms
            //
            // Five orders of magnitude on the reads. Neither count ever actually fell through —
            // none of the 23 runs failed — but "at least one" was reached with three to spare on a
            // loaded machine, and the property those counts exist to support, that a read is never
            // blocked out, was never in doubt in any run.
            //
            // Running until the counts are reached cannot fail for want of a scheduler slot: on a
            // fast machine the 400 ms still dominates and the loop takes its usual hundreds of
            // thousands of samples, and on a slow one it simply takes longer. The safety deadline
            // is the honest end of it — if even that is not enough, the message says which count
            // fell short rather than leaving a bare assertion to be re-run and forgotten.
            var window = System.Diagnostics.Stopwatch.StartNew();

            TimeSpan atLeast = TimeSpan.FromMilliseconds(400.0);
            TimeSpan giveUp = TimeSpan.FromSeconds(30.0);

            const int RequiredReads = 1;
            const long RequiredEvaluations = 1000L;

            int answers = 0;
            int wholeVerdicts = 0;
            double slowestReadMs = 0.0;

            var readClock = new System.Diagnostics.Stopwatch();

            while (window.Elapsed < giveUp &&
                   (window.Elapsed < atLeast ||
                    answers < RequiredReads ||
                    measurement.LimitTests.EvaluationCount < RequiredEvaluations))
            {
                // Time the read itself, not the loop. "Answerable while a measurement is running"
                // is a statement about how long a query waits, and that is what gets measured.
                readClock.Restart();
                LimitTestResult result = measurement.LimitTests.Result;
                double readMs = readClock.Elapsed.TotalMilliseconds;

                if (readMs > slowestReadMs)
                {
                    slowestReadMs = readMs;
                }

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

            stop.Cancel();

            Assert.True(writer.Join(TimeSpan.FromSeconds(10.0)), "The writer did not stop.");

            _output.WriteLine(
                answers + " queries during " + measurement.LimitTests.EvaluationCount +
                " evaluations over " + window.Elapsed.TotalMilliseconds.ToString("F0") +
                " ms; " + wholeVerdicts + " whole verdicts, 0 partial; slowest read " +
                slowestReadMs.ToString("F3") + " ms");

            // The writer must actually have been running, or nothing concurrent was demonstrated.
            // Guaranteed by the loop above rather than hoped for, so a failure here means the
            // deadline expired — and says so, instead of reading as a concurrency defect.
            Assert.True(
                measurement.LimitTests.EvaluationCount >= RequiredEvaluations,
                "The writer completed only " + measurement.LimitTests.EvaluationCount +
                " evaluations in " + window.Elapsed.TotalSeconds.ToString("F1") +
                " s, so the reads did not overlap a busy writer. That is a starved machine " +
                "rather than a blocked reader: the loop waits for this count and gave up.");

            // Every read returned a whole verdict. The writer had already evaluated before the
            // window opened, so there is no legitimate null in here — a mismatch would mean the
            // published reference went briefly back to nothing.
            Assert.True(
                wholeVerdicts >= RequiredReads,
                "No verdict was observed in " + window.Elapsed.TotalSeconds.ToString("F1") +
                " s. The reader is the test thread, and under a full-solution run pinned to one " +
                "core it has been measured taking THREE turns in its window — so this is the " +
                "deadline expiring, not a reader that was refused an answer.");

            Assert.Equal(answers, wholeVerdicts);

            // **This is the claim, not the query count.** An earlier version asserted more than a
            // hundred reads, which is a proxy for "never blocked out" that depends on how many
            // cores the machine has: a two-core CI runner managed 43 reads against 66 831
            // evaluations and failed, having demonstrated exactly the property it was meant to.
            // What matters is that no single read waited, and reading takes a volatile load and no
            // lock, so the bound is generous only to absorb scheduling.
            //
            // **#422 asked whether this absolute threshold is the fragile one. It was measured,
            // and it is not.** Across 23 full-solution runs the worst single read was 2.615 ms,
            // and it occurred on an UNPINNED one: adversity makes this number BETTER, not worse,
            // because the maximum is the worst of N samples and N collapses along with everything
            // else — 3 reads pinned to one core against over a million unpinned. Normalising it
            // against a reference operation, as #415 concluded for LoggingTests, would therefore
            // be treating the assertion that was not at risk. Left exactly as it is, deliberately,
            // with the figures recorded so the next person need not measure it again.
            Assert.True(
                slowestReadMs < 50.0,
                "A query took " + slowestReadMs.ToString("F1") + " ms, so reading is blocked by writing.");
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
