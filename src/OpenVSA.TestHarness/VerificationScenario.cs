using System;
using System.Collections.Generic;
using System.Globalization;

namespace OpenVSA.TestHarness
{
    /// <summary>What a scenario checks about a measured spectrum.</summary>
    public enum VerifiedQuantity
    {
        /// <summary>Frequency of the largest peak, in hertz.</summary>
        PeakFrequencyHz = 0,

        /// <summary>Level of the largest peak, in dBm.</summary>
        PeakLevelDbm,

        /// <summary>Offset of the largest peak from the analysis centre frequency, in hertz.</summary>
        PeakOffsetHz,

        /// <summary>How many tones of a comb were found, as a count.</summary>
        /// <remarks>
        /// Checked separately from the spacing because the two fail differently: a comb with a tone
        /// missing has the right spacing among the ones that remain, and a spacing check alone
        /// would pass it.
        /// </remarks>
        ToneCount,

        /// <summary>Mean spacing between adjacent tones of a comb, in hertz.</summary>
        ToneSpacingHz,

        /// <summary>
        /// Spread between the strongest and weakest tone of a comb, in decibels.
        /// </summary>
        /// <remarks>
        /// The tones are generated equal, so the expectation is zero and the measurement is
        /// entirely OpenVSA's amplitude behaviour across the span. A window correction applied per
        /// trace rather than per bin, or decimation that keeps the wrong sample, shows here and
        /// nowhere in a one-tone test.
        /// </remarks>
        ToneFlatnessDb,
    }

    /// <summary>
    /// One end-to-end check: a stimulus state, a measurement setup, and what the reading must be.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The point of a scenario is that its expectation comes from the <em>generator</em>, not from
    /// OpenVSA. Everything OpenVSA can check about itself is a closed loop against its own DSP; a
    /// scenario is the only kind of test that can catch an amplitude chain out by √2, a spectrum
    /// mirrored about centre, or a window correction applied to the wrong trace type.
    /// </para>
    /// <para>
    /// <strong>Tolerances are stated per scenario, not shared.</strong> A frequency check on a
    /// 29 kHz bin and a level check through an uncalibrated cable have nothing to say to one
    /// another, and one tolerance covering both would be loose enough to pass a real defect.
    /// </para>
    /// </remarks>
    public sealed class VerificationScenario
    {
        /// <summary>Creates a scenario.</summary>
        /// <param name="name">Short name, used in the report.</param>
        /// <param name="what">Which quantity is checked.</param>
        /// <param name="stimulusFrequencyHz">Carrier the generator is set to, in hertz.</param>
        /// <param name="stimulusLevelDbm">Level the generator is set to, in dBm.</param>
        /// <param name="centerFrequencyHz">Analysis centre frequency, in hertz.</param>
        /// <param name="spanHz">Analysis span, in hertz.</param>
        /// <param name="frequencyPoints">Displayed points to ask for.</param>
        /// <param name="tolerance">Allowed departure, in the quantity's own units.</param>
        /// <param name="outputEnabled">Whether the generator's RF output is on.</param>
        /// <exception cref="ArgumentException"><paramref name="name"/> is missing.</exception>
        /// <exception cref="ArgumentOutOfRangeException">A value is out of range.</exception>
        public VerificationScenario(
            string name,
            VerifiedQuantity what,
            double stimulusFrequencyHz,
            double stimulusLevelDbm,
            double centerFrequencyHz,
            double spanHz,
            int frequencyPoints,
            double tolerance,
            bool outputEnabled = true,
            int toneCount = 0,
            double toneSpacingHz = 0.0)
        {
            if (string.IsNullOrEmpty(name))
            {
                throw new ArgumentException("A scenario needs a name.", nameof(name));
            }

            if (!(spanHz > 0.0))
            {
                throw new ArgumentOutOfRangeException(nameof(spanHz), spanHz, "Span must be positive.");
            }

            if (!(tolerance > 0.0))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(tolerance), tolerance, "A tolerance must be positive; a scenario that " +
                    "allows no departure at all cannot pass against real hardware.");
            }

            Name = name;
            What = what;
            StimulusFrequencyHz = stimulusFrequencyHz;
            StimulusLevelDbm = stimulusLevelDbm;
            CenterFrequencyHz = centerFrequencyHz;
            SpanHz = spanHz;
            FrequencyPoints = frequencyPoints;
            Tolerance = tolerance;
            OutputEnabled = outputEnabled;
            RequestedToneCount = toneCount;
            RequestedToneSpacingHz = toneSpacingHz;
        }

        /// <summary>
        /// Tones the generator is asked for, or zero for an unmodulated carrier.
        /// </summary>
        /// <remarks>
        /// What makes a scenario a comb scenario. Zero is not "two by default": a scenario that did
        /// not ask for a comb must get a carrier, because the two need different things of the
        /// generator and a silent default would produce whichever the last scenario left behind.
        /// </remarks>
        public int RequestedToneCount { get; }

        /// <summary>Spacing the generator is asked for, in hertz.</summary>
        public double RequestedToneSpacingHz { get; }

        /// <summary>Whether this scenario needs a source that can produce a comb.</summary>
        public bool NeedsMultitone => RequestedToneCount >= 2;

        /// <summary>Short name, used in the report.</summary>
        public string Name { get; }

        /// <summary>Which quantity is checked.</summary>
        public VerifiedQuantity What { get; }

        /// <summary>Carrier the generator is set to, in hertz.</summary>
        public double StimulusFrequencyHz { get; }

        /// <summary>Level the generator is set to, in dBm.</summary>
        public double StimulusLevelDbm { get; }

        /// <summary>Analysis centre frequency, in hertz.</summary>
        public double CenterFrequencyHz { get; }

        /// <summary>Analysis span, in hertz.</summary>
        public double SpanHz { get; }

        /// <summary>Displayed points to ask for.</summary>
        public int FrequencyPoints { get; }

        /// <summary>Allowed departure from the expected value, in the quantity's own units.</summary>
        public double Tolerance { get; }

        /// <summary>Whether the generator's RF output is on.</summary>
        public bool OutputEnabled { get; }

        /// <summary>The units the quantity and its tolerance are in.</summary>
        public string Units
        {
            get
            {
                switch (What)
                {
                    case VerifiedQuantity.PeakLevelDbm:
                    case VerifiedQuantity.ToneFlatnessDb:
                        return "dB";

                    case VerifiedQuantity.ToneCount:
                        return "tones";

                    default:
                        return "Hz";
                }
            }
        }

        /// <summary>
        /// The expected reading, taken from what the generator says it is doing.
        /// </summary>
        /// <param name="source">The stimulus source, already refreshed.</param>
        /// <exception cref="ArgumentNullException"><paramref name="source"/> is null.</exception>
        /// <remarks>
        /// From the source's own read-back, not from this scenario's request. A generator that
        /// coerced the frequency it was given — or that somebody retuned between runs — must move
        /// the expectation with it, or the harness is checking the analyser against a wish.
        /// </remarks>
        public double ExpectedFrom(IStimulusSource source)
        {
            if (source == null)
            {
                throw new ArgumentNullException(nameof(source));
            }

            switch (What)
            {
                case VerifiedQuantity.PeakFrequencyHz:
                    return source.FrequencyHz;

                case VerifiedQuantity.PeakOffsetHz:
                    return source.FrequencyHz - CenterFrequencyHz;

                case VerifiedQuantity.PeakLevelDbm:
                    return source.LevelDbm;

                case VerifiedQuantity.ToneCount:
                    return Comb(source).ToneCount;

                case VerifiedQuantity.ToneSpacingHz:
                    return Comb(source).ToneSpacingHz;

                // The tones are generated equal, so any spread is the measurement's. Nothing is
                // read back from the generator here because there is nothing it could say: a
                // source that reported its own flatness would be marking its own work.
                case VerifiedQuantity.ToneFlatnessDb:
                    return 0.0;

                default:
                    throw new ArgumentOutOfRangeException(nameof(What), What, "Unknown quantity.");
            }
        }

        private static IMultitoneStimulus Comb(IStimulusSource source)
        {
            var comb = source as IMultitoneStimulus;

            if (comb == null)
            {
                throw new InvalidOperationException(
                    "This scenario checks a multitone comb and '" + source.DisplayName +
                    "' cannot produce one.");
            }

            return comb;
        }

        /// <inheritdoc />
        public override string ToString() =>
            Name + " (" + What + ", " +
            Tolerance.ToString("G4", CultureInfo.CurrentCulture) + " " + Units + ")";

        /// <summary>
        /// The scenarios worth having from the start.
        /// </summary>
        /// <param name="centerFrequencyHz">Centre frequency to measure at.</param>
        /// <param name="levelDbm">Generator level to use.</param>
        /// <returns>The default catalogue.</returns>
        /// <remarks>
        /// <para>
        /// Each catches something the simulator structurally cannot:
        /// </para>
        /// <list type="bullet">
        /// <item><description><strong>Level</strong> — the whole correction chain end to end, which
        /// is where a factor of √2 or of 2 hides.</description></item>
        /// <item><description><strong>Frequency, and its sign</strong> — a spectrum mirrored about
        /// centre is self-consistent everywhere inside OpenVSA. The offset is deliberately
        /// asymmetric so that a mirror produces the wrong answer rather than the same
        /// one.</description></item>
        /// <item><description><strong>Both signs of offset</strong> — a mirror that happens to be
        /// tested only on one side passes.</description></item>
        /// </list>
        /// </remarks>
        public static IReadOnlyList<VerificationScenario> Default(
            double centerFrequencyHz = 1.0e9, double levelDbm = -20.0)
        {
            return new[]
            {
                new VerificationScenario(
                    "Level of a centred tone", VerifiedQuantity.PeakLevelDbm,
                    centerFrequencyHz, levelDbm, centerFrequencyHz, 10e6, 801, 2.0),

                new VerificationScenario(
                    "Frequency of a tone above centre", VerifiedQuantity.PeakFrequencyHz,
                    centerFrequencyHz + 3.1e6, levelDbm, centerFrequencyHz, 10e6, 801, 60e3),

                new VerificationScenario(
                    "Frequency of a tone below centre", VerifiedQuantity.PeakFrequencyHz,
                    centerFrequencyHz - 2.3e6, levelDbm, centerFrequencyHz, 10e6, 801, 60e3),

                new VerificationScenario(
                    "Offset sign, above centre", VerifiedQuantity.PeakOffsetHz,
                    centerFrequencyHz + 3.1e6, levelDbm, centerFrequencyHz, 10e6, 801, 60e3),

                new VerificationScenario(
                    "Offset sign, below centre", VerifiedQuantity.PeakOffsetHz,
                    centerFrequencyHz - 2.3e6, levelDbm, centerFrequencyHz, 10e6, 801, 60e3),

                new VerificationScenario(
                    "Level at a narrower span", VerifiedQuantity.PeakLevelDbm,
                    centerFrequencyHz, levelDbm, centerFrequencyHz, 2e6, 801, 2.0),

                // Five tones, 1 MHz apart, across a 10 MHz span: the comb spans 4 MHz and sits
                // clear of both edges, and an odd count puts one tone on the carrier so a comb
                // shifted by half a spacing fails rather than looking symmetrical.
                new VerificationScenario(
                    "Every tone of a comb is present", VerifiedQuantity.ToneCount,
                    centerFrequencyHz, levelDbm, centerFrequencyHz, 10e6, 801, 0.5,
                    true, 5, 1e6),

                new VerificationScenario(
                    "Tone spacing across the comb", VerifiedQuantity.ToneSpacingHz,
                    centerFrequencyHz, levelDbm, centerFrequencyHz, 10e6, 801, 60e3,
                    true, 5, 1e6),

                new VerificationScenario(
                    "The comb reads flat", VerifiedQuantity.ToneFlatnessDb,
                    centerFrequencyHz, levelDbm, centerFrequencyHz, 10e6, 801, 3.0,
                    true, 5, 1e6),
            };
        }
    }
}
