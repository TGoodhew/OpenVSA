using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows.Automation;
using OpenVSA.PerformanceGate;

namespace OpenVSA.Benchmarks
{
    /// <summary>
    /// <c>REQ-NFR-025</c>: cold start to first trace displayed, against the simulated source.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The real shell, launched as a process, driven through UI Automation.</strong> Every
    /// cheaper approach measures something else. Constructing <c>ShellWindow</c> in the harness
    /// skips assembly load, JIT of the startup path and the WPF theme dictionaries, which is most
    /// of what a cold start is; hooking a stopwatch inside the product would put measurement
    /// scaffolding on the shipping path for a figure that is measurable from outside.
    /// </para>
    /// <para>
    /// The clock starts at the process's own <see cref="Process.StartTime"/>, not when this method
    /// got round to reading it, so scheduling delay in the harness is not charged to the product.
    /// </para>
    /// <para>
    /// "First trace displayed" is taken from the status bar leaving its initial <c>Ready</c>, which
    /// is the shell's own statement that a measurement has produced something. Polling the trace's
    /// pixels would be closer to the words and much further from anything observable through
    /// automation, and it would make the figure depend on the rasteriser rather than on start-up.
    /// </para>
    /// </remarks>
    public static class ColdStartMeasurement
    {
        /// <summary>How long to wait before deciding the shell is never going to get there.</summary>
        private static readonly TimeSpan Patience = TimeSpan.FromSeconds(60.0);

        /// <summary>Measures cold start a number of times and reports the spread.</summary>
        /// <param name="shellPath">The built <c>OpenVSA.exe</c>.</param>
        /// <param name="runs">How many launches to average over.</param>
        /// <returns>The measurement, or <c>null</c> when the shell could not be driven.</returns>
        public static TargetMeasurement Run(string shellPath, int runs = 5)
        {
            if (shellPath == null || !File.Exists(shellPath))
            {
                Console.Error.WriteLine("  REQ-NFR-025 skipped: no shell at " + (shellPath ?? "<null>"));
                return null;
            }

            var seconds = new double[runs];

            for (int i = 0; i < runs; i++)
            {
                var phases = new LaunchPhases();
                double elapsed = OneLaunch(shellPath, phases);

                if (double.IsNaN(elapsed))
                {
                    return null;
                }

                seconds[i] = elapsed;

                // The breakdown on every launch, not only the cold one. The useful question is which
                // phase is longer when cold, and answering it needs both to compare.
                Console.WriteLine(
                    "    launch " + (i + 1) + ": " + elapsed.ToString("F2") + " s  (" +
                    phases.Render() + ")");
            }

            // **Only the first launch is cold.** After it, the assemblies are in the OS file cache
            // and every later launch is a warm start — measured here as 3.29 s then 1.36, 1.39,
            // 1.36, 1.36. Averaging the two populations gives 1.75 s ± 43 %, a figure that
            // describes neither and whose spread is not noise but the difference between two
            // different things being measured.
            //
            // So they are reported apart. The gated number is the warm mean, because it is
            // reproducible to a percent or so and a start-up regression moves it; the cold figure
            // is stated beside it against the requirement's own 3 s, and cannot be repeated in one
            // session without dropping the file cache.
            Console.WriteLine(
                "    cold (first launch): " + seconds[0].ToString("F2") +
                " s against REQ-NFR-025's 3 s" +
                (seconds[0] > 3.0 ? "  — OVER" : string.Empty));

            if (runs < 3)
            {
                Console.Error.WriteLine("  REQ-NFR-025 needs at least three launches to separate cold from warm.");
                return null;
            }

            double mean = 0.0;

            for (int i = 1; i < runs; i++)
            {
                mean += seconds[i];
            }

            mean /= runs - 1;

            double sum = 0.0;

            for (int i = 1; i < runs; i++)
            {
                sum += (seconds[i] - mean) * (seconds[i] - mean);
            }

            // The warm mean is what the regression gate tracks; the COLD figure is what the
            // requirement's 3 s is held against. Passing only the warm mean reported a missed
            // requirement as met -- 1.36 s against 3 s looks comfortable, and the cold start it was
            // standing in for is 3.29 s. See TargetMeasurement.AgainstStated.
            return new TargetMeasurement(
                "ColdStartToFirstTrace",
                mean,
                Math.Sqrt(sum / (runs - 2)),
                runs - 1,
                againstStated: seconds[0]);
        }

        /// <summary>
        /// Where one launch spent its time, in seconds from the process's own start.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A total of 3.29 s against a 3 s requirement says the requirement is missed and nothing
        /// about what to do next, and there are four places it could be going: assembly load and JIT
        /// before a window exists, the front-end registry probing every <c>OpenVSA.Hal.*.dll</c>
        /// beside the shell (including the VISA one, which fails to load where there is no VISA),
        /// the capabilities query behind connecting, and the first frame itself.
        /// </para>
        /// <para>
        /// Every boundary here is observable from outside the process, so nothing is added to the
        /// shipping path to obtain it. That was the constraint that mattered: the alternative was a
        /// stopwatch inside <c>App</c>, and measurement scaffolding on the shipping path for a figure
        /// an automation client can already see is a poor trade.
        /// </para>
        /// </remarks>
        private sealed class LaunchPhases
        {
            internal double WindowAppeared { get; set; }

            internal double MenuPopulated { get; set; }

            internal double SourceConnected { get; set; }

            internal double FirstTrace { get; set; }

            /// <summary>The phases as one line: when the window came up, then each step after it.</summary>
            internal string Render() =>
                "window " + WindowAppeared.ToString("F2") +
                " s, +menu " + (MenuPopulated - WindowAppeared).ToString("F2") +
                " s, +connect " + (SourceConnected - MenuPopulated).ToString("F2") +
                " s, +first frame " + (FirstTrace - SourceConnected).ToString("F2") + " s";
        }

        private static double OneLaunch(string shellPath, LaunchPhases phases)
        {
            var info = new ProcessStartInfo(shellPath)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(shellPath),
            };

            Process process = null;

            try
            {
                process = Process.Start(info);

                if (process == null)
                {
                    return double.NaN;
                }

                process.WaitForInputIdle((int)Patience.TotalMilliseconds);

                AutomationElement window = MainWindow(process);

                if (window == null)
                {
                    Console.Error.WriteLine("  REQ-NFR-025: the shell's main window never appeared.");
                    return double.NaN;
                }

                phases.WindowAppeared = Since(process);

                // The shell opens with no source connected, and REQ-NFR-032 requires the simulator
                // to be *available*, not selected — so getting to a trace means walking the menu
                // the same way a user would. Every step of it is product work: the submenu is
                // populated on open from the front-end registry, and choosing an item runs
                // ConnectAsync and the capabilities query that ranges the settings pane.
                if (!SelectSimulatedSource(window, phases, process))
                {
                    return double.NaN;
                }

                phases.SourceConnected = Since(process);

                if (!Invoke(window, "Apply"))
                {
                    Console.Error.WriteLine("  REQ-NFR-025: no Apply control to start a measurement.");
                    return double.NaN;
                }

                if (!WaitForFirstTrace(window))
                {
                    Console.Error.WriteLine("  REQ-NFR-025: no trace within " + Patience.TotalSeconds + " s.");
                    return double.NaN;
                }

                // From the process's own start, so the harness's own scheduling is not charged to
                // the product.
                phases.FirstTrace = Since(process);

                return phases.FirstTrace;
            }
            finally
            {
                Close(process);
            }
        }

        /// <summary>The display name the simulated front end registers under.</summary>
        private const string SimulatedSource = "Simulated source";

        /// <summary>
        /// Walks Hardware ▸ Instruments… ▸ Simulated source, expanding as it goes.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Expanded rather than found by search. The instruments submenu is filled on
        /// <c>SubmenuOpened</c> from the front-end registry, so its items do not exist in the
        /// automation tree until the menu is opened — a <see cref="TreeScope.Descendants"/> search
        /// from the window finds nothing and would look like a missing front end rather than an
        /// unopened menu.
        /// </para>
        /// <para>
        /// Found by name, because these items are built in code with a header and no automation id.
        /// That is worth fixing on the shell side one day; until then the name is the only handle.
        /// </para>
        /// </remarks>
        private static bool SelectSimulatedSource(
            AutomationElement window, LaunchPhases phases, Process process)
        {
            AutomationElement hardware = Expand(window, "Hardware");

            if (hardware == null)
            {
                Console.Error.WriteLine("  REQ-NFR-025: no Hardware menu.");
                return false;
            }

            // "Instruments…" ends in a U+2026 ellipsis. Matched by prefix rather than written as
            // a literal: a source file whose encoding is guessed wrong turns that character into
            // something else, and the failure would look like a missing menu.
            AutomationElement instruments = Expand(hardware, "Instruments");

            if (instruments == null)
            {
                Console.Error.WriteLine("  REQ-NFR-025: no Instruments submenu.");
                return false;
            }

            AutomationElement source = ByName(instruments, SimulatedSource);

            // Here rather than after the click: reaching this point means the registry has probed
            // every OpenVSA.Hal.*.dll beside the shell and the submenu has been filled from what it
            // found, which is the phase a failed VISA load would show up in.
            phases.MenuPopulated = Since(process);

            if (source == null)
            {
                // The registry reported no simulated provider. That is a real failure and not a
                // slow one: REQ-NFR-032 requires the simulator to be available with no hardware.
                Console.Error.WriteLine(
                    "  REQ-NFR-025: '" + SimulatedSource + "' is not in the instruments menu.");
                return false;
            }

            // **Invoke, not Toggle.** A checkable MenuItem exposes both, and only Invoke raises
            // Click — TogglePattern.Toggle() flips the check state and the handler never runs, so
            // the menu closes, the tick appears, and nothing connects. Measured directly against
            // the running shell: Toggle left the status bar on "Ready" with Apply disabled, Invoke
            // produced "Simulated source connected" with Apply enabled.
            //
            // This is the same trap as the toolbar toggles, from the other side: there a
            // ToggleButton raised Checked and never Click. WPF's automation peers do not route
            // toggling through the click path, and anything that assumes they do fails silently.
            object pattern;

            if (source.TryGetCurrentPattern(InvokePattern.Pattern, out pattern))
            {
                ((InvokePattern)pattern).Invoke();
                return true;
            }

            if (source.TryGetCurrentPattern(TogglePattern.Pattern, out pattern))
            {
                ((TogglePattern)pattern).Toggle();
                return true;
            }

            Console.Error.WriteLine("  REQ-NFR-025: the source item cannot be activated.");
            return false;
        }

        /// <summary>Seconds since a process started, by its own clock.</summary>
        private static double Since(Process process) =>
            (DateTime.Now - process.StartTime).TotalSeconds;

        /// <summary>Expands a named menu item under a parent and returns it.</summary>
        private static AutomationElement Expand(AutomationElement parent, string name)
        {
            AutomationElement item = ByName(parent, name);

            if (item == null)
            {
                return null;
            }

            object pattern;

            if (!item.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out pattern))
            {
                return item;
            }

            var expander = (ExpandCollapsePattern)pattern;
            var clock = Stopwatch.StartNew();

            while (clock.Elapsed < Patience)
            {
                try
                {
                    expander.Expand();
                    return item;
                }
                catch (InvalidOperationException)
                {
                    // Not expandable yet: the parent menu is still opening. This also covers
                    // ElementNotEnabledException, which derives from it.
                }

                Thread.Sleep(20);
            }

            return item;
        }

        /// <summary>
        /// A descendant whose name starts with a prefix, once it exists and can be acted on.
        /// </summary>
        /// <remarks>
        /// By prefix because menu headers carry trailing punctuation — "Instruments…" — and by
        /// "can be acted on" because the tree holds two elements per menu item: the item and its
        /// text. The text child supports only SynchronizedInput, so a search that took the first
        /// match by name would find something that cannot be clicked about half the time.
        /// </remarks>
        private static AutomationElement ByName(AutomationElement root, string prefix)
        {
            var clock = Stopwatch.StartNew();

            while (clock.Elapsed < Patience)
            {
                try
                {
                    AutomationElementCollection all = root.FindAll(
                        TreeScope.Descendants, Condition.TrueCondition);

                    foreach (AutomationElement candidate in all)
                    {
                        string name = candidate.Current.Name;

                        if (name == null ||
                            !name.StartsWith(prefix, StringComparison.Ordinal))
                        {
                            continue;
                        }

                        object ignored;

                        if (candidate.TryGetCurrentPattern(InvokePattern.Pattern, out ignored) ||
                            candidate.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out ignored))
                        {
                            return candidate;
                        }
                    }
                }
                catch (ElementNotAvailableException)
                {
                }

                Thread.Sleep(20);
            }

            return null;
        }

        /// <summary>The process's main window, once it has one.</summary>
        private static AutomationElement MainWindow(Process process)
        {
            var clock = Stopwatch.StartNew();

            while (clock.Elapsed < Patience)
            {
                process.Refresh();

                if (process.HasExited)
                {
                    return null;
                }

                IntPtr handle = process.MainWindowHandle;

                if (handle != IntPtr.Zero)
                {
                    try
                    {
                        return AutomationElement.FromHandle(handle);
                    }
                    catch (ElementNotAvailableException)
                    {
                        // The window existed a moment ago and does not now: still starting.
                    }
                }

                Thread.Sleep(20);
            }

            return null;
        }

        /// <summary>Invokes a control by automation id.</summary>
        private static bool Invoke(AutomationElement window, string automationId)
        {
            AutomationElement element = Find(window, automationId);

            if (element == null)
            {
                return false;
            }

            // Present is not the same as ready. The control exists as soon as the window is built
            // and only becomes enabled once a front end has been negotiated, so invoking on sight
            // throws ElementNotEnabled — and the wait is itself part of the cold start being
            // measured, which is why it is inside the timed region rather than before it.
            var clock = Stopwatch.StartNew();

            while (clock.Elapsed < Patience)
            {
                try
                {
                    object pattern;

                    if (element.Current.IsEnabled &&
                        element.TryGetCurrentPattern(InvokePattern.Pattern, out pattern))
                    {
                        ((InvokePattern)pattern).Invoke();
                        return true;
                    }
                }
                catch (ElementNotEnabledException)
                {
                    // Became disabled between the check and the call: try again.
                }
                catch (ElementNotAvailableException)
                {
                    return false;
                }

                Thread.Sleep(20);
            }

            return false;
        }

        private static AutomationElement Find(AutomationElement root, string automationId)
        {
            var clock = Stopwatch.StartNew();

            while (clock.Elapsed < Patience)
            {
                try
                {
                    AutomationElement found = root.FindFirst(
                        TreeScope.Descendants,
                        new PropertyCondition(AutomationElement.AutomationIdProperty, automationId));

                    if (found != null)
                    {
                        return found;
                    }
                }
                catch (ElementNotAvailableException)
                {
                }

                Thread.Sleep(20);
            }

            return null;
        }

        /// <summary>
        /// Waits until the status bar stops saying <c>Ready</c>, which is the shell saying a
        /// measurement has produced something.
        /// </summary>
        private static bool WaitForFirstTrace(AutomationElement window)
        {
            var clock = Stopwatch.StartNew();

            while (clock.Elapsed < Patience)
            {
                AutomationElement status = null;

                try
                {
                    status = window.FindFirst(
                        TreeScope.Descendants,
                        new PropertyCondition(AutomationElement.AutomationIdProperty, "StatusText"));
                }
                catch (ElementNotAvailableException)
                {
                }

                if (status != null)
                {
                    string text = TextOf(status);

                    if (!string.IsNullOrEmpty(text) &&
                        !text.Equals("Ready", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }

                Thread.Sleep(10);
            }

            return false;
        }

        private static string TextOf(AutomationElement element)
        {
            try
            {
                string name = element.Current.Name;

                if (!string.IsNullOrEmpty(name))
                {
                    return name;
                }

                object pattern;

                if (element.TryGetCurrentPattern(ValuePattern.Pattern, out pattern))
                {
                    return ((ValuePattern)pattern).Current.Value;
                }
            }
            catch (ElementNotAvailableException)
            {
            }

            return null;
        }

        private static void Close(Process process)
        {
            if (process == null)
            {
                return;
            }

            try
            {
                if (!process.HasExited)
                {
                    process.CloseMainWindow();

                    if (!process.WaitForExit(5000))
                    {
                        // A shell that will not close would otherwise leave every launch of this
                        // measurement running at once.
                        process.Kill();
                        process.WaitForExit(5000);
                    }
                }
            }
            catch (InvalidOperationException)
            {
            }
            finally
            {
                process.Dispose();
            }
        }
    }
}
