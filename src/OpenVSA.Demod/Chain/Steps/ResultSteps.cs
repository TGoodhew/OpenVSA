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

            ErrorSummary summary = ErrorSummary.For(measured, ideal);

            context.Summary = summary;
            context.EvmPercent = Evm(summary);

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
    /// offers and <c>REQ-DEM-081</c> onward own how they are drawn; what this step produces is the
    /// <see cref="SymbolTrace"/> they are all built from.
    /// </para>
    /// <para>
    /// <strong>The decision instants are rounded here and only here.</strong> Step 8's timing
    /// estimate is fractional and everything upstream uses it as such. A display, though, draws
    /// samples, so the trace carries the nearest sample to each decision instant. Rounding at the
    /// point of display rather than at the point of estimation is what keeps the rounding out of
    /// the measurement.
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
            int samples = Iq.Count(result);
            int count = context.Symbols.Length;

            var decisions = new List<int>(count);

            for (int symbol = 0; symbol < count; symbol++)
            {
                int instant = (int)Math.Round(context.TimingSamples + (symbol * perSymbol));

                if (instant < 0)
                {
                    instant = 0;
                }

                if (instant > samples - 1)
                {
                    instant = samples - 1;
                }

                decisions.Add(instant);
            }

            var waveform = new float[result.Length];

            for (int index = 0; index < result.Length; index++)
            {
                waveform[index] = (float)result[index];
            }

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
