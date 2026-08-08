using System;
using System.Collections.Generic;
using OpenVSA.TestHarness;
using Xunit;

namespace OpenVSA.TestHarness.Tests
{
    /// <summary>
    /// Finding the tones of a comb (issue #393).
    /// </summary>
    /// <remarks>
    /// These run with no hardware, which is the point: the search is the part of a comb scenario
    /// that can be wrong quietly, and the bench cannot tell a wrong search from a wrong analyser.
    /// </remarks>
    public class ToneSearchTests
    {
        [Fact]
        public void EveryToneOfAnEvenCombIsFound()
        {
            float[] spectrum = Comb(length: 401, first: 40, spacing: 80, count: 5, toneDbm: -27.0f);

            IReadOnlyList<int> tones = ToneSearch.Find(spectrum, 5);

            Assert.Equal(new[] { 40, 120, 200, 280, 360 }, tones);
        }

        [Fact]
        public void TheShouldersOfOneStrongToneAreNotFiveTones()
        {
            // THE trap this search exists for. A strong tone's skirts are larger than a weak
            // tone's peak, so "the five largest bins" returns one tone counted five times and a
            // spacing of one bin — which reads as a catastrophic frequency-axis failure while the
            // axis is perfectly correct.
            var spectrum = new float[401];

            for (int index = 0; index < spectrum.Length; index++)
            {
                // A single tone at bin 200 with realistic skirts, and nothing else.
                spectrum[index] = (float)(-20.0 - (1.5 * Math.Abs(index - 200)));
            }

            IReadOnlyList<int> tones = ToneSearch.Find(spectrum, 5);

            Assert.Single(tones);
            Assert.Equal(200, tones[0]);
        }

        [Fact]
        public void AMissingToneIsMissingRatherThanMadeUp()
        {
            // A comb with a tone absent must report four, not five. That is why the count is
            // checked separately from the spacing: the four that remain are still evenly spaced.
            float[] spectrum = Comb(length: 401, first: 40, spacing: 80, count: 5, toneDbm: -27.0f);

            for (int index = 198; index <= 202; index++)
            {
                spectrum[index] = -95.0f;
            }

            Assert.Equal(4, ToneSearch.Find(spectrum, 5).Count);
        }

        [Fact]
        public void TheFloorFollowsTheToneCountRatherThanTheLargestBin()
        {
            // Equal tones share the total power, so each sits about 10*log10(N) below a single
            // carrier of the same total. A floor referred to the largest bin alone would reject
            // every tone of a wide comb; this is the case that catches that.
            float[] spectrum = Comb(length: 801, first: 50, spacing: 100, count: 8, toneDbm: -29.0f);

            // One tone is 5 dB down on the others, which is within the head room and must survive.
            spectrum[450] = -34.0f;

            Assert.Equal(8, ToneSearch.Find(spectrum, 8).Count);
        }

        [Fact]
        public void AFlatToppedToneIsCountedOnce()
        {
            // Two adjacent bins at the same level is what a tone straddling a bin boundary looks
            // like. Counting it twice would report a comb with a spurious extra tone and halve the
            // measured spacing at that point.
            var spectrum = new float[201];

            for (int index = 0; index < spectrum.Length; index++)
            {
                spectrum[index] = -90.0f;
            }

            spectrum[50] = -27.0f;
            spectrum[100] = -27.0f;
            spectrum[101] = -27.0f;
            spectrum[150] = -27.0f;

            Assert.Equal(new[] { 50, 101, 150 }, ToneSearch.Find(spectrum, 3));
        }

        [Fact]
        public void NothingIsFoundInAnEmptySpectrumOrForATrivialComb()
        {
            Assert.Empty(ToneSearch.Find(null, 5));
            Assert.Empty(ToneSearch.Find(new float[] { -90.0f, -90.0f }, 5));

            // Fewer than two tones is not a comb; asking for one must not return the whole
            // spectrum's local maxima.
            Assert.Empty(ToneSearch.Find(Comb(201, 40, 40, 4, -27.0f), 1));
        }

        [Fact]
        public void ANotANumberBinIsNotATone()
        {
            // A frame can carry NaN where nothing was measured. NaN compares false against
            // everything, so an unguarded comparison silently drops real tones beside it.
            float[] spectrum = Comb(length: 201, first: 40, spacing: 40, count: 4, toneDbm: -27.0f);

            spectrum[100] = float.NaN;

            IReadOnlyList<int> tones = ToneSearch.Find(spectrum, 4);

            Assert.DoesNotContain(100, (IEnumerable<int>)tones);
        }

        private static float[] Comb(int length, int first, int spacing, int count, float toneDbm)
        {
            var spectrum = new float[length];

            for (int index = 0; index < length; index++)
            {
                spectrum[index] = -95.0f;
            }

            for (int tone = 0; tone < count; tone++)
            {
                spectrum[first + (tone * spacing)] = toneDbm;
            }

            return spectrum;
        }
    }
}
