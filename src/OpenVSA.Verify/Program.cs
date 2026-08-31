using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OpenVSA.Demod.Chain;
using OpenVSA.Demod.Results;
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
            public DemodCase(
                string format,
                bool mirrored,
                bool expectMatch,
                string expectation,
                string demodulated = null,
                int repeats = 1,
                DifferentialReference reference = DifferentialReference.PerFormat,
                bool? expectRelabelling = null,
                BitMapping mapping = BitMapping.Natural)
            {
                ExpectRelabelling = expectRelabelling;
                Mapping = mapping;
                Format = format;
                Mirrored = mirrored;
                ExpectMatch = expectMatch;
                Expectation = expectation;
                Demodulated = demodulated ?? format;
                Repeats = repeats;
                Reference = reference;
            }

            /// <summary>Which bits the constellation's points carry (<c>REQ-DEM-011</c>).</summary>
            /// <remarks>
            /// A case of its own for the same reason the reference is: which labelling an instrument
            /// used is a fact about the instrument, and this bench measured it. The same three
            /// signals appear twice in the matrix, once against each labelling, and exactly one of
            /// the two readings is the sequence.
            /// </remarks>
            public BitMapping Mapping { get; }

            /// <summary>What a symbol's bits are read against (<c>REQ-DEM-012</c>).</summary>
            /// <remarks>
            /// A case of its own rather than a property of the format, because which encoding an
            /// instrument used is a fact about the instrument. This bench answered it: the ESG's
            /// Custom personality loads <c>P4DQPSK</c> and <c>D8PSK</c> as I/Q maps and leaves
            /// differential encoding to a separate switch, so its signals are read absolutely and
            /// the differential reading of them is the negative control.
            /// </remarks>
            public DifferentialReference Reference { get; }

            /// <summary>The generator's modulation format.</summary>
            public string Format { get; }

            /// <summary>
            /// What OpenVSA demodulates it as, which is not always spelled the same.
            /// </summary>
            /// <remarks>
            /// The instrument calls its eight-point ring <c>PSK8</c> and its sixteen-point QAM
            /// <c>QAM16</c>; this catalogue calls them 8PSK and 16QAM. Where the two names differ
            /// the case says both, rather than a translation table sitting between them deciding
            /// which format a measurement was really of.
            /// </remarks>
            public string Demodulated { get; }

            /// <summary>
            /// How many acquisitions to take before the case is judged, and the reason it is ever
            /// more than one.
            /// </summary>
            /// <remarks>
            /// <para>
            /// An offset format's half-symbol pairing is a free parameter — reading half a symbol
            /// late and turning by 90° gives an equally valid demodulation carrying different bits,
            /// and which one a measurement lands on depends on where the capture happened to start.
            /// So one acquisition of OQPSK is a coin toss, and a single non-match says nothing.
            /// Several of them say a great deal: if the mapping were wrong, none would ever match.
            /// </para>
            /// <para>
            /// <strong>How many is set by the false-alarm rate it leaves.</strong> A coin toss
            /// repeated <c>n</c> times misses altogether once in <c>2^n</c> runs, so four
            /// acquisitions report a good bench as bad in one run of sixteen — measured, twice, over
            /// the course of one afternoon's checks, and each time it costs a re-run to establish
            /// that nothing is wrong. Eight brings that to one run in 256.
            /// </para>
            /// <para>
            /// It costs almost nothing, because the loop stops at the first acquisition that comes
            /// out as expected: the <em>typical</em> case takes two acquisitions whether the bound
            /// is four or eight, and only the runs that were going to be a false alarm take longer.
            /// </para>
            /// </remarks>
            public int Repeats { get; }

            /// <summary>Whether to invert the modulated spectrum (<c>REQ-DEM-035</c>).</summary>
            public bool Mirrored { get; }

            /// <summary>Whether the bits are expected to be the sequence.</summary>
            public bool ExpectMatch { get; }

            /// <summary>
            /// Whether a <em>relabelling</em> of the symbols is expected to be the sequence, or
            /// <c>null</c> not to ask.
            /// </summary>
            /// <remarks>
            /// <para>
            /// The distinction this bench needs and a bit comparison cannot draw. A stream that is
            /// not the sequence can fail in two entirely different ways: the geometry and the
            /// arithmetic can be right with the labels somebody else's, in which case exactly one
            /// relabelling accounts for every symbol; or the demodulation can be wrong, in which
            /// case nothing accounts for it. Asserting which of the two happened is what turns
            /// "these bits are not the sequence" from a shrug into a measurement.
            /// </para>
            /// <para>
            /// It is what verified the differential half of <c>REQ-DEM-012</c> against a real
            /// transmitter: the E4438C's P4DQPSK and D8PSK are symbol-differential and Gray
            /// labelled, so OpenVSA's natural labelling misses the bits and a Gray relabelling
            /// accounts for 511 of 511 symbols of both. The arithmetic is right and the convention
            /// differs, which is exactly what could not be said before.
            /// </para>
            /// </remarks>
            public bool? ExpectRelabelling { get; }

            /// <summary>Why that is expected, in the words the run prints.</summary>
            public string Expectation { get; }

            /// <inheritdoc />
            public override string ToString() =>
                Format +
                (Mapping == BitMapping.Natural ? string.Empty : ", " + Mapping + " labelled") +
                (string.Equals(Demodulated, Format, StringComparison.Ordinal)
                    ? string.Empty
                    : " demodulated as " + Demodulated) +
                (Reference == DifferentialReference.PerFormat
                    ? string.Empty
                    : ", reference " + Reference) +
                (Mirrored ? ", spectrum inverted" : string.Empty);
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
                    "a Gray mapping transposes two symbols, which no rotation undoes",
                    demodulated: "QPSK"),
                new DemodCase(
                    "OQPSK",
                    false,
                    true,
                    "the same points as QPSK with Q sent half a symbol late. Half a symbol and a " +
                    "quarter-turn together are a free parameter, so which pairing a capture lands " +
                    "on is a coin toss and a mis-paired reading scores 75.10 % -- the SAME number " +
                    "a Gray mapping gives, and not a mapping error. Four acquisitions: if the " +
                    "mapping were wrong, none of them would ever match",
                    repeats: 8),
                new DemodCase(
                    "P4DQPSK",
                    false,
                    false,
                    "the reference the format asks for, which is the one this instrument used. " +
                    "The bits will NOT be the sequence, because the instrument labels its phase " +
                    "changes with a Gray code and OpenVSA labels them naturally -- and a Gray " +
                    "relabelling will then account for every symbol, which is the whole claim: " +
                    "the differential arithmetic is right and the convention differs",
                    demodulated: "PI4DQPSK",
                    expectRelabelling: true),
                new DemodCase(
                    "P4DQPSK",
                    false,
                    false,
                    "the same waveform with the reference forced to None, and REQ-DEM-012's " +
                    "criterion on real hardware: nothing accounts for it, under any labelling. " +
                    "Reading a differentially encoded signal absolutely does not degrade the " +
                    "answer, it destroys it -- and the demodulation converges and reports a " +
                    "perfectly good EVM while doing so",
                    demodulated: "PI4DQPSK",
                    reference: DifferentialReference.None,
                    expectRelabelling: false),
                new DemodCase(
                    "D8PSK",
                    false,
                    false,
                    "the same claim on eight points and three bits",
                    expectRelabelling: true),
                new DemodCase(
                    "D8PSK",
                    false,
                    false,
                    "and the same control",
                    reference: DifferentialReference.None,
                    expectRelabelling: false),

                // The same three signals again, against the labelling the relabelling line named.
                // Each one's pair is the whole of REQ-DEM-011's claim: the geometry and the
                // arithmetic never changed, only which bits the points are said to carry, and that
                // is the difference between a stream that is nobody's sequence and one that is
                // exactly the sequence.
                new DemodCase(
                    "GRAYQPSK",
                    false,
                    true,
                    "the case above with the labelling this instrument actually uses. Same " +
                    "waveform, same decisions, same EVM -- and the bits should now BE the sequence",
                    demodulated: "QPSK",
                    mapping: BitMapping.Gray),
                new DemodCase(
                    "P4DQPSK",
                    false,
                    true,
                    "differentially decoded and Gray labelled, which is what this instrument sends",
                    demodulated: "PI4DQPSK",
                    mapping: BitMapping.Gray),
                new DemodCase(
                    "D8PSK",
                    false,
                    true,
                    "and the same on eight points and three bits",
                    mapping: BitMapping.Gray),
            };

            Cases = 0;

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

                        bool asExpected = false;

                        for (int attempt = 1; attempt <= scenario.Repeats; attempt++)
                        {
                            if (scenario.Repeats > 1)
                            {
                                Console.WriteLine(
                                    "  acquisition " + attempt + " of " + scenario.Repeats + ":");
                            }

                            asExpected = await RunDemodCase(
                                frontEnd, stimulus, digital, options, scenario)
                                .ConfigureAwait(false);

                            Console.WriteLine();

                            // Repeats exist for one reason -- a free parameter the capture's timing
                            // chooses -- so the case is judged on whether the expected outcome ever
                            // happened, and stopping at the first one that does is the same
                            // statement in less bench time.
                            if (asExpected)
                            {
                                break;
                            }
                        }

                        if (!asExpected)
                        {
                            wrong++;
                        }
                    }

                    // REQ-DEM-050 to REQ-DEM-052 against a real transmitter and a real front end.
                    // The unit tests inject channels this program has no way to inject, but they
                    // cannot say whether the modes behave on a signal nobody synthesised — and the
                    // equaliser's whole job is a channel nobody described.
                    Cases += EqualiserCases;
                    wrong += await EqualiserCheck(frontEnd, stimulus, digital, options)
                        .ConfigureAwait(false);
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

                int total = cases.Count + Cases;

                Console.WriteLine();
                Console.WriteLine(
                    "  " + (total - wrong) + " of " + total +
                    " cases came out as expected.");

                return wrong == 0 ? 0 : 1;
            }
        }

        /// <summary>How many equaliser cases the last run added to the count.</summary>
        private static int Cases { get; set; }

        /// <summary>How many equaliser cases there are.</summary>
        private const int EqualiserCases = 6;

        /// <summary>
        /// The equaliser's modes and algorithms, against a signal from a real transmitter
        /// (<c>REQ-DEM-050</c>, <c>REQ-DEM-051</c>, <c>REQ-DEM-052</c>).
        /// </summary>
        /// <returns>How many cases did not come out as expected.</returns>
        /// <remarks>
        /// <para>
        /// <strong>What a bench adds here that a unit test cannot.</strong> The unit suite injects
        /// channels of known shape and checks the equaliser takes them out. It cannot check that the
        /// modes behave on a channel nobody described — this cable, this attenuator, this
        /// instrument's own passband — and that is the only kind of channel a user has. The
        /// improvement measured here is small, because the path is a good one; what is being checked
        /// is that each mode does what it says on a real one.
        /// </para>
        /// <para>
        /// <strong>One state object across every acquisition</strong>, as a repeating measurement
        /// has. That is what makes Run's "the coefficients changed" and Hold's "they did not"
        /// statements about successive measurements rather than about one.
        /// </para>
        /// </remarks>
        private static async Task<int> EqualiserCheck(
            E4406AFrontEnd frontEnd,
            IStimulusSource stimulus,
            IDigitalModulationStimulus digital,
            Options options)
        {
            const double SymbolRateHz = 500e3;
            const double RollOff = 0.35;
            const double SpanHz = 5e6;

            // How many blocks a case that compares EVM averages over. Eight, because the bench's
            // own block-to-block spread is comparable with the difference being looked for: see
            // Acquired.
            const int Blocks = 8;

            Console.WriteLine("  The equaliser, on a signal from the generator:");

            digital.SetDigitalModulation(
                options.CenterFrequencyHz,
                options.LevelDbm,
                "QPSK",
                SymbolRateHz,
                StimulusPulseFilter.RootRaisedCosine,
                RollOff,
                "PN9");

            digital.SetSpectrumInverted(false);
            stimulus.SetOutput(true);

            var setup = new MeasurementState
            {
                CenterFrequencyHz = options.CenterFrequencyHz,
                SpanHz = SpanHz,
            };

            setup.SelectKind(MeasurementKind.DigitalDemodulation);

            var contexts = new MeasurementContextSet();
            MeasurementContext demod = contexts.Add("Equaliser", setup);

            // A CHANGE OF SETUP HANDS OVER A NEW STATE. The context caches the chain's settings
            // against the state object it built them from, so a property written in place on the
            // same object is a change nothing sees -- which is how the shell and a recall both work,
            // and how this has to work to be measuring the product rather than a way round it.
            Func<EqualiserMode?, EqualiserAlgorithm, double, DemodState> configure =
                (mode, algorithm, step) =>
                {
                    var state = new DemodState
                    {
                        Format = "QPSK",
                        SymbolRateHz = SymbolRateHz,
                        ResultLengthSymbols = 512,
                        MeasurementFilter = PulseFilterType.RootRaisedCosine,
                        MeasurementFilterAlpha = RollOff,
                        ReferenceFilterAlpha = RollOff,
                        Equaliser = mode.HasValue,
                        EqualiserMode = mode ?? EqualiserMode.Run,
                        EqualiserAlgorithm = algorithm,
                        EqualiserConvergenceFactor = step,
                    };

                    setup.Demod = state;

                    return state;
                };

            int wrong = 0;

            configure(null, EqualiserAlgorithm.LeastSquares, 0.01);

            Acquired off = await Acquire(frontEnd, contexts, demod, options, SpanHz, Blocks)
                .ConfigureAwait(false);

            configure(EqualiserMode.Run, EqualiserAlgorithm.LeastSquares, 0.01);

            Acquired first = await Acquire(frontEnd, contexts, demod, options, SpanHz, Blocks)
                .ConfigureAwait(false);
            Acquired second = await Acquire(frontEnd, contexts, demod, options, SpanHz)
                .ConfigureAwait(false);

            wrong += EqualiserOutcome(
                "REQ-DEM-050",
                "The equaliser does not make a good path worse",
                first.MeanEvm <= off.MeanEvm * 1.05,
                "EVM " + off + " with it off, " + first + " with it on");

            wrong += EqualiserOutcome(
                "REQ-DEM-051",
                "Run fits each measurement and carries the result to the next",
                demod.EqualiserAdaptation.IsAdapted && first.Last != null && second.Last != null &&
                    Moved(first.Last.EqualiserCoefficients, second.Last.EqualiserCoefficients) > 0.0,
                "held " + demod.EqualiserAdaptation.Taps + " taps of norm " +
                Norm(first.Last == null ? null : first.Last.EqualiserCoefficients)
                    .ToString("G4", CultureInfo.InvariantCulture) + " and " +
                Norm(second.Last == null ? null : second.Last.EqualiserCoefficients)
                    .ToString("G4", CultureInfo.InvariantCulture) +
                "; successive measurements moved them by " + Moved(
                    first.Last == null ? null : first.Last.EqualiserCoefficients,
                    second.Last == null ? null : second.Last.EqualiserCoefficients)
                    .ToString("G4", CultureInfo.InvariantCulture));

            configure(EqualiserMode.Hold, EqualiserAlgorithm.LeastSquares, 0.01);

            Acquired held = await Acquire(frontEnd, contexts, demod, options, SpanHz)
                .ConfigureAwait(false);
            Acquired again = await Acquire(frontEnd, contexts, demod, options, SpanHz)
                .ConfigureAwait(false);

            bool frozen = held.Last != null && again.Last != null &&
                held.Last.EqualiserCoefficients != null &&
                again.Last.EqualiserCoefficients != null &&
                Moved(held.Last.EqualiserCoefficients, again.Last.EqualiserCoefficients) == 0.0;

            wrong += EqualiserOutcome(
                "REQ-DEM-051",
                "Hold applies bit-identical coefficients to successive measurements",
                frozen,
                frozen
                    ? "two measurements, " + held.Last.EqualiserCoefficients.Count +
                        " taps, not one bit apart; EVM " + held + " and " + again
                    : "the held coefficients moved between measurements");

            configure(EqualiserMode.Reset, EqualiserAlgorithm.LeastSquares, 0.01);

            Acquired reset = await Acquire(frontEnd, contexts, demod, options, SpanHz)
                .ConfigureAwait(false);

            IReadOnlyList<ConstellationPoint> taps =
                reset.Last == null ? null : reset.Last.EqualiserCoefficients;

            int impulse = setup.Demod.ToSettings().EqualiserImpulseIndex;
            bool unit = taps != null && taps.Count > impulse &&
                Math.Abs(taps[impulse].I - 1.0) < 1e-12 &&
                Math.Abs(taps[impulse].Q) < 1e-12 &&
                !demod.EqualiserAdaptation.IsAdapted;

            wrong += EqualiserOutcome(
                "REQ-DEM-051",
                "Reset returns a unit impulse and forgets what was fitted",
                unit,
                "tap " + impulse + " of " + (taps == null ? 0 : taps.Count) +
                " carries the impulse; EVM " + reset + " against " + off +
                " with no equaliser");

            // AT A STEP SIZE WHOSE OWN EXCESS ERROR LEAVES ROOM INSIDE THE DECIBEL. An LMS
            // filter sits mu*L*Px/2 above the optimum in mean-square error before convergence is
            // even in question, which at 0.01 and these taps is most of the decibel being tested;
            // at 0.003 it is a fraction of it. Measured on this bench at 0.01: 0.90 dB and 1.17 dB
            // on two runs, which is a measurement of the step size rather than of the algorithm.
            configure(EqualiserMode.Run, EqualiserAlgorithm.Lms, 0.003);

            Acquired gradient = await Acquire(frontEnd, contexts, demod, options, SpanHz, Blocks)
                .ConfigureAwait(false);

            double apart = 20.0 * Math.Log10(gradient.MeanEvm / first.MeanEvm);

            wrong += EqualiserOutcome(
                "REQ-DEM-052",
                "LMS lands within a decibel of the exact solution",
                apart < 1.0,
                "LMS " + gradient + "; least squares " + first + "; means " +
                apart.ToString("F2", CultureInfo.InvariantCulture) + " dB apart");

            configure(EqualiserMode.Run, EqualiserAlgorithm.Lms, 5.0);

            Acquired refused = await Acquire(frontEnd, contexts, demod, options, SpanHz)
                .ConfigureAwait(false);

            string bound = null;

            if (refused.Last != null)
            {
                foreach (string notice in refused.Last.Notices)
                {
                    if (notice.IndexOf("2/(L*Px)", StringComparison.Ordinal) >= 0)
                    {
                        bound = notice;
                    }
                }
            }

            wrong += EqualiserOutcome(
                "REQ-DEM-052",
                "A step size past the stability bound is refused, and named",
                bound != null && refused.Last != null && refused.Last.EvmPercent < 25.0,
                bound == null
                    ? "the bound was not reported"
                    : bound.Substring(0, Math.Min(96, bound.Length)) + "…");

            Console.WriteLine();

            return wrong;
        }

        /// <summary>Prints one equaliser case, and counts it.</summary>
        /// <returns><c>1</c> when it failed, so a caller can sum them.</returns>
        private static int EqualiserOutcome(
            string requirement, string what, bool held, string detail)
        {
            Console.WriteLine(
                "    " + (held ? "PASS " : "FAIL ") + requirement.PadRight(12) +
                what.PadRight(58) + " " + detail);

            return held ? 0 : 1;
        }

        /// <summary>The length of a coefficient vector.</summary>
        private static double Norm(IReadOnlyList<ConstellationPoint> taps)
        {
            if (taps == null)
            {
                return double.NaN;
            }

            double total = 0.0;

            foreach (ConstellationPoint tap in taps)
            {
                total += (tap.I * tap.I) + (tap.Q * tap.Q);
            }

            return Math.Sqrt(total);
        }

        /// <summary>How far one set of coefficients is from another.</summary>
        private static double Moved(
            IReadOnlyList<ConstellationPoint> first, IReadOnlyList<ConstellationPoint> second)
        {
            if (first == null || second == null || first.Count != second.Count)
            {
                return double.NaN;
            }

            double total = 0.0;

            for (int tap = 0; tap < first.Count; tap++)
            {
                double i = first[tap].I - second[tap].I;
                double q = first[tap].Q - second[tap].Q;

                total += Math.Sqrt((i * i) + (q * q));
            }

            return total;
        }

        /// <summary>Several blocks of one acquisition, and what they said.</summary>
        /// <remarks>
        /// <para>
        /// <strong>One block cannot answer "within 1 dB".</strong> Measured on this bench, EVM moves
        /// between 0.72 and 0.90 %rms from block to block with the equaliser's coefficients held
        /// bit-identical — a spread of 1.9 dB on a quantity two algorithms are to be compared within
        /// 1 dB on. A comparison of one block against one block would be a comparison of two
        /// different signals, and would pass or fail on which pair of blocks it happened to catch.
        /// </para>
        /// <para>
        /// So a case that compares EVM compares the MEAN over several blocks and prints the spread
        /// it was drawn from, which is the only form in which the reader can tell the comparison was
        /// worth making. The blocks come from one acquisition, so nothing is retuned between them.
        /// </para>
        /// </remarks>
        private sealed class Acquired
        {
            public Acquired(IReadOnlyList<DemodResult> blocks)
            {
                Blocks = blocks;
            }

            /// <summary>Every block demodulated, in order.</summary>
            public IReadOnlyList<DemodResult> Blocks { get; }

            /// <summary>The last block, which is the one whose coefficients are the newest.</summary>
            public DemodResult Last => Blocks.Count == 0 ? null : Blocks[Blocks.Count - 1];

            /// <summary>The mean EVM across the blocks.</summary>
            public double MeanEvm
            {
                get
                {
                    if (Blocks.Count == 0)
                    {
                        return double.NaN;
                    }

                    double total = 0.0;

                    foreach (DemodResult block in Blocks)
                    {
                        total += block.EvmPercent;
                    }

                    return total / Blocks.Count;
                }
            }

            /// <summary>How far apart the best and worst blocks were, in decibels.</summary>
            public double SpreadDb
            {
                get
                {
                    if (Blocks.Count == 0)
                    {
                        return double.NaN;
                    }

                    double least = double.MaxValue;
                    double most = 0.0;

                    foreach (DemodResult block in Blocks)
                    {
                        least = Math.Min(least, block.EvmPercent);
                        most = Math.Max(most, block.EvmPercent);
                    }

                    return 20.0 * Math.Log10(most / least);
                }
            }

            /// <inheritdoc />
            public override string ToString() =>
                MeanEvm.ToString("F4", CultureInfo.InvariantCulture) + " %rms mean of " +
                Blocks.Count.ToString(CultureInfo.InvariantCulture) + " blocks spanning " +
                SpreadDb.ToString("F2", CultureInfo.InvariantCulture) + " dB";
        }

        /// <summary>Takes one acquisition and returns the blocks demodulated from it.</summary>
        /// <remarks>
        /// The first block after a retune is dropped: the instrument is still settling through it,
        /// and a measurement of that is a measurement of the settling.
        /// </remarks>
        private static async Task<Acquired> Acquire(
            E4406AFrontEnd frontEnd,
            MeasurementContextSet contexts,
            MeasurementContext demod,
            Options options,
            double spanHz,
            int blocks = 3)
        {
            var analyser = new ContextAnalyser(contexts);
            var results = new List<DemodResult>();

            EventHandler<DemodResult> collect = (sender, result) =>
            {
                lock (results)
                {
                    results.Add(result);
                }
            };

            demod.ResultAnalysed += collect;

            try
            {
                using (var engine = new SpectrumEngine(frontEnd, null))
                {
                    analyser.Attach(engine);

                    engine.TargetUpdatesPerSecond = 0.0;

                    await engine.StartAsync(
                        new AcquisitionRequest(options.CenterFrequencyHz, spanHz, 32768, 0.0),
                        CancellationToken.None).ConfigureAwait(false);

                    for (int wait = 0; wait < 300; wait++)
                    {
                        lock (results)
                        {
                            if (results.Count > blocks)
                            {
                                break;
                            }
                        }

                        await Task.Delay(200).ConfigureAwait(false);
                    }

                    await engine.StopAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                demod.ResultAnalysed -= collect;
            }

            lock (results)
            {
                return new Acquired(
                    results.Count <= 1
                        ? new List<DemodResult>(results)
                        : results.GetRange(1, results.Count - 1));
            }
        }

        /// <summary>What each point of a constellation carries (<c>REQ-DEM-011</c>).</summary>
        /// <param name="constellation">The constellation the decisions were made against.</param>
        /// <returns>The table, or <c>null</c> under the natural mapping, where a point carries itself.</returns>
        private static IReadOnlyList<int> Labels(Constellation constellation)
        {
            if (constellation.Mapping == BitMapping.Natural)
            {
                return null;
            }

            var labels = new int[constellation.Count];

            for (int symbol = 0; symbol < labels.Length; symbol++)
            {
                labels[symbol] = constellation.CarriedBy(symbol);
            }

            return labels;
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

            setup.Demod.Format = scenario.Demodulated;
            setup.Demod.DifferentialReference = scenario.Reference;
            setup.Demod.BitMapping = scenario.Mapping;
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

            // Which stream to compare, and how many rotations to allow, are one decision:
            //
            //   Differential -- the DATA, and no rotation. The difference of two symbols is
            //   unchanged by turning both, so the freedom has already been divided out and
            //   searching it again would only offer extra chances to agree by accident.
            //
            //   Otherwise -- the decided POINTS, every rotation, and the labelling handed over
            //   separately. A turned constellation moves each point to its neighbour's place and
            //   what it then carries is whatever the labelling says, so the rotation has to be
            //   applied first and the labels second. Comparing the carried values directly and
            //   rotating those would be right only for the natural mapping.
            DemodSettings applied = setup.Demod.ToSettings();

            BitStreamMatch match = applied.DecodesDifferentially
                ? BitStreamAlignment.Find(
                    measured.DataSymbols,
                    measured.Trace.BitsPerSymbol,
                    1 << measured.Trace.BitsPerSymbol,
                    Pattern,
                    rotations: 1)
                : BitStreamAlignment.Find(
                    measured.Symbols,
                    measured.Trace.BitsPerSymbol,
                    1 << measured.Trace.BitsPerSymbol,
                    Pattern,
                    Labels(applied.Constellation));

            Console.WriteLine("    against " + Pattern + "      " + match);

            SymbolRelabellingMatch relabelling = null;

            if (!match.Found)
            {
                // The number a non-match leaves behind is the interesting one. If the geometry is
                // right and only the labels are somebody else's, a relabelling explains the stream
                // outright and this names it -- which is the difference between "this instrument
                // labels its constellation differently" and "something is wrong".
                relabelling = SymbolRelabelling.Explain(
                    measured.DataSymbols,
                    measured.Trace.BitsPerSymbol,
                    1 << measured.Trace.BitsPerSymbol,
                    Pattern);

                Console.WriteLine("    relabelling      " + relabelling);
            }

            if (scenario.ExpectRelabelling.HasValue &&
                (relabelling == null || relabelling.Found) != scenario.ExpectRelabelling.Value)
            {
                Console.WriteLine(
                    "    OUTCOME          NOT AS EXPECTED: a relabelling was " +
                    (scenario.ExpectRelabelling.Value ? "expected to" : "expected not to") +
                    " account for these symbols and " +
                    (relabelling != null && relabelling.Found ? "one does" : "none does") +
                    ". The bits failing to be the sequence is one statement; whether the " +
                    "demodulation was nonetheless right up to a labelling is another, and it is " +
                    "the one that was wrong here.");

                return false;
            }

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

        /// <summary>
        /// Measures the analyser's bandwidth-to-sample-rate law in the instrument's own steps
        /// (<c>REQ-E44-002b</c>).
        /// </summary>
        /// <remarks>
        /// <para>
        /// <strong>Why this exists.</strong> `REQ-E44-002b` had its maximum sample rate wrong until
        /// 23 August 2026 because it was read off the end of a table of six settings, and
        /// <c>EstimateSampleRate</c> still interpolates linearly between zero and the rate at the
        /// widest bandwidth — which measured half the truth at a 5 MHz span. Six points cannot
        /// distinguish a line from a staircase. This measures the staircase.
        /// </para>
        /// <para>
        /// <strong>A ladder, then a bisection, because a ladder alone only bounds a boundary.</strong>
        /// A geometric ladder over the whole range finds how many distinct sample periods there are
        /// and roughly where each begins. That is enough to know the shape and not enough to state a
        /// law: between two adjacent rungs with different periods the boundary could be anywhere. So
        /// every such gap is then bisected to a tenth of a per cent, which is what turns "somewhere
        /// between 1 and 5 MHz" — the honest but useless answer this replaces — into a number.
        /// </para>
        /// <para>
        /// <strong>Two coercions, not one.</strong> The instrument coerces the commanded bandwidth to
        /// one it can afford, and then picks a decimation from what it settled on. Both are recorded
        /// at every point, because a law stated in commanded bandwidth silently composes the two and
        /// would not survive a firmware that changed either.
        /// </para>
        /// </remarks>
        private static async Task<int> ProbeBandwidth(Options options)
        {
            const int LadderPoints = 40;
            const int BisectionLimit = 16;

            Console.WriteLine("OpenVSA analyser bandwidth law");
            Console.WriteLine("  analyser  " + options.AnalyserResource);
            Console.WriteLine();

            using (var frontEnd = new E4406AFrontEnd(options.AnalyserResource, null))
            {
                await frontEnd.ConnectAsync(CancellationToken.None).ConfigureAwait(false);

                Console.WriteLine("  measuring with " + frontEnd.DisplayName.Split('\n')[0].Trim());

                double minimum = frontEnd.Capabilities.MinSpanHz;
                double maximum = frontEnd.Capabilities.MaxSpanHz;

                Console.WriteLine(
                    "  the instrument reports bandwidth limits of " +
                    Engineering(minimum) + "Hz to " + Engineering(maximum) + "Hz, and a maximum " +
                    "sample rate of " +
                    (frontEnd.Capabilities.MaxSampleRateHz / 1e6).ToString(
                        "G6", CultureInfo.InvariantCulture) + " MS/s");
                Console.WriteLine();

                var readings = new List<E4406AFrontEnd.BandwidthReading>();

                Console.WriteLine("  ladder of " + LadderPoints + " points, geometric:");

                for (int point = 0; point < LadderPoints; point++)
                {
                    double fraction = point / (double)(LadderPoints - 1);
                    double commanded = minimum * Math.Pow(maximum / minimum, fraction);

                    E4406AFrontEnd.BandwidthReading reading =
                        frontEnd.MeasureBandwidthPoint(commanded);

                    readings.Add(reading);
                    Console.WriteLine("    " + reading);
                }

                Console.WriteLine();

                // Every place two adjacent rungs disagree is a boundary the ladder has only bounded.
                var steps = new List<StepBoundary>();

                for (int point = 1; point < readings.Count; point++)
                {
                    if (SamePeriod(readings[point - 1], readings[point]))
                    {
                        continue;
                    }

                    double low = readings[point - 1].CommandedHz;
                    double high = readings[point].CommandedHz;
                    double lowPeriod = readings[point - 1].ApertureSeconds;

                    E4406AFrontEnd.BandwidthReading below = readings[point - 1];
                    E4406AFrontEnd.BandwidthReading above = readings[point];

                    for (int iteration = 0; iteration < BisectionLimit; iteration++)
                    {
                        if (high - low <= Math.Max(1.0, high * 1e-4))
                        {
                            break;
                        }

                        double middle = (low + high) / 2.0;
                        E4406AFrontEnd.BandwidthReading probe =
                            frontEnd.MeasureBandwidthPoint(middle);

                        if (Math.Abs(probe.ApertureSeconds - lowPeriod) <=
                            lowPeriod * PeriodTolerance)
                        {
                            low = middle;
                            below = probe;
                        }
                        else
                        {
                            high = middle;
                            above = probe;
                        }
                    }

                    steps.Add(new StepBoundary(low, high, below, above));
                }

                Console.WriteLine("  boundaries, bisected:");

                if (steps.Count == 0)
                {
                    Console.WriteLine(
                        "    none — one sample period covered the whole range, which would make the " +
                        "linear model wrong everywhere rather than in places.");
                }

                foreach (StepBoundary step in steps)
                {
                    Console.WriteLine("    " + step);
                }

                Console.WriteLine();
                Console.WriteLine("  the law, as measured:");

                foreach (string row in Runs(readings, steps))
                {
                    Console.WriteLine("    " + row);
                }

                Console.WriteLine();

                int notIntegral = 0;

                foreach (E4406AFrontEnd.BandwidthReading reading in readings)
                {
                    if (Math.Abs(reading.Ticks - Math.Round(reading.Ticks)) > 0.01)
                    {
                        Console.WriteLine(
                            "    NOT A WHOLE NUMBER OF 1/15 MHz TICKS: " + reading);
                        notIntegral++;
                    }
                }

                Console.WriteLine(
                    "  every sample period a whole number of 1/15 MHz ticks: " +
                    (notIntegral == 0 ? "yes, at all " + readings.Count + " points"
                        : "NO, at " + notIntegral + " of " + readings.Count + " points"));

                // Measured against the model the planner actually uses, because "the estimate is
                // wrong" is worth nothing without the size of the error and where it is worst.
                double worst = 1.0;
                double worstDistance = 0.0;
                double worstAt = 0.0;

                foreach (E4406AFrontEnd.BandwidthReading reading in readings)
                {
                    if (!(reading.SampleRateHz > 0.0))
                    {
                        continue;
                    }

                    double estimated = frontEnd.Capabilities.MaxSampleRateHz *
                        (reading.CommandedHz / frontEnd.Capabilities.MaxSpanHz);

                    if (estimated > frontEnd.Capabilities.MaxSampleRateHz)
                    {
                        estimated = frontEnd.Capabilities.MaxSampleRateHz;
                    }

                    double ratio = estimated / reading.SampleRateHz;
                    double distance = Math.Abs(Math.Log(ratio));

                    if (distance > worstDistance)
                    {
                        worstDistance = distance;
                        worst = ratio;
                        worstAt = reading.CommandedHz;
                    }
                }

                Console.WriteLine(
                    "  the linear estimate's worst error over this ladder: x" +
                    worst.ToString("G4", CultureInfo.InvariantCulture) + " at " +
                    Engineering(worstAt) + "Hz commanded");

                if (!string.IsNullOrEmpty(options.ResultFile))
                {
                    WriteBandwidthFile(options.ResultFile, readings);
                    Console.WriteLine("  readings written to " + options.ResultFile);
                }

                // Left at the widest bandwidth, which is where connecting leaves it anyway: probing
                // the capabilities sets it there. Said rather than implied, because a probe that
                // moves a setting and stays quiet about it is how a bench ends up in a state nobody
                // chose.
                frontEnd.MeasureBandwidthPoint(maximum);
                Console.WriteLine("  bandwidth left at the widest, as connecting leaves it.");

                return notIntegral == 0 ? 0 : 1;
            }
        }

        /// <summary>How closely two sample periods must agree to be the same decimation step.</summary>
        /// <remarks>
        /// Loose enough for the instrument's own rounding of a period it reports in nanoseconds,
        /// tight enough that adjacent steps — which differ by whole ticks, so never by less than a
        /// half — cannot be confused. Nothing in between exists to be misjudged.
        /// </remarks>
        private const double PeriodTolerance = 1e-6;

        private static bool SamePeriod(
            E4406AFrontEnd.BandwidthReading first, E4406AFrontEnd.BandwidthReading second) =>
            Math.Abs(first.ApertureSeconds - second.ApertureSeconds) <=
                Math.Max(first.ApertureSeconds, second.ApertureSeconds) * PeriodTolerance;

        /// <summary>Where the instrument changes decimation, bracketed as tightly as it was found.</summary>
        private sealed class StepBoundary
        {
            public StepBoundary(
                double lastLowHz,
                double firstHighHz,
                E4406AFrontEnd.BandwidthReading below,
                E4406AFrontEnd.BandwidthReading above)
            {
                LastLowHz = lastLowHz;
                FirstHighHz = firstHighHz;
                Below = below;
                Above = above;
            }

            /// <summary>The widest commanded bandwidth still on the slower side.</summary>
            public double LastLowHz { get; }

            /// <summary>The narrowest commanded bandwidth found on the faster side.</summary>
            public double FirstHighHz { get; }

            /// <summary>The reading just below the boundary.</summary>
            public E4406AFrontEnd.BandwidthReading Below { get; }

            /// <summary>The reading just above it.</summary>
            public E4406AFrontEnd.BandwidthReading Above { get; }

            /// <inheritdoc />
            public override string ToString() =>
                Engineering(LastLowHz) + "Hz .. " + Engineering(FirstHighHz) + "Hz commanded: " +
                (Below.SampleRateHz / 1e6).ToString("G6", CultureInfo.InvariantCulture) + " MS/s (" +
                Math.Round(Below.Ticks) + " ticks, " + Engineering(Below.ActualHz) +
                "Hz actual) becomes " +
                (Above.SampleRateHz / 1e6).ToString("G6", CultureInfo.InvariantCulture) + " MS/s (" +
                Math.Round(Above.Ticks) + " ticks, " + Engineering(Above.ActualHz) + "Hz actual)" +
                ", bracketed to " +
                (FirstHighHz - LastLowHz <= 0.0
                    ? "nothing"
                    : Engineering(FirstHighHz - LastLowHz) + "Hz");
        }

        /// <summary>Collapses the ladder into the runs of commanded bandwidth that share a rate.</summary>
        private static IReadOnlyList<string> Runs(
            IReadOnlyList<E4406AFrontEnd.BandwidthReading> readings,
            IReadOnlyList<StepBoundary> steps)
        {
            var rows = new List<string>();

            if (readings.Count == 0)
            {
                return rows;
            }

            int start = 0;

            for (int point = 1; point <= readings.Count; point++)
            {
                bool last = point == readings.Count;

                if (!last && SamePeriod(readings[point - 1], readings[point]))
                {
                    continue;
                }

                E4406AFrontEnd.BandwidthReading first = readings[start];
                E4406AFrontEnd.BandwidthReading final = readings[point - 1];

                // The run's true lower edge is the bisected boundary below it where there is one,
                // not the rung that happened to be measured.
                string from = Engineering(first.CommandedHz) + "Hz";

                foreach (StepBoundary step in steps)
                {
                    if (SamePeriod(step.Above, first))
                    {
                        from = "~" + Engineering(step.FirstHighHz) + "Hz";
                    }
                }

                rows.Add(
                    from + " .. " + Engineering(final.CommandedHz) + "Hz commanded  ->  " +
                    (first.SampleRateHz / 1e6).ToString("G6", CultureInfo.InvariantCulture) +
                    " MS/s, " + Math.Round(first.Ticks) + " x 1/15 MHz, actual bandwidth " +
                    Engineering(first.ActualHz) + "Hz .. " + Engineering(final.ActualHz) + "Hz");

                start = point;
            }

            return rows;
        }

        private static void WriteBandwidthFile(
            string path, IReadOnlyList<E4406AFrontEnd.BandwidthReading> readings)
        {
            var text = new StringBuilder();
            text.AppendLine("commanded_hz\tactual_hz\taperture_s\tsample_rate_hz\tticks");

            foreach (E4406AFrontEnd.BandwidthReading reading in readings)
            {
                text.AppendLine(string.Join(
                    "\t",
                    Invariant(reading.CommandedHz),
                    Invariant(reading.ActualHz),
                    Invariant(reading.ApertureSeconds),
                    Invariant(reading.SampleRateHz),
                    Invariant(reading.Ticks)));
            }

            File.WriteAllText(path, text.ToString());
        }

        /// <summary>Formats a frequency with an engineering prefix, so a table can be read.</summary>
        private static string Engineering(double hertz)
        {
            if (hertz >= 1e6)
            {
                return (hertz / 1e6).ToString("G6", CultureInfo.InvariantCulture) + " M";
            }

            if (hertz >= 1e3)
            {
                return (hertz / 1e3).ToString("G6", CultureInfo.InvariantCulture) + " k";
            }

            return hertz.ToString("G6", CultureInfo.InvariantCulture) + " ";
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

            if (options.ProbeBandwidth)
            {
                return await ProbeBandwidth(options).ConfigureAwait(false);
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

            /// <summary>
            /// Whether to measure the analyser's bandwidth-to-sample-rate law in its own steps.
            /// </summary>
            public bool ProbeBandwidth { get; private set; }

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

                        case "--probe-bandwidth":
                            options.ProbeBandwidth = true;
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
                Console.WriteLine("  --probe-bandwidth       measure the analyser's bandwidth-to-sample-rate");
                Console.WriteLine("                          law in its own steps; needs no generator");
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
