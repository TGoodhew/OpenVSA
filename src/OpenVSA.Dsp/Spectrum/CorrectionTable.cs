using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using OpenVSA.Core;

namespace OpenVSA.Dsp.Spectrum
{
    /// <summary>One entry of a frequency-response correction: a frequency and a complex gain.</summary>
    public struct CorrectionPoint
    {
        /// <summary>Creates a point.</summary>
        /// <param name="frequencyHz">Frequency, in hertz.</param>
        /// <param name="magnitudeDb">Magnitude, in dB.</param>
        /// <param name="phaseDegrees">Phase, in degrees.</param>
        public CorrectionPoint(double frequencyHz, double magnitudeDb, double phaseDegrees = 0.0)
        {
            FrequencyHz = frequencyHz;
            MagnitudeDb = magnitudeDb;
            PhaseDegrees = phaseDegrees;
        }

        /// <summary>Frequency, in hertz.</summary>
        public double FrequencyHz { get; }

        /// <summary>Magnitude, in dB.</summary>
        public double MagnitudeDb { get; }

        /// <summary>Phase, in degrees.</summary>
        public double PhaseDegrees { get; }

        /// <inheritdoc />
        public override string ToString() =>
            (FrequencyHz / 1e6).ToString("0.###", CultureInfo.CurrentCulture) + " MHz: " +
            MagnitudeDb.ToString("+0.00;-0.00", CultureInfo.CurrentCulture) + " dB, " +
            PhaseDegrees.ToString("+0.0;-0.0", CultureInfo.CurrentCulture) + "°";
    }

    /// <summary>
    /// A frequency-response correction table (<c>REQ-AMP-003</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Magnitude and phase, not magnitude alone.</strong> A cable's loss is mostly
    /// magnitude and its delay is entirely phase, and a correction that carried only the first
    /// would leave the second in the measurement — which for a modulated signal is the part that
    /// shows up as error rather than as level. <c>REQ-AMP-004</c>'s de-embedding is the same table
    /// applied the other way round, which is why both live here.
    /// </para>
    /// <para>
    /// <strong>Interpolated in dB and degrees, not in volts.</strong> A cable loss quoted at
    /// 1 GHz and 2 GHz varies smoothly in decibels and not in voltage ratio, so interpolating the
    /// linear values would bend the curve between the points that were actually measured.
    /// </para>
    /// <para>
    /// <strong>Tables combine by addition</strong> — cable loss plus antenna factor plus fixture —
    /// which is what the requirement means by combinable, and is exact rather than approximate
    /// because decibels and degrees both add.
    /// </para>
    /// <para>
    /// Immutable once built, so a correction cannot change between the trace it was applied to and
    /// the annotation that says it was.
    /// </para>
    /// </remarks>
    public sealed class CorrectionTable
    {
        private readonly CorrectionPoint[] _points;

        /// <summary>Creates a table from points, which are sorted by frequency.</summary>
        /// <param name="name">What this correction is, for the annotation.</param>
        /// <param name="points">The points; at least one.</param>
        /// <exception cref="ArgumentNullException"><paramref name="points"/> is null.</exception>
        /// <exception cref="ArgumentException">There are no points, or two share a frequency.</exception>
        public CorrectionTable(string name, IEnumerable<CorrectionPoint> points)
        {
            if (points == null)
            {
                throw new ArgumentNullException(nameof(points));
            }

            _points = points.OrderBy(p => p.FrequencyHz).ToArray();

            if (_points.Length == 0)
            {
                throw new ArgumentException(
                    "A correction table needs at least one point.", nameof(points));
            }

            for (int i = 1; i < _points.Length; i++)
            {
                if (_points[i].FrequencyHz == _points[i - 1].FrequencyHz)
                {
                    throw new ArgumentException(
                        "Two points share the frequency " +
                        _points[i].FrequencyHz.ToString("G9", CultureInfo.CurrentCulture) +
                        " Hz, so the correction there is ambiguous.",
                        nameof(points));
                }
            }

            Name = name ?? string.Empty;
        }

        /// <summary>What this correction is, for the annotation.</summary>
        public string Name { get; }

        /// <summary>The points, in ascending frequency.</summary>
        public IReadOnlyList<CorrectionPoint> Points => _points;

        /// <summary>Lowest frequency the table states, in hertz.</summary>
        public double StartFrequencyHz => _points[0].FrequencyHz;

        /// <summary>Highest frequency the table states, in hertz.</summary>
        public double StopFrequencyHz => _points[_points.Length - 1].FrequencyHz;

        /// <summary>
        /// The correction at a frequency, interpolated linearly between the stated points.
        /// </summary>
        /// <param name="frequencyHz">The frequency.</param>
        /// <returns>Magnitude in dB and phase in degrees.</returns>
        /// <remarks>
        /// Held flat outside the stated range rather than extrapolated. Extrapolating a cable loss
        /// beyond where it was measured invents a number that looks like a measurement; holding
        /// the end value is visibly a limit of the table.
        /// </remarks>
        public CorrectionPoint At(double frequencyHz)
        {
            if (_points.Length == 1 || frequencyHz <= StartFrequencyHz)
            {
                return new CorrectionPoint(
                    frequencyHz, _points[0].MagnitudeDb, _points[0].PhaseDegrees);
            }

            if (frequencyHz >= StopFrequencyHz)
            {
                CorrectionPoint last = _points[_points.Length - 1];
                return new CorrectionPoint(frequencyHz, last.MagnitudeDb, last.PhaseDegrees);
            }

            int upper = 1;

            while (upper < _points.Length && _points[upper].FrequencyHz < frequencyHz)
            {
                upper++;
            }

            CorrectionPoint below = _points[upper - 1];
            CorrectionPoint above = _points[upper];

            double span = above.FrequencyHz - below.FrequencyHz;
            double t = span > 0.0 ? (frequencyHz - below.FrequencyHz) / span : 0.0;

            return new CorrectionPoint(
                frequencyHz,
                below.MagnitudeDb + t * (above.MagnitudeDb - below.MagnitudeDb),
                below.PhaseDegrees + t * (above.PhaseDegrees - below.PhaseDegrees));
        }

        /// <summary>
        /// Combines this table with another, adding both responses.
        /// </summary>
        /// <param name="other">The table to add.</param>
        /// <returns>A table stating the sum at the union of both frequency grids.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="other"/> is null.</exception>
        /// <remarks>
        /// Stated on the union of the two grids, so neither table's detail is lost to the other's
        /// coarser spacing — a cable loss given every 100 MHz combined with an antenna factor given
        /// every 10 MHz keeps the 10 MHz detail.
        /// </remarks>
        public CorrectionTable CombinedWith(CorrectionTable other)
        {
            if (other == null)
            {
                throw new ArgumentNullException(nameof(other));
            }

            IEnumerable<double> grid = _points.Select(p => p.FrequencyHz)
                .Concat(other._points.Select(p => p.FrequencyHz))
                .Distinct()
                .OrderBy(f => f);

            var combined = new List<CorrectionPoint>();

            foreach (double frequency in grid)
            {
                CorrectionPoint mine = At(frequency);
                CorrectionPoint theirs = other.At(frequency);

                combined.Add(new CorrectionPoint(
                    frequency,
                    mine.MagnitudeDb + theirs.MagnitudeDb,
                    mine.PhaseDegrees + theirs.PhaseDegrees));
            }

            return new CorrectionTable(
                string.IsNullOrEmpty(Name) ? other.Name : Name + " + " + other.Name,
                combined);
        }

        /// <summary>Returns a table whose response is the negative of this one's.</summary>
        /// <remarks>
        /// What turns a measured fixture response into the correction that removes it
        /// (<c>REQ-AMP-004</c>). Negating both magnitude and phase, because a de-embedding that
        /// negated only the first would leave the fixture's delay in the result.
        /// </remarks>
        public CorrectionTable Inverted() =>
            new CorrectionTable(
                Name.Length == 0 ? "inverted" : "inverse of " + Name,
                _points.Select(p => new CorrectionPoint(
                    p.FrequencyHz, -p.MagnitudeDb, -p.PhaseDegrees)));

        /// <summary>
        /// Reads a table from text: one point per line, <c>frequency, magnitude dB, phase degrees</c>.
        /// </summary>
        /// <param name="name">What this correction is.</param>
        /// <param name="text">The file's contents.</param>
        /// <returns>The table.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="text"/> is null.</exception>
        /// <exception cref="FormatException">A line is not a point.</exception>
        /// <remarks>
        /// Blank lines and lines beginning <c>#</c> or <c>!</c> are ignored, so a table can carry
        /// its provenance — which cable, measured when, on what — beside the numbers. Phase is
        /// optional and defaults to zero, because a magnitude-only table (an antenna factor, say)
        /// is a legitimate and common thing to have.
        /// </remarks>
        public static CorrectionTable Parse(string name, string text)
        {
            if (text == null)
            {
                throw new ArgumentNullException(nameof(text));
            }

            var points = new List<CorrectionPoint>();
            string[] lines = text.Split('\n');

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();

                if (line.Length == 0 || line[0] == '#' || line[0] == '!')
                {
                    continue;
                }

                string[] fields = line.Split(new[] { ',', '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries);

                if (fields.Length < 2)
                {
                    throw new FormatException(
                        "Line " + (i + 1) + " of '" + name + "' is '" + line +
                        "'; a point needs at least a frequency and a magnitude in dB.");
                }

                double frequency = Number(fields[0], i + 1, name);
                double magnitude = Number(fields[1], i + 1, name);
                double phase = fields.Length > 2 ? Number(fields[2], i + 1, name) : 0.0;

                points.Add(new CorrectionPoint(frequency, magnitude, phase));
            }

            if (points.Count == 0)
            {
                throw new FormatException("'" + name + "' holds no points.");
            }

            return new CorrectionTable(name, points);
        }

        /// <summary>Reads a table from a file.</summary>
        /// <param name="path">The file.</param>
        /// <returns>The table, named after the file.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="path"/> is null or empty.</exception>
        /// <exception cref="FormatException">A line is not a point.</exception>
        public static CorrectionTable Load(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                throw new ArgumentNullException(nameof(path));
            }

            return Parse(Path.GetFileNameWithoutExtension(path), File.ReadAllText(path));
        }

        /// <inheritdoc />
        public override string ToString() =>
            (Name.Length == 0 ? "correction" : Name) + ", " +
            _points.Length.ToString(CultureInfo.CurrentCulture) + " points from " +
            (StartFrequencyHz / 1e6).ToString("0.###", CultureInfo.CurrentCulture) + " to " +
            (StopFrequencyHz / 1e6).ToString("0.###", CultureInfo.CurrentCulture) + " MHz";

        private static double Number(string field, int line, string name)
        {
            double value;

            if (!double.TryParse(
                    field, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            {
                throw new FormatException(
                    "Line " + line + " of '" + name + "' has '" + field +
                    "' where a number was expected.");
            }

            return value;
        }
    }

    /// <summary>
    /// Applies a correction table to a spectrum (<c>REQ-AMP-003</c>, <c>REQ-AMP-004</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Correction and de-embedding are the same operation in opposite directions:
    /// <see cref="Apply"/> multiplies the measured spectrum by a response, and
    /// <see cref="Remove"/> divides it by one. A fixture is removed; a cable loss is applied as
    /// its own inverse. Writing them as one pair rather than two features is what stops them
    /// drifting into disagreement about the sign of the phase.
    /// </para>
    /// <para>
    /// <strong>Complex throughout.</strong> <c>REQ-AMP-004</c> is explicit that de-embedding is
    /// not magnitude-only, and the reason is that a fixture flat in magnitude can still be far from
    /// flat in phase — which leaves the level right and the demodulation wrong.
    /// </para>
    /// </remarks>
    public static class Corrections
    {
        /// <summary>
        /// Multiplies a spectrum by a response.
        /// </summary>
        /// <param name="frame">The spectrum.</param>
        /// <param name="table">The response to apply.</param>
        /// <returns>A new frame on the same axis.</returns>
        /// <exception cref="ArgumentNullException">An argument is null.</exception>
        public static SpectrumFrame Apply(SpectrumFrame frame, CorrectionTable table) =>
            Combine(frame, table, remove: false);

        /// <summary>
        /// Divides a spectrum by a response — de-embedding (<c>REQ-AMP-004</c>).
        /// </summary>
        /// <param name="frame">The spectrum.</param>
        /// <param name="table">The fixture response to remove.</param>
        /// <returns>A new frame on the same axis.</returns>
        /// <exception cref="ArgumentNullException">An argument is null.</exception>
        public static SpectrumFrame Remove(SpectrumFrame frame, CorrectionTable table) =>
            Combine(frame, table, remove: true);

        /// <summary>
        /// The correction across a frame's axis, as a trace in its own right.
        /// </summary>
        /// <param name="frame">The frame whose axis to state it on.</param>
        /// <param name="table">The response.</param>
        /// <returns>A frame carrying the response, for the Correction data type.</returns>
        /// <exception cref="ArgumentNullException">An argument is null.</exception>
        /// <remarks>
        /// <c>REQ-DSP-040</c>'s Correction data type: what is being applied, drawn beside what it
        /// was applied to. A correction nobody can see is one nobody can check.
        /// </remarks>
        public static SpectrumFrame AsTrace(SpectrumFrame frame, CorrectionTable table)
        {
            if (frame == null)
            {
                throw new ArgumentNullException(nameof(frame));
            }

            if (table == null)
            {
                throw new ArgumentNullException(nameof(table));
            }

            var response = new float[frame.PointCount * 2];

            for (int i = 0; i < frame.PointCount; i++)
            {
                CorrectionPoint point = table.At(frame.FrequencyAt(i));
                double gain = Math.Pow(10.0, point.MagnitudeDb / 20.0);
                double radians = point.PhaseDegrees * Math.PI / 180.0;

                response[i * 2] = (float)(gain * Math.Cos(radians));
                response[i * 2 + 1] = (float)(gain * Math.Sin(radians));
            }

            return SpectrumFrame.FromComplex(
                response,
                frame.StartFrequencyHz,
                frame.BinWidthHz,
                frame.Window,
                frame.EquivalentNoiseBandwidthBins);
        }

        private static SpectrumFrame Combine(
            SpectrumFrame frame, CorrectionTable table, bool remove)
        {
            if (frame == null)
            {
                throw new ArgumentNullException(nameof(frame));
            }

            if (table == null)
            {
                throw new ArgumentNullException(nameof(table));
            }

            ReadOnlySpan<float> source = frame.Complex;
            var result = new float[source.Length];

            for (int i = 0; i < frame.PointCount; i++)
            {
                CorrectionPoint point = table.At(frame.FrequencyAt(i));

                double db = remove ? -point.MagnitudeDb : point.MagnitudeDb;
                double degrees = remove ? -point.PhaseDegrees : point.PhaseDegrees;

                double gain = Math.Pow(10.0, db / 20.0);
                double radians = degrees * Math.PI / 180.0;

                double cos = gain * Math.Cos(radians);
                double sin = gain * Math.Sin(radians);

                double re = source[i * 2];
                double im = source[i * 2 + 1];

                result[i * 2] = (float)(re * cos - im * sin);
                result[i * 2 + 1] = (float)(re * sin + im * cos);
            }

            return frame.WithComplex(
                result, frame.HasPhase, frame.AverageCount, frame.EffectiveAverageCount);
        }
    }
}
