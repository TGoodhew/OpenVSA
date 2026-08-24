using System;
using System.Collections.Generic;
using OpenVSA.Demod.Results;
using OpenVSA.Demod.Signal;

namespace OpenVSA.Demod.Chain.Steps
{
    /// <summary>
    /// Step 13: compute the error metrics at the symbol instants.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>At the symbol instants, and nowhere else.</strong> That is the specification's own
    /// wording for this step, and it is what makes EVM the number people compare between
    /// instruments: a metric computed over every sample of a pulse-shaped waveform would be
    /// measuring the pulse shape, which is not an error.
    /// </para>
    /// <para>
    /// The metrics themselves are <see cref="ErrorSummary.For(IReadOnlyList{ConstellationPoint},
    /// IReadOnlyList{ConstellationPoint})"/>'s, which is the same implementation the error summary
    /// table renders from. <c>REQ-DEM-060</c> and the requirements around it own what those metrics
    /// are and how they are normalised; this step is where the chain computes them.
    /// </para>
    /// </remarks>
    internal sealed class ErrorMetricStep : IChainStep
    {
        /// <inheritdoc />
        public DemodStep Step => DemodStep.ErrorMetrics;

        /// <inheritdoc />
        public StepOutcome Run(DemodContext context)
        {
            if (context.MeasuredSymbols == null || context.IdealSymbols == null)
            {
                throw new ChainOrderException(
                    "Step 13 ran before there were symbols to compare. The chain was executed " +
                    "out of order.");
            }

            List<ConstellationPoint> measured = Points(context.MeasuredSymbols);
            List<ConstellationPoint> ideal = Points(context.IdealSymbols);

            ErrorSummary computed = ErrorSummary.For(measured, ideal);

            // The rows the format shows, not just the ones this build can fill in: REQ-DEM-071
            // wants a table whose shape follows the format, with NAN where a metric applies and has
            // not been measured. Reading EVM off the computed summary rather than off the table is
            // deliberate -- they agree today, and the table is the thing that will grow rows.
            Constellation constellation = context.Settings.Constellation;

            context.Summary = computed.AsTableFor(constellation.Family, constellation.IsOffset);
            context.EvmPercent = Evm(computed);

            return StepOutcome.Continue;
        }

        internal static List<ConstellationPoint> Points(Iq[] values)
        {
            var points = new List<ConstellationPoint>(values.Length);

            foreach (Iq value in values)
            {
                points.Add(new ConstellationPoint(value.I, value.Q));
            }

            return points;
        }

        private static double Evm(ErrorSummary summary)
        {
            foreach (ErrorMetric metric in summary.Metrics)
            {
                if (string.Equals(metric.Label, "EVM", StringComparison.Ordinal))
                {
                    return metric.Rms;
                }
            }

            return 0.0;
        }
    }

    /// <summary>
    /// Step 14: generate the result traces.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The last step of the chain assembles what the displays draw: the constellation's measured
    /// and ideal points, the symbols behind them, the waveform they were taken from and where on it
    /// each decision instant fell. <c>REQ-DEM-080</c> owns the catalogue of traces a demodulation
    /// offers and <c>REQ-UI-081</c> onward own how they are drawn; what this step produces is what
    /// they are all built from.
    /// </para>
    /// <para>
    /// <strong>The waveform is put on the symbol's own grid, and that is the whole of this
    /// step.</strong> What steps 1 to 13 worked on is the result window as it was acquired: the
    /// symbol instants fall at <c>τ + kP</c>, where τ is a fraction of a sample, and the samples
    /// carry whatever amplitude and carrier the signal arrived with. Handing that to a display
    /// gives three separate wrongnesses. A constellation point would not lie on the waveform it was
    /// taken from, because the nearest whole sample is up to half a sample away and the pulse has
    /// curvature. An eye folded on the symbol clock would smear, because the clock is not a whole
    /// number of samples. And the measured waveform and the regenerated reference would be drawn on
    /// different scales, because one is in the signal's units and the other in the constellation's.
    /// </para>
    /// <para>
    /// So the trace is resampled onto the grid the symbols define: sample <em>n</em> is the waveform
    /// at <c>τ + n</c>, corrected for the carrier, phase and amplitude step 8 estimated, which puts
    /// symbol <em>k</em> at index <c>kP</c> exactly. The decision instants are then whole numbers
    /// because they are, not because they were rounded, and the reference lands on the same grid so
    /// that the two traces can be drawn together and subtracted point by point.
    /// </para>
    /// <para>
    /// Interpolating rather than rounding costs one pass over the result window with the same
    /// interpolator step 8 reads through, and buys the property every display downstream assumes.
    /// </para>
    /// </remarks>
    internal sealed class ResultTraceStep : IChainStep
    {
        /// <inheritdoc />
        public DemodStep Step => DemodStep.ResultTraces;

        /// <inheritdoc />
        public StepOutcome Run(DemodContext context)
        {
            double[] result = DemodContext.Require(
                context.Result, DemodStep.ResultWindow, DemodStep.ResultTraces);

            if (context.Symbols == null)
            {
                throw new ChainOrderException(
                    "Step 14 ran before there were symbols to draw. The chain was executed out " +
                    "of order.");
            }

            DemodSettings settings = context.Settings;

            int perSymbol = settings.PointsPerSymbol;
            int count = context.Symbols.Length;
            double stagger = ReferenceRegenerationStep.Stagger(settings);

            // Half a symbol of tail for an offset format: its last Q symbol is sent after its last
            // I symbol, and a grid that stopped at the I instant would draw both traces falling
            // away from a symbol that had not happened yet.
            int samples = ((count - 1) * perSymbol) + 1 + (int)stagger;

            double omega =
                2.0 * Math.PI * context.PassFrequencyHz / settings.SymbolRateHz;

            double phase = context.PassPhaseRadians;
            double gain = context.PassGain;
            double timing = context.TimingSamples;

            var waveform = new float[2 * samples];
            var reference = new float[2 * samples];

            // Regenerated on this grid rather than interpolated off step 10's: the symbols sit on
            // whole samples here, so the reference at a decision instant is exactly the symbol that
            // was decided, which is what REQ-DEM-080 asks IQ Reference Time to be.
            double[] ideal = ReferenceRegenerationStep.Regenerate(
                context.IdealSymbols,
                0.0,
                samples,
                perSymbol,
                settings.FilterSymbolSpan,
                settings.ReferenceFilterAlpha,
                stagger);

            for (int sample = 0; sample < samples; sample++)
            {
                double position = timing + sample;

                Iq measured = Interpolator.At(result, position);
                Iq turn = Iq.FromPhase(-(((omega * sample) / perSymbol) + phase));
                Iq corrected = (measured * turn) / gain;

                waveform[2 * sample] = (float)corrected.I;
                waveform[(2 * sample) + 1] = (float)corrected.Q;

                reference[2 * sample] = (float)ideal[2 * sample];
                reference[(2 * sample) + 1] = (float)ideal[(2 * sample) + 1];
            }

            var decisions = new List<int>(count);

            for (int symbol = 0; symbol < count; symbol++)
            {
                decisions.Add(symbol * perSymbol);
            }

            context.TraceWaveform = waveform;
            context.ReferenceWaveform = reference;

            context.Trace = new SymbolTrace(
                settings.Constellation.Name,
                settings.Constellation.BitsPerSymbol,
                settings.Constellation.LevelsPerAxis,
                new List<int>(context.Symbols),
                ErrorMetricStep.Points(context.IdealSymbols),
                ErrorMetricStep.Points(context.MeasuredSymbols),
                decisions,
                waveform,
                perSymbol,
                settings.SymbolRateHz);

            return StepOutcome.Continue;
        }
    }
}
