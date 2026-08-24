using System;
using System.Globalization;
using System.Linq;
using OpenVSA.Demod.Chain;
using OpenVSA.Demod.Results;
using OpenVSA.Demod.Signal;
using OpenVSA.Synthesis;
using Xunit;
using Xunit.Abstractions;

namespace OpenVSA.Demod.Tests
{
    /// <summary>
    /// <c>REQ-DEM-072</c>: what was in force when the metrics were computed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The interesting assertion is that they cannot disagree.</strong> A provenance that is
    /// merely present can still be stale, and a stale one is worse than none: it says with authority
    /// that a number was measured under conditions it was not. So the tests below change a setting
    /// and assert that the metric AND the line that qualifies it both moved, from the same result.
    /// </para>
    /// </remarks>
    public class MetricProvenanceTests
    {
        private const double SymbolRateHz = 1e6;
        private const double SampleRateHz = 16e6;
        private const int Symbols = 4000;

        private readonly ITestOutputHelper _output;

        public MetricProvenanceTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void EveryThingTheRequirementNamesIsInTheProvenance()
        {
            // "the UI shows the normalisation reference in force, both filter selections with their
            // parameters, and the state of each compensation — equaliser on or off, IQ offset
            // removed or not, mirror spectrum on or off."
            DemodResult result = Demodulate(Settings());

            MeasurementProvenance provenance = result.Provenance;

            foreach (string line in provenance.Lines)
            {
                _output.WriteLine(line);
            }

            Assert.NotNull(provenance.Normalisation);

            // Both filters, with their parameters -- the alpha, not just the type.
            Assert.Contains("RootRaisedCosine", provenance.MeasurementFilter);
            Assert.Contains("0.35", provenance.MeasurementFilter);
            Assert.NotEqual(string.Empty, provenance.ReferenceFilter);
            Assert.Equal(20, provenance.FilterSymbolSpan);

            // Each compensation, named whichever way it is set: "off" is as much a part of a
            // figure's meaning as "on".
            string all = string.Join(" ", provenance.Lines.ToArray());

            Assert.Contains("Equaliser off", all, StringComparison.Ordinal);
            Assert.Contains("measured but not removed", all, StringComparison.Ordinal);
            Assert.Contains("not mirrored", all, StringComparison.Ordinal);
        }

        [Theory]
        [InlineData("equaliser")]
        [InlineData("mirror")]
        [InlineData("normalisation")]
        [InlineData("filter")]
        public void ChangingACompensationMovesTheMetricAndItsProvenanceTogether(string what)
        {
            // "Changing any of them updates the displayed provenance in the same measurement cycle
            // as the metric it qualifies, so the two can never disagree; a test changes a
            // compensation and asserts the metric and its provenance update together."
            //
            // Both halves are read off ONE result, which is what makes the property structural: the
            // chain builds the provenance from the settings it just demodulated with, in the same
            // pass, so there is no cycle in which one could be ahead of the other.
            //
            // 🔴 Each case names the observable that setting can actually move, and they are not all
            // EVM. A first version asserted EVM for all four and failed on two of them, for two
            // different and correct reasons:
            //
            //   mirror     conjugating QPSK maps its four points onto themselves, so the EVM of a
            //              mirrored reading is IDENTICAL -- that is the whole finding of
            //              MirrorSpectrumTests, and it is why the option needs a test that reads
            //              the SYMBOLS. An EVM assertion here would have been asserting a
            //              coincidence of the geometry.
            //   equaliser  on a clean signal through a matched filter there is nothing to equalise
            //              and the coefficients do not move, so the result is bit-identical. To make
            //              the equaliser do anything it has to be given intersymbol interference,
            //              which is what the mismatched roll-off below is for.
            DemodSettings before = Settings();
            DemodSettings after = Settings();

            switch (what)
            {
                case "equaliser":
                    // Both mismatched, so there is ISI for the equaliser to correct; only the
                    // equaliser differs.
                    before.MeasurementFilterAlpha = 0.2;
                    after.MeasurementFilterAlpha = 0.2;
                    after.EqualiserEnabled = true;
                    break;

                case "mirror":
                    after.MirrorSpectrum = true;
                    break;

                case "normalisation":
                    before.Constellation = Constellation.Qam(16);
                    after.Constellation = Constellation.Qam(16);
                    after.EvmNormalisation = EvmNormalisation.MaximumMagnitude;
                    break;

                case "filter":
                    after.MeasurementFilterAlpha = 0.2;
                    break;
            }

            DemodResult first = Demodulate(before);
            DemodResult second = Demodulate(after);

            _output.WriteLine(
                what + " before: EVM " +
                first.EvmPercent.ToString("F4", CultureInfo.InvariantCulture) + " %rms -- " +
                first.Provenance);

            _output.WriteLine(
                what + " after:  EVM " +
                second.EvmPercent.ToString("F4", CultureInfo.InvariantCulture) + " %rms -- " +
                second.Provenance);

            // The provenance moved, in every case.
            Assert.NotEqual(first.Provenance.ToString(), second.Provenance.ToString());

            // And so did the measurement it qualifies -- read off whichever quantity that setting
            // is able to move.
            if (what == "mirror")
            {
                bool same = true;

                for (int symbol = 0; symbol < 64 && same; symbol++)
                {
                    same = first.Symbols[symbol] == second.Symbols[symbol];
                }

                _output.WriteLine(
                    "  the first 64 symbols are " + (same ? "the same" : "different") +
                    ", and the EVM is identical either way -- which is the point of reading the " +
                    "symbols here");

                Assert.False(same, "Mirroring changed neither the symbols nor anything else.");
            }
            else
            {
                Assert.NotEqual(first.EvmPercent, second.EvmPercent);
            }
        }

        [Fact]
        public void TheNormalisationInTheProvenanceIsTheOneTheMetricsUsed()
        {
            // The provenance is not a second reading of the settings -- it carries the very
            // EvmReference the summary divided by, so a divergence is not expressible.
            DemodSettings settings = Settings();

            settings.Constellation = Constellation.Qam(16);
            settings.EvmNormalisation = EvmNormalisation.MaximumMagnitude;

            DemodResult result = Demodulate(settings);

            _output.WriteLine(result.Provenance.Normalisation.Describe());

            Assert.Same(result.Summary.Reference, result.Provenance.Normalisation);
            Assert.Equal(
                EvmNormalisation.MaximumMagnitude, result.Provenance.Normalisation.Choice);
        }

        [Fact]
        public void TheWindowSearchesAreNamedOnlyWhenTheyRan()
        {
            // A line that always said "window positioned by nothing in particular" would be noise on
            // every measurement that did not use a search, which is most of them.
            DemodResult plain = Demodulate(Settings());

            Assert.DoesNotContain(
                plain.Provenance.Lines,
                line => line.StartsWith("Window positioned", StringComparison.Ordinal));

            DemodSettings searching = Settings();

            searching.BurstSearchEnabled = true;

            DemodResult burst = Demodulate(searching);

            string positioned = burst.Provenance.Lines
                .Single(line => line.StartsWith("Window positioned", StringComparison.Ordinal));

            _output.WriteLine(positioned);

            Assert.Contains("burst search", positioned, StringComparison.Ordinal);
        }

        private static DemodSettings Settings() =>
            new DemodSettings
            {
                Constellation = Constellation.Qpsk(),
                SymbolRateHz = SymbolRateHz,
                ResultLengthSymbols = 512,
                FilterSymbolSpan = 20,
                MeasurementFilter = PulseFilterType.RootRaisedCosine,
                MeasurementFilterAlpha = 0.35,
                ReferenceFilterAlpha = 0.35,
            };

        private static DemodResult Demodulate(DemodSettings settings)
        {
            var source = new ContinuousModulatedSource
            {
                Scheme = ModulationScheme.Qpsk(),
                SymbolRateHz = SymbolRateHz,
                SampleRateHz = SampleRateHz,
                RollOff = 0.35,
                PulseSpanSymbols = 20,
                Seed = 20260824,
                SignalToNoiseDb = 30.0,
            };

            var samples = new float[2 * Symbols * 16];

            source.Restart();
            source.Fill(samples);

            return new Demodulator().Run(samples, SampleRateHz, settings);
        }
    }
}
