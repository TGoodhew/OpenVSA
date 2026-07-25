using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Configuration;
using System.Linq;
using System.Reflection;

namespace OpenVSA.Dsp.Fft
{
    /// <summary>
    /// The registry of available <see cref="IFftProvider"/>s and the configured active one.
    /// </summary>
    /// <remarks>
    /// <c>REQ-NFR-004</c> requires that selecting the provider is a configuration change which
    /// recompiles no DSP code. Nothing in the DSP layer may therefore name a provider type: it
    /// asks this registry for <see cref="Active"/> and gets whatever the configuration selected.
    /// </remarks>
    public static class FftProviders
    {
        /// <summary>The <c>appSettings</c> key that selects the active provider.</summary>
        public const string ConfigurationKey = "OpenVSA.Fft.Provider";

        /// <summary>The provider used when configuration says nothing.</summary>
        /// <remarks>
        /// Managed, so the default deployment carries no copyleft obligation and no native
        /// dependency (<c>REQ-NFR-004</c>, <c>REQ-NFR-008</c>).
        /// </remarks>
        public const string DefaultProviderName = "Managed";

        private static readonly object Gate = new object();
        private static readonly Dictionary<string, IFftProvider> Providers =
            new Dictionary<string, IFftProvider>(StringComparer.OrdinalIgnoreCase);

        private static IFftProvider _active;

        static FftProviders()
        {
            DiscoverIn(typeof(FftProviders).Assembly);
        }

        /// <summary>Every registered provider, in registration order.</summary>
        public static IReadOnlyList<IFftProvider> All
        {
            get
            {
                lock (Gate)
                {
                    return new ReadOnlyCollection<IFftProvider>(Providers.Values.ToList());
                }
            }
        }

        /// <summary>
        /// The provider in use. Resolved from configuration on first read.
        /// </summary>
        /// <remarks>
        /// Settable so a test can run the same suite against each provider in turn, which is how
        /// <c>REQ-NFR-004</c>'s acceptance criterion is met without rebuilding.
        /// </remarks>
        /// <exception cref="ConfigurationErrorsException">Configuration names a provider that is not registered.</exception>
        public static IFftProvider Active
        {
            get
            {
                lock (Gate)
                {
                    return _active ?? (_active = ResolveFromConfiguration());
                }
            }

            set
            {
                if (value == null)
                {
                    throw new ArgumentNullException(nameof(value));
                }

                lock (Gate)
                {
                    _active = value;
                }
            }
        }

        /// <summary>
        /// Registers types in <paramref name="assembly"/> that carry <see cref="FftProviderAttribute"/>.
        /// </summary>
        /// <param name="assembly">Assembly to scan.</param>
        /// <remarks>
        /// Called for this assembly at start-up. A native provider shipped separately registers by
        /// having its assembly scanned, which is what keeps the choice a deployment concern.
        /// </remarks>
        /// <exception cref="ArgumentNullException"><paramref name="assembly"/> is null.</exception>
        public static void DiscoverIn(Assembly assembly)
        {
            if (assembly == null)
            {
                throw new ArgumentNullException(nameof(assembly));
            }

            foreach (Type type in assembly.GetTypes())
            {
                var attribute = type.GetCustomAttribute<FftProviderAttribute>();
                if (attribute == null)
                {
                    continue;
                }

                if (!typeof(IFftProvider).IsAssignableFrom(type))
                {
                    throw new InvalidOperationException(
                        type.FullName + " is marked [FftProvider] but does not implement IFftProvider.");
                }

                Register((IFftProvider)Activator.CreateInstance(type));
            }
        }

        /// <summary>Registers a provider, replacing any registered under the same name.</summary>
        /// <param name="provider">The provider.</param>
        /// <exception cref="ArgumentNullException"><paramref name="provider"/> is null.</exception>
        public static void Register(IFftProvider provider)
        {
            if (provider == null)
            {
                throw new ArgumentNullException(nameof(provider));
            }

            lock (Gate)
            {
                Providers[provider.Name] = provider;
            }
        }

        /// <summary>Gets a provider by name.</summary>
        /// <param name="name">Provider name, matched case-insensitively.</param>
        /// <returns>The provider, or null if none is registered under that name.</returns>
        public static IFftProvider Find(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return null;
            }

            lock (Gate)
            {
                IFftProvider provider;
                return Providers.TryGetValue(name, out provider) ? provider : null;
            }
        }

        /// <summary>
        /// Gets a provider by name, failing if none is registered under it.
        /// </summary>
        /// <param name="name">Provider name, matched case-insensitively.</param>
        /// <returns>The provider.</returns>
        /// <exception cref="ConfigurationErrorsException">No provider is registered under that name.</exception>
        /// <remarks>
        /// Deliberately fatal rather than falling back to the default. A deployment that asks for
        /// a native provider and silently gets the managed one would look like it was meeting its
        /// throughput target when it was not, and nothing downstream could tell.
        /// </remarks>
        public static IFftProvider Resolve(string name)
        {
            IFftProvider provider = Find(name);
            if (provider != null)
            {
                return provider;
            }

            lock (Gate)
            {
                throw new ConfigurationErrorsException(
                    "No FFT provider named '" + name + "' is registered. Available: " +
                    string.Join(", ", Providers.Keys.OrderBy(k => k, StringComparer.Ordinal)) + ".");
            }
        }

        private static IFftProvider ResolveFromConfiguration()
        {
            string configured = ConfigurationManager.AppSettings[ConfigurationKey];
            return Resolve(string.IsNullOrEmpty(configured) ? DefaultProviderName : configured);
        }
    }
}
