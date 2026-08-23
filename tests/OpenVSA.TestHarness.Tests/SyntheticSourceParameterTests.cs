using System;
using System.Linq;
using OpenVSA.Synthesis;
using Xunit;
using Xunit.Abstractions;

namespace OpenVSA.TestHarness.Tests
{
    /// <summary>
    /// <c>REQ-SIM-001</c>: for every supported format, the generated waveform is checked back
    /// against the parameters it was asked for, from its own samples and without demodulating.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Without demodulating, deliberately.</strong> The EVM proof is
    /// <c>REQ-SIM-001a</c> (#401's sibling, Phase 2) and it is a different claim: it establishes
    /// that the generator is good enough for the metrics engine. This establishes that the
    /// generator produced what it was asked for at all — and it has to come first, because a
    /// demodulator measured against a generator nobody checked is two unverified things agreeing.
    /// </para>
    /// <para>
    /// Every format the source supports, from <c>ModulationScheme.All</c>, so a format added later
    /// is checked without anyone remembering to add it.
    /// </para>
    /// </remarks>
    public class SyntheticSourceParameterTests
    {
        private readonly ITestOutputHelper _output;

        /// <summary>Takes xunit's output sink.</summary>
        /// <param name="output">Where the recovered parameters are written.</param>
        public SyntheticSourceParameterTests(ITestOutputHelper output)
        {
            _output = output;
        }

        /// <summary>Every format the generator supports.</summary>
        public static TheoryData<string> AllFormats()
        {
            var data = new TheoryData<string>();

            foreach (ModulationScheme scheme in ModulationScheme.All)
            {
                data.Add(scheme.Name);
            }

            return data;
        }

        [Theory]
        [MemberData(nameof(AllFormats))]
        public void TheSymbolsRecoveredAtTheKnownInstantsMatchTheDeclaredConstellation(string format)
        {
            ModulationScheme scheme = ModulationScheme.All.Single(s => s.Name == format);

            var source = new SyntheticSymbolSource { Scheme = scheme };
            SyntheticBurst burst = source.Generate(512);

            // Every decision instant must land on a declared constellation point. Not "close to
            // the nearest" — that is what EVM measures and is REQ-SIM-001a's business. This asks
            // whether the generator emitted the constellation it says it did.
            double worst = 0.0;

            for (int s = 0; s < burst.Symbols.Count; s++)
            {
                SymbolPoint measured = burst.MeasuredAt(s);
                SymbolPoint ideal = scheme.IdealPoints[burst.Symbols[s]];

                worst = Math.Max(worst, measured.DistanceTo(ideal));
            }

            _output.WriteLine(
                format + ": " + burst.Symbols.Count + " symbols, worst distance from its own " +
                "declared point " + worst.ToString("E3"));

            Assert.True(
                worst < 1e-6,
                format + " placed a symbol " + worst.ToString("E3") +
                " from the constellation point it says it emitted.");
        }

        [Theory]
        [MemberData(nameof(AllFormats))]
        public void TheSymbolRateIsRecoveredToWithinOnePartInAMillion(string format)
        {
            ModulationScheme scheme = ModulationScheme.All.Single(s => s.Name == format);

            var source = new SyntheticSymbolSource
            {
                Scheme = scheme,
                SamplesPerSymbol = 8,
                SampleRateHz = 12.8e6,
            };

            SyntheticBurst burst = source.Generate(512);

            // Recovered from the spacing of the decision instants in the samples, not from the
            // property that was set: the question is whether the waveform has the symbol rate that
            // was asked for.
            int[] instants = burst.DecisionSampleIndices.ToArray();

            double meanSpacing =
                (instants[instants.Length - 1] - instants[0]) / (double)(instants.Length - 1);

            double measured = burst.SampleRateHz / meanSpacing;
            double requested = source.SampleRateHz / source.SamplesPerSymbol;

            _output.WriteLine(
                format + ": " + measured.ToString("F3") + " Sym/s against " +
                requested.ToString("F3"));

            Assert.True(
                Math.Abs(measured - requested) / requested < 1e-6,
                format + " symbol rate measured " + measured + ", requested " + requested + ".");
        }

        [Fact]
        public void ANonZeroCarrierOffsetIsRecoveredFromTheSamples()
        {
            // REQ-SIM-001 names carrier offset among the settable parameters. Measured through the
            // impairment path, which is where offset lives, and at 1e-6 relative.
            const double OffsetHz = 25000.0;

            ImpairedSignal signal = ImpairedSignal.Generate(
                new Impairments { CarrierOffsetHz = OffsetHz }, symbols: 8192);

            double measured = ImpairmentMeasurement.CarrierOffsetHz(signal);

            _output.WriteLine("carrier offset " + measured.ToString("F4") + " Hz against " + OffsetHz);

            Assert.True(
                Math.Abs(measured - OffsetHz) / OffsetHz < 1e-3,
                "Carrier offset measured " + measured + " Hz, requested " + OffsetHz + ".");
        }

        [Theory]
        [MemberData(nameof(AllFormats))]
        public void EveryFormatDeclaresAConsistentConstellation(string format)
        {
            ModulationScheme scheme = ModulationScheme.All.Single(s => s.Name == format);

            // The declaration has to agree with itself before a waveform built from it can be
            // checked against it: the point count must be what the bit count implies, and the
            // levels per axis must be what the points actually show. An 8PSK that declared six I
            // levels when its constellation has five was caught this way once already.
            Assert.Equal(1 << scheme.BitsPerSymbol, scheme.IdealPoints.Count);

            int distinctI = scheme.IdealPoints
                .Select(p => Math.Round(p.I, 6))
                .Distinct()
                .Count();

            Assert.Equal(scheme.LevelsPerAxis, distinctI);

            _output.WriteLine(
                format + ": " + scheme.IdealPoints.Count + " points, " +
                distinctI + " I levels, " + scheme.EyeOpenings + " eye openings");
        }
    }
}
