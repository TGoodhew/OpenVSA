namespace OpenVSA.Ui.Rendering
{
    /// <summary>
    /// The demodulation result displays a trace window can show.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A third axis alongside <c>TraceFormat</c> and <c>TraceAccumulator</c>, and separate from
    /// both for the same reason they are separate from each other. A format is a pure function of
    /// one calibrated spectrum; an accumulator builds across acquisitions; a result trace draws
    /// what a demodulator produced. Putting these in <c>TraceFormat</c> would break that
    /// enumeration's own stated rule, which is the mistake <c>REQ-TRC-001a</c> already caught once
    /// for the accumulators.
    /// </para>
    /// <para>
    /// <see cref="Constellation"/> and <see cref="IqVector"/> are the same data and differ by the
    /// connecting lines alone — <c>REQ-UI-050</c>'s "similar to the IQ trace format but without the
    /// lines that connect the points". They are two members rather than a flag so that a trace
    /// window's format list reads as the product's does.
    /// </para>
    /// </remarks>
    public enum ResultTraceKind
    {
        /// <summary>Not a result display; the window draws a spectrum.</summary>
        None = 0,

        /// <summary>Points at the symbol decision instants, with no connecting lines.</summary>
        Constellation,

        /// <summary>The same points with the inter-symbol trajectory drawn between them.</summary>
        IqVector,

        /// <summary>The waveform folded on the symbol clock.</summary>
        Eye,

        /// <summary>The error summary and the symbol stream, as one trace split top and bottom.</summary>
        SymbolTable,
    }
}
