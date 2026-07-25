using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using OpenVSA.Core;
using OpenVSA.Dsp.Spectrum;
using OpenVSA.Dsp.Windowing;
using OpenVSA.Hal;

namespace OpenVSA.Measurement
{
    /// <summary>
    /// A measurement setup converted into something a front end can be asked for.
    /// </summary>
    /// <remarks>
    /// The point count here is what the connected instrument can actually capture, not what was
    /// asked for. Both are kept, because a display that silently shows fewer points than the user
    /// selected is the same class of defect as a silently coerced span (<c>REQ-HAL-001</c>).
    /// </remarks>
    public sealed class PlannedAcquisition
    {
        internal PlannedAcquisition(
            AcquisitionRequest request,
            int frequencyPoints,
            int requestedFrequencyPoints,
            int transformLength,
            double maxTimeSeconds,
            WindowType window,
            double resolutionBandwidthHz,
            bool pointsWereAutomatic,
            IList<ParameterCoercion> coercions)
        {
            Request = request;
            FrequencyPoints = frequencyPoints;
            RequestedFrequencyPoints = requestedFrequencyPoints;
            TransformLength = transformLength;
            MaxTimeSeconds = maxTimeSeconds;
            Window = window;
            ResolutionBandwidthHz = resolutionBandwidthHz;
            PointsWereAutomatic = pointsWereAutomatic;
            Coercions = new ReadOnlyCollection<ParameterCoercion>(coercions);
        }

        /// <summary>What to ask the front end for.</summary>
        public AcquisitionRequest Request { get; }

        /// <summary>Displayed frequency points this instrument can support at this setting.</summary>
        public int FrequencyPoints { get; }

        /// <summary>Displayed frequency points that were asked for.</summary>
        public int RequestedFrequencyPoints { get; }

        /// <summary>FFT size implied by <see cref="FrequencyPoints"/> on the requested path.</summary>
        public int TransformLength { get; }

        /// <summary>Longest analysable time record, in seconds: <c>(N_f − 1) / Span</c>.</summary>
        public double MaxTimeSeconds { get; }

        /// <summary>The analysis window these figures assume.</summary>
        public WindowType Window { get; }

        /// <summary>
        /// Resolution bandwidth this setting achieves, in hertz (<c>REQ-DSP-020</c>).
        /// </summary>
        /// <remarks>
        /// Always computed, whether the point count was chosen or derived, because RBW is the
        /// figure that says what the measurement can actually resolve — and under the default Flat
        /// Top window it is nearly four times the spacing between displayed points.
        /// </remarks>
        public double ResolutionBandwidthHz { get; }

        /// <summary>Whether the point count was derived from an RBW rather than given.</summary>
        public bool PointsWereAutomatic { get; }

        /// <summary>Settings the planner had to change, and why. Never <c>null</c>.</summary>
        /// <remarks>
        /// Separate from <c>AcquisitionPlan.Coercions</c>, which are the front end's. These are the
        /// planner's own — a point count reduced to what the instrument can capture is decided
        /// before the front end is asked anything.
        /// </remarks>
        public IReadOnlyList<ParameterCoercion> Coercions { get; }

        /// <summary>Whether the planner had to change anything.</summary>
        public bool Coerced => Coercions.Count > 0;
    }

    /// <summary>
    /// Turns a span and a point count into an acquisition the connected front end can perform.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>REQ-ACQ-001</c> and <c>REQ-DSP-022</c>. The relations themselves live in
    /// <see cref="AcquisitionLaw"/> and <see cref="FrequencyPoints"/>; what this adds is the part
    /// that cannot be tabulated — how many of those points a <em>particular</em> instrument can
    /// deliver.
    /// </para>
    /// <para>
    /// <strong>Everything instrument-specific is read from <see cref="IFrontEndCapabilities"/>.</strong>
    /// There is no default block size here, no assumed capture depth and no model name: an
    /// instrument that can only return 4 096 samples in a block gets 3 201 points, one that can
    /// return a million gets far more, and neither case is special-cased. That is
    /// <c>REQ-HAL-002</c> applied to the one setting most tempting to hard-code, and it is why
    /// <see cref="MaximumPointsFor"/> is public: a UI ranging a points control asks the
    /// capabilities, exactly as it does for every other limit.
    /// </para>
    /// <para>
    /// The planner does not assume its request will be honoured. It says what it wants; what
    /// arrives is settled by <c>Negotiate</c>, and the displayed point count is derived from the
    /// blocks that actually turn up — see <c>SpectrumComputer.TrimToAnalysisSpan</c>.
    /// </para>
    /// </remarks>
    public static class AcquisitionPlanner
    {
        /// <summary>
        /// Point count used when a measurement does not specify one.
        /// </summary>
        /// <remarks>
        /// A modest default rather than the instrument's maximum. Points cost transform time
        /// quadratically in wall-clock terms at a fixed update rate, and a first-light measurement
        /// that opens at a million points would be slow for no reason the user asked for. The Auto
        /// mode of <c>REQ-DSP-022</c> — deriving the count from span, RBW and window — replaces
        /// this when it exists.
        /// </remarks>
        public const int DefaultFrequencyPoints = 801;

        /// <summary>
        /// The most displayed points this front end can support on a path.
        /// </summary>
        /// <param name="capabilities">The front end's declared capabilities.</param>
        /// <param name="path">The acquisition path.</param>
        /// <returns>An available point count, or 0 if the front end cannot capture even the minimum.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="capabilities"/> is null.</exception>
        public static int MaximumPointsFor(IFrontEndCapabilities capabilities, AnalysisPath path)
        {
            if (capabilities == null)
            {
                throw new ArgumentNullException(nameof(capabilities));
            }

            // Three ceilings, and the smallest wins: what the front end will return in one block,
            // how deep a capture it can hold, and the largest FFT the relations permit.
            long ceiling = Math.Min(
                capabilities.MaxSamplesPerBlock,
                Math.Min(capabilities.MaxCaptureSamples, FrequencyPoints.MaximumTransformLength));

            int best = 0;

            foreach (int candidate in FrequencyPoints.Supported)
            {
                if (AcquisitionLaw.TransformLengthFor(candidate, path) > ceiling)
                {
                    break;
                }

                best = candidate;
            }

            return best;
        }

        /// <summary>
        /// Plans an acquisition, reducing the point count to what the instrument can capture.
        /// </summary>
        /// <param name="capabilities">The front end's declared capabilities.</param>
        /// <param name="centerFrequencyHz">Requested centre frequency, in hertz.</param>
        /// <param name="spanHz">Requested span, in hertz.</param>
        /// <param name="frequencyPoints">Requested displayed points; must be an available count.</param>
        /// <param name="referenceLevelDbm">Requested reference level, in dBm.</param>
        /// <param name="path">Requested acquisition path.</param>
        /// <param name="window">Analysis window, for the resolution bandwidth these settings give.</param>
        /// <returns>The request to negotiate, and what the planner had to change to get there.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="capabilities"/> is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException">
        /// The point count is not an available one (<c>REQ-DSP-022</c>), or the front end cannot
        /// capture even the minimum count.
        /// </exception>
        public static PlannedAcquisition Plan(
            IFrontEndCapabilities capabilities,
            double centerFrequencyHz,
            double spanHz,
            int frequencyPoints,
            double referenceLevelDbm,
            AnalysisPath path,
            WindowType window = Window.Default)
        {
            if (capabilities == null)
            {
                throw new ArgumentNullException(nameof(capabilities));
            }

            // Rejected rather than snapped. REQ-DSP-022's criterion is that an unavailable count is
            // refused with a clear message; snapping it here would mean a user who typed 409 602
            // got 409 601 and never learned that the number they believed in does not exist.
            FrequencyPoints.Validate(frequencyPoints, nameof(frequencyPoints));

            return Build(
                capabilities, centerFrequencyHz, spanHz, frequencyPoints, referenceLevelDbm, path,
                window, automatic: false, coercions: new List<ParameterCoercion>());
        }

        /// <summary>
        /// Plans an acquisition from a resolution bandwidth, deriving the point count
        /// (<c>REQ-DSP-022</c> Auto, <c>REQ-DSP-020</c>).
        /// </summary>
        /// <param name="capabilities">The front end's declared capabilities.</param>
        /// <param name="centerFrequencyHz">Requested centre frequency, in hertz.</param>
        /// <param name="spanHz">Requested span, in hertz.</param>
        /// <param name="resolutionBandwidthHz">Wanted resolution bandwidth, in hertz.</param>
        /// <param name="referenceLevelDbm">Requested reference level, in dBm.</param>
        /// <param name="path">Requested acquisition path.</param>
        /// <param name="window">Analysis window; its ENBW is what couples RBW to record length.</param>
        /// <returns>The request to negotiate, and the RBW actually achieved.</returns>
        /// <remarks>
        /// <para>
        /// The count is rounded <strong>up</strong> to the next available one, so the RBW achieved
        /// is at least as fine as the one asked for. Rounding to nearest would hand back a coarser
        /// measurement than the one requested without saying so, and RBW is the setting a user is
        /// least willing to have quietly relaxed.
        /// </para>
        /// <para>
        /// Where the instrument cannot capture enough samples for the wanted RBW, the count is
        /// reduced to what it can and the coercion names the RBW that results — the one case where
        /// the answer is coarser, and the one case where the user is told.
        /// </para>
        /// </remarks>
        /// <exception cref="ArgumentNullException"><paramref name="capabilities"/> is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException">A value is out of range, or the front end cannot capture the minimum count.</exception>
        public static PlannedAcquisition PlanForResolutionBandwidth(
            IFrontEndCapabilities capabilities,
            double centerFrequencyHz,
            double spanHz,
            double resolutionBandwidthHz,
            double referenceLevelDbm,
            AnalysisPath path,
            WindowType window = Window.Default)
        {
            if (capabilities == null)
            {
                throw new ArgumentNullException(nameof(capabilities));
            }

            // ENBW at a nominal length. It is a property of the window's shape rather than of its
            // length - periodic Hann is exactly 1.5 at every N - so this is not an approximation
            // that needs iterating, and the figure reported below is recomputed at the length
            // actually chosen in any case.
            double enbw = Window.Get(window, EnbwReferenceLength).Enbw;
            double intervals = ResolutionBandwidth.RequiredIntervals(enbw, spanHz, resolutionBandwidthHz);

            int wanted = SmallestCountCovering(intervals);
            var coercions = new List<ParameterCoercion>();

            if (wanted == 0)
            {
                // Finer than the relations themselves allow, whatever the instrument can do.
                wanted = FrequencyPoints.Maximum;
                coercions.Add(new ParameterCoercion(
                    "ResolutionBandwidth", resolutionBandwidthHz,
                    ResolutionBandwidth.ForSpan(enbw, spanHz, wanted),
                    "finer than " + FrequencyPoints.Maximum.ToString(CultureInfo.CurrentCulture) +
                    " frequency points can resolve over this span"));
            }

            return Build(
                capabilities, centerFrequencyHz, spanHz, wanted, referenceLevelDbm, path,
                window, automatic: true, coercions: coercions);
        }

        /// <summary>Length at which a window's ENBW is evaluated before the transform size is known.</summary>
        private const int EnbwReferenceLength = 4096;

        /// <summary>The smallest available count whose intervals cover a requirement, or 0 if none does.</summary>
        private static int SmallestCountCovering(double intervals)
        {
            foreach (int candidate in FrequencyPoints.Supported)
            {
                if (candidate - 1 >= intervals)
                {
                    return candidate;
                }
            }

            return 0;
        }

        private static PlannedAcquisition Build(
            IFrontEndCapabilities capabilities,
            double centerFrequencyHz,
            double spanHz,
            int wantedPoints,
            double referenceLevelDbm,
            AnalysisPath path,
            WindowType window,
            bool automatic,
            List<ParameterCoercion> coercions)
        {
            int points = wantedPoints;
            int available = MaximumPointsFor(capabilities, path);

            if (available == 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(capabilities), capabilities.MaxSamplesPerBlock,
                    "This front end cannot capture the " +
                    AcquisitionLaw.TransformLengthFor(FrequencyPoints.Minimum, path)
                        .ToString(CultureInfo.CurrentCulture) +
                    " samples that the minimum of " +
                    FrequencyPoints.Minimum.ToString(CultureInfo.CurrentCulture) +
                    " frequency points needs, so no spectrum can be measured from it.");
            }

            if (points > available)
            {
                coercions.Add(new ParameterCoercion(
                    "FrequencyPoints", points, available,
                    "more points than this front end can capture in one block"));
                points = available;
            }

            int transformLength = AcquisitionLaw.TransformLengthFor(points, path);

            var request = new AcquisitionRequest(
                centerFrequencyHz, spanHz, transformLength, referenceLevelDbm, path);

            // Recomputed at the length actually chosen, so the figure reported is the one the
            // measurement will have rather than the one the estimate assumed.
            double achievedEnbw = Window.Get(window, transformLength).Enbw;

            return new PlannedAcquisition(
                request,
                points,
                wantedPoints,
                transformLength,
                AcquisitionLaw.MaxTimeSeconds(points, spanHz),
                window,
                ResolutionBandwidth.ForSpan(achievedEnbw, spanHz, points),
                automatic,
                coercions);
        }

        /// <summary>
        /// Plans an acquisition at <see cref="DefaultFrequencyPoints"/>, or fewer if the front end
        /// cannot manage them.
        /// </summary>
        /// <param name="capabilities">The front end's declared capabilities.</param>
        /// <param name="centerFrequencyHz">Requested centre frequency, in hertz.</param>
        /// <param name="spanHz">Requested span, in hertz.</param>
        /// <param name="referenceLevelDbm">Requested reference level, in dBm.</param>
        /// <returns>The request to negotiate.</returns>
        public static PlannedAcquisition Plan(
            IFrontEndCapabilities capabilities,
            double centerFrequencyHz,
            double spanHz,
            double referenceLevelDbm) =>
            Plan(
                capabilities,
                centerFrequencyHz,
                spanHz,
                DefaultFrequencyPoints,
                referenceLevelDbm,
                AnalysisPath.ComplexZoom);
    }
}
