using System;
using System.Globalization;
using System.IO;

namespace OpenVSA.Hal.Visa
{
    /// <summary>
    /// Parses IEEE 488.2 arbitrary block responses (<c>REQ-VISA-005</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The format is <c>#</c>, one digit giving the length of the length, that many digits of
    /// byte count, then the payload. <c>#0</c> is the indefinite form: the payload runs to EOI.
    /// </para>
    /// <para>
    /// Separated from the session so it can be tested byte by byte — including the case the
    /// requirement calls out, a payload with 0x0A bytes inside it, which is the classic VISA
    /// binary-transfer defect and presents as intermittent short reads that look like instrument
    /// faults.
    /// </para>
    /// </remarks>
    public static class BinaryBlock
    {
        /// <summary>Largest definite-length block the standard's 9-digit count can express.</summary>
        public const long MaximumDefiniteLength = 999999999L;

        /// <summary>
        /// Reads a block using a byte-reading delegate.
        /// </summary>
        /// <param name="read">Reads exactly the requested number of bytes, or fewer at end of data.</param>
        /// <param name="readToEnd">Reads the remainder of the response, for the indefinite form.</param>
        /// <returns>The payload bytes.</returns>
        /// <exception cref="ArgumentNullException">An argument is null.</exception>
        /// <exception cref="IOException">The response is not a well-formed block, or ends early.</exception>
        public static byte[] Read(Func<int, byte[]> read, Func<byte[]> readToEnd)
        {
            if (read == null)
            {
                throw new ArgumentNullException(nameof(read));
            }

            if (readToEnd == null)
            {
                throw new ArgumentNullException(nameof(readToEnd));
            }

            byte[] hash = ReadExactly(read, 1);

            if (hash.Length != 1 || hash[0] != (byte)'#')
            {
                throw new IOException(
                    "Expected an IEEE 488.2 block beginning with '#', got " +
                    (hash.Length == 0 ? "nothing" : "0x" + hash[0].ToString("X2", CultureInfo.InvariantCulture)) +
                    ". The instrument may have returned an error string instead of data.");
            }

            byte[] digitCount = ReadExactly(read, 1);

            if (digitCount.Length != 1 || digitCount[0] < (byte)'0' || digitCount[0] > (byte)'9')
            {
                throw new IOException("Malformed block header: the character after '#' is not a digit.");
            }

            int digits = digitCount[0] - (byte)'0';

            if (digits == 0)
            {
                // Indefinite length: the payload runs to EOI. Nothing states how long it is, so the
                // only correct read is "everything that is left".
                return readToEnd();
            }

            byte[] lengthText = ReadExactly(read, digits);

            if (lengthText.Length != digits)
            {
                throw new IOException("Block header ended before its length field was complete.");
            }

            long length = 0;

            for (int i = 0; i < digits; i++)
            {
                if (lengthText[i] < (byte)'0' || lengthText[i] > (byte)'9')
                {
                    throw new IOException("Block length field contains a character that is not a digit.");
                }

                length = length * 10 + (lengthText[i] - (byte)'0');
            }

            if (length > MaximumDefiniteLength)
            {
                throw new IOException(
                    "Block claims " + length.ToString(CultureInfo.InvariantCulture) +
                    " bytes, beyond the 9-digit definite-length ceiling.");
            }

            var payload = new byte[length];
            long taken = 0;

            // Chunked, because a single read of a large transfer is where an implementation that
            // assumes one read returns everything quietly truncates.
            while (taken < length)
            {
                int wanted = (int)Math.Min(int.MaxValue, length - taken);
                byte[] chunk = read(wanted);

                if (chunk.Length == 0)
                {
                    throw new IOException(
                        "Block ended after " + taken.ToString(CultureInfo.InvariantCulture) +
                        " of " + length.ToString(CultureInfo.InvariantCulture) + " bytes.");
                }

                Buffer.BlockCopy(chunk, 0, payload, (int)taken, chunk.Length);
                taken += chunk.Length;
            }

            return payload;
        }

        /// <summary>
        /// Reads exactly <paramref name="count"/> bytes, or as many as remain.
        /// </summary>
        /// <remarks>
        /// The header fields need this as much as the payload does. A transport that returns short
        /// reads — every one of them does, under the right timing — would otherwise fail on the
        /// two-digit length of a large block while reading the payload perfectly.
        /// </remarks>
        private static byte[] ReadExactly(Func<int, byte[]> read, int count)
        {
            var buffer = new byte[count];
            int taken = 0;

            while (taken < count)
            {
                byte[] chunk = read(count - taken);

                if (chunk == null || chunk.Length == 0)
                {
                    var partial = new byte[taken];
                    Buffer.BlockCopy(buffer, 0, partial, 0, taken);
                    return partial;
                }

                Buffer.BlockCopy(chunk, 0, buffer, taken, chunk.Length);
                taken += chunk.Length;
            }

            return buffer;
        }

        /// <summary>
        /// Reinterprets a payload of little-endian 32-bit floats.
        /// </summary>
        /// <param name="payload">The block payload.</param>
        /// <param name="destination">Receives the values; must be at least <c>payload.Length / 4</c> long.</param>
        /// <returns>The number of values written.</returns>
        /// <exception cref="ArgumentNullException">An argument is null.</exception>
        /// <exception cref="IOException">The payload is not a whole number of floats.</exception>
        /// <exception cref="ArgumentException"><paramref name="destination"/> is too small.</exception>
        /// <remarks>
        /// Little-endian because the instrument is told <c>:FORMat:BORDer SWAP</c>: VISA's NORMal
        /// order is big-endian, and every machine this runs on is not.
        /// </remarks>
        public static int ToSingles(byte[] payload, float[] destination)
        {
            if (payload == null)
            {
                throw new ArgumentNullException(nameof(payload));
            }

            if (destination == null)
            {
                throw new ArgumentNullException(nameof(destination));
            }

            if (payload.Length % 4 != 0)
            {
                throw new IOException(
                    "A REAL,32 payload must be a whole number of 4-byte floats; got " +
                    payload.Length.ToString(CultureInfo.InvariantCulture) + " bytes.");
            }

            int count = payload.Length / 4;

            if (destination.Length < count)
            {
                throw new ArgumentException(
                    "Destination holds " + destination.Length + " values but the payload carries " +
                    count + ".",
                    nameof(destination));
            }

            if (!BitConverter.IsLittleEndian)
            {
                for (int i = 0; i < count; i++)
                {
                    Array.Reverse(payload, i * 4, 4);
                }
            }

            Buffer.BlockCopy(payload, 0, destination, 0, count * 4);
            return count;
        }
    }
}
