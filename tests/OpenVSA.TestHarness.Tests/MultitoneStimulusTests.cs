using System;
using OpenVSA.TestHarness;
using Xunit;

namespace OpenVSA.TestHarness.Tests
{
    /// <summary>
    /// The comb half of the stimulus contract (issue #393), with no hardware.
    /// </summary>
    public class MultitoneStimulusTests
    {
        [Fact]
        public void TheCombIsACapabilityAndIsAskedForRatherThanAssumed()
        {
            // #393's first architectural constraint in miniature: a source is not obliged to
            // produce a comb, and a scenario finds that out by asking rather than by failing
            // half way through a run.
            var source = new SimulatedStimulus();

            Assert.IsAssignableFrom<IStimulusSource>(source);
            Assert.IsAssignableFrom<IMultitoneStimulus>(source);
        }

        [Fact]
        public void ACarrierIsNotAComb()
        {
            // Stale state between scenarios: a CW scenario running after a comb one must not read
            // back the comb's tone count. The real source achieves this by reading MTONe:ARB:STATe
            // rather than remembering.
            var source = new SimulatedStimulus();

            source.SetMultitone(1.0e9, 5, 1.0e6, -20.0);
            Assert.Equal(5, source.ToneCount);

            source.SetContinuousWave(1.0e9, -20.0);

            Assert.Equal(0, source.ToneCount);
            Assert.Equal(0.0, source.ToneSpacingHz);
        }

        [Fact]
        public void AToneCountOutsideTheSourcesRangeIsRefused()
        {
            var source = new SimulatedStimulus();

            Assert.Throws<ArgumentOutOfRangeException>(
                () => source.SetMultitone(1.0e9, 1, 1.0e6, -20.0));

            Assert.Throws<ArgumentOutOfRangeException>(
                () => source.SetMultitone(1.0e9, source.MaximumTones + 1, 1.0e6, -20.0));
        }

        [Fact]
        public void TheExpectationComesFromTheSourcesReadBackNotTheRequest()
        {
            // The same discipline the CW scenarios already follow. A generator quantises the
            // spacing to its sample clock; a harness expecting what it asked for would report the
            // analyser as wrong by the difference.
            var source = new SimulatedStimulus { CoerceSpacingTo = 996093.75 };

            source.SetMultitone(1.0e9, 5, 1.0e6, -20.0);

            var scenario = new VerificationScenario(
                "spacing", VerifiedQuantity.ToneSpacingHz,
                1.0e9, -20.0, 1.0e9, 10e6, 801, 60e3, true, 5, 1.0e6);

            Assert.Equal(996093.75, scenario.ExpectedFrom(source));
        }

        [Fact]
        public void FlatnessExpectsZeroBecauseTheSourceCannotMarkItsOwnWork()
        {
            var source = new SimulatedStimulus();

            source.SetMultitone(1.0e9, 5, 1.0e6, -20.0);

            var scenario = new VerificationScenario(
                "flatness", VerifiedQuantity.ToneFlatnessDb,
                1.0e9, -20.0, 1.0e9, 10e6, 801, 3.0, true, 5, 1.0e6);

            Assert.Equal(0.0, scenario.ExpectedFrom(source));
        }

        [Fact]
        public void ACombScenarioAgainstASourceThatCannotCombSaysSo()
        {
            // Named, not skipped. A harness that quietly dropped what its generator could not
            // produce would report a clean run over a reduced set.
            var scenario = new VerificationScenario(
                "spacing", VerifiedQuantity.ToneSpacingHz,
                1.0e9, -20.0, 1.0e9, 10e6, 801, 60e3, true, 5, 1.0e6);

            InvalidOperationException failure = Assert.Throws<InvalidOperationException>(
                () => scenario.ExpectedFrom(new CarrierOnlySource()));

            Assert.Contains("cannot produce one", failure.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void OnlyAScenarioAskingForTwoOrMoreTonesIsACombScenario()
        {
            // Zero is not "two by default". A scenario that did not ask for a comb must get a
            // carrier, or it inherits whatever the previous scenario left on the generator.
            var carrier = new VerificationScenario(
                "level", VerifiedQuantity.PeakLevelDbm, 1.0e9, -20.0, 1.0e9, 10e6, 801, 2.0);

            var comb = new VerificationScenario(
                "count", VerifiedQuantity.ToneCount,
                1.0e9, -20.0, 1.0e9, 10e6, 801, 0.5, true, 5, 1.0e6);

            Assert.False(carrier.NeedsMultitone);
            Assert.True(comb.NeedsMultitone);
        }

        [Fact]
        public void EachQuantityReportsItsOwnUnits()
        {
            // A count reported in hertz, or a flatness in hertz, makes a failure message wrong in
            // a way that sends the reader to the wrong instrument.
            Assert.Equal("tones", Scenario(VerifiedQuantity.ToneCount).Units);
            Assert.Equal("Hz", Scenario(VerifiedQuantity.ToneSpacingHz).Units);
            Assert.Equal("dB", Scenario(VerifiedQuantity.ToneFlatnessDb).Units);
            Assert.Equal("dB", Scenario(VerifiedQuantity.PeakLevelDbm).Units);
            Assert.Equal("Hz", Scenario(VerifiedQuantity.PeakFrequencyHz).Units);
        }

        private static VerificationScenario Scenario(VerifiedQuantity what) =>
            new VerificationScenario(
                what.ToString(), what, 1.0e9, -20.0, 1.0e9, 10e6, 801, 1.0, true, 5, 1.0e6);

        /// <summary>A source with no comb, to prove the capability is actually checked.</summary>
        private sealed class CarrierOnlySource : IStimulusSource
        {
            public string DisplayName => "Carrier-only source";

            public bool IsOutputEnabled { get; private set; }

            public double FrequencyHz { get; private set; }

            public double LevelDbm { get; private set; }

            public void Connect()
            {
            }

            public void SetContinuousWave(double frequencyHz, double levelDbm)
            {
                FrequencyHz = frequencyHz;
                LevelDbm = levelDbm;
            }

            public void SetOutput(bool enabled) => IsOutputEnabled = enabled;

            public void Refresh()
            {
            }

            public void Dispose()
            {
            }
        }
    }
}
