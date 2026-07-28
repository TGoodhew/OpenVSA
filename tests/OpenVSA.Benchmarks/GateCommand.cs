using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using OpenVSA.PerformanceGate;

namespace OpenVSA.Benchmarks
{
    /// <summary>
    /// The <c>--gate</c> mode: judge a run's measurements against the stored baselines
    /// (<c>REQ-TST-007</c>).
    /// </summary>
    /// <remarks>
    /// Separate from the measuring mode because they run in different processes and, in CI, at
    /// different times: a build measures, uploads its figures, and the gate judges them. Keeping
    /// the judgement out of the measuring process also means a gate can be re-run against a stored
    /// file to ask "what would this have said?" without measuring anything again.
    /// </remarks>
    public static class GateCommand
    {
        /// <summary>Runs the gate.</summary>
        /// <param name="args">
        /// <c>--gate --measurements &lt;path&gt; [--baselines &lt;path&gt;] [--adopt]</c>.
        /// <c>--adopt</c> writes this run's figures in as the baseline for this machine class,
        /// which is how a first baseline is taken and how a deliberate change is accepted.
        /// </param>
        /// <returns>0 when the gate passes, 1 on a regression or a skipped target, 2 on misuse.</returns>
        public static int Run(string[] args)
        {
            if (Has(args, "--stages"))
            {
                WindowedMeasurements.StageBreakdown(1 << 20);
                return 0;
            }

            if (Has(args, "--measure"))
            {
                return Measure(Argument(args, "--measurements"));
            }

            string measurementsPath = Argument(args, "--measurements");
            string baselinePath = Argument(args, "--baselines") ?? DefaultBaselinePath();
            bool adopt = Has(args, "--adopt");

            if (measurementsPath == null)
            {
                Console.Error.WriteLine("usage: OpenVSA.Benchmarks --gate --measurements <path> " +
                                        "[--baselines <path>] [--adopt]");
                return 2;
            }

            if (!File.Exists(measurementsPath))
            {
                Console.Error.WriteLine("No measurements at " + measurementsPath);
                return 2;
            }

            IList<TargetMeasurement> measurements;
            BaselineStore baselines;

            try
            {
                measurements = MeasurementFile.ReadFile(measurementsPath);
                baselines = BaselineStore.ReadFile(baselinePath);
            }
            catch (FormatException e)
            {
                // A half-read baseline file would compare against whichever rows happened to
                // parse, which is worse than not comparing.
                Console.Error.WriteLine("Could not read the figures: " + e.Message);
                return 2;
            }

            MachineClass machine = LocalMachine.Current();
            GateReport report = new RegressionGate(baselines).Judge(machine, measurements);

            Console.Write(report.Render());

            if (adopt)
            {
                Adopt(baselines, baselinePath, machine, measurements);
                Console.WriteLine();
                Console.WriteLine("  Adopted " + measurements.Count + " figure(s) as the baseline for");
                Console.WriteLine("  " + machine.Key);
                Console.WriteLine("  in " + baselinePath + ". Review the diff: it is a claim about how fast");
                Console.WriteLine("  OpenVSA is, and adopting a regression is how one becomes permanent.");
                return 0;
            }

            return report.ExitCode;
        }

        /// <summary>The <c>--measure</c> mode: take the rendered targets and write them out.</summary>
        /// <param name="path">Where to write the measurements.</param>
        private static int Measure(string path)
        {
            if (path == null)
            {
                Console.Error.WriteLine("usage: OpenVSA.Benchmarks --gate --measure --measurements <path>");
                return 2;
            }

            Console.WriteLine("Measuring the rendered targets of REQ-NFR-020, -021 and -024.");
            Console.WriteLine("Each runs for " + WindowedMeasurements.MeasurementWindow.TotalSeconds + " s with a live dispatcher.");
            Console.WriteLine();

            IList<TargetMeasurement> measurements = WindowedMeasurements.Run();

            foreach (TargetMeasurement m in measurements)
            {
                PerformanceTarget target = TargetCatalogue.ByName(m.Name);

                Console.WriteLine(
                    "  " + (target == null ? m.Name : target.Requirement).PadRight(12) +
                    m.Mean.ToString("F2") + " " + (target == null ? string.Empty : target.Unit) +
                    " ±" + (m.RelativeResolution * 100.0).ToString("F1") + "%" +
                    (target == null ? string.Empty : "   (target " + target.Stated.ToString("F0") + ")"));
            }

            string directory = Path.GetDirectoryName(path);

            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(path, MeasurementFile.Write(measurements), new UTF8Encoding(false));

            Console.WriteLine();
            Console.WriteLine("  written to " + path);
            return 0;
        }

        private static void Adopt(
            BaselineStore baselines,
            string path,
            MachineClass machine,
            IEnumerable<TargetMeasurement> measurements)
        {
            string commit = Environment.GetEnvironmentVariable("GITHUB_SHA") ?? string.Empty;

            foreach (TargetMeasurement m in measurements)
            {
                baselines.Set(new BaselineEntry(
                    machine, m.Name, m.Mean, m.RelativeResolution, DateTime.UtcNow, commit));
            }

            string directory = Path.GetDirectoryName(path);

            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(path, baselines.Write(), new UTF8Encoding(false));
        }

        /// <summary>Where the checked-in baselines live.</summary>
        /// <remarks>
        /// Found by walking up to the directory holding <c>OpenVSA.slnx</c> rather than by counting
        /// levels. The output path is <c>bin\x64\Release\net472</c> in one configuration and
        /// <c>bin\Debug\net472</c> in another, so a fixed number of <c>..</c> segments is right for
        /// one of them and silently writes the baseline into the project directory for the other —
        /// which is what it did.
        /// </remarks>
        private static string DefaultBaselinePath()
        {
            var directory = new DirectoryInfo(
                Path.GetDirectoryName(typeof(GateCommand).Assembly.Location) ?? ".");

            while (directory != null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "OpenVSA.slnx")))
                {
                    return Path.Combine(directory.FullName, "performance-baselines.tsv");
                }

                directory = directory.Parent;
            }

            // No solution above us: keep the figures beside the binary rather than refusing to run.
            return Path.Combine(
                Path.GetDirectoryName(typeof(GateCommand).Assembly.Location) ?? ".",
                "performance-baselines.tsv");
        }

        private static bool Has(string[] args, string name)
        {
            foreach (string arg in args)
            {
                if (string.Equals(arg, name, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static string Argument(string[] args, string name)
        {
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                {
                    return args[i + 1];
                }
            }

            return null;
        }
    }
}
