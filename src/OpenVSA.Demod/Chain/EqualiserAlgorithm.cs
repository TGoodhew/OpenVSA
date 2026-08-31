namespace OpenVSA.Demod.Chain
{
    /// <summary>
    /// Which algorithm fits the equaliser's coefficients (<c>REQ-DEM-052</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The choice is made on engineering grounds, and the requirement says so.</strong> The
    /// reference product does not publish its adaptation algorithm; its exposed controls — filter
    /// length, convergence factor, Run/Hold/Reset — are equally consistent with LMS, NLMS, CMA and
    /// recursive least squares with a forgetting factor, so they are no evidence for any of them.
    /// <c>REQ-DEM-052</c> therefore makes the exact least-squares solution the default and keeps a
    /// gradient mode for behavioural parity, because users may depend on the transient those
    /// controls imply.
    /// </para>
    /// </remarks>
    public enum EqualiserAlgorithm
    {
        /// <summary>
        /// The exact regularised least-squares (Wiener) solution, in one shot (the default).
        /// </summary>
        /// <remarks>
        /// <c>w = (XᴴX + λI)⁻¹Xᴴd</c>, computed from the whole block. The chain already processes
        /// whole blocks non-causally and step 10 has already regenerated the reference sequence, so
        /// this is available directly: it is optimal, deterministic, has no convergence dependence
        /// and needs no step size. With the reference in hand, an iterative gradient method is
        /// strictly worse.
        /// </remarks>
        LeastSquares = 0,

        /// <summary>Complex least-mean-squares, one update per symbol.</summary>
        /// <remarks>
        /// <c>w ← w + µ·e·x*</c>. Retained for parity with the reference product's exposed controls,
        /// not because it is better; the step size is
        /// <see cref="DemodSettings.EqualiserConvergenceFactor"/> and it is bounded by
        /// <c>2/(L·Pₓ)</c>, which this chain enforces rather than leaves to the user to respect.
        /// </remarks>
        Lms = 1,

        /// <summary>Normalised LMS: the step is divided by the input's own energy.</summary>
        /// <remarks>
        /// <c>µₙ = µ̃/(ε + ‖xₙ‖²)</c>, which is stable for <c>0 &lt; µ̃ &lt; 2</c> whatever the
        /// signal's power. <c>REQ-DEM-052</c> offers it as the alternative to enforcing the plain
        /// bound: a step size that means the same thing at every signal level is easier to set and
        /// cannot be invalidated by the operator changing the reference level.
        /// </remarks>
        NormalisedLms = 2,
    }
}
