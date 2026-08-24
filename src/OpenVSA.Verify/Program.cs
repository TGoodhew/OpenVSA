using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OpenVSA.Demod.Chain;
using OpenVSA.Demod.Signal;
using OpenVSA.Hal;
using OpenVSA.Hal.Visa;
using OpenVSA.Measurement;
using OpenVSA.Measurement.Contexts;
using OpenVSA.Measurement.State;
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
            // REQ-NFR-001's ceiling has to be demonstrated in a process configured for it.
            // gcAllowVeryLargeObjects is a runtime startup setting, so a unit test cannot show it:
            // the vstest host is not configured that way and the allocation fails there however
            // the product is built. This harness is, so the check runs here and the test drives it.
            foreach (string argument in args)
            {
                if (string.Equals(argument, "--check-large-array", StringComparison.OrdinalIgnoreCase))
                {
                    return CheckLargeArray();
                }
            }

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

        /// <summary>
        /// <c>REQ-NFR-001</c>: allocates the array the requirement names and touches both ends.
        /// </summary>
        /// <returns>0 when it succeeded, 1 when it was refused, 2 when there was not room.</returns>
        /// <remarks>
        /// Both ends are written, not just the length checked: a length alone would pass against a
        /// reservation that is committed lazily and faults on first use.
        /// </remarks>
        private static int CheckLargeArray()
        {
            const long Elements = 2000000000L;

            if (IntPtr.Size != 8)
            {
                Console.Error.WriteLine("Not a 64-bit process, so the ceiling cannot be tested.");
                return 1;
            }

            try
            {
                float[] huge = new float[Elements];

                huge[0] = 1.0f;
                huge[Elements - 1] = 2.0f;

                bool ok = huge[0] == 1.0f && huge[Elements - 1] == 2.0f;

                Console.WriteLine(
                    "allocated " + Elements + " floats (" +
                    (Elements * 4.0 / 1073741824.0).ToString("F1", CultureInfo.InvariantCulture) +
                    " GiB) and touched both ends: " + (ok ? "ok" : "VALUES WRONG"));

                huge = null;
                GC.Collect();

                return ok ? 0 : 1;
            }
            catch (OutOfMemoryException e)
            {
                // Two different failures wear this exception. "Array dimensions exceeded supported
                // range" means gcAllowVeryLargeObjects is not in force and is a real failure of the
                // requirement; anything else means this machine has not the room, which is not.
                bool refused = e.Message.IndexOf("dimensions", StringComparison.OrdinalIgnoreCase) >= 0;

                Console.Error.WriteLine(
                    (refused
                        ? "REFUSED, gcAllowVeryLargeObjects is not in force: "
                        : "no room on this machine: ") + e.Message);

                return refused ? 1 : 2;
            }
        }

        /// <summary>
        /// Lists what the bus reports, through the same path the connection dialog uses
        /// (<c>REQ-HAL-003</c>).
        /// </summary>
        /// <returns>0 if anything answered, 3 if nothing did.</returns>
        /// <remarks>
        /// Through <see cref="FrontEndRegistry.DiscoverResources"/> and not through
        /// <c>VisaResourceDiscovery</c> directly, so what is printed here is what the dialog shows —
        /// including the driver mapping, which is the registry's and not the transport's. A check
        /// that took a shorter path would confirm the transport and leave the assembled behaviour
        /// unverified, which is the state this was in.
        /// </remarks>
        /// <summary>
        /// Asks the generator what it really does with a digital modulation (<c>REQ-E44-007</c>).
        /// </summary>
        /// <remarks>
        /// <para>
        /// The generator alone, with no analyser opened: this answers "does the instrument behave as
        /// its manual describes", which is a question about one instrument and is worth being able
        /// to ask when the other is switched off.
        /// </para>
        /// <para>
        /// It writes to the instrument, because there is no other way to find out. It leaves the
        /// modulation off and the output off when it is done, whatever happened in between.
        /// </para>
        /// </remarks>
        private static int ProbeModulation(Options options)
        {
            Console.WriteLine("OpenVSA digital-modulation probe");
            Console.WriteLine("  generator " + options.GeneratorResource);
            Console.WriteLine();

            using (IStimulusSource stimulus = CreateStimulus(options))
            {
                var digital = stimulus as IDigitalModulationStimulus;

                if (digital == null)
                {
                    Console.WriteLine(
                        "  " + stimulus.DisplayName + " does not offer digital modulation.");

                    return 1;
                }

                stimulus.Connect();

                Console.WriteLine("  driving " + stimulus.DisplayName);
                Console.WriteLine();

                try
                {
                    Console.WriteLine("  declared, from the manual:");
                    Console.WriteLine(
                        "    symbol rate      " + digital.MinimumSymbolRateHz + " to " +
                        digital.MaximumSymbolRateHz(StimulusPulseFilter.RootRaisedCosine) +
                        " sym/s (root raised cosine)");
                    Console.WriteLine("    formats          " + digital.Formats.Count);
                    Console.WriteLine("    data patterns    " + digital.DataPatterns.Count);
                    Console.WriteLine();

                    var probing = stimulus as E4438CStimulus;

                    if (probing != null)
                    {
                        // Per format and per filter, because the manual's ceiling is a property of
                        // the pair: QPSK reaches 12.5 Msps and QAM16 half that. If the instrument
                        // answers the same number for both, the query is reporting the hardware's
                        // absolute limit rather than the one in force, which is a different fact
                        // and a more dangerous one to build a scenario on.
                        Console.WriteLine("  probed, from the instrument:");

                        foreach (string format in new[] { "QPSK", "QAM16", "QAM256" })
                        {
                            foreach (StimulusPulseFilter filter in new[]
                            {
                                StimulusPulseFilter.RootRaisedCosine,
                                StimulusPulseFilter.Gaussian,
                            })
                            {
                                double floorHz;
                                double ceilingHz;

                                probing.ProbeSymbolRateLimits(
                                    format, filter, out floorHz, out ceilingHz);

                                Console.WriteLine(
                                    "    " + format.PadRight(8) + filter.ToString().PadRight(18) +
                                    "MIN " + Reported(floorHz).PadRight(12) + "MAX " +
                                    Reported(ceilingHz));
                            }
                        }

                        Console.WriteLine();
                    }

                    foreach (string format in new[] { "QPSK", "GRAYQPSK" })
                    {
                        digital.SetDigitalModulation(
                            options.CenterFrequencyHz,
                            options.LevelDbm,
                            format,
                            1e6,
                            StimulusPulseFilter.RootRaisedCosine,
                            0.35,
                            "PN9");

                        Console.WriteLine("  asked for " + format + " at 1 Msym/s, RRC, alpha 0.35, PN9:");
                        Console.WriteLine("    format           " + digital.Format);
                        Console.WriteLine("    symbol rate      " + Reported(digital.SymbolRateHz));
                        Console.WriteLine("    filter           " + digital.PulseFilter);
                        Console.WriteLine("    alpha            " + Reported(digital.Alpha));
                        Console.WriteLine("    data             " + digital.DataPattern);
                        Console.WriteLine("    inverted         " + digital.IsSpectrumInverted);
                        Console.WriteLine("    carrier          " + Reported(stimulus.FrequencyHz));
                        Console.WriteLine("    level            " + Reported(stimulus.LevelDbm));
                        Console.WriteLine();
                    }

                    digital.SetSpectrumInverted(true);
                    Console.WriteLine("  after :POLarity INVerted -> inverted " +
                        digital.IsSpectrumInverted);

                    digital.SetSpectrumInverted(false);
                    Console.WriteLine("  after :POLarity NORMal   -> inverted " +
                        digital.IsSpectrumInverted);
                    Console.WriteLine();
                }
                finally
                {
                    // Whatever happened, the instrument is not left transmitting something nobody
                    // chose. The finally is the point: a probe that threw half way through is
                    // exactly when this matters.
                    try
                    {
                        digital.StopDigitalModulation();
                        stimulus.SetOutput(false);

                        Console.WriteLine("  modulation off, output off.");
                    }
                    catch (Exception failure)
                    {
                        Console.WriteLine("  COULD NOT RESTORE THE SOURCE: " + failure.Message);
                    }

                    // The transcript, because "which command broke it" is the only question worth
                    // asking of a failure like that and the message never says.
                    var recorded = stimulus as E4438CStimulus;

                    if (recorded != null)
                    {
                        Console.WriteLine();
                        Console.WriteLine("  SCPI sent, in order:");

                        foreach (string command in recorded.Sent)
                        {
                            Console.WriteLine("    " + command);
                        }
                    }
                }

                return 0;
            }
        }

        /// <summary>A probed figure, or a plain statement that the instrument would not say.</summary>
        private static string Reported(double value)
        {
            return double.IsNaN(value)
                ? "not reported"
                : value.ToString("G9", System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>One case of the bit-level cross-check: what to transmit, and what to expect.</summary>
        private sealed class DemodCase
        {
            public DemodCase(string format, bool mirrored, bool expectMatch, string expectation)
            {
                Format = format;
                Mirrored = mirrored;
                ExpectMatch = expectMatch;
                Expectation = expectation;
            }

            /// <summary>The generator's modulation format.</summary>
            public string Format { get; }

            /// <summary>Whether to invert the modulated spectrum (<c>REQ-DEM-035</c>).</summary>
            public bool Mirrored { get; }

            /// <summary>Whether the bits are expected to be the sequence.</summary>
            public bool ExpectMatch { get; }

            /// <summary>Why that is expected, in the words the run prints.</summary>
            public string Expectation { get; }

            /// <inheritdoc />
            public override string ToString() =>
                Format + (Mirrored ? ", spectrum inverted" : string.Empty);
        }

        /// <summary>
        /// Demodulates real modulated signals and checks the bits against the sequence the generator
        /// was transmitting (<c>REQ-E44-007</c> stage 1).
        /// </summary>
        /// <remarks>
        /// <para>
        /// The whole path: the generator modulates, the E4406A captures, OpenVSA's chain
        /// demodulates, and the recovered symbols are compared with a PN sequence generated on this
        /// side from its polynomial. That last step is what makes this different from every other
        /// check on the demodulator — everything else compares it with itself or with OpenVSA's own
        /// generator, and neither would notice a bit mapping that was consistently wrong.
        /// </para>
        /// <para>
        /// <strong>Three cases, two of which are expected to fail to match.</strong> A check that only
        /// ever passes proves nothing about what it would catch. So the matrix includes a
        /// Gray-coded QPSK, whose symbol labels are a transposition of the natural mapping's and
        /// therefore should <em>not</em> match, and an inverted spectrum, whose outcome is a
        /// prediction worth testing rather than an assumption: the search tries every rotation and
        /// both bit orders, and for a natural mapping the bit-order swap is a reflection, so the two
        /// together span the reflections as well as the rotations. A mirrored signal should therefore
        /// still match, with the bit order reported flipped — which would mean this check cannot
        /// detect a mirror, and that is worth knowing precisely because it is not obvious.
        /// </para>
        /// <para>
        /// <strong>Why 500 ksym/s and the widest span.</strong> On this front end the requested span
        /// is the waveform path's information bandwidth, and the sample rate follows it — so the
        /// widest span, not the narrowest one the signal fits in, is what buys samples a symbol. The
        /// signal has to fit inside that bandwidth, which at a roll-off of 0.35 a 500 ksym/s carrier
        /// does with room to spare. The cost of the wide filter is noise in the bandwidth the signal
        /// does not occupy, which at −20 dBm is affordable and an EVM figure will show if it is not.
        /// </para>
        /// <para>
        /// <strong>The rate is measured, not assumed.</strong> <c>AcquisitionPlan.SampleRateHz</c> is
        /// an estimate the front end labels as one — the instrument decimates in steps and coerces
        /// the sample interval to a multiple of 1/15 MHz — so what is checked here is the rate the
        /// instrument reported with the blocks it delivered, and the samples a symbol the chain
        /// actually had.
        /// </para>
        /// </remarks>
        private static async Task<int> DemodCheck(Options options)
        {
            var cases = new List<DemodCase>
            {
                new DemodCase(
                    "QPSK",
                    false,
                    true,
                    "the mapping OpenVSA decodes to should be this instrument's QPSK mapping"),
                new DemodCase(
                    "QPSK",
                    true,
                    true,
                    "predicted to match with the bit order flipped, because rotations and bit " +
                    "order together span the reflections"),
                new DemodCase(
                    "GRAYQPSK",
                    false,
                    false,
                    "a Gray mapping transposes two symbols, which no rotation undoes"),
            };

            Console.WriteLine("OpenVSA demodulation cross-check");
            Console.WriteLine("  analyser  " + options.AnalyserResource);
            Console.WriteLine("  generator " + options.GeneratorResource);
            Console.WriteLine();

            using (var frontEnd = new E4406AFrontEnd(options.AnalyserResource, null))
            using (IStimulusSource stimulus = CreateStimulus(options))
            {
                var digital = stimulus as IDigitalModulationStimulus;

                if (digital == null)
                {
                    Console.WriteLine(
                        "  " + stimulus.DisplayName + " does not offer digital modulation.");

                    return 1;
                }

                await frontEnd.ConnectAsync(CancellationToken.None).ConfigureAwait(false);
                stimulus.Connect();

                Console.WriteLine("  measuring with " + frontEnd.DisplayName.Split('\n')[0].Trim());
                Console.WriteLine("  driving        " + stimulus.DisplayName);
                Console.WriteLine();

                int wrong = 0;

                try
                {
                    foreach (DemodCase scenario in cases)
                    {
                        if (!digital.Formats.Contains(scenario.Format))
                        {
                            Console.WriteLine(
                                "  " + scenario + ": this source does not offer that format, skipped.");
                            Console.WriteLine();
                            continue;
                        }

                        bool asExpected = await RunDemodCase(
                            frontEnd, stimulus, digital, options, scenario)
                            .ConfigureAwait(false);

                        if (!asExpected)
                        {
                            wrong++;
                        }

                        Console.WriteLine();
                    }
                }
                finally
                {
                    try
                    {
                        digital.SetSpectrumInverted(false);
                        digital.StopDigitalModulation();
                        stimulus.SetOutput(false);

                        Console.WriteLine("  polarity normal, modulation off, output off.");
                    }
                    catch (Exception failure)
                    {
                        Console.WriteLine("  COULD NOT RESTORE THE SOURCE: " + failure.Message);
                    }
                }

                Console.WriteLine();
                Console.WriteLine(
                    "  " + (cases.Count - wrong) + " of " + cases.Count +
                    " cases came out as expected.");

                return wrong == 0 ? 0 : 1;
            }
        }

        /// <summary>Runs one case of the bit-level cross-check.</summary>
        /// <returns>Whether the outcome was the one the case expected.</returns>
        private static async Task<bool> RunDemodCase(
            E4406AFrontEnd frontEnd,
            IStimulusSource stimulus,
            IDigitalModulationStimulus digital,
            Options options,
            DemodCase scenario)
        {
            const double SymbolRateHz = 500e3;
            const double RollOff = 0.35;
            const int ResultLengthSymbols = 512;
            const string Pattern = "PN9";

            // Chosen here rather than taken from --span, because on this front end the span is
            // the waveform path's bandwidth: it sets the sample rate and it filters the signal, so
            // it is a property of the signal being measured rather than a display preference.
            const double SpanHz = 5e6;

            double measuredRateHz = 0.0;
            double measuredBandwidthHz = 0.0;

            Console.WriteLine("  " + scenario + ":");
            Console.WriteLine("    expected         " + scenario.Expectation);

            digital.SetDigitalModulation(
                options.CenterFrequencyHz,
                options.LevelDbm,
                scenario.Format,
                SymbolRateHz,
                StimulusPulseFilter.RootRaisedCosine,
                RollOff,
                Pattern);

            digital.SetSpectrumInverted(scenario.Mirrored);
            stimulus.SetOutput(true);

            Console.WriteLine(
                "    transmitting     " + digital.Format + " at " +
                (digital.SymbolRateHz / 1e3).ToString("F3", CultureInfo.InvariantCulture) +
                " ksym/s, root raised cosine, alpha " +
                digital.Alpha.ToString("F2", CultureInfo.InvariantCulture) + ", " +
                digital.DataPattern + ", " +
                stimulus.LevelDbm.ToString("F2", CultureInfo.InvariantCulture) +
                " dBm at " +
                (stimulus.FrequencyHz / 1e6).ToString(
                    "F3", CultureInfo.InvariantCulture) + " MHz");

            var setup = new MeasurementState
            {
                CenterFrequencyHz = options.CenterFrequencyHz,
                SpanHz = SpanHz,
            };

            setup.SelectKind(MeasurementKind.DigitalDemodulation);

            setup.Demod.Format = "QPSK";
            setup.Demod.SymbolRateHz = digital.SymbolRateHz;
            setup.Demod.ResultLengthSymbols = ResultLengthSymbols;
            setup.Demod.MeasurementFilter = PulseFilterType.RootRaisedCosine;
            setup.Demod.MeasurementFilterAlpha = RollOff;
            setup.Demod.ReferenceFilterAlpha = RollOff;

            var contexts = new MeasurementContextSet();
            MeasurementContext demod = contexts.Add("Demod", setup);

            var analyser = new ContextAnalyser(contexts);
            var results = new List<DemodResult>();
            var faults = new List<string>();

            demod.ResultAnalysed += (sender, result) =>
            {
                lock (results)
                {
                    results.Add(result);
                }
            };

            demod.DemodulationFaulted += (sender, failure) =>
            {
                lock (faults)
                {
                    faults.Add(failure.Message);
                }
            };

            using (var engine = new SpectrumEngine(frontEnd, null))
            {
                analyser.Attach(engine);

                engine.TargetUpdatesPerSecond = 0.0;

                AcquisitionPlan plan = await engine.StartAsync(
                    new AcquisitionRequest(options.CenterFrequencyHz, SpanHz, 32768, 0.0),
                    CancellationToken.None).ConfigureAwait(false);

                foreach (ParameterCoercion coercion in plan.Coercions)
                {
                    Console.WriteLine("    coerced          " + coercion);
                }

                // A few blocks, and the last one is read: the first after a retune carries whatever
                // the instrument was settling through.
                for (int wait = 0; wait < 100; wait++)
                {
                    lock (results)
                    {
                        if (results.Count >= 3)
                        {
                            break;
                        }
                    }

                    await Task.Delay(200).ConfigureAwait(false);
                }

                await engine.StopAsync().ConfigureAwait(false);

                measuredRateHz = frontEnd.SampleRateHz;
                measuredBandwidthHz = frontEnd.ActualBandwidthHz;
            }

            Console.WriteLine(
                "    bandwidth        " +
                (measuredBandwidthHz / 1e6).ToString("F4", CultureInfo.InvariantCulture) +
                " MHz reported, " + (SpanHz / 1e6).ToString("F4", CultureInfo.InvariantCulture) +
                " MHz asked for");
            Console.WriteLine(
                "    sample rate      " +
                (measuredRateHz / 1e6).ToString("F4", CultureInfo.InvariantCulture) +
                " MS/s reported, " +
                (measuredRateHz / digital.SymbolRateHz).ToString("F2", CultureInfo.InvariantCulture) +
                " samples/symbol");

            lock (faults)
            {
                foreach (string fault in faults)
                {
                    Console.WriteLine("    DEMODULATION FAULT: " + fault);
                }
            }

            DemodResult measured;

            lock (results)
            {
                if (results.Count == 0)
                {
                    Console.WriteLine("    no demodulated result arrived.");

                    return false;
                }

                measured = results[results.Count - 1];
            }

            if (measuredRateHz < digital.SymbolRateHz * 4.0)
            {
                // Below four samples a symbol the chain has nothing to resample from, and a result
                // computed anyway would measure the shortage rather than the signal.
                Console.WriteLine(
                    "    THE SAMPLE RATE IS TOO LOW FOR THIS SYMBOL RATE. The span sets it on this " +
                    "front end and this is already the widest it offers, so the remedy is a slower " +
                    "symbol rate rather than a different setup.");

                return false;
            }

            Console.WriteLine(
                "    symbols          " + measured.Trace.SymbolCount + ", EVM " +
                measured.EvmPercent.ToString("F4", CultureInfo.InvariantCulture) + " %rms, carrier " +
                "error " +
                measured.CarrierFrequencyErrorHz.ToString("F1", CultureInfo.InvariantCulture) +
                " Hz, " + (measured.Converged ? "converged" : "NOT CONVERGED") + " in " +
                measured.Passes.Count + " pass" + (measured.Passes.Count == 1 ? string.Empty : "es"));

            foreach (string notice in measured.Notices)
            {
                Console.WriteLine("    notice           " + notice);
            }

            BitStreamMatch match = BitStreamAlignment.Find(
                measured.Symbols,
                measured.Trace.BitsPerSymbol,
                1 << measured.Trace.BitsPerSymbol,
                Pattern);

            Console.WriteLine("    against " + Pattern + "      " + match);

            if (match.Found == scenario.ExpectMatch)
            {
                Console.WriteLine(
                    "    OUTCOME          as expected: " +
                    (match.Found
                        ? "the recovered bits are the transmitted sequence."
                        : "no reading of these bits is the sequence, which is what a wrong " +
                            "mapping looks like and is why a match means something."));

                return true;
            }

            Console.WriteLine(
                "    OUTCOME          NOT AS EXPECTED: the bits " +
                (match.Found ? "are" : "are not") + " the sequence and " +
                (scenario.ExpectMatch ? "they were expected to be" : "they were not expected to be") +
                ". That is a fact about this instrument and OpenVSA together, and it needs " +
                "explaining rather than tolerating.");

            return false;
        }

        private static int ListResources()
        {
            FrontEndRegistry registry = FrontEndRegistry.CreateDefault();

            Console.WriteLine("OpenVSA resource discovery");
            Console.WriteLine("  " + registry.Providers.Count + " front end(s), " +
                (registry.CanEnumerateResources ? "a transport that can enumerate" : "no transport that can enumerate"));
            Console.WriteLine();

            IReadOnlyList<DiscoveredResource> found = registry.DiscoverResources(CancellationToken.None);

            foreach (DiscoveredResource resource in found)
            {
                // A separator rather than padding alone. The resource manager on this bench
                // reports remote addresses as "visa://host.localdomain/GPIB0::9::INSTR", which is
                // longer than any column width worth choosing, and PadRight then runs the identity
                // straight onto the end of the name with nothing between them.
                Console.WriteLine(
                    "  " + resource.ResourceName.PadRight(30) + "  " +
                    (resource.Answered ? resource.Identity : "— " + resource.Failure));

                if (resource.HasDriver)
                {
                    Console.WriteLine("  " + new string(' ', 32) + "DRIVER: " + resource.Driver);
                }
            }

            Console.WriteLine();
            Console.WriteLine(
                "  " + found.Count + " resource(s), " +
                found.Count(r => r.Answered) + " answered, " +
                found.Count(r => r.HasDriver) + " with a driver");

            foreach (FrontEndDiscoveryFailure failure in registry.Failures)
            {
                Console.WriteLine("  ! " + failure);
            }

            return found.Any(r => r.Answered) ? 0 : 3;
        }

        private static async Task<int> RunAsync(string[] args)
        {
            var options = Options.Parse(args);

            if (options.ShowHelp)
            {
                Options.WriteUsage();
                return 0;
            }

            if (options.ListResources)
            {
                return ListResources();
            }

            if (options.ProbeModulation)
            {
                return ProbeModulation(options);
            }

            if (options.CheckDemodulation)
            {
                return await DemodCheck(options).ConfigureAwait(false);
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

            /// <summary>Whether to list what is on the bus and stop.</summary>
            public bool ListResources { get; private set; }

            /// <summary>Whether to ask the generator what it does with a digital modulation.</summary>
            public bool ProbeModulation { get; private set; }

            /// <summary>
            /// Whether to demodulate a real modulated signal and check the bits against the
            /// sequence the generator was transmitting.
            /// </summary>
            public bool CheckDemodulation { get; private set; }

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

                        case "--resources":
                            options.ListResources = true;
                            break;

                        case "--probe-modulation":
                            options.ProbeModulation = true;
                            break;

                        case "--demod-check":
                            options.CheckDemodulation = true;
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
                Console.WriteLine("  --resources             list what the resource manager reports,");
                Console.WriteLine("                          identified where safe, and stop");
                Console.WriteLine("  --probe-modulation      ask the generator what it really does");
                Console.WriteLine("                          with a digital modulation, and leave it off");
                Console.WriteLine("  --demod-check           demodulate a real modulated signal and check");
                Console.WriteLine("                          the bits against the sequence sent; chooses its");
                Console.WriteLine("                          own span, because the span sets the sample rate");
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
