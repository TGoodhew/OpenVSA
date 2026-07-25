using System;

namespace OpenVSA.Hal.Visa
{
    /// <summary>
    /// A live message-based connection to one instrument.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An interface rather than the VISA session itself, for one reason that matters: an
    /// instrument driver written against this can be tested against a scripted fake, with no
    /// hardware and no VISA runtime. Everything the E4406A driver does — the command order, the
    /// block parsing, the error checking, the coercion reporting — is then covered in CI, and what
    /// is left untested against real hardware is only the transport.
    /// </para>
    /// <para>
    /// Not thread-safe, and not required to be. A driver serialises access to its own session.
    /// </para>
    /// </remarks>
    public interface IInstrumentSession : IDisposable
    {
        /// <summary>The VISA resource string this session was opened for.</summary>
        string ResourceName { get; }

        /// <summary>I/O timeout, in milliseconds.</summary>
        int TimeoutMilliseconds { get; set; }

        /// <summary>Writes a command, appending the message terminator.</summary>
        /// <param name="command">The command to send.</param>
        void Write(string command);

        /// <summary>Reads a response as text, to the terminator or EOI.</summary>
        /// <returns>The response, with trailing whitespace removed.</returns>
        string ReadString();

        /// <summary>
        /// Reads an IEEE 488.2 block, with termination-character detection disabled.
        /// </summary>
        /// <returns>The block's payload bytes, without its header.</returns>
        /// <remarks>
        /// <c>REQ-VISA-005</c>. Separate from <see cref="ReadString"/> because the two need
        /// opposite termination settings, and a binary payload read with termination enabled
        /// truncates at the first 0x0A byte that happens to fall inside a float.
        /// </remarks>
        byte[] ReadBinaryBlock();

        /// <summary>Writes a command and reads its response as text.</summary>
        /// <param name="command">The query to send.</param>
        string Query(string command);

        /// <summary>Sends the IEEE 488.2 device clear, abandoning any transfer in progress.</summary>
        void Clear();
    }
}
