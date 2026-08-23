using System;
using System.Collections.Generic;
using OpenVSA.Core;
using OpenVSA.Demod.Chain;
using OpenVSA.Demod.Signal;
using OpenVSA.Dsp.Spectrum;
using OpenVSA.Measurement.Contexts;
using OpenVSA.Measurement.State;
using OpenVSA.TestHarness.Synthesis;
using Xunit;
using Xunit.Abstractions;

namespace OpenVSA.Measurement.Tests
{
    /// <summary>
    /// The demodulation leg of a measurement context: <c>REQ-DEM-001</c>'s chain, driven by an
    /// acquisition rather than by a test, and <c>REQ-NFR-032a</c>'s "a full demodulation
    /// measurement runs to an error summary".
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Contexts are where this belongs.</strong> <c>ContextAnalyser</c> was built so that
    /// "a spectrum and a demodulation are looking at the same acquisition rather than at two
    /// acquisitions taken a moment apart" — its own words, written before there was a demodulator.
    /// This is that second leg, and the test that matters most here is the one that shows both
    /// coming from one block.
    /// </para>
    /// <para>
    /// The signal comes from <c>SyntheticSymbolSource</c>, the generator <c>REQ-SIM-001</c>
    /// delivered and <c>REQ-SIM-001</c>'s own tests proved without demodulating anything. Using it
    /// here rather than writing another generator is deliberate: two generators means two things to
    /// keep correct, and this one has already been checked against the parameters it was asked for.
    /// </para>
    /// </remarks>
    public class DemodulationContextTests
    {
        private const double CentreHz = 1e9;

        private readonly ITestOutputHelper _output;

        public DemodulationContextTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void ADemodulationContextTurnsAnAcquiredBlockIntoAnErrorSummary()
        {
            var context = new MeasurementContext("Demod", Setup());

            DemodResult raised = null;

            context.ResultAnalysed += (sender, result) => raised = result;
            context.DemodulationFaulted += (sender, failure) => throw failure;

            using (IqBlock block = Block(600))
            {
                context.Analyse(block);
            }

            Assert.NotNull(raised);
            Assert.Same(raised, context.LatestResult);
            Assert.Equal(1, context.ResultsAnalysed);

            Assert.NotNull(raised.Summary);
            Assert.NotEmpty(raised.Summary.Metrics);
            Assert.NotNull(raised.Trace);

            foreach (string row in raised.Summary.Render())
            {
                _output.WriteLine(row);
            }

            Assert.True(
                raised.EvmPercent < 1.0,
                "EVM through the measurement path was " + raised.EvmPercent + " %rms.");
        }

        [Fact]
        public void ASpectrumAndADemodulationSeeTheSameAcquisition()
        {
            // REQ-DAT-010's concurrency, with the leg that could not be built until there was a
            // demodulator. One block, distributed, and two kinds of answer come back.
            var contexts = new MeasurementContextSet();

            MeasurementContext spectrum = contexts.Add("Spectrum");
            MeasurementContext demod = contexts.Add("Demod", Setup());

            SpectrumFrame frame = null;
            DemodResult result = null;

            spectrum.FrameAnalysed += (sender, computed) => frame = computed;
            demod.ResultAnalysed += (sender, demodulated) => result = demodulated;

            var analyser = new ContextAnalyser(contexts);

            using (IqBlock block = Block(600))
            {
                analyser.Distribute(block);
            }

            Assert.NotNull(frame);
            Assert.NotNull(result);
            Assert.NotNull(result.Summary);

            _output.WriteLine(
                frame.PointCount + " points and " + result.Trace.SymbolCount + " symbols from one block");
        }

        [Fact]
        public void ASpectrumContextDoesNotDemodulate()
        {
            var context = new MeasurementContext("Spectrum");

            context.ResultAnalysed += (sender, result) => throw new InvalidOperationException(
                "A spectrum context demodulated.");

            using (IqBlock block = Block(600))
            {
                context.Analyse(block);
            }

            Assert.False(context.IsDemodulating);
            Assert.Equal(0, context.ResultsAnalysed);
            Assert.Null(context.LatestResult);
            Assert.Equal(1, context.FramesAnalysed);
        }

        [Fact]
        public void ASettingThatCannotBeDemodulatedIsReportedRatherThanStoppingTheMeasurement()
        {
            // The acquisition pump stops when a BlockAcquired handler throws. A Result Length longer
            // than the record is a setting to correct, and taking the whole measurement down for it
            // would remove the spectrum the user needs in order to see what to correct it to.
            MeasurementState setup = Setup();

            // A symbol rate three orders out: the resampling to four points a symbol leaves a
            // couple of samples, which is not a waveform. A Result Length longer than the record
            // would NOT do here -- the chain shortens that and says so, which is a notice rather
            // than a fault.
            setup.Demod.SymbolRateHz = 1000.0;

            var context = new MeasurementContext("Demod", setup);

            Exception reported = null;

            context.DemodulationFaulted += (sender, failure) => reported = failure;

            using (IqBlock block = Block(600))
            {
                context.Analyse(block);
            }

            Assert.NotNull(reported);
            Assert.Null(context.LatestResult);
            Assert.Equal(0, context.ResultsAnalysed);

            // The spectrum leg of the same context carried on regardless.
            Assert.Equal(1, context.FramesAnalysed);

            _output.WriteLine(reported.Message);
        }

        [Fact]
        public void AFormatThisBuildCannotDemodulateIsRefusedWhenTheSettingsAreBuilt()
        {
            MeasurementState setup = Setup();

            setup.Demod.Format = "1024QAM";

            ArgumentException failure = Assert.Throws<ArgumentException>(
                () => setup.Demod.ToSettings());

            _output.WriteLine(failure.Message);

            Assert.Contains("1024QAM", failure.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void SelectingDigitalDemodulationDefaultsTheSymbolRateToHalfTheSpan()
        {
            // REQ-DEM-030: "On first selection of digital demodulation the default shall be Span/2."
            var setup = new MeasurementState { SpanHz = 10e6 };

            Assert.Equal(0.0, setup.Demod.SymbolRateHz);

            setup.SelectKind(MeasurementKind.DigitalDemodulation);

            Assert.Equal(5e6, setup.Demod.SymbolRateHz);
        }

        [Fact]
        public void ARateTheUserChoseSurvivesLeavingDemodulationAndComingBack()
        {
            // "On FIRST selection", and this is the half that wording is for: a default that
            // reapplied itself would discard the user's own rate the moment they looked at the
            // spectrum and came back.
            var setup = new MeasurementState { SpanHz = 10e6 };

            setup.SelectKind(MeasurementKind.DigitalDemodulation);
            setup.Demod.SymbolRateHz = 3.84e6;

            setup.SelectKind(MeasurementKind.Spectrum);
            setup.SelectKind(MeasurementKind.DigitalDemodulation);

            Assert.Equal(3.84e6, setup.Demod.SymbolRateHz);
        }

        [Fact]
        public void TheDemodulationSettingsAreCarriedWhateverTheMeasurementKindIs()
        {
            // Groundwork for REQ-ARC-002a: the settings are part of the setup rather than of the
            // mode, so nothing about them is lost by changing what the measurement is.
            var setup = new MeasurementState { SpanHz = 20e6 };

            setup.SelectKind(MeasurementKind.DigitalDemodulation);
            setup.Demod.Format = "QPSK";
            setup.Demod.ResultLengthSymbols = 512;
            setup.Demod.Equaliser = true;

            setup.SelectKind(MeasurementKind.Spectrum);

            Assert.Equal(512, setup.Demod.ResultLengthSymbols);
            Assert.True(setup.Demod.Equaliser);
            Assert.Equal(10e6, setup.Demod.SymbolRateHz);
        }

        [Fact]
        public void TheSettingsAreRebuiltWhenTheSetupChangesAndNotBefore()
        {
            var context = new MeasurementContext("Demod", Setup());

            var results = new List<DemodResult>();

            context.ResultAnalysed += (sender, result) => results.Add(result);

            using (IqBlock block = Block(600))
            {
                context.Analyse(block);

                MeasurementState changed = Setup();

                changed.Demod.ResultLengthSymbols = 128;
                context.Setup = changed;

                context.Analyse(block);
            }

            Assert.Equal(2, results.Count);
            Assert.Equal(256, results[0].Trace.SymbolCount);
            Assert.Equal(128, results[1].Trace.SymbolCount);
        }

        /// <summary>A setup that demodulates the burst <see cref="Block"/> produces.</summary>
        private static MeasurementState Setup()
        {
            var setup = new MeasurementState
            {
                CenterFrequencyHz = CentreHz,
                SpanHz = 10e6,
            };

            setup.SelectKind(MeasurementKind.DigitalDemodulation);

            setup.Demod.Format = "QPSK";
            setup.Demod.SymbolRateHz = 12.8e6 / SyntheticSymbolSource.DefaultSamplesPerSymbol;
            setup.Demod.ResultLengthSymbols = 256;

            // No measurement filter, because this source is deliberately both ends of a link at
            // once: it shapes with a full raised cosine so that its samples at the decision
            // instants are exactly the symbols it sent. Applying the receiver's matching half to a
            // waveform that has already had it costs about 10 % EVM -- measured, before
            // PulseFilterType.None existed.
            setup.Demod.MeasurementFilter = PulseFilterType.None;

            return setup;
        }

        /// <summary>A block of clean QPSK, as a front end would deliver it.</summary>
        private static IqBlock Block(int symbols)
        {
            var source = new SyntheticSymbolSource
            {
                Scheme = ModulationScheme.Qpsk(),
                Seed = 9,
            };

            return source.Generate(symbols).ToBlock(CentreHz, DateTime.UtcNow);
        }
    }
}
