using System;
using System.Collections.Generic;
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
    public sealed class E4438CStimulus : IStimulusSource, IMultitoneStimulus
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

            if (errors.Count == 0)
            {
                return;
            }

            throw new InvalidOperationException(
                "The stimulus source reported an error while " + what + ": " +
                string.Join("; ", errors.ToArray()));
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
