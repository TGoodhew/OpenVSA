using System;
using OpenVSA.Dsp.Zoom;
using Xunit;

namespace OpenVSA.Dsp.Tests
{
    /// <summary>
    /// <c>REQ-DSP-023a</c>: the Kaiser design the downconverter's decimation filters come from.
    /// </summary>
    /// <remarks>
    /// The stopband and ripple assertions measure the designed taps rather than trusting Kaiser's
    /// estimate. The estimate is what chooses the length; whether the length delivers is the thing
    /// under test, and it is the whole reason the design is closed form instead of iterative.
    /// </remarks>
    public class FirDesignTests
    {
        [Fact]
        public void BesselI0MatchesItsPublishedValues()
        {
            Assert.Equal(1.0, FirDesign.BesselI0(0.0), 15);
            Assert.Equal(1.2660658777520084, FirDesign.BesselI0(1.0), 12);
            Assert.Equal(2.2795853023360673, FirDesign.BesselI0(2.0), 12);
            Assert.Equal(27.239871823604442, FirDesign.BesselI0(5.0), 10);
            Assert.Equal(2815.7166284662544, FirDesign.BesselI0(10.0), 7);
        }

        [Fact]
        public void TheWindowIsUnityAtTheCentreAndZeroAtTheEnds()
        {
            foreach (double beta in new[] { 0.0, 3.5, 8.96, 11.2 })
            {
                Assert.Equal(1.0, FirDesign.Kaiser(0.0, beta), 12);
                Assert.Equal(0.0, FirDesign.Kaiser(1.0, beta), 12);
                Assert.Equal(0.0, FirDesign.Kaiser(-1.0, beta), 12);
                Assert.Equal(
                    FirDesign.Kaiser(0.37, beta), FirDesign.Kaiser(-0.37, beta), 15);
            }
        }

        [Fact]
        public void BetaIsZeroBelowTwentyOneDecibels()
        {
            // Below 21 dB the rectangular window already does better than the shape parameter can,
            // so Kaiser's fit returns nothing to apply.
            Assert.Equal(0.0, FirDesign.Beta(0.5), 15);
            Assert.Equal(0.0, FirDesign.Beta(20.999), 15);
            Assert.Equal(0.0, FirDesign.Beta(21.0), 15);
        }

        [Fact]
        public void BetaIsContinuousWhereItsBranchesMeet()
        {
            // The three-part fit is not continuous by construction, so a transcription error in one
            // branch shows up as a step at the join rather than as anything obviously wrong.
            double below = FirDesign.Beta(50.0);
            double above = FirDesign.Beta(50.0001);

            // Kaiser's own fit has a small step here - about 0.018 - so this bounds the join
            // rather than closing it. A mistyped constant moves beta by far more than that.
            Assert.True(
                Math.Abs(above - below) < 0.05,
                "beta stepped from " + below + " to " + above + " across the 50 dB join.");
            Assert.True(above > 4.5 && above < 4.6, "beta at 50 dB should be about 4.55, was " + above);
        }

        [Fact]
        public void BetaRisesWithTheAttenuationAsked()
        {
            double previous = -1.0;

            foreach (double db in new[] { 21.0, 30.0, 45.0, 60.0, 80.0, 110.0, 150.0 })
            {
                double beta = FirDesign.Beta(db);

                Assert.True(beta > previous, "beta fell going from below " + db + " dB to it.");
                previous = beta;
            }
        }

        [Fact]
        public void TapCountsAreOddAndAtLeastThree()
        {
            foreach (double width in new[] { 0.4, 0.1, 0.013, 7.8125e-4 })
            {
                foreach (double db in new[] { 1.0, 40.0, 110.0, 180.0 })
                {
                    int length = FirDesign.TapCountFor(width, db);

                    Assert.True(length >= 3, "length " + length + " for " + width + ", " + db + " dB");
                    Assert.True(length % 2 == 1, "length " + length + " is even");
                    Assert.Equal(length, FirDesign.LowPass(width, width, db).Length);
                }
            }
        }

        [Fact]
        public void TapCountGrowsAsTheTransitionNarrowsAndTheStopbandDeepens()
        {
            Assert.True(
                FirDesign.TapCountFor(0.01, 110.0) > FirDesign.TapCountFor(0.02, 110.0),
                "halving the transition width should roughly double the taps.");
            Assert.True(
                FirDesign.TapCountFor(0.01, 110.0) > FirDesign.TapCountFor(0.01, 60.0),
                "a deeper stopband should cost taps.");
        }

        [Fact]
        public void TheMarginIsWhatSeparatesTheEstimateFromTheDesign()
        {
            // TapCountFor is LengthFor with the margin applied, and nothing else. Stating it here
            // means a change to one without the other cannot pass unnoticed.
            Assert.Equal(
                FirDesign.LengthFor(0.0125, 100.0 + FirDesign.MarginDb),
                FirDesign.TapCountFor(0.0125, 100.0));
            Assert.True(
                FirDesign.TapCountFor(0.0125, 100.0) > FirDesign.LengthFor(0.0125, 100.0),
                "the margin should cost taps; if it does not, it is not doing anything.");
        }

        [Fact]
        public void AnImpossiblyNarrowTransitionSaturatesRatherThanWrapping()
        {
            // The count is checked against a limit by callers, and a wrapped negative would pass
            // every limit there is.
            Assert.Equal(int.MaxValue, FirDesign.TapCountFor(1e-12, 110.0));
        }

        [Fact]
        public void TheTapsAreSymmetricAndOfOddLength()
        {
            // What makes the phase exactly linear and the group delay a whole number of samples.
            double[] taps = FirDesign.LowPass(0.13, 0.031, 110.0);

            Assert.True(taps.Length % 2 == 1);

            for (int n = 0; n < taps.Length / 2; n++)
            {
                Assert.Equal(taps[n], taps[taps.Length - 1 - n], 15);
            }
        }

        [Fact]
        public void TheDcGainIsExactlyUnity()
        {
            // Not nearly unity. A zoom that changed the amplitude of what it zoomed into by even a
            // thousandth of a decibel would put the whole amplitude chain in doubt.
            foreach (double cutoff in new[] { 0.25, 0.0625, 0.001953125 })
            {
                double[] taps = FirDesign.LowPass(cutoff, cutoff * 0.4, 110.0);
                double sum = 0.0;

                foreach (double tap in taps)
                {
                    sum += tap;
                }

                Assert.Equal(1.0, sum, 14);
            }
        }

        [Theory]
        [InlineData(0.25, 0.1, 110.0)]
        [InlineData(0.125, 0.05, 110.0)]
        [InlineData(0.0625, 0.025, 100.0)]
        [InlineData(0.03125, 0.0125, 80.0)]
        [InlineData(0.2, 0.06, 60.0)]
        public void TheStopbandReachesTheAttenuationAskedFor(
            double cutoff, double width, double stopbandDb)
        {
            double[] taps = FirDesign.LowPass(cutoff, width, stopbandDb);
            double edge = cutoff + width / 2.0;
            double worst = 0.0;
            double worstAt = 0.0;

            // Densely enough to land in the stopband lobes rather than between them: the response
            // has of the order of one lobe per two taps across the band.
            int points = 8 * taps.Length;

            for (int i = 0; i <= points; i++)
            {
                double f = edge + (0.5 - edge) * i / points;
                double response = Response(taps, f);

                if (response > worst)
                {
                    worst = response;
                    worstAt = f;
                }
            }

            double achievedDb = -20.0 * Math.Log10(worst);

            Assert.True(
                achievedDb >= stopbandDb,
                "asked for " + stopbandDb + " dB, achieved " + achievedDb.ToString("F2") +
                " dB, worst at " + worstAt.ToString("F6") + " cycles/sample.");
        }

        [Theory]
        [InlineData(0.25, 0.1, 110.0)]
        [InlineData(0.0625, 0.025, 110.0)]
        [InlineData(0.03125, 0.0125, 80.0)]
        public void PassbandRippleTracksTheStopbandRipple(
            double cutoff, double width, double stopbandDb)
        {
            // A windowed design has near-equal passband and stopband ripple, which is why meeting
            // REQ-DSP-023a's 100 dB stopband gives its 0.05 dB passband ripple for nothing: at
            // 110 dB the ripple is about 5e-5 dB.
            double[] taps = FirDesign.LowPass(cutoff, width, stopbandDb);
            double edge = cutoff - width / 2.0;
            double worst = 0.0;
            int points = 8 * taps.Length;

            for (int i = 0; i <= points; i++)
            {
                double deviation = Math.Abs(Response(taps, edge * i / points) - 1.0);

                if (deviation > worst)
                {
                    worst = deviation;
                }
            }

            double allowed = Math.Pow(10.0, -stopbandDb / 20.0);

            Assert.True(
                worst < allowed * 4.0,
                "passband deviation " + worst.ToString("E3") + " against a stopband ripple of " +
                allowed.ToString("E3") + ".");
        }

        [Theory]
        [InlineData(0.0)]
        [InlineData(-0.1)]
        [InlineData(0.5)]
        [InlineData(0.7)]
        [InlineData(double.NaN)]
        public void ACutoffOutsideTheOpenIntervalIsRefused(double cutoff)
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => FirDesign.LowPass(cutoff, 0.05, 110.0));
        }

        [Theory]
        [InlineData(0.0)]
        [InlineData(-0.05)]
        [InlineData(double.NaN)]
        [InlineData(double.PositiveInfinity)]
        public void ATransitionWidthThatIsNotPositiveAndFiniteIsRefused(double width)
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => FirDesign.LowPass(0.2, width, 110.0));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => FirDesign.LengthFor(width, 110.0));
        }

        [Theory]
        [InlineData(0.0)]
        [InlineData(-40.0)]
        [InlineData(double.NaN)]
        [InlineData(double.PositiveInfinity)]
        public void AStopbandThatIsNotPositiveAndFiniteIsRefused(double stopbandDb)
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => FirDesign.LowPass(0.2, 0.05, stopbandDb));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => FirDesign.LengthFor(0.05, stopbandDb));
        }

        /// <summary>Amplitude response at a normalised frequency, in cycles per sample.</summary>
        private static double Response(double[] taps, double frequency)
        {
            double re = 0.0;
            double im = 0.0;

            for (int n = 0; n < taps.Length; n++)
            {
                double angle = -2.0 * Math.PI * frequency * n;

                re += taps[n] * Math.Cos(angle);
                im += taps[n] * Math.Sin(angle);
            }

            return Math.Sqrt(re * re + im * im);
        }
    }
}
