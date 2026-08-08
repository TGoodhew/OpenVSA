using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using OpenVSA.Hal.Visa;
using OpenVSA.TestHarness;
using Xunit;

namespace OpenVSA.TestHarness.Tests
{
    /// <summary>
    /// Where the interactive panel's ranges come from (issue #393, scope A).
    /// </summary>
    /// <remarks>
    /// The issue's constraint is that "ranges and limits come from the instrument's own MIN/MAX
    /// queries, not hard-coded", which is the generator-side statement of the discipline
    /// <c>REQ-HAL-002</c> imposes on the measurement side. These assert the two halves that are
    /// easy to get wrong: that the numbers really are the instrument's, and that asking for one it
    /// will not give does not poison the next operation.
    /// </remarks>
    public class StimulusLimitsTests
    {
        [Fact]
        public void TheRangesAreTheInstrumentsAndNotADataSheets()
        {
            // The fake answers a 250 kHz floor and a 6 GHz ceiling, neither of which is the number
            // any table in this repository holds. If a limit were hard-coded anywhere in the path,
            // this is the test that would still report the hard-coded one.
            var generator = new FakeGenerator
            {
                MinimumFrequencyHz = 250e3,
                MaximumFrequencyHz = 6e9,
                MinimumLevelDbm = -144.0,
                MaximumLevelDbm = 25.0,
            };

            using (var source = Connected(generator))
            {
                StimulusLimits limits = source.ReadLimits();

                Assert.Equal(250e3, limits.MinimumFrequencyHz);
                Assert.Equal(6e9, limits.MaximumFrequencyHz);
                Assert.Equal(-144.0, limits.MinimumLevelDbm);
                Assert.Equal(25.0, limits.MaximumLevelDbm);

                Assert.True(limits.HasFrequencyRange);
                Assert.True(limits.HasLevelRange);
            }
        }

        [Fact]
        public void ALimitTheInstrumentWillNotGiveIsUnknownRatherThanInvented()
        {
            // Firmware C.05.85 rejects a MIN/MAX query on some nodes by not answering at all. The
            // panel must range what it can and say so about the rest; substituting a data-sheet
            // number would be a confident statement about a different instrument, because this
            // model's top frequency depends on which option it carries.
            var generator = new FakeGenerator { AnswersLevelLimits = false };

            using (var source = Connected(generator))
            {
                StimulusLimits limits = source.ReadLimits();

                Assert.True(limits.HasFrequencyRange);
                Assert.False(limits.HasLevelRange);

                Assert.True(double.IsNaN(limits.MinimumLevelDbm));
                Assert.True(double.IsNaN(limits.MaximumLevelDbm));
            }
        }

        [Fact]
        public void ARefusedProbeDoesNotLeaveItsErrorForTheNextOperationToBeBlamedFor()
        {
            // The trap this harness has already been caught by once, in the exact form it took:
            // a rejected capability query timed out AND left -108 in the instrument's queue, and
            // the next scenario reported it as "-108 while setting the carrier" on an operation
            // that had done nothing wrong. Catching the exception does not help — the exception is
            // this side of the wire and the queue is the other.
            var generator = new FakeGenerator { AnswersLevelLimits = false };

            using (var source = Connected(generator))
            {
                source.ReadLimits();

                Assert.Empty(generator.QueuedErrors);

                // The proof that matters: an innocent operation afterwards must succeed.
                source.SetContinuousWave(1.0e9, -20.0);

                Assert.Equal(1.0e9, source.FrequencyHz);
            }
        }

        [Fact]
        public void TheProbeIsGivenLessTimeThanTheSessionAndGivesItBack()
        {
            // A probe that is refused costs a timeout apiece, and four of them at a session default
            // measured in tens of seconds is a panel that appears to hang while it opens. Shortened
            // only around the probe: an instrument that has really gone away must still fail
            // promptly everywhere else, and slowly nowhere.
            var generator = new FakeGenerator { AnswersLevelLimits = false, TimeoutMilliseconds = 30000 };

            using (var source = Connected(generator))
            {
                source.ReadLimits();

                Assert.Equal(30000, generator.TimeoutMilliseconds);

                Assert.True(
                    generator.LowestTimeoutSeen <= E4438CStimulus.LimitProbeTimeoutMilliseconds,
                    "The probe ran at " + generator.LowestTimeoutSeen + " ms, which is the " +
                    "session's own timeout rather than the probe's.");
            }
        }

        [Fact]
        public void ASourceWithNoInstrumentReportsTheSameRangeThroughTheSameInterface()
        {
            // What makes the simulated source a stand-in rather than a stub: the panel ranges
            // itself from it by the same path, so the ranging code is exercised with no hardware
            // instead of skipped.
            var source = new SimulatedStimulus();

            Assert.IsAssignableFrom<IStimulusLimits>(source);

            StimulusLimits limits = ((IStimulusLimits)source).ReadLimits();

            Assert.Equal(SimulatedStimulus.MinimumFrequencyHz, limits.MinimumFrequencyHz);
            Assert.Equal(SimulatedStimulus.MaximumFrequencyHz, limits.MaximumFrequencyHz);
            Assert.Equal(SimulatedStimulus.MinimumLevelDbm, limits.MinimumLevelDbm);
            Assert.Equal(SimulatedStimulus.MaximumLevelDbm, limits.MaximumLevelDbm);
        }

        [Fact]
        public void EverySourceTheShellCanOfferSaysWhatItIsCalledAndWhetherItNeedsAnAddress()
        {
            // The shell cannot reference this assembly (REQ-NFR-032, REQ-ARC-001), so it finds
            // sources by this attribute and nothing else. A source that carries it must therefore
            // carry everything the shell needs to offer it without knowing what it is.
            var marked = typeof(SimulatedStimulus).Assembly
                .GetTypes()
                .Where(t => t.GetCustomAttribute<StimulusProviderAttribute>() != null)
                .ToArray();

            Assert.Contains(typeof(SimulatedStimulus), marked);
            Assert.Contains(typeof(E4438CStimulus), marked);

            foreach (Type type in marked)
            {
                var provider = type.GetCustomAttribute<StimulusProviderAttribute>();

                Assert.False(string.IsNullOrWhiteSpace(provider.DisplayName));

                Assert.True(
                    typeof(IStimulusSource).IsAssignableFrom(type),
                    type.Name + " is marked as a stimulus provider but is not a stimulus source.");

                // The constructor the shell will use, and which one that is depends on the flag.
                // Asserted here rather than discovered at the click that would have created it.
                Type[] signature = provider.RequiresResource
                    ? new[] { typeof(string) }
                    : Type.EmptyTypes;

                Assert.True(
                    type.GetConstructor(signature) != null,
                    type.Name + " declares RequiresResource=" + provider.RequiresResource +
                    " but has no matching public constructor.");

                if (provider.RequiresResource)
                {
                    Assert.False(string.IsNullOrWhiteSpace(provider.DefaultResource));
                }
            }
        }

        private static E4438CStimulus Connected(FakeGenerator generator)
        {
            var source = new E4438CStimulus("FAKE", resource => generator);
            source.Connect();

            return source;
        }

        /// <summary>
        /// Just enough generator to answer the queries a limit probe and a carrier make.
        /// </summary>
        /// <remarks>
        /// <strong>It refuses the way the real one refuses.</strong> A query it will not answer
        /// throws — standing in for the timeout the real firmware produces — <em>and</em> queues an
        /// error, because refusing without queuing would let a driver that never drains the queue
        /// pass this test and fail on the bench.
        /// </remarks>
        private sealed class FakeGenerator : IInstrumentSession
        {
            private readonly Queue<string> _errors = new Queue<string>();
            private int _timeout = 5000;

            public double MinimumFrequencyHz { get; set; } = 100e3;
            public double MaximumFrequencyHz { get; set; } = 3e9;
            public double MinimumLevelDbm { get; set; } = -136.0;
            public double MaximumLevelDbm { get; set; } = 20.0;

            /// <summary>Whether the level node accepts a MIN/MAX suffix on this firmware.</summary>
            public bool AnswersLevelLimits { get; set; } = true;

            /// <summary>The shortest timeout the session was ever set to.</summary>
            public int LowestTimeoutSeen { get; private set; } = int.MaxValue;

            /// <summary>Errors still waiting to be read, as the instrument's queue holds them.</summary>
            public IReadOnlyList<string> QueuedErrors => _errors.ToArray();

            public double FrequencyHz { get; private set; }

            public double LevelDbm { get; private set; }

            public string ResourceName => "FAKE";

            public int TimeoutMilliseconds
            {
                get { return _timeout; }
                set
                {
                    _timeout = value;
                    LowestTimeoutSeen = Math.Min(LowestTimeoutSeen, value);
                }
            }

            public void Write(string command)
            {
                if (command.StartsWith(":FREQuency:CW ", StringComparison.Ordinal))
                {
                    FrequencyHz = TrailingNumber(command);
                }
                else if (command.StartsWith(":POWer:AMPLitude ", StringComparison.Ordinal))
                {
                    LevelDbm = TrailingNumber(command);
                }
            }

            public string Query(string command)
            {
                switch (command)
                {
                    case "*IDN?":
                        return "Agilent Technologies, E4438C, MY00000000, C.05.85";

                    case "*OPC?":
                        return "1";

                    case ":SYSTem:ERRor?":
                        return _errors.Count > 0 ? _errors.Dequeue() : "+0,\"No error\"";

                    case ":FREQuency:CW? MIN":
                        return Number(MinimumFrequencyHz);

                    case ":FREQuency:CW? MAX":
                        return Number(MaximumFrequencyHz);

                    case ":POWer:AMPLitude? MIN":
                        return AnswersLevelLimits ? Number(MinimumLevelDbm) : Refuse();

                    case ":POWer:AMPLitude? MAX":
                        return AnswersLevelLimits ? Number(MaximumLevelDbm) : Refuse();

                    case ":FREQuency:CW?":
                        return Number(FrequencyHz);

                    case ":POWer:AMPLitude?":
                        return Number(LevelDbm);

                    case ":OUTPut:STATe?":
                    case ":RADio:MTONe:ARB:STATe?":
                    case ":RADio:AWGN:ARB:STATe?":
                        return "0";

                    default:
                        return "0";
                }
            }

            public string ReadString() => string.Empty;

            public byte[] ReadBinaryBlock() => new byte[0];

            public void Clear() => _errors.Clear();

            public void Dispose()
            {
            }

            private string Refuse()
            {
                // Queued first, then thrown: that is the order the real instrument does it in, and
                // the order is the whole reason a tolerated probe has to drain afterwards.
                _errors.Enqueue("-108,\"Parameter not allowed\"");

                throw new TimeoutException("The instrument did not answer '" + _timeout + " ms'.");
            }

            private static string Number(double value) =>
                value.ToString("R", CultureInfo.InvariantCulture);

            private static double TrailingNumber(string command)
            {
                string[] parts = command.Split(' ');

                return double.Parse(parts[1], CultureInfo.InvariantCulture);
            }
        }
    }
}
