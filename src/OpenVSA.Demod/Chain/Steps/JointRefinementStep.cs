using System;
using System.Globalization;
using OpenVSA.Demod.Results;
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

            if (constellation.Family == ModulationFamily.Fsk)
            {
                return RefineFrequencyKeyed(context, result, perSymbol, count, samples);
            }

            if (constellation.Family == ModulationFamily.Vsb)
            {
                return RefineSingleSideband(context, result, perSymbol, count, samples);
            }

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
        /// <summary>
        /// Step 8 for a frequency-keyed format: discriminate, then fit deviation, offset and timing.
        /// </summary>
        /// <param name="context">The chain's state.</param>
        /// <param name="result">The result window.</param>
        /// <param name="perSymbol">The internal processing rate.</param>
        /// <param name="count">How many symbols the window holds.</param>
        /// <param name="samples">How long the window is.</param>
        /// <returns>Always <see cref="StepOutcome.Continue"/>.</returns>
        /// <remarks>
        /// <para>
        /// <strong>A different model, because a different thing carries the symbol.</strong> The
        /// linear path above fits a carrier frequency, a carrier phase, a gain and a timing offset,
        /// because those four are what stand between a phase-keyed signal and its constellation.
        /// For frequency keying the list is shorter and not the same: the symbol is the
        /// instantaneous FREQUENCY, so what stands between the signal and its levels is
        /// <strong>where zero is</strong> (a carrier offset, which shifts every level equally),
        /// <strong>how far a level is</strong> (the deviation, which scales them) and the timing.
        /// Carrier phase does not appear at all — a constant phase is invisible to a
        /// discriminator, which is why this family needs no phase estimate and cannot be given one.
        /// </para>
        /// <para>
        /// <strong>The discriminator is one line and the whole of the difference.</strong>
        /// <c>arg(w[n]·conj(w[n−1]))</c> is how far the phase turned between two samples, which is
        /// the frequency; scaled to cycles per symbol it is directly comparable with the level
        /// ladder <see cref="Constellation.Fsk"/> lays down. Everything after this step then works
        /// unchanged: the decisions are nearest-level on the real axis, the reference is those
        /// levels, and <c>REQ-DEM-071</c> already knows which metrics a frequency-keyed format has
        /// no meaning for.
        /// </para>
        /// <para>
        /// <strong>The deviation is the gain, and that is why it is a measurement.</strong> The
        /// constellation's levels are normalised, so the scale the fit recovers IS the signal's peak
        /// deviation as a fraction of the symbol rate — <c>REQ-DEM-070</c>'s FSK deviation, read off
        /// the signal rather than echoed back from a setting.
        /// </para>
        /// </remarks>
        private static StepOutcome RefineFrequencyKeyed(
            DemodContext context, double[] result, int perSymbol, int count, int samples)
        {
            DemodSettings settings = context.Settings;
            Constellation constellation = settings.Constellation;

            double[] frequency = Discriminate(result, samples, perSymbol);

            double timing = InitialFrequencyTiming(
                frequency, context.TimingSamples, perSymbol, samples, count);

            // The mean of the discriminated signal IS the carrier offset, because the level ladder
            // is symmetric about zero and a block of a few hundred symbols visits it evenly enough.
            // Starting from it rather than from nothing matters: the offset and the deviation are
            // fitted against decisions, and decisions taken with the levels displaced by half a
            // step are wrong in a way the fit will happily converge to and keep.
            double offset = Mean(frequency, timing, perSymbol, count);
            double gain = InitialDeviation(
                frequency, timing, offset, perSymbol, count, constellation);

            var measured = new Iq[count];
            var decided = new Iq[count];

            int iterations = 0;
            bool converged = false;
            double largest = double.MaxValue;

            for (int iteration = 1; iteration <= settings.MaxRefinementIterations; iteration++)
            {
                iterations = iteration;

                ProjectFrequency(frequency, measured, timing, offset, gain, perSymbol, count);

                for (int symbol = 0; symbol < count; symbol++)
                {
                    decided[symbol] = constellation.Ideal(
                        constellation.Decide(measured[symbol], symbol), symbol);
                }

                double difference = 0.0;
                double product = 0.0;
                double square = 0.0;

                for (int symbol = 0; symbol < count; symbol++)
                {
                    difference += measured[symbol].I - decided[symbol].I;
                    product += measured[symbol].I * decided[symbol].I;
                    square += decided[symbol].I * decided[symbol].I;
                }

                // Where zero is: the mean of what is left over once the levels are accounted for,
                // which is the carrier offset expressed in the discriminator's own units.
                double deltaOffset = count > 0 ? difference / count : 0.0;

                // How far a level is: the scale that best takes the decided ladder onto the
                // measured one, which is the same least-squares ratio the linear path fits.
                double gainRatio = square > 0.0 && product > 0.0 ? product / square : 1.0;

                double deltaTiming = FitFrequencyTiming(
                    frequency, measured, decided, timing, offset, gain, perSymbol, count);

                double limit = MaximumTimingStepSymbols * perSymbol;

                if (deltaTiming > limit)
                {
                    deltaTiming = limit;
                }
                else if (deltaTiming < -limit)
                {
                    deltaTiming = -limit;
                }

                offset += deltaOffset * gain;
                gain *= gainRatio;
                timing = Clamp(timing + deltaTiming, samples, perSymbol, count, 0.0);

                largest = Math.Max(
                    Math.Abs(deltaOffset),
                    Math.Max(Math.Abs(deltaTiming), Math.Abs(gainRatio - 1.0)));

                if (largest < settings.RefinementTolerance)
                {
                    converged = true;

                    break;
                }
            }

            ProjectFrequency(frequency, measured, timing, offset, gain, perSymbol, count);

            // The offset is in cycles per symbol, which is the discriminator's unit: a carrier that
            // is one symbol rate high turns the phase by a whole turn every symbol.
            double frequencyHz = offset * settings.SymbolRateHz;

            context.PassFrequencyHz = frequencyHz;
            context.PassPhaseRadians = 0.0;
            context.PassGain = gain;

            context.ResidualFrequencyHz += frequencyHz;
            context.Gain *= gain;
            context.TimingSamples = timing;
            context.MeasuredSymbols = measured;

            context.Convergence = new ConvergenceReport(
                iterations,
                settings.MaxRefinementIterations,
                converged,
                largest,
                settings.RefinementTolerance);

            if (!converged)
            {
                context.Note(
                    "Step 8 reached its bound of " +
                    settings.MaxRefinementIterations.ToString(CultureInfo.InvariantCulture) +
                    " iterations on pass " + context.Pass.ToString(CultureInfo.InvariantCulture) +
                    " without meeting the convergence criterion. The largest change on the last " +
                    "iteration was " + largest.ToString("G3", CultureInfo.InvariantCulture) +
                    ". The estimates are the ones it had got to, not the ones it was heading for.");
            }

            return StepOutcome.Continue;
        }

        /// <summary>
        /// Step 8 for a vestigial-sideband format: fit on the real axis, and call the offset a
        /// pilot.
        /// </summary>
        /// <param name="context">The chain's state.</param>
        /// <param name="result">The result window.</param>
        /// <param name="perSymbol">The internal processing rate.</param>
        /// <param name="count">How many symbols the window holds.</param>
        /// <param name="samples">How long the window is.</param>
        /// <returns>Always <see cref="StepOutcome.Continue"/>.</returns>
        /// <remarks>
        /// <para>
        /// <strong>Half the plane carries nothing, and reading it as error would report a fault on
        /// a perfect signal.</strong> A vestigial-sideband transmitter sends amplitude levels on one
        /// axis; what appears on the other is the Hilbert transform of the first, which is what
        /// suppressing a sideband produces and what makes the waveform analytic. It is not zero at
        /// a symbol instant and it is not an error — so the measured symbol here is the REAL PART
        /// and the imaginary part is dropped, deliberately and by name.
        /// </para>
        /// <para>
        /// <strong>The carrier phase still matters, and more than it does elsewhere.</strong>
        /// Rotating the waveform mixes the vestige into the axis that carries the data — at a phase
        /// error of θ what is read is <c>Re(s)cos θ + Im(s)sin θ</c> — so a phase error does not
        /// merely turn this constellation, it contaminates it. That is why the fit here estimates a
        /// phase, where the frequency-keyed path above cannot and need not.
        /// </para>
        /// <para>
        /// <strong>The offset is the pilot.</strong> Where a frequency-keyed format's DC term is a
        /// carrier error, a VSB signal's is the constant its transmitter adds to put a carrier at
        /// the band edge. Same arithmetic, different meaning, and <c>REQ-DEM-070</c> names the
        /// second one Pilot Lvl.
        /// </para>
        /// </remarks>
        private static StepOutcome RefineSingleSideband(
            DemodContext context, double[] result, int perSymbol, int count, int samples)
        {
            DemodSettings settings = context.Settings;
            Constellation constellation = settings.Constellation;

            double timing = InitialRealTiming(
                result, context.TimingSamples, perSymbol, samples, count);

            double omega = 0.0;
            double phase = 0.0;

            // 🔴 THE PILOT HAS TO BE TAKEN OUT BEFORE THE FIRST DECISION, NOT FITTED AFTER IT. A
            // pilot shifts the whole ladder, so decisions taken without it land a rung out -- and
            // then the mean of (measured - decided) is near zero, because the decisions have
            // absorbed the very thing that fit is meant to find. Measured with a pilot of 0.3 of a
            // level and no initial estimate: the offset converged to -0.10 where it should have
            // been +0.27, and the measurement read 11.2 %rms with 96 of 512 symbols.
            //
            // The mean of the raw levels is the pilot, because a data stream is balanced about zero
            // and a pilot is not. One line, before anything is decided.
            double offset = MeanReal(result, timing, perSymbol, count);
            double gain = InitialRealGain(result, timing, perSymbol, count);

            var measured = new Iq[count];
            var decided = new Iq[count];

            int iterations = 0;
            bool converged = false;
            double largest = double.MaxValue;

            for (int iteration = 1; iteration <= settings.MaxRefinementIterations; iteration++)
            {
                iterations = iteration;

                ProjectReal(
                    result, measured, timing, omega, phase, gain, offset, perSymbol, count);

                for (int symbol = 0; symbol < count; symbol++)
                {
                    decided[symbol] = constellation.Ideal(
                        constellation.Decide(measured[symbol], symbol), symbol);
                }

                double difference = 0.0;
                double product = 0.0;
                double square = 0.0;

                // The two-parameter fit for the turn: an error at symbol k is the slope times
                // (dphase + domega.k), so the normal equations are the usual weighted sums of 1, k
                // and k squared. One line of algebra more than the phase alone, and it is what lets
                // a carrier offset be corrected rather than merely noticed.
                double weightOne = 0.0;
                double weightIndex = 0.0;
                double weightIndexSquared = 0.0;
                double weightError = 0.0;
                double weightIndexError = 0.0;

                for (int symbol = 0; symbol < count; symbol++)
                {
                    difference += measured[symbol].I - decided[symbol].I;
                    product += measured[symbol].I * decided[symbol].I;
                    square += decided[symbol].I * decided[symbol].I;

                    // How the real part moves when the whole waveform turns. With u = v.e^-jθ,
                    // du/dθ = -j.u, so d(Re u)/dθ = Re(-j.u) = Im(u) -- the imaginary part IS the
                    // derivative of the real one, which is exactly why a phase error mixes the
                    // vestige into the axis that carries the data.
                    Iq turned = Turned(result, timing, omega, phase, gain, perSymbol, symbol);
                    double slope = turned.Q;
                    double error = decided[symbol].I - measured[symbol].I;

                    weightOne += slope * slope;
                    weightIndex += slope * slope * symbol;
                    weightIndexSquared += slope * slope * symbol * symbol;
                    weightError += slope * error;
                    weightIndexError += slope * error * symbol;
                }

                double deltaOffset = count > 0 ? difference / count : 0.0;
                double gainRatio = square > 0.0 && product > 0.0 ? product / square : 1.0;

                double determinant =
                    (weightOne * weightIndexSquared) - (weightIndex * weightIndex);

                double deltaPhase = 0.0;
                double deltaOmega = 0.0;

                if (Math.Abs(determinant) > 1e-30)
                {
                    deltaPhase =
                        ((weightError * weightIndexSquared) - (weightIndexError * weightIndex)) /
                        determinant;

                    deltaOmega =
                        ((weightOne * weightIndexError) - (weightIndex * weightError)) /
                        determinant;
                }

                double deltaTiming = FitRealTiming(
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

                offset += deltaOffset * gain;
                gain *= gainRatio;
                phase += deltaPhase;
                omega += deltaOmega;
                timing = Clamp(timing + deltaTiming, samples, perSymbol, count, 0.0);

                largest = Math.Max(
                    Math.Abs(deltaOmega) / (2.0 * Math.PI),
                    Math.Max(
                        Math.Abs(deltaOffset),
                        Math.Max(
                            Math.Abs(deltaPhase),
                            Math.Max(Math.Abs(deltaTiming), Math.Abs(gainRatio - 1.0)))));

                if (largest < settings.RefinementTolerance)
                {
                    converged = true;

                    break;
                }
            }

            ProjectReal(result, measured, timing, omega, phase, gain, offset, perSymbol, count);

            double frequencyHz = omega * settings.SymbolRateHz / (2.0 * Math.PI);

            context.PassFrequencyHz = frequencyHz;
            context.PassPhaseRadians = phase;
            context.PassGain = gain;

            context.ResidualFrequencyHz += frequencyHz;
            context.PhaseRadians += phase;
            context.Gain *= gain;
            context.TimingSamples = timing;
            context.MeasuredSymbols = measured;
            context.PilotLevel = offset / (gain == 0.0 ? 1.0 : gain);

            context.Convergence = new ConvergenceReport(
                iterations,
                settings.MaxRefinementIterations,
                converged,
                largest,
                settings.RefinementTolerance);

            if (!converged)
            {
                context.Note(
                    "Step 8 reached its bound of " +
                    settings.MaxRefinementIterations.ToString(CultureInfo.InvariantCulture) +
                    " iterations on pass " + context.Pass.ToString(CultureInfo.InvariantCulture) +
                    " without meeting the convergence criterion. The largest change on the last " +
                    "iteration was " + largest.ToString("G3", CultureInfo.InvariantCulture) +
                    ". The estimates are the ones it had got to, not the ones it was heading for.");
            }

            return StepOutcome.Continue;
        }

        /// <summary>Where the instants are, judged on the axis that carries the symbols.</summary>
        /// <remarks>
        /// 🔴 <strong><see cref="InitialTiming"/> reads its symbol-rate line out of the COMPLEX
        /// envelope, and half of a vestigial-sideband signal's envelope is the vestige.</strong> The
        /// vestige is the Hilbert transform of the data, so its own peaks do not fall where the
        /// data's do — the line is still there and its PHASE is somebody else's, which puts the
        /// first decisions at instants that are nobody's. The same shape of mistake the
        /// frequency-keyed path found in the same estimator, for a different reason: there the
        /// envelope carried no line at all, here it carries the wrong one.
        /// </remarks>
        private static double InitialRealTiming(
            double[] result, double nominal, int perSymbol, int samples, int count)
        {
            double real = 0.0;
            double imaginary = 0.0;

            for (int sample = 0; sample < samples; sample++)
            {
                double value = Iq.At(result, sample).I;
                double power = value * value;
                double angle = -2.0 * Math.PI * sample / perSymbol;

                real += power * Math.Cos(angle);
                imaginary += power * Math.Sin(angle);
            }

            if ((real * real) + (imaginary * imaginary) < 1e-30)
            {
                return nominal;
            }

            double estimate = -Math.Atan2(imaginary, real) * perSymbol / (2.0 * Math.PI);
            double shift = estimate - nominal;

            shift -= perSymbol * Math.Round(shift / perSymbol);

            return Clamp(nominal + shift, samples, perSymbol, count, 0.0);
        }

        /// <summary>The mean of the real axis at the decision instants.</summary>
        /// <remarks>
        /// For a balanced data stream the levels average to nothing and whatever is left is the
        /// pilot, so this is the pilot before any symbol has been decided. It is also why the
        /// estimate improves with the block length rather than with the iteration count.
        /// </remarks>
        private static double MeanReal(
            double[] result, double timing, int perSymbol, int count)
        {
            if (count < 1)
            {
                return 0.0;
            }

            double total = 0.0;

            for (int symbol = 0; symbol < count; symbol++)
            {
                total += Interpolator.At(result, timing + (symbol * perSymbol)).I;
            }

            return total / count;
        }

        /// <summary>The gain to start from, measured on the axis that carries the symbols.</summary>
        /// <remarks>
        /// 🔴 <strong><see cref="InitialGain"/> measures the whole complex magnitude, and half of a
        /// vestigial-sideband signal's magnitude is the sideband vestige.</strong> Measured on a
        /// generated 8VSB signal, the two axes carry almost the same power — 0.90 against 0.89 rms —
        /// so the complex estimate comes out about √2 too large, the levels are read about seven
        /// tenths of their true size, and on an eight-level ladder the first decisions land a rung
        /// or two out. The iteration then has a wrong ladder to converge on, and it converges: 13.2
        /// %rms with 105 of 512 symbols right, which looks like a poor signal and is a first guess
        /// taken on the wrong axis.
        /// </remarks>
        private static double InitialRealGain(
            double[] result, double timing, int perSymbol, int count)
        {
            double pilot = MeanReal(result, timing, perSymbol, count);
            double power = 0.0;

            for (int symbol = 0; symbol < count; symbol++)
            {
                // About the pilot rather than about zero: a pilot counted as level would make the
                // ladder look bigger than it is, which is the same mistake in the other direction.
                double real = Interpolator.At(result, timing + (symbol * perSymbol)).I - pilot;

                power += real * real;
            }

            double rms = count > 0 ? Math.Sqrt(power / count) : 0.0;

            return rms < 1e-15 ? 1.0 : rms;
        }

        /// <summary>The waveform at a symbol instant, with the current corrections applied.</summary>
        private static Iq Turned(
            double[] result,
            double timing,
            double omega,
            double phase,
            double gain,
            int perSymbol,
            int symbol)
        {
            Iq value = Interpolator.At(result, timing + (symbol * perSymbol));
            Iq turn = Iq.FromPhase(-((omega * symbol) + phase));

            return (value * turn) / gain;
        }

        /// <summary>Reads the real axis at the decision instants, less the pilot.</summary>
        private static void ProjectReal(
            double[] result,
            Iq[] measured,
            double timing,
            double omega,
            double phase,
            double gain,
            double offset,
            int perSymbol,
            int count)
        {
            for (int symbol = 0; symbol < count; symbol++)
            {
                Iq turned = Turned(result, timing, omega, phase, gain, perSymbol, symbol);

                // The imaginary part is the sideband vestige and is dropped here, which is the one
                // place in this chain where half a measurement is thrown away on purpose.
                measured[symbol] = new Iq(turned.I - (offset / (gain == 0.0 ? 1.0 : gain)), 0.0);
            }
        }

        /// <summary>How far the instants should move, judged on the real axis alone.</summary>
        private static double FitRealTiming(
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
            double numerator = 0.0;
            double denominator = 0.0;

            for (int symbol = 0; symbol < count; symbol++)
            {
                Iq slope = Interpolator.SlopeAt(result, timing + (symbol * perSymbol));
                Iq turn = Iq.FromPhase(-((omega * symbol) + phase));
                double real = ((slope * turn) / gain).I;
                double error = decided[symbol].I - measured[symbol].I;

                numerator += real * error;
                denominator += real * real;
            }

            return denominator > 0.0 ? numerator / denominator : 0.0;
        }

        /// <summary>
        /// Where the symbol instants are, for a signal whose envelope says nothing.
        /// </summary>
        /// <param name="frequency">The discriminated signal.</param>
        /// <param name="nominal">Where step 7 put the first symbol.</param>
        /// <param name="perSymbol">The internal processing rate.</param>
        /// <param name="samples">How long the window is.</param>
        /// <param name="count">How many symbols it holds.</param>
        /// <returns>The first symbol instant, in samples.</returns>
        /// <remarks>
        /// <para>
        /// 🔴 <strong><see cref="InitialTiming"/> reads a symbol-rate line out of the envelope, and
        /// a frequency-keyed signal has no envelope.</strong> That estimator is the right one for a
        /// pulse-shaped format, whose amplitude dips between symbols and so carries a line at the
        /// symbol rate; a constant-envelope one has <c>|w|²</c> flat to the noise, and what comes
        /// back is the angle of whatever numerical residue is left. Measured before this existed:
        /// 2FSK at a deviation of a quarter the symbol rate demodulated perfectly and everything
        /// wider failed — 4FSK at half returned 260 of 512 symbols, 8FSK 171, 16FSK 89, each with
        /// a large spurious carrier offset, because the fit converged on decisions taken at
        /// instants that were nobody's.
        /// </para>
        /// <para>
        /// <strong>What does carry the line is the discriminated signal's own movement.</strong> An
        /// FSK signal holds a level across a symbol and changes between symbols, so the energy of
        /// the CHANGE peaks at the boundaries — one peak a symbol, which is a line at the symbol
        /// rate whose phase says where the boundaries are. The instants are half a symbol from
        /// them.
        /// </para>
        /// </remarks>
        private static double InitialFrequencyTiming(
            double[] frequency, double nominal, int perSymbol, int samples, int count)
        {
            double real = 0.0;
            double imaginary = 0.0;

            for (int sample = 1; sample < samples; sample++)
            {
                double change = Iq.At(frequency, sample).I - Iq.At(frequency, sample - 1).I;
                double energy = change * change;
                double angle = -2.0 * Math.PI * sample / perSymbol;

                real += energy * Math.Cos(angle);
                imaginary += energy * Math.Sin(angle);
            }

            if ((real * real) + (imaginary * imaginary) < 1e-30)
            {
                return nominal;
            }

            // The angle locates the boundaries; the instants are half a symbol later.
            double estimate =
                (-Math.Atan2(imaginary, real) * perSymbol / (2.0 * Math.PI)) + (perSymbol / 2.0);

            double shift = estimate - nominal;

            shift -= perSymbol * Math.Round(shift / perSymbol);

            return Clamp(nominal + shift, samples, perSymbol, count, 0.0);
        }

        /// <summary>The mean of the discriminated signal at the decision instants.</summary>
        private static double Mean(
            double[] frequency, double timing, int perSymbol, int count)
        {
            if (count < 1)
            {
                return 0.0;
            }

            double total = 0.0;

            for (int symbol = 0; symbol < count; symbol++)
            {
                total += Interpolator.At(frequency, timing + (symbol * perSymbol)).I;
            }

            return total / count;
        }

        /// <summary>
        /// The waveform's instantaneous frequency, in cycles per symbol, as a real signal.
        /// </summary>
        /// <param name="result">The result window.</param>
        /// <param name="samples">How long it is.</param>
        /// <param name="perSymbol">The internal processing rate.</param>
        /// <returns>One value per sample, carried in the real part.</returns>
        /// <remarks>
        /// Carried as a complex array with nothing in the imaginary part so that the same
        /// interpolator reads it at fractional instants as reads the waveform everywhere else — a
        /// symbol instant is no more likely to fall on a sample here than it is anywhere else in the
        /// chain. The first sample has no predecessor to have turned from, so it repeats the second.
        /// </remarks>
        private static double[] Discriminate(double[] result, int samples, int perSymbol)
        {
            var frequency = new double[2 * samples];

            for (int sample = 1; sample < samples; sample++)
            {
                Iq turn = Iq.At(result, sample) * Iq.At(result, sample - 1).Conjugate();
                double radians = Math.Atan2(turn.Q, turn.I);

                Iq.Set(
                    frequency,
                    sample,
                    new Iq(radians * perSymbol / (2.0 * Math.PI), 0.0));
            }

            if (samples > 1)
            {
                Iq.Set(frequency, 0, Iq.At(frequency, 1));
            }

            return frequency;
        }

        /// <summary>The deviation to start from: the measured spread against the level ladder's.</summary>
        private static double InitialDeviation(
            double[] frequency,
            double timing,
            double offset,
            int perSymbol,
            int count,
            Constellation constellation)
        {
            double measured = 0.0;

            for (int symbol = 0; symbol < count; symbol++)
            {
                // About the offset, not about zero: a carrier error would otherwise be counted as
                // deviation, and the two would be estimated as one number.
                double value =
                    Interpolator.At(frequency, timing + (symbol * perSymbol)).I - offset;

                measured += value * value;
            }

            measured = count > 0 ? Math.Sqrt(measured / count) : 0.0;

            double ideal = 0.0;

            for (int point = 0; point < constellation.Count; point++)
            {
                ideal += constellation.Ideal(point).I * constellation.Ideal(point).I;
            }

            ideal = constellation.Count > 0 ? Math.Sqrt(ideal / constellation.Count) : 1.0;

            return ideal > 0.0 && measured > 0.0 ? measured / ideal : 1.0;
        }

        /// <summary>Reads the discriminated signal at the decision instants.</summary>
        private static void ProjectFrequency(
            double[] frequency,
            Iq[] measured,
            double timing,
            double offset,
            double gain,
            int perSymbol,
            int count)
        {
            for (int symbol = 0; symbol < count; symbol++)
            {
                double value = Interpolator.At(frequency, timing + (symbol * perSymbol)).I;

                measured[symbol] = new Iq((value - offset) / gain, 0.0);
            }
        }

        /// <summary>How far the decision instants should move, for a frequency-keyed format.</summary>
        /// <remarks>
        /// The same least-squares step the linear path takes, on the discriminated signal: how far
        /// the instants must move for the slope to close the gap between what was measured and what
        /// was decided.
        /// </remarks>
        private static double FitFrequencyTiming(
            double[] frequency,
            Iq[] measured,
            Iq[] decided,
            double timing,
            double offset,
            double gain,
            int perSymbol,
            int count)
        {
            double numerator = 0.0;
            double denominator = 0.0;

            for (int symbol = 0; symbol < count; symbol++)
            {
                double slope =
                    Interpolator.SlopeAt(frequency, timing + (symbol * perSymbol)).I / gain;
                double error = decided[symbol].I - measured[symbol].I;

                numerator += slope * error;
                denominator += slope * slope;
            }

            return denominator > 0.0 ? numerator / denominator : 0.0;
        }

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
