using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using OpenVSA.Core;
using OpenVSA.Core.Threading;
using OpenVSA.Dsp.Spectrum;
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

                    SpectrumFrame frame;
                    using (block)
                    {
                        frame = _computer.Compute(block);
                    }

                    Interlocked.Increment(ref _framesComputed);
                    UpdateRate(clock, ref previousTicks);

                    EventHandler<SpectrumFrame> handler = FrameComputed;
                    if (handler != null)
                    {
                        handler(this, frame);
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
