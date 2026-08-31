using System;
using System.Collections.Generic;
using System.Globalization;
using OpenVSA.Core;
using OpenVSA.Demod.Chain;
using OpenVSA.Demod.Results;
using OpenVSA.Demod.Signal;
using OpenVSA.Dsp.Spectrum;
using OpenVSA.Dsp.Windowing;
using OpenVSA.Hal;
using OpenVSA.Measurement.Limits;
using Newtonsoft.Json;

namespace OpenVSA.Measurement.State
{
    /// <summary>What kind of measurement a context is making.</summary>
    public enum MeasurementKind
    {
        /// <summary>Scalar spectrum.</summary>
        Spectrum = 0,

        /// <summary>Vector analysis: spectrum with phase, and time-domain traces.</summary>
        VectorAnalysis,

        /// <summary>Digital demodulation.</summary>
        DigitalDemodulation,

        /// <summary>Analogue demodulation.</summary>
        AnalogDemodulation,
    }

    /// <summary>How an input is coupled.</summary>
    public enum InputCoupling
    {
        /// <summary>AC coupled.</summary>
        Ac = 0,

        /// <summary>DC coupled.</summary>
        Dc,
    }

    /// <summary>
    /// The input settings a state carries (<c>REQ-STA-001</c>).
    /// </summary>
    public sealed class InputState
    {
        /// <summary>Input range, in dBm.</summary>
        public double RangeDbm { get; set; } = 0.0;

        /// <summary>Whether the range is chosen by the instrument.</summary>
        public bool RangeIsAutomatic { get; set; } = true;

        /// <summary>How the input is coupled.</summary>
        public InputCoupling Coupling { get; set; } = InputCoupling.Ac;

        /// <summary>Whether the digital input is selected rather than the analogue one.</summary>
        public bool IsDigital { get; set; }

        /// <summary>Whether an external mixer is in use.</summary>
        public bool ExternalMixer { get; set; }

        /// <summary>Harmonic number of the external mixer.</summary>
        public int ExternalMixerHarmonic { get; set; } = 1;

        /// <summary>Whether the external frequency reference is selected.</summary>
        public bool ExternalReference { get; set; }
    }

    /// <summary>
    /// The trigger settings a state carries (<c>REQ-STA-001</c>).
    /// </summary>
    public sealed class TriggerState
    {
        /// <summary>How acquisition is triggered.</summary>
        public TriggerStyle Style { get; set; } = TriggerStyle.Immediate;

        /// <summary>Trigger channel, as it is labelled.</summary>
        public string Channel { get; set; } = "Ch 1";

        /// <summary>Trigger level, in dBm.</summary>
        public double LevelDbm { get; set; } = -30.0;

        /// <summary>Delay from the trigger to the start of the record, in seconds.</summary>
        public double DelaySeconds { get; set; }

        /// <summary>Whether the trigger fires on the rising edge.</summary>
        public bool RisingEdge { get; set; } = true;

        /// <summary>Hold-off after a trigger, in seconds.</summary>
        public double HoldoffSeconds { get; set; }
    }

    /// <summary>
    /// The analysis settings a state carries (<c>REQ-STA-001</c>).
    /// </summary>
    public sealed class AnalysisState
    {
        /// <summary>Displayed frequency points (<c>REQ-DSP-022</c>).</summary>
        public int FrequencyPoints { get; set; } = 801;

        /// <summary>Whether the point count follows the resolution bandwidth.</summary>
        public bool PointsAreAutomatic { get; set; }

        /// <summary>Analysis window (<c>REQ-DSP-010</c>).</summary>
        public WindowType Window { get; set; } = WindowType.FlatTop;

        /// <summary>
        /// Whether the measurement is in zero-span/power-spectrum operation (<c>REQ-DSP-012</c>).
        /// </summary>
        /// <remarks>
        /// A mode, not a span of zero hertz — see <see cref="ZeroSpanMeasurement"/> for why. Saved
        /// because it decides which of <see cref="Window"/> and <see cref="ChannelFilter"/> produced
        /// the trace, and a recalled setup that lost it would report the wrong one of the two.
        /// </remarks>
        public bool ZeroSpan { get; set; }

        /// <summary>
        /// The channel filter shape that replaces the window in zero span (<c>REQ-DSP-012</c>).
        /// </summary>
        /// <remarks>
        /// Held whatever the mode, and deliberately: a user who leaves zero span and comes back
        /// should find the filter they chose, exactly as they find the window they chose. Which of
        /// the two was in force is what <see cref="ZeroSpan"/> records — "so a saved measurement
        /// records which filter produced it" is the criterion, and it takes both fields to answer.
        /// </remarks>
        public ChannelFilterType ChannelFilter { get; set; } = ChannelFilterType.Gaussian;

        /// <summary>Which acquisition path (<c>REQ-ACQ-001</c>).</summary>
        public AnalysisPath Path { get; set; } = AnalysisPath.ComplexZoom;

        /// <summary>Overlap between analysis frames (<c>REQ-ACQ-003</c>).</summary>
        public double Overlap { get; set; }

        /// <summary>
        /// What a span change does to the frequency axis (<c>REQ-DSP-023</c>'s
        /// <em>Zoom If Span Change</em>).
        /// </summary>
        /// <remarks>
        /// Carried here rather than left to the session, because it changes what the <em>next</em>
        /// span the user types will mean. A recalled state whose span behaviour reverted to the
        /// default would do the right thing until the moment someone changed the span, which is
        /// the worst place for a setting to go missing.
        /// </remarks>
        public SpanChangeBehaviour SpanChange { get; set; } = SpanChangeBehaviour.Zoom;

        /// <summary>
        /// Ceiling on transform size, in complex points (<c>REQ-DSP-024</c>'s
        /// <em>Max FFT Size</em>).
        /// </summary>
        public int MaxTransformLength { get; set; } = SpectrumComputer.DefaultMaxTransformLength;

        /// <summary>
        /// Whether a characterised instrument noise floor is subtracted (<c>REQ-DSP-024</c>'s
        /// <em>Noise Correction</em>).
        /// </summary>
        /// <remarks>
        /// The switch, not the characterisation. A measured noise floor is a calibration of the
        /// instrument rather than a setting of the measurement, and putting a whole trace in every
        /// saved state would make a state file that is mostly someone else's noise.
        /// </remarks>
        public bool NoiseCorrection { get; set; }

        /// <summary>Averaging type (<c>REQ-DSP-030</c>).</summary>
        public AveragingType Averaging { get; set; } = AveragingType.Off;

        /// <summary>Averages to run to.</summary>
        public int AverageCount { get; set; } = 10;

        /// <summary>Whether a completed average restarts.</summary>
        public bool RepeatAverage { get; set; }

        /// <summary>
        /// How points sharing a pixel column are reduced (<c>REQ-UI-072</c>'s Detectors tab).
        /// </summary>
        /// <remarks>
        /// Part of the measurement rather than of the display preferences, because it changes what
        /// the trace on screen asserts about the signal: a Peak detector and an Average detector
        /// over the same acquisition are two different claims, and a colleague recalling the state
        /// needs the one that was being made.
        /// </remarks>
        public TraceDetector Detector { get; set; } = TraceDetector.Normal;

        /// <summary>Whether the detector follows the averaging type (<c>REQ-UI-072</c>).</summary>
        public bool DetectorIsAutomatic { get; set; } = true;

        /// <summary>Whether time gating is in force (<c>REQ-DSP-050</c>).</summary>
        public bool GateEnabled { get; set; }

        /// <summary>Gate delay, in seconds.</summary>
        public double GateDelaySeconds { get; set; }

        /// <summary>Gate length, in seconds.</summary>
        public double GateLengthSeconds { get; set; } = 1e-3;
    }

    /// <summary>
    /// Where a trace window sits in the display grid (<c>REQ-STA-001</c>).
    /// </summary>
    public sealed class TraceWindowState
    {
        /// <summary>The trace letter this window shows (<c>REQ-UI-020</c>).</summary>
        public string Trace { get; set; } = "A";

        /// <summary>Row in the display grid.</summary>
        public int Row { get; set; }

        /// <summary>Column in the display grid.</summary>
        public int Column { get; set; }

        /// <summary>Rows spanned.</summary>
        public int RowSpan { get; set; } = 1;

        /// <summary>Columns spanned.</summary>
        public int ColumnSpan { get; set; } = 1;

        /// <summary>Whether this window is shown.</summary>
        public bool IsVisible { get; set; } = true;

        /// <summary>Whether this trace is overlaid on another window rather than given its own.</summary>
        public bool IsOverlaid { get; set; }

        /// <summary>The window this one is overlaid on, or empty.</summary>
        public string OverlaidOn { get; set; } = string.Empty;
    }

    /// <summary>
    /// A trace's display properties (<c>REQ-STA-001</c>).
    /// </summary>
    public sealed class TraceDisplayState
    {
        /// <summary>The trace letter (<c>REQ-UI-020</c>).</summary>
        public string Trace { get; set; } = "A";

        /// <summary>Display format (<c>REQ-DSP-041</c>).</summary>
        public TraceFormat Format { get; set; } = TraceFormat.LogMagnitude;

        /// <summary>Accumulating display over the format (<c>REQ-TRC-001a</c>).</summary>
        public TraceAccumulator Accumulator { get; set; } = TraceAccumulator.None;

        /// <summary>Level at the top of the graticule, in dBm.</summary>
        public double TopDbm { get; set; } = 0.0;

        /// <summary>Decibels per graticule division.</summary>
        public double DecibelsPerDivision { get; set; } = 10.0;

        /// <summary>Whether the Y axis is scaled automatically.</summary>
        public bool AutoScaleY { get; set; }

        /// <summary>Left end of the X axis, in hertz or seconds as the format requires.</summary>
        public double XStart { get; set; }

        /// <summary>Right end of the X axis.</summary>
        public double XStop { get; set; }

        /// <summary>Whether the X axis follows the measurement rather than being held.</summary>
        public bool AutoScaleX { get; set; } = true;

        /// <summary>Spectrogram depth, in frames.</summary>
        public int SpectrogramDepth { get; set; } = 200;

        /// <summary>Decibels covered by the spectrogram's colour map.</summary>
        public double SpectrogramRangeDb { get; set; } = 80.0;

        /// <summary>Persistence decay, in seconds, for a persistence display.</summary>
        public double PersistenceSeconds { get; set; } = 1.0;
    }

    /// <summary>
    /// A marker's type, place and calculation (<c>REQ-STA-001</c>, <c>REQ-MKR-001</c>).
    /// </summary>
    public sealed class MarkerState
    {
        /// <summary>Marker number, from 1.</summary>
        public int Number { get; set; } = 1;

        /// <summary>The trace it sits on.</summary>
        public string Trace { get; set; } = "A";

        /// <summary>Normal, delta or fixed.</summary>
        public string Type { get; set; } = "Normal";

        /// <summary>Position on the X axis.</summary>
        public double XHz { get; set; }

        /// <summary>Level, for a fixed marker.</summary>
        public double YDbm { get; set; }

        /// <summary>The marker this one is a delta from, or 0 for none.</summary>
        public int DeltaReference { get; set; }

        /// <summary>The band calculation this marker drives, if any.</summary>
        public string Calculation { get; set; } = string.Empty;

        /// <summary>Whether this is the selected marker.</summary>
        public bool IsSelected { get; set; }

        /// <summary>Whether the marker is drawn (<c>REQ-UI-062</c>).</summary>
        public bool IsVisible { get; set; } = true;
    }

    /// <summary>
    /// The digital demodulator's settings (<c>REQ-DEM-001</c> and the requirements it makes room
    /// for).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Why this is not <c>DemodSettings</c>.</strong> The chain of <c>REQ-DEM-001</c> takes
    /// a settings object of its own, carrying a <c>Constellation</c> — a list of points, computed.
    /// A state is what gets written to a file and read back by software that may be older or newer,
    /// so it holds a format's <em>name</em> and plain numbers, and <see cref="ToSettings"/> resolves
    /// the one into the other. Sharing one type would mean either a state file full of coordinates
    /// or a demodulator that parsed strings.
    /// </para>
    /// <para>
    /// <strong>What is deliberately not here yet.</strong> The sync pattern of <c>REQ-DEM-040</c>,
    /// because how a pattern is entered is that requirement's to decide and a guess here would be a
    /// format to migrate away from later. Step 6 therefore has nothing to search for and is not
    /// offered. The convergence bounds of <c>REQ-DEM-001</c> are absent for a different reason: they
    /// are not settings anyone picks, and the chain's defaults are what they are set to.
    /// </para>
    /// </remarks>
    public sealed class DemodState
    {
        /// <summary>The modulation format, by name (<c>REQ-DEM-010</c>).</summary>
        public string Format { get; set; } = "QPSK";

        /// <summary>
        /// The symbol rate, in hertz, applied exactly as entered (<c>REQ-DEM-030</c>).
        /// </summary>
        /// <remarks>
        /// Zero means no rate has been chosen yet. <c>REQ-DEM-030</c> makes the default Span/2 on
        /// first selection of digital demodulation, which is what
        /// <see cref="MeasurementState.SelectKind"/> applies — a default at the moment of choosing,
        /// not a rate the demodulator invents for itself while measuring.
        /// </remarks>
        public double SymbolRateHz { get; set; }

        /// <summary>
        /// The internal processing rate, in samples per symbol (<c>REQ-DEM-034a</c>).
        /// </summary>
        /// <remarks>
        /// Not the displayed points per symbol of <c>REQ-DEM-034</c>, which is a display parameter
        /// and is required not to change what the demodulator does.
        /// </remarks>
        public int PointsPerSymbol { get; set; } = 4;

        /// <summary>Which bits the constellation's points carry (<c>REQ-DEM-011</c>).</summary>
        /// <remarks>
        /// Defaults to <see cref="BitMapping.Natural"/>, which is what every format in this
        /// catalogue meant before the choice existed and therefore what a version 3 state file
        /// implies. <see cref="BitMappingTable"/> supplies the table when this is
        /// <see cref="BitMapping.Explicit"/>.
        /// </remarks>
        public BitMapping BitMapping { get; set; } = BitMapping.Natural;

        /// <summary>
        /// What each point carries, when <see cref="BitMapping"/> is
        /// <see cref="BitMapping.Explicit"/> (<c>REQ-DEM-011</c>).
        /// </summary>
        /// <remarks>
        /// Empty for the other mappings, and empty by default: a state's members all have defaults,
        /// per <c>REQ-STA-005</c>, and the default of a table nobody supplied is no table rather
        /// than a table of zeroes — which would be a labelling that sends every point to the same
        /// value, and is refused when applied.
        /// </remarks>
        public List<int> BitMappingTable { get; set; } = new List<int>();

        /// <summary>
        /// The rings a user-defined constellation is built from (<c>REQ-DEM-011</c>).
        /// </summary>
        /// <remarks>
        /// <para>
        /// Read only when <see cref="Format"/> names no catalogue format, which is how a state says
        /// "this one is mine". Empty by default, and empty is what a state file written before
        /// <c>REQ-DEM-011</c> has.
        /// </para>
        /// <para>
        /// <strong>Rings and points are alternatives, not layers.</strong> A definition is one or
        /// the other; a state holding both is refused rather than silently preferring one, because
        /// which one it preferred would decide what was measured.
        /// </para>
        /// </remarks>
        public List<ApskRingState> CustomRings { get; set; } = new List<ApskRingState>();

        /// <summary>
        /// The explicit points a user-defined constellation is built from (<c>REQ-DEM-011</c>).
        /// </summary>
        public List<ConstellationPointState> CustomPoints { get; set; } =
            new List<ConstellationPointState>();

        /// <summary>What a symbol's bits are read against (<c>REQ-DEM-012</c>).</summary>
        /// <remarks>
        /// <para>
        /// Defaults to <see cref="DifferentialReference.PerFormat"/>, which is what choosing DQPSK
        /// from a menu means and what a version 2 state file implies: it had no such member, and a
        /// format that carries its bits differentially was demodulated that way.
        /// </para>
        /// <para>
        /// It is here rather than derived at the point of use because it is a choice a user makes
        /// and a setup should carry — and because a signal is not obliged to be encoded the way its
        /// format's name suggests. That is the whole of what <c>REQ-DEM-012</c> means by
        /// selectable.
        /// </para>
        /// </remarks>
        public DifferentialReference DifferentialReference { get; set; } =
            DifferentialReference.PerFormat;

        /// <summary>
        /// How many points a symbol the traces are drawn at (<c>REQ-DEM-034</c>).
        /// </summary>
        /// <remarks>
        /// Trace resolution only: it cannot change a measured metric, because every metric is
        /// computed at the symbol decision instants and this is read by step 14 alone. Distinct
        /// from <see cref="PointsPerSymbol"/>, which is the internal rate and must not follow it —
        /// <c>REQ-DEM-034a</c> gives the reason. A version 5 state file has no such member and its
        /// traces were drawn at the internal rate, which is this member's default.
        /// </remarks>
        public int DisplayPointsPerSymbol { get; set; } = 4;

        /// <summary>How many symbols the Result Length window holds (<c>REQ-DEM-031</c>).</summary>
        public int ResultLengthSymbols { get; set; } = 256;

        /// <summary>
        /// How long the Search Length window is, in symbols; zero for the whole record
        /// (<c>REQ-DEM-033</c>).
        /// </summary>
        /// <remarks>
        /// Symbols rather than samples, as that requirement states it: every other length here is
        /// in symbols, and one in samples would be the only number that changed meaning when the
        /// acquisition's sample rate did. A version 6 file's <c>searchLengthSamples</c> is not
        /// carried over — see the migration, which is the first that does anything.
        /// </remarks>
        public int SearchLengthSymbols { get; set; }

        /// <summary>The longest a pulse is expected to be on, in symbols (<c>REQ-DEM-033</c>).</summary>
        public int MaximumPulseOnSymbols { get; set; }

        /// <summary>The longest a pulse is expected to be off, in symbols (<c>REQ-DEM-033</c>).</summary>
        public int MaximumPulseOffSymbols { get; set; }

        /// <summary>Which measurement filter is applied (<c>REQ-DEM-021</c>).</summary>
        public PulseFilterType MeasurementFilter { get; set; } = PulseFilterType.RootRaisedCosine;

        /// <summary>Which reference filter shapes the ideal waveform (<c>REQ-DEM-020</c>).</summary>
        /// <remarks>
        /// The raised cosine, because the measured signal has already been through the
        /// transmitter's root and the analyser's matching half, and the composite of those is a
        /// raised cosine. A version 4 state file has no such member and meant exactly this, which is
        /// why the migration to 5 transforms nothing.
        /// </remarks>
        public PulseFilterType ReferenceFilter { get; set; } = PulseFilterType.RaisedCosine;

        /// <summary>The measurement filter's roll-off (<c>REQ-DEM-020</c>).</summary>
        public double MeasurementFilterAlpha { get; set; } = PulseFilter.DefaultAlpha;

        /// <summary>The reference filter's roll-off (<c>REQ-DEM-020</c>).</summary>
        public double ReferenceFilterAlpha { get; set; } = PulseFilter.DefaultAlpha;

        /// <summary>The measurement filter's bandwidth–time product, for the Gaussian.</summary>
        public double MeasurementFilterBandwidthTime { get; set; } =
            PulseFilter.DefaultBandwidthTime;

        /// <summary>The reference filter's bandwidth–time product, for the Gaussian.</summary>
        public double ReferenceFilterBandwidthTime { get; set; } =
            PulseFilter.DefaultBandwidthTime;

        /// <summary>The measurement filter's cutoff, as a fraction of the symbol rate.</summary>
        public double MeasurementFilterCutoff { get; set; } = PulseFilter.DefaultCutoff;

        /// <summary>The reference filter's cutoff, as a fraction of the symbol rate.</summary>
        public double ReferenceFilterCutoff { get; set; } = PulseFilter.DefaultCutoff;

        /// <summary>The taps of a user-defined measurement filter (<c>REQ-DEM-021</c>).</summary>
        /// <remarks>
        /// Empty unless the filter is a user-defined one, and empty by default: a state's members
        /// all have defaults per <c>REQ-STA-005</c>, and the default of a filter nobody supplied is
        /// no taps rather than a tap of zero.
        /// </remarks>
        public List<double> MeasurementFilterTaps { get; set; } = new List<double>();

        /// <summary>The taps of a user-defined reference filter (<c>REQ-DEM-021</c>).</summary>
        public List<double> ReferenceFilterTaps { get; set; } = new List<double>();

        /// <summary>How many samples a symbol the user's taps were given at.</summary>
        /// <remarks>
        /// Part of the filter rather than of the measurement: a tap list is a sampled function and
        /// nothing about the numbers says how fast it was sampled.
        /// </remarks>
        public int UserFilterSamplesPerSymbol { get; set; } = 4;

        /// <summary>
        /// How many symbols either side of centre the filters span (<c>REQ-DEM-023</c>).
        /// </summary>
        /// <remarks>
        /// Eight, the least that requirement recommends for a root raised cosine, and six until
        /// 24 August 2026. The measured trade it is the informed end of is in the user help, which
        /// is where that requirement asks for it.
        /// </remarks>
        public int FilterSymbolSpan { get; set; } = DemodSettings.DefaultFilterSymbolSpan;

        /// <summary>Whether the burst search of step 2 runs (<c>REQ-DEM-041</c>).</summary>
        public bool BurstSearch { get; set; }

        /// <summary>
        /// Whether the sync pattern search positions the Result Length window
        /// (<c>REQ-DEM-040</c>).
        /// </summary>
        public bool SyncSearch { get; set; }

        /// <summary>
        /// Whether to conjugate the input before analysis, for a signal that arrives the wrong way
        /// round (<c>REQ-DEM-035</c>).
        /// </summary>
        public bool MirrorSpectrum { get; set; }

        /// <summary>
        /// What the error metrics are expressed as a percentage of (<c>REQ-DEM-061</c>).
        /// </summary>
        /// <remarks>
        /// Saved because <c>REQ-DEM-072</c> asks that a measurement's provenance travel with saved
        /// states: an EVM figure recalled under a different normalisation from the one it was taken
        /// under is a different number, and the file is the only place that can remember which.
        /// </remarks>
        public EvmNormalisation EvmNormalisation { get; set; } = EvmNormalisation.RmsMagnitude;

        /// <summary>The value <see cref="EvmNormalisation.UserSpecified"/> uses.</summary>
        public double EvmNormalisationVolts { get; set; }

        /// <summary>Whether the adaptive equaliser of step 11 runs (<c>REQ-DEM-050</c>).</summary>
        public bool Equaliser { get; set; }

        /// <summary>How many taps the equaliser has (<c>REQ-DEM-051</c>).</summary>
        public int EqualiserLengthSymbols { get; set; } =
            DemodSettings.DefaultEqualiserLengthSymbols;

        /// <summary>
        /// Whether the equaliser adapts, is frozen, or is a unit impulse (<c>REQ-DEM-051</c>).
        /// </summary>
        public EqualiserMode EqualiserMode { get; set; } = EqualiserMode.Run;

        /// <summary>The LMS step size (<c>REQ-DEM-051</c>).</summary>
        public double EqualiserConvergenceFactor { get; set; } =
            DemodSettings.DefaultEqualiserConvergenceFactor;

        /// <summary>Which algorithm fits the coefficients (<c>REQ-DEM-052</c>).</summary>
        public EqualiserAlgorithm EqualiserAlgorithm { get; set; } =
            EqualiserAlgorithm.LeastSquares;

        /// <summary>
        /// How a gradient equaliser starts when its decisions cannot be trusted yet
        /// (<c>REQ-DEM-052</c>).
        /// </summary>
        public EqualiserAcquisition EqualiserAcquisition { get; set; } =
            EqualiserAcquisition.DecisionDirected;

        /// <summary>
        /// The EVM at which acquisition hands over to decision-directed adaptation, as a percentage
        /// (<c>REQ-DEM-052</c>).
        /// </summary>
        public double EqualiserAcquisitionEvmPercent { get; set; } =
            DemodSettings.DefaultEqualiserAcquisitionEvmPercent;

        /// <summary>
        /// How many times a gradient equaliser may sweep the block in one pass
        /// (<c>REQ-DEM-052</c>).
        /// </summary>
        public int EqualiserAdaptationSweeps { get; set; } =
            DemodSettings.DefaultEqualiserAdaptationSweeps;

        /// <summary>
        /// The coefficients this measurement's equaliser carries from one block to the next
        /// (<c>REQ-DEM-051</c>).
        /// </summary>
        /// <remarks>
        /// <para>
        /// <strong>One instance, for the life of the measurement.</strong>
        /// <see cref="ToSettings"/> builds a fresh <see cref="DemodSettings"/> for every block, so
        /// coefficients held on the settings would be born empty each time and Hold would hold
        /// nothing. This object is created once with the state and handed to each settings object in
        /// turn, which is what gives Run and Hold something to be defined across.
        /// </para>
        /// <para>
        /// <strong>Not a setting, and marked as one thing that is not.</strong> It is the result of
        /// measurements already taken rather than anything the user chose, so it is neither written
        /// to a state file nor walked by the checks that hold every setting to save-and-recall:
        /// recalling a setup should restore the mode and the filter length the user picked, and
        /// should not restore an equaliser fitted to a channel that is no longer connected.
        /// </para>
        /// </remarks>
        [NotASetting]
        [JsonIgnore]
        public EqualiserState EqualiserAdaptation { get; } = new EqualiserState();

        /// <summary>The symbol rate a newly selected demodulation starts at (<c>REQ-DEM-030</c>).</summary>
        /// <param name="spanHz">The measurement's span.</param>
        /// <returns>Half the span.</returns>
        public static double DefaultSymbolRateFor(double spanHz) => spanHz / 2.0;

        /// <summary>
        /// The chain's settings for this state.
        /// </summary>
        /// <returns>A settings object the demodulator can be run with.</returns>
        /// <exception cref="ArgumentException">
        /// The format is not one this build demodulates, or a setting is outside its range.
        /// </exception>
        /// <remarks>
        /// Validated here rather than at the point of use: a setup that cannot be demodulated should
        /// say so when it is applied, not once per acquired block on the pump thread.
        /// </remarks>
        public DemodSettings ToSettings()
        {
            var settings = new DemodSettings
            {
                Constellation = Labelled(Resolve()),
                DifferentialReference = DifferentialReference,
                MeasurementFilter = MeasurementFilter,
                ReferenceFilter = ReferenceFilter,
                MeasurementFilterBandwidthTime = MeasurementFilterBandwidthTime,
                ReferenceFilterBandwidthTime = ReferenceFilterBandwidthTime,
                MeasurementFilterCutoff = MeasurementFilterCutoff,
                ReferenceFilterCutoff = ReferenceFilterCutoff,
                MeasurementFilterTaps =
                    MeasurementFilterTaps == null || MeasurementFilterTaps.Count == 0
                        ? null
                        : MeasurementFilterTaps.ToArray(),
                ReferenceFilterTaps =
                    ReferenceFilterTaps == null || ReferenceFilterTaps.Count == 0
                        ? null
                        : ReferenceFilterTaps.ToArray(),
                UserFilterSamplesPerSymbol = UserFilterSamplesPerSymbol,
                SymbolRateHz = SymbolRateHz,
                PointsPerSymbol = PointsPerSymbol,
                DisplayPointsPerSymbol = DisplayPointsPerSymbol,
                ResultLengthSymbols = ResultLengthSymbols,
                SearchLengthSymbols = SearchLengthSymbols,
                MaximumPulseOnSymbols = MaximumPulseOnSymbols,
                MaximumPulseOffSymbols = MaximumPulseOffSymbols,
                MeasurementFilterAlpha = MeasurementFilterAlpha,
                ReferenceFilterAlpha = ReferenceFilterAlpha,
                FilterSymbolSpan = FilterSymbolSpan,
                BurstSearchEnabled = BurstSearch,
                SyncSearchEnabled = SyncSearch,
                MirrorSpectrum = MirrorSpectrum,
                EvmNormalisation = EvmNormalisation,
                EvmNormalisationVolts = EvmNormalisationVolts,
                EqualiserEnabled = Equaliser,
                EqualiserLengthSymbols = EqualiserLengthSymbols,
                EqualiserMode = EqualiserMode,
                EqualiserConvergenceFactor = EqualiserConvergenceFactor,
                EqualiserAlgorithm = EqualiserAlgorithm,
                EqualiserAcquisition = EqualiserAcquisition,
                EqualiserAcquisitionEvmPercent = EqualiserAcquisitionEvmPercent,
                EqualiserAdaptationSweeps = EqualiserAdaptationSweeps,
                EqualiserState = EqualiserAdaptation,
            };

            settings.Validate();

            return settings;
        }

        /// <summary>
        /// The constellation this state describes: a catalogue format, or the user's own.
        /// </summary>
        /// <returns>The constellation, before its labelling is applied.</returns>
        /// <exception cref="ArgumentException">
        /// The definition is both rings and points, or neither and the format is not one this build
        /// knows.
        /// </exception>
        /// <remarks>
        /// A definition present at all is what makes the format a user-defined one, so a state that
        /// carries one is not consulted for a catalogue name — otherwise a file naming <c>QPSK</c>
        /// and carrying four rings would measure one of them, and which would depend on the order
        /// the two were read in.
        /// </remarks>
        private Constellation Resolve()
        {
            bool rings = CustomRings != null && CustomRings.Count > 0;
            bool points = CustomPoints != null && CustomPoints.Count > 0;

            if (rings && points)
            {
                throw new ArgumentException(
                    "A user-defined constellation is either rings or points, and this state has " +
                    CustomRings.Count + " ring(s) and " + CustomPoints.Count + " point(s). " +
                    "Whichever was preferred would decide what was measured, so neither is.");
            }

            if (rings)
            {
                var specification = new List<Constellation.ApskRing>(CustomRings.Count);

                foreach (ApskRingState ring in CustomRings)
                {
                    specification.Add(
                        new Constellation.ApskRing(
                            ring.Radius,
                            ring.Points,
                            ring.PhaseDegrees * Math.PI / 180.0));
                }

                return Constellation.Apsk(Format, specification);
            }

            if (points)
            {
                var list = new List<ConstellationPoint>(CustomPoints.Count);
                var ordered = new List<ConstellationPointState>(CustomPoints);

                // The requirement's point list is (I, Q, symbol value), so the order a file happens
                // to hold them in is not the order they mean. Sorted by the value each one carries,
                // which is what Constellation indexes by.
                ordered.Sort((first, second) => first.Symbol.CompareTo(second.Symbol));

                for (int index = 0; index < ordered.Count; index++)
                {
                    if (ordered[index].Symbol != index)
                    {
                        throw new ArgumentException(
                            "A point list gives every symbol value from 0 to " +
                            (ordered.Count - 1) + " exactly once; this one has " +
                            ordered[index].Symbol + " where " + index + " should be.");
                    }

                    list.Add(new ConstellationPoint(ordered[index].I, ordered[index].Q));
                }

                return Constellation.FromPoints(Format, list, LevelsIn(list));
            }

            return Constellation.ByName(Format);
        }

        /// <summary>Applies this state's labelling to a constellation.</summary>
        private Constellation Labelled(Constellation constellation)
        {
            if (BitMapping == BitMapping.Explicit)
            {
                return constellation.WithMapping(
                    BitMappingTable ?? new List<int>());
            }

            return constellation.WithMapping(BitMapping);
        }

        /// <summary>Distinct levels on the I axis, counted from the points.</summary>
        /// <remarks>
        /// Counted rather than declared, for the reason <c>REQ-DEM-010</c>'s catalogue counts them:
        /// an eight-point ring has five distinct cosines and not eight, and a declared number was
        /// wrong the one time it was declared.
        /// </remarks>
        private static int LevelsIn(IList<ConstellationPoint> points)
        {
            var levels = new HashSet<double>();

            foreach (ConstellationPoint point in points)
            {
                levels.Add(Math.Round(point.I, 6));
            }

            return levels.Count;
        }

        /// <inheritdoc />
        public override string ToString() =>
            Format + " at " + SymbolRateHz.ToString("G6", CultureInfo.InvariantCulture) +
            " symbols/s, " + ResultLengthSymbols.ToString(CultureInfo.InvariantCulture) + " symbols";
    }

    /// <summary>One ring of a user-defined constellation, as a state file holds it.</summary>
    /// <remarks>
    /// Degrees rather than radians, because a state file is meant to be read and edited by a person
    /// (<c>REQ-STA-003</c>) and nobody writes a ring offset as 0.7853981633974483.
    /// </remarks>
    public sealed class ApskRingState
    {
        /// <summary>The ring's radius, in the same arbitrary units as the other rings.</summary>
        public double Radius { get; set; } = 1.0;

        /// <summary>How many points are spaced evenly around it.</summary>
        public int Points { get; set; } = 4;

        /// <summary>Where its first point sits, anticlockwise from the I axis, in degrees.</summary>
        public double PhaseDegrees { get; set; }
    }

    /// <summary>One point of a user-defined constellation, as a state file holds it.</summary>
    /// <remarks>
    /// <c>REQ-DEM-011</c>'s point list is <em>(I, Q, symbol value)</em> — the value is carried with
    /// the coordinates rather than implied by the position in the file, so that a list can be
    /// reordered, diffed and merged without changing what it means.
    /// </remarks>
    public sealed class ConstellationPointState
    {
        /// <summary>The in-phase coordinate.</summary>
        public double I { get; set; }

        /// <summary>The quadrature coordinate.</summary>
        public double Q { get; set; }

        /// <summary>Which symbol value sits here.</summary>
        public int Symbol { get; set; }
    }

    /// <summary>
    /// One vertex of a saved limit line (<c>REQ-LIM-001</c>).
    /// </summary>
    public sealed class LimitPointState
    {
        /// <summary>Frequency, in hertz.</summary>
        public double XHz { get; set; }

        /// <summary>Level, in dBm.</summary>
        public double YDbm { get; set; }

        /// <summary>
        /// Whether a segment runs from the previous point to this one.
        /// </summary>
        /// <remarks>
        /// Saved because it is not decoration: a point with it clear starts a new segment, and the
        /// gap before it is not tested at all. A recalled mask that lost these flags would test
        /// bands the original left unconstrained, and would do it silently — the line would look
        /// right on screen apart from the gaps being filled in.
        /// </remarks>
        public bool ConnectToPrevious { get; set; } = true;
    }

    /// <summary>
    /// A saved limit line: its name, its side, its margin and its points (<c>REQ-LIM-001</c>).
    /// </summary>
    public sealed class LimitLineState
    {
        /// <summary>User-facing name.</summary>
        public string Name { get; set; } = "Limit 1";

        /// <summary>Which side of the line the trace must stay on: <c>Upper</c> or <c>Lower</c>.</summary>
        public LimitSide Side { get; set; } = LimitSide.Upper;

        /// <summary>Margin in dB, applied on the passing side.</summary>
        public double MarginDb { get; set; }

        /// <summary>The vertices, in order.</summary>
        public List<LimitPointState> Points { get; set; } = new List<LimitPointState>();
    }

    /// <summary>
    /// A saved limit test: a name, whether it is enabled, and its lines (<c>REQ-LIM-001</c>).
    /// </summary>
    /// <remarks>
    /// The third level of the hierarchy the requirement asks for. All three are named and all
    /// three names are saved, so a failure recalled a week later can still be reported as "which
    /// test, which line, where" rather than as a bare verdict against an anonymous shape.
    /// </remarks>
    public sealed class LimitTestState
    {
        /// <summary>User-facing name.</summary>
        public string Name { get; set; } = "Limit Test 1";

        /// <summary>Whether this test is evaluated at all.</summary>
        public bool IsEnabled { get; set; } = true;

        /// <summary>The lines belonging to this test.</summary>
        public List<LimitLineState> Lines { get; set; } = new List<LimitLineState>();
    }

    /// <summary>
    /// The source (tracking generator) settings a state carries (<c>REQ-STA-001</c>).
    /// </summary>
    public sealed class SourceState
    {
        /// <summary>Whether the source is on.</summary>
        public bool IsEnabled { get; set; }

        /// <summary>Source frequency, in hertz.</summary>
        public double FrequencyHz { get; set; } = 1e9;

        /// <summary>Source level, in dBm.</summary>
        public double LevelDbm { get; set; } = -30.0;

        /// <summary>What the source produces, as it is labelled.</summary>
        public string Waveform { get; set; } = "CW";
    }

    /// <summary>
    /// One measurement context's saved settings (<c>REQ-STA-001</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Plain settable properties throughout, and that is deliberate.</strong> The
    /// requirement's criterion is that the check <em>enumerates</em> the state rather than sampling
    /// it, so that a setting added here without save and recall support fails a test rather than
    /// going unnoticed. That is only possible if the state is walkable by reflection, which rules
    /// out behaviour hidden in accessors and hand-written serialisation per member.
    /// </para>
    /// <para>
    /// <strong>What is deliberately absent is as important as what is here.</strong>
    /// <c>REQ-STA-002</c> excludes recordings, math functions, data registers and display
    /// preferences from a state; they are saved through <see cref="SidecarState"/> instead. A
    /// property for any of them appearing here is a defect, and a test asserts their absence by
    /// name.
    /// </para>
    /// </remarks>
    public sealed class MeasurementState
    {
        /// <summary>
        /// The context this measurement belongs to.
        /// </summary>
        /// <remarks>
        /// What <c>REQ-STA-004</c> matches on when a multi-measurement state is recalled: a name,
        /// not a position, so that reordering contexts does not silently apply one measurement's
        /// settings to another.
        /// </remarks>
        public string ContextName { get; set; } = "Measurement 1";

        /// <summary>What kind of measurement this is.</summary>
        public MeasurementKind Kind { get; set; } = MeasurementKind.Spectrum;

        /// <summary>Centre frequency, in hertz.</summary>
        public double CenterFrequencyHz { get; set; } = 1e9;

        /// <summary>Span, in hertz.</summary>
        public double SpanHz { get; set; } = 10e6;

        /// <summary>Resolution bandwidth, in hertz (<c>REQ-DSP-020</c>).</summary>
        public double ResolutionBandwidthHz { get; set; } = 100e3;

        /// <summary>Whether the resolution bandwidth follows the point count.</summary>
        public bool ResolutionBandwidthIsAutomatic { get; set; } = true;

        /// <summary>Trigger configuration.</summary>
        public TriggerState Trigger { get; set; } = new TriggerState();

        /// <summary>Input settings.</summary>
        public InputState Input { get; set; } = new InputState();

        /// <summary>Analysis parameters.</summary>
        public AnalysisState Analysis { get; set; } = new AnalysisState();

        /// <summary>The digital demodulator's settings (<c>REQ-DEM-001</c>).</summary>
        /// <remarks>
        /// Carried whatever the measurement's kind is, so that switching to digital demodulation and
        /// back does not lose what was set up. <c>REQ-ARC-002a</c> asks the same of a front-end
        /// change, for the same reason: a setting the user chose surviving is the difference between
        /// changing a mode and starting again.
        /// </remarks>
        public DemodState Demod { get; set; } = new DemodState();

        /// <summary>Source parameters.</summary>
        public SourceState Source { get; set; } = new SourceState();

        /// <summary>Trace window positions and overlay state.</summary>
        public List<TraceWindowState> Windows { get; set; } = new List<TraceWindowState>
        {
            new TraceWindowState(),
        };

        /// <summary>Trace display properties.</summary>
        public List<TraceDisplayState> Traces { get; set; } = new List<TraceDisplayState>
        {
            new TraceDisplayState(),
        };

        /// <summary>Markers: types, positions and calculations.</summary>
        public List<MarkerState> Markers { get; set; } = new List<MarkerState>();

        /// <summary>
        /// Limit tests, with their lines and points (<c>REQ-LIM-001</c>).
        /// </summary>
        /// <remarks>
        /// Empty by default. A limit line is something a user drew or imported, so a new
        /// measurement has none — unlike a marker, where one is the useful starting point.
        /// </remarks>
        public List<LimitTestState> LimitTests { get; set; } = new List<LimitTestState>();

        /// <summary>
        /// Changes what kind of measurement this is, applying the defaults a first selection brings
        /// with it.
        /// </summary>
        /// <param name="kind">The kind to change to.</param>
        /// <remarks>
        /// <para>
        /// <strong><c>REQ-DEM-030</c>'s Span/2.</strong> "On first selection of digital
        /// demodulation the default shall be Span/2" — a default at the moment of choosing, and
        /// only when no rate has been chosen before. Applying it on every selection would discard a
        /// rate the user had entered the moment they looked at the spectrum and came back, and
        /// applying it inside the demodulator would be the estimation that same requirement
        /// forbids.
        /// </para>
        /// <para>
        /// A method rather than a setter on <see cref="Kind"/> because the state is walked by
        /// reflection for save and recall (<c>REQ-STA-005</c>), and behaviour hidden in an accessor
        /// would run during a recall — turning "load the setup I saved" into "load it and then
        /// change the symbol rate".
        /// </para>
        /// </remarks>
        public void SelectKind(MeasurementKind kind)
        {
            if (kind == MeasurementKind.DigitalDemodulation &&
                Kind != MeasurementKind.DigitalDemodulation &&
                Demod.SymbolRateHz <= 0.0)
            {
                Demod.SymbolRateHz = DemodState.DefaultSymbolRateFor(SpanHz);
            }

            Kind = kind;
        }

        /// <inheritdoc />
        public override string ToString() =>
            "'" + ContextName + "': " + Kind + " at " +
            (CenterFrequencyHz / 1e6).ToString("0.###", System.Globalization.CultureInfo.CurrentCulture) +
            " MHz";
    }
}
