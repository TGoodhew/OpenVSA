using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OpenVSA.Hal;
using OpenVSA.Hal.Visa;
using OpenVSA.TestHarness;

namespace OpenVSA.Verify
{
    /// <summary>
    /// Headless cross-validation: drive a generator, measure it with OpenVSA, report.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Exists so that verifying a change against real hardware is a command rather than an
    /// exercise in driving the UI by hand. It exits non-zero on any failure, so it can gate a
    /// build, and writes a machine-readable result file alongside the console report.
    /// </para>
    /// <para>
    /// With no instruments reachable it says so and exits non-zero rather than passing vacuously.
    /// A verification run that silently checks nothing is worse than one that fails.
    /// </para>
    /// </remarks>
    public static class Program
    {
        /// <summary>Runs the verification.</summary>
        /// <param name="args">Command-line arguments.</param>
        /// <returns>0 if every scenario passed.</returns>
        public static int Main(string[] args)
        {
            try
            {
                return RunAsync(args).GetAwaiter().GetResult();
            }
            catch (Exception failure)
            {
                Console.Error.WriteLine("Verification could not run: " + failure.Message);
                return 2;
            }
        }

        private static async Task<int> RunAsync(string[] args)
        {
            var options = Options.Parse(args);

            if (options.ShowHelp)
            {
                Options.WriteUsage();
                return 0;
            }

            Console.WriteLine("OpenVSA cross-validation");
            Console.WriteLine("  analyser  " + options.AnalyserResource);
            Console.WriteLine("  generator " + options.GeneratorResource);
            Console.WriteLine();

            using (var frontEnd = new E4406AFrontEnd(options.AnalyserResource, null))
            using (IStimulusSource stimulus = CreateStimulus(options))
            {
                await frontEnd.ConnectAsync(CancellationToken.None).ConfigureAwait(false);
                stimulus.Connect();

                Console.WriteLine("  measuring with " + frontEnd.DisplayName.Split('\n')[0].Trim());
                Console.WriteLine("  driving        " + stimulus.DisplayName);
                Console.WriteLine();

                if (options.Exercise)
                {
                    return await ExerciseAsync(frontEnd, stimulus, options).ConfigureAwait(false);
                }

                IReadOnlyList<VerificationScenario> scenarios =
                    VerificationScenario.Default(options.CenterFrequencyHz, options.LevelDbm);

                var runner = new VerificationRunner(frontEnd, stimulus);
                IReadOnlyList<VerificationResult> results =
                    await runner.RunAsync(scenarios, CancellationToken.None).ConfigureAwait(false);

                foreach (VerificationResult result in results)
                {
                    Console.WriteLine("  " + result);
                }

                int failed = results.Count(r => !r.Passed);

                Console.WriteLine();
                Console.WriteLine(
                    "  " + (results.Count - failed) + " of " + results.Count + " scenarios passed.");

                if (!string.IsNullOrEmpty(options.ResultFile))
                {
                    WriteResultFile(options.ResultFile, results);
                    Console.WriteLine("  results written to " + options.ResultFile);
                }

                return failed == 0 ? 0 : 1;
            }
        }

        /// <summary>
        /// Drives every feature that can be driven against one real acquisition.
        /// </summary>
        /// <remarks>
        /// Separate from the cross-validation because it answers a different question. That one
        /// asks whether the numbers are right; this one asks whether the features work on data the
        /// instrument actually produced rather than on signals a test made up to suit them.
        /// </remarks>
        private static async Task<int> ExerciseAsync(
            IFrontEnd frontEnd, IStimulusSource stimulus, Options options)
        {
            var exercise = new FeatureExercise(frontEnd, stimulus);

            IReadOnlyList<ExerciseResult> results = await exercise.RunAsync(
                options.CenterFrequencyHz,
                options.SpanHz,
                options.LevelDbm,
                CancellationToken.None).ConfigureAwait(false);

            foreach (ExerciseResult result in results)
            {
                Console.WriteLine("  " + result);
            }

            int failed = results.Count(r => !r.Passed);

            Console.WriteLine();
            Console.WriteLine(
                "  " + (results.Count - failed) + " of " + results.Count + " features exercised " +
                "successfully.");

            if (!string.IsNullOrEmpty(options.ResultFile))
            {
                WriteExerciseFile(options.ResultFile, results);
                Console.WriteLine("  results written to " + options.ResultFile);
            }

            return failed == 0 ? 0 : 1;
        }

        private static void WriteExerciseFile(string path, IReadOnlyList<ExerciseResult> results)
        {
            var text = new StringBuilder();
            text.AppendLine("requirement\tfeature\tresult\tdetail");

            foreach (ExerciseResult result in results)
            {
                text.AppendLine(string.Join(
                    "\t",
                    result.Requirement,
                    result.Name,
                    result.Passed ? "pass" : "fail",
                    result.Detail.Replace('\t', ' ')));
            }

            File.WriteAllText(path, text.ToString());
        }

        private static IStimulusSource CreateStimulus(Options options) =>
            options.UseSimulatedStimulus
                ? (IStimulusSource)new SimulatedStimulus()
                : new E4438CStimulus(options.GeneratorResource, null);

        /// <summary>Writes the results as tab-separated text, for a build to parse.</summary>
        private static void WriteResultFile(string path, IReadOnlyList<VerificationResult> results)
        {
            var text = new StringBuilder();
            text.AppendLine("scenario\tquantity\tunits\tpassed\texpected\tmeasured\terror\tmargin\tnote");

            foreach (VerificationResult result in results)
            {
                text.AppendLine(string.Join(
                    "\t",
                    result.Scenario.Name,
                    result.Scenario.What.ToString(),
                    result.Scenario.Units,
                    result.Passed ? "pass" : "fail",
                    Invariant(result.Expected),
                    Invariant(result.Measured),
                    Invariant(result.Error),
                    Invariant(result.Margin),
                    result.Note.Replace('\t', ' ')));
            }

            File.WriteAllText(path, text.ToString());
        }

        private static string Invariant(double value) =>
            value.ToString("R", CultureInfo.InvariantCulture);

        /// <summary>Command-line options.</summary>
        private sealed class Options
        {
            public string AnalyserResource { get; private set; } =
                VisaConfiguration.ResourceFor(
                    E4406AFrontEnd.ResourceSettingKey, E4406AFrontEnd.DefaultResource);

            public string GeneratorResource { get; private set; } =
                VisaConfiguration.ResourceFor(
                    E4438CStimulus.ResourceSettingKey, E4438CStimulus.DefaultResource);

            public double CenterFrequencyHz { get; private set; } = 1.0e9;

            public double LevelDbm { get; private set; } = -20.0;

            public double SpanHz { get; private set; } = 10e6;

            public bool Exercise { get; private set; }

            public string ResultFile { get; private set; }

            public bool UseSimulatedStimulus { get; private set; }

            public bool ShowHelp { get; private set; }

            public static Options Parse(string[] args)
            {
                var options = new Options();

                for (int i = 0; i < args.Length; i++)
                {
                    string argument = args[i];
                    string next = i + 1 < args.Length ? args[i + 1] : null;

                    switch (argument)
                    {
                        case "--analyser":
                            options.AnalyserResource = Take(ref i, next);
                            break;

                        case "--generator":
                            options.GeneratorResource = Take(ref i, next);
                            break;

                        case "--centre":
                        case "--center":
                            options.CenterFrequencyHz = double.Parse(
                                Take(ref i, next), CultureInfo.InvariantCulture);
                            break;

                        case "--level":
                            options.LevelDbm = double.Parse(
                                Take(ref i, next), CultureInfo.InvariantCulture);
                            break;

                        case "--results":
                            options.ResultFile = Take(ref i, next);
                            break;

                        case "--span":
                            options.SpanHz = double.Parse(
                                Take(ref i, next), CultureInfo.InvariantCulture);
                            break;

                        case "--exercise":
                            options.Exercise = true;
                            break;

                        case "--simulated-stimulus":
                            options.UseSimulatedStimulus = true;
                            break;

                        case "-h":
                        case "--help":
                            options.ShowHelp = true;
                            break;

                        default:
                            throw new ArgumentException("Unknown argument '" + argument + "'.");
                    }
                }

                return options;
            }

            public static void WriteUsage()
            {
                Console.WriteLine("OpenVSA.Verify — drives a generator, measures with OpenVSA, asserts.");
                Console.WriteLine();
                Console.WriteLine("  --analyser <resource>   VISA resource of the measuring instrument");
                Console.WriteLine("  --generator <resource>  VISA resource of the stimulus source");
                Console.WriteLine("  --centre <hz>           analysis centre frequency (default 1e9)");
                Console.WriteLine("  --level <dbm>           generator level (default -20)");
                Console.WriteLine("  --span <hz>             analysis span (default 10e6)");
                Console.WriteLine("  --exercise              drive every feature against one real");
                Console.WriteLine("                          acquisition instead of cross-validating");
                Console.WriteLine("  --results <path>        write tab-separated results here");
                Console.WriteLine("  --simulated-stimulus    exercise the harness with no generator");
                Console.WriteLine();
                Console.WriteLine("Exits 0 when every scenario passes, 1 on any failure, 2 if it could not run.");
            }

            private static string Take(ref int index, string next)
            {
                if (next == null)
                {
                    throw new ArgumentException("Missing value after the preceding argument.");
                }

                index++;
                return next;
            }
        }
    }
}
