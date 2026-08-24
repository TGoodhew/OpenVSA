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

            Constellation format = context.Settings.Constellation;

            // REQ-DEM-061 normalises to "the reference constellation", so the divisor comes from
            // the FORMAT's points rather than from the ones that happened to be decided. A short
            // window of 64-QAM visits a handful of its sixty-four points, and a divisor taken from
            // those would make the same signal read differently from one acquisition to the next.
            EvmReference reference = EvmReference.FromPoints(
                context.Settings.EvmNormalisation,
                format.Points,
                context.Settings.EvmNormalisationVolts);

            ErrorSummary computed = ErrorSummary.For(measured, ideal, reference);

            if (format.IsOffset && context.CommonInstantSymbols != null)
            {
                // REQ-DEM-062. Two rows, two meanings, and the pair is the point:
                //
                //   Offset EVM  one point per symbol from I and Q half a symbol apart -- what the
                //               format actually sent, and what a demodulation of it should read.
                //   EVM         the same symbols read at one instant, which is REQ-DEM-060's
                //               formula applied literally and is what an analyser that did not know
                //               about the stagger would report.
                //
                // On a clean OQPSK signal the first is near zero and the second is tens of per
                // cent. Showing only the first would be showing a good number with no way to tell
                // whether it was good because the signal was or because the measurement had been
                // told what it wanted to hear.
                ErrorSummary atOneInstant = ErrorSummary.For(
                    Points(context.CommonInstantSymbols), ideal, reference);

                computed.Replace(new ErrorMetric(
                    "Offset EVM",
                    "%rms",
                    Rms(computed, "EVM"),
                    Peak(computed, "EVM"),
                    PeakSymbol(computed, "EVM")));

                computed.Replace(new ErrorMetric(
                    "EVM",
                    "%rms",
                    Rms(atOneInstant, "EVM"),
                    Peak(atOneInstant, "EVM"),
                    PeakSymbol(atOneInstant, "EVM")));
            }

            // REQ-DEM-066: the origin offset comes from step 12's fit, not from the mean of the
            // error vectors this summary computed for itself. The two agree on a long balanced
            // block and part company on a short unbalanced one, where the mean of z - r carries
            // (g - 1) times the mean of r and reports a gain error as a carrier feedthrough. The
            // requirement names that case, and the fit is the thing that does not have it.
            if (context.Impairments != null && reference != null)
            {
                double magnitude = Math.Sqrt(
                    (context.Impairments.OffsetI * context.Impairments.OffsetI) +
                    (context.Impairments.OffsetQ * context.Impairments.OffsetQ)) /
                    reference.RmsMagnitude;

                computed.Replace(new ErrorMetric(
                    "IQ Offset",
                    "dB",
                    magnitude < 1e-12
                        ? ErrorSummary.NoOriginOffsetDb
                        : 20.0 * Math.Log10(magnitude)));

                computed.Replace(new ErrorMetric(
                    "IQ Gain Imbalance", "dB", context.Impairments.GainImbalanceDb));

                computed.Replace(new ErrorMetric(
                    "IQ Quad. Error", "deg", context.Impairments.QuadratureSkewDegrees));

                computed.Replace(new ErrorMetric(
                    "Amp Droop", "dB/sym", context.Impairments.AmplitudeDroopDbPerSymbol));
            }

            // REQ-DEM-065: the shift the analyser applied to achieve lock, which is step 3's coarse
            // estimate plus everything step 8 accumulated on top of it. It is a property of the
            // chain rather than of the constellation geometry, so it is added here and not inside
            // the summary -- which is handed points and could not know it.
            computed.Add(new ErrorMetric(
                "Freq Err", "Hz", context.CoarseFrequencyHz + context.ResidualFrequencyHz));

            // REQ-DEM-070's carrier offset. 🔴 The requirement names it beside REQ-DEM-065's
            // frequency error without saying how the two differ, and under REQ-DEM-065's own
            // definition -- "the frequency shift the analyser applied to achieve carrier lock" --
            // they would be one number in two rows, which reads to a user like a fault.
            //
            // So this is step 3's estimate ALONE: where the block-wide search found the carrier
            // before any decision-directed refinement. The difference between the two rows is then
            // exactly what step 8 had to pull in, which is the quantity REQ-DEM-036's lock tolerance
            // is about and the one that says whether a measurement was comfortably locked or
            // barely. #431 carries the question.
            computed.Add(new ErrorMetric("Carr Ofst", "Hz", context.CoarseFrequencyHz));

            // REQ-DEM-070's time offset: where the first symbol's decision instant falls inside the
            // Result Length window. Step 8 estimates it in samples of the internal processing rate,
            // and seconds is what a user can compare with a symbol period.
            double internalRateHz = InternalRateHz(context);

            computed.Add(new ErrorMetric(
                "Time Offset",
                "s",
                internalRateHz <= 0.0 ? 0.0 : context.TimingSamples / internalRateHz));

            // The rows the format shows, not just the ones this build can fill in: REQ-DEM-071
            // wants a table whose shape follows the format, with NAN where a metric applies and has
            // not been measured. Reading EVM off the computed summary rather than off the table is
            // deliberate -- they agree today, and the table is the thing that will grow rows.
            context.Summary = computed.AsTableFor(format.Family, format.IsOffset);

            // The headline is the meaningful one. For an offset format that is the Offset EVM: the
            // chain honours the stagger everywhere else -- the decisions, the reference, the lock
            // diagnosis -- and a headline that reported the common-instant figure would call a
            // perfectly good OQPSK measurement a failure.
            context.EvmPercent = format.IsOffset && context.CommonInstantSymbols != null
                ? Rms(computed, "Offset EVM")
                : Evm(computed);

            return StepOutcome.Continue;
        }

        /// <summary>The internal processing rate, which is what step 8's timing is in samples of.</summary>
        private static double InternalRateHz(DemodContext context) =>
            context.Settings.SymbolRateHz * context.Settings.PointsPerSymbol;

        internal static List<ConstellationPoint> Points(Iq[] values)
        {
            var points = new List<ConstellationPoint>(values.Length);

            foreach (Iq value in values)
            {
                points.Add(new ConstellationPoint(value.I, value.Q));
            }

            return points;
        }

        private static double Rms(ErrorSummary summary, string label)
        {
            foreach (ErrorMetric metric in summary.Metrics)
            {
                if (string.Equals(metric.Label, label, StringComparison.Ordinal))
                {
                    return metric.Rms;
                }
            }

            return 0.0;
        }

        private static double Peak(ErrorSummary summary, string label)
        {
            foreach (ErrorMetric metric in summary.Metrics)
            {
                if (string.Equals(metric.Label, label, StringComparison.Ordinal))
                {
                    return metric.Peak;
                }
            }

            return 0.0;
        }

        private static int PeakSymbol(ErrorSummary summary, string label)
        {
            foreach (ErrorMetric metric in summary.Metrics)
            {
                if (string.Equals(metric.Label, label, StringComparison.Ordinal))
                {
                    return metric.PeakSymbol;
                }
            }

            return 0;
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

            // REQ-DEM-034: the traces are drawn at the DISPLAY rate, which is not the rate anything
            // was computed at. Steps 1 to 13 worked at the internal rate and every metric came from
            // the symbol instants; this step is the only one that reads the display setting, which
            // is what makes "no effect on computed EVM" true by construction rather than by care.
            int drawn = settings.DisplayPointsPerSymbol;
            double step = perSymbol / (double)drawn;

            double stagger = ReferenceRegenerationStep.Stagger(settings);

            // Half a symbol of tail for an offset format: its last Q symbol is sent after its last
            // I symbol, and a grid that stopped at the I instant would draw both traces falling
            // away from a symbol that had not happened yet. In drawn points, half a symbol is
            // drawn/2 -- and at one point a symbol there is no half to draw.
            int tail = drawn / 2;
            int samples = ((count - 1) * drawn) + 1 + (stagger > 0.0 ? tail : 0);

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
                drawn,
                settings.FilterSymbolSpan,
                settings.ReferencePulse,
                stagger > 0.0 ? drawn / 2.0 : 0.0);

            for (int sample = 0; sample < samples; sample++)
            {
                double position = timing + (sample * step);

                Iq measured = Interpolator.At(result, position);
                Iq turn = Iq.FromPhase(-(((omega * sample) / drawn) + phase));
                Iq corrected = (measured * turn) / gain;

                waveform[2 * sample] = (float)corrected.I;
                waveform[(2 * sample) + 1] = (float)corrected.Q;

                reference[2 * sample] = (float)ideal[2 * sample];
                reference[(2 * sample) + 1] = (float)ideal[(2 * sample) + 1];
            }

            var decisions = new List<int>(count);

            for (int symbol = 0; symbol < count; symbol++)
            {
                decisions.Add(symbol * drawn);
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
                drawn,
                settings.SymbolRateHz);

            return StepOutcome.Continue;
        }
    }
}
