using System;
using System.Collections.Generic;
using System.Linq;
using OpenVSA.Core;
using OpenVSA.Dsp.Spectrum;
using OpenVSA.Dsp.Windowing;
using Xunit;
using Xunit.Abstractions;

namespace OpenVSA.Dsp.Tests
{
    /// <summary>
    /// <c>REQ-TRC-003</c>: the composition order is declared once, the pipeline is driven by that
    /// declaration, the legal combinations are exhaustive, and the gating case is pinned by
    /// measurement rather than by comment.
    /// </summary>
    public class CompositionOrderTests
    {
        private const double RateHz = 15e6;
        private const int Samples = 4096;

        private readonly ITestOutputHelper _output;

        public CompositionOrderTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void TheOrderIsDeclaredOnceAndIsWhatTheRequirementNames()
        {
            Assert.Equal(
                new[]
                {
                    AnalysisStage.Gating,
                    AnalysisStage.Windowing,
                    AnalysisStage.Transform,
                    AnalysisStage.Averaging,
                    AnalysisStage.Accumulation,
                    AnalysisStage.Format,
                },
                CompositionOrder.Stages);

            // The relations the remarks argue for, stated as assertions so a reordering of the
            // enumeration fails here rather than quietly changing what the product measures.
            Assert.True(CompositionOrder.IsAfter(AnalysisStage.Windowing, AnalysisStage.Gating));
            Assert.True(CompositionOrder.IsAfter(AnalysisStage.Accumulation, AnalysisStage.Averaging));
            Assert.True(CompositionOrder.IsAfter(AnalysisStage.Format, AnalysisStage.Accumulation));

            // Format is last, which is what makes REQ-TRC-001's "a format change recomputes
            // nothing" possible at all.
            Assert.Equal(
                CompositionOrder.Stages.Count - 1,
                CompositionOrder.PositionOf(AnalysisStage.Format));
        }

        [Fact]
        public void TheStageListIsDerivedFromTheEnumerationAndNotWrittenTwice()
        {
            // A second list is a second thing to keep in step. If a stage is ever added to the
            // enumeration and not to the order, this catches it.
            Assert.Equal(
                Enum.GetValues(typeof(AnalysisStage)).Length,
                CompositionOrder.Stages.Count);

            foreach (AnalysisStage stage in Enum.GetValues(typeof(AnalysisStage)))
            {
                Assert.Contains(stage, CompositionOrder.Stages);
            }
        }

        [Fact]
        public void ThePipelineRunsTheStagesInTheDeclaredOrder()
        {
            // The criterion: "a test fails if any stage is invoked out of declared order". The
            // pipeline records what it ran, so this compares behaviour against the declaration
            // rather than reading the source and agreeing with it.
            var pipeline = new AnalysisPipeline(new SpectrumComputer(WindowType.FlatTop, null, null))
            {
                Gate = new TimeGate(0.0, Samples / 2 / RateHz),
                Averager = new TraceAverager(AveragingType.RmsVideo, 4),
                Accumulator = new AccumulatingTrace { Accumulator = TraceAccumulator.Spectrogram },
                Format = TraceFormat.LogMagnitude,
            };

            using (IqBlock block = Burst())
            {
                pipeline.Run(block);
            }

            _output.WriteLine(string.Join(" -> ", pipeline.LastRunStages));

            Assert.Equal(CompositionOrder.Stages, pipeline.LastRunStages);
        }

        [Fact]
        public void StagesThatAreNotConfiguredAreSkippedButTheRestKeepTheirOrder()
        {
            var pipeline = new AnalysisPipeline(new SpectrumComputer(WindowType.FlatTop, null, null));

            using (IqBlock block = Burst())
            {
                pipeline.Run(block);
            }

            Assert.Equal(
                new[] { AnalysisStage.Windowing, AnalysisStage.Transform, AnalysisStage.Format },
                pipeline.LastRunStages);

            // A subsequence of the declaration, in the declaration's order.
            AssertIsSubsequenceOfDeclaredOrder(pipeline.LastRunStages);
        }

        [Fact]
        public void EveryConfigurationRunsInDeclaredOrder()
        {
            // Across the cross-product of what is switched on, not just one configuration.
            foreach (bool gated in new[] { false, true })
            {
                foreach (bool averaged in new[] { false, true })
                {
                    foreach (bool accumulated in new[] { false, true })
                    {
                        var pipeline = new AnalysisPipeline(
                            new SpectrumComputer(WindowType.FlatTop, null, null))
                        {
                            Gate = gated ? new TimeGate(0.0, Samples / 2 / RateHz) : null,
                            Averager = averaged
                                ? new TraceAverager(AveragingType.RmsVideo, 4)
                                : null,
                            Accumulator = accumulated
                                ? new AccumulatingTrace
                                {
                                    Accumulator = TraceAccumulator.Spectrogram,
                                }
                                : null,
                        };

                        using (IqBlock block = Burst())
                        {
                            pipeline.Run(block);
                        }

                        AssertIsSubsequenceOfDeclaredOrder(pipeline.LastRunStages);

                        Assert.Equal(gated, pipeline.LastRunStages.Contains(AnalysisStage.Gating));
                        Assert.Equal(
                            averaged, pipeline.LastRunStages.Contains(AnalysisStage.Averaging));
                        Assert.Equal(
                            accumulated,
                            pipeline.LastRunStages.Contains(AnalysisStage.Accumulation));
                    }
                }
            }
        }

        [Fact]
        public void GateThenAverageAndAverageThenGateAreDistinguishedByMeasurement()
        {
            // The criterion asks for the two to be told apart "by a test on a signal whose gated
            // and ungated averages provably differ, so the order is pinned by measurement rather
            // than by comment".
            //
            // The signal is a burst: a tone for the first half of the record and silence for the
            // second. Gating to the first half analyses a tone. Analysing first and gating the
            // result is not expressible at all - a spectrum has no time axis left to gate - so what
            // the wrong order actually produces is the ungated spectrum, in which the tone's power
            // is halved by the silence and the RBW is that of the whole record.
            var gatedFirst = new AnalysisPipeline(
                new SpectrumComputer(WindowType.FlatTop, null, null))
            {
                Gate = new TimeGate(0.0, Samples / 2 / RateHz),
            };

            var ungated = new AnalysisPipeline(
                new SpectrumComputer(WindowType.FlatTop, null, null));

            using (IqBlock block = Burst())
            {
                gatedFirst.Run(block);
                ungated.Run(block);
            }

            SpectrumFrame gatedFrame = gatedFirst.LastFrame;
            SpectrumFrame ungatedFrame = ungated.LastFrame;

            double gatedPeak = gatedFrame.LevelsDbm[gatedFrame.IndexOfPeak()];
            double ungatedPeak = ungatedFrame.LevelsDbm[ungatedFrame.IndexOfPeak()];

            _output.WriteLine(
                "gated " + gatedPeak.ToString("F3") + " dBm at RBW " +
                gatedFrame.ResolutionBandwidthHz.ToString("F1") + " Hz; ungated " +
                ungatedPeak.ToString("F3") + " dBm at RBW " +
                ungatedFrame.ResolutionBandwidthHz.ToString("F1") + " Hz");

            // The two provably differ, and in the direction the order predicts: gating first gives
            // the tone its true level, where the ungated record dilutes it with the silent half.
            Assert.True(
                gatedPeak - ungatedPeak > 3.0,
                "gated peak " + gatedPeak + " dBm against ungated " + ungatedPeak + " dBm.");

            // And REQ-DSP-050's coupling holds only because gating precedes windowing: the window
            // is sized to what survived the gate, so the RBW follows it.
            Assert.True(
                gatedFrame.ResolutionBandwidthHz > 1.5 * ungatedFrame.ResolutionBandwidthHz,
                "gated RBW " + gatedFrame.ResolutionBandwidthHz + " Hz against ungated " +
                ungatedFrame.ResolutionBandwidthHz + " Hz.");

            Assert.Equal(
                Samples / 2, SpectrumComputer.TransformLengthFor(Samples / 2));
            Assert.Equal(Samples / 2, gatedFirst.LastWindow.Length);
            Assert.Equal(Samples, ungated.LastWindow.Length);
        }

        [Fact]
        public void EveryCombinationIsEitherLegalOrRejectedByName()
        {
            // "Exhaustive over the cross-product ... with every combination either legal or
            // rejected by a named error; none is silently ignored."
            IReadOnlyList<KeyValuePair<CompositionSelection, CompositionVerdict>> all =
                CompositionOrder.AllCombinations();

            int expected = 2 *
                Enum.GetValues(typeof(AveragingType)).Length *
                Enum.GetValues(typeof(TraceAccumulator)).Length *
                Enum.GetValues(typeof(TraceFormat)).Length;

            Assert.Equal(expected, all.Count);

            int rejected = 0;

            foreach (KeyValuePair<CompositionSelection, CompositionVerdict> entry in all)
            {
                if (entry.Value.IsLegal)
                {
                    Assert.Equal(string.Empty, entry.Value.Reason);
                    continue;
                }

                rejected++;

                Assert.False(
                    string.IsNullOrEmpty(entry.Value.Reason),
                    entry.Key + " was rejected without saying why, which is the silent kind of " +
                    "rejection wearing a return value.");
            }

            _output.WriteLine(
                all.Count + " combinations, " + rejected + " rejected with a reason");

            // Both rules bite somewhere, or the enumeration proves nothing.
            Assert.True(rejected > 0);
            Assert.True(rejected < all.Count);
        }

        [Fact]
        public void APhaseFormatAfterAPowerAverageIsRejected()
        {
            // Averaging precedes format, so a format needing phase cannot recover what a power
            // average already discarded. The rule follows from the order rather than from taste.
            CompositionVerdict verdict = CompositionOrder.Validate(
                new CompositionSelection(
                    false, AveragingType.RmsVideo, TraceAccumulator.None,
                    TraceFormat.UnwrappedPhase));

            Assert.False(verdict.IsLegal);
            Assert.Contains("phase", verdict.Reason);

            // The same format under coherent averaging is fine.
            Assert.True(
                CompositionOrder.Validate(
                    new CompositionSelection(
                        false, AveragingType.Time, TraceAccumulator.None,
                        TraceFormat.UnwrappedPhase)).IsLegal);
        }

        [Fact]
        public void ASpectrogramOfTheComplexPairIsRejected()
        {
            CompositionVerdict verdict = CompositionOrder.Validate(
                new CompositionSelection(
                    false, AveragingType.Off, TraceAccumulator.Spectrogram, TraceFormat.IQ));

            Assert.False(verdict.IsLegal);
            Assert.Contains("one number", verdict.Reason);
        }

        [Fact]
        public void ThePipelineRefusesAnIllegalCombinationWithTheNamedReason()
        {
            var pipeline = new AnalysisPipeline(new SpectrumComputer(WindowType.FlatTop, null, null))
            {
                Averager = new TraceAverager(AveragingType.RmsVideo, 4),
                Format = TraceFormat.WrappedPhase,
            };

            using (IqBlock block = Burst())
            {
                InvalidOperationException error =
                    Assert.Throws<InvalidOperationException>(() => pipeline.Run(block));

                Assert.Equal(
                    CompositionOrder.Validate(pipeline.Selection).Reason, error.Message);
            }
        }

        [Fact]
        public void AnUnknownStageIsRefused()
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => CompositionOrder.PositionOf((AnalysisStage)99));
        }

        [Fact]
        public void MissingArgumentsAreRefused()
        {
            Assert.Throws<ArgumentNullException>(() => new AnalysisPipeline(null));
            Assert.Throws<ArgumentNullException>(
                () => new AnalysisPipeline(new SpectrumComputer()).Run(null));
        }

        private static void AssertIsSubsequenceOfDeclaredOrder(IReadOnlyList<AnalysisStage> ran)
        {
            int previous = -1;

            foreach (AnalysisStage stage in ran)
            {
                int position = CompositionOrder.PositionOf(stage);

                Assert.True(
                    position > previous,
                    stage + " ran out of declared order: " + string.Join(" -> ", ran));

                previous = position;
            }
        }

        /// <summary>A tone for the first half of the record and silence for the second.</summary>
        private static IqBlock Burst()
        {
            IqBlock block = IqBlock.Rent(new IqBlockMetadata(
                sampleCount: Samples,
                sampleRateHz: RateHz,
                centerFrequencyHz: 1e9,
                isBaseband: false,
                fullScaleVolts: 1.0,
                referenceLevelDbm: 0.0,
                sequenceNumber: 1,
                acquiredUtc: new DateTime(2026, 7, 26, 0, 0, 0, DateTimeKind.Utc),
                triggerOffsetSeconds: 0.0,
                triggerCorrectionsApplied: false,
                source: new FrontEndId("test"),
                extended: null));

            Span<float> data = block.GetSamples();
            const double cycles = 2.3117e6 / RateHz;

            for (int n = 0; n < Samples / 2; n++)
            {
                double angle = 2.0 * Math.PI * cycles * n;

                data[n * 2] = (float)Math.Cos(angle);
                data[n * 2 + 1] = (float)Math.Sin(angle);
            }

            return block;
        }
    }
}
