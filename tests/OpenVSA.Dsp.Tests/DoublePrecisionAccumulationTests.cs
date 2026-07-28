using System;
using OpenVSA.Dsp.Spectrum;
using OpenVSA.Dsp.Windowing;
using Xunit;
using Xunit.Abstractions;

namespace OpenVSA.Dsp.Tests
{
    /// <summary>
    /// <c>REQ-DSP-002</c>: averaging 100 000 frames of a constant-amplitude signal lands within
    /// 1e-9 relative of the analytic value, and the same sum in single precision demonstrably does
    /// not.
    /// </summary>
    /// <remarks>
    /// The second half is the whole point. "Use double" is an assertion about the code; the
    /// requirement asks for a demonstration that it matters, because a reviewer who has not seen
    /// single precision fail at this length will eventually decide the wider accumulator is
    /// unnecessary. Float has 24 bits of significand: once the running sum exceeds about 2^24 times
    /// the increment, each further addition rounds to no change at all and the average stalls
    /// short of the true value.
    /// </remarks>
    public class DoublePrecisionAccumulationTests
    {
        private const int Frames = 100000;
        private const float Level = -37.5f;

        private readonly ITestOutputHelper _output;

        /// <summary>Takes xunit's output sink.</summary>
        /// <param name="output">Where both errors are written.</param>
        public DoublePrecisionAccumulationTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void AveragingOneHundredThousandFramesStaysWithinOnePartInABillion()
        {
            var averager = new TraceAverager(AveragingType.RmsVideo, Frames);
            SpectrumFrame result = null;

            for (int i = 0; i < Frames; i++)
            {
                result = averager.Accumulate(Frame());
            }

            Assert.NotNull(result);

            ReadOnlySpan<float> levels = result.LevelsDbm;
            double worst = 0.0;

            for (int i = 0; i < levels.Length; i++)
            {
                worst = Math.Max(worst, Math.Abs((levels[i] - Level) / Level));
            }

            _output.WriteLine(
                Frames + " frames averaged, worst relative error " + worst.ToString("E3"));

            Assert.True(
                worst < 1e-9,
                "Averaging " + Frames + " constant frames drifted " + worst.ToString("E3") +
                " from the analytic value, which is worse than the 1e-9 REQ-DSP-002 requires.");
        }

        [Fact]
        public void TheSameSumInSinglePrecisionDemonstrablyFails()
        {
            // Not a claim about the product — a demonstration that the requirement is about
            // something real. Both accumulators add the identical sequence; only the width differs.
            // 0.1, not 1.0. Adding 1.0 a hundred thousand times is EXACT in float, because 10^5
            // is comfortably below the 2^24 where consecutive integers stop being representable —
            // the first version of this test used it and failed to demonstrate anything. 0.1 has
            // no exact binary representation, so every addition rounds and the error accumulates,
            // which is what a real signal average does.
            const double Increment = 0.1;

            float single = 0.0f;
            double wide = 0.0;

            for (int i = 0; i < Frames; i++)
            {
                single += (float)Increment;
                wide += Increment;
            }

            double expected = Frames * Increment;
            double singleError = Math.Abs((single - expected) / expected);
            double wideError = Math.Abs((wide - expected) / expected);

            _output.WriteLine(
                "after " + Frames + " additions of " + Increment + ": single " + single.ToString("F4") +
                " (relative error " + singleError.ToString("E3") + "), double " +
                wide.ToString("F4") + " (relative error " + wideError.ToString("E3") + "), expected " + expected.ToString("F4"));

            // 1e-10, not zero: 0.1 is not exactly representable in double either, so a hundred
            // thousand additions accumulate about 2e-12. The point is the six orders of magnitude
            // between the two, not that double is perfect.
            Assert.True(wideError < 1e-10, "Double precision drifted " + wideError.ToString("E3") + ".");
            Assert.True(singleError / wideError > 1000.0, "The two widths are too close to demonstrate anything.");

            Assert.True(
                singleError > 1e-9,
                "Single precision did not fail at " + Frames + " additions, so this test is no " +
                "longer demonstrating why REQ-DSP-002 exists and needs a longer run or a larger " +
                "increment.");
        }

        private static SpectrumFrame Frame()
        {
            var levels = new float[64];

            for (int i = 0; i < levels.Length; i++)
            {
                levels[i] = Level;
            }

            return SpectrumFrame.FromLevels(levels, 999.0e6, 1.0e3, WindowType.FlatTop, 3.8194);
        }
    }
}
