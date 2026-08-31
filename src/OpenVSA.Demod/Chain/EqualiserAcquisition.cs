namespace OpenVSA.Demod.Chain
{
    /// <summary>
    /// How a gradient equaliser gets started when its decisions cannot yet be trusted
    /// (<c>REQ-DEM-052</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Decision-directed adaptation assumes what it is trying to achieve.</strong> Its error
    /// is the distance from the output to the nearest constellation point, which is the right error
    /// only when the nearest point is the transmitted one. On a channel severe enough to close the
    /// eye it is not: the equaliser is then driven towards the wrong symbols and converges
    /// confidently on nonsense, which is worse than not converging.
    /// </para>
    /// <para>
    /// So <c>REQ-DEM-052</c> requires an acquisition mode for start-up — an error that does not need
    /// to know which symbol was sent — and a handover to decision-directed adaptation once the
    /// decisions have become reliable, judged by EVM falling below
    /// <see cref="DemodSettings.EqualiserAcquisitionEvmPercent"/>. This applies to the gradient
    /// modes only. The least-squares default has no start-up problem to solve: it does not iterate.
    /// </para>
    /// </remarks>
    public enum EqualiserAcquisition
    {
        /// <summary>
        /// Decision-directed from the first update: no acquisition stage (the default).
        /// </summary>
        /// <remarks>
        /// The right choice whenever the eye is open to begin with, which on this chain it usually
        /// is — the equaliser runs after carrier, timing and gain have been estimated, so what is
        /// left for it is the residual channel rather than a signal from nowhere.
        /// </remarks>
        DecisionDirected = 0,

        /// <summary>Blind constant-modulus (Godard) acquisition.</summary>
        /// <remarks>
        /// The error <c>e = y(R₂ − |y|²)</c> asks only that the output have the right modulus, and
        /// says nothing about which symbol it is. That makes it usable when no symbol is known, and
        /// it is also why it cannot finish the job: it is blind to phase, so it converges to the
        /// constellation up to a rotation, and the handover to decision-directed adaptation is what
        /// resolves that. <c>R₂ = E|a|⁴/E|a|²</c> is computed from the constellation in force, so a
        /// format whose points are not all one modulus is handled by the same code.
        /// </remarks>
        ConstantModulus = 1,

        /// <summary>Data-aided from the known sync sequence.</summary>
        /// <remarks>
        /// Where a sync pattern is set and step 6 found it, the symbols under the pattern are known
        /// rather than decided, so the ordinary error <c>e = d − y</c> can be formed from them with
        /// no assumption about the eye at all. Better than blind acquisition where it applies —
        /// it fixes the phase as well as the modulus — and it applies only there: a pattern of a few
        /// tens of symbols is what the acquisition has to work with, and the rest of the window
        /// waits for the handover.
        /// </remarks>
        DataAided = 2,
    }
}
