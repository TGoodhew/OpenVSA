using System;
using System.Collections.Generic;
using System.Globalization;
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
    /// <c>REQ-DEM-051</c>: the equaliser's parameters, its three modes, and where its impulse sits.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>A mode is a statement about successive measurements</strong>, so every test here
    /// demodulates more than one block. One measurement cannot tell Run from Hold: both apply a
    /// filter and both report one. What separates them is what the second block does with what the
    /// first one found, which is why these tests run a source on and hand the demodulator a
    /// different stretch of the same signal each time — as a repeating measurement does.
    /// </para>
    /// <para>
    /// <strong>The channel is the same throughout and the equaliser is told nothing about it.</strong>
    /// The tilt below is applied to the samples and nowhere else.
    /// </para>
    /// </remarks>
    public class EqualiserModeTests
    {
        private const double SymbolRateHz = 1e6;
        private const double SampleRateHz = 16e6;
        private const int Symbols = 4000;

        private readonly ITestOutputHelper _output;

        public EqualiserModeTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void RunFitsEachMeasurementAndCarriesTheResultIntoTheNext()
        {
            // REQ-DEM-051: "Run updates coefficients from the current measurement and applies them
            // to the next, so coefficients change between successive measurements."
            var state = new EqualiserState();

            List<IReadOnlyList<ConstellationPoint>> taps = Blocks(EqualiserMode.Run, state, 3);

            Assert.True(state.IsAdapted, "Run left nothing behind for the next measurement.");

            for (int block = 1; block < taps.Count; block++)
            {
                double moved = Distance(taps[block - 1], taps[block]);

                _output.WriteLine(
                    "Block " + block.ToString(CultureInfo.InvariantCulture) + " moved the " +
                    "coefficients by " + moved.ToString("G4", CultureInfo.InvariantCulture) + ".");

                // Each block fits its own filter from its own samples, so successive filters differ
                // -- by little, because the channel is the same, but not by nothing.
                Assert.True(
                    moved > 0.0,
                    "Run gave block " + block.ToString(CultureInfo.InvariantCulture) +
                    " coefficients identical to the block before it, which is Hold's behaviour, " +
                    "not Run's.");
            }
        }

        [Fact]
        public void HoldFreezesTheCoefficientsBitForBit()
        {
            // REQ-DEM-051: "Hold freezes them, asserted by bit-identical coefficients across
            // measurements."
            var state = new EqualiserState();

            // Something worth holding: one Run block fits the channel, and Hold is asked to keep it.
            Blocks(EqualiserMode.Run, state, 1);

            IReadOnlyList<ConstellationPoint> fitted = state.Coefficients;

            List<IReadOnlyList<ConstellationPoint>> taps = Blocks(EqualiserMode.Hold, state, 3);

            foreach (IReadOnlyList<ConstellationPoint> block in taps)
            {
                Identical(fitted, block);
            }

            // And Hold wrote nothing back: what it holds is still what Run left.
            Identical(fitted, state.Coefficients);
        }

        [Fact]
        public void HoldStillAppliesWhatItHolds()
        {
            // Hold freezes the fit; it does not switch the equaliser off. EqualiserEnabled does
            // that, and the difference matters -- an equaliser that abstained while "held" would
            // leave the channel in the measurement it was told to keep correcting.
            var state = new EqualiserState();

            Blocks(EqualiserMode.Run, state, 1);

            double held = Evm(EqualiserMode.Hold, state);
            double reset = Evm(EqualiserMode.Reset, new EqualiserState());

            _output.WriteLine(
                "Held " + held.ToString("F4", CultureInfo.InvariantCulture) + " %rms against " +
                reset.ToString("F4", CultureInfo.InvariantCulture) + " %rms uncorrected.");

            Assert.True(
                held < reset / 2.0,
                "A held equaliser left EVM at " + held.ToString("F4", CultureInfo.InvariantCulture) +
                " %rms against " + reset.ToString("F4", CultureInfo.InvariantCulture) +
                " %rms with no equaliser at all, so it is not applying the coefficients it holds.");
        }

        [Fact]
        public void ResetIsAUnitImpulseAndForgetsWhatWasFitted()
        {
            // REQ-DEM-051: "Reset returns a unit-impulse response."
            var state = new EqualiserState();

            Blocks(EqualiserMode.Run, state, 1);

            Assert.True(state.IsAdapted, "Nothing was fitted, so there is nothing to reset.");

            IReadOnlyList<ConstellationPoint> taps = Blocks(EqualiserMode.Reset, state, 1)[0];

            int impulse = Settings(EqualiserMode.Reset, null).EqualiserImpulseIndex;

            for (int tap = 0; tap < taps.Count; tap++)
            {
                double wanted = tap == impulse ? 1.0 : 0.0;

                Assert.Equal(wanted, taps[tap].I, 12);
                Assert.Equal(0.0, taps[tap].Q, 12);
            }

            Assert.False(
                state.IsAdapted,
                "Reset applied a unit impulse but kept the old coefficients, so selecting Run " +
                "again would carry on from a filter the user asked to be rid of.");
        }

        [Fact]
        public void ResetLeavesTheWaveformExactlyAsItWasFound()
        {
            // A unit impulse is worth nothing as a coefficient set if the convolution that applies
            // it changes the waveform anyway. The measurement with the equaliser reset and the
            // measurement with it switched off are the same measurement.
            double off = Evm(EqualiserMode.Run, null, equalise: false);
            double reset = Evm(EqualiserMode.Reset, null);

            _output.WriteLine(
                "Off " + off.ToString("F6", CultureInfo.InvariantCulture) + " %rms, reset " +
                reset.ToString("F6", CultureInfo.InvariantCulture) + " %rms.");

            Assert.Equal(off, reset, 6);
        }

        [Fact]
        public void CoefficientsFittedAtOneFilterLengthAreNotHeldAtAnother()
        {
            // Coefficients are as long as the filter they were fitted for. Stretching them to
            // another length would be inventing taps; keeping them would apply a filter of the
            // wrong length. Neither is honest, so they are dropped -- and the measurement says so.
            var state = new EqualiserState();

            Blocks(EqualiserMode.Run, state, 1);

            int fitted = state.Taps;

            DemodResult result = Demodulate(
                Settings(EqualiserMode.Hold, state, lengthSymbols: 15), 1)[0];

            Assert.NotEqual(fitted, result.EqualiserCoefficients.Count);
            Assert.Contains(
                result.Notices,
                notice => notice.IndexOf("Hold", StringComparison.Ordinal) >= 0);

            int impulse = Settings(EqualiserMode.Hold, state, lengthSymbols: 15)
                .EqualiserImpulseIndex;

            Assert.Equal(1.0, result.EqualiserCoefficients[impulse].I, 12);
        }

        [Fact]
        public void TheImpulseStartsCentredAndMovesTowardsTheStartAsTheFilterGrows()
        {
            // REQ-DEM-051: "at short filter lengths the impulse sits at the centre, and as length
            // grows its index moves proportionally toward the start -- measured across at least
            // three lengths and checked as a trend, since a fixed-centre implementation passes the
            // short case and fails the long one."
            int[] lengths = { 6, 12, 24, 48 };
            var fractions = new List<double>();

            foreach (int length in lengths)
            {
                DemodResult result = Demodulate(
                    Settings(EqualiserMode.Reset, null, lengthSymbols: length, span: 8), 1)[0];

                int impulse = Impulse(result.EqualiserCoefficients);
                double fraction = impulse / (double)result.EqualiserCoefficients.Count;

                fractions.Add(fraction);

                _output.WriteLine(
                    length.ToString(CultureInfo.InvariantCulture) + " symbols: " +
                    result.EqualiserCoefficients.Count.ToString(CultureInfo.InvariantCulture) +
                    " taps, impulse at " + impulse.ToString(CultureInfo.InvariantCulture) +
                    " (" + fraction.ToString("F3", CultureInfo.InvariantCulture) +
                    " of the filter).");
            }

            // Short: the centre, so the filter reaches equally far either side of the instant.
            Assert.Equal(0.5, fractions[0], 2);

            // The trend, which is the part a fixed-centre implementation cannot pass: the impulse's
            // position as a fraction of the filter never moves back towards the middle, and by the
            // longest filter it has moved decisively towards the start.
            for (int length = 1; length < fractions.Count; length++)
            {
                Assert.True(
                    fractions[length] <= fractions[length - 1] + 1e-9,
                    "The impulse moved back towards the centre between " +
                    lengths[length - 1].ToString(CultureInfo.InvariantCulture) + " and " +
                    lengths[length].ToString(CultureInfo.InvariantCulture) + " symbols.");
            }

            Assert.True(
                fractions[fractions.Count - 1] < 0.5 - 0.1,
                "At " + lengths[lengths.Length - 1].ToString(CultureInfo.InvariantCulture) +
                " symbols the impulse is still at " +
                fractions[fractions.Count - 1].ToString("F3", CultureInfo.InvariantCulture) +
                " of the filter, so the length is being divided equally either side of it and a " +
                "delay spread longer than half the filter cannot be corrected however long the " +
                "filter is made.");
        }

        [Fact]
        public void TheConvergenceFactorIsAStepSizeAndMustBePositive()
        {
            // REQ-DEM-051: "Filter Length is settable in symbols and Convergence factor as a step
            // size."
            var settings = Settings(EqualiserMode.Run, null);

            Assert.Equal(DemodSettings.DefaultEqualiserConvergenceFactor,
                settings.EqualiserConvergenceFactor);

            settings.EqualiserConvergenceFactor = 0.05;
            settings.Validate();

            settings.EqualiserConvergenceFactor = 0.0;

            ArgumentException refused =
                Assert.Throws<ArgumentException>(() => settings.Validate());

            Assert.Contains("convergence factor", refused.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void TheHelpStatesTheTapCountAndWhatEachModeDoes()
        {
            // REQ-DEM-052 asks for the length-to-tap-count relationship to be stated where a user
            // meets it, and REQ-DEM-051's modes are not self-explanatory from their labels -- Hold
            // applies its coefficients, which "hold" does not obviously say.
            string help = HelpTopics.Read(HelpTopics.Equaliser);

            Assert.Contains("2N taps", help.Replace("**", string.Empty), StringComparison.Ordinal);
            Assert.Contains("22", help, StringComparison.Ordinal);

            foreach (string mode in new[] { "Run", "Hold", "Reset" })
            {
                Assert.Contains(mode, help, StringComparison.Ordinal);
            }

            Assert.Contains("Convergence factor", help, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>The index of the largest tap.</summary>
        private static int Impulse(IReadOnlyList<ConstellationPoint> taps)
        {
            int at = 0;
            double largest = -1.0;

            for (int tap = 0; tap < taps.Count; tap++)
            {
                double magnitude =
                    (taps[tap].I * taps[tap].I) + (taps[tap].Q * taps[tap].Q);

                if (magnitude > largest)
                {
                    largest = magnitude;
                    at = tap;
                }
            }

            return at;
        }

        /// <summary>How far one set of coefficients is from another, as a total magnitude.</summary>
        private static double Distance(
            IReadOnlyList<ConstellationPoint> first, IReadOnlyList<ConstellationPoint> second)
        {
            Assert.Equal(first.Count, second.Count);

            double total = 0.0;

            for (int tap = 0; tap < first.Count; tap++)
            {
                double i = first[tap].I - second[tap].I;
                double q = first[tap].Q - second[tap].Q;

                total += Math.Sqrt((i * i) + (q * q));
            }

            return total;
        }

        private static void Identical(
            IReadOnlyList<ConstellationPoint> wanted, IReadOnlyList<ConstellationPoint> got)
        {
            Assert.Equal(wanted.Count, got.Count);

            for (int tap = 0; tap < wanted.Count; tap++)
            {
                // Bit-identical, which is what the criterion asks for: a frozen filter that is
                // merely very close to the last one is a filter that is still being fitted.
                Assert.Equal(
                    BitConverter.DoubleToInt64Bits(wanted[tap].I),
                    BitConverter.DoubleToInt64Bits(got[tap].I));

                Assert.Equal(
                    BitConverter.DoubleToInt64Bits(wanted[tap].Q),
                    BitConverter.DoubleToInt64Bits(got[tap].Q));
            }
        }

        private static DemodSettings Settings(
            EqualiserMode mode,
            EqualiserState state,
            int lengthSymbols = 11,
            int span = 20,
            bool equalise = true)
        {
            return new DemodSettings
            {
                Constellation = Constellation.Qpsk(),
                SymbolRateHz = SymbolRateHz,
                ResultLengthSymbols = 512,
                FilterSymbolSpan = span,
                MeasurementFilter = PulseFilterType.RootRaisedCosine,
                MeasurementFilterAlpha = 0.35,
                ReferenceFilterAlpha = 0.35,
                EqualiserEnabled = equalise,
                EqualiserLengthSymbols = lengthSymbols,
                EqualiserMode = mode,
                EqualiserState = state,
            };
        }

        /// <summary>The EVM one block is measured at under a given mode.</summary>
        private double Evm(EqualiserMode mode, EqualiserState state, bool equalise = true)
        {
            return Demodulate(Settings(mode, state, equalise: equalise), 1)[0].EvmPercent;
        }

        /// <summary>The coefficients successive blocks are equalised by.</summary>
        private List<IReadOnlyList<ConstellationPoint>> Blocks(
            EqualiserMode mode, EqualiserState state, int blocks)
        {
            var taps = new List<IReadOnlyList<ConstellationPoint>>();

            foreach (DemodResult result in Demodulate(Settings(mode, state), blocks))
            {
                taps.Add(result.EqualiserCoefficients);
            }

            return taps;
        }

        /// <summary>
        /// Demodulates successive blocks of one continuing signal, through one channel.
        /// </summary>
        /// <param name="settings">What to demodulate them as; the same object throughout, as a
        /// repeating measurement's would be.</param>
        /// <param name="blocks">How many blocks.</param>
        /// <returns>One result per block, in order.</returns>
        /// <remarks>
        /// The source runs on between blocks rather than restarting, so each block carries different
        /// symbols — which is what makes "the coefficients changed" evidence that a fit happened
        /// rather than evidence of a random number generator. The channel is a fixed 6 dB tilt and
        /// the demodulator is told nothing about it.
        /// </remarks>
        private List<DemodResult> Demodulate(DemodSettings settings, int blocks)
        {
            var source = new ContinuousModulatedSource
            {
                Scheme = ModulationScheme.Qpsk(),
                SymbolRateHz = SymbolRateHz,
                SampleRateHz = SampleRateHz,
                RollOff = 0.35,
                PulseSpanSymbols = 20,
                Seed = 20260831,
            };

            source.Restart();

            var results = new List<DemodResult>();
            var demodulator = new Demodulator();

            for (int block = 0; block < blocks; block++)
            {
                var samples = new float[2 * Symbols * 16];

                source.Fill(samples);
                Tilt(samples);

                results.Add(demodulator.Run(samples, SampleRateHz, settings));
            }

            return results;
        }

        /// <summary>A 6 dB frequency-response tilt across the occupied band.</summary>
        /// <remarks>
        /// 🔴 Defined against the OCCUPIED band and not against Nyquist, which
        /// <c>EqualiserChannelTests</c> records as a fixture trap: an impairment scaled to the
        /// sample rate reaches the band edges at a magnitude that step 4's resampling folds back in
        /// as noise, and the resulting measurement is insensitive to every equaliser setting —
        /// the signature of a corrupted fixture rather than of a channel.
        /// </remarks>
        private static void Tilt(float[] samples)
        {
            double occupied = SymbolRateHz * 1.35;

            Filter(samples, hertz =>
            {
                double slope = Math.Max(-1.0, Math.Min(1.0, hertz / (occupied / 2.0)));

                return new Iq(Math.Pow(10.0, 6.0 * slope / 40.0), 0.0);
            });
        }

        private static void Filter(float[] samples, Func<double, Iq> response)
        {
            int count = samples.Length / 2;
            int length = 1;

            while (length < count)
            {
                length *= 2;
            }

            var spectrum = new double[2 * length];

            for (int sample = 0; sample < count; sample++)
            {
                Iq.Set(spectrum, sample, new Iq(samples[2 * sample], samples[(2 * sample) + 1]));
            }

            Dsp.Fft.IFftProvider fft = Dsp.Fft.FftProviders.Active;

            fft.Forward(new Span<double>(spectrum));

            for (int bin = 0; bin < length; bin++)
            {
                double hertz = (bin <= length / 2 ? bin : bin - length) * SampleRateHz / length;

                Iq.Set(spectrum, bin, Iq.At(spectrum, bin) * response(hertz));
            }

            fft.Inverse(new Span<double>(spectrum));

            for (int sample = 0; sample < count; sample++)
            {
                Iq value = Iq.At(spectrum, sample);

                samples[2 * sample] = (float)(value.I / length);
                samples[(2 * sample) + 1] = (float)(value.Q / length);
            }
        }
    }
}
