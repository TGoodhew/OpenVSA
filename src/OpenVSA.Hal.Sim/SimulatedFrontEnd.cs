using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using OpenVSA.Core;

namespace OpenVSA.Hal.Sim
{
    /// <summary>
    /// Settings for the synthetic source.
    /// </summary>
    /// <remarks>
    /// Presently a complex tone plus AWGN — enough to exercise the acquisition path, the spectrum
    /// chain and the plot surface. The modulated formats of <c>REQ-SIM-001</c> and the full
    /// impairment set of <c>REQ-SIM-002</c> are added alongside the demodulator, since their
    /// acceptance criteria are stated in terms of what the demodulator recovers and cannot be
    /// verified before it exists.
    /// </remarks>
    public sealed class SimulatedSignalSettings
    {
        /// <summary>Tone offset from centre frequency, in hertz. Default 0.</summary>
        public double ToneOffsetHz { get; set; }

        /// <summary>Tone amplitude in volts peak, referred to the input. Default 1.</summary>
        public double AmplitudeVolts { get; set; } = 1.0;

        /// <summary>Initial carrier phase, in radians. Default 0.</summary>
        public double PhaseRadians { get; set; }

        /// <summary>
        /// Signal-to-noise ratio in dB. <see cref="double.PositiveInfinity"/> means no noise,
        /// which is the default so that a clean reference signal is the easy thing to ask for.
        /// </summary>
        public double SnrDb { get; set; } = double.PositiveInfinity;

        /// <summary>Seed for all stochastic elements (<c>REQ-SIM-003</c>). Default 0.</summary>
        public long Seed { get; set; }
    }

    /// <summary>
    /// A front end that synthesises IQ instead of acquiring it.
    /// </summary>
    /// <remarks>
    /// <c>REQ-SIM-001</c>, and the practical basis for <c>REQ-NFR-032</c>: with this present the
    /// whole analysis stack runs with no hardware and no VISA installed, which is what makes the
    /// DSP developable and testable in CI. The specification calls that an architectural
    /// constraint rather than a convenience, and this class is where it is honoured.
    /// </remarks>
    [FrontEndProvider("Simulated source")]
    public sealed class SimulatedFrontEnd : IFrontEnd
    {
        private readonly SimulatedSignalSettings _settings;
        private DeterministicRandom _random;
        private AcquisitionPlan _plan;
        private long _sequenceNumber;
        private double _phaseAccumulator;
        private bool _disposed;

        /// <summary>Creates a simulated front end with default signal settings.</summary>
        /// <remarks>
        /// Explicit, not an optional parameter on the constructor below. A constructor with all
        /// parameters optional is <em>not</em> a parameterless constructor to reflection:
        /// <c>GetConstructor(Type.EmptyTypes)</c> returns null for it and
        /// <c>Activator.CreateInstance(type)</c> throws. Without this the simulator would compile,
        /// pass every direct test, and then be silently rejected by <c>FrontEndRegistry</c> as
        /// having no usable constructor.
        /// </remarks>
        public SimulatedFrontEnd()
            : this(null)
        {
        }

        /// <summary>Creates a simulated front end.</summary>
        /// <param name="settings">Signal settings, or <c>null</c> for the defaults.</param>
        public SimulatedFrontEnd(SimulatedSignalSettings settings)
        {
            _settings = settings ?? new SimulatedSignalSettings();
            _random = new DeterministicRandom(_settings.Seed);
            Id = new FrontEndId("sim");
            Capabilities = new SimulatedCapabilities();
        }

        /// <inheritdoc />
        public FrontEndId Id { get; }

        /// <inheritdoc />
        public string DisplayName => "Simulated source";

        /// <inheritdoc />
        public IFrontEndCapabilities Capabilities { get; }

        /// <inheritdoc />
        public FrontEndState State { get; private set; } = FrontEndState.Disconnected;

        /// <inheritdoc />
        public event EventHandler<FrontEndEvent> Notification;

        /// <inheritdoc />
        public Task ConnectAsync(CancellationToken ct)
        {
            ThrowIfDisposed();
            ct.ThrowIfCancellationRequested();
            State = FrontEndState.Connected;
            return Task.FromResult(true);
        }

        /// <inheritdoc />
        public Task DisconnectAsync()
        {
            ThrowIfDisposed();
            State = FrontEndState.Disconnected;
            return Task.FromResult(true);
        }

        /// <summary>
        /// Validates and coerces a request. Pure: sends nothing, changes nothing.
        /// </summary>
        /// <param name="request">What the user asked for.</param>
        /// <returns>The plan this front end would honour.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="request"/> is <c>null</c>.</exception>
        public AcquisitionPlan Negotiate(AcquisitionRequest request)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            // No ThrowIfDisposed and no state check: REQ-HAL-001 makes this a pure function, so it
            // must be callable on a disconnected front end to discover what is achievable.
            var coercions = new List<ParameterCoercion>();
            IFrontEndCapabilities caps = Capabilities;

            double centre = request.CenterFrequencyHz;
            if (!caps.CenterFrequencyRange.Contains(centre))
            {
                double honoured = caps.CenterFrequencyRange.Clamp(centre);
                coercions.Add(new ParameterCoercion(
                    "CenterFrequency", centre, honoured,
                    "outside the front-end's centre-frequency range"));
                centre = honoured;
            }

            double span = request.SpanHz;
            if (span > caps.MaxSpanHz)
            {
                coercions.Add(new ParameterCoercion(
                    "Span", span, caps.MaxSpanHz, "exceeds front-end maximum span"));
                span = caps.MaxSpanHz;
            }
            else if (span < caps.MinSpanHz)
            {
                coercions.Add(new ParameterCoercion(
                    "Span", span, caps.MinSpanHz, "below front-end minimum span"));
                span = caps.MinSpanHz;
            }

            // The path decides the sample-rate law: 1.28 x span on the complex path, 2.56 x span on
            // the real one (REQ-ACQ-001). A front end that cannot deliver real baseband coerces the
            // path rather than quietly acquiring the other one.
            AnalysisPath path = request.Path;

            if (path == AnalysisPath.RealBaseband && !caps.SupportsBasebandIq)
            {
                coercions.Add(new ParameterCoercion(
                    "Path", (double)AnalysisPath.RealBaseband, (double)AnalysisPath.ComplexZoom,
                    "this front end has no real-baseband path"));
                path = AnalysisPath.ComplexZoom;
            }

            // Unlike an instrument with a quantised, RBW-driven rate, the simulator can honour the
            // law exactly - which is precisely why the HAL negotiates rather than assuming a single
            // law for all sources, and why every consumer reads the rate back from the plan.
            double sampleRate = AcquisitionLaw.SampleRateFor(span, path);
            if (sampleRate > caps.MaxSampleRateHz)
            {
                double honouredSpan = AcquisitionLaw.SpanFor(caps.MaxSampleRateHz, path);
                coercions.Add(new ParameterCoercion(
                    "SampleRate", sampleRate, caps.MaxSampleRateHz,
                    "span implies a sample rate above the front-end maximum"));
                sampleRate = caps.MaxSampleRateHz;
                span = honouredSpan;
            }

            int samples = request.SamplesPerBlock;
            if (samples > caps.MaxSamplesPerBlock)
            {
                coercions.Add(new ParameterCoercion(
                    "SamplesPerBlock", samples, caps.MaxSamplesPerBlock,
                    "exceeds front-end maximum block size"));
                samples = caps.MaxSamplesPerBlock;
            }

            double refLevel = request.ReferenceLevelDbm;
            if (!caps.ReferenceLevelRange.Contains(refLevel))
            {
                double honoured = caps.ReferenceLevelRange.Clamp(refLevel);
                coercions.Add(new ParameterCoercion(
                    "ReferenceLevel", refLevel, honoured,
                    "outside the front-end's reference-level range"));
                refLevel = honoured;
            }

            // REQ-NFR-027: computed per plan, never hard-coded. A synthetic source has no transport
            // between it and the analysis chain, so it is always gap-free — but it is stated as a
            // computed property of the plan so no caller learns to special-case the source type.
            const bool gapFree = true;

            return new AcquisitionPlan(
                centre, span, sampleRate, samples, refLevel, gapFree, coercions, path);
        }

        /// <inheritdoc />
        public Task ConfigureAsync(AcquisitionPlan plan, CancellationToken ct)
        {
            ThrowIfDisposed();
            ct.ThrowIfCancellationRequested();

            if (plan == null)
            {
                throw new ArgumentNullException(nameof(plan));
            }

            if (State == FrontEndState.Disconnected)
            {
                throw new InvalidOperationException("Connect before configuring.");
            }

            _plan = plan;

            // Reset the stochastic stream and the phase so that a configured-then-acquired sequence
            // is reproducible from the seed alone (REQ-SIM-003).
            _random = new DeterministicRandom(_settings.Seed);
            _phaseAccumulator = _settings.PhaseRadians;
            _sequenceNumber = 0;

            State = FrontEndState.Configured;
            return Task.FromResult(true);
        }

        /// <inheritdoc />
        public Task ArmAsync(CancellationToken ct)
        {
            ThrowIfDisposed();
            ct.ThrowIfCancellationRequested();

            if (State != FrontEndState.Configured && State != FrontEndState.Acquiring)
            {
                throw new InvalidOperationException("Configure before arming.");
            }

            State = FrontEndState.Armed;
            return Task.FromResult(true);
        }

        /// <inheritdoc />
        public Task<IqBlock> AcquireNextAsync(CancellationToken ct)
        {
            ThrowIfDisposed();
            ct.ThrowIfCancellationRequested();

            if (State != FrontEndState.Armed && State != FrontEndState.Acquiring)
            {
                throw new InvalidOperationException("Arm before acquiring.");
            }

            State = FrontEndState.Acquiring;

            AcquisitionPlan plan = _plan;
            var metadata = new IqBlockMetadata(
                sampleCount: plan.SamplesPerBlock,
                sampleRateHz: plan.SampleRateHz,

                // On the real path the acquisition is from 0 Hz, whatever centre frequency was
                // asked for: a baseband digitiser has no local oscillator to tune.
                centerFrequencyHz: plan.Path == AnalysisPath.RealBaseband ? 0.0 : plan.CenterFrequencyHz,
                isBaseband: plan.Path == AnalysisPath.RealBaseband,
                fullScaleVolts: 1.0,
                referenceLevelDbm: plan.ReferenceLevelDbm,
                sequenceNumber: _sequenceNumber++,
                acquiredUtc: DateTime.UtcNow,
                triggerOffsetSeconds: 0.0,

                // The simulator applies no trigger delay, so there is nothing to correct for and
                // nothing is silently lost. REQ-DAT-002 wants the fact recorded either way.
                triggerCorrectionsApplied: true,
                source: Id,
                extended: null);

            IqBlock block = IqBlock.Rent(metadata);
            try
            {
                Fill(block.GetSamples(), plan);
            }
            catch
            {
                block.Dispose();
                throw;
            }

            return Task.FromResult(block);
        }

        private void Fill(Span<float> samples, AcquisitionPlan plan)
        {
            int count = plan.SamplesPerBlock;
            double phaseStep = 2.0 * Math.PI * _settings.ToneOffsetHz / plan.SampleRateHz;
            double amplitude = _settings.AmplitudeVolts;

            // SNR is per-component: a complex tone of amplitude A has power A^2/2, and the noise is
            // split equally between I and Q, so each component gets sigma = A / sqrt(2 * 2 * SNR).
            bool addNoise = !double.IsPositiveInfinity(_settings.SnrDb);
            double sigma = 0.0;
            if (addNoise)
            {
                double snrLinear = Math.Pow(10.0, _settings.SnrDb / 10.0);
                double signalPower = amplitude * amplitude / 2.0;
                sigma = Math.Sqrt(signalPower / snrLinear / 2.0);
            }

            // On the real path there is no quadrature channel: the signal is a real cosine and Q is
            // zero. The tone is placed at the same frequency the complex path would have put it,
            // measured from 0 Hz rather than from a centre frequency.
            bool real = plan.Path == AnalysisPath.RealBaseband;

            if (real)
            {
                phaseStep = 2.0 * Math.PI * (plan.SpanHz / 4.0 + _settings.ToneOffsetHz) / plan.SampleRateHz;
            }

            for (int n = 0; n < count; n++)
            {
                double i = amplitude * Math.Cos(_phaseAccumulator);
                double q = real ? 0.0 : amplitude * Math.Sin(_phaseAccumulator);

                if (addNoise)
                {
                    i += sigma * _random.NextGaussian();

                    if (!real)
                    {
                        q += sigma * _random.NextGaussian();
                    }
                }

                samples[n * 2] = (float)i;
                samples[n * 2 + 1] = (float)q;

                _phaseAccumulator += phaseStep;
            }

            // Keep the accumulator bounded so a long run does not lose phase resolution to
            // floating-point magnitude — an error that would appear as a slow frequency drift.
            _phaseAccumulator = Math.IEEERemainder(_phaseAccumulator, 2.0 * Math.PI);
        }

        /// <inheritdoc />
        public Task AbortAsync()
        {
            ThrowIfDisposed();

            if (State == FrontEndState.Acquiring || State == FrontEndState.Armed)
            {
                State = FrontEndState.Configured;
            }

            return Task.FromResult(true);
        }

        /// <summary>Raises a notification, for tests and for future impairment events.</summary>
        /// <param name="e">The event to raise.</param>
        private void OnNotification(FrontEndEvent e) => Notification?.Invoke(this, e);

        /// <inheritdoc />
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            State = FrontEndState.Disconnected;
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(SimulatedFrontEnd));
            }
        }

        private sealed class SimulatedCapabilities : IFrontEndCapabilities
        {
            private static readonly IReadOnlyList<TriggerStyle> Styles =
                new List<TriggerStyle> { TriggerStyle.Immediate, TriggerStyle.Level }.AsReadOnly();

            public FrequencyRange CenterFrequencyRange => new FrequencyRange(0.0, 26.5e9);
            public double MaxSpanHz => 40e6;
            public double MinSpanHz => 1.0;
            public double MaxSampleRateHz => 51.2e6;
            public int MaxSamplesPerBlock => 1 << 22;
            public long MaxCaptureSamples => 1L << 32;
            public bool SupportsBasebandIq => true;
            public int ChannelCount => 1;
            public bool SupportsPhaseCoherentChannels => false;
            public IReadOnlyList<TriggerStyle> TriggerStyles => Styles;
            public AmplitudeRange ReferenceLevelRange => new AmplitudeRange(-100.0, 30.0);
            public bool SupportsExternalRef => false;
        }
    }
}
