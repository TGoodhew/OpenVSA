using System;

namespace OpenVSA.SoakGate
{
    /// <summary>
    /// One observation of a running shell, taken by the soak host (<c>REQ-TST-009</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every field is a raw reading, and none is a judgement. The whole point of the split is that
    /// the deciding — trends, hourly rates, whether handles came back — is arithmetic over these,
    /// testable in milliseconds, rather than something only an eight-hour run can exercise.
    /// </para>
    /// <para>
    /// The counters are cumulative rather than per-interval, because a difference of two readings is
    /// exact where a per-interval figure has already been rounded to whatever the host's sampling
    /// clock happened to be.
    /// </para>
    /// </remarks>
    public sealed class SoakSample
    {
        /// <summary>Creates a sample.</summary>
        /// <param name="elapsedSeconds">Seconds since the run began.</param>
        /// <param name="managedBytes">Managed heap size, without forcing a collection.</param>
        /// <param name="collectedManagedBytes">
        /// Managed heap size after a full blocking collection, or zero on samples that did not
        /// collect. Only the non-zero ones can answer whether managed memory is bounded, because an
        /// uncollected heap rises and falls by design.
        /// </param>
        /// <param name="privateBytes">The process's private bytes: the unmanaged half.</param>
        /// <param name="handles">Kernel handle count.</param>
        /// <param name="gdiObjects">GDI object count.</param>
        /// <param name="userObjects">USER object count.</param>
        /// <param name="framesDrawn">Cumulative frames collected for drawing.</param>
        /// <param name="framesDropped">Cumulative frames coalesced away.</param>
        /// <param name="pooledBuffers">Buffers the sample-buffer pool is retaining.</param>
        /// <param name="pooledBytes">Bytes the sample-buffer pool is retaining.</param>
        /// <param name="tracesOpen">Trace windows open at this moment.</param>
        /// <param name="cycles">Completed create-and-destroy cycles of windows and traces.</param>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="elapsedSeconds"/> is negative or not a number.</exception>
        public SoakSample(
            double elapsedSeconds,
            long managedBytes,
            long collectedManagedBytes,
            long privateBytes,
            int handles,
            int gdiObjects,
            int userObjects,
            long framesDrawn,
            long framesDropped,
            int pooledBuffers,
            long pooledBytes,
            int tracesOpen,
            int cycles)
        {
            if (double.IsNaN(elapsedSeconds) || elapsedSeconds < 0.0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(elapsedSeconds), elapsedSeconds, "A sample cannot be taken before the run started.");
            }

            ElapsedSeconds = elapsedSeconds;
            ManagedBytes = managedBytes;
            CollectedManagedBytes = collectedManagedBytes;
            PrivateBytes = privateBytes;
            Handles = handles;
            GdiObjects = gdiObjects;
            UserObjects = userObjects;
            FramesDrawn = framesDrawn;
            FramesDropped = framesDropped;
            PooledBuffers = pooledBuffers;
            PooledBytes = pooledBytes;
            TracesOpen = tracesOpen;
            Cycles = cycles;
        }

        /// <summary>Seconds since the run began.</summary>
        public double ElapsedSeconds { get; }

        /// <summary>Hours since the run began.</summary>
        public double ElapsedHours => ElapsedSeconds / 3600.0;

        /// <summary>Managed heap size, uncollected.</summary>
        public long ManagedBytes { get; }

        /// <summary>Managed heap size after a full collection, or zero if none was forced.</summary>
        public long CollectedManagedBytes { get; }

        /// <summary>Whether this sample forced a collection, so its managed figure is a floor.</summary>
        public bool Collected => CollectedManagedBytes > 0L;

        /// <summary>The process's private bytes.</summary>
        public long PrivateBytes { get; }

        /// <summary>Kernel handle count.</summary>
        public int Handles { get; }

        /// <summary>GDI object count.</summary>
        public int GdiObjects { get; }

        /// <summary>USER object count.</summary>
        public int UserObjects { get; }

        /// <summary>Cumulative frames collected for drawing.</summary>
        public long FramesDrawn { get; }

        /// <summary>Cumulative frames coalesced away (<c>REQ-NFR-012</c>).</summary>
        public long FramesDropped { get; }

        /// <summary>Buffers retained by the pool of <c>REQ-NFR-011</c>.</summary>
        public int PooledBuffers { get; }

        /// <summary>Bytes retained by the pool of <c>REQ-NFR-011</c>.</summary>
        public long PooledBytes { get; }

        /// <summary>Trace windows open at this moment.</summary>
        public int TracesOpen { get; }

        /// <summary>Completed create-and-destroy cycles.</summary>
        public int Cycles { get; }
    }
}
