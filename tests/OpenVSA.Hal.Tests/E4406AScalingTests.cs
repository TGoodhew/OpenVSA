using System;
using OpenVSA.Dsp.Spectrum;
using Xunit;
using Xunit.Abstractions;

namespace OpenVSA.Hal.Tests
{
    /// <summary>
    /// <c>REQ-E44-002a</c>: the instrument's I/Q are volts, peak-referenced.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The requirement's method, applied to a capture from this bench: take the raw I/Q, compute
    /// the instrument's own four scalars from them, and check they agree. If the scaling
    /// convention were wrong the reconstruction would not match, and no other test in the system
    /// would reveal it — a spectrum computed from mis-scaled samples looks entirely reasonable and
    /// is uniformly wrong.
    /// </para>
    /// <para>
    /// <strong>The wrong answer is pinned as well as the right one.</strong> Reading the values as
    /// RMS rather than peak gives a figure exactly 3.01 dB high. That is close enough to look
    /// plausible in isolation and is the specific trap this requirement exists to prevent, so the
    /// test asserts the erroneous value too — a change that silently switched conventions would
    /// otherwise only break the assertion it was meant to satisfy.
    /// </para>
    /// </remarks>
    public class E4406AScalingTests
    {
        /// <summary>Reference impedance, per the requirement.</summary>
        private const double Ohms = 50.0;

        /// <summary>
        /// A capture taken from the E4406A at GPIB 17, firmware A.08.10, on 2026-07-25:
        /// 100 kHz information bandwidth at 1.002 GHz, sixteen samples, interleaved I,Q in volts.
        /// </summary>
        private static readonly double[] Capture =
        {
            -2.17928868E-005, +3.50415070E-006, -2.29216666E-005, +3.46365305E-006,
            -2.21374849E-005, +4.14254083E-006, -1.94985112E-005, +5.24849468E-006,
            -1.54045675E-005, +6.37948343E-006, -1.04947804E-005, +7.14893867E-006,
            -5.49000790E-006, +7.30872028E-006, -1.04042129E-006, +6.82348487E-006,
            +2.38199779E-006, +5.86774047E-006, +4.53794510E-006, +4.75368710E-006,
            +5.40606733E-006, +3.81414175E-006, +5.13215599E-006, +3.29061766E-006,
            +3.97686865E-006, +3.25306530E-006, +2.26050486E-006, +3.59545448E-006,
            +3.17354266E-007, +4.08878941E-006, -1.54185576E-006, +4.47314887E-006,
        };

        /// <summary>The scalars the instrument itself reported for that same capture.</summary>
        private const double ReportedMeanDbm = -87.6354178;
        private const double ReportedPeakToMeanDb = 4.93839189;
        private const double ReportedMaximumDbm = -82.6970259;
        private const double ReportedMinimumDbm = -97.7420209;
        private const double ReportedSampleIntervalSeconds = 7.33333333E-007;

        private readonly ITestOutputHelper _output;

        public E4406AScalingTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void PeakReferencedVoltsReproduceTheInstrumentsOwnScalars()
        {
            double mean = MeanPowerDbm(Capture);
            double maximum = ExtremePowerDbm(Capture, highest: true);
            double minimum = ExtremePowerDbm(Capture, highest: false);
            double peakToMean = maximum - mean;

            _output.WriteLine("mean        " + mean.ToString("F4") + " against " + ReportedMeanDbm);
            _output.WriteLine("maximum     " + maximum.ToString("F4") + " against " + ReportedMaximumDbm);
            _output.WriteLine("minimum     " + minimum.ToString("F4") + " against " + ReportedMinimumDbm);
            _output.WriteLine("peak-to-mean " + peakToMean.ToString("F4") + " against " + ReportedPeakToMeanDb);

            Assert.Equal(ReportedMeanDbm, mean, 3);
            Assert.Equal(ReportedMaximumDbm, maximum, 3);
            Assert.Equal(ReportedMinimumDbm, minimum, 3);
            Assert.Equal(ReportedPeakToMeanDb, peakToMean, 3);
        }

        [Fact]
        public void ReadingTheValuesAsRmsIsWrongByExactlyThreeDecimalOneDb()
        {
            // The trap, pinned. An RMS interpretation omits the factor of two in P = (I^2+Q^2)/2R,
            // which is 10*log10(2) = 3.0103 dB - small enough to look like a calibration offset
            // and large enough to make every absolute reading wrong.
            double correct = MeanPowerDbm(Capture);
            double asRms = MeanPowerDbm(Capture, impedanceFactor: 1.0);

            Assert.Equal(3.0103, asRms - correct, 4);
            Assert.NotEqual(ReportedMeanDbm, asRms, 3);
        }

        [Fact]
        public void TheSampleIntervalIsAWholeMultipleOfTheFifteenMegahertzClock()
        {
            // REQ-E44-002b: the instrument quantises its sample period, so a requested rate is not
            // generally the one honoured and the driver must read it back rather than assume it.
            const double tick = 1.0 / 15e6;
            double multiple = ReportedSampleIntervalSeconds / tick;

            Assert.Equal(Math.Round(multiple), multiple, 3);
            _output.WriteLine(
                "Ts = " + ReportedSampleIntervalSeconds + " s = " + Math.Round(multiple) +
                " x 1/15 MHz, so Fs = " + (1.0 / ReportedSampleIntervalSeconds).ToString("F0") + " Hz");
        }

        [Fact]
        public void TheAmplitudeChainAgreesWithTheInstrumentsOwnMeanPower()
        {
            // The chain OpenVSA actually uses, against the instrument's own answer for the same
            // samples. This is the end-to-end check that REQ-AMP-001's arithmetic and this
            // instrument's scaling convention agree; either alone can be self-consistent and wrong.
            var chain = new AmplitudeChain();

            // Full scale of one volt, because the samples are already volts. A rectangular window
            // over the whole record has coherent gain 1, so the chain reduces to the same
            // expression the instrument used.
            AmplitudeScale scale = chain.ScaleFor(1.0, 0.0, 1, 1.0);

            double sum = 0.0;

            for (int n = 0; n < Capture.Length; n += 2)
            {
                sum += Capture[n] * Capture[n] + Capture[n + 1] * Capture[n + 1];
            }

            double meanSquare = sum / (Capture.Length / 2);

            Assert.Equal(ReportedMeanDbm, scale.PowerToDbm(meanSquare), 3);
        }

        private static double MeanPowerDbm(double[] interleaved, double impedanceFactor = 2.0)
        {
            double sum = 0.0;

            for (int n = 0; n < interleaved.Length; n += 2)
            {
                sum += Power(interleaved[n], interleaved[n + 1], impedanceFactor);
            }

            return ToDbm(sum / (interleaved.Length / 2));
        }

        private static double ExtremePowerDbm(double[] interleaved, bool highest)
        {
            double extreme = highest ? double.NegativeInfinity : double.PositiveInfinity;

            for (int n = 0; n < interleaved.Length; n += 2)
            {
                double power = Power(interleaved[n], interleaved[n + 1], 2.0);

                if (highest ? power > extreme : power < extreme)
                {
                    extreme = power;
                }
            }

            return ToDbm(extreme);
        }

        /// <summary>Instantaneous power, <c>(I² + Q²) / (factor · R)</c>.</summary>
        private static double Power(double i, double q, double impedanceFactor) =>
            (i * i + q * q) / (impedanceFactor * Ohms);

        private static double ToDbm(double watts) => 10.0 * Math.Log10(watts * 1000.0);
    }
}
