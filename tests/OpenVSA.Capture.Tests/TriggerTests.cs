using System;
using System.Collections.Generic;
using System.Linq;
using OpenVSA.Capture.Triggering;
using OpenVSA.Core;
using OpenVSA.Hal;
using Xunit;
using Xunit.Abstractions;

namespace OpenVSA.Capture.Tests
{
    /// <summary>
    /// <c>REQ-TRG-001</c> styles, <c>REQ-TRG-002</c> delay including pre-trigger, and
    /// <c>REQ-TRG-003</c> hold-off.
    /// </summary>
    public class TriggerTests
    {
        private const double SampleRateHz = 100e3;

        private readonly ITestOutputHelper _output;

        public TriggerTests(ITestOutputHelper output)
        {
            _output = output;
        }

        // ---- REQ-TRG-001 -----------------------------------------------------------------------

        [Fact]
        public void ASourceIsOfferedExactlyTheStylesItDeclaresAndTheRestAreExplained()
        {
            // The requirement's criterion, expressed against capabilities rather than against a
            // model name - REQ-HAL-002 forbids the latter, and a test written that way would be
            // the first place the rule was broken.
            var capabilities = new Capabilities
            {
                Styles = new[] { TriggerStyle.Immediate, TriggerStyle.External, TriggerStyle.Level },
                RealTime = false,
            };

            IReadOnlyList<TriggerOption> options = TriggerAvailability.For(capabilities);

            foreach (TriggerOption option in options)
            {
                _output.WriteLine(option.ToString());
            }

            // Every style is offered to the eye, so a user can see what exists and what this
            // instrument cannot do.
            Assert.Equal(TriggerAvailability.AllStyles.Count, options.Count);

            Assert.True(options.Single(o => o.Style == TriggerStyle.Immediate).IsAvailable);
            Assert.True(options.Single(o => o.Style == TriggerStyle.External).IsAvailable);
            Assert.True(options.Single(o => o.Style == TriggerStyle.Level).IsAvailable);
            Assert.False(options.Single(o => o.Style == TriggerStyle.Periodic).IsAvailable);
            Assert.False(options.Single(o => o.Style == TriggerStyle.FrequencyMask).IsAvailable);
        }

        [Fact]
        public void EveryUnavailableStyleCarriesAnExplanationAndEveryAvailableOneDoesNot()
        {
            // "Greys the rest with an explanatory tooltip." A control greyed with no reason reads
            // as a bug in the software rather than a limit of the instrument.
            var capabilities = new Capabilities
            {
                Styles = new[] { TriggerStyle.Immediate },
                RealTime = false,
            };

            foreach (TriggerOption option in TriggerAvailability.For(capabilities))
            {
                if (option.IsAvailable)
                {
                    Assert.Equal(string.Empty, option.Explanation);
                }
                else
                {
                    Assert.False(
                        string.IsNullOrWhiteSpace(option.Explanation),
                        TriggerAvailability.NameOf(option.Style) +
                        " is greyed with no explanation.");
                }
            }
        }

        [Fact]
        public void TheFrequencyMaskIsUnsupportedWithoutRealTimeEvenIfTheSourceListsIt()
        {
            // REQ-TRG-001 states this as an override, and it has to be one: a source that declares
            // the style but cannot analyse gap-free would offer a mask that silently missed
            // precisely the transient it was drawn to catch.
            var declaresIt = new Capabilities
            {
                Styles = new[] { TriggerStyle.Immediate, TriggerStyle.FrequencyMask },
                RealTime = false,
            };

            TriggerOption mask =
                TriggerAvailability.For(declaresIt).Single(o => o.Style == TriggerStyle.FrequencyMask);

            _output.WriteLine(mask.Explanation);

            Assert.False(mask.IsAvailable);
            Assert.Contains("real time", mask.Explanation);

            // And with real-time capability it is offered.
            var realTime = new Capabilities
            {
                Styles = new[] { TriggerStyle.Immediate, TriggerStyle.FrequencyMask },
                RealTime = true,
            };

            Assert.True(TriggerAvailability.Offers(realTime, TriggerStyle.FrequencyMask));
        }

        [Fact]
        public void EveryStyleHasTheNameTheRequirementUses()
        {
            // "Free Run", not "Immediate": the enumerator is the code's name and this is the
            // instrument world's, and the requirement lists the latter.
            Assert.Equal("Free Run", TriggerAvailability.NameOf(TriggerStyle.Immediate));
            Assert.Equal("External", TriggerAvailability.NameOf(TriggerStyle.External));
            Assert.Equal("Channel level", TriggerAvailability.NameOf(TriggerStyle.Level));
            Assert.Equal("Periodic", TriggerAvailability.NameOf(TriggerStyle.Periodic));
            Assert.Equal("Frequency mask", TriggerAvailability.NameOf(TriggerStyle.FrequencyMask));

            Assert.Throws<ArgumentOutOfRangeException>(
                () => TriggerAvailability.NameOf((TriggerStyle)99));
        }

        // ---- REQ-TRG-002 -----------------------------------------------------------------------

        [Fact]
        public void ANegativeDelayGivesARecordThatStartsBeforeTheTrigger()
        {
            // REQ-TRG-002's criterion, to within one sample: a burst is injected at a known place,
            // a -10 ms delay is asked for, and the record's first sample must precede the trigger
            // by 10 ms.
            const int burstAt = 4000;                       // 40 ms in, at 100 kS/s
            const double preTriggerSeconds = -10e-3;
            const int record = 2048;

            using (IqBlock block = Burst(12000, burstAt, 3000, 1.0))
            {
                var settings = new TriggerSettings(
                    TriggerStyle.Level, levelVolts: 0.5, delaySeconds: preTriggerSeconds);

                IReadOnlyList<int> instants = TriggerSearch.Instants(block, settings);

                Assert.NotEmpty(instants);
                Assert.True(
                    Math.Abs(instants[0] - burstAt) <= 1,
                    "The trigger fired at " + instants[0] + " against a burst starting at " +
                    burstAt + ".");

                using (IqBlock triggered = TriggerSearch.Extract(block, settings, record))
                {
                    Assert.NotNull(triggered);

                    // The trigger sits 10 ms into the record, so the first sample precedes it by
                    // exactly that.
                    Assert.Equal(-preTriggerSeconds, triggered.TriggerOffsetSeconds, 9);

                    int preSamples = (int)Math.Round(-preTriggerSeconds * SampleRateHz);

                    // Checked against the injected burst rather than against the arithmetic: the
                    // first sample is quiet and the sample at the trigger offset is not.
                    Assert.True(Magnitude(triggered, 0) < 0.1);
                    Assert.True(Magnitude(triggered, preSamples + 1) > 0.5);
                }
            }
        }

        [Fact]
        public void APositiveDelayGivesARecordThatStartsAfterTheTrigger()
        {
            using (IqBlock block = Burst(12000, 4000, 6000, 1.0))
            {
                var settings = new TriggerSettings(
                    TriggerStyle.Level, levelVolts: 0.5, delaySeconds: 10e-3);

                using (IqBlock triggered = TriggerSearch.Extract(block, settings, 1024))
                {
                    Assert.NotNull(triggered);

                    // The trigger is now before the record, so the offset is negative.
                    Assert.Equal(-10e-3, triggered.TriggerOffsetSeconds, 9);
                    Assert.True(Magnitude(triggered, 0) > 0.5);
                }
            }
        }

        [Fact]
        public void APreTriggerThatRunsOffTheFrontIsRefusedRatherThanQuietlyMoved()
        {
            // A record that had been clamped would be a differently-timed record with nothing on
            // screen to say so, which is worse than a measurement that could not be made.
            using (IqBlock block = Burst(4000, 100, 2000, 1.0))
            {
                var settings = new TriggerSettings(
                    TriggerStyle.Level, levelVolts: 0.5, delaySeconds: -10e-3);

                Assert.Null(TriggerSearch.Extract(block, settings, 512));
            }
        }

        [Fact]
        public void WhetherASourceCanServeThePreTriggerIsAskedOfItsCapabilities()
        {
            var withMemory = new Capabilities { PreTrigger = 4096 };
            var without = new Capabilities { PreTrigger = 0 };

            var settings = new TriggerSettings(
                TriggerStyle.Level, levelVolts: 0.5, delaySeconds: -10e-3);

            string reason;

            Assert.True(TriggerSearch.CanServePreTrigger(withMemory, settings, SampleRateHz, out reason));
            Assert.Equal(string.Empty, reason);

            Assert.False(TriggerSearch.CanServePreTrigger(without, settings, SampleRateHz, out reason));
            _output.WriteLine(reason);
            Assert.Contains("1000", reason);

            // No pre-trigger asked for, so there is nothing to refuse.
            Assert.True(
                TriggerSearch.CanServePreTrigger(
                    without, new TriggerSettings(delaySeconds: 1e-3), SampleRateHz, out reason));
        }

        // ---- REQ-TRG-003 -----------------------------------------------------------------------

        [Fact]
        public void ConventionalHoldoffBlanksAFixedWindowAfterEachTrigger()
        {
            // A pulse train of known period, so the trigger instants are analytically predictable:
            // with a hold-off shorter than the period, every pulse triggers.
            const int period = 1000;
            const int width = 200;

            using (IqBlock block = PulseTrain(10000, period, width, 1.0))
            {
                var settings = new TriggerSettings(
                    TriggerStyle.Level,
                    levelVolts: 0.5,
                    holdoff: HoldoffStyle.Conventional,
                    holdoffSeconds: 500.0 / SampleRateHz);

                IReadOnlyList<int> instants = TriggerSearch.Instants(block, settings);

                _output.WriteLine("triggers at " + string.Join(", ", instants));

                Assert.Equal(10, instants.Count);

                for (int i = 0; i < instants.Count; i++)
                {
                    Assert.Equal(FirstPulseAt + i * period, instants[i]);
                }
            }
        }

        [Fact]
        public void AHoldoffLongerThanThePeriodSkipsPulses()
        {
            // The discriminating case: the same train with a hold-off of two and a half periods
            // must trigger on every third pulse, which is what makes the test above mean something.
            const int period = 1000;

            using (IqBlock block = PulseTrain(10000, period, 200, 1.0))
            {
                var settings = new TriggerSettings(
                    TriggerStyle.Level,
                    levelVolts: 0.5,
                    holdoff: HoldoffStyle.Conventional,
                    holdoffSeconds: 2500.0 / SampleRateHz);

                IReadOnlyList<int> instants = TriggerSearch.Instants(block, settings);

                _output.WriteLine("triggers at " + string.Join(", ", instants));

                Assert.Equal(new[] { 100, 3100, 6100, 9100 }, instants.ToArray());
            }
        }

        [Fact]
        public void BelowLevelHoldoffRearmsOnlyAfterAQuietRunOfTheHoldoffLength()
        {
            // The signal must stay below the level for the whole hold-off before the trigger
            // re-arms. In a train of 200-sample pulses every 1000, the quiet run is 800 samples:
            // a 600-sample hold-off is satisfied within every gap, so every pulse triggers.
            const int period = 1000;

            using (IqBlock block = PulseTrain(10000, period, 200, 1.0))
            {
                IReadOnlyList<int> everyPulse = TriggerSearch.Instants(
                    block,
                    new TriggerSettings(
                        TriggerStyle.Level,
                        levelVolts: 0.5,
                        holdoff: HoldoffStyle.BelowLevel,
                        holdoffSeconds: 600.0 / SampleRateHz));

                _output.WriteLine("600-sample hold-off: " + string.Join(", ", everyPulse));
                Assert.Equal(10, everyPulse.Count);

                // A hold-off longer than the quiet run is never satisfied, so after the first
                // trigger the signal never stays below the level for long enough and nothing else
                // fires. That is the behaviour the style exists for, and it is not what a
                // conventional hold-off of the same length would do.
                IReadOnlyList<int> onlyTheFirst = TriggerSearch.Instants(
                    block,
                    new TriggerSettings(
                        TriggerStyle.Level,
                        levelVolts: 0.5,
                        holdoff: HoldoffStyle.BelowLevel,
                        holdoffSeconds: 900.0 / SampleRateHz));

                _output.WriteLine("900-sample hold-off: " + string.Join(", ", onlyTheFirst));
                Assert.Single(onlyTheFirst);

                IReadOnlyList<int> conventional = TriggerSearch.Instants(
                    block,
                    new TriggerSettings(
                        TriggerStyle.Level,
                        levelVolts: 0.5,
                        holdoff: HoldoffStyle.Conventional,
                        holdoffSeconds: 900.0 / SampleRateHz));

                Assert.Equal(10, conventional.Count);
            }
        }

        [Fact]
        public void AboveLevelHoldoffIsTheMirrorCase()
        {
            // The signal must stay above the level for the whole hold-off. In the same train the
            // pulses are 200 samples long, so a 100-sample hold-off is satisfied inside a pulse and
            // a 300-sample one never is.
            using (IqBlock block = PulseTrain(10000, 1000, 200, 1.0))
            {
                IReadOnlyList<int> satisfied = TriggerSearch.Instants(
                    block,
                    new TriggerSettings(
                        TriggerStyle.Level,
                        levelVolts: 0.5,
                        holdoff: HoldoffStyle.AboveLevel,
                        holdoffSeconds: 100.0 / SampleRateHz));

                IReadOnlyList<int> never = TriggerSearch.Instants(
                    block,
                    new TriggerSettings(
                        TriggerStyle.Level,
                        levelVolts: 0.5,
                        holdoff: HoldoffStyle.AboveLevel,
                        holdoffSeconds: 300.0 / SampleRateHz));

                _output.WriteLine(
                    "above-level, 100 samples: " + satisfied.Count + " triggers; 300 samples: " +
                    never.Count);

                Assert.Equal(10, satisfied.Count);
                Assert.Single(never);
            }
        }

        [Fact]
        public void ANegativeHoldoffIsRejectedAtInputValidation()
        {
            ArgumentOutOfRangeException failure = Assert.Throws<ArgumentOutOfRangeException>(
                () => new TriggerSettings(holdoffSeconds: -1e-3));

            _output.WriteLine(failure.Message);
            Assert.Contains("cannot be negative", failure.Message);
        }

        [Fact]
        public void TheOtherArgumentsAreChecked()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new TriggerSettings(levelVolts: -1.0));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => new TriggerSettings(delaySeconds: double.NaN));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => new TriggerSettings(TriggerStyle.Periodic, periodSeconds: 0.0));

            using (IqBlock block = PulseTrain(100, 50, 10, 1.0))
            {
                Assert.Throws<ArgumentNullException>(() => TriggerSearch.Instants(null, new TriggerSettings()));
                Assert.Throws<ArgumentNullException>(() => TriggerSearch.Instants(block, null));
                Assert.Throws<ArgumentOutOfRangeException>(
                    () => TriggerSearch.Extract(block, new TriggerSettings(), 0));
                Assert.Throws<ArgumentOutOfRangeException>(
                    () => TriggerSearch.Extract(block, new TriggerSettings(), 10, -1));
            }

            Assert.Throws<ArgumentNullException>(() => TriggerAvailability.For(null));
        }

        [Fact]
        public void FreeRunTriggersAtTheStartAndPeriodicAtItsPeriod()
        {
            using (IqBlock block = PulseTrain(10000, 1000, 200, 1.0))
            {
                Assert.Equal(new[] { 0 }, TriggerSearch.Instants(block, new TriggerSettings()).ToArray());

                IReadOnlyList<int> periodic = TriggerSearch.Instants(
                    block,
                    new TriggerSettings(TriggerStyle.Periodic, periodSeconds: 2500.0 / SampleRateHz));

                Assert.Equal(new[] { 0, 2500, 5000, 7500 }, periodic.ToArray());
            }
        }

        [Fact]
        public void AFallingEdgeTriggersWhereARisingOneDoesNot()
        {
            // The edge selection has to do something, or every test above would pass on an
            // implementation that ignored it.
            using (IqBlock block = PulseTrain(10000, 1000, 200, 1.0))
            {
                IReadOnlyList<int> rising = TriggerSearch.Instants(
                    block, new TriggerSettings(TriggerStyle.Level, levelVolts: 0.5, risingEdge: true));

                IReadOnlyList<int> falling = TriggerSearch.Instants(
                    block, new TriggerSettings(TriggerStyle.Level, levelVolts: 0.5, risingEdge: false));

                Assert.Equal(rising.Count, falling.Count);

                for (int i = 0; i < rising.Count; i++)
                {
                    Assert.Equal(200, falling[i] - rising[i]);
                }
            }
        }

        [Fact]
        public void TheTriggerFiresOnTheEnvelopeRatherThanOnTheInPhaseComponent()
        {
            // A carrier well above the level passes through zero twice a cycle, so a trigger on I
            // would fire on the carrier rather than on the burst - many times, not once.
            using (IqBlock block = Burst(4000, 1000, 2000, 1.0))
            {
                IReadOnlyList<int> instants = TriggerSearch.Instants(
                    block, new TriggerSettings(TriggerStyle.Level, levelVolts: 0.5));

                Assert.Single(instants);
            }
        }

        // ---- Signals ---------------------------------------------------------------------------

        private static double Magnitude(IqBlock block, int n)
        {
            Complex32 sample = block.GetSample(n);
            return Math.Sqrt(sample.MagnitudeSquared);
        }

        /// <summary>A quiet record with one burst of a rotating carrier in the middle of it.</summary>
        private static IqBlock Burst(int count, int start, int length, double amplitude)
        {
            IqBlock block = Rent(count);
            Span<float> samples = block.GetSamples();

            for (int n = 0; n < count; n++)
            {
                bool inBurst = n >= start && n < start + length;
                double a = inBurst ? amplitude : 0.0;
                double phase = 2.0 * Math.PI * 0.03 * n;

                samples[n * 2] = (float)(a * Math.Cos(phase));
                samples[n * 2 + 1] = (float)(a * Math.Sin(phase));
            }

            return block;
        }

        /// <summary>Where the first pulse of a train starts.</summary>
        /// <remarks>
        /// Not zero. A train that was already high at sample 0 presents no crossing there, so the
        /// first trigger would land one sample late and every expectation in these tests would
        /// carry an off-by-one that had nothing to do with the trigger.
        /// </remarks>
        private const int FirstPulseAt = 100;

        /// <summary>A train of rectangular pulses of known period.</summary>
        private static IqBlock PulseTrain(int count, int period, int width, double amplitude)
        {
            IqBlock block = Rent(count);
            Span<float> samples = block.GetSamples();

            for (int n = 0; n < count; n++)
            {
                int intoPeriod = (n - FirstPulseAt) % period;
                bool high = n >= FirstPulseAt && intoPeriod >= 0 && intoPeriod < width;

                samples[n * 2] = (float)(high ? amplitude : 0.0);
                samples[n * 2 + 1] = 0.0f;
            }

            return block;
        }

        private static IqBlock Rent(int count) =>
            IqBlock.Rent(new IqBlockMetadata(
                sampleCount: count,
                sampleRateHz: SampleRateHz,
                centerFrequencyHz: 1e9,
                isBaseband: false,
                fullScaleVolts: 1.0,
                referenceLevelDbm: 0.0,
                sequenceNumber: 0,
                acquiredUtc: DateTime.UtcNow,
                triggerOffsetSeconds: 0.0,
                triggerCorrectionsApplied: false,
                source: new FrontEndId("test"),
                extended: null));

        /// <summary>Capabilities that declare exactly what a test wants to declare.</summary>
        private sealed class Capabilities : IFrontEndCapabilities
        {
            public IReadOnlyList<TriggerStyle> Styles { get; set; } = new TriggerStyle[0];

            public bool RealTime { get; set; }

            public long PreTrigger { get; set; }

            public FrequencyRange CenterFrequencyRange => new FrequencyRange(0.0, 26.5e9);
            public double MaxSpanHz => 40e6;
            public double MinSpanHz => 1.0;
            public double MaxSampleRateHz => 51.2e6;
            public int MaxSamplesPerBlock => 1 << 20;
            public long MaxCaptureSamples => 1L << 30;
            public bool SupportsBasebandIq => true;
            public int ChannelCount => 1;
            public bool SupportsPhaseCoherentChannels => false;
            public IReadOnlyList<TriggerStyle> TriggerStyles => Styles;
            public AmplitudeRange ReferenceLevelRange => new AmplitudeRange(-100.0, 30.0);
            public bool SupportsExternalRef => false;
            public bool SupportsRealTimeAnalysis => RealTime;
            public long MaxPreTriggerSamples => PreTrigger;
        }
    }
}
