using System;
using System.Collections.Generic;
using System.Linq;
using OpenVSA.Ui.Bench;
using Xunit;

namespace OpenVSA.Ui.Tests
{
    /// <summary>
    /// What the test signal source panel decides, asserted without a window (issue #393, scope A).
    /// </summary>
    /// <remarks>
    /// The three properties the issue names for the interactive half: ranges come from the
    /// instrument rather than from a table, coercions and instrument errors reach the event log
    /// rather than a dialog, and a source that will not state a limit is not given an invented one.
    /// </remarks>
    public class SourceControlModelTests
    {
        [Fact]
        public void TheRangesOfferedAreTheSourcesOwn()
        {
            // Numbers no table in this product holds. If a bound were hard-coded anywhere in the
            // panel's path, this is the test that would still report the hard-coded one — the same
            // check REQ-HAL-002 makes of the measurement side.
            var generator = new FakeGenerator
            {
                Limits = new StimulusLimitsShape
                {
                    MinimumFrequencyHz = 250e3,
                    MaximumFrequencyHz = 6.0e9,
                    MinimumLevelDbm = -144.0,
                    MaximumLevelDbm = 25.0,
                },
            };

            var log = new List<string>();
            SourceControlModel model = Connected(generator, log);

            Assert.Equal(250e3, model.Limits.MinimumFrequencyHz);
            Assert.Equal(6.0e9, model.Limits.MaximumFrequencyHz);

            Assert.Null(model.ValidateFrequency(250e3));
            Assert.Null(model.ValidateFrequency(6.0e9));

            Assert.NotNull(model.ValidateFrequency(249e3));
            Assert.NotNull(model.ValidateFrequency(6.1e9));

            Assert.NotNull(model.ValidateLevel(26.0));
            Assert.Null(model.ValidateLevel(25.0));

            // And the refusal quotes the source's numbers, so a user is told what this instrument
            // does rather than that they were wrong.
            Assert.Contains("250", model.ValidateFrequency(1.0e3));
        }

        [Fact]
        public void ALimitTheSourceWillNotStateIsNotEnforced()
        {
            // The alternative is to substitute a plausible bound, and a plausible bound belongs to
            // some other instrument: it would refuse settings this source can honour and accept
            // ones it cannot. Unstated is left to the instrument, which clips and says so.
            var generator = new FakeGenerator { Limits = null };

            var log = new List<string>();
            SourceControlModel model = Connected(generator, log);

            Assert.False(model.Limits.HasFrequencyRange);
            Assert.False(model.Limits.HasLevelRange);

            Assert.Null(model.ValidateFrequency(1.0e12));
            Assert.Null(model.ValidateLevel(1000.0));

            // Still refused: not a number at all is a different thing from an unstated bound.
            Assert.NotNull(model.ValidateFrequency(double.NaN));

            Assert.Contains(log, line => line.Contains("unstated frequency range"));
        }

        [Fact]
        public void ACoercionIsReportedWithBothNumbers()
        {
            // The generator quantises its level and clips rather than refusing, so what it settles
            // on is routinely not what it was asked for. A panel that reported the request would
            // leave a user certain of a stimulus the instrument is not producing.
            var generator = new FakeGenerator
            {
                CoerceLevelTo = -13.78,
                CoerceFrequencyTo = 1.0e9,
            };

            var log = new List<string>();
            SourceControlModel model = Connected(generator, log);

            Assert.True(model.Apply(StimulusKind.ContinuousWave, 1.0e9, -13.775, 0, 0.0, 0.0));

            string reported = log.Last();

            Assert.Contains("coerced", reported);
            Assert.Contains("-13.775", reported);
            Assert.Contains("-13.78", reported);

            // The carrier was honoured, so it is not among the coercions.
            Assert.DoesNotContain("carrier", reported);
        }

        [Fact]
        public void ACoercionTooSmallForTheDefaultPrecisionIsStillShownAsTwoNumbers()
        {
            // The defect this found: both readings are rounded for display, so a coercion smaller
            // than the rounding printed as "from 1.000 GHz to 1.000 GHz" — a sentence that reports
            // a difference and then hides it. A kilohertz at a gigahertz is a real coercion and is
            // exactly the size that disappears.
            var generator = new FakeGenerator { CoerceFrequencyTo = 1.000001e9 };

            var log = new List<string>();
            SourceControlModel model = Connected(generator, log);

            Assert.True(model.Apply(StimulusKind.ContinuousWave, 1.0e9, -20.0, 0, 0.0, 0.0));

            string reported = log.Last();
            string[] halves = reported.Split(new[] { " from ", " to " }, StringSplitOptions.None);

            Assert.Equal(3, halves.Length);
            Assert.NotEqual(halves[1], halves[2].TrimEnd('.'));
        }

        [Fact]
        public void AnHonouredSettingIsReportedAsWhatTheSourceSettledOn()
        {
            var generator = new FakeGenerator();
            var log = new List<string>();

            SourceControlModel model = Connected(generator, log);

            Assert.True(model.Apply(StimulusKind.ContinuousWave, 1.0e9, -20.0, 0, 0.0, 0.0));

            Assert.DoesNotContain("coerced", log.Last());
            Assert.Contains("carrier", log.Last());
        }

        [Fact]
        public void AnInstrumentErrorReachesTheLogAndNotAnException()
        {
            // Issue #393: "coercions and instrument errors surface in the event log rather than a
            // dialog". A modal box would also stop the bench run it interrupted.
            var generator = new FakeGenerator
            {
                Refusal = new InvalidOperationException("-222,\"Data out of range\""),
            };

            var log = new List<string>();
            SourceControlModel model = Connected(generator, log);

            Assert.False(model.Apply(StimulusKind.ContinuousWave, 1.0e9, -20.0, 0, 0.0, 0.0));

            Assert.Contains("-222", log.Last());
            Assert.Contains("refused", log.Last());
        }

        [Fact]
        public void ASourceThatWillNotOpenIsReportedRatherThanThrown()
        {
            // The ordinary case on a bench — the instrument is off, or its address has moved. Not
            // an application error, and not a reason for a dialog.
            var registry = new StimulusRegistry();
            var log = new List<string>();
            var model = new SourceControlModel(registry, log.Add);

            var descriptor = new StimulusDescriptor(
                "a source that will not open", false, string.Empty, typeof(UnopenableGenerator));

            Assert.False(model.Connect(descriptor, null));
            Assert.False(model.IsConnected);

            Assert.Contains("could not be opened", log[0]);
            Assert.Contains("not present", log[0]);
        }

        [Fact]
        public void ACapabilityTheSourceLacksIsRefusedBeforeAnythingIsSent()
        {
            var generator = new FakeGenerator();
            var log = new List<string>();

            SourceControlModel model = Connected(generator, log);

            // FakeGenerator produces a carrier and nothing else, which is a perfectly good source.
            Assert.False(model.Source.CanProduceMultitone);
            Assert.NotNull(model.ValidateToneCount(5));

            Assert.False(model.Apply(StimulusKind.Multitone, 1.0e9, -20.0, 5, 1.0e6, 0.0));
            Assert.Empty(generator.Sent.Where(s => s.StartsWith("comb", StringComparison.Ordinal)));
        }

        [Fact]
        public void ClosingTheSourceTurnsItOff()
        {
            // Leaving a generator radiating because a panel was closed is not something to do on
            // somebody else's bench. The source's own Dispose does it; this is the path to it.
            var generator = new FakeGenerator();
            var log = new List<string>();

            SourceControlModel model = Connected(generator, log);

            model.SetOutput(true);
            Assert.True(generator.IsOutputEnabled);

            model.Disconnect();

            Assert.False(model.IsConnected);
            Assert.False(generator.IsOutputEnabled);
            Assert.Contains("closed", log.Last());
        }

        [Fact]
        public void ArithmeticIsNotACoercion()
        {
            // The tolerance has to be small enough to catch the 0.02 dB level step and large enough
            // to ignore a round trip through a decimal string. A test either side of that.
            Assert.False(SourceControlModel.Differs(1.0e9, 1.0e9));
            Assert.False(SourceControlModel.Differs(1.0e9, 1.0e9 + 1e-6));

            Assert.True(SourceControlModel.Differs(-13.775, -13.78));
            Assert.True(SourceControlModel.Differs(1.0e9, 1.0e9 + 1.0));
        }

        private static SourceControlModel Connected(FakeGenerator generator, List<string> log)
        {
            var model = new SourceControlModel(new StimulusRegistry(), log.Add);

            // The instance under test is handed in rather than constructed by type, so that the
            // test can look at the generator the model is actually driving. What is being checked
            // here is the model's decisions; the late binding has its own tests.
            var descriptor = new StimulusDescriptor(
                "a fake generator", false, string.Empty, typeof(FakeGenerator),
                resource => generator);

            Assert.True(model.Connect(descriptor, null));

            return model;
        }

        /// <summary>The shape a source's limits are read through, by name.</summary>
        public sealed class StimulusLimitsShape
        {
            /// <summary>Lowest carrier, in hertz.</summary>
            public double MinimumFrequencyHz { get; set; }

            /// <summary>Highest carrier, in hertz.</summary>
            public double MaximumFrequencyHz { get; set; }

            /// <summary>Lowest level, in dBm.</summary>
            public double MinimumLevelDbm { get; set; }

            /// <summary>Highest level, in dBm.</summary>
            public double MaximumLevelDbm { get; set; }
        }

        /// <summary>
        /// A source with a carrier and nothing else, driven through the same late binding.
        /// </summary>
        /// <remarks>
        /// Duck-typed rather than implementing an interface, because that is exactly how the shell
        /// sees a real one — there is no interface here to implement. A source with no comb and no
        /// noise is also the case worth having: the panel must offer what the source has.
        /// </remarks>
        public sealed class FakeGenerator
        {
            private readonly List<string> _sent = new List<string>();

            /// <summary>What the source will say its limits are, or null to say nothing.</summary>
            public StimulusLimitsShape Limits { get; set; } = new StimulusLimitsShape
            {
                MinimumFrequencyHz = 100e3,
                MaximumFrequencyHz = 3.0e9,
                MinimumLevelDbm = -136.0,
                MaximumLevelDbm = 20.0,
            };

            /// <summary>Reports this carrier whatever it is asked for; <c>NaN</c> to obey.</summary>
            public double CoerceFrequencyTo { get; set; } = double.NaN;

            /// <summary>Reports this level whatever it is asked for; <c>NaN</c> to obey.</summary>
            public double CoerceLevelTo { get; set; } = double.NaN;

            /// <summary>Thrown by the next setting, standing in for an instrument's refusal.</summary>
            public Exception Refusal { get; set; }

            /// <summary>Commands the source was driven with.</summary>
            public IReadOnlyList<string> Sent => _sent;

            /// <summary>What the source calls itself.</summary>
            public string DisplayName => "a fake generator";

            /// <summary>Whether the output is on.</summary>
            public bool IsOutputEnabled { get; private set; }

            /// <summary>The carrier the source reports, in hertz.</summary>
            public double FrequencyHz { get; private set; }

            /// <summary>The level the source reports, in dBm.</summary>
            public double LevelDbm { get; private set; }

            /// <summary>Connects.</summary>
            public void Connect() => _sent.Add("connect");

            /// <summary>Reads back.</summary>
            public void Refresh() => _sent.Add("refresh");

            /// <summary>Says what it can produce, or nothing.</summary>
            public StimulusLimitsShape ReadLimits() =>
                Limits ?? new StimulusLimitsShape
                {
                    MinimumFrequencyHz = double.NaN,
                    MaximumFrequencyHz = double.NaN,
                    MinimumLevelDbm = double.NaN,
                    MaximumLevelDbm = double.NaN,
                };

            /// <summary>Sets an unmodulated carrier.</summary>
            /// <param name="frequencyHz">Carrier, in hertz.</param>
            /// <param name="levelDbm">Level, in dBm.</param>
            public void SetContinuousWave(double frequencyHz, double levelDbm)
            {
                _sent.Add("carrier");

                if (Refusal != null)
                {
                    throw Refusal;
                }

                FrequencyHz = double.IsNaN(CoerceFrequencyTo) ? frequencyHz : CoerceFrequencyTo;
                LevelDbm = double.IsNaN(CoerceLevelTo) ? levelDbm : CoerceLevelTo;
            }

            /// <summary>Turns the output on or off.</summary>
            /// <param name="enabled">Whether RF should be on.</param>
            public void SetOutput(bool enabled)
            {
                _sent.Add("output " + enabled);
                IsOutputEnabled = enabled;
            }

            /// <summary>Turns the output off and closes.</summary>
            public void Dispose()
            {
                _sent.Add("dispose");
                IsOutputEnabled = false;
            }
        }

        /// <summary>A source whose construction fails the way a missing instrument does.</summary>
        public sealed class UnopenableGenerator
        {
            /// <summary>Refuses to be created.</summary>
            /// <exception cref="InvalidOperationException">Always.</exception>
            public UnopenableGenerator()
            {
                throw new InvalidOperationException(
                    "Insufficient location information or the device or resource is not present.");
            }

            /// <summary>What the source would call itself.</summary>
            public string DisplayName => string.Empty;

            /// <summary>Whether the output is on.</summary>
            public bool IsOutputEnabled => false;

            /// <summary>The carrier, in hertz.</summary>
            public double FrequencyHz => 0.0;

            /// <summary>The level, in dBm.</summary>
            public double LevelDbm => 0.0;

            /// <summary>Connects.</summary>
            public void Connect()
            {
            }

            /// <summary>Reads back.</summary>
            public void Refresh()
            {
            }

            /// <summary>Closes.</summary>
            public void Dispose()
            {
            }

            /// <summary>Turns the output on or off.</summary>
            /// <param name="enabled">Whether RF should be on.</param>
            public void SetOutput(bool enabled)
            {
            }

            /// <summary>Sets an unmodulated carrier.</summary>
            /// <param name="frequencyHz">Carrier, in hertz.</param>
            /// <param name="levelDbm">Level, in dBm.</param>
            public void SetContinuousWave(double frequencyHz, double levelDbm)
            {
            }
        }
    }
}
