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
    /// <para>
    /// <strong>Its limits and quantisation are the real E4438C's, measured rather than invented</strong>
    /// (2026-08-08, firmware C.05.85, Option 503). A simulator that accepts anything and reports it
    /// back unchanged is not a stand-in for an instrument, it is a stand-in for a wish: harness
    /// logic that must cope with a coerced read-back would never be exercised by it, and the first
    /// time it met real hardware would be the first time it was tested.
    /// </para>
    /// <list type="bullet">
    /// <item><description>Frequency <strong>100 kHz to 3 GHz</strong>, honoured exactly inside that
    /// (1 000 100 003 Hz was read back to the hertz).</description></item>
    /// <item><description>Level <strong>−136 to +20 dBm</strong>, quantised to <strong>0.02 dB
    /// </strong>. Asked for −13.774, −13.775 and −13.7749 the instrument answered −13.78 to all
    /// three; asked for −13.77, exactly between two steps, it answered −13.76.</description></item>
    /// <item><description>Out of range values are <strong>CLIPPED, never refused</strong>, with
    /// <c>-222 "Data out of range;value clipped to …"</c> left in the error queue. 7 GHz became
    /// 3 GHz and +40 dBm became +20 dBm.</description></item>
    /// <item><description>Tone spacing and noise bandwidth are <strong>not</strong> quantised —
    /// 137 kHz and 1 234 567 Hz both came back exactly. The 996 093.75 Hz seen in a comb scenario
    /// is the <em>analyser's</em> bin resolution, not the generator's.</description></item>
    /// </list>
    /// </remarks>
    [StimulusProvider("Simulated source (no instrument)")]
    public sealed class SimulatedStimulus : IStimulusSource, IMultitoneStimulus, INoiseStimulus,
        IStimulusLimits
    {
        /// <summary>Lowest carrier the modelled instrument produces, in hertz.</summary>
        /// <remarks>From the instrument's own <c>:FREQuency:CW? MIN</c>, not from the data sheet.</remarks>
        public const double MinimumFrequencyHz = 100e3;

        /// <summary>Highest carrier, in hertz. Option 503 tops out at 3 GHz.</summary>
        public const double MaximumFrequencyHz = 3e9;

        /// <summary>Lowest output level, in dBm, from <c>:POWer:AMPLitude? MIN</c>.</summary>
        public const double MinimumLevelDbm = -136.0;

        /// <summary>Highest output level, in dBm.</summary>
        public const double MaximumLevelDbm = 20.0;

        /// <summary>Level resolution, in decibels.</summary>
        /// <remarks>
        /// Measured, not assumed: −13.774, −13.775 and −13.7749 all read back as −13.78, and −13.77
        /// — exactly between two steps — read back as −13.76.
        /// </remarks>
        public const double LevelStepDb = 0.02;

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
            FrequencyHz = double.IsNaN(CoerceFrequencyTo) ? Carrier(frequencyHz) : CoerceFrequencyTo;
            LevelDbm = double.IsNaN(CoerceLevelTo) ? Level(levelDbm) : CoerceLevelTo;

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
        /// The comb's counterpart to <see cref="CoerceFrequencyTo"/>. <strong>The E4438C does not in
        /// fact quantise the spacing</strong> — 137 kHz came back as 137 kHz — so this exists to
        /// exercise the harness against a generator that might, not to imitate one that does. The
        /// note that used to be here said otherwise and was wrong; the 996 093.75 Hz that prompted
        /// it is the analyser's bin resolution.
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

            FrequencyHz = double.IsNaN(CoerceFrequencyTo) ? Carrier(centreFrequencyHz) : CoerceFrequencyTo;
            LevelDbm = double.IsNaN(CoerceLevelTo) ? Level(levelDbm) : CoerceLevelTo;
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

            FrequencyHz = double.IsNaN(CoerceFrequencyTo) ? Carrier(centreFrequencyHz) : CoerceFrequencyTo;
            LevelDbm = double.IsNaN(CoerceLevelTo) ? Level(levelDbm) : CoerceLevelTo;

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
        /// <remarks>
        /// The measured limits above, reported through the same interface the real source reports
        /// its probed ones through. That is what makes this a stand-in: a panel ranged from this
        /// offers the same frequency and level range the instrument would have given it, so the
        /// path that ranges a control is exercised with no hardware rather than skipped.
        /// </remarks>
        public StimulusLimits ReadLimits() =>
            new StimulusLimits(
                MinimumFrequencyHz, MaximumFrequencyHz, MinimumLevelDbm, MaximumLevelDbm);

        /// <inheritdoc />
        public void Dispose() => IsOutputEnabled = false;

        /// <summary>
        /// The carrier the modelled instrument would settle on, clipped rather than refused.
        /// </summary>
        /// <param name="requestedHz">What was asked for, in hertz.</param>
        /// <remarks>
        /// <strong>Clipped, because that is what the instrument does.</strong> Asked for 7 GHz it
        /// answers 3 GHz and queues <c>-222 "Data out of range;value clipped to upper limit."</c>;
        /// it does not refuse and it does not stop. A simulator that threw here would let a harness
        /// that cannot cope with clipping pass in CI and fail on the bench.
        /// </remarks>
        public static double Carrier(double requestedHz) =>
            Math.Min(MaximumFrequencyHz, Math.Max(MinimumFrequencyHz, requestedHz));

        /// <summary>
        /// The level the modelled instrument would settle on: clipped, then quantised.
        /// </summary>
        /// <param name="requestedDbm">What was asked for, in dBm.</param>
        /// <remarks>
        /// <strong>The tie goes upward.</strong> −13.77 sits exactly between −13.76 and −13.78 and
        /// the instrument answers −13.76, so the rounding is away from negative infinity rather
        /// than to even. <see cref="Math.Round(double, MidpointRounding)"/> with
        /// <see cref="MidpointRounding.AwayFromZero"/> would go the other way for a negative level,
        /// which is why this is written as a floor of the shifted value rather than as a Round.
        /// </remarks>
        public static double Level(double requestedDbm)
        {
            double clipped = Math.Min(MaximumLevelDbm, Math.Max(MinimumLevelDbm, requestedDbm));
            double steps = Math.Floor((clipped / LevelStepDb) + 0.5);

            // Back through the step size and rounded to the step's own precision, so the answer is
            // -13.76 rather than -13.760000000000002 and a test can compare it as a number.
            return Math.Round(steps * LevelStepDb, 10);
        }
    }
}
