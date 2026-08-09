using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reflection;

namespace OpenVSA.Ui.Bench
{
    /// <summary>
    /// A discovered test signal source: what it is called, and how to open one.
    /// </summary>
    public sealed class StimulusDescriptor
    {
        private readonly Type _providerType;
        private readonly Func<string, object> _factory;

        /// <summary>Describes a discovered source.</summary>
        /// <param name="displayName">Name to offer it under.</param>
        /// <param name="requiresResource">Whether it must be told an address.</param>
        /// <param name="defaultResource">The address to offer as a starting point.</param>
        /// <param name="providerType">The implementing type, which names it in a failure.</param>
        /// <param name="factory">
        /// Creates the instance, or <c>null</c> to construct <paramref name="providerType"/>.
        /// Discovery passes null; a test that needs to inspect the source the model is driving
        /// passes the one it prepared, because constructing by type would hand it a different one.
        /// </param>
        internal StimulusDescriptor(
            string displayName,
            bool requiresResource,
            string defaultResource,
            Type providerType,
            Func<string, object> factory = null)
        {
            DisplayName = displayName;
            RequiresResource = requiresResource;
            DefaultResource = defaultResource ?? string.Empty;
            _providerType = providerType;
            _factory = factory;
        }

        /// <summary>Name shown in the panel, as the provider declares it.</summary>
        public string DisplayName { get; }

        /// <summary>Whether an address has to be supplied before this source can be opened.</summary>
        public bool RequiresResource { get; }

        /// <summary>The address to offer as a starting point, for one that needs an address.</summary>
        /// <remarks>
        /// Offered, never used silently. A bench instrument's address moves, and a stale one fails
        /// in a way that reads exactly like a powered-off instrument — so what the panel opens is
        /// what the panel shows.
        /// </remarks>
        public string DefaultResource { get; }

        /// <summary>Full name of the implementing type.</summary>
        public string TypeName => _providerType.FullName;

        /// <summary>Simple name of the assembly the source came from.</summary>
        public string AssemblyName => _providerType.Assembly.GetName().Name;

        /// <summary>
        /// Creates an unconnected source.
        /// </summary>
        /// <param name="resource">
        /// The address to open, for a source that <see cref="RequiresResource"/>; ignored by one
        /// that does not.
        /// </param>
        /// <returns>A source that has not yet been connected.</returns>
        /// <exception cref="InvalidOperationException">The source could not be constructed.</exception>
        public StimulusSource Create(string resource)
        {
            object instance;

            try
            {
                instance = _factory != null
                    ? _factory(resource)
                    : RequiresResource
                        ? Activator.CreateInstance(_providerType, resource)
                        : Activator.CreateInstance(_providerType);
            }
            catch (Exception e)
            {
                throw new InvalidOperationException(
                    "Could not open '" + DisplayName + "': " + Unwrap(e).Message, Unwrap(e));
            }

            return StimulusSource.Around(instance, DisplayName);
        }

        /// <inheritdoc />
        public override string ToString() => DisplayName + " (" + AssemblyName + ")";

        internal static Exception Unwrap(Exception e) =>
            e is TargetInvocationException && e.InnerException != null ? e.InnerException : e;
    }

    /// <summary>
    /// Why a candidate file or type did not yield a usable test signal source.
    /// </summary>
    /// <remarks>
    /// Discovery failures are <em>data</em>, for the reason <c>FrontEndDiscoveryFailure</c> says
    /// they are: <c>REQ-NFR-032</c> requires the application to start with no hardware and no VISA
    /// installed, and in that configuration a source that talks over VISA is present on disk and
    /// will not load. If that threw, the application would fail to start on exactly the machine the
    /// requirement is about.
    /// </remarks>
    public sealed class StimulusDiscoveryFailure
    {
        internal StimulusDiscoveryFailure(string source, string reason)
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
    /// Finds the test signal sources this build can drive, at run time (issue #393, scope A).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Discovery, because a reference is not allowed here.</strong> The sources live in the
    /// cross-validation harness, which is test infrastructure and talks over VISA. <c>REQ-ARC-001</c>
    /// bars test infrastructure from becoming a dependency of the product, and <c>REQ-NFR-032</c>
    /// requires this application to start with no VISA installed at all — so the shell cannot
    /// reference the harness at compile time, and finds it the same way it finds front ends: by
    /// looking for it beside itself and degrading with a stated reason when it is not there.
    /// </para>
    /// <para>
    /// <strong>Not there is the normal case for an installed copy.</strong> The harness is a
    /// developer-build component and is deliberately not in the installer's payload, so an
    /// installed OpenVSA discovers nothing, disables the menu item and says why. That is the
    /// honest arrangement rather than an accident of packaging: driving a generator is bench
    /// equipment for cross-validating this product, not a feature of it.
    /// </para>
    /// <para>
    /// <strong>The attribute is matched by name.</strong> There is no shared assembly declaring it
    /// — that is precisely the coupling this class exists to avoid — so the match is on the
    /// attribute type's full name and the members are late-bound in
    /// <see cref="StimulusSource"/>. The cost is that a rename in the harness breaks a binding no
    /// compiler can see; the guard is a test that loads the real harness and asserts every name
    /// used here is still there, naming the one that is not.
    /// </para>
    /// </remarks>
    public sealed class StimulusRegistry
    {
        /// <summary>Subdirectory of the application folder that sources are loaded from.</summary>
        public const string PluginDirectoryName = "TestHarness";

        /// <summary>File pattern for candidate assemblies.</summary>
        /// <remarks>
        /// A pattern rather than every <c>*.dll</c>, for <c>FrontEndRegistry</c>'s reason: loading
        /// arbitrary files to see whether they happen to contain a source is slow, and it runs
        /// assembly initialisers from files that were never meant to be loaded.
        /// </remarks>
        public const string PluginSearchPattern = "OpenVSA.TestHarness.dll";

        /// <summary>Full name of the attribute a source is marked with.</summary>
        public const string ProviderAttributeName = "OpenVSA.TestHarness.StimulusProviderAttribute";

        /// <summary>Full name of the interface every source implements.</summary>
        public const string SourceInterfaceName = "OpenVSA.TestHarness.IStimulusSource";

        private readonly List<StimulusDescriptor> _sources = new List<StimulusDescriptor>();
        private readonly List<StimulusDiscoveryFailure> _failures = new List<StimulusDiscoveryFailure>();
        private readonly HashSet<string> _seenAssemblies =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Sources discovered so far, in discovery order.</summary>
        public IReadOnlyList<StimulusDescriptor> Sources =>
            new ReadOnlyCollection<StimulusDescriptor>(_sources);

        /// <summary>Candidates that could not be used, and why.</summary>
        public IReadOnlyList<StimulusDiscoveryFailure> Failures =>
            new ReadOnlyCollection<StimulusDiscoveryFailure>(_failures);

        /// <summary>Whether any source was found.</summary>
        public bool IsAvailable => _sources.Count > 0;

        /// <summary>
        /// Why the panel cannot be opened, or an empty string when it can.
        /// </summary>
        /// <remarks>
        /// This is what the disabled menu item says. Issue #393 asks for "visibly disabled with the
        /// reason, never a startup failure", and a reason that merely said "unavailable" would
        /// leave a user unable to tell a build without the harness from a harness that failed to
        /// load — which are different problems with different answers.
        /// </remarks>
        public string UnavailableReason
        {
            get
            {
                if (IsAvailable)
                {
                    return string.Empty;
                }

                if (_failures.Count > 0)
                {
                    return "The test signal source could not be loaded — " + _failures[0].Reason;
                }

                return "No test signal source is present. The bench harness that drives a signal " +
                       "generator is a developer-build component and is not part of the installed " +
                       "product.";
            }
        }

        /// <summary>
        /// Builds a registry from the application directory and its harness subdirectory.
        /// </summary>
        /// <returns>A populated registry. Never throws for a missing or unusable source.</returns>
        public static StimulusRegistry CreateDefault()
        {
            var registry = new StimulusRegistry();
            string baseDirectory = AppDomain.CurrentDomain.BaseDirectory;

            registry.ProbeDirectory(baseDirectory);
            registry.ProbeDirectory(Path.Combine(baseDirectory, PluginDirectoryName));

            return registry;
        }

        /// <summary>
        /// Loads every matching assembly in a directory and registers the sources it declares.
        /// </summary>
        /// <param name="directory">Directory to probe. A directory that does not exist is not an error.</param>
        /// <returns>The number of sources added.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="directory"/> is null.</exception>
        public int ProbeDirectory(string directory)
        {
            if (directory == null)
            {
                throw new ArgumentNullException(nameof(directory));
            }

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
                    _failures.Add(new StimulusDiscoveryFailure(
                        Path.GetFileName(file), "could not be loaded — " + e.Message));
                    continue;
                }

                added += AddAssembly(assembly);
            }

            return added;
        }

        /// <summary>
        /// Registers every source an assembly declares.
        /// </summary>
        /// <param name="assembly">Assembly to scan.</param>
        /// <returns>The number of sources added.</returns>
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
                // otherwise list every source twice.
                return 0;
            }

            int added = 0;

            foreach (Type type in Loadable(assembly, name))
            {
                CustomAttributeData marker = type
                    .GetCustomAttributesData()
                    .FirstOrDefault(a => a.AttributeType.FullName == ProviderAttributeName);

                if (marker == null)
                {
                    continue;
                }

                if (!type.GetInterfaces().Any(i => i.FullName == SourceInterfaceName))
                {
                    _failures.Add(new StimulusDiscoveryFailure(
                        type.FullName, "is marked as a test signal source but is not one"));
                    continue;
                }

                if (type.IsAbstract || type.IsInterface)
                {
                    _failures.Add(new StimulusDiscoveryFailure(
                        type.FullName, "is marked as a test signal source but cannot be created"));
                    continue;
                }

                string displayName = marker.ConstructorArguments.Count > 0
                    ? marker.ConstructorArguments[0].Value as string
                    : null;

                if (string.IsNullOrEmpty(displayName))
                {
                    _failures.Add(new StimulusDiscoveryFailure(
                        type.FullName, "is marked as a test signal source but is not named"));
                    continue;
                }

                bool requiresResource = NamedArgument(marker, "RequiresResource") as bool? ?? false;
                string defaultResource = NamedArgument(marker, "DefaultResource") as string;

                Type[] signature = requiresResource ? new[] { typeof(string) } : Type.EmptyTypes;

                if (type.GetConstructor(signature) == null)
                {
                    // Reported at discovery rather than at the click that would have created it,
                    // so a user finds out a source is unusable before choosing it.
                    _failures.Add(new StimulusDiscoveryFailure(
                        type.FullName,
                        "has no constructor the shell can use" +
                        (requiresResource ? " (it asks for an address)" : string.Empty)));
                    continue;
                }

                string missing = StimulusSource.FirstUnbindableMember(type);

                if (missing != null)
                {
                    // The rename this class's remarks warn about, caught at the only moment it can
                    // be: the member is named, rather than the panel silently doing nothing.
                    _failures.Add(new StimulusDiscoveryFailure(
                        type.FullName, "does not provide '" + missing + "'"));
                    continue;
                }

                _sources.Add(new StimulusDescriptor(
                    displayName, requiresResource, defaultResource, type));
                added++;
            }

            return added;
        }

        /// <summary>Finds a source by display name, case-insensitively.</summary>
        /// <param name="displayName">Name to look for.</param>
        /// <returns>The descriptor, or null if there is none.</returns>
        public StimulusDescriptor Find(string displayName) =>
            string.IsNullOrEmpty(displayName)
                ? null
                : _sources.FirstOrDefault(
                    s => string.Equals(s.DisplayName, displayName, StringComparison.OrdinalIgnoreCase));

        private static object NamedArgument(CustomAttributeData attribute, string name)
        {
            foreach (CustomAttributeNamedArgument argument in attribute.NamedArguments)
            {
                if (string.Equals(argument.MemberName, name, StringComparison.Ordinal))
                {
                    return argument.TypedValue.Value;
                }
            }

            return null;
        }

        private IEnumerable<Type> Loadable(Assembly assembly, string name)
        {
            try
            {
                return assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException e)
            {
                // The VISA-less machine, again. A source whose transport will not resolve is one
                // type that cannot be examined, not a reason to abandon the assembly: the source
                // that needs no instrument is in the same file and is still perfectly usable.
                foreach (Exception failure in e.LoaderExceptions ?? new Exception[0])
                {
                    if (failure != null)
                    {
                        _failures.Add(new StimulusDiscoveryFailure(name, failure.Message));
                    }
                }

                return e.Types.Where(t => t != null);
            }
        }
    }
}
