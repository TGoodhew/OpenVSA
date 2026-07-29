using System;
using System.Threading;

namespace OpenVSA.Core
{
    /// <summary>
    /// A reference-counted loan of a pooled buffer, which fails loudly rather than quietly when it
    /// outlives its lease (<c>REQ-NFR-002</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The problem this exists to solve.</strong> Pooling the per-frame spectrum buffer
    /// means a buffer can be handed back and reissued while something still holds a reference to
    /// it. Done with bare reference counting, a consumer that forgets to <see cref="Retain"/>
    /// keeps reading an array that now belongs to a later frame — and gets a *plausible spectrum
    /// that is not the one it asked for*. Nothing throws, nothing logs, and the trace on screen is
    /// simply wrong. That is the worst failure this codebase can produce, and the reason
    /// <c>REQ-NFR-002</c> sat open rather than being implemented quickly.
    /// </para>
    /// <para>
    /// <strong>What makes it safe.</strong> The buffer is reachable only through
    /// <see cref="Array"/>, which throws <see cref="ObjectDisposedException"/> once the lease has
    /// been released. A missed <see cref="Retain"/> therefore fails at the first read, in the
    /// consumer that made the mistake, with a stack trace pointing at it — instead of surfacing as
    /// a measurement nobody can explain. Making wrongness loud is the same principle the rest of
    /// the test strategy is built on.
    /// </para>
    /// <para>
    /// <strong>Why the lease object is not itself pooled.</strong> A pooled lease would have to
    /// carry a generation counter, because a reissued lease object would otherwise look alive to a
    /// stale holder. One small short-lived object per frame is gen-0 garbage that never reaches
    /// gen 2, which is what the requirement is about — so the simpler thing that cannot go wrong
    /// is the right trade. The 8 MiB array it wraps is the allocation that mattered.
    /// </para>
    /// </remarks>
    public sealed class BufferLease
    {
        private readonly SampleBufferPool _pool;
        private float[] _array;
        private int _references;

        internal BufferLease(SampleBufferPool pool, float[] array)
        {
            _pool = pool;
            _array = array;
            _references = 1;
        }

        /// <summary>
        /// The buffer, while the lease is held.
        /// </summary>
        /// <exception cref="ObjectDisposedException">The lease has been released.</exception>
        /// <remarks>
        /// Read through <see cref="Volatile"/> so a release on another thread is observed rather
        /// than cached in a register: the whole value of this type is that the failure is prompt.
        /// </remarks>
        public float[] Array
        {
            get
            {
                float[] array = Volatile.Read(ref _array);

                if (array == null)
                {
                    throw new ObjectDisposedException(
                        nameof(BufferLease),
                        "This pooled buffer has been returned and may already hold another " +
                        "frame's data. Something kept a reference past its lease: call Retain() " +
                        "when storing a frame beyond the call that produced it, and Release() " +
                        "when done. See REQ-NFR-002.");
                }

                return array;
            }
        }

        /// <summary>Whether the lease is still held by at least one owner.</summary>
        public bool IsAlive => Volatile.Read(ref _array) != null;

        /// <summary>How many owners hold this lease. For tests and diagnostics.</summary>
        public int References => Volatile.Read(ref _references);

        /// <summary>Takes an additional share of the lease.</summary>
        /// <exception cref="ObjectDisposedException">The lease has already been released.</exception>
        public void Retain()
        {
            if (Volatile.Read(ref _array) == null)
            {
                throw new ObjectDisposedException(
                    nameof(BufferLease), "Cannot retain a buffer that has already been returned.");
            }

            Interlocked.Increment(ref _references);
        }

        /// <summary>
        /// Gives up a share of the lease, returning the buffer to the pool at the last one.
        /// </summary>
        /// <remarks>
        /// Releasing more times than retained is a bug in the caller, but throwing here would turn
        /// it into a crash on a background thread during shutdown. It is clamped instead, and the
        /// buffer is returned exactly once.
        /// </remarks>
        public void Release()
        {
            if (Volatile.Read(ref _array) == null)
            {
                return;
            }

            if (Interlocked.Decrement(ref _references) > 0)
            {
                return;
            }

            // Exchange, so two racing final releases cannot both return the array to the pool --
            // which would put one buffer in two buckets and hand it to two owners at once.
            float[] array = Interlocked.Exchange(ref _array, null);

            if (array != null)
            {
                _pool.Return(array);
            }
        }
    }
}
