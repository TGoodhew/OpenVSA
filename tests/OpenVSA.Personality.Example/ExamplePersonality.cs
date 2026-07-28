using System;
using System.Collections.Generic;
using OpenVSA.Core;
using OpenVSA.Personality;

namespace OpenVSA.Personality.Example
{
    /// <summary>
    /// A personality that measures a block's mean power, and nothing else.
    /// </summary>
    /// <remarks>
    /// Its job is to be found, not to be useful. <c>REQ-ARC-003</c>'s criterion is that a new
    /// personality assembly dropped into <c>Personalities\</c> is discovered on next launch and
    /// runs with no rebuild of the host — and that cannot be demonstrated by a type the host
    /// already references, because then the host was rebuilt to know about it.
    /// </remarks>
    [MeasurementPersonality]
    public sealed class ExamplePersonality : IMeasurementPersonality
    {
        /// <inheritdoc />
        public string DisplayName => "Example mean power";

        /// <inheritdoc />
        public string Standard => "None (example)";

        /// <inheritdoc />
        public string StandardRevision => "n/a";

        /// <inheritdoc />
        public bool CanMeasure(IqBlock block) => block != null && block.SampleCount > 0;

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

                sum += i * i + q * q;
            }

            double mean = block.SampleCount == 0 ? 0.0 : sum / block.SampleCount;

            return new[]
            {
                new PersonalityReading("Mean power", mean, "V²"),
                new PersonalityReading("Samples", block.SampleCount, string.Empty),
            };
        }
    }
}
