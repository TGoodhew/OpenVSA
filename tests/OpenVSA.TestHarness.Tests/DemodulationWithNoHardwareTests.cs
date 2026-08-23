using System;
using System.Threading;
using System.Threading.Tasks;
using OpenVSA.Demod.Chain;
using OpenVSA.Demod.Signal;
using OpenVSA.Hal;
using OpenVSA.Hal.Sim;
using OpenVSA.Measurement;
using OpenVSA.Measurement.Contexts;
using OpenVSA.Measurement.State;
using OpenVSA.Synthesis;
using Xunit;
using Xunit.Abstractions;

namespace OpenVSA.TestHarness.Tests
{
    /// <summary>
    /// <c>REQ-NFR-032a</c>: "On the same machine, a full demodulation measurement runs to an error
    /// summary" — with no instrument, no VISA and nothing but the simulated source.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The whole path, not a stand-in for it.</strong> A simulated front end negotiates a
    /// plan, is armed and acquires; a <c>SpectrumEngine</c> pumps its blocks; a
    /// <c>ContextAnalyser</c> distributes them; a measurement context demodulates. Every one of
    /// those is the piece the shell uses. What is absent is the instrument, which is the point.
    /// </para>
    /// <para>
    /// <strong>Why this could not be written until now.</strong> <c>SimulatedFrontEnd</c> emitted a
    /// tone. <c>REQ-SIM-001</c> says it "shall generate IQ for any modulation format supported by
    /// the demodulator", and the generator that does it had been delivered into the bench harness,
    /// where a transport reference kept it away from anything the analysis stack could use. It now
    /// lives in <c>OpenVSA.Synthesis</c>, which references <c>OpenVSA.Core</c> and nothing else.
    /// </para>
    /// </remarks>
    public class DemodulationWithNoHardwareTests
    {
        private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20.0);

        private const double CentreHz = 1e9;
        private const double SpanHz = 10e6;
        private const double SymbolRateHz = 1e6;
        private const double CarrierOffsetHz = 12e3;

        private readonly ITestOutputHelper _output;

        public DemodulationWithNoHardwareTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public async Task AFullDemodulationMeasurementRunsWithNoHardware()
        {
            var settings = new SimulatedSignalSettings
            {
                Modulation = "QPSK",
                SymbolRateHz = SymbolRateHz,
                ToneOffsetHz = CarrierOffsetHz,
                AmplitudeVolts = 0.4,
                SnrDb = 45.0,
                Seed = 17,
            };

            DemodResult result;

            using (var frontEnd = new SimulatedFrontEnd(settings))
            using (var engine = new SpectrumEngine(frontEnd, null))
            {
                var contexts = new MeasurementContextSet();
                MeasurementContext demod = contexts.Add("Demod", Setup());

                var analyser = new ContextAnalyser(contexts);
                var arrived = new ManualResetEventSlim();
                Exception faulted = null;

                demod.ResultAnalysed += (sender, computed) => arrived.Set();
                demod.DemodulationFaulted += (sender, failure) => faulted = failure;

                analyser.Attach(engine);

                engine.TargetUpdatesPerSecond = 0.0;

                await engine.StartAsync(
                    new AcquisitionRequest(CentreHz, SpanHz, 65536, 0.0), CancellationToken.None);

                bool came = arrived.Wait(Patience);

                await engine.StopAsync();

                Assert.Null(faulted);
                Assert.True(came, "No demodulated result arrived within " + Patience + ".");

                result = demod.LatestResult;
            }

            Assert.NotNull(result);
            Assert.NotNull(result.Summary);
            Assert.NotEmpty(result.Summary.Metrics);

            foreach (string row in result.Summary.Render())
            {
                _output.WriteLine(row);
            }

            _output.WriteLine(
                "carrier error " + result.CarrierFrequencyErrorHz.ToString("F1") + " Hz of " +
                CarrierOffsetHz.ToString("F0") + " Hz injected");

            // 45 dB of signal-to-noise puts a floor near 0.6 %rms on EVM, so this is a measurement
            // of the noise that was asked for rather than of anything the chain did.
            Assert.True(
                result.EvmPercent < 1.5,
                "EVM off the simulated source was " + result.EvmPercent + " %rms.");

            Assert.True(
                Math.Abs(result.CarrierFrequencyErrorHz - CarrierOffsetHz) < 200.0,
                "The chain read the carrier offset as " + result.CarrierFrequencyErrorHz + " Hz.");
        }

        [Fact]
        public void TheSimulatedSourceStillEmitsAToneWhenNoModulationIsAskedFor()
        {
            // The default has to stay what every spectrum test and demonstration expects. A source
            // that started modulating because a new setting existed would change the meaning of
            // tests that never mentioned it.
            var settings = new SimulatedSignalSettings();

            Assert.Null(settings.Modulation);
        }

        [Fact]
        public void TheGeneratedSignalIsContinuousAcrossBlockBoundaries()
        {
            // Nothing is remembered between blocks except how far along the signal is: symbol k is
            // a function of the seed and k. So a block taken as two halves and a block taken whole
            // are the same samples, which is what "continuous" has to mean for a spectrogram or a
            // trigger to be looking at one signal.
            var whole = new ContinuousModulatedSource
            {
                Scheme = ModulationScheme.Qpsk(),
                SymbolRateHz = SymbolRateHz,
                SampleRateHz = 12.8e6,
                Seed = 5,
            };

            var halves = new ContinuousModulatedSource
            {
                Scheme = ModulationScheme.Qpsk(),
                SymbolRateHz = SymbolRateHz,
                SampleRateHz = 12.8e6,
                Seed = 5,
            };

            var one = new float[2048];
            var first = new float[1024];
            var second = new float[1024];

            whole.Fill(new Span<float>(one));
            halves.Fill(new Span<float>(first));
            halves.Fill(new Span<float>(second));

            for (int index = 0; index < first.Length; index++)
            {
                Assert.Equal(one[index], first[index]);
                Assert.Equal(one[first.Length + index], second[index]);
            }
        }

        [Fact]
        public void TheSameSeedGivesTheSameSignalAndADifferentOneDoesNot()
        {
            // REQ-SIM-003, on the source that has to answer for it block by block.
            var first = new float[512];
            var same = new float[512];
            var other = new float[512];

            Source(5).Fill(new Span<float>(first));
            Source(5).Fill(new Span<float>(same));
            Source(6).Fill(new Span<float>(other));

            Assert.Equal(first, same);
            Assert.NotEqual(first, other);
        }

        private static ContinuousModulatedSource Source(long seed) =>
            new ContinuousModulatedSource
            {
                Scheme = ModulationScheme.Qpsk(),
                SymbolRateHz = SymbolRateHz,
                SampleRateHz = 12.8e6,
                Seed = seed,
            };

        private static MeasurementState Setup()
        {
            var setup = new MeasurementState
            {
                CenterFrequencyHz = CentreHz,
                SpanHz = SpanHz,
            };

            setup.SelectKind(MeasurementKind.DigitalDemodulation);

            setup.Demod.Format = "QPSK";
            setup.Demod.SymbolRateHz = SymbolRateHz;
            setup.Demod.ResultLengthSymbols = 512;

            // The simulated source is a transmitter: it shapes with a root raised cosine, so the
            // demodulator's own default measurement filter is the matching half and is right as it
            // stands. Named here rather than left implicit because the burst generator next door
            // wants the opposite, and which one a test is using decides the answer.
            setup.Demod.MeasurementFilter = PulseFilterType.RootRaisedCosine;

            return setup;
        }
    }
}
