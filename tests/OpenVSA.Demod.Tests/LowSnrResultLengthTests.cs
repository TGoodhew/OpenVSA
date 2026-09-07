using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    /// <c>REQ-DEM-013</c>: the low SNR enhancement extends an offset format's Result Length from
    /// about 2 048 symbols to about 40 000, and the longer window genuinely analyses more.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The criterion's second half is the interesting one. A raised limit that truncated internally
    /// would accept 40 000 symbols and pass the first half, so what is checked is the consequence a
    /// longer analysis has and a truncated one cannot fake: the spread of the EVM estimate across
    /// independent noise falls with the square root of the symbol count.
    /// </para>
    /// <para>
    /// <strong>The criterion says "variance" where the law it quotes is the standard
    /// deviation's.</strong> Variance falls as 1/N; the standard error falls as
    /// 1/sqrt(N), which is what the criterion writes. The tests measure the standard deviation and
    /// compare it against sqrt(N2/N1), because that is the relationship the quoted formula
    /// describes; the variance ratio is reported beside it so the reading is visible rather than
    /// quietly chosen.
    /// </para>
    /// </remarks>
    public class LowSnrResultLengthTests
    {
        private const double SymbolRateHz = 1e6;
        private const double SampleRateHz = 4e6;
        private const int PerSymbol = 2;

        /// <summary>Symbols skipped before the analysis starts, past the generator's ramp.</summary>
        private const int LeadInSymbols = 200;

        private readonly ITestOutputHelper _output;

        public LowSnrResultLengthTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void AnOffsetFormatIsCappedNearTwoThousandSymbolsWithTheOptionOff()
        {
            DemodSettings settings = Settings(Constellation.Oqpsk(), 2048);

            Assert.Equal(2048, DemodSettings.OffsetResultLengthLimit);
            Assert.Equal(2048, settings.MaximumResultLengthSymbols);

            // At the limit: allowed.
            settings.Validate();

            // One past it: refused, by name, with the way out in the message.
            settings.ResultLengthSymbols = 2049;

            ArgumentException refused =
                Assert.Throws<ArgumentException>(() => settings.Validate());

            _output.WriteLine(refused.Message);

            Assert.Contains("OQPSK", refused.Message, StringComparison.Ordinal);
            Assert.Contains("2048", refused.Message, StringComparison.Ordinal);
            Assert.Contains("REQ-DEM-013", refused.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void TheOptionRaisesTheLimitToFortyThousand()
        {
            DemodSettings settings = Settings(Constellation.Oqpsk(), 40000);

            Assert.Throws<ArgumentException>(() => settings.Validate());

            settings.LowSnrEnhancement = true;

            Assert.Equal(40000, settings.MaximumResultLengthSymbols);

            settings.Validate();
        }

        [Fact]
        public void ANonOffsetFormatWasNeverSubjectToTheShorterLimit()
        {
            // The requirement's limit is on offset formats, and lifting it for the rest would be
            // inventing a restriction to relieve. QPSK takes 40 000 symbols with the option off.
            DemodSettings settings = Settings(Constellation.Qpsk(), 40000);

            Assert.Equal(40000, settings.MaximumResultLengthSymbols);

            settings.Validate();

            // And setting the option on a format it does not apply to is not an error: settings are
            // carried from one format to the next, and refusing here would make changing format
            // fail for a reason the user did not cause.
            settings.LowSnrEnhancement = true;

            settings.Validate();
        }

        [Fact]
        public void FortyThousandSymbolsAreActuallyDemodulated()
        {
            // The first half of the criterion: "at least 40 000 symbols are accepted AND
            // demodulated". Accepted is the validation above; this is the demodulated.
            var clock = Stopwatch.StartNew();

            DemodResult result = Demodulate(40000, 12.0, 1, out int available);

            clock.Stop();

            _output.WriteLine(
                "40 000 symbols asked for, " + result.Trace.SymbolCount + " returned from a " +
                available + " symbol record, in " + clock.ElapsedMilliseconds + " ms; EVM " +
                result.EvmPercent.ToString("F4", CultureInfo.InvariantCulture) + " %rms");

            Assert.True(
                result.Trace.SymbolCount >= 40000,
                "The chain returned " + result.Trace.SymbolCount +
                " symbols, so something truncated the window.");

            Assert.Equal(40000, result.Trace.SymbolCount);

            // No notice about a shortened window: the record was long enough, and a note here would
            // mean the count above came from somewhere other than the setting.
            Assert.DoesNotContain(
                result.Notices,
                notice => notice.IndexOf("had room for", StringComparison.Ordinal) >= 0);
        }

        [Fact]
        public void TheLongerWindowSteadiesTheEstimateAsTheSquareRootOfTheCount()
        {
            // The half of the criterion a truncating implementation could not fake. Independent
            // noise on each run, the same signal otherwise, and the spread of the EVM estimate
            // measured at both lengths.
            const int Short = 2048;
            const int Long = 40000;
            const int Runs = 7;
            const double Snr = 12.0;

            // ONE RECORD LENGTH FOR BOTH ARMS. Sizing the record to the Result Length made the
            // record a second thing that differed between them, and a difference in the result
            // could then have belonged to either. The long arm needs the longer record, so both
            // get it.
            int record = (int)(Long * 1.35) + 200 + LeadInSymbols;

            double[] atShort = Estimates(Short, Snr, Runs, record, out double[] carrierShort);
            double[] atLong = Estimates(Long, Snr, Runs, record, out double[] carrierLong);

            // WHY THE RATIO BEATS sqrt(N), REPORTED RATHER THAN LEFT AS A SURPRISE. Step 8 fits the
            // residual carrier over the window, and the error of a linear phase fit falls far
            // faster with the symbol count than a per-symbol statistic does. So the EVM estimate
            // has two components that both shrink with N, and the total shrinks faster than the
            // per-symbol one alone. This line is what says so.
            _output.WriteLine(
                "carrier estimate sd: " + Sigma(carrierShort).ToString("F3") + " Hz at " + Short +
                ", " + Sigma(carrierLong).ToString("F3") + " Hz at " + Long + " -- ratio " +
                (Sigma(carrierShort) / Sigma(carrierLong)).ToString("F1"));

            double shortSigma = Sigma(atShort);
            double longSigma = Sigma(atLong);

            double ratio = shortSigma / longSigma;
            double expected = Math.Sqrt((double)Long / Short);

            _output.WriteLine(
                Short + " symbols: mean " + Mean(atShort).ToString("F4") + " %rms, sd " +
                shortSigma.ToString("F5"));

            _output.WriteLine(
                Long + " symbols: mean " + Mean(atLong).ToString("F4") + " %rms, sd " +
                longSigma.ToString("F5"));

            _output.WriteLine(
                "sd ratio " + ratio.ToString("F2") + " against sqrt(N2/N1) = " +
                expected.ToString("F2") + "; variance ratio " +
                (ratio * ratio).ToString("F1") + " against N2/N1 = " +
                ((double)Long / Short).ToString("F1"));

            // The estimate is of the same quantity, so the two means agree: a longer analysis
            // steadies the estimate, it does not move it. Checked first, because a ratio of
            // spreads about two different numbers would say nothing about either.
            //
            // As a RELATIVE difference rather than to a decimal place: these are two estimates of
            // the same quantity from different amounts of data, so they are expected to agree
            // within their own uncertainty and not to round to the same digit.
            double separation =
                Math.Abs(Mean(atShort) - Mean(atLong)) / Mean(atLong);

            _output.WriteLine("means differ by " + (separation * 100.0).ToString("F2") + " %");

            // Two per cent. The residual difference is about 1.4 %, and it is not noise: it is the
            // result window's leading edge, which sits on the measurement filter's transient and is
            // a far larger FRACTION of 2 048 symbols than of 40 000. Measured separately in
            // TheShortWindowReadsHigherBecauseOfItsLeadingEDGE_NotItsLength, which finds the
            // leading 2 048 symbols of one 40 000-symbol result reading 1.5 % above an interior
            // 2 048 of the same result -- the same figure, from a comparison with no length
            // difference in it at all.
            Assert.True(
                separation < 0.02,
                "The two lengths measured different quantities: " + Mean(atShort) + " against " +
                Mean(atLong) + ", " + (separation * 100.0).ToString("F2") + " % apart.");

            // A LOWER BOUND, NOT A BAND, AND THAT IS THE HONEST SHAPE OF THE CLAIM. sqrt(N2/N1)
            // is what the per-symbol statistics alone would give, and the measurement beats it --
            // 7.6 against 4.4 -- because the residual carrier estimate ALSO improves with the
            // window, and far faster: the line above measures its spread falling by 76x, which is
            // N^1.5, the scaling of a linear phase fit. Two components shrink, so the total shrinks
            // faster than either. Asserting a two-sided band around 4.42 would be asserting that
            // the second mechanism does not exist.
            //
            // The tolerance on the bound is the sampling error of a standard deviation, not a round
            // number: one from n samples is itself uncertain by about 1/sqrt(2(n-1)), which at
            // seven runs is 29 % on each of the two, so their ratio carries about 41 %. Written
            // out because the first version of this line had sqrt(2/(n-1)) -- 58 % each, 82 % on
            // the ratio -- which put the floor at 0.81 and would have passed an implementation
            // that truncated internally and steadied nothing at all.
            double uncertainty = 1.0 / Math.Sqrt(2.0 * (Runs - 1));
            double band = Math.Sqrt(2.0) * uncertainty;

            _output.WriteLine(
                "floor " + (expected * (1.0 - band)).ToString("F2") + " (= sqrt(N2/N1) less " +
                (band * 100.0).ToString("F0") + " % sampling error)");

            Assert.True(
                ratio >= expected * (1.0 - band),
                "The estimate steadied by " + ratio.ToString("F2") + "x, short of the " +
                expected.ToString("F2") + "x that " + Long + " symbols against " + Short +
                " implies even before the carrier estimate is counted.");

            // What the floor is really for: a window that truncated internally would analyse the
            // same 2 048 symbols under both settings and steady nothing, giving a ratio near one.
            Assert.True(
                expected * (1.0 - band) > 1.5,
                "The floor is " + (expected * (1.0 - band)) + ", which is too near the 1.0 a " +
                "truncating implementation would give for this test to discriminate. Raise the " +
                "run count.");
        }

        [Fact]
        public void TheShortWindowReadsHigherBecauseOfItsLeadingEDGE_NotItsLength()
        {
            // A control for the test above, and the answer to the one thing in it that does not
            // follow from the symbol count. The 2 048-symbol window reads about 1.4 % higher than
            // the 40 000-symbol one, which is far more than the residual carrier accounts for --
            // 0.8 Hz over 2 048 symbols is 0.6 degrees of drift, worth 0.3 % of EVM in quadrature
            // with 12.6 %, or 0.03 % of the reading.
            //
            // The candidate left is the window's own leading edge: the chain starts the result
            // window AT the measurement filter's transient, so a fixed number of symbols there are
            // slightly degraded, and a fixed number is 0.6 % of 2 048 symbols against 0.03 % of
            // 40 000. That is checked here inside ONE result, so the length, the record, the noise
            // and the carrier estimate are all held identical and only the position varies.
            DemodResult result = Demodulate(40000, 12.0, 1, out int available);

            double leading = RmsErrorOver(result, 0, 2048);
            double middle = RmsErrorOver(result, 20000, 22048);
            double trailing = RmsErrorOver(result, 37952, 40000);

            _output.WriteLine(
                "rms error over symbols 0-2048: " + leading.ToString("F4") +
                "; 20000-22048: " + middle.ToString("F4") +
                "; 37952-40000: " + trailing.ToString("F4"));

            _output.WriteLine(
                "leading edge is " +
                (((leading / middle) - 1.0) * 100.0).ToString("F2") + " % above the middle");

            // The leading 2 048 symbols read higher than an interior 2 048 of the same result. If
            // this were flat, the difference in the test above would need another explanation and
            // this comment would be wrong.
            Assert.True(
                leading > middle,
                "The window's leading symbols read " + leading + " against " + middle +
                " in the middle, so the edge is not what raises the short window's reading.");
        }

        /// <summary>The rms error magnitude over a range of symbols, in the trace's own units.</summary>
        /// <param name="result">The demodulation.</param>
        /// <param name="from">First symbol.</param>
        /// <param name="to">One past the last.</param>
        private static double RmsErrorOver(DemodResult result, int from, int to)
        {
            double sum = 0.0;

            for (int symbol = from; symbol < to; symbol++)
            {
                ConstellationPoint error = result.Trace.ErrorAt(symbol);

                sum += (error.I * error.I) + (error.Q * error.Q);
            }

            return Math.Sqrt(sum / (to - from));
        }

        private double[] Estimates(int resultLength, double snrDb, int runs) =>
            Estimates(resultLength, snrDb, runs, out double[] ignored);

        private double[] Estimates(
            int resultLength, double snrDb, int runs, out double[] carrierErrorsHz) =>
            Estimates(resultLength, snrDb, runs, 0, out carrierErrorsHz);

        private double[] Estimates(
            int resultLength, double snrDb, int runs, int recordSymbols,
            out double[] carrierErrorsHz)
        {
            var values = new List<double>(runs);
            var carriers = new List<double>(runs);

            for (int run = 0; run < runs; run++)
            {
                DemodResult result = Demodulate(
                    resultLength, snrDb, run + 1, recordSymbols, out int available);

                Assert.Equal(resultLength, result.Trace.SymbolCount);

                values.Add(result.EvmPercent);
                carriers.Add(result.CarrierFrequencyErrorHz);
            }

            carrierErrorsHz = carriers.ToArray();

            return values.ToArray();
        }

        private static double Mean(IReadOnlyList<double> values) => values.Average();

        private static double Sigma(IReadOnlyList<double> values)
        {
            double mean = Mean(values);
            double sum = values.Sum(value => (value - mean) * (value - mean));

            return Math.Sqrt(sum / (values.Count - 1));
        }

        private static DemodResult Demodulate(
            int resultLength, double snrDb, int seed, out int availableSymbols) =>
            Demodulate(resultLength, snrDb, seed, 0, out availableSymbols);

        /// <summary>Demodulates one record.</summary>
        /// <param name="resultLength">Result Length, in symbols.</param>
        /// <param name="snrDb">Signal-to-noise ratio of the generated signal.</param>
        /// <param name="seed">Which noise realisation.</param>
        /// <param name="recordSymbols">
        /// The record length to generate, or zero to size it to the Result Length. Fixing it is
        /// what lets two Result Lengths be compared without the record varying with them.
        /// </param>
        /// <param name="availableSymbols">The record length actually generated.</param>
        private static DemodResult Demodulate(
            int resultLength, double snrDb, int seed, int recordSymbols, out int availableSymbols)
        {
            Constellation format = Constellation.Oqpsk();

            var points = new List<SymbolPoint>(format.Count);

            foreach (ConstellationPoint point in format.Points)
            {
                points.Add(new SymbolPoint(point.I, point.Q));
            }

            // Room for the Result Length, the filter's transient at each end, and the 20 % the
            // pre-demodulation window of REQ-DEM-032 takes beyond it.
            availableSymbols = recordSymbols > 0
                ? recordSymbols
                : (int)(resultLength * 1.35) + 200 + LeadInSymbols;

            var source = new ContinuousModulatedSource
            {
                Scheme = ModulationScheme.FromPoints(
                    format.Name, points, format.IsOffset, format.RotationPerSymbolRadians),
                SymbolRateHz = SymbolRateHz,
                SampleRateHz = SampleRateHz,
                RollOff = 0.35,
                PulseSpanSymbols = 12,
                SignalToNoiseDb = snrDb,
                Seed = 20260907 + seed,
            };

            var samples = new float[2 * (int)(availableSymbols * (SampleRateHz / SymbolRateHz))];

            source.Restart();
            source.Fill(samples);

            DemodSettings settings = Settings(format, resultLength);

            settings.LowSnrEnhancement = resultLength > DemodSettings.OffsetResultLengthLimit;

            // THE LEAD-IN IS A CONTROL, NOT A CONVENIENCE. The generator's own pulse shaping ramps
            // up over its first PulseSpanSymbols, and the chain starts the result window at the
            // measurement filter's transient -- a FIXED number of contaminated symbols, which is a
            // far larger FRACTION of 2 048 symbols than of 40 000. Left in, it adds both bias and
            // run-to-run spread to the short window only, and this test would be reading that
            // difference as the window-length effect it is trying to measure.
            settings.SearchStartSample =
                (int)(LeadInSymbols * (SampleRateHz / SymbolRateHz));

            return new Demodulator().Run(samples, SampleRateHz, settings);
        }

        private static DemodSettings Settings(Constellation format, int resultLength) =>
            new DemodSettings
            {
                Constellation = format,
                SymbolRateHz = SymbolRateHz,
                PointsPerSymbol = PerSymbol,
                ResultLengthSymbols = resultLength,
                FilterSymbolSpan = 12,
                MeasurementFilter = PulseFilterType.RootRaisedCosine,
                MeasurementFilterAlpha = 0.35,
                ReferenceFilterAlpha = 0.35,
            };
    }
}
