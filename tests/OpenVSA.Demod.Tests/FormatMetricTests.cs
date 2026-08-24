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
    /// <c>REQ-DEM-070</c>: the metrics that belong to particular formats.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The requirement names six — amplitude droop, FSK error, FSK deviation, carrier offset, pilot
    /// level and time offset — and asks that each be checked against a deliberately injected
    /// impairment of known magnitude.
    /// </para>
    /// <para>
    /// <strong>Three of them are testable against this catalogue and three are not.</strong> FSK
    /// error and FSK deviation need an FSK format and pilot level needs a VSB one; the catalogue has
    /// PSK, QAM, APSK and ASK, and <c>REQ-DEM-010</c>'s remaining families are #125. Those three are
    /// left, and said to be left, rather than tested against something they are not.
    /// </para>
    /// </remarks>
    public class FormatMetricTests
    {
        private const double SymbolRateHz = 1e6;
        private const double SampleRateHz = 16e6;
        private const int Symbols = 4000;

        private readonly ITestOutputHelper _output;

        public FormatMetricTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Theory]
        [InlineData(-0.002)]
        [InlineData(-0.010)]
        [InlineData(0.004)]
        public void AmplitudeDroopIsReadBackFromAKnownLogMagnitudeSlope(double dbPerSymbol)
        {
            // "amplitude droop in dB/symbol from a signal with a known log-magnitude slope."
            //
            // The injection is exactly what the metric is defined as: the envelope is multiplied by
            // 10^(slope * k / 20) at symbol k, so the logarithm of the magnitude is linear in the
            // symbol index by construction and the slope is the number the fit should return.
            DemodResult result = Demodulate(droopDbPerSymbol: dbPerSymbol);

            double read = result.Impairments.AmplitudeDroopDbPerSymbol;

            _output.WriteLine(
                "injected " + dbPerSymbol.ToString("F4", CultureInfo.InvariantCulture) +
                " dB/symbol, read " + read.ToString("F6", CultureInfo.InvariantCulture) +
                " dB/symbol");

            Assert.True(
                Math.Abs(read - dbPerSymbol) < Math.Abs(dbPerSymbol) * 0.02,
                "injected " + dbPerSymbol + " dB/symbol and read " + read);
        }

        [Fact]
        public void AnExactlyLinearDroopLeavesAFitResidualNearZero()
        {
            // "Amplitude droop comes from a linear fit of log magnitude versus symbol index,
            // verified by a signal whose droop is exactly linear returning a fit residual near
            // zero."
            //
            // The residual is what the requirement asks to see, so it is computed here from the
            // reported slope rather than trusted: log|z_k| - log|r_k| against a straight line
            // through it. A fit that had found the wrong slope, or that had fitted the RATIO
            // instead of its logarithm, would leave a residual with obvious curvature in it.
            const double Slope = -0.006;

            DemodResult result = Demodulate(droopDbPerSymbol: Slope);

            double read = result.Impairments.AmplitudeDroopDbPerSymbol;

            IReadOnlyList<ConstellationPoint> measured = result.Trace.Measured;
            IReadOnlyList<ConstellationPoint> ideal = result.Trace.Ideal;

            double mean = 0.0;
            var residuals = new List<double>(measured.Count);

            for (int symbol = 0; symbol < measured.Count; symbol++)
            {
                double got = Math.Sqrt(
                    (measured[symbol].I * measured[symbol].I) +
                    (measured[symbol].Q * measured[symbol].Q));

                double want = Math.Sqrt(
                    (ideal[symbol].I * ideal[symbol].I) + (ideal[symbol].Q * ideal[symbol].Q));

                if (got < 1e-12 || want < 1e-12)
                {
                    continue;
                }

                double db = 20.0 * Math.Log10(got / want);

                residuals.Add(db - (read * symbol));
            }

            foreach (double residual in residuals)
            {
                mean += residual;
            }

            mean /= residuals.Count;

            double rms = 0.0;

            foreach (double residual in residuals)
            {
                rms += (residual - mean) * (residual - mean);
            }

            rms = Math.Sqrt(rms / residuals.Count);

            // Against the droop the fit is measuring across the block, which is the scale a residual
            // has to be small compared with to mean anything.
            double swing = Math.Abs(Slope) * measured.Count;

            _output.WriteLine(
                "slope " + read.ToString("F6", CultureInfo.InvariantCulture) +
                " dB/symbol over " + measured.Count + " symbols is a swing of " +
                swing.ToString("F3", CultureInfo.InvariantCulture) +
                " dB; the residual about the fit is " +
                rms.ToString("F4", CultureInfo.InvariantCulture) + " dB rms");

            Assert.True(
                rms < swing * 0.05,
                "the residual was " + rms + " dB rms against a " + swing + " dB swing");
        }

        [Fact]
        public void CarrierOffsetIsWhatTheBlockSearchFoundAndFrequencyErrorIsTheTotal()
        {
            // 🔴 REQ-DEM-070 names a carrier offset beside REQ-DEM-065's frequency error without
            // saying how they differ, and under REQ-DEM-065's own definition they would be one
            // number in two rows. Carr Ofst is therefore step 3's block-wide estimate alone, and
            // Freq Err is the total after step 8 has refined it -- so the DIFFERENCE between the
            // rows is what the decision-directed fit had to pull in, which is the quantity
            // REQ-DEM-036's lock tolerance is about. #431 carries the question.
            const double OffsetHz = 12e3;

            DemodResult result = Demodulate(carrierOffsetHz: OffsetHz);

            double coarse = Row(result, "Carr Ofst").Rms;
            double total = Row(result, "Freq Err").Rms;

            _output.WriteLine(
                "injected " + (OffsetHz / 1e3).ToString("F1", CultureInfo.InvariantCulture) +
                " kHz: the block search found " +
                coarse.ToString("F2", CultureInfo.InvariantCulture) +
                " Hz and the total applied was " +
                total.ToString("F2", CultureInfo.InvariantCulture) + " Hz, so step 8 pulled in " +
                (total - coarse).ToString("F2", CultureInfo.InvariantCulture) + " Hz");

            // The total is the injected offset, to the accuracy REQ-DEM-065 asks of it.
            Assert.True(Math.Abs(total - OffsetHz) < 0.1);

            // The coarse estimate is close but NOT the same number -- if it were, the two rows would
            // be the same reading and the split would be pointless.
            Assert.True(Math.Abs(coarse - OffsetHz) < 500.0);
            Assert.NotEqual(coarse, total);
        }

        [Theory]
        [InlineData(0.0)]
        [InlineData(0.25)]
        [InlineData(0.5)]
        public void TimeOffsetFollowsAKnownTimingShift(double shiftSymbols)
        {
            // "time offset against a known timing shift."
            //
            // 🔴 What is testable is the CHANGE, not the absolute value. The reported offset is where
            // the first symbol's decision instant falls inside the Result Length window, and where
            // that window opens depends on the Search Length and on step 7 -- so the absolute number
            // is a property of the whole chain's positioning rather than of the signal. Delaying the
            // signal by a known fraction of a symbol and reading how far the offset moved is a
            // statement about the measurement.
            DemodResult reference = Demodulate(delaySymbols: 0.0);
            DemodResult delayed = Demodulate(delaySymbols: shiftSymbols);

            double symbolPeriod = 1.0 / SymbolRateHz;

            double moved =
                (Row(delayed, "Time Offset").Rms - Row(reference, "Time Offset").Rms) /
                symbolPeriod;

            // A timing offset is only defined to within a symbol: a delay of one symbol is the same
            // measurement one symbol later.
            moved -= Math.Round(moved);

            double expected = shiftSymbols - Math.Round(shiftSymbols);

            _output.WriteLine(
                "delayed by " + shiftSymbols.ToString("F2", CultureInfo.InvariantCulture) +
                " symbols; the reported time offset moved by " +
                moved.ToString("F4", CultureInfo.InvariantCulture) + " symbols (" +
                (Row(delayed, "Time Offset").Rms * 1e9).ToString(
                    "F1", CultureInfo.InvariantCulture) + " ns against " +
                (Row(reference, "Time Offset").Rms * 1e9).ToString(
                    "F1", CultureInfo.InvariantCulture) + " ns)");

            Assert.True(
                Math.Abs(moved - expected) < 0.02 ||
                Math.Abs(Math.Abs(moved - expected) - 1.0) < 0.02,
                "a delay of " + shiftSymbols + " symbols moved the offset by " + moved);
        }

        [Fact]
        public void EachMetricAppearsOnlyForTheFormatsItAppliesTo()
        {
            // "Each metric appears only for formats it applies to, per REQ-DEM-071."
            Assert.True(MetricApplicability.Applies("Amp Droop", ModulationFamily.Msk, false));
            Assert.False(MetricApplicability.Applies("Amp Droop", ModulationFamily.Qam, false));

            Assert.True(MetricApplicability.Applies("Pilot Lvl", ModulationFamily.Vsb, false));
            Assert.False(MetricApplicability.Applies("Pilot Lvl", ModulationFamily.Psk, false));

            // Carrier offset and time offset are properties of any demodulation, so they apply
            // everywhere -- which is a statement worth asserting rather than assuming, because the
            // default in MetricApplicability is "applies" and a mistake there is silent.
            foreach (ModulationFamily family in new[]
            {
                ModulationFamily.Psk, ModulationFamily.Qam, ModulationFamily.Apsk,
                ModulationFamily.Fsk, ModulationFamily.Msk, ModulationFamily.Vsb,
            })
            {
                Assert.True(MetricApplicability.Applies("Carr Ofst", family, false));
                Assert.True(MetricApplicability.Applies("Time Offset", family, false));
            }

            _output.WriteLine(
                "QPSK shows: " +
                string.Join(
                    ", ",
                    MetricApplicability.LabelsFor(ModulationFamily.Psk, false).ToArray()));
        }

        [Fact]
        public void TheThreeMetricsThisCatalogueCannotYetShowAreNamed()
        {
            // 🔴 Not a test of behaviour -- a record of what REQ-DEM-070 asks for that cannot be
            // delivered against this catalogue, so that it is visible here rather than only in an
            // issue. FSK error and FSK deviation need an FSK format and pilot level needs a VSB one;
            // Constellation has PSK, QAM, APSK, ASK and Custom. #125 is the format catalogue.
            //
            // The applicability table already knows about the families, so the rows will appear the
            // moment the formats do -- and will read NAN until the metrics behind them exist, which
            // is REQ-DEM-071's own distinction between "does not apply" and "not measured".
            Assert.DoesNotContain("FSK Err", MetricApplicability.AllLabels);
            Assert.DoesNotContain("FSK Dev", MetricApplicability.AllLabels);

            // Pilot level HAS a row, and reads NAN, because VSB is a family the table knows even
            // though the catalogue has no VSB format to select.
            Assert.Contains("Pilot Lvl", MetricApplicability.AllLabels);

            _output.WriteLine(
                "still owed by REQ-DEM-070: FSK error and FSK deviation (need an FSK format), " +
                "pilot level (needs a VSB format). See #125.");
        }

        private static ErrorMetric Row(DemodResult result, string label) =>
            result.Summary.Metrics.Single(metric => metric.Label == label);

        private static DemodResult Demodulate(
            double droopDbPerSymbol = 0.0,
            double carrierOffsetHz = 0.0,
            double delaySymbols = 0.0)
        {
            var source = new ContinuousModulatedSource
            {
                Scheme = ModulationScheme.Qpsk(),
                SymbolRateHz = SymbolRateHz,
                SampleRateHz = SampleRateHz,
                RollOff = 0.35,
                PulseSpanSymbols = 20,
                Seed = 20260824,
                CarrierOffsetHz = carrierOffsetHz,
            };

            var samples = new float[2 * Symbols * 16];

            source.Restart();
            source.Fill(samples);

            if (delaySymbols != 0.0)
            {
                samples = Delayed(samples, delaySymbols * SampleRateHz / SymbolRateHz);
            }

            if (droopDbPerSymbol != 0.0)
            {
                Droop(samples, droopDbPerSymbol);
            }

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

            return new Demodulator().Run(samples, SampleRateHz, settings);
        }

        /// <summary>Multiplies the envelope by a straight line in decibels.</summary>
        /// <remarks>
        /// Per SYMBOL, not per sample, because that is the unit <c>REQ-DEM-070</c> states the metric
        /// in — and applying it per sample would inject a slope sixteen times steeper here.
        /// </remarks>
        private static void Droop(float[] samples, double dbPerSymbol)
        {
            double perSample = SampleRateHz / SymbolRateHz;

            for (int sample = 0; sample < samples.Length; sample += 2)
            {
                double symbol = (sample / 2) / perSample;
                double gain = Math.Pow(10.0, dbPerSymbol * symbol / 20.0);

                samples[sample] = (float)(samples[sample] * gain);
                samples[sample + 1] = (float)(samples[sample + 1] * gain);
            }
        }

        /// <summary>Delays a waveform by a fractional number of samples, by linear interpolation.</summary>
        private static float[] Delayed(float[] samples, double bySamples)
        {
            int count = samples.Length / 2;
            var moved = new float[samples.Length];

            for (int sample = 0; sample < count; sample++)
            {
                double from = sample - bySamples;
                int whole = (int)Math.Floor(from);
                double fraction = from - whole;

                if (whole < 0 || whole + 1 >= count)
                {
                    continue;
                }

                moved[2 * sample] = (float)(
                    (samples[2 * whole] * (1.0 - fraction)) +
                    (samples[2 * (whole + 1)] * fraction));

                moved[(2 * sample) + 1] = (float)(
                    (samples[(2 * whole) + 1] * (1.0 - fraction)) +
                    (samples[(2 * (whole + 1)) + 1] * fraction));
            }

            return moved;
        }
    }
}
