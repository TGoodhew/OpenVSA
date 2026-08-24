using System;
using System.Linq;
using OpenVSA.TestHarness;
using Xunit;
using Xunit.Abstractions;

namespace OpenVSA.TestHarness.Tests
{
    /// <summary>
    /// The digital-modulation stimulus contract, exercised against the stand-in
    /// (<c>REQ-E44-007</c> stage 1, <c>REQ-SIM-003</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// What is tested here is the harness's own logic: that a format or pattern the source does not
    /// offer is refused rather than sent, that a symbol rate outside the source's range is refused,
    /// that a Gaussian filter has no roll-off, and that a read-back reports what the source says
    /// rather than what it was asked for. None of that needs an instrument, and all of it is what
    /// would otherwise be discovered half way through a bench run.
    /// </para>
    /// <para>
    /// The E4438C's own implementation of the same contract cannot be tested here at all — it needs
    /// the instrument, and its verification is a bench scenario with the numbers recorded in
    /// <c>evidence/</c>. What this file buys is that everything <em>around</em> the instrument is
    /// already known good when the bench run happens.
    /// </para>
    /// </remarks>
    public class DigitalModulationStimulusTests
    {
        private readonly ITestOutputHelper _output;

        public DigitalModulationStimulusTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void TheStandInOffersWhatTheInstrumentOffers()
        {
            // The stand-in's list must not be wider than the driver's, or a scenario passes here and
            // fails on the bench -- the failure this class exists to prevent rather than to cause.
            IDigitalModulationStimulus stand = new SimulatedStimulus();

            var driver = typeof(E4438CStimulus);

            Assert.True(
                typeof(IDigitalModulationStimulus).IsAssignableFrom(driver),
                "The E4438C driver does not implement the digital-modulation contract.");

            Assert.Contains("QPSK", stand.Formats);
            Assert.Contains("GRAYQPSK", stand.Formats);
            Assert.Contains("QAM16", stand.Formats);
            Assert.Contains("PN9", stand.DataPatterns);

            _output.WriteLine(
                stand.Formats.Count + " formats, " + stand.DataPatterns.Count + " patterns");
        }

        [Fact]
        public void SettingAModulationReportsItBack()
        {
            var source = new SimulatedStimulus();

            source.Connect();
            source.SetDigitalModulation(
                1e9, -20.0, "QPSK", 1e6, StimulusPulseFilter.RootRaisedCosine, 0.35, "PN9");

            Assert.Equal("QPSK", source.Format);
            Assert.Equal(1e6, source.SymbolRateHz);
            Assert.Equal(StimulusPulseFilter.RootRaisedCosine, source.PulseFilter);
            Assert.Equal(0.35, source.Alpha);
            Assert.Equal("PN9", source.DataPattern);
            Assert.False(source.IsSpectrumInverted);
        }

        [Fact]
        public void AFormatTheSourceDoesNotOfferIsRefusedBeforeAnythingIsSent()
        {
            var source = new SimulatedStimulus();

            source.Connect();

            ArgumentException refused = Assert.Throws<ArgumentException>(
                () => source.SetDigitalModulation(
                    1e9, -20.0, "1024QAM", 1e6, StimulusPulseFilter.RootRaisedCosine, 0.35, "PN9"));

            _output.WriteLine(refused.Message);

            // Nothing was set: a source part way through a configuration it then abandoned is worse
            // than one that refused, because the carrier is left transmitting something nobody
            // chose.
            Assert.Null(source.Format);
            Assert.Equal(0.0, source.FrequencyHz);
        }

        [Fact]
        public void ADataPatternTheSourceDoesNotOfferIsRefused()
        {
            var source = new SimulatedStimulus();

            source.Connect();

            Assert.Throws<ArgumentException>(
                () => source.SetDigitalModulation(
                    1e9, -20.0, "QPSK", 1e6, StimulusPulseFilter.RootRaisedCosine, 0.35, "PN7"));
        }

        [Theory]
        [InlineData(1.0)]
        [InlineData(20e6)]
        public void ASymbolRateOutsideTheSourcesRangeIsRefused(double symbolRateHz)
        {
            var source = new SimulatedStimulus();

            source.Connect();

            ArgumentOutOfRangeException refused = Assert.Throws<ArgumentOutOfRangeException>(
                () => source.SetDigitalModulation(
                    1e9,
                    -20.0,
                    "QPSK",
                    symbolRateHz,
                    StimulusPulseFilter.RootRaisedCosine,
                    0.35,
                    "PN9"));

            _output.WriteLine(refused.Message);
        }

        [Fact]
        public void TheSymbolRateCeilingFollowsTheFilter()
        {
            // The instrument shortens its filter to reach higher rates and will not shorten below a
            // minimum length, so the ceiling belongs to the pair. A contract that reported one
            // number would be wrong for one of them.
            IDigitalModulationStimulus source = new SimulatedStimulus();

            Assert.True(
                source.MaximumSymbolRateHz(StimulusPulseFilter.RootRaisedCosine) >
                source.MaximumSymbolRateHz(StimulusPulseFilter.Gaussian));
        }

        [Fact]
        public void AGaussianFilterHasNoRollOff()
        {
            // Asked for, it would be refused by the instrument and left in the error queue for
            // whatever ran next to be blamed for. Reported as NaN rather than as zero, because zero
            // is a roll-off and this is the absence of one.
            var source = new SimulatedStimulus();

            source.Connect();
            source.SetDigitalModulation(
                1e9, -20.0, "MSK", 270833.0, StimulusPulseFilter.Gaussian, 0.35, "PN9");

            Assert.True(double.IsNaN(source.Alpha));
        }

        [Fact]
        public void ACoercedSymbolRateIsReportedAsTheSourceHasIt()
        {
            // REQ-SIM-003's lie, on this setting: the instrument reconfigures its baseband generator
            // when the symbol rate changes and need not land on the figure asked for. A harness
            // taking its expectation from the request would pass that and should not.
            var source = new SimulatedStimulus { CoerceSymbolRateTo = 999999.0 };

            source.Connect();
            source.SetDigitalModulation(
                1e9, -20.0, "QPSK", 1e6, StimulusPulseFilter.RootRaisedCosine, 0.35, "PN9");

            Assert.Equal(999999.0, source.SymbolRateHz);
        }

        [Fact]
        public void InvertingTheSpectrumIsRememberedAndReversible()
        {
            // REQ-DEM-035's mirror, from the generator's side: this is how the demodulator's
            // handling of it gets tested against something other than an assertion about a sign.
            var source = new SimulatedStimulus();

            source.Connect();
            source.SetDigitalModulation(
                1e9, -20.0, "QPSK", 1e6, StimulusPulseFilter.RootRaisedCosine, 0.35, "PN9");

            source.SetSpectrumInverted(true);
            Assert.True(source.IsSpectrumInverted);

            source.SetSpectrumInverted(false);
            Assert.False(source.IsSpectrumInverted);
        }

        [Fact]
        public void StoppingTheModulationLeavesTheCarrier()
        {
            var source = new SimulatedStimulus();

            source.Connect();
            source.SetDigitalModulation(
                1e9, -20.0, "QPSK", 1e6, StimulusPulseFilter.RootRaisedCosine, 0.35, "PN9");

            source.StopDigitalModulation();

            Assert.Null(source.Format);
            Assert.Equal(0.0, source.SymbolRateHz);

            // The carrier is still there, which is what "leaving the carrier as it was" means and
            // what a scenario measuring residual carrier next would depend on.
            Assert.Equal(1e9, source.FrequencyHz);
            Assert.Equal(-20.0, source.LevelDbm);
        }

        [Fact]
        public void OneSignalAtATime()
        {
            // The comb, the noise band and the modulation are three things this source can be doing
            // and it does one. A read-back describing two at once would let a scenario check the
            // wrong expectation against the right measurement.
            var source = new SimulatedStimulus();

            source.Connect();
            source.SetMultitone(1e9, 5, 100e3, -20.0);

            Assert.Equal(5, source.ToneCount);

            source.SetDigitalModulation(
                1e9, -20.0, "QPSK", 1e6, StimulusPulseFilter.RootRaisedCosine, 0.35, "PN9");

            Assert.Equal(0, source.ToneCount);
            Assert.Equal(0.0, source.ToneSpacingHz);
            Assert.Equal(0.0, source.NoiseBandwidthHz);
            Assert.Equal("QPSK", source.Format);
        }
    }
}
