using OpenVSA.Demod.Signal;

namespace OpenVSA.Demod.Chain.Steps
{
    /// <summary>
    /// Step 5: the measurement filter, applied to the acquired signal.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>REQ-DEM-020</c> is the requirement this serves, and its point is that the measurement
    /// filter and the reference filter of step 10 are independently selectable: the measurement
    /// filter emulates the receiver half of a Nyquist pair and so must match the transmitter's
    /// shaping, while the reference filter shapes the ideal waveform the result is compared
    /// against. They are separate settings here — <see cref="DemodSettings.MeasurementFilterAlpha"/>
    /// and <see cref="DemodSettings.ReferenceFilterAlpha"/> — for that reason, and the catalogue of
    /// types they can each be set to belongs to <c>REQ-DEM-021</c>.
    /// </para>
    /// <para>
    /// The filter is applied on its centre tap, so it introduces no delay for step 8 to have to
    /// estimate away as symbol timing.
    /// </para>
    /// <para>
    /// <strong>A signal that has already been matched-filtered wants none.</strong> That is what
    /// <see cref="PulseFilterType.None"/> is for, and it is not a hypothetical case: the synthetic
    /// source of <c>REQ-SIM-001</c> shapes with a full raised cosine on purpose. Filtering it again
    /// costs about 10 % EVM on a signal with nothing wrong with it.
    /// </para>
    /// </remarks>
    internal sealed class MeasurementFilterStep : IChainStep
    {
        /// <inheritdoc />
        public DemodStep Step => DemodStep.MeasurementFilter;

        /// <inheritdoc />
        public StepOutcome Run(DemodContext context)
        {
            double[] working = DemodContext.Require(
                context.Working, DemodStep.Resample, DemodStep.MeasurementFilter);

            if (context.Settings.MeasurementFilter == PulseFilterType.None)
            {
                // The step ran and applied the filter it was asked for. "None" is an entry in
                // REQ-DEM-021's catalogue, not a step being skipped, and the journal records it as
                // executed because it was.
                return StepOutcome.Continue;
            }

            double[] taps = PulseShaping.RootRaisedCosine(
                context.Settings.MeasurementFilterAlpha,
                context.Settings.PointsPerSymbol,
                context.Settings.FilterSymbolSpan);

            context.Working = PulseShaping.Convolve(working, taps);

            return StepOutcome.Continue;
        }
    }
}
