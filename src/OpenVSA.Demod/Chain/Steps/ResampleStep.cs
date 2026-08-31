using System;
using System.Globalization;
using OpenVSA.Demod.Signal;

namespace OpenVSA.Demod.Chain.Steps
{
    /// <summary>
    /// Step 4: resample to the internal processing rate.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The acquisition arrives at whatever rate the front end and the span chose, and the symbol
    /// rate is a property of the signal. The two have no reason to be related, so everything from
    /// step 5 onward works at a whole number of points per symbol and this is where that happens.
    /// </para>
    /// <para>
    /// <strong>The internal rate is not the displayed one.</strong> <c>REQ-DEM-034a</c> requires
    /// that the points per symbol a user chooses for a display not change what the demodulator
    /// does, and this step reads <see cref="DemodSettings.PointsPerSymbol"/> — the internal rate —
    /// for that reason.
    /// </para>
    /// </remarks>
    internal sealed class ResampleStep : IChainStep
    {
        /// <inheritdoc />
        public DemodStep Step => DemodStep.Resample;

        /// <inheritdoc />
        public StepOutcome Run(DemodContext context)
        {
            double[] search = DemodContext.Require(
                context.Search, DemodStep.SearchWindow, DemodStep.Resample);

            double wanted = context.Settings.PointsPerSymbol * context.Settings.SymbolRateHz;
            double ratio = wanted / context.SampleRateHz;

            if (ratio > 1.0 + 1e-9)
            {
                context.Note(
                    "The record was acquired at " +
                    (context.SampleRateHz / context.Settings.SymbolRateHz).ToString(
                        "G4", CultureInfo.InvariantCulture) +
                    " samples per symbol and the chain works at " +
                    context.Settings.PointsPerSymbol.ToString(CultureInfo.InvariantCulture) +
                    ". Interpolating up recovers nothing the acquisition did not capture.");
            }

            string advice = context.Settings.PointsPerSymbolAdvice;

            if (advice != null)
            {
                // Said here because this is the step that band-limits: what the internal rate
                // excludes is excluded on this line, and a caller reading a puzzling error vector
                // should be told so where it happens.
                context.Note(advice);
            }

            context.Working = Interpolator.Resample(search, ratio);
            context.ResampleRatio = ratio;

            if (Iq.Count(context.Working) < context.Settings.PointsPerSymbol * 8)
            {
                throw new ArgumentException(
                    "Resampling left " + Iq.Count(context.Working).ToString(CultureInfo.InvariantCulture) +
                    " samples, which is not enough waveform to demodulate. The Search Length " +
                    "window, the sample rate or the symbol rate is wrong.");
            }

            return StepOutcome.Continue;
        }
    }
}
