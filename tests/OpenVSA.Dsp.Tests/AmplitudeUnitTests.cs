using System;
using System.Linq;
using OpenVSA.Dsp.Spectrum;
using Xunit;
using Xunit.Abstractions;

namespace OpenVSA.Dsp.Tests
{
    /// <summary>
    /// <c>REQ-AMP-002</c>: the amplitude units, and the impedance every power conversion uses.
    /// </summary>
    public class AmplitudeUnitTests
    {
        private readonly ITestOutputHelper _output;

        public AmplitudeUnitTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void EveryUnitTheRequirementListsIsAvailable()
        {
            string[] required = { "dBm", "dBmV", "dBµV", "dBV", "V pk", "V rms", "W" };

            string[] available = AmplitudeUnits.All
                .Select(AmplitudeUnits.SymbolOf)
                .OrderBy(s => s, StringComparer.Ordinal)
                .ToArray();

            Assert.Equal(
                required.OrderBy(s => s, StringComparer.Ordinal).ToArray(),
                available);
        }

        [Fact]
        public void ChangingFromFiftyToSeventyFiveOhmsMovesADbmReadingByTheAnalyticFigure()
        {
            // REQ-AMP-002's criterion. 10*log10(50/75) = -1.7609 dB, and the sign matters as much
            // as the magnitude: the same voltage across a larger resistance dissipates *less*
            // power, so the reading falls. A test on the magnitude alone would pass on an
            // implementation that had it backwards.
            const double voltsPeak = 1.0;

            double at50 = AmplitudeUnits.FromVoltsPeak(voltsPeak, AmplitudeUnit.Dbm, 50.0);
            double at75 = AmplitudeUnits.FromVoltsPeak(voltsPeak, AmplitudeUnit.Dbm, 75.0);

            _output.WriteLine(
                voltsPeak + " V peak reads " + at50.ToString("F4") + " dBm into 50 ohms and " +
                at75.ToString("F4") + " dBm into 75");

            Assert.Equal(-1.7609, at75 - at50, 4);
            Assert.Equal(at75 - at50, AmplitudeUnits.ImpedanceChangeDb(50.0, 75.0), 9);

            // 1 V peak into 50 ohms is 10 mW, which is +10 dBm exactly.
            Assert.Equal(10.0, at50, 9);
        }

        [Fact]
        public void AVoltageReadingDoesNotMoveWithTheImpedance()
        {
            // The other half of the same point: the dBV family and the volt readings are voltages,
            // so the impedance has no bearing on them. A conversion that routed them through power
            // would make them move, which is the mistake the separation exists to prevent.
            foreach (AmplitudeUnit unit in AmplitudeUnits.All.Where(u => !AmplitudeUnits.IsPower(u)))
            {
                double at50 = AmplitudeUnits.FromVoltsPeak(1.0, unit, 50.0);
                double at75 = AmplitudeUnits.FromVoltsPeak(1.0, unit, 75.0);

                Assert.Equal(at50, at75, 12);
            }
        }

        [Theory]
        [InlineData(AmplitudeUnit.Dbm)]
        [InlineData(AmplitudeUnit.Watts)]
        [InlineData(AmplitudeUnit.DbMillivolts)]
        [InlineData(AmplitudeUnit.DbMicrovolts)]
        [InlineData(AmplitudeUnit.DbVolts)]
        [InlineData(AmplitudeUnit.VoltsPeak)]
        [InlineData(AmplitudeUnit.VoltsRms)]
        public void EveryUnitRoundTripsThroughVoltsPeak(AmplitudeUnit unit)
        {
            const double voltsPeak = 0.3162277;

            double expressed = AmplitudeUnits.FromVoltsPeak(voltsPeak, unit, 50.0);
            double back = AmplitudeUnits.ToVoltsPeak(expressed, unit, 50.0);

            Assert.Equal(voltsPeak, back, 9);
        }

        [Fact]
        public void TheDecibelVoltFamilyIsReferredToRmsAsTheIndustryUsesIt()
        {
            // 0 dBV is 1 V RMS, not 1 V peak. Referring them to peak would put every reading
            // 3.01 dB out against every other instrument.
            double oneVoltRmsPeak = Math.Sqrt(2.0);

            Assert.Equal(0.0, AmplitudeUnits.FromVoltsPeak(oneVoltRmsPeak, AmplitudeUnit.DbVolts, 50.0), 9);
            Assert.Equal(60.0, AmplitudeUnits.FromVoltsPeak(oneVoltRmsPeak, AmplitudeUnit.DbMillivolts, 50.0), 9);
            Assert.Equal(120.0, AmplitudeUnits.FromVoltsPeak(oneVoltRmsPeak, AmplitudeUnit.DbMicrovolts, 50.0), 9);
        }

        [Fact]
        public void ADbmReadingConvertsToTheRightVoltageAndBack()
        {
            // The classic bench figure: 0 dBm into 50 ohms is 0.2236 V RMS, 0.3162 V peak.
            double voltsPeak = AmplitudeUnits.ToVoltsPeak(0.0, AmplitudeUnit.Dbm, 50.0);

            Assert.Equal(0.31623, voltsPeak, 5);
            Assert.Equal(
                0.22361,
                AmplitudeUnits.Convert(0.0, AmplitudeUnit.Dbm, AmplitudeUnit.VoltsRms, 50.0),
                5);
            Assert.Equal(
                1e-3,
                AmplitudeUnits.Convert(0.0, AmplitudeUnit.Dbm, AmplitudeUnit.Watts, 50.0),
                12);
        }

        [Fact]
        public void ZeroAmplitudeReadsTheFloorRatherThanNegativeInfinity()
        {
            // A blank bin must plot at the bottom of the graticule rather than taking the axis
            // with it.
            foreach (AmplitudeUnit unit in new[]
                     {
                         AmplitudeUnit.Dbm, AmplitudeUnit.DbVolts,
                         AmplitudeUnit.DbMillivolts, AmplitudeUnit.DbMicrovolts,
                     })
            {
                Assert.Equal(AmplitudeScale.FloorDbm, AmplitudeUnits.FromVoltsPeak(0.0, unit, 50.0));
            }

            Assert.Equal(0.0, AmplitudeUnits.FromVoltsPeak(0.0, AmplitudeUnit.Watts, 50.0));
        }

        [Fact]
        public void ConvertingToTheSameUnitChangesNothing()
        {
            Assert.Equal(
                -13.7,
                AmplitudeUnits.Convert(-13.7, AmplitudeUnit.Dbm, AmplitudeUnit.Dbm, 75.0),
                12);
        }

        [Fact]
        public void TheArgumentsAreChecked()
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => AmplitudeUnits.FromVoltsPeak(1.0, AmplitudeUnit.Dbm, 0.0));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => AmplitudeUnits.ToVoltsPeak(1.0, AmplitudeUnit.Dbm, -50.0));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => AmplitudeUnits.Convert(1.0, AmplitudeUnit.Dbm, AmplitudeUnit.Watts, 0.0));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => AmplitudeUnits.SymbolOf((AmplitudeUnit)99));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => AmplitudeUnits.IsPower((AmplitudeUnit)99));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => AmplitudeUnits.ImpedanceChangeDb(0.0, 50.0));

            Assert.Equal(new[] { 50.0, 75.0 }, AmplitudeUnits.CommonImpedancesOhms.ToArray());
        }
    }
}
