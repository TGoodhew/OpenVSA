using System;
using System.Collections.Generic;

namespace OpenVSA.TestHarness
{
    /// <summary>Which pulse-shaping filter a generator applies before modulating.</summary>
    /// <remarks>
    /// The subset of the E4438C's Custom filter list that means something to a demodulator built to
    /// <c>REQ-DEM-020</c>. The instrument also offers the IS-95 family, the APCO C4FM filter, a
    /// backwards-compatible GSM Gaussian and a user FIR; those arrive with the requirements that
    /// need them, because a filter nothing can demodulate is a setting nothing can check.
    /// </remarks>
    public enum StimulusPulseFilter
    {
        /// <summary>
        /// Root raised cosine: the transmitter's half of a Nyquist pair, which the analyser's
        /// matched filter completes (<c>RNYQuist</c>).
        /// </summary>
        RootRaisedCosine = 0,

        /// <summary>
        /// Raised cosine: the whole Nyquist filter at the transmitter, so the receiver applies none
        /// (<c>NYQuist</c>).
        /// </summary>
        RaisedCosine,

        /// <summary>Gaussian, for the GMSK family (<c>GAUSsian</c>).</summary>
        Gaussian,

        /// <summary>
        /// Rectangular: no shaping of the symbol stream at all (<c>RECTangle</c>).
        /// </summary>
        /// <remarks>
        /// <para>
        /// <strong>The one that matters for MSK, and it matters because this generator's filter is
        /// a PRE-modulation filter.</strong> The instrument's manual is explicit: the Custom
        /// subsystem's filter "selects the pre-modulation filter type", so for a constant-envelope
        /// format it shapes the frequency path rather than the envelope. MSK through a rectangular
        /// pre-modulation filter is MSK — a rectangular frequency pulse is what makes the phase
        /// advance linearly across a symbol — and MSK through a Gaussian one is GMSK.
        /// </para>
        /// <para>
        /// So the two formats this analyser distinguishes as MSK and GMSK are one modulation type
        /// and two filters on this generator, which is worth knowing before looking for a GMSK
        /// entry in its format list. There is not one.
        /// </para>
        /// </remarks>
        Rectangular,
    }

    /// <summary>
    /// A stimulus source that can also produce a digitally modulated carrier
    /// (<c>REQ-E44-007</c>, <c>REQ-SIM-001</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The one stimulus a synthetic signal cannot stand in for.</strong> Everything the
    /// demodulator does is proved against signals OpenVSA generated itself, which leaves every
    /// <em>convention</em> in it unverified: what α means, whether the transmit pulse is the root or
    /// the whole Nyquist filter, which bit maps to which constellation point, which way round the
    /// spectrum is, and what EVM is normalised to. A real transmitter settles all of them, and it
    /// settles them the way a customer's transmitter will. This class of error has already cost
    /// 10 % EVM once inside OpenVSA, when a generator shaping with a full raised cosine met a
    /// demodulator applying the matched root.
    /// </para>
    /// <para>
    /// <strong>Separate from <see cref="IStimulusSource"/>, for the reason
    /// <see cref="IMultitoneStimulus"/> and <see cref="INoiseStimulus"/> are.</strong> Digital
    /// modulation is the E4438C's Option 001/601 or 002/602 — the internal baseband generator — and
    /// an instrument may have it or not. A scenario asks rather than discovering half way through a
    /// bench run that the carrier it is measuring was never modulated.
    /// </para>
    /// <para>
    /// <strong>The data pattern is part of the contract, and that is the point.</strong> A PN
    /// sequence is reproducible outside the instrument, so "the recovered bits are the transmitted
    /// bits" becomes a comparison against something independently known rather than a demodulator
    /// agreeing with itself. It is the strongest claim this interface makes available.
    /// </para>
    /// </remarks>
    public interface IDigitalModulationStimulus
    {
        /// <summary>The modulation formats this source can produce, by name.</summary>
        /// <remarks>
        /// The instrument's own names, as its manual and its front panel use them, rather than
        /// OpenVSA's: a scenario that asked for "QPSK" and got something else because two catalogues
        /// spell a format differently would be a scenario measuring the wrong signal. The mapping
        /// between these and <c>REQ-DEM-010</c>'s catalogue belongs to whatever compares them.
        /// </remarks>
        IReadOnlyList<string> Formats { get; }

        /// <summary>The data patterns this source can transmit, by name.</summary>
        IReadOnlyList<string> DataPatterns { get; }

        /// <summary>The modulation in force, as the source reports it, or <c>null</c> when off.</summary>
        string Format { get; }

        /// <summary>The symbol rate in force, in symbols per second. Zero when off.</summary>
        double SymbolRateHz { get; }

        /// <summary>The pulse-shaping filter in force.</summary>
        StimulusPulseFilter PulseFilter { get; }

        /// <summary>The filter's roll-off in force, where the filter has one.</summary>
        double Alpha { get; }

        /// <summary>The data pattern in force, as the source reports it.</summary>
        string DataPattern { get; }

        /// <summary>Whether the modulated spectrum is inverted.</summary>
        /// <remarks>
        /// A generator that can invert its own I/Q is how <c>REQ-DEM-035</c>'s mirror-frequency
        /// handling is tested against something other than an assertion about a sign in the code.
        /// </remarks>
        bool IsSpectrumInverted { get; }

        /// <summary>
        /// The fastest symbol rate this source will produce for a filter.
        /// </summary>
        /// <param name="filter">The pulse-shaping filter.</param>
        /// <returns>The maximum symbol rate, in symbols per second.</returns>
        /// <remarks>
        /// Filter-dependent because it is: the instrument shortens its filter to reach higher symbol
        /// rates and will not shorten below a minimum length, so the ceiling is a property of the
        /// pair rather than of the instrument.
        /// </remarks>
        double MaximumSymbolRateHz(StimulusPulseFilter filter);

        /// <summary>The slowest symbol rate this source will produce.</summary>
        double MinimumSymbolRateHz { get; }

        /// <summary>
        /// Sets a digitally modulated carrier.
        /// </summary>
        /// <param name="frequencyHz">Carrier frequency, in hertz.</param>
        /// <param name="levelDbm">Output level, in dBm.</param>
        /// <param name="format">One of <see cref="Formats"/>.</param>
        /// <param name="symbolRateHz">Symbol rate, in symbols per second.</param>
        /// <param name="filter">The pulse-shaping filter.</param>
        /// <param name="alpha">The filter's roll-off, where it has one.</param>
        /// <param name="dataPattern">One of <see cref="DataPatterns"/>.</param>
        /// <exception cref="ArgumentException">
        /// The format or the data pattern is not one this source offers.
        /// </exception>
        /// <exception cref="ArgumentOutOfRangeException">
        /// The symbol rate or the roll-off is outside what this source supports.
        /// </exception>
        /// <remarks>
        /// Everything is read back from the source afterwards rather than assumed, because an
        /// instrument coerces: the expectation a scenario checks against has to be what the
        /// generator says it produced, not what it was asked for.
        /// </remarks>
        void SetDigitalModulation(
            double frequencyHz,
            double levelDbm,
            string format,
            double symbolRateHz,
            StimulusPulseFilter filter,
            double alpha,
            string dataPattern);

        /// <summary>Inverts or restores the modulated spectrum (<c>REQ-DEM-035</c>).</summary>
        /// <param name="inverted">Whether to invert.</param>
        void SetSpectrumInverted(bool inverted);

        /// <summary>Turns the modulation off, leaving the carrier as it was.</summary>
        void StopDigitalModulation();
    }
}
