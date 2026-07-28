using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reflection;

namespace OpenVSA.Personality
{
    /// <summary>Why a candidate assembly could not be used.</summary>
    public sealed class PersonalityDiscoveryFailure
    {
        /// <summary>Records a failure.</summary>
        /// <param name="candidate">The file or type.</param>
        /// <param name="reason">What went wrong, in words a user can act on.</param>
        public PersonalityDiscoveryFailure(string candidate, string reason)
        {
            Candidate = candidate ?? string.Empty;
            Reason = reason ?? string.Empty;
        }

        /// <summary>The file or type.</summary>
        public string Candidate { get; }

        /// <summary>What went wrong.</summary>
        public string Reason { get; }

        /// <inheritdoc />
        public override string ToString() => Candidate + ": " + Reason;
    }

    /// <summary>
    /// Discovers personalities from plug-in directories (<c>REQ-ARC-003</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately the same shape as <c>FrontEndRegistry</c>: probe a directory, load assemblies
    /// matching a pattern, instantiate the marked types, and record every failure rather than
    /// throwing. A single unusable plug-in must not stop the others being found, and it must not
    /// stop the application starting — <c>REQ-NFR-032</c> requires OpenVSA to run usefully with
    /// nothing installed at all.
    /// </para>
    /// <para>
    /// <strong>A pattern, not every <c>*.dll</c>.</strong> Loading arbitrary files to see whether
    /// they happen to contain a personality is slow and runs assembly initialisers from files that
    /// were never meant to be plug-ins.
    /// </para>
    /// </remarks>
    public sealed class PersonalityRegistry
    {
        /// <summary>Subdirectory of the application folder personalities are loaded from.</summary>
        /// <remarks>
        /// Named by <c>REQ-ARC-003</c> itself, and a subdirectory rather than the application
        /// folder so the plug-in boundary is visible on disk.
        /// </remarks>
        public const string PluginDirectoryName = "Personalities";

        /// <summary>File pattern for candidate plug-in assemblies.</summary>
        public const string PluginSearchPattern = "OpenVSA.Personality.*.dll";

        private readonly List<IMeasurementPersonality> _personalities = new List<IMeasurementPersonality>();
        private readonly List<PersonalityDiscoveryFailure> _failures = new List<PersonalityDiscoveryFailure>();
        private readonly HashSet<string> _seenAssemblies =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Personalities discovered so far, in discovery order.</summary>
        public IReadOnlyList<IMeasurementPersonality> Personalities =>
            new ReadOnlyCollection<IMeasurementPersonality>(_personalities);

        /// <summary>Candidates that could not be used, and why.</summary>
        public IReadOnlyList<PersonalityDiscoveryFailure> Failures =>
            new ReadOnlyCollection<PersonalityDiscoveryFailure>(_failures);

        /// <summary>
        /// Builds a registry from the application's <c>Personalities</c> subdirectory.
        /// </summary>
        /// <returns>A populated registry. Never throws for a missing or unusable plug-in.</returns>
        public static PersonalityRegistry CreateDefault()
        {
            var registry = new PersonalityRegistry();

            registry.ProbeDirectory(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, PluginDirectoryName));

            return registry;
        }

        /// <summary>Loads matching assemblies from a directory and registers what they contain.</summary>
        /// <param name="directory">Directory to probe. A missing directory is not an error.</param>
        /// <returns>The number of personalities added.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="directory"/> is null.</exception>
        public int ProbeDirectory(string directory)
        {
            if (directory == null)
            {
                throw new ArgumentNullException(nameof(directory));
            }

            // The normal case for a plain deployment, not a fault.
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
                    // one that is locked or blocked by a zone identifier. Either way the rest of
                    // the folder must still be discovered.
                    _failures.Add(new PersonalityDiscoveryFailure(
                        Path.GetFileName(file), "could not be loaded — " + e.Message));
                    continue;
                }

                added += AddAssembly(assembly);
            }

            return added;
        }

        /// <summary>Registers every marked type in an assembly.</summary>
        /// <param name="assembly">Assembly to scan.</param>
        /// <returns>The number of personalities added.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="assembly"/> is null.</exception>
        public int AddAssembly(Assembly assembly)
        {
            if (assembly == null)
            {
                throw new ArgumentNullException(nameof(assembly));
            }

            // Probing both the application directory and a plug-in folder can present the same
            // assembly twice; registering it twice would show every personality doubled.
            if (!_seenAssemblies.Add(assembly.GetName().Name))
            {
                return 0;
            }

            Type[] types;

            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException e)
            {
                // A plug-in built against a different version of the SDK loads but cannot resolve
                // all its types. The ones that did resolve are still usable.
                types = e.Types.Where(t => t != null).ToArray();

                _failures.Add(new PersonalityDiscoveryFailure(
                    assembly.GetName().Name,
                    "some types could not be loaded — " +
                    string.Join("; ", e.LoaderExceptions.Select(x => x.Message).Distinct().Take(3))));
            }

            int added = 0;

            foreach (Type type in types)
            {
                if (type.GetCustomAttribute<MeasurementPersonalityAttribute>() == null)
                {
                    continue;
                }

                if (!typeof(IMeasurementPersonality).IsAssignableFrom(type) ||
                    type.IsAbstract ||
                    type.GetConstructor(Type.EmptyTypes) == null)
                {
                    _failures.Add(new PersonalityDiscoveryFailure(
                        type.FullName,
                        "is marked [MeasurementPersonality] but is not a concrete " +
                        "IMeasurementPersonality with a parameterless constructor"));
                    continue;
                }

                try
                {
                    _personalities.Add((IMeasurementPersonality)Activator.CreateInstance(type));
                    added++;
                }
                catch (Exception e)
                {
                    _failures.Add(new PersonalityDiscoveryFailure(
                        type.FullName, "could not be constructed — " + Unwrap(e).Message));
                }
            }

            return added;
        }

        /// <summary>The personality with a given display name, or <c>null</c>.</summary>
        /// <param name="displayName">The name, compared case-insensitively.</param>
        public IMeasurementPersonality Find(string displayName) =>
            displayName == null
                ? null
                : _personalities.FirstOrDefault(
                    p => string.Equals(p.DisplayName, displayName, StringComparison.OrdinalIgnoreCase));

        private static Exception Unwrap(Exception failure)
        {
            while (failure is TargetInvocationException && failure.InnerException != null)
            {
                failure = failure.InnerException;
            }

            return failure;
        }
    }
}
