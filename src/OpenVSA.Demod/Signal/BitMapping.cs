namespace OpenVSA.Demod.Signal
{
    /// <summary>
    /// Which bits a constellation's points carry (<c>REQ-DEM-011</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The geometry and the labelling are separate facts, and only one of them is
    /// measurable.</strong> Where the points sit shows up in the EVM; which bits each one carries
    /// does not, because every labelling of the same points demodulates identically and reports the
    /// same error vector. So a wrong labelling is invisible to everything except a comparison with
    /// what a transmitter actually sent.
    /// </para>
    /// <para>
    /// <strong>That is not theoretical.</strong> On 24 August 2026 an E4438C's <c>P4DQPSK</c> and
    /// <c>D8PSK</c> were demodulated against this catalogue's natural labelling and compared with an
    /// independently generated PN9: the bits missed, at 0.87 and 0.91 %rms — and a Gray relabelling
    /// accounted for 511 of 511 symbols of each. The demodulation was right and the labels were the
    /// instrument's. <c>evidence/req-dem-012/</c> has it.
    /// </para>
    /// </remarks>
    public enum BitMapping
    {
        /// <summary>
        /// Point <em>n</em> carries the bits of <em>n</em>.
        /// </summary>
        /// <remarks>
        /// The default, and what every format in this catalogue used before <c>REQ-DEM-011</c>. It
        /// is a convention rather than a standard: what makes it the right default is that it is the
        /// one the rest of the code can be read against, not that any transmitter uses it.
        /// </remarks>
        Natural = 0,

        /// <summary>
        /// Neighbouring points differ in one bit.
        /// </summary>
        /// <remarks>
        /// <para>
        /// What nearly every standard specifies, because a symbol decided as its neighbour then
        /// costs one bit rather than several. <strong>It means two different things, and which one
        /// depends on the geometry</strong> — a distinction that is easy to lose and produces a
        /// perfectly plausible wrong answer:
        /// </para>
        /// <list type="bullet">
        /// <item><description>
        /// On a ring — the phase-keyed family — neighbouring points are neighbouring indices, so the
        /// labelling is the reflected binary code of the index, taken around the circle.
        /// </description></item>
        /// <item><description>
        /// On a square grid — quadrature amplitude modulation — a point has neighbours on both
        /// axes, so the code is applied to the I level and the Q level separately. Applying the
        /// ring's version to a QAM would leave points that touch differing in several bits, which is
        /// the whole property Gray coding exists for.
        /// </description></item>
        /// </list>
        /// <para>
        /// Where neither applies — a cross QAM, a star, an arbitrary set of rings — there is no one
        /// Gray code, and asking for this is refused rather than answered with a guess.
        /// <see cref="Explicit"/> is how a user says what they mean in that case.
        /// </para>
        /// </remarks>
        Gray = 1,

        /// <summary>
        /// The user says which bits each point carries, point by point.
        /// </summary>
        /// <remarks>
        /// The general case, and the only one that can express a standard's own table. The table is
        /// a permutation of the symbol values and is refused if it is not: a labelling that gave two
        /// points the same bits would make those two symbols indistinguishable in the bit stream
        /// while leaving them perfectly distinguishable on the constellation, which is a defect that
        /// would show up as a bit error rate and nothing else.
        /// </remarks>
        Explicit = 2,
    }
}
