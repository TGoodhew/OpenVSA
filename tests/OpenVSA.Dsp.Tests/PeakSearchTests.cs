using System;
using System.Collections.Generic;
using OpenVSA.Dsp.Spectrum;
using OpenVSA.Dsp.Windowing;
using Xunit;

namespace OpenVSA.Dsp.Tests
{
    /// <summary>
    /// <c>REQ-MKR-005</c>: peak search, next peak and minimum search.
    /// </summary>
    public class PeakSearchTests
    {
        [Fact]
        public void PeakSearchFindsTheLargestTone()
        {
            SpectrumFrame frame = Tones(new[] { 100, 300, 500 }, new[] { -30.0, -10.0, -20.0 });

            Assert.Equal(300, PeakSearch.Highest(frame));
        }

        [Fact]
        public void RepeatedNextPeakVisitsTheTonesInDescendingOrderWithoutRevisiting()
        {
            // The requirement's own criterion. The failure it guards against is sorting the bins
            // rather than the peaks, which walks across one tone's shoulders reporting it several
            // times before ever reaching the next tone.
            SpectrumFrame frame = Tones(
                new[] { 100, 300, 500, 700 },
                new[] { -30.0, -10.0, -20.0, -40.0 });

            var visited = new List<int>();
            int at = PeakSearch.Highest(frame);

            while (at >= 0 && visited.Count < 4)
            {
                visited.Add(at);
                at = PeakSearch.Next(frame, at);
            }

            Assert.Equal(new[] { 300, 500, 100, 700 }, visited);
        }

        [Fact]
        public void EachPeakIsWithinOneBinOfItsTone()
        {
            SpectrumFrame frame = Tones(new[] { 137, 411 }, new[] { -15.0, -25.0 });

            Assert.InRange(PeakSearch.Highest(frame), 136, 138);
            Assert.InRange(PeakSearch.Next(frame, PeakSearch.Highest(frame)), 410, 412);
        }

        [Fact]
        public void NextPeakEventuallyRunsOut()
        {
            SpectrumFrame frame = Tones(new[] { 100 }, new[] { -10.0 });

            Assert.Equal(-1, PeakSearch.Next(frame, 100));
        }

        [Fact]
        public void APeakIsALocalMaximum_NotJustALargeSample()
        {
            // A single tone spread over several bins is one peak, not four. This is the property
            // that makes "without revisiting a peak" achievable.
            SpectrumFrame frame = Tones(new[] { 300 }, new[] { -10.0 });

            Assert.Single(PeakSearch.Peaks(frame));
        }

        [Fact]
        public void MinimumSearchFindsTheAnalyticMinimum()
        {
            var levels = new float[201];

            for (int i = 0; i < levels.Length; i++)
            {
                levels[i] = (float)(-50.0 + Math.Abs(i - 120) * 0.1);
            }

            SpectrumFrame frame = Frame(levels);

            Assert.Equal(120, PeakSearch.Lowest(frame));
        }

        [Fact]
        public void BlankedPointsBreakARunRatherThanFormingAPeak()
        {
            var levels = new float[11];

            for (int i = 0; i < levels.Length; i++)
            {
                levels[i] = -50.0f;
            }

            levels[5] = float.NaN;

            SpectrumFrame frame = Frame(levels);

            Assert.DoesNotContain(5, PeakSearch.Peaks(frame));
            Assert.NotEqual(5, PeakSearch.Lowest(frame));
        }

        [Fact]
        public void AFlatRunYieldsOnePeakRatherThanNone()
        {
            // A plateau of equal bins has no bin that is strictly greater than both neighbours.
            // Requiring "greater than the one before, not less than the one after" gives its
            // leading edge, which is a defensible answer; requiring strict inequality both ways
            // gives none at all, which is not.
            var levels = new float[11];

            for (int i = 0; i < levels.Length; i++)
            {
                levels[i] = -50.0f;
            }

            levels[4] = -10.0f;
            levels[5] = -10.0f;
            levels[6] = -10.0f;

            IReadOnlyList<int> peaks = PeakSearch.Peaks(Frame(levels));

            Assert.Single(peaks);
            Assert.Equal(4, peaks[0]);
        }

        [Fact]
        public void ItRefusesAFrameOfNull()
        {
            Assert.Throws<ArgumentNullException>(() => PeakSearch.Peaks(null));
            Assert.Throws<ArgumentNullException>(() => PeakSearch.Highest(null));
            Assert.Throws<ArgumentNullException>(() => PeakSearch.Lowest(null));
        }

        /// <summary>A 1001-point trace with raised-cosine tones at the given bins.</summary>
        private static SpectrumFrame Tones(int[] bins, double[] levels)
        {
            var values = new float[1001];

            for (int i = 0; i < values.Length; i++)
            {
                values[i] = -80.0f;
            }

            for (int t = 0; t < bins.Length; t++)
            {
                // A mainlobe several bins wide, so that a peak really is a local maximum with
                // shoulders rather than a single sample.
                for (int offset = -4; offset <= 4; offset++)
                {
                    int index = bins[t] + offset;

                    if (index < 0 || index >= values.Length)
                    {
                        continue;
                    }

                    double shape = Math.Cos(offset * Math.PI / 10.0);
                    var level = (float)(-80.0 + (levels[t] + 80.0) * shape * shape);

                    if (level > values[index])
                    {
                        values[index] = level;
                    }
                }
            }

            return Frame(values);
        }

        private static SpectrumFrame Frame(float[] levels) =>
            SpectrumFrame.FromLevels(levels, 1.0e9, 10e3, WindowType.FlatTop, 3.8194);
    }
}
