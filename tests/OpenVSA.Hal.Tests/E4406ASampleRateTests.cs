using System;
using OpenVSA.Hal.Visa;
using Xunit;
using Xunit.Abstractions;

namespace OpenVSA.Hal.Tests
{
    /// <summary>
    /// The E4406A's bandwidth-to-sample-rate law against the bench readings it was derived from
    /// (<c>REQ-E44-002b</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The point of this file is that the two previous models could not be tested.</strong> A
    /// linear interpolation living in a private nested class inside a VISA transport reported ×0.170
    /// of the true rate without anything noticing, because nothing could compare it with a table of
    /// instrument readings. The numbers below are the real ones, measured on `US40062429` firmware
    /// `A.08.10` on 24 August 2026 by `OpenVSA.Verify --probe-bandwidth`, and the full sweep is in
    /// the repository at `evidence/req-e44-007/bandwidth-law.tsv`.
    /// </para>
    /// <para>
    /// These are therefore not tests of arithmetic. They are a bench measurement, pinned, so a change
    /// to the model has to argue with an instrument rather than with an opinion.
    /// </para>
    /// </remarks>
    public class E4406ASampleRateTests
    {
        /// <summary>The instrument's maximum, measured: 66.667 ns a sample.</summary>
        private const double MaxSampleRateHz = 15e6;

        /// <summary>
        /// The widest bandwidth still sampled at the maximum rate, measured: 3.1 MHz.
        /// </summary>
        private const double ReferenceBandwidthHz = 3.1e6;

        /// <summary>
        /// The narrowest commanded bandwidth at which the model reproduces the instrument exactly.
        /// </summary>
        /// <remarks>
        /// Below this the instrument's own step list stops coinciding with <c>W₁/n</c> for integer
        /// <em>n</em> — it picked 3 094 ticks where the model says 3 100, and 308 805 where the model
        /// says 310 000. Measured, not guessed: over the sweep's 40 points the error above this
        /// bandwidth is zero to the last digit reported, and below it never exceeds 1.4 %.
        /// </remarks>
        private const double ExactAboveHz = 17e3;

        private readonly ITestOutputHelper _output;

        public E4406ASampleRateTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Theory]
        // Commanded bandwidth, and the sample rate the instrument produced for it. Every rung of the
        // sweep's geometric ladder from 17 kHz up, which is why the commanded figures are untidy.
        [InlineData(17012.5, 82417.6)]
        [InlineData(24244.6, 118110.0)]
        [InlineData(34551.1, 168539.0)]
        [InlineData(49238.8, 241935.0)]
        [InlineData(70170.4, 340909.0)]
        [InlineData(100000.0, 483871.0)]
        [InlineData(142510.0, 714286.0)]
        [InlineData(203092.0, 1e6)]
        [InlineData(289427.0, 1.5e6)]
        [InlineData(412463.0, 2.14286e6)]
        [InlineData(587802.0, 3e6)]
        [InlineData(837678.0, 5e6)]
        [InlineData(1.19378e6, 7.5e6)]
        [InlineData(1.70125e6, 15e6)]
        [InlineData(2.42446e6, 15e6)]
        [InlineData(3.45511e6, 15e6)]
        [InlineData(4.92388e6, 15e6)]
        [InlineData(7.01704e6, 15e6)]
        [InlineData(10e6, 15e6)]
        public void TheLawReproducesWhatTheInstrumentDid(double commandedHz, double measuredHz)
        {
            Assert.True(commandedHz >= ExactAboveHz, "This row belongs in the other theory.");

            double predicted = E4406ASampleRate.For(
                commandedHz, ReferenceBandwidthHz, MaxSampleRateHz);

            _output.WriteLine(
                commandedHz + " Hz commanded: predicted " + predicted + ", measured " + measuredHz);

            // A part in ten thousand, which is far tighter than one decimation step, so a step chosen
            // wrongly cannot pass. The measured figures are quoted to six digits, which is what sets
            // the floor on how tight this can be.
            Assert.True(
                Math.Abs(predicted - measuredHz) <= measuredHz * 1e-4,
                "Predicted " + predicted + " Hz for " + commandedHz +
                " Hz commanded; the instrument produced " + measuredHz + " Hz.");
        }

        [Theory]
        // Below 17 kHz the instrument's step list and W1/n part company. These are the rungs where
        // it happened, with the worst of them first: recorded rather than tuned away, because an
        // estimate used to size a block does not care about 1.4 % and the next person reading this
        // model deserves to know exactly where it stops being exact.
        [InlineData(14.251, 68.0041, 1.401)]
        [InlineData(20.3092, 97.0685, 1.238)]
        [InlineData(10.0, 48.5743, 0.385)]
        [InlineData(345.511, 1680.67, 0.524)]
        [InlineData(1000.0, 4848.09, 0.194)]
        [InlineData(5878.02, 28571.4, 0.380)]
        [InlineData(11937.8, 58139.5, 0.386)]
        public void BelowThatTheModelIsCloseAndTheGapIsKnown(
            double commandedHz, double measuredHz, double knownErrorPercent)
        {
            double predicted = E4406ASampleRate.For(
                commandedHz, ReferenceBandwidthHz, MaxSampleRateHz);

            double errorPercent = Math.Abs(predicted - measuredHz) / measuredHz * 100.0;

            _output.WriteLine(
                commandedHz + " Hz commanded: predicted " + predicted + ", measured " + measuredHz +
                ", out by " + errorPercent.ToString("F3") + " %");

            // Pinned both ways. An upper bound alone would pass a model that had drifted to a
            // different wrong answer, and the gap is a property of the instrument worth noticing if
            // it ever changes.
            Assert.True(
                Math.Abs(errorPercent - knownErrorPercent) < 0.01,
                "The gap at " + commandedHz + " Hz is now " + errorPercent.ToString("F3") +
                " %, where it measured " + knownErrorPercent + " %.");
        }

        [Theory]
        // Either side of the two boundaries the sweep bisected — 1.03685 MHz for 5 to 7.5 MS/s and
        // 1.5578 MHz for 7.5 to 15 MS/s. Set about 1.5 % clear of each, because the model places its
        // steps at W1/n and the instrument's are up to half a per cent above that; testing closer
        // would assert a coincidence rather than the step.
        [InlineData(1.020e6, 5e6)]
        [InlineData(1.055e6, 7.5e6)]
        [InlineData(1.535e6, 7.5e6)]
        [InlineData(1.580e6, 15e6)]
        public void TheStepsFallWhereTheyWereBisectedTo(double commandedHz, double expectedHz)
        {
            double predicted = E4406ASampleRate.For(
                commandedHz, ReferenceBandwidthHz, MaxSampleRateHz);

            _output.WriteLine(commandedHz + " Hz commanded -> " + predicted + " Hz");

            Assert.Equal(expectedHz, predicted, 3);
        }

        [Fact]
        public void EverySampleRateIsTheMaximumDividedByAWholeNumber()
        {
            // The instrument decimates a fixed clock, so nothing else is reachable — confirmed at all
            // 40 measured points, where the tick count ran from 1 to 308 805 and every one was whole.
            // Asserted across the range rather than at the rungs, because it is the shape of the law
            // and not a property of where the ladder landed.
            for (double commanded = 10.0; commanded < 12e6; commanded *= 1.1)
            {
                double rate = E4406ASampleRate.For(
                    commanded, ReferenceBandwidthHz, MaxSampleRateHz);

                double steps = MaxSampleRateHz / rate;

                Assert.True(
                    Math.Abs(steps - Math.Round(steps)) < 1e-9,
                    commanded + " Hz gave " + rate + " Hz, the maximum over " + steps + ".");
                Assert.True(rate <= MaxSampleRateHz + 1e-9, "The rate exceeded the maximum.");
            }
        }

        [Fact]
        public void TheRateNeverRisesAsTheBandwidthNarrows()
        {
            // A staircase, but a monotone one. A model that inverted a floor somewhere could still
            // pass every rung above and fail here.
            double previous = double.MaxValue;

            for (double commanded = 12e6; commanded > 10.0; commanded /= 1.05)
            {
                double rate = E4406ASampleRate.For(
                    commanded, ReferenceBandwidthHz, MaxSampleRateHz);

                Assert.True(
                    rate <= previous + 1e-9,
                    "At " + commanded + " Hz the rate rose to " + rate + " from " + previous + ".");

                previous = rate;
            }
        }

        [Theory]
        // Readings from inside the tracking region: actual bandwidth and sample period, and the
        // reference bandwidth all of them must yield. Each landed on a different decimation step,
        // which is the property that lets connect take one reading without knowing in advance which
        // step it will get.
        [InlineData(100000.0, 2066.67e-9)]
        [InlineData(310000.0, 666.667e-9)]
        [InlineData(1.03333e6, 200e-9)]
        [InlineData(1.55e6, 133.333e-9)]
        [InlineData(3.1e6, 66.6667e-9)]
        public void OneReadingFromTheTrackingRegionGivesTheReferenceBandwidth(
            double actualHz, double apertureSeconds)
        {
            double reference = E4406ASampleRate.ReferenceBandwidthFrom(
                actualHz, apertureSeconds, MaxSampleRateHz);

            _output.WriteLine(
                actualHz + " Hz at " + (apertureSeconds * 1e9) + " ns -> " + reference + " Hz");

            Assert.True(
                Math.Abs(reference - ReferenceBandwidthHz) <= ReferenceBandwidthHz * 1e-4,
                "Recovered " + reference + " Hz rather than " + ReferenceBandwidthHz + " Hz.");
        }

        [Fact]
        public void AReadingFromTheClampedRegionDoesNotGiveTheReferenceBandwidth()
        {
            // A limit, not a defect. Above the reference bandwidth the instrument holds its rate and
            // widens the filter, so the arithmetic returns the filter width instead — 6.7 MHz was
            // really measured for a 5 MHz command. This is why connect probes at a thirty-second of
            // the maximum rather than at the maximum, and a test says so because whoever
            // "simplifies" that probe will not otherwise find out.
            double recovered = E4406ASampleRate.ReferenceBandwidthFrom(
                6.7e6, 66.6667e-9, MaxSampleRateHz);

            Assert.Equal(6.7e6, recovered, 0);
            Assert.NotEqual(ReferenceBandwidthHz, recovered, 0);
        }

        [Theory]
        [InlineData(0.0, 3.1e6, 15e6, 15e6)]
        [InlineData(1e6, 0.0, 15e6, 15e6)]
        [InlineData(double.PositiveInfinity, 3.1e6, 15e6, 15e6)]
        [InlineData(1e6, 3.1e6, 0.0, 0.0)]
        public void MissingConstantsFallBackToTheMaximumRatherThanToNonsense(
            double commandedHz, double referenceHz, double maximumHz, double expectedHz)
        {
            // Connect can fail to measure the reference bandwidth — a timeout, or a firmware that
            // answers differently — and a plan built on a zero or a NaN would size a block from it.
            // The maximum is the same answer the old interpolation gave at the top of its range and
            // the only safe one when there is nothing to compute from.
            Assert.Equal(
                expectedHz, E4406ASampleRate.For(commandedHz, referenceHz, maximumHz), 3);
        }
    }
}
