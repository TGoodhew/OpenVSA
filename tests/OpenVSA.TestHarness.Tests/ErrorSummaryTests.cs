using System;
using System.Collections.Generic;
using System.Linq;
using OpenVSA.Demod.Results;
using OpenVSA.TestHarness.Synthesis;
using Xunit;
using Xunit.Abstractions;

namespace OpenVSA.TestHarness.Tests
{
    /// <summary>
    /// <c>REQ-UI-053</c>: the error summary's layout, and <c>REQ-UI-052</c>'s symbol table.
    /// </summary>
    /// <remarks>
    /// Both are text, and both are checked as text. The requirement gives the actual on-screen
    /// output of a real analyser as the layout model, so what is asserted is the shape of the
    /// rendered rows rather than the values behind them.
    /// </remarks>
    public class ErrorSummaryTests
    {
        private readonly ITestOutputHelper _output;

        /// <summary>Takes xunit's output sink.</summary>
        /// <param name="output">Where the rendered rows are written.</param>
        public ErrorSummaryTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void TheRowLabelsAreExactlyTheTerseAbbreviations()
        {
            // "Row labels are exactly the listed abbreviations, asserted as literals — Carr Ofst,
            // not 'Carrier Offset'." The house style is short, truncated and
            // no-space-where-possible, and the natural instinct is to write it out in full.
            Assert.Equal(
                new[]
                {
                    "Amp Droop", "Carr Ofst", "EVM", "EVM Pk", "Freq Err", "Mag Err",
                    "Offset EVM", "Phase Err", "Pilot Lvl", "Time Offset", "IQ Offset",
                    "IQ Gain Imbalance", "IQ Quad. Error", "IQ Timing Skew", "SymClk Err", "RSSI",
                },
                ErrorSummary.Labels.ToArray());

            Assert.DoesNotContain("Carrier Offset", ErrorSummary.Labels);
            Assert.DoesNotContain("Symbol Clock Error", ErrorSummary.Labels);
            Assert.DoesNotContain("Frequency Error", ErrorSummary.Labels);
        }

        [Fact]
        public void TheEqualsSignIsAtAFixedColumnOnEveryRow()
        {
            // The requirement's first structural point, and the reason the block needs a
            // fixed-width slot. Its own model has `Phase Error=` running into the sign, so the
            // label field is exactly as wide as the longest label rather than a space wider.
            var summary = new ErrorSummary()
                .Add(new ErrorMetric("EVM", "%rms", 0.2487475, 0.7322379, 73))
                .Add(new ErrorMetric("Mag Err", "%rms", 0.1668398, -0.7294476, 73))
                .Add(new ErrorMetric("Phase Err", "deg", 0.2519865, 1.043872, 168))
                .Add(new ErrorMetric("Freq Err", "Hz", -384.55))
                .Add(new ErrorMetric("IQ Offset", "dB", -67.543));

            IReadOnlyList<string> rows = summary.Render();

            foreach (string row in rows)
            {
                _output.WriteLine(row);
            }

            Assert.Equal(5, rows.Count);

            foreach (string row in rows)
            {
                Assert.Equal(ErrorSummary.EqualsColumn, row.IndexOf('='));
            }
        }

        [Fact]
        public void TheOrderIsRmsThenPeakThenAtSymbol()
        {
            var summary = new ErrorSummary()
                .Add(new ErrorMetric("EVM", "%rms", 0.2487475, 0.7322379, 73));

            string row = summary.Render()[0];

            _output.WriteLine(row);

            int rms = row.IndexOf("m%rms", StringComparison.Ordinal);
            int peak = row.IndexOf("m% pk", StringComparison.Ordinal);
            int at = row.IndexOf("at symbol 73", StringComparison.Ordinal);

            Assert.True(rms > 0, "No RMS value with an engineering prefix.");
            Assert.True(peak > rms, "The peak does not follow the RMS value.");
            Assert.True(at > peak, "'at symbol N' does not follow the peak.");
        }

        [Fact]
        public void ValuesCarryEngineeringPrefixesRatherThanExponents()
        {
            // "engineering prefixes on units (m%rms, mdeg) rather than exponent notation".
            Assert.Equal("248.7475 m%rms", ErrorSummary.Engineering(0.2487475, "%rms"));
            Assert.Equal("251.9865 mdeg", ErrorSummary.Engineering(0.2519865, "deg"));
            Assert.Equal("1.043872 deg", ErrorSummary.Engineering(1.043872, "deg"));

            // A level is already logarithmic and takes no prefix — every instrument shows
            // -67.543 dB, and a prefixed decibel is a unit nobody uses.
            Assert.Equal("-67.543 dB", ErrorSummary.Engineering(-67.543, "dB"));

            foreach (double value in new[] { 0.2487475, 1.043872, -384.55, 0.000012 })
            {
                Assert.DoesNotContain("E", ErrorSummary.Engineering(value, "%rms"));
            }
        }

        [Fact]
        public void AScalarMetricOmitsThePeakColumnsRatherThanPaddingThem()
        {
            // "Scalar-only metrics render one value and omit the peak columns rather than padding
            // them with zeros."
            var summary = new ErrorSummary()
                .Add(new ErrorMetric("Freq Err", "Hz", -384.55));

            string row = summary.Render()[0];

            _output.WriteLine(row);

            Assert.DoesNotContain("pk", row);
            Assert.DoesNotContain("at symbol", row);
            Assert.Contains("-384.55 Hz", row);
        }

        [Fact]
        public void TheSummaryOfAKnownImpairmentReadsAsTheRequirementsModelDoes()
        {
            // Rendered against a signal of known impairments, which is the criterion's own framing.
            var source = new SyntheticSymbolSource
            {
                Scheme = ModulationScheme.Qam16(),
                SignalToNoiseDb = 26.0,
            };

            SymbolTrace trace = source.Generate(400).ToSymbolTrace();
            ErrorSummary summary = ErrorSummary.For(trace);

            IReadOnlyList<string> rows = summary.Render();

            foreach (string row in rows)
            {
                _output.WriteLine(row);
            }

            Assert.Equal(4, rows.Count);

            // Each row has the shape the model shows: label, = at its column, a value with a unit.
            foreach (string row in rows)
            {
                Assert.Equal(ErrorSummary.EqualsColumn, row.IndexOf('='));
            }

            Assert.StartsWith("EVM", rows[0]);
            Assert.StartsWith("Mag Err", rows[1]);
            Assert.StartsWith("Phase Err", rows[2]);
            Assert.StartsWith("IQ Offset", rows[3]);

            // Every label the summary produces is one the requirement lists.
            foreach (ErrorMetric metric in summary.Metrics)
            {
                Assert.Contains(metric.Label, ErrorSummary.Labels);
            }
        }

        [Fact]
        public void TheEvmFiguresFollowTheImpairment()
        {
            // A summary whose numbers did not move with the signal would satisfy every layout
            // check above and be useless.
            var clean = ErrorSummary.For(
                new SyntheticSymbolSource { Scheme = ModulationScheme.Qpsk() }
                    .Generate(300).ToSymbolTrace());

            var noisy = ErrorSummary.For(
                new SyntheticSymbolSource
                {
                    Scheme = ModulationScheme.Qpsk(),
                    SignalToNoiseDb = 20.0,
                }.Generate(300).ToSymbolTrace());

            double cleanEvm = clean.Metrics.First(m => m.Label == "EVM").Rms;
            double noisyEvm = noisy.Metrics.First(m => m.Label == "EVM").Rms;

            _output.WriteLine(
                "clean EVM " + cleanEvm.ToString("0.0000") + " %, at 20 dB SNR " +
                noisyEvm.ToString("0.0000") + " %");

            Assert.True(cleanEvm < 0.5, "A clean signal reports " + cleanEvm + " % EVM.");
            Assert.InRange(noisyEvm, 6.0, 16.0);

            // And the peak is at least the RMS, on the symbol it says.
            ErrorMetric evm = noisy.Metrics.First(m => m.Label == "EVM");

            Assert.True(evm.HasPeak);
            Assert.True(Math.Abs(evm.Peak) >= evm.Rms);
            Assert.InRange(evm.PeakSymbol, 0, 299);
        }

        [Fact]
        public void AnEmptyResultSummarisesToNothingRatherThanToZeroes()
        {
            var trace = new SymbolTrace(
                "QPSK", 2, 2,
                new List<int>(), new List<ConstellationPoint>(), new List<ConstellationPoint>(),
                new List<int>(), new float[64], 8, 1e6);

            Assert.Empty(ErrorSummary.For(trace).Metrics);
            Assert.Empty(ErrorSummary.For(trace).Render());
        }

        [Fact]
        public void MissingArgumentsAreRefused()
        {
            Assert.Throws<ArgumentNullException>(() => ErrorSummary.For(null));
            Assert.Throws<ArgumentNullException>(() => new ErrorSummary().Add(null));
            Assert.Throws<ArgumentException>(() => new ErrorMetric("  ", "dB", 1.0));
        }
    }
}
