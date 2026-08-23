namespace OpenVSA.Demod.Chain
{
    /// <summary>What a step asks the chain to do next.</summary>
    internal enum StepOutcome
    {
        /// <summary>Carry on down the order.</summary>
        Continue = 0,

        /// <summary>
        /// Run the chain again from <see cref="ProcessingOrder.ReEntryPoint"/> once this pass has
        /// finished.
        /// </summary>
        /// <remarks>
        /// Only the equaliser returns this, and <see cref="Demodulator"/> refuses it from anything
        /// else. The specification gives one step a loop; a second step quietly acquiring one is
        /// the failure <c>REQ-DEM-001</c>'s declared order exists to prevent.
        /// </remarks>
        ReEnter,
    }

    /// <summary>One step of the chain.</summary>
    /// <remarks>
    /// <para>
    /// <strong><see cref="Step"/> is not decoration.</strong> <see cref="Demodulator"/> checks that
    /// each registered step's own answer matches the position it was registered at, so a handler
    /// cannot be wired into the wrong slot and quietly do the wrong work at the wrong time. That is
    /// the failure the declared order is there to make impossible, and it is the one a registry
    /// keyed by an enum would otherwise introduce.
    /// </para>
    /// <para>
    /// Internal for the reason <see cref="DemodContext"/> is: a step is defined by what it does to
    /// an intermediate whose invariants are positional.
    /// </para>
    /// </remarks>
    internal interface IChainStep
    {
        /// <summary>Which step of <c>REQ-DEM-001</c>'s order this is.</summary>
        DemodStep Step { get; }

        /// <summary>Runs the step.</summary>
        /// <param name="context">What the previous steps left.</param>
        /// <returns>Whether the chain carries on or re-enters.</returns>
        StepOutcome Run(DemodContext context);
    }
}
