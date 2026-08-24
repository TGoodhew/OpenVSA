using System;
using OpenVSA.Demod.Signal;

namespace OpenVSA.Demod.Chain.Steps
{
    /// <summary>
    /// Step 9: decide each symbol, and with it the detected bits.
    /// </summary>
    /// <remarks>
    /// The decisions of step 8 were provisional — the fit needed something to aim at while the
    /// parameters were still moving. These are made once, on the converged estimates, and they are
    /// the ones the result reports. On a clean signal the two agree symbol for symbol; on a
    /// marginal one they need not, and it is this step's answer that is defensible because it is
    /// the only one taken with the estimation finished.
    /// </remarks>
    internal sealed class SymbolDecisionStep : IChainStep
    {
        /// <inheritdoc />
        public DemodStep Step => DemodStep.SymbolDecisions;

        /// <inheritdoc />
        public StepOutcome Run(DemodContext context)
        {
            if (context.MeasuredSymbols == null)
            {
                throw new ChainOrderException(
                    "Step 9 ran with nothing from step 8. The chain was executed out of order.");
            }

            Constellation constellation = context.Settings.Constellation;
            Iq[] measured = context.MeasuredSymbols;

            var symbols = new int[measured.Length];
            var ideal = new Iq[measured.Length];
            var bits = new int[measured.Length * constellation.BitsPerSymbol];

            for (int symbol = 0; symbol < measured.Length; symbol++)
            {
                int decided = constellation.Decide(measured[symbol].I, measured[symbol].Q);

                symbols[symbol] = decided;
                ideal[symbol] = constellation.Ideal(decided);

                int[] carried = constellation.BitsOf(decided);

                Array.Copy(
                    carried, 0, bits, symbol * constellation.BitsPerSymbol, carried.Length);
            }

            context.Symbols = symbols;
            context.IdealSymbols = ideal;
            context.Bits = bits;

            return StepOutcome.Continue;
        }
    }

    /// <summary>
    /// Step 10: regenerate the ideal waveform — bits to ideal symbols, through the reference
    /// filter.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The reference waveform is what the transmitter would have sent had it been perfect, and it
    /// is what the equaliser of step 11 fits the measured waveform towards and what the error
    /// metrics of step 13 are differences from. <c>REQ-DEM-020</c> requires the filter that shapes
    /// it to be selectable independently of the measurement filter, which is why this reads
    /// <see cref="DemodSettings.ReferenceFilterAlpha"/> and not the measurement filter's.
    /// </para>
    /// <para>
    /// <strong>The reference filter is the full Nyquist pulse.</strong> The measured waveform has
    /// been through the transmitter's root-raised cosine and step 5's matching half, so the ideal
    /// waveform it is compared against is shaped by the composite of the two — a raised cosine.
    /// Shaping the reference with another root instead would put several per cent of EVM on a
    /// perfect signal, because the two waveforms would differ in shape between the symbol instants
    /// even when every symbol was right. <c>REQ-DEM-020</c> states that split, and
    /// <c>REQ-DEM-021</c>'s catalogue is where the reference filter becomes selectable in type as
    /// well as in roll-off.
    /// </para>
    /// <para>
    /// <strong>Built on the measured waveform's own grid.</strong> The symbol instants sit at
    /// <c>τ + kP</c>, where τ is step 8's timing estimate and is not a whole number of samples. The
    /// reference is therefore evaluated from the pulse's continuous form at those instants rather
    /// than assembled from an impulse train and a tap array, which could only place symbols on
    /// whole samples. Rounding them to the nearest sample would introduce a timing error of up to
    /// half a sample into the reference — an error that the equaliser would then correct for, by
    /// building the rounding into its coefficients.
    /// </para>
    /// </remarks>
    internal sealed class ReferenceRegenerationStep : IChainStep
    {
        /// <inheritdoc />
        public DemodStep Step => DemodStep.ReferenceRegeneration;

        /// <inheritdoc />
        public StepOutcome Run(DemodContext context)
        {
            double[] result = DemodContext.Require(
                context.Result, DemodStep.ResultWindow, DemodStep.ReferenceRegeneration);

            if (context.IdealSymbols == null)
            {
                throw new ChainOrderException(
                    "Step 10 ran with nothing from step 9. The chain was executed out of order.");
            }

            DemodSettings settings = context.Settings;

            context.IdealWaveform = Regenerate(
                context.IdealSymbols,
                context.TimingSamples,
                Iq.Count(result),
                settings.PointsPerSymbol,
                settings.FilterSymbolSpan,
                settings.ReferenceFilterAlpha);

            return StepOutcome.Continue;
        }

        /// <summary>
        /// Shapes ideal symbols into a waveform on a given grid.
        /// </summary>
        /// <param name="symbols">The ideal symbol for each decided symbol.</param>
        /// <param name="firstInstant">
        /// Where the first symbol's instant falls on the grid, in samples.
        /// </param>
        /// <param name="samples">How many samples the grid holds.</param>
        /// <param name="perSymbol">Samples per symbol.</param>
        /// <param name="span">How many symbols either side of centre the pulse spans.</param>
        /// <param name="alpha">The reference filter's roll-off.</param>
        /// <returns>The waveform, interleaved.</returns>
        /// <remarks>
        /// <strong>Shared with step 14, and that is not tidiness.</strong> Step 10 regenerates on
        /// the result window's grid, where the symbols sit at a fractional offset, because that is
        /// the grid the equaliser fits against. Step 14 regenerates on the symbol's own grid, where
        /// they sit on whole samples, because that is what a display draws. Producing the second by
        /// interpolating the first costs about 5e-4 of the constellation's scale -- measured -- and
        /// it is unnecessary: the pulse has a closed form and both grids are just a list of
        /// positions to evaluate it at.
        /// </remarks>
        internal static double[] Regenerate(
            Iq[] symbols,
            double firstInstant,
            int samples,
            int perSymbol,
            int span,
            double alpha)
        {
            var ideal = new double[2 * samples];
            int reach = perSymbol * span;

            for (int symbol = 0; symbol < symbols.Length; symbol++)
            {
                double centre = firstInstant + (symbol * perSymbol);

                int from = (int)Math.Ceiling(centre - reach);
                int to = (int)Math.Floor(centre + reach);

                if (from < 0)
                {
                    from = 0;
                }

                if (to > samples - 1)
                {
                    to = samples - 1;
                }

                for (int sample = from; sample <= to; sample++)
                {
                    double weight =
                        PulseShaping.RaisedCosineAt((sample - centre) / perSymbol, alpha);

                    ideal[2 * sample] += symbols[symbol].I * weight;
                    ideal[(2 * sample) + 1] += symbols[symbol].Q * weight;
                }
            }

            return ideal;
        }
    }
}
