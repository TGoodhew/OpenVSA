using System;
using System.Collections.Generic;
using OpenVSA.Core;
using OpenVSA.Dsp.Spectrum;
using OpenVSA.Personality;

namespace OpenVSA.TestHarness
{
    /// <summary>
    /// A measurement personality that reports a block's total power, used to exercise
    /// <c>REQ-ARC-003</c> against a real acquisition.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Why a personality needs bench evidence at all.</strong> The requirement is that a
    /// personality consumes an acquired block rather than a spectrum. Everything about that claim
    /// can be asserted headlessly except the one thing that matters: that the samples a personality
    /// is handed by a *real* front end are the calibrated ones the analysis path works from. A
    /// simulator hands out whatever it was told to; only an instrument can disagree.
    /// </para>
    /// <para>
    /// <strong>It reaches the same answer by a different route.</strong> Total power here is a sum
    /// over the time domain; the analysis path reaches it through a window, a transform and the
    /// amplitude chain. Parseval says they must agree, so a disagreement means the block handed to
    /// the personality was not the block that was analysed, or was not scaled as its metadata says.
    /// The two share only <see cref="AmplitudeChain"/> itself, which is <c>REQ-AMP-001</c>'s
    /// "implemented once" doing its job — restating <c>−10·log10(2R) + 30</c> here would be a
    /// second derivation to get out of step with the first.
    /// </para>
    /// <para>
    /// Not marked with <see cref="MeasurementPersonalityAttribute"/>, and deliberately. Discovery
    /// is a separate claim with its own evidence — an assembly the host does not reference, dropped
    /// into <c>Personalities\</c> — and a discoverable type sitting inside the harness would make
    /// the harness's own assembly a personality source wherever it was loaded.
    /// </para>
    /// </remarks>
    internal sealed class BenchPersonality : IMeasurementPersonality
    {
        /// <inheritdoc />
        public string DisplayName => "Bench total power";

        /// <inheritdoc />
        public string Standard => "None (verification harness)";

        /// <inheritdoc />
        public string StandardRevision => "n/a";

        /// <inheritdoc />
        /// <remarks>
        /// A block with no samples, or one whose front end declared no full scale, cannot yield a
        /// calibrated power. Refusing it is the honest answer; returning a number computed from a
        /// full scale of zero would be a reading of −400 dBm that looked like a measurement.
        /// </remarks>
        public bool CanMeasure(IqBlock block) =>
            block != null && block.SampleCount > 0 && block.FullScaleVolts > 0.0;

        /// <inheritdoc />
        public IReadOnlyList<PersonalityReading> Measure(IqBlock block)
        {
            if (block == null)
            {
                throw new ArgumentNullException(nameof(block));
            }

            ReadOnlySpan<float> samples = block.GetSamples();
            double sum = 0.0;

            for (int n = 0; n < block.SampleCount; n++)
            {
                double i = samples[n * 2];
                double q = samples[n * 2 + 1];

                sum += (i * i) + (q * q);
            }

            double meanSquare = sum / block.SampleCount;

            // A transform length of one and a coherent gain of one, because there is no transform
            // and no window here: that leaves VoltsPerUnit as the full scale itself and
            // PowerOffsetDb as the impedance and milliwatt terms, which is exactly the part of the
            // chain a time-domain measurement needs.
            AmplitudeScale scale = new AmplitudeChain().ScaleFor(
                block.FullScaleVolts, block.ReferenceLevelDbm, 1, 1.0);

            double voltsSquared = meanSquare * scale.VoltsPerUnit * scale.VoltsPerUnit;

            return new[]
            {
                new PersonalityReading("Total power", scale.VoltsSquaredToDbm(voltsSquared), "dBm"),
                new PersonalityReading("Samples", block.SampleCount, string.Empty),
            };
        }
    }
}
