using System;

namespace OpenVSA.TestHarness
{
    /// <summary>
    /// A stimulus source that can also produce a comb of equal tones (issue #393).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>A separate interface, not more members on <see cref="IStimulusSource"/>.</strong>
    /// Multitone is an option on the generator this bench happens to have — the E4438C needs
    /// Option 001/601 or 002/602 for it — and a source without it is still a perfectly good source
    /// for every scenario that wants a carrier. Widening the base interface would make every future
    /// generator claim a capability it may not have, and the claim would only be found to be false
    /// at the moment a scenario ran. Asking for the capability is how a scenario finds out before
    /// it starts.
    /// </para>
    /// <para>
    /// <strong>The limits are the instrument's, not this file's.</strong>
    /// <see cref="MinimumTones"/> and <see cref="MaximumTones"/> are read from the generator, in the
    /// same way the measurement side derives everything from <c>IFrontEndCapabilities</c> rather
    /// than hard-coding what a particular model can do.
    /// </para>
    /// <para>
    /// <strong>Why a comb is worth measuring at all.</strong> A single tone can be right while the
    /// spectrum is wrong: decimation that drops the wrong bin, a window correction applied per
    /// trace rather than per bin, and an amplitude chain that is level-dependent all agree with a
    /// one-tone test. Several equal tones at a known spacing disagree with all three, and the
    /// disagreement is quantitative.
    /// </para>
    /// </remarks>
    public interface IMultitoneStimulus
    {
        /// <summary>Fewest tones the source will produce.</summary>
        int MinimumTones { get; }

        /// <summary>Most tones the source will produce.</summary>
        int MaximumTones { get; }

        /// <summary>Tones currently configured, or zero when the comb is off.</summary>
        int ToneCount { get; }

        /// <summary>Spacing between adjacent tones, in hertz, as the source reports it.</summary>
        double ToneSpacingHz { get; }

        /// <summary>
        /// Sets a comb of equal tones centred on the carrier.
        /// </summary>
        /// <param name="centreFrequencyHz">Carrier the comb is centred on, in hertz.</param>
        /// <param name="toneCount">How many tones; between the two limits above.</param>
        /// <param name="spacingHz">Spacing between adjacent tones, in hertz.</param>
        /// <param name="levelDbm">Total output level of the comb, in dBm.</param>
        /// <exception cref="ArgumentOutOfRangeException">
        /// <paramref name="toneCount"/> is outside what the source supports.
        /// </exception>
        /// <remarks>
        /// <strong>The level is the comb's total, not each tone's.</strong> The generator sets its
        /// output power for the whole waveform, so each of <paramref name="toneCount"/> equal tones
        /// carries that power divided between them — about
        /// <c>levelDbm - 10·log10(toneCount)</c> each. A scenario checking absolute tone level has
        /// to account for that; one checking spacing or flatness does not, which is why those are
        /// the two the default catalogue checks.
        /// </remarks>
        void SetMultitone(
            double centreFrequencyHz, int toneCount, double spacingHz, double levelDbm);
    }
}
