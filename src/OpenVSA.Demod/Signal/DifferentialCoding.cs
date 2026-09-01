namespace OpenVSA.Demod.Signal
{
    /// <summary>
    /// How much of a symbol a format carries in the change from the symbol before it
    /// (<c>REQ-DEM-012</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>A bool said "differential" and that turned out to be two different things.</strong>
    /// DQPSK carries its whole symbol in the change of phase; DVB's cable and terrestrial QAMs
    /// carry only the QUADRANT in the change and the point within that quadrant absolutely. Both
    /// are differential, both are immune to a rotated constellation, and a decoder that treated one
    /// as the other would produce a well-formed bit stream that meant nothing — at an EVM beyond
    /// reproach, because the decisions themselves are unaffected.
    /// </para>
    /// <para>
    /// It is a property of the format rather than of its points, like
    /// <see cref="Constellation.IsOffset"/>: DQPSK's constellation is QPSK's and DVB-64QAM's is
    /// 64QAM's, and what differs is what the recovered symbols are read against.
    /// </para>
    /// </remarks>
    public enum DifferentialCoding
    {
        /// <summary>The symbol carries its own bits. Everything in the catalogue but the rows below.</summary>
        None = 0,

        /// <summary>
        /// The whole symbol is the change: the data is the difference of two indices around one
        /// ring, which is a change of phase. DQPSK, D8PSK, π/4-DQPSK, MSK type 1.
        /// </summary>
        WholeSymbol,

        /// <summary>
        /// Only the quadrant is the change; the point within the quadrant is absolute. The DVB
        /// QAMs, and the reason they need no phase reference: a square constellation has a
        /// four-fold ambiguity nothing in the signal can resolve, and encoding the quadrant as a
        /// difference makes the data independent of which of the four a receiver landed on.
        /// </summary>
        Quadrant,
    }
}
