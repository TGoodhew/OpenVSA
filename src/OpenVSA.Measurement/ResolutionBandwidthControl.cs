using System;
using System.Collections.Generic;
using System.Globalization;
using OpenVSA.Core;
using OpenVSA.Dsp.Spectrum;
using OpenVSA.Dsp.Windowing;
using OpenVSA.Hal;

namespace OpenVSA.Measurement
{
    /// <summary>
    /// How a resolution bandwidth is chosen (<c>REQ-DSP-021</c>'s <em>ResBW Mode</em>).
    /// </summary>
    public enum ResolutionBandwidthMode
    {
        /// <summary>
        /// Any bandwidth the instrument can reach is used exactly as asked for.
        /// </summary>
        /// <remarks>
        /// The vector analyser's own behaviour, and the reason it can hit a stated RBW rather than
        /// the nearest one on a ladder: the record length is simply solved for.
        /// </remarks>
        Arbitrary = 0,

        /// <summary>
        /// Bandwidths are restricted to a swept spectrum analyser's 1–3–10 ladder.
        /// </summary>
        /// <remarks>
        /// For comparing against a measurement made on a swept analyser, where the filters exist in
        /// hardware and only those values are available. Choosing it changes the numbers: a
        /// coupled sweep steps down a ladder instead of tracking the span continuously.
        /// </remarks>
        SpectrumAnalyser,
    }

    /// <summary>
    /// Whether the resolution bandwidth follows the span (<c>REQ-DSP-021</c>'s
    /// <em>ResBW Coupling</em>).
    /// </summary>
    public enum ResolutionBandwidthCoupling
    {
        /// <summary>RBW tracks the span, holding the span-to-RBW ratio.</summary>
        Coupled = 0,

        /// <summary>RBW is held where it was put, whatever the span does.</summary>
        Uncoupled,
    }

    /// <summary>
    /// The resolution bandwidths reachable at a setting, and why the ends are where they are.
    /// </summary>
    public readonly struct ResolutionBandwidthRange
    {
        /// <summary>Finest reachable bandwidth, in hertz.</summary>
        public readonly double MinHz;

        /// <summary>Coarsest reachable bandwidth, in hertz.</summary>
        public readonly double MaxHz;

        /// <summary>Creates a range.</summary>
        /// <param name="minHz">Finest reachable bandwidth, in hertz.</param>
        /// <param name="maxHz">Coarsest reachable bandwidth, in hertz.</param>
        /// <exception cref="ArgumentOutOfRangeException">The bounds are not positive or are inverted.</exception>
        public ResolutionBandwidthRange(double minHz, double maxHz)
        {
            if (!(minHz > 0.0) || double.IsInfinity(minHz))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(minHz), minHz, "A resolution bandwidth must be positive and finite.");
            }

            if (!(maxHz >= minHz) || double.IsInfinity(maxHz))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(maxHz), maxHz, "The coarsest bandwidth cannot be finer than the finest.");
            }

            MinHz = minHz;
            MaxHz = maxHz;
        }

        /// <summary>Whether a bandwidth is reachable, inclusive.</summary>
        /// <param name="hz">Bandwidth to test, in hertz.</param>
        public bool Contains(double hz) => hz >= MinHz && hz <= MaxHz;

        /// <inheritdoc />
        public override string ToString() =>
            EngineeringHertz(MinHz) + " to " + EngineeringHertz(MaxHz);

        internal static string EngineeringHertz(double hertz)
        {
            double magnitude = Math.Abs(hertz);

            if (magnitude >= 1e6)
            {
                return (hertz / 1e6).ToString("0.####", CultureInfo.CurrentCulture) + " MHz";
            }

            if (magnitude >= 1e3)
            {
                return (hertz / 1e3).ToString("0.####", CultureInfo.CurrentCulture) + " kHz";
            }

            return hertz.ToString("0.####", CultureInfo.CurrentCulture) + " Hz";
        }
    }

    /// <summary>
    /// The resolution-bandwidth setting: its range, its mode, and how it tracks span
    /// (<c>REQ-DSP-021</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The relation is <see cref="ResolutionBandwidth"/>'s; what is here is the policy.</strong>
    /// <c>RBW = ENBW / T_rec</c> is stated once, in the DSP layer, and this never restates it —
    /// every bandwidth this class hands out is one that relation produced, so the annotation and
    /// the record length cannot drift apart.
    /// </para>
    /// <para>
    /// <strong>Rejected, not clamped.</strong> A bandwidth the instrument cannot reach is refused
    /// with the reachable bound named. Clamping it would hand back a measurement at a resolution
    /// the user did not choose and did not hear about — and RBW is the setting a reader is least
    /// able to infer from the trace, because a spectrum measured at the wrong RBW looks entirely
    /// normal.
    /// </para>
    /// <para>
    /// <strong>The two ends come from different places, and neither is a constant.</strong> The
    /// finest bandwidth is the longest record this front end can capture over this span, read
    /// through <see cref="AcquisitionPlanner.MaximumPointsFor"/> — so a deep-capture source
    /// reaches below 1 Hz at a narrow span and a shallow one does not, without either being
    /// special-cased. The coarsest is <see cref="CoarsestFractionOfSpan"/> of the span.
    /// </para>
    /// </remarks>
    public sealed class ResolutionBandwidthControl
    {
        /// <summary>
        /// The coarsest bandwidth offered, as a fraction of span.
        /// </summary>
        /// <remarks>
        /// <c>REQ-DSP-021</c> requires better than 0.287 of the span, which is the reference
        /// product's figure; 0.3 is that rounded to something a person can hold in their head, and
        /// it is a stated design figure rather than a measured one. Past about a third of the span
        /// the "resolution" bandwidth covers a third of the display and the word has stopped
        /// meaning anything.
        /// </remarks>
        public const double CoarsestFractionOfSpan = 0.3;

        /// <summary>The span-to-RBW ratio a new control starts at.</summary>
        /// <remarks>
        /// A hundred points of resolution across the display: the figure a swept analyser's
        /// auto-coupling has used for decades, and a reasonable first look at an unknown signal.
        /// </remarks>
        public const double DefaultSpanToRatio = 100.0;

        /// <summary>The mantissas of the swept-analyser ladder: 1, 3, 10, 30, 100, …</summary>
        private static readonly double[] LadderMantissas = { 1.0, 3.0 };

        private readonly IFrontEndCapabilities _capabilities;
        private readonly AnalysisPath _path;

        private double _spanHz;
        private double _resolutionBandwidthHz;
        private double _spanToRatio = DefaultSpanToRatio;

        /// <summary>
        /// Creates a control over a front end, coupled at the default ratio.
        /// </summary>
        /// <param name="capabilities">What the front end can capture; the finest RBW comes from it.</param>
        /// <param name="spanHz">Starting span, in hertz.</param>
        /// <param name="window">Analysis window, whose ENBW couples RBW to record length.</param>
        /// <param name="path">Acquisition path, which fixes the transform length for a point count.</param>
        /// <exception cref="ArgumentNullException"><paramref name="capabilities"/> is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="spanHz"/> is not positive.</exception>
        public ResolutionBandwidthControl(
            IFrontEndCapabilities capabilities,
            double spanHz,
            WindowType window = Window.Default,
            AnalysisPath path = AnalysisPath.ComplexZoom)
        {
            if (capabilities == null)
            {
                throw new ArgumentNullException(nameof(capabilities));
            }

            RequirePositive(spanHz, nameof(spanHz));

            _capabilities = capabilities;
            _path = path;
            _spanHz = spanHz;
            AnalysisWindow = window;

            _resolutionBandwidthHz = Reachable(Snap(spanHz / _spanToRatio));
        }

        /// <summary>The analysis window whose ENBW this control's arithmetic uses.</summary>
        public WindowType AnalysisWindow { get; }

        /// <summary>Which bandwidths may be chosen.</summary>
        public ResolutionBandwidthMode Mode { get; private set; }

        /// <summary>Whether the bandwidth follows the span.</summary>
        public ResolutionBandwidthCoupling Coupling { get; private set; }

        /// <summary>The span in force, in hertz.</summary>
        public double SpanHz => _spanHz;

        /// <summary>The resolution bandwidth in force, in hertz.</summary>
        public double ResolutionBandwidthHz => _resolutionBandwidthHz;

        /// <summary>
        /// The span-to-RBW ratio held when coupled.
        /// </summary>
        /// <remarks>
        /// Kept in step with a bandwidth set by hand: setting RBW while coupled is a statement
        /// about the ratio too, and leaving the old one would make the next span change undo what
        /// the user just did.
        /// </remarks>
        public double SpanToRatio => _spanToRatio;

        /// <summary>
        /// The time record this bandwidth implies, in seconds: <c>ENBW / RBW</c>
        /// (<c>REQ-DSP-020</c>).
        /// </summary>
        public double RecordSeconds =>
            ResolutionBandwidth.RecordLengthFor(Enbw, _resolutionBandwidthHz);

        /// <summary>The bandwidths reachable at the current span.</summary>
        public ResolutionBandwidthRange Achievable => AchievableFor(_spanHz);

        /// <summary>
        /// The bandwidths reachable at a span.
        /// </summary>
        /// <param name="spanHz">The span, in hertz.</param>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="spanHz"/> is not positive.</exception>
        /// <remarks>
        /// The finest end is a capability question, not a constant: it is the RBW of the longest
        /// record this front end can capture over this span. A source that can hold a million
        /// samples reaches far below 1 Hz at a narrow span; one that can hold four thousand does
        /// not, and says so rather than accepting a setting it cannot honour.
        /// </remarks>
        public ResolutionBandwidthRange AchievableFor(double spanHz)
        {
            RequirePositive(spanHz, nameof(spanHz));

            int points = AcquisitionPlanner.MaximumPointsFor(_capabilities, _path);

            if (points < 2)
            {
                throw new InvalidOperationException(
                    "This front end cannot capture enough samples for a spectrum, so it has no " +
                    "resolution bandwidth to offer.");
            }

            double finest = ResolutionBandwidth.ForSpan(Enbw, spanHz, points);
            double coarsest = CoarsestFractionOfSpan * spanHz;

            // A front end whose deepest capture is short can have a finest RBW coarser than the
            // fraction-of-span ceiling. Reported as the single bandwidth it can reach rather than
            // as an inverted range.
            return new ResolutionBandwidthRange(finest, Math.Max(finest, coarsest));
        }

        /// <summary>
        /// Sets the resolution bandwidth.
        /// </summary>
        /// <param name="hz">Wanted bandwidth, in hertz.</param>
        /// <returns>The bandwidth now in force, which in analyser mode is on the ladder.</returns>
        /// <exception cref="ArgumentOutOfRangeException">
        /// The bandwidth is not reachable at this span. The message names the reachable bound
        /// rather than clamping to it — <c>REQ-DSP-021</c>'s criterion.
        /// </exception>
        /// <remarks>
        /// While coupled this also restates the ratio, so the setting survives the next span
        /// change. Setting a bandwidth and then watching the next span change discard it would be
        /// the more surprising of the two behaviours.
        /// </remarks>
        public double SetResolutionBandwidth(double hz)
        {
            RequirePositive(hz, nameof(hz));

            double chosen = Snap(hz);
            ResolutionBandwidthRange range = AchievableFor(_spanHz);

            if (!range.Contains(chosen))
            {
                throw new ArgumentOutOfRangeException(nameof(hz), hz, Explain(chosen, hz, range));
            }

            _resolutionBandwidthHz = chosen;

            if (Coupling == ResolutionBandwidthCoupling.Coupled)
            {
                _spanToRatio = _spanHz / chosen;
            }

            return chosen;
        }

        /// <summary>
        /// Sets the span, moving the bandwidth with it when coupled.
        /// </summary>
        /// <param name="spanHz">The new span, in hertz.</param>
        /// <returns>The bandwidth now in force.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="spanHz"/> is not positive.</exception>
        /// <remarks>
        /// <para>
        /// Uncoupled, the bandwidth is left exactly where it was — including where the new span
        /// cannot reach it. That is deliberate: the reachable range is reported by
        /// <see cref="IsReachable"/>, and silently moving a bandwidth the user pinned would defeat
        /// the point of pinning it. The measurement is planned against
        /// <see cref="AcquisitionPlanner"/>, which is where an unreachable setting is caught.
        /// </para>
        /// <para>
        /// Coupled, the ratio is what is held; the bandwidth follows, snapped to the mode's ladder,
        /// and is bounded by what the new span can reach.
        /// </para>
        /// </remarks>
        public double SetSpan(double spanHz)
        {
            RequirePositive(spanHz, nameof(spanHz));

            _spanHz = spanHz;

            if (Coupling == ResolutionBandwidthCoupling.Coupled)
            {
                _resolutionBandwidthHz = Reachable(Snap(spanHz / _spanToRatio));
            }

            return _resolutionBandwidthHz;
        }

        /// <summary>Sets the span-to-RBW ratio, moving the bandwidth if coupled.</summary>
        /// <param name="ratio">Span divided by RBW; must be positive and finite.</param>
        /// <returns>The bandwidth now in force.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="ratio"/> is not positive.</exception>
        public double SetSpanToRatio(double ratio)
        {
            RequirePositive(ratio, nameof(ratio));

            _spanToRatio = ratio;

            if (Coupling == ResolutionBandwidthCoupling.Coupled)
            {
                _resolutionBandwidthHz = Reachable(Snap(_spanHz / ratio));
            }

            return _resolutionBandwidthHz;
        }

        /// <summary>Selects how bandwidths are chosen, re-snapping the current one.</summary>
        /// <param name="mode">The mode.</param>
        /// <returns>The bandwidth now in force.</returns>
        public double SetMode(ResolutionBandwidthMode mode)
        {
            Mode = mode;
            _resolutionBandwidthHz = Reachable(Snap(_resolutionBandwidthHz));

            return _resolutionBandwidthHz;
        }

        /// <summary>
        /// Couples or uncouples the bandwidth from the span.
        /// </summary>
        /// <param name="coupling">The coupling.</param>
        /// <returns>The bandwidth now in force.</returns>
        /// <remarks>
        /// Coupling takes the ratio from where the bandwidth already is, rather than jumping to the
        /// last ratio in force — the user's most recent statement about the measurement is the
        /// bandwidth they are looking at.
        /// </remarks>
        public double SetCoupling(ResolutionBandwidthCoupling coupling)
        {
            Coupling = coupling;

            if (coupling == ResolutionBandwidthCoupling.Coupled)
            {
                _spanToRatio = _spanHz / _resolutionBandwidthHz;
            }

            return _resolutionBandwidthHz;
        }

        /// <summary>Whether a bandwidth is reachable at the current span.</summary>
        /// <param name="hz">Bandwidth to test, in hertz.</param>
        public bool IsReachable(double hz) => AchievableFor(_spanHz).Contains(hz);

        /// <summary>
        /// The bandwidth this mode would use for a wanted one.
        /// </summary>
        /// <param name="hz">Wanted bandwidth, in hertz.</param>
        /// <returns>The same figure in arbitrary mode; the ladder step at or below it otherwise.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="hz"/> is not positive.</exception>
        /// <remarks>
        /// Downward on the ladder, never up. A swept analyser asked for 1.5 kHz gives 1 kHz, and
        /// the reason is the same reason the point count rounds up: the measurement delivered must
        /// resolve at least as well as the one asked for, and a coarser answer is a different
        /// measurement.
        /// </remarks>
        public double Snap(double hz)
        {
            RequirePositive(hz, nameof(hz));

            if (Mode == ResolutionBandwidthMode.Arbitrary)
            {
                return hz;
            }

            double decade = Math.Pow(10.0, Math.Floor(Math.Log10(hz)));
            double best = 0.0;

            // One decade either side of the estimate, because log10 of a value that is exactly on a
            // decade can land on either side of it in floating point.
            for (int shift = -1; shift <= 1; shift++)
            {
                double scale = decade * Math.Pow(10.0, shift);

                foreach (double mantissa in LadderMantissas)
                {
                    double candidate = mantissa * scale;

                    if (candidate <= hz * (1.0 + LadderTolerance) && candidate > best)
                    {
                        best = candidate;
                    }
                }
            }

            return best > 0.0 ? best : hz;
        }

        /// <summary>The ladder steps within a range, coarsest first.</summary>
        /// <param name="range">The reachable range.</param>
        /// <remarks>
        /// For a selector: analyser mode offers a list, arbitrary mode offers a box. Coarsest first
        /// because that is the order a swept analyser's key sequence walks, and because the coarse
        /// end is where a first look starts.
        /// </remarks>
        public static IReadOnlyList<double> LadderWithin(ResolutionBandwidthRange range)
        {
            var steps = new List<double>();

            int lowest = (int)Math.Floor(Math.Log10(range.MinHz)) - 1;
            int highest = (int)Math.Ceiling(Math.Log10(range.MaxHz)) + 1;

            for (int decade = highest; decade >= lowest; decade--)
            {
                double scale = Math.Pow(10.0, decade);

                for (int i = LadderMantissas.Length - 1; i >= 0; i--)
                {
                    double candidate = LadderMantissas[i] * scale;

                    if (range.Contains(candidate))
                    {
                        steps.Add(candidate);
                    }
                }
            }

            return steps;
        }

        /// <summary>The window's ENBW, in bins, at the length the relation is evaluated for.</summary>
        /// <remarks>
        /// A property of the window's shape rather than of its length — periodic Hann is exactly
        /// 1.5 at every N — so the reference length here is not an approximation that needs
        /// iterating. <see cref="AcquisitionPlanner"/> uses the same figure for the same reason.
        /// </remarks>
        private double Enbw => Window.Get(AnalysisWindow, EnbwReferenceLength).Enbw;

        /// <summary>Length at which the window's ENBW is evaluated.</summary>
        private const int EnbwReferenceLength = 4096;

        /// <summary>Fraction of a step within which a ladder value counts as at or below a request.</summary>
        private const double LadderTolerance = 1e-12;

        private double Reachable(double hz)
        {
            ResolutionBandwidthRange range = AchievableFor(_spanHz);

            return hz < range.MinHz ? range.MinHz : (hz > range.MaxHz ? range.MaxHz : hz);
        }

        private string Explain(double chosen, double wanted, ResolutionBandwidthRange range)
        {
            string asked = Math.Abs(chosen - wanted) < LadderTolerance * Math.Abs(wanted)
                ? ResolutionBandwidthRange.EngineeringHertz(wanted)
                : ResolutionBandwidthRange.EngineeringHertz(wanted) + ", which this mode's ladder " +
                  "makes " + ResolutionBandwidthRange.EngineeringHertz(chosen) + ",";

            if (chosen < range.MinHz)
            {
                return "A resolution bandwidth of " + asked + " is finer than this source can " +
                       "reach over a span of " + ResolutionBandwidthRange.EngineeringHertz(_spanHz) +
                       ". The finest available is " +
                       ResolutionBandwidthRange.EngineeringHertz(range.MinHz) +
                       ", set by the longest record it can capture; a narrower span reaches finer.";
            }

            return "A resolution bandwidth of " + asked + " is coarser than " +
                   ResolutionBandwidthRange.EngineeringHertz(range.MaxHz) + ", which is " +
                   CoarsestFractionOfSpan.ToString("0.##", CultureInfo.CurrentCulture) +
                   " of the span of " + ResolutionBandwidthRange.EngineeringHertz(_spanHz) +
                   ". A wider span allows a coarser bandwidth.";
        }

        private static void RequirePositive(double value, string name)
        {
            if (!(value > 0.0) || double.IsInfinity(value))
            {
                throw new ArgumentOutOfRangeException(
                    name, value, name + " must be positive and finite.");
            }
        }
    }
}
