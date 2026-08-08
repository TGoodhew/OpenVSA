using System;

namespace OpenVSA.TestHarness
{
    /// <summary>
    /// A stimulus source that can also produce band-limited noise of known total power (#393).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The one stimulus a tone cannot stand in for.</strong> Every tone scenario measures a
    /// peak, and a peak is one bin: the equivalent noise bandwidth of the analysis window divides
    /// out of the answer and is never exercised. Noise is spread over every bin, so the reading
    /// depends on <c>REQ-DSP-011</c>'s ENBW correction directly — get that wrong by the flat top's
    /// 3.8194 and every tone in the product still reads correctly while every noise-marker and
    /// channel-power figure is out by 5.8 dB.
    /// </para>
    /// <para>
    /// <strong>Separate from <see cref="IMultitoneStimulus"/> for the same reason that is separate
    /// from <see cref="IStimulusSource"/>.</strong> Noise is Option 403 on the E4438C and multitone
    /// is Option 001/601 or 002/602; an instrument may have either, both or neither, and a scenario
    /// finds out by asking rather than by failing part way through a bench run.
    /// </para>
    /// </remarks>
    public interface INoiseStimulus
    {
        /// <summary>Narrowest noise bandwidth the source will produce, in hertz.</summary>
        double MinimumNoiseBandwidthHz { get; }

        /// <summary>Widest noise bandwidth the source will produce, in hertz.</summary>
        double MaximumNoiseBandwidthHz { get; }

        /// <summary>Noise bandwidth in force, in hertz, as the source reports it. Zero when off.</summary>
        double NoiseBandwidthHz { get; }

        /// <summary>
        /// Sets band-limited noise of a stated total power.
        /// </summary>
        /// <param name="centreFrequencyHz">Centre of the noise band, in hertz.</param>
        /// <param name="bandwidthHz">Noise bandwidth, in hertz.</param>
        /// <param name="levelDbm">Total power in the band, in dBm.</param>
        /// <exception cref="ArgumentOutOfRangeException">
        /// <paramref name="bandwidthHz"/> is outside what the source supports.
        /// </exception>
        /// <remarks>
        /// <strong><paramref name="levelDbm"/> is the total in the band, not a density.</strong>
        /// The expected density follows as <c>levelDbm - 10·log10(bandwidthHz)</c>, and that
        /// subtraction is the whole reference the measurement is checked against — which is why the
        /// bandwidth is read back from the source afterwards rather than assumed to be the one
        /// asked for.
        /// </remarks>
        void SetNoise(double centreFrequencyHz, double bandwidthHz, double levelDbm);
    }
}
