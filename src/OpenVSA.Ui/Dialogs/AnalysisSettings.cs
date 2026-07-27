using System;
using OpenVSA.Core;
using OpenVSA.Dsp.Spectrum;
using OpenVSA.Dsp.Windowing;
using OpenVSA.Measurement;
using OpenVSA.Measurement.State;

namespace OpenVSA.Ui.Dialogs
{
    /// <summary>
    /// The analysis settings the seven tabs of <c>REQ-UI-072</c> edit, live.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>One object, edited in place, announcing itself.</strong> <c>REQ-UI-070</c> requires
    /// a setting dialog to apply as it is changed and requires a parameter edited from a hot spot
    /// and from its dialog to be one piece of state. That is only true if there is one piece of
    /// state, so the settings live here rather than in the dialog's controls or in the shell's
    /// entry boxes — both of those follow this.
    /// </para>
    /// <para>
    /// <strong>Not <see cref="MeasurementState"/> itself.</strong> That is a serialisation model:
    /// plain properties with no notification, no validation and no invariants, which is exactly
    /// what a file format should be. Putting change events on it would make every saved state
    /// carry the shape of the UI that happened to write it.
    /// <see cref="LoadFrom"/> and <see cref="SaveInto"/> are the join.
    /// </para>
    /// <para>
    /// <strong>What is not here is as deliberate as what is.</strong> Nothing about the front end,
    /// nothing about colours, nothing about window arrangement. This is the measurement's
    /// definition — what would have to be the same for two people to be making the same
    /// measurement.
    /// </para>
    /// </remarks>
    public sealed class AnalysisSettings
    {
        /// <summary>Smallest averaging count the Average tab offers.</summary>
        public const int MinimumAverageCount = 1;

        /// <summary>Largest averaging count the Average tab offers.</summary>
        /// <remarks>
        /// Ten thousand. Beyond it an RMS average takes longer to settle than any bench session,
        /// and a user who wants more is really asking for a longer acquisition.
        /// </remarks>
        public const int MaximumAverageCount = 10000;

        /// <summary>Shallowest spectrogram the Heatmaps tab offers.</summary>
        public const int MinimumHeatmapDepth = 2;

        /// <summary>Deepest spectrogram the Heatmaps tab offers.</summary>
        public const int MaximumHeatmapDepth = 4096;

        private double _centerFrequencyHz = 1e9;
        private double _spanHz = 10e6;
        private int _frequencyPoints = AcquisitionPlanner.DefaultFrequencyPoints;
        private bool _pointsAreAutomatic;
        private SpanChangeBehaviour _spanChange = SpanChangeBehaviour.Zoom;

        private double _resolutionBandwidthHz = 100e3;
        private bool _resolutionBandwidthIsAutomatic = true;
        private double _spanToRatio = ResolutionBandwidthControl.DefaultSpanToRatio;
        private WindowType _window = WindowType.FlatTop;

        private double _overlap;
        private bool _gateEnabled;
        private double _gateDelaySeconds;
        private double _gateLengthSeconds = 1e-3;

        private TraceDetector _detector = TraceDetector.Normal;
        private bool _detectorIsAutomatic = true;

        private AnalysisPath _path = AnalysisPath.ComplexZoom;
        private int _maxTransformLength = SpectrumComputer.DefaultMaxTransformLength;
        private bool _noiseCorrection;

        private AveragingType _averaging = AveragingType.Off;
        private int _averageCount = 10;
        private bool _repeatAverage;

        private TraceAccumulator _accumulator = TraceAccumulator.None;
        private int _heatmapDepth = 200;
        private double _heatmapRangeDb = 80.0;
        private double _persistenceSeconds = 1.0;

        private int _suppressed;
        private bool _pending;

        /// <summary>Raised whenever any setting changes, so every surface can follow.</summary>
        public event EventHandler Changed;

        // ---- Frequency -------------------------------------------------------------------------

        /// <summary>Centre frequency, in hertz.</summary>
        /// <exception cref="ArgumentOutOfRangeException">Not a finite, positive frequency.</exception>
        public double CenterFrequencyHz
        {
            get { return _centerFrequencyHz; }
            set { SetPositive(ref _centerFrequencyHz, value, nameof(value), "A centre frequency"); }
        }

        /// <summary>Span, in hertz.</summary>
        /// <exception cref="ArgumentOutOfRangeException">Not a finite, positive frequency.</exception>
        public double SpanHz
        {
            get { return _spanHz; }
            set { SetPositive(ref _spanHz, value, nameof(value), "A span"); }
        }

        /// <summary>Displayed frequency points (<c>REQ-DSP-022</c>).</summary>
        /// <exception cref="ArgumentOutOfRangeException">Not a supported point count.</exception>
        public int FrequencyPoints
        {
            get { return _frequencyPoints; }

            set
            {
                if (!FrequencyPointsAreSupported(value))
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(value), value, "Not one of REQ-DSP-022's supported point counts.");
                }

                Set(ref _frequencyPoints, value);
            }
        }

        /// <summary>Whether the point count follows the resolution bandwidth.</summary>
        public bool PointsAreAutomatic
        {
            get { return _pointsAreAutomatic; }
            set { Set(ref _pointsAreAutomatic, value); }
        }

        /// <summary>What a span change does to the frequency axis (<c>REQ-DSP-023</c>).</summary>
        public SpanChangeBehaviour SpanChange
        {
            get { return _spanChange; }
            set { Set(ref _spanChange, value); }
        }

        // ---- ResBW -----------------------------------------------------------------------------

        /// <summary>Resolution bandwidth, in hertz (<c>REQ-DSP-020</c>).</summary>
        /// <exception cref="ArgumentOutOfRangeException">Not a finite, positive bandwidth.</exception>
        public double ResolutionBandwidthHz
        {
            get { return _resolutionBandwidthHz; }

            set
            {
                SetPositive(
                    ref _resolutionBandwidthHz, value, nameof(value), "A resolution bandwidth");
            }
        }

        /// <summary>Whether the resolution bandwidth is coupled to the span (<c>REQ-DSP-021</c>).</summary>
        public bool ResolutionBandwidthIsAutomatic
        {
            get { return _resolutionBandwidthIsAutomatic; }
            set { Set(ref _resolutionBandwidthIsAutomatic, value); }
        }

        /// <summary>The span-to-resolution-bandwidth ratio used while coupled (<c>REQ-DSP-021</c>).</summary>
        /// <exception cref="ArgumentOutOfRangeException">Not a finite ratio above one.</exception>
        public double SpanToRatio
        {
            get { return _spanToRatio; }

            set
            {
                if (double.IsNaN(value) || double.IsInfinity(value) || value <= 1.0)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(value), value, "A span-to-resolution ratio is greater than one.");
                }

                Set(ref _spanToRatio, value);
            }
        }

        /// <summary>Analysis window (<c>REQ-DSP-010</c>).</summary>
        /// <remarks>
        /// On the ResBW tab rather than a tab of its own, because the window is what turns a time
        /// record into a resolution bandwidth: <c>REQ-DSP-020</c>'s RBW is the window's equivalent
        /// noise bandwidth over the record length, and choosing one without seeing the other is
        /// choosing half a setting.
        /// </remarks>
        public WindowType Window
        {
            get { return _window; }
            set { Set(ref _window, value); }
        }

        // ---- Time ------------------------------------------------------------------------------

        /// <summary>Overlap between analysis frames, 0 to just under 1 (<c>REQ-ACQ-003</c>).</summary>
        /// <exception cref="ArgumentOutOfRangeException">Outside 0 to 0.99.</exception>
        public double Overlap
        {
            get { return _overlap; }

            set
            {
                if (double.IsNaN(value) || value < 0.0 || value > 0.99)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(value), value,
                        "An overlap is a fraction from 0 to 0.99; 1 would advance by nothing.");
                }

                Set(ref _overlap, value);
            }
        }

        /// <summary>Whether time gating is in force (<c>REQ-DSP-050</c>).</summary>
        public bool GateEnabled
        {
            get { return _gateEnabled; }
            set { Set(ref _gateEnabled, value); }
        }

        /// <summary>Gate delay from the trigger, in seconds.</summary>
        /// <exception cref="ArgumentOutOfRangeException">Negative or not finite.</exception>
        public double GateDelaySeconds
        {
            get { return _gateDelaySeconds; }

            set
            {
                if (double.IsNaN(value) || double.IsInfinity(value) || value < 0.0)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(value), value, "A gate delay is zero or more seconds.");
                }

                Set(ref _gateDelaySeconds, value);
            }
        }

        /// <summary>Gate length, in seconds.</summary>
        /// <exception cref="ArgumentOutOfRangeException">Not a finite, positive length.</exception>
        public double GateLengthSeconds
        {
            get { return _gateLengthSeconds; }
            set { SetPositive(ref _gateLengthSeconds, value, nameof(value), "A gate length"); }
        }

        /// <summary>
        /// The time record these settings imply, in seconds (<c>REQ-ACQ-002</c>).
        /// </summary>
        /// <remarks>
        /// Derived, not stored. Main time length, span and point count are three names for two
        /// degrees of freedom — <c>T = N / (k · span)</c> — and storing all three would let a
        /// recalled state disagree with itself. The Time tab shows this and lets the two it is
        /// derived from be edited, which is the honest way round.
        /// </remarks>
        public double MainTimeSeconds =>
            AcquisitionLaw.MaxTimeSeconds(_frequencyPoints, _spanHz);

        // ---- Detectors -------------------------------------------------------------------------

        /// <summary>
        /// How points sharing a pixel column are reduced (<c>REQ-UI-072</c>).
        /// </summary>
        /// <remarks>
        /// Setting it explicitly uncouples it, exactly as setting a resolution bandwidth uncouples
        /// that from the span (<c>REQ-DSP-021</c>). Turning
        /// <see cref="DetectorIsAutomatic"/> back on re-couples it to the averaging.
        /// </remarks>
        public TraceDetector Detector
        {
            get { return _detectorIsAutomatic ? DetectorFor(_averaging) : _detector; }

            set
            {
                using (Batch())
                {
                    Set(ref _detectorIsAutomatic, false);
                    Set(ref _detector, value);
                }
            }
        }

        /// <summary>
        /// Whether the detector follows the averaging (<c>REQ-UI-072</c>).
        /// </summary>
        /// <remarks>
        /// <para>
        /// On by default, and the coupling is not decoration: an RMS average is a statement about
        /// mean power, and drawing it with a peak detector would put the column's loudest bin on
        /// screen beside an annotation claiming a mean. Peak hold is the same argument the other
        /// way round. The rule is <see cref="DetectorFor"/>, in one place.
        /// </para>
        /// <para>
        /// A user who wants a peak detector over an RMS average can have one — that is what
        /// uncoupling is for. What they cannot have is that combination by accident.
        /// </para>
        /// </remarks>
        public bool DetectorIsAutomatic
        {
            get { return _detectorIsAutomatic; }
            set { Set(ref _detectorIsAutomatic, value); }
        }

        /// <summary>The detector an averaging type implies.</summary>
        /// <param name="averaging">The averaging in force.</param>
        public static TraceDetector DetectorFor(AveragingType averaging)
        {
            switch (averaging)
            {
                case AveragingType.RmsVideo:
                case AveragingType.RmsVideoExponential:
                    return TraceDetector.Average;

                case AveragingType.PeakHold:
                case AveragingType.ContinuousPeakHold:
                    return TraceDetector.Peak;
            }

            // Off, and the coherent averages: nothing has been claimed about the column, so keep
            // both extrema and lose nothing.
            return TraceDetector.Normal;
        }

        // ---- Conversion ------------------------------------------------------------------------

        /// <summary>Which acquisition path (<c>REQ-ACQ-001</c>).</summary>
        public AnalysisPath Path
        {
            get { return _path; }
            set { Set(ref _path, value); }
        }

        /// <summary>Ceiling on transform size, in complex points (<c>REQ-DSP-024</c>).</summary>
        /// <exception cref="ArgumentOutOfRangeException">Not a power of two of at least 2.</exception>
        public int MaxTransformLength
        {
            get { return _maxTransformLength; }

            set
            {
                if (value < 2 || (value & (value - 1)) != 0)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(value), value, "A transform ceiling is a power of two, at least 2.");
                }

                Set(ref _maxTransformLength, value);
            }
        }

        /// <summary>Whether a characterised noise floor is subtracted (<c>REQ-DSP-024</c>).</summary>
        public bool NoiseCorrection
        {
            get { return _noiseCorrection; }
            set { Set(ref _noiseCorrection, value); }
        }

        // ---- Average ---------------------------------------------------------------------------

        /// <summary>Averaging type (<c>REQ-DSP-030</c>).</summary>
        public AveragingType Averaging
        {
            get { return _averaging; }
            set { Set(ref _averaging, value); }
        }

        /// <summary>Averages to run to.</summary>
        /// <exception cref="ArgumentOutOfRangeException">Outside the settable range.</exception>
        public int AverageCount
        {
            get { return _averageCount; }

            set
            {
                if (value < MinimumAverageCount || value > MaximumAverageCount)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(value), value,
                        "An average count is between " + MinimumAverageCount + " and " +
                        MaximumAverageCount + ".");
                }

                Set(ref _averageCount, value);
            }
        }

        /// <summary>Whether a completed average restarts.</summary>
        public bool RepeatAverage
        {
            get { return _repeatAverage; }
            set { Set(ref _repeatAverage, value); }
        }

        // ---- Heatmaps --------------------------------------------------------------------------

        /// <summary>
        /// The accumulating display mode (<c>REQ-TRC-001a</c>).
        /// </summary>
        /// <remarks>
        /// The three of these are what the Heatmaps tab is about, and they are an axis rather than
        /// a format: changing the accumulator necessarily discards what has been accumulated, while
        /// changing the format does not.
        /// </remarks>
        public TraceAccumulator Accumulator
        {
            get { return _accumulator; }
            set { Set(ref _accumulator, value); }
        }

        /// <summary>Rows of history a spectrogram keeps.</summary>
        /// <exception cref="ArgumentOutOfRangeException">Outside the settable range.</exception>
        public int HeatmapDepth
        {
            get { return _heatmapDepth; }

            set
            {
                if (value < MinimumHeatmapDepth || value > MaximumHeatmapDepth)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(value), value,
                        "A heatmap keeps between " + MinimumHeatmapDepth + " and " +
                        MaximumHeatmapDepth + " rows.");
                }

                Set(ref _heatmapDepth, value);
            }
        }

        /// <summary>The dynamic range a heatmap's colour map is spread over, in dB.</summary>
        /// <exception cref="ArgumentOutOfRangeException">Not a finite, positive range.</exception>
        public double HeatmapRangeDb
        {
            get { return _heatmapRangeDb; }
            set { SetPositive(ref _heatmapRangeDb, value, nameof(value), "A heatmap range"); }
        }

        /// <summary>How long a persistence display holds a point, in seconds.</summary>
        /// <exception cref="ArgumentOutOfRangeException">Not a finite, positive time.</exception>
        public double PersistenceSeconds
        {
            get { return _persistenceSeconds; }
            set { SetPositive(ref _persistenceSeconds, value, nameof(value), "A persistence time"); }
        }

        // ---- Batching, and the join to the state model -------------------------------------------

        /// <summary>
        /// Suspends change notification until the returned token is disposed.
        /// </summary>
        /// <returns>A token; disposing it raises one change if anything moved.</returns>
        /// <remarks>
        /// So that loading a state, or a tab writing two coupled settings, costs one re-plan rather
        /// than one per property. Nested batches raise once, at the outermost.
        /// </remarks>
        public IDisposable Batch() => new Batched(this);

        /// <summary>
        /// Reads the settings out of a saved measurement.
        /// </summary>
        /// <param name="state">The state to read.</param>
        /// <exception cref="ArgumentNullException"><paramref name="state"/> is null.</exception>
        /// <remarks>
        /// Values outside this build's settable ranges are clamped rather than thrown on, for the
        /// reason every other loader here gives: a state written by another version should cost the
        /// user the setting it disagrees about, not the whole recall.
        /// </remarks>
        public void LoadFrom(MeasurementState state)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            AnalysisState analysis = state.Analysis ?? new AnalysisState();

            using (Batch())
            {
                _centerFrequencyHz = Positive(state.CenterFrequencyHz, _centerFrequencyHz);
                _spanHz = Positive(state.SpanHz, _spanHz);
                _frequencyPoints = FrequencyPointsAreSupported(analysis.FrequencyPoints)
                    ? analysis.FrequencyPoints
                    : _frequencyPoints;
                _pointsAreAutomatic = analysis.PointsAreAutomatic;
                _spanChange = analysis.SpanChange;

                _resolutionBandwidthHz =
                    Positive(state.ResolutionBandwidthHz, _resolutionBandwidthHz);
                _resolutionBandwidthIsAutomatic = state.ResolutionBandwidthIsAutomatic;
                _window = analysis.Window;

                _overlap = analysis.Overlap >= 0.0 && analysis.Overlap <= 0.99
                    ? analysis.Overlap
                    : 0.0;
                _gateEnabled = analysis.GateEnabled;
                _gateDelaySeconds = analysis.GateDelaySeconds >= 0.0 ? analysis.GateDelaySeconds : 0.0;
                _gateLengthSeconds = Positive(analysis.GateLengthSeconds, _gateLengthSeconds);

                _detector = analysis.Detector;
                _detectorIsAutomatic = analysis.DetectorIsAutomatic;

                _path = analysis.Path;
                _maxTransformLength = analysis.MaxTransformLength >= 2 &&
                                      (analysis.MaxTransformLength &
                                       (analysis.MaxTransformLength - 1)) == 0
                    ? analysis.MaxTransformLength
                    : _maxTransformLength;
                _noiseCorrection = analysis.NoiseCorrection;

                _averaging = analysis.Averaging;
                _averageCount = Clamp(
                    analysis.AverageCount, MinimumAverageCount, MaximumAverageCount);
                _repeatAverage = analysis.RepeatAverage;

                TraceDisplayState display =
                    state.Traces != null && state.Traces.Count > 0 ? state.Traces[0] : null;

                if (display != null)
                {
                    _accumulator = display.Accumulator;
                    _heatmapDepth = Clamp(
                        display.SpectrogramDepth, MinimumHeatmapDepth, MaximumHeatmapDepth);
                    _heatmapRangeDb = Positive(display.SpectrogramRangeDb, _heatmapRangeDb);
                    _persistenceSeconds = Positive(display.PersistenceSeconds, _persistenceSeconds);
                }

                Touch();
            }
        }

        /// <summary>
        /// Writes the settings into a measurement state.
        /// </summary>
        /// <param name="state">The state to write into.</param>
        /// <exception cref="ArgumentNullException"><paramref name="state"/> is null.</exception>
        public void SaveInto(MeasurementState state)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            state.CenterFrequencyHz = _centerFrequencyHz;
            state.SpanHz = _spanHz;
            state.ResolutionBandwidthHz = _resolutionBandwidthHz;
            state.ResolutionBandwidthIsAutomatic = _resolutionBandwidthIsAutomatic;

            AnalysisState analysis = state.Analysis ?? (state.Analysis = new AnalysisState());

            analysis.FrequencyPoints = _frequencyPoints;
            analysis.PointsAreAutomatic = _pointsAreAutomatic;
            analysis.SpanChange = _spanChange;
            analysis.Window = _window;
            analysis.Overlap = _overlap;
            analysis.GateEnabled = _gateEnabled;
            analysis.GateDelaySeconds = _gateDelaySeconds;
            analysis.GateLengthSeconds = _gateLengthSeconds;
            analysis.Detector = Detector;
            analysis.DetectorIsAutomatic = _detectorIsAutomatic;
            analysis.Path = _path;
            analysis.MaxTransformLength = _maxTransformLength;
            analysis.NoiseCorrection = _noiseCorrection;
            analysis.Averaging = _averaging;
            analysis.AverageCount = _averageCount;
            analysis.RepeatAverage = _repeatAverage;

            if (state.Traces != null && state.Traces.Count > 0)
            {
                TraceDisplayState display = state.Traces[0];

                display.Accumulator = _accumulator;
                display.SpectrogramDepth = _heatmapDepth;
                display.SpectrogramRangeDb = _heatmapRangeDb;
                display.PersistenceSeconds = _persistenceSeconds;
            }
        }

        /// <summary>Whether a point count is one <c>REQ-DSP-022</c> supports.</summary>
        /// <param name="points">The count.</param>
        public static bool FrequencyPointsAreSupported(int points)
        {
            foreach (int supported in FrequencyPoints_Supported)
            {
                if (supported == points)
                {
                    return true;
                }
            }

            return false;
        }

        private static readonly System.Collections.Generic.IReadOnlyList<int> FrequencyPoints_Supported =
            OpenVSA.Core.FrequencyPoints.Supported;

        private static int Clamp(int value, int minimum, int maximum)
        {
            if (value < minimum)
            {
                return minimum;
            }

            return value > maximum ? maximum : value;
        }

        private static double Positive(double value, double fallback) =>
            double.IsNaN(value) || double.IsInfinity(value) || value <= 0.0 ? fallback : value;

        private void SetPositive(ref double field, double value, string name, string what)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0.0)
            {
                throw new ArgumentOutOfRangeException(
                    name, value, what + " is a finite, positive number of its unit.");
            }

            Set(ref field, value);
        }

        private void Set<T>(ref T field, T value)
        {
            if (Equals(field, value))
            {
                return;
            }

            field = value;
            Touch();
        }

        private void Touch()
        {
            if (_suppressed > 0)
            {
                _pending = true;
                return;
            }

            EventHandler handler = Changed;

            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }
        }

        /// <inheritdoc />
        public override string ToString() =>
            EngineeringText.Frequency(_centerFrequencyHz) + " centre, " +
            EngineeringText.Frequency(_spanHz) + " span, " +
            _frequencyPoints + " points, " + TraceDetection.NameOf(Detector) + " detector" +
            (_detectorIsAutomatic ? " (coupled)" : string.Empty);

        private sealed class Batched : IDisposable
        {
            private readonly AnalysisSettings _settings;

            private bool _closed;

            internal Batched(AnalysisSettings settings)
            {
                _settings = settings;
                _settings._suppressed++;
            }

            public void Dispose()
            {
                if (_closed)
                {
                    return;
                }

                _closed = true;

                if (--_settings._suppressed > 0)
                {
                    return;
                }

                if (_settings._pending)
                {
                    _settings._pending = false;
                    _settings.Touch();
                }
            }
        }
    }
}
