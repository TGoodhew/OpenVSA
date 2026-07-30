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

            if (options.JudgeCyclesOnly != null)
            {
                return ReportRetention(
                    new RetentionGate().Judge(SoakLog.ReadFile(options.JudgeCyclesOnly)),
                    options.JudgeCyclesOnly);
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

        /// <summary>
        /// Prints and files a retention run's conclusion.
        /// </summary>
        /// <param name="report">What the gate concluded.</param>
        /// <param name="logPath">The log it read.</param>
        /// <returns>
        /// Zero when the run decided the question, either way. A refutation is a result: this run
        /// exists to choose between two explanations, and it has done its job when it chooses.
        /// </returns>
        internal static int ReportRetention(RetentionReport report, string logPath)
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

            return report.Decided ? 0 : 1;
        }
    }

    /// <summary>Which part of a create-and-destroy cycle to drive.</summary>
    /// <remarks>
    /// Attribution. If cycles do retain memory, the next question is which half — and a run that can
    /// drive the traces alone and the tool windows alone answers it without anyone reading code and
    /// guessing.
    /// </remarks>
    internal enum CycleParts
    {
        /// <summary>Three traces and two tool windows, as the soak's own cycle does.</summary>
        All = 0,

        /// <summary>Three traces opened and closed, and no tool windows.</summary>
        Traces,

        /// <summary>Two tool windows shown and hidden, and no traces.</summary>
        Tools,

        /// <summary>
        /// Nothing at all: the control experiment, which measures the measuring.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A run of this kind drives no windows and no traces, so every sample sees a shell in the
        /// same state as the last one. Anything the fit then finds is retained by the sampling — by
        /// <c>Process.Refresh</c>, by the forced collection, by writing the log — and not by the
        /// product.
        /// </para>
        /// <para>
        /// <strong>Why it is needed.</strong> Two runs over the same 5,000 cycles and the same six
        /// minutes, differing only in taking 60 samples or 619, disagreed about the slope: −0.9 ±1.7
        /// against +4.6 ±0.5 bytes a cycle. A difference that depends on how often it is measured
        /// belongs to the measuring, and until it is subtracted, every figure this harness reports
        /// about the product carries it.
        /// </para>
        /// </remarks>
        None,
    }

    /// <summary>What the host was asked to do.</summary>
    internal sealed class Options
    {
        internal double Hours { get; private set; } = EnduranceGate.RequiredHours;

        internal double SampleSeconds { get; private set; } = 60.0;

        internal double CycleMinutes { get; private set; } = 5.0;

        internal int CollectEvery { get; private set; } = 10;

        /// <summary>
        /// Cycles to drive back to back, or zero for a time-based soak.
        /// </summary>
        /// <remarks>
        /// This is the switch between the two runs the harness can do. A soak spends a stated time
        /// and drives cycles occasionally; a retention run drives a stated number of cycles as fast
        /// as the shell allows and spends whatever time that takes. Only the second can separate a
        /// rise per cycle from a rise per hour — see <c>RetentionGate</c> for why.
        /// </remarks>
        internal int Cycles { get; private set; }

        internal CycleParts CycleParts { get; private set; } = CycleParts.All;

        internal int CycleSamples { get; private set; } = 40;

        /// <summary>
        /// Frames a second to ask the engine for, or zero to leave its own target alone.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The second thing the eight-hour run could not separate. Its managed floor rose 0.0106
        /// MiB/hour, and at a fixed 60 frames a second "per hour" and "per frame" are the same
        /// straight line — exactly as "per hour" and "per cycle" were, and for the same reason.
        /// Raising the rate breaks that line: if the rise is per frame it scales, and shows up in
        /// half an hour instead of eight.
        /// </para>
        /// <para>
        /// <strong>A number, and not simply "unbounded".</strong> The first attempt set the engine's
        /// target to zero, which is what its pacer takes for no limit. The pump then posted to the
        /// dispatcher at <c>Render</c> priority faster than it drained, and the sampling ticker —
        /// deliberately <c>Background</c>, so that measuring perturbs as little as possible — did
        /// not run <em>once</em> in fifteen minutes. The run burned four cores and wrote a single
        /// sample, the one taken before the timer started. Any rate the pacer still sleeps between
        /// leaves the queue somewhere to drain, so the lever is a rate rather than a switch.
        /// </para>
        /// <para>
        /// The reading is one sided, and worth saying so: a rise that scales with the rate
        /// identifies frames, while a rise that does not has only failed to identify them at this
        /// run's length.
        /// </para>
        /// </remarks>
        internal double RateHz { get; private set; }

        internal string LogPath { get; private set; }

        internal string JudgeOnly { get; private set; }

        internal string JudgeCyclesOnly { get; private set; }

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

                    case "--cycles":
                        if (!int.TryParse(value, out int count) || count <= 0) { return null; }
                        options.Cycles = count;
                        i++;
                        break;

                    case "--cycle-parts":
                        if (!TryReadParts(value, out CycleParts parts)) { return null; }
                        options.CycleParts = parts;
                        i++;
                        break;

                    case "--cycle-samples":
                        if (!int.TryParse(value, out int wanted) || wanted < 3) { return null; }
                        options.CycleSamples = wanted;
                        i++;
                        break;

                    case "--rate":
                        if (!TryRead(value, out double rate) || rate <= 0.0) { return null; }
                        options.RateHz = rate;
                        i++;
                        break;

                    case "--judge":
                        if (value == null) { return null; }
                        options.JudgeOnly = value;
                        i++;
                        break;

                    case "--judge-cycles":
                        if (value == null) { return null; }
                        options.JudgeCyclesOnly = value;
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

            if (options.JudgeOnly != null && options.JudgeCyclesOnly != null)
            {
                Console.WriteLine("One log is judged by one gate. Give --judge or --judge-cycles.");
                return null;
            }

            if (options.LogPath == null)
            {
                options.LogPath = Path.Combine(
                    Path.GetDirectoryName(typeof(Options).Assembly.Location) ?? ".",
                    (options.Cycles > 0 ? "retention-" : "soak-") +
                    DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) +
                    ".tsv");
            }

            return options;
        }

        private static bool TryReadParts(string text, out CycleParts parts)
        {
            parts = CycleParts.All;

            if (text == null)
            {
                return false;
            }

            switch (text.ToLowerInvariant())
            {
                case "all": parts = CycleParts.All; return true;
                case "traces": parts = CycleParts.Traces; return true;
                case "tools": parts = CycleParts.Tools; return true;
                case "none": parts = CycleParts.None; return true;
                default:
                    Console.WriteLine("--cycle-parts takes all, traces, tools or none.");
                    return false;
            }
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
            Console.WriteLine("  --rate N             frames a second to ask for, to tell per-frame from per-hour");
            Console.WriteLine("  --log PATH           where to write the samples");
            Console.WriteLine("  --judge PATH         judge an existing soak log and exit, running nothing");
            Console.WriteLine();
            Console.WriteLine("A retention run instead — cycles as fast as the shell allows, to tell a rise");
            Console.WriteLine("per cycle from a rise per hour (see RetentionGate):");
            Console.WriteLine();
            Console.WriteLine("  --cycles N           drive N cycles back to back rather than soaking");
            Console.WriteLine("  --cycle-parts WHICH  all (default), traces, tools, or none for a control");
            Console.WriteLine("  --cycle-samples N    how many times to measure the floor (default 40)");
            Console.WriteLine("  --judge-cycles PATH  judge an existing retention log and exit");
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
            if (_options.Cycles > 0)
            {
                Console.WriteLine("OpenVSA retention run (REQ-TST-009, diagnosing #356)");
                Console.WriteLine("  cycles    " + _options.Cycles.ToString("N0", CultureInfo.InvariantCulture) + ", back to back");
                Console.WriteLine("  driving   " + _options.CycleParts.ToString().ToLowerInvariant());
                Console.WriteLine("  sampling  " + _options.CycleSamples + " measurements of the collected floor");
                Console.WriteLine("  log       " + _options.LogPath);
                Console.WriteLine();
            }
            else
            {
                Console.WriteLine("OpenVSA soak (REQ-TST-009)");
                Console.WriteLine("  duration  " + _options.Hours.ToString("0.###", CultureInfo.InvariantCulture) + " hours");
                Console.WriteLine("  sampling  every " + _options.SampleSeconds.ToString("0.#", CultureInfo.InvariantCulture) + " s");
                Console.WriteLine("  cycling   every " + _options.CycleMinutes.ToString("0.#", CultureInfo.InvariantCulture) + " min");
                Console.WriteLine("  log       " + _options.LogPath);
                Console.WriteLine();
            }

            var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };

            _log = new StreamWriter(_options.LogPath, false, new UTF8Encoding(false))
            {
                AutoFlush = true,
            };

            using (_log)
            {
                _log.Write(SoakLog.Preamble(_options.Cycles > 0
                    ? _options.Cycles.ToString(CultureInfo.InvariantCulture) +
                      " cycles requested (" + _options.CycleParts.ToString().ToLowerInvariant() +
                      "), started " + DateTime.Now.ToString("u", CultureInfo.InvariantCulture)
                    : _options.Hours.ToString("0.###", CultureInfo.InvariantCulture) +
                      " hours requested, sampling every " +
                      _options.SampleSeconds.ToString("0.#", CultureInfo.InvariantCulture) +
                      " s, started " + DateTime.Now.ToString("u", CultureInfo.InvariantCulture)));

                application.Startup += (sender, e) => Begin();
                application.Run();
            }

            if (_faults > 0)
            {
                // Said out loud rather than buried. A run that could not take some of its samples
                // has measured less than it claims to, and the gate cannot see that from the log.
                Console.WriteLine();
                Console.WriteLine("  " + _faults + " sample(s) or cycle(s) failed during the run.");
            }

            if (_options.Cycles > 0)
            {
                return Program.ReportRetention(
                    new RetentionGate().Judge(SoakLog.ReadFile(_options.LogPath)), _options.LogPath);
            }

            return Program.Report(
                new EnduranceGate(_options.Hours).Judge(SoakLog.ReadFile(_options.LogPath)),
                _options.LogPath);
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

            if (_options.Cycles > 0)
            {
                Retention();
                return;
            }

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

        /// <summary>
        /// Drives cycles back to back, measuring the collected floor as it goes.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A loop rather than a timer, and deliberately the opposite choice from the soak's. The
        /// soak spends a stated time and needs the dispatcher to go on drawing between samples; this
        /// spends a stated number of cycles and wants the time between them as short as possible, so
        /// that elapsed hours — and anything that grows with them — cannot account for what it sees.
        /// The dispatcher still pumps, because every invoke settles it.
        /// </para>
        /// <para>
        /// <strong>The acquisition is left running.</strong> It makes the run slower and it adds
        /// nothing this is measuring, but it is what the eight-hour run did, and a cycle driven in a
        /// quieter shell is not the cycle whose retention is in question.
        /// </para>
        /// </remarks>
        private void Retention()
        {
            int every = Math.Max(1, _options.Cycles / Math.Max(1, _options.CycleSamples - 1));

            // Cycle zero, so the fit has a point before anything was created and destroyed.
            Sample(forceCollect: true);

            for (int i = 1; i <= _options.Cycles; i++)
            {
                try
                {
                    Cycle(_options.CycleParts);
                }
                catch (Exception failure)
                {
                    _faults++;
                    Console.WriteLine(
                        "  [cycle " + i + "] " + failure.GetType().Name + ": " + failure.Message);
                }

                if (i % every == 0 || i == _options.Cycles)
                {
                    Sample(forceCollect: true);

                    Console.WriteLine(
                        "  [" + Elapsed() + "] cycle " +
                        i.ToString("N0", CultureInfo.InvariantCulture) + " of " +
                        _options.Cycles.ToString("N0", CultureInfo.InvariantCulture) + ", " +
                        Mib(GC.GetTotalMemory(false)) + " managed");
                }
            }

            Finish();
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

            if (_options.RateHz > 0.0)
            {
                if (_shell.Engine == null)
                {
                    throw new InvalidOperationException(
                        "The rate cannot be raised: no engine is running. A run that quietly " +
                        "stayed at 60 frames a second would answer the wrong question.");
                }

                // Set after the first frame, so the measurement has been through Start at the rate
                // everything else was measured at. Not zero -- see Options.RateHz for what that did.
                _shell.Engine.TargetUpdatesPerSecond = _options.RateHz;
                Console.WriteLine(
                    "  frame rate raised to " +
                    _options.RateHz.ToString("0.#", CultureInfo.InvariantCulture) + " a second.");
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
                    Cycle(CycleParts.All);
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
        /// <param name="parts">Which half of the cycle to drive.</param>
        /// <remarks>
        /// Three traces and two tool windows, opened and then closed again. <c>REQ-TST-009</c> asks
        /// that the counts "return to their starting range after windows and traces are created and
        /// destroyed repeatedly" — so something has to be repeatedly created and destroyed, and a
        /// soak that only sat there would answer nothing.
        /// </remarks>
        private void Cycle(CycleParts parts)
        {
            bool traces = parts == CycleParts.All || parts == CycleParts.Traces;
            bool tools = parts == CycleParts.All || parts == CycleParts.Tools;
            UIElement closeTrace = null;

            if (traces)
            {
                ToolBar bar = _driver.Toolbar("Trace");
                UIElement newTrace = ShellDriver.Control(bar, "New");

                closeTrace = ShellDriver.Control(bar, "Close");

                for (int i = 0; i < 3; i++)
                {
                    _driver.Invoke(newTrace);
                }
            }

            if (tools)
            {
                Toggle("Window", "Output");
                Toggle("Marker", "Markers Window");

                Toggle("Window", "Output");
                Toggle("Marker", "Markers Window");
            }

            if (parts == CycleParts.None)
            {
                // The control still lets the dispatcher run. A shell that stopped drawing between
                // samples would not be the same shell the other runs measured, and the comparison
                // would be with something else.
                _driver.Settle();
            }

            if (traces)
            {
                // Back to one: the shell refuses to close the last trace, so this leaves the count
                // where it started rather than trying to close it.
                while (_shell.DocumentArea.Traces.Count > 1)
                {
                    int before = _shell.DocumentArea.Traces.Count;

                    _driver.Invoke(closeTrace);

                    if (_shell.DocumentArea.Traces.Count >= before)
                    {
                        break;
                    }
                }
            }

            _cycles++;
        }

        private void Toggle(params string[] path)
        {
            _driver.Invoke(_driver.MenuPath(path));
        }

        /// <summary>Takes one sample.</summary>
        /// <param name="forceCollect">
        /// Whether to collect regardless of the sampling interval. A retention run measures the
        /// floor at every sample, because it takes forty of them rather than four hundred and can
        /// afford the collection each time.
        /// </param>
        private void Sample(bool forceCollect = false)
        {
            bool collect = forceCollect || _samples % _options.CollectEvery == 0;
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
            // scrolling four hundred lines past whoever looks at it in the morning. A retention run
            // reports its own progress by cycle, which is the axis it is measuring against.
            if (_options.Cycles == 0 &&
                (_samples == 1 || _clock.Elapsed.TotalSeconds % 3600.0 < _options.SampleSeconds))
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
