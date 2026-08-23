namespace OpenVSA.Demod.Chain
{
    /// <summary>
    /// The steps of the demodulation chain, in the order they are applied
    /// (<c>REQ-DEM-001</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Declaration order is application order.</strong> The values are numbered by their
    /// position in the specification's chain, and <see cref="ProcessingOrder.Steps"/> is derived
    /// from this enumeration rather than written out beside it — the same arrangement
    /// <c>REQ-TRC-003</c> uses for the analysis stages, and for the same reason: one place to
    /// change, and no second list to fall out of step with it.
    /// </para>
    /// <para>
    /// <strong>The numbers start at one, not at zero.</strong> Everywhere else in this codebase an
    /// enumeration starts at zero, and this one deliberately does not: the specification, the user
    /// help and the error messages all say "step 8", and a value whose name says 8 while its number
    /// says 7 would make every one of those a translation. <see cref="ProcessingOrder.PositionOf"/>
    /// gives the zero-based position for code that wants to index with it.
    /// </para>
    /// </remarks>
    public enum DemodStep
    {
        /// <summary>Extract the Search Length window from Main Time (<c>REQ-DEM-033</c>).</summary>
        SearchWindow = 1,

        /// <summary>Burst / pulse search, optional (<c>REQ-DEM-041</c>).</summary>
        BurstSearch = 2,

        /// <summary>Coarse carrier estimate (<c>REQ-DEM-002</c>).</summary>
        CoarseCarrier = 3,

        /// <summary>Resample to N points per symbol (<c>REQ-DEM-034a</c>).</summary>
        Resample = 4,

        /// <summary>The measurement (matched) filter (<c>REQ-DEM-020</c>).</summary>
        MeasurementFilter = 5,

        /// <summary>Sync-pattern search, optional (<c>REQ-DEM-040</c>).</summary>
        SyncSearch = 6,

        /// <summary>Position the Result Length window (<c>REQ-DEM-031</c>).</summary>
        ResultWindow = 7,

        /// <summary>
        /// Joint refinement of carrier frequency, carrier phase, symbol timing and amplitude,
        /// iterated to convergence (<c>REQ-DEM-002</c>).
        /// </summary>
        JointRefinement = 8,

        /// <summary>Symbol decisions, giving the detected bits (<c>REQ-DEM-010</c>).</summary>
        SymbolDecisions = 9,

        /// <summary>
        /// Reference regeneration: bits to ideal symbols, through the reference filter, to an ideal
        /// waveform (<c>REQ-DEM-020</c>).
        /// </summary>
        ReferenceRegeneration = 10,

        /// <summary>
        /// The adaptive equaliser, optional; on update it re-enters the chain at
        /// <see cref="JointRefinement"/> (<c>REQ-DEM-050</c>).
        /// </summary>
        Equaliser = 11,

        /// <summary>
        /// Impairment estimation: IQ offset, gain imbalance, quadrature skew and amplitude droop
        /// (<c>REQ-DEM-066</c>, <c>REQ-DEM-067</c>).
        /// </summary>
        ImpairmentEstimation = 12,

        /// <summary>Error metric computation at the symbol instants (<c>REQ-DEM-060</c>).</summary>
        ErrorMetrics = 13,

        /// <summary>Result trace generation (<c>REQ-DEM-080</c>).</summary>
        ResultTraces = 14,
    }
}
