using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using OpenVSA.Core;
using OpenVSA.Hal;
using Xunit;
using Xunit.Abstractions;

namespace OpenVSA.Hal.Tests
{
    /// <summary>
    /// <c>REQ-HAL-003</c>: the registry unions what every transport can reach and marks the
    /// resources a driver exists for.
    /// </summary>
    /// <remarks>
    /// The mapping from an <c>*IDN?</c> response to a driver has to live here, because the registry
    /// is the only thing that knows every driver. Each front end answers
    /// <see cref="IInstrumentRecogniser.Recognises"/> for itself and the registry asks all of them —
    /// which is what keeps <c>REQ-HAL-002</c>'s prohibition on instrument-specific conditionals
    /// intact, since nothing has to hold a list of model names.
    /// </remarks>
    public class ResourceDiscoveryRegistryTests
    {
        private readonly ITestOutputHelper _output;

        /// <summary>Takes xunit's output sink.</summary>
        /// <param name="output">Where the discovered resources are written.</param>
        public ResourceDiscoveryRegistryTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void AnEnumeratorInAPluginAssemblyIsFoundByTheSameScanAsAFrontEnd()
        {
            var registry = new FrontEndRegistry();
            registry.AddAssembly(typeof(ResourceDiscoveryRegistryTests).Assembly);

            Assert.True(registry.CanEnumerateResources);

            IReadOnlyList<DiscoveredResource> found = registry.DiscoverResources();

            foreach (DiscoveredResource resource in found)
            {
                _output.WriteLine(resource.ToString());
            }

            Assert.Equal(2, found.Count);
        }

        [Fact]
        public void ADriverThatRecognisesAnIdentityClaimsIt()
        {
            var registry = new FrontEndRegistry();
            registry.AddAssembly(typeof(ResourceDiscoveryRegistryTests).Assembly);

            IReadOnlyList<DiscoveredResource> found = registry.DiscoverResources();

            DiscoveredResource claimed = found.Single(r => r.HasDriver);
            DiscoveredResource unclaimed = found.Single(r => !r.HasDriver);

            Assert.Equal("Test instrument", claimed.Driver);
            Assert.True(unclaimed.Answered);
        }

        [Fact]
        public void AFrontEndThatDoesNotRecogniseAnythingNeverClaimsAResource()
        {
            // The simulator and file playback are in this position, correctly: they drive no
            // address, so they must not appear beside one. A registry that fell back to "the first
            // provider" would offer the simulator as the driver for a real instrument.
            var registry = new FrontEndRegistry();
            registry.AddAssembly(typeof(ResourceDiscoveryRegistryTests).Assembly);

            Assert.Equal(string.Empty, registry.DriverFor("Some,Instrument,We,Do,Not,Drive"));
            Assert.Equal(string.Empty, registry.DriverFor(string.Empty));
            Assert.Equal(string.Empty, registry.DriverFor(null));
        }

        [Fact]
        public void WithNoTransportAtAllTheRegistrySaysSoRatherThanReturningNothing()
        {
            // REQ-NFR-032's machine. "Cannot enumerate" and "enumerated and found nothing" are
            // different facts and the dialog says them differently, so the registry has to keep
            // them apart.
            var registry = new FrontEndRegistry();

            Assert.False(registry.CanEnumerateResources);
            Assert.Empty(registry.DiscoverResources());
        }

        [Fact]
        public void AnEnumeratorThatThrowsIsRecordedAndTheOthersStillReport()
        {
            // One misbehaving transport must not empty a dialog the others could have filled.
            var registry = new FrontEndRegistry();
            registry.AddAssembly(typeof(ThrowingEnumerator).Assembly);

            IReadOnlyList<DiscoveredResource> found = registry.DiscoverResources();

            _output.WriteLine(string.Join(Environment.NewLine, registry.Failures.Select(f => f.ToString())));

            Assert.Equal(2, found.Count);
            Assert.Contains(registry.Failures, f => f.Reason.Contains("could not enumerate"));
        }

        [Fact]
        public void CancellationStopsAnEnumerationRatherThanRunningItOut()
        {
            // Thirty GPIB addresses at 700 ms each is twenty seconds of a dialog somebody may have
            // opened by mistake.
            using (var cancel = new CancellationTokenSource())
            {
                cancel.Cancel();

                var registry = new FrontEndRegistry();
                registry.AddAssembly(typeof(ResourceDiscoveryRegistryTests).Assembly);

                Assert.Throws<OperationCanceledException>(
                    () => registry.DiscoverResources(cancel.Token));
            }
        }
    }

    /// <summary>A transport that reports two addresses, one of them drivable.</summary>
    public sealed class FakeEnumerator : IResourceEnumerator
    {
        /// <inheritdoc />
        public IReadOnlyList<DiscoveredResource> Discover(
            Func<string, string> driverFor, CancellationToken cancel)
        {
            cancel.ThrowIfCancellationRequested();

            return new[]
            {
                new DiscoveredResource(
                    "FAKE::1::INSTR", "OpenVSA,TestInstrument,0,1.0", null,
                    driverFor("OpenVSA,TestInstrument,0,1.0")),
                new DiscoveredResource(
                    "FAKE::2::INSTR", "Someone,Else,0,1.0", null,
                    driverFor("Someone,Else,0,1.0")),
            };
        }
    }

    /// <summary>A transport that cannot enumerate, to prove one failure does not take the rest.</summary>
    public sealed class ThrowingEnumerator : IResourceEnumerator
    {
        /// <inheritdoc />
        public IReadOnlyList<DiscoveredResource> Discover(
            Func<string, string> driverFor, CancellationToken cancel)
        {
            throw new InvalidOperationException("this transport is broken");
        }
    }

    /// <summary>A front end that drives the fake instrument, and says so.</summary>
    [FrontEndProvider("Test instrument")]
    public sealed class RecognisingFrontEnd : IFrontEnd, IInstrumentRecogniser
    {
        /// <inheritdoc />
        public bool Recognises(string identity) =>
            identity != null &&
            identity.IndexOf("TestInstrument", StringComparison.OrdinalIgnoreCase) >= 0;

        /// <inheritdoc />
        public FrontEndId Id => default(FrontEndId);

        /// <inheritdoc />
        public string DisplayName => "Test instrument";

        /// <inheritdoc />
        public IFrontEndCapabilities Capabilities => null;

        /// <inheritdoc />
        public FrontEndState State => FrontEndState.Disconnected;

        /// <inheritdoc />
        public event EventHandler<FrontEndEvent> Notification;

        /// <inheritdoc />
        public System.Threading.Tasks.Task ConnectAsync(CancellationToken cancel) => null;

        /// <inheritdoc />
        public System.Threading.Tasks.Task DisconnectAsync() => null;

        /// <inheritdoc />
        public AcquisitionPlan Negotiate(AcquisitionRequest request) => null;

        /// <inheritdoc />
        public System.Threading.Tasks.Task ConfigureAsync(AcquisitionPlan plan, CancellationToken cancel) => null;

        /// <inheritdoc />
        public System.Threading.Tasks.Task ArmAsync(CancellationToken cancel) => null;

        /// <inheritdoc />
        public System.Threading.Tasks.Task<IqBlock> AcquireNextAsync(CancellationToken cancel) => null;

        /// <inheritdoc />
        public System.Threading.Tasks.Task AbortAsync() => null;

        /// <inheritdoc />
        public void Dispose() => Notification?.Invoke(this, null);
    }
}
