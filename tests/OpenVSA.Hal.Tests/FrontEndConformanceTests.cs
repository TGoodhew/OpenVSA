using System;
using System.Threading;
using System.Threading.Tasks;
using OpenVSA.Core;
using OpenVSA.Hal;
using OpenVSA.Hal.Sim;
using Xunit;

namespace OpenVSA.Hal.Tests
{
    /// <summary>
    /// The front-end conformance suite required by <c>REQ-DAT-001</c>: it runs against
    /// <em>every</em> <see cref="IFrontEnd"/> implementation and asserts metadata completeness and
    /// self-consistency, plus the negotiate-then-configure contract of <c>REQ-HAL-001</c>.
    /// </summary>
    /// <remarks>
    /// Derive one fixture per implementation. A front end with no fixture is not covered, so the
    /// suite is only as complete as its derivations — which is why <see
    /// cref="SimulatedFrontEndConformanceTests"/> sits alongside the implementation rather than
    /// waiting for a tidier moment.
    /// </remarks>
    public abstract class FrontEndConformanceTests
    {
        /// <summary>Creates a fresh, unconnected front end to test.</summary>
        protected abstract IFrontEnd CreateFrontEnd();

        /// <summary>A request this front end can honour without coercion.</summary>
        protected abstract AcquisitionRequest CreateHonourableRequest();

        private async Task<IqBlock> AcquireOne(IFrontEnd frontEnd, AcquisitionRequest request)
        {
            await frontEnd.ConnectAsync(CancellationToken.None);
            AcquisitionPlan plan = frontEnd.Negotiate(request);
            await frontEnd.ConfigureAsync(plan, CancellationToken.None);
            await frontEnd.ArmAsync(CancellationToken.None);
            return await frontEnd.AcquireNextAsync(CancellationToken.None);
        }

        // ---- REQ-DAT-001: metadata completeness and self-consistency -------------------------

        [Fact]
        public async Task Block_SampleRateIsPositive()
        {
            using (IFrontEnd frontEnd = CreateFrontEnd())
            using (IqBlock block = await AcquireOne(frontEnd, CreateHonourableRequest()))
            {
                Assert.True(block.SampleRateHz > 0.0, "Fs must be greater than zero.");
                Assert.False(double.IsNaN(block.SampleRateHz));
                Assert.False(double.IsInfinity(block.SampleRateHz));
            }
        }

        [Fact]
        public async Task Block_SampleViewIsExactlyTwiceSampleCount()
        {
            using (IFrontEnd frontEnd = CreateFrontEnd())
            using (IqBlock block = await AcquireOne(frontEnd, CreateHonourableRequest()))
            {
                Assert.Equal(block.SampleCount * 2, block.GetSamples().Length);
            }
        }

        [Fact]
        public async Task Block_CentreFrequencyIsWithinDeclaredRange()
        {
            using (IFrontEnd frontEnd = CreateFrontEnd())
            using (IqBlock block = await AcquireOne(frontEnd, CreateHonourableRequest()))
            {
                Assert.True(
                    frontEnd.Capabilities.CenterFrequencyRange.Contains(block.CenterFrequencyHz),
                    "Centre frequency must lie within the front end's declared range.");
            }
        }

        [Fact]
        public async Task Block_CarriesItsSourceAndASequenceNumber()
        {
            using (IFrontEnd frontEnd = CreateFrontEnd())
            {
                AcquisitionRequest request = CreateHonourableRequest();
                await frontEnd.ConnectAsync(CancellationToken.None);
                await frontEnd.ConfigureAsync(frontEnd.Negotiate(request), CancellationToken.None);
                await frontEnd.ArmAsync(CancellationToken.None);

                using (IqBlock first = await frontEnd.AcquireNextAsync(CancellationToken.None))
                using (IqBlock second = await frontEnd.AcquireNextAsync(CancellationToken.None))
                {
                    Assert.Equal(frontEnd.Id, first.Source);
                    Assert.True(
                        second.SequenceNumber > first.SequenceNumber,
                        "Sequence numbers must advance so dropped frames are detectable.");
                    Assert.Equal(DateTimeKind.Utc, first.AcquiredUtc.Kind);
                }
            }
        }

        [Fact]
        public async Task Block_MatchesThePlanItWasAcquiredUnder()
        {
            // Self-consistency: a block that disagrees with its own plan makes every downstream
            // frequency axis wrong, and nothing else would catch it.
            using (IFrontEnd frontEnd = CreateFrontEnd())
            {
                AcquisitionRequest request = CreateHonourableRequest();
                await frontEnd.ConnectAsync(CancellationToken.None);
                AcquisitionPlan plan = frontEnd.Negotiate(request);
                await frontEnd.ConfigureAsync(plan, CancellationToken.None);
                await frontEnd.ArmAsync(CancellationToken.None);

                using (IqBlock block = await frontEnd.AcquireNextAsync(CancellationToken.None))
                {
                    Assert.Equal(plan.SampleRateHz, block.SampleRateHz);
                    Assert.Equal(plan.CenterFrequencyHz, block.CenterFrequencyHz);
                    Assert.Equal(plan.SamplesPerBlock, block.SampleCount);
                    Assert.Equal(plan.ReferenceLevelDbm, block.ReferenceLevelDbm);
                }
            }
        }

        // ---- REQ-HAL-001: negotiate is pure ---------------------------------------------------

        [Fact]
        public void Negotiate_IsPure_LeavesStateUntouched()
        {
            // "No hardware command is sent during Negotiate" — asserted here as the observable
            // consequence: negotiating on a disconnected front end works and connects nothing.
            using (IFrontEnd frontEnd = CreateFrontEnd())
            {
                Assert.Equal(FrontEndState.Disconnected, frontEnd.State);

                AcquisitionPlan plan = frontEnd.Negotiate(CreateHonourableRequest());

                Assert.NotNull(plan);
                Assert.Equal(FrontEndState.Disconnected, frontEnd.State);
            }
        }

        [Fact]
        public void Negotiate_IsRepeatable()
        {
            using (IFrontEnd frontEnd = CreateFrontEnd())
            {
                AcquisitionRequest request = CreateHonourableRequest();

                AcquisitionPlan first = frontEnd.Negotiate(request);
                AcquisitionPlan second = frontEnd.Negotiate(request);

                Assert.Equal(first.SampleRateHz, second.SampleRateHz);
                Assert.Equal(first.SpanHz, second.SpanHz);
                Assert.Equal(first.Coercions.Count, second.Coercions.Count);
            }
        }

        [Fact]
        public void Negotiate_HonourableRequestIsNotCoerced()
        {
            using (IFrontEnd frontEnd = CreateFrontEnd())
            {
                AcquisitionPlan plan = frontEnd.Negotiate(CreateHonourableRequest());

                Assert.False(
                    plan.Coerced,
                    "A request within capabilities must not be coerced. Coercions: " +
                    string.Join("; ", plan.Coercions));
            }
        }

        [Fact]
        public void Negotiate_ExcessiveSpanIsCoercedWithAReason()
        {
            // REQ-HAL-001 AC, in the shape the specification states it.
            using (IFrontEnd frontEnd = CreateFrontEnd())
            {
                double beyond = frontEnd.Capabilities.MaxSpanHz * 5.0;
                AcquisitionRequest request = CreateHonourableRequest();

                AcquisitionPlan plan = frontEnd.Negotiate(new AcquisitionRequest(
                    request.CenterFrequencyHz, beyond, request.SamplesPerBlock,
                    request.ReferenceLevelDbm));

                Assert.True(plan.Coerced);
                Assert.Equal(frontEnd.Capabilities.MaxSpanHz, plan.SpanHz);

                ParameterCoercion coercion = plan.CoercionFor("Span");
                Assert.NotNull(coercion);
                Assert.Equal("exceeds front-end maximum span", coercion.Reason);
                Assert.Equal(beyond, coercion.Requested);
                Assert.Equal(frontEnd.Capabilities.MaxSpanHz, coercion.Honoured);
            }
        }

        [Fact]
        public void Capabilities_AreSelfConsistent()
        {
            using (IFrontEnd frontEnd = CreateFrontEnd())
            {
                IFrontEndCapabilities caps = frontEnd.Capabilities;

                Assert.True(caps.MinSpanHz > 0.0, "Minimum span must be positive.");
                Assert.True(caps.MaxSpanHz >= caps.MinSpanHz, "Span range must not be inverted.");
                Assert.True(caps.MaxSampleRateHz > 0.0, "Maximum sample rate must be positive.");
                Assert.True(caps.MaxSamplesPerBlock > 0, "Maximum block size must be positive.");
                Assert.True(caps.ChannelCount >= 1, "Channel count must be at least one.");
                Assert.NotNull(caps.TriggerStyles);
                Assert.NotEmpty(caps.TriggerStyles);

                Assert.True(
                    caps.MaxCaptureSamples >= caps.MaxSamplesPerBlock,
                    "A deep capture cannot be smaller than a single block.");

                Assert.False(
                    caps.SupportsPhaseCoherentChannels && caps.ChannelCount < 2,
                    "Phase coherence between channels is meaningless with fewer than two.");
            }
        }

        [Fact]
        public void Negotiate_RejectsNull()
        {
            using (IFrontEnd frontEnd = CreateFrontEnd())
            {
                Assert.Throws<ArgumentNullException>(() => frontEnd.Negotiate(null));
            }
        }
    }

    /// <summary>Runs the conformance suite against <see cref="SimulatedFrontEnd"/>.</summary>
    public sealed class SimulatedFrontEndConformanceTests : FrontEndConformanceTests
    {
        /// <inheritdoc />
        protected override IFrontEnd CreateFrontEnd() =>
            new SimulatedFrontEnd(new SimulatedSignalSettings { ToneOffsetHz = 1e5, Seed = 12345 });

        /// <inheritdoc />
        protected override AcquisitionRequest CreateHonourableRequest() =>
            new AcquisitionRequest(
                centerFrequencyHz: 1e9,
                spanHz: 1e6,
                samplesPerBlock: 4096,
                referenceLevelDbm: -10.0);
    }
}
