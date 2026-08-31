using System;
using System.Globalization;
using OpenVSA.Demod.Signal;

namespace OpenVSA.Demod.Chain.Steps
{
    /// <summary>
    /// The gradient equaliser modes <c>REQ-DEM-052</c> keeps for behavioural parity: complex LMS,
    /// NLMS, and the two acquisition modes that start them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This is not the equaliser; it is the alternative to it.</strong> The chain's default
    /// is the exact regularised least-squares solution, computed in one shot from the whole block,
    /// and the requirement is explicit that an iterative gradient method is strictly worse when the
    /// reference sequence is already in hand — which here it is. What justifies this code is parity:
    /// the reference product exposes a convergence factor and Run/Hold/Reset, which imply
    /// incremental adaptation, and a user may depend on the transient those controls produce.
    /// </para>
    /// <para>
    /// <strong>The step size is bounded, and the bound is enforced rather than documented.</strong>
    /// Plain LMS diverges for <c>µ ≥ 2/(L·Pₓ)</c>. That bound is not a property of the algorithm
    /// alone — it depends on the tap count and on the power of the signal actually presented — so a
    /// step size that is safe on one measurement is not safe on the next, and a user cannot be
    /// expected to recompute it when the reference level changes. The bound is therefore evaluated
    /// from the measurement's own regressors and a violating step size is refused, with the bound it
    /// violated reported. NLMS exists for those who would rather not think about it at all: its step
    /// is divided by the input's own energy, so <c>0 &lt; µ̃ &lt; 2</c> holds at every signal level.
    /// </para>
    /// <para>
    /// <strong>What "converged" means here.</strong> A pass sweeps the block up to
    /// <see cref="DemodSettings.EqualiserAdaptationSweeps"/> times, because a result window holds
    /// fewer symbols than a small step size needs updates. That makes this batch gradient descent on
    /// the objective the least-squares solution answers exactly, which is the honest description of
    /// it: cycling the block is what allows an incremental method to reach the same answer, and it
    /// is why the two can be compared at all.
    /// </para>
    /// </remarks>
    internal static class GradientEqualiser
    {
        /// <summary>What NLMS adds to the input energy so that a silent stretch cannot divide by
        /// zero.</summary>
        private const double Epsilon = 1e-12;

        /// <summary>
        /// Adapts a set of coefficients over the block.
        /// </summary>
        /// <param name="context">The chain's state, for its settings and its notices.</param>
        /// <param name="regressors">The tap inputs for every symbol, symbol-major.</param>
        /// <param name="start">What to start from; a unit impulse when there is nothing better.</param>
        /// <param name="taps">How many taps.</param>
        /// <param name="count">How many symbols.</param>
        /// <returns>The adapted coefficients, or <c>null</c> when the step size was refused.</returns>
        /// <remarks>
        /// Returning <c>null</c> leaves the caller to keep whatever coefficients were already in
        /// force, which is what makes a refused step size safe: an equaliser that will not run
        /// cannot diverge.
        /// </remarks>
        internal static Iq[] Adapt(
            DemodContext context, Iq[] regressors, Iq[] start, int taps, int count)
        {
            DemodSettings settings = context.Settings;
            Constellation constellation = settings.Constellation;

            bool normalised = settings.EqualiserAlgorithm == EqualiserAlgorithm.NormalisedLms;
            double step = settings.EqualiserConvergenceFactor;

            if (!Stable(context, regressors, taps, count, normalised, step))
            {
                return null;
            }

            var coefficients = new Iq[taps];

            start.CopyTo(coefficients, 0);

            int[] known = Aided(context, count);
            double modulus = Modulus(constellation);
            bool directed = settings.EqualiserAcquisition == EqualiserAcquisition.DecisionDirected;
            int acquired = directed ? 0 : -1;

            // THE BUDGET IS IN UPDATES, NOT SWEEPS. Data-aided acquisition updates only under the
            // sync pattern -- a few tens of symbols in a window of hundreds -- so a budget counted
            // in sweeps would hand it a fraction of the adaptation every other mode gets, and it
            // would fail for want of arithmetic rather than for want of information. Measured on a
            // closed-eye echo with a 32-symbol pattern in a 512-symbol window: fifty sweeps left it
            // at 11.8 %rms and never reaching the handover threshold, while the same number of
            // UPDATES took it to 0.70 %rms.
            long budget = (long)settings.EqualiserAdaptationSweeps * Math.Max(1, count);
            long spent = 0;

            for (int sweep = 0; spent < budget; sweep++)
            {
                double moved = 0.0;

                for (int symbol = 0; symbol < count; symbol++)
                {
                    Iq output = Output(regressors, coefficients, symbol, taps);
                    Iq error;

                    if (directed)
                    {
                        // The error against the point the output itself is nearest, which is what
                        // makes this decision-DIRECTED rather than a fit to step 9's decisions: the
                        // decisions improve as the filter does, and the filter follows them.
                        error = constellation.Ideal(constellation.Decide(output, symbol), symbol) -
                            output;
                    }
                    else if (settings.EqualiserAcquisition == EqualiserAcquisition.ConstantModulus)
                    {
                        // Godard: asks for the right modulus and nothing about the phase, so it
                        // needs to know no symbol at all.
                        error = output *
                            new Iq(modulus - output.MagnitudeSquared, 0.0);
                    }
                    else if (known != null && known[symbol] >= 0)
                    {
                        // Known, not decided: under the sync pattern the transmitted symbol is not
                        // in doubt however closed the eye is.
                        error = constellation.Ideal(known[symbol], symbol) - output;
                    }
                    else
                    {
                        // Data-aided, away from the pattern. There is no error that can be formed
                        // without a decision here, and a decision is the thing acquisition does not
                        // trust yet, so this symbol contributes nothing until the handover.
                        continue;
                    }

                    double scale = normalised
                        ? step / (Epsilon + Energy(regressors, symbol, taps))
                        : step;

                    spent++;

                    for (int tap = 0; tap < taps; tap++)
                    {
                        Iq update = error * regressors[(symbol * taps) + tap].Conjugate() * scale;

                        coefficients[tap] = coefficients[tap] + update;
                        moved += update.MagnitudeSquared;
                    }
                }

                double evm = Evm(regressors, coefficients, constellation, taps, count);

                if (!directed && evm < settings.EqualiserAcquisitionEvmPercent)
                {
                    directed = true;
                    acquired = sweep + 1;
                }

                // Nothing is moving any more, so the sweeps that remain would produce this same
                // filter at the cost of the time they take.
                if (moved < 1e-24)
                {
                    break;
                }
            }

            Report(context, acquired);

            return coefficients;
        }

        /// <summary>Whether the step size is inside the bound for the algorithm in force.</summary>
        /// <remarks>
        /// <para>
        /// <c>REQ-DEM-052</c>: "The stability bound <c>0 &lt; µ &lt; 2/(L·Pₓ)</c> is enforced and a
        /// violating step size is rejected with the bound reported, or NLMS is selected instead."
        /// Both halves are here — the bound is enforced for LMS, and NLMS is the alternative the
        /// user selects to be free of it, where the condition is on the normalised step alone.
        /// </para>
        /// <para>
        /// <strong>Refused, not clamped.</strong> A step size quietly reduced to the largest safe
        /// one produces a measurement the user did not ask for and cannot tell from the one they
        /// did. Refusing it leaves the coefficients as they were, reports the number that would have
        /// been needed, and lets the user decide — which is also the only reading under which "a
        /// violating step size is rejected" is true.
        /// </para>
        /// </remarks>
        private static bool Stable(
            DemodContext context,
            Iq[] regressors,
            int taps,
            int count,
            bool normalised,
            double step)
        {
            if (normalised)
            {
                if (step < 2.0)
                {
                    return true;
                }

                context.Note(
                    "The equaliser's convergence factor is " +
                    step.ToString("G4", CultureInfo.InvariantCulture) +
                    ", and normalised LMS is stable only below 2. The step size was refused and " +
                    "the coefficients were left as they were.");

                return false;
            }

            double power = 0.0;

            for (int entry = 0; entry < count * taps; entry++)
            {
                power += regressors[entry].MagnitudeSquared;
            }

            power /= Math.Max(1, count * taps);

            double bound = power <= 0.0 ? double.PositiveInfinity : 2.0 / (taps * power);

            if (step < bound)
            {
                return true;
            }

            context.Note(
                "The equaliser's convergence factor is " +
                step.ToString("G4", CultureInfo.InvariantCulture) +
                ", which is outside the stability bound 2/(L*Px) = " +
                bound.ToString("G4", CultureInfo.InvariantCulture) + " for " +
                taps.ToString(CultureInfo.InvariantCulture) + " taps at a signal power of " +
                power.ToString("G4", CultureInfo.InvariantCulture) +
                ". The step size was refused and the coefficients were left as they were: LMS " +
                "above the bound does not converge slowly, it diverges. Use a smaller step size, " +
                "or normalised LMS, whose step means the same thing at every signal level.");

            return false;
        }

        /// <summary>Says whether and when acquisition handed over.</summary>
        private static void Report(DemodContext context, int acquired)
        {
            if (acquired == 0)
            {
                return;
            }

            if (acquired < 0)
            {
                context.Note(
                    "The equaliser's acquisition mode did not reach the handover threshold of " +
                    context.Settings.EqualiserAcquisitionEvmPercent.ToString(
                        "G4", CultureInfo.InvariantCulture) +
                    " %rms within " + context.Settings.EqualiserAdaptationSweeps.ToString(
                        CultureInfo.InvariantCulture) +
                    " sweeps, so it never handed over to decision-directed adaptation. The " +
                    "coefficients are an acquisition's, not a measurement's.");

                return;
            }

            context.Note(
                "The equaliser acquired in " + acquired.ToString(CultureInfo.InvariantCulture) +
                " sweep(s) and handed over to decision-directed adaptation at the " +
                context.Settings.EqualiserAcquisitionEvmPercent.ToString(
                    "G4", CultureInfo.InvariantCulture) + " %rms threshold.");
        }

        /// <summary>The equaliser's output at one symbol.</summary>
        private static Iq Output(Iq[] regressors, Iq[] coefficients, int symbol, int taps)
        {
            Iq output = Iq.Zero;

            for (int tap = 0; tap < taps; tap++)
            {
                output = output + (coefficients[tap] * regressors[(symbol * taps) + tap]);
            }

            return output;
        }

        /// <summary>The energy of one symbol's tap inputs, for the normalised step.</summary>
        private static double Energy(Iq[] regressors, int symbol, int taps)
        {
            double energy = 0.0;

            for (int tap = 0; tap < taps; tap++)
            {
                energy += regressors[(symbol * taps) + tap].MagnitudeSquared;
            }

            return energy;
        }

        /// <summary>How far the current filter's outputs are from the nearest points, as EVM.</summary>
        /// <remarks>
        /// Measured against decisions rather than against known symbols, because this is what
        /// decides whether decisions can be trusted — and a signal whose outputs sit close to
        /// <em>some</em> point is one whose decisions have become worth directing on. It is the same
        /// quantity the measurement reports later, computed on the equaliser's own output.
        /// </remarks>
        private static double Evm(
            Iq[] regressors,
            Iq[] coefficients,
            Constellation constellation,
            int taps,
            int count)
        {
            double difference = 0.0;
            double reference = 0.0;

            for (int symbol = 0; symbol < count; symbol++)
            {
                Iq output = Output(regressors, coefficients, symbol, taps);
                Iq ideal = constellation.Ideal(constellation.Decide(output, symbol), symbol);

                difference += (output - ideal).MagnitudeSquared;
                reference += ideal.MagnitudeSquared;
            }

            return reference <= 0.0
                ? double.PositiveInfinity
                : 100.0 * Math.Sqrt(difference / reference);
        }

        /// <summary>Godard's <c>R₂ = E|a|⁴/E|a|²</c> for the constellation in force.</summary>
        /// <remarks>
        /// Computed from the points rather than written down as 1, so that a format whose points are
        /// not all one modulus gets the dispersion constant that belongs to it. For QPSK, whose
        /// points are all unit modulus, it comes to exactly 1 and the error reduces to the familiar
        /// <c>y(1 − |y|²)</c>.
        /// </remarks>
        private static double Modulus(Constellation constellation)
        {
            double fourth = 0.0;
            double square = 0.0;

            for (int point = 0; point < constellation.Count; point++)
            {
                double magnitude = constellation.Ideal(point).MagnitudeSquared;

                fourth += magnitude * magnitude;
                square += magnitude;
            }

            return square <= 0.0 ? 1.0 : fourth / square;
        }

        /// <summary>
        /// Which symbol of the window is known from the sync pattern, or <c>-1</c> where none is.
        /// </summary>
        /// <param name="context">The chain's state.</param>
        /// <param name="count">How many symbols the window holds.</param>
        /// <returns>The known symbol at each index, or <c>null</c> when nothing is known.</returns>
        /// <remarks>
        /// The result window starts at the pattern's first symbol displaced by
        /// <c>REQ-DEM-040</c>'s Search Offset, so window symbol <c>i</c> is pattern symbol
        /// <c>i + offset</c> — which is outside the pattern for most of the window, and that is the
        /// point: data-aided acquisition has a few tens of symbols to work with and hands over for
        /// the rest.
        /// </remarks>
        private static int[] Aided(DemodContext context, int count)
        {
            DemodSettings settings = context.Settings;

            if (settings.EqualiserAcquisition != EqualiserAcquisition.DataAided)
            {
                return null;
            }

            int[] pattern = settings.SyncSymbols();

            if (!context.SyncFound || pattern == null || pattern.Length == 0)
            {
                context.Note(
                    "The equaliser was asked to acquire from a known sync sequence, but " +
                    (pattern == null || pattern.Length == 0
                        ? "no sync pattern is set"
                        : "step 6 did not find the pattern") +
                    ", so there is no symbol it can be sure of. It adapted on its decisions " +
                    "instead, which is what it would have done with no acquisition mode at all.");

                return null;
            }

            var known = new int[count];
            int offset = settings.SearchOffsetSymbols;
            int found = 0;

            for (int symbol = 0; symbol < count; symbol++)
            {
                int at = symbol + offset;

                known[symbol] = at >= 0 && at < pattern.Length ? pattern[at] : -1;

                if (known[symbol] >= 0)
                {
                    found++;
                }
            }

            if (found != 0)
            {
                return known;
            }

            context.Note(
                "The equaliser was asked to acquire from the sync sequence, but the Search Offset " +
                "puts the whole pattern outside the result window, so none of its symbols is in " +
                "the block being adapted on. It adapted on its decisions instead.");

            return null;
        }
    }
}
