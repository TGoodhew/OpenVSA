using System;
using System.Text;
using Ivi.Visa;

namespace OpenVSA.Hal.Visa
{
    /// <summary>
    /// A message-based VISA session, opened through the IVI Foundation shared components.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>REQ-VISA-001</c>: this type references <c>Ivi.Visa</c> alone and opens every session
    /// through <see cref="GlobalResourceManager"/>, letting the shared components dispatch to
    /// whichever vendor provider is registered. Referencing <c>NationalInstruments.Visa</c> or
    /// <c>Keysight.Visa</c> would hard-bind the binary to one vendor and is prohibited; so is the
    /// VISA-COM interop layer, which adds marshalling cost and an apartment-threading hazard in a
    /// host as threaded as this one.
    /// </para>
    /// <para>
    /// <strong>This is the only type here that needs VISA to be installed.</strong> On a machine
    /// without it the assembly loads and this type fails when it is first used, which is what
    /// <c>FrontEndRegistry</c> reports as an unavailable source — the application still starts,
    /// per <c>REQ-NFR-032</c>.
    /// </para>
    /// </remarks>
    public sealed class VisaSession : IInstrumentSession
    {
        private readonly IMessageBasedSession _session;
        private bool _disposed;

        private VisaSession(IMessageBasedSession session, string resourceName)
        {
            _session = session;
            ResourceName = resourceName;
        }

        /// <summary>
        /// Opens a session to a resource.
        /// </summary>
        /// <param name="resourceName">VISA resource string, such as <c>GPIB0::18::INSTR</c>.</param>
        /// <param name="timeoutMilliseconds">I/O timeout.</param>
        /// <returns>The open session.</returns>
        /// <exception cref="ArgumentException"><paramref name="resourceName"/> is missing.</exception>
        /// <exception cref="InvalidOperationException">The resource is not message-based, or could not be opened.</exception>
        public static VisaSession Open(string resourceName, int timeoutMilliseconds = 10000)
        {
            if (string.IsNullOrEmpty(resourceName))
            {
                throw new ArgumentException("A VISA resource name is required.", nameof(resourceName));
            }

            IVisaSession opened;

            try
            {
                opened = GlobalResourceManager.Open(resourceName, AccessModes.None, timeoutMilliseconds);
            }
            catch (Exception e)
            {
                // Wrapped rather than propagated raw: the caller is the front-end registry, which
                // shows the reason beside the source it could not offer.
                throw new InvalidOperationException(
                    "Could not open VISA resource '" + resourceName + "': " + e.Message, e);
            }

            var message = opened as IMessageBasedSession;

            if (message == null)
            {
                opened.Dispose();
                throw new InvalidOperationException(
                    "VISA resource '" + resourceName + "' is not message-based, so no instrument " +
                    "commands can be sent to it.");
            }

            message.TimeoutMilliseconds = timeoutMilliseconds;

            return new VisaSession(message, resourceName);
        }

        /// <summary>Every resource the registered VISA providers report.</summary>
        /// <returns>Resource strings.</returns>
        /// <remarks>
        /// Offered for diagnostics, and deliberately not used to choose an instrument. On a bench
        /// with HP-IB extenders every address answers the scan whether anything is there or not, so
        /// the resource to open is taken from configuration.
        /// </remarks>
        public static System.Collections.Generic.IEnumerable<string> Find()
        {
            try
            {
                return GlobalResourceManager.Find("?*INSTR");
            }
            catch (Exception)
            {
                return new string[0];
            }
        }

        /// <inheritdoc />
        public string ResourceName { get; }

        /// <inheritdoc />
        public int TimeoutMilliseconds
        {
            get { return _session.TimeoutMilliseconds; }
            set { _session.TimeoutMilliseconds = value; }
        }

        /// <inheritdoc />
        public void Write(string command)
        {
            ThrowIfDisposed();
            _session.RawIO.Write(command + "\n");
        }

        /// <inheritdoc />
        public string ReadString()
        {
            ThrowIfDisposed();
            _session.TerminationCharacterEnabled = true;
            return _session.RawIO.ReadString().TrimEnd('\r', '\n', ' ', '\0');
        }

        /// <inheritdoc />
        public string Query(string command)
        {
            Write(command);
            return ReadString();
        }

        /// <inheritdoc />
        public byte[] ReadBinaryBlock()
        {
            ThrowIfDisposed();

            // REQ-VISA-005: termination-character detection off for the whole block, or a 0x0A
            // byte inside a float truncates the read.
            bool previous = _session.TerminationCharacterEnabled;
            _session.TerminationCharacterEnabled = false;

            try
            {
                byte[] payload = BinaryBlock.Read(
                    count => _session.RawIO.Read(count),
                    () => _session.RawIO.Read());

                // The instrument sends a terminator after the block. It has to be consumed or the
                // next response begins with somebody else's newline.
                TryConsumeTerminator();

                return payload;
            }
            finally
            {
                _session.TerminationCharacterEnabled = previous;
            }
        }

        /// <inheritdoc />
        public void Clear()
        {
            ThrowIfDisposed();
            _session.Clear();
        }

        /// <summary>Closes the session.</summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _session.Dispose();
        }

        private void TryConsumeTerminator()
        {
            int timeout = _session.TimeoutMilliseconds;

            try
            {
                // Briefly, because a well-behaved instrument has already sent it and a badly
                // behaved one must not stall the acquisition loop for the full I/O timeout.
                _session.TimeoutMilliseconds = 200;
                _session.RawIO.Read(1);
            }
            catch (Exception)
            {
                // Nothing there, which is legitimate: the block may have ended with EOI alone.
            }
            finally
            {
                _session.TimeoutMilliseconds = timeout;
            }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(VisaSession));
            }
        }

        /// <summary>The text encoding instrument responses are decoded with: one byte, one char.</summary>
        internal static readonly Encoding Latin1 = Encoding.GetEncoding(28591);
    }
}
