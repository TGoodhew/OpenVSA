using System;
using OpenVSA.Core;
using OpenVSA.Dsp.Spectrum;
using OpenVSA.Dsp.Windowing;
using Xunit;
using Xunit.Abstractions;

namespace OpenVSA.Dsp.Tests
{
    /// <summary>
    /// The reported level of a tone does not depend on how many points were transformed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Nothing asserted this, which is why a screenshot was the first thing to notice it was worth
    /// asking about. A harness reported +58.83 dBm for the same carrier at 8 192 points and
    /// +100.97 dBm at 2²⁰ — a difference of 42.14 dB, and 20·log₁₀(1048576/8192) is 42.144 dB. The
    /// signature of a missing 1/N normalisation, and it turned out to be the harness computing
    /// levels as a raw 10·log₁₀|X|² rather than going through <see cref="AmplitudeChain"/>, which
    /// divides by the transform length.
    /// </para>
    /// <para>
    /// The lesson is not about that harness. It is that a level scaling with N is invisible to
    /// every test that uses one size, and every test did.
    /// </para>
    /// </remarks>
    public class LevelIsIndependentOfTransformSizeTests
    {
        private readonly ITestOutputHelper _output;

        /// <summary>Takes xunit's output sink.</summary>
        /// <param name="output">Where the levels at each size are written.</param>
        public LevelIsIndependentOfTransformSizeTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void ACarrierReadsTheSameLevelAtEveryTransformSize()
        {
            double first = double.NaN;

            foreach (int points in new[] { 4096, 8192, 65536, 262144 })
            {
                double peak = PeakOf(points);

                _output.WriteLine(points.ToString().PadLeft(7) + " pts   " + peak.ToString("F3") + " dBm");

                if (double.IsNaN(first))
                {
                    first = peak;
                    continue;
                }

                // 0.1 dB covers the window's scalloping between sizes; a missing 1/N would show as
                // 6.02 dB per doubling, which is sixty times this tolerance.
                Assert.Equal(first, peak, 1);
            }
        }

        [Fact]
        public void AMissingNormalisationWouldBeCaught()
        {
            // The guard is only worth having if it would fail. A level scaled by N, as the raw
            // 10*log10|X|^2 in the harness was, differs by 6.02 dB per doubling.
            double small = PeakOf(4096);
            double large = PeakOf(8192);

            double unnormalisedDifference = 20.0 * Math.Log10(8192.0 / 4096.0);

            Assert.True(unnormalisedDifference > 6.0);
            Assert.True(
                Math.Abs(large - small) < unnormalisedDifference / 10.0,
                "The levels differ by " + Math.Abs(large - small).ToString("F2") +
                " dB, which is the scale of a normalisation fault rather than of windowing.");
        }

        /// <summary>The peak level of a fixed carrier, computed through the product's own path.</summary>
        /// <remarks>
        /// Through <see cref="SpectrumComputer"/> deliberately. Computing magnitudes here would be
        /// the reimplementation that caused the confusion in the first place, and would test this
        /// file rather than the product.
        /// </remarks>
        private static double PeakOf(int points)
        {
            var computer = new SpectrumComputer(WindowType.FlatTop, null, null);

            var metadata = new IqBlockMetadata(
                points,
                2.0e6,
                1.0e9,
                isBaseband: false,
                fullScaleVolts: 1.0,
                referenceLevelDbm: 0.0,
                sequenceNumber: 1L,
                acquiredUtc: new DateTime(2026, 7, 28, 0, 0, 0, DateTimeKind.Utc),
                triggerOffsetSeconds: 0.0,
                triggerCorrectionsApplied: false,
                source: new FrontEndId("test"),
                extended: null);

            IqBlock block = IqBlock.Rent(metadata);
            Span<float> samples = block.GetSamples();

            // A tone on an exact bin at every size tested, so scalloping cannot masquerade as a
            // scaling error: 0.125 cycles/sample lands on a bin whenever N is a multiple of 8.
            for (int n = 0; n < points; n++)
            {
                double angle = 2.0 * Math.PI * 0.125 * n;

                samples[n * 2] = (float)(0.5 * Math.Cos(angle));
                samples[n * 2 + 1] = (float)(0.5 * Math.Sin(angle));
            }

            SpectrumFrame frame = computer.Compute(block);
            ReadOnlySpan<float> levels = frame.LevelsDbm;

            double peak = double.NegativeInfinity;

            for (int i = 0; i < levels.Length; i++)
            {
                if (levels[i] > peak)
                {
                    peak = levels[i];
                }
            }

            return peak;
        }
    }
}
