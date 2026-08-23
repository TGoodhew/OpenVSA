using System;
using System.Globalization;
using OpenVSA.Demod.Signal;

namespace OpenVSA.Demod.Chain.Steps
{
    /// <summary>
    /// Step 11, optional: fit an equaliser to the channel, and re-enter the chain at step 8.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Scope.</strong> <c>REQ-DEM-050</c> to <c>REQ-DEM-053</c> specify the equaliser
    /// properly — its parameters and modes, the least-squares and LMS algorithms it offers for
    /// parity, and the traces it produces. What <c>REQ-DEM-001</c> needs, and what this is, is the
    /// step that closes the chain's one loop: it fits coefficients against the regenerated
    /// reference of step 10 and, when they move, sends the chain back to step 8 so the joint
    /// refinement runs again on what it produced.
    /// </para>
    /// <para>
    /// <strong>The loop is genuine, and this is what makes it so.</strong> The equalised waveform
    /// replaces the result window, so the second pass's step 8 estimates carrier, timing and
    /// amplitude on a signal whose linear distortion has been taken out — which is a different
    /// estimation problem with a different answer, not a repetition. On a signal with a distorting
    /// channel the second pass's EVM is lower than the first's, and that difference is
    /// <c>REQ-DEM-001</c>'s own test of whether the re-entry is real.
    /// </para>
    /// <para>
    /// <strong>Why the carrier is taken out before the fit and left out afterwards.</strong> The
    /// equaliser is a time-invariant filter, and a residual carrier offset is a rotation that grows
    /// along the block; fitting one with the other still present would ask a fixed set of
    /// coefficients to model a rotation that is different at every sample. So the waveform is
    /// derotated by step 8's estimates first, and the derotated form is what is stored back. The
    /// estimates have already been accumulated into the totals the result reports, and the
    /// per-pass estimates are cleared here so the next pass starts from a waveform with nothing
    /// left to remove but what the equaliser did not fix.
    /// </para>
    /// <para>
    /// <strong>The update is measured on the waveform, not on the coefficients.</strong> Whether
    /// the equaliser changed anything is a question about what it did to the signal, and asking it
    /// of the taps gives the wrong answer. The taps are not unique: the signal occupies about a
    /// third of the band the working rate could carry, so every filter that agrees inside that band
    /// acts identically on it and differs freely outside. Regularised, the fit returns the
    /// smallest-energy member of that family — which on a clean signal is a band-limiting kernel,
    /// measurably transparent and nowhere near a single tap of one. Compared against the identity
    /// it looked like a change of 0.63 on a signal it had not changed at all, and the chain
    /// re-entered to its bound every time. Compared on the waveform, the same case is a change of
    /// nothing.
    /// </para>
    /// </remarks>
    internal sealed class EqualiserStep : IChainStep
    {
        /// <summary>
        /// How much the normal equations are loaded on their diagonal, relative to their average
        /// diagonal term.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <strong>Without this the fit is not merely inaccurate, it is unstable.</strong> The
        /// equaliser's taps are one working sample apart — four to a symbol — while the signal only
        /// occupies about a third of the band that rate could carry. The rest of the band has no
        /// signal in it, so the autocorrelation matrix is nearly singular there, and a least-squares
        /// solution is free to put anything it likes into that null space at no cost to the fit.
        /// What it puts there is large, and the next pass amplifies it further: measured on this
        /// chain's own inter-symbol-interference test, EVM went 10 %, then 2 %, then 22 % over
        /// three passes.
        /// </para>
        /// <para>
        /// A thousandth of the average diagonal is enough to make the null space cost something
        /// without measurably biasing the part of the solution the data does determine.
        /// <c>REQ-DEM-052</c> owns the equaliser's algorithm and may state the regularisation
        /// differently — as tap leakage, which is the same idea worn as an adaptive-filter
        /// parameter — but it will need one.
        /// </para>
        /// </remarks>
        private const double DiagonalLoading = 1e-3;

        /// <inheritdoc />
        public DemodStep Step => DemodStep.Equaliser;

        /// <inheritdoc />
        public StepOutcome Run(DemodContext context)
        {
            double[] result = DemodContext.Require(
                context.Result, DemodStep.ResultWindow, DemodStep.Equaliser);

            double[] ideal = DemodContext.Require(
                context.IdealWaveform, DemodStep.ReferenceRegeneration, DemodStep.Equaliser);

            DemodSettings settings = context.Settings;

            int perSymbol = settings.PointsPerSymbol;
            int samples = Iq.Count(result);
            int taps = settings.EqualiserTaps;
            int half = taps / 2;

            double[] derotated = Derotate(context, result, samples, perSymbol);

            // The fit runs over the interior, not over every symbol in the window. The reference
            // waveform of step 10 is built from the symbols the window holds, so within a pulse
            // length of either end it is missing the tails of the symbols just outside — the
            // measured waveform has them and the reference does not. Fitting there asks the
            // equaliser to explain that difference, and it obliges, by building the window's edges
            // into its coefficients. That is not a subtle effect: fitted edge to edge on this
            // chain's own test signal it made EVM worse, three times over, on every pass.
            int guard = (context.Settings.FilterSymbolSpan * perSymbol) + half;

            int from = (int)Math.Ceiling(context.TimingSamples) + guard;
            int to = (int)Math.Floor(
                context.TimingSamples + ((context.ResultSymbolCount - 1) * perSymbol)) - guard;

            from = Math.Max(from, half);
            to = Math.Min(to, samples - 1 - half);

            if (to - from < taps * 2)
            {
                context.Note(
                    "The equaliser had " + Math.Max(0, to - from).ToString(CultureInfo.InvariantCulture) +
                    " samples to fit " + taps.ToString(CultureInfo.InvariantCulture) +
                    " taps over, which is not enough to fit anything. It did nothing.");

                return StepOutcome.Continue;
            }

            Iq[] coefficients = Fit(derotated, ideal, from, to, taps, half);

            if (coefficients == null)
            {
                context.Note(
                    "The equaliser's normal equations were singular, so no coefficients could be " +
                    "fitted. The waveform was left as it was.");

                return StepOutcome.Continue;
            }

            double[] equalised = Apply(derotated, samples, coefficients, half);
            double change = Difference(equalised, derotated, from, to);

            context.Result = equalised;
            context.EqualiserCoefficients = coefficients;

            // Everything step 8 estimated this pass is now part of the waveform rather than a
            // correction waiting to be applied to it.
            context.PassFrequencyHz = 0.0;
            context.PassPhaseRadians = 0.0;
            context.PassGain = 1.0;

            context.EqualiserUpdated = change > settings.EqualiserUpdateThreshold;

            return context.EqualiserUpdated ? StepOutcome.ReEnter : StepOutcome.Continue;
        }

        private static double[] Derotate(
            DemodContext context, double[] result, int samples, int perSymbol)
        {
            double omega =
                2.0 * Math.PI * context.PassFrequencyHz / context.Settings.SymbolRateHz;

            double phase = context.PassPhaseRadians;
            double gain = context.PassGain;
            double timing = context.TimingSamples;

            var derotated = new double[2 * samples];

            for (int sample = 0; sample < samples; sample++)
            {
                double symbols = (sample - timing) / perSymbol;
                Iq turn = Iq.FromPhase(-((omega * symbols) + phase));

                Iq.Set(derotated, sample, (Iq.At(result, sample) * turn) / gain);
            }

            return derotated;
        }

        private static Iq[] Fit(
            double[] measured, double[] ideal, int from, int to, int taps, int half)
        {
            var matrix = new Iq[taps * taps];
            var right = new Iq[taps];

            for (int row = 0; row < taps; row++)
            {
                int rowDelay = row - half;

                for (int column = 0; column < taps; column++)
                {
                    int columnDelay = column - half;
                    Iq sum = Iq.Zero;

                    for (int sample = from; sample <= to; sample++)
                    {
                        Iq a = Iq.At(measured, sample - columnDelay);
                        Iq b = Iq.At(measured, sample - rowDelay);

                        sum = sum + (a * b.Conjugate());
                    }

                    matrix[(row * taps) + column] = sum;
                }

                Iq correlation = Iq.Zero;

                for (int sample = from; sample <= to; sample++)
                {
                    Iq target = Iq.At(ideal, sample);
                    Iq source = Iq.At(measured, sample - rowDelay);

                    correlation = correlation + (target * source.Conjugate());
                }

                right[row] = correlation;
            }

            double diagonal = 0.0;

            for (int tap = 0; tap < taps; tap++)
            {
                diagonal += matrix[(tap * taps) + tap].Magnitude;
            }

            double loading = DiagonalLoading * diagonal / taps;

            for (int tap = 0; tap < taps; tap++)
            {
                matrix[(tap * taps) + tap] =
                    matrix[(tap * taps) + tap] + new Iq(loading, 0.0);
            }

            return ComplexSolver.Solve(matrix, right, taps);
        }

        private static double[] Apply(double[] measured, int samples, Iq[] coefficients, int half)
        {
            var equalised = new double[2 * samples];

            for (int sample = 0; sample < samples; sample++)
            {
                Iq sum = Iq.Zero;

                for (int tap = 0; tap < coefficients.Length; tap++)
                {
                    int source = sample - (tap - half);

                    if (source < 0 || source >= samples)
                    {
                        continue;
                    }

                    sum = sum + (coefficients[tap] * Iq.At(measured, source));
                }

                Iq.Set(equalised, sample, sum);
            }

            return equalised;
        }

        /// <summary>
        /// How much the equaliser changed the waveform, as a fraction of its energy.
        /// </summary>
        /// <param name="equalised">What the equaliser produced.</param>
        /// <param name="measured">What it was given.</param>
        /// <param name="from">The first sample of the region fitted over.</param>
        /// <param name="to">The last.</param>
        /// <returns>The energy of the difference, over the energy of the input.</returns>
        private static double Difference(double[] equalised, double[] measured, int from, int to)
        {
            double difference = 0.0;
            double energy = 0.0;

            for (int sample = from; sample <= to; sample++)
            {
                Iq before = Iq.At(measured, sample);
                Iq after = Iq.At(equalised, sample);

                difference += (after - before).MagnitudeSquared;
                energy += before.MagnitudeSquared;
            }

            return energy < 1e-30 ? 0.0 : difference / energy;
        }
    }
}
