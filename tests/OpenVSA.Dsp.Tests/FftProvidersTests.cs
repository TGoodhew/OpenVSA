using System;
using System.Configuration;
using System.Linq;
using OpenVSA.Dsp.Fft;
using Xunit;

namespace OpenVSA.Dsp.Tests
{
    /// <summary>
    /// <c>REQ-NFR-004</c>: providers are registered by attribute and selected by configuration, so
    /// changing the active provider recompiles no DSP code.
    /// </summary>
    public class FftProvidersTests
    {
        [Fact]
        public void MoreThanOneProviderIsRegistered()
        {
            Assert.True(FftProviders.All.Count >= 2,
                "The parametrised suite is only meaningful with more than one provider.");
        }

        [Fact]
        public void RegisteredProvidersAreDiscoveredByAttribute()
        {
            // The same discovery rule as front ends (REQ-HAL-003): adding a provider never means
            // editing a registry in core code.
            Type[] marked = typeof(IFftProvider).Assembly
                .GetTypes()
                .Where(t => t.GetCustomAttributes(typeof(FftProviderAttribute), false).Any())
                .ToArray();

            Assert.NotEmpty(marked);

            foreach (Type type in marked)
            {
                var attribute = (FftProviderAttribute)type
                    .GetCustomAttributes(typeof(FftProviderAttribute), false)
                    .Single();

                Assert.True(typeof(IFftProvider).IsAssignableFrom(type),
                    type.Name + " is marked [FftProvider] but does not implement IFftProvider.");
                Assert.NotNull(FftProviders.Find(attribute.Name));
            }
        }

        [Fact]
        public void TheDefaultProviderIsManagedAndCarriesNoCopyleftObligation()
        {
            // REQ-NFR-004 and REQ-NFR-008. The shipped default must not be a native library whose
            // licence could block distribution — the reason the interface exists at all.
            IFftProvider provider = FftProviders.Find(FftProviders.DefaultProviderName);

            Assert.NotNull(provider);
            Assert.False(provider.IsNativeAccelerated);
            Assert.Equal(53, provider.SignificandBits);
        }

        [Fact]
        public void TheSameSuiteRunsAgainstEveryProviderWithoutRebuilding()
        {
            // The acceptance criterion asks for the suite to run twice with different providers
            // selected and the same binaries. This is that, in miniature: the identical transform
            // is driven through each registered provider, resolved by name at run time.
            foreach (IFftProvider provider in FftProviders.All)
            {
                var data = new double[128];
                data[0] = 1.0;

                provider.Forward(data);

                Assert.True(Math.Abs(data[0] - 1.0) < 1e-6, provider.Name + " failed the impulse pair.");
                Assert.True(Math.Abs(data[126] - 1.0) < 1e-6, provider.Name + " failed the impulse pair.");
            }
        }

        [Fact]
        public void ActiveIsSettableAndRoundTrips()
        {
            IFftProvider original = FftProviders.Active;
            try
            {
                IFftProvider other = FftProviders.All.First(p => !ReferenceEquals(p, original));
                FftProviders.Active = other;

                Assert.Same(other, FftProviders.Active);
            }
            finally
            {
                FftProviders.Active = original;
            }
        }

        [Fact]
        public void ActiveRejectsNull()
        {
            Assert.Throws<ArgumentNullException>(() => FftProviders.Active = null);
        }

        [Fact]
        public void RegisterRejectsNull()
        {
            Assert.Throws<ArgumentNullException>(() => FftProviders.Register(null));
        }

        [Fact]
        public void DiscoverInRejectsNull()
        {
            Assert.Throws<ArgumentNullException>(() => FftProviders.DiscoverIn(null));
        }

        [Fact]
        public void FindReturnsNullForAnUnknownName()
        {
            Assert.Null(FftProviders.Find("NoSuchProvider"));
            Assert.Null(FftProviders.Find(null));
            Assert.Null(FftProviders.Find(string.Empty));
        }

        [Fact]
        public void FindIsCaseInsensitive()
        {
            Assert.NotNull(FftProviders.Find("managed"));
            Assert.NotNull(FftProviders.Find("MANAGED"));
        }

        [Fact]
        public void SelectingAnUnknownProviderIsFatal()
        {
            // Deliberately not a fallback to the default. A deployment that asked for a native
            // provider and silently got the managed one would appear to be meeting its throughput
            // target when it was not, and nothing downstream could tell.
            var thrown = Assert.Throws<ConfigurationErrorsException>(
                () => FftProviders.Resolve("NotARealProvider"));

            Assert.Contains("NotARealProvider", thrown.Message);

            // The message must name what is available, or a misconfigured deployment gives the
            // operator nothing to act on.
            Assert.Contains("Managed", thrown.Message);
        }

        [Fact]
        public void ResolveReturnsTheNamedProvider()
        {
            Assert.Same(
                FftProviders.Find(FftProviders.DefaultProviderName),
                FftProviders.Resolve(FftProviders.DefaultProviderName));
        }

        [Fact(Skip = "REQ-NFR-004 requires a native provider (oneMKL or IPP). Not yet implemented; " +
                     "the interface, registry and parametrised suite are in place to receive one.")]
        public void ANativeProviderIsRegistered()
        {
            Assert.Contains(FftProviders.All, p => p.IsNativeAccelerated);
        }
    }
}
