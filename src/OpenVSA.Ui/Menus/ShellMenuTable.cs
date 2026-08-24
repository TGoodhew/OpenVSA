using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace OpenVSA.Ui.Menus
{
    /// <summary>
    /// The menu contents of <c>REQ-UI-061</c>: what is on each of <c>REQ-UI-060</c>'s ten menus.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The bar is built from this table, and the table is checked against the
    /// specification.</strong> Two tests hold it in place from opposite sides: one walks the real
    /// menu bar of a real shell and compares it with what is written here, and one parses
    /// <c>REQ-UI-061</c>'s own list out of the requirements document and compares that with what is
    /// written here. Neither on its own would be worth much — the first would prove the shell agrees
    /// with a list somebody typed, the second that the list agrees with the specification while the
    /// shell showed something else entirely.
    /// </para>
    /// <para>
    /// <strong>The exactness runs both ways.</strong> The criterion is that "an item present in the
    /// tree but not in the list also fails, so the menus stay as specified rather than accreting",
    /// which is the half that a menu quietly grows past. It is also the half that made this change
    /// larger than it looks: several working items were on menus of their own invention and had to
    /// be found a listed home rather than left where they were convenient.
    /// </para>
    /// <para>
    /// <strong>Where the specification enumerates, this is a transcription; where it does not, the
    /// nesting is OpenVSA's own.</strong> <c>REQ-UI-061</c> lists the top level of all ten menus and
    /// the children of Recall and Preset. Everything deeper — what is under Save, Format, Control,
    /// Limit Tests — is a choice made here, and marked as such by not appearing in the parsed
    /// comparison. The spec-parsing test covers exactly the levels the specification writes down.
    /// </para>
    /// <para>
    /// <strong>Every entry carries either an action or a reason.</strong> See
    /// <see cref="ShellMenuEntry.Reason"/>: a great many of these are Phase 2 and Phase 3 work, and
    /// they are here, disabled, saying so. That is what the requirement asks for, and it is more
    /// useful than an empty menu: a user can see that OpenVSA knows what an external mixer is and
    /// that this build cannot drive one.
    /// </para>
    /// </remarks>
    public static class ShellMenuTable
    {
        /// <summary>How a path names an entry inside a menu.</summary>
        public const string PathSeparator = " > ";

        private static readonly ReadOnlyCollection<ShellMenu> Table = Build();

        /// <summary>The ten menus and their contents, in order.</summary>
        public static IReadOnlyList<ShellMenu> Menus => Table;

        /// <summary>
        /// One menu by name.
        /// </summary>
        /// <param name="name">The menu's name.</param>
        /// <exception cref="ArgumentOutOfRangeException">There is no such menu.</exception>
        public static ShellMenu For(string name)
        {
            foreach (ShellMenu menu in Table)
            {
                if (string.Equals(menu.Name, name, StringComparison.Ordinal))
                {
                    return menu;
                }
            }

            throw new ArgumentOutOfRangeException(
                nameof(name), name, "REQ-UI-060's bar has no menu of that name.");
        }

        /// <summary>
        /// The entry at a path, or <c>null</c> if there is none.
        /// </summary>
        /// <param name="path">A path such as <c>File &gt; Preset &gt; Factory Defaults</c>.</param>
        public static ShellMenuEntry At(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return null;
            }

            string[] steps = path.Split(new[] { PathSeparator }, StringSplitOptions.None);
            IReadOnlyList<ShellMenuEntry> level = null;

            foreach (ShellMenu menu in Table)
            {
                if (string.Equals(menu.Name, steps[0], StringComparison.Ordinal))
                {
                    level = menu.Items;
                    break;
                }
            }

            ShellMenuEntry found = null;

            for (int step = 1; level != null && step < steps.Length; step++)
            {
                found = null;

                foreach (ShellMenuEntry entry in level)
                {
                    if (string.Equals(entry.Name, steps[step], StringComparison.Ordinal))
                    {
                        found = entry;
                        break;
                    }
                }

                level = found?.Children;
            }

            return found;
        }

        /// <summary>Joins a parent path and a name.</summary>
        /// <param name="parent">The parent's path.</param>
        /// <param name="name">The entry's name.</param>
        public static string PathOf(string parent, string name) =>
            string.IsNullOrEmpty(parent) ? name : parent + PathSeparator + name;

        /// <summary>
        /// Every entry in the table, with its path, depth-first in menu order.
        /// </summary>
        public static IEnumerable<KeyValuePair<string, ShellMenuEntry>> All()
        {
            foreach (ShellMenu menu in Table)
            {
                foreach (KeyValuePair<string, ShellMenuEntry> found in Walk(menu.Name, menu.Items))
                {
                    yield return found;
                }
            }
        }

        private static IEnumerable<KeyValuePair<string, ShellMenuEntry>> Walk(
            string parent, IReadOnlyList<ShellMenuEntry> level)
        {
            foreach (ShellMenuEntry entry in level)
            {
                if (entry.Kind != ShellMenuEntryKind.Item)
                {
                    continue;
                }

                string path = PathOf(parent, entry.Name);

                yield return new KeyValuePair<string, ShellMenuEntry>(path, entry);

                foreach (KeyValuePair<string, ShellMenuEntry> child in Walk(path, entry.Children))
                {
                    yield return child;
                }
            }
        }

        // ---- The table ------------------------------------------------------------------------

        private static ReadOnlyCollection<ShellMenu> Build()
        {
            var menus = new List<ShellMenu>
            {
                new ShellMenu("File", File()),
                new ShellMenu("Edit", Edit()),
                new ShellMenu("Hardware", Hardware()),
                new ShellMenu("Acquisition", Acquisition()),
                new ShellMenu("Analysis", Analysis()),
                new ShellMenu("Trace", Trace()),
                new ShellMenu("Marker", Marker()),
                new ShellMenu("Utilities", Utilities()),
                new ShellMenu("Window", Window()),
                new ShellMenu("Help", Help()),
            };

            // The bar itself is REQ-UI-060's, and it is named in one place. A second list of the
            // ten here is a second thing to keep in step, and the failure would be a menu that
            // exists in the table and never appears - or worse, one that appears twice.
            if (menus.Count != ShellMenus.Names.Count)
            {
                throw new InvalidOperationException(
                    "REQ-UI-061's table has " + menus.Count + " menus; REQ-UI-060's bar has " +
                    ShellMenus.Names.Count + ".");
            }

            for (int index = 0; index < menus.Count; index++)
            {
                if (!string.Equals(menus[index].Name, ShellMenus.Names[index], StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "REQ-UI-061's table has '" + menus[index].Name + "' where REQ-UI-060's bar " +
                        "has '" + ShellMenus.Names[index] + "'.");
                }
            }

            return new ReadOnlyCollection<ShellMenu>(menus);
        }

        private static List<ShellMenuEntry> File() => new List<ShellMenuEntry>
        {
            Menu(
                "Recall",
                Item("Setup"),
                Off("Recording",
                    "Recordings arrive with REQ-REC-001 in Phase 2. There is no recording format " +
                    "to read yet, and an item that opened a file picker onto nothing would be " +
                    "worse than one that says so."),
                Off("Trace",
                    "Reading a trace back from a file needs the trace data format of REQ-TRC-030, " +
                    "which is Phase 2 work. Saved setups (Recall > Setup) carry trace formats and " +
                    "scaling today, but not the measured points."),
                Off("Layout",
                    "Window arrangements persist automatically across a restart. Recalling a named " +
                    "layout file needs the layout state of REQ-STA-002's sidecar to be separable " +
                    "from the rest of it, which it is not yet."),
                Off("Demo",
                    "The demonstration signals of REQ-DEM-001 are Phase 3. The simulated front end " +
                    "on the Hardware menu is what stands in for them in this build.")),

            Live(
                "Preset",
                Item("Measurement"),
                Off("Measurement to Standard",
                    "A standard is a named set of measurement settings — a radio format, a mask, a " +
                    "channel plan — and OpenVSA has no standards library until the digital " +
                    "demodulation work of Phase 3. Preset > Measurement to Defaults is the same " +
                    "reset without one."),
                Item("Measurement to Defaults"),
                Item("Setup"),
                Item("Traces"),
                Item("Application and Traces"),
                Item("Display Preferences"),
                Item("Toolbars"),
                Item("Factory Defaults")),

            Rule(),

            Menu(
                "Save",
                Item("Setup"),
                Off("Trace",
                    "Writing measured points to a file needs the trace data format of REQ-TRC-030, " +
                    "which is Phase 2 work. Export > Trace bitmap writes the picture today."),
                Off("Layout",
                    "Window arrangements are saved automatically when the shell closes. Saving one " +
                    "under a name of its own waits on the same sidecar separation as Recall > Layout."),
                Item("Preset")),

            Menu(
                "Export",
                Item("Trace bitmap"),
                Off("Trace data",
                    "The CSV and MATLAB exports of REQ-TRC-031 are Phase 2 work.")),

            Menu(
                "Print",
                Item("Print trace"),
                Tick("Force white background")),

            Rule(),
            Item("Exit"),
        };

        private static List<ShellMenuEntry> Edit() => new List<ShellMenuEntry>
        {
            Item("Copy"),
            Item("Copy Markers"),
            Off("Paste",
                "There is nothing OpenVSA reads from the clipboard. Copy writes trace data and " +
                "marker readouts out as text for something else to read; bringing measured data " +
                "back in is the import half of REQ-TRC-030, in Phase 2."),
        };

        private static List<ShellMenuEntry> Hardware() => new List<ShellMenuEntry>
        {
            Live("Instruments…"),
            Off("Configurations…",
                "A configuration is a named set of instruments and their roles, saved and recalled " +
                "together. OpenVSA drives one front end at a time, so there is nothing yet to name."),
            Item("Rediscover"),
            Off("Analyzer",
                "Which instrument acts as the analyser is settled by choosing it under " +
                "Instruments…, because OpenVSA opens one front end at a time. This item is where " +
                "the role is assigned once a configuration can hold several."),
            Off("Frequency Reference…",
                "Switching between the internal and an external 10 MHz reference is not in " +
                "IFrontEndCapabilities, so no front end can be asked to do it. Adding the item " +
                "before the capability would be a control that reported success and changed nothing."),
            Off("Calibration…",
                "Alignment and calibration are the instrument's own, run from its front panel or " +
                "its SCPI interface. OpenVSA reads the corrections the front end applies; it does " +
                "not yet run the routines that produce them."),
            Item("Disconnect"),
            Off("Source",
                "Which instrument acts as the source is settled by opening it under Source " +
                "Control…, because OpenVSA drives one source at a time — the same reason Analyzer " +
                "is not an item here. This is where the role is assigned once a configuration can " +
                "hold several."),
            Item("Source Control…"),
            Off("Switch",
                "Switch matrices and multiport test sets are outside the scope of a vector signal " +
                "analyser in this phase. The item is listed because the requirement lists it."),
        };

        private static List<ShellMenuEntry> Acquisition() => new List<ShellMenuEntry>
        {
            Off("Data",
                "Choosing between live input, a recording and a data register needs two of the " +
                "three to exist. Live input is what this build acquires; REQ-REC-001 brings the " +
                "others in Phase 2."),
            Off("Channels",
                "Multi-channel acquisition is Phase 2. The front ends OpenVSA opens today declare " +
                "one input channel, and the settings pane ranges itself from that declaration " +
                "rather than assuming it."),
            Item("Amplitude…"),
            Off("External Mixer…",
                "External mixing extends an analyser above its own frequency limit with a " +
                "harmonic mixer. The state carries the harmonic number already; no front end " +
                "declares the capability, so nothing can be asked to use it."),
            Off("Extended Settings…",
                "The overload, dither and preamplifier controls behind this item are per-front-end " +
                "and are not in IFrontEndCapabilities yet."),
            Item("Trigger…"),
            Off("Segmented Capture…",
                "Segmented capture — many short records armed as one — arrives with the recording " +
                "work of REQ-REC-001 in Phase 2."),
            Off("Digital…",
                "Digital baseband input (a bit stream rather than an analogue signal) is Phase 3, " +
                "alongside digital demodulation."),
            Off("Gate Trigger…",
                "Time gating is specified in REQ-TRG-020 and is Phase 2 work. The state carries " +
                "the gate delay and length; nothing applies them yet."),
            Off("Playback Trigger…",
                "Triggering within a recording needs recordings, which arrive with REQ-REC-001."),
            Off("User Correction…",
                "User correction tables — a cable or antenna response applied to every " +
                "measurement — are Phase 2. Applying one silently would be worse than not " +
                "offering it: every amplitude in the display would be adjusted by an amount " +
                "nothing on screen accounted for."),
            Item("Player Window"),
            Off("Recording/Playback…",
                "The recorder of REQ-REC-001 and REQ-REC-002 is Phase 2. The Player window opens " +
                "and shows what a transport will look like; there is nothing to load into it."),
            Menu(
                "Control",
                Item("Start"),
                Item("Stop"),
                Item("Pause"),
                Item("Restart")),
        };

        private static List<ShellMenuEntry> Analysis() => new List<ShellMenuEntry>
        {
            // In this order, which the requirement states and its criterion checks. The seven
            // dialogs in the middle are REQ-UI-072's tab set, and they are built from the dialog's
            // own tab names rather than written out again here.
            // Live, not Menu: REQ-ARC-003 makes the measurement-type selector the place a
            // discovered personality appears, and a personality assembly dropped into
            // Personalities\ is by definition not in any list written here. The four built-in
            // types keep their places and their order — the exactness walk still checks them —
            // and discovery may only add past the end of them.
            Live(
                "Type",
                Tick("Spectrum"),
                Off("Vector Analysis",
                    "Vector analysis — spectrum with phase, and time-domain traces from the same " +
                    "acquisition — is Phase 2. The IQ path that feeds it exists; the measurement " +
                    "type that presents it does not."),
                Tick("Digital Demodulation"),
                Off("Analogue Demodulation",
                    "Analogue demodulation — AM, FM and PM detection — is Phase 3.")),
            Off("Properties…",
                "The measurement properties sheet names the measurement, records what it is for and " +
                "shows what it is costing. It needs the multiple measurement contexts below it, " +
                "which this build does not have."),
            Item("Frequency…"),
            Item("ResBW…"),
            Item("Time…"),
            Item("Detectors…"),
            Item("Conversion…"),
            Item("Average…"),
            Item("Heatmaps…"),
            Off("Measurements…",
                "A list of the measurements in the session, to switch between. This build runs one; " +
                "the Contexts window on the Window menu shows what is there."),
            Off("New Measurement",
                "Several measurements at once — each with its own settings, traces and markers — " +
                "is Phase 2 work. The state format carries a list of them already (REQ-STA-004), " +
                "which is why recalling a multi-context state works and creating one does not."),
            Off("Duplicate Measurement",
                "Follows New Measurement: there is nowhere to put the copy."),
        };

        private static List<ShellMenuEntry> Trace() => new List<ShellMenuEntry>
        {
            // REQ-UI-062 requires the embedded toolbar to be the menu's TOPMOST element.
            // REQ-UI-061 lists it third - and its criterion fixes the order of the Analysis menu
            // only, so both are satisfied by putting it first here. MenuSpecificationTests
            // compares this menu as a set for that reason.
            Tools("Trace tools"),
            Live("Trace List"),
            Item("New Trace"),
            Off("Data",
                "Which acquisition a trace draws from, when there is more than one to choose. See " +
                "Analysis > New Measurement."),

            // REQ-UI-061 writes the next six under a "Properties:" heading and the four after that
            // under "Calculation:". Those are headings in the requirement's own list, not items -
            // adding them as items would fail the criterion that nothing is in the tree that is not
            // in the list. A rule between the groups says the same thing without inventing an item.
            Rule(),
            Live("Format"),
            Off("Coupling",
                "Whether a trace follows the measurement's settings or holds the ones it was made " +
                "with. Every trace is coupled in this build; uncoupling one needs the per-trace " +
                "settings of Phase 2."),
            Live("Y Scale"),
            Off("X Scale",
                "The X axis follows the measurement span, and Select Area on the trace toolbar " +
                "zooms it (REQ-DSP-023). Setting start and stop independently of the measurement " +
                "is Phase 2."),
            Off("Average",
                "Per-trace averaging, separate from the measurement's. Analysis > Average… sets " +
                "the measurement's, which is what this build applies."),
            Off("Digital Demod",
                "The demodulation traces — constellation, eye, error vector — are Phase 3."),

            Rule(),
            Off("Results Window",
                "A pane of computed results for the active trace. OBW… and ACP… write their " +
                "results to the Output window today, which is where this would collect them."),
            Item("OBW…"),
            Item("ACP…"),
            Menu(
                "Limit Tests…",
                Tick("Indicate limit failures"),
                Tick("Indicate margin warnings")),

            Rule(),
            Live("Spectrogram / Colour Map"),
            Off("Math Functions",
                "Trace maths — the operator set of REQ-DSP-046 — is Phase 2."),
            Off("Stimulus-Response / X-Y…",
                "Plotting one trace against another needs a source to sweep and two channels to " +
                "measure. Both are Phase 2."),
            Item("Auto Scale"),
            Off("Overlay",
                "Drawing several traces in one window, sharing its axes. The document area places " +
                "each trace in its own window today; the state format carries the overlay flag " +
                "against the day it does not."),
            Item("Copy Trace"),
        };

        private static List<ShellMenuEntry> Marker() => new List<ShellMenuEntry>
        {
            // Topmost, per REQ-UI-062. See the Trace menu for why that does not conflict with
            // REQ-UI-061's listed order.
            Tools("Marker tools"),
            Item("Markers Window"),
            Menu(
                "New Marker",
                Item("Normal"),
                Item("Delta"),
                Item("Fixed")),
            Item("Position…"),
            Off("Calculation…",
                "Band power, occupied bandwidth and the other per-marker calculations of " +
                "REQ-MKR-010 are Phase 2. Trace > OBW… and ACP… compute the band figures over the " +
                "whole trace in this build."),
            Menu(
                "Peak Search",
                Item("Peak"),
                Item("Next peak"),
                Item("Minimum")),
            Off("Copy Marker To",
                "Placing the same marker on another trace at the same X. Markers belong to one " +
                "trace in this build; the state format records which, ready for the rest."),
            Off("Couple Markers",
                "Moving one marker moves its counterparts on the other traces. Follows Copy " +
                "Marker To — there are no counterparts yet."),
            Item("Copy to Clipboard"),
            Item("All Markers Off"),
        };

        private static List<ShellMenuEntry> Utilities() => new List<ShellMenuEntry>
        {
            // No Licenses… item, and its absence is deliberate. REQ-UI-061 points out that the
            // reference product has one and that OpenVSA must not: there is nothing to license
            // (REQ-LIC-010), and this menu's exact-list criterion means adding one fails the build.
            Off("Macros…",
                "Recording and replaying command sequences is Phase 2. The Macros window on the " +
                "Window menu is where they will be listed."),
            Off("Event-Based Actions…",
                "Running something when a limit fails or a trigger arrives. It needs the limit " +
                "test results of REQ-LIM-001 to be raised as events, which is Phase 2."),
            Off("Trend/Statistics…",
                "Tracking a measured value over time, with its statistics. Phase 2."),
            Off("General Preferences…",
                "The application-wide preferences that are not about the display: units, warm-up " +
                "behaviour, what to do on start-up. Display Preferences… holds everything " +
                "configurable in this build."),
            Off("SCPI Preferences…",
                "Settings for the SCPI server of REQ-API-020, which is Phase 2. The SCPI Log " +
                "window on the Window menu shows the traffic OpenVSA sends to instruments."),
            Item("Display Preferences…"),
            Item("Toolbars…"),
            Off("Manage Registers…",
                "Data registers — measured traces held in memory for maths and comparison — are " +
                "Phase 2, with REQ-TRC-030."),
            Off("Extension Manager…",
                "OpenVSA loads no extensions. The .NET API of REQ-API-001 is how it is driven from " +
                "outside; there is no plug-in host to manage."),
        };

        private static List<ShellMenuEntry> Window() => new List<ShellMenuEntry>
        {
            // Six of REQ-UI-002's eight tool windows. The Markers window is on the Marker menu and
            // the Player window on Acquisition, because that is where REQ-UI-061 lists them - the
            // names here are ToolWindows.NameOf's, not a second spelling of them.
            Item("Output"),
            Item("SCPI Log"),
            Item("Event Log"),
            Item("Contexts"),
            Item("Block Diagram"),
            Item("Macros"),
            Rule(),
            Live("Trace Layout"),
            Off("New Trace Window",
                "Tearing a trace off into a window of its own is REQ-UI-003, and it is a full " +
                "secondary window with its own menus and toolbars rather than a floating panel. " +
                "Phase 2."),
            Item("Resize Traces"),
            Off("Collect Traces",
                "Brings detached trace windows back into the main one. Follows New Trace Window."),
        };

        private static List<ShellMenuEntry> Help() => new List<ShellMenuEntry>
        {
            Item("Help", "F1", "Help (F1)"),
            Item("Dynamic Help", "Ctrl+F1"),
            Off("Getting Started",
                "No getting-started guide is written yet. The one topic this build does carry, " +
                "the demodulation processing order of REQ-DEM-001, is what Help (F1) and Dynamic " +
                "Help show; naming this item after it would be naming a manual after its one " +
                "page."),
            Off("Demos",
                "The demonstration signals of REQ-DEM-001 are Phase 3."),
            Off("Examples",
                "Worked examples belong with the .NET API of REQ-API-001, and are written " +
                "alongside it."),
            Off("API Reference",
                "The API reference is generated from the assemblies' own documentation comments. " +
                "It is not published anywhere this item could open."),
            Off("SCPI Reference",
                "The SCPI server of REQ-API-020 is Phase 2. There is no command set to document."),
            Off("Support",
                "OpenVSA has no support channel to open from a menu. It is a repository with an " +
                "issue tracker, and About says where."),
            Item("Privacy"),
            Item("About"),
        };

        // ---- Constructors, kept short so the table above reads as a list ----------------------

        private static ShellMenuEntry Item(string name, string gesture = null, string spec = null) =>
            new ShellMenuEntry(
                name, ShellMenuEntryKind.Item, null, false, false, spec, gesture, null);

        private static ShellMenuEntry Tick(string name) =>
            new ShellMenuEntry(
                name, ShellMenuEntryKind.Item, null, true, false, null, null, null);

        private static ShellMenuEntry Off(string name, string reason) =>
            new ShellMenuEntry(
                name, ShellMenuEntryKind.Item, reason, false, false, null, null, null);

        private static ShellMenuEntry Menu(string name, params ShellMenuEntry[] children) =>
            new ShellMenuEntry(
                name, ShellMenuEntryKind.Item, null, false, false, null, null, children);

        private static ShellMenuEntry Live(string name, params ShellMenuEntry[] children) =>
            new ShellMenuEntry(
                name, ShellMenuEntryKind.Item, null, false, true, null, null, children);

        private static ShellMenuEntry Rule() =>
            new ShellMenuEntry(
                string.Empty, ShellMenuEntryKind.Separator, null, false, false, null, null, null);

        private static ShellMenuEntry Tools(string name) =>
            new ShellMenuEntry(
                name, ShellMenuEntryKind.EmbeddedToolbar, null, false, false, null, null, null);
    }
}
