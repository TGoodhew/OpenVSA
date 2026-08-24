using System;
using OpenVSA.Demod.Signal;

namespace OpenVSA.Demod.Chain.Steps
{
    /// <summary>
    /// Step 9: decide each symbol, and with it the detected bits.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The decisions of step 8 were provisional — the fit needed something to aim at while the
    /// parameters were still moving. These are made once, on the converged estimates, and they are
    /// the ones the result reports. On a clean signal the two agree symbol for symbol; on a
    /// marginal one they need not, and it is this step's answer that is defensible because it is
    /// the only one taken with the estimation finished.
    /// </para>
    /// <para>
    /// <strong>The symbol that was sent and the bits it carried are two different things.</strong>
    /// For most of the catalogue they are the same thing said twice, and for a differential format
    /// they are not: the data is the <em>change</em> from one symbol to the next
    /// (<c>REQ-DEM-012</c>), so the first symbol of the window is a reference that carries nothing
    /// and a window of n symbols yields n − 1 symbols of data. This step therefore produces both,
    /// and they are separate fields rather than one because a constellation display draws the
    /// symbols and a bit stream reads the data.
    /// </para>
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

            DemodSettings settings = context.Settings;
            Constellation constellation = settings.Constellation;
            Iq[] measured = context.MeasuredSymbols;

            var symbols = new int[measured.Length];
            var ideal = new Iq[measured.Length];

            for (int symbol = 0; symbol < measured.Length; symbol++)
            {
                int decided = constellation.Decide(measured[symbol], symbol);

                symbols[symbol] = decided;
                ideal[symbol] = constellation.Ideal(decided, symbol);
            }

            int[] data = Carried(symbols, constellation, settings.DecodesDifferentially);
            var bits = new int[data.Length * constellation.BitsPerSymbol];

            for (int symbol = 0; symbol < data.Length; symbol++)
            {
                // The plain binary of the value, NOT Constellation.BitsOf: that takes a point and
                // applies the labelling, and these have been through it already. Sending them
                // through twice would compose the mapping with itself — which for the natural one
                // is invisible, for Gray is a different labelling nobody chose, and for an explicit
                // table is nonsense.
                int value = data[symbol];

                for (int bit = 0; bit < constellation.BitsPerSymbol; bit++)
                {
                    bits[(symbol * constellation.BitsPerSymbol) + bit] =
                        (value >> (constellation.BitsPerSymbol - 1 - bit)) & 1;
                }
            }

            context.Symbols = symbols;
            context.DataSymbols = data;
            context.IdealSymbols = ideal;
            context.Bits = bits;

            return StepOutcome.Continue;
        }

        /// <summary>What the decided symbols carried, once the reference is accounted for.</summary>
        /// <param name="symbols">The symbol decided at each instant.</param>
        /// <param name="constellation">What they were decided against.</param>
        /// <param name="differentially">Whether the bits are the change rather than the symbol.</param>
        /// <returns>One symbol value per symbol of data.</returns>
        /// <remarks>
        /// <strong>Two steps, and the order of them matters.</strong> The difference is taken
        /// between <em>points</em>, because it is a change of phase; the labelling is applied to the
        /// difference, because that is what the signal carried. Applying the labels first and
        /// subtracting afterwards would subtract two Gray codes, which is not a phase change and not
        /// anything else either. The bench settles that it is this way round: an E4438C's D8PSK is
        /// recovered symbol for symbol when the difference of the points is labelled with a Gray
        /// code, and not otherwise.
        /// </remarks>
        private static int[] Carried(
            int[] symbols, Constellation constellation, bool differentially)
        {
            if (!differentially)
            {
                var absolute = new int[symbols.Length];

                for (int symbol = 0; symbol < symbols.Length; symbol++)
                {
                    absolute[symbol] = constellation.CarriedBy(symbols[symbol]);
                }

                return absolute;
            }

            if (symbols.Length < 2)
            {
                return new int[0];
            }

            var data = new int[symbols.Length - 1];

            for (int symbol = 1; symbol < symbols.Length; symbol++)
            {
                data[symbol - 1] = constellation.CarriedBy(
                    constellation.DifferenceFrom(symbols[symbol], symbols[symbol - 1]));
            }

            return data;
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
                settings.ReferencePulse,
                Stagger(settings));

            return StepOutcome.Continue;
        }

        /// <summary>
        /// How far after I this format's Q axis is sent, in samples (<c>REQ-DEM-012</c>).
        /// </summary>
        /// <param name="settings">What the demodulation was asked for.</param>
        /// <returns>Half a symbol for an offset format, and zero for every other.</returns>
        internal static double Stagger(DemodSettings settings) =>
            settings.Constellation.IsOffset ? settings.PointsPerSymbol / 2.0 : 0.0;

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
        /// <param name="pulse">The reference filter, from <c>REQ-DEM-021</c>'s catalogue.</param>
        /// <param name="stagger">
        /// How far after I the Q axis is sent, in samples; zero for everything but an offset format.
        /// </param>
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
            PulseFilter pulse,
            double stagger)
        {
            var ideal = new double[2 * samples];

            Shape(ideal, symbols, firstInstant, samples, perSymbol, span, pulse, true);

            if (stagger == 0.0)
            {
                Shape(ideal, symbols, firstInstant, samples, perSymbol, span, pulse, false);
            }
            else
            {
                // The Q axis is a pulse train of its own, half a symbol behind: an offset format's
                // reference is not the same waveform delayed, it is two waveforms that were never
                // aligned. Regenerating it as one and delaying it would put I half a symbol late
                // as well, and the error metrics would then measure that.
                Shape(
                    ideal, symbols, firstInstant + stagger, samples, perSymbol, span, pulse, false);
            }

            return ideal;
        }

        /// <summary>Adds one axis's pulse train to a waveform.</summary>
        /// <param name="ideal">The waveform being built, interleaved.</param>
        /// <param name="symbols">The ideal symbol for each decided symbol.</param>
        /// <param name="firstInstant">Where this axis's first symbol falls, in samples.</param>
        /// <param name="samples">How many samples the grid holds.</param>
        /// <param name="perSymbol">Samples per symbol.</param>
        /// <param name="span">How many symbols either side of centre the pulse spans.</param>
        /// <param name="pulse">The reference filter.</param>
        /// <param name="inPhase">Whether this is the I axis rather than the Q axis.</param>
        private static void Shape(
            double[] ideal,
            Iq[] symbols,
            double firstInstant,
            int samples,
            int perSymbol,
            int span,
            PulseFilter pulse,
            bool inPhase)
        {
            int reach = perSymbol * span;
            int part = inPhase ? 0 : 1;

            for (int symbol = 0; symbol < symbols.Length; symbol++)
            {
                double centre = firstInstant + (symbol * perSymbol);
                double amplitude = inPhase ? symbols[symbol].I : symbols[symbol].Q;

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
                    double weight = pulse.Shape(
                        (sample - centre) / perSymbol, span, FilterRole.Reference);

                    ideal[(2 * sample) + part] += amplitude * weight;
                }
            }
        }
    }
}
