using System;
using System.Collections.Generic;
using OpenVSA.Dsp.Spectrum;
using OpenVSA.Measurement.Limits;

namespace OpenVSA.Ui.Rendering
{
    /// <summary>
    /// How a trace point stands against a limit line (<c>REQ-UI-023</c>).
    /// </summary>
    public enum LimitStanding
    {
        /// <summary>Clear of both the limit and its margin, or not tested at all.</summary>
        Clear = 0,

        /// <summary>Inside the margin but still on the passing side of the limit itself.</summary>
        InsideMargin,

        /// <summary>Past the limit.</summary>
        BeyondLimit,
    }

    /// <summary>
    /// The four limit-related colours of <c>REQ-UI-023</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Two of these colour the line; two colour the trace.</strong> <em>Limit</em> and
    /// <em>Margin</em> paint the limit line and the margin line. <em>Fail Limit</em> and
    /// <em>Fail Margin</em> paint <em>the trace's own points</em> where they fail — the line keeps
    /// its own colour throughout. Implementing this the other way round, so that a failure recolours
    /// the limit line, is the stated risk of the requirement: it looks plausible on a screenshot and
    /// is useless in practice, because on a display with several limit lines a recoloured line tells
    /// you which line failed but not <em>where</em>, which is the only thing worth knowing.
    /// </para>
    /// <para>
    /// The defaults deliberately match the line each refers to — a failing stretch of trace turns
    /// the colour of the line it broke, which is what makes the association readable when three
    /// limit lines are on screen. They are separate entries so a user who dislikes that can pull
    /// them apart. Because the defaults match, the tests for this requirement set all four to
    /// distinct colours; a test left on the defaults could not tell a correct implementation from
    /// the inverted one.
    /// </para>
    /// </remarks>
    public sealed class LimitColours
    {
        /// <summary>Creates a set with the <c>REQ-UI-023</c> defaults.</summary>
        public LimitColours()
        {
            Limit = new PlotColor(255, 0, 0);
            FailLimit = new PlotColor(255, 0, 0);
            Margin = new PlotColor(255, 255, 0);
            FailMargin = new PlotColor(255, 255, 0);
        }

        /// <summary>The limit line's colour. Default red.</summary>
        public PlotColor Limit { get; set; }

        /// <summary>The margin line's colour. Default yellow.</summary>
        public PlotColor Margin { get; set; }

        /// <summary>The colour of <em>trace</em> points that are past the limit.</summary>
        public PlotColor FailLimit { get; set; }

        /// <summary>The colour of <em>trace</em> points inside the margin.</summary>
        public PlotColor FailMargin { get; set; }

        /// <summary>Whether failing points are recoloured at all.</summary>
        /// <remarks>
        /// Off leaves the trace one colour and the verdict to the pass/fail annotation, which is
        /// what a user comparing several traces' shapes wants; the colouring is an aid, not the
        /// result.
        /// </remarks>
        public bool IndicateFailures { get; set; } = true;

        /// <summary>Whether points inside the margin are recoloured.</summary>
        /// <remarks>
        /// Separate from <see cref="IndicateFailures"/> because a margin is a warning, not a
        /// failure: a test with a generous margin would otherwise paint most of a passing trace.
        /// </remarks>
        public bool IndicateMargin { get; set; } = true;

        /// <summary>The colour a standing calls for, or <c>null</c> to leave the trace alone.</summary>
        /// <param name="standing">How the point stands.</param>
        internal PlotColor? Overlay(LimitStanding standing)
        {
            switch (standing)
            {
                case LimitStanding.BeyondLimit:
                    return IndicateFailures ? FailLimit : (PlotColor?)null;

                case LimitStanding.InsideMargin:
                    return IndicateFailures && IndicateMargin ? FailMargin : (PlotColor?)null;

                default:
                    return null;
            }
        }
    }

    /// <summary>
    /// Works out which trace points a limit test recolours (<c>REQ-UI-023</c>).
    /// </summary>
    /// <remarks>
    /// Deliberately produces a colour per trace point and nothing else. The limit line's own colour
    /// is <see cref="LimitColours.Limit"/> whatever the verdict, so there is no function here that
    /// could return anything else for it — the inverted implementation is not merely untested, it
    /// has nowhere to live.
    /// </remarks>
    public static class LimitShading
    {
        /// <summary>
        /// How each trace point stands against one limit line.
        /// </summary>
        /// <param name="frame">The trace.</param>
        /// <param name="line">The line to test against.</param>
        /// <returns>One standing per trace point.</returns>
        /// <exception cref="ArgumentNullException">An argument is null.</exception>
        /// <remarks>
        /// The comparisons match <see cref="LimitLine.Evaluate"/>'s: the limit is inclusive, so a
        /// point exactly on it is clear, and the margin is applied on the passing side. Frequencies
        /// in a gap between segments have no limit and are therefore clear rather than failing,
        /// which is the same reading <c>Evaluate</c> takes when it declines to count them.
        /// </remarks>
        public static LimitStanding[] Classify(SpectrumFrame frame, LimitLine line)
        {
            if (frame == null)
            {
                throw new ArgumentNullException(nameof(frame));
            }

            if (line == null)
            {
                throw new ArgumentNullException(nameof(line));
            }

            ReadOnlySpan<float> levels = frame.LevelsDbm;
            var standings = new LimitStanding[levels.Length];

            for (int i = 0; i < levels.Length; i++)
            {
                if (float.IsNaN(levels[i]))
                {
                    continue;
                }

                double limit = line.LimitAt(frame.FrequencyAt(i));

                if (double.IsNaN(limit))
                {
                    continue;
                }

                standings[i] = Standing(levels[i], limit, line.MarginDb, line.Side);
            }

            return standings;
        }

        /// <summary>
        /// How each trace point stands against every enabled line of a test, worst case winning.
        /// </summary>
        /// <param name="frame">The trace.</param>
        /// <param name="test">The test.</param>
        /// <returns>One standing per trace point.</returns>
        /// <exception cref="ArgumentNullException">An argument is null.</exception>
        /// <remarks>
        /// A point breaking one line and merely nearing another is coloured for the break. Anything
        /// else would let a lenient line paint over a failure.
        /// </remarks>
        public static LimitStanding[] Classify(SpectrumFrame frame, LimitTest test)
        {
            if (frame == null)
            {
                throw new ArgumentNullException(nameof(frame));
            }

            if (test == null)
            {
                throw new ArgumentNullException(nameof(test));
            }

            var worst = new LimitStanding[frame.LevelsDbm.Length];

            if (!test.IsEnabled)
            {
                return worst;
            }

            foreach (LimitLine line in test.Lines)
            {
                LimitStanding[] standings = Classify(frame, line);

                for (int i = 0; i < worst.Length; i++)
                {
                    if (standings[i] > worst[i])
                    {
                        worst[i] = standings[i];
                    }
                }
            }

            return worst;
        }

        /// <summary>
        /// The colour to draw each trace point in.
        /// </summary>
        /// <param name="standings">How each point stands, from <c>Classify</c>.</param>
        /// <param name="colours">The four limit colours.</param>
        /// <param name="traceColour">The trace's own colour, used wherever it is clear.</param>
        /// <returns>One colour per trace point.</returns>
        /// <exception cref="ArgumentNullException">An argument is null.</exception>
        public static PlotColor[] ShadeTrace(
            IReadOnlyList<LimitStanding> standings, LimitColours colours, PlotColor traceColour)
        {
            if (standings == null)
            {
                throw new ArgumentNullException(nameof(standings));
            }

            if (colours == null)
            {
                throw new ArgumentNullException(nameof(colours));
            }

            var shaded = new PlotColor[standings.Count];

            for (int i = 0; i < shaded.Length; i++)
            {
                shaded[i] = colours.Overlay(standings[i]) ?? traceColour;
            }

            return shaded;
        }

        /// <summary>
        /// The colour to draw each trace point in, against one line.
        /// </summary>
        /// <param name="frame">The trace.</param>
        /// <param name="line">The line to test against.</param>
        /// <param name="colours">The four limit colours.</param>
        /// <param name="traceColour">The trace's own colour.</param>
        /// <exception cref="ArgumentNullException">An argument is null.</exception>
        public static PlotColor[] ShadeTrace(
            SpectrumFrame frame, LimitLine line, LimitColours colours, PlotColor traceColour) =>
            ShadeTrace(Classify(frame, line), colours, traceColour);

        /// <summary>
        /// The colour to draw each trace point in, against a whole test.
        /// </summary>
        /// <param name="frame">The trace.</param>
        /// <param name="test">The test.</param>
        /// <param name="colours">The four limit colours.</param>
        /// <param name="traceColour">The trace's own colour.</param>
        /// <exception cref="ArgumentNullException">An argument is null.</exception>
        public static PlotColor[] ShadeTrace(
            SpectrumFrame frame, LimitTest test, LimitColours colours, PlotColor traceColour) =>
            ShadeTrace(Classify(frame, test), colours, traceColour);

        /// <summary>
        /// The runs of consecutive points sharing a standing, so a renderer can stroke each in one
        /// go rather than a segment at a time.
        /// </summary>
        /// <param name="standings">How each point stands.</param>
        /// <returns>Start index, length and standing for each run, in order.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="standings"/> is null.</exception>
        public static IReadOnlyList<LimitRun> Runs(IReadOnlyList<LimitStanding> standings)
        {
            if (standings == null)
            {
                throw new ArgumentNullException(nameof(standings));
            }

            var runs = new List<LimitRun>();
            int start = 0;

            for (int i = 1; i <= standings.Count; i++)
            {
                if (i < standings.Count && standings[i] == standings[start])
                {
                    continue;
                }

                runs.Add(new LimitRun(start, i - start, standings[start]));
                start = i;
            }

            return runs;
        }

        /// <summary>
        /// Reduces per-point standings to one per pixel column, the worst in each winning.
        /// </summary>
        /// <param name="standings">How each trace point stands.</param>
        /// <param name="columns">Pixel columns across the graticule; must be positive.</param>
        /// <returns>One standing per column.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="standings"/> is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="columns"/> is not positive.</exception>
        /// <remarks>
        /// <para>
        /// The same column boundaries <see cref="TraceDecimator"/> uses, so a column's colour and
        /// the min/max pair drawn in it describe the same points. Deriving them separately is how
        /// the colouring ends up one pixel out from the excursion it refers to.
        /// </para>
        /// <para>
        /// Worst-wins for the same reason the min/max envelope keeps both extrema: on a trace
        /// decimated ten points to a column, a single-point breach that averaged away would be a
        /// failure the display did not show.
        /// </para>
        /// </remarks>
        public static LimitStanding[] ToColumns(IReadOnlyList<LimitStanding> standings, int columns)
        {
            if (standings == null)
            {
                throw new ArgumentNullException(nameof(standings));
            }

            if (columns <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(columns), columns, "Column count must be positive.");
            }

            var byColumn = new LimitStanding[columns];
            int count = standings.Count;

            for (int column = 0; column < columns; column++)
            {
                int start = (int)((long)column * count / columns);
                int end = (int)(((long)column + 1) * count / columns);

                for (int i = start; i < end; i++)
                {
                    if (standings[i] > byColumn[column])
                    {
                        byColumn[column] = standings[i];
                    }
                }
            }

            return byColumn;
        }

        private static LimitStanding Standing(double level, double limit, double margin, LimitSide side)
        {
            // Mirrors LimitLine.Evaluate: the limit is inclusive, and the margin moves the line
            // towards the trace on the passing side.
            if (side == LimitSide.Upper)
            {
                if (level > limit)
                {
                    return LimitStanding.BeyondLimit;
                }

                return level > limit - margin ? LimitStanding.InsideMargin : LimitStanding.Clear;
            }

            if (level < limit)
            {
                return LimitStanding.BeyondLimit;
            }

            return level < limit + margin ? LimitStanding.InsideMargin : LimitStanding.Clear;
        }
    }

    /// <summary>A run of consecutive trace points sharing a standing.</summary>
    public struct LimitRun : IEquatable<LimitRun>
    {
        /// <summary>Creates a run.</summary>
        /// <param name="start">Index of the first point.</param>
        /// <param name="length">How many points.</param>
        /// <param name="standing">How they stand.</param>
        public LimitRun(int start, int length, LimitStanding standing)
        {
            Start = start;
            Length = length;
            Standing = standing;
        }

        /// <summary>Index of the first point.</summary>
        public int Start { get; }

        /// <summary>How many points.</summary>
        public int Length { get; }

        /// <summary>How they stand.</summary>
        public LimitStanding Standing { get; }

        /// <inheritdoc />
        public bool Equals(LimitRun other) =>
            Start == other.Start && Length == other.Length && Standing == other.Standing;

        /// <inheritdoc />
        public override bool Equals(object obj) => obj is LimitRun other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode() =>
            (Start * 397 ^ Length) * 397 ^ (int)Standing;

        /// <summary>Compares two runs.</summary>
        public static bool operator ==(LimitRun left, LimitRun right) => left.Equals(right);

        /// <summary>Compares two runs.</summary>
        public static bool operator !=(LimitRun left, LimitRun right) => !left.Equals(right);
    }
}
