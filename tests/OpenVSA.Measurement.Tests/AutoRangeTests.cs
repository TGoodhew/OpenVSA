using System;
using System.Collections.Generic;
using OpenVSA.Hal;
using OpenVSA.Measurement;
using Xunit;
using Xunit.Abstractions;

namespace OpenVSA.Measurement.Tests
{
    /// <summary>
    /// <c>REQ-ACQ-004</c>: auto-ranging settles inside a stated headroom band, raises an
    /// indication when it acts, and is unavailable where the front end cannot range.
    /// </summary>
    public class AutoRangeTests
    {
        private readonly ITestOutputHelper _output;

        public AutoRangeTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void ASignalTwentyDecibelsBelowTheRangeIsBroughtBackIntoTheBand()
        {
            // The first half of the criterion, in its numbers. Awkward ones deliberately: a peak at
            // a round -20.0 dBm would sit exactly on the 1 dB grid and could not tell a quantiser
            // that rounds the wrong way from one that rounds at all.
            var capabilities = new Capabilities();

            AutoRangeResult result = AutoRange.Adjust(capabilities, -3.7, -23.7);

            _output.WriteLine(result.Message);

            Assert.True(result.Changed);
            Assert.True(result.IsWithinBand);
            Assert.False(result.LimitedByRange);

            // -23.7 + 10 = -13.7, rounded up onto the 1 dB grid.
            Assert.Equal(-13.0, result.ReferenceLevelDbm, 9);
            Assert.Equal(10.7, result.HeadroomDb, 9);
        }

        [Fact]
        public void ASignalThatOverloadsTheRangeIsGivenRoomAboveIt()
        {
            // The second half: a peak above the reference level. Nothing about the arithmetic knows
            // which direction it is moving, which is the point - one rule covers both faults.
            var capabilities = new Capabilities();

            AutoRangeResult result = AutoRange.Adjust(capabilities, -3.7, 4.2);

            _output.WriteLine(result.Message);

            Assert.True(result.Changed);
            Assert.True(result.IsWithinBand);
            Assert.False(result.IsOverloaded);
            Assert.Equal(15.0, result.ReferenceLevelDbm, 9);
            Assert.Contains("over the range", result.Message);
        }

        [Theory]
        [InlineData(-23.7)]
        [InlineData(4.2)]
        [InlineData(-83.1)]
        [InlineData(21.6)]
        public void RepeatedInvocationOnAnUnchangingSignalProducesNoFurtherChange(double peakDbm)
        {
            // "Settles rather than oscillating" is the criterion that fails silently if it is only
            // reasoned about, so it is run: adjust, feed the answer back, and require the second
            // pass to do nothing at all - and a third, in case the second merely alternated.
            var capabilities = new Capabilities();

            AutoRangeResult first = AutoRange.Adjust(capabilities, -3.7, peakDbm);
            AutoRangeResult second = AutoRange.Adjust(capabilities, first.ReferenceLevelDbm, peakDbm);
            AutoRangeResult third = AutoRange.Adjust(capabilities, second.ReferenceLevelDbm, peakDbm);

            _output.WriteLine(first.Message);
            _output.WriteLine(second.Message);

            Assert.False(second.Changed);
            Assert.False(third.Changed);
            Assert.Equal(first.ReferenceLevelDbm, second.ReferenceLevelDbm, 9);
            Assert.Equal(first.ReferenceLevelDbm, third.ReferenceLevelDbm, 9);
        }

        [Fact]
        public void APeakAlreadyInsideTheBandIsLeftAlone()
        {
            // The dead zone. Without it every invocation would move the level by a fraction of a
            // decibel and raise the indication for it, and the indication would stop meaning
            // anything.
            var capabilities = new Capabilities();

            AutoRangeResult result = AutoRange.Adjust(capabilities, -3.7, -12.1);

            _output.WriteLine(result.Message);

            Assert.False(result.Changed);
            Assert.Equal(-3.7, result.ReferenceLevelDbm, 9);
            Assert.Contains("inside", result.Message);
        }

        [Theory]
        [InlineData(3.9)]
        [InlineData(16.1)]
        public void APeakJustOutsideTheBandIsActedOn(double headroomDb)
        {
            // The two edges of the dead zone, from just outside. A band that is not tested at its
            // boundaries is a band whose comparison could be the wrong way round in either place.
            var capabilities = new Capabilities();
            double peak = -11.3;

            AutoRangeResult result = AutoRange.Adjust(capabilities, peak + headroomDb, peak);

            _output.WriteLine(result.Message);

            Assert.True(result.Changed);
            Assert.True(result.IsWithinBand);
        }

        [Fact]
        public void ASignalTooLargeForTheHighestRangeSettlesAtItAndSaysSo()
        {
            // An overload that cannot be escaped. The wrong behaviour is to keep asking for a level
            // the instrument does not have, reporting an adjustment every time on a signal that is
            // not moving; the right one is to go as far as it can, say why that is not far enough,
            // and stop.
            var capabilities = new Capabilities { Reference = new AmplitudeRange(-100.0, 10.0) };

            AutoRangeResult first = AutoRange.Adjust(capabilities, -3.7, 8.4);
            AutoRangeResult second = AutoRange.Adjust(capabilities, first.ReferenceLevelDbm, 8.4);

            _output.WriteLine(first.Message);
            _output.WriteLine(second.Message);

            Assert.True(first.Changed);
            Assert.True(first.LimitedByRange);
            Assert.False(first.IsWithinBand);
            Assert.Equal(10.0, first.ReferenceLevelDbm, 9);
            Assert.Contains("highest", first.Message);

            // And it stops there.
            Assert.False(second.Changed);
            Assert.True(second.LimitedByRange);
        }

        [Fact]
        public void ASignalBelowTheLowestRangeSettlesAtItAndSaysSo()
        {
            // The mirror case, which a rule written only for overload gets wrong: there is no point
            // chasing a noise floor down past the bottom of the instrument's range.
            var capabilities = new Capabilities { Reference = new AmplitudeRange(-20.0, 30.0) };

            AutoRangeResult first = AutoRange.Adjust(capabilities, 12.3, -70.6);
            AutoRangeResult second = AutoRange.Adjust(capabilities, first.ReferenceLevelDbm, -70.6);

            _output.WriteLine(first.Message);
            _output.WriteLine(second.Message);

            Assert.True(first.Changed);
            Assert.True(first.LimitedByRange);
            Assert.Equal(-20.0, first.ReferenceLevelDbm, 9);
            Assert.Contains("lowest", first.Message);
            Assert.False(second.Changed);
        }

        [Fact]
        public void AFrontEndWithoutRangeControlOffersNothingRatherThanDoingNothing()
        {
            // The requirement's last clause. A command that is present, enabled, and inert is worse
            // than one that is absent: the user believes the range has been set.
            var capabilities = new Capabilities { RangeControl = false };

            AutoRangeAvailability availability = AutoRangeAvailability.For(capabilities);

            _output.WriteLine(availability.Explanation);

            Assert.False(availability.IsAvailable);
            Assert.NotEqual(string.Empty, availability.Explanation);

            InvalidOperationException refused = Assert.Throws<InvalidOperationException>(
                () => AutoRange.Adjust(capabilities, -3.7, -23.7));

            Assert.Equal(availability.Explanation, refused.Message);
        }

        [Fact]
        public void AFrontEndWithASingleReferenceLevelAlsoOffersNothing()
        {
            // Range control declared, but nowhere to move to. Reported as unavailable for its own
            // reason rather than the previous one, because "it has no range control" would be a
            // false statement about this front end.
            var capabilities = new Capabilities { Reference = new AmplitudeRange(-10.0, -10.0) };

            AutoRangeAvailability availability = AutoRangeAvailability.For(capabilities);

            _output.WriteLine(availability.Explanation);

            Assert.False(availability.IsAvailable);
            Assert.Contains("single reference level", availability.Explanation);
        }

        [Fact]
        public void AFrontEndThatCanRangeOffersTheCommand()
        {
            Assert.True(AutoRangeAvailability.For(new Capabilities()).IsAvailable);
            Assert.Equal(string.Empty, AutoRangeAvailability.For(new Capabilities()).Explanation);
        }

        [Fact]
        public void AnAdjustmentReportsTheLevelItCameFromAsWellAsTheOneItWentTo()
        {
            // Both ends of the move, because the indication of REQ-UI-007 is raised on the change
            // and the event log needs to say what changed, not merely that something did.
            var capabilities = new Capabilities();

            AutoRangeResult result = AutoRange.Adjust(capabilities, -3.7, -23.7);

            Assert.Equal(-3.7, result.PreviousReferenceLevelDbm, 9);
            Assert.Equal(-23.7, result.PeakDbm, 9);
            Assert.Contains("-3.7", result.Message);
            Assert.Contains("-13", result.Message);
        }

        [Fact]
        public void ANarrowerBandStillSettles()
        {
            // The band is stated, so a caller may state a different one. Whatever they choose has
            // to settle, which is why the constructor refuses one that cannot.
            var capabilities = new Capabilities();
            var tight = new HeadroomBand(1.0, 3.0, 1.5, 0.5);

            AutoRangeResult first = AutoRange.Adjust(capabilities, -3.7, -23.7, tight);
            AutoRangeResult second = AutoRange.Adjust(
                capabilities, first.ReferenceLevelDbm, -23.7, tight);

            _output.WriteLine(first.Message);

            Assert.True(first.Changed);
            Assert.True(first.IsWithinBand);
            Assert.False(second.Changed);
        }

        [Fact]
        public void ABandThatCouldNotSettleIsRefusedWhenItIsBuilt()
        {
            // Target plus step must fit inside the band, or an adjustment can land outside the band
            // it was made to satisfy and adjust again. Caught here rather than as an oscillation
            // somebody has to diagnose from a flickering RNG.
            ArgumentOutOfRangeException refused = Assert.Throws<ArgumentOutOfRangeException>(
                () => new HeadroomBand(4.0, 10.0, 9.5, 1.0));

            _output.WriteLine(refused.Message);
            Assert.Contains("settle", refused.Message);

            // And the surrounding checks on a band that makes no sense at all.
            Assert.Throws<ArgumentOutOfRangeException>(() => new HeadroomBand(-1.0, 10.0, 5.0, 1.0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new HeadroomBand(4.0, 4.0, 4.0, 1.0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new HeadroomBand(4.0, 16.0, 10.0, 0.0));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => new HeadroomBand(4.0, 16.0, double.NaN, 1.0));
        }

        [Fact]
        public void TheDefaultBandIsTheOneTheHelpTextStates()
        {
            HeadroomBand band = HeadroomBand.Default;

            Assert.Equal(4.0, band.MinimumDb, 9);
            Assert.Equal(16.0, band.MaximumDb, 9);
            Assert.Equal(10.0, band.TargetDb, 9);
            Assert.Equal(1.0, band.StepDb, 9);
            Assert.True(band.Contains(4.0));
            Assert.True(band.Contains(16.0));
            Assert.False(band.Contains(3.99));
            Assert.False(band.Contains(16.01));
        }

        [Fact]
        public void ALevelAlreadyOnTheGridIsNotNudgedOffIt()
        {
            // Rounding up must leave a value that is already exact alone. -30 + 10 is arithmetically
            // -20 exactly; a ceiling with no tolerance turns a hair of representation error into a
            // 1 dB move, and the second invocation would then differ from the first.
            var capabilities = new Capabilities();

            AutoRangeResult result = AutoRange.Adjust(capabilities, 12.0, -30.0);

            Assert.Equal(-20.0, result.ReferenceLevelDbm, 9);
            Assert.Equal(10.0, result.HeadroomDb, 9);
        }

        [Fact]
        public void NothingIsDecidedFromValuesThatAreNotNumbers()
        {
            var capabilities = new Capabilities();

            Assert.Throws<ArgumentNullException>(() => AutoRange.Adjust(null, 0.0, -20.0));
            Assert.Throws<ArgumentNullException>(() => AutoRangeAvailability.For(null));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => AutoRange.Adjust(capabilities, double.NaN, -20.0));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => AutoRange.Adjust(capabilities, 0.0, double.NegativeInfinity));
        }

        /// <summary>Capabilities whose range control and range are set per test.</summary>
        private sealed class Capabilities : IFrontEndCapabilities
        {
            private static readonly IReadOnlyList<TriggerStyle> Styles =
                new List<TriggerStyle> { TriggerStyle.Immediate }.AsReadOnly();

            public bool RangeControl { get; set; } = true;

            public AmplitudeRange Reference { get; set; } = new AmplitudeRange(-100.0, 30.0);

            public FrequencyRange CenterFrequencyRange => new FrequencyRange(0.0, 26.5e9);
            public double MaxSpanHz => 40e6;
            public double MinSpanHz => 1.0;
            public double MaxSampleRateHz => 51.2e6;
            public int MaxSamplesPerBlock => 1 << 16;
            public long MaxCaptureSamples => 1L << 32;
            public bool SupportsBasebandIq => true;
            public int ChannelCount => 1;
            public bool SupportsPhaseCoherentChannels => false;
            public IReadOnlyList<TriggerStyle> TriggerStyles => Styles;
            public AmplitudeRange ReferenceLevelRange => Reference;
            public bool SupportsExternalRef => false;
            public bool SupportsInputRangeControl => RangeControl;
            public bool SupportsRealTimeAnalysis => false;
            public long MaxPreTriggerSamples => 0L;
        }
    }
}
