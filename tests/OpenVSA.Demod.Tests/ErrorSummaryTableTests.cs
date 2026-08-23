using System;
using System.Collections.Generic;
using System.Linq;
using OpenVSA.Demod.Chain;
using OpenVSA.Demod.Results;
using OpenVSA.Demod.Signal;
using OpenVSA.Demod.Tests.Signals;
using Xunit;
using Xunit.Abstractions;

namespace OpenVSA.Demod.Tests
{
    /// <summary>
    /// <c>REQ-DEM-071</c>: the error summary table shows exactly the metrics the format applies to,
    /// each with its unit, following <c>REQ-UI-053</c>'s layout, and reading <c>NAN</c> where a
    /// metric applies and has not been measured.
    /// </summary>
    public class ErrorSummaryTableTests
    {
        private readonly ITestOutputHelper _output;

        public ErrorSummaryTableTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void AQpskResultShowsExactlyTheRowsQpskApplies()
        {
            // The enumeration the criterion asks for, for the one format this build demodulates.
            // It is short because REQ-DEM-010's catalogue is not built yet, and it is exact: a row
            // appearing that PSK does not apply, or one missing that it does, fails here.
            DemodResult result = Demodulate();

            string[] shown = result.Summary.Metrics.Select(metric => metric.Label).ToArray();

            Assert.Equal(
                new[]
                {
                    "EVM",
                    "Mag Err",
                    "Phase Err",
                    "Freq Err",
                    "Carr Ofst",
                    "Time Offset",
                    "SymClk Err",
                    "IQ Offset",
                    "IQ Gain Imbalance",
                    "IQ Quad. Error",
                    "IQ Timing Skew",
                    "SNR (MER)",
                    "RSSI",
                },
                shown);

            foreach (string row in result.Summary.Render())
            {
                _output.WriteLine(row);
            }
        }

        [Fact]
        public void EveryRowCarriesAUnit()
        {
            foreach (ErrorMetric metric in Demodulate().Summary.Metrics)
            {
                Assert.False(
                    string.IsNullOrEmpty(metric.Unit),
                    metric.Label + " is shown without a unit.");

                Assert.Equal(MetricApplicability.UnitOf(metric.Label), metric.Unit);
            }
        }

        [Fact]
        public void AMetricThatAppliesButIsNotMeasuredReadsNan()
        {
            // The criterion's own words: "A metric that is applicable but not yet computed shows
            // NAN per REQ-UI-032 rather than a stale value from the previous format."
            ErrorSummary summary = Demodulate().Summary;

            ErrorMetric frequency = summary.Metrics.Single(
                metric => metric.Label == "Freq Err");

            Assert.True(double.IsNaN(frequency.Rms));

            string rendered = summary.Render().Single(
                row => row.StartsWith("Freq Err", StringComparison.Ordinal));

            _output.WriteLine(rendered);

            Assert.Contains("NAN", rendered);
            Assert.DoesNotContain("NaN", rendered);
        }

        [Fact]
        public void AMeasuredMetricCarriesItsNumberAndItsPeak()
        {
            ErrorSummary summary = Demodulate().Summary;

            ErrorMetric evm = summary.Metrics.Single(metric => metric.Label == "EVM");

            Assert.False(double.IsNaN(evm.Rms));
            Assert.True(evm.HasPeak);
            Assert.True(evm.Rms < 1.0);
        }

        [Fact]
        public void TheRowsFollowTheFormatWithNoManualStep()
        {
            // "Rows appear and disappear on a format change with no manual step." The summary is
            // built from the format every time it is computed, so the only way to keep a stale row
            // would be to cache one.
            ErrorSummary computed = new ErrorSummary()
                .Add(new ErrorMetric("EVM", "%rms", 1.0, 2.0, 3));

            IReadOnlyList<string> psk = Labels(computed, ModulationFamily.Psk, false);
            IReadOnlyList<string> fsk = Labels(computed, ModulationFamily.Fsk, false);

            Assert.Contains("Mag Err", psk);
            Assert.DoesNotContain("Mag Err", fsk);

            Assert.Contains("SNR (MER)", psk);
            Assert.DoesNotContain("SNR (MER)", fsk);

            // And the metric that survives the change keeps its value rather than being reset.
            ErrorSummary table = computed.AsTableFor(ModulationFamily.Fsk, false);

            Assert.Equal(1.0, table.Metrics.Single(metric => metric.Label == "EVM").Rms);
        }

        [Theory]
        [InlineData(ModulationFamily.Fsk, "Mag Err")]
        [InlineData(ModulationFamily.Fsk, "Phase Err")]
        [InlineData(ModulationFamily.Fsk, "IQ Offset")]
        [InlineData(ModulationFamily.Fsk, "IQ Gain Imbalance")]
        [InlineData(ModulationFamily.Msk, "IQ Quad. Error")]
        [InlineData(ModulationFamily.Psk, "Amp Droop")]
        [InlineData(ModulationFamily.Qam, "Pilot Lvl")]
        [InlineData(ModulationFamily.Fsk, "SNR (MER)")]
        public void AMetricThatDoesNotApplyIsAbsent(ModulationFamily family, string label)
        {
            Assert.False(MetricApplicability.Applies(label, family, false));
            Assert.DoesNotContain(label, MetricApplicability.LabelsFor(family, false));
        }

        [Theory]
        [InlineData(ModulationFamily.Msk, "Amp Droop")]
        [InlineData(ModulationFamily.Vsb, "Pilot Lvl")]
        [InlineData(ModulationFamily.Qam, "SNR (MER)")]
        [InlineData(ModulationFamily.Apsk, "SNR (MER)")]
        [InlineData(ModulationFamily.Fsk, "EVM")]
        [InlineData(ModulationFamily.Fsk, "Freq Err")]
        [InlineData(ModulationFamily.Custom, "RSSI")]
        public void AMetricThatAppliesIsPresent(ModulationFamily family, string label)
        {
            Assert.True(MetricApplicability.Applies(label, family, false));
            Assert.Contains(label, MetricApplicability.LabelsFor(family, false));
        }

        [Fact]
        public void OffsetEvmBelongsToOffsetFormatsAndNoOthers()
        {
            // REQ-DEM-062 gives the variant to offset formats. It is the one rule that turns on the
            // format rather than on its family: OQPSK's constellation is QPSK's.
            Assert.False(MetricApplicability.Applies("Offset EVM", ModulationFamily.Psk, false));
            Assert.True(MetricApplicability.Applies("Offset EVM", ModulationFamily.Psk, true));

            Assert.DoesNotContain("Offset EVM", MetricApplicability.LabelsFor(ModulationFamily.Psk, false));
            Assert.Contains("Offset EVM", MetricApplicability.LabelsFor(ModulationFamily.Psk, true));
        }

        [Fact]
        public void EveryLabelInTheCatalogueHasAUnitAndEveryUnitALabel()
        {
            foreach (string label in MetricApplicability.AllLabels)
            {
                Assert.False(string.IsNullOrEmpty(MetricApplicability.UnitOf(label)));
            }

            Assert.Throws<ArgumentException>(() => MetricApplicability.UnitOf("Not A Metric"));
            Assert.Throws<ArgumentException>(
                () => MetricApplicability.Applies("Not A Metric", ModulationFamily.Psk, false));
        }

        [Fact]
        public void TheCatalogueUsesTheRequirementsOwnLabels()
        {
            // REQ-UI-053's list, asserted as literals there and honoured here. Every label the
            // table can show is one of them, or is SNR (MER), which REQ-DEM-069 spells out and that
            // list omits.
            foreach (string label in MetricApplicability.AllLabels)
            {
                Assert.True(
                    ErrorSummary.Labels.Contains(label) ||
                        label == MetricApplicability.SignalToNoise,
                    label + " is not one of REQ-UI-053's labels.");
            }
        }

        private static IReadOnlyList<string> Labels(
            ErrorSummary computed, ModulationFamily family, bool isOffset) =>
            computed.AsTableFor(family, isOffset)
                .Metrics
                .Select(metric => metric.Label)
                .ToList();

        private static DemodResult Demodulate()
        {
            var source = new QpskSource(3)
            {
                SymbolRateHz = 1e6,
                SampleRateHz = 5.3e6,
                CarrierOffsetHz = 5000.0,
                Amplitude = 0.5,
            };

            var settings = new DemodSettings
            {
                SymbolRateHz = source.SymbolRateHz,
                ResultLengthSymbols = 256,
            };

            return new Demodulator().Run(source.Generate(500), source.SampleRateHz, settings);
        }
    }
}
