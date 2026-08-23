using System;
using OpenVSA.Demod.Signal;

namespace OpenVSA.Demod.Chain.Steps
{
    /// <summary>
    /// Step 12: estimate the impairments that are properties of the block.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Scope.</strong> <c>REQ-DEM-066</c> owns the IQ origin offset, <c>REQ-DEM-067</c> the
    /// gain imbalance and quadrature skew, and <c>REQ-DEM-067a</c> the joint estimation of phase
    /// and skew that keeps the two from being confused for one another. This step is the one
    /// <c>REQ-DEM-001</c> puts at position 12, and it estimates all four of the specification's
    /// quantities by least squares over the whole Result Length.
    /// </para>
    /// <para>
    /// <strong>One fit, not four.</strong> The measured symbols are modelled as
    /// <c>y = A·I(d) + B·Q(d) + offset</c>, where <c>A</c> and <c>B</c> are complex and <c>d</c> is
    /// the ideal symbol. A perfect transmitter gives <c>A = 1</c> and <c>B = j</c>: the two axes
    /// equal in length and a right angle apart. Everything the step reports is then a reading of
    /// that one fit — the imbalance is the ratio of their lengths, the skew is their angle less a
    /// right angle, and the offset is the constant. Estimating them one at a time would let each
    /// absorb some of the others, which is exactly the confusion <c>REQ-DEM-067a</c> is about.
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

            double offsetI = 0.0;
            double offsetQ = 0.0;

            for (int symbol = 0; symbol < count; symbol++)
            {
                offsetI += measured[symbol].I - ideal[symbol].I;
                offsetQ += measured[symbol].Q - ideal[symbol].Q;
            }

            offsetI /= count;
            offsetQ /= count;

            var offset = new Iq(offsetI, offsetQ);

            double inPhaseEnergy = 0.0;
            double quadratureEnergy = 0.0;
            double cross = 0.0;
            Iq inPhaseCorrelation = Iq.Zero;
            Iq quadratureCorrelation = Iq.Zero;

            for (int symbol = 0; symbol < count; symbol++)
            {
                double i = ideal[symbol].I;
                double q = ideal[symbol].Q;
                Iq centred = measured[symbol] - offset;

                inPhaseEnergy += i * i;
                quadratureEnergy += q * q;
                cross += i * q;

                inPhaseCorrelation = inPhaseCorrelation + (centred * i);
                quadratureCorrelation = quadratureCorrelation + (centred * q);
            }

            double determinant = (inPhaseEnergy * quadratureEnergy) - (cross * cross);

            Iq axisI;
            Iq axisQ;

            if (Math.Abs(determinant) < 1e-15)
            {
                // The symbols do not exercise both axes independently — a constellation on one
                // line, or a block short enough to have used only part of one. There is no
                // imbalance to report, and reporting one anyway would be reporting the noise.
                axisI = new Iq(1.0, 0.0);
                axisQ = new Iq(0.0, 1.0);

                context.Note(
                    "Step 12 could not separate the two axes: the symbols in this block do not " +
                    "exercise them independently. Gain imbalance and quadrature skew are reported " +
                    "as zero because they were not measured, not because they were absent.");
            }
            else
            {
                axisI =
                    ((inPhaseCorrelation * quadratureEnergy) - (quadratureCorrelation * cross)) /
                    determinant;

                axisQ =
                    ((quadratureCorrelation * inPhaseEnergy) - (inPhaseCorrelation * cross)) /
                    determinant;
            }

            double lengthI = axisI.Magnitude;
            double lengthQ = axisQ.Magnitude;

            double imbalanceDb = lengthI < 1e-15 || lengthQ < 1e-15
                ? 0.0
                : 20.0 * Math.Log10(lengthI / lengthQ);

            double skewDegrees = Wrap(axisQ.Phase - axisI.Phase - (Math.PI / 2.0)) * 180.0 / Math.PI;

            context.Impairments = new ImpairmentEstimate(
                offsetI, offsetQ, imbalanceDb, skewDegrees, Droop(measured, ideal, count));

            return StepOutcome.Continue;
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
