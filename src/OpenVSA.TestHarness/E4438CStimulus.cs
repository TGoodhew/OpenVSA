using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using OpenVSA.Hal.Visa;

namespace OpenVSA.TestHarness
{
    /// <summary>
    /// A Keysight E4438C ESG driven over VISA, as stimulus for cross-validation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only what a verification scenario needs: an unmodulated carrier at a stated frequency and
    /// level, and the ability to turn it off. The instrument's ARB, sweep and modulation
    /// subsystems are not touched, and the Dual ARB path of issue #393 is deliberately left for
    /// when a scenario needs a modulated stimulus.
    /// </para>
    /// <para>
    /// Every setting is read back after it is sent. A harness that reports what it asked for has
    /// verified the analyser against its own intentions rather than against a signal — and this
    /// bench has already produced the failure that guards against, when a generator retuned
    /// between two runs made a correct measurement look like a mirrored spectrum.
    /// </para>
    /// </remarks>
    [StimulusProvider(
        "Signal generator over VISA",
        RequiresResource = true,
        DefaultResource = DefaultResource)]
    public sealed class E4438CStimulus : IStimulusSource, IMultitoneStimulus, INoiseStimulus,
        IStimulusLimits, IDigitalModulationStimulus
    {
        /// <summary>Resource used when configuration names none.</summary>
        /// <remarks>
        /// <strong>This address moves, and a stale one does not look like a stale one.</strong> It
        /// was <c>192.168.1.82</c> and became <c>192.168.1.85</c>; VISA answers a wrong address with
        /// "Insufficient location information or the device or resource is not present in the
        /// system", which reads exactly like a powered-off instrument and was once reported as one.
        /// Set <see cref="ResourceSettingKey"/> or its environment variable rather than relying on
        /// this.
        /// </remarks>
        public const string DefaultResource = "TCPIP0::192.168.1.85::inst1::INSTR";

        /// <summary><c>appSettings</c> key naming the VISA resource to open.</summary>
        public const string ResourceSettingKey = "OpenVSA.Visa.E4438C.Resource";

        /// <summary>How long to allow the instrument to synthesise a noise waveform.</summary>
        /// <remarks>
        /// Measured at <strong>10.8 s</strong> for a 5 MHz band on firmware C.05.85. Three times
        /// that, because the figure will scale with bandwidth and a harness that fails on a wider
        /// band would be reporting its own impatience as an instrument fault.
        /// </remarks>
        public const int NoiseBuildTimeoutMilliseconds = 30000;

        private readonly Func<string, IInstrumentSession> _openSession;
        private readonly string _resourceName;
        private readonly List<string> _sent = new List<string>();

        private IInstrumentSession _session;
        private bool _disposed;

        /// <summary>Creates a stimulus source over the configured resource.</summary>
        public E4438CStimulus()
            : this(VisaConfiguration.ResourceFor(ResourceSettingKey, DefaultResource), null)
        {
        }

        /// <summary>Creates a stimulus source over a named resource.</summary>
        /// <param name="resourceName">VISA resource string.</param>
        /// <exception cref="ArgumentException"><paramref name="resourceName"/> is missing.</exception>
        /// <remarks>
        /// The constructor the shell uses. It asks the user for the resource rather than reading
        /// configuration, because the panel's whole job is to open the instrument in front of the
        /// person using it, and this bench's generator has moved address before.
        /// </remarks>
        public E4438CStimulus(string resourceName)
            : this(resourceName, null)
        {
        }

        /// <summary>Creates a stimulus source.</summary>
        /// <param name="resourceName">VISA resource string.</param>
        /// <param name="openSession">Opens a session, or <c>null</c> for a real VISA one.</param>
        /// <exception cref="ArgumentException"><paramref name="resourceName"/> is missing.</exception>
        public E4438CStimulus(string resourceName, Func<string, IInstrumentSession> openSession)
        {
            if (string.IsNullOrEmpty(resourceName))
            {
                throw new ArgumentException("A VISA resource name is required.", nameof(resourceName));
            }

            _resourceName = resourceName;
            _openSession = openSession ?? (resource => VisaSession.Open(resource));
        }

        /// <inheritdoc />
        public string DisplayName { get; private set; } = "Keysight E4438C";

        /// <inheritdoc />
        public bool IsOutputEnabled { get; private set; }

        /// <inheritdoc />
        public double FrequencyHz { get; private set; }

        /// <inheritdoc />
        public double LevelDbm { get; private set; }

        /// <summary>Commands sent since construction, for diagnostics.</summary>
        public IReadOnlyList<string> Sent
        {
            get { lock (_sent) { return _sent.ToArray(); } }
        }

        /// <inheritdoc />
        public void Connect()
        {
            ThrowIfDisposed();

            IInstrumentSession session = _openSession(_resourceName);

            try
            {
                // As found, not as hoped: a query left unread by the last program makes the first
                // command here fail with -410 and blames this driver for it.
                session.Clear();
                Send(session, "*CLS");

                string identity = Query(session, "*IDN?");

                if (identity == null ||
                    identity.IndexOf("E4438C", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    throw new InvalidOperationException(
                        "The instrument at '" + _resourceName + "' identifies itself as '" +
                        identity + "', which is not an E4438C.");
                }

                DisplayName = identity;
            }
            catch (Exception failure)
            {
                session.Dispose();

                throw new InvalidOperationException(
                    "Could not use the stimulus source at '" + _resourceName + "'. " +
                    failure.Message + Environment.NewLine + Environment.NewLine +
                    "Set '" + ResourceSettingKey + "' or the " +
                    VisaConfiguration.EnvironmentVariableFor(ResourceSettingKey) +
                    " environment variable to point at it.",
                    failure);
            }

            _session = session;
            Refresh();
        }

        /// <inheritdoc />
        public void SetContinuousWave(double frequencyHz, double levelDbm)
        {
            IInstrumentSession session = RequireSession();

            // Modulation off explicitly. A generator left modulated by whoever used it last
            // produces a spread spectrum where the scenario expects a tone, and the failure reads
            // as a defect in the analyser.
            //
            // Both baseband personalities off by name as well, not just the modulator: leaving one
            // running makes Refresh report a live comb or noise band on what is meant to be a bare
            // carrier, and a scenario reading that back would take its expectation from it.
            Send(session, ":RADio:MTONe:ARB:STATe OFF");
            Send(session, ":RADio:AWGN:ARB:STATe OFF");
            Send(session, ":OUTPut:MODulation:STATe OFF");
            Send(session, ":FREQuency:CW " + Number(frequencyHz) + " HZ");
            Send(session, ":POWer:AMPLitude " + Number(levelDbm) + " dBm");

            Query(session, "*OPC?");
            ThrowOnInstrumentError(session, "setting the carrier");

            Refresh();
        }

        /// <inheritdoc />
        public int MinimumTones { get; private set; } = 2;

        /// <inheritdoc />
        public int MaximumTones { get; private set; } = 64;

        /// <inheritdoc />
        public int ToneCount { get; private set; }

        /// <inheritdoc />
        public double ToneSpacingHz { get; private set; }

        /// <summary>
        /// Sets the multitone comb, in the order the instrument requires.
        /// </summary>
        /// <param name="centreFrequencyHz">Carrier the comb is centred on, in hertz.</param>
        /// <param name="toneCount">How many tones.</param>
        /// <param name="spacingHz">Spacing between adjacent tones, in hertz.</param>
        /// <param name="levelDbm">Total output level, in dBm.</param>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="toneCount"/> is unsupported.</exception>
        /// <remarks>
        /// <para>
        /// <strong>The order is the manual's, and it is not arbitrary.</strong> The SCPI reference
        /// numbers these as the first three steps of "Creating a Multitone Waveform": phase
        /// initialisation, then spacing, then tone count. Setting the count before the spacing
        /// re-tables the waveform and the spacing is applied to a table that is then rebuilt.
        /// </para>
        /// <para>
        /// <strong>Modulation goes back ON.</strong> The comb is generated by the internal baseband
        /// generator and reaches the RF output through the modulator, which
        /// <see cref="SetContinuousWave"/> deliberately switches off. Leaving it off produces a bare
        /// carrier and a scenario that then reports "one tone where five were asked for" — a
        /// failure that reads as a defect in the analyser.
        /// </para>
        /// <para>
        /// Phase is initialised FIXed rather than RANDom so that two runs of the same scenario
        /// produce the same waveform. A random phase set changes the comb's peak-to-average ratio
        /// between runs, which moves the measured tone levels for no reason the harness can report.
        /// </para>
        /// </remarks>
        public void SetMultitone(
            double centreFrequencyHz, int toneCount, double spacingHz, double levelDbm)
        {
            IInstrumentSession session = RequireSession();

            if (toneCount < MinimumTones || toneCount > MaximumTones)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(toneCount), toneCount,
                    "This source produces between " + MinimumTones + " and " + MaximumTones +
                    " tones.");
            }

            Send(session, ":FREQuency:CW " + Number(centreFrequencyHz) + " HZ");
            Send(session, ":POWer:AMPLitude " + Number(levelDbm) + " dBm");

            Send(session, ":RADio:MTONe:ARB:SETup:TABLe:PHASe:INITialize FIXed");
            Send(session, ":RADio:MTONe:ARB:SETup:TABLe:FSPacing " + Number(spacingHz) + " HZ");
            Send(session, ":RADio:MTONe:ARB:SETup:TABLe:NTONes " + toneCount.ToString(
                CultureInfo.InvariantCulture));

            Send(session, ":RADio:MTONe:ARB:STATe ON");
            Send(session, ":OUTPut:MODulation:STATe ON");

            Query(session, "*OPC?");
            ThrowOnInstrumentError(session, "setting the multitone comb");

            Refresh();
        }

        /// <inheritdoc />
        /// <remarks>
        /// The manual's range for <c>:RADio:AWGN:ARB:BWIDth</c> on Option 403: 50 kHz to 15 MHz.
        /// Not probed — see the note beside the multitone limits for what a rejected range query
        /// costs on this firmware.
        /// </remarks>
        public double MinimumNoiseBandwidthHz => 50e3;

        /// <inheritdoc />
        public double MaximumNoiseBandwidthHz => 15e6;

        /// <inheritdoc />
        public double NoiseBandwidthHz { get; private set; }

        /// <summary>
        /// Sets band-limited additive white Gaussian noise (Option 403).
        /// </summary>
        /// <param name="centreFrequencyHz">Centre of the noise band, in hertz.</param>
        /// <param name="bandwidthHz">Noise bandwidth, in hertz.</param>
        /// <param name="levelDbm">Total power in the band, in dBm.</param>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="bandwidthHz"/> is unsupported.</exception>
        /// <remarks>
        /// <para>
        /// <strong>The multitone comb is switched off explicitly.</strong> Both personalities feed
        /// the same baseband generator, and a comb left on from a previous scenario would be
        /// measured as a very unflat noise floor — a failure that reads as a defect in the
        /// analyser's density calculation.
        /// </para>
        /// <para>
        /// Modulation goes on, for the reason it does for the comb: the noise reaches the RF output
        /// through the modulator that <see cref="SetContinuousWave"/> switches off.
        /// </para>
        /// </remarks>
        public void SetNoise(double centreFrequencyHz, double bandwidthHz, double levelDbm)
        {
            IInstrumentSession session = RequireSession();

            if (bandwidthHz < MinimumNoiseBandwidthHz || bandwidthHz > MaximumNoiseBandwidthHz)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(bandwidthHz), bandwidthHz,
                    "This source produces noise between " + MinimumNoiseBandwidthHz + " and " +
                    MaximumNoiseBandwidthHz + " Hz wide.");
            }

            Send(session, ":RADio:MTONe:ARB:STATe OFF");

            Send(session, ":FREQuency:CW " + Number(centreFrequencyHz) + " HZ");
            Send(session, ":POWer:AMPLitude " + Number(levelDbm) + " dBm");

            Send(session, ":RADio:AWGN:ARB:BWIDth " + Number(bandwidthHz) + " HZ");
            Send(session, ":RADio:AWGN:ARB:STATe ON");
            Send(session, ":OUTPut:MODulation:STATe ON");

            // Enabling AWGN makes the instrument SYNTHESISE the noise waveform, and that is slow:
            // measured at 10.8 s for a 5 MHz band on firmware C.05.85, against a default I/O
            // timeout of a few seconds. The first attempt at this scenario failed with a bare
            // IOTimeoutException, which - after a day of learning that a rejected command also
            // returns nothing - looked exactly like an unsupported command and was not one.
            //
            // Raised only around this wait, and restored afterwards, so an instrument that really
            // has gone away still fails promptly everywhere else.
            int wasTimeout = session.TimeoutMilliseconds;

            try
            {
                session.TimeoutMilliseconds = Math.Max(wasTimeout, NoiseBuildTimeoutMilliseconds);
                Query(session, "*OPC?");
            }
            finally
            {
                session.TimeoutMilliseconds = wasTimeout;
            }

            ThrowOnInstrumentError(session, "setting the noise band");

            Refresh();
        }

        // ---- IDigitalModulationStimulus (Option 001/601 or 002/602) ----------------------------

        /// <summary>
        /// The Custom personality's formats, named as the instrument names them.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A subset of what <c>:RADio:CUSTom:MODulation:TYPE</c> accepts — the manual lists forty-odd
        /// entries including the IS-95 and APCO variants, the APSK code rates and user files. These
        /// are the ones a demodulator built to <c>REQ-DEM-010</c> has anything to say about, and
        /// naming only those keeps a scenario from setting a signal nothing can measure.
        /// </para>
        /// <para>
        /// <strong>QPSK and GRAYQPSK are both here on purpose.</strong> They differ only in bit
        /// mapping, so running one measurement against each is what settles which mapping OpenVSA
        /// implements — a question its own generator cannot answer, because both ends of that
        /// comparison would be OpenVSA's.
        /// </para>
        /// </remarks>
        public IReadOnlyList<string> Formats { get; } = new ReadOnlyCollection<string>(
            new List<string>
            {
                "BPSK", "QPSK", "GRAYQPSK", "OQPSK", "P4DQPSK", "PSK8", "PSK16", "D8PSK",
                "MSK", "FSK2", "FSK4", "FSK8", "FSK16",
                "QAM4", "QAM16", "QAM32", "QAM64", "QAM128", "QAM256",
            });

        /// <summary>
        /// The pseudo-random patterns the Custom personality transmits.
        /// </summary>
        /// <remarks>
        /// <c>FIX4</c> and file-based patterns are left out: a fixed four-bit pattern makes a
        /// constellation of four points whatever the format is, and a file pattern is only as
        /// reproducible as the file. A PN sequence can be generated outside the instrument, which is
        /// what makes "the recovered bits are the transmitted bits" a comparison rather than a
        /// demodulator agreeing with itself.
        /// </remarks>
        public IReadOnlyList<string> DataPatterns { get; } = new ReadOnlyCollection<string>(
            new List<string> { "PN9", "PN11", "PN15", "PN20", "PN23" });

        /// <inheritdoc />
        public string Format { get; private set; }

        /// <inheritdoc />
        public double SymbolRateHz { get; private set; }

        /// <inheritdoc />
        public StimulusPulseFilter PulseFilter { get; private set; }

        /// <inheritdoc />
        public double Alpha { get; private set; }

        /// <inheritdoc />
        public string DataPattern { get; private set; }

        /// <inheritdoc />
        public bool IsSpectrumInverted { get; private set; }

        /// <summary>The slowest symbol rate the Custom personality produces.</summary>
        /// <remarks>
        /// The manual's floor for every filter in its symbol-rate table is 4 symbols per second. Not
        /// probed: a range query this firmware rejects does not answer at all, it times out — see
        /// the note beside the multitone limits for what that costs.
        /// </remarks>
        public double MinimumSymbolRateHz => 4.0;

        /// <summary>
        /// The fastest symbol rate the Custom personality produces for a filter.
        /// </summary>
        /// <param name="filter">The pulse-shaping filter.</param>
        /// <returns>The maximum symbol rate, in symbols per second.</returns>
        /// <remarks>
        /// <para>
        /// From the manual's symbol-rate table at the 32-symbol filter length the instrument
        /// truncates to in order to reach its higher rates: QPSK and QAM4 reach 12.5 Msps, the
        /// Gaussian-filtered formats less. The ceiling is filter-dependent because the instrument
        /// shortens its filter to reach higher rates and refuses to shorten below a minimum length,
        /// so the limit belongs to the pair rather than to the instrument.
        /// </para>
        /// <para>
        /// <strong>The analyser is the tighter constraint on this bench.</strong>
        /// <c>REQ-E44-002b</c> measured the E4406A's capture path at 7.5 MS/s maximum, so a scenario
        /// runs out of samples per symbol long before it reaches these figures. Recorded because a
        /// source should know its own limits, not because anything will approach them.
        /// </para>
        /// </remarks>
        public double MaximumSymbolRateHz(StimulusPulseFilter filter)
        {
            return filter == StimulusPulseFilter.Gaussian ? 6.25e6 : 12.5e6;
        }

        /// <inheritdoc />
        public void SetDigitalModulation(
            double frequencyHz,
            double levelDbm,
            string format,
            double symbolRateHz,
            StimulusPulseFilter filter,
            double alpha,
            string dataPattern)
        {
            IInstrumentSession session = RequireSession();

            RequireOffered(Formats, format, "format");
            RequireOffered(DataPatterns, dataPattern, "data pattern");

            double ceiling = MaximumSymbolRateHz(filter);

            if (symbolRateHz < MinimumSymbolRateHz || symbolRateHz > ceiling)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(symbolRateHz),
                    symbolRateHz,
                    "This source produces " + Number(MinimumSymbolRateHz) + " to " +
                    Number(ceiling) + " symbols per second with the " + filter + " filter.");
            }

            if (alpha < 0.0 || alpha > 1.0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(alpha), alpha, "The filter's roll-off runs from 0 to 1.");
            }

            Send(session, ":FREQuency:CW " + Number(frequencyHz) + " HZ");
            Send(session, ":POWer:AMPLitude " + Number(levelDbm) + " dBm");

            Send(session, ":RADio:CUSTom:MODulation:TYPE " + format);
            Send(session, ":RADio:CUSTom:SRATe " + Number(symbolRateHz));
            Send(session, ":RADio:CUSTom:FILTer " + FilterWord(filter));

            // Only the Nyquist pair has a roll-off. Sending one to the Gaussian or the rectangular
            // filter would leave an error in the queue for whatever ran next to be blamed for.
            if (filter == StimulusPulseFilter.RootRaisedCosine ||
                filter == StimulusPulseFilter.RaisedCosine)
            {
                Send(session, ":RADio:CUSTom:ALPHa " + Number(alpha));
            }

            if (filter == StimulusPulseFilter.Gaussian)
            {
                Send(session, ":RADio:CUSTom:BBT " + Number(BandwidthTime));
            }

            // Stated rather than left at the reset value, because it is the whole of what MSK is:
            // a right angle of phase across a symbol, which is a deviation of a quarter the symbol
            // rate. The instrument resets to 90 degrees and a previous setup may not have.
            if (string.Equals(format, "MSK", StringComparison.OrdinalIgnoreCase))
            {
                Send(session, ":RADio:CUSTom:MODulation:MSK:PHASe 90");
            }

            // 🔴 THE RESET DEVIATION IS 400 Hz, which on any symbol rate this bench uses is a
            // modulation index near zero -- a carrier with a wobble on it rather than FSK. Left
            // alone it would produce a signal that demodulates to noise and looks like a
            // demodulator fault.
            if (format.StartsWith("FSK", StringComparison.OrdinalIgnoreCase))
            {
                Send(
                    session,
                    ":RADio:CUSTom:MODulation:FSK:DEViation " +
                    Number(DeviationPerSymbolRate * symbolRateHz) + " HZ");
            }

            Send(session, ":RADio:CUSTom:DATA " + dataPattern);

            Send(session, ":RADio:CUSTom:STATe ON");
            Send(session, ":OUTPut:MODulation:STATe ON");

            Query(session, "*OPC?");
            ThrowOnInstrumentError(session, "setting the digital modulation");

            Refresh();
        }

        /// <summary>
        /// The bandwidth-time product the Gaussian pre-modulation filter is set to.
        /// </summary>
        /// <remarks>
        /// Only the Gaussian filter has one; the instrument says so and refuses to be told
        /// otherwise. Three tenths is GSM's, and it is the BbT the analyser's own linearised-GMSK
        /// pulse is derived at, so it is the value at which the two ends are describing the same
        /// signal.
        /// </remarks>
        public double BandwidthTime { get; set; } = 0.3;

        /// <summary>
        /// The peak FSK deviation to set, as a fraction of the symbol rate.
        /// </summary>
        /// <remarks>
        /// A half, which is a modulation index of one: wide enough that the levels are well apart
        /// and narrow enough to sit inside the analyser's bandwidth at every order this bench
        /// measures. The instrument states the same quantity in hertz and resets it to 400, which is
        /// why it is always sent rather than assumed.
        /// </remarks>
        public double DeviationPerSymbolRate { get; set; } = 0.5;

        /// <inheritdoc />
        public void SetSpectrumInverted(bool inverted)
        {
            IInstrumentSession session = RequireSession();

            Send(session, ":RADio:CUSTom:POLarity:ALL " + (inverted ? "INVerted" : "NORMal"));

            Query(session, "*OPC?");
            ThrowOnInstrumentError(
                session, inverted ? "inverting the spectrum" : "restoring the spectrum");

            Refresh();
        }

        /// <inheritdoc />
        public void StopDigitalModulation()
        {
            IInstrumentSession session = RequireSession();

            Send(session, ":RADio:CUSTom:STATe OFF");

            Query(session, "*OPC?");
            ThrowOnInstrumentError(session, "stopping the digital modulation");

            Refresh();
        }

        private static string FilterWord(StimulusPulseFilter filter)
        {
            switch (filter)
            {
                case StimulusPulseFilter.RaisedCosine:
                    return "NYQuist";

                case StimulusPulseFilter.Gaussian:
                    return "GAUSsian";

                case StimulusPulseFilter.Rectangular:
                    return "RECTangle";

                default:
                    return "RNYQuist";
            }
        }

        private static StimulusPulseFilter FilterFrom(string word)
        {
            string trimmed = (word ?? string.Empty).Trim().ToUpperInvariant();

            if (trimmed.StartsWith("NYQ", StringComparison.Ordinal))
            {
                return StimulusPulseFilter.RaisedCosine;
            }

            if (trimmed.StartsWith("RECT", StringComparison.Ordinal))
            {
                return StimulusPulseFilter.Rectangular;
            }

            return trimmed.StartsWith("GAUS", StringComparison.Ordinal)
                ? StimulusPulseFilter.Gaussian
                : StimulusPulseFilter.RootRaisedCosine;
        }

        private static void RequireOffered(
            IReadOnlyList<string> offered, string wanted, string what)
        {
            foreach (string candidate in offered)
            {
                if (string.Equals(candidate, wanted, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }

            throw new ArgumentException(
                "This source does not offer the " + what + " asked for. It offers: " +
                string.Join(", ", new List<string>(offered).ToArray()) + ".",
                what);
        }

        /// <summary>Switches the noise off and leaves an unmodulated carrier.</summary>
        public void StopNoise()
        {
            IInstrumentSession session = RequireSession();

            Send(session, ":RADio:AWGN:ARB:STATe OFF");
            Send(session, ":OUTPut:MODulation:STATe OFF");

            Query(session, "*OPC?");
            ThrowOnInstrumentError(session, "stopping the noise band");

            Refresh();
        }

        /// <summary>Switches the comb off and leaves an unmodulated carrier.</summary>
        public void StopMultitone()
        {
            IInstrumentSession session = RequireSession();

            Send(session, ":RADio:MTONe:ARB:STATe OFF");
            Send(session, ":OUTPut:MODulation:STATe OFF");

            Query(session, "*OPC?");
            ThrowOnInstrumentError(session, "stopping the multitone comb");

            Refresh();
        }

        /// <inheritdoc />
        public void SetOutput(bool enabled)
        {
            IInstrumentSession session = RequireSession();

            Send(session, ":OUTPut:STATe " + (enabled ? "ON" : "OFF"));
            Query(session, "*OPC?");
            ThrowOnInstrumentError(session, enabled ? "enabling the output" : "disabling the output");

            Refresh();
        }

        /// <inheritdoc />
        public void Refresh()
        {
            IInstrumentSession session = RequireSession();

            FrequencyHz = QueryDouble(session, ":FREQuency:CW?");
            LevelDbm = QueryDouble(session, ":POWer:AMPLitude?");
            IsOutputEnabled = QueryDouble(session, ":OUTPut:STATe?") > 0.5;

            // Zero when the comb is off, so a scenario reading it back cannot mistake a stale tone
            // count for a live one.
            bool comb = QueryDouble(session, ":RADio:MTONe:ARB:STATe?") > 0.5;

            ToneCount = comb
                ? (int)Math.Round(QueryDouble(session, ":RADio:MTONe:ARB:SETup:TABLe:NTONes?"))
                : 0;

            ToneSpacingHz = comb
                ? QueryDouble(session, ":RADio:MTONe:ARB:SETup:TABLe:FSPacing?")
                : 0.0;

            // Zero when the noise is off, for the reason the tone count is: a stale bandwidth read
            // back as live would make a carrier scenario look like a noise one.
            NoiseBandwidthHz = QueryDouble(session, ":RADio:AWGN:ARB:STATe?") > 0.5
                ? QueryDouble(session, ":RADio:AWGN:ARB:BWIDth?")
                : 0.0;
            // Null and zero when the Custom personality is off, for the reason the tone count and
            // the noise bandwidth are: what a scenario checks its expectation against has to be what
            // the generator says it is producing now, not what it was last asked for.
            bool modulated = QueryDouble(session, ":RADio:CUSTom:STATe?") > 0.5;

            Format = modulated ? Query(session, ":RADio:CUSTom:MODulation:TYPE?").Trim() : null;
            SymbolRateHz = modulated ? QueryDouble(session, ":RADio:CUSTom:SRATe?") : 0.0;
            DataPattern = modulated ? Query(session, ":RADio:CUSTom:DATA?").Trim() : null;

            if (modulated)
            {
                PulseFilter = FilterFrom(Query(session, ":RADio:CUSTom:FILTer?"));

                // Not asked for at all on a Gaussian filter, which has no roll-off: the query
                // would be refused, and a refused query on this firmware does not answer, it
                // times out.
                Alpha = PulseFilter == StimulusPulseFilter.Gaussian
                    ? double.NaN
                    : QueryDouble(session, ":RADio:CUSTom:ALPHa?");

                IsSpectrumInverted = Query(session, ":RADio:CUSTom:POLarity:ALL?")
                    .Trim()
                    .ToUpperInvariant()
                    .StartsWith("INV", StringComparison.Ordinal);
            }
            else
            {
                Alpha = 0.0;
                IsSpectrumInverted = false;
            }

        }

        /// <summary>How long to allow a limit probe before giving up on it.</summary>
        /// <remarks>
        /// Short, and deliberately shorter than the session's own timeout. A query this firmware
        /// rejects does not answer at all — it times out — so the cost of asking for a limit that
        /// cannot be had is one timeout apiece, and four of them at the session default is a panel
        /// that appears to hang while it opens.
        /// </remarks>
        public const int LimitProbeTimeoutMilliseconds = 2000;

        /// <summary>
        /// Asks the instrument for its frequency and level range.
        /// </summary>
        /// <returns>The limits, with <c>NaN</c> for anything it would not answer for.</returns>
        /// <remarks>
        /// <para>
        /// <strong>Four queries, and the error queue drained behind all four.</strong> The lesson
        /// that made this method careful is recorded below beside the tone-count limits: a probe
        /// this firmware rejects times out with no reply <em>and leaves its error in the
        /// instrument's queue</em>, where the next unrelated operation picks it up and is blamed
        /// for it. Catching the timeout is not enough — the exception is this side of the wire and
        /// the queue is the other — so the queue is read to the end here whether anything failed or
        /// not.
        /// </para>
        /// <para>
        /// <strong>Nothing is substituted for a limit that does not answer.</strong> The tempting
        /// fallback is the data sheet, and it is wrong: this instrument's top frequency depends on
        /// which of Options 501 to 506 it carries, so a data-sheet number would be a confident
        /// statement about a different instrument. Unknown is reported as unknown and the panel
        /// says so.
        /// </para>
        /// </remarks>
        public StimulusLimits ReadLimits()
        {
            IInstrumentSession session = RequireSession();

            int wasTimeout = session.TimeoutMilliseconds;

            try
            {
                session.TimeoutMilliseconds =
                    Math.Min(wasTimeout, LimitProbeTimeoutMilliseconds);

                return new StimulusLimits(
                    Probe(session, ":FREQuency:CW? MIN"),
                    Probe(session, ":FREQuency:CW? MAX"),
                    Probe(session, ":POWer:AMPLitude? MIN"),
                    Probe(session, ":POWer:AMPLitude? MAX"));
            }
            finally
            {
                session.TimeoutMilliseconds = wasTimeout;

                // Behind all four, and outside the try that produced them: a probe that threw is
                // exactly the one whose error is still sitting in the queue.
                try
                {
                    ReadErrors(session);
                }
                catch (Exception)
                {
                    // An instrument that will not even report its errors has bigger problems than
                    // an unranged panel, and they will be reported by the next real operation.
                }
            }
        }


        /// <summary>
        /// Asks the instrument what symbol rates it will accept, rather than taking the manual's
        /// word for it.
        /// </summary>
        /// <param name="format">The format to ask about, since the ceiling depends on it.</param>
        /// <param name="filter">The filter to ask about, for the same reason.</param>
        /// <param name="minimumHz">What the instrument reports as its floor, or NaN if it will not say.</param>
        /// <param name="maximumHz">What it reports as its ceiling, or NaN if it will not say.</param>
        /// <remarks>
        /// <para>
        /// The same shape as <see cref="ReadLimits"/> and for the same reason: a query this firmware
        /// rejects does not answer at all, it times out, so the probe runs under a short timeout and
        /// answers NaN rather than failing. <c>:RADio:MTONe:ARB:SETup:TABLe:NTONes? MIN</c> is
        /// already recorded as refused on this firmware, so there is every reason to expect the same
        /// here and to find out rather than assume.
        /// </para>
        /// <para>
        /// The filter has to be set before asking, because the ceiling is a property of the pair.
        /// That makes this a probe with a side effect, which is why it is not part of
        /// <see cref="IDigitalModulationStimulus"/>: a scenario should not be able to change the
        /// signal by asking a question about it. It is here for the bench run that records what this
        /// instrument does.
        /// </para>
        /// </remarks>
        public void ProbeSymbolRateLimits(
            string format,
            StimulusPulseFilter filter,
            out double minimumHz,
            out double maximumHz)
        {
            IInstrumentSession session = RequireSession();

            RequireOffered(Formats, format, "format");

            int wasTimeout = session.TimeoutMilliseconds;

            try
            {
                Send(session, ":RADio:CUSTom:MODulation:TYPE " + format);
                Send(session, ":RADio:CUSTom:FILTer " + FilterWord(filter));

                session.TimeoutMilliseconds =
                    Math.Min(wasTimeout, LimitProbeTimeoutMilliseconds);

                minimumHz = Probe(session, ":RADio:CUSTom:SRATe? MIN");
                maximumHz = Probe(session, ":RADio:CUSTom:SRATe? MAX");
            }
            finally
            {
                session.TimeoutMilliseconds = wasTimeout;

                try
                {
                    ReadErrors(session);
                }
                catch (Exception)
                {
                    // As in ReadLimits: an instrument that will not report its errors has larger
                    // problems, and the next real operation will say so.
                }
            }
        }

        /// <summary>A limit query that answers with <c>NaN</c> rather than failing.</summary>
        private double Probe(IInstrumentSession session, string query)
        {
            try
            {
                return QueryDouble(session, query);
            }
            catch (Exception)
            {
                // Rejected, or unanswered until the timeout. Either way this instrument does not
                // report that limit, which is a fact about it and not a fault in the harness.
                return double.NaN;
            }
        }

        // The tone-count limits are the manual's 2-64 for this model, and are NOT probed.
        //
        // ":RADio:MTONe:ARB:SETup:TABLe:NTONes? MIN" is rejected by firmware C.05.85 with
        // -108 "Parameter not allowed" -- and it does not merely fail. It TIMES OUT with no
        // reply and leaves the error sitting in the instrument's queue, where the next
        // ThrowOnInstrumentError picks it up and attributes it to whatever ran next: the first
        // run of this reported "-108 Parameter not allowed while setting the carrier" on a
        // scenario that had done nothing wrong.
        //
        // Swallowing the exception did not help, because the exception is this side of the wire
        // and the error queue is the other. That is the lesson worth keeping: a tolerated probe
        // must clear the queue behind it, or it poisons a later, unrelated check.

        /// <summary>Turns the output off and closes the session.</summary>
        /// <remarks>
        /// The output is turned off on the way out. Leaving a generator radiating because a test
        /// run ended is not something to do on somebody else's bench.
        /// </remarks>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            IInstrumentSession session = _session;
            _session = null;

            if (session == null)
            {
                return;
            }

            try
            {
                session.Write(":OUTPut:STATe OFF");
            }
            catch (Exception)
            {
                // Closing must not fail because the instrument has already gone.
            }

            session.Dispose();
        }

        private IInstrumentSession RequireSession()
        {
            ThrowIfDisposed();

            IInstrumentSession session = _session;

            if (session == null)
            {
                throw new InvalidOperationException("Connect to the stimulus source first.");
            }

            return session;
        }

        private void Send(IInstrumentSession session, string command)
        {
            Record(command);
            session.Write(command);
        }

        private string Query(IInstrumentSession session, string command)
        {
            Record(command);
            return session.Query(command);
        }

        private double QueryDouble(IInstrumentSession session, string command)
        {
            string reply = Query(session, command);
            double value;

            return double.TryParse(reply, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
                ? value
                : double.NaN;
        }

        /// <summary>
        /// Reads the instrument's error queue to the end, and throws if anything was in it.
        /// </summary>
        /// <remarks>
        /// <strong>To the end, not one entry.</strong> The queue is a queue: reading a single entry
        /// leaves any others behind for the next command to trip over, and the report then names
        /// the wrong operation. That is not hypothetical here — a rejected capability probe left
        /// <c>-108</c> queued and the next scenario reported it as a failure to set the carrier.
        /// The first error is the one raised, because it is the one with a cause; the rest are
        /// listed after it.
        /// </remarks>
        private void ThrowOnInstrumentError(IInstrumentSession session, string what)
        {
            List<string> errors = ReadErrors(session);

            if (errors.Count == 0)
            {
                return;
            }

            throw new InvalidOperationException(
                "The stimulus source reported an error while " + what + ": " +
                string.Join("; ", errors.ToArray()));
        }

        /// <summary>Reads the instrument's error queue to the end and returns what was in it.</summary>
        /// <remarks>
        /// Separated from <see cref="ThrowOnInstrumentError"/> so that a tolerated probe can drain
        /// the queue without raising: draining and raising are two decisions, and the caller that
        /// asked a question it was willing to be refused makes the second one differently.
        /// </remarks>
        private List<string> ReadErrors(IInstrumentSession session)
        {
            var errors = new List<string>();

            // Bounded: an instrument that answers every read with an error would otherwise spin
            // here for ever, which is a worse failure than the one being reported.
            for (int read = 0; read < 16; read++)
            {
                string reply = Query(session, ":SYSTem:ERRor?");

                if (string.IsNullOrEmpty(reply) ||
                    reply.StartsWith("0,", StringComparison.Ordinal) ||
                    reply.StartsWith("+0,", StringComparison.Ordinal))
                {
                    break;
                }

                errors.Add(reply.Trim());
            }

            return errors;
        }

        private void Record(string command)
        {
            lock (_sent)
            {
                _sent.Add(command);
            }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(E4438CStimulus));
            }
        }

        private static string Number(double value) =>
            value.ToString("R", CultureInfo.InvariantCulture);
    }
}
