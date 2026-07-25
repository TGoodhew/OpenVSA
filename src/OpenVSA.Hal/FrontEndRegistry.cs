using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reflection;

namespace OpenVSA.Hal
{
    /// <summary>
    /// A discovered front-end provider: what it is called, and how to create one.
    /// </summary>
    public sealed class FrontEndDescriptor
    {
        private readonly Type _providerType;

        internal FrontEndDescriptor(string displayName, Type providerType)
        {
            DisplayName = displayName;
            _providerType = providerType;
        }

        /// <summary>Name shown in the connection UI, from <see cref="FrontEndProviderAttribute"/>.</summary>
        public string DisplayName { get; }

        /// <summary>Full name of the implementing type.</summary>
        public string TypeName => _providerType.FullName;

        /// <summary>Simple name of the assembly the provider came from.</summary>
        public string AssemblyName => _providerType.Assembly.GetName().Name;

        /// <summary>Creates an instance of this front end.</summary>
        /// <returns>A new, unconnected front end.</returns>
        /// <exception cref="InvalidOperationException">The provider could not be constructed.</exception>
        public IFrontEnd Create()
        {
            try
            {
                return (IFrontEnd)Activator.CreateInstance(_providerType);
            }
            catch (Exception e)
            {
                throw new InvalidOperationException(
                    "Could not create front end '" + DisplayName + "' (" + TypeName + "): " +
                    e.Message, e);
            }
        }

        /// <inheritdoc />
        public override string ToString() => DisplayName + " (" + AssemblyName + ")";
    }

    /// <summary>
    /// Why a candidate assembly or type did not yield a usable front end.
    /// </summary>
    /// <remarks>
    /// Discovery failures are <em>data</em>, not exceptions. <c>REQ-NFR-032</c> requires the
    /// application to start and run usefully with no hardware and no VISA installed — and in that
    /// configuration <c>OpenVSA.Hal.Visa</c> is present on disk but its VISA dependency will not
    /// resolve. If that threw, the application would fail to start on exactly the machine the
    /// requirement is about. Instead the provider is absent, the reason is recorded, and the UI
    /// can say why the option is unavailable.
    /// </remarks>
    public sealed class FrontEndDiscoveryFailure
    {
        internal FrontEndDiscoveryFailure(string source, string reason)
        {
            Source = source;
            Reason = reason;
        }

        /// <summary>The file or type that failed.</summary>
        public string Source { get; }

        /// <summary>What went wrong, in terms a user can act on.</summary>
        public string Reason { get; }

        /// <inheritdoc />
        public override string ToString() => Source + ": " + Reason;
    }

    /// <summary>
    /// Discovers front ends from plug-in assemblies at start-up (<c>REQ-HAL-003</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// This exists so that <c>OpenVSA.Ui</c> can offer the simulator without referencing
    /// <c>OpenVSA.Hal.Sim</c>. <c>REQ-ARC-001</c> bars every layer from L3 upward — the UI
    /// included — from a compile-time reference to any L0 transport assembly, so the only way the
    /// shell can offer a concrete source is to find one at run time.
    /// </para>
    /// <para>
    /// Discovery is by <see cref="FrontEndProviderAttribute"/>, which is what keeps
    /// <c>REQ-HAL-002</c>'s prohibition on instrument-specific conditionals enforceable: adding a
    /// front end never means editing a registry in core code, so there is never a reason for the
    /// UI to name one.
    /// </para>
    /// </remarks>
    public sealed class FrontEndRegistry
    {
        /// <summary>Subdirectory of the application folder that plug-in front ends are loaded from.</summary>
        public const string PluginDirectoryName = "FrontEnds";

        /// <summary>File pattern for candidate plug-in assemblies.</summary>
        /// <remarks>
        /// A pattern rather than every <c>*.dll</c> in the folder. Loading arbitrary files to see
        /// whether they happen to contain a provider is slow, and it runs assembly initialisers
        /// from files that were never meant to be plug-ins.
        /// </remarks>
        public const string PluginSearchPattern = "OpenVSA.Hal.*.dll";

        private readonly List<FrontEndDescriptor> _providers = new List<FrontEndDescriptor>();
        private readonly List<FrontEndDiscoveryFailure> _failures = new List<FrontEndDiscoveryFailure>();
        private readonly HashSet<string> _seenAssemblies =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Front ends discovered so far, in discovery order.</summary>
        public IReadOnlyList<FrontEndDescriptor> Providers =>
            new ReadOnlyCollection<FrontEndDescriptor>(_providers);

        /// <summary>Candidates that could not be used, and why.</summary>
        public IReadOnlyList<FrontEndDiscoveryFailure> Failures =>
            new ReadOnlyCollection<FrontEndDiscoveryFailure>(_failures);

        /// <summary>
        /// Builds a registry from the application directory and its <c>FrontEnds</c> subdirectory.
        /// </summary>
        /// <returns>A populated registry. Never throws for a missing or unusable plug-in.</returns>
        public static FrontEndRegistry CreateDefault()
        {
            var registry = new FrontEndRegistry();
            string baseDirectory = AppDomain.CurrentDomain.BaseDirectory;

            registry.ProbeDirectory(baseDirectory);
            registry.ProbeDirectory(Path.Combine(baseDirectory, PluginDirectoryName));

            return registry;
        }

        /// <summary>
        /// Loads every assembly in <paramref name="directory"/> matching
        /// <see cref="PluginSearchPattern"/> and registers the providers it finds.
        /// </summary>
        /// <param name="directory">Directory to probe. A directory that does not exist is not an error.</param>
        /// <returns>The number of providers added.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="directory"/> is null.</exception>
        public int ProbeDirectory(string directory)
        {
            if (directory == null)
            {
                throw new ArgumentNullException(nameof(directory));
            }

            // A missing plug-in folder is the normal case for a plain deployment, not a fault.
            if (!Directory.Exists(directory))
            {
                return 0;
            }

            int added = 0;

            foreach (string file in Directory.GetFiles(directory, PluginSearchPattern))
            {
                Assembly assembly;

                try
                {
                    assembly = Assembly.LoadFrom(file);
                }
                catch (Exception e)
                {
                    // BadImageFormatException for a native or corrupt file, FileLoadException for
                    // one that is locked or blocked by zone identifier. Either way the rest of the
                    // folder must still be discovered.
                    _failures.Add(new FrontEndDiscoveryFailure(
                        Path.GetFileName(file), "could not be loaded — " + e.Message));
                    continue;
                }

                added += AddAssembly(assembly);
            }

            return added;
        }

        /// <summary>
        /// Registers every <see cref="FrontEndProviderAttribute"/>-marked type in an assembly.
        /// </summary>
        /// <param name="assembly">Assembly to scan.</param>
        /// <returns>The number of providers added.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="assembly"/> is null.</exception>
        public int AddAssembly(Assembly assembly)
        {
            if (assembly == null)
            {
                throw new ArgumentNullException(nameof(assembly));
            }

            string name = assembly.GetName().Name;
            if (!_seenAssemblies.Add(name))
            {
                // Probing the application directory and then a subdirectory that shadows it would
                // otherwise list every provider twice.
                return 0;
            }

            int added = 0;

            foreach (Type type in AssemblyTypes.Loadable(assembly, name, _failures))
            {
                var attribute = type.GetCustomAttribute<FrontEndProviderAttribute>();
                if (attribute == null)
                {
                    continue;
                }

                if (!typeof(IFrontEnd).IsAssignableFrom(type))
                {
                    _failures.Add(new FrontEndDiscoveryFailure(
                        type.FullName, "is marked [FrontEndProvider] but does not implement IFrontEnd"));
                    continue;
                }

                if (type.IsAbstract || type.IsInterface)
                {
                    _failures.Add(new FrontEndDiscoveryFailure(
                        type.FullName, "is marked [FrontEndProvider] but cannot be instantiated"));
                    continue;
                }

                if (type.GetConstructor(Type.EmptyTypes) == null)
                {
                    // Reported rather than thrown, and reported at discovery rather than at the
                    // click that would have created it — the user finds out the provider is
                    // unusable before choosing it, not after.
                    _failures.Add(new FrontEndDiscoveryFailure(
                        type.FullName, "has no public parameterless constructor"));
                    continue;
                }

                _providers.Add(new FrontEndDescriptor(attribute.DisplayName, type));
                added++;
            }

            return added;
        }

        /// <summary>Finds a provider by display name, case-insensitively.</summary>
        /// <param name="displayName">Name to look for.</param>
        /// <returns>The descriptor, or null if there is none.</returns>
        public FrontEndDescriptor Find(string displayName) =>
            string.IsNullOrEmpty(displayName)
                ? null
                : _providers.FirstOrDefault(
                    p => string.Equals(p.DisplayName, displayName, StringComparison.OrdinalIgnoreCase));
    }
}
