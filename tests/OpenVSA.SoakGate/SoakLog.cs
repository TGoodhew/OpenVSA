using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace OpenVSA.SoakGate
{
    /// <summary>
    /// The samples a soak took, as tab-separated text.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Written a line at a time as the run goes, and flushed, so a run that dies at hour seven has
    /// left seven hours of evidence rather than nothing. That is the difference between a soak that
    /// can be diagnosed and one that has to be repeated overnight.
    /// </para>
    /// <para>
    /// Text rather than a binary format for the same reason <c>MeasurementFile</c> is: the file is
    /// the thing to look at when a claim fails, and a verdict with no figures behind it cannot be
    /// argued with.
    /// </para>
    /// </remarks>
    public static class SoakLog
    {
        private const string Header =
            "elapsed_s\tmanaged_b\tcollected_b\tprivate_b\thandles\tgdi\tuser\t" +
            "frames_drawn\tframes_dropped\tpooled_buffers\tpooled_b\ttraces\tcycles";

        /// <summary>The header lines a log begins with.</summary>
        /// <param name="note">A line describing the run, without a leading hash.</param>
        public static string Preamble(string note) =>
            "# REQ-TST-009 soak samples. " + (note ?? string.Empty) + "\n" + Header + "\n";

        /// <summary>Renders one sample as a line, newline included.</summary>
        /// <param name="sample">The sample.</param>
        /// <exception cref="ArgumentNullException"><paramref name="sample"/> is null.</exception>
        public static string Line(SoakSample sample)
        {
            if (sample == null)
            {
                throw new ArgumentNullException(nameof(sample));
            }

            var text = new StringBuilder();

            text.Append(sample.ElapsedSeconds.ToString("0.###", CultureInfo.InvariantCulture)).Append('\t')
                .Append(sample.ManagedBytes.ToString(CultureInfo.InvariantCulture)).Append('\t')
                .Append(sample.CollectedManagedBytes.ToString(CultureInfo.InvariantCulture)).Append('\t')
                .Append(sample.PrivateBytes.ToString(CultureInfo.InvariantCulture)).Append('\t')
                .Append(sample.Handles.ToString(CultureInfo.InvariantCulture)).Append('\t')
                .Append(sample.GdiObjects.ToString(CultureInfo.InvariantCulture)).Append('\t')
                .Append(sample.UserObjects.ToString(CultureInfo.InvariantCulture)).Append('\t')
                .Append(sample.FramesDrawn.ToString(CultureInfo.InvariantCulture)).Append('\t')
                .Append(sample.FramesDropped.ToString(CultureInfo.InvariantCulture)).Append('\t')
                .Append(sample.PooledBuffers.ToString(CultureInfo.InvariantCulture)).Append('\t')
                .Append(sample.PooledBytes.ToString(CultureInfo.InvariantCulture)).Append('\t')
                .Append(sample.TracesOpen.ToString(CultureInfo.InvariantCulture)).Append('\t')
                .Append(sample.Cycles.ToString(CultureInfo.InvariantCulture)).Append('\n');

            return text.ToString();
        }

        /// <summary>Renders a whole log.</summary>
        /// <param name="samples">The samples.</param>
        /// <param name="note">A line describing the run.</param>
        /// <exception cref="ArgumentNullException"><paramref name="samples"/> is null.</exception>
        public static string Write(IEnumerable<SoakSample> samples, string note = null)
        {
            if (samples == null)
            {
                throw new ArgumentNullException(nameof(samples));
            }

            var text = new StringBuilder(Preamble(note));

            foreach (SoakSample sample in samples)
            {
                text.Append(Line(sample));
            }

            return text.ToString();
        }

        /// <summary>Reads a log.</summary>
        /// <param name="text">The text; empty or null yields no samples.</param>
        /// <exception cref="FormatException">A row is malformed.</exception>
        /// <remarks>
        /// A truncated final line is dropped rather than thrown on: the host appends as it goes, so a
        /// run killed mid-write leaves one, and refusing to read the file would throw away the whole
        /// night over the last three seconds of it.
        /// </remarks>
        public static IList<SoakSample> Read(string text)
        {
            var samples = new List<SoakSample>();

            if (string.IsNullOrEmpty(text))
            {
                return samples;
            }

            string[] lines = text.Replace("\r\n", "\n").Split('\n');

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];

                if (line.Length == 0 || line[0] == '#' ||
                    line.StartsWith("elapsed_s\t", StringComparison.Ordinal))
                {
                    continue;
                }

                string[] cells = line.Split('\t');

                if (cells.Length < 13)
                {
                    // The last line of a killed run.
                    if (i == lines.Length - 1 || lines[i + 1].Length == 0)
                    {
                        continue;
                    }

                    throw new FormatException(
                        "Soak sample on line " + (i + 1) + " has " + cells.Length +
                        " columns, expected 13.");
                }

                samples.Add(new SoakSample(
                    Number(cells[0], i),
                    Whole(cells[1], i),
                    Whole(cells[2], i),
                    Whole(cells[3], i),
                    (int)Whole(cells[4], i),
                    (int)Whole(cells[5], i),
                    (int)Whole(cells[6], i),
                    Whole(cells[7], i),
                    Whole(cells[8], i),
                    (int)Whole(cells[9], i),
                    Whole(cells[10], i),
                    (int)Whole(cells[11], i),
                    (int)Whole(cells[12], i)));
            }

            return samples;
        }

        /// <summary>Reads a file, or nothing when there is none.</summary>
        /// <param name="path">The path.</param>
        /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
        public static IList<SoakSample> ReadFile(string path)
        {
            if (path == null)
            {
                throw new ArgumentNullException(nameof(path));
            }

            return File.Exists(path)
                ? Read(File.ReadAllText(path, Encoding.UTF8))
                : new List<SoakSample>();
        }

        private static double Number(string cell, int line)
        {
            double value;

            if (!double.TryParse(cell, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            {
                throw new FormatException(
                    "Soak sample on line " + (line + 1) + " has an unreadable number '" + cell + "'.");
            }

            return value;
        }

        private static long Whole(string cell, int line)
        {
            long value;

            if (!long.TryParse(cell, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
            {
                throw new FormatException(
                    "Soak sample on line " + (line + 1) + " has an unreadable integer '" + cell + "'.");
            }

            return value;
        }
    }
}
