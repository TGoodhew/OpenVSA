using System;
using System.Collections.Generic;
using System.Linq;
using OpenVSA.Demod.Chain;
using OpenVSA.Demod.Signal;
using OpenVSA.Demod.Tests.Signals;
using Xunit;
using Xunit.Abstractions;

namespace OpenVSA.Demod.Tests
{
    /// <summary>
    /// <c>REQ-DEM-001</c>: the chain runs in the declared order, the optional steps are skippable
    /// without disturbing the rest, step 8's bound is reported, and the equaliser's re-entry is a
    /// genuine loop that improves the result.
    /// </summary>
    public class DemodulationChainTests
    {
        private readonly ITestOutputHelper _output;

        public DemodulationChainTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void TheChainExecutesEveryStepInTheDeclaredOrder()
        {
            DemodResult result = Demodulate(Source(), Settings());

            Assert.Equal(
                ProcessingOrder.Steps.ToArray(),
                result.Journal.Pass(1).Select(entry => entry.Step).ToArray());

            Assert.Equal(1, result.Journal.PassCount);

            _output.WriteLine(result.Journal.ToString());
        }

        [Fact]
        public void ACleanSignalDemodulatesToTheSymbolsThatWereSent()
        {
            var source = Source();
            float[] record = source.Generate(400);

            DemodResult result = Demodulate(record, source, Settings());

            _output.WriteLine(result.ToString());

            Assert.True(
                result.EvmPercent < 1.0,
                "EVM was " + result.EvmPercent + " %rms on a clean signal.");

            Assert.True(
                MatchesTransmitted(source.Symbols, result),
                "The decided symbols do not follow the transmitted ones.");
        }

        [Fact]
        public void TheOptionalStepsAreSkippableWithoutDisturbingTheOrderOfTheRest()
        {
            DemodSettings settings = Settings();

            settings.BurstSearchEnabled = false;
            settings.SyncSearchEnabled = false;
            settings.EqualiserEnabled = false;

            DemodResult result = Demodulate(Source(), settings);

            DemodStep[] executed = result.Journal.Executed().ToArray();
            DemodStep[] expected = ProcessingOrder.Steps
                .Where(step => !ProcessingOrder.IsOptional(step))
                .ToArray();

            Assert.Equal(expected, executed);

            // The skipped steps are recorded rather than omitted: a gap in the journal would be
            // indistinguishable from a step nobody implemented.
            foreach (DemodStep step in ProcessingOrder.Steps.Where(ProcessingOrder.IsOptional))
            {
                ChainEntry entry = result.Journal.Entries.Single(e => e.Step == step);

                Assert.False(entry.Executed);
            }

            Assert.True(result.EvmPercent < 1.0);
        }

        [Fact]
        public void TheChainStillRunsInOrderWithEveryOptionalStepTurnedOn()
        {
            var source = Source();
            source.CarrierOffsetHz = 0.0;

            int[] pattern = { 0, 1, 2, 3, 3, 2, 1, 0, 0, 0, 3, 3 };
            var symbols = new List<int>(pattern);
            var random = new Random(11);

            while (symbols.Count < 400)
            {
                symbols.Add(random.Next(4));
            }

            float[] record = Burst(source.Generate(symbols.ToArray()), source);

            DemodSettings settings = Settings();

            settings.BurstSearchEnabled = true;
            settings.SyncSearchEnabled = true;
            settings.EqualiserEnabled = true;
            settings.SyncPattern = pattern;
            settings.ResultLengthSymbols = 200;

            DemodResult result = Demodulate(record, source, settings);

            Assert.Equal(
                ProcessingOrder.Steps.ToArray(),
                result.Journal.Pass(1).Select(entry => entry.Step).ToArray());

            Assert.All(result.Journal.Entries, entry => Assert.True(entry.Executed));

            _output.WriteLine(result.ToString());
            _output.WriteLine(string.Join(Environment.NewLine, result.Notices));

            Assert.True(
                result.EvmPercent < 2.0,
                "EVM was " + result.EvmPercent + " %rms with every optional step on.");
        }

        [Fact]
        public void TheEqualiserReEntersAtStepEightAndTheSecondPassIsBetter()
        {
            var source = Source();

            // Echoes a whole symbol either side of the main path: inter-symbol interference, which
            // is the linear distortion an equaliser exists to remove and the joint refinement
            // cannot touch.
            source.ChannelTaps = new[] { 0.22, 1.0, -0.16 };

            float[] record = source.Generate(500);

            DemodSettings settings = Settings();

            settings.EqualiserEnabled = true;
            settings.ResultLengthSymbols = 300;

            DemodResult equalised = Demodulate(record, source, settings);

            _output.WriteLine(string.Join(Environment.NewLine, equalised.Passes.Select(p => p.ToString())));

            Assert.True(
                equalised.Passes.Count >= 2,
                "The equaliser did not re-enter: " + equalised.Passes.Count + " pass(es).");

            PassResult first = equalised.Passes[0];
            PassResult second = equalised.Passes[1];

            Assert.True(
                second.EvmPercent < first.EvmPercent / 2.0,
                "EVM did not improve on the second pass: " + first.EvmPercent + " then " +
                second.EvmPercent + " %rms.");

            // The re-entry is at step 8 and the pass that follows is the rest of the chain from
            // there — which is what makes it the specification's loop rather than a second run.
            IReadOnlyList<ChainEntry> secondPass = equalised.Journal.Pass(2);

            Assert.Equal(ProcessingOrder.ReEntryPoint, secondPass[0].Step);

            Assert.Equal(
                ProcessingOrder.Steps
                    .Where(step => !ProcessingOrder.IsAfter(ProcessingOrder.ReEntryPoint, step))
                    .ToArray(),
                secondPass.Select(entry => entry.Step).ToArray());
        }

        [Fact]
        public void TheEqualiserIsWhatImprovedIt()
        {
            var source = Source();

            source.ChannelTaps = new[] { 0.22, 1.0, -0.16 };

            float[] record = source.Generate(500);

            DemodSettings without = Settings();

            without.ResultLengthSymbols = 300;

            DemodSettings with = Settings();

            with.EqualiserEnabled = true;
            with.ResultLengthSymbols = 300;

            DemodResult unequalised = Demodulate(record, source, without);
            DemodResult equalised = Demodulate(record, source, with);

            _output.WriteLine(
                "unequalised " + unequalised.EvmPercent + " %rms, equalised " +
                equalised.EvmPercent + " %rms");

            // Without the equaliser the chain runs once, the distortion stays, and the EVM is the
            // distortion. This is the control: it separates "the second pass helped" from "any
            // second pass would have helped".
            Assert.Single(unequalised.Passes);

            Assert.True(unequalised.EvmPercent > 5.0);
            Assert.True(equalised.EvmPercent < unequalised.EvmPercent / 4.0);
        }

        [Fact]
        public void ReachingTheIterationBoundIsReportedRatherThanSilentlyAccepted()
        {
            var source = Source();

            source.CarrierOffsetHz = 40e3;
            source.TimingOffsetSymbols = 0.4;

            DemodSettings settings = Settings();

            settings.MaxRefinementIterations = 1;

            DemodResult result = Demodulate(source.Generate(400), source, settings);

            Assert.NotNull(result.Convergence);
            Assert.True(result.Convergence.ReachedBound);
            Assert.False(result.Converged);
            Assert.Equal(1, result.Convergence.Iterations);

            Assert.Contains(
                result.Notices,
                notice => notice.IndexOf("bound", StringComparison.OrdinalIgnoreCase) >= 0);

            _output.WriteLine(result.Convergence.ToString());
            _output.WriteLine(result.Convergence.Criterion);
        }

        [Fact]
        public void TheIterationConvergesOnASignalItCanFit()
        {
            DemodResult result = Demodulate(Source(), Settings());

            Assert.True(result.Converged);
            Assert.True(result.Convergence.Iterations < result.Convergence.Bound);
            Assert.True(result.Convergence.LargestChange < result.Convergence.Tolerance);
        }

        [Fact]
        public void ThePassBoundIsReportedWhenTheEqualiserIsStillMoving()
        {
            var source = Source();

            source.ChannelTaps = new[] { 0.35, 1.0, -0.3, 0.12 };

            DemodSettings settings = Settings();

            settings.EqualiserEnabled = true;
            settings.MaxPasses = 1;
            settings.ResultLengthSymbols = 300;

            DemodResult result = Demodulate(source.Generate(500), source, settings);

            Assert.Single(result.Passes);

            Assert.Contains(
                result.Notices,
                notice => notice.IndexOf("bound of 1 pass", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        [Fact]
        public void TheCarrierOffsetIsRecoveredFromTheSignalItWasPutOn()
        {
            var source = Source();

            source.CarrierOffsetHz = 12345.0;

            DemodResult result = Demodulate(source.Generate(400), source, Settings());

            _output.WriteLine("recovered " + result.CarrierFrequencyErrorHz + " Hz");

            Assert.True(
                Math.Abs(result.CarrierFrequencyErrorHz - 12345.0) < 20.0,
                "The chain recovered " + result.CarrierFrequencyErrorHz + " Hz of a 12 345 Hz offset.");
        }

        [Fact]
        public void TheResultCarriesTheTraceAndTheSummaryTheDisplaysAreBuiltFrom()
        {
            DemodResult result = Demodulate(Source(), Settings());

            Assert.NotNull(result.Trace);
            Assert.NotNull(result.Summary);
            Assert.Equal(result.Trace.SymbolCount, result.Symbols.Count);
            Assert.Equal(result.Symbols.Count * 2, result.Bits.Count);
            Assert.NotEmpty(result.Summary.Metrics);
            Assert.NotNull(result.Impairments);

            foreach (string row in result.Summary.Render())
            {
                _output.WriteLine(row);
            }
        }

        private static QpskSource Source() =>
            new QpskSource(7)
            {
                SymbolRateHz = 1e6,
                SampleRateHz = 5.3e6,
                CarrierOffsetHz = 8000.0,
                PhaseRadians = 0.7,
                Amplitude = 0.35,
                TimingOffsetSymbols = 0.3,
            };

        private static DemodSettings Settings() =>
            new DemodSettings
            {
                SymbolRateHz = 1e6,
                ResultLengthSymbols = 256,
                MeasurementFilterAlpha = 0.35,
                ReferenceFilterAlpha = 0.35,
            };

        private static DemodResult Demodulate(QpskSource source, DemodSettings settings) =>
            Demodulate(source.Generate(400), source, settings);

        private static DemodResult Demodulate(
            float[] record, QpskSource source, DemodSettings settings)
        {
            settings.SymbolRateHz = source.SymbolRateHz;

            return new Demodulator().Run(record, source.SampleRateHz, settings);
        }

        /// <summary>
        /// Whether the decided symbols follow the transmitted ones, allowing for the two things a
        /// demodulator with no sync pattern legitimately does not know.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <strong>Which rotation.</strong> Without a sync pattern or differential decoding —
        /// <c>REQ-DEM-040</c> and <c>REQ-DEM-012</c>, neither of which this requirement is — every
        /// QPSK demodulator finds the constellation at one of four rotations and all four are
        /// correct. What must hold is that the sequence follows the transmitted one under a single
        /// constant rotation, not that it happens to have landed on the same one.
        /// </para>
        /// <para>
        /// <strong>Which symbol it started on.</strong> The Result Length window is positioned
        /// somewhere inside a continuous transmission, and nothing has told the chain which symbol
        /// of the stream that is. The transmitted sequence is generated cyclically, so the answer
        /// is a rotation of it, and the alignment is searched for rather than assumed.
        /// </para>
        /// </remarks>
        private static bool MatchesTransmitted(int[] transmitted, DemodResult result)
        {
            IReadOnlyList<int> decided = result.Symbols;

            for (int start = 0; start < transmitted.Length; start++)
            {
                for (int rotation = 0; rotation < 4; rotation++)
                {
                    bool matches = true;

                    for (int symbol = 0; symbol < decided.Count; symbol++)
                    {
                        int sent = transmitted[(start + symbol) % transmitted.Length];

                        if (((sent + rotation) % 4) != decided[symbol])
                        {
                            matches = false;

                            break;
                        }
                    }

                    if (matches)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>Pads a record with silence either side, so there is a burst to find.</summary>
        private static float[] Burst(float[] record, QpskSource source)
        {
            int quiet = (int)(source.SampleRateHz / source.SymbolRateHz * 40);
            var padded = new float[record.Length + (4 * quiet)];

            Array.Copy(record, 0, padded, 2 * quiet, record.Length);

            return padded;
        }
    }
}
