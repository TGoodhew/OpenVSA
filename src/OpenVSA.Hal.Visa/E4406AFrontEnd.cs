using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using OpenVSA.Core;

namespace OpenVSA.Hal.Visa
{
    /// <summary>
    /// An I/Q front end backed by an Agilent E4406A VSA transmitter tester over VISA.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The instrument's Basic-mode waveform measurement returns, at <c>n=0</c>, "unprocessed I/Q
    /// trace data … in volts. The I values are listed first in each pair, using the 0 and
    /// even-indexed values" — which is <see cref="IqBlock"/>'s layout exactly, and in volts, so
    /// the block declares a full scale of 1 V and the amplitude chain needs no instrument-specific
    /// term.
    /// </para>
    /// <para>
    /// <strong>Nothing here assumes a relationship between span and sample rate.</strong> The
    /// information bandwidth is set, and the sample period is then <em>asked for</em>
    /// (<c>:WAVeform:APERture?</c>), because on this instrument the two are related by its own
    /// decimation and filter type rather than by the product's 1.28 law. Every block carries the
    /// rate the instrument reported, so the display's frequency axis is right whatever that
    /// relationship turns out to be.
    /// </para>
    /// <para>
    /// <strong>The instrument is asked for its own limits.</strong> Centre-frequency, bandwidth,
    /// capture-length and input-range limits come from <c>MIN</c>/<c>MAX</c> queries at connect,
    /// not from the datasheet, so an instrument with different options installed reports what it
    /// actually has.
    /// </para>
    /// </remarks>
    [FrontEndProvider("Agilent E4406A (VSA, I/Q over VISA)")]
    public sealed class E4406AFrontEnd : IFrontEnd
    {
        /// <summary>Resource used when configuration names none.</summary>
        /// <remarks>
        /// Configuration, never a bus scan. On a bench with HP-IB extenders every address answers
        /// a scan whether an instrument is there or not, so discovery would report a full bus of
        /// imaginary equipment.
        /// </remarks>
        public const string DefaultResource = "GPIB0::17::INSTR";

        /// <summary><c>appSettings</c> key naming the VISA resource to open.</summary>
        public const string ResourceSettingKey = "OpenVSA.Visa.E4406A.Resource";

        /// <summary>
        /// Most complex samples this front end will ask for in one block.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <strong>A declared capture depth has to mean "can actually be delivered".</strong> The
        /// instrument reports a maximum sweep time of 100 s, which at its sample rate is over a
        /// billion samples — arithmetically true and useless as a block size. Declaring it let the
        /// settings pane offer every point count on <c>REQ-DSP-022</c>'s ladder, and choosing one
        /// near the top asked for a transfer that could not finish: the application appeared to
        /// lock up while a multi-megabyte block crawled over GPIB behind a ten-second timeout.
        /// </para>
        /// <para>
        /// A hard ceiling only. The real bound is <see cref="MaximumBlockSeconds"/>, measured
        /// against the instrument at connect, because how fast a block arrives is a property of
        /// the interface and the cabling rather than of the model — this bench manages about
        /// 20 kB/s, where a direct GPIB card manages fifty times that.
        /// </para>
        /// </remarks>
        public const int MaximumTransferSamples = 1 << 17;

        /// <summary>
        /// Longest a single block may take to transfer, in seconds, when sizing the capture.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Measured against this bench: 32 768 samples took 13.4 s and 65 536 timed out, both
        /// offered by a settings list built from a capture depth that only counted what the
        /// instrument could <em>digitise</em>. Ten seconds is already a long time to wait for one
        /// frame; it is here as the point past which a measurement is no longer interactive, not as
        /// a performance target.
        /// </para>
        /// <para>
        /// Deeper captures belong to the recording path of <c>REQ-REC-001</c>, which streams rather
        /// than returning one block.
        /// </para>
        /// </remarks>
        public const double MaximumBlockSeconds = 10.0;

        /// <summary>Samples used to measure the instrument's transfer rate at connect.</summary>
        private const int ThroughputProbeSamples = 1024;

        private readonly Func<string, IInstrumentSession> _openSession;
        private readonly string _resourceName;
        private readonly List<string> _sent = new List<string>();

        private IInstrumentSession _session;
        private InstrumentLimits _capabilities;
        private AcquisitionPlan _plan;
        private double _sampleRateHz;
        private double _actualBandwidthHz;
        private string _priorMode;
        private double _priorCenterFrequencyHz;
        private long _sequenceNumber;
        private float[] _scratch;
        private bool _disposed;

        /// <summary>Creates a front end that opens a real VISA session to the configured resource.</summary>
        /// <remarks>
        /// Parameterless, because <c>FrontEndRegistry</c> constructs providers by reflection and a
        /// constructor with all-optional parameters is not a parameterless constructor to it.
        /// </remarks>
        public E4406AFrontEnd()
            : this(VisaConfiguration.ResourceFor(ResourceSettingKey, DefaultResource), null)
        {
        }

        /// <summary>Creates a front end over a given resource and session factory.</summary>
        /// <param name="resourceName">VISA resource string.</param>
        /// <param name="openSession">Opens a session, or <c>null</c> to open a real VISA one.</param>
        /// <exception cref="ArgumentException"><paramref name="resourceName"/> is missing.</exception>
        public E4406AFrontEnd(string resourceName, Func<string, IInstrumentSession> openSession)
        {
            if (string.IsNullOrEmpty(resourceName))
            {
                throw new ArgumentException("A VISA resource name is required.", nameof(resourceName));
            }

            _resourceName = resourceName;
            _openSession = openSession ?? (resource => VisaSession.Open(resource));
            Id = new FrontEndId("e4406a:" + resourceName);
        }

        /// <inheritdoc />
        public FrontEndId Id { get; }

        /// <inheritdoc />
        public string DisplayName { get; private set; } = "Agilent E4406A";

        /// <inheritdoc />
        public IFrontEndCapabilities Capabilities => _capabilities;

        /// <inheritdoc />
        public FrontEndState State { get; private set; } = FrontEndState.Disconnected;

        /// <inheritdoc />
        public event EventHandler<FrontEndEvent> Notification;

        /// <summary>Commands sent since construction, for diagnostics and for asserting order.</summary>
        public IReadOnlyList<string> Sent
        {
            get { lock (_sent) { return _sent.ToArray(); } }
        }

        /// <summary>The information bandwidth the instrument reported it was actually using, in hertz.</summary>
        public double ActualBandwidthHz => _actualBandwidthHz;

        /// <summary>The sample rate the instrument reported, in hertz.</summary>
        public double SampleRateHz => _sampleRateHz;

        /// <summary>
        /// The option codes the instrument reports, which are its installed personalities
        /// (<c>REQ-E44-001</c>).
        /// </summary>
        /// <remarks>
        /// Read but not acted on. They say which measurement personalities the instrument carries —
        /// GSM, EDGE, cdmaOne, W-CDMA, baseband I/Q — and OpenVSA uses none of them: it takes raw
        /// I/Q from Basic mode and does its own analysis. They are surfaced because knowing what a
        /// borrowed instrument has is worth a query, and because a personality-specific capability
        /// would have to come from here rather than from a model name.
        /// </remarks>
        public IReadOnlyList<string> InstalledOptions { get; private set; } = new string[0];

        /// <inheritdoc />
        public Task ConnectAsync(CancellationToken ct)
        {
            ThrowIfDisposed();
            ct.ThrowIfCancellationRequested();

            IInstrumentSession session = _openSession(_resourceName);

            try
            {
                // Take the instrument as found, not as hoped. A device clear abandons any transfer
                // in progress and empties the output queue; *CLS then clears the status registers.
                // Without this, a previous program that left a query unread makes the first command
                // here fail with -410, and the error names this driver rather than the cause.
                session.Clear();
                Send(session, E4406ACommands.ClearStatus);

                string identity = Send(session, E4406ACommands.Identify, query: true);
                DisplayName = identity;
                RequireModel(identity);

                InstalledOptions = ParseOptions(
                    Send(session, E4406ACommands.Options, query: true));

                // Recorded before anything is changed, so the instrument can be handed back as it
                // was found. It is somebody's bench, and a driver that leaves it in a mode nobody
                // chose is a driver people stop running.
                _priorMode = Send(session, E4406ACommands.SelectedMode, query: true);
                _priorCenterFrequencyHz = QueryDouble(session, ":SENSe:FREQuency:CENTer?");

                Send(session, E4406ACommands.SelectBasicMode);
                Send(session, E4406ACommands.ConfigureWaveform);
                Send(session, E4406ACommands.BinaryFormat);
                Send(session, E4406ACommands.SwapByteOrder);
                Send(session, E4406ACommands.SingleMeasurement);
                Send(session, E4406ACommands.FlatTopFilter);
                Send(session, E4406ACommands.AveragingOff);
                Send(session, E4406ACommands.AutoAdcRange);
                Send(session, E4406ACommands.DisableDisplay);

                _capabilities = ProbeCapabilities(session);
                ThrowOnInstrumentError(session, "connecting");
            }
            catch (Exception failure)
            {
                session.Dispose();

                // Named, with the resource and how to change it. The bare transport exception says
                // only that something timed out, and the first thing anyone needs to know is which
                // address was tried - especially on a bus where every address answers a scan.
                throw new InvalidOperationException(
                    "No instrument answered at '" + _resourceName + "'. " + failure.Message +
                    Environment.NewLine + Environment.NewLine +
                    "Check the instrument is switched on and at that address, then set '" +
                    ResourceSettingKey + "' in the application configuration, or the " +
                    VisaConfiguration.EnvironmentVariableFor(ResourceSettingKey) +
                    " environment variable. A remote VISA server is addressed as " +
                    "visa://host/GPIB0::18::INSTR.",
                    failure);
            }

            _session = session;
            State = FrontEndState.Connected;

            return Task.FromResult(true);
        }

        /// <inheritdoc />
        public Task DisconnectAsync()
        {
            IInstrumentSession session = _session;
            _session = null;
            State = FrontEndState.Disconnected;

            if (session != null)
            {
                try
                {
                    // Handed back as it was found: the display on, and the mode and centre
                    // frequency somebody had set before OpenVSA took the instrument over.
                    Send(session, E4406ACommands.EnableDisplay);

                    if (!string.IsNullOrEmpty(_priorMode))
                    {
                        Send(session, E4406ACommands.SelectMode(_priorMode));
                    }

                    if (_priorCenterFrequencyHz > 0.0)
                    {
                        Send(session, E4406ACommands.SetCenterFrequency(_priorCenterFrequencyHz));
                    }
                }
                catch (Exception)
                {
                    // Disconnecting must not fail because the instrument has already gone.
                }

                session.Dispose();
            }

            return Task.FromResult(true);
        }

        /// <summary>
        /// Validates and coerces a request. Pure: sends nothing, changes nothing.
        /// </summary>
        /// <param name="request">What the user asked for.</param>
        /// <returns>The plan this instrument would honour.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="request"/> is null.</exception>
        /// <exception cref="InvalidOperationException">The instrument has not been connected, so its limits are unknown.</exception>
        /// <remarks>
        /// <c>REQ-HAL-001</c> makes this pure, so it works from the limits cached at connect. The
        /// sample rate it states is this instrument's own figure for the bandwidth being asked for,
        /// scaled from the aperture measured at connect — and it is only an estimate, which is why
        /// the rate that reaches the analysis chain is the one each block carries.
        /// </remarks>
        public AcquisitionPlan Negotiate(AcquisitionRequest request)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            InstrumentLimits caps = _capabilities;

            if (caps == null)
            {
                throw new InvalidOperationException(
                    "Connect before negotiating: this front end takes its limits from the " +
                    "instrument rather than from a datasheet.");
            }

            var coercions = new List<ParameterCoercion>();

            double centre = request.CenterFrequencyHz;
            if (!caps.CenterFrequencyRange.Contains(centre))
            {
                double honoured = caps.CenterFrequencyRange.Clamp(centre);
                coercions.Add(new ParameterCoercion(
                    "CenterFrequency", centre, honoured,
                    "outside this instrument's tuning range"));
                centre = honoured;
            }

            double span = request.SpanHz;
            if (span > caps.MaxSpanHz)
            {
                coercions.Add(new ParameterCoercion(
                    "Span", span, caps.MaxSpanHz, "exceeds the instrument's information bandwidth"));
                span = caps.MaxSpanHz;
            }
            else if (span < caps.MinSpanHz)
            {
                coercions.Add(new ParameterCoercion(
                    "Span", span, caps.MinSpanHz, "below the instrument's minimum information bandwidth"));
                span = caps.MinSpanHz;
            }

            AnalysisPath path = request.Path;
            if (path == AnalysisPath.RealBaseband)
            {
                coercions.Add(new ParameterCoercion(
                    "Path", (double)AnalysisPath.RealBaseband, (double)AnalysisPath.ComplexZoom,
                    "this instrument digitises at IF and returns complex I/Q only"));
                path = AnalysisPath.ComplexZoom;
            }

            double sampleRate = caps.EstimateSampleRate(span);

            int samples = request.SamplesPerBlock;
            int maxSamples = caps.MaxSamplesFor(sampleRate);

            if (samples > maxSamples)
            {
                coercions.Add(new ParameterCoercion(
                    "SamplesPerBlock", samples, maxSamples,
                    "longer than the instrument's maximum capture at this bandwidth"));
                samples = maxSamples;
            }

            double refLevel = request.ReferenceLevelDbm;
            if (!caps.ReferenceLevelRange.Contains(refLevel))
            {
                double honoured = caps.ReferenceLevelRange.Clamp(refLevel);
                coercions.Add(new ParameterCoercion(
                    "ReferenceLevel", refLevel, honoured, "outside this instrument's input range"));
                refLevel = honoured;
            }

            // REQ-NFR-027: GPIB moves roughly a megabyte a second at best, and one block of
            // complex float32 is 8 bytes a sample. Whether that keeps up is a property of the plan,
            // computed here rather than asserted per instrument.
            bool gapFree = samples * 8.0 / (samples / sampleRate) <= GpibBytesPerSecond;

            return new AcquisitionPlan(
                centre, span, sampleRate, samples, refLevel, gapFree, coercions, path);
        }

        /// <summary>Nominal GPIB throughput, in bytes per second, for the gap-free estimate.</summary>
        /// <remarks>
        /// Deliberately conservative. <c>REQ-NFR-027</c> requires honest expectations rather than
        /// an optimistic figure that makes a measurement look sustainable when it is not.
        /// </remarks>
        private const double GpibBytesPerSecond = 1.0e6;

        /// <inheritdoc />
        public Task ConfigureAsync(AcquisitionPlan plan, CancellationToken ct)
        {
            ThrowIfDisposed();
            ct.ThrowIfCancellationRequested();

            if (plan == null)
            {
                throw new ArgumentNullException(nameof(plan));
            }

            IInstrumentSession session = RequireSession();

            Send(session, E4406ACommands.SetCenterFrequency(plan.CenterFrequencyHz));
            Send(session, E4406ACommands.SetBandwidth(plan.SpanHz));

            // Read back before computing the capture length: the instrument may not have honoured
            // the bandwidth, and the sample period follows from what it did honour.
            _actualBandwidthHz = QueryDouble(session, E4406ACommands.ActualBandwidth);
            double aperture = QueryDouble(session, E4406ACommands.Aperture);

            if (!(aperture > 0.0))
            {
                throw new InvalidOperationException(
                    "The instrument reported a sample period of " +
                    aperture.ToString("R", CultureInfo.InvariantCulture) +
                    " s, which cannot be used to form a frequency axis.");
            }

            _sampleRateHz = 1.0 / aperture;

            Send(session, E4406ACommands.SetSweepTime(plan.SamplesPerBlock * aperture));
            ThrowOnInstrumentError(session, "configuring");

            // The I/O timeout has to allow for the transfer this plan implies. A fixed timeout is
            // fine until a block is large enough to exceed it, and then it fails partway through a
            // read that was working — which is how a deep capture came to look like a fault.
            session.TimeoutMilliseconds = TimeoutForBlock(plan.SamplesPerBlock);

            ReportCoercions(plan);

            _plan = plan;
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

            IInstrumentSession session = RequireSession();
            State = FrontEndState.Acquiring;

            byte[] payload;
            double[] scalars;

            try
            {
                Record(E4406ACommands.ReadIqTrace);
                session.Write(E4406ACommands.ReadIqTrace);
                payload = session.ReadBinaryBlock();

                // The scalars of the acquisition just taken. FETCh, not READ, so they describe
                // this capture rather than a fresh one - and scalar 1 is where REQ-E44-002b
                // requires the sample interval to come from, because the instrument quantises it
                // to a multiple of 1/15 MHz and a requested rate is not generally the one
                // honoured. Inside the same try: a failure here leaves the session needing
                // recovery just as much as a failed trace read does.
                scalars = QueryScalars(session, E4406ACommands.FetchScalars);
            }
            catch (Exception)
            {
                // One failed transfer must not take the session with it: a read that timed out
                // leaves the response waiting and every command after it fails with -410, which
                // presents as the application locking up rather than as one dropped frame.
                Recover(session);
                State = FrontEndState.Configured;
                throw;
            }

            if (scalars.Length > E4406ACommands.SampleIntervalScalar)
            {
                double interval = scalars[E4406ACommands.SampleIntervalScalar];

                if (interval > 0.0)
                {
                    _sampleRateHz = 1.0 / interval;
                }
            }

            int values = payload.Length / 4;

            if (values < 2 || values % 2 != 0)
            {
                throw new InvalidOperationException(
                    "The instrument returned " + values + " values, which is not a whole number of " +
                    "I/Q pairs.");
            }

            if (_scratch == null || _scratch.Length < values)
            {
                _scratch = new float[values];
            }

            BinaryBlock.ToSingles(payload, _scratch);

            int sampleCount = values / 2;

            var metadata = new IqBlockMetadata(
                sampleCount: sampleCount,
                sampleRateHz: _sampleRateHz,
                centerFrequencyHz: _plan.CenterFrequencyHz,
                isBaseband: false,

                // The trace is already in volts, so full scale is one volt per unit and the
                // amplitude chain's V_fs term is 1 - see AmplitudeChain's remarks on the
                // fractions-of-full-scale convention.
                fullScaleVolts: 1.0,
                referenceLevelDbm: _plan.ReferenceLevelDbm,
                sequenceNumber: _sequenceNumber++,
                // REQ-ACQ-010: a monotonic clock, not DateTime.UtcNow, whose granularity is
                // longer than a block. Each transfer is a separate arm-and-read over the bus,
                // so the timeline is placed by the clock per block rather than counted on from
                // the last - there is a real gap between them and claiming otherwise would be
                // a fiction.
                acquiredUtc: AcquisitionClock.UtcNow,
                triggerOffsetSeconds: 0.0,

                // No trigger is applied, so there is no trigger delay to correct for and nothing
                // is silently lost. REQ-DAT-002 wants the fact recorded either way.
                triggerCorrectionsApplied: true,
                source: Id,
                extended: new Dictionary<string, object>
                {
                    { IqBlockMetadata.UsableBandwidthKey, _actualBandwidthHz },
                    { ResourceKey, _resourceName },
                });

            IqBlock block = IqBlock.Rent(metadata);

            try
            {
                Span<float> samples = block.GetSamples();

                for (int i = 0; i < sampleCount * 2; i++)
                {
                    samples[i] = _scratch[i];
                }
            }
            catch
            {
                block.Dispose();
                throw;
            }

            return Task.FromResult(block);
        }

        /// <summary>
        /// Extended-metadata key carrying the alias-free bandwidth of a block, in hertz.
        /// </summary>
        /// <remarks>
        /// The fraction of the sample rate that is usable is a property of the front end's
        /// decimation filter, not of the product. A consumer that wants to display only the
        /// alias-free part reads this; one that does not gets the whole Nyquist band, which is
        /// honest if wider than useful.
        /// </remarks>
        public const string UsableBandwidthKey = "UsableBandwidthHz";

        /// <summary>Extended-metadata key carrying the VISA resource a block came from.</summary>
        public const string ResourceKey = "VisaResource";

        /// <inheritdoc />
        public Task AbortAsync()
        {
            if (State == FrontEndState.Acquiring || State == FrontEndState.Armed)
            {
                State = FrontEndState.Configured;
            }

            return Task.FromResult(true);
        }

        /// <summary>Closes the session, restoring the instrument's display first.</summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            DisconnectAsync().GetAwaiter().GetResult();
        }

        private InstrumentLimits ProbeCapabilities(IInstrumentSession session)
        {
            double minCentre = QueryDouble(session, E4406ACommands.CenterFrequencyLimit(false));
            double maxCentre = QueryDouble(session, E4406ACommands.CenterFrequencyLimit(true));
            double minSpan = QueryDouble(session, E4406ACommands.BandwidthLimit(false));
            double maxSpan = QueryDouble(session, E4406ACommands.BandwidthLimit(true));
            double maxSweep = QueryDouble(session, E4406ACommands.SweepTimeLimit(true));

            // The sample period at the widest bandwidth, which is the only way to learn this
            // instrument's rate-to-bandwidth relationship without assuming one.
            Send(session, E4406ACommands.SetBandwidth(maxSpan));
            double apertureAtMaxSpan = QueryDouble(session, E4406ACommands.Aperture);

            double samplesPerSecond = MeasureTransferRate(session, apertureAtMaxSpan);

            return new InstrumentLimits(
                new FrequencyRange(minCentre, maxCentre),
                minSpan,
                maxSpan,
                apertureAtMaxSpan > 0.0 ? 1.0 / apertureAtMaxSpan : maxSpan,
                maxSweep,

                // The display reference level, not a commanded input range: this instrument
                // auto-ranges its attenuator in Basic mode, and its reference-level command is
                // documented as belonging to the other modes. The upper bound is the instrument's
                // own damage limit — "external attenuation required above 30 dBm".
                new AmplitudeRange(-100.0, 30.0),
                samplesPerSecond);
        }

        /// <summary>
        /// Times one small acquisition to learn how fast this instrument hands over samples.
        /// </summary>
        /// <param name="session">The open session.</param>
        /// <param name="aperture">Sample period currently in force, in seconds.</param>
        /// <returns>Samples per second of transfer, or 0 if it could not be measured.</returns>
        /// <remarks>
        /// <para>
        /// The one measurement that cannot be looked up. Transfer speed belongs to the interface
        /// and the cabling, not to the model: this bench manages about 2 400 samples a second over
        /// an extender, where a direct card manages far more. Sizing the capture from a datasheet
        /// figure is how a settings list comes to offer a block that takes a minute.
        /// </para>
        /// <para>
        /// Costs one short acquisition at connect. That is the point at which the user is already
        /// waiting for the instrument to answer, and it is paid once.
        /// </para>
        /// </remarks>
        private double MeasureTransferRate(IInstrumentSession session, double aperture)
        {
            if (!(aperture > 0.0))
            {
                return 0.0;
            }

            try
            {
                Send(session, E4406ACommands.SetSweepTime(ThroughputProbeSamples * aperture));

                var clock = System.Diagnostics.Stopwatch.StartNew();
                Record(E4406ACommands.ReadIqTrace);
                session.Write(E4406ACommands.ReadIqTrace);
                byte[] payload = session.ReadBinaryBlock();
                clock.Stop();

                int samples = payload.Length / 8;
                double seconds = clock.Elapsed.TotalSeconds;

                return samples > 0 && seconds > 0.0 ? samples / seconds : 0.0;
            }
            catch (Exception)
            {
                // Not fatal: without a measurement the capture is bounded by the hard ceiling
                // alone, which is the behaviour before this existed.
                Recover(session);
                return 0.0;
            }
        }

        /// <summary>
        /// I/O timeout for a block of a given size, in milliseconds.
        /// </summary>
        /// <param name="samples">Complex samples the block will carry.</param>
        /// <remarks>
        /// Three times the measured transfer time, floored at ten seconds. Generous because the
        /// cost of being wrong is asymmetric: a timeout that is too long delays noticing a dead
        /// instrument by seconds, while one that is too short aborts a transfer that was working
        /// and leaves the session needing recovery.
        /// </remarks>
        private int TimeoutForBlock(int samples)
        {
            const int floorMilliseconds = 10000;

            double rate = _capabilities == null ? 0.0 : _capabilities.SamplesPerSecond;

            if (!(rate > 0.0))
            {
                return floorMilliseconds;
            }

            double milliseconds = samples / rate * 3000.0;

            if (milliseconds < floorMilliseconds)
            {
                return floorMilliseconds;
            }

            return milliseconds > int.MaxValue ? int.MaxValue : (int)milliseconds;
        }

        /// <summary>
        /// Puts the session back in a usable state after an I/O failure.
        /// </summary>
        /// <remarks>
        /// A read that timed out leaves the instrument's response waiting, and every command after
        /// it earns <c>-410 Query INTERRUPTED</c>. Measured on this bench: one over-long transfer
        /// turned into a cascade of failures that looked like the application had locked up. A
        /// device clear costs milliseconds and confines the damage to the operation that failed.
        /// </remarks>
        private void Recover(IInstrumentSession session)
        {
            try
            {
                session.Clear();
                session.Write(E4406ACommands.ClearStatus);
            }
            catch (Exception)
            {
                // The session is beyond saving; the original failure is the one worth reporting.
            }
        }

        private void ReportCoercions(AcquisitionPlan plan)
        {
            if (_actualBandwidthHz > 0.0 &&
                Math.Abs(_actualBandwidthHz - plan.SpanHz) > plan.SpanHz * 1e-6)
            {
                // REQ-HAL-001: what the instrument actually did, said out loud. The plan was
                // negotiated against cached limits; only the instrument knows the rest.
                Raise(FrontEndEventKind.ParameterCoerced,
                    "Information bandwidth was set to " +
                    _actualBandwidthHz.ToString("G6", CultureInfo.CurrentCulture) +
                    " Hz rather than the requested " +
                    plan.SpanHz.ToString("G6", CultureInfo.CurrentCulture) + " Hz.");
            }
        }

        private IInstrumentSession RequireSession()
        {
            IInstrumentSession session = _session;

            if (session == null)
            {
                throw new InvalidOperationException("Not connected to the instrument.");
            }

            return session;
        }

        private string Send(IInstrumentSession session, string command, bool query = false)
        {
            Record(command);

            if (query || command.IndexOf('?') >= 0)
            {
                return session.Query(command);
            }

            session.Write(command);
            return null;
        }

        /// <summary>
        /// Reads a numeric result block from a query.
        /// </summary>
        /// <param name="session">The open session.</param>
        /// <param name="command">The query to send.</param>
        /// <returns>The values.</returns>
        /// <remarks>
        /// <strong>Binary, because <c>:FORMat:DATA</c> is global.</strong> Selecting <c>REAL,32</c>
        /// for the I/Q trace also applies to the scalar block, so a scalar query answers with an
        /// IEEE 488.2 block and not with text. Parsing it as text fails on the first byte that is
        /// not a printable character, which is how this presented: a decoder complaining about
        /// byte 0x5F rather than anything recognisable as a protocol error.
        /// </remarks>
        private double[] QueryScalars(IInstrumentSession session, string command)
        {
            Record(command);
            session.Write(command);

            byte[] payload = session.ReadBinaryBlock();
            int count = payload.Length / 4;

            if (count == 0)
            {
                return new double[0];
            }

            var singles = new float[count];
            BinaryBlock.ToSingles(payload, singles);

            var values = new double[count];

            for (int i = 0; i < count; i++)
            {
                values[i] = singles[i];
            }

            return values;
        }

        /// <summary>
        /// Refuses an instrument that is not the model this driver knows (<c>REQ-E44-001</c>).
        /// </summary>
        /// <param name="identity">The <c>*IDN?</c> reply.</param>
        /// <exception cref="InvalidOperationException">The reply does not name this model.</exception>
        /// <remarks>
        /// The configured address may be anything — this bench has a source-measure unit that
        /// answers perfectly well on the same bus. Sending Basic-mode SCPI to it would produce
        /// errors that describe the commands rather than the mistake.
        /// </remarks>
        private static void RequireModel(string identity)
        {
            if (identity != null && identity.IndexOf("E4406A", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return;
            }

            throw new InvalidOperationException(
                "The instrument at this address identifies itself as '" + identity +
                "', which is not an E4406A. Check the configured VISA resource.");
        }

        /// <summary>Parses the quoted, comma-separated option list of <c>*OPT?</c>.</summary>
        /// <param name="reply">The reply, such as <c>"BAH","202","BAC"</c>.</param>
        private static IReadOnlyList<string> ParseOptions(string reply)
        {
            if (string.IsNullOrEmpty(reply))
            {
                return new string[0];
            }

            string[] parts = reply.Split(',');
            var options = new List<string>(parts.Length);

            foreach (string part in parts)
            {
                string option = part.Trim().Trim('"').Trim();

                if (option.Length > 0)
                {
                    options.Add(option);
                }
            }

            return options;
        }

        private double QueryDouble(IInstrumentSession session, string command)
        {
            string text = Send(session, command, query: true);
            double value;

            if (!double.TryParse(
                text, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            {
                throw new InvalidOperationException(
                    "Expected a number from '" + command + "' but the instrument replied '" +
                    text + "'.");
            }

            return value;
        }

        private void ThrowOnInstrumentError(IInstrumentSession session, string what)
        {
            string reply = Send(session, E4406ACommands.ErrorQuery, query: true);

            if (string.IsNullOrEmpty(reply) || reply.StartsWith("0,", StringComparison.Ordinal) ||
                reply.StartsWith("+0,", StringComparison.Ordinal))
            {
                return;
            }

            // Fail fast rather than measure with a setting the instrument rejected: a silently
            // ignored command produces plausible data from the wrong configuration.
            throw new InvalidOperationException(
                "The instrument reported an error while " + what + ": " + reply);
        }

        private void Record(string command)
        {
            lock (_sent)
            {
                _sent.Add(command);
            }
        }

        private void Raise(FrontEndEventKind kind, string message)
        {
            EventHandler<FrontEndEvent> handler = Notification;

            if (handler != null)
            {
                handler(this, new FrontEndEvent(kind, message));
            }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(E4406AFrontEnd));
            }
        }

        /// <summary>Limits as this particular instrument reports them.</summary>
        private sealed class InstrumentLimits : IFrontEndCapabilities
        {
            private static readonly IReadOnlyList<TriggerStyle> Styles =
                new List<TriggerStyle> { TriggerStyle.Immediate }.AsReadOnly();

            private readonly double _maxSweepSeconds;
            private readonly double _samplesPerSecond;

            public InstrumentLimits(
                FrequencyRange centre,
                double minSpanHz,
                double maxSpanHz,
                double maxSampleRateHz,
                double maxSweepSeconds,
                AmplitudeRange referenceLevel,
                double samplesPerSecond)
            {
                CenterFrequencyRange = centre;
                MinSpanHz = minSpanHz;
                MaxSpanHz = maxSpanHz;
                MaxSampleRateHz = maxSampleRateHz;
                _maxSweepSeconds = maxSweepSeconds;
                ReferenceLevelRange = referenceLevel;
                _samplesPerSecond = samplesPerSecond;
            }

            /// <summary>Measured transfer rate in samples per second, or 0 if unknown.</summary>
            public double SamplesPerSecond => _samplesPerSecond;

            public FrequencyRange CenterFrequencyRange { get; }

            public double MaxSpanHz { get; }

            public double MinSpanHz { get; }

            public double MaxSampleRateHz { get; }

            public int MaxSamplesPerBlock => MaxSamplesFor(MaxSampleRateHz);

            public long MaxCaptureSamples => MaxSamplesPerBlock;

            public bool SupportsBasebandIq => false;

            public int ChannelCount => 1;

            public bool SupportsPhaseCoherentChannels => false;

            public IReadOnlyList<TriggerStyle> TriggerStyles => Styles;

            public AmplitudeRange ReferenceLevelRange { get; }

            public bool SupportsExternalRef => true;

            /// <summary>
            /// False: this transport fetches one record at a time over the bus.
            /// </summary>
            /// <remarks>
            /// Whatever the instrument does internally, the analysis here sees a record, a gap
            /// while it is transferred, and then another record — so the mask of
            /// <c>REQ-TRG-001</c> cannot be tested against every transform, and the style is
            /// declared unsupported rather than offered and silently missing transients.
            /// </remarks>
            public bool SupportsRealTimeAnalysis => false;

            /// <summary>Zero: pre-trigger from capture memory is not exposed over this interface.</summary>
            public long MaxPreTriggerSamples => 0L;

            /// <summary>
            /// The sample rate this instrument would use for a bandwidth, estimated from the
            /// aperture measured at connect.
            /// </summary>
            /// <remarks>
            /// An estimate, and labelled one. The instrument decimates in steps, so the true rate
            /// for an arbitrary bandwidth is only known once it has been set — which is why
            /// <c>ConfigureAsync</c> asks for it again and every block carries the answer.
            /// </remarks>
            public double EstimateSampleRate(double spanHz)
            {
                if (!(MaxSpanHz > 0.0))
                {
                    return MaxSampleRateHz;
                }

                double scaled = MaxSampleRateHz * (spanHz / MaxSpanHz);
                return scaled > MaxSampleRateHz ? MaxSampleRateHz : scaled;
            }

            /// <summary>
            /// Samples the instrument can deliver in one block at a rate.
            /// </summary>
            /// <remarks>
            /// Bounded by <see cref="MaximumTransferSamples"/> as well as by the sweep time. The
            /// sweep-time figure alone is what the instrument could <em>capture</em>; this is what
            /// it can hand over in one block, and it is the second that a settings control must be
            /// ranged against.
            /// </remarks>
            public int MaxSamplesFor(double sampleRateHz)
            {
                double samples = _maxSweepSeconds * sampleRateHz;

                // What it will actually hand over inside the block budget, where that was
                // measurable. This is the bound that stops the settings list offering a capture
                // the instrument cannot deliver in an interactive time.
                if (_samplesPerSecond > 0.0)
                {
                    double transferable = _samplesPerSecond * MaximumBlockSeconds;

                    if (transferable < samples)
                    {
                        samples = transferable;
                    }
                }

                if (samples < 2.0)
                {
                    return 2;
                }

                return samples > MaximumTransferSamples ? MaximumTransferSamples : (int)samples;
            }
        }
    }
}
