using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using OpenVSA.Dsp.Spectrum;
using OpenVSA.Measurement.Markers;

namespace OpenVSA.Ui.ToolWindows
{
    /// <summary>
    /// What a tool window shows.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Lines of text rather than a bespoke model per window. Seven of the eight are lists — of
    /// markers, of log entries, of contexts, of macros — and the eighth is a diagram that reads
    /// perfectly well as indented text. A richer model per window would be eight models to keep in
    /// step with eight renderers before any of them had real data behind it.
    /// </para>
    /// <para>
    /// <strong><see cref="IsLive"/> is not decoration.</strong> Four of these windows have nothing
    /// real to show yet, and a Player reporting a position or a SCPI Log showing traffic would be
    /// read as a recording that is loaded and an instrument that is talking. A source that is not
    /// live must say so in its own first line — <see cref="ToolWindowSource.Lines"/> enforces it
    /// rather than trusting each source to remember.
    /// </para>
    /// </remarks>
    public interface IToolWindowSource
    {
        /// <summary>Which window this feeds.</summary>
        ToolWindow Window { get; }

        /// <summary>
        /// Whether this is real data. <c>false</c> for a demonstration source.
        /// </summary>
        bool IsLive { get; }

        /// <summary>The lines to show, newest last.</summary>
        IReadOnlyList<string> Lines { get; }

        /// <summary>Re-reads whatever this source is showing.</summary>
        void Refresh();
    }

    /// <summary>
    /// The shared part of a tool window's content: the live/demonstration rule.
    /// </summary>
    public abstract class ToolWindowSource : IToolWindowSource
    {
        /// <summary>
        /// The line prepended to every source that is not live.
        /// </summary>
        /// <remarks>
        /// One string, so a reader who learns to recognise it once recognises it everywhere, and
        /// so a test can assert its presence without knowing which window it came from.
        /// </remarks>
        public const string DemonstrationNotice =
            "— demonstration data — nothing is connected to this window yet —";

        /// <summary>Creates a source.</summary>
        /// <param name="window">Which window this feeds.</param>
        protected ToolWindowSource(ToolWindow window)
        {
            // Called for its argument check.
            ToolWindows.NameOf(window);

            Window = window;
        }

        /// <inheritdoc />
        public ToolWindow Window { get; }

        /// <inheritdoc />
        public abstract bool IsLive { get; }

        /// <summary>
        /// The lines to show, with the demonstration notice first where one is due.
        /// </summary>
        /// <remarks>
        /// Applied here rather than left to each source, because "remember to say it is fake" is
        /// exactly the kind of instruction that is followed seven times out of eight.
        /// </remarks>
        public IReadOnlyList<string> Lines
        {
            get
            {
                IReadOnlyList<string> body = Content();

                if (IsLive)
                {
                    return body;
                }

                var withNotice = new List<string>(body.Count + 2) { DemonstrationNotice, string.Empty };
                withNotice.AddRange(body);

                return new ReadOnlyCollection<string>(withNotice);
            }
        }

        /// <inheritdoc />
        public virtual void Refresh()
        {
        }

        /// <summary>The source's own lines, without the demonstration notice.</summary>
        protected abstract IReadOnlyList<string> Content();
    }

    /// <summary>
    /// A tool window fed by appending lines to it: Output, SCPI Log and Event Log.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Live as soon as anything real is written to it, and demonstration until then — which is
    /// why <see cref="IsLive"/> is a fact about what has happened rather than a flag someone sets.
    /// A SCPI Log that claimed to be live while showing only seeded traffic would be the exact
    /// misreading the notice exists to prevent.
    /// </para>
    /// <para>
    /// <strong>Bounded.</strong> A SCPI log on a chatty instrument grows without limit, and an
    /// unbounded list behind a UI control is a leak that only shows up after a long session. The
    /// oldest lines are dropped, and the window says how many.
    /// </para>
    /// </remarks>
    public sealed class ToolWindowLog : ToolWindowSource
    {
        private readonly List<string> _lines = new List<string>();

        private long _dropped;
        private bool _hasRealEntries;

        /// <summary>Creates a log.</summary>
        /// <param name="window">Which window this feeds.</param>
        /// <param name="capacity">Lines retained; must be positive.</param>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="capacity"/> is not positive.</exception>
        public ToolWindowLog(ToolWindow window, int capacity = 500)
            : base(window)
        {
            if (capacity < 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(capacity), capacity, "A log retains at least one line.");
            }

            Capacity = capacity;
        }

        /// <summary>Lines retained before the oldest are dropped.</summary>
        public int Capacity { get; }

        /// <summary>Lines dropped because the log was full.</summary>
        public long DroppedCount => _dropped;

        /// <inheritdoc />
        public override bool IsLive => _hasRealEntries;

        /// <summary>
        /// Appends a real entry, which makes this log live.
        /// </summary>
        /// <param name="line">The entry; may not be null.</param>
        /// <exception cref="ArgumentNullException"><paramref name="line"/> is null.</exception>
        /// <remarks>
        /// The seeded lines are discarded by the first real one. Leaving them above genuine traffic
        /// would put invented entries in a log someone is reading to find out what actually
        /// happened, which is worse than an empty log by some margin.
        /// </remarks>
        public void Append(string line)
        {
            if (line == null)
            {
                throw new ArgumentNullException(nameof(line));
            }

            if (!_hasRealEntries)
            {
                _hasRealEntries = true;
                _lines.Clear();
            }

            _lines.Add(line);
            Trim();
        }

        /// <summary>
        /// Seeds a demonstration line, shown only until the first real entry arrives.
        /// </summary>
        /// <param name="line">The line; may not be null.</param>
        /// <exception cref="ArgumentNullException"><paramref name="line"/> is null.</exception>
        /// <exception cref="InvalidOperationException">The log already has real entries.</exception>
        public void Seed(string line)
        {
            if (line == null)
            {
                throw new ArgumentNullException(nameof(line));
            }

            if (_hasRealEntries)
            {
                throw new InvalidOperationException(
                    "This log already carries real entries; seeding demonstration lines into it " +
                    "would put invented traffic among measured traffic.");
            }

            _lines.Add(line);
            Trim();
        }

        /// <summary>
        /// Empties the log.
        /// </summary>
        /// <remarks>
        /// It becomes not-live and <em>empty</em> — the seeded lines are not restored. Putting
        /// demonstration traffic back into a log somebody has just cleared would be the same
        /// misreading the notice exists to prevent, arrived at from the other direction. A caller
        /// that wants the examples back can <see cref="Seed"/> them again, which is now permitted.
        /// </remarks>
        public void Clear()
        {
            _lines.Clear();
            _dropped = 0;
            _hasRealEntries = false;
        }

        /// <inheritdoc />
        protected override IReadOnlyList<string> Content()
        {
            if (_dropped == 0)
            {
                return new ReadOnlyCollection<string>(_lines);
            }

            var shown = new List<string>(_lines.Count + 1)
            {
                "… " + _dropped.ToString(CultureInfo.CurrentCulture) + " earlier line(s) dropped",
            };

            shown.AddRange(_lines);

            return new ReadOnlyCollection<string>(shown);
        }

        private void Trim()
        {
            while (_lines.Count > Capacity)
            {
                _lines.RemoveAt(0);
                _dropped++;
            }
        }
    }

    /// <summary>
    /// The Markers window, fed by the marker model (<c>REQ-MKR-006</c>).
    /// </summary>
    /// <remarks>
    /// The rows are <see cref="MarkerCollection.Readouts"/>'s own text, not a second rendering of
    /// the same values. That is what makes <c>REQ-MKR-006</c>'s "both surfaces show the same value
    /// for the same marker" true by construction rather than by two formatters agreeing.
    /// </remarks>
    public sealed class MarkerWindowSource : ToolWindowSource
    {
        private readonly MarkerCollection _markers;
        private IReadOnlyList<string> _lines = new ReadOnlyCollection<string>(new string[0]);

        /// <summary>Creates the source.</summary>
        /// <param name="markers">The markers to show.</param>
        /// <exception cref="ArgumentNullException"><paramref name="markers"/> is null.</exception>
        public MarkerWindowSource(MarkerCollection markers)
            : base(ToolWindow.Markers)
        {
            if (markers == null)
            {
                throw new ArgumentNullException(nameof(markers));
            }

            _markers = markers;
            Refresh();
        }

        /// <inheritdoc />
        public override bool IsLive => true;

        /// <inheritdoc />
        public override void Refresh()
        {
            var lines = new List<string>();

            foreach (MarkerReadout readout in _markers.Readouts())
            {
                lines.Add(
                    (readout.IsActive ? "▶ " : "  ") + readout.TraceLetter + "  " + readout.Text);
            }

            if (lines.Count == 0)
            {
                lines.Add("No markers. Marker → Normal marker at peak.");
            }

            _lines = new ReadOnlyCollection<string>(lines);
        }

        /// <inheritdoc />
        protected override IReadOnlyList<string> Content() => _lines;
    }

    /// <summary>
    /// The Contexts window: the measurement contexts in the session (<c>REQ-STA-004</c>).
    /// </summary>
    public sealed class ContextWindowSource : ToolWindowSource
    {
        private readonly List<string> _contexts = new List<string>();

        private string _active = string.Empty;

        /// <summary>Creates the source.</summary>
        public ContextWindowSource()
            : base(ToolWindow.Contexts)
        {
        }

        /// <inheritdoc />
        public override bool IsLive => _contexts.Count > 0;

        /// <summary>
        /// Replaces the contexts shown.
        /// </summary>
        /// <param name="names">The context names, in order.</param>
        /// <param name="active">The active context's name, or an empty string.</param>
        /// <exception cref="ArgumentNullException"><paramref name="names"/> is null.</exception>
        public void Set(IEnumerable<string> names, string active)
        {
            if (names == null)
            {
                throw new ArgumentNullException(nameof(names));
            }

            _contexts.Clear();
            _contexts.AddRange(names);
            _active = active ?? string.Empty;
        }

        /// <inheritdoc />
        protected override IReadOnlyList<string> Content()
        {
            if (_contexts.Count == 0)
            {
                // REQ-STA-004 matches contexts by name on recall, so a session always has at least
                // the one it started with once a measurement exists. Before then there is nothing
                // to list, and the demonstration source supplies an example.
                return new ReadOnlyCollection<string>(
                    new[] { "Measurement 1", "Measurement 2 (spectrum, 2.4 GHz)" });
            }

            var lines = new List<string>(_contexts.Count);

            foreach (string name in _contexts)
            {
                lines.Add(
                    (string.Equals(name, _active, StringComparison.Ordinal) ? "▶ " : "  ") + name);
            }

            return new ReadOnlyCollection<string>(lines);
        }
    }

    /// <summary>
    /// A window with no data source yet, showing worked demonstration content.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Player, Block Diagram and Macros have nothing behind them: playback is <c>REQ-REC-002</c>,
    /// the diagram needs a signal-path model, and macros need the automation API of
    /// <c>REQ-API-*</c> — an empty project today. <c>REQ-UI-002</c> nonetheless requires all eight
    /// to exist under exactly these names.
    /// </para>
    /// <para>
    /// <strong>Demonstration content, not an empty pane.</strong> An empty window says nothing
    /// about what will go in it; a worked example says what the window is for and what it will
    /// look like when it works, which is the more useful thing for a window that cannot yet do its
    /// job. It is labelled as demonstration by <see cref="ToolWindowSource.Lines"/> so it cannot be
    /// mistaken for a loaded recording or live traffic.
    /// </para>
    /// </remarks>
    public sealed class DemonstrationSource : ToolWindowSource
    {
        private readonly ReadOnlyCollection<string> _lines;

        /// <summary>Creates a demonstration source.</summary>
        /// <param name="window">Which window this feeds.</param>
        /// <param name="lines">The lines to show.</param>
        /// <exception cref="ArgumentNullException"><paramref name="lines"/> is null.</exception>
        public DemonstrationSource(ToolWindow window, IEnumerable<string> lines)
            : base(window)
        {
            if (lines == null)
            {
                throw new ArgumentNullException(nameof(lines));
            }

            _lines = new ReadOnlyCollection<string>(new List<string>(lines));
        }

        /// <inheritdoc />
        public override bool IsLive => false;

        /// <inheritdoc />
        protected override IReadOnlyList<string> Content() => _lines;
    }

    /// <summary>
    /// The demonstration content for the windows that have no data source yet.
    /// </summary>
    /// <remarks>
    /// Gathered in one place so that when a window gains a real source, the thing to delete is
    /// obvious and is one method — rather than canned strings dispersed through the shell.
    /// </remarks>
    public static class ToolWindowDemonstrations
    {
        /// <summary>A source for a window that has no live data, or <c>null</c> if it has.</summary>
        /// <param name="window">The window.</param>
        /// <exception cref="ArgumentOutOfRangeException">Not a known tool window.</exception>
        public static IToolWindowSource For(ToolWindow window)
        {
            switch (window)
            {
                case ToolWindow.Player:
                    return new DemonstrationSource(window, Player());

                case ToolWindow.BlockDiagram:
                    return new DemonstrationSource(window, BlockDiagram());

                case ToolWindow.Macros:
                    return new DemonstrationSource(window, Macros());

                default:
                    ToolWindows.NameOf(window);
                    return null;
            }
        }

        /// <summary>The lines a SCPI log is seeded with until an instrument talks.</summary>
        /// <remarks>
        /// <para>
        /// The <em>shape</em> of a session — a query and its response, commands that answer
        /// nothing, a response parsed for a number — so the window shows what it will look like
        /// in use.
        /// </para>
        /// <para>
        /// <strong>The identity response carries placeholders, not a real one.</strong>
        /// <c>REQ-HAL-002</c>'s criterion is that a search of this assembly for instrument names
        /// returns nothing, and the architecture suite enforces it — it caught a transcribed
        /// <c>*IDN?</c> reply here. Removed rather than exempted: an exemption list is how such a
        /// search stops meaning anything. Angle brackets also make it unmistakably a template
        /// rather than a device somebody might go looking for.
        /// </para>
        /// </remarks>
        public static IReadOnlyList<string> ScpiLog() =>
            new ReadOnlyCollection<string>(new[]
            {
                "→ *IDN?",
                "← <manufacturer>,<model>,<serial>,<firmware>",
                "→ :FREQ:CENT 1.0E9",
                "→ :WAV:BWID?",
                "← +1.00000000E+007",
            });

        /// <summary>The lines an event log is seeded with until something happens.</summary>
        public static IReadOnlyList<string> EventLog() =>
            new ReadOnlyCollection<string>(new[]
            {
                "Front-end registry: 2 signal sources discovered.",
                "No source selected. Choose one under Hardware ▸ Instruments….",
            });

        /// <summary>The lines an output window is seeded with until a measurement runs.</summary>
        public static IReadOnlyList<string> Output() =>
            new ReadOnlyCollection<string>(new[]
            {
                "Peak      1.003105 GHz    −20.62 dBm",
                "Band pwr  −20.64 dBm over 20 bins",
                "OBW 99%   105.4 kHz",
            });

        private static IEnumerable<string> Player() => new[]
        {
            "Recording   (none loaded)",
            "Length      2.048 s at 15.000 MS/s, 30 720 000 samples",
            "Position    0.512 s   ▶ ▮▮ ◀◀ ▶▶   Loop: off",
            string.Empty,
            "Playback arrives with REQ-REC-002. The transport, the position readout and the",
            "loop control are what this window will carry; the recording behind them does not",
            "exist yet.",
        };

        /// <summary>
        /// The signal path, with its analysis stages read from <c>CompositionOrder</c>.
        /// </summary>
        /// <remarks>
        /// Generated from the declaration rather than drawn, so a stage added to
        /// <c>REQ-TRC-003</c>'s pipeline appears here without anyone remembering to redraw it —
        /// and a stage removed from it disappears. A hand-drawn diagram of a pipeline is a
        /// document that is correct on the day it is written.
        /// </remarks>
        private static IEnumerable<string> BlockDiagram()
        {
            var lines = new List<string>
            {
                "Input  ──▶  Range / attenuation  ──▶  ADC",
                "                    │",
                "     Digital downconverter (zoom, REQ-DSP-023a)",
                "                    │",
            };

            foreach (AnalysisStage stage in CompositionOrder.Stages)
            {
                lines.Add("             " + Describe(stage));
                lines.Add("                    │");
            }

            lines.Add("                  Trace");
            lines.Add(string.Empty);
            lines.Add("The analysis stages and their order are REQ-TRC-003's, read from");
            lines.Add("CompositionOrder rather than drawn here, so this");
            lines.Add("diagram cannot drift from the pipeline that actually runs. What is missing is");
            lines.Add("the live annotation: which stages are active, and what each is set to.");

            return lines;
        }

        /// <summary>A stage as the diagram labels it, keeping the declared name visible.</summary>
        private static string Describe(AnalysisStage stage)
        {
            switch (stage)
            {
                case AnalysisStage.Gating: return "Gating  (time gate, REQ-DSP-050)";
                case AnalysisStage.Windowing: return "Windowing  (REQ-DSP-010)";
                case AnalysisStage.Transform: return "Transform  (FFT, REQ-DSP-001)";
                case AnalysisStage.Averaging: return "Averaging  (REQ-DSP-030)";
                case AnalysisStage.Accumulation: return "Accumulation  (REQ-TRC-001a)";
                case AnalysisStage.Format: return "Format  (REQ-DSP-041)";

                // A stage added to the declaration and not to this switch still appears, under its
                // own name. Falling back beats leaving it out of the diagram silently.
                default: return stage.ToString();
            }
        }

        private static IEnumerable<string> Macros() => new[]
        {
            "Capture and save          Ctrl+Shift+1",
            "Zoom to marker            Ctrl+Shift+2",
            "ACP at 5 MHz offsets      Ctrl+Shift+3",
            string.Empty,
            "Macros record and replay automation commands. They wait on the automation API of",
            "REQ-API-*, which is an empty project today — there is nothing yet for a macro to be",
            "a sequence of.",
        };
    }
}
