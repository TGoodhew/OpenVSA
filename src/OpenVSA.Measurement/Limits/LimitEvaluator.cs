using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using OpenVSA.Dsp.Spectrum;

namespace OpenVSA.Measurement.Limits
{
    /// <summary>
    /// Evaluates a limit test per frame and publishes the last completed verdict
    /// (<c>REQ-LIM-003</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>One evaluation, two readers.</strong> <c>REQ-LIM-003</c>'s criterion is that the
    /// queried result "matches the on-screen pass/fail for the same trace in every case a test
    /// exercises — including the failing case, so the API is not reporting a stale or separately
    /// computed verdict". The way that goes wrong is for the display to evaluate for its shading
    /// and the API to evaluate again when asked: two computations that agree until one of them is
    /// given a different frame. Here there is one, and both read it.
    /// </para>
    /// <para>
    /// <strong>Published whole, never half-written.</strong> "reports the state of a completed
    /// evaluation rather than a partially updated one" — so the result is an immutable object
    /// swapped in with a single reference write once it is finished. A reader either sees the
    /// previous verdict or the new one, never a line list halfway through being replaced.
    /// </para>
    /// <para>
    /// <strong>And it answers while a measurement is running</strong>, which is the third clause:
    /// reading takes no lock and blocks nothing, so a query during acquisition costs the caller a
    /// volatile read and costs the measurement nothing.
    /// </para>
    /// </remarks>
    public sealed class LimitEvaluator
    {
        private LimitTest _test;
        private LimitTestResult _latest;
        private long _completed;

        /// <summary>
        /// The limit test being evaluated, or <c>null</c> when there is none.
        /// </summary>
        /// <remarks>
        /// Replacing it discards the previous verdict rather than leaving it standing: a result
        /// that outlived the test it was computed against would be exactly the stale answer the
        /// requirement forbids.
        /// </remarks>
        public LimitTest Test
        {
            get { return Volatile.Read(ref _test); }

            set
            {
                Volatile.Write(ref _latest, null);
                Volatile.Write(ref _test, value);
            }
        }

        /// <summary>
        /// How many limit lines are under test (<c>REQ-LIM-003</c>'s count).
        /// </summary>
        /// <remarks>
        /// The lines, not the tests. The reference product's <c>:TRACe:LIMit:COUNt</c> counts what
        /// can pass or fail on a trace, and a test with no lines tests nothing.
        /// </remarks>
        public int LineCount
        {
            get
            {
                LimitTest test = Test;

                return test == null ? 0 : test.Lines.Count;
            }
        }

        /// <summary>
        /// The last completed verdict, or <c>null</c> when nothing has been evaluated.
        /// </summary>
        /// <remarks>
        /// <c>null</c> rather than a manufactured pass. "No evaluation yet" and "everything passed"
        /// are different answers, and an API that conflated them would report a pass for a
        /// measurement that had not started.
        /// </remarks>
        public LimitTestResult Latest => Volatile.Read(ref _latest);

        /// <summary>How many evaluations have completed since the test was set.</summary>
        /// <remarks>
        /// So a caller can tell a verdict that has just been recomputed from one that is standing
        /// because no frame has arrived — the same distinction <c>Spectrogram.AddedCount</c> draws.
        /// </remarks>
        public long CompletedCount => Interlocked.Read(ref _completed);

        /// <summary>
        /// Evaluates a frame and publishes the verdict.
        /// </summary>
        /// <param name="frame">The spectrum to test.</param>
        /// <returns>The verdict, or <c>null</c> when there is no test.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="frame"/> is null.</exception>
        /// <remarks>
        /// The evaluation happens before the publish, so the reference that becomes visible is
        /// always to a finished object. Writing the result into a shared mutable holder and then
        /// filling it in is the shape this avoids.
        /// </remarks>
        public LimitTestResult Offer(SpectrumFrame frame)
        {
            if (frame == null)
            {
                throw new ArgumentNullException(nameof(frame));
            }

            LimitTest test = Test;

            if (test == null)
            {
                return null;
            }

            LimitTestResult result = test.Evaluate(frame);

            Volatile.Write(ref _latest, result);
            Interlocked.Increment(ref _completed);

            return result;
        }

        /// <summary>Discards the standing verdict without changing the test.</summary>
        /// <remarks>
        /// What a restart calls. A verdict computed against the previous measurement is not a
        /// verdict about this one.
        /// </remarks>
        public void Clear()
        {
            Volatile.Write(ref _latest, null);
            Interlocked.Exchange(ref _completed, 0L);
        }

        /// <summary>The per-line verdicts of the last completed evaluation.</summary>
        public IReadOnlyList<LimitLineResult> Lines
        {
            get
            {
                LimitTestResult result = Latest;

                return result == null
                    ? new ReadOnlyCollection<LimitLineResult>(new LimitLineResult[0])
                    : result.Lines;
            }
        }

        /// <inheritdoc />
        public override string ToString()
        {
            LimitTestResult result = Latest;

            return LineCount + " line(s), " +
                (result == null ? "not evaluated" : (result.Passed ? "PASS" : "FAIL"));
        }
    }
}
