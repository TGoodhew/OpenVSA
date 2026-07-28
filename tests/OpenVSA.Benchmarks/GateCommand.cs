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

        /// <summary>Where the checked-in baselines live, relative to the running assembly.</summary>
        private static string DefaultBaselinePath()
        {
            string here = Path.GetDirectoryName(typeof(GateCommand).Assembly.Location);

            // bin\<config>\<tfm> under the project, so four levels up is the repository root.
            return Path.GetFullPath(Path.Combine(
                here ?? ".", "..", "..", "..", "..", "performance-baselines.tsv"));
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
