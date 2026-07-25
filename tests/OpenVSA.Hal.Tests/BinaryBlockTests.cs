using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using OpenVSA.Hal.Visa;
using Xunit;

namespace OpenVSA.Hal.Tests
{
    /// <summary>
    /// <c>REQ-VISA-005</c>: IEEE 488.2 block parsing, including the payload-with-newlines case.
    /// </summary>
    public class BinaryBlockTests
    {
        [Fact]
        public void ADefiniteLengthBlockIsReadWhole()
        {
            byte[] payload = { 1, 2, 3, 4, 5 };
            Assert.Equal(payload, ReadBlock(Block(payload)));
        }

        [Fact]
        public void APayloadContainingNewlinesIsReadCompleteAndByteExact()
        {
            // The classic VISA binary-transfer defect: 0x0A inside a float truncates a read that
            // has termination-character detection enabled. It presents as an intermittent short
            // read that looks like an instrument fault, so it is asserted directly.
            var payload = new byte[256];

            for (int i = 0; i < payload.Length; i++)
            {
                payload[i] = (byte)(i % 256);
            }

            byte[] read = ReadBlock(Block(payload));

            Assert.Equal(payload.Length, read.Length);
            Assert.Equal(payload, read);
            Assert.Contains((byte)0x0A, read);
        }

        [Fact]
        public void AnIndefiniteLengthBlockIsReadToTheEnd()
        {
            // #0 says "the payload runs to EOI", so the only correct read is everything left.
            byte[] payload = { 9, 8, 7 };
            var response = new List<byte> { (byte)'#', (byte)'0' };
            response.AddRange(payload);

            Assert.Equal(payload, ReadBlock(response.ToArray()));
        }

        [Fact]
        public void ABlockDeliveredInChunksIsStillReadWhole()
        {
            // A single read returning everything is the assumption under which a large transfer
            // quietly truncates, so the reader is fed three bytes at a time.
            byte[] payload = new byte[1000];
            new Random(1).NextBytes(payload);

            byte[] encoded = Block(payload);
            int position = 0;

            byte[] read = BinaryBlock.Read(
                count =>
                {
                    int take = Math.Min(Math.Min(count, 3), encoded.Length - position);
                    var chunk = new byte[take];
                    Buffer.BlockCopy(encoded, position, chunk, 0, take);
                    position += take;
                    return chunk;
                },
                () => new byte[0]);

            Assert.Equal(payload, read);
        }

        [Fact]
        public void AResponseThatIsNotABlockSaysSo()
        {
            // An instrument that rejects the query answers with text. Reporting that as a parse
            // failure beats returning nonsense that will be plotted.
            IOException failure = Assert.Throws<IOException>(
                () => ReadBlock(Encoding.ASCII.GetBytes("-113,\"Undefined header\"")));

            Assert.Contains("'#'", failure.Message);
        }

        [Fact]
        public void ABlockThatEndsEarlySaysSoRatherThanReturningShortData()
        {
            var truncated = new List<byte>(Encoding.ASCII.GetBytes("#3010"));
            truncated.AddRange(new byte[4]);

            Assert.Throws<IOException>(() => ReadBlock(truncated.ToArray()));
        }

        [Fact]
        public void ALengthBeyondTheNineDigitCeilingIsRefused()
        {
            Assert.Throws<IOException>(
                () => ReadBlock(Encoding.ASCII.GetBytes("#99999999999")));
        }

        [Fact]
        public void FloatsAreReadLittleEndian()
        {
            // The instrument is told :FORMat:BORDer SWAP because VISA's NORMal order is big-endian
            // and this machine is not.
            float[] expected = { 1.0f, -0.5f, 3.25e-3f };
            var payload = new byte[expected.Length * 4];

            for (int i = 0; i < expected.Length; i++)
            {
                byte[] bytes = BitConverter.GetBytes(expected[i]);
                Buffer.BlockCopy(bytes, 0, payload, i * 4, 4);
            }

            var destination = new float[expected.Length];
            int count = BinaryBlock.ToSingles(payload, destination);

            Assert.Equal(expected.Length, count);
            Assert.Equal(expected, destination);
        }

        [Fact]
        public void APayloadThatIsNotAWholeNumberOfFloatsIsRefused()
        {
            Assert.Throws<IOException>(() => BinaryBlock.ToSingles(new byte[6], new float[2]));
        }

        [Fact]
        public void ADestinationTooSmallIsRefused()
        {
            Assert.Throws<ArgumentException>(() => BinaryBlock.ToSingles(new byte[8], new float[1]));
        }

        /// <summary>Wraps a payload in a definite-length header.</summary>
        private static byte[] Block(byte[] payload)
        {
            string length = payload.Length.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var header = new List<byte>();
            header.Add((byte)'#');
            header.AddRange(Encoding.ASCII.GetBytes(length.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            header.AddRange(Encoding.ASCII.GetBytes(length));
            header.AddRange(payload);

            return header.ToArray();
        }

        private static byte[] ReadBlock(byte[] response)
        {
            int position = 0;

            return BinaryBlock.Read(
                count =>
                {
                    int take = Math.Min(count, response.Length - position);
                    var chunk = new byte[take];
                    Buffer.BlockCopy(response, position, chunk, 0, take);
                    position += take;
                    return chunk;
                },
                () =>
                {
                    var rest = new byte[response.Length - position];
                    Buffer.BlockCopy(response, position, rest, 0, rest.Length);
                    position = response.Length;
                    return rest;
                });
        }
    }
}
