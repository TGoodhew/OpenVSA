using System;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using OpenVSA.Measurement.State;
using Xunit;
using Xunit.Abstractions;

namespace OpenVSA.Measurement.Tests.State
{
    /// <summary>
    /// <c>REQ-STA-003</c> format and versioning, and <c>REQ-STA-002</c>'s exclusions.
    /// </summary>
    public class StateFormatTests
    {
        private readonly ITestOutputHelper _output;

        public StateFormatTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void UnknownMembersSurviveARoundTripAtEveryDepth()
        {
            // REQ-STA-003's criterion. A file written by later software, loaded here and saved
            // again, must come back whole - otherwise an older build is a one-way door and the loss
            // only surfaces on somebody else's machine.
            string original = StateFile.Write(ApplicationState.Default("Bench"));

            var document = JObject.Parse(original);
            document["futureTopLevelSetting"] = "kept";
            document["measurements"][0]["futureMeasurementSetting"] = 42;
            document["measurements"][0]["analysis"]["futureAnalysisSetting"] =
                new JObject { { "nested", true } };
            document["measurements"][0]["traces"][0]["futureTraceSetting"] = 1.5;

            string fromTheFuture = document.ToString();

            ApplicationState loaded = StateFile.Read(fromTheFuture);
            string rewritten = StateFile.Write(loaded);

            var back = JObject.Parse(rewritten);

            _output.WriteLine(loaded.UnknownMembersJson);

            Assert.Equal("kept", (string)back["futureTopLevelSetting"]);
            Assert.Equal(42, (int)back["measurements"][0]["futureMeasurementSetting"]);
            Assert.True((bool)back["measurements"][0]["analysis"]["futureAnalysisSetting"]["nested"]);
            Assert.Equal(1.5, (double)back["measurements"][0]["traces"][0]["futureTraceSetting"], 6);
        }

        [Fact]
        public void KnownSettingsAreStillReadFromAFileThatAlsoHasUnknownOnes()
        {
            // The preservation must not come at the cost of reading what is understood.
            var document = JObject.Parse(StateFile.Write(ApplicationState.Default("Bench")));
            document["measurements"][0]["centerFrequencyHz"] = 2.4e9;
            document["measurements"][0]["futureSetting"] = "x";

            ApplicationState loaded = StateFile.Read(document.ToString());

            Assert.Equal(2.4e9, loaded.Measurements[0].CenterFrequencyHz, 3);
        }

        [Fact]
        public void AFileWithNoSchemaVersionIsRefusedBySayingSo()
        {
            StateFormatException failure = Assert.Throws<StateFormatException>(
                () => StateFile.Read("{ \"measurements\": [] }"));

            Assert.Contains("schema version", failure.Message);
        }

        [Fact]
        public void AFileOlderThanTheSoftwareReadsIsRefusedBySayingWhichVersionsItReads()
        {
            StateFormatException failure = Assert.Throws<StateFormatException>(
                () => StateFile.Read("{ \"schemaVersion\": 0, \"measurements\": [] }"));

            _output.WriteLine(failure.Message);

            Assert.Contains("0", failure.Message);
            Assert.Contains(
                ApplicationState.OldestReadableSchemaVersion.ToString(
                    System.Globalization.CultureInfo.CurrentCulture),
                failure.Message);
        }

        [Fact]
        public void TextThatIsNotJsonIsRefusedRatherThanMisread()
        {
            Assert.Throws<StateFormatException>(() => StateFile.Read("not a state at all"));
            Assert.Throws<ArgumentNullException>(() => StateFile.Read(null));
            Assert.Throws<ArgumentNullException>(() => StateFile.Write(null));
        }

        [Fact]
        public void AStateCarriesNoneOfTheFourExclusions()
        {
            // REQ-STA-002. Checked over the model's own types rather than over a saved file, so a
            // property added for one of these fails at once instead of when somebody notices their
            // display preferences travelling in a colleague's setup.
            string[] forbidden = { "recording", "math", "register", "preference" };

            foreach (Type type in StateReflection.TypesIn(typeof(ApplicationState)))
            {
                foreach (PropertyInfo property in
                         type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    foreach (string word in forbidden)
                    {
                        Assert.False(
                            property.Name.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0,
                            type.Name + "." + property.Name +
                            " looks like one of REQ-STA-002's exclusions, which a state must not " +
                            "carry.");
                    }
                }
            }
        }

        [Fact]
        public void SavingAndRecallingAStateLeavesTheFourExclusionsUntouched()
        {
            // Neither captured into the state nor cleared by the recall - both are failures the
            // requirement names.
            var sidecar = new SidecarState();
            sidecar.Math.Add(new TraceMathState { Trace = "B", Operator = "Subtract", Left = "A", Right = "D1" });
            sidecar.Registers.Add(new RegisterState { Register = 1, Complex = { 1.0f, 2.0f } });
            sidecar.Display.Trace = 0xFF00FF00;
            sidecar.Recording.Path = @"C:\recordings\bench.ovsa-rec";

            string before = SidecarFile.Write(sidecar);

            ApplicationState state = ApplicationState.Default("Bench");
            StateReflection.Perturb(state);
            state.Measurements[0].ContextName = "Bench";
            string json = StateFile.Write(state);

            // Not captured: nothing of the sidecar appears in the state.
            Assert.DoesNotContain("bench.ovsa-rec", json);
            Assert.DoesNotContain("D1", json);

            StateRecall.Apply(
                StateFile.Read(json),
                new System.Collections.Generic.Dictionary<string, MeasurementState>(StringComparer.Ordinal)
                {
                    { "Bench", new MeasurementState { ContextName = "Bench" } },
                });

            // Not cleared: the sidecar is exactly as it was.
            Assert.Equal(before, SidecarFile.Write(sidecar));
        }

        [Fact]
        public void EachExclusionIsSaveableAndRecallableThroughItsOwnCommand()
        {
            var math = new TraceMathState { Trace = "C", Operator = "Divide", Left = "A", Right = "D2" };
            var register = new RegisterState { Register = 3, BinWidthHz = 250.0, Complex = { 0.5f, -0.25f } };
            var display = new DisplayPreferencesState { Grid = 0xFF123456, AnnotationFontSize = 13.0 };
            var recording = new RecordingState { Path = "bench.ovsa-rec", PositionSeconds = 2.5 };

            Assert.Equal("C", SidecarFile.Read<TraceMathState>(SidecarFile.Write(math)).Trace);
            Assert.Equal("D2", SidecarFile.Read<TraceMathState>(SidecarFile.Write(math)).Right);
            Assert.Equal(250.0, SidecarFile.Read<RegisterState>(SidecarFile.Write(register)).BinWidthHz, 6);
            Assert.Equal(
                new[] { 0.5f, -0.25f },
                SidecarFile.Read<RegisterState>(SidecarFile.Write(register)).Complex.ToArray());
            Assert.Equal(
                0xFF123456u,
                SidecarFile.Read<DisplayPreferencesState>(SidecarFile.Write(display)).Grid);
            Assert.Equal(
                2.5, SidecarFile.Read<RecordingState>(SidecarFile.Write(recording)).PositionSeconds, 6);

            // Distinct extensions, so the four are separate files and separate commands rather than
            // one file with four sections.
            var extensions = new[]
            {
                SidecarState.MathExtension,
                SidecarState.RegistersExtension,
                SidecarState.PreferencesExtension,
                SidecarState.RecordingExtension,
                StateFile.Extension,
            };

            Assert.Equal(extensions.Length, extensions.Distinct(StringComparer.Ordinal).Count());
        }

        [Fact]
        public void TheSaveDialogTextNamesAllFourExclusions()
        {
            // "The save dialog shall state this explicitly rather than leaving users to discover
            // it" - so the wording lives with the requirement it implements and is asserted here.
            string notice = StateFile.ExclusionNotice;

            _output.WriteLine(notice);

            foreach (string word in new[] { "recording", "math", "register", "preference" })
            {
                Assert.True(
                    notice.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0,
                    "The save dialog's text does not mention " + word + "s.");
            }
        }
    }
}
