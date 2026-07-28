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
                double elapsed = OneLaunch(shellPath);

                if (double.IsNaN(elapsed))
                {
                    return null;
                }

                seconds[i] = elapsed;
                Console.WriteLine("    launch " + (i + 1) + ": " + elapsed.ToString("F2") + " s");
            }

            double mean = 0.0;

            foreach (double s in seconds)
            {
                mean += s;
            }

            mean /= runs;

            double sum = 0.0;

            foreach (double s in seconds)
            {
                sum += (s - mean) * (s - mean);
            }

            return new TargetMeasurement(
                "ColdStartToFirstTrace", mean, Math.Sqrt(sum / Math.Max(1, runs - 1)), runs);
        }

        private static double OneLaunch(string shellPath)
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
                return (DateTime.Now - process.StartTime).TotalSeconds;
            }
            finally
            {
                Close(process);
            }
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
