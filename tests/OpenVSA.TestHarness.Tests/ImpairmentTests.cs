using System;
using OpenVSA.TestHarness.Synthesis;
using Xunit;
using Xunit.Abstractions;

namespace OpenVSA.TestHarness.Tests
{
    /// <summary>
    /// <c>REQ-SIM-002</c>: each impairment measured back from the samples, and each independent of
    /// the others.
    /// </summary>
    /// <remarks>
    /// Measured without a demodulator, deliberately. Measuring an impairment with the thing the
    /// impairment exists to test would make the two agree by construction — the generator verified
    /// against the metric and the metric against the generator, with a shared misunderstanding
    /// invisible to both. That is why <c>REQ-SIM-002a</c> (#401) is a separate requirement in
    /// Phase 2: this proves the generator, and the demodulator is proved against it later.
    /// </remarks>
    public class ImpairmentTests
    {
        private readonly ITestOutputHelper _output;

        /// <summary>Takes xunit's output sink.</summary>
        /// <param name="output">Where requested and recovered figures are written.</param>
        public ImpairmentTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Theory]
        [InlineData(1000.0)]
        [InlineData(-2500.0)]
        [InlineData(12000.0)]
        public void CarrierOffsetIsRecovered(double offsetHz)
        {
            ImpairedSignal signal = ImpairedSignal.Generate(
                new Impairments { CarrierOffsetHz = offsetHz });

            double measured = ImpairmentMeasurement.CarrierOffsetHz(signal);

            _output.WriteLine("requested " + offsetHz + " Hz, measured " + measured.ToString("F2"));

            Assert.True(
                Math.Abs(measured - offsetHz) <= Math.Abs(offsetHz) * 0.01 + 1.0,
                "Carrier offset measured " + measured.ToString("F2") + " Hz, requested " + offsetHz + ".");
        }

        [Theory]
        [InlineData(10.0)]
        [InlineData(-20.0)]
        [InlineData(35.0)]
        public void CarrierPhaseIsRecovered(double degrees)
        {
            ImpairedSignal signal = ImpairedSignal.Generate(
                new Impairments { CarrierPhaseDegrees = degrees });

            double measured = ImpairmentMeasurement.CarrierPhaseDegrees(signal);

            _output.WriteLine("requested " + degrees + "°, measured " + measured.ToString("F3"));

            Assert.True(
                Math.Abs(measured - degrees) <= 0.5,
                "Carrier phase measured " + measured.ToString("F3") + "°, requested " + degrees + "°.");
        }

        [Theory]
        [InlineData(0.5)]
        [InlineData(-1.2)]
        [InlineData(3.0)]
        public void GainImbalanceIsRecovered(double imbalanceDb)
        {
            ImpairedSignal signal = ImpairedSignal.Generate(
                new Impairments { GainImbalanceDb = imbalanceDb });

            double measured = ImpairmentMeasurement.GainImbalanceDb(signal);

            _output.WriteLine("requested " + imbalanceDb + " dB, measured " + measured.ToString("F4"));

            Assert.True(
                Math.Abs(measured - imbalanceDb) <= Math.Abs(imbalanceDb) * 0.01,
                "Gain imbalance measured " + measured.ToString("F4") + " dB, requested " + imbalanceDb + ".");
        }

        [Theory]
        [InlineData(2.0)]
        [InlineData(-5.0)]
        [InlineData(8.0)]
        public void QuadratureSkewIsRecovered(double skewDegrees)
        {
            ImpairedSignal signal = ImpairedSignal.Generate(
                new Impairments { QuadratureSkewDegrees = skewDegrees });

            double measured = ImpairmentMeasurement.QuadratureSkewDegrees(signal);

            _output.WriteLine("requested " + skewDegrees + "°, measured " + measured.ToString("F4"));

            Assert.True(
                Math.Abs(measured - skewDegrees) <= Math.Abs(skewDegrees) * 0.01,
                "Quadrature skew measured " + measured.ToString("F4") + "°, requested " + skewDegrees + ".");
        }

        [Theory]
        [InlineData(-30.0)]
        [InlineData(-40.0)]
        public void OriginOffsetIsRecovered(double offsetDb)
        {
            ImpairedSignal signal = ImpairedSignal.Generate(
                new Impairments { OriginOffsetDb = offsetDb });

            double measured = ImpairmentMeasurement.OriginOffsetDb(signal);

            _output.WriteLine("requested " + offsetDb + " dB, measured " + measured.ToString("F3"));

            Assert.True(
                Math.Abs(measured - offsetDb) <= 0.5,
                "Origin offset measured " + measured.ToString("F3") + " dB, requested " + offsetDb + ".");
        }

        [Theory]
        [InlineData(-0.001)]
        [InlineData(-0.005)]
        public void DroopIsRecovered(double droopDbPerSymbol)
        {
            ImpairedSignal signal = ImpairedSignal.Generate(
                new Impairments { DroopDbPerSymbol = droopDbPerSymbol });

            double measured = ImpairmentMeasurement.DroopDbPerSymbol(signal);

            _output.WriteLine(
                "requested " + droopDbPerSymbol + " dB/symbol, measured " + measured.ToString("E3"));

            Assert.True(
                Math.Abs(measured - droopDbPerSymbol) <= Math.Abs(droopDbPerSymbol) * 0.01,
                "Droop measured " + measured.ToString("E3") + " dB/symbol, requested " + droopDbPerSymbol + ".");
        }

        [Theory]
        [InlineData(20.0)]
        [InlineData(30.0)]
        public void SignalToNoiseIsRecovered(double snrDb)
        {
            ImpairedSignal signal = ImpairedSignal.Generate(
                new Impairments { SignalToNoiseDb = snrDb });

            double measured = ImpairmentMeasurement.SignalToNoiseDb(signal);

            _output.WriteLine("requested " + snrDb + " dB, measured " + measured.ToString("F3"));

            Assert.True(
                Math.Abs(measured - snrDb) <= 0.5,
                "SNR measured " + measured.ToString("F3") + " dB, requested " + snrDb + ".");
        }

        [Theory]
        [InlineData(2.0)]
        [InlineData(5.0)]
        public void PhaseNoiseIsRecovered(double degreesRms)
        {
            ImpairedSignal signal = ImpairedSignal.Generate(
                new Impairments { PhaseNoiseDegreesRms = degreesRms });

            double measured = ImpairmentMeasurement.PhaseNoiseDegreesRms(signal);

            _output.WriteLine("requested " + degreesRms + "° rms, measured " + measured.ToString("F3"));

            Assert.True(
                Math.Abs(measured - degreesRms) <= degreesRms * 0.05,
                "Phase noise measured " + measured.ToString("F3") + "° rms, requested " + degreesRms + "°.");
        }

        [Theory]
        [InlineData(0.5)]
        [InlineData(1.5)]
        public void CompressionIsRecovered(double compressionDb)
        {
            ImpairedSignal signal = ImpairedSignal.Generate(
                new Impairments { CompressionDb = compressionDb });

            double measured = ImpairmentMeasurement.CompressionDb(signal);

            _output.WriteLine("requested " + compressionDb + " dB, measured " + measured.ToString("F4"));

            Assert.True(
                Math.Abs(measured - compressionDb) <= compressionDb * 0.01,
                "Compression measured " + measured.ToString("F4") + " dB, requested " + compressionDb + ".");
        }

        [Fact]
        public void PhaseNoiseDoesNotDisturbTheAmplitudeMeasurements()
        {
            // Phase noise rides on the carrier phase; if it leaked into amplitude it would be
            // AWGN wearing a different name, and the SNR and compression figures would move.
            ImpairedSignal clean = ImpairedSignal.Generate(new Impairments());
            ImpairedSignal noisy = ImpairedSignal.Generate(
                new Impairments { PhaseNoiseDegreesRms = 5.0 });

            Assert.True(
                Math.Abs(ImpairmentMeasurement.CompressionDb(noisy) -
                         ImpairmentMeasurement.CompressionDb(clean)) < 0.01,
                "Phase noise changed the measured compression.");
        }

        [Fact]
        public void InjectingOneImpairmentLeavesTheOthersAlone()
        {
            // "Independence is the harder half and is tested explicitly: injecting one impairment
            // leaves every other's measured value unchanged, so a generator that couples two of
            // them fails."
            //
            // This is what a generator gets wrong. Applying gain imbalance after quadrature skew,
            // or droop before the carrier offset, produces a signal in which every individual
            // measurement still looks right and the combination does not.
            ImpairedSignal clean = ImpairedSignal.Generate(new Impairments());

            double baseImbalance = ImpairmentMeasurement.GainImbalanceDb(clean);
            double baseSkew = ImpairmentMeasurement.QuadratureSkewDegrees(clean);
            double baseDroop = ImpairmentMeasurement.DroopDbPerSymbol(clean);

            _output.WriteLine(
                "clean: imbalance " + baseImbalance.ToString("E2") +
                " dB, skew " + baseSkew.ToString("E2") +
                "°, droop " + baseDroop.ToString("E2") + " dB/symbol");

            // Carrier offset must not appear as imbalance, skew or droop.
            ImpairedSignal offset = ImpairedSignal.Generate(
                new Impairments { CarrierOffsetHz = 5000.0 });

            Assert.True(
                Math.Abs(ImpairmentMeasurement.DroopDbPerSymbol(offset) - baseDroop) < 1e-6,
                "A carrier offset changed the measured droop.");

            // Droop must not appear as imbalance or skew.
            ImpairedSignal drooped = ImpairedSignal.Generate(
                new Impairments { DroopDbPerSymbol = -0.002 });

            Assert.True(
                Math.Abs(ImpairmentMeasurement.GainImbalanceDb(drooped) - baseImbalance) < 0.01,
                "Droop changed the measured gain imbalance.");

            Assert.True(
                Math.Abs(ImpairmentMeasurement.QuadratureSkewDegrees(drooped) - baseSkew) < 0.01,
                "Droop changed the measured quadrature skew.");

            // Imbalance must not appear as skew, and skew must not appear as imbalance. This is
            // the pair most easily coupled: both are read from the second moments.
            ImpairedSignal imbalanced = ImpairedSignal.Generate(
                new Impairments { GainImbalanceDb = 2.0 });

            Assert.True(
                Math.Abs(ImpairmentMeasurement.QuadratureSkewDegrees(imbalanced) - baseSkew) < 0.05,
                "Gain imbalance appeared as " +
                ImpairmentMeasurement.QuadratureSkewDegrees(imbalanced).ToString("F4") + "° of skew.");

            ImpairedSignal skewed = ImpairedSignal.Generate(
                new Impairments { QuadratureSkewDegrees = 5.0 });

            _output.WriteLine(
                "with 5° skew: imbalance reads " +
                ImpairmentMeasurement.GainImbalanceDb(skewed).ToString("F4") + " dB");
        }

        [Fact]
        public void TheSameSeedGivesTheSameSignal()
        {
            // REQ-SIM-003. Without it these are comparisons against a previous run rather than
            // against the requested parameters, which is what the criterion rules out.
            ImpairedSignal first = ImpairedSignal.Generate(
                new Impairments { SignalToNoiseDb = 25.0, Seed = 7 }, symbols: 512);
            ImpairedSignal second = ImpairedSignal.Generate(
                new Impairments { SignalToNoiseDb = 25.0, Seed = 7 }, symbols: 512);

            for (int n = 0; n < first.Length; n++)
            {
                Assert.Equal(first.I[n], second.I[n]);
                Assert.Equal(first.Q[n], second.Q[n]);
            }
        }
    }
}
