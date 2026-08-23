using System;
using System.Linq;

namespace OpenVSA.Synthesis
{
    /// <summary>
    /// A tapped-delay-line channel recovered from the samples, with the confidence that it could be
    /// recovered at all (<c>REQ-SIM-002</c>).
    /// </summary>
    /// <remarks>
    /// The second field is the point. Every other measurement in this harness answers a question the
    /// signal can always answer; a channel estimate can be asked for a delay set the reference
    /// sequence does not distinguish, and there is nothing in the returned taps to show that it was.
    /// Carrying the identifiability beside them means the caller cannot read the answer without also
    /// being able to see whether there was one.
    /// </remarks>
    public sealed class ChannelEstimate
    {
        /// <summary>Creates an estimate.</summary>
        /// <param name="taps">The recovered taps, in the order the delays were given.</param>
        /// <param name="identifiability">Smallest conditional variance of the normalised system.</param>
        public ChannelEstimate(MultipathTap[] taps, double identifiability)
        {
            Taps = taps ?? throw new ArgumentNullException(nameof(taps));
            Identifiability = identifiability;
        }

        /// <summary>The recovered taps.</summary>
        public MultipathTap[] Taps { get; }

        /// <summary>
        /// The smallest conditional variance of the normalised normal equations, between 0 and 1.
        /// </summary>
        public double Identifiability { get; }

        /// <summary>Whether the reference sequence could distinguish these delays.</summary>
        public bool IsIdentifiable =>
            Identifiability >= ImpairmentMeasurement.MinimumIdentifiability;

        /// <inheritdoc />
        public override string ToString()
        {
            return string.Join("; ", Taps.Select(t => t.ToString())) +
                " (identifiability " + Identifiability.ToString("G3") +
                (IsIdentifiable ? ")" : ", REFUSED)");
        }
    }
}
