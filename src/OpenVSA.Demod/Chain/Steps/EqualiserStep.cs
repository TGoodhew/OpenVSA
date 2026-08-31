using System;
using System.Globalization;
using OpenVSA.Demod.Results;
using OpenVSA.Demod.Signal;

namespace OpenVSA.Demod.Chain.Steps
{
    /// <summary>
    /// Step 11: fit an equaliser to the channel and take it out (<c>REQ-DEM-050</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The exact regularised least-squares solution, in one shot.</strong>
    /// <c>REQ-DEM-052</c> makes this a design choice with a stated rationale: the chain already
    /// processes whole blocks non-causally and step 10 has already regenerated the full reference
    /// sequence, so <c>w = (X^H X + lambda I)^-1 X^H d</c> is available directly. It is optimal,
    /// deterministic, has no convergence dependence and needs no step size; with the reference in
    /// hand an iterative gradient method would be strictly worse.
    /// </para>
    /// <para>
    /// <strong>One filter, re-estimated — not a product of corrections.</strong> Each pass fits from
    /// the waveform as it ARRIVED, saved once in
    /// <see cref="DemodContext.EqualiserSource"/>, against whatever reference the current decisions
    /// support. It does not fit a correction on top of the last pass's output.
    /// </para>
    /// <para>
    /// 🔴 That is the whole of <c>#432</c>, and it is worth stating what the alternative did. An
    /// earlier form replaced the result window with its own output and let the next pass fit again
    /// on that, so the total equaliser was <c>w1 · w2 · w3 …</c>, each factor fitted under a
    /// different assumption about what the signal was. Every fit carries a bias out of the
    /// directions the data does not constrain, and composing them multiplies the biases: measured on
    /// a 6 dB tilt, EVM went 0.409 %rms after two passes, 0.918 after ten and 1.493 after thirty.
    /// It did not converge on a wrong answer — it walked away from the right one, and only the
    /// re-entry threshold stopping early made it look like a floor. A channel is one filter, so the
    /// equaliser is fitted as one filter.
    /// </para>
    /// <para>
    /// <strong>T/2 spacing, and why it is not merely a convention.</strong> The taps are spaced half
    /// a symbol apart, which puts the fit's Nyquist frequency at the symbol rate. A root raised
    /// cosine at alpha 0.35 then fills about two thirds of the band the fit spans. At the internal
    /// rate — T/4 by default — the band is twice as wide and the signal fills a third of it, so two
    /// thirds of the solution's degrees of freedom sit where there is no signal to constrain them at
    /// all. That null space is where the bias above comes from, and halving the tap spacing halves
    /// it. <c>REQ-DEM-052</c> asks for T/2 spacing and for the tap count to be stated as
    /// <em>2N taps for an N-symbol filter</em>, which is what <see cref="DemodSettings.EqualiserTaps"/>
    /// computes.
    /// </para>
    /// <para>
    /// <strong>A pass that does not help is not taken.</strong> The fit residual is measured against
    /// the residual the last accepted coefficients left, and coefficients that do not improve it are
    /// discarded — the waveform and the taps stay as they were. That makes a divergence structurally
    /// impossible rather than merely unlikely, and it is the reason the number of passes can no
    /// longer change the answer for the worse.
    /// </para>
    /// <para>
    /// <strong>Applied with full support.</strong> The source carries
    /// <see cref="DemodContext.EqualiserPad"/> samples of the working waveform either side of the
    /// result window, so every output sample is convolved against real signal. Filtering the window
    /// alone would zero-pad its ends and corrupt the symbols there — measured before this changed,
    /// EVM peaked on the very last symbol of the window and the outer symbols carried nearly seven
    /// times the error energy of the interior.
    /// </para>
    /// <para>
    /// <strong>Run, Hold and Reset are about measurements, not passes.</strong>
    /// <c>REQ-DEM-051</c>'s three modes govern what happens to the coefficients between one
    /// measurement and the next, and <see cref="EqualiserState"/> is what carries them across.
    /// <see cref="EqualiserMode.Run"/> fits as described above and the chain keeps the result;
    /// <see cref="EqualiserMode.Hold"/> applies what is held and fits nothing, so successive
    /// measurements are equalised by bit-identical taps; <see cref="EqualiserMode.Reset"/> applies
    /// a unit impulse, which is an equaliser that leaves the waveform alone.
    /// </para>
    /// <para>
    /// What Run inherits is a standard rather than a starting point. There is no starting point to
    /// inherit — the least-squares solution is computed in one shot — so the held coefficients are
    /// used as the residual the new fit has to beat, exactly as a previous pass's are. A block that
    /// cannot fit better than the filter it was handed keeps that filter, which is what stops one
    /// bad measurement from undoing a good one; and because each measurement still fits its own
    /// filter from its own samples, the coefficients change from measurement to measurement, which
    /// is what the requirement asks Run to be observable by.
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
        /// <strong>Without this the fit is not merely inaccurate, it is unstable.</strong> Even at
        /// T/2 spacing the signal does not fill the band the taps span, so the autocorrelation
        /// matrix is near-singular in the directions outside it and a least-squares solution is free
        /// to put anything it likes there at no cost to the fit. Measured on this chain's own
        /// intersymbol-interference test before any of this was understood, EVM went 10 %, then
        /// 2 %, then 22 % over three passes.
        /// </para>
        /// <para>
        /// A thousandth of the average diagonal is enough to make the null space cost something
        /// without measurably biasing the part of the solution the data does determine. It is a
        /// backstop rather than the cure: the cure is fitting one filter from the original waveform
        /// each pass and refusing a pass that does not help, and with those in place the loading's
        /// exact value stops mattering — a thousandfold change in it moves the result by less than
        /// the impairments being corrected.
        /// </para>
        /// </remarks>
        private const double DiagonalLoading = 1e-6;

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

            if (settings.Constellation.Family == ModulationFamily.Fsk)
            {
                // The equaliser fits a linear filter that takes the measured waveform onto the
                // ideal SYMBOLS. For a frequency-keyed format those symbols are levels of
                // instantaneous frequency, and no linear filter on the waveform maps onto them --
                // the relationship between the two is the discriminator, which is not linear. A
                // filter fitted between them would be fitted between quantities of different kinds
                // and would report a channel response for a channel nobody has.
                context.Note(
                    "The equaliser did not run: " + settings.Constellation.Name +
                    " carries its symbols as frequency, and an equaliser fits a linear filter " +
                    "between a waveform and a constellation. Those are not linearly related for a " +
                    "frequency-keyed format, so there is nothing for it to fit.");

                return StepOutcome.Continue;
            }

            int perSymbol = settings.PointsPerSymbol;
            int samples = Iq.Count(result);
            int taps = settings.EqualiserTaps;

            // REQ-DEM-051's impulse position: the centre while the filter is short, and a fixed
            // pre-cursor reach once it is long, so further length is spent on delay spread. The fit
            // reads it as the reference delay -- which tap of the filter the decision instant lines
            // up with -- and it is the index the unit impulse of Reset sits at.
            int half = settings.EqualiserImpulseIndex;

            // Half a symbol, which is T/2 spacing at whatever internal rate the chain is running.
            int stride = Math.Max(1, perSymbol / 2);
            int pad = half * stride;

            if (context.EqualiserSource == null)
            {
                context.EqualiserSource = Extended(context, result, samples, pad);
                context.EqualiserPad = pad;
            }

            double[] source = Derotate(
                context, context.EqualiserSource, samples + (2 * pad), pad, perSymbol);

            // Hold and Reset do not fit, so they are answered before anything that needs symbols:
            // a measurement too poor to decide symbols still has a held filter applied to it, which
            // is the whole point of holding one.
            if (settings.EqualiserMode != EqualiserMode.Run)
            {
                return Frozen(context, source, samples, taps, half, stride, pad);
            }

            // EVERY symbol in the window, with no guard. The fit's targets are the decided ideal
            // POINTS rather than a regenerated waveform, so there is no stretch of the window where
            // the target is unreliable -- and the source is padded, so there is no stretch where the
            // filter has nothing to convolve against either. The guard the waveform-domain fit
            // needed was the reason EVM peaked on the last symbol of the window.
            Iq[] symbols = context.IdealSymbols;

            if (symbols == null || symbols.Length < taps * 2)
            {
                context.Note(
                    "The equaliser had " +
                    (symbols == null ? 0 : symbols.Length).ToString(CultureInfo.InvariantCulture) +
                    " symbols to fit " + taps.ToString(CultureInfo.InvariantCulture) +
                    " taps over, which is not enough to fit anything. It did nothing.");

                return StepOutcome.Continue;
            }

            // What the last accepted coefficients left, or -- on the first pass -- what the filter
            // this measurement inherited leaves, which in Run mode is the previous measurement's
            // and is doing nothing when there is none. Either way the new coefficients have to beat
            // something real: an equaliser that cannot improve on what it was handed should not
            // replace it.
            Iq[] inherited = context.Pass == 1 && settings.EqualiserState != null
                ? settings.EqualiserState.Held(taps)
                : null;

            Iq[] regressors = Regressors(
                source, symbols.Length, context.TimingSamples, perSymbol, taps, half, stride, pad);

            double baseline = double.IsNaN(context.EqualiserResidual)
                ? Residual(regressors, symbols, inherited, taps, half)
                : context.EqualiserResidual;

            Iq[] coefficients = settings.EqualiserAlgorithm == EqualiserAlgorithm.LeastSquares
                ? LeastSquares(regressors, symbols, taps)
                : GradientEqualiser.Adapt(
                    context, regressors, Start(context, inherited, taps, half), taps,
                    symbols.Length);

            if (coefficients == null)
            {
                // A gradient mode that refused its step size has already said so, in the terms the
                // user can act on. Only the one-shot solution needs a word here.
                if (settings.EqualiserAlgorithm == EqualiserAlgorithm.LeastSquares)
                {
                    context.Note(
                        "The equaliser's normal equations were singular, so no coefficients " +
                        "could be fitted. The waveform was left as it was.");
                }

                return Kept(context, source, samples, inherited, baseline, half, stride, pad);
            }

            double[] equalised = Apply(source, samples, coefficients, half, stride, pad);

            double residual = Residual(regressors, symbols, coefficients, taps, half);

            if (!(residual < baseline * (1.0 - settings.EqualiserUpdateThreshold)))
            {
                if (residual > baseline)
                {
                    context.Note(
                        "The equaliser fitted coefficients that would have made the fit worse -- " +
                        "residual " + residual.ToString("G4", CultureInfo.InvariantCulture) +
                        " against " + baseline.ToString("G4", CultureInfo.InvariantCulture) +
                        " -- so they were discarded and the previous ones kept. More passes cannot " +
                        "make this measurement worse.");
                }

                return Kept(context, source, samples, inherited, baseline, half, stride, pad);
            }

            context.Result = equalised;
            context.EqualiserCoefficients = coefficients;
            context.EqualiserResidual = residual;

            Absorbed(context);

            context.EqualiserUpdated = true;

            return StepOutcome.ReEnter;
        }

        /// <summary>
        /// Applies the coefficients a non-adapting mode calls for, and fits nothing.
        /// </summary>
        /// <param name="context">The chain's state.</param>
        /// <param name="source">The padded, derotated source.</param>
        /// <param name="samples">How long the result window is.</param>
        /// <param name="taps">How many taps.</param>
        /// <param name="half">Which tap the impulse sits on.</param>
        /// <param name="stride">How many samples apart the taps are.</param>
        /// <param name="pad">Where the result window starts in <paramref name="source"/>.</param>
        /// <returns>One re-entry when a held filter was applied, so that the symbols the metrics
        /// are computed from are read off the equalised waveform; otherwise
        /// <see cref="StepOutcome.Continue"/>.</returns>
        /// <remarks>
        /// <para>
        /// <strong>Hold applies, it does not abstain.</strong> Held coefficients are put on every
        /// measurement — that is what makes Hold useful on a signal whose channel is known and whose
        /// individual blocks are too poor to fit from. Only
        /// <see cref="DemodSettings.EqualiserEnabled"/> switches the equaliser off.
        /// </para>
        /// <para>
        /// <strong>Reset is a unit impulse, and so is a Hold with nothing held.</strong> Both
        /// produce a filter that returns the waveform unchanged, which is the honest thing for an
        /// equaliser that has never adapted; the waveform still goes through the convolution rather
        /// than around it, so the two paths cannot differ by so much as a rounding.
        /// </para>
        /// <para>
        /// 🔴 <strong>Applying a filter is not enough on its own.</strong> Steps 12 to 14 read the
        /// symbols step 9 decided, not the waveform this step writes, so a filter applied here
        /// reaches the reported numbers only by way of the re-entry that has steps 8 to 10 read the
        /// equalised waveform again. Measured before this was understood, Hold applied the right
        /// coefficients and reported EVM to the last decimal place of the uncorrected measurement —
        /// which looks exactly like an equaliser that is not applying what it holds, and is not.
        /// </para>
        /// <para>
        /// One re-entry and no more. The filter is fitted from the untouched original either way, so
        /// a second application produces the same waveform and the same numbers at the cost of the
        /// pass it took; and a mode that cannot move its coefficients has nothing for a further pass
        /// to converge towards. A unit impulse asks for no re-entry at all, because a waveform it
        /// has not changed cannot change the symbols read from it.
        /// </para>
        /// </remarks>
        private static StepOutcome Frozen(
            DemodContext context,
            double[] source,
            int samples,
            int taps,
            int half,
            int stride,
            int pad)
        {
            DemodSettings settings = context.Settings;

            Iq[] held = settings.EqualiserMode == EqualiserMode.Hold &&
                settings.EqualiserState != null
                    ? settings.EqualiserState.Held(taps)
                    : null;

            Iq[] coefficients = held;

            if (coefficients == null)
            {
                coefficients = Impulse(taps, half);

                if (settings.EqualiserMode == EqualiserMode.Hold)
                {
                    context.Note(
                        "The equaliser is on Hold with no coefficients of the current length to " +
                        "hold, so it applied a unit impulse and left the waveform alone. Run it " +
                        "once at this filter length to give Hold something to freeze.");
                }
            }

            context.Result = Apply(source, samples, coefficients, half, stride, pad);
            context.EqualiserCoefficients = coefficients;

            Absorbed(context);

            context.EqualiserUpdated = false;

            return held != null && context.Pass == 1
                ? StepOutcome.ReEnter
                : StepOutcome.Continue;
        }

        /// <summary>
        /// Equalises with the filter this measurement inherited, when its own fit was not taken.
        /// </summary>
        /// <param name="context">The chain's state.</param>
        /// <param name="source">The padded, derotated source.</param>
        /// <param name="samples">How long the result window is.</param>
        /// <param name="inherited">The inherited filter, or <c>null</c> when there is none.</param>
        /// <param name="baseline">The residual that filter leaves.</param>
        /// <param name="half">Which tap the impulse sits on.</param>
        /// <param name="stride">How many samples apart the taps are.</param>
        /// <param name="pad">Where the result window starts in <paramref name="source"/>.</param>
        /// <returns>One re-entry when an inherited filter was applied, so the metrics are computed
        /// from symbols read off the equalised waveform; otherwise
        /// <see cref="StepOutcome.Continue"/>.</returns>
        /// <remarks>
        /// Reached two ways: a fit that could not be computed at all, and a fit that came out worse
        /// than the filter already in force. Both mean the same thing — this measurement has nothing
        /// better to offer than what it was handed — and in neither case should Run fall back to no
        /// equaliser at all on a block whose own fit happened to be poor.
        /// </remarks>
        private static StepOutcome Kept(
            DemodContext context,
            double[] source,
            int samples,
            Iq[] inherited,
            double baseline,
            int half,
            int stride,
            int pad)
        {
            context.EqualiserUpdated = false;

            if (inherited == null)
            {
                return StepOutcome.Continue;
            }

            context.Result = Apply(source, samples, inherited, half, stride, pad);
            context.EqualiserCoefficients = inherited;
            context.EqualiserResidual = baseline;

            Absorbed(context);

            return StepOutcome.ReEnter;
        }

        /// <summary>Where a gradient mode starts adapting from.</summary>
        /// <param name="context">The chain's state.</param>
        /// <param name="inherited">The filter this measurement inherited, or <c>null</c>.</param>
        /// <param name="taps">How many taps.</param>
        /// <param name="half">Which tap the impulse sits on.</param>
        /// <returns>The starting coefficients.</returns>
        /// <remarks>
        /// The best filter already in hand: this pass's predecessor within the measurement, else the
        /// one carried in from the last measurement, else a unit impulse. An incremental method that
        /// restarted from an impulse each pass would spend every pass covering the ground the last
        /// one covered, which is the one thing an incremental method is supposed to avoid.
        /// </remarks>
        private static Iq[] Start(DemodContext context, Iq[] inherited, int taps, int half)
        {
            Iq[] running = context.EqualiserCoefficients;

            if (running != null && running.Length == taps)
            {
                return running;
            }

            return inherited ?? Impulse(taps, half);
        }

        /// <summary>A filter that does nothing: one at the impulse index, zero everywhere else.</summary>
        /// <param name="taps">How many taps.</param>
        /// <param name="half">Which tap the impulse sits on.</param>
        /// <returns>The coefficients.</returns>
        internal static Iq[] Impulse(int taps, int half)
        {
            var coefficients = new Iq[taps];

            for (int tap = 0; tap < taps; tap++)
            {
                coefficients[tap] = tap == half ? new Iq(1.0, 0.0) : Iq.Zero;
            }

            return coefficients;
        }

        /// <summary>
        /// Records that the chain's estimates are now in the waveform rather than pending on it.
        /// </summary>
        /// <remarks>
        /// Everything step 8 estimated has been taken out by <see cref="Derotate"/>, so it is part
        /// of the waveform the later steps read rather than a correction still waiting to be
        /// applied to it. The derotation takes out the accumulated total, not this pass's share,
        /// because the source it is applied to is the untouched original.
        /// </remarks>
        private static void Absorbed(DemodContext context)
        {
            context.PassFrequencyHz = 0.0;
            context.PassPhaseRadians = 0.0;
            context.PassGain = 1.0;
        }

        /// <summary>
        /// The result window with the working waveform either side of it.
        /// </summary>
        /// <param name="context">The chain's state.</param>
        /// <param name="result">The result window, as a fallback.</param>
        /// <param name="samples">How long the result window is.</param>
        /// <param name="pad">How many samples of context to take either side.</param>
        /// <returns>A window of <c>samples + 2 * pad</c>, the result window starting at
        /// <paramref name="pad"/>.</returns>
        /// <remarks>
        /// The result window is a straight slice of the working waveform, so the samples either side
        /// of it are simply the ones step 7 did not take. Where the working waveform does not reach
        /// — a result window against the end of a short acquisition — the padding is zeros, which is
        /// what filtering the window alone would have given everywhere.
        /// </remarks>
        private static double[] Extended(
            DemodContext context, double[] result, int samples, int pad)
        {
            var extended = new double[2 * (samples + (2 * pad))];

            double[] working = context.Working;
            int available = working == null ? 0 : Iq.Count(working);
            int start = context.ResultStartSample;

            for (int sample = 0; sample < samples + (2 * pad); sample++)
            {
                int at = start + sample - pad;

                if (working != null && at >= 0 && at < available)
                {
                    Iq.Set(extended, sample, Iq.At(working, at));
                }
                else
                {
                    int inside = sample - pad;

                    if (inside >= 0 && inside < samples)
                    {
                        Iq.Set(extended, sample, Iq.At(result, inside));
                    }
                }
            }

            return extended;
        }

        /// <summary>
        /// Takes the carrier, phase and gain the chain has estimated out of the source.
        /// </summary>
        /// <remarks>
        /// <strong>The accumulated totals, not this pass's share.</strong> The source is the
        /// waveform as it arrived, so what has to come out of it is everything the chain has
        /// estimated since — which is what <see cref="DemodContext.ResidualFrequencyHz"/>,
        /// <see cref="DemodContext.PhaseRadians"/> and <see cref="DemodContext.Gain"/> hold. The
        /// per-pass values are the right thing only when derotating a waveform that already carries
        /// the earlier passes' corrections, which this one does not.
        /// </remarks>
        private static double[] Derotate(
            DemodContext context, double[] source, int samples, int pad, int perSymbol)
        {
            double omega =
                2.0 * Math.PI * context.ResidualFrequencyHz / context.Settings.SymbolRateHz;

            double phase = context.PhaseRadians;
            double gain = context.Gain == 0.0 ? 1.0 : context.Gain;
            double timing = context.TimingSamples;

            var derotated = new double[2 * samples];

            for (int sample = 0; sample < samples; sample++)
            {
                double symbols = ((sample - pad) - timing) / perSymbol;
                Iq turn = Iq.FromPhase(-((omega * symbols) + phase));

                Iq.Set(derotated, sample, (Iq.At(source, sample) * turn) / gain);
            }

            return derotated;
        }

        /// <summary>
        /// Reads what every tap sees at every decision instant.
        /// </summary>
        /// <param name="source">The padded, derotated source.</param>
        /// <param name="count">How many symbols the window holds.</param>
        /// <param name="timing">Where the first symbol's decision instant falls.</param>
        /// <param name="perSymbol">The internal processing rate.</param>
        /// <param name="taps">How many taps.</param>
        /// <param name="half">Which tap the reference delay sits on.</param>
        /// <param name="stride">How many samples apart the taps are.</param>
        /// <param name="pad">Where the result window starts in <paramref name="source"/>.</param>
        /// <returns>The tap inputs, symbol-major: <c>taps</c> values for each of
        /// <paramref name="count"/> symbols.</returns>
        /// <remarks>
        /// <para>
        /// <strong>Read where the measurement is read, and that is the whole point.</strong>
        /// <c>REQ-DEM-052</c> writes the target as the reference sequence <c>r_k</c> — the same
        /// subscript the metric requirements use for the ideal SYMBOL — and this is a fit of the
        /// equaliser's output at each decision instant onto that point. Together with T/2-spaced
        /// taps it is the classical fractionally-spaced equaliser.
        /// </para>
        /// <para>
        /// 🔴 An earlier form minimised the error against the regenerated reference WAVEFORM over
        /// every sample. That is a different objective and a worse one: EVM is read at the decision
        /// instants and nowhere else, so a fit that weights the space between them equally spends
        /// its degrees of freedom on samples nobody measures — and the reference waveform between
        /// the instants is only as good as the model of the transmitter's pulse, tapering and
        /// truncation included, whereas AT the instants it is exactly the decided point. Measured,
        /// the waveform-domain fit left a converged floor around 0.2 to 0.46 %rms on impairments
        /// this one takes to a few hundredths.
        /// </para>
        /// <para>
        /// The instants are generally not whole samples, so the source is interpolated at each of
        /// them, with the same interpolator step 8 reads through. Every algorithm reads the signal
        /// through this one matrix, which is what makes the one-shot solution and the gradient modes
        /// answers to the same question rather than to two similar ones.
        /// </para>
        /// </remarks>
        private static Iq[] Regressors(
            double[] source,
            int count,
            double timing,
            int perSymbol,
            int taps,
            int half,
            int stride,
            int pad)
        {
            // The regressor for every symbol and tap, built once. The normal equations read it
            // twice, a gradient sweep reads it once per sweep, and the residual reads it again --
            // and an interpolation is dear enough not to repeat.
            var regressors = new Iq[count * taps];

            for (int symbol = 0; symbol < count; symbol++)
            {
                double instant = timing + (symbol * (double)perSymbol) + pad;

                for (int tap = 0; tap < taps; tap++)
                {
                    regressors[(symbol * taps) + tap] =
                        Interpolator.At(source, instant - ((tap - half) * stride));
                }
            }

            return regressors;
        }

        /// <summary>
        /// Solves the regularised normal equations for the taps (<c>REQ-DEM-052</c>).
        /// </summary>
        /// <param name="regressors">The tap inputs for every symbol, symbol-major.</param>
        /// <param name="symbols">The ideal point for each symbol in the window.</param>
        /// <param name="taps">How many taps.</param>
        /// <returns>The taps, or <c>null</c> when the equations are singular.</returns>
        /// <remarks>
        /// <c>w = (XᴴX + λI)⁻¹Xᴴd</c>, exactly and in one shot. It is internal rather than private
        /// because <c>REQ-DEM-052</c>'s criterion is that this be the analytic solution and not an
        /// iterate — "matches the analytic regularised solution to within 1e-9" — and a test can
        /// only hold the shipped code to that by calling it.
        /// </remarks>
        internal static Iq[] LeastSquares(Iq[] regressors, Iq[] symbols, int taps)
        {
            int count = symbols.Length;

            var matrix = new Iq[taps * taps];
            var right = new Iq[taps];

            for (int row = 0; row < taps; row++)
            {
                for (int column = 0; column < taps; column++)
                {
                    Iq sum = Iq.Zero;

                    for (int symbol = 0; symbol < count; symbol++)
                    {
                        Iq a = regressors[(symbol * taps) + column];
                        Iq b = regressors[(symbol * taps) + row];

                        sum = sum + (a * b.Conjugate());
                    }

                    matrix[(row * taps) + column] = sum;
                }

                Iq correlation = Iq.Zero;

                for (int symbol = 0; symbol < count; symbol++)
                {
                    correlation = correlation +
                        (symbols[symbol] * regressors[(symbol * taps) + row].Conjugate());
                }

                right[row] = correlation;
            }

            double loading = Loading(matrix, taps);

            for (int tap = 0; tap < taps; tap++)
            {
                matrix[(tap * taps) + tap] =
                    matrix[(tap * taps) + tap] + new Iq(loading, 0.0);
            }

            return ComplexSolver.Solve(matrix, right, taps);
        }

        /// <summary>The diagonal loading the normal equations are solved with.</summary>
        /// <param name="matrix">The unloaded normal equations.</param>
        /// <param name="taps">How many taps.</param>
        /// <returns>The absolute amount added to each diagonal term.</returns>
        /// <remarks>
        /// Internal so that a test can form the same analytic solution this step forms without
        /// repeating the constant, which would make the test agree with a copy of the code rather
        /// than with the code.
        /// </remarks>
        internal static double Loading(Iq[] matrix, int taps)
        {
            double diagonal = 0.0;

            for (int tap = 0; tap < taps; tap++)
            {
                diagonal += matrix[(tap * taps) + tap].Magnitude;
            }

            return DiagonalLoading * diagonal / taps;
        }

        /// <summary>Convolves the padded source with the taps, producing the result window.</summary>
        private static double[] Apply(
            double[] source, int samples, Iq[] coefficients, int half, int stride, int pad)
        {
            int available = Iq.Count(source);
            var equalised = new double[2 * samples];

            for (int sample = 0; sample < samples; sample++)
            {
                Iq sum = Iq.Zero;

                for (int tap = 0; tap < coefficients.Length; tap++)
                {
                    int at = sample + pad - ((tap - half) * stride);

                    if (at < 0 || at >= available)
                    {
                        continue;
                    }

                    sum = sum + (coefficients[tap] * Iq.At(source, at));
                }

                Iq.Set(equalised, sample, sum);
            }

            return equalised;
        }

        /// <summary>
        /// How far the equaliser's output at the decision instants is from the ideal points.
        /// </summary>
        /// <param name="regressors">The tap inputs for every symbol, symbol-major.</param>
        /// <param name="symbols">The ideal point for each symbol.</param>
        /// <param name="coefficients">The taps, or <c>null</c> to measure the source untouched.</param>
        /// <param name="taps">How many taps.</param>
        /// <param name="half">Which tap the reference delay sits on.</param>
        /// <returns>The error energy as a fraction of the reference's, which is EVM squared.</returns>
        /// <remarks>
        /// <para>
        /// <strong>This is the objective, so it is what a pass is judged on</strong> — and it is
        /// also, to a square root, the EVM the measurement will report. A pass that reduces it is a
        /// pass that improves the number the user reads.
        /// </para>
        /// <para>
        /// 🔴 The measure it replaced asked how much the equaliser had MOVED the waveform, as a
        /// fraction of the waveform's own energy, which cannot tell "nothing left to correct" from
        /// "the correction left is small next to the signal and large next to the error". A pass
        /// that halves a 0.4 % error vector moves the waveform by under two parts in a hundred
        /// thousand of its energy, and the old measure called that nothing -- which is how a slow
        /// divergence went unnoticed (<c>#432</c>).
        /// </para>
        /// <para>
        /// <strong>Every algorithm is judged by it, on the same targets.</strong> A gradient mode
        /// adapts on its own decisions and the one-shot solution fits to step 9's, but what decides
        /// whether a pass is taken is this one measure against this one reference — otherwise
        /// "the fit improved" would mean a different thing for each mode, and the guarantee that
        /// more passes cannot make a measurement worse would hold for one of them.
        /// </para>
        /// </remarks>
        private static double Residual(
            Iq[] regressors, Iq[] symbols, Iq[] coefficients, int taps, int half)
        {
            double difference = 0.0;
            double energy = 0.0;

            for (int symbol = 0; symbol < symbols.Length; symbol++)
            {
                Iq got;

                if (coefficients == null)
                {
                    // The tap at the reference delay reads the source at the decision instant
                    // itself, which is the waveform with no filter on it.
                    got = regressors[(symbol * taps) + half];
                }
                else
                {
                    got = Iq.Zero;

                    for (int tap = 0; tap < taps; tap++)
                    {
                        got = got + (coefficients[tap] * regressors[(symbol * taps) + tap]);
                    }
                }

                difference += (got - symbols[symbol]).MagnitudeSquared;
                energy += symbols[symbol].MagnitudeSquared;
            }

            return energy < 1e-30 ? double.PositiveInfinity : difference / energy;
        }
    }
}
