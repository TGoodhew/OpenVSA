using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using OpenVSA.Core.Threading;
using OpenVSA.SoakGate;
using OpenVSA.Ui;
using OpenVSA.Ui.ToolWindows;

namespace OpenVSA.Soak
{
    /// <summary>
    /// The long-duration soak host of <c>REQ-TST-009</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>All the measuring, none of the deciding.</strong> This runs the shell against the
    /// simulator, creates and destroys windows and traces, and writes a sample a minute to a log.
    /// Every judgement is <c>OpenVSA.SoakGate</c>'s, which is exercised from both sides by a unit
    /// suite in milliseconds — because a rule that only an eight-hour run can reach is a rule nobody
    /// can test.
    /// </para>
    /// <para>
    /// <strong>The log is appended and flushed as it goes.</strong> A run that dies at hour seven has
    /// left seven hours of evidence, and <c>--judge</c> will read it back. That is the difference
    /// between a soak that can be diagnosed and one that has to be repeated overnight.
    /// </para>
    /// </remarks>
    public static class Program
    {
        /// <summary>Runs a soak, or judges a log from one.</summary>
        /// <param name="args">Command line; <c>--help</c> lists it.</param>
        /// <returns>Zero when every claim holds.</returns>
        [STAThread]
        public static int Main(string[] args)
        {
            var options = Options.Parse(args);

            if (options == null)
            {
                Options.Usage();
                return 2;
            }

            if (options.JudgeOnly != null)
            {
                return Report(
                    new EnduranceGate(options.Hours).Judge(SoakLog.ReadFile(options.JudgeOnly)),
                    options.JudgeOnly);
            }

            // As App does, and for the same reason: every layer below asserts that it is not doing
            // DSP or I/O on this thread, and the assertion needs to know which thread it is.
            ThreadAffinity.MarkUiThread();

            return new Soak(options).Run();
        }

        internal static int Report(SoakReport report, string logPath)
        {
            Console.WriteLine();
            Console.WriteLine(report.Render());

            string reportPath = Path.ChangeExtension(logPath, ".report.txt");

            try
            {
                File.WriteAllText(reportPath, report.Render(), Encoding.UTF8);
                Console.WriteLine("  report  " + reportPath);
                Console.WriteLine("  samples " + logPath);
            }
            catch (IOException failure)
            {
                Console.WriteLine("  (the report could not be written: " + failure.Message + ")");
            }

            return report.Passed ? 0 : 1;
        }
    }

    /// <summary>What the host was asked to do.</summary>
    internal sealed class Options
    {
        internal double Hours { get; private set; } = EnduranceGate.RequiredHours;

        internal double SampleSeconds { get; private set; } = 60.0;

        internal double CycleMinutes { get; private set; } = 5.0;

        internal int CollectEvery { get; private set; } = 10;

        internal string LogPath { get; private set; }

        internal string JudgeOnly { get; private set; }

        internal static Options Parse(string[] args)
        {
            var options = new Options();

            for (int i = 0; i < args.Length; i++)
            {
                string argument = args[i];
                string value = i + 1 < args.Length ? args[i + 1] : null;

                switch (argument)
                {
                    case "--hours":
                        if (!TryRead(value, out double hours) || hours <= 0.0) { return null; }
                        options.Hours = hours;
                        i++;
                        break;

                    case "--minutes":
                        // For rehearsing the harness itself. The gate is told the same duration, so a
                        // two-minute run is judged as a two-minute run rather than passed as a soak.
                        if (!TryRead(value, out double minutes) || minutes <= 0.0) { return null; }
                        options.Hours = minutes / 60.0;
                        i++;
                        break;

                    case "--sample-seconds":
                        if (!TryRead(value, out double seconds) || seconds <= 0.0) { return null; }
                        options.SampleSeconds = seconds;
                        i++;
                        break;

                    case "--cycle-minutes":
                        if (!TryRead(value, out double cycle) || cycle <= 0.0) { return null; }
                        options.CycleMinutes = cycle;
                        i++;
                        break;

                    case "--collect-every":
                        if (!int.TryParse(value, out int every) || every <= 0) { return null; }
                        options.CollectEvery = every;
                        i++;
                        break;

                    case "--log":
                        if (value == null) { return null; }
                        options.LogPath = value;
                        i++;
                        break;

                    case "--judge":
                        if (value == null) { return null; }
                        options.JudgeOnly = value;
                        i++;
                        break;

                    case "--help":
                    case "-h":
                        return null;

                    default:
                        Console.WriteLine("Unrecognised argument '" + argument + "'.");
                        return null;
                }
            }

            if (options.LogPath == null)
            {
                options.LogPath = Path.Combine(
                    Path.GetDirectoryName(typeof(Options).Assembly.Location) ?? ".",
                    "soak-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) +
                    ".tsv");
            }

            return options;
        }

        internal static void Usage()
        {
            Console.WriteLine("OpenVSA.Soak — the long-duration soak of REQ-TST-009.");
            Console.WriteLine();
            Console.WriteLine("  --hours N            how long to run (default 8, the requirement's own)");
            Console.WriteLine("  --minutes N          the same in minutes, for rehearsing the harness");
            Console.WriteLine("  --sample-seconds N   how often to sample (default 60)");
            Console.WriteLine("  --cycle-minutes N    how often to create and destroy windows (default 5)");
            Console.WriteLine("  --collect-every N    force a collection every Nth sample (default 10)");
            Console.WriteLine("  --log PATH           where to write the samples");
            Console.WriteLine("  --judge PATH         judge an existing log and exit, running nothing");
            Console.WriteLine();
            Console.WriteLine("Exits 0 when every claim holds, 1 when one does not, 2 on a bad command line.");
        }

        private static bool TryRead(string text, out double value) =>
            double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>One soak run.</summary>
    internal sealed class Soak
    {
        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetGuiResources(IntPtr process, int flags);

        private const int GdiObjects = 0;
        private const int UserObjects = 1;

        /// <summary>
        /// The simulated front end's name, as its <c>FrontEndProviderAttribute</c> declares it.
        /// </summary>
        /// <remarks>
        /// Spelled rather than referenced: <c>REQ-ARC-001</c> keeps <c>OpenVSA.Hal.Sim</c> off every
        /// compile-time reference from L3 up, so this host deploys it into <c>FrontEnds\</c> instead.
        /// Named rather than "whichever source is offered first", because a VISA front end can reach
        /// real instruments on this bench and a soak must not spend a night driving one.
        /// </remarks>
        private const string SimulatedSourceName = "Simulated source";

        private readonly Options _options;
        private readonly Stopwatch _clock = new Stopwatch();
        private readonly Process _self = Process.GetCurrentProcess();

        private ShellWindow _shell;
        private ShellDriver _driver;
        private StreamWriter _log;
        private int _samples;
        private int _cycles;
        private int _faults;
        private double _nextCycleSeconds;

        internal Soak(Options options)
        {
            _options = options;
        }

        internal int Run()
        {
            Console.WriteLine("OpenVSA soak (REQ-TST-009)");
            Console.WriteLine("  duration  " + _options.Hours.ToString("0.###", CultureInfo.InvariantCulture) + " hours");
            Console.WriteLine("  sampling  every " + _options.SampleSeconds.ToString("0.#", CultureInfo.InvariantCulture) + " s");
            Console.WriteLine("  cycling   every " + _options.CycleMinutes.ToString("0.#", CultureInfo.InvariantCulture) + " min");
            Console.WriteLine("  log       " + _options.LogPath);
            Console.WriteLine();

            var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };

            _log = new StreamWriter(_options.LogPath, false, new UTF8Encoding(false))
            {
                AutoFlush = true,
            };

            using (_log)
            {
                _log.Write(SoakLog.Preamble(
                    _options.Hours.ToString("0.###", CultureInfo.InvariantCulture) +
                    " hours requested, sampling every " +
                    _options.SampleSeconds.ToString("0.#", CultureInfo.InvariantCulture) +
                    " s, started " + DateTime.Now.ToString("u", CultureInfo.InvariantCulture)));

                application.Startup += (sender, e) => Begin();
                application.Run();
            }

            SoakReport report = new EnduranceGate(_options.Hours).Judge(SoakLog.ReadFile(_options.LogPath));

            if (_faults > 0)
            {
                // Said out loud rather than buried. A run that could not take some of its samples
                // has measured less than it claims to, and the gate cannot see that from the log.
                Console.WriteLine();
                Console.WriteLine("  " + _faults + " sample(s) or cycle(s) failed during the run.");
            }

            return Program.Report(report, _options.LogPath);
        }

        private void Begin()
        {
            _shell = new ShellWindow
            {
                // Nothing modal: a soak that stopped on a file picker at hour two would be a night
                // wasted, and Interactive is the shell's own switch for exactly that.
                Interactive = false,

                // The user's layout, colours and dialog modes are not this run's to rewrite.
                PersistPreferences = false,

                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -4000.0,
                Top = -4000.0,
                ShowInTaskbar = false,
            };

            _shell.Show();
            _driver = new ShellDriver(_shell, _shell.MenuBar);

            Connect();

            _clock.Start();
            _nextCycleSeconds = _options.CycleMinutes * 60.0;

            // A timer rather than a loop: the dispatcher has to keep pumping, because a shell that
            // is not drawing is not the thing the requirement is about.
            var ticker = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromSeconds(_options.SampleSeconds),
            };

            ticker.Tick += (sender, e) => Tick(ticker);
            ticker.Start();

            Sample();
        }

        private void Connect()
        {
            _driver.Invoke(_driver.MenuPath("Hardware", "Instruments", SimulatedSourceName));

            // Connecting is awaited on a worker, so the pane comes live a moment later.
            if (!WaitFor(() => _shell.ActiveFrontEnd != null, TimeSpan.FromSeconds(30.0)))
            {
                throw new InvalidOperationException(
                    "The simulated source did not connect. Is OpenVSA.Hal.Sim in FrontEnds\\?");
            }

            _driver.Invoke(_driver.MenuPath("Acquisition", "Control", "Start"));

            if (!WaitFor(() => _shell.FramesDrawn > 0L, TimeSpan.FromSeconds(60.0)))
            {
                throw new InvalidOperationException(
                    "No frame was drawn in the first minute, so there is nothing to soak.");
            }

            Console.WriteLine(
                "  connected to " + _shell.ActiveFrontEnd.DisplayName + " and measuring.");
            Console.WriteLine();
        }

        private void Tick(DispatcherTimer ticker)
        {
            try
            {
                if (_clock.Elapsed.TotalSeconds >= _nextCycleSeconds)
                {
                    Cycle();
                    _nextCycleSeconds += _options.CycleMinutes * 60.0;
                }

                Sample();
            }
            catch (Exception failure)
            {
                // One bad sample must not end the night. Counted, reported, and carried on from.
                _faults++;
                Console.WriteLine(
                    "  [" + Elapsed() + "] " + failure.GetType().Name + ": " + failure.Message);
            }

            if (_clock.Elapsed.TotalHours >= _options.Hours)
            {
                ticker.Stop();
                Finish();
            }
        }

        /// <summary>
        /// Creates and destroys windows and traces, which is what the handle claim is about.
        /// </summary>
        /// <remarks>
        /// Three traces and two tool windows, opened and then closed again. <c>REQ-TST-009</c> asks
        /// that the counts "return to their starting range after windows and traces are created and
        /// destroyed repeatedly" — so something has to be repeatedly created and destroyed, and a
        /// soak that only sat there would answer nothing.
        /// </remarks>
        private void Cycle()
        {
            ToolBar traces = _driver.Toolbar("Trace");
            UIElement newTrace = ShellDriver.Control(traces, "New");
            UIElement closeTrace = ShellDriver.Control(traces, "Close");

            for (int i = 0; i < 3; i++)
            {
                _driver.Invoke(newTrace);
            }

            Toggle("Window", "Output");
            Toggle("Marker", "Markers Window");

            Toggle("Window", "Output");
            Toggle("Marker", "Markers Window");

            // Back to one: the shell refuses to close the last trace, so this leaves the count where
            // it started rather than trying to close it.
            while (_shell.DocumentArea.Traces.Count > 1)
            {
                int before = _shell.DocumentArea.Traces.Count;

                _driver.Invoke(closeTrace);

                if (_shell.DocumentArea.Traces.Count >= before)
                {
                    break;
                }
            }

            _cycles++;
        }

        private void Toggle(params string[] path)
        {
            _driver.Invoke(_driver.MenuPath(path));
        }

        private void Sample()
        {
            bool collect = _samples % _options.CollectEvery == 0;
            long collected = 0L;

            if (collect)
            {
                // A full blocking collection, so the figure is the floor rather than the sawtooth.
                // Twice, because the first pass runs finalizers that may release more.
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();

                collected = GC.GetTotalMemory(false);
            }

            _self.Refresh();

            var sample = new SoakSample(
                _clock.Elapsed.TotalSeconds,
                GC.GetTotalMemory(false),
                collected,
                _self.PrivateMemorySize64,
                _self.HandleCount,
                GetGuiResources(_self.Handle, GdiObjects),
                GetGuiResources(_self.Handle, UserObjects),
                _shell.FramesDrawn,
                _shell.FramesDropped,
                OpenVSA.Core.SampleBufferPool.Instance.RetainedBuffers,
                OpenVSA.Core.SampleBufferPool.Instance.RetainedBytes,
                _shell.DocumentArea.Traces.Count,
                _cycles);

            _log.Write(SoakLog.Line(sample));
            _samples++;

            // One line an hour to the console, so a night's run leaves a readable trail without
            // scrolling four hundred lines past whoever looks at it in the morning.
            if (_samples == 1 || _clock.Elapsed.TotalSeconds % 3600.0 < _options.SampleSeconds)
            {
                Console.WriteLine(
                    "  [" + Elapsed() + "] " +
                    Mib(sample.PrivateBytes) + " private, " +
                    Mib(sample.ManagedBytes) + " managed, " +
                    sample.Handles + " handles, " +
                    sample.GdiObjects + " GDI, " +
                    sample.FramesDrawn.ToString("N0", CultureInfo.InvariantCulture) + " frames, " +
                    sample.Cycles + " cycles");
            }
        }

        private void Finish()
        {
            try
            {
                _driver.Invoke(_driver.MenuPath("Acquisition", "Control", "Stop"));
                _shell.Close();
            }
            catch (Exception failure)
            {
                _faults++;
                Console.WriteLine("  closing: " + failure.GetType().Name + ": " + failure.Message);
            }

            Application.Current.Shutdown();
        }

        /// <summary>Pumps the dispatcher until a condition holds, or gives up.</summary>
        private bool WaitFor(Func<bool> satisfied, TimeSpan limit)
        {
            var waited = Stopwatch.StartNew();

            while (!satisfied())
            {
                if (waited.Elapsed > limit)
                {
                    return false;
                }

                _driver.Settle();
                Thread.Sleep(10);
            }

            return true;
        }

        private string Elapsed() =>
            _clock.Elapsed.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);

        private static string Mib(long bytes) =>
            (bytes / (1024.0 * 1024.0)).ToString("0", CultureInfo.InvariantCulture) + " MiB";
    }
}
