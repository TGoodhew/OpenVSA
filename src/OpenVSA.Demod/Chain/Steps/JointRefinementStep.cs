using System;
using System.Globalization;
using OpenVSA.Demod.Signal;

namespace OpenVSA.Demod.Chain.Steps
{
    /// <summary>
    /// Step 8: refine carrier frequency, carrier phase, symbol timing and amplitude together,
    /// iterating to convergence.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Jointly, because they are not separable.</strong> A frequency error looks like a
    /// phase that grows; a timing error on a pulse-shaped signal looks like an amplitude that
    /// shrinks; an amplitude error looks like a constellation that has moved in. Estimating any one
    /// of them while the others are wrong gives an answer that is wrong by whatever the others
    /// were, which is why this is one step in the specification's chain and not four.
    /// </para>
    /// <para>
    /// <strong>Block estimation, per <c>REQ-DEM-002</c>.</strong> Each iteration fits one frequency,
    /// one phase, one timing offset and one gain across the whole Result Length by least squares.
    /// There is no loop bandwidth, nothing settles, and the first symbol is estimated as well as the
    /// last — which is the property the reference product's documented behaviour on short bursts is
    /// consistent with, and the reason that requirement records the choice as a design decision
    /// rather than as a deduction.
    /// </para>
    /// <para>
    /// <strong>Decision-directed, and the decisions here are provisional.</strong> The iteration
    /// needs something to fit towards, so it decides each symbol against the constellation as it
    /// goes. Those decisions are internal to the fit; step 9 is where decisions become the symbols
    /// and bits the result reports. Keeping the two apart matters because they can differ: step 9
    /// decides once, on the converged parameters, and its answer is the one that is defensible.
    /// </para>
    /// <para>
    /// <strong>Convergence is stated, bounded, and reported.</strong> The criterion is that every
    /// parameter moved by less than <see cref="DemodSettings.RefinementTolerance"/> on an
    /// iteration — frequency in cycles per symbol, phase in radians, timing in samples and gain as
    /// a fraction, four quantities that are all dimensionless once expressed per symbol.
    /// <see cref="DemodSettings.MaxRefinementIterations"/> bounds the count, and reaching that
    /// bound produces a <see cref="ConvergenceReport"/> that says so and a notice on the result.
    /// <c>REQ-DEM-001</c> asks for exactly that: the bound "is reported rather than silently
    /// accepted".
    /// </para>
    /// </remarks>
    internal sealed class JointRefinementStep : IChainStep
    {
        /// <summary>The most the timing estimate may move in one iteration, in symbols.</summary>
        /// <remarks>
        /// A quarter of a symbol. The timing update is a Gauss-Newton step along the waveform's
        /// slope, and on a signal whose slope reverses — which is every pulse-shaped signal, half a
        /// symbol away from the decision instant — an unbounded step can jump to the neighbouring
        /// symbol and converge neatly onto the wrong one.
        /// </remarks>
        private const double MaximumTimingStepSymbols = 0.25;

        /// <inheritdoc />
        public DemodStep Step => DemodStep.JointRefinement;

        /// <inheritdoc />
        public StepOutcome Run(DemodContext context)
        {
            double[] result = DemodContext.Require(
                context.Result, DemodStep.ResultWindow, DemodStep.JointRefinement);

            DemodSettings settings = context.Settings;
            Constellation constellation = settings.Constellation;

            int perSymbol = settings.PointsPerSymbol;
            int count = context.ResultSymbolCount;
            int samples = Iq.Count(result);

            double omega = 0.0;
            double phase = 0.0;
            double timing = InitialTiming(result, context.TimingSamples, perSymbol, samples, count);
            double gain = InitialGain(result, timing, perSymbol, count);

            var measured = new Iq[count];
            var decided = new Iq[count];

            int iterations = 0;
            bool converged = false;
            double largest = double.MaxValue;

            for (int iteration = 1; iteration <= settings.MaxRefinementIterations; iteration++)
            {
                iterations = iteration;

                Project(result, measured, timing, omega, phase, gain, perSymbol, count);

                for (int symbol = 0; symbol < count; symbol++)
                {
                    decided[symbol] = constellation.Ideal(
                        constellation.Decide(measured[symbol].I, measured[symbol].Q));
                }

                double deltaOmega;
                double deltaPhase;

                FitPhaseRamp(measured, decided, count, out deltaOmega, out deltaPhase);

                double gainRatio = FitGain(measured, decided, count);

                double deltaTiming = FitTiming(
                    result, measured, decided, timing, omega, phase, gain, perSymbol, count);

                double limit = MaximumTimingStepSymbols * perSymbol;

                if (deltaTiming > limit)
                {
                    deltaTiming = limit;
                }
                else if (deltaTiming < -limit)
                {
                    deltaTiming = -limit;
                }

                omega += deltaOmega;
                phase += deltaPhase;
                gain *= gainRatio;
                timing = Clamp(timing + deltaTiming, samples, perSymbol, count);

                largest = Math.Max(
                    Math.Abs(deltaOmega) / (2.0 * Math.PI),
                    Math.Max(
                        Math.Abs(deltaPhase),
                        Math.Max(Math.Abs(deltaTiming), Math.Abs(gainRatio - 1.0))));

                if (largest < settings.RefinementTolerance)
                {
                    converged = true;

                    break;
                }
            }

            Project(result, measured, timing, omega, phase, gain, perSymbol, count);

            double frequencyHz = omega * settings.SymbolRateHz / (2.0 * Math.PI);

            context.PassFrequencyHz = frequencyHz;
            context.PassPhaseRadians = phase;
            context.PassGain = gain;

            context.ResidualFrequencyHz += frequencyHz;
            context.PhaseRadians += phase;
            context.Gain *= gain;
            context.TimingSamples = timing;
            context.MeasuredSymbols = measured;

            var report = new ConvergenceReport(
                iterations,
                settings.MaxRefinementIterations,
                converged,
                largest,
                settings.RefinementTolerance);

            context.Convergence = report;

            if (!converged)
            {
                context.Note(
                    "Step 8 reached its bound of " +
                    settings.MaxRefinementIterations.ToString(CultureInfo.InvariantCulture) +
                    " iterations on pass " + context.Pass.ToString(CultureInfo.InvariantCulture) +
                    " without meeting the convergence criterion (" + report.Criterion +
                    "). The largest change on the last iteration was " +
                    largest.ToString("G3", CultureInfo.InvariantCulture) +
                    ". The estimates are the ones it had got to, not the ones it was heading for.");
            }

            return StepOutcome.Continue;
        }

        /// <summary>
        /// Where the symbol instants are, before any decision has been made.
        /// </summary>
        /// <param name="result">The result window.</param>
        /// <param name="nominal">Where step 7 put the first symbol.</param>
        /// <param name="perSymbol">The internal processing rate.</param>
        /// <param name="samples">How long the window is.</param>
        /// <param name="count">How many symbols it holds.</param>
        /// <returns>The first symbol's instant, in samples from the window's start.</returns>
        /// <remarks>
        /// <para>
        /// <strong>The iteration is local, so its starting point has to be roughly right.</strong>
        /// A decision-directed fit that begins halfway between two symbols decides on samples that
        /// are halfway between two constellation points, and then fits perfectly well to those
        /// wrong decisions: it converges, reports that it converged, and returns an EVM of around
        /// 50 %. That is not a hypothetical — it is what this step did before this method existed.
        /// </para>
        /// <para>
        /// The estimate is the square-law one: the squared magnitude of a pulse-shaped signal has a
        /// component at the symbol rate whose phase says where the symbol instants fall, and one
        /// sum over the block reads it. It needs no decisions, so it cannot be misled by them, and
        /// it is a block estimate over the whole window rather than a loop — which is what
        /// <c>REQ-DEM-002</c> asks of everything in this part of the chain.
        /// </para>
        /// <para>
        /// It resolves the instants only to within a symbol, which is all that is wanted: step 7
        /// has already decided which symbol the window starts on, and this says where within that
        /// symbol the decision instant sits. The answer is therefore taken to the value congruent
        /// to the estimate that is nearest step 7's nominal position, and never further than half a
        /// symbol from it — the window's margins are sized for exactly that much movement.
        /// </para>
        /// </remarks>
        private static double InitialTiming(
            double[] result, double nominal, int perSymbol, int samples, int count)
        {
            double real = 0.0;
            double imaginary = 0.0;

            for (int sample = 0; sample < samples; sample++)
            {
                double power = Iq.At(result, sample).MagnitudeSquared;
                double angle = -2.0 * Math.PI * sample / perSymbol;

                real += power * Math.Cos(angle);
                imaginary += power * Math.Sin(angle);
            }

            if ((real * real) + (imaginary * imaginary) < 1e-30)
            {
                return nominal;
            }

            double estimate = -Math.Atan2(imaginary, real) * perSymbol / (2.0 * Math.PI);

            // Congruent to the estimate, modulo a symbol, and as near step 7's position as that
            // allows.
            double shift = estimate - nominal;

            shift -= perSymbol * Math.Round(shift / perSymbol);

            return Clamp(nominal + shift, samples, perSymbol, count);
        }

        private static double InitialGain(
            double[] result, double timing, int perSymbol, int count)
        {
            double power = 0.0;

            for (int symbol = 0; symbol < count; symbol++)
            {
                power += Interpolator.At(result, timing + (symbol * perSymbol)).MagnitudeSquared;
            }

            double rms = Math.Sqrt(power / count);

            // The constellation is normalised to unit mean power, so the signal's own RMS at the
            // symbol instants is the gain to a first approximation. Starting the iteration from one
            // instead would make the first set of decisions those of a constellation the wrong size,
            // and on anything but QPSK the wrong size means the wrong ring.
            return rms < 1e-15 ? 1.0 : rms;
        }

        private static void Project(
            double[] result,
            Iq[] measured,
            double timing,
            double omega,
            double phase,
            double gain,
            int perSymbol,
            int count)
        {
            for (int symbol = 0; symbol < count; symbol++)
            {
                Iq value = Interpolator.At(result, timing + (symbol * perSymbol));
                Iq turn = Iq.FromPhase(-((omega * symbol) + phase));

                measured[symbol] = (value * turn) / gain;
            }
        }

        private static void FitPhaseRamp(
            Iq[] measured, Iq[] decided, int count, out double deltaOmega, out double deltaPhase)
        {
            double weight = 0.0;
            double weightIndex = 0.0;
            double weightIndexSquared = 0.0;
            double weightAngle = 0.0;
            double weightIndexAngle = 0.0;

            for (int symbol = 0; symbol < count; symbol++)
            {
                Iq residual = measured[symbol] * decided[symbol].Conjugate();

                if (residual.MagnitudeSquared < 1e-24)
                {
                    continue;
                }

                double angle = residual.Phase;
                double w = decided[symbol].MagnitudeSquared;

                weight += w;
                weightIndex += w * symbol;
                weightIndexSquared += w * symbol * symbol;
                weightAngle += w * angle;
                weightIndexAngle += w * symbol * angle;
            }

            double determinant = (weight * weightIndexSquared) - (weightIndex * weightIndex);

            if (Math.Abs(determinant) < 1e-18)
            {
                deltaOmega = 0.0;
                deltaPhase = weight < 1e-18 ? 0.0 : weightAngle / weight;

                return;
            }

            deltaOmega =
                ((weight * weightIndexAngle) - (weightIndex * weightAngle)) / determinant;

            deltaPhase =
                ((weightIndexSquared * weightAngle) - (weightIndex * weightIndexAngle)) /
                determinant;
        }

        private static double FitGain(Iq[] measured, Iq[] decided, int count)
        {
            double projection = 0.0;
            double reference = 0.0;

            for (int symbol = 0; symbol < count; symbol++)
            {
                Iq product = measured[symbol] * decided[symbol].Conjugate();

                projection += product.I;
                reference += decided[symbol].MagnitudeSquared;
            }

            if (reference < 1e-18 || projection <= 0.0)
            {
                return 1.0;
            }

            return projection / reference;
        }

        private static double FitTiming(
            double[] result,
            Iq[] measured,
            Iq[] decided,
            double timing,
            double omega,
            double phase,
            double gain,
            int perSymbol,
            int count)
        {
            double projection = 0.0;
            double energy = 0.0;

            for (int symbol = 0; symbol < count; symbol++)
            {
                Iq slope = Interpolator.SlopeAt(result, timing + (symbol * perSymbol));
                Iq turn = Iq.FromPhase(-((omega * symbol) + phase));

                Iq corrected = (slope * turn) / gain;
                Iq error = decided[symbol] - measured[symbol];

                Iq product = corrected.Conjugate() * error;

                projection += product.I;
                energy += corrected.MagnitudeSquared;
            }

            return energy < 1e-18 ? 0.0 : projection / energy;
        }

        private static double Clamp(double timing, int samples, int perSymbol, int count)
        {
            double last = samples - 1 - ((count - 1) * perSymbol);

            if (last < 0.0)
            {
                return timing;
            }

            if (timing < 0.0)
            {
                return 0.0;
            }

            return timing > last ? last : timing;
        }
    }
}
