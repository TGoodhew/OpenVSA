using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using OpenVSA.Core;
using OpenVSA.Dsp.Spectrum;
using OpenVSA.Dsp.Windowing;
using OpenVSA.Hal;
using OpenVSA.Measurement.Contexts;
using Xunit;

namespace OpenVSA.Measurement.Tests.Contexts
{
    /// <summary>
    /// Two contexts running concurrently against one capture session (<c>REQ-DAT-010</c>).
    /// </summary>
    /// <remarks>
    /// The front end is a local fake rather than the simulator, for the same reason
    /// <c>SpectrumEngineTests</c>' is: a test that reaches for a concrete transport is the first step
    /// towards production code doing the same (<c>REQ-ARC-001</c>).
    /// </remarks>
    public class ContextAnalyserTests
    {
        private const int Samples = 4096;

        private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10.0);

        [Fact]
        public async Task OneCaptureSessionFeedsEveryContext()
        {
            var set = new MeasurementContextSet("Spectrum");
            MeasurementContext spectrum = set.Active;
            MeasurementContext demod = set.Add("QPSK demod");

            // Different analysis of the same samples, which is what makes two contexts worth having:
            // the acquired band belongs to the capture, the way it is transformed belongs to each
            // context.
            spectrum.Setup.Analysis.Window = WindowType.FlatTop;
            demod.Setup.Analysis.Window = WindowType.Uniform;

            using (var frontEnd = new OneToneFrontEnd())
            using (var engine = new SpectrumEngine(frontEnd, null))
            using (var analyser = new ContextAnalyser(set))
            {
                // The engine's own inline analysis is the primary context's, so that one is not
                // transformed twice.
                analyser.Primary = spectrum;
                analyser.Attach(engine);

                var primaryFrames = new CountdownEvent(3);
                engine.TargetUpdatesPerSecond = 0.0;
                engine.FrameComputed += (sender, frame) =>
                {
                    if (!primaryFrames.IsSet)
                    {
                        primaryFrames.Signal();
                    }
                };

                await engine.StartAsync(new AcquisitionRequest(1e9, 10e6, Samples, 0.0),
                    CancellationToken.None);

                Assert.True(primaryFrames.Wait(Patience), "No frames arrived within " + Patience + ".");

                await engine.StopAsync();

                // Both contexts are live off the one acquisition: the secondary from the block, the
                // primary from the pump's own transform of the same block.
                Assert.True(demod.FramesAnalysed >= 3, "The secondary context analysed " +
                    demod.FramesAnalysed + " frames.");
                Assert.Equal(0L, spectrum.FramesAnalysed);
                Assert.True(engine.FramesComputed >= 3);

                // And it is one front end, one arm, one stream of blocks -- not a second acquisition
                // taken a moment later.
                Assert.Equal(1, frontEnd.Arms);
                Assert.Equal(analyser.BlocksAnalysed, demod.FramesAnalysed);

                SpectrumFrame latest = demod.TakeLatestFrame();

                try
                {
                    Assert.NotNull(latest);
                    Assert.True(latest.LevelsDbm.Length > 0);
                }
                finally
                {
                    latest?.Release();
                    demod.ClearFrame();
                }
            }
        }

        [Fact]
        public void EachContextTransformsTheBlockItsOwnWay()
        {
            var set = new MeasurementContextSet("Wide");
            MeasurementContext wide = set.Active;
            MeasurementContext narrow = set.Add("Narrow");

            wide.Setup.Analysis.Window = WindowType.Uniform;
            narrow.Setup.Analysis.Window = WindowType.FlatTop;

            using (var analyser = new ContextAnalyser(set))
            using (IqBlock block = Tone(bin: 100))
            {
                analyser.Distribute(block);

                SpectrumFrame fromWide = wide.TakeLatestFrame();
                SpectrumFrame fromNarrow = narrow.TakeLatestFrame();

                try
                {
                    Assert.NotNull(fromWide);
                    Assert.NotNull(fromNarrow);

                    // The tone itself is in the same place in both, because it is one acquisition.
                    int uniformPeak = PeakBin(fromWide.LevelsDbm);
                    int flatTopPeak = PeakBin(fromNarrow.LevelsDbm);

                    Assert.Equal(uniformPeak, flatTopPeak);

                    // Discriminating: if both contexts shared one computer, or one context's frame
                    // were handed to the other, these two skirts would be identical. A flat-top
                    // window spreads an on-bin tone across its main lobe; a uniform one puts a null
                    // two bins away. Measured relative to each frame's own peak rather than at an
                    // absolute index, because the displayed band is trimmed to the analysis span.
                    double uniformSkirt = fromWide.LevelsDbm[uniformPeak + 2];
                    double flatTopSkirt = fromNarrow.LevelsDbm[flatTopPeak + 2];

                    Assert.True(flatTopSkirt > uniformSkirt + 3.0,
                        "The two windows produced the same skirt: uniform " + uniformSkirt +
                        " dB, flat-top " + flatTopSkirt + " dB.");
                }
                finally
                {
                    fromWide?.Release();
                    fromNarrow?.Release();
                    wide.ClearFrame();
                    narrow.ClearFrame();
                }
            }
        }

        [Fact]
        public void ThePrimaryContextIsSkipped()
        {
            var set = new MeasurementContextSet("Primary");
            MeasurementContext primary = set.Active;
            MeasurementContext secondary = set.Add("Secondary");

            using (var analyser = new ContextAnalyser(set) { Primary = primary })
            using (IqBlock block = Tone(bin: 64))
            {
                analyser.Distribute(block);

                Assert.Equal(0L, primary.FramesAnalysed);
                Assert.Equal(1L, secondary.FramesAnalysed);
                Assert.False(primary.HasFrame);

                // Discriminating: with no primary named, every context in the set is analysed. If
                // the skip were unconditional this would still be zero.
                analyser.Primary = null;
                analyser.Distribute(block);

                Assert.Equal(1L, primary.FramesAnalysed);
                Assert.Equal(2L, secondary.FramesAnalysed);
            }

            primary.ClearFrame();
            secondary.ClearFrame();
        }

        [Fact]
        public void AContextGivesUpTheFrameItWasHoldingWhenItIsReplaced()
        {
            var set = new MeasurementContextSet("Only");
            MeasurementContext context = set.Active;

            using (var analyser = new ContextAnalyser(set))
            using (IqBlock block = Tone(bin: 64))
            {
                analyser.Distribute(block);
                SpectrumFrame first = context.TakeLatestFrame();

                analyser.Distribute(block);
                SpectrumFrame second = context.TakeLatestFrame();

                try
                {
                    Assert.NotSame(first, second);

                    // REQ-NFR-002: the share TakeLatestFrame took is the reason the first frame is
                    // still readable after being displaced. Without it the buffer would already be
                    // back in the pool and holding another frame's data.
                    Assert.True(first.LevelsDbm.Length > 0);
                }
                finally
                {
                    first.Release();
                    second.Release();
                }

                // Now nothing holds the first frame, and reading it says so rather than handing back
                // a buffer that belongs to something else.
                Assert.Throws<ObjectDisposedException>(() => _ = first.Complex.Length);

                context.ClearFrame();

                // Clearing releases the one the context was keeping, which was the last share.
                Assert.Throws<ObjectDisposedException>(() => _ = second.Complex.Length);
                Assert.False(context.HasFrame);
            }
        }

        [Fact]
        public void AHandlerThatKeepsAFrameWithoutRetainingItIsToldSo()
        {
            var set = new MeasurementContextSet("Only");
            MeasurementContext context = set.Active;

            SpectrumFrame kept = null;
            context.FrameAnalysed += (sender, frame) => kept = frame;

            using (var analyser = new ContextAnalyser(set))
            using (IqBlock block = Tone(bin: 64))
            {
                analyser.Distribute(block);

                // Readable while the context still holds it.
                Assert.NotNull(kept);
                Assert.True(kept.LevelsDbm.Length > 0);

                context.ClearFrame();

                // And loud once it does not: a consumer that missed the protocol gets an exception
                // naming the fix, not a later frame's spectrum.
                Assert.Throws<ObjectDisposedException>(() => _ = kept.Complex.Length);
            }
        }

        [Fact]
        public void RemovingAContextGivesItsBufferBack()
        {
            var set = new MeasurementContextSet("Primary");
            MeasurementContext secondary = set.Add("Secondary");

            using (var analyser = new ContextAnalyser(set) { Primary = set.Active })
            using (IqBlock block = Tone(bin: 64))
            {
                analyser.Distribute(block);

                SpectrumFrame held = secondary.TakeLatestFrame();
                Assert.NotNull(held);

                Assert.True(set.Remove(secondary));

                // The removed context's own share is gone; the caller's is not, so this still reads.
                Assert.True(held.LevelsDbm.Length > 0);
                held.Release();

                Assert.Throws<ObjectDisposedException>(() => _ = held.Complex.Length);
                Assert.DoesNotContain(secondary, analyser.Secondaries);
            }
        }

        [Fact]
        public void AnAnalyserFollowsOneSessionAtATime()
        {
            var set = new MeasurementContextSet("Primary");
            set.Add("Secondary");

            using (var first = new OneToneFrontEnd())
            using (var second = new OneToneFrontEnd())
            using (var engineA = new SpectrumEngine(first, null))
            using (var engineB = new SpectrumEngine(second, null))
            using (var analyser = new ContextAnalyser(set))
            {
                analyser.Attach(engineA);
                analyser.Attach(engineB);

                // Detaching from the one it left: the shell builds a new engine on every Apply, and
                // an analyser still subscribed to the old one would be analysing blocks from a front
                // end that had been abandoned.
                analyser.Attach(null);

                Assert.Equal(0L, analyser.BlocksAnalysed);
            }
        }

        private static int PeakBin(ReadOnlySpan<float> levels)
        {
            int peak = 0;

            for (int i = 1; i < levels.Length; i++)
            {
                if (levels[i] > levels[peak])
                {
                    peak = i;
                }
            }

            return peak;
        }

        private static IqBlock Tone(int bin)
        {
            var metadata = new IqBlockMetadata(
                Samples, 12.8e6, 1.0e9, false, 1.0, 0.0, 1L,
                new DateTime(2026, 7, 29, 0, 0, 0, DateTimeKind.Utc), 0.0, true,
                new FrontEndId("test"), null);

            IqBlock block = IqBlock.Rent(metadata);
            Span<float> samples = block.GetSamples();

            for (int n = 0; n < Samples; n++)
            {
                double phase = 2.0 * Math.PI * bin * n / Samples;
                samples[n * 2] = (float)(0.5 * Math.Cos(phase));
                samples[n * 2 + 1] = (float)(0.5 * Math.Sin(phase));
            }

            return block;
        }

        /// <summary>A front end that delivers one tone, endlessly, and counts its arms.</summary>
        private sealed class OneToneFrontEnd : IFrontEnd
        {
            private AcquisitionPlan _plan;
            private long _sequence;

            public int Arms { get; private set; }

            public FrontEndId Id => new FrontEndId("one-tone");

            public string DisplayName => "One tone";

            public IFrontEndCapabilities Capabilities { get; } = new WideCapabilities();

            public FrontEndState State { get; private set; } = FrontEndState.Disconnected;

            public event EventHandler<FrontEndEvent> Notification;

            public Task ConnectAsync(CancellationToken ct)
            {
                State = FrontEndState.Connected;
                return Task.FromResult(true);
            }

            public Task DisconnectAsync()
            {
                State = FrontEndState.Disconnected;
                return Task.FromResult(true);
            }

            public AcquisitionPlan Negotiate(AcquisitionRequest request) =>
                new AcquisitionPlan(
                    request.CenterFrequencyHz, request.SpanHz, request.SpanHz * 1.28,
                    request.SamplesPerBlock, request.ReferenceLevelDbm, true,
                    new List<ParameterCoercion>());

            public Task ConfigureAsync(AcquisitionPlan plan, CancellationToken ct)
            {
                _plan = plan;
                State = FrontEndState.Configured;
                return Task.FromResult(true);
            }

            public Task ArmAsync(CancellationToken ct)
            {
                Arms++;
                State = FrontEndState.Armed;
                return Task.FromResult(true);
            }

            public Task<IqBlock> AcquireNextAsync(CancellationToken ct)
            {
                ct.ThrowIfCancellationRequested();
                State = FrontEndState.Acquiring;

                var metadata = new IqBlockMetadata(
                    _plan.SamplesPerBlock, _plan.SampleRateHz, _plan.CenterFrequencyHz, false,
                    1.0, _plan.ReferenceLevelDbm, _sequence++, DateTime.UtcNow, 0.0, true, Id, null);

                IqBlock block = IqBlock.Rent(metadata);
                Span<float> samples = block.GetSamples();

                for (int n = 0; n < metadata.SampleCount; n++)
                {
                    double phase = 2.0 * Math.PI * 100 * n / metadata.SampleCount;
                    samples[n * 2] = (float)(0.5 * Math.Cos(phase));
                    samples[n * 2 + 1] = (float)(0.5 * Math.Sin(phase));
                }

                return Task.FromResult(block);
            }

            public Task AbortAsync()
            {
                State = FrontEndState.Configured;
                return Task.FromResult(true);
            }

            public void Dispose() => State = FrontEndState.Disconnected;

            /// <summary>Present so the interface's event is not merely declared.</summary>
            public void Report(FrontEndEvent e)
            {
                EventHandler<FrontEndEvent> handler = Notification;

                if (handler != null)
                {
                    handler(this, e);
                }
            }

            private sealed class WideCapabilities : IFrontEndCapabilities
            {
                private static readonly IReadOnlyList<TriggerStyle> Styles =
                    new List<TriggerStyle> { TriggerStyle.Immediate }.AsReadOnly();

                public FrequencyRange CenterFrequencyRange => new FrequencyRange(0.0, 26.5e9);
                public double MaxSpanHz => 40e6;
                public double MinSpanHz => 1.0;
                public double MaxSampleRateHz => 51.2e6;
                public int MaxSamplesPerBlock => 1 << 22;
                public long MaxCaptureSamples => 1L << 32;
                public bool SupportsBasebandIq => true;
                public int ChannelCount => 1;
                public bool SupportsPhaseCoherentChannels => false;
                public IReadOnlyList<TriggerStyle> TriggerStyles => Styles;
                public AmplitudeRange ReferenceLevelRange => new AmplitudeRange(-100.0, 30.0);
                public bool SupportsExternalRef => false;
                public bool SupportsInputRangeControl => true;
                public bool SupportsRealTimeAnalysis => false;
                public long MaxPreTriggerSamples => 0L;
            }
        }
    }
}
