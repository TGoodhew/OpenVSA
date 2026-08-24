using System;
using System.Globalization;
using OpenVSA.Demod.Chain;
using OpenVSA.Demod.Results;
using OpenVSA.Demod.Signal;
using OpenVSA.Synthesis;
using Xunit;
using Xunit.Abstractions;

namespace OpenVSA.Demod.Tests
{
    /// <summary>
    /// <c>REQ-DEM-036</c>: carrier lock tolerance, and a diagnostic that names the cause.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The acceptance criterion is "each of those four fault conditions, injected deliberately,
    /// produces the corresponding diagnostic rather than a bare 'demodulation failed'". So each of
    /// the four is injected into the same signal, one at a time, and the assertion is that the named
    /// cause is among the ones reported — and, where the fault does not implicate another, that the
    /// others are not.
    /// </para>
    /// <para>
    /// <strong>Each injection must actually break the lock.</strong> A fault that leaves the
    /// demodulation working is not a fault condition, and a test that injected one would be
    /// asserting against a diagnosis that never ran. So each case prints its EVM, and the control
    /// case asserts the same signal locks cleanly when nothing is injected — without which none of
    /// this would show that the injections are what broke it.
    /// </para>
    /// <para>
    /// <strong>Three of the four are injected alone; the fourth cannot be.</strong> A Result Length
    /// below the format's recommendation does not break lock in this chain — measured across four
    /// formats down to four-symbol windows and 15 dB, in
    /// <c>evidence/req-dem-036/result-length-sweep.txt</c> — so it is injected alongside a filter
    /// mismatch that does, with a paired control showing the short window alone locking and being
    /// accused of nothing. <c>#429</c> carries what the requirement should say about it.
    /// </para>
    /// <para>
    /// <strong>Two of the cases are pairs rather than single injections</strong>, and that is the
    /// part worth reading. A rule that fires is easy; a rule that fires on the fault and stays quiet
    /// on the near miss is the one worth having, and the carrier pair —
    /// <see cref="ACentreFrequencyBeyondTheCoarseSearchIsNamedAsTheCentreFrequency"/> against
    /// <see cref="ASignalOffCentreWithinTheCoarseSearchIsNotBlamedOnItsCentreFrequency"/> — is what
    /// shows the tolerance is being applied to the residual and not to the acquisition.
    /// </para>
    /// </remarks>
    public class LockDiagnosticTests
    {
        private const double SymbolRateHz = 1e6;
        private const double SampleRateHz = 16e6;
        private const int Symbols = 4000;

        private readonly ITestOutputHelper _output;

        public LockDiagnosticTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void AGoodSignalLocksAndTheMeasurementsAgreeWithIt()
        {
            // The control. Nothing injected: it locks, no cause is named, and the four quantities
            // the diagnosis is built on read what the generator was told to produce. Without this
            // the four cases below would show only that SOMETHING was reported.
            DemodResult result = Demodulate(Source(), Settings());

            Say("no fault injected", result);

            Assert.True(result.Lock.Locked);
            Assert.Empty(result.Lock.Causes);
            Assert.Equal(string.Empty, result.Lock.Explanation);

            // The symbol rate read off the signal, against the one it was generated at.
            Assert.True(
                Math.Abs(result.Lock.MeasuredSymbolRateHz - SymbolRateHz) < SymbolRateHz * 0.005,
                "the symbol-rate line was read at " + result.Lock.MeasuredSymbolRateHz);

            // A root-raised-cosine transmitter's power spectrum and a root-raised-cosine filter's
            // energy spectrum are the same function, so a matched pair reads the same width. That
            // is what makes a ratio of one the baseline the filter test is measured against.
            double ratio = result.Lock.OccupiedBandwidthHz / result.Lock.FilterBandwidthHz;

            _output.WriteLine(
                "matched pair: signal / filter = " +
                ratio.ToString("F3", CultureInfo.InvariantCulture));

            Assert.True(ratio > 0.9 && ratio < 1.1, "matched bandwidths differed by " + ratio);
        }

        [Fact]
        public void AWrongSymbolRateIsNamedAsTheSymbolRate()
        {
            // Five per cent out. Over a 256-symbol Result Length that is 12.8 symbols of timing
            // drift, which no single timing offset can absorb -- and the chain cannot notice on its
            // own, because REQ-DEM-030 makes the symbol rate an input rather than an estimate.
            DemodSettings settings = Settings();

            settings.SymbolRateHz = SymbolRateHz * 1.05;

            DemodResult result = Demodulate(Source(), settings);

            Say("symbol rate 5 % high", result);

            Assert.False(result.Lock.Locked);
            Assert.Contains(LockFault.SymbolRate, result.Lock.Causes);

            // And it says what the rate really is, not merely that it is wrong.
            Assert.True(
                Math.Abs(result.Lock.MeasuredSymbolRateHz - SymbolRateHz) < SymbolRateHz * 0.005,
                "the signal's own rate was read as " + result.Lock.MeasuredSymbolRateHz);

            // Five per cent moves the filter by five per cent too, which is inside the tolerance --
            // so this fault does not drag the filter in with it.
            Assert.DoesNotContain(LockFault.Filter, result.Lock.Causes);
            Assert.DoesNotContain(LockFault.CentreFrequency, result.Lock.Causes);
            Assert.DoesNotContain(LockFault.ResultLength, result.Lock.Causes);
        }

        [Fact]
        public void AWrongFilterIsNamedAsTheFilter()
        {
            // A Gaussian of bandwidth-time 0.15 against a signal shaped by a root raised cosine at
            // 0.35: a filter far narrower than the signal, which is the mismatch that destroys a
            // demodulation rather than merely degrading it.
            DemodSettings settings = Settings();

            settings.MeasurementFilter = PulseFilterType.Gaussian;
            settings.MeasurementFilterBandwidthTime = 0.15;

            DemodResult result = Demodulate(Source(), settings);

            Say("Gaussian BT 0.15 against a root-raised-cosine signal", result);

            Assert.False(result.Lock.Locked);
            Assert.Contains(LockFault.Filter, result.Lock.Causes);

            Assert.DoesNotContain(LockFault.SymbolRate, result.Lock.Causes);
            Assert.DoesNotContain(LockFault.CentreFrequency, result.Lock.Causes);
            Assert.DoesNotContain(LockFault.ResultLength, result.Lock.Causes);
        }

        [Fact]
        public void ACentreFrequencyBeyondTheCoarseSearchIsNamedAsTheCentreFrequency()
        {
            // Tuned 1.2 symbol rates away, in an analysis span four times the symbol rate -- which
            // is what an analyser gives you, and the reason the offset that breaks this is a
            // fraction of the span rather than a fraction of a symbol.
            //
            // 🔴 Step 3 estimates the carrier over the whole block and takes it out before anything
            // else runs, so this chain demodulates a signal well outside REQ-DEM-036's +/-10 %
            // perfectly happily -- the paired test below shows it doing so at four times the
            // tolerance. What it cannot do is distinguish an offset beyond the range that raising
            // the signal to its rotational symmetry reaches, a quarter of the sample rate here; past
            // that the estimate aliases and step 3 removes the wrong amount. That is why the
            // diagnosis judges what step 3 LEFT.
            const double SpanHz = 4e6;
            const double OffsetHz = 1.2e6;

            ContinuousModulatedSource source = Source();

            source.SampleRateHz = SpanHz;
            source.CarrierOffsetHz = OffsetHz;

            DemodResult result = Demodulate(source, Settings(), SpanHz);

            Say("carrier 1.2 MHz off centre in a 4 MHz span", result);
            SayCentre(result);

            Assert.False(result.Lock.Locked);
            Assert.Contains(LockFault.CentreFrequency, result.Lock.Causes);

            // The offset it reports is the one that was injected, to a fiftieth of the symbol rate.
            // The circular first moment locates a signal; it is not a carrier estimator, and step 3
            // is the thing that measures the offset properly.
            Assert.True(
                Math.Abs(result.Lock.CentreOffsetHz - OffsetHz) < SymbolRateHz * 0.02,
                "the offset was read as " + result.Lock.CentreOffsetHz);

            Assert.DoesNotContain(LockFault.SymbolRate, result.Lock.Causes);
            Assert.DoesNotContain(LockFault.ResultLength, result.Lock.Causes);
        }

        [Fact]
        public void ASignalOffCentreWithinTheCoarseSearchIsNotBlamedOnItsCentreFrequency()
        {
            // The other half of the case above, and what makes it a measurement rather than a rule:
            // a carrier 40 % of the symbol rate off centre -- four times REQ-DEM-036's tolerance --
            // locks, because step 3 removed it. Judging the offset the signal ARRIVED with would
            // have blamed the centre frequency here, on a demodulation that worked.
            const double SpanHz = 4e6;

            ContinuousModulatedSource source = Source();

            source.SampleRateHz = SpanHz;
            source.CarrierOffsetHz = SymbolRateHz * 0.4;

            DemodResult result = Demodulate(source, Settings(), SpanHz);

            Say("carrier 400 kHz off centre in a 4 MHz span", result);
            SayCentre(result);

            Assert.True(result.Lock.Locked);
            Assert.Empty(result.Lock.Causes);

            // Four times the tolerance on arrival, a fraction of it once step 3 had finished.
            Assert.True(Math.Abs(result.Lock.CentreOffsetHz) > result.Lock.CentreToleranceHz * 3.0);
            Assert.True(Math.Abs(result.Lock.ResidualOffsetHz) < result.Lock.CentreToleranceHz);
        }

        [Fact]
        public void AResultLengthTooShortForTheFormatIsNamedAsTheResultLength()
        {
            // 🔴 This fault is injected ALONGSIDE another, and the reason is a measurement rather
            // than a convenience. A Result Length below the format's own recommendation does not
            // break lock in this chain: 256-QAM and 128-cross QAM were demodulated at four symbols
            // -- the shortest the settings allow, and a sixty-fourth of the recommendation -- at
            // signal-to-noise ratios down to 15 dB, and every one of them locked, none worse than
            // 13 %rms. The block estimators of REQ-DEM-002 fit carrier, timing and gain from very
            // few symbols, which is exactly what they were chosen to do.
            // evidence/req-dem-036/result-length-sweep.txt has the numbers, and #429 carries the
            // question of what REQ-DEM-036's fourth cause should therefore say.
            //
            // So what is tested is what the diagnosis can honestly claim: when a demodulation HAS
            // failed and the window is also too short for the format, the short window is named as
            // a cause alongside the one that broke it. The companion test below is the control that
            // keeps this honest -- the same short window, nothing else wrong, locks and is accused
            // of nothing.
            DemodSettings settings = Settings();

            settings.ResultLengthSymbols = 24;
            settings.MeasurementFilter = PulseFilterType.Gaussian;
            settings.MeasurementFilterBandwidthTime = 0.15;

            DemodResult result = Demodulate(Source(), settings);

            Say("QPSK in a 24-symbol Result Length, with a filter that breaks the lock", result);

            Assert.False(result.Lock.Locked);
            Assert.Contains(LockFault.ResultLength, result.Lock.Causes);
            Assert.Contains(LockFault.Filter, result.Lock.Causes);

            // And it says what the length should have been, from the format itself.
            Assert.Contains(
                Constellation.Qpsk().RecommendedResultLengthSymbols.ToString(
                    CultureInfo.InvariantCulture),
                result.Lock.Explanation);
        }

        [Fact]
        public void AShortResultLengthOnItsOwnLocksAndIsAccusedOfNothing()
        {
            // The control for the case above, and the finding in its own right: a window half the
            // recommended length demodulates. A diagnosis that named the Result Length whenever it
            // was short would be naming it on measurements that worked.
            DemodSettings settings = Settings();

            settings.ResultLengthSymbols = 24;

            DemodResult result = Demodulate(Source(), settings);

            Say("QPSK in a 24-symbol Result Length, nothing else wrong", result);

            Assert.NotNull(settings.ResultLengthAdvice);
            Assert.True(result.Lock.Locked);
            Assert.Empty(result.Lock.Causes);
        }

        [Fact]
        public void TheCausesComeBackInTheDocumentedOrderOfLikelihood()
        {
            // "in documented order of likelihood". A wrong symbol rate and a wrong filter at once:
            // both are named, and the symbol rate comes first because that is the order the
            // requirement documents and LockFault declares.
            DemodSettings settings = Settings();

            settings.SymbolRateHz = SymbolRateHz * 1.05;
            settings.MeasurementFilter = PulseFilterType.Gaussian;
            settings.MeasurementFilterBandwidthTime = 0.15;

            DemodResult result = Demodulate(Source(), settings);

            Say("both a wrong symbol rate and a wrong filter", result);

            Assert.Contains(LockFault.SymbolRate, result.Lock.Causes);
            Assert.Contains(LockFault.Filter, result.Lock.Causes);

            for (int cause = 1; cause < result.Lock.Causes.Count; cause++)
            {
                Assert.True(
                    result.Lock.Causes[cause] > result.Lock.Causes[cause - 1],
                    "the causes came back as " + result.Lock);
            }

            // And the sentences are in that order too, not merely the enumeration.
            Assert.True(
                result.Lock.Explanation.IndexOf("THE SYMBOL RATE", StringComparison.Ordinal) <
                result.Lock.Explanation.IndexOf("THE FILTER", StringComparison.Ordinal),
                result.Lock.Explanation);
        }

        [Fact]
        public void ADiagnosisIsSaidOutLoudAndIsNotJustAvailable()
        {
            // A diagnosis nobody is shown is not one. The shell says the notices; the explanation
            // has to be among them.
            DemodSettings settings = Settings();

            settings.SymbolRateHz = SymbolRateHz * 1.05;

            DemodResult result = Demodulate(Source(), settings);

            Assert.Contains(result.Lock.Explanation, result.Notices);
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

        private static DemodResult Demodulate(
            ContinuousModulatedSource source, DemodSettings settings)
        {
            return Demodulate(source, settings, SampleRateHz);
        }

        private static DemodResult Demodulate(
            ContinuousModulatedSource source, DemodSettings settings, double sampleRateHz)
        {
            var samples = new float[2 * (int)Math.Ceiling(Symbols * source.SamplesPerSymbol)];

            source.Restart();
            source.Fill(samples);

            return new Demodulator().Run(samples, sampleRateHz, settings);
        }

        private void SayCentre(DemodResult result)
        {
            _output.WriteLine(
                "  arrived " +
                (result.Lock.CentreOffsetHz / 1e3).ToString("F1", CultureInfo.InvariantCulture) +
                " kHz off centre; step 3 left " +
                (result.Lock.ResidualOffsetHz / 1e3).ToString("F1", CultureInfo.InvariantCulture) +
                " kHz of it, against a tolerance of " +
                (result.Lock.CentreToleranceHz / 1e3).ToString("F1", CultureInfo.InvariantCulture) +
                " kHz");
        }

        private void Say(string what, DemodResult result)
        {
            LockReport report = result.Lock;

            _output.WriteLine(what + ": " + report);
            _output.WriteLine(
                "  symbol-rate line " +
                (report.MeasuredSymbolRateHz / 1e3).ToString("F2", CultureInfo.InvariantCulture) +
                " kHz; signal " +
                (report.OccupiedBandwidthHz / 1e3).ToString("F1", CultureInfo.InvariantCulture) +
                " kHz wide; filter " +
                (report.FilterBandwidthHz / 1e3).ToString("F1", CultureInfo.InvariantCulture) +
                " kHz wide");

            if (!report.Locked)
            {
                _output.WriteLine("  " + report.Explanation);
            }
        }
    }
}
