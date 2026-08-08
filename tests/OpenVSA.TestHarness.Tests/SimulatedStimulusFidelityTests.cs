using System;
using OpenVSA.TestHarness;
using Xunit;

namespace OpenVSA.TestHarness.Tests
{
    /// <summary>
    /// The simulated source against what the real E4438C actually does (issue #393).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every figure here was measured on the instrument on 2026-08-08, firmware C.05.85, Option 503,
    /// and is quoted in the test that pins it. <strong>These are not invented tolerances</strong>:
    /// if the simulator drifts from the hardware, one of these fails and says which way.
    /// </para>
    /// <para>
    /// The reason this matters is that CI has no bench. A simulator that accepts anything and
    /// reports it back unchanged lets harness logic that cannot cope with a coerced read-back pass
    /// every run and fail the first time it meets an instrument.
    /// </para>
    /// </remarks>
    public class SimulatedStimulusFidelityTests
    {
        [Fact]
        public void AFrequencyInsideTheRangeIsHonouredToTheHertz()
        {
            // Measured: 1 000 100 003 Hz read back as +1.0001000030000E+09, exactly.
            var source = new SimulatedStimulus();

            source.SetContinuousWave(1000100003.0, -20.0);

            Assert.Equal(1000100003.0, source.FrequencyHz);
        }

        [Theory]
        [InlineData(7.0e9, 3.0e9)]      // measured: 7 GHz -> +3.0000000000000E+09, -222 upper
        [InlineData(1000.0, 100e3)]     // measured: 1 kHz -> +1.0000000000000E+05, -222 lower
        public void AFrequencyOutOfRangeIsClippedAndNotRefused(double asked, double expected)
        {
            // The instrument CLIPS and carries on, leaving -222 in the error queue. It does not
            // refuse and it does not stop, so neither may the simulator: a throw here would let a
            // harness that cannot cope with clipping pass in CI.
            var source = new SimulatedStimulus();

            source.SetContinuousWave(asked, -20.0);

            Assert.Equal(expected, source.FrequencyHz);
        }

        [Theory]
        [InlineData(-13.774, -13.78)]
        [InlineData(-13.775, -13.78)]
        [InlineData(-13.7749, -13.78)]
        [InlineData(-13.77, -13.76)]    // exactly between two steps; the instrument goes up
        public void TheLevelIsQuantisedToTheInstrumentsOwnStep(double asked, double expected)
        {
            var source = new SimulatedStimulus();

            source.SetContinuousWave(1.0e9, asked);

            Assert.Equal(expected, source.LevelDbm, 6);
        }

        [Theory]
        [InlineData(40.0, 20.0)]        // measured: +40 dBm -> +2.00000000E+001, -222 upper
        [InlineData(-200.0, -136.0)]    // measured: -200 dBm -> -1.36000000E+002, -222 lower
        public void ALevelOutOfRangeIsClippedAndNotRefused(double asked, double expected)
        {
            var source = new SimulatedStimulus();

            source.SetContinuousWave(1.0e9, asked);

            Assert.Equal(expected, source.LevelDbm, 6);
        }

        [Theory]
        [InlineData(1000000.0)]
        [InlineData(250000.0)]
        [InlineData(137000.0)]
        public void ToneSpacingIsNotQuantised(double spacingHz)
        {
            // Measured: all three read back exactly. The 996 093.75 Hz seen in a comb scenario is
            // the ANALYSER's bin resolution, and an earlier note in this codebase wrongly recorded
            // it as the generator quantising.
            var source = new SimulatedStimulus();

            source.SetMultitone(1.0e9, 5, spacingHz, -20.0);

            Assert.Equal(spacingHz, source.ToneSpacingHz);
        }

        [Theory]
        [InlineData(5000000.0)]
        [InlineData(1234567.0)]
        [InlineData(50000.0)]
        public void NoiseBandwidthIsNotQuantised(double bandwidthHz)
        {
            // Measured: 1 234 567 Hz read back as +1.23456700E+006.
            var source = new SimulatedStimulus();

            source.SetNoise(1.0e9, bandwidthHz, -20.0);

            Assert.Equal(bandwidthHz, source.NoiseBandwidthHz);
        }

        [Fact]
        public void TheQuantisedLevelIsAWholeNumberOfSteps()
        {
            // A level that is not on the grid would be a simulator artefact rather than a
            // measurement, and comparing it in a scenario would report a phantom error.
            for (double asked = -30.0; asked <= -10.0; asked += 0.003)
            {
                double level = SimulatedStimulus.Level(asked);
                double steps = level / SimulatedStimulus.LevelStepDb;

                Assert.Equal(Math.Round(steps), steps, 6);
            }
        }

        [Fact]
        public void ClippingAppliesToEveryStimulusMode()
        {
            // The instrument's limits are the instrument's, not the CW path's. A comb or a noise
            // band at an impossible carrier is clipped exactly the same way.
            var source = new SimulatedStimulus();

            source.SetMultitone(7.0e9, 5, 1.0e6, 40.0);
            Assert.Equal(3.0e9, source.FrequencyHz);
            Assert.Equal(20.0, source.LevelDbm, 6);

            source.SetNoise(1000.0, 5.0e6, -200.0);
            Assert.Equal(100e3, source.FrequencyHz);
            Assert.Equal(-136.0, source.LevelDbm, 6);
        }
    }
}
