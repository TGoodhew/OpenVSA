using System;

namespace OpenVSA.TestHarness
{
    /// <summary>
    /// A stimulus source with no instrument behind it, for exercising the harness itself.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Issue #393 asks for this explicitly: the harness's own logic — scenario sequencing, where
    /// the expectation comes from, how a failure is reported — stays under test in CI even though
    /// the measurements it makes there are not real.
    /// </para>
    /// <para>
    /// <strong>It can be told to lie.</strong> <see cref="CoerceFrequencyTo"/> makes it report a
    /// different carrier from the one it was asked for, which is what a real generator does at the
    /// edge of its range. A harness that took its expectation from the request rather than from
    /// the read-back would pass that and should not.
    /// </para>
    /// </remarks>
    public sealed class SimulatedStimulus : IStimulusSource, IMultitoneStimulus, INoiseStimulus
    {
        /// <summary>Reports this frequency whatever it is asked for; <c>NaN</c> to obey.</summary>
        public double CoerceFrequencyTo { get; set; } = double.NaN;

        /// <summary>Reports this level whatever it is asked for; <c>NaN</c> to obey.</summary>
        public double CoerceLevelTo { get; set; } = double.NaN;

        /// <inheritdoc />
        public string DisplayName => "Simulated stimulus";

        /// <inheritdoc />
        public bool IsOutputEnabled { get; private set; }

        /// <inheritdoc />
        public double FrequencyHz { get; private set; }

        /// <inheritdoc />
        public double LevelDbm { get; private set; }

        /// <summary>Whether <see cref="Connect"/> has been called.</summary>
        public bool IsConnected { get; private set; }

        /// <inheritdoc />
        public void Connect() => IsConnected = true;

        /// <inheritdoc />
        public void SetContinuousWave(double frequencyHz, double levelDbm)
        {
            FrequencyHz = double.IsNaN(CoerceFrequencyTo) ? frequencyHz : CoerceFrequencyTo;
            LevelDbm = double.IsNaN(CoerceLevelTo) ? levelDbm : CoerceLevelTo;

            // A carrier is not a comb. Leaving the count set would let a CW scenario run after a
            // multitone one and still read back tones, which is the stale-state failure the real
            // source avoids by reading MTONe:ARB:STATe.
            ToneCount = 0;
            ToneSpacingHz = 0.0;
            NoiseBandwidthHz = 0.0;
        }

        /// <inheritdoc />
        public int MinimumTones => 2;

        /// <inheritdoc />
        public int MaximumTones => 64;

        /// <inheritdoc />
        public int ToneCount { get; private set; }

        /// <inheritdoc />
        public double ToneSpacingHz { get; private set; }

        /// <summary>Reports this spacing whatever it is asked for; <c>NaN</c> to obey.</summary>
        /// <remarks>
        /// The comb's counterpart to <see cref="CoerceFrequencyTo"/>. A real generator quantises the
        /// spacing to its sample clock, and a harness taking its expectation from the request rather
        /// than the read-back would report the analyser as wrong by the difference.
        /// </remarks>
        public double CoerceSpacingTo { get; set; } = double.NaN;

        /// <inheritdoc />
        public void SetMultitone(
            double centreFrequencyHz, int toneCount, double spacingHz, double levelDbm)
        {
            if (toneCount < MinimumTones || toneCount > MaximumTones)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(toneCount), toneCount,
                    "This source produces between " + MinimumTones + " and " + MaximumTones +
                    " tones.");
            }

            FrequencyHz = double.IsNaN(CoerceFrequencyTo) ? centreFrequencyHz : CoerceFrequencyTo;
            LevelDbm = double.IsNaN(CoerceLevelTo) ? levelDbm : CoerceLevelTo;
            ToneSpacingHz = double.IsNaN(CoerceSpacingTo) ? spacingHz : CoerceSpacingTo;
            ToneCount = toneCount;
            NoiseBandwidthHz = 0.0;
        }

        /// <inheritdoc />
        public double MinimumNoiseBandwidthHz => 50e3;

        /// <inheritdoc />
        public double MaximumNoiseBandwidthHz => 15e6;

        /// <inheritdoc />
        public double NoiseBandwidthHz { get; private set; }

        /// <summary>Reports this noise bandwidth whatever it is asked for; <c>NaN</c> to obey.</summary>
        public double CoerceNoiseBandwidthTo { get; set; } = double.NaN;

        /// <inheritdoc />
        public void SetNoise(double centreFrequencyHz, double bandwidthHz, double levelDbm)
        {
            if (bandwidthHz < MinimumNoiseBandwidthHz || bandwidthHz > MaximumNoiseBandwidthHz)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(bandwidthHz), bandwidthHz,
                    "This source produces noise between " + MinimumNoiseBandwidthHz + " and " +
                    MaximumNoiseBandwidthHz + " Hz wide.");
            }

            FrequencyHz = double.IsNaN(CoerceFrequencyTo) ? centreFrequencyHz : CoerceFrequencyTo;
            LevelDbm = double.IsNaN(CoerceLevelTo) ? levelDbm : CoerceLevelTo;

            NoiseBandwidthHz = double.IsNaN(CoerceNoiseBandwidthTo)
                ? bandwidthHz
                : CoerceNoiseBandwidthTo;

            // One personality at a time, as on the real source.
            ToneCount = 0;
            ToneSpacingHz = 0.0;
        }

        /// <inheritdoc />
        public void SetOutput(bool enabled) => IsOutputEnabled = enabled;

        /// <inheritdoc />
        public void Refresh()
        {
        }

        /// <inheritdoc />
        public void Dispose() => IsOutputEnabled = false;
    }
}
