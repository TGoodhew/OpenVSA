using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using OpenVSA.Measurement.State;
using Xunit;
using Xunit.Abstractions;

namespace OpenVSA.Measurement.Tests.State
{
    /// <summary>
    /// <c>REQ-STA-001</c>: everything a state carries survives a save, a reset and a recall.
    /// </summary>
    public class StateSaveRecallTests
    {
        private readonly ITestOutputHelper _output;

        public StateSaveRecallTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void EverySettingSurvivesSaveResetAndRecall()
        {
            // The requirement's criterion, done by enumeration rather than by sampling: every value
            // in the model is moved away from its default, the state is written, the application is
            // reset to defaults, and the state read back. A setting added without save and recall
            // support fails here rather than going unnoticed.
            ApplicationState configured = ApplicationState.Default("Bench");
            StateReflection.Perturb(configured);

            // The perturbation has to have done something, or everything below would pass over a
            // state that was never changed from its default.
            string difference;
            Assert.False(
                StateReflection.Same(configured, ApplicationState.Default("Bench"), out difference),
                "The configured state is indistinguishable from a default one.");

            string json = StateFile.Write(configured);

            // The reset: nothing of the configured state is reachable from here.
            ApplicationState reset = ApplicationState.Default("Bench");
            Assert.NotEqual(
                configured.Measurements[0].CenterFrequencyHz,
                reset.Measurements[0].CenterFrequencyHz);

            ApplicationState recalled = StateFile.Read(json);

            IReadOnlyList<StateLeaf> saved = StateReflection.Leaves(configured);
            IReadOnlyList<StateLeaf> back = StateReflection.Leaves(recalled);

            _output.WriteLine(saved.Count + " values enumerated across the state model");

            Assert.True(saved.Count > 50, "Only " + saved.Count + " values were found to check.");

            // Same shape, or a list came back with more or fewer entries than it was saved with -
            // which the per-value comparison below would only notice by accident.
            Assert.Equal(saved.Count, back.Count);

            for (int i = 0; i < saved.Count; i++)
            {
                Assert.Equal(saved[i].Path, back[i].Path);
                Assert.True(
                    Equals(saved[i].Value, back[i].Value),
                    saved[i].Path + " was saved as " + saved[i].Value +
                    " and came back as " + back[i].Value + ".");
            }
        }

        [Fact]
        public void TheStateCoversEveryItemTheRequirementLists()
        {
            // The requirement enumerates what a state contains. This pins the model to that list,
            // so an item dropped from the model fails rather than quietly ceasing to be saved.
            var measurement = new MeasurementState();

            AssertHas(measurement, "Kind");                         // measurement type
            AssertHas(measurement, "CenterFrequencyHz", "SpanHz");  // frequency and span
            AssertHas(measurement, "ResolutionBandwidthHz");        // resolution bandwidth
            AssertHas(measurement, "Trigger");                      // trigger configuration
            AssertHas(measurement, "Input");                        // input settings
            AssertHas(measurement, "Analysis");                     // analysis parameters
            AssertHas(measurement, "Windows");                      // trace window positions
            AssertHas(measurement, "Traces");                       // trace display properties
            AssertHas(measurement, "Markers");                      // marker types and positions
            AssertHas(measurement, "Source");                       // source parameters

            // Input settings: range, coupling, digital, external mixer.
            AssertHas(measurement.Input, "RangeDbm", "Coupling", "IsDigital", "ExternalMixer");

            // Analysis parameters, including REQ-DSP-023's Zoom If Span Change. That one changes
            // what the *next* span the user types will mean, so a recalled state that lost it
            // would behave correctly right up until someone changed the span.
            AssertHas(measurement.Analysis, "Window", "Overlap", "SpanChange");

            // Trace display properties: format, X and Y scaling, spectrogram settings.
            TraceDisplayState trace = measurement.Traces[0];
            AssertHas(trace, "Format", "TopDbm", "DecibelsPerDivision", "XStart", "XStop");
            AssertHas(trace, "SpectrogramDepth", "SpectrogramRangeDb");

            // Trace window positions and overlay state.
            AssertHas(measurement.Windows[0], "Row", "Column", "IsVisible", "IsOverlaid");

            // Marker types, positions and calculations.
            var marker = new MarkerState();
            AssertHas(marker, "Type", "XHz", "YDbm", "Calculation");
        }

        [Fact]
        public void ARecallIsCompleteBeforeItReturns()
        {
            // "Recall is complete before the first post-recall acquisition": there is no deferred
            // work, so a measurement started on the next line sees the recalled settings.
            ApplicationState configured = ApplicationState.Default("Bench");
            configured.Measurements[0].CenterFrequencyHz = 2.4e9;
            configured.Measurements[0].SpanHz = 40e6;

            var contexts = new Dictionary<string, MeasurementState>(StringComparer.Ordinal)
            {
                { "Bench", new MeasurementState { ContextName = "Bench" } },
            };

            StateRecall.Apply(StateFile.Read(StateFile.Write(configured)), contexts);

            Assert.Equal(2.4e9, contexts["Bench"].CenterFrequencyHz, 3);
            Assert.Equal(40e6, contexts["Bench"].SpanHz, 3);
        }

        [Fact]
        public void TheStateIsHumanReadableAndCarriesItsSchemaVersion()
        {
            // REQ-STA-003: readable and diffable is the whole reason for choosing text over the
            // reference product's opaque binary.
            string json = StateFile.Write(ApplicationState.Default("Bench"));

            _output.WriteLine(json.Substring(0, Math.Min(400, json.Length)));

            Assert.Contains("\"schemaVersion\": 1", json);
            Assert.Contains("\"centerFrequencyHz\"", json);
            Assert.Contains(Environment.NewLine, json);
            Assert.Contains("  ", json);
        }

        private static void AssertHas(object state, params string[] properties)
        {
            IEnumerable<string> present = state.GetType()
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(p => p.Name);

            var have = new HashSet<string>(present, StringComparer.Ordinal);

            foreach (string property in properties)
            {
                Assert.True(
                    have.Contains(property),
                    state.GetType().Name + " has no '" + property +
                    "', which REQ-STA-001 requires a state to carry.");
            }
        }
    }
}
