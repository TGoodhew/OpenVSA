using System;
using System.Collections.Generic;
using System.Reflection;

namespace OpenVSA.Hal
{
    /// <summary>
    /// Enumerates the types of an assembly that can actually be loaded.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This is the piece that makes <c>REQ-NFR-032</c> work.</strong> On a machine with no
    /// VISA runtime installed, <c>OpenVSA.Hal.Visa.dll</c> is still sitting in the application
    /// folder — it ships with the product — and it still loads. What fails is
    /// <see cref="Assembly.GetTypes"/>, when the CLR tries to resolve the VISA types its
    /// providers derive from, and it fails with a <see cref="ReflectionTypeLoadException"/>.
    /// </para>
    /// <para>
    /// That exception carries partial results: <see cref="ReflectionTypeLoadException.Types"/>
    /// holds every type that <em>did</em> load, with nulls where one did not. Taking the non-null
    /// entries is what lets a partially-loadable assembly still contribute the providers that have
    /// no unresolvable dependency, and lets the application start on a machine with no VISA rather
    /// than dying on the folder scan.
    /// </para>
    /// </remarks>
    internal static class AssemblyTypes
    {
        /// <summary>Returns the loadable types of an assembly, recording any shortfall.</summary>
        /// <param name="assembly">Assembly to enumerate.</param>
        /// <param name="source">Name used when recording a failure.</param>
        /// <param name="failures">Collects the reason when types could not all be loaded.</param>
        public static IEnumerable<Type> Loadable(
            Assembly assembly, string source, ICollection<FrontEndDiscoveryFailure> failures)
        {
            try
            {
                return assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException e)
            {
                Type[] loaded = Salvage(e);

                failures.Add(new FrontEndDiscoveryFailure(
                    source,
                    "some types could not be loaded, most likely a missing dependency — " +
                    DescribeFirstLoaderError(e) + " (" + loaded.Length + " of " +
                    e.Types.Length + " types usable)"));

                return loaded;
            }
            catch (Exception e)
            {
                failures.Add(new FrontEndDiscoveryFailure(
                    source, "types could not be enumerated — " + e.Message));

                return Array.Empty<Type>();
            }
        }

        /// <summary>Extracts the types that loaded from a partial failure.</summary>
        /// <param name="exception">The failure.</param>
        /// <remarks>Separate from <see cref="Loadable"/> so the salvage rule can be tested directly.</remarks>
        public static Type[] Salvage(ReflectionTypeLoadException exception)
        {
            if (exception?.Types == null)
            {
                return Array.Empty<Type>();
            }

            var loaded = new List<Type>(exception.Types.Length);
            foreach (Type type in exception.Types)
            {
                if (type != null)
                {
                    loaded.Add(type);
                }
            }

            return loaded.ToArray();
        }

        /// <summary>Summarises the first loader error, for a message a user can act on.</summary>
        /// <param name="exception">The failure.</param>
        public static string DescribeFirstLoaderError(ReflectionTypeLoadException exception)
        {
            if (exception?.LoaderExceptions == null)
            {
                return "no further detail";
            }

            foreach (Exception loaderException in exception.LoaderExceptions)
            {
                if (loaderException != null)
                {
                    return loaderException.Message;
                }
            }

            return "no further detail";
        }
    }
}
