using System;
using System.Collections.Generic;
using System.Globalization;
using OpenVSA.Demod.Chain;
using OpenVSA.Demod.Signal;
using OpenVSA.Synthesis;
using Xunit;
using Xunit.Abstractions;

namespace OpenVSA.Demod.Tests
{
    /// <summary>
    /// <c>REQ-DEM-033</c> and <c>REQ-DEM-041</c>: how long the window searched is, and finding a
    /// pulse in it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both requirements are about the same failure — a demodulation positioned on the wrong part of
    /// a signal, which produces a result rather than an error. So the tests here are mostly about
    /// what is <em>not</em> found: a pulse too near the noise, a pulse whose start is outside the
    /// window, a Search Length too short to guarantee either.
    /// </para>
    /// <para>
    /// <strong>The inequality is tested because the equality is the plausible misreading.</strong>
    /// <c>REQ-DEM-033</c> says so in as many words: <c>2 × MaxOn + MaxOff</c> is the minimum that
    /// guarantees a whole pulse, and enforcing it as an equality would prohibit longer searches for
    /// no reason.
    /// </para>
    /// </remarks>
    public class SearchLengthAndPulseTests
    {
        private const double SymbolRateHz = 1e6;
        private const double SampleRateHz = 16e6;
        private const int PerSymbol = 16;

        private readonly ITestOutputHelper _output;

        public SearchLengthAndPulseTests(ITestOutputHelper output)
        {
            _output = output;
        }

        // ---- REQ-DEM-033 -----------------------------------------------------------------------

        [Fact]
        public void ASearchLengthShorterThanTheResultLengthIsRefusedWithTheMinimum()
        {
            // "Search Length is expressed in symbols and a value below Result Length is rejected
            // with the minimum reported."
            var settings = Settings();

            settings.ResultLengthSymbols = 512;
            settings.SearchLengthSymbols = 256;

            ArgumentException refused = Assert.Throws<ArgumentException>(() => settings.Validate());

            Assert.Contains("512", refused.Message, StringComparison.Ordinal);
            Assert.Contains("REQ-DEM-033", refused.Message, StringComparison.Ordinal);

            _output.WriteLine(refused.Message);

            // At the minimum it is accepted, and above it too.
            settings.SearchLengthSymbols = 512;
            settings.Validate();

            settings.SearchLengthSymbols = 5000;
            settings.Validate();

            // And zero still means the whole record, which is longer than anything.
            settings.SearchLengthSymbols = 0;
            settings.Validate();
        }

        [Fact]
        public void ThePulseConstraintIsAnInequalityAndNotAnEquality()
        {
            // "For pulse search the constraint Search Length >= 2 MaxOn + MaxOff is enforced as an
            // inequality, not an equality — a longer Search Length is accepted, and a test asserts
            // that, since enforcing equality is the plausible misreading."
            var settings = Settings();

            settings.ResultLengthSymbols = 64;
            settings.BurstSearchEnabled = true;
            settings.MaximumPulseOnSymbols = 100;
            settings.MaximumPulseOffSymbols = 100;

            int minimum = (2 * 100) + 100;

            settings.SearchLengthSymbols = minimum - 1;

            ArgumentException refused = Assert.Throws<ArgumentException>(() => settings.Validate());

            Assert.Contains("300", refused.Message, StringComparison.Ordinal);
            _output.WriteLine(refused.Message);

            // Exactly the minimum.
            settings.SearchLengthSymbols = minimum;
            settings.Validate();

            // And longer, which is the half a misreading would refuse.
            foreach (int longer in new[] { minimum + 1, minimum * 2, minimum * 10 })
            {
                settings.SearchLengthSymbols = longer;
                settings.Validate();
            }

            _output.WriteLine(
                "accepted at " + minimum + " and at " + (minimum * 10) + " symbols");
        }

        [Fact]
        public void TheConstraintIsNotAppliedToASearchThatIsNotForAPulse()
        {
            // MaxOn and MaxOff describe a pulse. With the pulse search off they are not a constraint
            // on anything, and applying them would refuse a perfectly good continuous measurement.
            var settings = Settings();

            settings.ResultLengthSymbols = 64;
            settings.BurstSearchEnabled = false;
            settings.MaximumPulseOnSymbols = 1000;
            settings.MaximumPulseOffSymbols = 1000;
            settings.SearchLengthSymbols = 128;

            settings.Validate();
        }

        [Fact]
        public void AtExactlyTheMinimumAPulseAtTheWorstPhaseIsStillFoundComplete()
        {
            // "At exactly the minimum, a pulse placed at the least favourable phase relative to the
            // window is still found complete."
            //
            // The least favourable phase is a window that opens one symbol AFTER a pulse began: that
            // pulse's start is outside and it cannot be used, so the window has to hold the whole of
            // the next one. 2 x MaxOn + MaxOff is exactly the length that guarantees it, and this
            // builds that case rather than describing it.
            const int MaxOn = 100;
            const int MaxOff = 100;

            int searchSymbols = (2 * MaxOn) + MaxOff;

            float[] samples = Pulsed(
                onSymbols: MaxOn,
                offSymbols: MaxOff,
                bursts: 4,
                silenceSymbols: 0,
                phaseSymbols: 1,
                noiseDb: -30.0);

            var settings = Settings();

            settings.ResultLengthSymbols = 32;
            settings.BurstSearchEnabled = true;
            settings.MaximumPulseOnSymbols = MaxOn;
            settings.MaximumPulseOffSymbols = MaxOff;
            settings.SearchLengthSymbols = searchSymbols;

            DemodResult result = new Demodulator().Run(samples, SampleRateHz, settings);

            string found = Notice(result, "found a pulse of");

            _output.WriteLine(found ?? "NOT FOUND");

            Assert.NotNull(found);

            // A WHOLE pulse rather than the tail of the one already in progress. The window opens
            // one symbol into a burst, so the fragment ahead of it is MaxOn - 1 symbols long and
            // has no start; the pulse reported has to be the next one, which begins after that
            // fragment and its following gap. Measured a little over MaxOn because the power is
            // smoothed over a symbol, which is a rise time rather than an error.
            double begins = Symbols(found, "beginning ");
            double lasts = Symbols(found, "found a pulse of ");

            _output.WriteLine(
                "the fragment ahead of it was " + (MaxOn - 1) + " symbols with no start; the pulse " +
                "reported begins at " + begins.ToString("F1", CultureInfo.InvariantCulture) +
                " and lasts " + lasts.ToString("F1", CultureInfo.InvariantCulture));

            Assert.InRange(begins, (MaxOn - 1) + MaxOff - 2.0, (MaxOn - 1) + MaxOff + 2.0);
            Assert.InRange(lasts, MaxOn - 2.0, MaxOn + 2.0);
        }

        // ---- REQ-DEM-041 -----------------------------------------------------------------------

        [Fact]
        public void APulseTwentyDecibelsAboveTheNoiseIsFound()
        {
            // "A simulated burst 20 dB above noise is found and centred".
            float[] samples = Pulsed(
                onSymbols: 600, offSymbols: 600, bursts: 3, silenceSymbols: 0, phaseSymbols: 0,
                noiseDb: -20.0);

            DemodResult result = Demodulate(samples);

            string found = Notice(result, "found a pulse of");

            _output.WriteLine(found ?? "NOT FOUND");

            Assert.NotNull(found);
            Assert.Contains("dB above the noise floor", found, StringComparison.Ordinal);
        }

        [Fact]
        public void TheSamePulseTenDecibelsAboveTheNoiseIsReportedAsNotFound()
        {
            // "the same burst at 10 dB above noise is reported as not found, rather than silently
            // mis-locating." The second half is the point: the failure to avoid is a window
            // confidently placed on something that is not a pulse.
            float[] samples = Pulsed(
                onSymbols: 600, offSymbols: 600, bursts: 3, silenceSymbols: 0, phaseSymbols: 0,
                noiseDb: -10.0);

            DemodResult result = Demodulate(samples);

            Assert.Null(Notice(result, "found a pulse of"));

            string missing = Notice(result, "dB above the noise floor:");

            _output.WriteLine(missing ?? "(said nothing at all)");

            Assert.NotNull(missing);
            Assert.Contains("REQ-DEM-041", missing, StringComparison.Ordinal);
            Assert.Contains("left where it was", missing, StringComparison.Ordinal);
        }

        [Fact]
        public void AContinuousSignalHasNoPulseToFind()
        {
            // The case a threshold placed below the peak gets wrong: a signal that never stops has
            // an edge nowhere, and 15 dB above the noise floor is a statement about two levels that
            // a continuous signal does not have.
            var source = Source();
            var samples = new float[2 * 40000];

            source.Fill(samples);

            DemodResult result = new Demodulator().Run(samples, SampleRateHz, Enabled());

            Assert.Null(Notice(result, "found a pulse of"));

            string said = Notice(result, "dB above the noise floor:");

            _output.WriteLine(said ?? "(said nothing at all)");
            Assert.NotNull(said);
        }

        [Fact]
        public void TheWindowIsCentredOnThePulseRatherThanLeftWhereTheRecordBegins()
        {
            // "Without sync search, the Result Length window shall be auto-centred on the detected
            // pulse." Demonstrated by its effect rather than by reaching inside: the burst is put in
            // the second half of the record, so a window left at the start of the record lands in
            // the silence before it. With the search on, the window moves and the demodulation
            // works; with it off, it does not.
            float[] samples = Pulsed(
                onSymbols: 1200, offSymbols: 600, bursts: 1, silenceSymbols: 1500, phaseSymbols: 0,
                noiseDb: -40.0);

            DemodSettings searching = Enabled();

            searching.ResultLengthSymbols = 512;

            DemodResult found = new Demodulator().Run(samples, SampleRateHz, searching);

            DemodSettings ignoring = Settings();

            ignoring.ResultLengthSymbols = 512;
            ignoring.BurstSearchEnabled = false;

            DemodResult blind = new Demodulator().Run(samples, SampleRateHz, ignoring);

            _output.WriteLine(
                "with the pulse search: EVM " +
                found.EvmPercent.ToString("F3", CultureInfo.InvariantCulture) +
                " %rms; without it, starting where the record does: " +
                blind.EvmPercent.ToString("F3", CultureInfo.InvariantCulture) + " %rms");

            Assert.NotNull(Notice(found, "found a pulse of"));

            Assert.True(
                found.EvmPercent < 5.0,
                "Centred on the pulse the demodulation read " + found.EvmPercent + " %rms.");

            Assert.True(
                blind.EvmPercent > 10.0 * found.EvmPercent,
                "Ignoring the pulse read " + blind.EvmPercent + " %rms against the centred " +
                found.EvmPercent + " — so the window did not have to move, and this test proves " +
                "nothing about centring.");
        }

        // ---- fixtures --------------------------------------------------------------------------

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

        /// <summary>
        /// A modulated signal switched on and off, with a noise floor a stated distance below it.
        /// </summary>
        /// <param name="onSymbols">How long each burst is.</param>
        /// <param name="offSymbols">How long the gaps are.</param>
        /// <param name="bursts">How many bursts.</param>
        /// <param name="silenceSymbols">How much silence comes before the pattern starts.</param>
        /// <param name="phaseSymbols">
        /// How far into the on/off cycle the pattern begins — one symbol of it is the least
        /// favourable phase there is, because the window then opens on a burst whose start it
        /// missed.
        /// </param>
        /// <param name="noiseDb">The noise floor, in dB relative to the burst's power.</param>
        /// <remarks>
        /// Built here rather than taken from <c>PulsedSource</c> because what is wanted is a
        /// <em>modulated</em> burst — the thing the demodulator is asked to position itself on — and
        /// because the noise level has to be stated in the same terms the requirement states its
        /// criterion in.
        /// </remarks>
        private static float[] Pulsed(
            int onSymbols,
            int offSymbols,
            int bursts,
            int silenceSymbols,
            int phaseSymbols,
            double noiseDb)
        {
            ContinuousModulatedSource source = Source();

            int period = onSymbols + offSymbols;
            int symbols = silenceSymbols + ((bursts + 1) * period) + phaseSymbols;
            int samples = symbols * PerSymbol;

            var modulated = new float[2 * samples];

            source.Fill(modulated);

            double sigma = Math.Sqrt(Math.Pow(10.0, noiseDb / 10.0) / 2.0);
            var noise = new Random(20260824);
            var output = new float[2 * samples];

            for (int sample = 0; sample < samples; sample++)
            {
                int symbol = sample / PerSymbol;
                bool on = false;

                if (symbol >= silenceSymbols)
                {
                    int into = (symbol - silenceSymbols + phaseSymbols) % Math.Max(1, period);

                    on = into < onSymbols;
                }

                double envelope = on ? 1.0 : 0.0;

                output[2 * sample] =
                    (float)((modulated[2 * sample] * envelope) + Gaussian(noise, sigma));
                output[(2 * sample) + 1] =
                    (float)((modulated[(2 * sample) + 1] * envelope) + Gaussian(noise, sigma));
            }

            return output;
        }

        private static double Gaussian(Random random, double sigma)
        {
            double first = 1.0 - random.NextDouble();
            double second = random.NextDouble();

            return sigma * Math.Sqrt(-2.0 * Math.Log(first)) *
                Math.Cos(2.0 * Math.PI * second);
        }

        private static DemodSettings Settings() =>
            new DemodSettings
            {
                Constellation = Constellation.Qpsk(),
                SymbolRateHz = SymbolRateHz,
                ResultLengthSymbols = 256,
                FilterSymbolSpan = 20,
                MeasurementFilter = PulseFilterType.RootRaisedCosine,
                MeasurementFilterAlpha = 0.35,
                ReferenceFilterAlpha = 0.35,
            };

        private static DemodSettings Enabled()
        {
            DemodSettings settings = Settings();

            settings.BurstSearchEnabled = true;

            return settings;
        }

        private static DemodResult Demodulate(float[] samples) =>
            new Demodulator().Run(samples, SampleRateHz, Enabled());

        /// <summary>The number that follows a phrase in a notice, in symbols.</summary>
        private static double Symbols(string notice, string after)
        {
            int at = notice.IndexOf(after, StringComparison.Ordinal) + after.Length;
            int end = at;

            while (end < notice.Length &&
                (char.IsDigit(notice[end]) || notice[end] == '.' || notice[end] == '-'))
            {
                end++;
            }

            return double.Parse(
                notice.Substring(at, end - at), CultureInfo.InvariantCulture);
        }

        private static string Notice(DemodResult result, string containing)
        {
            foreach (string notice in result.Notices)
            {
                if (notice.IndexOf(containing, StringComparison.Ordinal) >= 0)
                {
                    return notice;
                }
            }

            return null;
        }
    }
}
