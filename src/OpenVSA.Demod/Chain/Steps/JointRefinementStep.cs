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

            // Half a symbol for an offset format, and nothing for every other: REQ-DEM-012's two
            // instants per symbol, carried through this step as one number so that every place that
            // reads the waveform at a symbol instant reads both of them or neither.
            double stagger = constellation.IsOffset ? perSymbol / 2.0 : 0.0;

            double omega = 0.0;
            double phase = 0.0;
            double timing;

            if (constellation.IsOffset)
            {
                Align(
                    result,
                    context.TimingSamples,
                    perSymbol,
                    samples,
                    count,
                    constellation,
                    out timing,
                    out phase);
            }
            else
            {
                timing = InitialTiming(result, context.TimingSamples, perSymbol, samples, count);
            }

            double gain = InitialGain(result, timing, perSymbol, count, stagger, phase);

            var measured = new Iq[count];
            var decided = new Iq[count];

            int iterations = 0;
            bool converged = false;
            double largest = double.MaxValue;

            for (int iteration = 1; iteration <= settings.MaxRefinementIterations; iteration++)
            {
                iterations = iteration;

                Project(result, measured, timing, omega, phase, gain, perSymbol, count, stagger);

                for (int symbol = 0; symbol < count; symbol++)
                {
                    decided[symbol] = constellation.Ideal(
                        constellation.Decide(measured[symbol], symbol), symbol);
                }

                double deltaOmega;
                double deltaPhase;

                FitPhaseRamp(measured, decided, count, out deltaOmega, out deltaPhase);

                double gainRatio = FitGain(measured, decided, count);

                double deltaTiming = FitTiming(
                    result, measured, decided, timing, omega, phase, gain, perSymbol, count, stagger);

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
                timing = Clamp(timing + deltaTiming, samples, perSymbol, count, stagger);

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

            Project(result, measured, timing, omega, phase, gain, perSymbol, count, stagger);

            double frequencyHz = omega * settings.SymbolRateHz / (2.0 * Math.PI);

            context.PassFrequencyHz = frequencyHz;
            context.PassPhaseRadians = phase;
            context.PassGain = gain;

            context.ResidualFrequencyHz += frequencyHz;
            context.PhaseRadians += phase;
            context.Gain *= gain;
            context.TimingSamples = timing;
            context.MeasuredSymbols = measured;

            if (stagger != 0.0)
            {
                // REQ-DEM-062's other half. The points above are the format's own: I at the symbol
                // instant and Q half a symbol later, which is where an offset format PUT them.
                // These are the same waveform read as though it were not an offset format at all --
                // both parts at the one instant -- and they exist so that the Offset EVM variant has
                // something to be a variant OF. On a clean OQPSK signal the two differ by orders of
                // magnitude, and that difference is the evidence that the stagger is honoured
                // rather than an assertion that it is.
                //
                // One extra projection with the converged parameters: no fitting, no iteration.
                var common = new Iq[count];

                Project(result, common, timing, omega, phase, gain, perSymbol, count, 0.0);

                context.CommonInstantSymbols = common;
            }

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

            return Clamp(nominal + shift, samples, perSymbol, count, 0.0);
        }

        /// <summary>
        /// Where an offset format's symbol instants and carrier phase start from, searched rather
        /// than derived (<c>REQ-DEM-012</c>).
        /// </summary>
        /// <param name="result">The result window.</param>
        /// <param name="nominal">Where step 7 put the first symbol.</param>
        /// <param name="perSymbol">The internal processing rate.</param>
        /// <param name="samples">How long the window is.</param>
        /// <param name="count">How many symbols it holds.</param>
        /// <param name="constellation">What the symbols are decided against.</param>
        /// <param name="timing">The first symbol's instant, in samples from the window's start.</param>
        /// <param name="phase">The carrier phase to start the iteration from, in radians.</param>
        /// <remarks>
        /// <para>
        /// <strong>An offset format has no timing tone to read, and this is why.</strong> The
        /// square-law estimator the rest of the catalogue uses works because the squared magnitude of
        /// a pulse-shaped signal carries a component at the symbol rate. For an offset format it does
        /// not: the I stream contributes one and the Q stream contributes another half a symbol
        /// later, which is half a cycle at that frequency, and two equal powers half a cycle apart
        /// cancel exactly. Nor is there anything at twice the rate to fall back on — that would need
        /// the signal to be more than a symbol rate wide, and a root raised cosine of roll-off α is
        /// (1 + α)/2 wide. Both were checked before this was written, and both are zero rather than
        /// small.
        /// </para>
        /// <para>
        /// <strong>And timing cannot be separated from carrier phase here.</strong> The two axes are
        /// sampled at different instants, so they have to be told apart before either can be read —
        /// and what tells them apart is the carrier phase, which is not known yet. So the two are
        /// searched together on a coarse grid, scored by how near the pairs land to the
        /// constellation, and handed to the iteration to refine. A block estimate, per
        /// <c>REQ-DEM-002</c>, and one with no starting point of its own to be wrong.
        /// </para>
        /// <para>
        /// <strong>Half a symbol and a quarter-turn together are a free parameter, and no
        /// estimator can resolve them.</strong> Reading the signal half a symbol late and turning it
        /// by 90° gives the pair (Q of symbol k, −I of symbol k+1) — every one of them an exact
        /// constellation point, and so a demodulation with the same near-zero EVM as the intended
        /// one and a different pairing of the bits. The two alignments score identically here
        /// because they are identically good; which of them a measurement lands on is settled by the
        /// sync-pattern search of <c>REQ-DEM-040</c> and by nothing in this step. Reading a passing
        /// EVM as evidence that the bits are paired the way the transmitter paired them is therefore
        /// a mistake — the same shape of mistake as reading a passing bit check as evidence about
        /// spectral sense.
        /// </para>
        /// </remarks>
        private static void Align(
            double[] result,
            double nominal,
            int perSymbol,
            int samples,
            int count,
            Constellation constellation,
            out double timing,
            out double phase)
        {
            // Sixteen positions across a symbol and sixteen angles around the circle. The iteration
            // that follows converges from a quarter of a symbol and a sixteenth of a turn without
            // difficulty; a finer grid here would cost time to arrive at the same answer.
            const int Positions = 16;
            const int Angles = 16;

            // Scored on at most this many symbols. The measure is an average over symbols, and a
            // hundred of them settle it to far better than the grid's own spacing, so a 2 048-symbol
            // Result Length pays for a hundred rather than for all of it.
            const int MostSymbols = 100;

            double stagger = perSymbol / 2.0;
            int scored = Math.Min(count, MostSymbols);

            double best = double.MaxValue;

            timing = nominal;
            phase = 0.0;

            for (int position = 0; position < Positions; position++)
            {
                double candidate = Clamp(
                    nominal + (perSymbol * (Outward(position) / (double)Positions)),
                    samples,
                    perSymbol,
                    count,
                    stagger);

                for (int angle = 0; angle < Angles; angle++)
                {
                    double turn = 2.0 * Math.PI * Outward(angle) / Angles;

                    double residual = Residual(
                        result, candidate, turn, perSymbol, scored, stagger, constellation);

                    // Twice as good, not merely better, and the search runs outwards from the
                    // nominal position and from no rotation. There are always four alignments that
                    // fit — the remarks above say why — and they fit to within a few per cent of one
                    // another rather than exactly, so a margin of a rounding error would let the
                    // noise pick between them and a display would turn by a quarter between one
                    // frame and the next. A factor of two separates them from a genuinely better
                    // alignment, which is orders of magnitude better and not a few per cent: a
                    // candidate off the symbol instant leaves the samples halfway between
                    // constellation points, and the residual says so. It resolves nothing; it only
                    // stops the tie being broken by noise.
                    if (residual < best * 0.5)
                    {
                        best = residual;
                        timing = candidate;
                        phase = turn;
                    }
                }
            }
        }

        /// <summary>Counts 0, 1, −1, 2, −2 … so that a search runs outwards from its centre.</summary>
        /// <param name="step">Which step of the search this is, counting from zero.</param>
        private static int Outward(int step) => ((step + 1) / 2) * ((step % 2) == 0 ? 1 : -1);

        /// <summary>
        /// How far a candidate alignment leaves the symbols from the constellation, per symbol.
        /// </summary>
        /// <remarks>
        /// Normalised by the signal's own power at those instants, so that the comparison is between
        /// alignments rather than between gains: an alignment that sampled the waveform where it is
        /// smaller would otherwise score better for having less of everything, error included.
        /// </remarks>
        private static double Residual(
            double[] result,
            double timing,
            double phase,
            int perSymbol,
            int count,
            double stagger,
            Constellation constellation)
        {
            var measured = new Iq[count];

            Project(result, measured, timing, 0.0, phase, 1.0, perSymbol, count, stagger);

            double power = 0.0;

            for (int symbol = 0; symbol < count; symbol++)
            {
                power += measured[symbol].MagnitudeSquared;
            }

            if (power < 1e-30)
            {
                return double.MaxValue;
            }

            double gain = Math.Sqrt(power / count);
            double error = 0.0;

            for (int symbol = 0; symbol < count; symbol++)
            {
                Iq scaled = measured[symbol] / gain;
                Iq ideal = constellation.Ideal(constellation.Decide(scaled, symbol), symbol);

                error += (scaled - ideal).MagnitudeSquared;
            }

            return error / count;
        }

        private static double InitialGain(
            double[] result, double timing, int perSymbol, int count, double stagger, double phase)
        {
            var measured = new Iq[count];

            Project(result, measured, timing, 0.0, phase, 1.0, perSymbol, count, stagger);

            double power = 0.0;

            for (int symbol = 0; symbol < count; symbol++)
            {
                power += measured[symbol].MagnitudeSquared;
            }

            double rms = Math.Sqrt(power / count);

            // The constellation is normalised to unit mean power, so the signal's own RMS at the
            // symbol instants is the gain to a first approximation. Starting the iteration from one
            // instead would make the first set of decisions those of a constellation the wrong size,
            // and on anything but QPSK the wrong size means the wrong ring.
            return rms < 1e-15 ? 1.0 : rms;
        }

        /// <summary>
        /// Reads the waveform at the symbol instants, correcting for carrier, phase and gain.
        /// </summary>
        /// <param name="result">The result window.</param>
        /// <param name="measured">Filled with one value per symbol.</param>
        /// <param name="timing">Where the first symbol's instant falls, in samples.</param>
        /// <param name="omega">The residual carrier, in radians per symbol.</param>
        /// <param name="phase">The carrier phase, in radians.</param>
        /// <param name="gain">The amplitude to divide out.</param>
        /// <param name="perSymbol">The internal processing rate.</param>
        /// <param name="count">How many symbols to read.</param>
        /// <param name="stagger">
        /// How far after I the Q axis is sampled, in samples: half a symbol for an offset format and
        /// zero for every other (<c>REQ-DEM-012</c>).
        /// </param>
        /// <remarks>
        /// <strong>The carrier comes out before the axes are split, not after.</strong> What arrives
        /// is the two staggered streams turned by the carrier's phase, so the I part of the received
        /// sample is a mixture of both axes until that phase is taken out. Each of the two instants
        /// is therefore corrected by the phase <em>at that instant</em> — which differ by half a
        /// symbol's worth of the residual carrier — and only then does one contribute its I and the
        /// other its Q.
        /// </remarks>
        private static void Project(
            double[] result,
            Iq[] measured,
            double timing,
            double omega,
            double phase,
            double gain,
            int perSymbol,
            int count,
            double stagger)
        {
            for (int symbol = 0; symbol < count; symbol++)
            {
                Iq value = Interpolator.At(result, timing + (symbol * perSymbol));
                Iq turn = Iq.FromPhase(-((omega * symbol) + phase));
                Iq inPhase = (value * turn) / gain;

                if (stagger == 0.0)
                {
                    measured[symbol] = inPhase;

                    continue;
                }

                Iq later = Interpolator.At(result, timing + (symbol * perSymbol) + stagger);
                Iq laterTurn = Iq.FromPhase(-((omega * (symbol + 0.5)) + phase));
                Iq quadrature = (later * laterTurn) / gain;

                measured[symbol] = new Iq(inPhase.I, quadrature.Q);
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

        /// <remarks>
        /// The two axes move with the timing separately for an offset format, so the slope is read
        /// at both instants and each axis is paired with its own error. The dot product below is
        /// then the same arithmetic in both cases — <c>corrected.I × error.I + corrected.Q ×
        /// error.Q</c> — because each component's derivative already belongs to the instant its
        /// error came from.
        /// </remarks>
        private static double FitTiming(
            double[] result,
            Iq[] measured,
            Iq[] decided,
            double timing,
            double omega,
            double phase,
            double gain,
            int perSymbol,
            int count,
            double stagger)
        {
            double projection = 0.0;
            double energy = 0.0;

            for (int symbol = 0; symbol < count; symbol++)
            {
                Iq slope = Interpolator.SlopeAt(result, timing + (symbol * perSymbol));
                Iq turn = Iq.FromPhase(-((omega * symbol) + phase));

                Iq corrected = (slope * turn) / gain;

                if (stagger != 0.0)
                {
                    Iq later = Interpolator.SlopeAt(
                        result, timing + (symbol * perSymbol) + stagger);

                    Iq laterTurn = Iq.FromPhase(-((omega * (symbol + 0.5)) + phase));

                    corrected = new Iq(corrected.I, ((later * laterTurn) / gain).Q);
                }

                Iq error = decided[symbol] - measured[symbol];

                Iq product = corrected.Conjugate() * error;

                projection += product.I;
                energy += corrected.MagnitudeSquared;
            }

            return energy < 1e-18 ? 0.0 : projection / energy;
        }

        private static double Clamp(
            double timing, int samples, int perSymbol, int count, double stagger)
        {
            double last = samples - 1 - ((count - 1) * perSymbol) - stagger;

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
