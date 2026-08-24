using System;
using System.Globalization;
using OpenVSA.Demod.Chain;
using OpenVSA.Demod.Signal;
using OpenVSA.Synthesis;
using Xunit;
using Xunit.Abstractions;

namespace OpenVSA.Demod.Tests
{
    /// <summary>
    /// <c>REQ-DEM-035</c>: conjugating the input, for a signal that arrives the wrong way round.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This is the control the bit-level bench check cannot be.</strong>
    /// <c>evidence/req-e44-007/</c> records that an inverted spectrum still matched a PN sequence
    /// 1024 bits of 1024: the rotations and bit orders a bit check has to search close the dihedral
    /// group and absorb a conjugation with them. So a passing bit check says nothing about spectral
    /// sense, and the only thing that does is a measurement that fails one way and succeeds the
    /// other — which is what is here.
    /// </para>
    /// </remarks>
    public class MirrorSpectrumTests
    {
        private const double SymbolRateHz = 1e6;
        private const double SampleRateHz = 16e6;
        private const int Symbols = 4000;

        private readonly ITestOutputHelper _output;

        public MirrorSpectrumTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void ATonePlacedAbovoveCentreIsReportedBelowItWhenMirrored()
        {
            // "a tone at +f appears at −f". Read through the carrier estimate, which is the chain's
            // own statement about where the signal sits: an offset of +40 kHz becomes −40 kHz, sign
            // and magnitude both.
            const double OffsetHz = 40e3;

            ContinuousModulatedSource source = Source();

            source.CarrierOffsetHz = OffsetHz;

            float[] samples = Generate(source);

            DemodResult upright = Demodulate(samples, mirrored: false);
            DemodResult mirrored = Demodulate(samples, mirrored: true);

            _output.WriteLine(
                "a carrier " + (OffsetHz / 1e3).ToString("F1", CultureInfo.InvariantCulture) +
                " kHz above centre reads " +
                (upright.CarrierFrequencyErrorHz / 1e3).ToString("F3", CultureInfo.InvariantCulture) +
                " kHz upright and " +
                (mirrored.CarrierFrequencyErrorHz / 1e3).ToString("F3", CultureInfo.InvariantCulture) +
                " kHz mirrored");

            Assert.True(Math.Abs(upright.CarrierFrequencyErrorHz - OffsetHz) < 100.0);
            Assert.True(Math.Abs(mirrored.CarrierFrequencyErrorHz + OffsetHz) < 100.0);
        }

        [Fact]
        public void MirroringATwiceMirroredSignalReturnsTheOriginalExactly()
        {
            // "applying the option twice returns the original to bit-identical values". The option
            // is applied once by the chain, so the second application is done to the signal — and
            // the two together have to be the identity, to the last bit of every metric rather than
            // to a tolerance. A conjugation is its own inverse; anything that merely looked like one
            // would not be.
            float[] samples = Generate(Source());
            float[] conjugated = Conjugated(samples);

            DemodResult once = Demodulate(samples, mirrored: false);
            DemodResult twice = Demodulate(conjugated, mirrored: true);

            _output.WriteLine(
                "upright " + once.EvmPercent.ToString("R", CultureInfo.InvariantCulture) +
                " %rms; conjugated and mirrored back " +
                twice.EvmPercent.ToString("R", CultureInfo.InvariantCulture) + " %rms");

            Assert.Equal(once.EvmPercent, twice.EvmPercent);
            Assert.Equal(once.CarrierFrequencyErrorHz, twice.CarrierFrequencyErrorHz);
            Assert.Equal(once.Symbols.Count, twice.Symbols.Count);

            for (int symbol = 0; symbol < once.Symbols.Count; symbol++)
            {
                Assert.Equal(once.Symbols[symbol], twice.Symbols[symbol]);
            }
        }

        [Fact]
        public void AnInvertedSignalDemodulatesToTheRightSymbolsOnlyWithTheOptionOn()
        {
            // "A spectrally inverted signal that fails to demodulate with the option off demodulates
            // to the correct bits with it on, which is the case the option exists for."
            //
            // 🔴 "Fails" has to be read carefully, and this is the whole reason the option needs a
            // test of its own. An inverted QPSK signal still demodulates to a clean constellation at
            // a respectable EVM -- conjugation maps the four points onto themselves. What it does
            // not do is produce the symbols that were SENT. So the failure is asserted on the
            // symbols and the success is asserted on the symbols, and EVM is printed to show that it
            // would have told nobody anything.
            ContinuousModulatedSource source = Source();

            float[] inverted = Conjugated(Generate(source));

            DemodResult ignoring = Demodulate(inverted, mirrored: false);
            DemodResult corrected = Demodulate(inverted, mirrored: true);

            var sent = new int[Symbols];

            for (int symbol = 0; symbol < Symbols; symbol++)
            {
                sent[symbol] = source.SymbolAt(symbol);
            }

            int wrong = LongestAgreement(ignoring.Symbols, sent);
            int right = LongestAgreement(corrected.Symbols, sent);

            _output.WriteLine(
                "option off: EVM " +
                ignoring.EvmPercent.ToString("F4", CultureInfo.InvariantCulture) + " %rms and " +
                wrong + " of " + ignoring.Symbols.Count + " symbols right");

            _output.WriteLine(
                "option on:  EVM " +
                corrected.EvmPercent.ToString("F4", CultureInfo.InvariantCulture) + " %rms and " +
                right + " of " + corrected.Symbols.Count + " symbols right");

            Assert.Equal(corrected.Symbols.Count, right);

            Assert.True(
                wrong < ignoring.Symbols.Count,
                "The inverted signal demodulated to the transmitted symbols with the option OFF, " +
                "so this test cannot tell whether the option does anything.");
        }

        [Fact]
        public void TheOptionIsOffUnlessItIsAskedFor()
        {
            Assert.False(new DemodSettings().MirrorSpectrum);
        }

        private static ContinuousModulatedSource Source() =>
            new ContinuousModulatedSource
            {
                Scheme = ModulationScheme.Qpsk(),
                SymbolRateHz = SymbolRateHz,
                SampleRateHz = SampleRateHz,
                RollOff = 0.35,
                PulseSpanSymbols = 20,
                Seed = 20260824,
            };

        private static float[] Generate(ContinuousModulatedSource source)
        {
            var samples = new float[2 * (int)Math.Ceiling(Symbols * source.SamplesPerSymbol)];

            source.Restart();
            source.Fill(samples);

            return samples;
        }

        /// <summary>The same waveform with its spectrum the other way round.</summary>
        private static float[] Conjugated(float[] samples)
        {
            var flipped = new float[samples.Length];

            for (int sample = 0; sample < samples.Length; sample += 2)
            {
                flipped[sample] = samples[sample];
                flipped[sample + 1] = -samples[sample + 1];
            }

            return flipped;
        }

        private static DemodResult Demodulate(float[] samples, bool mirrored)
        {
            var settings = new DemodSettings
            {
                Constellation = Constellation.Qpsk(),
                SymbolRateHz = SymbolRateHz,
                ResultLengthSymbols = 512,
                FilterSymbolSpan = 20,
                MeasurementFilter = PulseFilterType.RootRaisedCosine,
                MeasurementFilterAlpha = 0.35,
                ReferenceFilterAlpha = 0.35,
                MirrorSpectrum = mirrored,
            };

            return new Demodulator().Run(samples, SampleRateHz, settings);
        }

        private static int LongestAgreement(
            System.Collections.Generic.IReadOnlyList<int> recovered, int[] sent)
        {
            int best = 0;

            for (int offset = 0; offset < sent.Length; offset++)
            {
                int matched = 0;

                for (int index = 0; index < recovered.Count; index++)
                {
                    if (recovered[index] == sent[(offset + index) % sent.Length])
                    {
                        matched++;
                    }
                }

                if (matched > best)
                {
                    best = matched;
                }

                if (best == recovered.Count)
                {
                    break;
                }
            }

            return best;
        }
    }
}
