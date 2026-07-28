using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using OpenVSA.Core;
using OpenVSA.Dsp.Spectrum;
using OpenVSA.Dsp.Windowing;
using Xunit;
using Xunit.Abstractions;

namespace OpenVSA.Dsp.Tests
{
    /// <summary>
    /// <c>REQ-NFR-037</c>: 100 consecutive runs over a fixed recording produce byte-identical
    /// result buffers on the same machine, at every supported degree of parallelism.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Byte-identical, not "equal to a tolerance".</strong> That is the whole requirement:
    /// a result that varies in the last bits between runs cannot be used as a regression baseline,
    /// and a comparison against a stored expected output becomes a judgement call about how much
    /// drift is acceptable. Comparing bits removes the judgement.
    /// </para>
    /// <para>
    /// The parallelism clause is the one with teeth. Floating-point addition is not associative, so
    /// a reduction that splits its work by thread count produces different bits at different
    /// degrees of parallelism — correct to a tolerance, and not reproducible. This runs the same
    /// input at one, two and many threads and requires the same bytes from all of them.
    /// </para>
    /// </remarks>
    public class ReproducibleResultsTests
    {
        private readonly ITestOutputHelper _output;

        /// <summary>Takes xunit's output sink.</summary>
        /// <param name="output">Where the run count and hash are written.</param>
        public ReproducibleResultsTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void OneHundredConsecutiveRunsProduceIdenticalBytes()
        {
            byte[] first = null;

            for (int run = 0; run < 100; run++)
            {
                byte[] bytes = Bytes(Compute());

                if (first == null)
                {
                    first = bytes;
                    continue;
                }

                // Not Assert.Equal on the arrays: that reports "collections differ" and nothing
                // about where, and 100 failures would each print a megabyte.
                int difference = FirstDifference(first, bytes);

                Assert.True(
                    difference < 0,
                    "Run " + run + " differs from run 0 at byte " + difference +
                    ". REQ-NFR-037 requires byte-identical results, not results equal to a tolerance.");
            }

            _output.WriteLine("100 runs, " + first.Length + " bytes each, all identical");
        }

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(4)]
        [InlineData(16)]
        public void TheSameBytesComeBackAtEveryDegreeOfParallelism(int threads)
        {
            // Floating-point addition is not associative, so a reduction that splits its work by
            // thread count gives different bits at different thread counts — correct to a
            // tolerance, and not reproducible. Running concurrently proves the computation carries
            // no shared mutable state and no work-splitting that depends on how many threads are
            // available.
            byte[] reference = Bytes(Compute());

            var results = new byte[threads][];

            Parallel.For(0, threads, new ParallelOptions { MaxDegreeOfParallelism = threads },
                i => results[i] = Bytes(Compute()));

            for (int i = 0; i < threads; i++)
            {
                int difference = FirstDifference(reference, results[i]);

                Assert.True(
                    difference < 0,
                    "At " + threads + " threads, result " + i + " differs at byte " + difference + ".");
            }

            _output.WriteLine(threads + " threads: identical");
        }

        [Fact]
        public void TheComparisonWouldNoticeASingleChangedBit()
        {
            // A byte comparison that could not fail proves nothing. One bit of one sample, which is
            // far below any tolerance a comparison of levels would use.
            byte[] a = Bytes(Compute());
            byte[] b = Bytes(Compute());

            Assert.True(FirstDifference(a, b) < 0);

            b[b.Length / 2] ^= 0x01;

            Assert.True(FirstDifference(a, b) >= 0);
        }

        /// <summary>The fixed input, computed through the product's own path.</summary>
        /// <remarks>
        /// The block is rebuilt each run from the same constants rather than shared, so a run
        /// cannot be reproducible merely because it read a buffer the previous run left behind.
        /// </remarks>
        private static SpectrumFrame Compute()
        {
            const int Points = 8192;

            var computer = new SpectrumComputer(WindowType.FlatTop, null, null);

            var metadata = new IqBlockMetadata(
                Points, 2.0e6, 1.0e9, false, 1.0, 0.0, 1L,
                new DateTime(2026, 7, 28, 0, 0, 0, DateTimeKind.Utc), 0.0, false,
                new FrontEndId("fixed"), null);

            IqBlock block = IqBlock.Rent(metadata);
            Span<float> samples = block.GetSamples();

            for (int n = 0; n < Points; n++)
            {
                double angle = 2.0 * Math.PI * 0.125 * n;

                // A carrier plus a deterministic pseudo-noise floor, so the result exercises the
                // whole dynamic range rather than one large bin and a lot of zeros.
                samples[n * 2] = (float)(0.5 * Math.Cos(angle) + 0.001 * Math.Cos(0.37 * n));
                samples[n * 2 + 1] = (float)(0.5 * Math.Sin(angle) + 0.001 * Math.Sin(0.11 * n));
            }

            return computer.Compute(block);
        }

        private static byte[] Bytes(SpectrumFrame frame)
        {
            ReadOnlySpan<float> levels = frame.LevelsDbm;
            var bytes = new byte[levels.Length * 4];

            for (int i = 0; i < levels.Length; i++)
            {
                // The bits, not the value: BitConverter on the float preserves the distinction
                // between two levels that print the same and are not the same.
                Buffer.BlockCopy(BitConverter.GetBytes(levels[i]), 0, bytes, i * 4, 4);
            }

            return bytes;
        }

        /// <summary>The index of the first differing byte, or -1 when identical.</summary>
        private static int FirstDifference(byte[] a, byte[] b)
        {
            if (a.Length != b.Length)
            {
                return Math.Min(a.Length, b.Length);
            }

            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i])
                {
                    return i;
                }
            }

            return -1;
        }
    }
}
