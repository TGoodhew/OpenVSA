using System;
using System.Collections.Generic;
using OpenVSA.Core;
using OpenVSA.Dsp.Spectrum;
using OpenVSA.Dsp.Windowing;
using Xunit;

namespace OpenVSA.Dsp.Tests
{
    /// <summary>
    /// <c>REQ-AMP-001</c>: a tone of known absolute power reads correctly under every window,
    /// every span and every FFT size, to within 0.05 dB.
    /// </summary>
    /// <remarks>
    /// The expected value is computed from the physics — <c>P = A²/2R</c> for a tone of amplitude
    /// <c>A</c> volts peak — and never from a previous run of the chain, per <c>REQ-TST-001</c>.
    /// The three dimensions the criterion names are swept as a cross-product rather than sampled:
    /// a chain that has the window correction right and the FFT normalisation wrong passes any one
    /// of them alone.
    /// </remarks>
    public class AmplitudeChainTests
    {
        /// <summary>The reference impedance the default chain uses.</summary>
        private const double Ohms = 50.0;

        public static IEnumerable<object[]> EveryWindow()
        {
            foreach (WindowType type in Enum.GetValues(typeof(WindowType)))
            {
                yield return new object[] { type };
            }
        }

        public static IEnumerable<object[]> EveryWindowAndSize()
        {
            foreach (WindowType type in Enum.GetValues(typeof(WindowType)))
            {
                foreach (int length in new[] { 256, 1024, 8192 })
                {
                    yield return new object[] { type, length };
                }
            }
        }

        // ---- The acceptance criterion ----------------------------------------------------------

        [Theory]
        [MemberData(nameof(EveryWindowAndSize))]
        public void KnownTone_ReadsItsAbsolutePower_UnderEveryWindowAndSize(WindowType window, int length)
        {
            const double amplitudeFraction = 0.5;
            const double fullScaleVolts = 1.0;

            // Three spans, so the sample rate and hence the bin width differ while the answer must
            // not: absolute amplitude is a property of the tone, not of how finely it was measured.
            foreach (double spanHz in new[] { 1e3, 10e6, 40e6 })
            {
                using (IqBlock block = ComplexTone(
                    length, spanHz * 1.28, 1e9, length / 4, amplitudeFraction, fullScaleVolts))
                {
                    var computer = new SpectrumComputer(window, null, new AmplitudeChain());
                    SpectrumFrame frame = computer.Compute(block);

                    double expected = ExpectedDbm(amplitudeFraction * fullScaleVolts, Ohms);
                    double measured = frame.LevelsDbm[frame.IndexOfPeak()];

                    Assert.True(
                        Math.Abs(measured - expected) <= 0.05,
                        window + " at N=" + length + ", span " + spanHz + " Hz: read " +
                        measured.ToString("F4") + " dBm against " + expected.ToString("F4") +
                        " dBm — " + Math.Abs(measured - expected).ToString("F4") +
                        " dB error, tolerance 0.05 dB.");
                }
            }
        }

        [Theory]
        [MemberData(nameof(EveryWindow))]
        public void KnownTone_ReadsItsAbsolutePower_WhateverTheFrontEndsFullScale(WindowType window)
        {
            // The full-scale term has to be real, not a constant that happens to be 1. A front end
            // reporting 2 V full scale and half-scale samples is a 1 V tone: +10 dBm.
            const int length = 4096;

            using (IqBlock block = ComplexTone(length, 12.8e6, 1e9, 1000, 0.5, 2.0))
            {
                var computer = new SpectrumComputer(window, null, new AmplitudeChain());
                SpectrumFrame frame = computer.Compute(block);

                double measured = frame.LevelsDbm[frame.IndexOfPeak()];
                Assert.True(Math.Abs(measured - 10.0) <= 0.05,
                    window + ": read " + measured.ToString("F4") + " dBm against +10.0000 dBm.");
            }
        }

        [Fact]
        public void OneVoltPeakInto50Ohms_ReadsPlus10Dbm()
        {
            // The single figure that anchors the whole chain, and the one to check by hand: a tone
            // of 1 V peak into 50 ohms is 10 mW.
            using (IqBlock block = ComplexTone(4096, 12.8e6, 1e9, 512, 1.0, 1.0))
            {
                SpectrumFrame frame = new SpectrumComputer().Compute(block);
                Assert.Equal(10.0, frame.LevelsDbm[frame.IndexOfPeak()], 2);
            }
        }

        // ---- The terms of the expression, each shown to be doing something -----------------------

        [Fact]
        public void ReferenceLevel_SuppliesTheFullScaleWhenTheFrontEndDeclaresNone()
        {
            // A front end that expresses its range as a reference level rather than a full scale
            // must still read correctly, and must read the *same*: +10 dBm reference level is
            // exactly the statement that full scale is 1 V into 50 ohms.
            using (IqBlock declared = ComplexTone(4096, 12.8e6, 1e9, 512, 0.5, 1.0))
            using (IqBlock derived = ComplexTone(4096, 12.8e6, 1e9, 512, 0.5, double.NaN, referenceLevelDbm: 10.0))
            {
                var computer = new SpectrumComputer();
                double fromFullScale = computer.Compute(declared).LevelsDbm[512 + 2048];
                double fromReferenceLevel = computer.Compute(derived).LevelsDbm[512 + 2048];

                Assert.Equal(fromFullScale, fromReferenceLevel, 6);
            }
        }

        [Fact]
        public void ReferenceLevel_IsNotAppliedOnTopOfADeclaredFullScale()
        {
            // The double-counting failure: applying both terms would make the reading move with the
            // reference level even though the front end told us its full scale.
            using (IqBlock low = ComplexTone(1024, 12.8e6, 1e9, 128, 0.5, 1.0, referenceLevelDbm: -30.0))
            using (IqBlock high = ComplexTone(1024, 12.8e6, 1e9, 128, 0.5, 1.0, referenceLevelDbm: 30.0))
            {
                var computer = new SpectrumComputer();
                double atLowRange = computer.Compute(low).LevelsDbm[128 + 512];
                double atHighRange = computer.Compute(high).LevelsDbm[128 + 512];

                Assert.Equal(atLowRange, atHighRange, 6);
            }
        }

        [Fact]
        public void ExternalGain_IsReferredOutOfTheReading()
        {
            using (IqBlock block = ComplexTone(1024, 12.8e6, 1e9, 128, 0.5, 1.0))
            {
                double atConnector = new SpectrumComputer(
                    Window.Default, null, new AmplitudeChain(Ohms, 0.0))
                    .Compute(block).LevelsDbm[128 + 512];

                double throughAmplifier = new SpectrumComputer(
                    Window.Default, null, new AmplitudeChain(Ohms, 20.0))
                    .Compute(block).LevelsDbm[128 + 512];

                // A 20 dB amplifier ahead of the input means the signal at the plane of interest is
                // 20 dB smaller than what the connector sees.
                //
                // Four decimal places, not more: levels are stored as float, so two of them that
                // differ by an exact 20 dB in double arithmetic agree to about 1e-6 dB once
                // rounded. That is five orders of magnitude inside REQ-AMP-001's 0.05 dB and is a
                // property of the storage, not of the chain.
                Assert.Equal(atConnector - 20.0, throughAmplifier, 4);
            }
        }

        [Fact]
        public void ReferenceImpedance_ChangesTheReadingByTheAnalyticAmount()
        {
            // REQ-AMP-002's criterion: the same voltage into 75 ohms is 10*log10(75/50) = 1.76 dB
            // less power.
            using (IqBlock block = ComplexTone(1024, 12.8e6, 1e9, 128, 0.5, 1.0))
            {
                double at50 = new SpectrumComputer(Window.Default, null, new AmplitudeChain(50.0, 0.0))
                    .Compute(block).LevelsDbm[128 + 512];
                double at75 = new SpectrumComputer(Window.Default, null, new AmplitudeChain(75.0, 0.0))
                    .Compute(block).LevelsDbm[128 + 512];

                Assert.Equal(-10.0 * Math.Log10(75.0 / 50.0), at75 - at50, 4);
            }
        }

        [Fact]
        public void ARealBasebandTone_ReadsTheSamePowerAsAComplexOne()
        {
            // The one-sided path doubles its interior bins. Without that the same tone reads 6 dB
            // low on baseband, which is the classic error this asserts against.
            const int length = 4096;

            using (IqBlock complexBlock = ComplexTone(length, 12.8e6, 1e9, length / 4, 0.5, 1.0))
            using (IqBlock realBlock = RealTone(length, 12.8e6, length / 4, 0.5, 1.0))
            {
                var computer = new SpectrumComputer();
                SpectrumFrame fromComplex = computer.Compute(complexBlock);
                SpectrumFrame fromReal = computer.Compute(realBlock);

                double expected = ExpectedDbm(0.5, Ohms);
                double measured = fromReal.LevelsDbm[fromReal.IndexOfPeak()];

                Assert.True(Math.Abs(measured - expected) <= 0.05,
                    "Baseband read " + measured.ToString("F4") + " dBm against " +
                    expected.ToString("F4") + " dBm.");
                Assert.Equal(
                    fromComplex.LevelsDbm[fromComplex.IndexOfPeak()], measured, 2);
            }
        }

        [Fact]
        public void ADeadBlock_ReadsTheFloorRatherThanNegativeInfinity()
        {
            using (IqBlock silence = ComplexTone(256, 12.8e6, 1e9, 0, 0.0, 1.0))
            {
                SpectrumFrame frame = new SpectrumComputer().Compute(silence);

                for (int i = 0; i < frame.PointCount; i++)
                {
                    Assert.Equal(AmplitudeScale.FloorDbm, frame.LevelsDbm[i]);
                }
            }
        }

        [Fact]
        public void AChainWithNoUsableScale_SaysSoRatherThanReadingZero()
        {
            var chain = new AmplitudeChain();

            ArgumentException failure = Assert.Throws<ArgumentException>(
                () => chain.ResolveFullScaleVolts(double.NaN, double.NaN));

            Assert.Contains("REQ-AMP-001", failure.Message);
        }

        [Theory]
        [InlineData(-1.0)]
        [InlineData(0.0)]
        [InlineData(double.PositiveInfinity)]
        public void ImpedanceMustBePositiveAndFinite(double ohms)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new AmplitudeChain(ohms, 0.0));
        }

        // ---- Helpers ---------------------------------------------------------------------------

        /// <summary>The power of a tone of <paramref name="voltsPeak"/> into <paramref name="ohms"/>, in dBm.</summary>
        private static double ExpectedDbm(double voltsPeak, double ohms) =>
            10.0 * Math.Log10(voltsPeak * voltsPeak / (2.0 * ohms) * 1000.0);

        /// <summary>A complex tone sitting exactly on bin <paramref name="bin"/>.</summary>
        private static IqBlock ComplexTone(
            int count,
            double sampleRateHz,
            double centerFrequencyHz,
            int bin,
            double amplitudeFraction,
            double fullScaleVolts,
            double referenceLevelDbm = 0.0)
        {
            IqBlock block = IqBlock.Rent(Metadata(
                count, sampleRateHz, centerFrequencyHz, false, fullScaleVolts, referenceLevelDbm));

            Span<float> samples = block.GetSamples();

            for (int n = 0; n < count; n++)
            {
                double phase = 2.0 * Math.PI * bin * n / count;
                samples[n * 2] = (float)(amplitudeFraction * Math.Cos(phase));
                samples[n * 2 + 1] = (float)(amplitudeFraction * Math.Sin(phase));
            }

            return block;
        }

        /// <summary>A real tone in a baseband block: Q is zero throughout.</summary>
        private static IqBlock RealTone(
            int count, double sampleRateHz, int bin, double amplitudeFraction, double fullScaleVolts)
        {
            IqBlock block = IqBlock.Rent(Metadata(
                count, sampleRateHz, 0.0, true, fullScaleVolts, 0.0));

            Span<float> samples = block.GetSamples();

            for (int n = 0; n < count; n++)
            {
                double phase = 2.0 * Math.PI * bin * n / count;
                samples[n * 2] = (float)(amplitudeFraction * Math.Cos(phase));
                samples[n * 2 + 1] = 0.0f;
            }

            return block;
        }

        private static IqBlockMetadata Metadata(
            int count,
            double sampleRateHz,
            double centerFrequencyHz,
            bool isBaseband,
            double fullScaleVolts,
            double referenceLevelDbm) =>
            new IqBlockMetadata(
                sampleCount: count,
                sampleRateHz: sampleRateHz,
                centerFrequencyHz: centerFrequencyHz,
                isBaseband: isBaseband,
                fullScaleVolts: fullScaleVolts,
                referenceLevelDbm: referenceLevelDbm,
                sequenceNumber: 0,
                acquiredUtc: DateTime.UtcNow,
                triggerOffsetSeconds: 0.0,
                triggerCorrectionsApplied: true,
                source: new FrontEndId("test"),
                extended: null);
    }
}
