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
            double amplitudeDroopDbPerSymbol)
        {
            OffsetI = offsetI;
            OffsetQ = offsetQ;
            GainImbalanceDb = gainImbalanceDb;
            QuadratureSkewDegrees = quadratureSkewDegrees;
            AmplitudeDroopDbPerSymbol = amplitudeDroopDbPerSymbol;
        }

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
