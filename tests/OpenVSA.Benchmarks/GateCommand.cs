using System;
using System.Collections.Generic;
using System.Diagnostics;
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
            if (Has(args, "--allocation"))
            {
                return MeasureAllocation();
            }

            if (Has(args, "--kernels"))
            {
                return CompareKernels();
            }

            if (Has(args, "--fft-compare"))
            {
                return CompareFftProviders();
            }

            if (Has(args, "--render-compare"))
            {
                WindowedMeasurements.CompareRenderStrategies(1 << 20);
                return 0;
            }

            if (Has(args, "--stages"))
            {
                WindowedMeasurements.StageBreakdown(1 << 20);
                return 0;
            }

            if (Has(args, "--cold-start"))
            {
                return ColdStart(args);
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

        /// <summary>
        /// Measures <c>REQ-NFR-025</c> against a shell named on the command line (#410).
        /// </summary>
        /// <param name="args"><c>--cold-start --shell &lt;path&gt; [--runs &lt;n&gt;]</c>.</param>
        /// <returns>0 when the cold figure meets the requirement, 1 when it does not, 2 on misuse.</returns>
        /// <remarks>
        /// <para>
        /// <strong>Why a mode of its own, when <c>--measure</c> already measures this.</strong>
        /// That path finds the shell by walking up from its own <c>bin</c> to
        /// <c>src\OpenVSA.Ui\bin</c>, which is a statement about a source tree. The figure
        /// <c>REQ-NFR-025</c> actually asks for cannot be taken in a source tree at all: "cold"
        /// means a machine that has never had this product on it, so the shell to measure is an
        /// INSTALLED one and the machine has no repository, no SDK and no build tools. The
        /// measurement is unchanged — only the way it is told where to look.
        /// </para>
        /// <para>
        /// <strong>The cold figure is the first launch and nothing else.</strong> It cannot be
        /// repeated on the same machine without dropping the file cache, and installing the
        /// product destroys the property permanently, so this mode reports the first launch
        /// against the requirement and the later ones only as context.
        /// </para>
        /// </remarks>
        private static int ColdStart(string[] args)
        {
            string shell = Argument(args, "--shell");

            if (shell == null)
            {
                Console.Error.WriteLine(
                    "usage: OpenVSA.Benchmarks --gate --cold-start --shell <path to OpenVSA.exe> " +
                    "[--runs <n>]");

                return 2;
            }

            if (!File.Exists(shell))
            {
                Console.Error.WriteLine("There is no shell at '" + shell + "'.");
                return 2;
            }

            int runs = 5;
            string requested = Argument(args, "--runs");

            if (requested != null &&
                (!int.TryParse(requested, out runs) || runs < 3))
            {
                // Three is the floor the measurement itself imposes: fewer cannot separate the
                // cold launch from the warm ones, and a mean over both describes neither.
                Console.Error.WriteLine("--runs must be a whole number of at least 3.");
                return 2;
            }

            Console.WriteLine("REQ-NFR-025: cold start to first trace displayed, simulated source.");
            Console.WriteLine("  shell   " + shell);
            Console.WriteLine("  version " + FileVersionInfo.GetVersionInfo(shell).FileVersion);
            Console.WriteLine("  runs    " + runs + " (the first is the cold one)");
            Console.WriteLine();

            TargetMeasurement measured = ColdStartMeasurement.Run(shell, runs);

            if (measured == null)
            {
                Console.Error.WriteLine();
                Console.Error.WriteLine(
                    "The shell could not be driven to a trace, so there is no figure. The reason " +
                    "is above; it is a failure to measure, NOT a slow start.");

                return 2;
            }

            Console.WriteLine();
            Console.WriteLine("  cold (first launch) " + measured.AgainstStated.ToString("F2") + " s");
            Console.WriteLine("  warm mean           " + measured.Mean.ToString("F2") + " s +/- " +
                              measured.StandardDeviation.ToString("F2") + " over " +
                              measured.SampleCount + " launches");
            Console.WriteLine("  requirement         3.00 s, cold");
            Console.WriteLine();

            bool met = measured.AgainstStated <= 3.0;

            Console.WriteLine(met
                ? "  MET: the cold launch is within REQ-NFR-025."
                : "  NOT MET: the cold launch is over REQ-NFR-025's 3 s by " +
                  (measured.AgainstStated - 3.0).ToString("F2") + " s.");

            // Exit code carries the verdict so a script can report it without parsing prose, but
            // the log is the artefact: this figure can be taken once per machine and the phase
            // breakdown above is what says where the time went.
            return met ? 0 : 1;
        }

        /// <summary>
        /// REQ-NFR-002: DSP-attributable allocation and Gen-2 collections over a sustained run.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The requirement is explicit that process-wide "zero Gen-2" is not a realistic target in
        /// a WPF host and shall not be used as the criterion — it asks for DSP-<em>attributable</em>
        /// allocation, by call site.
        /// </para>
        /// <para>
        /// <strong>This attributes by isolation rather than by call site, and that is weaker.</strong>
        /// Nothing but the DSP pipeline runs in this process during the window, so every byte and
        /// every collection counted is the pipeline's. What it cannot do is say which line
        /// allocated them, which a profiler would — so it can prove the bound is exceeded and can
        /// prove it is met, but cannot point at the culprit when it fails. That is a real
        /// limitation and it is stated rather than papered over.
        /// </para>
        /// </remarks>
        private static int MeasureAllocation()
        {
            // Both sizes. 8 192 is the everyday frame; 2^20 is where SpectrumComputer's per-frame
            // float[points * 2] lands on the large object heap, which is what #409's
            // burst-versus-sustained gap pointed at.
            foreach (int points in new[] { 8192, 1 << 20 })
            {
                // One measurement, of what the product does. SpectrumEngine sets PoolFrames on
                // the computer it pumps, so the pooled path IS the product path now that every
                // consumer honours the lease; measuring an unpooled variant beside it would be
                // reporting a configuration nothing runs.
                if (MeasureAllocationAt(points) != 0)
                {
                    return 1;
                }
            }

            return 0;
        }

        private static int MeasureAllocationAt(int Points)
        {
            const double FramesPerSecond = 20.0;

            // REQ-NFR-002 states ten minutes. That is the figure the requirement is closed
            // against and it is run at least once per change to this path; CI exercises the shape
            // at 30 s, because twenty minutes of waiting on every build buys nothing a shorter run
            // does not already show -- the same arrangement REQ-NFR-011's soak uses, and for the
            // same reason. OPENVSA_ALLOCATION_SECONDS selects it.
            double seconds = 30.0;
            string configured = Environment.GetEnvironmentVariable("OPENVSA_ALLOCATION_SECONDS");

            if (!string.IsNullOrEmpty(configured))
            {
                double parsed;

                if (double.TryParse(
                        configured, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out parsed) &&
                    parsed > 0.0)
                {
                    seconds = parsed;
                }
            }

            TimeSpan window = TimeSpan.FromSeconds(seconds);

            Console.WriteLine("REQ-NFR-002: allocation over a sustained run at " +
                              FramesPerSecond + " frames/s, " + Points +
                              "-point frames, pooled as SpectrumEngine pumps them.");
            Console.WriteLine("  Attributed by isolation: nothing else runs in this process.");
            Console.WriteLine();

            var computer = new OpenVSA.Dsp.Spectrum.SpectrumComputer(
                OpenVSA.Dsp.Windowing.WindowType.FlatTop, null, null)
            {
                // As SpectrumEngine configures the computer it pumps.
                PoolFrames = true,
            };

            var metadata = new OpenVSA.Core.IqBlockMetadata(
                Points, 2.0e6, 1.0e9, false, 1.0, 0.0, 1L,
                new DateTime(2026, 7, 28, 0, 0, 0, DateTimeKind.Utc), 0.0, false,
                new OpenVSA.Core.FrontEndId("bench"), null);

            OpenVSA.Core.IqBlock block = OpenVSA.Core.IqBlock.Rent(metadata);
            Span<float> samples = block.GetSamples();

            for (int n = 0; n < Points; n++)
            {
                samples[n * 2] = (float)Math.Cos(0.125 * 2.0 * Math.PI * n);
                samples[n * 2 + 1] = (float)Math.Sin(0.125 * 2.0 * Math.PI * n);
            }

            // Settle before counting, so start-up allocation is not attributed to the loop.
            for (int i = 0; i < 20; i++)
            {
                computer.Compute(block).Release();
            }

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            long allocatedBefore = GC.GetTotalMemory(false);
            int gen0 = GC.CollectionCount(0);
            int gen1 = GC.CollectionCount(1);
            int gen2 = GC.CollectionCount(2);

            var clock = System.Diagnostics.Stopwatch.StartNew();
            var period = TimeSpan.FromSeconds(1.0 / FramesPerSecond);
            int frames = 0;

            while (clock.Elapsed < window)
            {
                // Release is a no-op on an unpooled frame, so the two paths run the same loop.
                computer.Compute(block).Release();
                frames++;

                TimeSpan due = TimeSpan.FromTicks((long)(frames * period.Ticks));
                TimeSpan wait = due - clock.Elapsed;

                if (wait > TimeSpan.Zero)
                {
                    System.Threading.Thread.Sleep(wait);
                }
            }

            long allocatedAfter = GC.GetTotalMemory(false);

            int gen0Delta = GC.CollectionCount(0) - gen0;
            int gen1Delta = GC.CollectionCount(1) - gen1;
            int gen2Delta = GC.CollectionCount(2) - gen2;

            Console.WriteLine("  " + frames + " frames over " +
                              clock.Elapsed.TotalSeconds.ToString("F1") + " s");
            Console.WriteLine("  heap " + (allocatedBefore / 1048576.0).ToString("F1") + " -> " +
                              (allocatedAfter / 1048576.0).ToString("F1") + " MiB");
            Console.WriteLine("  collections: gen0 " + gen0Delta + ", gen1 " + gen1Delta +
                              ", gen2 " + gen2Delta);
            Console.WriteLine();
            Console.WriteLine("  gen-2 collections attributable to the DSP pipeline: " + gen2Delta +
                              (gen2Delta == 0 ? "  (meets REQ-NFR-002)" : "  (REQ-NFR-002 requires none)"));
            Console.WriteLine();

            return gen2Delta == 0 ? 0 : 1;
        }

        /// <summary>
        /// REQ-NFR-003: the vector kernels against the scalar ones they replace.
        /// </summary>
        /// <remarks>
        /// The requirement asks for a factor, and its alternative branch turns on
        /// Vector&lt;float&gt;.Count, so both are printed. A ratio with no lane count beside it
        /// cannot be read against the requirement at all.
        /// </remarks>
        private static int CompareKernels()
        {
            Console.WriteLine(
                "REQ-NFR-003: Vector<float>.Count = " + OpenVSA.Dsp.Kernels.Lanes +
                ", hardware accelerated = " + OpenVSA.Dsp.Kernels.IsAccelerated);
            Console.WriteLine();

            // Swept across the cache hierarchy rather than measured at one size, because a single
            // ratio cannot distinguish a kernel that does not vectorise from one that vectorises
            // perfectly well and is waiting for memory. The two want opposite responses -- fix the
            // kernel, or stop expecting arithmetic width to help -- and at 2^20 alone they look
            // identical. The working set is printed so the reading can be placed against the
            // machine's own cache sizes instead of against an assumption about them.
            int[] sizes = { 1 << 10, 1 << 12, 1 << 14, 1 << 16, 1 << 18, 1 << 20 };

            Console.WriteLine(
                "  " + "samples".PadLeft(9) + "  " + "working set".PadLeft(11) + "  " +
                "window multiply".PadLeft(16) + "  " + "magnitude squared".PadLeft(18));
            Console.WriteLine();

            foreach (int samples in sizes)
            {
                // Enough repetitions that the smallest size is still timed over milliseconds, or
                // the stopwatch resolution is the measurement.
                int repetitions = (int)Math.Max(20, (1L << 28) / samples);

                var interleaved = new float[samples * 2];
                var window = new float[samples];
                var magnitudes = new float[samples];

                for (int n = 0; n < samples; n++)
                {
                    interleaved[n * 2] = (float)Math.Cos(0.1 * n);
                    interleaved[n * 2 + 1] = (float)Math.Sin(0.17 * n);
                    window[n] = (float)(0.5 - 0.5 * Math.Cos(2.0 * Math.PI * n / samples));
                }

                double windowScalar = Time(
                    repetitions, () => OpenVSA.Dsp.Kernels.WindowMultiplyScalar(interleaved, window));
                double windowVector = Time(
                    repetitions, () => OpenVSA.Dsp.Kernels.WindowMultiplyVector(interleaved, window));

                double magnitudeScalar = Time(
                    repetitions, () => OpenVSA.Dsp.Kernels.MagnitudeSquaredScalar(interleaved, magnitudes));
                double magnitudeVector = Time(
                    repetitions, () => OpenVSA.Dsp.Kernels.MagnitudeSquaredVector(interleaved, magnitudes));

                // The interleaved buffer dominates: 8 bytes a sample against 4 for the window and
                // 4 for the magnitudes.
                long workingSet = ((long)samples * 8) + ((long)samples * 4) + ((long)samples * 4);

                Console.WriteLine(
                    "  " + samples.ToString().PadLeft(9) + "  " +
                    ((workingSet / 1024.0).ToString("F0") + " KiB").PadLeft(11) + "  " +
                    ((windowScalar / windowVector).ToString("F2") + "x").PadLeft(16) + "  " +
                    ((magnitudeScalar / magnitudeVector).ToString("F2") + "x").PadLeft(18));
            }

            Console.WriteLine();

            // The figures the requirement is stated against, kept at the size the DSP pipeline
            // actually runs at so the sweep above cannot be read as replacing them.
            const int Samples = 1 << 20;
            const int Repetitions = 200;

            var full = new float[Samples * 2];
            var fullWindow = new float[Samples];
            var fullMagnitudes = new float[Samples];

            for (int n = 0; n < Samples; n++)
            {
                full[n * 2] = (float)Math.Cos(0.1 * n);
                full[n * 2 + 1] = (float)Math.Sin(0.17 * n);
                fullWindow[n] = (float)(0.5 - 0.5 * Math.Cos(2.0 * Math.PI * n / Samples));
            }

            Report("window multiply",
                Time(Repetitions, () => OpenVSA.Dsp.Kernels.WindowMultiplyScalar(full, fullWindow)),
                Time(Repetitions, () => OpenVSA.Dsp.Kernels.WindowMultiplyVector(full, fullWindow)));

            Report("magnitude squared",
                Time(Repetitions, () => OpenVSA.Dsp.Kernels.MagnitudeSquaredScalar(full, fullMagnitudes)),
                Time(Repetitions, () => OpenVSA.Dsp.Kernels.MagnitudeSquaredVector(full, fullMagnitudes)));

            return 0;
        }

        private static double Time(int repetitions, Action work)
        {
            work();

            var clock = System.Diagnostics.Stopwatch.StartNew();

            for (int r = 0; r < repetitions; r++)
            {
                work();
            }

            return clock.Elapsed.TotalMilliseconds / repetitions;
        }

        private static void Report(string name, double scalarMs, double vectorMs)
        {
            double factor = scalarMs / vectorMs;

            Console.WriteLine(
                "  " + name.PadRight(20) +
                "scalar " + scalarMs.ToString("F3").PadLeft(8) + " ms   " +
                "vector " + vectorMs.ToString("F3").PadLeft(8) + " ms   " +
                factor.ToString("F2") + "x   " +
                (factor >= 2.5 ? "meets the 2.5x target" : "BELOW the 2.5x target"));
        }

        /// <summary>
        /// REQ-NFR-004: the same binaries, each registered provider, timed and cross-checked.
        /// </summary>
        /// <remarks>
        /// This is the acceptance criterion's "running the suite twice with different providers
        /// selected and the same binaries", made observable: nothing here is recompiled between
        /// providers, they are taken from the registry as deployed.
        /// </remarks>
        private static int CompareFftProviders()
        {
            Console.WriteLine("REQ-NFR-004 providers, same binaries:");
            Console.WriteLine();

            foreach (OpenVSA.Dsp.Fft.IFftProvider provider in OpenVSA.Dsp.Fft.FftProviders.All)
            {
                Console.WriteLine(
                    "  " + provider.Name.PadRight(10) +
                    (provider.IsNativeAccelerated ? "native " : "managed") +
                    "   " + provider.SignificandBits + "-bit significand");
            }

            foreach (var kv in OpenVSA.Dsp.Fft.FftProviders.UnavailableProviders)
            {
                Console.WriteLine("  UNAVAILABLE " + kv.Key + ": " + kv.Value);
            }

            Console.WriteLine();

            foreach (int points in new[] { 8192, 1 << 20 })
            {
                double[] reference = null;

                foreach (OpenVSA.Dsp.Fft.IFftProvider provider in OpenVSA.Dsp.Fft.FftProviders.All)
                {
                    var buffer = new double[points * 2];

                    for (int i = 0; i < points; i++)
                    {
                        buffer[2 * i] = Math.Cos(0.1 * i) + 0.01 * Math.Cos(0.7 * i);
                        buffer[2 * i + 1] = Math.Sin(0.1 * i);
                    }

                    provider.Forward(new Span<double>(buffer));

                    // Re-fill and time, so the warm-up transform is not in the figure.
                    int repetitions = points <= 8192 ? 300 : 20;
                    var clock = System.Diagnostics.Stopwatch.StartNew();

                    for (int r = 0; r < repetitions; r++)
                    {
                        provider.Forward(new Span<double>(buffer));
                    }

                    clock.Stop();

                    // Agreement is checked against whichever provider ran first, at the tolerance
                    // REQ-NFR-004a states for the less precise of the two.
                    string agreement = string.Empty;

                    var fresh = new double[points * 2];

                    for (int i = 0; i < points; i++)
                    {
                        fresh[2 * i] = Math.Cos(0.1 * i) + 0.01 * Math.Cos(0.7 * i);
                        fresh[2 * i + 1] = Math.Sin(0.1 * i);
                    }

                    provider.Forward(new Span<double>(fresh));

                    if (reference == null)
                    {
                        reference = fresh;
                    }
                    else
                    {
                        double worst = 0.0;
                        double scale = 0.0;

                        for (int i = 0; i < fresh.Length; i++)
                        {
                            worst = Math.Max(worst, Math.Abs(fresh[i] - reference[i]));
                            scale = Math.Max(scale, Math.Abs(reference[i]));
                        }

                        agreement = "   agrees to " + (worst / Math.Max(scale, 1e-300)).ToString("E2");
                    }

                    Console.WriteLine(
                        "  " + provider.Name.PadRight(10) + points.ToString().PadLeft(8) + " pts   " +
                        (clock.Elapsed.TotalMilliseconds / repetitions).ToString("F3").PadLeft(8) +
                        " ms" + agreement);
                }

                Console.WriteLine();
            }

            return 0;
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
