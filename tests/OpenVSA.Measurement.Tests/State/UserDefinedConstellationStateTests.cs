using System;
using System.Collections.Generic;
using OpenVSA.Demod.Chain;
using OpenVSA.Demod.Results;
using OpenVSA.Demod.Signal;
using OpenVSA.Measurement.State;
using Xunit;
using Xunit.Abstractions;

namespace OpenVSA.Measurement.Tests.State
{
    /// <summary>
    /// <c>REQ-DEM-011</c>'s side of the state: a constellation a user defined, saved and read back.
    /// </summary>
    /// <remarks>
    /// A user-defined constellation nobody can save is not one a user has defined — so the
    /// definition travels in the setup, and this is where it is checked that what comes out the far
    /// side is the geometry and the labelling that went in. The constellation itself, and what it
    /// demodulates, belong to <c>OpenVSA.Demod.Tests</c>; what is here is the crossing.
    /// </remarks>
    public class UserDefinedConstellationStateTests
    {
        private const double SymbolRateHz = 1e6;

        private readonly ITestOutputHelper _output;

        public UserDefinedConstellationStateTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void APointListCarriesItsOwnSymbolValues()
        {
            // "define a constellation by explicit point list (I, Q, symbol value)". The value is
            // carried with the coordinates, so the order a caller happens to write them in is not
            // the order they mean -- and a state file can be reordered, diffed or merged without
            // changing what it measures.
            var state = new DemodState
            {
                Format = "Mine",
                SymbolRateHz = SymbolRateHz,
                CustomPoints = new List<ConstellationPointState>
                {
                    new ConstellationPointState { I = -1.0, Q = 0.0, Symbol = 3 },
                    new ConstellationPointState { I = 0.0, Q = 1.0, Symbol = 1 },
                    new ConstellationPointState { I = 1.0, Q = 0.0, Symbol = 0 },
                    new ConstellationPointState { I = 0.0, Q = -1.0, Symbol = 2 },
                },
            };

            Constellation mine = state.ToSettings().Constellation;

            Assert.Equal("Mine", mine.Name);
            Assert.Equal(4, mine.Count);

            // Symbol 0 is the point that said it was symbol 0, wherever it sat in the list.
            Assert.True(mine.Points[0].I > 0.9);
            Assert.True(mine.Points[1].Q > 0.9);
            Assert.True(mine.Points[2].Q < -0.9);
            Assert.True(mine.Points[3].I < -0.9);
        }

        [Fact]
        public void APointListWithASymbolValueMissingIsRefused()
        {
            var state = new DemodState
            {
                Format = "Mine",
                SymbolRateHz = SymbolRateHz,
                CustomPoints = new List<ConstellationPointState>
                {
                    new ConstellationPointState { I = 1.0, Q = 0.0, Symbol = 0 },
                    new ConstellationPointState { I = 0.0, Q = 1.0, Symbol = 1 },
                    new ConstellationPointState { I = -1.0, Q = 0.0, Symbol = 1 },
                    new ConstellationPointState { I = 0.0, Q = -1.0, Symbol = 3 },
                },
            };

            ArgumentException refused = Assert.Throws<ArgumentException>(() => state.ToSettings());

            _output.WriteLine(refused.Message);
        }

        [Fact]
        public void ADefinitionThatIsBothRingsAndPointsIsRefused()
        {
            // Whichever was preferred would decide what was measured, so neither is.
            var state = new DemodState
            {
                Format = "Mine",
                SymbolRateHz = SymbolRateHz,
                CustomRings = new List<ApskRingState>
                {
                    new ApskRingState { Radius = 1.0, Points = 4 },
                },
                CustomPoints = new List<ConstellationPointState>
                {
                    new ConstellationPointState { I = 1.0, Q = 0.0, Symbol = 0 },
                    new ConstellationPointState { I = -1.0, Q = 0.0, Symbol = 1 },
                },
            };

            ArgumentException refused = Assert.Throws<ArgumentException>(() => state.ToSettings());

            Assert.Contains("either rings or points", refused.Message, StringComparison.Ordinal);
            _output.WriteLine(refused.Message);
        }

        [Fact]
        public void AStateCarriesTheDefinitionAndTheLabellingThroughToTheChain()
        {
            var state = Apsk32State();
            var table = new List<int>();

            for (int symbol = 0; symbol < 32; symbol++)
            {
                table.Add(31 - symbol);
            }

            state.BitMapping = BitMapping.Explicit;
            state.BitMappingTable = table;

            DemodSettings settings = state.ToSettings();

            Assert.Equal("32APSK", settings.Constellation.Name);
            Assert.Equal(32, settings.Constellation.Count);
            Assert.Equal(BitMapping.Explicit, settings.Mapping);
            Assert.Equal(31, settings.Constellation.CarriedBy(0));
            Assert.Equal(0, settings.Constellation.CarriedBy(31));

            // Degrees in the file, radians in the model: the inner ring's first point is at 45°.
            ConstellationPoint first = settings.Constellation.Points[0];

            Assert.Equal(Math.PI / 4.0, Math.Atan2(first.Q, first.I), 9);
        }

        [Fact]
        public void TheDefinitionSurvivesSaveAndRecall()
        {
            // The whole point of it being in the state. Rings, their phases, the labelling and its
            // table all come back, through the file rather than through the object.
            ApplicationState application = ApplicationState.Default("Demod");

            application.Measurements[0].Demod = Apsk32State();
            application.Measurements[0].Demod.BitMapping = BitMapping.Gray;

            string json = StateFile.Write(application);
            ApplicationState recalled = StateFile.Read(json);

            DemodState demod = recalled.Measurements[0].Demod;

            Assert.Equal(3, demod.CustomRings.Count);
            Assert.Equal(4, demod.CustomRings[0].Points);
            Assert.Equal(12, demod.CustomRings[1].Points);
            Assert.Equal(16, demod.CustomRings[2].Points);
            Assert.Equal(45.0, demod.CustomRings[0].PhaseDegrees, 9);
            Assert.Equal(2.64, demod.CustomRings[1].Radius, 9);
            Assert.Equal(BitMapping.Gray, demod.BitMapping);

            _output.WriteLine(
                "recalled " + demod.CustomRings.Count + " rings, mapping " + demod.BitMapping);
        }

        private static DemodState Apsk32State() =>
            new DemodState
            {
                Format = "32APSK",
                SymbolRateHz = SymbolRateHz,
                CustomRings = new List<ApskRingState>
                {
                    new ApskRingState { Radius = 1.0, Points = 4, PhaseDegrees = 45.0 },
                    new ApskRingState { Radius = 2.64, Points = 12, PhaseDegrees = 15.0 },
                    new ApskRingState { Radius = 4.64, Points = 16, PhaseDegrees = 11.25 },
                },
            };
    }
}
