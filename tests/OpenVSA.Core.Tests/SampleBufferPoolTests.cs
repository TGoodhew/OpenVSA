using System;
using OpenVSA.Core;
using Xunit;

namespace OpenVSA.Core.Tests
{
    /// <summary>
    /// <c>REQ-NFR-002</c>: the sample-buffer pool retains what <c>ArrayPool&lt;T&gt;.Shared</c>
    /// silently dropped.
    /// </summary>
    public class SampleBufferPoolTests
    {
        /// <summary>A 2²⁰-sample complex block, which is the size the requirement is about.</summary>
        private const int LargestRealBlock = 2 << 20;

        [Fact]
        public void AReturnedBufferComesBackToTheNextCaller()
        {
            var pool = new SampleBufferPool();

            float[] first = pool.Rent(4096);
            pool.Return(first);

            float[] second = pool.Rent(4096);

            // Assert.Same, not "a buffer of the right length". A pool that allocated every time
            // would satisfy every weaker check here, which is the whole failure this test exists
            // to catch -- and is exactly what ArrayPool<T>.Shared was doing above its cap.
            Assert.Same(first, second);
            Assert.Equal(1L, pool.Hits);
        }

        [Fact]
        public void TheBufferSizeThatDefeatedTheSharedPoolIsRetained()
        {
            var pool = new SampleBufferPool();

            // 2^21 floats. ArrayPool<float>.Shared has a MaxBufferSize of 2^20 ELEMENTS, so this
            // was allocated fresh and dropped on return -- the pool declining to keep precisely the
            // buffers whose churn REQ-NFR-002 exists to prevent, and saying nothing about it.
            float[] first = pool.Rent(LargestRealBlock);
            Assert.True(first.Length >= LargestRealBlock);

            pool.Return(first);

            Assert.Same(first, pool.Rent(LargestRealBlock));
            Assert.Equal(0L, pool.OversizeRequests);
        }

        [Fact]
        public void ARequestAboveTheCapIsServedAndCounted()
        {
            var pool = new SampleBufferPool();

            float[] huge = pool.Rent(SampleBufferPool.MaximumLength + 1);

            // Served, so no caller is broken by asking for too much...
            Assert.Equal(SampleBufferPool.MaximumLength + 1, huge.Length);

            // ...but counted, so "the pool is not helping" is a number somebody can read rather
            // than a slow heap nobody can explain. That is the whole difference from the
            // behaviour this type replaces.
            Assert.Equal(1L, pool.OversizeRequests);

            pool.Return(huge);
            Assert.NotSame(huge, pool.Rent(SampleBufferPool.MaximumLength + 1));
        }

        [Fact]
        public void RetentionIsBounded()
        {
            var pool = new SampleBufferPool();

            var buffers = new float[SampleBufferPool.MaximumPerBucket + 3][];

            for (int i = 0; i < buffers.Length; i++)
            {
                buffers[i] = pool.Rent(4096);
            }

            foreach (float[] buffer in buffers)
            {
                pool.Return(buffer);
            }

            // At 2^21 floats a single buffer is 8 MiB, so an unbounded bucket of them is how a pool
            // becomes the problem it was solving. Only the cap is kept.
            int fromPool = 0;

            for (int i = 0; i < buffers.Length; i++)
            {
                if (Array.IndexOf(buffers, pool.Rent(4096)) >= 0)
                {
                    fromPool++;
                }
            }

            Assert.Equal(SampleBufferPool.MaximumPerBucket, fromPool);
        }

        [Fact]
        public void WhatThePoolIsHoldingCanBeCounted()
        {
            // REQ-TST-009 asks a soak to show that pooled buffers have no net growth, and Rents and
            // Hits cannot answer that: both rise for ever in a healthy run. What is bounded is what
            // the pool RETAINS, so that is what these report.
            var pool = new SampleBufferPool();

            Assert.Equal(0, pool.RetainedBuffers);
            Assert.Equal(0L, pool.RetainedBytes);

            float[] small = pool.Rent(1024);
            float[] large = pool.Rent(65536);

            // Rented, not returned: the pool is holding nothing while the caller has them.
            Assert.Equal(0, pool.RetainedBuffers);

            pool.Return(small);
            pool.Return(large);

            Assert.Equal(2, pool.RetainedBuffers);

            // Counted from each bucket's own length, so the geometric sizes are reflected rather
            // than a buffer being a buffer: one 64Ki-element array is 64 times a 1Ki one.
            Assert.Equal(
                (long)(small.Length + large.Length) * sizeof(float), pool.RetainedBytes);

            // And handing one back out reduces it again, or a soak would read every rent as growth.
            pool.Rent(65536);

            Assert.Equal(1, pool.RetainedBuffers);
            Assert.Equal((long)small.Length * sizeof(float), pool.RetainedBytes);

            pool.Clear();

            Assert.Equal(0, pool.RetainedBuffers);
            Assert.Equal(0L, pool.RetainedBytes);
        }

        [Fact]
        public void ABufferOfAForeignLengthIsNotPooled()
        {
            var pool = new SampleBufferPool();

            // Not one of the pool's own sizes. Accepting it would hand the next caller an array
            // shorter than its bucket promises, which is a buffer overrun waiting to be blamed on
            // the DSP.
            var foreign = new float[4097];
            pool.Return(foreign);

            Assert.NotSame(foreign, pool.Rent(4097));
        }

        [Fact]
        public void ARentedBufferIsAtLeastAsLongAsAsked()
        {
            var pool = new SampleBufferPool();

            foreach (int wanted in new[] { 0, 1, 1023, 1024, 1025, 65536, LargestRealBlock })
            {
                Assert.True(
                    pool.Rent(wanted).Length >= wanted,
                    "asked for " + wanted + " and got less");
            }
        }

        [Fact]
        public void ANegativeLengthIsRefused() =>
            Assert.Throws<ArgumentOutOfRangeException>(() => new SampleBufferPool().Rent(-1));

        [Fact]
        public void AnIqBlockReusesItsBufferAcrossTheLargestRealAcquisition()
        {
            // The end-to-end version of the second test, through the type that actually rents:
            // IqBlock at the block size REQ-NFR-001 allows. Before SampleBufferPool this allocated
            // 8 MiB on the large object heap per acquisition and dropped it.
            SampleBufferPool.Instance.Clear();

            var metadata = new IqBlockMetadata(
                1 << 20, 2.0e6, 1.0e9, false, 1.0, 0.0, 1L,
                new DateTime(2026, 7, 29, 0, 0, 0, DateTimeKind.Utc), 0.0, false,
                new FrontEndId("test"), null);

            float[] first;

            using (IqBlock block = IqBlock.Rent(metadata))
            {
                first = ArrayBehind(block);
            }

            using (IqBlock second = IqBlock.Rent(metadata))
            {
                Assert.Same(first, ArrayBehind(second));
            }

            Assert.True(SampleBufferPool.Instance.Hits >= 1);
        }

        /// <summary>The array a block is using, identified without exposing it on the type.</summary>
        /// <remarks>
        /// Reflection rather than a test-only accessor, because widening <see cref="IqBlock"/>'s
        /// surface so a test can check pooling would let production code reach the buffer past the
        /// span that bounds it.
        /// </remarks>
        private static float[] ArrayBehind(IqBlock block) =>
            (float[])typeof(IqBlock)
                .GetField("_buffer", System.Reflection.BindingFlags.Instance |
                                     System.Reflection.BindingFlags.NonPublic)
                .GetValue(block);
    }
}
