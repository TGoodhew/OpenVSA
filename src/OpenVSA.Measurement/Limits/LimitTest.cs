using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using OpenVSA.Dsp.Spectrum;

namespace OpenVSA.Measurement.Limits
{
    /// <summary>Which side of a limit line a trace must stay on.</summary>
    public enum LimitSide
    {
        /// <summary>The trace must stay below the line; being above it fails.</summary>
        Upper = 0,

        /// <summary>The trace must stay above the line; being below it fails.</summary>
        Lower,
    }

    /// <summary>
    /// One vertex of a limit line.
    /// </summary>
    /// <remarks>
    /// <see cref="ConnectToPrevious"/> is what makes a limit line a set of segments rather than
    /// one unbroken run. A point with it clear starts a new segment, and the gap before it is not
    /// tested at all — which is how a mask leaves a band unconstrained rather than constraining it
    /// to whatever a straight line between the neighbouring points would have implied.
    /// </remarks>
    public sealed class LimitPoint
    {
        /// <summary>Creates a limit point.</summary>
        /// <param name="xHz">Frequency, in hertz.</param>
        /// <param name="yDbm">Level, in dBm.</param>
        /// <param name="connectToPrevious">Whether a segment runs from the previous point to this one.</param>
        public LimitPoint(double xHz, double yDbm, bool connectToPrevious = true)
        {
            XHz = xHz;
            YDbm = yDbm;
            ConnectToPrevious = connectToPrevious;
        }

        /// <summary>Frequency, in hertz.</summary>
        public double XHz { get; }

        /// <summary>Level, in dBm.</summary>
        public double YDbm { get; }

        /// <summary>Whether a segment runs from the previous point to this one.</summary>
        public bool ConnectToPrevious { get; }
    }

    /// <summary>The outcome of evaluating one limit line.</summary>
    public sealed class LimitLineResult
    {
        internal LimitLineResult(
            LimitLine line, bool passed, double worstMarginDb, double worstXHz, int testedPoints)
        {
            Line = line;
            Passed = passed;
            WorstMarginDb = worstMarginDb;
            WorstXHz = worstXHz;
            TestedPoints = testedPoints;
        }

        /// <summary>The line evaluated.</summary>
        public LimitLine Line { get; }

        /// <summary>Whether every tested point stayed on the passing side.</summary>
        public bool Passed { get; }

        /// <summary>
        /// Worst margin in dB: positive when passing, negative by the amount of the worst breach.
        /// </summary>
        /// <remarks>
        /// Signed the same way for both sides, so "more margin is better" holds without the reader
        /// having to remember which side the line is. An Upper line's margin is
        /// <c>limit − trace</c>; a Lower line's is <c>trace − limit</c>.
        /// </remarks>
        public double WorstMarginDb { get; }

        /// <summary>Frequency at which the worst margin occurred, in hertz.</summary>
        public double WorstXHz { get; }

        /// <summary>Trace points that fell inside a segment and were therefore tested.</summary>
        public int TestedPoints { get; }

        /// <inheritdoc />
        public override string ToString() =>
            Line.Name + ": " + (Passed ? "PASS" : "FAIL") + ", worst margin " +
            WorstMarginDb.ToString("F2", CultureInfo.CurrentCulture) + " dB at " +
            WorstXHz.ToString("G8", CultureInfo.CurrentCulture) + " Hz";
    }

    /// <summary>
    /// A named limit line: connected segments, a side, and a margin (<c>REQ-LIM-001</c>).
    /// </summary>
    public sealed class LimitLine
    {
        private readonly List<LimitPoint> _points = new List<LimitPoint>();

        /// <summary>Creates a limit line.</summary>
        /// <param name="name">User-facing name; required.</param>
        /// <param name="side">Which side the trace must stay on.</param>
        /// <exception cref="ArgumentException"><paramref name="name"/> is missing.</exception>
        public LimitLine(string name, LimitSide side)
        {
            if (string.IsNullOrEmpty(name))
            {
                throw new ArgumentException("A limit line needs a name.", nameof(name));
            }

            Name = name;
            Side = side;
        }

        /// <summary>User-facing name.</summary>
        public string Name { get; }

        /// <summary>Which side of this line the trace must stay on.</summary>
        public LimitSide Side { get; }

        /// <summary>
        /// Margin in dB, applied on the passing side of the limit.
        /// </summary>
        /// <remarks>
        /// <c>REQ-LIM-001</c>: "Margin is applied on the pass side of the limit, never the fail
        /// side." A margin therefore makes a test <em>harder</em> to pass — it moves the line
        /// towards the trace — which is the only reading under which a margin is a safety factor
        /// rather than an allowance.
        /// </remarks>
        public double MarginDb { get; set; }

        /// <summary>The points, in the order they were added.</summary>
        public IReadOnlyList<LimitPoint> Points => new ReadOnlyCollection<LimitPoint>(_points);

        /// <summary>Adds a point.</summary>
        /// <param name="point">The point.</param>
        /// <returns>This line, so points can be chained.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="point"/> is null.</exception>
        public LimitLine Add(LimitPoint point)
        {
            if (point == null)
            {
                throw new ArgumentNullException(nameof(point));
            }

            _points.Add(point);
            return this;
        }

        /// <summary>Adds a point by coordinates.</summary>
        /// <param name="xHz">Frequency, in hertz.</param>
        /// <param name="yDbm">Level, in dBm.</param>
        /// <param name="connectToPrevious">Whether a segment runs from the previous point to this one.</param>
        /// <returns>This line, so points can be chained.</returns>
        public LimitLine Add(double xHz, double yDbm, bool connectToPrevious = true) =>
            Add(new LimitPoint(xHz, yDbm, connectToPrevious));

        /// <summary>
        /// The limit at a frequency, or <see cref="double.NaN"/> where no segment covers it.
        /// </summary>
        /// <param name="xHz">Frequency, in hertz.</param>
        /// <remarks>
        /// Linear interpolation in dB against frequency, between the two points of the segment
        /// that spans the frequency. A frequency in a gap has no limit at all rather than an
        /// extrapolated one, which is the whole purpose of the connect flag.
        /// </remarks>
        public double LimitAt(double xHz)
        {
            for (int i = 1; i < _points.Count; i++)
            {
                if (!_points[i].ConnectToPrevious)
                {
                    continue;
                }

                LimitPoint from = _points[i - 1];
                LimitPoint to = _points[i];

                double low = Math.Min(from.XHz, to.XHz);
                double high = Math.Max(from.XHz, to.XHz);

                if (xHz < low || xHz > high)
                {
                    continue;
                }

                if (Math.Abs(to.XHz - from.XHz) < double.Epsilon)
                {
                    return Math.Max(from.YDbm, to.YDbm);
                }

                double fraction = (xHz - from.XHz) / (to.XHz - from.XHz);
                return from.YDbm + fraction * (to.YDbm - from.YDbm);
            }

            return double.NaN;
        }

        /// <summary>
        /// Evaluates a trace against this line.
        /// </summary>
        /// <param name="frame">The trace to test.</param>
        /// <returns>Pass or fail, with the worst margin and where it occurred.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="frame"/> is null.</exception>
        public LimitLineResult Evaluate(SpectrumFrame frame)
        {
            if (frame == null)
            {
                throw new ArgumentNullException(nameof(frame));
            }

            ReadOnlySpan<float> levels = frame.LevelsDbm;

            double worstMargin = double.PositiveInfinity;
            double worstX = double.NaN;
            int tested = 0;

            for (int i = 0; i < levels.Length; i++)
            {
                if (float.IsNaN(levels[i]))
                {
                    continue;
                }

                double x = frame.FrequencyAt(i);
                double limit = LimitAt(x);

                if (double.IsNaN(limit))
                {
                    // In a gap between segments: not tested here at all.
                    continue;
                }

                tested++;

                // The margin moves the line towards the trace, so it tightens the test.
                double margin = Side == LimitSide.Upper
                    ? limit - MarginDb - levels[i]
                    : levels[i] - (limit + MarginDb);

                if (margin < worstMargin)
                {
                    worstMargin = margin;
                    worstX = x;
                }
            }

            if (tested == 0)
            {
                // Nothing was tested, so nothing failed. Reported with a zero margin and no
                // location, rather than as a pass that implies a measurement was made.
                return new LimitLineResult(this, true, double.NaN, double.NaN, 0);
            }

            // A trace exactly on the limit passes: the documented boundary convention is that the
            // limit is inclusive, so a margin of exactly zero is a pass. Stated because the
            // requirement asks for the boundary case to be documented and tested rather than left
            // to whichever comparison operator was typed.
            return new LimitLineResult(this, worstMargin >= 0.0, worstMargin, worstX, tested);
        }
    }

    /// <summary>The outcome of a whole limit test.</summary>
    public sealed class LimitTestResult
    {
        internal LimitTestResult(string name, IReadOnlyList<LimitLineResult> lines)
        {
            Name = name;
            Lines = lines;
        }

        /// <summary>The test's name.</summary>
        public string Name { get; }

        /// <summary>One result per line.</summary>
        public IReadOnlyList<LimitLineResult> Lines { get; }

        /// <summary>PASS only if every line passed (<c>REQ-LIM-002</c>).</summary>
        public bool Passed => Lines.All(l => l.Passed);

        /// <summary>The worst margin across every line that tested anything.</summary>
        public double WorstMarginDb
        {
            get
            {
                LimitLineResult[] measured = Lines.Where(l => l.TestedPoints > 0).ToArray();
                return measured.Length == 0 ? double.NaN : measured.Min(l => l.WorstMarginDb);
            }
        }

        /// <summary>The line with the worst margin, or <c>null</c> if nothing was tested.</summary>
        public LimitLineResult Worst
        {
            get
            {
                LimitLineResult[] measured = Lines.Where(l => l.TestedPoints > 0).ToArray();

                if (measured.Length == 0)
                {
                    return null;
                }

                return measured.Aggregate((a, b) => b.WorstMarginDb < a.WorstMarginDb ? b : a);
            }
        }

        /// <inheritdoc />
        public override string ToString()
        {
            LimitLineResult worst = Worst;

            return Name + ": " + (Passed ? "PASS" : "FAIL") +
                (worst == null ? " (nothing tested)" : ", worst " + worst);
        }
    }

    /// <summary>
    /// A named limit test: several limit lines evaluated together (<c>REQ-LIM-001</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The three-level hierarchy the requirement asks for — test, line, point — each named, so a
    /// failure can be reported as "which test, which line, where" rather than as a bare verdict.
    /// </para>
    /// <para>
    /// <strong>One comparison, used everywhere.</strong> <c>REQ-CHM-003</c>'s emission mask reuses
    /// this engine rather than reimplementing the comparison, because a second implementation is
    /// where an upper/lower inversion comes back.
    /// </para>
    /// </remarks>
    public sealed class LimitTest
    {
        private readonly List<LimitLine> _lines = new List<LimitLine>();

        /// <summary>Creates a limit test.</summary>
        /// <param name="name">User-facing name; required.</param>
        /// <exception cref="ArgumentException"><paramref name="name"/> is missing.</exception>
        public LimitTest(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                throw new ArgumentException("A limit test needs a name.", nameof(name));
            }

            Name = name;
        }

        /// <summary>User-facing name.</summary>
        public string Name { get; }

        /// <summary>Whether this test is evaluated at all.</summary>
        public bool IsEnabled { get; set; } = true;

        /// <summary>The lines belonging to this test.</summary>
        public IReadOnlyList<LimitLine> Lines => new ReadOnlyCollection<LimitLine>(_lines);

        /// <summary>Adds a line.</summary>
        /// <param name="line">The line.</param>
        /// <returns>This test, so lines can be chained.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="line"/> is null.</exception>
        public LimitTest Add(LimitLine line)
        {
            if (line == null)
            {
                throw new ArgumentNullException(nameof(line));
            }

            _lines.Add(line);
            return this;
        }

        /// <summary>Evaluates every line against a trace.</summary>
        /// <param name="frame">The trace to test.</param>
        /// <exception cref="ArgumentNullException"><paramref name="frame"/> is null.</exception>
        public LimitTestResult Evaluate(SpectrumFrame frame)
        {
            if (frame == null)
            {
                throw new ArgumentNullException(nameof(frame));
            }

            var results = new List<LimitLineResult>(_lines.Count);

            foreach (LimitLine line in _lines)
            {
                results.Add(line.Evaluate(frame));
            }

            return new LimitTestResult(Name, results);
        }
    }
}
