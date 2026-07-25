using System;
using OpenVSA.Core;
using OpenVSA.Dsp.Windowing;

namespace OpenVSA.Dsp.Spectrum
{
    /// <summary>
    /// One computed spectrum, in dBm, with the axis and settings that produced it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Immutable, and that is the point.</strong> <c>REQ-NFR-011</c> requires trace results
    /// crossing into the UI thread to be snapshots no DSP thread can still be writing. The level
    /// array is owned exclusively by the frame and reachable only as a
    /// <see cref="ReadOnlySpan{T}"/>, so a consumer can neither mutate it nor store it past the
    /// frame — the same reasoning <c>REQ-DAT-001a</c> applies to <see cref="IqBlock"/>.
    /// </para>
    /// <para>
    /// <strong>One array per published frame.</strong> The buffer is not pooled, because pooling it
    /// would need a lifetime protocol saying when the UI has finished reading — and a frame that
    /// can be recycled while it is still on screen is precisely the torn frame the requirement
    /// forbids. The cost is bounded and known: one array of <see cref="PointCount"/> floats per
    /// update, which at the 2²⁰-point, 10 updates/s corner of <c>REQ-NFR-021</c> is 40 MB/s of
    /// gen-2 traffic. Recycling behind an explicit release is a later optimisation with a real
    /// invariant to state; guessing at it now would be the cheap kind of fast.
    /// </para>
    /// <para>
    /// Levels are in ascending frequency order — index 0 is <see cref="StartFrequencyHz"/> — not in
    /// the transform's natural order. Displaying raw FFT order is a spectrum split down the middle,
    /// so the shift belongs here rather than in every consumer.
    /// </para>
    /// </remarks>
    public sealed class SpectrumFrame
    {
        private readonly float[] _levelsDbm;

        private SpectrumFrame(
            float[] levelsDbm,
            double startFrequencyHz,
            double binWidthHz,
            double centerFrequencyHz,
            double sampleRateHz,
            bool isBaseband,
            WindowType window,
            double equivalentNoiseBandwidthBins,
            double referenceLevelDbm,
            long sequenceNumber,
            DateTime acquiredUtc,
            FrontEndId source)
        {
            _levelsDbm = levelsDbm;
            StartFrequencyHz = startFrequencyHz;
            BinWidthHz = binWidthHz;
            CenterFrequencyHz = centerFrequencyHz;
            SampleRateHz = sampleRateHz;
            IsBaseband = isBaseband;
            Window = window;
            EquivalentNoiseBandwidthBins = equivalentNoiseBandwidthBins;
            ReferenceLevelDbm = referenceLevelDbm;
            SequenceNumber = sequenceNumber;
            AcquiredUtc = acquiredUtc;
            Source = source;
        }

        /// <summary>
        /// Wraps an array the caller must not retain, without copying it.
        /// </summary>
        /// <remarks>
        /// Internal because the no-retention rule cannot be enforced, only stated;
        /// <see cref="FromLevels"/> is the safe public route and copies. The producer is
        /// <see cref="SpectrumComputer"/>, which fills a fresh array per frame and drops it here.
        /// </remarks>
        internal static SpectrumFrame Adopt(
            float[] levelsDbm,
            double startFrequencyHz,
            double binWidthHz,
            double centerFrequencyHz,
            double sampleRateHz,
            bool isBaseband,
            WindowType window,
            double equivalentNoiseBandwidthBins,
            double referenceLevelDbm,
            long sequenceNumber,
            DateTime acquiredUtc,
            FrontEndId source) =>
            new SpectrumFrame(
                levelsDbm, startFrequencyHz, binWidthHz, centerFrequencyHz, sampleRateHz,
                isBaseband, window, equivalentNoiseBandwidthBins, referenceLevelDbm,
                sequenceNumber, acquiredUtc, source);

        /// <summary>
        /// Creates a frame from levels, copying them.
        /// </summary>
        /// <param name="levelsDbm">Levels in dBm, in ascending frequency order; must not be empty.</param>
        /// <param name="startFrequencyHz">Frequency of index 0, in hertz.</param>
        /// <param name="binWidthHz">Spacing between points, in hertz; must be positive.</param>
        /// <param name="window">Window used to compute them.</param>
        /// <param name="equivalentNoiseBandwidthBins">The window's ENBW in bins, for <see cref="ResolutionBandwidthHz"/>.</param>
        /// <returns>An immutable frame.</returns>
        /// <exception cref="ArgumentException"><paramref name="levelsDbm"/> is empty.</exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="binWidthHz"/> is not positive.</exception>
        /// <remarks>
        /// For tests and for consumers assembling a frame from stored data. A live measurement does
        /// not take this path, so the copy is not on the hot loop.
        /// </remarks>
        public static SpectrumFrame FromLevels(
            ReadOnlySpan<float> levelsDbm,
            double startFrequencyHz,
            double binWidthHz,
            WindowType window,
            double equivalentNoiseBandwidthBins)
        {
            if (levelsDbm.Length == 0)
            {
                throw new ArgumentException("A frame needs at least one point.", nameof(levelsDbm));
            }

            if (!(binWidthHz > 0.0) || double.IsInfinity(binWidthHz))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(binWidthHz), binWidthHz, "Bin width must be positive and finite.");
            }

            var copy = new float[levelsDbm.Length];
            levelsDbm.CopyTo(new Span<float>(copy));

            double span = binWidthHz * (levelsDbm.Length - 1);

            return new SpectrumFrame(
                copy,
                startFrequencyHz,
                binWidthHz,
                startFrequencyHz + span / 2.0,
                binWidthHz * levelsDbm.Length,
                isBaseband: false,
                window: window,
                equivalentNoiseBandwidthBins: equivalentNoiseBandwidthBins,
                referenceLevelDbm: 0.0,
                sequenceNumber: 0,
                acquiredUtc: DateTime.UtcNow,
                source: default(FrontEndId));
        }

        /// <summary>The levels in dBm, ascending in frequency, in a view that cannot be written through.</summary>
        public ReadOnlySpan<float> LevelsDbm => new ReadOnlySpan<float>(_levelsDbm);

        /// <summary>Number of displayed frequency points.</summary>
        public int PointCount => _levelsDbm.Length;

        /// <summary>Frequency of point 0, in hertz.</summary>
        public double StartFrequencyHz { get; }

        /// <summary>Spacing between points, in hertz.</summary>
        public double BinWidthHz { get; }

        /// <summary>Frequency of the last point, in hertz.</summary>
        public double StopFrequencyHz => StartFrequencyHz + (PointCount - 1) * BinWidthHz;

        /// <summary>Centre frequency of the acquisition, in hertz.</summary>
        public double CenterFrequencyHz { get; }

        /// <summary>
        /// Width of the displayed axis, in hertz: <see cref="StopFrequencyHz"/> −
        /// <see cref="StartFrequencyHz"/>.
        /// </summary>
        /// <remarks>
        /// Point count minus one bins, not point count. The distinction is one bin out of several
        /// thousand and would be invisible — except that it is what makes the displayed span read
        /// as exactly the span the user asked for rather than a hair over it.
        /// </remarks>
        public double SpanHz => (PointCount - 1) * BinWidthHz;

        /// <summary>Sample rate of the acquisition, in hertz.</summary>
        public double SampleRateHz { get; }

        /// <summary>Whether this came from a real-baseband acquisition, and so is one-sided.</summary>
        public bool IsBaseband { get; }

        /// <summary>The analysis window.</summary>
        public WindowType Window { get; }

        /// <summary>The window's equivalent noise bandwidth, in bins.</summary>
        public double EquivalentNoiseBandwidthBins { get; }

        /// <summary>
        /// Resolution bandwidth, in hertz: <c>ENBW × bin width</c> (<c>REQ-DSP-020</c>).
        /// </summary>
        public double ResolutionBandwidthHz => EquivalentNoiseBandwidthBins * BinWidthHz;

        /// <summary>Reference level of the acquisition, in dBm.</summary>
        public double ReferenceLevelDbm { get; }

        /// <summary>Sequence number of the block this was computed from.</summary>
        public long SequenceNumber { get; }

        /// <summary>Acquisition timestamp of that block, UTC.</summary>
        public DateTime AcquiredUtc { get; }

        /// <summary>The front end that produced the block, for provenance only.</summary>
        public FrontEndId Source { get; }

        /// <summary>Frequency of a point, in hertz.</summary>
        /// <param name="index">Point index.</param>
        /// <exception cref="ArgumentOutOfRangeException">The index is outside the frame.</exception>
        public double FrequencyAt(int index)
        {
            if (index < 0 || index >= PointCount)
            {
                throw new ArgumentOutOfRangeException(nameof(index), index, "Outside the frame.");
            }

            return StartFrequencyHz + index * BinWidthHz;
        }

        /// <summary>
        /// Index of the highest point, or −1 for an empty frame.
        /// </summary>
        /// <remarks>
        /// Here rather than in the marker layer because it is the one search the annotation needs
        /// before markers exist (<c>REQ-MKR-001</c> owns the rest).
        /// </remarks>
        public int IndexOfPeak()
        {
            int peak = -1;
            float highest = float.NegativeInfinity;

            for (int i = 0; i < _levelsDbm.Length; i++)
            {
                if (_levelsDbm[i] > highest)
                {
                    highest = _levelsDbm[i];
                    peak = i;
                }
            }

            return peak;
        }
    }
}
