using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using OpenVSA.Core;
using OpenVSA.Dsp.Spectrum;
using OpenVSA.Hal;
using OpenVSA.Measurement;
using Xunit;

namespace OpenVSA.Measurement.Tests
{
    /// <summary>
    /// The acquisition pump: lifecycle, threading and what it does at the end of a source.
    /// </summary>
    /// <remarks>
    /// The front end here is a local fake rather than the simulator, so that end-of-source and
    /// failure can be provoked on demand — and because a test that reaches for a concrete transport
    /// is the first step towards production code doing the same (<c>REQ-ARC-001</c>).
    /// </remarks>
    public class SpectrumEngineTests
    {
        private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10.0);

        private static AcquisitionRequest Request =>
            new AcquisitionRequest(1e9, 10e6, 4096, 0.0);

        [Fact]
        public async Task ItPumpsFramesUntilStopped()
        {
            using (var frontEnd = new FakeFrontEnd())
            using (var engine = new SpectrumEngine(frontEnd, null))
            {
                var arrived = new CountdownEvent(5);
                engine.TargetUpdatesPerSecond = 0.0;
                engine.FrameComputed += (sender, frame) =>
                {
                    if (!arrived.IsSet)
                    {
                        arrived.Signal();
                    }
                };

                await engine.StartAsync(Request, CancellationToken.None);

                Assert.True(arrived.Wait(Patience), "No frames arrived within " + Patience + ".");
                Assert.True(engine.FramesComputed >= 5);

                await engine.StopAsync();
                Assert.False(engine.IsRunning);
            }
        }

        [Fact]
        public async Task FramesAreComputedOffTheCallingThread()
        {
            // REQ-NFR-010: the dispatcher performs no DSP. The pump must therefore not run inline
            // on its starter, which is exactly what would happen with a synchronous front end and
            // no Task.Run - the fake here completes every acquisition synchronously, so each await
            // in the pump would continue on the caller.
            //
            // Stated by holding a frame rather than by comparing thread identifiers. The thread
            // pool recycles threads, so once the test body has awaited and released its thread the
            // pump may legitimately land on that very thread; an identity comparison therefore
            // fails occasionally on correct code, which is worse than not testing it. Blocking
            // inside the handler admits no such ambiguity: if the pump were running on the caller,
            // StartAsync could not return while a handler was still inside it.
            using (var frontEnd = new FakeFrontEnd())
            using (var engine = new SpectrumEngine(frontEnd, null))
            {
                var held = new ManualResetEventSlim();
                var release = new ManualResetEventSlim();

                engine.TargetUpdatesPerSecond = 0.0;
                engine.FrameComputed += (sender, frame) =>
                {
                    held.Set();
                    release.Wait(Patience);
                };

                Task<AcquisitionPlan> start = engine.StartAsync(Request, CancellationToken.None);

                Assert.True(held.Wait(Patience), "No frame was computed within " + Patience + ".");

                // Timed rather than simply awaited, so that a pump running on the caller shows up
                // as a failure with a reason rather than as a test run that never finishes.
                Task first = await Task.WhenAny(start, Task.Delay(Patience));
                release.Set();

                await start;
                await engine.StopAsync();

                Assert.True(
                    ReferenceEquals(first, start),
                    "StartAsync had not returned while a frame handler was still running, so the " +
                    "pump was executing on the calling thread.");
            }
        }

        [Fact]
        public async Task TheFrameIsTheSpectrumOfTheBlockThatProducedIt()
        {
            using (var frontEnd = new FakeFrontEnd { ToneBin = 512 })
            using (var engine = new SpectrumEngine(frontEnd, null))
            {
                SpectrumFrame first = null;
                var arrived = new ManualResetEventSlim();

                engine.TargetUpdatesPerSecond = 0.0;
                engine.FrameComputed += (sender, frame) =>
                {
                    if (first == null)
                    {
                        first = frame;
                        arrived.Set();
                    }
                };

                AcquisitionPlan plan = await engine.StartAsync(Request, CancellationToken.None);
                Assert.True(arrived.Wait(Patience));
                await engine.StopAsync();

                // The displayed axis is the analysis span, not the wider band that was acquired to
                // produce it (REQ-ACQ-001), so the frame is narrower than the block.
                Assert.Equal(plan.SpanHz, first.SpanHz, 3);
                Assert.True(first.PointCount < plan.SamplesPerBlock);
                Assert.Equal(plan.CenterFrequencyHz, first.CenterFrequencyHz, 3);

                double expected = plan.CenterFrequencyHz + 512 * plan.SampleRateHz / plan.SamplesPerBlock;
                Assert.Equal(expected, first.FrequencyAt(first.IndexOfPeak()), 3);
            }
        }

        [Fact]
        public async Task ItReportsTheCoercionsTheFrontEndMade()
        {
            // REQ-HAL-001: what the plan changed is carried out of the engine, not swallowed by it.
            using (var frontEnd = new FakeFrontEnd())
            using (var engine = new SpectrumEngine(frontEnd, null))
            {
                engine.TargetUpdatesPerSecond = 0.0;

                AcquisitionPlan plan = await engine.StartAsync(
                    new AcquisitionRequest(1e9, 500e6, 4096, 0.0), CancellationToken.None);

                await engine.StopAsync();

                Assert.True(plan.Coerced);
                Assert.NotNull(plan.CoercionFor("Span"));
                Assert.Same(plan, engine.Plan);
            }
        }

        [Fact]
        public async Task TheEndOfASourceIsCompletion_NotFailure()
        {
            using (var frontEnd = new FakeFrontEnd { BlocksBeforeEnd = 3 })
            using (var engine = new SpectrumEngine(frontEnd, null))
            {
                var completed = new ManualResetEventSlim();
                Exception failure = null;

                engine.TargetUpdatesPerSecond = 0.0;
                engine.Completed += (sender, e) => completed.Set();
                engine.Faulted += (sender, e) => failure = e;

                await engine.StartAsync(Request, CancellationToken.None);

                Assert.True(completed.Wait(Patience), "The source ended without raising Completed.");
                Assert.Null(failure);
                Assert.Equal(3, engine.FramesComputed);

                await engine.StopAsync();
                Assert.False(engine.IsRunning);
            }
        }

        [Fact]
        public async Task AFailureMidRunIsReported_NotLostOnAnUnobservedTask()
        {
            using (var frontEnd = new FakeFrontEnd { FailAfter = 2 })
            using (var engine = new SpectrumEngine(frontEnd, null))
            {
                Exception failure = null;
                var faulted = new ManualResetEventSlim();

                engine.TargetUpdatesPerSecond = 0.0;
                engine.Faulted += (sender, e) =>
                {
                    failure = e;
                    faulted.Set();
                };

                await engine.StartAsync(Request, CancellationToken.None);

                Assert.True(faulted.Wait(Patience), "The failure was never reported.");
                Assert.IsType<InvalidOperationException>(failure);

                await engine.StopAsync();
            }
        }

        [Fact]
        public async Task ItConnectsConfiguresAndArmsBeforePumping()
        {
            using (var frontEnd = new FakeFrontEnd())
            using (var engine = new SpectrumEngine(frontEnd, null))
            {
                var arrived = new ManualResetEventSlim();
                engine.TargetUpdatesPerSecond = 0.0;
                engine.FrameComputed += (sender, frame) => arrived.Set();

                await engine.StartAsync(Request, CancellationToken.None);
                Assert.True(arrived.Wait(Patience));
                await engine.StopAsync();

                Assert.Equal(
                    new[] { "Connect", "Negotiate", "Configure", "Arm", "Acquire" },
                    frontEnd.Calls.GetRange(0, 5));
            }
        }

        [Fact]
        public async Task StartingTwiceIsRefused()
        {
            using (var frontEnd = new FakeFrontEnd())
            using (var engine = new SpectrumEngine(frontEnd, null))
            {
                engine.TargetUpdatesPerSecond = 0.0;
                await engine.StartAsync(Request, CancellationToken.None);

                await Assert.ThrowsAsync<InvalidOperationException>(
                    () => engine.StartAsync(Request, CancellationToken.None));

                await engine.StopAsync();
            }
        }

        [Fact]
        public async Task StoppingWhenNotRunningIsHarmless()
        {
            using (var frontEnd = new FakeFrontEnd())
            using (var engine = new SpectrumEngine(frontEnd, null))
            {
                await engine.StopAsync();
                await engine.StopAsync();
                Assert.False(engine.IsRunning);
            }
        }

        [Fact]
        public void ItRefusesAFrontEndOfNull()
        {
            Assert.Throws<ArgumentNullException>(() => new SpectrumEngine(null, null));
        }

        [Fact]
        public async Task OverlapCutsSeveralAnalysisFramesFromOneAcquiredBlock()
        {
            // REQ-ACQ-003 through the pump. A 4096-sample block cut into 1024-sample records at
            // half overlap yields seven frames; without overlap it would yield one, because the
            // engine analyses a block whole by default.
            List<SpectrumFrame> frames = await Collect(engine =>
            {
                engine.RecordSamples = 1024;
                engine.Overlap = 0.5;
            });

            Assert.Equal(7, frames.Count);
        }

        [Fact]
        public async Task WithoutOverlapABlockIsOneFrame()
        {
            // The default path, unchanged: the block is the record and there is nothing to cut.
            List<SpectrumFrame> frames = await Collect(engine => { });

            Assert.Single(frames);
        }

        [Fact]
        public async Task AnAverageOfOverlappedFramesIsWorthLessThanItsCount()
        {
            // REQ-DSP-031 through the pump. Seven overlapped frames are seven acquisitions but not
            // seven independent ones, and the frame has to say so - a display that showed the raw
            // count would overstate the confidence of the measurement.
            List<SpectrumFrame> frames = await Collect(engine =>
            {
                engine.RecordSamples = 1024;
                engine.Overlap = 0.75;
                engine.Averager = new TraceAverager(AveragingType.RmsVideo, 100);
            });

            SpectrumFrame last = frames[frames.Count - 1];

            Assert.Equal(frames.Count, last.AverageCount);
            Assert.True(
                last.EffectiveAverageCount < last.AverageCount,
                last.AverageCount + " overlapped frames were reported as worth " +
                last.EffectiveAverageCount.ToString("F2") + " independent averages.");
        }

        [Fact]
        public async Task AnAverageOfIndependentAcquisitionsIsWorthItsCount()
        {
            // The other half of the same requirement, and what makes the test above meaningful:
            // separate acquisitions share no samples, so the effective count is the plain one.
            List<SpectrumFrame> frames = await Collect(
                engine => engine.Averager = new TraceAverager(AveragingType.RmsVideo, 100),
                blocks: 4);

            SpectrumFrame last = frames[frames.Count - 1];

            Assert.Equal(4, last.AverageCount);
            Assert.Equal(4.0, last.EffectiveAverageCount, 10);
        }

        [Fact]
        public void TheOverlapAndRecordLengthAreValidated()
        {
            using (var frontEnd = new FakeFrontEnd())
            using (var engine = new SpectrumEngine(frontEnd, null))
            {
                Assert.Throws<ArgumentOutOfRangeException>(() => engine.Overlap = 1.0);
                Assert.Throws<ArgumentOutOfRangeException>(() => engine.Overlap = -0.1);
                Assert.Throws<ArgumentOutOfRangeException>(() => engine.RecordSamples = -1);

                engine.Overlap = 0.9999;
                engine.RecordSamples = 0;
            }
        }

        /// <summary>Runs the pump over a fixed number of blocks and returns every frame published.</summary>
        private static async Task<List<SpectrumFrame>> Collect(
            Action<SpectrumEngine> configure, int blocks = 1)
        {
            using (var frontEnd = new FakeFrontEnd { BlocksBeforeEnd = blocks })
            using (var engine = new SpectrumEngine(frontEnd, null))
            {
                var frames = new List<SpectrumFrame>();
                var completed = new ManualResetEventSlim();
                Exception failure = null;

                engine.TargetUpdatesPerSecond = 0.0;
                configure(engine);

                engine.FrameComputed += (sender, frame) =>
                {
                    lock (frames)
                    {
                        frames.Add(frame);
                    }
                };

                engine.Faulted += (sender, e) =>
                {
                    failure = e;
                    completed.Set();
                };

                engine.Completed += (sender, e) => completed.Set();

                await engine.StartAsync(Request, CancellationToken.None);
                Assert.True(completed.Wait(Patience), "The source never ended.");
                await engine.StopAsync();

                Assert.Null(failure);

                lock (frames)
                {
                    return new List<SpectrumFrame>(frames);
                }
            }
        }

        /// <summary>
        /// A front end that returns a tone, and that can be made to end or to fail on demand.
        /// </summary>
        private sealed class FakeFrontEnd : IFrontEnd
        {
            private readonly List<string> _calls = new List<string>();
            private AcquisitionPlan _plan;
            private long _sequence;
            private int _delivered;

            /// <summary>Blocks to deliver before reporting end of source; negative for endless.</summary>
            public int BlocksBeforeEnd { get; set; } = -1;

            /// <summary>Blocks to deliver before throwing; negative for never.</summary>
            public int FailAfter { get; set; } = -1;

            /// <summary>Bin the synthesised tone sits on.</summary>
            public int ToneBin { get; set; } = 100;

            /// <summary>Calls made, in order, for asserting the lifecycle.</summary>
            public List<string> Calls
            {
                get { lock (_calls) { return new List<string>(_calls); } }
            }

            public FrontEndId Id => new FrontEndId("fake");

            public string DisplayName => "Fake source";

            public IFrontEndCapabilities Capabilities { get; } = new FakeCapabilities();

            public FrontEndState State { get; private set; } = FrontEndState.Disconnected;

            public event EventHandler<FrontEndEvent> Notification;

            public Task ConnectAsync(CancellationToken ct)
            {
                Record("Connect");
                State = FrontEndState.Connected;
                return Task.FromResult(true);
            }

            public Task DisconnectAsync()
            {
                State = FrontEndState.Disconnected;
                return Task.FromResult(true);
            }

            public AcquisitionPlan Negotiate(AcquisitionRequest request)
            {
                Record("Negotiate");

                var coercions = new List<ParameterCoercion>();
                double span = request.SpanHz;

                if (span > Capabilities.MaxSpanHz)
                {
                    coercions.Add(new ParameterCoercion(
                        "Span", span, Capabilities.MaxSpanHz, "exceeds front-end maximum span"));
                    span = Capabilities.MaxSpanHz;
                }

                return new AcquisitionPlan(
                    request.CenterFrequencyHz, span, span * 1.28, request.SamplesPerBlock,
                    request.ReferenceLevelDbm, true, coercions);
            }

            public Task ConfigureAsync(AcquisitionPlan plan, CancellationToken ct)
            {
                Record("Configure");
                _plan = plan;
                State = FrontEndState.Configured;
                return Task.FromResult(true);
            }

            public Task ArmAsync(CancellationToken ct)
            {
                Record("Arm");
                State = FrontEndState.Armed;
                return Task.FromResult(true);
            }

            public Task<IqBlock> AcquireNextAsync(CancellationToken ct)
            {
                Record("Acquire");
                ct.ThrowIfCancellationRequested();
                State = FrontEndState.Acquiring;

                if (FailAfter >= 0 && _delivered >= FailAfter)
                {
                    throw new InvalidOperationException("The fake front end was told to fail.");
                }

                if (BlocksBeforeEnd >= 0 && _delivered >= BlocksBeforeEnd)
                {
                    return Task.FromResult<IqBlock>(null);
                }

                _delivered++;

                var metadata = new IqBlockMetadata(
                    sampleCount: _plan.SamplesPerBlock,
                    sampleRateHz: _plan.SampleRateHz,
                    centerFrequencyHz: _plan.CenterFrequencyHz,
                    isBaseband: false,
                    fullScaleVolts: 1.0,
                    referenceLevelDbm: _plan.ReferenceLevelDbm,
                    sequenceNumber: _sequence++,
                    acquiredUtc: DateTime.UtcNow,
                    triggerOffsetSeconds: 0.0,
                    triggerCorrectionsApplied: true,
                    source: Id,
                    extended: null);

                IqBlock block = IqBlock.Rent(metadata);
                Span<float> samples = block.GetSamples();

                for (int n = 0; n < metadata.SampleCount; n++)
                {
                    double phase = 2.0 * Math.PI * ToneBin * n / metadata.SampleCount;
                    samples[n * 2] = (float)(0.5 * Math.Cos(phase));
                    samples[n * 2 + 1] = (float)(0.5 * Math.Sin(phase));
                }

                return Task.FromResult(block);
            }

            public Task AbortAsync()
            {
                Record("Abort");
                State = FrontEndState.Configured;
                return Task.FromResult(true);
            }

            public void Dispose()
            {
                State = FrontEndState.Disconnected;
            }

            /// <summary>Raises a notification. Present so the interface's event is not merely declared.</summary>
            public void Report(FrontEndEvent e)
            {
                EventHandler<FrontEndEvent> handler = Notification;
                if (handler != null)
                {
                    handler(this, e);
                }
            }

            private void Record(string call)
            {
                lock (_calls)
                {
                    // Only the first acquisition matters to the lifecycle assertion, and an endless
                    // run would otherwise grow this list without bound.
                    if (_calls.Count < 8)
                    {
                        _calls.Add(call);
                    }
                }
            }

            private sealed class FakeCapabilities : IFrontEndCapabilities
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
                public bool SupportsRealTimeAnalysis => false;
                public long MaxPreTriggerSamples => 0L;
            }
        }
    }
}
