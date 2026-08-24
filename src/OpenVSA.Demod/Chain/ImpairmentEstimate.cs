using System.Globalization;

namespace OpenVSA.Demod.Chain
{
    /// <summary>
    /// What step 12 found: the impairments that are properties of the transmitter rather than of
    /// the noise.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Four quantities, each estimated over the whole Result Length rather than symbol by symbol,
    /// because each of them is by definition a property of the block: an IQ offset that changed
    /// from symbol to symbol would be a modulation, not an offset.
    /// </para>
    /// <para>
    /// <strong>These are the chain's step 12, not the measurements.</strong> <c>REQ-DEM-066</c> and
    /// <c>REQ-DEM-067</c> specify the IQ origin offset and the gain-imbalance and skew measurements
    /// properly, including how they are reported and what they are referenced to.
    /// <c>REQ-DEM-001</c> needs step 12 to exist and to run where the order says; those
    /// requirements own what it reports.
    /// </para>
    /// </remarks>
    public sealed class ImpairmentEstimate
    {
        internal ImpairmentEstimate(
            double offsetI,
            double offsetQ,
            double gainImbalanceDb,
            double quadratureSkewDegrees,
            double amplitudeDroopDbPerSymbol,
            double residualRotationDegrees)
        {
            OffsetI = offsetI;
            OffsetQ = offsetQ;
            GainImbalanceDb = gainImbalanceDb;
            QuadratureSkewDegrees = quadratureSkewDegrees;
            AmplitudeDroopDbPerSymbol = amplitudeDroopDbPerSymbol;
            ResidualRotationDegrees = residualRotationDegrees;
        }

        /// <summary>
        /// What the impairment fit found left over as a rotation, in degrees
        /// (<c>REQ-DEM-067a</c>).
        /// </summary>
        /// <remarks>
        /// <para>
        /// <strong>This is the number that says the skew and the carrier phase were separated.</strong>
        /// Step 8 estimates and removes the carrier phase; step 12 then fits a general affine map
        /// and splits it into a rotation, two gains and a symmetric skew. On a signal whose only
        /// impairment is quadrature skew this comes out near zero, because the symmetric model has
        /// no rotational component for step 8 to have absorbed.
        /// </para>
        /// <para>
        /// A one-sided shear model would put half the skew here instead, and
        /// <see cref="QuadratureSkewDegrees"/> would be short by the same amount — which is the
        /// failure <c>REQ-DEM-067a</c> is written against and what its test looks for.
        /// </para>
        /// </remarks>
        public double ResidualRotationDegrees { get; }

        /// <summary>The in-phase part of the origin offset, in the constellation's units.</summary>
        public double OffsetI { get; }

        /// <summary>The quadrature part of the origin offset.</summary>
        public double OffsetQ { get; }

        /// <summary>The gain imbalance between the axes, in decibels.</summary>
        public double GainImbalanceDb { get; }

        /// <summary>The quadrature skew — the axes' departure from a right angle — in degrees.</summary>
        public double QuadratureSkewDegrees { get; }

        /// <summary>The amplitude droop across the block, in decibels per symbol.</summary>
        public double AmplitudeDroopDbPerSymbol { get; }

        /// <inheritdoc />
        public override string ToString() =>
            "offset (" + OffsetI.ToString("G4", CultureInfo.InvariantCulture) + ", " +
            OffsetQ.ToString("G4", CultureInfo.InvariantCulture) + "), imbalance " +
            GainImbalanceDb.ToString("G4", CultureInfo.InvariantCulture) + " dB, skew " +
            QuadratureSkewDegrees.ToString("G4", CultureInfo.InvariantCulture) + "°, droop " +
            AmplitudeDroopDbPerSymbol.ToString("G4", CultureInfo.InvariantCulture) + " dB/symbol";
    }
}
