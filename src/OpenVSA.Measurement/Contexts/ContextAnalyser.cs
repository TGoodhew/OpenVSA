using System;
using System.Collections.Generic;
using OpenVSA.Core;

namespace OpenVSA.Measurement.Contexts
{
    /// <summary>
    /// Feeds every context in a set from one capture session (<c>REQ-DAT-010</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is what makes two contexts <em>concurrent</em> rather than two modes. It subscribes to
    /// <see cref="SpectrumEngine.BlockAcquired"/> — the samples, before any one context's analysis
    /// of them — and hands each block to every context in turn, so a spectrum and a demodulation are
    /// looking at the same acquisition rather than at two acquisitions taken a moment apart. There
    /// is no second front end, no second arm, and nothing retuned.
    /// </para>
    /// <para>
    /// <strong>The primary context is skipped, because the pump has already analysed it.</strong>
    /// <see cref="SpectrumEngine"/> runs its DSP stage inline and publishes through
    /// <c>FrameComputed</c>, which is the path the display draws from and the one whose zoom,
    /// trimming and averaging are configured from the active context's setup. Analysing it again here
    /// would repeat the transform — 84 % of a frame by measurement — to produce a second copy of a
    /// result already on screen. <see cref="Primary"/> is therefore set to whichever context the
    /// engine is configured for, and every other context is analysed here.
    /// </para>
    /// <para>
    /// <strong>Runs on the pump thread, inside the block's lifetime.</strong> The block is disposed
    /// as soon as every <c>BlockAcquired</c> handler returns, so each context's transform happens
    /// synchronously here. A context that threw would stop the pump, which is the engine's stated
    /// contract for that event and the right outcome: a context that cannot analyse the signal is a
    /// fault worth reporting rather than one worth continuing through.
    /// </para>
    /// </remarks>
    public sealed class ContextAnalyser : IDisposable
    {
        private readonly MeasurementContextSet _set;

        private SpectrumEngine _engine;
        private bool _disposed;

        /// <summary>
        /// Creates an analyser over a set of contexts.
        /// </summary>
        /// <param name="contexts">The contexts to feed.</param>
        /// <exception cref="ArgumentNullException"><paramref name="contexts"/> is null.</exception>
        public ContextAnalyser(MeasurementContextSet contexts)
        {
            if (contexts == null)
            {
                throw new ArgumentNullException(nameof(contexts));
            }

            _set = contexts;
        }

        /// <summary>
        /// The context the capture session's own analysis produces, and which is therefore not
        /// analysed again here. <c>null</c> analyses every context.
        /// </summary>
        public MeasurementContext Primary { get; set; }

        /// <summary>Blocks this analyser has distributed.</summary>
        /// <remarks>
        /// Written on the pump thread and read from the UI's, so read through
        /// <see cref="System.Threading.Interlocked"/> rather than as a plain field.
        /// </remarks>
        public long BlocksAnalysed => System.Threading.Interlocked.Read(ref _blocksAnalysed);

        private long _blocksAnalysed;

        /// <summary>The contexts a block would be analysed for, as things stand.</summary>
        public IReadOnlyList<MeasurementContext> Secondaries
        {
            get
            {
                var secondaries = new List<MeasurementContext>();

                foreach (MeasurementContext context in _set.Contexts)
                {
                    if (!ReferenceEquals(context, Primary))
                    {
                        secondaries.Add(context);
                    }
                }

                return secondaries;
            }
        }

        /// <summary>
        /// Attaches to a capture session, detaching from any previous one.
        /// </summary>
        /// <param name="engine">The session, or <c>null</c> to detach.</param>
        /// <remarks>
        /// One session at a time. The shell builds a new engine on every Apply, and an analyser that
        /// stayed subscribed to the old one would keep analysing blocks from a front end that had
        /// been abandoned — or, worse, from two.
        /// </remarks>
        public void Attach(SpectrumEngine engine)
        {
            ThrowIfDisposed();

            if (ReferenceEquals(_engine, engine))
            {
                return;
            }

            if (_engine != null)
            {
                _engine.BlockAcquired -= OnBlockAcquired;
            }

            _engine = engine;

            if (_engine != null)
            {
                _engine.BlockAcquired += OnBlockAcquired;
            }
        }

        /// <summary>
        /// Analyses a block for every context but the primary.
        /// </summary>
        /// <param name="block">The block, owned by the caller.</param>
        /// <exception cref="ArgumentNullException"><paramref name="block"/> is null.</exception>
        /// <remarks>
        /// Public so that the distribution can be exercised without a running front end: what the
        /// event handler does is call this, and a test that drove the event instead would be testing
        /// the engine.
        /// </remarks>
        public void Distribute(IqBlock block)
        {
            if (block == null)
            {
                throw new ArgumentNullException(nameof(block));
            }

            // Snapshotted, because a handler is free to add or remove a context and mutating the
            // set while it is being walked would throw on the pump thread.
            IReadOnlyList<MeasurementContext> secondaries = Secondaries;

            for (int i = 0; i < secondaries.Count; i++)
            {
                secondaries[i].Analyse(block);
            }

            System.Threading.Interlocked.Increment(ref _blocksAnalysed);
        }

        /// <summary>Detaches from the capture session. The contexts and the engine are not disposed.</summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            Attach(null);
            _disposed = true;
        }

        private void OnBlockAcquired(object sender, IqBlock block) => Distribute(block);

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(ContextAnalyser));
            }
        }
    }
}
