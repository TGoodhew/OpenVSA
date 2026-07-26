using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OpenVSA.Measurement.State;
using Xunit;
using Xunit.Abstractions;

namespace OpenVSA.Measurement.Tests.State
{
    /// <summary>
    /// <c>REQ-STA-004</c> context matching on recall, and <c>REQ-STA-005</c> presets.
    /// </summary>
    public class StateRecallTests : IDisposable
    {
        private readonly string _directory;
        private readonly ITestOutputHelper _output;

        public StateRecallTests(ITestOutputHelper output)
        {
            _output = output;
            _directory = Path.Combine(
                Path.GetTempPath(), "OpenVSA.PresetTests." + Guid.NewGuid().ToString("N"));
        }

        public void Dispose()
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }

        [Fact]
        public void EachMeasurementIsRestoredToTheContextOfItsOwnName()
        {
            var saved = new ApplicationState
            {
                Measurements =
                {
                    new MeasurementState { ContextName = "Uplink", CenterFrequencyHz = 1.95e9 },
                    new MeasurementState { ContextName = "Downlink", CenterFrequencyHz = 2.14e9 },
                },
            };

            Dictionary<string, MeasurementState> contexts = Contexts("Downlink", "Uplink");

            StateRecall.Apply(saved, contexts);

            // Matched by name, not by position - the contexts here are in the opposite order.
            Assert.Equal(1.95e9, contexts["Uplink"].CenterFrequencyHz, 3);
            Assert.Equal(2.14e9, contexts["Downlink"].CenterFrequencyHz, 3);
        }

        [Fact]
        public void AMismatchRefusesTheWholeRecallAndLeavesTheConfigurationIntact()
        {
            // A partial apply is the failure this exists to prevent, so the check is a full
            // comparison of the configuration before and after the refused recall - not a spot
            // check of the context that did match.
            var saved = new ApplicationState
            {
                Measurements =
                {
                    new MeasurementState { ContextName = "Uplink", CenterFrequencyHz = 1.95e9 },
                    new MeasurementState { ContextName = "Sidelink", CenterFrequencyHz = 5.9e9 },
                },
            };

            Dictionary<string, MeasurementState> contexts = Contexts("Uplink", "Downlink");
            Dictionary<string, MeasurementState> before = Contexts("Uplink", "Downlink");

            ContextMismatchException failure =
                Assert.Throws<ContextMismatchException>(() => StateRecall.Apply(saved, contexts));

            _output.WriteLine(failure.Message);

            foreach (string name in before.Keys)
            {
                string difference;

                Assert.True(
                    StateReflection.Same(before[name], contexts[name], out difference),
                    "'" + name + "' changed during a refused recall: " + difference + ".");
            }
        }

        [Fact]
        public void TheErrorNamesTheContextsThatDidNotMatchAndWhatWasExpected()
        {
            var saved = new ApplicationState
            {
                Measurements =
                {
                    new MeasurementState { ContextName = "Sidelink" },
                    new MeasurementState { ContextName = "Backhaul" },
                },
            };

            ContextMismatchException failure = Assert.Throws<ContextMismatchException>(
                () => StateRecall.Apply(saved, Contexts("Uplink", "Downlink")));

            _output.WriteLine(failure.Message);

            Assert.Contains("Sidelink", failure.Message);
            Assert.Contains("Backhaul", failure.Message);
            Assert.Contains("Uplink", failure.Message);
            Assert.Contains("Downlink", failure.Message);
            Assert.Contains("Nothing has been changed", failure.Message);

            Assert.Equal(new[] { "Sidelink", "Backhaul" }, failure.Missing.ToArray());
            Assert.Equal(new[] { "Downlink", "Uplink" }, failure.Available.ToArray());
        }

        [Fact]
        public void ContextsTheStateDoesNotNameAreLeftAlone()
        {
            var saved = new ApplicationState
            {
                Measurements = { new MeasurementState { ContextName = "Uplink", SpanHz = 1e6 } },
            };

            Dictionary<string, MeasurementState> contexts = Contexts("Uplink", "Downlink");
            double downlink = contexts["Downlink"].SpanHz;

            StateRecall.Apply(saved, contexts);

            Assert.Equal(1e6, contexts["Uplink"].SpanHz, 3);
            Assert.Equal(downlink, contexts["Downlink"].SpanHz, 3);
        }

        [Fact]
        public void ContextNamesAreMatchedOrdinally()
        {
            // Not culture-aware: a context called "I" must not match one called "ı" because the
            // state happened to be recalled on a Turkish system.
            var saved = new ApplicationState
            {
                Measurements = { new MeasurementState { ContextName = "Uplink" } },
            };

            Assert.Empty(StateRecall.Mismatches(saved, new[] { "Uplink" }));
            Assert.Single(StateRecall.Mismatches(saved, new[] { "uplink" }));
        }

        [Fact]
        public void TheArgumentsAreChecked()
        {
            Assert.Throws<ArgumentNullException>(() => StateRecall.Apply(null, Contexts("A")));
            Assert.Throws<ArgumentNullException>(
                () => StateRecall.Apply(ApplicationState.Default(), null));
            Assert.Throws<ArgumentNullException>(
                () => StateRecall.Mismatches(ApplicationState.Default(), null));
        }

        // ---- REQ-STA-005 -----------------------------------------------------------------------

        [Fact]
        public void TheFactoryPresetIsEverySettingAtItsDocumentedDefault()
        {
            // Enumerated over the settings model, so a setting whose default is undocumented fails
            // rather than passing unnoticed: the factory preset is constructed from the model's own
            // defaults, and any leaf left null or NaN is a default nobody wrote down.
            ApplicationState factory = Presets.Factory("Bench");

            IReadOnlyList<StateLeaf> leaves = StateReflection.Leaves(factory);

            _output.WriteLine(leaves.Count + " settings enumerated");

            foreach (StateLeaf leaf in leaves)
            {
                Assert.True(
                    leaf.Value != null,
                    leaf.Path + " has no documented default; it came out null.");

                if (leaf.Value is double)
                {
                    Assert.False(
                        double.IsNaN((double)leaf.Value),
                        leaf.Path + " has no documented default; it came out NaN.");
                }
            }

            string difference;
            Assert.True(
                StateReflection.Same(factory, ApplicationState.Default("Bench"), out difference),
                "The factory preset differs from the model's defaults: " + difference + ".");
        }

        [Fact]
        public void TheFactoryPresetLeavesTheHardwareSetupAlone()
        {
            // REQ-UI-061, structurally: a state carries no front end, no resource string and no
            // connection, so a preset cannot disturb one.
            string json = StateFile.Write(Presets.Factory());

            foreach (string word in new[] { "resource", "visa", "gpib", "frontEnd", "instrument" })
            {
                Assert.False(
                    json.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0,
                    "A preset mentions '" + word + "', so it is not leaving the hardware alone.");
            }
        }

        [Fact]
        public void UserPresetsCanBeCreatedAppliedAndDeleted()
        {
            var library = new PresetLibrary(_directory);

            Assert.Empty(library.Names);

            ApplicationState mine = ApplicationState.Default("Bench");
            mine.Measurements[0].CenterFrequencyHz = 2.4e9;
            mine.Measurements[0].SpanHz = 20e6;

            library.Save("Wi-Fi channel 1", mine);

            Assert.True(library.Contains("Wi-Fi channel 1"));
            Assert.Equal(new[] { "Wi-Fi channel 1" }, library.Names.ToArray());

            Assert.Equal(2.4e9, library.Load("Wi-Fi channel 1").Measurements[0].CenterFrequencyHz, 3);

            Assert.True(library.Delete("Wi-Fi channel 1"));
            Assert.False(library.Delete("Wi-Fi channel 1"));
            Assert.Empty(library.Names);
        }

        [Fact]
        public void UserPresetsSurviveARestart()
        {
            // Files on disk, so "restart" is a second library over the same directory - which is
            // exactly what a restart is.
            new PresetLibrary(_directory).Save("Bench", ApplicationState.Default("Bench"));

            var afterRestart = new PresetLibrary(_directory);

            Assert.True(afterRestart.Contains("Bench"));
            Assert.Equal("Bench", afterRestart.Load("Bench").Measurements[0].ContextName);
        }

        [Fact]
        public void ApplyingAPresetIsRecallingTheStateItWasCapturedFrom()
        {
            // The same code path, not a parallel one, which is what makes the equivalence true
            // rather than intended.
            ApplicationState captured = ApplicationState.Default("Bench");
            StateReflection.Perturb(captured);
            captured.Measurements[0].ContextName = "Bench";

            var library = new PresetLibrary(_directory);
            library.Save("Everything moved", captured);

            Dictionary<string, MeasurementState> viaPreset = Contexts("Bench");
            Dictionary<string, MeasurementState> viaRecall = Contexts("Bench");

            StateRecall.Apply(library.Load("Everything moved"), viaPreset);
            StateRecall.Apply(StateFile.Read(StateFile.Write(captured)), viaRecall);

            string difference;

            Assert.True(
                StateReflection.Same(viaPreset["Bench"], viaRecall["Bench"], out difference),
                "Applying a preset and recalling its state gave different results: " +
                difference + ".");
        }

        [Fact]
        public void APresetNameThatCannotBeAFileNameIsRefused()
        {
            var library = new PresetLibrary(_directory);

            Assert.Throws<ArgumentException>(
                () => library.Save("bench: 2.4 GHz", ApplicationState.Default()));
            Assert.Throws<ArgumentNullException>(() => library.Save(null, ApplicationState.Default()));
            Assert.Throws<ArgumentNullException>(() => library.Save("Bench", null));
            Assert.Throws<FileNotFoundException>(() => library.Load("Never saved"));
            Assert.Throws<ArgumentNullException>(() => new PresetLibrary(null));
        }

        private static Dictionary<string, MeasurementState> Contexts(params string[] names)
        {
            var contexts = new Dictionary<string, MeasurementState>(StringComparer.Ordinal);

            foreach (string name in names)
            {
                contexts[name] = new MeasurementState { ContextName = name };
            }

            return contexts;
        }
    }
}
