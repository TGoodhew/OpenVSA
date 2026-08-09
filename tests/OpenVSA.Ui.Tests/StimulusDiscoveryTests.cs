using System;
using System.Linq;
using OpenVSA.TestHarness;
using OpenVSA.Ui.Bench;
using Xunit;

namespace OpenVSA.Ui.Tests
{
    /// <summary>
    /// The binding between the shell and a test signal source it may not reference (issue #393).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This is the guard the late binding is only safe because of.</strong> The shell finds
    /// sources by attribute name and drives them by member name, because <c>REQ-ARC-001</c> keeps
    /// bench infrastructure out of the product's references and <c>REQ-NFR-032</c> keeps VISA off
    /// the start-up path — so no compiler checks that the names still agree. This test project can
    /// reference the harness, which is what lets it check.
    /// </para>
    /// <para>
    /// A rename in the harness therefore fails here, naming the member, rather than producing a
    /// panel whose buttons silently do nothing.
    /// </para>
    /// </remarks>
    public class StimulusDiscoveryTests
    {
        [Fact]
        public void TheShellFindsEverySourceTheHarnessDeclares()
        {
            var registry = new StimulusRegistry();

            int added = registry.AddAssembly(typeof(SimulatedStimulus).Assembly);

            Assert.True(registry.IsAvailable);
            Assert.Equal(string.Empty, registry.UnavailableReason);

            // Both of them: the one that needs an instrument and the one that does not. The second
            // is what makes the panel usable, and testable, on a machine with no bench at all.
            Assert.True(added >= 2, "Discovery found " + added + " source(s).");

            Assert.Contains(registry.Sources, s => s.RequiresResource);
            Assert.Contains(registry.Sources, s => !s.RequiresResource);

            foreach (StimulusDescriptor descriptor in registry.Sources)
            {
                Assert.False(string.IsNullOrWhiteSpace(descriptor.DisplayName));

                if (descriptor.RequiresResource)
                {
                    // Offered so the panel can show it, never used silently — a bench instrument's
                    // address moves and a stale one reads like a powered-off instrument.
                    Assert.False(string.IsNullOrWhiteSpace(descriptor.DefaultResource));
                }
            }
        }

        [Fact]
        public void EveryMemberTheShellLateBindsIsStillOnEveryRealSource()
        {
            // The rename guard, stated directly. If this fails, the message names the member and
            // the fix is either to restore the name in the harness or to change it in
            // StimulusSource - not to relax the assertion.
            foreach (Type type in new[] { typeof(SimulatedStimulus), typeof(E4438CStimulus) })
            {
                string missing = StimulusSource.FirstUnbindableMember(type);

                Assert.True(
                    missing == null,
                    type.Name + " no longer provides '" + missing +
                    "', which the shell's test signal source panel binds by name.");
            }
        }

        [Fact]
        public void TheOptionalCapabilitiesAreDiscoveredRatherThanAssumed()
        {
            // A comb, noise and a statement of limits are three separate capabilities in the
            // harness precisely because an instrument may have any combination of them. The panel
            // has to find out which by asking, before it offers a control.
            var registry = new StimulusRegistry();
            registry.AddAssembly(typeof(SimulatedStimulus).Assembly);

            StimulusDescriptor descriptor =
                registry.Sources.First(s => !s.RequiresResource);

            using (StimulusSource source = descriptor.Create(null))
            {
                Assert.True(source.CanReportLimits);
                Assert.True(source.CanProduceMultitone);
                Assert.True(source.CanProduceNoise);

                source.Connect();

                SourceLimits limits = source.ReadLimits();

                // Through the late binding, from the harness's own measured constants: the four
                // properties on the returned object are bound by name too.
                Assert.Equal(SimulatedStimulus.MinimumFrequencyHz, limits.MinimumFrequencyHz);
                Assert.Equal(SimulatedStimulus.MaximumLevelDbm, limits.MaximumLevelDbm);
            }
        }

        [Fact]
        public void ASourceDrivenThroughTheProxyReachesTheRealOne()
        {
            var registry = new StimulusRegistry();
            registry.AddAssembly(typeof(SimulatedStimulus).Assembly);

            StimulusDescriptor descriptor = registry.Sources.First(s => !s.RequiresResource);

            using (StimulusSource source = descriptor.Create(null))
            {
                source.Connect();
                source.SetContinuousWave(1.0e9, -20.0);

                Assert.Equal(1.0e9, source.FrequencyHz);
                Assert.Equal(-20.0, source.LevelDbm);

                source.SetOutput(true);
                Assert.True(source.IsOutputEnabled);

                source.SetMultitone(1.0e9, 5, 1.0e6, -20.0);
                Assert.Equal(5, source.ToneCount);
                Assert.Equal(1.0e6, source.ToneSpacingHz);

                source.SetNoise(1.0e9, 5.0e6, -20.0);
                Assert.Equal(5.0e6, source.NoiseBandwidthHz);
            }
        }

        [Fact]
        public void ASourceMissingAMemberIsRefusedAndTheMemberIsNamed()
        {
            // The half that proves the guard above can fail. A source without SetContinuousWave is
            // not a source this panel can drive, and finding that out at discovery is what keeps it
            // from being found out at a click.
            string missing = StimulusSource.FirstUnbindableMember(typeof(HalfASource));

            Assert.Equal("SetContinuousWave(double, double)", missing);

            InvalidOperationException refusal = Assert.Throws<InvalidOperationException>(
                () => StimulusSource.Around(new HalfASource(), "half a source"));

            Assert.Contains("SetContinuousWave", refusal.Message);
        }

        [Fact]
        public void AnErrorFromTheSourceArrivesUnwrapped()
        {
            // Reflection wraps whatever the target threw in "Exception has been thrown by the
            // target of an invocation", and the thing worth reading is what the instrument said.
            // The panel puts this message in the event log, so the wrapper would be the whole
            // entry.
            StimulusSource source = StimulusSource.Around(new RefusingSource(), "a refusing source");

            InvalidOperationException failure = Assert.Throws<InvalidOperationException>(
                () => source.SetContinuousWave(1.0e9, -20.0));

            Assert.Equal("-222,\"Data out of range\"", failure.Message);
        }

        [Fact]
        public void ABuildWithNoHarnessSaysSoRatherThanFailing()
        {
            // REQ-NFR-032's normal case, and an installed copy's: no source at all. The reason has
            // to distinguish "this build carries no harness" from "the harness would not load",
            // because they are different problems with different answers.
            var registry = new StimulusRegistry();

            Assert.False(registry.IsAvailable);
            Assert.Contains("developer-build component", registry.UnavailableReason);

            // A directory that is not there is the ordinary case, not an error.
            Assert.Equal(0, registry.ProbeDirectory(
                System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "NoSuchFolder")));
        }

        /// <summary>A source with one member of the required set missing.</summary>
        private sealed class HalfASource
        {
            public string DisplayName => "half a source";

            public bool IsOutputEnabled => false;

            public double FrequencyHz => 0.0;

            public double LevelDbm => 0.0;

            public void Connect()
            {
            }

            public void Refresh()
            {
            }

            public void Dispose()
            {
            }

            public void SetOutput(bool enabled)
            {
            }
        }

        /// <summary>A source that refuses a setting the way an instrument does.</summary>
        private sealed class RefusingSource
        {
            public string DisplayName => "a refusing source";

            public bool IsOutputEnabled => false;

            public double FrequencyHz => 0.0;

            public double LevelDbm => 0.0;

            public void Connect()
            {
            }

            public void Refresh()
            {
            }

            public void Dispose()
            {
            }

            public void SetOutput(bool enabled)
            {
            }

            public void SetContinuousWave(double frequencyHz, double levelDbm) =>
                throw new InvalidOperationException("-222,\"Data out of range\"");
        }
    }
}
