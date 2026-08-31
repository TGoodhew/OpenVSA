namespace OpenVSA.Demod.Chain
{
    /// <summary>
    /// What the equaliser does with its coefficients from one measurement to the next
    /// (<c>REQ-DEM-051</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The mode is about measurements, not about passes.</strong> Within one measurement the
    /// equaliser may fit several times — that is <c>REQ-DEM-050</c>'s re-entry loop, and it is
    /// governed by <see cref="DemodSettings.MaxPasses"/>. What this chooses is whether the
    /// coefficients a measurement finishes with are carried into the next one, frozen, or thrown
    /// away, which is the distinction the reference product's Run/Hold/Reset controls make.
    /// </para>
    /// <para>
    /// <strong>The carrier of that memory is <see cref="EqualiserState"/>.</strong> A
    /// <see cref="DemodSettings"/> is built afresh for each measurement, so a mode that has to
    /// remember something cannot remember it there; the state object is created once by whoever owns
    /// the measurement and handed to every settings object it builds.
    /// </para>
    /// </remarks>
    public enum EqualiserMode
    {
        /// <summary>
        /// Fit from the current measurement and carry the result into the next (the default).
        /// </summary>
        /// <remarks>
        /// The coefficients change between successive measurements, because each measurement fits
        /// its own. What it inherits from the last one is the standard the new fit has to beat: a
        /// measurement whose own fit is worse than the filter it was handed keeps the filter it was
        /// handed, so a bad block cannot undo a good one.
        /// </remarks>
        Run = 0,

        /// <summary>Freeze the coefficients: apply them, and fit nothing.</summary>
        /// <remarks>
        /// Bit-identical coefficients across measurements, which is <c>REQ-DEM-051</c>'s criterion
        /// for this mode. Held coefficients are applied to every measurement all the same — Hold
        /// freezes the equaliser, it does not switch it off; <see cref="DemodSettings.EqualiserEnabled"/>
        /// does that.
        /// </remarks>
        Hold = 1,

        /// <summary>Return to a unit impulse: an equaliser that does nothing.</summary>
        /// <remarks>
        /// The impulse sits at <see cref="DemodSettings.EqualiserImpulseIndex"/>, so a Reset filter
        /// convolved with the waveform gives the waveform back unchanged. Reset is a state rather
        /// than an action here: while it is selected every measurement starts and ends at the unit
        /// impulse, and selecting Run afterwards begins adapting again from nothing.
        /// </remarks>
        Reset = 2,
    }
}
