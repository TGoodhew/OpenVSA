using System;
using System.Globalization;
using OpenVSA.Core;

namespace OpenVSA.Dsp.Spectrum
{
    /// <summary>
    /// Selects a sub-interval of a time record for spectral analysis (<c>REQ-DSP-050</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Gating changes the resolution bandwidth, and that is the point.</strong> RBW is
    /// <c>ENBW / T_rec</c> and a gate shortens the record, so a gated measurement necessarily has
    /// a coarser RBW than an ungated one over the same acquisition. A gate that left the annotated
    /// RBW alone would be claiming a resolution the analysis no longer has — which is why the
    /// requirement asks for the annotation to reflect gate length rather than record length.
    /// </para>
    /// <para>
    /// <strong>Gate, then window.</strong> The gate selects which samples are analysed; the window
    /// then tapers those samples. Applying the window first and gating afterwards would taper
    /// against the full record and then cut a piece out of the taper, so the effective window
    /// would depend on where the gate happened to fall.
    /// </para>
    /// </remarks>
    public sealed class TimeGate
    {
        /// <summary>Creates a gate.</summary>
        /// <param name="delaySeconds">Delay from the start of the record to the gate, in seconds.</param>
        /// <param name="lengthSeconds">Gate length, in seconds; must be positive.</param>
        /// <exception cref="ArgumentOutOfRangeException">A value is out of range.</exception>
        public TimeGate(double delaySeconds, double lengthSeconds)
        {
            if (delaySeconds < 0.0 || double.IsNaN(delaySeconds) || double.IsInfinity(delaySeconds))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(delaySeconds), delaySeconds, "Gate delay cannot be negative.");
            }

            if (!(lengthSeconds > 0.0) || double.IsInfinity(lengthSeconds))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(lengthSeconds), lengthSeconds, "Gate length must be positive and finite.");
            }

            DelaySeconds = delaySeconds;
            LengthSeconds = lengthSeconds;
        }

        /// <summary>Delay from the start of the record to the start of the gate, in seconds.</summary>
        public double DelaySeconds { get; }

        /// <summary>Gate length, in seconds.</summary>
        public double LengthSeconds { get; }

        /// <summary>The first sample the gate admits, for a given sample rate.</summary>
        /// <param name="sampleRateHz">Sample rate, in hertz.</param>
        public int FirstSample(double sampleRateHz) =>
            (int)Math.Round(DelaySeconds * sampleRateHz);

        /// <summary>The number of samples the gate admits, for a given sample rate.</summary>
        /// <param name="sampleRateHz">Sample rate, in hertz.</param>
        public int SampleCount(double sampleRateHz) =>
            (int)Math.Round(LengthSeconds * sampleRateHz);

        /// <summary>
        /// Extracts the gated samples from a block.
        /// </summary>
        /// <param name="block">The block to gate.</param>
        /// <returns>A new block holding only the gated interval; the caller disposes it.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="block"/> is null.</exception>
        /// <exception cref="ArgumentException">The gate falls outside the record.</exception>
        /// <remarks>
        /// A new block rather than a view, because everything downstream takes an
        /// <see cref="IqBlock"/> and a gated analysis is simply an analysis of a shorter record.
        /// Its metadata carries the gated length, so the frequency axis and the resolution
        /// bandwidth follow from the gate without anything downstream being told about gating at
        /// all.
        /// </remarks>
        public IqBlock Apply(IqBlock block)
        {
            if (block == null)
            {
                throw new ArgumentNullException(nameof(block));
            }

            int first = FirstSample(block.SampleRateHz);
            int count = SampleCount(block.SampleRateHz);

            if (first >= block.SampleCount || count < 1)
            {
                throw new ArgumentException(
                    "The gate at " + DelaySeconds.ToString("G4", CultureInfo.CurrentCulture) +
                    " s for " + LengthSeconds.ToString("G4", CultureInfo.CurrentCulture) +
                    " s selects nothing from a record of " +
                    (block.SampleCount / block.SampleRateHz).ToString("G4", CultureInfo.CurrentCulture) +
                    " s.",
                    nameof(block));
            }

            // Truncated rather than refused: a gate running past the end of the record is a
            // legitimate way of saying "from here to the end", and the annotation reports the
            // length actually analysed.
            if (first + count > block.SampleCount)
            {
                count = block.SampleCount - first;
            }

            var metadata = new IqBlockMetadata(
                sampleCount: count,
                sampleRateHz: block.SampleRateHz,
                centerFrequencyHz: block.CenterFrequencyHz,
                isBaseband: block.IsBaseband,
                fullScaleVolts: block.FullScaleVolts,
                referenceLevelDbm: block.ReferenceLevelDbm,
                sequenceNumber: block.SequenceNumber,
                acquiredUtc: block.AcquiredUtc,

                // The gate moves the first sample, so the trigger is now that much further back.
                triggerOffsetSeconds: block.TriggerOffsetSeconds - first / block.SampleRateHz,
                triggerCorrectionsApplied: block.TriggerCorrectionsApplied,
                source: block.Source,
                extended: block.Extended);

            IqBlock gated = IqBlock.Rent(metadata);

            try
            {
                ReadOnlySpan<float> source = block.GetSamples();
                Span<float> destination = gated.GetSamples();

                source.Slice(first * 2, count * 2).CopyTo(destination);
            }
            catch
            {
                gated.Dispose();
                throw;
            }

            return gated;
        }

        /// <summary>
        /// The resolution bandwidth a gate implies, given a window.
        /// </summary>
        /// <param name="equivalentNoiseBandwidthBins">The window's ENBW, in bins.</param>
        /// <returns>Resolution bandwidth in hertz.</returns>
        /// <remarks>
        /// <c>REQ-DSP-020</c>'s relation applied to the gate length rather than the record length,
        /// which is exactly what <c>REQ-DSP-050</c> requires: under gating, RBW tracks the gate.
        /// </remarks>
        public double ResolutionBandwidthHz(double equivalentNoiseBandwidthBins) =>
            ResolutionBandwidth.ForRecordLength(equivalentNoiseBandwidthBins, LengthSeconds);

        /// <inheritdoc />
        public override string ToString() =>
            "gate " + DelaySeconds.ToString("G4", CultureInfo.CurrentCulture) + " s for " +
            LengthSeconds.ToString("G4", CultureInfo.CurrentCulture) + " s";
    }
}
