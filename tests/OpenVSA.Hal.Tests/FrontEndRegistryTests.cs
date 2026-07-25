using System;
using System.IO;
using System.Linq;
using System.Reflection;
using OpenVSA.Core;
using OpenVSA.Hal;
using OpenVSA.Hal.Sim;
using Xunit;

namespace OpenVSA.Hal.Tests
{
    /// <summary>
    /// <c>REQ-HAL-003</c>: front ends discovered from plug-in assemblies by attribute, and
    /// <c>REQ-NFR-032</c>'s requirement that a plug-in which cannot load never stops the
    /// application starting.
    /// </summary>
    public class FrontEndRegistryTests : IDisposable
    {
        private readonly string _scratch;

        public FrontEndRegistryTests()
        {
            _scratch = Path.Combine(
                Path.GetTempPath(), "openvsa-registry-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_scratch);
        }

        /// <inheritdoc />
        public void Dispose()
        {
            try
            {
                Directory.Delete(_scratch, recursive: true);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                // A probed assembly stays loaded for the life of the AppDomain and keeps its file
                // mapped, so the delete fails — as UnauthorizedAccessException, not IOException.
                // Leaving a temp directory behind is not worth failing a test over.
            }
        }

        // ---- Discovery -------------------------------------------------------------------------

        [Fact]
        public void DiscoversTheSimulatorFromItsAssembly()
        {
            var registry = new FrontEndRegistry();
            int added = registry.AddAssembly(typeof(SimulatedFrontEnd).Assembly);

            Assert.True(added >= 1);

            FrontEndDescriptor descriptor = registry.Find("Simulated source");
            Assert.NotNull(descriptor);
            Assert.Equal(typeof(SimulatedFrontEnd).FullName, descriptor.TypeName);
            Assert.Equal("OpenVSA.Hal.Sim", descriptor.AssemblyName);
        }

        [Fact]
        public void ADiscoveredProviderCanBeCreatedAndUsed()
        {
            // Discovery is worthless if the descriptor cannot produce a working front end, so the
            // test goes as far as negotiating - the first thing the UI will do with one.
            var registry = new FrontEndRegistry();
            registry.AddAssembly(typeof(SimulatedFrontEnd).Assembly);

            using (IFrontEnd frontEnd = registry.Find("Simulated source").Create())
            {
                Assert.Equal(FrontEndState.Disconnected, frontEnd.State);

                AcquisitionPlan plan = frontEnd.Negotiate(
                    new AcquisitionRequest(1e9, 1e6, 4096, -10.0));

                Assert.NotNull(plan);
                Assert.False(plan.Coerced);
            }
        }

        [Fact]
        public void ProbingTheTestOutputDirectoryFindsTheSimulator()
        {
            // The real path the shell takes, exercised against a real directory of real
            // assemblies rather than a hand-fed one.
            string directory = Path.GetDirectoryName(
                new Uri(typeof(FrontEndRegistryTests).Assembly.CodeBase).LocalPath);

            var registry = new FrontEndRegistry();
            registry.ProbeDirectory(directory);

            Assert.NotNull(registry.Find("Simulated source"));
        }

        [Fact]
        public void FindIsCaseInsensitiveAndRejectsNothing()
        {
            var registry = new FrontEndRegistry();
            registry.AddAssembly(typeof(SimulatedFrontEnd).Assembly);

            Assert.NotNull(registry.Find("simulated SOURCE"));
            Assert.Null(registry.Find("no such front end"));
            Assert.Null(registry.Find(null));
            Assert.Null(registry.Find(string.Empty));
        }

        [Fact]
        public void AnAssemblyIsNotScannedTwice()
        {
            // The shell probes the application directory and then the FrontEnds subdirectory. If
            // one shadows the other, every provider would otherwise be listed twice.
            var registry = new FrontEndRegistry();

            int first = registry.AddAssembly(typeof(SimulatedFrontEnd).Assembly);
            int second = registry.AddAssembly(typeof(SimulatedFrontEnd).Assembly);

            Assert.True(first >= 1);
            Assert.Equal(0, second);
            Assert.Single(registry.Providers, p => p.DisplayName == "Simulated source");
        }

        // ---- Failures are data, not exceptions --------------------------------------------------

        [Fact]
        public void AMissingPluginDirectoryIsNotAFailure()
        {
            var registry = new FrontEndRegistry();

            int added = registry.ProbeDirectory(Path.Combine(_scratch, "does-not-exist"));

            Assert.Equal(0, added);
            Assert.Empty(registry.Failures);
        }

        [Fact]
        public void AFileThatIsNotAnAssemblyIsRecordedAndSkipped()
        {
            string junk = Path.Combine(_scratch, "OpenVSA.Hal.Junk.dll");
            File.WriteAllText(junk, "this is not a portable executable");

            var registry = new FrontEndRegistry();
            registry.ProbeDirectory(_scratch);

            Assert.Empty(registry.Providers);
            Assert.Single(registry.Failures);
            Assert.Contains("OpenVSA.Hal.Junk.dll", registry.Failures[0].Source);
        }

        [Fact]
        public void OneUnusablePluginDoesNotPreventDiscoveringTheOthers()
        {
            // This is REQ-NFR-032 in miniature. A folder containing a broken plug-in alongside a
            // good one must still yield the good one - otherwise a machine with no VISA runtime
            // loses the simulator too, and the application has nothing to run with at all.
            File.WriteAllText(Path.Combine(_scratch, "OpenVSA.Hal.Broken.dll"), "not an assembly");
            File.Copy(
                new Uri(typeof(SimulatedFrontEnd).Assembly.CodeBase).LocalPath,
                Path.Combine(_scratch, "OpenVSA.Hal.Sim.dll"));

            var registry = new FrontEndRegistry();
            registry.ProbeDirectory(_scratch);

            Assert.NotNull(registry.Find("Simulated source"));
            Assert.Contains(registry.Failures, f => f.Source.Contains("Broken"));
        }

        [Fact]
        public void ProbeDirectoryRejectsNull()
        {
            Assert.Throws<ArgumentNullException>(() => new FrontEndRegistry().ProbeDirectory(null));
        }

        [Fact]
        public void AddAssemblyRejectsNull()
        {
            Assert.Throws<ArgumentNullException>(() => new FrontEndRegistry().AddAssembly(null));
        }

        // ---- Malformed providers are reported at discovery, not at first use ---------------------

        [Fact]
        public void AProviderWithoutAParameterlessConstructorIsReported()
        {
            var registry = new FrontEndRegistry();
            registry.AddAssembly(typeof(FrontEndRegistryTests).Assembly);

            Assert.Contains(
                registry.Failures,
                f => f.Source == typeof(NeedsAnArgument).FullName &&
                     f.Reason.Contains("parameterless constructor"));

            // Reported at discovery rather than at the click that would have created it: the user
            // finds out the provider is unusable before choosing it, not after.
            Assert.DoesNotContain(registry.Providers, p => p.TypeName == typeof(NeedsAnArgument).FullName);
        }

        [Fact]
        public void AMarkedTypeThatIsNotAFrontEndIsReported()
        {
            var registry = new FrontEndRegistry();
            registry.AddAssembly(typeof(FrontEndRegistryTests).Assembly);

            Assert.Contains(
                registry.Failures,
                f => f.Source == typeof(NotAFrontEnd).FullName &&
                     f.Reason.Contains("does not implement IFrontEnd"));
        }

        [Fact]
        public void AnAbstractProviderIsReported()
        {
            var registry = new FrontEndRegistry();
            registry.AddAssembly(typeof(FrontEndRegistryTests).Assembly);

            Assert.Contains(
                registry.Failures,
                f => f.Source == typeof(AbstractProvider).FullName &&
                     f.Reason.Contains("cannot be instantiated"));
        }

        // ---- The partial-load rule that REQ-NFR-032 rests on ------------------------------------

        [Fact]
        public void PartiallyLoadableAssembly_YieldsTheTypesThatDidLoad()
        {
            // On a machine with no VISA runtime, OpenVSA.Hal.Visa.dll loads but GetTypes() throws
            // ReflectionTypeLoadException as the CLR fails to resolve the VISA base types. The
            // exception carries partial results, and taking the non-null entries is what lets the
            // application start there rather than dying on the folder scan.
            //
            // A real one cannot be produced from a test without shipping a deliberately broken
            // assembly, so the salvage rule is asserted against a constructed exception.
            var exception = new ReflectionTypeLoadException(
                new[] { typeof(string), null, typeof(int) },
                new Exception[] { new TypeLoadException("Could not load type 'Ivi.Visa.IMessageSession'.") });

            Type[] salvaged = AssemblyTypes.Salvage(exception);

            Assert.Equal(2, salvaged.Length);
            Assert.Contains(typeof(string), salvaged);
            Assert.Contains(typeof(int), salvaged);
            Assert.DoesNotContain(null, salvaged);
        }

        [Fact]
        public void PartialLoadFailure_NamesTheMissingDependency()
        {
            // "some types could not be loaded" is not actionable. The loader's own message names
            // the type that could not be resolved, which is what tells an operator that VISA is
            // missing rather than that the product is broken.
            var exception = new ReflectionTypeLoadException(
                new Type[] { null },
                new Exception[] { new TypeLoadException("Could not load type 'Ivi.Visa.IMessageSession'.") });

            Assert.Contains("Ivi.Visa.IMessageSession", AssemblyTypes.DescribeFirstLoaderError(exception));
        }

        [Fact]
        public void SalvageToleratesAnExceptionWithNoTypes()
        {
            Assert.Empty(AssemblyTypes.Salvage(null));
            Assert.Equal(
                "no further detail",
                AssemblyTypes.DescribeFirstLoaderError(
                    new ReflectionTypeLoadException(new Type[0], new Exception[0])));
        }

        // ---- Deliberately malformed providers, for the tests above ------------------------------

        /// <summary>Marked as a provider but needs a constructor argument.</summary>
        [FrontEndProvider("Needs an argument")]
        private sealed class NeedsAnArgument : IFrontEnd
        {
            public NeedsAnArgument(int unused)
            {
                Unused = unused;
            }

            public int Unused { get; }

            public FrontEndId Id => default(FrontEndId);
            public string DisplayName => "Needs an argument";
            public IFrontEndCapabilities Capabilities => null;
            public FrontEndState State => FrontEndState.Disconnected;

            public event EventHandler<FrontEndEvent> Notification;

            public System.Threading.Tasks.Task ConnectAsync(System.Threading.CancellationToken ct) => null;
            public System.Threading.Tasks.Task DisconnectAsync() => null;
            public AcquisitionPlan Negotiate(AcquisitionRequest request) => null;
            public System.Threading.Tasks.Task ConfigureAsync(AcquisitionPlan plan, System.Threading.CancellationToken ct) => null;
            public System.Threading.Tasks.Task ArmAsync(System.Threading.CancellationToken ct) => null;
            public System.Threading.Tasks.Task<IqBlock> AcquireNextAsync(System.Threading.CancellationToken ct) => null;
            public System.Threading.Tasks.Task AbortAsync() => null;
            public void Dispose() => Notification?.Invoke(this, null);
        }

        /// <summary>Marked as a provider but does not implement the interface.</summary>
        [FrontEndProvider("Not a front end")]
        private sealed class NotAFrontEnd
        {
        }

        /// <summary>Marked as a provider but abstract.</summary>
        [FrontEndProvider("Abstract provider")]
        private abstract class AbstractProvider : IFrontEnd
        {
            public FrontEndId Id => default(FrontEndId);
            public string DisplayName => "Abstract";
            public IFrontEndCapabilities Capabilities => null;
            public FrontEndState State => FrontEndState.Disconnected;

            public event EventHandler<FrontEndEvent> Notification;

            public System.Threading.Tasks.Task ConnectAsync(System.Threading.CancellationToken ct) => null;
            public System.Threading.Tasks.Task DisconnectAsync() => null;
            public AcquisitionPlan Negotiate(AcquisitionRequest request) => null;
            public System.Threading.Tasks.Task ConfigureAsync(AcquisitionPlan plan, System.Threading.CancellationToken ct) => null;
            public System.Threading.Tasks.Task ArmAsync(System.Threading.CancellationToken ct) => null;
            public System.Threading.Tasks.Task<IqBlock> AcquireNextAsync(System.Threading.CancellationToken ct) => null;
            public System.Threading.Tasks.Task AbortAsync() => null;
            public void Dispose() => Notification?.Invoke(this, null);
        }
    }
}
