using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Xunit;
using Xunit.Abstractions;

namespace OpenVSA.Architecture.Tests
{
    /// <summary>
    /// <c>REQ-LIC-010</c>: no entitlement or licence-check machinery exists in any shipped
    /// assembly, and exactly one edition is produced.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is a product decision stated normatively so that a later change of heart has to be
    /// argued for and re-specified, rather than arriving one <c>if</c> statement at a time. The
    /// check is what makes that true in practice: gating does not usually appear as a licensing
    /// subsystem, it appears as a single <c>IsFeatureEnabled</c> that nobody objects to.
    /// </para>
    /// <para>
    /// <strong>Public and internal, not just public.</strong> A gate is more likely to be internal
    /// than public — it is infrastructure, not API — so a scan of the exported surface would look
    /// clean while the thing itself sat one accessibility level down.
    /// </para>
    /// </remarks>
    public class NoLicenceGatingTests
    {
        private static readonly string[] ShippedAssemblies =
        {
            "OpenVSA.Core", "OpenVSA.Hal", "OpenVSA.Dsp", "OpenVSA.Capture",
            "OpenVSA.Measurement", "OpenVSA.Demod", "OpenVSA.Personality", "OpenVSA.Api",
        };

        /// <summary>
        /// Names that mean gating. Deliberately not "licence" alone.
        /// </summary>
        /// <remarks>
        /// The Syncfusion licence key is registered by the shell and is a third-party activation,
        /// not a gate on OpenVSA's own features — <c>SyncfusionLicense</c> must not be flagged, or
        /// the check fires on the one legitimate use of the word and gets suppressed. What is
        /// forbidden is machinery that decides whether a *feature of OpenVSA* may run.
        /// </remarks>
        private static readonly string[] ForbiddenNameFragments =
        {
            "Entitlement", "FeatureGate", "IsFeatureEnabled", "IsLicensed", "LicenceCheck",
            "LicenseCheck", "ActivationKey", "TrialPeriod", "EditionLevel", "SkuLevel",
        };

        private readonly ITestOutputHelper _output;

        /// <summary>Takes xunit's output sink.</summary>
        /// <param name="output">Where the examined member count is written.</param>
        public NoLicenceGatingTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void NoShippedAssemblyDeclaresGatingMachinery()
        {
            var offenders = new List<string>();
            int examined = 0;

            foreach (string name in ShippedAssemblies)
            {
                Assembly assembly = Assembly.Load(name);

                // GetTypes, not GetExportedTypes: a gate is infrastructure and more likely to be
                // internal than public.
                foreach (Type type in assembly.GetTypes())
                {
                    examined++;
                    Check(type.Name, name + " type " + type.FullName, offenders);

                    foreach (MemberInfo member in type.GetMembers(
                        BindingFlags.Public | BindingFlags.NonPublic |
                        BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                    {
                        examined++;
                        Check(member.Name, name + " member " + type.Name + "." + member.Name, offenders);
                    }
                }
            }

            _output.WriteLine(examined + " types and members examined across " +
                              ShippedAssemblies.Length + " shipped assemblies");

            Assert.True(examined > 1000, "Only " + examined + " members were examined.");
            Assert.False(offenders.Any(), string.Join(Environment.NewLine, offenders.Distinct()));
        }

        [Fact]
        public void NoLicensingProjectIsInTheSolution()
        {
            string solution = File.ReadAllText(Path.Combine(RepositoryRoot(), "OpenVSA.slnx"));

            foreach (string fragment in new[] { "Licence", "Licensing", "Entitlement" })
            {
                Assert.False(
                    solution.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0,
                    "OpenVSA.slnx names a project containing '" + fragment +
                    "'. REQ-LIC-010 requires the absence of any licensing project.");
            }
        }

        [Fact]
        public void NoBuildConfigurationPartitionsTheFeatureSet()
        {
            // "A test fails if a build configuration name or an #if symbol partitions the feature
            // set." Debug and Release differ in optimisation, not in what the product can do.
            var offenders = new List<string>();
            string root = RepositoryRoot();

            foreach (string file in Directory.GetFiles(root, "*.csproj", SearchOption.AllDirectories)
                         .Concat(Directory.GetFiles(root, "Directory.Build.props", SearchOption.AllDirectories)))
            {
                if (file.IndexOf(@"\obj\", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    file.IndexOf(@"\bin\", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    continue;
                }

                string text = File.ReadAllText(file);

                foreach (string fragment in new[] { "FREE_EDITION", "PRO_EDITION", "TRIAL", "LICENSED" })
                {
                    if (text.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        offenders.Add(Path.GetFileName(file) + " defines " + fragment);
                    }
                }
            }

            Assert.False(offenders.Any(), string.Join(Environment.NewLine, offenders));
        }

        private static void Check(string name, string where, List<string> offenders)
        {
            foreach (string fragment in ForbiddenNameFragments)
            {
                if (name.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    offenders.Add(where + " matches '" + fragment +
                                  "'. REQ-LIC-010: every feature is available to every user.");
                }
            }
        }

        private static string RepositoryRoot()
        {
            var directory = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);

            while (directory != null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "OpenVSA.slnx")))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }

            throw new InvalidOperationException("Could not find the repository root.");
        }
    }
}
