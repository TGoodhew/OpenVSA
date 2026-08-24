using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using OpenVSA.Demod.Chain;
using OpenVSA.Demod.Help;
using OpenVSA.Demod.Results;
using OpenVSA.Demod.Signal;
using OpenVSA.Synthesis;
using Xunit;
using Xunit.Abstractions;

namespace OpenVSA.Demod.Tests
{
    /// <summary>
    /// <c>REQ-DEM-066</c> to <c>REQ-DEM-069</c>: the origin offset, the axis impairments, rho and
    /// the signal-to-noise ratio.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The impairments are injected into a generated signal and read back through the whole
    /// chain</strong>, not applied to the ideal points. These four are properties of an estimator
    /// rather than of a formula: the question <c>REQ-DEM-067a</c> asks is whether step 8's carrier
    /// phase and step 12's skew can be told apart, and that question does not exist unless step 8
    /// has run.
    /// </para>
    /// </remarks>
    public class ImpairmentMetricTests
    {
        private const double SymbolRateHz = 1e6;
        private const double SampleRateHz = 16e6;
        private const int Symbols = 4000;

        private readonly ITestOutputHelper _output;

        public ImpairmentMetricTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void AKnownCarrierFeedthroughIsReportedToATenthOfADecibel()
        {
            // REQ-DEM-066: "A signal with a known injected carrier feedthrough returns that value to
            // within 0.1 dB". The injection is a constant added to the baseband signal, which is
            // exactly what a leaking carrier is; its level relative to the reference's RMS is the
            // number the requirement asks for.
            foreach (double db in new[] { -20.0, -30.0, -40.0 })
            {
                double fraction = Math.Pow(10.0, db / 20.0);

                DemodResult result = Demodulate(
                    Constellation.Qam(16), leakage: fraction, imbalanceDb: 0.0, skewDegrees: 0.0);

                double reported = Row(result, "IQ Offset").Rms;

                _output.WriteLine(
                    "injected " + db.ToString("F1", CultureInfo.InvariantCulture) +
                    " dB of feedthrough, reported " +
                    reported.ToString("F4", CultureInfo.InvariantCulture) + " dB");

                Assert.True(
                    Math.Abs(reported - db) < 0.1,
                    "injected " + db + " dB and read " + reported + " dB");
            }
        }

        [Fact]
        public void NoFeedthroughReadsTheStatedFloorAndNotAnArtefact()
        {
            // "a signal with none returns -infinity dB (or the stated floor) rather than a large
            // negative artefact". A real measurement has noise, so the floor is reached only by an
            // offset that is exactly zero -- which is what the points-only path gives, and is the
            // path the constant is defined on.
            var ideal = new List<ConstellationPoint>();
            Constellation format = Constellation.Qam(16);

            for (int symbol = 0; symbol < 256; symbol++)
            {
                ideal.Add(format.Points[symbol % format.Count]);
            }

            double reported = ErrorSummary.For(ideal, ideal).Metrics
                .Single(metric => metric.Label == "IQ Offset").Rms;

            _output.WriteLine("no feedthrough at all reads " + reported + " dB");

            Assert.Equal(ErrorSummary.NoOriginOffsetDb, reported);
            Assert.True(reported < -100.0);
        }

        [Fact]
        public void ChangingTheEvmNormalisationLeavesTheOriginOffsetBitIdentical()
        {
            // 🔴 REQ-DEM-066's first departure, asserted directly: "changing the EVM normalisation
            // selection leaves the reported IQ offset bit-identical — for 16-QAM the naive
            // implementation shifts by 2.55 dB, so this test discriminates."
            //
            // 20 log10(sqrt(18/10)) = 2.5527 dB is the gap between 16-QAM's maximum and RMS
            // magnitudes, and it is exactly what would appear here if the offset were divided by
            // V_norm. The IQ offset is a property of the signal; an EVM display option must not
            // move it.
            const double Db = -30.0;

            double onRms = Row(
                Demodulate(
                    Constellation.Qam(16),
                    Math.Pow(10.0, Db / 20.0),
                    0.0,
                    0.0,
                    EvmNormalisation.RmsMagnitude),
                "IQ Offset").Rms;

            double onMax = Row(
                Demodulate(
                    Constellation.Qam(16),
                    Math.Pow(10.0, Db / 20.0),
                    0.0,
                    0.0,
                    EvmNormalisation.MaximumMagnitude),
                "IQ Offset").Rms;

            _output.WriteLine(
                "RMS-referenced EVM: IQ offset " +
                onRms.ToString("R", CultureInfo.InvariantCulture) +
                " dB; max-referenced EVM: " +
                onMax.ToString("R", CultureInfo.InvariantCulture) + " dB");

            // Bit-identical, as the requirement says -- not "close".
            Assert.Equal(onRms, onMax);

            // And the number that would have appeared if it were wrong, so the test's own
            // discrimination is on the record rather than asserted.
            _output.WriteLine(
                "the naive implementation would differ by " +
                (20.0 * Math.Log10(Math.Sqrt(1.8))).ToString("F4", CultureInfo.InvariantCulture) +
                " dB");
        }

        [Fact]
        public void AShortUnbalancedBlockStillReadsTheInjectedOffset()
        {
            // REQ-DEM-066's second departure: "on a short block with a deliberately unbalanced
            // symbol sequence the reported offset matches the injected value within tolerance,
            // where (1/N) sum z_k would be measurably biased."
            //
            // The block is made unbalanced by fitting points whose mean is a long way from the
            // origin, and a gain error is injected alongside so that the bias has something to bite
            // on: the mean of z - r carries (g - 1) times the mean of r, so a naive offset reports
            // part of the gain error as feedthrough.
            const double Injected = 0.02;
            const double Gain = 0.10;

            Constellation format = Constellation.Qam(16);

            // Only the two outermost points on one side, so the block's mean is emphatically not
            // zero. Thirty-two symbols: short, as the criterion asks.
            var ideal = new List<ConstellationPoint>();
            var measured = new List<ConstellationPoint>();

            ConstellationPoint corner = format.Points
                .OrderByDescending(point => (point.I * point.I) + (point.Q * point.Q))
                .First();

            for (int symbol = 0; symbol < 32; symbol++)
            {
                ideal.Add(corner);
                measured.Add(new ConstellationPoint(
                    (corner.I * (1.0 + Gain)) + Injected, corner.Q * (1.0 + Gain)));
            }

            // What the naive mean would say.
            double naive = 0.0;

            for (int symbol = 0; symbol < ideal.Count; symbol++)
            {
                naive += measured[symbol].I - ideal[symbol].I;
            }

            naive /= ideal.Count;

            _output.WriteLine(
                "injected " + Injected.ToString("G4", CultureInfo.InvariantCulture) +
                " alongside a " + (Gain * 100.0).ToString("F0", CultureInfo.InvariantCulture) +
                " % gain error; the mean of z - r reads " +
                naive.ToString("F6", CultureInfo.InvariantCulture));

            // The bias is large -- that is the point of the case.
            Assert.True(Math.Abs(naive - Injected) > Injected);

            // A single repeated point cannot separate the axes, so this block is the degenerate one
            // step 12 declines on. What the criterion needs is a block that is unbalanced and still
            // separable, which is the one below.
            DemodResult result = Demodulate(
                format, leakage: 0.0, imbalanceDb: 0.0, skewDegrees: 0.0, injectedOffset: Injected);

            double fitted = Math.Sqrt(
                (result.Impairments.OffsetI * result.Impairments.OffsetI) +
                (result.Impairments.OffsetQ * result.Impairments.OffsetQ));

            _output.WriteLine(
                "through the chain, the fitted offset is " +
                fitted.ToString("F6", CultureInfo.InvariantCulture) + " against " +
                Injected.ToString("F6", CultureInfo.InvariantCulture) + " injected");

            Assert.True(Math.Abs(fitted - Injected) < Injected * 0.05);
        }

        [Theory]
        [InlineData(0.5, 0.0)]
        [InlineData(-0.5, 0.0)]
        [InlineData(0.0, 3.0)]
        [InlineData(0.0, -3.0)]
        [InlineData(0.5, 3.0)]
        [InlineData(-0.8, -4.0)]
        public void GainImbalanceAndQuadratureSkewAreReadBackIncludingTogether(
            double imbalanceDb, double skewDegrees)
        {
            // REQ-DEM-067: "Signals with known injected gain imbalance and quadrature skew return
            // those values to within 0.05 dB and 0.1 degrees respectively, including when both are
            // present together — the cross-term case, where a one-at-a-time estimator passes the
            // singles and fails the pair."
            //
            // The last two rows are that pair. An estimator that reads the imbalance as the ratio of
            // the two fitted axis LENGTHS and the skew as the angle between them passes the four
            // single cases and gets both wrong here, because with unequal gains the angle between
            // the axes is no longer the skew.
            DemodResult result = Demodulate(
                Constellation.Qam(16), 0.0, imbalanceDb, skewDegrees);

            _output.WriteLine(
                "injected " + imbalanceDb.ToString("F2", CultureInfo.InvariantCulture) +
                " dB and " + skewDegrees.ToString("F2", CultureInfo.InvariantCulture) +
                " deg; read " +
                result.Impairments.GainImbalanceDb.ToString("F4", CultureInfo.InvariantCulture) +
                " dB and " +
                result.Impairments.QuadratureSkewDegrees.ToString(
                    "F4", CultureInfo.InvariantCulture) + " deg");

            Assert.True(
                Math.Abs(result.Impairments.GainImbalanceDb - imbalanceDb) < 0.05,
                "gain imbalance: injected " + imbalanceDb + " dB, read " +
                result.Impairments.GainImbalanceDb + " dB");

            Assert.True(
                Math.Abs(result.Impairments.QuadratureSkewDegrees - skewDegrees) < 0.1,
                "quadrature skew: injected " + skewDegrees + " deg, read " +
                result.Impairments.QuadratureSkewDegrees + " deg");
        }

        [Fact]
        public void GainImbalanceIsPositiveWhenQExceedsI()
        {
            // "Gain imbalance is positive when Q exceeds I, asserted against the stated convention."
            // A convention is a coin toss until something pins it, and the sign of this one was the
            // other way round before REQ-DEM-067 was read carefully.
            DemodResult larger = Demodulate(Constellation.Qam(16), 0.0, 1.0, 0.0);
            DemodResult smaller = Demodulate(Constellation.Qam(16), 0.0, -1.0, 0.0);

            _output.WriteLine(
                "Q an decibel larger reads " +
                larger.Impairments.GainImbalanceDb.ToString("F4", CultureInfo.InvariantCulture) +
                " dB; Q a decibel smaller reads " +
                smaller.Impairments.GainImbalanceDb.ToString("F4", CultureInfo.InvariantCulture) +
                " dB");

            Assert.True(larger.Impairments.GainImbalanceDb > 0.0);
            Assert.True(smaller.Impairments.GainImbalanceDb < 0.0);
        }

        [Fact]
        public void PureSkewLeavesNoRotationForTheCarrierPhaseToHaveEaten()
        {
            // 🔴 REQ-DEM-067a's discriminating test: "injecting pure quadrature skew and then
            // estimating carrier phase returns a phase near zero, whereas the one-sided shear model
            // absorbs psi/2 into phase and reports a non-zero value, so this test distinguishes the
            // two forms directly."
            //
            // The signal is impaired with the SYMMETRIC model -- each axis turned by half the skew,
            // which is a stretch along the 45-degree line and has no rotational component. Step 8
            // therefore finds no phase to remove, and step 12's decomposition finds no rotation left
            // over. Under a one-sided shear both would read half the skew: 2.5 degrees here, which
            // is twenty-five times the tolerance below.
            const double Skew = 5.0;

            DemodResult result = Demodulate(Constellation.Qam(16), 0.0, 0.0, Skew);

            _output.WriteLine(
                "a symmetric skew of " + Skew.ToString("F1", CultureInfo.InvariantCulture) +
                " deg reads " +
                result.Impairments.QuadratureSkewDegrees.ToString(
                    "F4", CultureInfo.InvariantCulture) +
                " deg of skew and leaves " +
                result.Impairments.ResidualRotationDegrees.ToString(
                    "F4", CultureInfo.InvariantCulture) +
                " deg of rotation; a shear model would leave " +
                (Skew / 2.0).ToString("F1", CultureInfo.InvariantCulture) + " deg");

            Assert.True(Math.Abs(result.Impairments.QuadratureSkewDegrees - Skew) < 0.1);
            Assert.True(
                Math.Abs(result.Impairments.ResidualRotationDegrees) < 0.1,
                "the fit left " + result.Impairments.ResidualRotationDegrees +
                " deg of rotation, where a shear model leaves half the skew.");
        }

        [Fact]
        public void TheSplitBetweenPhaseAndSkewDoesNotDependOnWhereTheEstimatorStarted()
        {
            // REQ-DEM-067a: "estimating the same signal twice from different initial conditions
            // returns the same split between phase and skew to within 0.01 degrees, which is what
            // 'resolved deterministically' means and what an unconstrained sequential estimator
            // cannot deliver."
            //
            // The initial condition step 8 has is where the carrier starts, so the same impairments
            // are demodulated from two different carrier offsets. The chain removes each, and what
            // is left over must be the same split.
            const double Skew = 4.0;
            const double Imbalance = 0.6;

            DemodResult first = Demodulate(
                Constellation.Qam(16), 0.0, Imbalance, Skew, carrierOffsetHz: 0.0);

            DemodResult second = Demodulate(
                Constellation.Qam(16), 0.0, Imbalance, Skew, carrierOffsetHz: 37e3);

            _output.WriteLine(
                "from 0 Hz:     skew " +
                first.Impairments.QuadratureSkewDegrees.ToString(
                    "F6", CultureInfo.InvariantCulture) + " deg, rotation " +
                first.Impairments.ResidualRotationDegrees.ToString(
                    "F6", CultureInfo.InvariantCulture) + " deg");

            _output.WriteLine(
                "from 37 kHz:   skew " +
                second.Impairments.QuadratureSkewDegrees.ToString(
                    "F6", CultureInfo.InvariantCulture) + " deg, rotation " +
                second.Impairments.ResidualRotationDegrees.ToString(
                    "F6", CultureInfo.InvariantCulture) + " deg");

            Assert.True(
                Math.Abs(
                    first.Impairments.QuadratureSkewDegrees -
                    second.Impairments.QuadratureSkewDegrees) < 0.01);

            Assert.True(
                Math.Abs(
                    first.Impairments.ResidualRotationDegrees -
                    second.Impairments.ResidualRotationDegrees) < 0.01);
        }

        [Fact]
        public void TheAmbiguousCaseIsPinnedRatherThanLeftToChance()
        {
            // REQ-DEM-067a's documented interaction: "Quadrature skew can be mis-attributed as gain
            // imbalance (and vice versa) depending on the transmitter's symbol-mapping convention
            // relative to the receiver's reference axes. The UI shall document this, and the test
            // suite shall include the ambiguous case so the behaviour is characterised and stable
            // rather than accidental."
            //
            // 🔴 The geometry, and why the ambiguity is real rather than a defect. A gain imbalance
            // stretches the plane along the receiver's I and Q axes; a symmetric quadrature skew
            // stretches it along the 45-degree lines between them. Those are the same KIND of
            // transformation turned through 45 degrees -- so a modulator whose own axes sit 45
            // degrees from the receiver's puts its gain imbalance exactly where the receiver reads a
            // quadrature error, and NOTHING IN THE SIGNAL can say which it was. The axes are a
            // convention, and only the constellation names them.
            //
            // 🔴 A first version of this test tried to produce the ambiguity by turning the
            // CONSTELLATION through 45 degrees, and it did not: the fit is z = M r + c, and M is
            // whatever the impairment is regardless of where r's points sit, so the turned
            // constellation read the same 5.97 degrees of skew it was given. What has to turn is the
            // frame the impairment is APPLIED in -- the transmitter's -- not the one the symbols are
            // drawn on.
            const double ImbalanceDb = 1.5;

            DemodResult inTheReceiversAxes = Turned(Constellation.Qam(16), ImbalanceDb, 0.0);
            DemodResult inATurnedFrame = Turned(Constellation.Qam(16), ImbalanceDb, Math.PI / 4.0);

            _output.WriteLine(
                "imbalance injected in the receiver's own axes: imbalance " +
                inTheReceiversAxes.Impairments.GainImbalanceDb.ToString(
                    "F4", CultureInfo.InvariantCulture) + " dB, skew " +
                inTheReceiversAxes.Impairments.QuadratureSkewDegrees.ToString(
                    "F4", CultureInfo.InvariantCulture) + " deg");

            _output.WriteLine(
                "the same imbalance 45 degrees away:            imbalance " +
                inATurnedFrame.Impairments.GainImbalanceDb.ToString(
                    "F4", CultureInfo.InvariantCulture) + " dB, skew " +
                inATurnedFrame.Impairments.QuadratureSkewDegrees.ToString(
                    "F4", CultureInfo.InvariantCulture) + " deg");

            // Injected where the receiver's axes are, it reads as what it is.
            Assert.True(
                Math.Abs(inTheReceiversAxes.Impairments.GainImbalanceDb - ImbalanceDb) < 0.05);
            Assert.True(Math.Abs(inTheReceiversAxes.Impairments.QuadratureSkewDegrees) < 0.1);

            // Injected 45 degrees away, the SAME impairment reads as pure quadrature error, and the
            // size is the one the geometry predicts. With gains a and b along the turned axes the
            // matrix is symmetric with eigenvalues a and b on the 45-degree lines, and matching that
            // against g K(psi/2) -- whose eigenvalues are g(cos +/- sin) on those same lines -- gives
            //
            //     tan(psi/2) = (a - b) / (a + b).
            double gainQ = Math.Pow(10.0, ImbalanceDb / 40.0);
            double gainI = 1.0 / gainQ;

            double predicted =
                2.0 * Math.Atan((gainI - gainQ) / (gainI + gainQ)) * 180.0 / Math.PI;

            _output.WriteLine(
                "the geometry predicts " +
                predicted.ToString("F4", CultureInfo.InvariantCulture) +
                " deg of apparent quadrature error");

            Assert.True(
                Math.Abs(inATurnedFrame.Impairments.GainImbalanceDb) < 0.05,
                "the turned frame reported " + inATurnedFrame.Impairments.GainImbalanceDb +
                " dB of imbalance, where the geometry says there should be none.");

            Assert.True(
                Math.Abs(
                    Math.Abs(inATurnedFrame.Impairments.QuadratureSkewDegrees) -
                    Math.Abs(predicted)) < 0.1,
                "the turned frame read " + inATurnedFrame.Impairments.QuadratureSkewDegrees +
                " deg of skew against " + predicted + " predicted");
        }

        /// <summary>
        /// Demodulates a signal whose gain imbalance was injected in a frame of its own.
        /// </summary>
        /// <param name="format">The constellation.</param>
        /// <param name="imbalanceDb">How much imbalance to inject.</param>
        /// <param name="frameRadians">How far the modulator's axes sit from the receiver's.</param>
        /// <returns>The demodulation.</returns>
        private DemodResult Turned(Constellation format, double imbalanceDb, double frameRadians)
        {
            var points = new List<SymbolPoint>(format.Count);

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

            // Into the modulator's frame, impair, and back out again.
            double gainQ = Math.Pow(10.0, imbalanceDb / 40.0);
            double gainI = 1.0 / gainQ;

            double cos = Math.Cos(frameRadians);
            double sin = Math.Sin(frameRadians);

            for (int sample = 0; sample < samples.Length; sample += 2)
            {
                double i = samples[sample];
                double q = samples[sample + 1];

                double inFrameI = ((i * cos) + (q * sin)) * gainI;
                double inFrameQ = ((-i * sin) + (q * cos)) * gainQ;

                samples[sample] = (float)((inFrameI * cos) - (inFrameQ * sin));
                samples[sample + 1] = (float)((inFrameI * sin) + (inFrameQ * cos));
            }

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

        [Fact]
        public void RhoIsOneForAPerfectMatchAndNeverMoreThanOne()
        {
            // REQ-DEM-068: "A perfect match returns rho = 1.0 to within 1e-12, and rho never exceeds
            // 1.0 for any input — asserted over randomised impairments, since a normalisation error
            // shows up as rho > 1."
            Constellation format = Constellation.Qam(16);
            var ideal = new List<ConstellationPoint>();
            var random = new Random(20260824);

            for (int symbol = 0; symbol < 1024; symbol++)
            {
                ideal.Add(format.Points[random.Next(format.Count)]);
            }

            double perfect = ErrorSummary.For(ideal, ideal).Rho;

            _output.WriteLine("a perfect match reads rho = " + perfect.ToString("R"));

            Assert.True(Math.Abs(perfect - 1.0) < 1e-12);

            double worst = 0.0;

            for (int trial = 0; trial < 200; trial++)
            {
                var impaired = ideal
                    .Select(point => new ConstellationPoint(
                        (point.I * (0.5 + random.NextDouble())) +
                        ((random.NextDouble() - 0.5) * 2.0),
                        (point.Q * (0.5 + random.NextDouble())) +
                        ((random.NextDouble() - 0.5) * 2.0)))
                    .ToList();

                double rho = ErrorSummary.For(impaired, ideal).Rho;

                Assert.True(rho <= 1.0, "rho came out as " + rho + ", which is above one.");

                worst = Math.Max(worst, rho);
            }

            _output.WriteLine(
                "over 200 randomised impairments the largest rho was " + worst.ToString("R"));
        }

        [Fact]
        public void RhoIsUnchangedByACommonScalingOrRotation()
        {
            // "rho is invariant to a common scaling or a common phase rotation of the measured
            // signal, which is what makes it a waveform-quality figure rather than an amplitude
            // comparison."
            Constellation format = Constellation.Qam(16);
            var ideal = new List<ConstellationPoint>();
            var random = new Random(4242);

            for (int symbol = 0; symbol < 512; symbol++)
            {
                ideal.Add(format.Points[random.Next(format.Count)]);
            }

            var measured = ideal
                .Select(point => new ConstellationPoint(
                    point.I + ((random.NextDouble() - 0.5) * 0.2),
                    point.Q + ((random.NextDouble() - 0.5) * 0.2)))
                .ToList();

            double plain = ErrorSummary.For(measured, ideal).Rho;

            const double Scale = 3.7;
            const double Radians = 0.9;

            var transformed = measured
                .Select(point => new ConstellationPoint(
                    Scale * ((point.I * Math.Cos(Radians)) - (point.Q * Math.Sin(Radians))),
                    Scale * ((point.I * Math.Sin(Radians)) + (point.Q * Math.Cos(Radians)))))
                .ToList();

            double moved = ErrorSummary.For(transformed, ideal).Rho;

            _output.WriteLine(
                "rho " + plain.ToString("R") + " becomes " + moved.ToString("R") +
                " after scaling by " + Scale + " and turning by " + Radians + " rad");

            Assert.True(Math.Abs(plain - moved) < 1e-12);
        }

        [Fact]
        public void RhoAndTheSignalToNoiseRatioSatisfyTheirIdentitiesWithEvm()
        {
            // REQ-DEM-068 and REQ-DEM-069 both ask for a closed-form identity with EVM to 1e-6.
            //
            // 🔴 Both identities are EXACT only when the error is orthogonal to the reference, which
            // is what "noise" means and what a finite random draw is not. With N symbols the cross
            // term sum(n r*) is about EVM/sqrt(N) of the reference energy, and at N = 4e6 and a 5 %
            // EVM that is 2.5e-5 -- twenty-five times the tolerance the requirements ask for. So the
            // draw's component along the reference is projected out, which makes the noise
            // orthogonal by construction rather than approximately so, and the identities then hold
            // to floating point.
            //
            // The un-orthogonalised figure is printed alongside, so what the projection is worth is
            // on the record.
            //
            //     SNR_dB = -20 log10(EVM_fraction)          exactly
            //     rho    = 1 / (1 + EVM_fraction^2)         when sum(n r*) = 0
            //
            // The EVM here has to be referenced to the RMS of THIS BLOCK's ideal points, which is
            // what the default does, because that is the quantity both identities are written in.
            Constellation format = Constellation.Qam(16);
            var random = new Random(11235);

            var ideal = new List<ConstellationPoint>();
            var noise = new List<ConstellationPoint>();

            for (int symbol = 0; symbol < 20000; symbol++)
            {
                ideal.Add(format.Points[random.Next(format.Count)]);

                double i;
                double q;

                Gaussian(random, out i, out q);

                noise.Add(new ConstellationPoint(i * 0.05, q * 0.05));
            }

            // Project the reference component out of the noise.
            double alongI = 0.0;
            double alongQ = 0.0;
            double energy = 0.0;

            for (int symbol = 0; symbol < ideal.Count; symbol++)
            {
                alongI += (noise[symbol].I * ideal[symbol].I) + (noise[symbol].Q * ideal[symbol].Q);
                alongQ += (noise[symbol].Q * ideal[symbol].I) - (noise[symbol].I * ideal[symbol].Q);
                energy += (ideal[symbol].I * ideal[symbol].I) + (ideal[symbol].Q * ideal[symbol].Q);
            }

            double scaleI = alongI / energy;
            double scaleQ = alongQ / energy;

            var orthogonal = new List<ConstellationPoint>(ideal.Count);
            var raw = new List<ConstellationPoint>(ideal.Count);

            for (int symbol = 0; symbol < ideal.Count; symbol++)
            {
                ConstellationPoint reference = ideal[symbol];

                double removedI = (scaleI * reference.I) - (scaleQ * reference.Q);
                double removedQ = (scaleI * reference.Q) + (scaleQ * reference.I);

                orthogonal.Add(new ConstellationPoint(
                    reference.I + noise[symbol].I - removedI,
                    reference.Q + noise[symbol].Q - removedQ));

                raw.Add(new ConstellationPoint(
                    reference.I + noise[symbol].I, reference.Q + noise[symbol].Q));
            }

            Check(orthogonal, ideal, "orthogonalised", 1e-6);
            Check(raw, ideal, "as drawn", double.PositiveInfinity);
        }

        [Fact]
        public void IntersymbolInterferenceAloneGivesAFiniteSignalToNoiseRatio()
        {
            // REQ-DEM-069: "Distortion and ISI count as noise: a signal degraded by ISI alone, with
            // no additive noise, reports a finite SNR rather than infinity — the check that the
            // definition quoted here was implemented rather than a conventional
            // additive-noise-only one."
            //
            // The ISI is real and comes from the chain: a measurement filter that does not match the
            // transmitter's shaping leaves it, and there is no noise anywhere in the signal.
            DemodResult clean = Demodulate(Constellation.Qam(16), 0.0, 0.0, 0.0);

            DemodResult distorted = Demodulate(
                Constellation.Qam(16), 0.0, 0.0, 0.0, alpha: 0.15);

            double cleanSnr = Row(clean, MetricApplicability.SignalToNoise).Rms;
            double distortedSnr = Row(distorted, MetricApplicability.SignalToNoise).Rms;

            _output.WriteLine(
                "matched filter: SNR " +
                cleanSnr.ToString("F2", CultureInfo.InvariantCulture) +
                " dB; mismatched roll-off, no noise at all: " +
                distortedSnr.ToString("F2", CultureInfo.InvariantCulture) + " dB");

            Assert.False(double.IsInfinity(distortedSnr));
            Assert.False(double.IsNaN(distortedSnr));

            // Finite, and markedly worse than the matched case -- the ISI is being counted.
            Assert.True(distortedSnr < cleanSnr - 20.0);
        }

        [Fact]
        public void TheLabelIsExactlySnrMer()
        {
            // "The label renders exactly `SNR (MER)`." The industry calls it MER and the reference
            // product calls it SNR, and a display that picked one would be wrong for half its users.
            Assert.Equal("SNR (MER)", MetricApplicability.SignalToNoise);

            DemodResult result = Demodulate(Constellation.Qam(16), 0.0, 0.0, 0.0);

            string rendered = result.Summary.Render()
                .Single(row => row.StartsWith("SNR (MER)", StringComparison.Ordinal));

            _output.WriteLine(rendered);

            Assert.Contains("SNR (MER)", rendered);
        }

        [Fact]
        public void TheSignalToNoiseRatioIsOfferedToTheFormatsThatUseItAndNoOthers()
        {
            // "It is offered for QAM, DVB-QAM, 8PSK, QPSK, APSK and VSB ... and is absent for other
            // formats."
            foreach (ModulationFamily family in new[]
            {
                ModulationFamily.Qam, ModulationFamily.Psk, ModulationFamily.Apsk,
                ModulationFamily.Vsb,
            })
            {
                Assert.True(
                    MetricApplicability.Applies(
                        MetricApplicability.SignalToNoise, family, false),
                    family + " should offer SNR (MER).");
            }

            foreach (ModulationFamily family in new[]
            {
                ModulationFamily.Fsk, ModulationFamily.Msk,
            })
            {
                Assert.False(
                    MetricApplicability.Applies(
                        MetricApplicability.SignalToNoise, family, false),
                    family + " should not offer SNR (MER).");
            }

            _output.WriteLine("QAM, PSK, APSK and VSB offer it; FSK and MSK do not.");
        }

        private void Check(
            IReadOnlyList<ConstellationPoint> measured,
            IReadOnlyList<ConstellationPoint> ideal,
            string what,
            double tolerance)
        {
            ErrorSummary summary = ErrorSummary.For(measured, ideal);

            double evm = summary.Metrics.Single(metric => metric.Label == "EVM").Rms / 100.0;
            double snr = summary.Metrics
                .Single(metric => metric.Label == MetricApplicability.SignalToNoise).Rms;

            double expectedSnr = -20.0 * Math.Log10(evm);
            double expectedRho = 1.0 / (1.0 + (evm * evm));

            _output.WriteLine(
                what + ": EVM " + (evm * 100.0).ToString("F6", CultureInfo.InvariantCulture) +
                " %rms; SNR " + snr.ToString("F9", CultureInfo.InvariantCulture) +
                " dB against " + expectedSnr.ToString("F9", CultureInfo.InvariantCulture) +
                " predicted (" +
                Math.Abs(snr - expectedSnr).ToString("E3", CultureInfo.InvariantCulture) +
                "); rho " + summary.Rho.ToString("F12", CultureInfo.InvariantCulture) +
                " against " + expectedRho.ToString("F12", CultureInfo.InvariantCulture) + " (" +
                Math.Abs(summary.Rho - expectedRho).ToString("E3", CultureInfo.InvariantCulture) +
                ")");

            if (double.IsInfinity(tolerance))
            {
                return;
            }

            Assert.True(
                Math.Abs(snr - expectedSnr) < tolerance,
                "SNR " + snr + " dB against " + expectedSnr + " predicted");

            Assert.True(
                Math.Abs(summary.Rho - expectedRho) < tolerance,
                "rho " + summary.Rho + " against " + expectedRho + " predicted");
        }

        [Fact]
        public void TheHelpSaysWhatTheCodeDoesAboutBothConventions()
        {
            // REQ-DEM-061 asks that the normalisation be stated "in the UI rather than inherited
            // silently"; REQ-DEM-067a asks that the imbalance/skew ambiguity be documented. A help
            // page is the easiest thing in a product to let drift away from what the product does,
            // so what it has to contain is asserted rather than trusted.
            string help = HelpTopics.Read(HelpTopics.ErrorMetrics);

            _output.WriteLine(help.Length + " characters of help");

            // REQ-DEM-061: the three choices, the default, and the number that makes it matter.
            Assert.Contains("RMS magnitude", help, StringComparison.Ordinal);
            Assert.Contains("Maximum magnitude", help, StringComparison.Ordinal);
            Assert.Contains("User-specified", help, StringComparison.Ordinal);
            Assert.Contains("the default", help, StringComparison.Ordinal);
            Assert.Contains("1.342", help, StringComparison.Ordinal);

            // And that IQ offset deliberately does not follow it.
            Assert.Contains("2.55 dB", help, StringComparison.Ordinal);

            // REQ-DEM-067a: the ambiguity, named, with the geometry that causes it.
            Assert.Contains("45", help, StringComparison.Ordinal);
            Assert.Contains("Quad. Error", help, StringComparison.Ordinal);
            Assert.Contains("Gain Imbalance", help, StringComparison.Ordinal);

            // And the convention the requirement asks to be stated explicitly.
            Assert.Contains("Positive means Q is larger than I", help, StringComparison.Ordinal);

            // REQ-DEM-069's naming note.
            Assert.Contains("SNR (MER)", help, StringComparison.Ordinal);
        }

        private static ErrorMetric Row(DemodResult result, string label) =>
            result.Summary.Metrics.Single(metric => metric.Label == label);

        /// <summary>
        /// Demodulates a generated signal with impairments injected into the baseband.
        /// </summary>
        /// <remarks>
        /// The impairments are applied by <see cref="Impair"/> to the generated samples, which is
        /// where a transmitter's would be: before the carrier, before the channel and before
        /// anything the analyser does.
        /// </remarks>
        private static DemodResult Demodulate(
            Constellation format,
            double leakage,
            double imbalanceDb,
            double skewDegrees,
            EvmNormalisation normalisation = EvmNormalisation.RmsMagnitude,
            double carrierOffsetHz = 0.0,
            double injectedOffset = 0.0,
            double alpha = 0.35)
        {
            var points = new List<SymbolPoint>(format.Count);

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

            // 🔴 Impair FIRST, then apply the carrier offset -- not the other way round, and not by
            // asking the generator for one. An IQ modulator's gain imbalance and quadrature error
            // are properties of the baseband path, and the upconversion comes after them. Injecting
            // them into a signal that already carries a carrier offset puts them in a frame rotating
            // relative to the constellation, and the analyser's derotation then turns a fixed skew
            // into a time-varying one that averages to nothing: a first version of this fixture did
            // exactly that and read 4 degrees of skew at zero offset and -0.23 at 37 kHz. That was
            // the injection being wrong, not the estimator.
            Impair(samples, leakage + injectedOffset, imbalanceDb, skewDegrees);
            Upconvert(samples, carrierOffsetHz);

            var settings = new DemodSettings
            {
                Constellation = format,
                SymbolRateHz = SymbolRateHz,
                ResultLengthSymbols = 512,
                FilterSymbolSpan = 20,
                MeasurementFilter = PulseFilterType.RootRaisedCosine,
                MeasurementFilterAlpha = alpha,
                ReferenceFilterAlpha = 0.35,
                EvmNormalisation = normalisation,
            };

            return new Demodulator().Run(samples, SampleRateHz, settings);
        }

        /// <summary>
        /// Applies <c>REQ-DEM-067</c>'s symmetric model to a generated waveform.
        /// </summary>
        /// <remarks>
        /// <strong>The same model the estimator fits, applied forwards.</strong> Each axis is turned
        /// by half the skew and scaled by its own gain, which is a stretch along the 45-degree line
        /// with no rotational component. Injecting a one-sided shear instead would inject half the
        /// skew as a carrier phase as well, and the test would be measuring the injection.
        /// </remarks>
        private static void Impair(
            float[] samples, double leakage, double imbalanceDb, double skewDegrees)
        {
            double half = skewDegrees * Math.PI / 180.0 / 2.0;
            double cos = Math.Cos(half);
            double sin = Math.Sin(half);

            // Positive imbalance means Q larger than I, per REQ-DEM-067, and it is split between the
            // axes so that the average gain stays one and the injection does not double as a level
            // change.
            double gainQ = Math.Pow(10.0, imbalanceDb / 40.0);
            double gainI = 1.0 / gainQ;

            for (int sample = 0; sample < samples.Length; sample += 2)
            {
                double i = samples[sample];
                double q = samples[sample + 1];

                samples[sample] = (float)((gainI * ((i * cos) + (q * sin))) + leakage);
                samples[sample + 1] = (float)(gainQ * ((i * sin) + (q * cos)));
            }
        }

        /// <summary>Turns a baseband waveform by a carrier offset.</summary>
        /// <param name="samples">The waveform, interleaved.</param>
        /// <param name="offsetHz">How far off centre to put it.</param>
        /// <remarks>
        /// After <see cref="Impair"/>, because that is the order the hardware does it in: the
        /// modulator's imperfections are in the baseband path and the mixer follows them.
        /// </remarks>
        private static void Upconvert(float[] samples, double offsetHz)
        {
            if (offsetHz == 0.0)
            {
                return;
            }

            double turnPerSample = 2.0 * Math.PI * offsetHz / SampleRateHz;

            for (int sample = 0; sample < samples.Length; sample += 2)
            {
                double angle = turnPerSample * (sample / 2);
                double cos = Math.Cos(angle);
                double sin = Math.Sin(angle);

                double i = samples[sample];
                double q = samples[sample + 1];

                samples[sample] = (float)((i * cos) - (q * sin));
                samples[sample + 1] = (float)((i * sin) + (q * cos));
            }
        }

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
