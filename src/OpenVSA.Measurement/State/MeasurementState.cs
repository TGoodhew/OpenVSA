using System;
using System.Collections.Generic;
using OpenVSA.Core;
using OpenVSA.Dsp.Spectrum;
using OpenVSA.Dsp.Windowing;
using OpenVSA.Hal;
using OpenVSA.Measurement.Limits;

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

        /// <inheritdoc />
        public override string ToString() =>
            "'" + ContextName + "': " + Kind + " at " +
            (CenterFrequencyHz / 1e6).ToString("0.###", System.Globalization.CultureInfo.CurrentCulture) +
            " MHz";
    }
}
