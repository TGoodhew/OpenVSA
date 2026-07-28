using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace OpenVSA.PerformanceGate
{
    /// <summary>One stored figure: what a target measured on a machine class, and when.</summary>
    public sealed class BaselineEntry
    {
        /// <summary>Creates an entry.</summary>
        /// <param name="machine">The machine class it was measured on.</param>
        /// <param name="name">The benchmark name.</param>
        /// <param name="mean">The mean, in the target's unit.</param>
        /// <param name="relativeResolution">The run's resolving power when it was stored.</param>
        /// <param name="recorded">When it was recorded, in UTC.</param>
        /// <param name="commit">The commit the figure came from, or empty.</param>
        /// <exception cref="ArgumentNullException">A required argument is null.</exception>
        public BaselineEntry(
            MachineClass machine,
            string name,
            double mean,
            double relativeResolution,
            DateTime recorded,
            string commit)
        {
            if (machine == null)
            {
                throw new ArgumentNullException(nameof(machine));
            }

            if (name == null)
            {
                throw new ArgumentNullException(nameof(name));
            }

            Machine = machine;
            Name = name;
            Mean = mean;
            RelativeResolution = relativeResolution;
            Recorded = recorded;
            Commit = commit ?? string.Empty;
        }

        /// <summary>The machine class.</summary>
        public MachineClass Machine { get; }

        /// <summary>The benchmark name.</summary>
        public string Name { get; }

        /// <summary>The stored mean.</summary>
        public double Mean { get; }

        /// <summary>How well the storing run could resolve a change.</summary>
        /// <remarks>
        /// Carried so a baseline taken on a noisy machine can be recognised as one. A gate that
        /// compares a quiet run against a noisy baseline is measuring the baseline's noise.
        /// </remarks>
        public double RelativeResolution { get; }

        /// <summary>When the figure was recorded, in UTC.</summary>
        public DateTime Recorded { get; }

        /// <summary>The commit it was taken at, for tracing a shift back to a change.</summary>
        public string Commit { get; }
    }

    /// <summary>
    /// The stored baselines, one file, keyed by machine class and benchmark name.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Tab-separated text rather than JSON or a binary format, and checked in. A baseline is a
    /// claim about how fast the product is; it is reviewed in a pull request like any other claim,
    /// and a diff that reads <c>60.4 -&gt; 51.2</c> on one line is reviewable where a re-serialised
    /// JSON document is not.
    /// </para>
    /// <para>
    /// No third-party serialiser, which also keeps <c>REQ-NFR-008</c>'s dependency register from
    /// growing an entry for the sake of six columns.
    /// </para>
    /// </remarks>
    public sealed class BaselineStore
    {
        private const string Header =
            "machine\tbenchmark\tmean\tresolution\trecorded\tcommit";

        private readonly Dictionary<string, BaselineEntry> _entries =
            new Dictionary<string, BaselineEntry>(StringComparer.OrdinalIgnoreCase);

        /// <summary>How many figures are stored.</summary>
        public int Count => _entries.Count;

        /// <summary>Every machine class the store holds a figure for.</summary>
        public IEnumerable<MachineClass> Machines
        {
            get
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (BaselineEntry entry in _entries.Values)
                {
                    if (seen.Add(entry.Machine.Key))
                    {
                        yield return entry.Machine;
                    }
                }
            }
        }

        /// <summary>Whether the store holds any figure for a machine class.</summary>
        /// <param name="machine">The machine class.</param>
        /// <remarks>
        /// The question <c>REQ-TST-007</c> asks before comparing anything: an unrecognised machine
        /// is reported, not measured against somebody else's hardware.
        /// </remarks>
        public bool Recognises(MachineClass machine)
        {
            if (machine == null)
            {
                return false;
            }

            foreach (BaselineEntry entry in _entries.Values)
            {
                if (entry.Machine.Equals(machine))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>The stored figure for a target on a machine, or <c>null</c>.</summary>
        /// <param name="machine">The machine class.</param>
        /// <param name="name">The benchmark name.</param>
        public BaselineEntry Find(MachineClass machine, string name)
        {
            if (machine == null || name == null)
            {
                return null;
            }

            BaselineEntry entry;
            return _entries.TryGetValue(KeyOf(machine, name), out entry) ? entry : null;
        }

        /// <summary>Adds or replaces a figure.</summary>
        /// <param name="entry">The figure.</param>
        /// <exception cref="ArgumentNullException"><paramref name="entry"/> is null.</exception>
        public void Set(BaselineEntry entry)
        {
            if (entry == null)
            {
                throw new ArgumentNullException(nameof(entry));
            }

            _entries[KeyOf(entry.Machine, entry.Name)] = entry;
        }

        /// <summary>Reads a store from tab-separated text.</summary>
        /// <param name="text">The file's contents; empty or null yields an empty store.</param>
        /// <exception cref="FormatException">A row is malformed.</exception>
        public static BaselineStore Read(string text)
        {
            var store = new BaselineStore();

            if (string.IsNullOrEmpty(text))
            {
                return store;
            }

            string[] lines = text.Replace("\r\n", "\n").Split('\n');

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];

                if (line.Length == 0 || line[0] == '#' || line.StartsWith("machine\t", StringComparison.Ordinal))
                {
                    continue;
                }

                string[] cells = line.Split('\t');

                if (cells.Length < 6)
                {
                    throw new FormatException(
                        "Baseline line " + (i + 1) + " has " + cells.Length + " columns, expected 6.");
                }

                MachineClass machine = MachineClass.Parse(cells[0]);

                if (machine == null)
                {
                    throw new FormatException(
                        "Baseline line " + (i + 1) + " has an unreadable machine key: " + cells[0]);
                }

                double mean;
                double resolution;
                DateTime recorded;

                if (!double.TryParse(cells[2], NumberStyles.Float, CultureInfo.InvariantCulture, out mean) ||
                    !double.TryParse(cells[3], NumberStyles.Float, CultureInfo.InvariantCulture, out resolution) ||
                    !DateTime.TryParse(cells[4], CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out recorded))
                {
                    throw new FormatException("Baseline line " + (i + 1) + " has an unreadable number or date.");
                }

                store.Set(new BaselineEntry(machine, cells[1], mean, resolution, recorded, cells[5]));
            }

            return store;
        }

        /// <summary>Reads a store from a file, or an empty store when there is none.</summary>
        /// <param name="path">The file path.</param>
        /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
        public static BaselineStore ReadFile(string path)
        {
            if (path == null)
            {
                throw new ArgumentNullException(nameof(path));
            }

            return File.Exists(path)
                ? Read(File.ReadAllText(path, Encoding.UTF8))
                : new BaselineStore();
        }

        /// <summary>Renders the store as tab-separated text, in a stable order.</summary>
        /// <remarks>
        /// Sorted by machine then benchmark so a re-write produces a diff of the lines that
        /// actually changed, rather than a reordering that hides them.
        /// </remarks>
        public string Write()
        {
            var keys = new List<string>(_entries.Keys);
            keys.Sort(StringComparer.OrdinalIgnoreCase);

            var builder = new StringBuilder();
            builder.Append("# REQ-TST-007 performance baselines. One row per machine class and benchmark.\n");
            builder.Append("# A change here is a claim about how fast OpenVSA is; review it as one.\n");
            builder.Append(Header).Append('\n');

            foreach (string key in keys)
            {
                BaselineEntry e = _entries[key];

                builder
                    .Append(e.Machine.Key).Append('\t')
                    .Append(e.Name).Append('\t')
                    .Append(e.Mean.ToString("G9", CultureInfo.InvariantCulture)).Append('\t')
                    .Append(e.RelativeResolution.ToString("G6", CultureInfo.InvariantCulture)).Append('\t')
                    .Append(e.Recorded.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)).Append('\t')
                    .Append(e.Commit)
                    .Append('\n');
            }

            return builder.ToString();
        }

        private static string KeyOf(MachineClass machine, string name) => machine.Key + " " + name;
    }
}
