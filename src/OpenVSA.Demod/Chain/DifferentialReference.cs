namespace OpenVSA.Demod.Chain
{
    /// <summary>
    /// What a symbol's bits are read against (<c>REQ-DEM-012</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Why this is selectable at all.</strong> A differentially encoded signal and an
    /// absolutely encoded one are the same waveform: nothing in the signal says which it is, because
    /// the encoding is a statement about what the symbols <em>mean</em> rather than about where they
    /// sit. A demodulator therefore cannot work it out, and <c>REQ-DEM-012</c> requires it to be
    /// chosen instead of assumed.
    /// </para>
    /// <para>
    /// <strong>Choosing wrongly is not a failure, and that is the danger.</strong> Either choice
    /// demodulates, converges and reports the same EVM — the constellation is the same either way.
    /// What changes is the bit stream, and it changes into a bit stream that is perfectly
    /// well-formed and wrong. Which is why the criterion for this requirement is not "the right
    /// selection works" but that the wrong one is wrong <em>predictably</em>: the bits it gives are
    /// the encoded symbols rather than the data, and a test says so by computing them.
    /// </para>
    /// </remarks>
    public enum DifferentialReference
    {
        /// <summary>
        /// Whatever the format implies: the previous symbol for a differential format, and the
        /// symbol itself for every other.
        /// </summary>
        /// <remarks>
        /// The default, and what a user selecting "DQPSK" from a menu means. It is a separate value
        /// rather than the settings being pre-filled from the format because a setting that was
        /// copied at the moment of choosing would go stale the moment the format changed underneath
        /// it — the same trap the synthetic source's symbol rate is deliberately kept out of.
        /// </remarks>
        PerFormat = 0,

        /// <summary>The symbol's own value; no differential decoding.</summary>
        None = 1,

        /// <summary>The change from the symbol before it, around the constellation's ring.</summary>
        /// <remarks>
        /// The first symbol of the Result Length window is then the reference and carries no data,
        /// so a window of <em>n</em> symbols yields <em>n − 1</em> symbols of data. That is a
        /// property of differential encoding rather than a shortfall: there is nothing for the first
        /// symbol to be a change from.
        /// </remarks>
        PreviousSymbol = 2,
    }
}
