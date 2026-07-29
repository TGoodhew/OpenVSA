using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;

namespace OpenVSA.Core.Diagnostics
{
    /// <summary>How much a subsystem says.</summary>
    public enum LogLevel
    {
        /// <summary>Nothing.</summary>
        Off = 0,

        /// <summary>Something went wrong and the operation did not complete.</summary>
        Error,

        /// <summary>Something is not as expected but the operation continued.</summary>
        Warning,

        /// <summary>A normal event worth recording.</summary>
        Information,

        /// <summary>Detail for diagnosis, off by default.</summary>
        Debug,
    }

    /// <summary>One structured log entry.</summary>
    /// <remarks>
    /// <strong>Fields, not a formatted sentence.</strong> <c>REQ-NFR-034</c> requires entries a
    /// support bundle can be <em>queried</em> rather than read, and a message built by string
    /// concatenation cannot be filtered by the values inside it — the moment a rate is written into
    /// prose, "show me every entry where the rate fell below 2 kB/s" stops being answerable.
    /// </remarks>
    public sealed class LogEntry
    {
        /// <summary>Creates an entry.</summary>
        /// <param name="utc">When it happened.</param>
        /// <param name="subsystem">Which subsystem emitted it.</param>
        /// <param name="level">Its level.</param>
        /// <param name="message">A short constant description; values go in <paramref name="fields"/>.</param>
        /// <param name="fields">Named values, machine-parseable.</param>
        /// <exception cref="ArgumentNullException">A required argument is null.</exception>
        public LogEntry(
            DateTime utc,
            string subsystem,
            LogLevel level,
            string message,
            IReadOnlyDictionary<string, object> fields)
        {
            Utc = utc;
            Subsystem = subsystem ?? throw new ArgumentNullException(nameof(subsystem));
            Level = level;
            Message = message ?? string.Empty;
            Fields = fields ?? new Dictionary<string, object>();
        }

        /// <summary>When it happened.</summary>
        public DateTime Utc { get; }

        /// <summary>Which subsystem emitted it.</summary>
        public string Subsystem { get; }

        /// <summary>Its level.</summary>
        public LogLevel Level { get; }

        /// <summary>A short constant description.</summary>
        public string Message { get; }

        /// <summary>Named values.</summary>
        public IReadOnlyDictionary<string, object> Fields { get; }

        /// <summary>The entry as one tab-separated line, fields as name=value.</summary>
        public string ToLine()
        {
            var builder = new StringBuilder();

            builder
                .Append(Utc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture))
                .Append('\t').Append(Level)
                .Append('\t').Append(Subsystem)
                .Append('\t').Append(Message);

            foreach (KeyValuePair<string, object> field in Fields.OrderBy(f => f.Key, StringComparer.Ordinal))
            {
                builder.Append('\t').Append(field.Key).Append('=')
                    .Append(Convert.ToString(field.Value, CultureInfo.InvariantCulture));
            }

            return builder.ToString();
        }

        /// <inheritdoc />
        public override string ToString() => ToLine();
    }

    /// <summary>
    /// Structured logging with a level per subsystem (<c>REQ-NFR-034</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Writing never blocks the caller.</strong> The requirement makes this a correctness
    /// matter rather than a performance one, and it is right to: <c>REQ-NFR-011</c> and
    /// <c>REQ-NFR-012</c> put the measurement pipeline on a thread that must not wait for anything,
    /// and a log write that took a lock held by a slow sink would stall an acquisition. So
    /// <see cref="Write"/> enqueues and returns, and a full queue drops rather than waits.
    /// </para>
    /// <para>
    /// <strong>Dropping is counted, not hidden.</strong> A log that quietly lost entries under load
    /// would be worst exactly when it was most needed — a support bundle from a machine in trouble
    /// would be missing the part that mattered and would not say so.
    /// </para>
    /// </remarks>
    public sealed class Log
    {
        /// <summary>How many entries the queue holds before it starts dropping.</summary>
        public const int Capacity = 4096;

        private readonly ConcurrentQueue<LogEntry> _entries = new ConcurrentQueue<LogEntry>();
        private readonly ConcurrentDictionary<string, LogLevel> _levels =
            new ConcurrentDictionary<string, LogLevel>(StringComparer.OrdinalIgnoreCase);

        private int _count;
        private long _dropped;

        /// <summary>The level applied to a subsystem with no level of its own.</summary>
        public LogLevel DefaultLevel { get; set; } = LogLevel.Information;

        /// <summary>Entries dropped because the queue was full.</summary>
        public long Dropped => Interlocked.Read(ref _dropped);

        /// <summary>Every entry currently held, oldest first.</summary>
        public IReadOnlyList<LogEntry> Entries =>
            new ReadOnlyCollection<LogEntry>(_entries.ToArray());

        /// <summary>Subsystems that have been given a level of their own.</summary>
        public IReadOnlyList<string> ConfiguredSubsystems =>
            new ReadOnlyCollection<string>(_levels.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray());

        /// <summary>Sets a subsystem's level, effective immediately.</summary>
        /// <param name="subsystem">The subsystem.</param>
        /// <param name="level">Its new level.</param>
        /// <exception cref="ArgumentNullException"><paramref name="subsystem"/> is null.</exception>
        /// <remarks>
        /// No restart, which is the point: a fault that only appears after twenty minutes of
        /// acquisition cannot be diagnosed by a setting that needs the application closed to apply.
        /// </remarks>
        public void SetLevel(string subsystem, LogLevel level)
        {
            if (subsystem == null)
            {
                throw new ArgumentNullException(nameof(subsystem));
            }

            _levels[subsystem] = level;
        }

        /// <summary>The level in force for a subsystem.</summary>
        /// <param name="subsystem">The subsystem.</param>
        public LogLevel LevelOf(string subsystem)
        {
            LogLevel level;

            return subsystem != null && _levels.TryGetValue(subsystem, out level)
                ? level
                : DefaultLevel;
        }

        /// <summary>Whether an entry at this level would be recorded.</summary>
        /// <param name="subsystem">The subsystem.</param>
        /// <param name="level">The level.</param>
        /// <remarks>
        /// Offered so a caller can skip building fields for an entry that will be discarded. The
        /// fields are the expensive part of a structured entry, not the write.
        /// </remarks>
        public bool IsEnabled(string subsystem, LogLevel level) =>
            level != LogLevel.Off && level <= LevelOf(subsystem);

        /// <summary>Records an entry, without blocking.</summary>
        /// <param name="subsystem">Which subsystem.</param>
        /// <param name="level">Its level.</param>
        /// <param name="message">A short constant description.</param>
        /// <param name="fields">Named values.</param>
        /// <returns><c>true</c> when recorded, <c>false</c> when filtered out or dropped.</returns>
        public bool Write(
            string subsystem,
            LogLevel level,
            string message,
            IReadOnlyDictionary<string, object> fields = null)
        {
            if (!IsEnabled(subsystem, level))
            {
                return false;
            }

            if (Volatile.Read(ref _count) >= Capacity)
            {
                // Drop the oldest rather than the newest: what a support bundle needs is the state
                // near the fault, and the fault is at the end.
                LogEntry discarded;

                if (_entries.TryDequeue(out discarded))
                {
                    Interlocked.Decrement(ref _count);
                    Interlocked.Increment(ref _dropped);
                }
            }

            _entries.Enqueue(new LogEntry(DateTime.UtcNow, subsystem, level, message, fields));
            Interlocked.Increment(ref _count);

            return true;
        }

        /// <summary>How many entries a subsystem has recorded.</summary>
        /// <param name="subsystem">The subsystem.</param>
        public int CountFor(string subsystem) =>
            _entries.Count(e => string.Equals(e.Subsystem, subsystem, StringComparison.OrdinalIgnoreCase));

        /// <summary>Discards everything held.</summary>
        public void Clear()
        {
            LogEntry discarded;

            while (_entries.TryDequeue(out discarded))
            {
            }

            Interlocked.Exchange(ref _count, 0);
        }
    }
}
