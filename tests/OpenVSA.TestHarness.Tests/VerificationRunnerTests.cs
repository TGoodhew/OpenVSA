using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OpenVSA.Hal.Sim;
using OpenVSA.TestHarness;
using Xunit;

namespace OpenVSA.TestHarness.Tests
{
    /// <summary>
    /// The harness's own logic, with no bench attached.
    /// </summary>
    /// <remarks>
    /// A cross-validation harness is only worth trusting if its own reasoning is tested: where the
    /// expectation comes from, whether a failure is reported with its numbers, and whether a
    /// scenario that should fail does. Run against the simulated source, the measurements are not
    /// real — but the decisions are the same ones it makes on the bench.
    /// </remarks>
    public class VerificationRunnerTests
    {
        [Fact]
        public async Task AToneAtTheCentreIsMeasuredAtItsOwnFrequency()
        {
            // The simulated front end synthesises its tone at the centre frequency, so a
            // frequency scenario centred on the generator's carrier should agree.
            using (var frontEnd = new SimulatedFrontEnd())
            using (var stimulus = new SimulatedStimulus())
            {
                stimulus.Connect();

                var scenario = new VerificationScenario(
                    "centred tone", VerifiedQuantity.PeakFrequencyHz,
                    stimulusFrequencyHz: 1e9, stimulusLevelDbm: -20.0,
                    centerFrequencyHz: 1e9, spanHz: 10e6, frequencyPoints: 801, tolerance: 60e3);

                var runner = new VerificationRunner(frontEnd, stimulus);
                VerificationResult result = await runner.RunOneAsync(scenario, CancellationToken.None);

                Assert.True(result.Passed, result.ToString());
                Assert.Equal(1e9, result.Measured, 0);
            }
        }

        [Fact]
        public async Task TheExpectationComesFromTheGeneratorsReadBack_NotFromTheRequest()
        {
            // The failure this guards against has already happened on the bench: a generator
            // retuned between runs made a correct measurement look like a mirrored spectrum. A
            // harness that remembered what it asked for would have blamed the analyser.
            using (var frontEnd = new SimulatedFrontEnd())
            using (var stimulus = new SimulatedStimulus { CoerceFrequencyTo = 1.234e9 })
            {
                stimulus.Connect();

                var scenario = new VerificationScenario(
                    "coerced carrier", VerifiedQuantity.PeakFrequencyHz,
                    stimulusFrequencyHz: 1e9, stimulusLevelDbm: -20.0,
                    centerFrequencyHz: 1e9, spanHz: 10e6, frequencyPoints: 801, tolerance: 60e3);

                var runner = new VerificationRunner(frontEnd, stimulus);
                VerificationResult result = await runner.RunOneAsync(scenario, CancellationToken.None);

                // The expectation followed the generator, so this correctly fails: the analyser
                // is measuring 1 GHz and the source says it is producing 1.234 GHz.
                Assert.Equal(1.234e9, result.Expected, 0);
                Assert.False(result.Passed);
            }
        }

        [Fact]
        public async Task AFailingScenarioNamesMeasuredExpectedAndMargin()
        {
            // REQ-E44-007: a failure names the values, not just "failed". A result that says only
            // that something is wrong sends the reader back to the bench to find out what.
            using (var frontEnd = new SimulatedFrontEnd())
            using (var stimulus = new SimulatedStimulus())
            {
                stimulus.Connect();

                var scenario = new VerificationScenario(
                    "deliberately wrong", VerifiedQuantity.PeakFrequencyHz,
                    stimulusFrequencyHz: 2e9, stimulusLevelDbm: -20.0,
                    centerFrequencyHz: 1e9, spanHz: 10e6, frequencyPoints: 801, tolerance: 1e3);

                var runner = new VerificationRunner(frontEnd, stimulus);
                VerificationResult result = await runner.RunOneAsync(scenario, CancellationToken.None);

                Assert.False(result.Passed);

                string report = result.ToString();
                Assert.Contains("FAIL", report);
                Assert.Contains("measured", report);
                Assert.Contains("expected", report);
                Assert.Contains("margin", report);
                Assert.True(result.Margin < 0.0, "A failure must report a negative margin.");
            }
        }

        [Fact]
        public async Task EveryDefaultScenarioRunsAndReports()
        {
            // The catalogue is exercised end to end. Against the simulated source most will not
            // pass - it puts its tone at the centre frequency whatever the generator says - so
            // what is asserted is that each produces a result rather than throwing.
            using (var frontEnd = new SimulatedFrontEnd())
            using (var stimulus = new SimulatedStimulus())
            {
                stimulus.Connect();

                var runner = new VerificationRunner(frontEnd, stimulus);
                IReadOnlyList<VerificationResult> results = await runner.RunAsync(
                    VerificationScenario.Default(), CancellationToken.None);

                Assert.Equal(6, results.Count);
                Assert.All(results, r => Assert.NotNull(r.Scenario));
                Assert.All(results, r => Assert.False(string.IsNullOrEmpty(r.ToString())));
            }
        }

        [Fact]
        public void TheCatalogueTestsBothSidesOfCentre()
        {
            // A mirrored spectrum that is only ever tested on one side of centre passes. The
            // catalogue therefore has to carry an offset of each sign.
            IReadOnlyList<VerificationScenario> scenarios = VerificationScenario.Default();

            var offsets = scenarios
                .Where(s => s.What == VerifiedQuantity.PeakOffsetHz)
                .Select(s => s.StimulusFrequencyHz - s.CenterFrequencyHz)
                .ToList();

            Assert.Contains(offsets, o => o > 0.0);
            Assert.Contains(offsets, o => o < 0.0);

            // And asymmetric magnitudes, so a mirror cannot produce the same answer by symmetry.
            Assert.NotEqual(Math.Abs(offsets[0]), Math.Abs(offsets[1]), 3);
        }

        [Fact]
        public void AScenarioNeedsANameAndAPositiveTolerance()
        {
            Assert.Throws<ArgumentException>(() => new VerificationScenario(
                string.Empty, VerifiedQuantity.PeakLevelDbm, 1e9, -20, 1e9, 1e6, 801, 1.0));

            Assert.Throws<ArgumentOutOfRangeException>(() => new VerificationScenario(
                "no tolerance", VerifiedQuantity.PeakLevelDbm, 1e9, -20, 1e9, 1e6, 801, 0.0));
        }

        [Fact]
        public void ARunnerNeedsBothSidesOfTheBench()
        {
            using (var frontEnd = new SimulatedFrontEnd())
            {
                Assert.Throws<ArgumentNullException>(() => new VerificationRunner(null, new SimulatedStimulus()));
                Assert.Throws<ArgumentNullException>(() => new VerificationRunner(frontEnd, null));
            }
        }
    }
}
