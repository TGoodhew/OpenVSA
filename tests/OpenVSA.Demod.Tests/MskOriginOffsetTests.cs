using System;
using System.Collections.Generic;
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
    /// <c>REQ-DEM-066</c>'s MSK clause: the IQ origin offset is computed at symbol times, "except
    /// for MSK, which uniquely uses all points rather than only symbol instants".
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>A clause like this needs a control, not just a passing measurement.</strong> The
    /// requirement says MSK is different without saying what goes wrong if it is not treated
    /// differently, and an implementation that quietly ignored the clause would still report a
    /// plausible number. So the symbol-instant estimate is computed here as well, from the same
    /// result, and the two are compared: if all points were no better than symbol instants, the
    /// clause would be decoration and this file would be saying so.
    /// </para>
    /// <para>
    /// The geometry behind it: MSK's symbol instants land on four points and its information lives
    /// in the continuous path between them, so fitting a constant to four clusters leans on how the
    /// block's symbols happened to be distributed among them. Every sample visits the whole circle.
    /// </para>
    /// </remarks>
    public class MskOriginOffsetTests
    {
        private const double SymbolRateHz = 1e6;
        private const double SampleRateHz = 16e6;
        private const int PerSymbol = 4;
        private const int Span = 12;

        private readonly ITestOutputHelper _output;

        public MskOriginOffsetTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void AKnownFeedthroughOnMskIsReportedToATenthOfADecibel()
        {
            // 512 symbols, which is what ImpairmentMetricTests uses for the same criterion on
            // 16-QAM -- not a length chosen until this passed. The estimate has a block-length
            // floor and it is worth stating rather than hiding: measured at 256 symbols the -30 dB
            // case reads -29.818, which is 0.18 dB out and would fail; at 512 it reads -29.929.
            // The residual is a proportional bias of about 1 % in magnitude that falls with the
            // injected level, so it is an estimator floor rather than a scale error.
            foreach (double db in new[] { -20.0, -30.0, -40.0 })
            {
                double fraction = Math.Pow(10.0, db / 20.0);

                DemodResult result = Demodulate(fraction, 512, 20260907);

                double reported = Row(result, "IQ Offset").Rms;

                _output.WriteLine(
                    "injected " + db.ToString("F1", CultureInfo.InvariantCulture) +
                    " dB, reported " + reported.ToString("F4", CultureInfo.InvariantCulture) +
                    " dB");

                Assert.True(
                    Math.Abs(reported - db) < 0.1,
                    "injected " + db + " dB and read " + reported + " dB");
            }
        }

        [Fact]
        public void AllPointsBeatsSymbolInstantsOnShortMskBlocks()
        {
            // THE CONTROL. Short blocks, because that is where the symbol-instant estimate is
            // supposed to suffer: a constant fitted to four clusters depends on how the block's
            // symbols fell among them, and a short block is where that lottery has not averaged
            // out. Several seeds, so what is compared is the spread of an estimator rather than one
            // lucky or unlucky draw.
            const double Db = -30.0;
            const int Short = 64;

            double fraction = Math.Pow(10.0, Db / 20.0);

            var allPoints = new List<double>();
            var symbolInstants = new List<double>();

            for (int seed = 0; seed < 8; seed++)
            {
                DemodResult result = Demodulate(fraction, Short, 20260907 + seed);

                allPoints.Add(Row(result, "IQ Offset").Rms);
                symbolInstants.Add(SymbolInstantOffsetDb(result));
            }

            double allError = RmsError(allPoints, Db);
            double symbolError = RmsError(symbolInstants, Db);

            _output.WriteLine(
                Short + "-symbol blocks, injected " + Db + " dB over " + allPoints.Count +
                " seeds:");

            _output.WriteLine(
                "  all points     mean " + allPoints.Average().ToString("F3") +
                " dB, rms error " + allError.ToString("F4") + " dB");

            _output.WriteLine(
                "  symbol instants mean " + symbolInstants.Average().ToString("F3") +
                " dB, rms error " + symbolError.ToString("F4") + " dB");

            _output.WriteLine(
                "  all points is " + (symbolError / allError).ToString("F1") +
                "x closer to the injected value");

            // The claim the requirement's clause makes, stated as the comparison it implies.
            Assert.True(
                allError < symbolError,
                "All points was no better than symbol instants (" + allError + " dB against " +
                symbolError + " dB), so REQ-DEM-066's MSK clause is buying nothing here and " +
                "either the implementation or this test is wrong.");
        }

        [Fact]
        public void ANonMskFormatStillUsesSymbolInstants()
        {
            // The clause is MSK's alone -- "uniquely", the requirement says. On QPSK the reported
            // offset must be the symbol-instant fit, so the two agree closely; if the all-points
            // path had been taken for every format, this is where it would show.
            DemodResult result = DemodulateQpsk(Math.Pow(10.0, -30.0 / 20.0), 256, 20260907);

            double reported = Row(result, "IQ Offset").Rms;
            double instants = SymbolInstantOffsetDb(result);

            _output.WriteLine(
                "QPSK: reported " + reported.ToString("F4") + " dB, symbol-instant fit " +
                instants.ToString("F4") + " dB");

            Assert.Equal(instants, reported, 2);
        }

        /// <summary>
        /// The origin offset from the symbol instants alone, as the requirement's default path.
        /// </summary>
        /// <param name="result">The demodulation.</param>
        /// <returns>The offset in decibels relative to the reference's rms magnitude.</returns>
        /// <remarks>
        /// The same affine model the chain fits, refitted here from the trace's own measured and
        /// ideal symbols. Recomputed rather than read off the result because the result no longer
        /// carries it for MSK — which is the whole point of the clause under test.
        /// </remarks>
        private static double SymbolInstantOffsetDb(DemodResult result)
        {
            IReadOnlyList<ConstellationPoint> measured = result.Trace.Measured;
            IReadOnlyList<ConstellationPoint> ideal = result.Trace.Ideal;

            int count = Math.Min(measured.Count, ideal.Count);

            double sumII = 0.0, sumIQ = 0.0, sumQQ = 0.0, sumI = 0.0, sumQ = 0.0;
            double crossIx = 0.0, crossQx = 0.0, sumX = 0.0;
            double crossIy = 0.0, crossQy = 0.0, sumY = 0.0;
            double referenceSquares = 0.0;

            for (int symbol = 0; symbol < count; symbol++)
            {
                double i = ideal[symbol].I;
                double q = ideal[symbol].Q;
                double x = measured[symbol].I;
                double y = measured[symbol].Q;

                sumII += i * i;
                sumIQ += i * q;
                sumQQ += q * q;
                sumI += i;
                sumQ += q;

                crossIx += i * x;
                crossQx += q * x;
                sumX += x;

                crossIy += i * y;
                crossQy += q * y;
                sumY += y;

                referenceSquares += (i * i) + (q * q);
            }

            double cI = Constant(sumII, sumIQ, sumI, sumQQ, sumQ, count, crossIx, crossQx, sumX);
            double cQ = Constant(sumII, sumIQ, sumI, sumQQ, sumQ, count, crossIy, crossQy, sumY);

            double rms = Math.Sqrt(referenceSquares / count);
            double magnitude = Math.Sqrt((cI * cI) + (cQ * cQ)) / rms;

            return magnitude < 1e-12 ? double.NegativeInfinity : 20.0 * Math.Log10(magnitude);
        }

        /// <summary>The constant term of a three-parameter least-squares fit, by Cramer's rule.</summary>
        private static double Constant(
            double sumII, double sumIQ, double sumI, double sumQQ, double sumQ, int count,
            double crossI, double crossQ, double sum)
        {
            double a = sumII, b = sumIQ, c = sumI;
            double d = sumIQ, e = sumQQ, f = sumQ;
            double g = sumI, h = sumQ, k = count;

            double determinant =
                (a * ((e * k) - (f * h))) -
                (b * ((d * k) - (f * g))) +
                (c * ((d * h) - (e * g)));

            if (Math.Abs(determinant) < 1e-18)
            {
                return 0.0;
            }

            // Third unknown only: replace the third column with the right-hand side.
            double third =
                (a * ((e * sum) - (crossQ * h))) -
                (b * ((d * sum) - (crossQ * g))) +
                (crossI * ((d * h) - (e * g)));

            return third / determinant;
        }

        private static double RmsError(IReadOnlyList<double> values, double wanted)
        {
            double sum = 0.0;

            foreach (double value in values)
            {
                sum += (value - wanted) * (value - wanted);
            }

            return Math.Sqrt(sum / values.Count);
        }

        private static ErrorMetric Row(DemodResult result, string label) =>
            result.Summary.Metrics.Single(metric => metric.Label == label);

        private static DemodResult Demodulate(double leakage, int resultLength, int seed)
        {
            Constellation format = Constellation.ByName("MSK");

            double[] taps = PulseFilter.Msk().Taps(PerSymbol, Span, FilterRole.Reference);

            var source = new ContinuousModulatedSource
            {
                Scheme = SchemeFor(format),
                SymbolRateHz = SymbolRateHz,
                SampleRateHz = SampleRateHz,
                PulseSpanSymbols = Span,
                TransmitPulse = taps,
                TransmitPulseSamplesPerSymbol = PerSymbol,
                Seed = seed,
            };

            var samples = new float[
                2 * (int)Math.Ceiling((resultLength + 400) * source.SamplesPerSymbol)];

            source.Restart();
            source.Fill(samples);
            Leak(samples, leakage);

            // The transmit pulse IS the whole shaping for MSK, so the measurement filter is None
            // and the reference is that same pulse -- the arrangement FormatCatalogueTests
            // establishes. Filtering a half-sine-shaped signal with a half-sine again would apply
            // the shaping twice.
            var settings = new DemodSettings
            {
                Constellation = format,
                SymbolRateHz = SymbolRateHz,
                PointsPerSymbol = PerSymbol,
                ResultLengthSymbols = resultLength,
                FilterSymbolSpan = Span,
                MeasurementFilter = PulseFilterType.None,
                ReferenceFilter = PulseFilterType.Msk,
            };

            return new Demodulator().Run(samples, SampleRateHz, settings);
        }

        private static DemodResult DemodulateQpsk(double leakage, int resultLength, int seed)
        {
            Constellation format = Constellation.Qpsk();

            var source = new ContinuousModulatedSource
            {
                Scheme = SchemeFor(format),
                SymbolRateHz = SymbolRateHz,
                SampleRateHz = SampleRateHz,
                RollOff = 0.35,
                PulseSpanSymbols = Span,
                Seed = seed,
            };

            var samples = new float[
                2 * (int)Math.Ceiling((resultLength + 400) * source.SamplesPerSymbol)];

            source.Restart();
            source.Fill(samples);
            Leak(samples, leakage);

            var settings = new DemodSettings
            {
                Constellation = format,
                SymbolRateHz = SymbolRateHz,
                ResultLengthSymbols = resultLength,
                FilterSymbolSpan = Span,
                MeasurementFilter = PulseFilterType.RootRaisedCosine,
                MeasurementFilterAlpha = 0.35,
                ReferenceFilterAlpha = 0.35,
            };

            return new Demodulator().Run(samples, SampleRateHz, settings);
        }

        /// <summary>Adds a constant to the baseband, which is what a leaking carrier is.</summary>
        private static void Leak(float[] samples, double leakage)
        {
            for (int sample = 0; sample < samples.Length; sample += 2)
            {
                samples[sample] = (float)(samples[sample] + leakage);
            }
        }

        private static ModulationScheme SchemeFor(Constellation constellation)
        {
            var points = new List<SymbolPoint>(constellation.Count);

            foreach (ConstellationPoint point in constellation.Points)
            {
                points.Add(new SymbolPoint(point.I, point.Q));
            }

            return ModulationScheme.FromPoints(
                constellation.Name,
                points,
                constellation.IsOffset,
                constellation.RotationPerSymbolRadians);
        }
    }
}
