using System;
using OpenVSA.Demod.Results;
using OpenVSA.Demod.Signal;

namespace OpenVSA.Demod.Chain.Steps
{
    /// <summary>
    /// Step 12: estimate the impairments that are properties of the block.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Scope.</strong> <c>REQ-DEM-067</c> owns the gain imbalance and quadrature skew and
    /// states the model to fit; <c>REQ-DEM-067a</c> owns the split between skew and carrier phase;
    /// <c>REQ-DEM-066</c> takes its origin offset from this same fit. This is the step
    /// <c>REQ-DEM-001</c> puts at position 12.
    /// </para>
    /// <para>
    /// <strong>One fit, not four.</strong> Each axis of the measured symbol is fitted against both
    /// axes of the ideal one and a constant:
    /// </para>
    /// <code>
    ///     Re z = a Re r + b Im r + cI
    ///     Im z = d Re r + e Im r + cQ
    /// </code>
    /// <para>
    /// which is the general affine map, six parameters, two independent three-parameter
    /// least-squares problems sharing one Gram matrix. Estimating the impairments one at a time
    /// would let each absorb some of the others, which is the confusion <c>REQ-DEM-067a</c> exists
    /// to prevent — and which shows up not in the singles but in the pair, where a one-at-a-time
    /// estimator passes each impairment alone and fails both together.
    /// </para>
    /// <para>
    /// <strong>The constant is fitted, not subtracted first.</strong> An earlier form of this took
    /// the mean of <c>z - r</c> and fitted the axes to what was left. That is unbiased only when the
    /// block's ideal symbols average to zero: with a gain error present the mean of <c>z - r</c>
    /// carries <c>(g - 1)</c> times the mean of <c>r</c>, so a short block with an unbalanced symbol
    /// sequence reports an origin offset that is really a gain error. <c>REQ-DEM-066</c> names that
    /// case in its acceptance criterion.
    /// </para>
    /// <para>
    /// <strong>The decomposition is where the requirement's model lives.</strong> The six fitted
    /// numbers are a general affine map, and <c>REQ-DEM-067</c>'s model has five: two gains, a skew
    /// and two offsets. The missing sixth is a rotation — and that is the whole point. The matrix is
    /// split as
    /// </para>
    /// <code>
    ///     M = R(theta) . diag(gI, gQ) . K(psi/2),    K(x) = [[cos x, sin x], [sin x, cos x]]
    /// </code>
    /// <para>
    /// exactly, four parameters for four degrees of freedom. <c>K</c> is symmetric: it stretches
    /// along the 45-degree line and has no rotational component, which is what makes <c>psi</c>
    /// identifiable at all. Putting the whole skew on Q instead — a shear — decomposes as a rotation
    /// by <c>psi/2</c> composed with this, and that rotation is indistinguishable from carrier
    /// phase and is silently eaten by step 8. <c>theta</c> is what is left over after step 8 has
    /// done its work, and on a signal impaired by pure skew it comes out near zero, which is
    /// <c>REQ-DEM-067a</c>'s test.
    /// </para>
    /// <para>
    /// <strong>Droop is fitted in the logarithm.</strong> Amplitude droop is a decay along the
    /// block, so the quantity that is linear in symbol number is the logarithm of the magnitude
    /// ratio, and that is what is fitted. Fitting the ratio itself would give a number that agreed
    /// for small droops and disagreed for the large ones a droop measurement exists to find.
    /// </para>
    /// </remarks>
    internal sealed class ImpairmentStep : IChainStep
    {
        /// <inheritdoc />
        public DemodStep Step => DemodStep.ImpairmentEstimation;

        /// <inheritdoc />
        public StepOutcome Run(DemodContext context)
        {
            Iq[] measured = context.MeasuredSymbols;
            Iq[] ideal = context.IdealSymbols;

            if (measured == null || ideal == null)
            {
                throw new ChainOrderException(
                    "Step 12 ran before the symbols it estimates from existed. The chain was " +
                    "executed out of order.");
            }

            int count = measured.Length;

            // The Gram matrix of [Re r, Im r, 1], which both axes share.
            double sumII = 0.0;
            double sumIQ = 0.0;
            double sumQQ = 0.0;
            double sumI = 0.0;
            double sumQ = 0.0;

            double crossIx = 0.0;
            double crossQx = 0.0;
            double sumX = 0.0;
            double crossIy = 0.0;
            double crossQy = 0.0;
            double sumY = 0.0;

            for (int symbol = 0; symbol < count; symbol++)
            {
                double i = ideal[symbol].I;
                double q = ideal[symbol].Q;
                double x = measured[symbol].I;
                double y = measured[symbol].Q;

                sumII += i * i;
                sumIQ += i * q;
                sumQQ += q * q;
                sumI += i;
                sumQ += q;

                crossIx += i * x;
                crossQx += q * x;
                sumX += x;

                crossIy += i * y;
                crossQy += q * y;
                sumY += y;
            }

            double[] alongI;
            double[] alongQ;

            // Both, not short-circuited: the two solves share a Gram matrix, so either both succeed
            // or neither does, and writing it as && leaves the second output unassigned.
            bool fittedI = Solve(
                sumII, sumIQ, sumI, sumIQ, sumQQ, sumQ, sumI, sumQ, count,
                crossIx, crossQx, sumX, out alongI);

            bool fittedQ = Solve(
                sumII, sumIQ, sumI, sumIQ, sumQQ, sumQ, sumI, sumQ, count,
                crossIy, crossQy, sumY, out alongQ);

            bool separable = fittedI && fittedQ;

            if (!separable)
            {
                // The symbols do not exercise both axes independently -- a constellation on one
                // line, or a block short enough to have used only part of one. There is no
                // imbalance to report, and reporting one anyway would be reporting the noise.
                context.Note(
                    "Step 12 could not separate the two axes: the symbols in this block do not " +
                    "exercise them independently. Gain imbalance and quadrature skew are reported " +
                    "as zero because they were not measured, not because they were absent.");

                context.Impairments = new ImpairmentEstimate(
                    Mean(measured, ideal, true, count),
                    Mean(measured, ideal, false, count),
                    0.0,
                    0.0,
                    Droop(measured, ideal, count),
                    0.0);

                return StepOutcome.Continue;
            }

            double gainI;
            double gainQ;
            double skewRadians;
            double rotationRadians;

            Decompose(
                alongI[0], alongI[1], alongQ[0], alongQ[1],
                out gainI, out gainQ, out skewRadians, out rotationRadians);

            // REQ-DEM-067's stated convention, and it is stated because it is a coin toss otherwise:
            // POSITIVE means Q is larger than I.
            double imbalanceDb = gainI < 1e-15 || gainQ < 1e-15
                ? 0.0
                : 20.0 * Math.Log10(gainQ / gainI);

            double offsetI = alongI[2];
            double offsetQ = alongQ[2];

            // REQ-DEM-066's exception, and the only place in the chain where a metric is computed
            // off a different point set from its neighbours.
            if (context.Settings.Constellation.Family == ModulationFamily.Msk)
            {
                AllPointOffset(context, count, ref offsetI, ref offsetQ);
            }

            context.Impairments = new ImpairmentEstimate(
                offsetI,
                offsetQ,
                imbalanceDb,
                skewRadians * 180.0 / Math.PI,
                Droop(measured, ideal, count),
                rotationRadians * 180.0 / Math.PI);

            return StepOutcome.Continue;
        }

        /// <summary>
        /// Re-fits the origin offset over every sample rather than the symbol instants, for MSK
        /// (<c>REQ-DEM-066</c>).
        /// </summary>
        /// <param name="context">The chain's state.</param>
        /// <param name="count">How many symbols the result holds.</param>
        /// <param name="offsetI">The symbol-instant estimate, replaced on success.</param>
        /// <param name="offsetQ">The symbol-instant estimate, replaced on success.</param>
        /// <remarks>
        /// <para>
        /// <strong>The requirement singles MSK out, and the geometry says why.</strong> Carrier
        /// feedthrough is a constant added to the trajectory, and what an estimator has to work
        /// with is how many independent directions that trajectory visits. A QAM constellation's
        /// symbol instants are spread over the plane, so a handful of them pins a constant down.
        /// MSK's are not: its symbol instants land on FOUR points and its information lives in the
        /// continuous path between them. Fitting a constant to four clusters leans on how the
        /// block's symbols happened to be distributed among them, which is the same dependence on
        /// symbol statistics that the fitted constant was introduced to escape at symbol times.
        /// </para>
        /// <para>
        /// Every sample visits the whole circle, so the offset is determined by the shape of the
        /// path rather than by which symbols were sent. At MSK's 32 samples a symbol that is 32
        /// times the data for one number.
        /// </para>
        /// <para>
        /// <strong>Only the offset.</strong> <c>REQ-DEM-067</c>'s gain imbalance, skew and rotation
        /// stay on the symbol instants where that requirement puts them. This replaces two of the
        /// six fitted numbers and leaves four alone: two fits rather than one, and only for MSK.
        /// </para>
        /// <para>
        /// <strong>The measured samples are corrected the way step 8 corrected the symbols</strong>
        /// — the same de-rotation and the same gain — because the ideal waveform they are fitted
        /// against was regenerated from decided symbols with those corrections already taken out.
        /// Fitting a corrected reference against an uncorrected measurement would report the
        /// carrier phase as an origin offset.
        /// </para>
        /// <para>
        /// The ends are left out. Step 10 regenerates the ideal on the result's own grid, so its
        /// first and last samples carry a pulse still filling; the fit runs between the first and
        /// last symbol instants, where both waveforms are fully formed.
        /// </para>
        /// </remarks>
        private static void AllPointOffset(
            DemodContext context, int count, ref double offsetI, ref double offsetQ)
        {
            double[] result = context.Result;
            double[] idealWaveform = context.IdealWaveform;

            if (result == null || idealWaveform == null || count < 2)
            {
                return;
            }

            DemodSettings settings = context.Settings;
            int perSymbol = settings.PointsPerSymbol;
            double timing = context.TimingSamples;

            int available = Math.Min(Iq.Count(result), Iq.Count(idealWaveform));
            int first = Math.Max(0, (int)Math.Ceiling(timing));
            int last = Math.Min(
                available - 1, (int)Math.Floor(timing + ((count - 1) * perSymbol)));

            int points = last - first + 1;

            if (points < 4)
            {
                return;
            }

            double omega = settings.SymbolRateHz <= 0.0
                ? 0.0
                : 2.0 * Math.PI * context.PassFrequencyHz / settings.SymbolRateHz;

            double phase = context.PassPhaseRadians;
            double gain = context.PassGain == 0.0 ? 1.0 : context.PassGain;

            double sumII = 0.0;
            double sumIQ = 0.0;
            double sumQQ = 0.0;
            double sumI = 0.0;
            double sumQ = 0.0;

            double crossIx = 0.0;
            double crossQx = 0.0;
            double sumX = 0.0;
            double crossIy = 0.0;
            double crossQy = 0.0;
            double sumY = 0.0;

            for (int sample = first; sample <= last; sample++)
            {
                Iq raw = Iq.At(result, sample);
                Iq turn = Iq.FromPhase(
                    -(((omega * (sample - timing)) / perSymbol) + phase));

                Iq corrected = (raw * turn) / gain;
                Iq reference = Iq.At(idealWaveform, sample);

                double i = reference.I;
                double q = reference.Q;
                double x = corrected.I;
                double y = corrected.Q;

                sumII += i * i;
                sumIQ += i * q;
                sumQQ += q * q;
                sumI += i;
                sumQ += q;

                crossIx += i * x;
                crossQx += q * x;
                sumX += x;

                crossIy += i * y;
                crossQy += q * y;
                sumY += y;
            }

            double[] alongI;
            double[] alongQ;

            // Both, not short-circuited, for the reason the symbol-instant fit above states: the
            // two solves share a Gram matrix, so either both succeed or neither does, and an &&
            // leaves the second output unassigned. Written as && here first, and the compiler
            // caught it -- which is the second time this file has had to say so.
            bool fittedI = Solve(
                sumII, sumIQ, sumI, sumIQ, sumQQ, sumQ, sumI, sumQ, points,
                crossIx, crossQx, sumX, out alongI);

            bool fittedQ = Solve(
                sumII, sumIQ, sumI, sumIQ, sumQQ, sumQ, sumI, sumQ, points,
                crossIy, crossQy, sumY, out alongQ);

            bool fitted = fittedI && fittedQ;

            if (!fitted)
            {
                // The trajectory did not exercise both axes independently over this block. The
                // symbol-instant estimate stands: it is the weaker one the requirement moves away
                // from, and it is better than none.
                context.Note(
                    "REQ-DEM-066 computes MSK's IQ offset over every sample rather than the " +
                    "symbol instants, and this block's waveform did not separate the two axes. " +
                    "The offset reported is the symbol-instant estimate.");

                return;
            }

            offsetI = alongI[2];
            offsetQ = alongQ[2];
        }

        /// <summary>
        /// Splits a fitted affine map into a rotation, two axis gains and a symmetric skew.
        /// </summary>
        /// <param name="m11">The I-to-I term.</param>
        /// <param name="m12">The Q-to-I term.</param>
        /// <param name="m21">The I-to-Q term.</param>
        /// <param name="m22">The Q-to-Q term.</param>
        /// <param name="gainI">The I axis's gain.</param>
        /// <param name="gainQ">The Q axis's gain.</param>
        /// <param name="skewRadians">The quadrature error.</param>
        /// <param name="rotationRadians">What is left over as a rotation.</param>
        /// <remarks>
        /// <para>
        /// <c>M = R(theta) . diag(gI, gQ) . K(psi/2)</c>. Writing <c>N = R(-theta) M</c>, the
        /// product form requires <c>N11 N21 = N12 N22</c>, and expanding that in <c>theta</c> gives
        /// </para>
        /// <code>
        ///     tan 2theta = -2 (m11 m21 - m12 m22) / ((m21^2 - m11^2) - (m22^2 - m12^2))
        /// </code>
        /// <para>
        /// which is closed form. It has two roots a quarter turn apart, and they are not equivalent:
        /// one is a small skew, the other the same map read as a quarter-turn rotation with a skew
        /// near a right angle. <strong>The convention is the smaller skew</strong>, because a
        /// quadrature error beyond 45 degrees is not a quadrature error, it is a different
        /// constellation — and <c>REQ-DEM-067a</c> asks for a convention that is documented and
        /// deterministic rather than whichever root the arctangent happened to return.
        /// </para>
        /// </remarks>
        private static void Decompose(
            double m11,
            double m12,
            double m21,
            double m22,
            out double gainI,
            out double gainQ,
            out double skewRadians,
            out double rotationRadians)
        {
            double a = ((m21 * m21) - (m11 * m11)) - ((m22 * m22) - (m12 * m12));
            double b = (m11 * m21) - (m12 * m22);

            double first = 0.5 * Math.Atan2(-2.0 * b, a);

            gainI = 0.0;
            gainQ = 0.0;
            skewRadians = 0.0;
            rotationRadians = 0.0;

            bool chosen = false;

            for (int root = 0; root < 2; root++)
            {
                double theta = first + (root * Math.PI / 2.0);

                double cos = Math.Cos(theta);
                double sin = Math.Sin(theta);

                double n11 = (cos * m11) + (sin * m21);
                double n12 = (cos * m12) + (sin * m22);
                double n21 = (-sin * m11) + (cos * m21);
                double n22 = (-sin * m12) + (cos * m22);

                // A solution with negative gains is the same map turned through half a turn; turning
                // it back keeps the gains positive, which is what a gain is.
                if (n11 + n22 < 0.0)
                {
                    theta += Math.PI;
                    n11 = -n11;
                    n12 = -n12;
                    n21 = -n21;
                    n22 = -n22;
                }

                double half = Math.Atan2(n12, n11);
                double candidateSkew = 2.0 * half;

                if (chosen && Math.Abs(candidateSkew) >= Math.Abs(skewRadians))
                {
                    continue;
                }

                chosen = true;
                gainI = Math.Sqrt((n11 * n11) + (n12 * n12));
                gainQ = Math.Sqrt((n21 * n21) + (n22 * n22));
                skewRadians = candidateSkew;
                rotationRadians = Wrap(theta);
            }
        }

        /// <summary>Solves a symmetric three-by-three system by elimination.</summary>
        /// <returns>Whether it could be solved: a singular system is a block that says nothing.</returns>
        private static bool Solve(
            double a11, double a12, double a13,
            double a21, double a22, double a23,
            double a31, double a32, double a33,
            double b1, double b2, double b3,
            out double[] solution)
        {
            solution = null;

            double determinant =
                (a11 * ((a22 * a33) - (a23 * a32))) -
                (a12 * ((a21 * a33) - (a23 * a31))) +
                (a13 * ((a21 * a32) - (a22 * a31)));

            // Scaled against the matrix's own size, because the entries are sums over the block and
            // grow with it: an absolute floor would call a long block singular or a short one fine
            // depending only on how many symbols it held.
            double scale =
                Math.Abs(a11) + Math.Abs(a22) + Math.Abs(a33) + 1e-300;

            if (Math.Abs(determinant) < 1e-12 * scale * scale * scale)
            {
                return false;
            }

            double x =
                ((b1 * ((a22 * a33) - (a23 * a32))) -
                 (a12 * ((b2 * a33) - (a23 * b3))) +
                 (a13 * ((b2 * a32) - (a22 * b3)))) / determinant;

            double y =
                ((a11 * ((b2 * a33) - (a23 * b3))) -
                 (b1 * ((a21 * a33) - (a23 * a31))) +
                 (a13 * ((a21 * b3) - (b2 * a31)))) / determinant;

            double z =
                ((a11 * ((a22 * b3) - (b2 * a32))) -
                 (a12 * ((a21 * b3) - (b2 * a31))) +
                 (b1 * ((a21 * a32) - (a22 * a31)))) / determinant;

            solution = new[] { x, y, z };

            return true;
        }

        /// <summary>The mean of one axis of <c>z - r</c>, for the block that cannot be fitted.</summary>
        private static double Mean(Iq[] measured, Iq[] ideal, bool inPhase, int count)
        {
            double sum = 0.0;

            for (int symbol = 0; symbol < count; symbol++)
            {
                sum += inPhase
                    ? measured[symbol].I - ideal[symbol].I
                    : measured[symbol].Q - ideal[symbol].Q;
            }

            return count == 0 ? 0.0 : sum / count;
        }

        private static double Droop(Iq[] measured, Iq[] ideal, int count)
        {
            double sumIndex = 0.0;
            double sumIndexSquared = 0.0;
            double sumRatio = 0.0;
            double sumIndexRatio = 0.0;
            int used = 0;

            for (int symbol = 0; symbol < count; symbol++)
            {
                double reference = ideal[symbol].Magnitude;
                double magnitude = measured[symbol].Magnitude;

                if (reference < 1e-12 || magnitude < 1e-12)
                {
                    continue;
                }

                double ratio = Math.Log(magnitude / reference);

                sumIndex += symbol;
                sumIndexSquared += (double)symbol * symbol;
                sumRatio += ratio;
                sumIndexRatio += symbol * ratio;
                used++;
            }

            if (used < 2)
            {
                return 0.0;
            }

            double determinant = (used * sumIndexSquared) - (sumIndex * sumIndex);

            if (Math.Abs(determinant) < 1e-12)
            {
                return 0.0;
            }

            double slope = ((used * sumIndexRatio) - (sumIndex * sumRatio)) / determinant;

            return slope * 20.0 / Math.Log(10.0);
        }

        private static double Wrap(double radians)
        {
            double wrapped = radians;

            while (wrapped > Math.PI)
            {
                wrapped -= 2.0 * Math.PI;
            }

            while (wrapped < -Math.PI)
            {
                wrapped += 2.0 * Math.PI;
            }

            return wrapped;
        }
    }
}
