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
    public sealed class E4438CStimulus : IStimulusSource
    {
        /// <summary>Resource used when configuration names none.</summary>
        public const string DefaultResource = "TCPIP0::192.168.1.82::inst1::INSTR";

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
        }

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

        private void ThrowOnInstrumentError(IInstrumentSession session, string what)
        {
            string reply = Query(session, ":SYSTem:ERRor?");

            if (string.IsNullOrEmpty(reply) ||
                reply.StartsWith("0,", StringComparison.Ordinal) ||
                reply.StartsWith("+0,", StringComparison.Ordinal))
            {
                return;
            }

            throw new InvalidOperationException(
                "The stimulus source reported an error while " + what + ": " + reply);
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
