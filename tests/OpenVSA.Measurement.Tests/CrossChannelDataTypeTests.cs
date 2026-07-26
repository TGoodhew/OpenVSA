using System;
using System.Collections.Generic;
using System.Linq;
using OpenVSA.Core;
using OpenVSA.Dsp.Spectrum;
using OpenVSA.Hal;
using OpenVSA.Measurement;
using Xunit;

namespace OpenVSA.Measurement.Tests
{
    /// <summary>
    /// <c>REQ-DSP-040a</c>: the cross-channel types are offered only where the front end can
    /// actually supply them.
    /// </summary>
    /// <remarks>
    /// The rest of that requirement — coherence of a signal with itself, the recovery of a known
    /// filter's response — needs a phase-coherent two-channel source, which no front end provides.
    /// The availability rule does not, and it is the half that can be settled now.
    /// </remarks>
    public class CrossChannelDataTypeTests
    {
        [Fact]
        public void ASingleChannelSourceOffersNoneOfThem()
        {
            // "Absent from the selectable set rather than present and erroring." Absent is the
            // point: an entry that throws when picked says nothing until it is too late.
            var single = new Capabilities { Channels = 1, Coherent = false };

            Assert.False(CrossChannelDataTypes.IsSupportedBy(single));
            Assert.Empty(CrossChannelDataTypes.AvailableFor(single));
        }

        [Fact]
        public void TwoChannelsWithoutCoherenceOfferNoneEither()
        {
            // Two channels digitised independently give a cross spectrum whose phase is whatever
            // the two clocks were doing - a number with no meaning and no obvious symptom.
            var incoherent = new Capabilities { Channels = 2, Coherent = false };

            Assert.False(CrossChannelDataTypes.IsSupportedBy(incoherent));
            Assert.Empty(CrossChannelDataTypes.AvailableFor(incoherent));
        }

        [Fact]
        public void OneChannelDeclaredCoherentIsStillNotEnough()
        {
            var lonely = new Capabilities { Channels = 1, Coherent = true };

            Assert.False(CrossChannelDataTypes.IsSupportedBy(lonely));
            Assert.Empty(CrossChannelDataTypes.AvailableFor(lonely));
        }

        [Fact]
        public void ACoherentTwoChannelSourceOffersAllFive()
        {
            // All or nothing: they rest on the same guarantee, so a source that can supply one can
            // supply all of them.
            var coherent = new Capabilities { Channels = 2, Coherent = true };

            Assert.True(CrossChannelDataTypes.IsSupportedBy(coherent));

            IReadOnlyList<CrossChannelDataType> offered =
                CrossChannelDataTypes.AvailableFor(coherent);

            Assert.Equal(5, offered.Count);
            Assert.Equal(
                new[]
                {
                    "Cross Spectrum", "Cross Correlation", "Coherence",
                    "Frequency Response", "Impulse Response",
                },
                offered.Select(CrossChannelDataTypes.Describe));
        }

        [Fact]
        public void TheTwoWaysOfBeingUnavailableAreExplainedDifferently()
        {
            // One is "you need a different instrument"; the other is "your instrument has the
            // inputs but will not promise they share a clock". Collapsing them into one sentence
            // would send someone looking for the wrong thing.
            string oneInput = CrossChannelDataTypes.ExplainUnavailability(
                new Capabilities { Channels = 1, Coherent = false });
            string noClock = CrossChannelDataTypes.ExplainUnavailability(
                new Capabilities { Channels = 2, Coherent = false });

            Assert.Contains("two inputs", oneInput);
            Assert.Contains("phase coherent", noClock);
            Assert.NotEqual(oneInput, noClock);

            Assert.Equal(
                string.Empty,
                CrossChannelDataTypes.ExplainUnavailability(
                    new Capabilities { Channels = 2, Coherent = true }));
        }

        [Fact]
        public void TheyAreNotBaseTraceDataTypes()
        {
            // A separate enumeration, because everything in TraceDataType is a function of one
            // IqBlock and every one of these is a function of two. One list with two availability
            // rules and two input shapes would have the difference rediscovered at every call site.
            string[] baseTypes = Enum.GetNames(typeof(TraceDataType));

            foreach (string name in Enum.GetNames(typeof(CrossChannelDataType)))
            {
                Assert.DoesNotContain(name, baseTypes);
            }
        }

        [Fact]
        public void EveryTypeHasAName()
        {
            foreach (CrossChannelDataType type in CrossChannelDataTypes.All)
            {
                Assert.False(string.IsNullOrEmpty(CrossChannelDataTypes.Describe(type)));
            }

            Assert.Throws<ArgumentOutOfRangeException>(
                () => CrossChannelDataTypes.Describe((CrossChannelDataType)99));
        }

        [Fact]
        public void MissingCapabilitiesAreRefused()
        {
            Assert.Throws<ArgumentNullException>(() => CrossChannelDataTypes.IsSupportedBy(null));
            Assert.Throws<ArgumentNullException>(() => CrossChannelDataTypes.AvailableFor(null));
            Assert.Throws<ArgumentNullException>(
                () => CrossChannelDataTypes.ExplainUnavailability(null));
        }

        private sealed class Capabilities : IFrontEndCapabilities
        {
            private static readonly IReadOnlyList<TriggerStyle> Styles =
                new List<TriggerStyle> { TriggerStyle.Immediate }.AsReadOnly();

            public int Channels { get; set; } = 1;

            public bool Coherent { get; set; }

            public FrequencyRange CenterFrequencyRange => new FrequencyRange(0.0, 26.5e9);
            public double MaxSpanHz => 40e6;
            public double MinSpanHz => 1.0;
            public double MaxSampleRateHz => 51.2e6;
            public int MaxSamplesPerBlock => 1 << 20;
            public long MaxCaptureSamples => 1 << 20;
            public bool SupportsBasebandIq => true;
            public int ChannelCount => Channels;
            public bool SupportsPhaseCoherentChannels => Coherent;
            public IReadOnlyList<TriggerStyle> TriggerStyles => Styles;
            public AmplitudeRange ReferenceLevelRange => new AmplitudeRange(-100.0, 30.0);
            public bool SupportsExternalRef => false;
            public bool SupportsInputRangeControl => true;
            public bool SupportsRealTimeAnalysis => false;
            public long MaxPreTriggerSamples => 0L;
        }
    }
}
