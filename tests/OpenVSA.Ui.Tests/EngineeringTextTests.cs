using OpenVSA.Ui;
using Xunit;

namespace OpenVSA.Ui.Tests
{
    /// <summary>
    /// Entry and display of the quantities a measurement is set up with.
    /// </summary>
    public class EngineeringTextTests
    {
        [Theory]
        [InlineData("1.5 GHz", 1.5e9)]
        [InlineData("1.5GHz", 1.5e9)]
        [InlineData("1.5G", 1.5e9)]
        [InlineData("1500 MHz", 1.5e9)]
        [InlineData("1500M", 1.5e9)]
        [InlineData("1500000 kHz", 1.5e9)]
        [InlineData("1.5e9", 1.5e9)]
        [InlineData("1500000000", 1.5e9)]
        [InlineData("0", 0.0)]
        [InlineData("  10 MHz  ", 10e6)]
        public void FrequenciesAreAcceptedInEveryFormAUserWouldType(string text, double expected)
        {
            double hertz;
            Assert.True(EngineeringText.TryParseFrequency(text, out hertz));
            Assert.Equal(expected, hertz, 3);
        }

        [Fact]
        public void TheMultiplierIsCaseSensitive_BecauseMilliAndMegaBothMatter()
        {
            // REQ-DSP-021 requires resolution bandwidths below 1 Hz, so mHz has to mean millihertz.
            // Folding case would make "10m" mean 10 MHz to one user and 10 mHz to another, with
            // nothing on screen to say which was understood.
            double mega;
            double milli;

            Assert.True(EngineeringText.TryParseFrequency("10 MHz", out mega));
            Assert.True(EngineeringText.TryParseFrequency("10 mHz", out milli));

            Assert.Equal(10e6, mega, 6);
            Assert.Equal(0.01, milli, 9);
        }

        [Theory]
        [InlineData("")]
        [InlineData(null)]
        [InlineData("wide")]
        [InlineData("Hz")]
        [InlineData("1.2.3")]
        [InlineData("NaN")]
        public void TextThatIsNotAFrequencyIsRejected(string text)
        {
            double hertz;
            Assert.False(EngineeringText.TryParseFrequency(text, out hertz));
        }

        [Theory]
        [InlineData("-10", -10.0)]
        [InlineData("-10 dBm", -10.0)]
        [InlineData("+20dBm", 20.0)]
        [InlineData("0 dB", 0.0)]
        [InlineData("  -3.5  ", -3.5)]
        public void LevelsAreAcceptedWithOrWithoutTheirUnit(string text, double expected)
        {
            double dbm;
            Assert.True(EngineeringText.TryParseDecibels(text, out dbm));
            Assert.Equal(expected, dbm, 6);
        }

        [Theory]
        [InlineData("loud")]
        [InlineData("dBm")]
        [InlineData("")]
        public void TextThatIsNotALevelIsRejected(string text)
        {
            double dbm;
            Assert.False(EngineeringText.TryParseDecibels(text, out dbm));
        }

        [Theory]
        [InlineData(1.5e9, "1.500 GHz")]
        [InlineData(10e6, "10.000 MHz")]
        [InlineData(47742.5, "47.743 kHz")]
        [InlineData(500.0, "500.000 Hz")]
        [InlineData(0.01, "10.000 mHz")]
        public void FrequenciesAreDisplayedInEngineeringNotation(double hertz, string expected)
        {
            Assert.Equal(expected, EngineeringText.Frequency(hertz));
        }

        [Fact]
        public void FormattingAndParsingRoundTrip()
        {
            foreach (double hertz in new[] { 1.5e9, 10e6, 47742.5, 500.0, 0.01 })
            {
                double parsed;
                Assert.True(EngineeringText.TryParseFrequency(EngineeringText.Frequency(hertz, 9), out parsed));
                Assert.Equal(hertz, parsed, 6);
            }
        }

        [Theory]
        [InlineData(80e-6, "80 us")]
        [InlineData(50e-3, "50 ms")]
        [InlineData(1.5, "1.5 s")]
        [InlineData(20e-9, "20 ns")]
        public void TimesAreDisplayedInEngineeringNotation(double seconds, string expected)
        {
            Assert.Equal(expected, EngineeringText.Time(seconds));
        }

        [Fact]
        public void InvalidAndOverflowingReadoutsRenderTheLiteralsTheSpecificationNames()
        {
            // REQ-UI-032, asserted as exact strings: the framework would render these as "NaN" and
            // the infinity sign, and under some cultures as "NeuN", none of which is what a user of
            // the reference product sees.
            Assert.Equal("NAN", EngineeringText.Readout(double.NaN));
            Assert.Equal("INF", EngineeringText.Readout(double.PositiveInfinity));

            // Negative overflow keeps its sign, which the specification does not say and a level
            // readout cannot do without: an empty bin underflows to minus infinity, and rendering
            // that as "INF" would lose the whole of the answer.
            Assert.Equal("-INF", EngineeringText.Readout(double.NegativeInfinity));
        }

        [Fact]
        public void AFiniteReadoutIsJustTheNumber()
        {
            Assert.Equal("-42.500", EngineeringText.Readout(-42.5));
            Assert.Equal("-42.5", EngineeringText.Readout(-42.5, "0.0"));
        }
    }
}
