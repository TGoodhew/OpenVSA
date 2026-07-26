using System;
using System.Collections.Generic;

namespace OpenVSA.Dsp.Spectrum
{
    /// <summary>
    /// Finds the peaks of a spectrum: the searches behind <c>REQ-MKR-005</c>'s marker functions.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Local maxima, not the sorted samples.</strong> "Next peak" must visit a different
    /// signal each time, and the several bins that make up one tone's mainlobe are all large. Sorting
    /// the bins and stepping down them would walk across a single peak's shoulders reporting it as
    /// four separate signals, which is exactly what the acceptance criterion's "without revisiting a
    /// peak" rules out.
    /// </para>
    /// <para>
    /// A bin is a local maximum when it is above the bin before it and not below the bin after —
    /// the asymmetry matters, because a flat top of equal bins should yield one peak rather than
    /// none. Blanked (<see cref="float.NaN"/>) bins break the run, so a peak cannot straddle a gap.
    /// </para>
    /// </remarks>
    public static class PeakSearch
    {
        /// <summary>
        /// Every local maximum, in descending order of level.
        /// </summary>
        /// <param name="frame">The spectrum to search.</param>
        /// <returns>Point indices, highest level first. Empty if the trace has no peak.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="frame"/> is null.</exception>
        public static IReadOnlyList<int> Peaks(SpectrumFrame frame)
        {
            if (frame == null)
            {
                throw new ArgumentNullException(nameof(frame));
            }

            ReadOnlySpan<float> levels = frame.LevelsDbm;
            var peaks = new List<int>();

            for (int i = 0; i < levels.Length; i++)
            {
                if (IsLocalMaximum(levels, i))
                {
                    peaks.Add(i);
                }
            }

            // Descending by level; ties broken by index so the order is deterministic, which
            // repeated next-peak depends on.
            var levelAt = new float[levels.Length];
            levels.CopyTo(new Span<float>(levelAt));

            peaks.Sort((a, b) =>
            {
                int byLevel = levelAt[b].CompareTo(levelAt[a]);
                return byLevel != 0 ? byLevel : a.CompareTo(b);
            });

            return peaks;
        }

        /// <summary>
        /// The highest point of the trace, or −1 if it has none.
        /// </summary>
        /// <param name="frame">The spectrum to search.</param>
        /// <remarks>
        /// The global maximum, which is a local maximum unless the trace is monotonic — in which
        /// case the endpoint is the answer a user expects from "peak search", so it is returned
        /// rather than reporting no peak.
        /// </remarks>
        /// <exception cref="ArgumentNullException"><paramref name="frame"/> is null.</exception>
        public static int Highest(SpectrumFrame frame)
        {
            if (frame == null)
            {
                throw new ArgumentNullException(nameof(frame));
            }

            return frame.IndexOfPeak();
        }

        /// <summary>
        /// The next peak below a given level, excluding peaks already visited.
        /// </summary>
        /// <param name="frame">The spectrum to search.</param>
        /// <param name="fromIndex">The peak currently occupied, or −1 to start from the highest.</param>
        /// <returns>The index of the next-highest distinct peak, or −1 if there is none.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="frame"/> is null.</exception>
        public static int Next(SpectrumFrame frame, int fromIndex)
        {
            IReadOnlyList<int> peaks = Peaks(frame);

            if (peaks.Count == 0)
            {
                return -1;
            }

            if (fromIndex < 0)
            {
                return peaks[0];
            }

            for (int i = 0; i < peaks.Count; i++)
            {
                if (peaks[i] == fromIndex)
                {
                    return i + 1 < peaks.Count ? peaks[i + 1] : -1;
                }
            }

            // Not on a peak: take the highest peak strictly below the current level, so that
            // "next peak" from an arbitrary position still descends.
            float current = frame.LevelsDbm[fromIndex];

            foreach (int peak in peaks)
            {
                if (frame.LevelsDbm[peak] < current)
                {
                    return peak;
                }
            }

            return -1;
        }

        /// <summary>
        /// The peak nearest a position, for a marker that tracks a signal (<c>REQ-MKR-005</c>).
        /// </summary>
        /// <param name="frame">The spectrum to search.</param>
        /// <param name="fromIndex">The position to search out from.</param>
        /// <returns>The index of the nearest local maximum, or −1 if the trace has none.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="frame"/> is null.</exception>
        /// <remarks>
        /// <para>
        /// Nearest in bins, not highest. A tracking marker must stay on the tone it was put on: a
        /// search for the highest peak would abandon it for a larger neighbour the moment one
        /// appeared, which is the opposite of tracking.
        /// </para>
        /// <para>
        /// A tie — a peak the same distance either side — resolves to the higher of the two, and to
        /// the lower index if those are equal as well. Something has to break it, and taking the
        /// stronger signal is the choice that keeps a marker on a carrier rather than on a spur
        /// that happened to be equidistant.
        /// </para>
        /// </remarks>
        public static int Nearest(SpectrumFrame frame, int fromIndex)
        {
            IReadOnlyList<int> peaks = Peaks(frame);

            if (peaks.Count == 0)
            {
                return -1;
            }

            if (fromIndex < 0)
            {
                return Highest(frame);
            }

            ReadOnlySpan<float> levels = frame.LevelsDbm;
            int nearest = -1;
            int bestDistance = int.MaxValue;
            float bestLevel = float.NegativeInfinity;

            foreach (int peak in peaks)
            {
                int distance = Math.Abs(peak - fromIndex);

                if (distance > bestDistance)
                {
                    continue;
                }

                if (distance < bestDistance || levels[peak] > bestLevel)
                {
                    nearest = peak;
                    bestDistance = distance;
                    bestLevel = levels[peak];
                }
            }

            return nearest;
        }

        /// <summary>
        /// The lowest point of the trace, or −1 if it is empty.
        /// </summary>
        /// <param name="frame">The spectrum to search.</param>
        /// <remarks>
        /// The global minimum rather than a local one: a noise floor has thousands of local minima
        /// and none of them is what "minimum search" means.
        /// </remarks>
        /// <exception cref="ArgumentNullException"><paramref name="frame"/> is null.</exception>
        public static int Lowest(SpectrumFrame frame)
        {
            if (frame == null)
            {
                throw new ArgumentNullException(nameof(frame));
            }

            ReadOnlySpan<float> levels = frame.LevelsDbm;
            int lowest = -1;
            float smallest = float.PositiveInfinity;

            for (int i = 0; i < levels.Length; i++)
            {
                if (!float.IsNaN(levels[i]) && levels[i] < smallest)
                {
                    smallest = levels[i];
                    lowest = i;
                }
            }

            return lowest;
        }

        private static bool IsLocalMaximum(ReadOnlySpan<float> levels, int index)
        {
            float value = levels[index];

            if (float.IsNaN(value))
            {
                return false;
            }

            bool hasLeft = index > 0 && !float.IsNaN(levels[index - 1]);
            bool hasRight = index < levels.Length - 1 && !float.IsNaN(levels[index + 1]);

            if (!hasLeft && !hasRight)
            {
                return true;
            }

            // An endpoint - or a point beside a blanked one - must be strictly above its single
            // neighbour. Treating a missing neighbour as satisfied instead makes the first bin of
            // every flat noise floor a peak, and "next peak" then reports the left-hand edge of the
            // trace as a signal.
            if (!hasLeft)
            {
                return value > levels[index + 1];
            }

            if (!hasRight)
            {
                return value > levels[index - 1];
            }

            // Strictly above the bin before and not below the one after, so a plateau of equal bins
            // yields its leading edge rather than nothing at all.
            return value > levels[index - 1] && value >= levels[index + 1];
        }
    }
}
