using System;
using System.Collections.Generic;
using System.Globalization;
using OpenVSA.Core;
using OpenVSA.Dsp.Spectrum;
using OpenVSA.Dsp.Windowing;
using OpenVSA.Measurement;

namespace OpenVSA.Ui.Dialogs.Pages
{
    /// <summary>
    /// The <strong>Frequency</strong> tab: what band is analysed and how finely
    /// (<c>REQ-UI-072</c>).
    /// </summary>
    public sealed class FrequencyPage : AnalysisPage
    {
        /// <summary>Creates the page.</summary>
        /// <param name="settings">The settings to edit.</param>
        public FrequencyPage(AnalysisSettings settings)
            : base(settings)
        {
            AddNote(
                "Centre and span say which band is analysed; the point count says how finely it " +
                "is divided. Ranges are the front end's own and are checked when the acquisition " +
                "is planned.");

            AddNumber(
                "Centre frequency",
                () => Settings.CenterFrequencyHz,
                v => Settings.CenterFrequencyHz = v,
                Frequency,
                ParseFrequency);

            AddNumber(
                "Span",
                () => Settings.SpanHz,
                v => Settings.SpanHz = v,
                Frequency,
                ParseFrequency);

            AddChoice(
                "Frequency points",
                FrequencyPoints.Supported,
                p => p.ToString(CultureInfo.CurrentCulture),
                () => Settings.FrequencyPoints,
                p => Settings.FrequencyPoints = p);

            AddCheck(
                "Points follow the resolution bandwidth",
                () => Settings.PointsAreAutomatic,
                on => Settings.PointsAreAutomatic = on);

            AddChoice(
                "On a span change",
                new[] { SpanChangeBehaviour.Zoom, SpanChangeBehaviour.HoldStartFrequency },
                Describe,
                () => Settings.SpanChange,
                b => Settings.SpanChange = b);

            AddFollowingNote(() =>
                "Start " + Frequency(Settings.CenterFrequencyHz - Settings.SpanHz / 2.0) +
                ", stop " + Frequency(Settings.CenterFrequencyHz + Settings.SpanHz / 2.0) + ".");

            Refresh();
        }

        private static string Describe(SpanChangeBehaviour behaviour) =>
            behaviour == SpanChangeBehaviour.Zoom
                ? "Zoom — hold the centre frequency"
                : "Hold the start frequency";
    }

    /// <summary>
    /// The <strong>ResBW</strong> tab: resolution bandwidth, its coupling, and the window that
    /// defines it (<c>REQ-UI-072</c>).
    /// </summary>
    public sealed class ResolutionBandwidthPage : AnalysisPage
    {
        /// <summary>Creates the page.</summary>
        /// <param name="settings">The settings to edit.</param>
        public ResolutionBandwidthPage(AnalysisSettings settings)
            : base(settings)
        {
            AddNote(
                "The resolution bandwidth is the analysis window's equivalent noise bandwidth " +
                "over the time record, so the window belongs on this tab and not on one of its " +
                "own — choosing one without the other is choosing half a setting.");

            AddNumber(
                "Resolution bandwidth",
                () => Settings.ResolutionBandwidthHz,
                v => Settings.ResolutionBandwidthHz = v,
                Frequency,
                ParseFrequency);

            AddCheck(
                "Couple to the span",
                () => Settings.ResolutionBandwidthIsAutomatic,
                on => Settings.ResolutionBandwidthIsAutomatic = on);

            AddNumber(
                "Span : ResBW ratio",
                () => Settings.SpanToRatio,
                v => Settings.SpanToRatio = v,
                Plain,
                ParsePlain);

            AddChoice(
                "Window",
                Windows(),
                WindowText.Describe,
                () => Settings.Window,
                w => Settings.Window = w);

            AddFollowingNote(() =>
                "Time record " + Time(
                    ResolutionBandwidth.RecordLengthFor(
                        Window.Get(Settings.Window, EnbwReferenceLength).Enbw,
                        Settings.ResolutionBandwidthHz)) +
                (Settings.ResolutionBandwidthIsAutomatic
                    ? ", coupled at " + Plain(Settings.SpanToRatio) + ":1."
                    : ", uncoupled."));

            Refresh();
        }

        /// <summary>
        /// The length the window's equivalent noise bandwidth is read at.
        /// </summary>
        /// <remarks>
        /// ENBW is a property of the window's shape and converges within a fraction of a per cent
        /// well below this; a fixed reference length keeps the note on this tab from moving as the
        /// transform size changes, which would look like the resolution bandwidth drifting.
        /// </remarks>
        private const int EnbwReferenceLength = 4096;

        private static IEnumerable<WindowType> Windows()
        {
            foreach (WindowType type in (WindowType[])Enum.GetValues(typeof(WindowType)))
            {
                yield return type;
            }
        }
    }

    /// <summary>
    /// The <strong>Time</strong> tab: the time record, the gate over it, and frame overlap
    /// (<c>REQ-UI-072</c>).
    /// </summary>
    public sealed class TimePage : AnalysisPage
    {
        /// <summary>Creates the page.</summary>
        /// <param name="settings">The settings to edit.</param>
        public TimePage(AnalysisSettings settings)
            : base(settings)
        {
            AddNote(
                "Main time length is derived from the point count and the span, not set here: " +
                "the three are two degrees of freedom, and a setting that could disagree with " +
                "its own arithmetic would be worse than one that is read-only.");

            AddDerived("Main time length", () => Time(Settings.MainTimeSeconds));

            AddNumber(
                "Frame overlap",
                () => Settings.Overlap,
                v => Settings.Overlap = v,
                Plain,
                ParsePlain);

            AddCheck(
                "Gate the time record",
                () => Settings.GateEnabled,
                on => Settings.GateEnabled = on);

            AddNumber(
                "Gate delay",
                () => Settings.GateDelaySeconds,
                v => Settings.GateDelaySeconds = v,
                Time,
                ParseTime);

            AddNumber(
                "Gate length",
                () => Settings.GateLengthSeconds,
                v => Settings.GateLengthSeconds = v,
                Time,
                ParseTime);

            AddFollowingNote(() =>
                Settings.GateEnabled
                    ? "Gating shortens the record the transform sees, so it coarsens the " +
                      "resolution bandwidth in proportion."
                    : "Ungated: the transform sees the whole record.");

            Refresh();
        }
    }

    /// <summary>
    /// The <strong>Detectors</strong> tab: how points sharing a pixel column are reduced
    /// (<c>REQ-UI-072</c>).
    /// </summary>
    public sealed class DetectorPage : AnalysisPage
    {
        /// <summary>Creates the page.</summary>
        /// <param name="settings">The settings to edit.</param>
        public DetectorPage(AnalysisSettings settings)
            : base(settings)
        {
            AddNote(
                "A detector is a display decision. Every point is still computed and every point " +
                "is still what a marker reads; the detector says only what to draw when a column " +
                "covers more points than it has pixels.");

            AddCheck(
                "Follow the averaging",
                () => Settings.DetectorIsAutomatic,
                on => Settings.DetectorIsAutomatic = on);

            AddChoice(
                "Detector",
                TraceDetection.All,
                TraceDetection.NameOf,
                () => Settings.Detector,
                d => Settings.Detector = d);

            AddFollowingNote(() =>
                TraceDetection.Describe(Settings.Detector) +
                (Settings.DetectorIsAutomatic
                    ? " Coupled: this follows the averaging, and choosing a detector here " +
                      "uncouples it."
                    : " Uncoupled: chosen by hand, and it stays chosen when the averaging changes."));

            AddDerived(
                "Points per column",
                () => Settings.FrequencyPoints.ToString(CultureInfo.CurrentCulture) +
                      " points across the graticule; below one point per column the trace is " +
                      "interpolated and the detector has nothing to choose between.");

            AddFollowingNote(() =>
                Settings.Detector == TraceDetector.Average
                    ? "The average is taken in power, not in decibels. Averaging dB values reads " +
                      "low by an amount that grows with the scatter — worst on the noise floor, " +
                      "which is where this detector is most used."
                    : string.Empty);

            Refresh();
        }
    }

    /// <summary>
    /// The <strong>Conversion</strong> tab: the acquisition path and the transform it feeds
    /// (<c>REQ-UI-072</c>).
    /// </summary>
    public sealed class ConversionPage : AnalysisPage
    {
        /// <summary>Creates the page.</summary>
        /// <param name="settings">The settings to edit.</param>
        public ConversionPage(AnalysisSettings settings)
            : base(settings)
        {
            AddNote(
                "How the input is converted before it is transformed: which analysis path the " +
                "acquisition takes, how large a transform it may use, and whether the " +
                "instrument's own noise floor is taken off the result.");

            AddChoice(
                "Analysis path",
                new[] { AnalysisPath.ComplexZoom, AnalysisPath.RealBaseband },
                Describe,
                () => Settings.Path,
                p => Settings.Path = p);

            AddChoice(
                "Maximum transform size",
                TransformSizes(),
                n => n.ToString(CultureInfo.CurrentCulture),
                () => Settings.MaxTransformLength,
                n => Settings.MaxTransformLength = n);

            AddCheck(
                "Noise correction",
                () => Settings.NoiseCorrection,
                on => Settings.NoiseCorrection = on);

            AddFollowingNote(() =>
                "Sample rate " +
                Frequency(AcquisitionLaw.SampleRateFor(Settings.SpanHz, Settings.Path)) +
                " for a span of " + Frequency(Settings.SpanHz) + "." +
                (Settings.NoiseCorrection
                    ? " A corrected trace carries no phase: subtracting an incoherent power leaves " +
                      "a magnitude."
                    : string.Empty));

            Refresh();
        }

        private static string Describe(AnalysisPath path) =>
            path == AnalysisPath.ComplexZoom
                ? "Complex zoom — the analytic signal"
                : "Real baseband — a real signal from 0 Hz";

        private static IEnumerable<int> TransformSizes()
        {
            for (int size = 256; size <= 1 << 22; size <<= 1)
            {
                yield return size;
            }
        }
    }

    /// <summary>
    /// The <strong>Average</strong> tab: what kind of averaging, how much of it, and what happens
    /// when it finishes (<c>REQ-UI-072</c>).
    /// </summary>
    public sealed class AveragePage : AnalysisPage
    {
        /// <summary>Creates the page.</summary>
        /// <param name="settings">The settings to edit.</param>
        public AveragePage(AnalysisSettings settings)
            : base(settings)
        {
            AddNote(
                "Averaging trades time for a lower noise floor. Which kind matters as much as how " +
                "much: RMS averaging discards phase, and a trace without phase cannot be shown in " +
                "the phase formats or used for group delay.");

            AddChoice(
                "Averaging",
                Types(),
                Describe,
                () => Settings.Averaging,
                t => Settings.Averaging = t);

            AddNumber(
                "Average count",
                () => Settings.AverageCount,
                v => Settings.AverageCount = (int)Math.Round(v),
                v => ((int)Math.Round(v)).ToString(CultureInfo.CurrentCulture),
                ParsePlain);

            AddCheck(
                "Repeat when the average completes",
                () => Settings.RepeatAverage,
                on => Settings.RepeatAverage = on);

            AddFollowingNote(() =>
                Settings.Overlap > 0.0
                    ? "Frames overlap by " + Plain(Settings.Overlap * 100.0) + " %, so " +
                      Settings.AverageCount + " averages are worth fewer than " +
                      Settings.AverageCount + " independent ones."
                    : "Frames do not overlap, so every average is independent.");

            Refresh();
        }

        private static IEnumerable<AveragingType> Types()
        {
            foreach (AveragingType type in (AveragingType[])Enum.GetValues(typeof(AveragingType)))
            {
                yield return type;
            }
        }

        private static string Describe(AveragingType type)
        {
            switch (type)
            {
                case AveragingType.Off: return "Off";
                case AveragingType.RmsVideo: return "RMS video — power, to the count; discards phase";
                case AveragingType.RmsVideoExponential: return "RMS video, exponential — runs on";
                case AveragingType.Time: return "Time — coherent, to the count; keeps phase";
                case AveragingType.TimeExponential: return "Time, exponential — runs on";
                case AveragingType.PeakHold: return "Peak hold — to the count";
                case AveragingType.ContinuousPeakHold: return "Peak hold, continuous — runs on";
            }

            return type.ToString();
        }
    }

    /// <summary>
    /// The <strong>Heatmaps</strong> tab: the accumulating displays of <c>REQ-TRC-001a</c>
    /// (<c>REQ-UI-072</c>).
    /// </summary>
    public sealed class HeatmapPage : AnalysisPage
    {
        /// <summary>Creates the page.</summary>
        /// <param name="settings">The settings to edit.</param>
        public HeatmapPage(AnalysisSettings settings)
            : base(settings)
        {
            AddNote(
                "The accumulating displays: a spectrogram of successive traces, a persistence " +
                "display that fades them, and a cumulative history that does not. These are an " +
                "axis rather than a format — changing the accumulator discards what has been " +
                "accumulated, while changing the format does not.");

            AddChoice(
                "Accumulator",
                Accumulators(),
                Describe,
                () => Settings.Accumulator,
                a => Settings.Accumulator = a);

            AddNumber(
                "Rows kept",
                () => Settings.HeatmapDepth,
                v => Settings.HeatmapDepth = (int)Math.Round(v),
                v => ((int)Math.Round(v)).ToString(CultureInfo.CurrentCulture),
                ParsePlain);

            AddNumber(
                "Colour range",
                () => Settings.HeatmapRangeDb,
                v => Settings.HeatmapRangeDb = v,
                v => Plain(v) + " dB",
                text =>
                {
                    double parsed;

                    return EngineeringText.TryParseDecibels(text, out parsed)
                        ? parsed
                        : (double?)null;
                });

            AddNumber(
                "Persistence",
                () => Settings.PersistenceSeconds,
                v => Settings.PersistenceSeconds = v,
                Time,
                ParseTime);

            AddFollowingNote(() =>
                Settings.Accumulator == TraceAccumulator.None
                    ? "No accumulation: the trace shows the current acquisition."
                    : Describe(Settings.Accumulator) + " over " + Settings.HeatmapDepth +
                      " rows, coloured across " + Plain(Settings.HeatmapRangeDb) + " dB.");

            Refresh();
        }

        private static IEnumerable<TraceAccumulator> Accumulators()
        {
            foreach (TraceAccumulator accumulator in
                (TraceAccumulator[])Enum.GetValues(typeof(TraceAccumulator)))
            {
                yield return accumulator;
            }
        }

        private static string Describe(TraceAccumulator accumulator)
        {
            switch (accumulator)
            {
                case TraceAccumulator.None: return "None";
                case TraceAccumulator.Spectrogram: return "Spectrogram";
                case TraceAccumulator.DigitalPersistence: return "Digital persistence";
                case TraceAccumulator.CumulativeHistory: return "Cumulative history";
            }

            return accumulator.ToString();
        }
    }
}
