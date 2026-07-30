using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using OpenVSA.Core;
using OpenVSA.Dsp.Spectrum;
using OpenVSA.Dsp.Windowing;
using OpenVSA.Measurement.Markers;
using OpenVSA.Measurement.State;

namespace OpenVSA.Measurement.Contexts
{
    /// <summary>
    /// One live measurement context: a name, a complete measurement setup, its own trace windows
    /// and its own markers (<c>REQ-DAT-010</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The reference product calls these Analyzer Configurations, and the point of them is that two
    /// can be looking at the same signal in different ways at the same time — a spectrum and a
    /// demodulation, say — without either being a mode the other has to be switched out of. So a
    /// context owns everything that makes a measurement <em>its</em> measurement: the setup, the
    /// transform, the averaging, the traces and the markers. What it deliberately does not own is
    /// the acquisition: <see cref="ContextAnalyser"/> feeds every context from one capture session,
    /// because the samples are a property of the input and not of any one way of looking at it.
    /// </para>
    /// <para>
    /// <strong>Analysis settings differ between contexts; the acquired band does not.</strong> Two
    /// contexts can use different windows, point counts, averaging and detectors against the same
    /// blocks, and that is the useful case. Centre frequency and span belong to the capture — one
    /// acquisition cannot be at two centre frequencies — so a context whose setup names a different
    /// centre is analysing the band that was actually captured, and <see cref="Setup"/> records what
    /// it asked for so a recall of it as the primary context tunes there.
    /// </para>
    /// <para>
    /// <strong><see cref="Analyse"/> runs on the acquisition pump; the rest of this is the UI's.</strong>
    /// The frame it produces is handed over by <see cref="TakeLatestFrame"/> under a lock and with a
    /// share of its own already taken (<c>REQ-NFR-002</c>), so a display can collect the newest
    /// frame of a context it has not been drawing without racing the thread that is replacing it.
    /// </para>
    /// </remarks>
    public sealed class MeasurementContext
    {
        private readonly MarkerCollection _markers = new MarkerCollection();
        private readonly List<char> _traces = new List<char>();
        private readonly object _gate = new object();

        private MeasurementState _setup;
        private SpectrumComputer _computer;
        private WindowType _computerWindow;
        private int _computerMaxTransform;
        private TraceAverager _averager;
        private AveragingType _averagerType;
        private int _averagerCount;
        private SpectrumFrame _latest;
        private long _framesAnalysed;
        private char _activeTrace = 'A';

        /// <summary>
        /// Creates a context.
        /// </summary>
        /// <param name="name">The context's name, which is what a state is matched on.</param>
        /// <param name="setup">Its setup, or <c>null</c> for the defaults under that name.</param>
        /// <exception cref="ArgumentException"><paramref name="name"/> is null or blank.</exception>
        public MeasurementContext(string name, MeasurementState setup = null)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("A context needs a name.", nameof(name));
            }

            Name = name;
            _setup = setup ?? new MeasurementState { ContextName = name };
            _setup.ContextName = name;
        }

        /// <summary>
        /// Raised on the analysing thread for every frame this context computes.
        /// </summary>
        /// <remarks>
        /// The frame is the analyser's, not the handler's: it is released as soon as every handler
        /// returns, so a handler that keeps it must <see cref="SpectrumFrame.Retain"/> — and one
        /// that does not reads an <see cref="ObjectDisposedException"/> rather than a later frame's
        /// spectrum. <see cref="TakeLatestFrame"/> exists so that the ordinary consumer does not
        /// have to subscribe at all.
        /// </remarks>
        public event EventHandler<SpectrumFrame> FrameAnalysed;

        /// <summary>
        /// The context's name.
        /// </summary>
        /// <remarks>
        /// Renamed through <see cref="MeasurementContextSet.Rename"/> rather than here, because
        /// uniqueness is a property of the set and a name that collided would break the by-name
        /// matching <c>REQ-STA-004</c> recalls on.
        /// </remarks>
        public string Name { get; internal set; }

        /// <summary>
        /// The complete measurement setup this context holds (<c>REQ-STA-001</c>).
        /// </summary>
        /// <exception cref="ArgumentNullException">The value is null.</exception>
        /// <remarks>
        /// Assigning one renames it to this context: a setup recalled from a file carries the name
        /// it was saved under, and the two disagreeing is how a subsequent save writes a state that
        /// cannot be recalled into the session that produced it.
        /// </remarks>
        public MeasurementState Setup
        {
            get { return _setup; }

            set
            {
                if (value == null)
                {
                    throw new ArgumentNullException(nameof(value));
                }

                value.ContextName = Name;
                _setup = value;
            }
        }

        /// <summary>This context's markers, per trace (<c>REQ-MKR-002</c>).</summary>
        /// <remarks>
        /// Per context, which is the half of <c>REQ-DAT-010</c> that a single global marker set
        /// cannot express: a marker put on a spectrum has no meaning on a constellation, and a
        /// context switch that carried markers across would move them somewhere they were never
        /// placed.
        /// </remarks>
        public MarkerCollection Markers => _markers;

        /// <summary>The trace windows belonging to this context, in the order they were opened.</summary>
        public IReadOnlyList<char> Traces => new ReadOnlyCollection<char>(_traces);

        /// <summary>Frames this context has analysed.</summary>
        public long FramesAnalysed => Interlocked.Read(ref _framesAnalysed);

        /// <summary>
        /// The trace this context's commands act on.
        /// </summary>
        /// <exception cref="ArgumentException">That trace does not belong to this context.</exception>
        public char ActiveTrace
        {
            get { return _activeTrace; }

            set
            {
                if (!_traces.Contains(value))
                {
                    throw new ArgumentException(
                        "Trace " + value + " does not belong to context '" + Name + "'.",
                        nameof(value));
                }

                _activeTrace = value;
            }
        }

        /// <summary>
        /// Gives this context a trace window.
        /// </summary>
        /// <param name="trace">The trace letter.</param>
        /// <returns><c>false</c> when it already had it.</returns>
        public bool AddTrace(char trace)
        {
            if (_traces.Contains(trace))
            {
                return false;
            }

            _traces.Add(trace);

            if (_traces.Count == 1)
            {
                _activeTrace = trace;
            }

            return true;
        }

        /// <summary>
        /// Takes a trace window away from this context.
        /// </summary>
        /// <param name="trace">The trace letter.</param>
        /// <returns><c>false</c> when it did not have it.</returns>
        public bool RemoveTrace(char trace)
        {
            if (!_traces.Remove(trace))
            {
                return false;
            }

            if (_activeTrace == trace && _traces.Count > 0)
            {
                _activeTrace = _traces[0];
            }

            return true;
        }

        /// <summary>Whether a trace window belongs to this context.</summary>
        /// <param name="trace">The trace letter.</param>
        public bool HasTrace(char trace) => _traces.Contains(trace);

        /// <summary>
        /// The transform this context's setup asks for, rebuilt when the setup changes it.
        /// </summary>
        /// <remarks>
        /// <see cref="SpectrumComputer.WindowType"/> is fixed at construction — the window's
        /// coefficients and its noise-bandwidth correction are computed once from it — so a context
        /// that changed window gets a new computer rather than a mutated one.
        /// </remarks>
        public SpectrumComputer Computer
        {
            get
            {
                AnalysisState analysis = _setup.Analysis;

                if (_computer == null ||
                    _computerWindow != analysis.Window ||
                    _computerMaxTransform != analysis.MaxTransformLength)
                {
                    _computer = new SpectrumComputer(analysis.Window, null, null)
                    {
                        MaxTransformLength = analysis.MaxTransformLength,

                        // The analysis span, not the wider band the anti-alias filter needed
                        // (REQ-ACQ-001) -- the same trimming the pump's own computer does, so a
                        // secondary context's frame covers the same axis as the primary's.
                        TrimToAnalysisSpan = true,

                        // REQ-NFR-002. Safe because this context is the only holder: Analyse
                        // releases its share when the last handler returns, and the frame it keeps
                        // for TakeLatestFrame gives up the one it replaces.
                        PoolFrames = true,
                    };

                    _computerWindow = analysis.Window;
                    _computerMaxTransform = analysis.MaxTransformLength;
                }

                return _computer;
            }
        }

        /// <summary>
        /// The averaging this context's setup asks for, or <c>null</c> for none
        /// (<c>REQ-DSP-030</c>).
        /// </summary>
        public TraceAverager Averager
        {
            get
            {
                AnalysisState analysis = _setup.Analysis;

                if (analysis.Averaging == AveragingType.Off)
                {
                    _averager = null;
                    _averagerType = AveragingType.Off;

                    return null;
                }

                if (_averager == null ||
                    _averagerType != analysis.Averaging ||
                    _averagerCount != analysis.AverageCount)
                {
                    _averager = new TraceAverager(analysis.Averaging, analysis.AverageCount)
                    {
                        RepeatAverage = analysis.RepeatAverage,
                    };

                    _averagerType = analysis.Averaging;
                    _averagerCount = analysis.AverageCount;
                }

                _averager.RepeatAverage = analysis.RepeatAverage;

                return _averager;
            }
        }

        /// <summary>
        /// Analyses one acquired block as this context's setup asks for it.
        /// </summary>
        /// <param name="block">The block, owned by the caller.</param>
        /// <exception cref="ArgumentNullException"><paramref name="block"/> is null.</exception>
        /// <remarks>
        /// The block is not kept: it belongs to the pump and is disposed as soon as every consumer
        /// of <c>SpectrumEngine.BlockAcquired</c> has returned, so the analysis happens here and now
        /// rather than being queued.
        /// </remarks>
        public void Analyse(IqBlock block)
        {
            if (block == null)
            {
                throw new ArgumentNullException(nameof(block));
            }

            SpectrumFrame computed = Computer.Compute(block);
            SpectrumFrame frame = computed;
            TraceAverager averager = Averager;

            if (averager != null)
            {
                averager.Overlap = _setup.Analysis.Overlap;
                averager.RecordSamples = block.SampleCount;

                frame = averager.Accumulate(computed);
            }

            Interlocked.Increment(ref _framesAnalysed);

            try
            {
                Keep(frame);

                EventHandler<SpectrumFrame> handler = FrameAnalysed;

                if (handler != null)
                {
                    handler(this, frame);
                }
            }
            finally
            {
                // The shares this method was handed, given up in the same shape the pump uses: an
                // averager returns a different frame from the one it was given, and the computed
                // one's pooled buffer would otherwise never come back. Release is a no-op on an
                // unpooled frame, so the averaging-off case releases exactly once.
                if (!ReferenceEquals(frame, computed))
                {
                    frame.Release();
                }

                computed.Release();
            }
        }

        /// <summary>
        /// The newest frame this context analysed, with a share taken for the caller, or
        /// <c>null</c> when it has analysed none.
        /// </summary>
        /// <remarks>
        /// The caller releases it. Retained under the lock rather than handed out and retained
        /// afterwards, because the thread that replaces it would otherwise be free to release the
        /// last share in between — and the reader would then be reading a buffer that had gone back
        /// to the pool.
        /// </remarks>
        public SpectrumFrame TakeLatestFrame()
        {
            lock (_gate)
            {
                _latest?.Retain();

                return _latest;
            }
        }

        /// <summary>Whether this context has a frame to hand over.</summary>
        public bool HasFrame
        {
            get
            {
                lock (_gate)
                {
                    return _latest != null;
                }
            }
        }

        /// <summary>
        /// Releases the frame this context is holding.
        /// </summary>
        /// <remarks>
        /// Called when a context is removed from a set, and when a measurement stops: a pooled
        /// buffer held by a context nothing will ever display again is a buffer the pool has lost.
        /// </remarks>
        public void ClearFrame() => Keep(null);

        /// <inheritdoc />
        public override string ToString() =>
            "'" + Name + "': " + _traces.Count + " trace" + (_traces.Count == 1 ? string.Empty : "s");

        private void Keep(SpectrumFrame frame)
        {
            SpectrumFrame replaced;

            // Retained before the swap, and outside the lock: the caller still holds a share of the
            // frame being kept, so taking a second one cannot be the moment it goes away, and doing
            // it here keeps the lock down to the two field accesses a reader has to be excluded
            // from.
            frame?.Retain();

            lock (_gate)
            {
                replaced = _latest;
                _latest = frame;
            }

            if (!ReferenceEquals(replaced, frame))
            {
                replaced?.Release();
            }
        }
    }
}
