using System;
using System.Text;

namespace OpenVSA.PerformanceGate
{
    /// <summary>
    /// The class of machine a measurement was taken on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>REQ-TST-007</c>: "baselines are stored per machine class, since the targets are stated
    /// for the reference machine, and a run on unrecognised hardware reports that rather than
    /// comparing against an inapplicable baseline." A CI runner is not the reference machine, and
    /// a 15 % gate applied across the two would fire on the hardware rather than on the change.
    /// </para>
    /// <para>
    /// Deliberately coarse: processor name, core count and memory to the nearest gibibyte. Finer
    /// identification — clock speed, microcode revision, the machine's own name — would make every
    /// machine its own class, which is the same as having no baselines at all. Coarser would put
    /// two machines of genuinely different speed in one class, which is worse than no comparison
    /// because it looks like one.
    /// </para>
    /// </remarks>
    public sealed class MachineClass : IEquatable<MachineClass>
    {
        /// <summary>Creates a machine class.</summary>
        /// <param name="processor">The processor's name.</param>
        /// <param name="cores">Logical processor count.</param>
        /// <param name="memoryGib">Physical memory, rounded to whole gibibytes.</param>
        /// <exception cref="ArgumentNullException"><paramref name="processor"/> is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException">A count is not positive.</exception>
        public MachineClass(string processor, int cores, int memoryGib)
        {
            if (processor == null)
            {
                throw new ArgumentNullException(nameof(processor));
            }

            if (cores <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(cores), cores, "Cores must be positive.");
            }

            if (memoryGib <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(memoryGib), memoryGib, "Memory must be positive.");
            }

            Processor = Normalise(processor);
            Cores = cores;
            MemoryGib = memoryGib;
        }

        /// <summary>The processor name, whitespace-normalised.</summary>
        public string Processor { get; }

        /// <summary>Logical processor count.</summary>
        public int Cores { get; }

        /// <summary>Physical memory in whole gibibytes.</summary>
        public int MemoryGib { get; }

        /// <summary>
        /// The key this class is stored under, stable across runs and safe in a text file.
        /// </summary>
        public string Key => Processor + " | " + Cores + "c | " + MemoryGib + "GiB";

        /// <summary>Parses a key produced by <see cref="Key"/>.</summary>
        /// <param name="key">The key.</param>
        /// <returns>The class, or <c>null</c> when the key is not one this wrote.</returns>
        public static MachineClass Parse(string key)
        {
            if (key == null)
            {
                return null;
            }

            string[] parts = key.Split('|');

            if (parts.Length != 3)
            {
                return null;
            }

            int cores;
            int memory;

            if (!int.TryParse(parts[1].Trim().TrimEnd('c'), out cores) ||
                !int.TryParse(parts[2].Trim().Replace("GiB", string.Empty).Trim(), out memory))
            {
                return null;
            }

            string processor = parts[0].Trim();

            if (processor.Length == 0 || cores <= 0 || memory <= 0)
            {
                return null;
            }

            return new MachineClass(processor, cores, memory);
        }

        /// <summary>Collapses runs of whitespace, so two spellings of one processor agree.</summary>
        private static string Normalise(string text)
        {
            var builder = new StringBuilder(text.Length);
            bool space = false;

            foreach (char c in text.Trim())
            {
                // '|' is the key's separator, so it cannot appear inside a field or Parse would
                // split a processor name in half and silently produce a different class.
                char safe = c == '|' ? '/' : c;

                if (char.IsWhiteSpace(safe))
                {
                    space = true;
                    continue;
                }

                if (space && builder.Length > 0)
                {
                    builder.Append(' ');
                }

                space = false;
                builder.Append(safe);
            }

            return builder.ToString();
        }

        /// <inheritdoc />
        public bool Equals(MachineClass other) =>
            other != null &&
            string.Equals(Processor, other.Processor, StringComparison.OrdinalIgnoreCase) &&
            Cores == other.Cores &&
            MemoryGib == other.MemoryGib;

        /// <inheritdoc />
        public override bool Equals(object obj) => Equals(obj as MachineClass);

        /// <inheritdoc />
        public override int GetHashCode() =>
            StringComparer.OrdinalIgnoreCase.GetHashCode(Processor) ^ (Cores * 397) ^ MemoryGib;

        /// <inheritdoc />
        public override string ToString() => Key;
    }
}
