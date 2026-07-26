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
            bool outputEnabled = true)
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
        }

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
        public string Units => What == VerifiedQuantity.PeakLevelDbm ? "dB" : "Hz";

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

                default:
                    throw new ArgumentOutOfRangeException(nameof(What), What, "Unknown quantity.");
            }
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
            };
        }
    }
}
