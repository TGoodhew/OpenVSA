using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace OpenVSA.Ui.Toolbars
{
    /// <summary>What kind of control a toolbar entry is.</summary>
    public enum ToolbarControlKind
    {
        /// <summary>A button: press it and something happens.</summary>
        Button = 0,

        /// <summary>A button that stays in until pressed again.</summary>
        Toggle,

        /// <summary>One of a set of which exactly one is chosen.</summary>
        Radio,

        /// <summary>A button with a dropdown beside it, each with its own meaning.</summary>
        Split,

        /// <summary>A list to choose from.</summary>
        Dropdown,

        /// <summary>Something to read rather than press.</summary>
        Readout,

        /// <summary>A rule between groups.</summary>
        Separator,
    }

    /// <summary>One control on one of <c>REQ-UI-063</c>'s toolbars.</summary>
    public sealed class ToolbarControl
    {
        internal ToolbarControl(
            string name, ToolbarControlKind kind, string reason, string group, string tip)
        {
            Name = name ?? string.Empty;
            Kind = kind;
            Reason = reason;
            Group = group;
            Tip = tip;
        }

        /// <summary>The control's caption, as the requirement names it.</summary>
        public string Name { get; }

        /// <summary>What kind of control it is.</summary>
        public ToolbarControlKind Kind { get; }

        /// <summary>Why it is disabled, or <c>null</c> when the shell binds it.</summary>
        public string Reason { get; }

        /// <summary>Which radio group it belongs to, or <c>null</c>.</summary>
        public string Group { get; }

        /// <summary>What it does, for the tooltip; the reason takes over when there is one.</summary>
        public string Tip { get; }

        /// <summary>Whether the shell is expected to bind it.</summary>
        public bool IsImplemented => Reason == null;

        /// <inheritdoc />
        public override string ToString() => Name + " (" + Kind + ")";
    }

    /// <summary>One of <c>REQ-UI-063</c>'s six toolbars.</summary>
    public sealed class ShellToolbar
    {
        internal ShellToolbar(string name, bool isCustomisable, IList<ToolbarControl> controls)
        {
            Name = name;
            IsCustomisable = isCustomisable;
            Controls = new ReadOnlyCollection<ToolbarControl>(controls);
        }

        /// <summary>The toolbar's name, as the requirement names it.</summary>
        public string Name { get; }

        /// <summary>
        /// Whether the toolbar customiser of <c>REQ-UI-064</c> may edit it.
        /// </summary>
        /// <remarks>
        /// False for exactly one of the six. <c>REQ-UI-063</c> says the macro-button bar is
        /// "managed by the macros utility; not user-editable through the toolbar customiser", and
        /// <c>REQ-UI-064</c>'s criterion repeats it — a rule stated twice is one worth asserting.
        /// </remarks>
        public bool IsCustomisable { get; }

        /// <summary>What is on it, in order.</summary>
        public IReadOnlyList<ToolbarControl> Controls { get; }

        /// <inheritdoc />
        public override string ToString() => Name + " (" + Controls.Count + " controls)";
    }

    /// <summary>
    /// The five preconfigured toolbars and the macro bar (<c>REQ-UI-063</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Declared here and built from here, for the reason <see cref="Menus.ShellMenuTable"/> is: the
    /// requirement's criterion is that "all six toolbars exist with the listed contents", and a
    /// second copy of the list in markup is a second thing to drift.
    /// </para>
    /// <para>
    /// <strong>What the requirement asks to be <em>tested</em> is the behaviour, not the
    /// presence.</strong> Restart discarding averaging, Pause meaning two different things
    /// depending on the sweep mode, the split button's two halves, and the mouse modes being a
    /// radio group are all called out by name, with "since collapsing them is the likely shortcut"
    /// written against the one most likely to be got wrong.
    /// </para>
    /// </remarks>
    public static class ShellToolbars
    {
        /// <summary>The Marker Tools radio group's name.</summary>
        public const string MouseModeGroup = "MouseMode";

        /// <summary>
        /// The accumulator group's name.
        /// </summary>
        /// <remarks>
        /// Grouped like the mouse modes, but the controls are toggles rather than radios: the
        /// setting behind them has a value — no accumulator at all — that a radio group offers no
        /// way back to.
        /// </remarks>
        public const string AccumulatorGroup = "Accumulator";

        private const string Accumulators = AccumulatorGroup;

        private static readonly ReadOnlyCollection<ShellToolbar> Bars = Build();

        /// <summary>The six toolbars, in the order the tray shows them.</summary>
        public static IReadOnlyList<ShellToolbar> All => Bars;

        /// <summary>
        /// One toolbar by name.
        /// </summary>
        /// <param name="name">The toolbar's name.</param>
        /// <exception cref="ArgumentOutOfRangeException">There is no such toolbar.</exception>
        public static ShellToolbar For(string name)
        {
            foreach (ShellToolbar bar in Bars)
            {
                if (string.Equals(bar.Name, name, StringComparison.Ordinal))
                {
                    return bar;
                }
            }

            throw new ArgumentOutOfRangeException(
                nameof(name), name, "REQ-UI-063 has no toolbar of that name.");
        }

        /// <summary>The path a control is known by: its toolbar and its name.</summary>
        /// <param name="toolbar">The toolbar's name.</param>
        /// <param name="control">The control's name.</param>
        public static string PathOf(string toolbar, string control) => toolbar + " > " + control;

        /// <summary>
        /// The control at a path, or <c>null</c> if this build declares none.
        /// </summary>
        /// <param name="path">A path from <see cref="PathOf"/>.</param>
        /// <remarks>
        /// How <see cref="ToolbarLayout"/>'s stored paths become controls again. The path is the
        /// control's identity wherever the customiser of <c>REQ-UI-064</c> has put it, so this looks
        /// the control up by where the requirement declares it and not by where it now sits.
        /// </remarks>
        public static ToolbarControl ControlAt(string path)
        {
            foreach (KeyValuePair<string, ToolbarControl> found in AllControls())
            {
                if (string.Equals(found.Key, path, StringComparison.Ordinal))
                {
                    return found.Value;
                }
            }

            return null;
        }

        /// <summary>Every control, with its path, in order.</summary>
        public static IEnumerable<KeyValuePair<string, ToolbarControl>> AllControls()
        {
            foreach (ShellToolbar bar in Bars)
            {
                foreach (ToolbarControl control in bar.Controls)
                {
                    if (control.Kind != ToolbarControlKind.Separator)
                    {
                        yield return new KeyValuePair<string, ToolbarControl>(
                            PathOf(bar.Name, control.Name), control);
                    }
                }
            }
        }

        private static ReadOnlyCollection<ShellToolbar> Build() =>
            new ReadOnlyCollection<ShellToolbar>(new List<ShellToolbar>
            {
                new ShellToolbar("Control", true, Control()),
                new ShellToolbar("Marker Tools", true, MarkerTools()),
                new ShellToolbar("Record", true, Record()),
                new ShellToolbar("Trace / Block Diagram", true, Trace()),
                new ShellToolbar("Spectrogram / Colour Map", true, Spectrogram()),

                // The one that is not the customiser's to edit. REQ-UI-063 says so and REQ-UI-064
                // repeats it.
                new ShellToolbar("Macro Buttons", false, Macros()),
            });

        private static List<ToolbarControl> Control() => new List<ToolbarControl>
        {
            Button("Restart",
                "Start the measurement, or restart it. Everything accumulated, averaging " +
                "included, is discarded."),

            Button("Pause",
                "Hold the measurement. Pressing again single-steps under Single sweep, or " +
                "continues under Continuous."),

            Toggle("Single Sweep",
                "Take one sweep at a time instead of running continuously. Changing this does " +
                "not start or stop anything; it says what the next press of Pause means."),

            Rule(),

            Split("Auto-range",
                "Range every input channel for the signal present. The dropdown ranges one " +
                "chosen channel instead."),
        };

        private static List<ToolbarControl> MarkerTools() => new List<ToolbarControl>
        {
            // A radio group, and the requirement says so: "selecting one mouse mode deselects the
            // others". Five modes, in the order it lists them.
            Radio("Pointer", "Clicking a trace does nothing to the measurement."),
            Radio("Area Select",
                "Drag a rectangle over a trace to examine it: scale X, scale Y, or set the " +
                "centre frequency and span from the region."),
            Radio("Marker", "Click a trace to place a marker there."),
            Radio("Band Power", "Drag the band limits to integrate the power between them."),
            Radio("Time Gate", "Drag to isolate a portion of the time record."),
        };

        private static List<ToolbarControl> Record() => new List<ToolbarControl>
        {
            Off("Record",
                "Recording arrives with REQ-REC-001 in Phase 2. The Player window opens and " +
                "shows what a transport will look like; there is nothing to record into yet."),

            Off("Toggle Data Source",
                "Switching between live hardware and a recording needs recordings, which are " +
                "Phase 2 work. Live input is what this build measures."),

            Button("Disconnect", "Release the instrument from OpenVSA's control."),
        };

        private static List<ToolbarControl> Trace() => new List<ToolbarControl>
        {
            // "Active Trace (shows the active trace's letter)" - the letter is the requirement,
            // not a name or a number. REQ-UI-020 identifies traces by letter throughout.
            Readout("Active Trace", "Which trace the trace commands act on."),
            Dropdown("Trace Layout", "How the trace windows are arranged."),
            Toggle("Block Diagram", "Show the signal path from input to trace."),
        };

        private static List<ToolbarControl> Spectrogram() => new List<ToolbarControl>
        {
            // REQ-TRC-001a's accumulators: one setting with three values, so choosing one clears
            // the others. Toggles rather than radios, because the setting has a fourth value -
            // None - and a radio group has no way back to it. Pressing the chosen one again is
            // how a user turns the accumulator off.
            Toggle("Spectrogram", "Draw each sweep as a row, oldest at the bottom.", Accumulators),
            Toggle(
                "Digital Persistence",
                "Fade older sweeps into the picture rather than replacing them.",
                Accumulators),
            Toggle(
                "Cumulative History",
                "Keep the extremes of every sweep since the last restart.",
                Accumulators),

            Rule(),

            Toggle("Enhance",
                "Stretch the colour map about the levels the history actually holds, so that " +
                "faint detail becomes visible instead of the whole map being spent on a range " +
                "nothing occupies."),

            Dropdown("Threshold",
                "Hide cells below a chosen level, so that a spectrogram shows only what is above " +
                "the noise."),

            Dropdown("Map Colour Scheme", "Which colour map the spectrogram is drawn with."),
        };

        private static List<ToolbarControl> Macros() => new List<ToolbarControl>
        {
            Off("Macros",
                "The macro bar is filled by the macros utility, which is Phase 2 work. It is here " +
                "because REQ-UI-063 lists it as one of the six, and empty because there are no " +
                "macros to put on it."),
        };

        // ---- Constructors, kept short so the table above reads as a list ----------------------

        private static ToolbarControl Button(string name, string tip) =>
            new ToolbarControl(name, ToolbarControlKind.Button, null, null, tip);

        private static ToolbarControl Toggle(string name, string tip, string group = null) =>
            new ToolbarControl(name, ToolbarControlKind.Toggle, null, group, tip);

        private static ToolbarControl Radio(string name, string tip) =>
            new ToolbarControl(name, ToolbarControlKind.Radio, null, MouseModeGroup, tip);

        private static ToolbarControl Split(string name, string tip) =>
            new ToolbarControl(name, ToolbarControlKind.Split, null, null, tip);

        private static ToolbarControl Dropdown(string name, string tip) =>
            new ToolbarControl(name, ToolbarControlKind.Dropdown, null, null, tip);

        private static ToolbarControl Readout(string name, string tip) =>
            new ToolbarControl(name, ToolbarControlKind.Readout, null, null, tip);

        private static ToolbarControl Off(string name, string reason) =>
            new ToolbarControl(name, ToolbarControlKind.Button, reason, null, null);

        private static ToolbarControl Rule() =>
            new ToolbarControl(string.Empty, ToolbarControlKind.Separator, null, null, null);
    }
}
