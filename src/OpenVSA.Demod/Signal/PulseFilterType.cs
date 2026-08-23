namespace OpenVSA.Demod.Signal
{
    /// <summary>
    /// Which pulse-shaping filter a stage of the chain applies.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Two of nine, and the two are not an arbitrary pair.</strong> <c>REQ-DEM-021</c>'s
    /// catalogue has nine entries — root raised cosine, raised cosine, Gaussian, EDGE, half sine,
    /// rectangular, low-pass, user-defined FIR and none — and that requirement is where the rest
    /// arrive. These two are here because between them they cover both kinds of signal a
    /// demodulator is handed: one that has been through half of a Nyquist filter and needs the
    /// matching half, and one that has already been through all of it and needs nothing.
    /// </para>
    /// <para>
    /// <strong>Why the second case is real and not a curiosity.</strong> The synthetic source of
    /// <c>REQ-SIM-001</c> is deliberately both ends of a link at once: it shapes with a full raised
    /// cosine so that its samples at the decision instants are exactly the symbols it sent, which
    /// is what lets it be checked against its own truth without a demodulator. Applying a matched
    /// filter to a waveform that is already matched-filtered costs about 10 % EVM — measured, on
    /// that source, before this existed.
    /// </para>
    /// </remarks>
    public enum PulseFilterType
    {
        /// <summary>
        /// Root raised cosine: the receiver's half of a Nyquist pair (<c>REQ-DEM-020</c>).
        /// </summary>
        RootRaisedCosine = 0,

        /// <summary>
        /// None: the signal is passed through unfiltered.
        /// </summary>
        /// <remarks>
        /// For a signal that is already Nyquist-shaped end to end. It is a filter choice rather
        /// than a step being skipped — step 5 of <c>REQ-DEM-001</c>'s order runs, and applies this.
        /// </remarks>
        None,
    }
}
