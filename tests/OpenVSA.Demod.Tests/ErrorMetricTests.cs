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
    /// <c>REQ-DEM-060</c> to <c>REQ-DEM-065</c>: the error metrics, their normalisation and their
    /// units.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Every one of these is checked against a closed form, not against a previous
    /// run.</strong> Each requirement in this group states the arithmetic it wants, and each
    /// acceptance criterion is an impairment whose effect on that arithmetic can be worked out on
    /// paper. So the impairments are applied to the ideal points directly rather than transmitted
    /// through the chain: what is being tested is the formula, and a signal that had been through
    /// fourteen steps first would be testing the steps.
    /// </para>
    /// <para>
    /// The chain's own agreement with these formulas is what
    /// <see cref="ErrorSummaryTableTests"/> and the bench cross-check cover.
    /// </para>
    /// </remarks>
    public class ErrorMetricTests
    {
        private readonly ITestOutputHelper _output;

        public ErrorMetricTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void AnUnimpairedSignalReportsNoErrorAtAll()
        {
            // REQ-DEM-060: "An unimpaired reference signal returns EVM below 1e-6 %, confirming no
            // error is introduced by the measurement chain itself." Measured against a constellation
            // whose points are not all the same distance out, so that a normalisation mistake would
            // show rather than cancel.
            List<ConstellationPoint> ideal = Points(Constellation.Qam(16), 512);

            ErrorSummary summary = ErrorSummary.For(ideal, ideal);

            double evm = Value(summary, "EVM");

            _output.WriteLine(
                "measured against itself: EVM " + evm.ToString("E3", CultureInfo.InvariantCulture) +
                " %rms, Mag Err " +
                Value(summary, "Mag Err").ToString("E3", CultureInfo.InvariantCulture) +
                " %rms, Phase Err " +
                Value(summary, "Phase Err").ToString("E3", CultureInfo.InvariantCulture) + " deg");

            Assert.True(evm < 1e-6, "an unimpaired signal reported " + evm + " % EVM");
            Assert.True(Value(summary, "Mag Err") < 1e-6);
            Assert.True(Value(summary, "Phase Err") < 1e-6);
        }

        [Fact]
        public void NoiseOfAKnownVarianceGivesTheClosedFormEvm()
        {
            // REQ-DEM-060: "For a constellation impaired by additive noise of known variance, EVM
            // matches the closed-form value to within 0.1 % relative."
            //
            // The closed form: each symbol's error vector is complex Gaussian of total variance
            // sigma^2, so the mean square error is sigma^2 exactly and EVM_rms = sigma / V_norm.
            // Nothing is estimated here -- the noise is ADDED to the ideal points, so the error
            // vector IS the noise and the expected value is arithmetic rather than a simulation.
            // V_norm cancels between the two sides, so what is being compared is the realised RMS
            // of the noise against the sigma it was drawn at.
            //
            // 🔴 The number of symbols is set by the tolerance, not chosen for roundness. |e|^2 for
            // complex Gaussian noise is exponentially distributed, so the mean of N of them has a
            // relative standard error of 1/sqrt(N) and its square root -- which is what EVM is --
            // has 1/(2 sqrt(N)). The requirement asks for agreement within 0.1 % relative, so
            //
            //     4 sigma of margin  =>  4 / (2 sqrt(N)) <= 0.001  =>  N >= 4e6.
            //
            // A first version of this test used 200 000 symbols, where the standard error is
            // 0.112 % and the 0.1 % tolerance is ONE sigma. It read 0.1326 % out and failed, which
            // was the sample size being wrong rather than the metric -- and is exactly the failure
            // that would have been "fixed" by widening the tolerance to 0.2 %.
            const double Sigma = 0.05;
            const int Symbols = 4000000;

            // Arrays rather than lists: two of these is 128 MB of ConstellationPoint either way,
            // and a List's growth doubling would briefly ask for half as much again.
            var ideal = new ConstellationPoint[Symbols];
            var noisy = new ConstellationPoint[Symbols];

            Constellation format = Constellation.Qam(16);
            var random = new Random(20260824);

            for (int symbol = 0; symbol < Symbols; symbol++)
            {
                double i;
                double q;

                Gaussian(random, out i, out q);

                ConstellationPoint point = format.Points[random.Next(format.Count)];

                ideal[symbol] = point;
                noisy[symbol] = new ConstellationPoint(
                    point.I + (i * Sigma / Math.Sqrt(2.0)),
                    point.Q + (q * Sigma / Math.Sqrt(2.0)));
            }

            ErrorSummary summary = ErrorSummary.For(noisy, ideal);

            double expected = Sigma / summary.Reference.Volts * 100.0;
            double measured = Value(summary, "EVM");

            _output.WriteLine(
                "sigma " + Sigma.ToString("G4", CultureInfo.InvariantCulture) + " over " +
                Symbols.ToString(CultureInfo.InvariantCulture) + " symbols: expected " +
                expected.ToString("F6", CultureInfo.InvariantCulture) + " %rms, measured " +
                measured.ToString("F6", CultureInfo.InvariantCulture) + " %rms, " +
                (Math.Abs(measured - expected) / expected * 100.0).ToString(
                    "F4", CultureInfo.InvariantCulture) + " % relative");

            Assert.True(
                Math.Abs(measured - expected) / expected < 1e-3,
                "expected " + expected + " %rms and measured " + measured);
        }

        [Fact]
        public void OneDisplacedSymbolGivesThePredictablePeakAtItsOwnIndex()
        {
            // REQ-DEM-060: "A single symbol displaced by a known amount produces the analytically
            // predictable EVM_peak, and the reported index k is that symbol's index exactly."
            const int Symbols = 400;
            const int Displaced = 137;
            const double By = 0.31;

            List<ConstellationPoint> ideal = Points(Constellation.Qam(16), Symbols);
            var measured = new List<ConstellationPoint>(ideal);

            measured[Displaced] = new ConstellationPoint(
                ideal[Displaced].I + By, ideal[Displaced].Q);

            ErrorSummary summary = ErrorSummary.For(measured, ideal);
            ErrorMetric evm = summary.Metrics.Single(metric => metric.Label == "EVM");

            double expectedPeak = By / summary.Reference.Volts * 100.0;

            // And the RMS, which is the one displacement spread over N symbols.
            double expectedRms = Math.Sqrt((By * By) / Symbols) / summary.Reference.Volts * 100.0;

            _output.WriteLine(
                "one symbol displaced by " + By.ToString("G3", CultureInfo.InvariantCulture) +
                ": peak " + evm.Peak.ToString("F6", CultureInfo.InvariantCulture) +
                " % against " + expectedPeak.ToString("F6", CultureInfo.InvariantCulture) +
                " predicted, at symbol " + evm.PeakSymbol + " against " + Displaced +
                "; RMS " + evm.Rms.ToString("F6", CultureInfo.InvariantCulture) + " against " +
                expectedRms.ToString("F6", CultureInfo.InvariantCulture));

            Assert.Equal(Displaced, evm.PeakSymbol);
            Assert.True(Math.Abs(evm.Peak - expectedPeak) / expectedPeak < 1e-9);
            Assert.True(Math.Abs(evm.Rms - expectedRms) / expectedRms < 1e-9);
        }

        [Fact]
        public void SwitchingNormalisationChangesEvmByExactlyThePredictedRatio()
        {
            // REQ-DEM-061: "Switching normalisation between max-magnitude and RMS for a 16-QAM
            // signal changes reported EVM by exactly the analytically predicted ratio
            // (sqrt(P_max / P_avg)), confirming the normalisation is applied and not hard-coded."
            //
            // For 16-QAM on the +/-1, +/-3 grid the corner is 1 + 9 = 18 and the mean power is 10,
            // so the ratio is sqrt(1.8) = 1.341641. Whatever scale the constellation is built at,
            // the RATIO is a property of the geometry and not of the scale.
            Constellation format = Constellation.Qam(16);

            List<ConstellationPoint> ideal = Points(format, 4096);
            List<ConstellationPoint> measured = Displaced(ideal, 0.037, 20260824);

            double onRms = Evm(measured, ideal, format, EvmNormalisation.RmsMagnitude);
            double onMax = Evm(measured, ideal, format, EvmNormalisation.MaximumMagnitude);

            var reference = EvmReference.FromPoints(
                EvmNormalisation.RmsMagnitude, format.Points, 0.0);

            _output.WriteLine(
                "16-QAM: RMS-referenced " + onRms.ToString("F6", CultureInfo.InvariantCulture) +
                " %rms, max-referenced " + onMax.ToString("F6", CultureInfo.InvariantCulture) +
                " %rms, ratio " + (onRms / onMax).ToString("F6", CultureInfo.InvariantCulture) +
                " against a predicted " +
                reference.MaximumOverRms.ToString("F6", CultureInfo.InvariantCulture));

            Assert.True(Math.Abs(reference.MaximumOverRms - Math.Sqrt(1.8)) < 1e-12);
            Assert.True(Math.Abs((onRms / onMax) - reference.MaximumOverRms) < 1e-12);

            _output.WriteLine(reference.Describe());
        }

        [Fact]
        public void TheChoiceIsInertOnAConstantModulusFormatAndSaysSo()
        {
            // "The choice only has consequences for variable-envelope formats; for constant-modulus
            // formats the maximum and RMS magnitudes are the same number and the setting is inert."
            // Asserted BOTH ways: the readings are identical to the last bit, and the reference says
            // in advance that they will be, so a user is not left changing a control and watching
            // nothing happen.
            foreach (Constellation format in new[]
            {
                Constellation.Bpsk(), Constellation.Qpsk(), Constellation.Psk(8),
            })
            {
                List<ConstellationPoint> ideal = Points(format, 1024);
                List<ConstellationPoint> measured = Displaced(ideal, 0.04, 7);

                double onRms = Evm(measured, ideal, format, EvmNormalisation.RmsMagnitude);
                double onMax = Evm(measured, ideal, format, EvmNormalisation.MaximumMagnitude);

                var reference = EvmReference.FromPoints(
                    EvmNormalisation.RmsMagnitude, format.Points, 0.0);

                _output.WriteLine(
                    format.Name + ": " + onRms.ToString("R", CultureInfo.InvariantCulture) +
                    " either way, inert " + reference.IsInert);

                Assert.True(reference.IsInert);
                Assert.Equal(onRms, onMax);
            }

            // And it is NOT inert where the requirement says it has consequences.
            foreach (Constellation format in new[]
            {
                Constellation.Qam(16), Constellation.Qam(64), Constellation.StarQam(32),
            })
            {
                var reference = EvmReference.FromPoints(
                    EvmNormalisation.RmsMagnitude, format.Points, 0.0);

                _output.WriteLine(
                    format.Name + ": max/RMS = " +
                    reference.MaximumOverRms.ToString("F4", CultureInfo.InvariantCulture));

                Assert.False(reference.IsInert);
            }
        }

        [Fact]
        public void AUserSpecifiedNormalisationIsUsedAndAnImpossibleOneIsRefused()
        {
            Constellation format = Constellation.Qam(16);

            List<ConstellationPoint> ideal = Points(format, 512);
            List<ConstellationPoint> measured = Displaced(ideal, 0.05, 11);

            var reference = EvmReference.FromPoints(
                EvmNormalisation.RmsMagnitude, format.Points, 0.0);

            // Twice the RMS: every percentage halves, exactly.
            double onRms = Evm(measured, ideal, format, EvmNormalisation.RmsMagnitude);
            double onUser = Value(
                ErrorSummary.For(
                    measured,
                    ideal,
                    new EvmReference(
                        EvmNormalisation.UserSpecified,
                        reference.MaximumMagnitude,
                        reference.RmsMagnitude,
                        reference.RmsMagnitude * 2.0)),
                "EVM");

            _output.WriteLine(
                "RMS-referenced " + onRms.ToString("F6", CultureInfo.InvariantCulture) +
                " %rms; referenced to twice the RMS " +
                onUser.ToString("F6", CultureInfo.InvariantCulture) + " %rms");

            Assert.True(Math.Abs((onRms / 2.0) - onUser) < 1e-12);

            // A normalisation of zero would report every error as infinite.
            Assert.Throws<ArgumentException>(() => new EvmReference(
                EvmNormalisation.UserSpecified, 1.0, 1.0, 0.0));

            var settings = new DemodSettings
            {
                Constellation = format,
                SymbolRateHz = 1e6,
                EvmNormalisation = EvmNormalisation.UserSpecified,
            };

            ArgumentException refused = Assert.Throws<ArgumentException>(() => settings.Validate());

            Assert.Contains("REQ-DEM-061", refused.Message, StringComparison.Ordinal);
            _output.WriteLine(refused.Message);
        }

        [Fact]
        public void MagnitudeErrorReadsAKnownGainErrorAndIgnoresAPhaseOne()
        {
            // REQ-DEM-063: "A constellation impaired by a known pure magnitude error returns that
            // value to within 0.1 % relative, and a signal impaired by pure phase error returns a
            // magnitude error near zero — the pair establishes that the metric separates magnitude
            // from phase rather than mixing them."
            //
            // A gain of (1 + g) makes each symbol's magnitude error g|r_k|, so the RMS over the
            // block is g * RMS(|r|) and the reported figure is g * RMS(|r|) / V_norm. With V_norm
            // the RMS magnitude those cancel and it is g exactly -- which is why this is stated
            // against the default normalisation and why the number to compare with is g itself.
            const double Gain = 0.023;

            Constellation format = Constellation.Qam(16);
            List<ConstellationPoint> ideal = Points(format, 4096);

            var scaled = ideal
                .Select(point => new ConstellationPoint(
                    point.I * (1.0 + Gain), point.Q * (1.0 + Gain)))
                .ToList();

            ErrorSummary magnitudeOnly = ErrorSummary.For(scaled, ideal);

            double expected = Gain * 100.0;
            double measured = Value(magnitudeOnly, "Mag Err");

            _output.WriteLine(
                "a gain error of " + expected.ToString("F4", CultureInfo.InvariantCulture) +
                " % reads " + measured.ToString("F6", CultureInfo.InvariantCulture) +
                " %rms magnitude error and " +
                Value(magnitudeOnly, "Phase Err").ToString("E3", CultureInfo.InvariantCulture) +
                " deg of phase error");

            Assert.True(Math.Abs(measured - expected) / expected < 1e-3);

            // Pure phase: every magnitude is untouched, so the magnitude error is zero -- and it has
            // to be zero to floating point, not merely small, because a rotation changes no
            // magnitude at all.
            const double Radians = 0.05;

            var rotated = ideal
                .Select(point => new ConstellationPoint(
                    (point.I * Math.Cos(Radians)) - (point.Q * Math.Sin(Radians)),
                    (point.I * Math.Sin(Radians)) + (point.Q * Math.Cos(Radians))))
                .ToList();

            ErrorSummary phaseOnly = ErrorSummary.For(rotated, ideal);

            _output.WriteLine(
                "a rotation of " + Radians.ToString("G3", CultureInfo.InvariantCulture) +
                " rad reads " +
                Value(phaseOnly, "Mag Err").ToString("E3", CultureInfo.InvariantCulture) +
                " %rms magnitude error and " +
                Value(phaseOnly, "Phase Err").ToString("F6", CultureInfo.InvariantCulture) +
                " deg of phase error");

            Assert.True(Value(phaseOnly, "Mag Err") < 1e-10);
        }

        [Fact]
        public void PhaseErrorIsReportedInDegreesAndNotInRadians()
        {
            // REQ-DEM-064: "A signal with a 1 radian error reports approximately 57.3, not 1 — the
            // radians/degrees slip is the defect this requirement calls out, so it is asserted
            // rather than left to inspection."
            const double Radians = 1.0;

            List<ConstellationPoint> ideal = Points(Constellation.Qam(16), 1024);

            var rotated = ideal
                .Select(point => new ConstellationPoint(
                    (point.I * Math.Cos(Radians)) - (point.Q * Math.Sin(Radians)),
                    (point.I * Math.Sin(Radians)) + (point.Q * Math.Cos(Radians))))
                .ToList();

            double measured = Value(ErrorSummary.For(rotated, ideal), "Phase Err");

            _output.WriteLine(
                "a rotation of one radian reads " +
                measured.ToString("F6", CultureInfo.InvariantCulture) + " deg");

            Assert.True(Math.Abs(measured - (180.0 / Math.PI)) / (180.0 / Math.PI) < 1e-3);

            // Stated the other way round, because this is the whole point of the requirement.
            Assert.True(measured > 50.0, "one radian was reported as " + measured);
        }

        [Fact]
        public void APhaseErrorAtTheBranchCutKeepsItsSign()
        {
            // REQ-DEM-064: "Symbols whose error approaches +/-pi are handled by the principal-value
            // branch without wrapping to the wrong sign, tested at the boundary."
            //
            // A rotation of pi - epsilon is a large POSITIVE error. Computed as a difference of two
            // arguments it can come out as -(pi + epsilon) folded to just under +pi or just over
            // -pi depending on where each symbol's own argument sits, which is a sign that flips
            // symbol by symbol. Computed as arg(z r*) it cannot: the product's argument is already
            // the principal value.
            const double NearPi = Math.PI - 1e-6;

            foreach (double radians in new[] { NearPi, -NearPi })
            {
                List<ConstellationPoint> ideal = Points(Constellation.Psk(8), 512);

                var rotated = ideal
                    .Select(point => new ConstellationPoint(
                        (point.I * Math.Cos(radians)) - (point.Q * Math.Sin(radians)),
                        (point.I * Math.Sin(radians)) + (point.Q * Math.Cos(radians))))
                    .ToList();

                ErrorMetric phase = ErrorSummary.For(rotated, ideal).Metrics
                    .Single(metric => metric.Label == "Phase Err");

                double expected = Math.Abs(radians) * 180.0 / Math.PI;

                _output.WriteLine(
                    "a rotation of " + (radians * 180.0 / Math.PI).ToString(
                        "F6", CultureInfo.InvariantCulture) +
                    " deg reads " + phase.Rms.ToString("F6", CultureInfo.InvariantCulture) +
                    " deg rms, peak " +
                    phase.Peak.ToString("F6", CultureInfo.InvariantCulture));

                // The RMS is the magnitude, and the PEAK carries the sign -- which is the thing
                // that would flip if the branch were got wrong.
                Assert.True(Math.Abs(phase.Rms - expected) < 1e-6);
                Assert.Equal(Math.Sign(radians), Math.Sign(phase.Peak));
            }
        }

        [Fact]
        public void AKilohertzOfCarrierOffsetIsReportedAsAKilohertz()
        {
            // REQ-DEM-065: "A simulated 1 kHz carrier offset is reported as 1 kHz +/- 0.1 Hz."
            const double OffsetHz = 1e3;
            const double SymbolRateHz = 1e6;
            const double SampleRateHz = 16e6;

            var source = new ContinuousModulatedSource
            {
                Scheme = ModulationScheme.Qpsk(),
                SymbolRateHz = SymbolRateHz,
                SampleRateHz = SampleRateHz,
                RollOff = 0.35,
                PulseSpanSymbols = 20,
                Seed = 20260824,
                CarrierOffsetHz = OffsetHz,
            };

            var samples = new float[2 * 4000 * 16];

            source.Restart();
            source.Fill(samples);

            var settings = new DemodSettings
            {
                Constellation = Constellation.Qpsk(),
                SymbolRateHz = SymbolRateHz,
                ResultLengthSymbols = 512,
                FilterSymbolSpan = 20,
                MeasurementFilter = PulseFilterType.RootRaisedCosine,
                MeasurementFilterAlpha = 0.35,
                ReferenceFilterAlpha = 0.35,
            };

            DemodResult result = new Demodulator().Run(samples, SampleRateHz, settings);

            double row = result.Summary.Metrics
                .Single(metric => metric.Label == "Freq Err").Rms;

            _output.WriteLine(
                "a 1 kHz offset reads " + row.ToString("F4", CultureInfo.InvariantCulture) +
                " Hz in the summary and " +
                result.CarrierFrequencyErrorHz.ToString("F4", CultureInfo.InvariantCulture) +
                " Hz on the result");

            Assert.True(
                Math.Abs(row - OffsetHz) < 0.1,
                "the frequency error row read " + row + " Hz");

            // And the row is the same number the result reports, not a second estimate of it.
            Assert.Equal(result.CarrierFrequencyErrorHz, row);
        }

        [Fact]
        public void TheSummaryCarriesWhatItsPercentagesArePercentagesOf()
        {
            // REQ-DEM-072 through REQ-DEM-061: a percentage whose denominator is unstated is a
            // number without its provenance, and the denominator here is a setting.
            Constellation format = Constellation.Qam(16);
            List<ConstellationPoint> ideal = Points(format, 256);

            ErrorSummary summary = ErrorSummary.For(
                Displaced(ideal, 0.02, 3),
                ideal,
                EvmReference.FromPoints(
                    EvmNormalisation.MaximumMagnitude, format.Points, 0.0));

            _output.WriteLine(summary.Reference.Describe());

            Assert.Equal(EvmNormalisation.MaximumMagnitude, summary.Reference.Choice);
            Assert.Equal(summary.Reference.MaximumMagnitude, summary.Reference.Volts);
            Assert.Contains("largest magnitude", summary.Reference.Describe());

            // And it survives being turned into the table the display renders.
            ErrorSummary table = summary.AsTableFor(format.Family, format.IsOffset);

            Assert.Equal(summary.Reference.Volts, table.Reference.Volts);
        }

        private static List<ConstellationPoint> Points(Constellation format, int symbols)
        {
            var points = new List<ConstellationPoint>(symbols);
            var random = new Random(1234567);

            for (int symbol = 0; symbol < symbols; symbol++)
            {
                points.Add(format.Points[random.Next(format.Count)]);
            }

            return points;
        }

        /// <summary>The same points, each nudged by a fixed distance in a random direction.</summary>
        private static List<ConstellationPoint> Displaced(
            IReadOnlyList<ConstellationPoint> ideal, double by, int seed)
        {
            var random = new Random(seed);
            var moved = new List<ConstellationPoint>(ideal.Count);

            foreach (ConstellationPoint point in ideal)
            {
                double angle = random.NextDouble() * 2.0 * Math.PI;

                moved.Add(new ConstellationPoint(
                    point.I + (by * Math.Cos(angle)), point.Q + (by * Math.Sin(angle))));
            }

            return moved;
        }

        private static double Evm(
            IReadOnlyList<ConstellationPoint> measured,
            IReadOnlyList<ConstellationPoint> ideal,
            Constellation format,
            EvmNormalisation choice) =>
            Value(
                ErrorSummary.For(
                    measured, ideal, EvmReference.FromPoints(choice, format.Points, 0.0)),
                "EVM");

        private static double Value(ErrorSummary summary, string label) =>
            summary.Metrics.Single(metric => metric.Label == label).Rms;

        /// <summary>Two standard normal deviates, by Box and Muller.</summary>
        private static void Gaussian(Random random, out double first, out double second)
        {
            double u = 1.0 - random.NextDouble();
            double v = random.NextDouble();
            double radius = Math.Sqrt(-2.0 * Math.Log(u));

            first = radius * Math.Cos(2.0 * Math.PI * v);
            second = radius * Math.Sin(2.0 * Math.PI * v);
        }
    }
}
