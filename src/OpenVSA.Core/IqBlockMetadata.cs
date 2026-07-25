using System;
using System.Collections.Generic;

namespace OpenVSA.Core
{
    /// <summary>
    /// The acquisition metadata that accompanies every <see cref="IqBlock"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Separated from <see cref="IqBlock"/> so that every field must be supplied in one place at
    /// construction. <c>REQ-DAT-001</c> makes all of them mandatory for every front end: a front
    /// end that cannot supply a value must supply a documented, explicitly flagged default rather
    /// than a silent zero. Optional constructor parameters would quietly reintroduce exactly the
    /// silent zeros that requirement forbids, so there are none.
    /// </para>
    /// </remarks>
    public readonly struct IqBlockMetadata
    {
        /// <summary>
        /// <see cref="Extended"/> key carrying the alias-free bandwidth of a block, in hertz.
        /// </summary>
        /// <remarks>
        /// <para>
        /// How much of the sample rate is usable is a property of the front end's decimation
        /// filter, not a law of the product. <c>REQ-ACQ-001</c>'s <c>Fs = 1.28 · Span</c> describes
        /// the reference product; a real instrument may differ — an E4406A digitises at 1.5 times
        /// its information bandwidth — and a display that assumed the product's figure would draw
        /// the filter's roll-off as though it were measurement data.
        /// </para>
        /// <para>
        /// A front end that knows its alias-free bandwidth states it here. One that does not says
        /// nothing, and the consumer falls back to the product law. Declared as a constant rather
        /// than left as a string literal in two assemblies, because a key spelled differently at
        /// either end fails by being silently absent.
        /// </para>
        /// </remarks>
        public const string UsableBandwidthKey = "UsableBandwidthHz";


        /// <summary>Number of complex samples. The interleaved buffer holds twice this many floats.</summary>
        public readonly int SampleCount;

        /// <summary>Sample rate in hertz. Must be greater than zero.</summary>
        public readonly double SampleRateHz;

        /// <summary>Centre frequency in hertz.</summary>
        public readonly double CenterFrequencyHz;

        /// <summary><c>true</c> for real baseband; <c>false</c> for a complex zoom/IF acquisition.</summary>
        public readonly bool IsBaseband;

        /// <summary>ADC full scale referred to the input, in volts, for absolute amplitude.</summary>
        public readonly double FullScaleVolts;

        /// <summary>Reference level in dBm.</summary>
        public readonly double ReferenceLevelDbm;

        /// <summary>Monotonic sequence number within an acquisition session.</summary>
        public readonly long SequenceNumber;

        /// <summary>Acquisition timestamp, UTC.</summary>
        public readonly DateTime AcquiredUtc;

        /// <summary>Trigger offset in seconds; negative values denote pre-trigger data.</summary>
        public readonly double TriggerOffsetSeconds;

        /// <summary>
        /// Whether trigger delay/phase corrections have been applied to the samples.
        /// </summary>
        /// <remarks>
        /// <c>REQ-DAT-002</c>. The reference product does not apply trigger corrections to exported
        /// data except in one format, and does not say so where the user can see it. OpenVSA tracks
        /// the fact explicitly and propagates it into exported files rather than losing it.
        /// </remarks>
        public readonly bool TriggerCorrectionsApplied;

        /// <summary>Identifies the front end that produced the block, for provenance only.</summary>
        public readonly FrontEndId Source;

        /// <summary>Per-front-end extras. Never <c>null</c>; empty when there are none.</summary>
        public readonly IReadOnlyDictionary<string, object> Extended;

        /// <summary>Creates acquisition metadata. Every value is required.</summary>
        /// <param name="sampleCount">Number of complex samples; must be positive.</param>
        /// <param name="sampleRateHz">Sample rate in hertz; must be positive and finite.</param>
        /// <param name="centerFrequencyHz">Centre frequency in hertz; must be finite.</param>
        /// <param name="isBaseband"><c>true</c> for real baseband, <c>false</c> for complex zoom/IF.</param>
        /// <param name="fullScaleVolts">ADC full scale referred to input, in volts.</param>
        /// <param name="referenceLevelDbm">Reference level in dBm.</param>
        /// <param name="sequenceNumber">Monotonic sequence number within the session.</param>
        /// <param name="acquiredUtc">Acquisition timestamp; must be <see cref="DateTimeKind.Utc"/>.</param>
        /// <param name="triggerOffsetSeconds">Trigger offset in seconds; negative means pre-trigger.</param>
        /// <param name="triggerCorrectionsApplied">Whether trigger corrections have been applied.</param>
        /// <param name="source">Identifier of the producing front end.</param>
        /// <param name="extended">Per-front-end extras, or <c>null</c> for none.</param>
        /// <exception cref="ArgumentOutOfRangeException">A numeric value is out of range or not finite.</exception>
        /// <exception cref="ArgumentException"><paramref name="acquiredUtc"/> is not UTC.</exception>
        public IqBlockMetadata(
            int sampleCount,
            double sampleRateHz,
            double centerFrequencyHz,
            bool isBaseband,
            double fullScaleVolts,
            double referenceLevelDbm,
            long sequenceNumber,
            DateTime acquiredUtc,
            double triggerOffsetSeconds,
            bool triggerCorrectionsApplied,
            FrontEndId source,
            IReadOnlyDictionary<string, object> extended)
        {
            if (sampleCount <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(sampleCount), sampleCount, "Sample count must be positive.");
            }

            // REQ-DAT-001's conformance criterion is Fs > 0. NaN fails this comparison, which is
            // the intent: a front end that could not determine its rate must say so, not pass NaN
            // down the chain to surface as a silently wrong frequency axis.
            if (!(sampleRateHz > 0.0) || double.IsInfinity(sampleRateHz))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(sampleRateHz), sampleRateHz, "Sample rate must be positive and finite.");
            }

            if (double.IsNaN(centerFrequencyHz) || double.IsInfinity(centerFrequencyHz))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(centerFrequencyHz), centerFrequencyHz, "Centre frequency must be finite.");
            }

            if (acquiredUtc.Kind != DateTimeKind.Utc)
            {
                throw new ArgumentException(
                    "Acquisition timestamp must be UTC.", nameof(acquiredUtc));
            }

            SampleCount = sampleCount;
            SampleRateHz = sampleRateHz;
            CenterFrequencyHz = centerFrequencyHz;
            IsBaseband = isBaseband;
            FullScaleVolts = fullScaleVolts;
            ReferenceLevelDbm = referenceLevelDbm;
            SequenceNumber = sequenceNumber;
            AcquiredUtc = acquiredUtc;
            TriggerOffsetSeconds = triggerOffsetSeconds;
            TriggerCorrectionsApplied = triggerCorrectionsApplied;
            Source = source;
            Extended = extended ?? EmptyExtended;
        }

        private static readonly IReadOnlyDictionary<string, object> EmptyExtended =
            new Dictionary<string, object>(0);
    }
}
