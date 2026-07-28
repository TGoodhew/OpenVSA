using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace OpenVSA.PerformanceGate
{
    /// <summary>
    /// The measurements one run produced, written by the benchmark host and read by the gate.
    /// </summary>
    /// <remarks>
    /// A file rather than an in-process hand-off, because the two halves genuinely have to be
    /// separate processes: BenchmarkDotNet launches its own optimised child to measure in, and the
    /// windowed targets need a real message loop. The file is also the thing to attach to a build
    /// when a gate fires — a verdict with no figures behind it cannot be argued with.
    /// </remarks>
    public static class MeasurementFile
    {
        private const string Header = "benchmark\tmean\tstddev\tsamples";

        /// <summary>Renders measurements as tab-separated text.</summary>
        /// <param name="measurements">The measurements.</param>
        /// <exception cref="ArgumentNullException"><paramref name="measurements"/> is null.</exception>
        public static string Write(IEnumerable<TargetMeasurement> measurements)
        {
            if (measurements == null)
            {
                throw new ArgumentNullException(nameof(measurements));
            }

            var builder = new StringBuilder();
            builder.Append("# REQ-TST-007 run measurements.\n").Append(Header).Append('\n');

            foreach (TargetMeasurement m in measurements)
            {
                builder
                    .Append(m.Name).Append('\t')
                    .Append(m.Mean.ToString("G9", CultureInfo.InvariantCulture)).Append('\t')
                    .Append(m.StandardDeviation.ToString("G9", CultureInfo.InvariantCulture)).Append('\t')
                    .Append(m.SampleCount.ToString(CultureInfo.InvariantCulture))
                    .Append('\n');
            }

            return builder.ToString();
        }

        /// <summary>Reads measurements from tab-separated text.</summary>
        /// <param name="text">The text; empty or null yields none.</param>
        /// <exception cref="FormatException">A row is malformed.</exception>
        public static IList<TargetMeasurement> Read(string text)
        {
            var results = new List<TargetMeasurement>();

            if (string.IsNullOrEmpty(text))
            {
                return results;
            }

            string[] lines = text.Replace("\r\n", "\n").Split('\n');

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];

                if (line.Length == 0 || line[0] == '#' ||
                    line.StartsWith("benchmark\t", StringComparison.Ordinal))
                {
                    continue;
                }

                string[] cells = line.Split('\t');

                if (cells.Length < 4)
                {
                    throw new FormatException(
                        "Measurement line " + (i + 1) + " has " + cells.Length + " columns, expected 4.");
                }

                double mean;
                double deviation;
                int samples;

                if (!double.TryParse(cells[1], NumberStyles.Float, CultureInfo.InvariantCulture, out mean) ||
                    !double.TryParse(cells[2], NumberStyles.Float, CultureInfo.InvariantCulture, out deviation) ||
                    !int.TryParse(cells[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out samples))
                {
                    throw new FormatException("Measurement line " + (i + 1) + " has an unreadable number.");
                }

                results.Add(new TargetMeasurement(cells[0], mean, deviation, samples));
            }

            return results;
        }

        /// <summary>Reads a file, or nothing when there is none.</summary>
        /// <param name="path">The path.</param>
        /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
        public static IList<TargetMeasurement> ReadFile(string path)
        {
            if (path == null)
            {
                throw new ArgumentNullException(nameof(path));
            }

            return File.Exists(path)
                ? Read(File.ReadAllText(path, Encoding.UTF8))
                : new List<TargetMeasurement>();
        }
    }
}
