using System;
using System.Collections.Generic;
using System.Threading;

namespace OpenVSA.Core
{
    /// <summary>
    /// The sample-buffer pool of <c>REQ-NFR-002</c>: power-of-two buckets with an explicit maximum
    /// array length, and no silent drops.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Why not <see cref="System.Buffers.ArrayPool{T}.Shared"/>.</strong> The requirement
    /// names this and it is not a stylistic preference. <c>ArrayPool&lt;T&gt;.Shared</c> has a
    /// <c>MaxBufferSize</c> of 2²⁰ <em>elements</em>. A 2²⁰-sample complex block is 2²¹ floats, so
    /// every acquisition at the top of the range was allocated fresh and then **silently dropped**
    /// on <c>Return</c> — the pool declined to keep exactly the buffers whose churn the requirement
    /// exists to prevent, and said nothing. Measured before this type existed: **30 gen-2
    /// collections in a 30-second run at 2²⁰ points**, against a requirement of none.
    /// </para>
    /// <para>
    /// <strong>Above the cap, buffers are refused audibly rather than dropped quietly.</strong>
    /// <see cref="MaximumLength"/> bounds what the pool will retain, because a pool with no ceiling
    /// is a memory leak with a friendly name. A request above it is served by a fresh allocation
    /// and <see cref="OversizeRequests"/> counts it, so "the pool is not helping" is a number
    /// somebody can read instead of a slow heap nobody can explain. That is the whole difference
    /// from the behaviour above: the same outcome, made observable.
    /// </para>
    /// <para>
    /// <strong>Retention is bounded per bucket.</strong> At 2²¹ floats a single buffer is 8 MiB, so
    /// an unbounded bucket of them is how a pool turns into the problem it was solving. Buffers
    /// beyond <see cref="MaximumPerBucket"/> are let go to the collector.
    /// </para>
    /// <para>
    /// <strong>Buffers are not cleared on return.</strong> A caller receives a buffer holding
    /// whatever the last one left, exactly as <c>ArrayPool</c> behaves, because clearing 8 MiB per
    /// acquisition is a pass over the data on the hot path. Every consumer here fills before it
    /// reads. <see cref="IqBlock"/>'s sample count, not the buffer length, is what bounds reads.
    /// </para>
    /// </remarks>
    public sealed class SampleBufferPool
    {
        /// <summary>The process-wide pool used by the acquisition and DSP paths.</summary>
        /// <remarks>
        /// One pool, because two pools of 8 MiB buffers keep two sets of them alive. Not named
        /// <c>Shared</c>, to avoid reading as <c>ArrayPool&lt;T&gt;.Shared</c> at a glance — the
        /// distinction between the two is the entire point of this type.
        /// </remarks>
        public static readonly SampleBufferPool Instance = new SampleBufferPool();

        /// <summary>Smallest bucket, in elements. Below this the allocation is not worth pooling.</summary>
        public const int MinimumLength = 1024;

        /// <summary>
        /// Largest array length the pool will retain, in elements: 2²⁴ floats, 64 MiB.
        /// </summary>
        /// <remarks>
        /// Eight times the largest block the analysis path produces (2²¹ floats for a 2²⁰-sample
        /// complex acquisition), so the sizes that matter are comfortably inside it, and finite so
        /// that a caller which asks for something absurd does not have it kept for ever.
        /// </remarks>
        public const int MaximumLength = 1 << 24;

        /// <summary>How many buffers a single bucket retains.</summary>
        /// <remarks>
        /// Four is enough for the pipeline's depth — an acquisition in flight, one being analysed,
        /// one published and one spare — and bounds the largest bucket at 256 MiB rather than at
        /// however many the machine can be persuaded to allocate.
        /// </remarks>
        public const int MaximumPerBucket = 4;

        private readonly Stack<float[]>[] _buckets;
        private readonly object[] _gates;

        private long _rents;
        private long _hits;
        private long _oversize;

        /// <summary>Creates an empty pool.</summary>
        public SampleBufferPool()
        {
            int buckets = BucketFor(MaximumLength) + 1;

            _buckets = new Stack<float[]>[buckets];
            _gates = new object[buckets];

            for (int i = 0; i < buckets; i++)
            {
                _buckets[i] = new Stack<float[]>();
                _gates[i] = new object();
            }
        }

        /// <summary>Total <see cref="Rent"/> calls.</summary>
        public long Rents => Interlocked.Read(ref _rents);

        /// <summary>Rents served from the pool rather than by allocating.</summary>
        /// <remarks>
        /// Exposed so a test can assert the pool is <em>reused</em>, not merely called. A pool test
        /// that rents and returns without checking this passes against an implementation that
        /// allocates every time.
        /// </remarks>
        public long Hits => Interlocked.Read(ref _hits);

        /// <summary>Rents above <see cref="MaximumLength"/>, which the pool cannot retain.</summary>
        public long OversizeRequests => Interlocked.Read(ref _oversize);

        /// <summary>
        /// Rents a buffer of at least <paramref name="minimumLength"/> elements.
        /// </summary>
        /// <param name="minimumLength">Elements required; must not be negative.</param>
        /// <returns>A buffer of at least that length, holding arbitrary data.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="minimumLength"/> is negative.</exception>
        public float[] Rent(int minimumLength)
        {
            if (minimumLength < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(minimumLength), minimumLength, "A buffer length cannot be negative.");
            }

            Interlocked.Increment(ref _rents);

            if (minimumLength > MaximumLength)
            {
                Interlocked.Increment(ref _oversize);
                return new float[minimumLength];
            }

            int bucket = BucketFor(minimumLength);
            int length = LengthOf(bucket);

            lock (_gates[bucket])
            {
                if (_buckets[bucket].Count > 0)
                {
                    Interlocked.Increment(ref _hits);
                    return _buckets[bucket].Pop();
                }
            }

            return new float[length];
        }

        /// <summary>
        /// Returns a buffer to the pool.
        /// </summary>
        /// <param name="buffer">The buffer; <c>null</c> is ignored.</param>
        /// <remarks>
        /// A buffer whose length is not one of the pool's own sizes is let go rather than kept: it
        /// came from an oversize rent or from somewhere else, and putting it in a bucket would hand
        /// the next caller an array shorter than the bucket promises.
        /// </remarks>
        public void Return(float[] buffer)
        {
            if (buffer == null || buffer.Length < MinimumLength || buffer.Length > MaximumLength)
            {
                return;
            }

            int bucket = BucketFor(buffer.Length);

            if (LengthOf(bucket) != buffer.Length)
            {
                return;
            }

            lock (_gates[bucket])
            {
                if (_buckets[bucket].Count < MaximumPerBucket)
                {
                    _buckets[bucket].Push(buffer);
                }
            }
        }

        /// <summary>Drops every retained buffer. For tests that measure from a known state.</summary>
        public void Clear()
        {
            for (int i = 0; i < _buckets.Length; i++)
            {
                lock (_gates[i])
                {
                    _buckets[i].Clear();
                }
            }

            Interlocked.Exchange(ref _rents, 0);
            Interlocked.Exchange(ref _hits, 0);
            Interlocked.Exchange(ref _oversize, 0);
        }

        /// <summary>The bucket index holding arrays of at least this length.</summary>
        private static int BucketFor(int length)
        {
            int size = MinimumLength;
            int index = 0;

            while (size < length)
            {
                size <<= 1;
                index++;
            }

            return index;
        }

        /// <summary>The array length a bucket holds.</summary>
        private static int LengthOf(int bucket) => MinimumLength << bucket;
    }
}
