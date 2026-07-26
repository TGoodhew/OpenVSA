using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using OpenVSA.Core;
using OpenVSA.Dsp.Windowing;

namespace OpenVSA.Dsp.Spectrum
{
    /// <summary>
    /// Cuts overlapping analysis frames from a longer record (<c>REQ-ACQ-003</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Advance is defined on the analysed record length, not on the FFT size.</strong>
    /// Under time gating, and under the RBW/time coupling, the two are not the same — and defining
    /// advance on the FFT size gives wrong frame counts whenever they differ. The requirement says
    /// so in as many words, so the record length is what this takes.
    /// </para>
    /// <para>
    /// Overlap recovers information that window tapering would otherwise weight to zero at frame
    /// edges. It is not free: successive frames share samples and are therefore correlated, so
    /// more of them are needed for the same variance reduction. <see cref="EffectiveAverages"/>
    /// is what makes that cost visible rather than letting a frame count overstate it.
    /// </para>
    /// </remarks>
    public static class FrameExtraction
    {
        /// <summary>Largest overlap the requirement permits.</summary>
        public const double MaximumOverlap = 0.9999;

        /// <summary>
        /// The advance between successive frames, in samples.
        /// </summary>
        /// <param name="recordSamples">Analysed record length, in samples.</param>
        /// <param name="overlap">Overlap fraction, from 0 to <see cref="MaximumOverlap"/>.</param>
        /// <returns><c>⌊(1 − overlap) · N_rec⌋</c>, at least one sample.</returns>
        /// <exception cref="ArgumentOutOfRangeException">A value is out of range.</exception>
        public static int Advance(int recordSamples, double overlap)
        {
            if (recordSamples < 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(recordSamples), recordSamples, "A record needs at least one sample.");
            }

            if (overlap < 0.0 || overlap > MaximumOverlap || double.IsNaN(overlap))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(overlap), overlap,
                    "Overlap runs from 0 to " + MaximumOverlap + ".");
            }

            double exact = (1.0 - overlap) * recordSamples;
            double nearest = Math.Round(exact);

            // Snapped before flooring. 1 − 0.9 is 0.09999999999999998 in binary, so 90 % overlap
            // of a 1000-sample record floors to 99 rather than 100 — an advance one sample short,
            // and a frame count wrong by a percent, from nothing but the representation of the
            // overlap the user typed. The tolerance is scaled by the record because the error in
            // the product grows with it.
            if (Math.Abs(exact - nearest) < 1e-9 * recordSamples)
            {
                exact = nearest;
            }

            var advance = (int)Math.Floor(exact);
            return advance < 1 ? 1 : advance;
        }

        /// <summary>
        /// The number of whole frames a record yields.
        /// </summary>
        /// <param name="availableSamples">Samples available.</param>
        /// <param name="recordSamples">Analysed record length, in samples.</param>
        /// <param name="overlap">Overlap fraction.</param>
        public static int FrameCount(int availableSamples, int recordSamples, double overlap)
        {
            if (availableSamples < recordSamples)
            {
                return 0;
            }

            return (availableSamples - recordSamples) / Advance(recordSamples, overlap) + 1;
        }

        /// <summary>
        /// Cuts frames from a block.
        /// </summary>
        /// <param name="block">The record to cut up.</param>
        /// <param name="recordSamples">Analysed record length, in samples.</param>
        /// <param name="overlap">Overlap fraction.</param>
        /// <returns>One block per frame; the caller disposes each.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="block"/> is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException">A value is out of range.</exception>
        /// <remarks>
        /// Yielded lazily, so a long recording does not have to exist as frames all at once — at
        /// 99 % overlap a record produces a hundred times its own length in frames, and
        /// materialising those would defeat <c>REQ-NFR-002</c>'s bounded allocation.
        /// </remarks>
        public static IEnumerable<IqBlock> Extract(IqBlock block, int recordSamples, double overlap)
        {
            if (block == null)
            {
                throw new ArgumentNullException(nameof(block));
            }

            int advance = Advance(recordSamples, overlap);
            int frames = FrameCount(block.SampleCount, recordSamples, overlap);

            return Cut(block, recordSamples, advance, frames);
        }

        private static IEnumerable<IqBlock> Cut(
            IqBlock block, int recordSamples, int advance, int frames)
        {
            for (int f = 0; f < frames; f++)
            {
                int first = f * advance;
                double advanceSeconds = first / block.SampleRateHz;

                var metadata = new IqBlockMetadata(
                    sampleCount: recordSamples,
                    sampleRateHz: block.SampleRateHz,
                    centerFrequencyHz: block.CenterFrequencyHz,
                    isBaseband: block.IsBaseband,
                    fullScaleVolts: block.FullScaleVolts,
                    referenceLevelDbm: block.ReferenceLevelDbm,

                    // Each frame is its own acquisition as far as everything downstream is
                    // concerned, so it carries its own place in the sequence.
                    sequenceNumber: block.SequenceNumber + f,

                    // The timestamp is the first sample and the trigger lies TriggerOffsetSeconds
                    // after it (REQ-ACQ-010), so a frame that starts later carries a later
                    // timestamp and a correspondingly smaller offset. Moving only the offset would
                    // leave every frame reporting the trigger at a different instant from the
                    // block it was cut from, and from the frame before it.
                    acquiredUtc: block.AcquiredUtc.AddTicks(
                        (long)Math.Round(advanceSeconds * TimeSpan.TicksPerSecond)),
                    triggerOffsetSeconds: block.TriggerOffsetSeconds - advanceSeconds,
                    triggerCorrectionsApplied: block.TriggerCorrectionsApplied,
                    source: block.Source,
                    extended: block.Extended);

                IqBlock frame = IqBlock.Rent(metadata);

                try
                {
                    block.GetSamples()
                        .Slice(first * 2, recordSamples * 2)
                        .CopyTo(frame.GetSamples());
                }
                catch
                {
                    frame.Dispose();
                    throw;
                }

                yield return frame;
            }
        }
    }

    /// <summary>
    /// How many independent averages a set of overlapping frames is worth (<c>REQ-DSP-031</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Overlapped frames share samples and are therefore correlated, so <c>N</c> of them reduce
    /// variance by less than <c>N</c>. Reporting the raw frame count would overstate the
    /// confidence of a noise measurement by exactly the amount the overlap bought in resolution —
    /// which is why the requirement asks for the effective count to be computed and displayed
    /// rather than the frame count relabelled.
    /// </para>
    /// <para>
    /// The correlation between two frames separated by <c>m</c> samples is set by the window's own
    /// autocorrelation at that lag, so the figure depends on the window as well as the overlap:
    /// a Uniform window overlapped by half is far more correlated than a Hann one, because Hann
    /// has already weighted the shared samples towards zero.
    /// </para>
    /// </remarks>
    public static class EffectiveAverages
    {
        private static readonly ConcurrentDictionary<CorrelationKey, double[]> Cache =
            new ConcurrentDictionary<CorrelationKey, double[]>();

        /// <summary>
        /// The effective number of independent averages.
        /// </summary>
        /// <param name="frames">Frames averaged.</param>
        /// <param name="recordSamples">Analysed record length, in samples.</param>
        /// <param name="overlap">Overlap fraction.</param>
        /// <param name="window">The analysis window.</param>
        /// <returns>A count no greater than <paramref name="frames"/>.</returns>
        /// <exception cref="ArgumentOutOfRangeException">A value is out of range.</exception>
        /// <remarks>
        /// Welch's expression: <c>N_eff = N / (1 + 2·Σ (1 − k/N)·ρ_k²)</c>, where <c>ρ_k</c> is
        /// the window's normalised autocorrelation at a lag of <c>k</c> frame advances. With no
        /// overlap every <c>ρ_k</c> is zero and the effective count is the frame count exactly.
        /// </remarks>
        public static double Compute(
            int frames, int recordSamples, double overlap, WindowType window)
        {
            if (frames < 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(frames), frames, "At least one frame.");
            }

            int advance = FrameExtraction.Advance(recordSamples, overlap);

            if (advance >= recordSamples || frames == 1)
            {
                // No shared samples: the frames are independent and the count is exact.
                return frames;
            }

            double[] rho = Correlations(recordSamples, advance, window);
            double correction = 0.0;

            for (int k = 1; k < frames && k <= rho.Length; k++)
            {
                correction += (1.0 - (double)k / frames) * rho[k - 1] * rho[k - 1];
            }

            return frames / (1.0 + 2.0 * correction);
        }

        /// <summary>
        /// The window's normalised autocorrelation at each whole multiple of the frame advance.
        /// </summary>
        /// <remarks>
        /// Cached, because this depends only on the window, the record length and the advance —
        /// none of which change from frame to frame — while the sum behind it is
        /// <c>O(N · N/advance)</c>, which at a long record and a high overlap is far too much work
        /// to repeat for every displayed update.
        /// </remarks>
        private static double[] Correlations(int recordSamples, int advance, WindowType window)
        {
            var key = new CorrelationKey(recordSamples, advance, window);

            return Cache.GetOrAdd(key, k =>
            {
                ReadOnlySpan<double> w = Window.Get(k.Window, k.RecordSamples).Coefficients;

                double energy = 0.0;

                for (int n = 0; n < k.RecordSamples; n++)
                {
                    energy += w[n] * w[n];
                }

                int lags = (k.RecordSamples - 1) / k.Advance;
                var values = new double[lags];

                if (!(energy > 0.0))
                {
                    return values;
                }

                for (int i = 0; i < lags; i++)
                {
                    int lag = (i + 1) * k.Advance;
                    double shared = 0.0;

                    for (int n = 0; n + lag < k.RecordSamples; n++)
                    {
                        shared += w[n] * w[n + lag];
                    }

                    values[i] = shared / energy;
                }

                return values;
            });
        }

        private struct CorrelationKey : IEquatable<CorrelationKey>
        {
            public CorrelationKey(int recordSamples, int advance, WindowType window)
            {
                RecordSamples = recordSamples;
                Advance = advance;
                Window = window;
            }

            public int RecordSamples { get; }

            public int Advance { get; }

            public WindowType Window { get; }

            public bool Equals(CorrelationKey other) =>
                RecordSamples == other.RecordSamples &&
                Advance == other.Advance &&
                Window == other.Window;

            public override bool Equals(object obj) =>
                obj is CorrelationKey other && Equals(other);

            public override int GetHashCode() =>
                (RecordSamples * 397 ^ Advance) * 397 ^ (int)Window;
        }
    }
}
