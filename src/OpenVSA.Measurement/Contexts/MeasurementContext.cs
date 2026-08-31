using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using OpenVSA.Core;
using OpenVSA.Demod.Chain;
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

        private readonly EqualiserState _equaliser = new EqualiserState();

        private Demodulator _demodulator;
        private DemodSettings _demodSettings;
        private DemodState _demodSettingsFrom;
        private DemodResult _latestResult;
        private long _resultsAnalysed;
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

        /// <summary>Raised when this context has demodulated a block.</summary>
        /// <remarks>
        /// The demodulation leg of <see cref="FrameAnalysed"/>, raised on the same thread at the
        /// same point: a context whose setup asks for digital demodulation produces both from one
        /// block, so a spectrum and a constellation on screen are two views of one acquisition
        /// rather than of two.
        /// </remarks>
        public event EventHandler<DemodResult> ResultAnalysed;

        /// <summary>
        /// Raised when a demodulation could not be performed with this context's settings.
        /// </summary>
        /// <remarks>
        /// A setting to correct rather than an acquisition to abandon, which is why it is an event
        /// and not an exception -- see <c>Demodulate</c>. Nothing subscribing is the same as nobody
        /// being told, so the shell subscribes and says so rather than leaving a constellation to
        /// go quietly stale.
        /// </remarks>
        public event EventHandler<Exception> DemodulationFaulted;

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

        /// <summary>Blocks this context has demodulated.</summary>
        public long ResultsAnalysed => Interlocked.Read(ref _resultsAnalysed);

        /// <summary>Whether this context's setup asks for digital demodulation.</summary>
        public bool IsDemodulating => _setup.Kind == MeasurementKind.DigitalDemodulation;

        /// <summary>The newest demodulation this context produced, or <c>null</c>.</summary>
        /// <remarks>
        /// Unlike a frame, a result owns no pooled buffer, so it is handed out as it stands rather
        /// than with a share taken. It is read once, under the lock, as a whole object: reading two
        /// properties off "the latest result" could otherwise read them off two different ones.
        /// </remarks>
        public DemodResult LatestResult
        {
            get
            {
                lock (_gate)
                {
                    return _latestResult;
                }
            }
        }

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

            if (IsDemodulating)
            {
                Demodulate(block);
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
        /// Demodulates one block as this context's setup asks for it (<c>REQ-DEM-001</c>).
        /// </summary>
        /// <param name="block">The block, owned by the caller.</param>
        /// <remarks>
        /// <para>
        /// <strong>A failure here is reported, not thrown.</strong> The block arrives on the
        /// acquisition pump's thread, and <c>SpectrumEngine</c>'s contract is that a handler which
        /// throws stops the pump. For a transform that is the right outcome, because nothing the
        /// context produced afterwards would mean anything. A demodulation is different: a record
        /// too short for the Result Length, or a symbol rate that does not suit the span, is a
        /// setting to correct rather than an acquisition to abandon -- and stopping the measurement
        /// would take down the spectrum the user needs in order to see what the setting should be.
        /// </para>
        /// <para>
        /// <strong>The samples are copied.</strong> The chain takes an array; the block owns a span
        /// over a pooled buffer that goes back to the pool as soon as the pump's handlers return.
        /// One copy per block, at the boundary where the block's lifetime ends and the result's
        /// begins.
        /// </para>
        /// </remarks>
        private void Demodulate(IqBlock block)
        {
            DemodResult result;

            try
            {
                DemodSettings settings = DemodulationSettings();

                var samples = new float[block.SampleCount * 2];

                block.GetSamples().CopyTo(new Span<float>(samples));

                result = Chain().Run(samples, block.SampleRateHz, settings);
            }
            catch (Exception failure) when (
                failure is ArgumentException || failure is ChainOrderException)
            {
                EventHandler<Exception> faulted = DemodulationFaulted;

                if (faulted != null)
                {
                    faulted(this, failure);
                }

                return;
            }

            Interlocked.Increment(ref _resultsAnalysed);

            lock (_gate)
            {
                _latestResult = result;
            }

            EventHandler<DemodResult> handler = ResultAnalysed;

            if (handler != null)
            {
                handler(this, result);
            }
        }

        /// <summary>
        /// What the equaliser has learnt, across every measurement this context makes
        /// (<c>REQ-DEM-051</c>).
        /// </summary>
        /// <remarks>
        /// <para>
        /// <strong>The context owns it, not the setup.</strong> Run carries coefficients from one
        /// measurement into the next and Hold freezes them, so they have to outlive a
        /// <see cref="DemodSettings"/>, which is rebuilt whenever the setup changes. They have to
        /// outlive the <see cref="DemodState"/> too: <em>changing the mode is changing the setup</em>,
        /// and a memory kept on the setup would be lost by the very act of selecting Hold. The
        /// context is the thing that persists across both, so it is where this belongs.
        /// </para>
        /// <para>
        /// 🔴 It was on the setup first, and the bench found what that costs. Switching the mode
        /// replaced the state, the new state brought an empty memory, and Hold froze nothing: the
        /// measurement reported no coefficients at all while EVM moved around with the block. Which
        /// object owns a piece of state is not bookkeeping when the state is what a mode is
        /// <em>about</em>.
        /// </para>
        /// <para>
        /// Not saved with a state file. It is the result of measurements taken, not a choice, and
        /// recalling a setup should not restore an equaliser fitted to a channel that is no longer
        /// connected.
        /// </para>
        /// </remarks>
        public EqualiserState EqualiserAdaptation => _equaliser;

        /// <summary>
        /// The chain's settings for this context, rebuilt when the setup changes them.
        /// </summary>
        /// <remarks>
        /// Rebuilt rather than remade per block: resolving a format's name allocates its
        /// constellation, and doing that for every acquired block would build a list of points
        /// sixty times a second to describe something that had not changed. The cache is keyed on
        /// the <see cref="DemodState"/> instance, so a setting changed in place on the same state
        /// object does not take effect — changing the setup means handing over a new state, which
        /// is how the shell and a recall both do it.
        /// </remarks>
        private DemodSettings DemodulationSettings()
        {
            DemodState state = _setup.Demod;

            if (_demodSettings == null || !ReferenceEquals(_demodSettingsFrom, state))
            {
                _demodSettings = state.ToSettings();
                _demodSettingsFrom = state;
            }

            // Every settings object this context builds shares the one memory, so a rebuilt
            // settings -- a format change, a mode change, a recall -- does not wipe the equaliser.
            _demodSettings.EqualiserState = _equaliser;

            return _demodSettings;
        }

        private Demodulator Chain() => _demodulator ?? (_demodulator = new Demodulator());

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
