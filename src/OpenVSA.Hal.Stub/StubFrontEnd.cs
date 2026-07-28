using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using OpenVSA.Core;
using OpenVSA.Hal;

namespace OpenVSA.Hal.Stub
{
    /// <summary>
    /// A front end that produces silence, standing in for every real transport
    /// (<c>REQ-ARC-001</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>REQ-ARC-001</c>'s criterion is that the solution builds with <c>OpenVSA.Hal.Visa</c>,
    /// <c>.File</c> and <c>.Sim</c> removed, substituting a single stub front end, and that the DSP
    /// and measurement tests pass unchanged. This is that stub, and the point of it is what it does
    /// <em>not</em> reference: the HAL interface assembly and nothing else.
    /// </para>
    /// <para>
    /// <strong>Not a simulator.</strong> <c>OpenVSA.Hal.Sim</c> generates a signal and is a
    /// measurement source in its own right; this generates zeros and exists so that the analysis
    /// stack can be built and run with no acquisition code present at all. Anything more capable
    /// would weaken the proof, because the question is whether L3 and above can be built without a
    /// transport, not whether they work with a convenient one.
    /// </para>
    /// </remarks>
    public sealed class StubFrontEnd : IFrontEnd
    {
        private AcquisitionPlan _plan;
        private long _sequence;

        /// <inheritdoc />
        public FrontEndId Id => new FrontEndId("stub");

        /// <inheritdoc />
        public string DisplayName => "Stub source (acquires nothing)";

        /// <inheritdoc />
        public IFrontEndCapabilities Capabilities { get; } = new StubCapabilities();

        /// <inheritdoc />
        public FrontEndState State { get; private set; } = FrontEndState.Disconnected;

        /// <inheritdoc />
        public event EventHandler<FrontEndEvent> Notification;

        /// <inheritdoc />
        public Task ConnectAsync(CancellationToken ct)
        {
            State = FrontEndState.Connected;
            return Task.FromResult(true);
        }

        /// <inheritdoc />
        public Task DisconnectAsync()
        {
            State = FrontEndState.Disconnected;
            return Task.FromResult(true);
        }

        /// <inheritdoc />
        /// <remarks>
        /// Coerces nothing and says so honestly: a stub that silently accepted an impossible span
        /// would make the negotiation contract look weaker than it is.
        /// </remarks>
        public AcquisitionPlan Negotiate(AcquisitionRequest request)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            var coercions = new List<ParameterCoercion>();
            double span = request.SpanHz;

            if (span > Capabilities.MaxSpanHz)
            {
                coercions.Add(new ParameterCoercion(
                    "Span", span, Capabilities.MaxSpanHz, "exceeds the stub's maximum span"));

                span = Capabilities.MaxSpanHz;
            }

            return new AcquisitionPlan(
                request.CenterFrequencyHz,
                span,
                span * 1.28,
                request.SamplesPerBlock,
                request.ReferenceLevelDbm,
                supportsGapFreeStreaming: false,
                coercions: coercions);
        }

        /// <inheritdoc />
        public Task ConfigureAsync(AcquisitionPlan plan, CancellationToken ct)
        {
            _plan = plan ?? throw new ArgumentNullException(nameof(plan));
            State = FrontEndState.Configured;
            return Task.FromResult(true);
        }

        /// <inheritdoc />
        public Task ArmAsync(CancellationToken ct)
        {
            State = FrontEndState.Armed;
            return Task.FromResult(true);
        }

        /// <inheritdoc />
        /// <remarks>
        /// A block of zeros, correctly shaped and stamped. Silence rather than a signal, because
        /// the stub's job is to let the analysis stack run, not to give it something to find.
        /// </remarks>
        public Task<IqBlock> AcquireNextAsync(CancellationToken ct)
        {
            if (_plan == null)
            {
                throw new InvalidOperationException("Configure before acquiring.");
            }

            ct.ThrowIfCancellationRequested();

            State = FrontEndState.Acquiring;

            var metadata = new IqBlockMetadata(
                _plan.SamplesPerBlock,
                _plan.SampleRateHz,
                _plan.CenterFrequencyHz,
                isBaseband: false,
                fullScaleVolts: 1.0,
                referenceLevelDbm: _plan.ReferenceLevelDbm,
                sequenceNumber: Interlocked.Increment(ref _sequence),
                acquiredUtc: DateTime.UtcNow,
                triggerOffsetSeconds: 0.0,
                triggerCorrectionsApplied: false,
                source: Id,
                extended: null);

            // Rented and already cleared: IqBlock.Rent zeroes the region it exposes.
            return Task.FromResult(IqBlock.Rent(metadata));
        }

        /// <inheritdoc />
        public Task AbortAsync()
        {
            State = FrontEndState.Connected;
            return Task.FromResult(true);
        }

        /// <summary>Raises a notification, so the event is not merely declared.</summary>
        /// <param name="notification">What happened.</param>
        public void Notify(FrontEndEvent notification) =>
            Notification?.Invoke(this, notification);

        /// <inheritdoc />
        /// <remarks>Nothing to release; the state is moved so a disposed stub reads as disconnected.</remarks>
        public void Dispose()
        {
            State = FrontEndState.Disconnected;
            _plan = null;
        }

        /// <summary>Ranges wide enough that nothing under test is coerced by accident.</summary>
        private sealed class StubCapabilities : IFrontEndCapabilities
        {
            private static readonly IReadOnlyList<TriggerStyle> Styles =
                new ReadOnlyCollection<TriggerStyle>(new[] { TriggerStyle.Immediate });

            public FrequencyRange CenterFrequencyRange => new FrequencyRange(0.0, 26.5e9);

            public double MaxSpanHz => 40.0e6;

            public double MinSpanHz => 1.0;

            public double MaxSampleRateHz => 51.2e6;

            public int MaxSamplesPerBlock => 1 << 21;

            public long MaxCaptureSamples => 1L << 28;

            public bool SupportsBasebandIq => true;

            public int ChannelCount => 1;

            public bool SupportsPhaseCoherentChannels => false;

            public IReadOnlyList<TriggerStyle> TriggerStyles => Styles;

            public AmplitudeRange ReferenceLevelRange => new AmplitudeRange(-100.0, 30.0);

            public bool SupportsExternalRef => false;

            public bool SupportsRealTimeAnalysis => false;

            public bool SupportsInputRangeControl => false;

            public long MaxPreTriggerSamples => 0L;
        }
    }
}
