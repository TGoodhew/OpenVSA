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
    /// <c>REQ-DEM-062</c>: Offset EVM, and the conventional figure it is a variant of.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The pair is the measurement.</strong> The criterion asks that Offset EVM "returns
    /// near-zero where conventional EVM computed at a common instant does not — the difference
    /// between the two is what shows the half-symbol stagger is honoured". A near-zero Offset EVM on
    /// its own shows nothing: a chain that silently sampled both parts at whichever instant suited
    /// it would produce one too. What cannot be faked is the same signal reading tens of per cent
    /// when the stagger is ignored and hundredths of one when it is not.
    /// </para>
    /// </remarks>
    public class OffsetEvmTests
    {
        private const double SymbolRateHz = 1e6;
        private const double SampleRateHz = 16e6;
        private const int Symbols = 4000;

        private readonly ITestOutputHelper _output;

        public OffsetEvmTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void OnACleanOqpskSignalOffsetEvmIsNearZeroAndTheCommonInstantFigureIsNot()
        {
            DemodResult result = Demodulate(Constellation.Oqpsk());

            ErrorMetric offset = Row(result, "Offset EVM");
            ErrorMetric common = Row(result, "EVM");

            _output.WriteLine(
                "OQPSK: Offset EVM " +
                offset.Rms.ToString("F6", CultureInfo.InvariantCulture) +
                " %rms; the same symbols read at one instant " +
                common.Rms.ToString("F4", CultureInfo.InvariantCulture) + " %rms");

            foreach (string row in result.Summary.Render())
            {
                _output.WriteLine("  " + row);
            }

            Assert.True(
                offset.Rms < 0.1,
                "Offset EVM on a clean OQPSK signal read " + offset.Rms + " %rms.");

            // Tens of per cent, not a little worse. Half a symbol into a root-raised-cosine pulse is
            // most of the way to the next symbol's decision, so the Q part read at the wrong instant
            // is very nearly uncorrelated with the one that was sent.
            Assert.True(
                common.Rms > 20.0,
                "Reading OQPSK at a common instant gave " + common.Rms +
                " %rms. If that is small, the two figures are the same computation and the " +
                "stagger is not being demonstrated.");

            // And by orders of magnitude, which is the criterion's "near-zero ... where ... does
            // not" stated as a number.
            Assert.True(common.Rms > 100.0 * offset.Rms);
        }

        [Fact]
        public void TheHeadlineEvmIsTheOffsetOneAndNotTheCommonInstantOne()
        {
            // 🔴 The two rows disagree for an offset format, deliberately, and something has to say
            // which of them the rest of the analyser means by "the EVM". It is the Offset EVM: the
            // chain honours the stagger in its decisions, its regenerated reference and its lock
            // diagnosis, and a headline carrying the common-instant figure would call a perfectly
            // good OQPSK measurement a failure. #430 carries the question of whether the EVM ROW
            // should read the common-instant figure at all.
            DemodResult result = Demodulate(Constellation.Oqpsk());

            _output.WriteLine(
                "headline " + result.EvmPercent.ToString("F6", CultureInfo.InvariantCulture) +
                " %rms against an Offset EVM row of " +
                Row(result, "Offset EVM").Rms.ToString("F6", CultureInfo.InvariantCulture) +
                " %rms and an EVM row of " +
                Row(result, "EVM").Rms.ToString("F4", CultureInfo.InvariantCulture) + " %rms");

            Assert.Equal(Row(result, "Offset EVM").Rms, result.EvmPercent);

            // And the lock diagnosis therefore sees a signal that locked, which is the practical
            // consequence of that choice.
            Assert.True(result.Lock.Locked);
        }

        [Fact]
        public void OnANonOffsetFormatTheVariantIsAbsentRatherThanMeaningless()
        {
            // "On a non-offset format the variant is unavailable rather than computing a
            // meaningless value." Absent from the table, not present and zero, and not present and
            // NAN either -- a row that reads NAN is a metric that applies and has not been measured
            // (REQ-DEM-071), which is a different statement from one that does not apply.
            DemodResult result = Demodulate(Constellation.Qpsk());

            _output.WriteLine(
                "QPSK rows: " +
                string.Join(", ", result.Summary.Metrics.Select(m => m.Label).ToArray()));

            Assert.DoesNotContain(
                result.Summary.Metrics, metric => metric.Label == "Offset EVM");

            // The plain EVM row is still there and still means what it always meant.
            Assert.Equal(result.EvmPercent, Row(result, "EVM").Rms);
            Assert.True(result.EvmPercent < 0.1);
        }

        [Fact]
        public void OffsetEvmIsOnePointPerSymbolAndNotTwo()
        {
            // "an Offset EVM variant shall be computed using ONE point per symbol formed from a
            // complex point whose real and imaginary parts are taken from different time
            // locations". The alternative -- treating each half-symbol transition as its own point
            // -- would give twice as many, so the count is worth asserting rather than assuming.
            DemodResult result = Demodulate(Constellation.Oqpsk());

            _output.WriteLine(
                result.Trace.SymbolCount + " symbols, " + result.Trace.Measured.Count +
                " measured points, " + result.Symbols.Count + " decided");

            Assert.Equal(result.Trace.SymbolCount, result.Trace.Measured.Count);
            Assert.Equal(result.Trace.SymbolCount, result.Symbols.Count);
        }

        [Fact]
        public void ThePeakAndItsSymbolBelongToTheRowTheyAreOn()
        {
            // Each row carries its own peak and its own index. A first implementation of this copied
            // EVM's peak onto both rows, which reads plausibly and says that the worst common-instant
            // symbol and the worst staggered symbol are the same one -- which on OQPSK they are not.
            DemodResult result = Demodulate(Constellation.Oqpsk());

            ErrorMetric offset = Row(result, "Offset EVM");
            ErrorMetric common = Row(result, "EVM");

            _output.WriteLine(
                "Offset EVM peaks at " +
                offset.Peak.ToString("F4", CultureInfo.InvariantCulture) + " % on symbol " +
                offset.PeakSymbol + "; the common-instant reading peaks at " +
                common.Peak.ToString("F4", CultureInfo.InvariantCulture) + " % on symbol " +
                common.PeakSymbol);

            Assert.True(offset.HasPeak);
            Assert.True(common.HasPeak);
            Assert.True(Math.Abs(offset.Peak) >= offset.Rms);
            Assert.True(Math.Abs(common.Peak) >= common.Rms);
            Assert.True(Math.Abs(common.Peak) > Math.Abs(offset.Peak));
        }

        private static ErrorMetric Row(DemodResult result, string label) =>
            result.Summary.Metrics.Single(metric => metric.Label == label);

        private static DemodResult Demodulate(Constellation format)
        {
            var points = new System.Collections.Generic.List<SymbolPoint>(format.Count);

            foreach (ConstellationPoint point in format.Points)
            {
                points.Add(new SymbolPoint(point.I, point.Q));
            }

            var source = new ContinuousModulatedSource
            {
                Scheme = ModulationScheme.FromPoints(
                    format.Name, points, format.IsOffset, format.RotationPerSymbolRadians),
                SymbolRateHz = SymbolRateHz,
                SampleRateHz = SampleRateHz,
                RollOff = 0.35,
                PulseSpanSymbols = 20,
                Seed = 20260824,
            };

            var samples = new float[2 * Symbols * 16];

            source.Restart();
            source.Fill(samples);

            var settings = new DemodSettings
            {
                Constellation = format,
                SymbolRateHz = SymbolRateHz,
                ResultLengthSymbols = 512,
                FilterSymbolSpan = 20,
                MeasurementFilter = PulseFilterType.RootRaisedCosine,
                MeasurementFilterAlpha = 0.35,
                ReferenceFilterAlpha = 0.35,
            };

            return new Demodulator().Run(samples, SampleRateHz, settings);
        }
    }
}
