using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using OpenVSA.Core;
using OpenVSA.Core.Threading;
using OpenVSA.Dsp.Spectrum;
using OpenVSA.Dsp.Zoom;
using OpenVSA.Hal;

namespace OpenVSA.Measurement
{
    /// <summary>
    /// The acquisition pump: negotiates a plan, pulls blocks from a front end and publishes the
    /// spectrum of each one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the "acquisition pump" row of <c>REQ-NFR-010</c>'s topology, with the DSP stage
    /// running inline on it. The two are not split until there is a reason to: the transform is
    /// 84 % of a frame by measurement, so a hand-off between them would move the same work across a
    /// queue boundary and add latency without adding parallelism. Frames are independent
    /// (<c>REQ-DSP-001</c>), so when that reason arrives — several traces from one acquisition —
    /// the split is a <c>Parallel.For</c> over traces here, not a redesign.
    /// </para>
    /// <para>
    /// <strong>Nothing here touches the UI.</strong> <see cref="FrameComputed"/> is raised on the
    /// pump thread, and a handler that wants to draw must marshal. The frame it receives is an
    /// immutable snapshot (<c>REQ-NFR-011</c>), so the marshalling is a hand-off of a reference and
    /// not a copy.
    /// </para>
    /// <para>
    /// <strong>Pacing is a first-class setting, not a sleep.</strong> A synthetic front end returns
    /// blocks as fast as the CPU can make them; left unpaced the pump would saturate a core to
    /// produce frames no display can show. <see cref="TargetUpdatesPerSecond"/> bounds the rate,
    /// and setting it to zero removes the bound — which is what the throughput measurements of
    /// <c>REQ-NFR-020</c>/<c>021</c> need.
    /// </para>
    /// </remarks>
    public sealed class SpectrumEngine : IDisposable
    {
        private readonly IFrontEnd _frontEnd;
        private readonly SpectrumComputer _computer;

        private CancellationTokenSource _cancellation;
        private Task _pump;
        private long _framesComputed;
        private double _measuredUpdatesPerSecond;
        private double _overlap;
        private int _recordSamples;
        private bool _disposed;

        /// <summary>Creates an engine over a front end.</summary>
        /// <param name="frontEnd">The source; the engine does not own it and will not dispose it.</param>
        /// <param name="computer">The spectrum computation, or <c>null</c> for the defaults.</param>
        /// <exception cref="ArgumentNullException"><paramref name="frontEnd"/> is null.</exception>
        public SpectrumEngine(IFrontEnd frontEnd, SpectrumComputer computer)
        {
            if (frontEnd == null)
            {
                throw new ArgumentNullException(nameof(frontEnd));
            }

            _frontEnd = frontEnd;
            _computer = computer ?? new SpectrumComputer();

            // REQ-NFR-002. The pump is the one producer whose frames are published at rate, and
            // every consumer downstream of it now takes its own share of the buffer: the render
            // marshal's pending snapshot, the shell's held frame, each trace plot, the spectrogram
            // history, the trace registers, the marker collection. A consumer that missed the
            // protocol reads an ObjectDisposedException rather than a later frame's spectrum, so a
            // mistake here is loud and attributable instead of being a wrong trace nobody notices.
            //
            // Set on whatever computer was supplied as well as the default one: a caller passing a
            // configured computer is still driving this pump, and the pump's release discipline is
            // what makes pooling correct.
            _computer.PoolFrames = true;
        }

        /// <summary>Raised on the pump thread for every computed frame.</summary>
        public event EventHandler<SpectrumFrame> FrameComputed;

        /// <summary>
        /// Raised on the pump thread when acquisition stops because of an error.
        /// </summary>
        /// <remarks>
        /// An event rather than a faulted task, because the pump outlives the call that started it:
        /// an exception thrown three seconds into a run has no caller left to propagate to, and
        /// leaving it on an unobserved task is how a measurement stops silently.
        /// </remarks>
        public event EventHandler<Exception> Faulted;

        /// <summary>Raised on the pump thread when the source reports itself exhausted.</summary>
        public event EventHandler Completed;

        /// <summary>
        /// Raised on the pump thread for every acquired block, before it is analysed
        /// (<c>REQ-ARC-003</c>).
        /// </summary>
        /// <remarks>
        /// <para>
        /// What a measurement personality consumes. Personalities work from samples rather than
        /// from a spectrum, because a demodulator that was handed a spectrum would have to invert
        /// the analysis to get back what it needed — and a personality that could only see what the
        /// spectrum path happened to compute would not be a plug-in in any useful sense.
        /// </para>
        /// <para>
        /// <strong>The block is disposed as soon as every handler returns.</strong> A handler that
        /// keeps it is reading a buffer that has gone back to whoever owns it. Copy what is needed.
        /// </para>
        /// <para>
        /// A handler that throws stops the pump, in the same way that a fault in analysis does. A
        /// personality that cannot process a block is a fault worth reporting, not one worth
        /// continuing through at whatever rate the exceptions allow.
        /// </para>
        /// </remarks>
        public event EventHandler<IqBlock> BlockAcquired;

        /// <summary>The plan the front end honoured, or <c>null</c> before the first start.</summary>
        /// <remarks>
        /// Kept because <c>REQ-HAL-001</c> requires the coercions it carries to be shown, not just
        /// obeyed: what the user asked for and what the hardware agreed to are different things and
        /// the UI has to be able to say so.
        /// </remarks>
        public AcquisitionPlan Plan { get; private set; }

        /// <summary>The spectrum computation in use.</summary>
        public SpectrumComputer Computer => _computer;

        /// <summary>Whether the pump is running.</summary>
        public bool IsRunning => _pump != null && !_pump.IsCompleted;

        /// <summary>Frames computed since construction.</summary>
        public long FramesComputed => Interlocked.Read(ref _framesComputed);

        /// <summary>Measured update rate, in frames per second, smoothed over recent frames.</summary>
        public double MeasuredUpdatesPerSecond => Volatile.Read(ref _measuredUpdatesPerSecond);

        /// <summary>
        /// Upper bound on the frame rate, in updates per second. Zero or negative means unbounded.
        /// </summary>
        public double TargetUpdatesPerSecond { get; set; } = 60.0;

        /// <summary>
        /// Overlap between successive analysis frames cut from one acquired block
        /// (<c>REQ-ACQ-003</c>), from 0 to <see cref="FrameExtraction.MaximumOverlap"/>.
        /// </summary>
        /// <remarks>
        /// Zero — the default — analyses each block once, which is the only thing a block sized to
        /// exactly one record can do. Overlap is worth setting when the front end returns more
        /// samples than the analysis needs: the surplus is then time resolution that would
        /// otherwise be thrown away at the window's tapered edges.
        /// </remarks>
        /// <exception cref="ArgumentOutOfRangeException">The value is outside the permitted range.</exception>
        public double Overlap
        {
            get { return _overlap; }

            set
            {
                // Validated through the extractor rather than here, so there is one statement of
                // what a legal overlap is.
                FrameExtraction.Advance(1, value);
                _overlap = value;
            }
        }

        /// <summary>
        /// Analysed record length, in samples, or 0 to analyse each block whole.
        /// </summary>
        /// <remarks>
        /// Separate from the block length because <c>REQ-ACQ-003</c> defines the frame advance on
        /// the record, and a block long enough to overlap within is by definition longer than the
        /// record it is cut into.
        /// </remarks>
        /// <exception cref="ArgumentOutOfRangeException">The value is negative.</exception>
        public int RecordSamples
        {
            get { return _recordSamples; }

            set
            {
                if (value < 0)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(value), value, "A record length cannot be negative.");
                }

                _recordSamples = value;
            }
        }

        /// <summary>
        /// Averaging applied to every computed frame, or <c>null</c> for none
        /// (<c>REQ-DSP-030</c>).
        /// </summary>
        /// <remarks>
        /// Held here rather than downstream because the effective average count of
        /// <c>REQ-DSP-031</c> depends on how the frames were cut, which is something only the pump
        /// knows: it tells the averager the overlap and record length it used.
        /// </remarks>
        public TraceAverager Averager { get; set; }

        /// <summary>
        /// The zoom downconverter applied to every acquired block, or <c>null</c> for full span
        /// (<c>REQ-DSP-023</c>).
        /// </summary>
        /// <remarks>
        /// <para>
        /// <strong>Set while running, and it takes effect on the next block.</strong> That is the
        /// whole point of zoom being a downconversion rather than a re-tune: the instrument is not
        /// touched, nothing is re-armed, and the analysis narrows on samples of the kind already
        /// arriving. Written through <see cref="Volatile"/> because the pump reads it on its own
        /// thread and a torn read would be a downconverter half-applied.
        /// </para>
        /// <para>
        /// A block too short for the filter is passed through undownconverted rather than dropped.
        /// A zoom deeper than the record supports is <c>REQ-REC-004</c>'s business to refuse before
        /// it gets here; silently producing no frames at all would look like the instrument had
        /// stopped.
        /// </para>
        /// </remarks>
        public DigitalDownconverter Zoom
        {
            get { return Volatile.Read(ref _downconverter); }
            set { Volatile.Write(ref _downconverter, value); }
        }

        private DigitalDownconverter _downconverter;

        /// <summary>
        /// Connects, negotiates, configures, arms and starts pumping.
        /// </summary>
        /// <param name="request">What to ask the front end for.</param>
        /// <param name="ct">Cancellation token for the set-up phase.</param>
        /// <returns>The plan the front end honoured, including any coercions (<c>REQ-HAL-001</c>).</returns>
        /// <exception cref="ArgumentNullException"><paramref name="request"/> is null.</exception>
        /// <exception cref="InvalidOperationException">The engine is already running.</exception>
        public async Task<AcquisitionPlan> StartAsync(AcquisitionRequest request, CancellationToken ct)
        {
            ThrowIfDisposed();

            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            if (IsRunning)
            {
                throw new InvalidOperationException("The engine is already running.");
            }

            // Off the caller's thread, which is the dispatcher. A front end may implement these
            // synchronously - the simulator does, and a VISA one does seconds of GPIB traffic
            // before returning - and awaiting a completed Task runs all of it inline. That is
            // REQ-NFR-010's "no blocking wait over 16 ms" broken by a method that looks async.
            AcquisitionPlan plan = await Task.Run(
                async () =>
                {
                    if (_frontEnd.State == FrontEndState.Disconnected)
                    {
                        await _frontEnd.ConnectAsync(ct).ConfigureAwait(false);
                    }

                    AcquisitionPlan negotiated = _frontEnd.Negotiate(request);
                    await _frontEnd.ConfigureAsync(negotiated, ct).ConfigureAwait(false);
                    await _frontEnd.ArmAsync(ct).ConfigureAwait(false);

                    return negotiated;
                },
                ct).ConfigureAwait(false);

            Plan = plan;

            // The acquired band is wider than the analysis span (REQ-ACQ-001); the display shows
            // the span, not the surplus the anti-alias filter rolls off in. The point count is
            // derived from the blocks that arrive rather than from the plan, so it stays right for
            // a front end that could not honour the requested rate exactly.
            _computer.TrimToAnalysisSpan = true;

            _cancellation = new CancellationTokenSource();
            CancellationToken pumpToken = _cancellation.Token;

            // Task.Run rather than an async method called directly: the front end may complete its
            // acquisition synchronously - the simulator does - and without this the whole pump
            // would run inline on the caller, which is the UI thread.
            _pump = Task.Run(() => PumpAsync(pumpToken), pumpToken);

            return plan;
        }

        /// <summary>Stops pumping and waits for the pump to finish.</summary>
        public async Task StopAsync()
        {
            CancellationTokenSource cancellation = _cancellation;
            Task pump = _pump;

            if (cancellation == null || pump == null)
            {
                return;
            }

            cancellation.Cancel();

            try
            {
                await pump.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected: this is how a running pump ends.
            }

            _pump = null;
            _cancellation = null;
            cancellation.Dispose();

            if (!_disposed)
            {
                await _frontEnd.AbortAsync().ConfigureAwait(false);
            }
        }

        private async Task PumpAsync(CancellationToken ct)
        {
            ThreadAffinity.AssertNotOnUiThread("Acquisition");

            var clock = Stopwatch.StartNew();
            long previousTicks = -1;

            try
            {
                while (!ct.IsCancellationRequested)
                {
                    IqBlock block = await _frontEnd.AcquireNextAsync(ct).ConfigureAwait(false);

                    if (block == null)
                    {
                        // Null is end-of-source, not an error: a recording has an end (IFrontEnd).
                        RaiseCompleted();
                        return;
                    }

                    using (block)
                    {
                        RaiseBlockAcquired(block);
                        AnalyseAndPublish(block, clock, ref previousTicks);
                    }

                    await PaceAsync(clock, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                EventHandler<Exception> handler = Faulted;
                if (handler == null)
                {
                    throw;
                }

                handler(this, e);
            }
        }

        /// <summary>
        /// Offers a block to whatever is listening, before it is analysed (<c>REQ-ARC-003</c>).
        /// </summary>
        /// <param name="block">The block, owned by the caller and disposed when this returns.</param>
        /// <remarks>
        /// Read into a local first: <see cref="BlockAcquired"/> may be unsubscribed between the
        /// null check and the call, and the field read would then be a null invocation on the pump
        /// thread. Not wrapped in a catch, deliberately — see the event's own remarks.
        /// </remarks>
        private void RaiseBlockAcquired(IqBlock block)
        {
            EventHandler<IqBlock> handler = BlockAcquired;

            if (handler != null)
            {
                handler(this, block);
            }
        }

        /// <summary>
        /// Turns one acquired block into one or more published frames.
        /// </summary>
        /// <remarks>
        /// The unoverlapped whole-block case is kept distinct rather than expressed as a
        /// degenerate extraction, because extracting would copy the block — at 2²⁰ points, 8 MB
        /// per acquisition to produce the frame the block already was.
        /// </remarks>
        private void AnalyseAndPublish(IqBlock block, Stopwatch clock, ref long previousTicks)
        {
            IqBlock zoomed = null;

            try
            {
                // REQ-DSP-023: zoom is a downconversion of the block that was acquired, applied
                // before anything else looks at it. Ahead of gating and the window because it
                // changes the sample rate, and every stage after it is sized in samples.
                DigitalDownconverter downconverter = Volatile.Read(ref _downconverter);

                if (downconverter != null &&
                    downconverter.OutputCountFor(block.SampleCount) > 0)
                {
                    zoomed = downconverter.Downconvert(block);
                    block = zoomed;
                }

                int record = _recordSamples > 0 ? _recordSamples : block.SampleCount;

                if (_overlap <= 0.0 && record >= block.SampleCount)
                {
                    Publish(_computer.Compute(block), record, clock, ref previousTicks);
                    return;
                }

                foreach (IqBlock cut in FrameExtraction.Extract(block, record, _overlap))
                {
                    using (cut)
                    {
                        Publish(_computer.Compute(cut), record, clock, ref previousTicks);
                    }
                }
            }
            finally
            {
                if (zoomed != null)
                {
                    zoomed.Dispose();
                }
            }
        }

        private void Publish(
            SpectrumFrame computed, int recordSamples, Stopwatch clock, ref long previousTicks)
        {
            SpectrumFrame frame = computed;
            TraceAverager averager = Averager;

            if (averager != null)
            {
                // The averager cannot know how the frames were cut, and REQ-DSP-031's effective
                // count depends on exactly that, so the pump tells it.
                averager.Overlap = _overlap;
                averager.RecordSamples = recordSamples;

                frame = averager.Accumulate(frame);
            }

            Interlocked.Increment(ref _framesComputed);
            UpdateRate(clock, ref previousTicks);

            EventHandler<SpectrumFrame> handler = FrameComputed;

            try
            {
                if (handler != null)
                {
                    handler(this, frame);
                }
            }
            finally
            {
                // REQ-NFR-002: the pump holds the reference the computer handed it, and gives it up
                // here. A handler that stores the frame past this call must Retain() -- and if it
                // does not, its next read throws rather than returning a later frame's spectrum.
                //
                // In the finally, so a throwing handler does not leak the buffer: the pump treats a
                // handler fault as fatal and stops, and a stopped pump that had also lost its
                // buffers would make the fault harder to diagnose than it already is.
                //
                // Two frames, because an averager returns a DIFFERENT frame from the one it was
                // given -- one that owns its own array -- and the computed frame's pooled buffer
                // would otherwise never come back. Release is a no-op on an unpooled frame, so the
                // averaging-off case, where the two are the same object, releases exactly once.
                if (!ReferenceEquals(frame, computed))
                {
                    frame.Release();
                }

                computed.Release();
            }
        }

        /// <summary>
        /// Exponentially smoothed frame rate.
        /// </summary>
        /// <remarks>
        /// Smoothed because an instantaneous figure from one frame interval is unreadable — it
        /// jitters by tens of percent frame to frame — and a status bar that flickers is one nobody
        /// reads. The coefficient gives a time constant of roughly ten frames.
        /// </remarks>
        private void UpdateRate(Stopwatch clock, ref long previousTicks)
        {
            long now = clock.ElapsedTicks;

            if (previousTicks >= 0)
            {
                double seconds = (now - previousTicks) / (double)Stopwatch.Frequency;

                if (seconds > 0.0)
                {
                    double instantaneous = 1.0 / seconds;
                    double smoothed = Volatile.Read(ref _measuredUpdatesPerSecond);
                    Volatile.Write(
                        ref _measuredUpdatesPerSecond,
                        smoothed <= 0.0 ? instantaneous : smoothed + 0.1 * (instantaneous - smoothed));
                }
            }

            previousTicks = now;
        }

        /// <summary>
        /// Waits out the remainder of the frame period, if there is one.
        /// </summary>
        /// <remarks>
        /// The wait is computed against the run clock rather than accumulated from the last frame,
        /// so a slow frame is absorbed instead of pushing every subsequent one late.
        /// </remarks>
        private async Task PaceAsync(Stopwatch clock, CancellationToken ct)
        {
            double target = TargetUpdatesPerSecond;

            if (target > 0.0)
            {
                double periodMs = 1000.0 / target;
                int waitMs = (int)Math.Round(periodMs - clock.Elapsed.TotalMilliseconds % periodMs);

                if (waitMs > 0)
                {
                    await Task.Delay(waitMs, ct).ConfigureAwait(false);
                    return;
                }
            }

            // Unbounded, or already late: still yield, so that a synchronous front end cannot turn
            // the pump into a loop the thread pool never gets a scheduling point out of.
            await Task.Yield();
        }

        private void RaiseCompleted()
        {
            EventHandler handler = Completed;
            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }
        }

        /// <summary>Stops the pump and releases its cancellation source. The front end is not disposed.</summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            CancellationTokenSource cancellation = _cancellation;
            if (cancellation != null)
            {
                cancellation.Cancel();
                cancellation.Dispose();
                _cancellation = null;
            }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(SpectrumEngine));
            }
        }
    }
}
