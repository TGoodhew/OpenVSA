using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using OpenVSA.Core;
using OpenVSA.Hal;

namespace OpenVSA.Hal.File
{
    /// <summary>
    /// Plays a recorded I/Q file back as a front end (<c>REQ-REC-003</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Playback is a first-class front end, not a special case.</strong> That is the whole
    /// design: the analysis layers must be incapable of telling a live instrument from a file
    /// (<c>REQ-ARC-001</c>), so a recording arrives through the same <see cref="IFrontEnd"/>
    /// negotiation as an instrument does — with capabilities, a plan, coercions and the same
    /// lifecycle. A playback path that bypassed negotiation would let a file do things no
    /// instrument could, and the difference would surface as a measurement that only reproduces
    /// from a recording.
    /// </para>
    /// <para>
    /// <strong>Its capabilities are the recording's, not an instrument's.</strong> A file has one
    /// sample rate, one centre frequency and a fixed number of samples, and it cannot be retuned.
    /// Asking for a different centre is not an error — it is coerced, and the coercion says why,
    /// exactly as it would against an instrument that could not reach the frequency.
    /// </para>
    /// </remarks>
    [FrontEndProvider("File playback")]
    public sealed class FilePlaybackFrontEnd : IFrontEnd
    {
        /// <summary>The header a native recording starts with.</summary>
        public const string Magic = "OPENVSA-IQ1";

        private readonly object _gate = new object();

        private float[] _samples;
        private RecordingHeader _header;
        private AcquisitionPlan _plan;
        private int _position;
        private long _sequence;

        /// <summary>The recording currently loaded, or empty when none.</summary>
        public string Path { get; private set; } = string.Empty;

        /// <inheritdoc />
        public FrontEndId Id => new FrontEndId("file");

        /// <inheritdoc />
        public string DisplayName =>
            Path.Length == 0 ? "File playback" : "File playback — " + System.IO.Path.GetFileName(Path);

        /// <inheritdoc />
        public IFrontEndCapabilities Capabilities { get; private set; } = new PlaybackCapabilities(null);

        /// <inheritdoc />
        public FrontEndState State { get; private set; } = FrontEndState.Disconnected;

        /// <inheritdoc />
        public event EventHandler<FrontEndEvent> Notification;

        /// <summary>Loads a recording, replacing any already open.</summary>
        /// <param name="path">The file.</param>
        /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
        /// <exception cref="FileNotFoundException">The file does not exist.</exception>
        /// <exception cref="InvalidDataException">The file is not a recording this can read.</exception>
        public void Open(string path)
        {
            if (path == null)
            {
                throw new ArgumentNullException(nameof(path));
            }

            if (!System.IO.File.Exists(path))
            {
                throw new FileNotFoundException("No recording at " + path, path);
            }

            RecordingHeader header;
            float[] samples = Read(path, out header);

            lock (_gate)
            {
                Path = path;
                _header = header;
                _samples = samples;
                _position = 0;

                Capabilities = new PlaybackCapabilities(header);
            }

            Raise(FrontEndEventKind.RangeChanged,
                "Loaded " + System.IO.Path.GetFileName(path) + ": " +
                header.SampleCount.ToString(CultureInfo.InvariantCulture) + " samples at " +
                header.SampleRateHz.ToString("G6", CultureInfo.InvariantCulture) + " S/s");
        }

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
        /// Everything a file cannot change is coerced rather than refused, and each coercion says
        /// what the recording actually holds. A file that rejected a retune would make switching
        /// to it from an instrument an error rather than a degradation, which is the opposite of
        /// what <c>REQ-ARC-002</c> asks for.
        /// </remarks>
        public AcquisitionPlan Negotiate(AcquisitionRequest request)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            RecordingHeader header;

            lock (_gate)
            {
                header = _header;
            }

            var coercions = new List<ParameterCoercion>();

            if (header == null)
            {
                // Nothing loaded: honour the shape of the request so a plan exists, and say that
                // it describes nothing.
                coercions.Add(new ParameterCoercion(
                    "Recording", 0.0, 0.0, "no recording is open, so nothing can be played back"));

                return new AcquisitionPlan(
                    request.CenterFrequencyHz, request.SpanHz, request.SpanHz * 1.28,
                    request.SamplesPerBlock, request.ReferenceLevelDbm, false, coercions, request.Path);
            }

            double centre = request.CenterFrequencyHz;

            if (Math.Abs(centre - header.CenterFrequencyHz) > 1e-6)
            {
                coercions.Add(new ParameterCoercion(
                    "CenterFrequency", centre, header.CenterFrequencyHz,
                    "a recording cannot be retuned; it was captured at this centre"));

                centre = header.CenterFrequencyHz;
            }

            double span = request.SpanHz;
            double maxSpan = header.SampleRateHz / 1.28;

            if (span > maxSpan)
            {
                coercions.Add(new ParameterCoercion(
                    "Span", span, maxSpan, "wider than the recording's sample rate allows"));

                span = maxSpan;
            }

            int samples = request.SamplesPerBlock;

            if (samples > header.SampleCount)
            {
                coercions.Add(new ParameterCoercion(
                    "SamplesPerBlock", samples, header.SampleCount,
                    "longer than the recording"));

                samples = header.SampleCount;
            }

            double level = request.ReferenceLevelDbm;

            if (Math.Abs(level - header.ReferenceLevelDbm) > 1e-9)
            {
                coercions.Add(new ParameterCoercion(
                    "ReferenceLevel", level, header.ReferenceLevelDbm,
                    "the recording carries the reference level it was captured at"));

                level = header.ReferenceLevelDbm;
            }

            // Gap-free by definition: the samples are already here, so nothing can fall behind.
            return new AcquisitionPlan(
                centre, span, header.SampleRateHz, samples, level, true, coercions, request.Path);
        }

        /// <inheritdoc />
        public Task ConfigureAsync(AcquisitionPlan plan, CancellationToken ct)
        {
            lock (_gate)
            {
                _plan = plan ?? throw new ArgumentNullException(nameof(plan));
                _position = 0;
            }

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
        /// Wraps at the end rather than stopping. A recording used as a source should behave like
        /// one — a measurement left running against a file that simply stopped would look like an
        /// instrument that had failed.
        /// </remarks>
        public Task<IqBlock> AcquireNextAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            AcquisitionPlan plan;
            RecordingHeader header;
            float[] samples;
            int start;

            lock (_gate)
            {
                if (_plan == null || _samples == null || _header == null)
                {
                    throw new InvalidOperationException(
                        "Open a recording and configure a plan before acquiring.");
                }

                plan = _plan;
                header = _header;
                samples = _samples;

                start = _position;
                _position = (_position + plan.SamplesPerBlock) % Math.Max(1, header.SampleCount);
            }

            State = FrontEndState.Acquiring;

            var metadata = new IqBlockMetadata(
                plan.SamplesPerBlock,
                header.SampleRateHz,
                header.CenterFrequencyHz,
                isBaseband: false,
                fullScaleVolts: header.FullScaleVolts,
                referenceLevelDbm: header.ReferenceLevelDbm,
                sequenceNumber: Interlocked.Increment(ref _sequence),
                acquiredUtc: DateTime.UtcNow,
                triggerOffsetSeconds: 0.0,
                triggerCorrectionsApplied: header.TriggerCorrectionsApplied,
                source: Id,
                extended: null);

            IqBlock block = IqBlock.Rent(metadata);
            Span<float> destination = block.GetSamples();

            for (int n = 0; n < plan.SamplesPerBlock; n++)
            {
                int index = (start + n) % header.SampleCount;

                destination[n * 2] = samples[index * 2];
                destination[n * 2 + 1] = samples[index * 2 + 1];
            }

            return Task.FromResult(block);
        }

        /// <inheritdoc />
        public Task AbortAsync()
        {
            State = FrontEndState.Connected;
            return Task.FromResult(true);
        }

        /// <inheritdoc />
        public void Dispose()
        {
            lock (_gate)
            {
                _samples = null;
                _header = null;
                _plan = null;
            }

            State = FrontEndState.Disconnected;
        }

        /// <summary>Writes a recording this front end can read.</summary>
        /// <param name="path">Where to write.</param>
        /// <param name="header">What the recording describes.</param>
        /// <param name="interleaved">Interleaved I/Q, two floats per sample.</param>
        /// <exception cref="ArgumentNullException">A required argument is null.</exception>
        /// <exception cref="ArgumentException">The sample count and the buffer disagree.</exception>
        public static void Write(string path, RecordingHeader header, float[] interleaved)
        {
            if (path == null)
            {
                throw new ArgumentNullException(nameof(path));
            }

            if (header == null)
            {
                throw new ArgumentNullException(nameof(header));
            }

            if (interleaved == null)
            {
                throw new ArgumentNullException(nameof(interleaved));
            }

            if (interleaved.Length != header.SampleCount * 2)
            {
                throw new ArgumentException(
                    "The header says " + header.SampleCount + " samples but the buffer holds " +
                    (interleaved.Length / 2) + ".", nameof(interleaved));
            }

            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(Magic.ToCharArray());
                writer.Write(header.SampleCount);
                writer.Write(header.SampleRateHz);
                writer.Write(header.CenterFrequencyHz);
                writer.Write(header.FullScaleVolts);
                writer.Write(header.ReferenceLevelDbm);
                writer.Write(header.TriggerCorrectionsApplied);

                foreach (float value in interleaved)
                {
                    writer.Write(value);
                }
            }
        }

        private static float[] Read(string path, out RecordingHeader header)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read))
            using (var reader = new BinaryReader(stream))
            {
                string magic = new string(reader.ReadChars(Magic.Length));

                if (magic != Magic)
                {
                    throw new InvalidDataException(
                        System.IO.Path.GetFileName(path) + " is not an OpenVSA recording.");
                }

                int count = reader.ReadInt32();

                if (count <= 0)
                {
                    throw new InvalidDataException("The recording declares " + count + " samples.");
                }

                header = new RecordingHeader
                {
                    SampleCount = count,
                    SampleRateHz = reader.ReadDouble(),
                    CenterFrequencyHz = reader.ReadDouble(),
                    FullScaleVolts = reader.ReadDouble(),
                    ReferenceLevelDbm = reader.ReadDouble(),
                    TriggerCorrectionsApplied = reader.ReadBoolean(),
                };

                var samples = new float[count * 2];

                for (int i = 0; i < samples.Length; i++)
                {
                    samples[i] = reader.ReadSingle();
                }

                return samples;
            }
        }

        private void Raise(FrontEndEventKind kind, string message) =>
            Notification?.Invoke(this, new FrontEndEvent(kind, message));

        /// <summary>What a recording declares about itself.</summary>
        private sealed class PlaybackCapabilities : IFrontEndCapabilities
        {
            private static readonly IReadOnlyList<TriggerStyle> Styles =
                new ReadOnlyCollection<TriggerStyle>(new[] { TriggerStyle.Immediate });

            private readonly RecordingHeader _header;

            public PlaybackCapabilities(RecordingHeader header)
            {
                _header = header;
            }

            // A recording can only be played at the frequency it was captured at, and saying so
            // through the capability surface is what makes the UI range itself correctly
            // (REQ-HAL-002) rather than offering a tuning control that does nothing.
            public FrequencyRange CenterFrequencyRange => _header == null
                ? new FrequencyRange(0.0, 0.0)
                : new FrequencyRange(_header.CenterFrequencyHz, _header.CenterFrequencyHz);

            public double MaxSpanHz => _header == null ? 0.0 : _header.SampleRateHz / 1.28;

            public double MinSpanHz => _header == null ? 0.0 : MaxSpanHz / 256.0;

            public double MaxSampleRateHz => _header == null ? 0.0 : _header.SampleRateHz;

            public int MaxSamplesPerBlock => _header == null ? 0 : _header.SampleCount;

            public long MaxCaptureSamples => _header == null ? 0L : _header.SampleCount;

            public bool SupportsBasebandIq => true;

            public int ChannelCount => 1;

            public bool SupportsPhaseCoherentChannels => false;

            public IReadOnlyList<TriggerStyle> TriggerStyles => Styles;

            public AmplitudeRange ReferenceLevelRange => _header == null
                ? new AmplitudeRange(0.0, 0.0)
                : new AmplitudeRange(_header.ReferenceLevelDbm, _header.ReferenceLevelDbm);

            public bool SupportsExternalRef => false;

            public bool SupportsRealTimeAnalysis => false;

            public bool SupportsInputRangeControl => false;

            public long MaxPreTriggerSamples => 0L;
        }
    }

    /// <summary>What a recording says about the samples it holds.</summary>
    public sealed class RecordingHeader
    {
        /// <summary>Complex sample count.</summary>
        public int SampleCount { get; set; }

        /// <summary>Sample rate, in hertz.</summary>
        public double SampleRateHz { get; set; } = 2.0e6;

        /// <summary>Centre frequency the samples were captured at, in hertz.</summary>
        public double CenterFrequencyHz { get; set; } = 1.0e9;

        /// <summary>Full scale, in volts.</summary>
        public double FullScaleVolts { get; set; } = 1.0;

        /// <summary>Reference level the capture was made at, in dBm.</summary>
        public double ReferenceLevelDbm { get; set; }

        /// <summary>
        /// Whether trigger corrections had been applied when the samples were written.
        /// </summary>
        /// <remarks>
        /// <c>REQ-DAT-002</c>: this is the fidelity flag, and a recording that dropped it would be
        /// the silent loss that requirement exists to prevent — the samples survive and the
        /// statement about what was done to them does not.
        /// </remarks>
        public bool TriggerCorrectionsApplied { get; set; }
    }
}
