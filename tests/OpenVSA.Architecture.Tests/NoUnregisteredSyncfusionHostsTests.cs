using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace OpenVSA.Architecture.Tests
{
    /// <summary>
    /// Every path that constructs a Syncfusion control registers the licence first, and the
    /// registration itself happens in exactly one place.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Why this is enforced and not merely documented.</strong> Registration lived inline
    /// in <c>App</c>'s constructor, so it happened only for paths that went through <c>App</c>. The
    /// test host does not: it starts its own STA thread and builds a <c>ShellWindow</c> directly.
    /// An unlicensed Syncfusion control raises a **modal** trial dialog as it is constructed, that
    /// dialog blocked the test thread's dispatcher, and the snapshot soak rendered 1 frame in 49 s
    /// instead of thousands — failing with a message about tearing, which is a long way from the
    /// cause. The failure mode is bad enough, and the symptom misleading enough, that a convention
    /// is not sufficient.
    /// </para>
    /// <para>
    /// <strong>The rule.</strong> A file in <c>src/</c> that names a Syncfusion type outside a
    /// comment must contain a <c>SyncfusionLicense.Register()</c> call, and a XAML file that
    /// declares the Syncfusion namespace puts that obligation on its code-behind. Plus:
    /// <c>SyncfusionLicenseProvider.RegisterLicense</c> has exactly one call site in the whole
    /// tree, so the policy — resolve, guard, swallow, record — cannot be reimplemented differently
    /// somewhere else.
    /// </para>
    /// </remarks>
    public class NoUnregisteredSyncfusionHostsTests
    {
        /// <summary>The file that owns the policy and is therefore exempt from needing the call.</summary>
        private const string PolicyFile = "SyncfusionLicense.cs";

        /// <summary>A Syncfusion type or namespace, in C# or in XAML.</summary>
        /// <remarks>
        /// Case-insensitive because XAML spells the prefix in lower case
        /// (<c>&lt;syncfusion:DockingManager&gt;</c>) while C# spells the namespace capitalised.
        /// The first draft was case-sensitive and matched only the <c>xmlns</c> declaration, so a
        /// markup file that used the prefix without declaring it locally would have slipped
        /// through. <see cref="TheSearchWouldCatchAnUnregisteredHost"/> caught that.
        /// </remarks>
        private static readonly Regex SyncfusionType = new Regex(
            @"\bsyncfusion\s*[\.:]|xmlns:syncfusion\s*=",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex RegisterCall =
            new Regex(@"\bSyncfusionLicense\s*\.\s*Register\s*\(", RegexOptions.Compiled);

        [Fact]
        public void EveryFileThatTouchesSyncfusionRegistersTheLicence()
        {
            var offences = new List<string>();
            string source = Path.Combine(RepositoryRoot(), "src");

            foreach (string file in SourceFiles(source, "*.cs"))
            {
                if (string.Equals(Path.GetFileName(file), PolicyFile, StringComparison.Ordinal))
                {
                    continue;
                }

                string[] lines = File.ReadAllLines(file);

                if (!NamesSyncfusion(lines) || lines.Any(l => RegisterCall.IsMatch(l)))
                {
                    continue;
                }

                offences.Add(Path.GetFileName(file));
            }

            // A XAML file cannot call anything, so the obligation lands on its code-behind: the
            // DockingManager in ShellWindow.xaml is constructed by InitializeComponent, which is
            // exactly the call that has to be preceded by registration.
            foreach (string markup in SourceFiles(source, "*.xaml"))
            {
                if (!NamesSyncfusion(File.ReadAllLines(markup)))
                {
                    continue;
                }

                string codeBehind = markup + ".cs";

                if (!File.Exists(codeBehind) ||
                    !File.ReadAllLines(codeBehind).Any(l => RegisterCall.IsMatch(l)))
                {
                    offences.Add(Path.GetFileName(markup) + " (via " +
                                 Path.GetFileName(codeBehind) + ")");
                }
            }

            Assert.True(
                offences.Count == 0,
                "An unlicensed Syncfusion control raises a MODAL trial dialog as it is constructed, " +
                "which on a dispatcher thread stops that dispatcher pumping. Call " +
                "SyncfusionLicense.Register() before constructing one -- it is idempotent. " +
                "Missing in:" + Environment.NewLine + string.Join(Environment.NewLine, offences));
        }

        [Fact]
        public void TheLicenceIsRegisteredInExactlyOnePlace()
        {
            var callSites = new List<string>();

            foreach (string file in SourceFiles(Path.Combine(RepositoryRoot(), "src"), "*.cs"))
            {
                string[] lines = File.ReadAllLines(file);

                for (int i = 0; i < lines.Length; i++)
                {
                    string trimmed = lines[i].TrimStart();

                    if (trimmed.StartsWith("//", StringComparison.Ordinal) ||
                        trimmed.StartsWith("///", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (Regex.IsMatch(lines[i], @"\bSyncfusionLicenseProvider\s*\.\s*RegisterLicense\s*\("))
                    {
                        callSites.Add(Path.GetFileName(file) + ":" + (i + 1));
                    }
                }
            }

            // One place, so "resolve, guard against a null key, swallow a malformed one, record
            // that it happened" is decided once. Two call sites is how one of them ends up
            // throwing on a bad key while the other does not.
            Assert.True(
                callSites.Count == 1 && callSites[0].StartsWith(PolicyFile, StringComparison.Ordinal),
                "SyncfusionLicenseProvider.RegisterLicense must be called only from " + PolicyFile +
                ". Found: " + string.Join(", ", callSites));
        }

        [Fact]
        public void TheSearchWouldCatchAnUnregisteredHost()
        {
            // A shape check passes by finding nothing, so show it finds something.
            Assert.True(NamesSyncfusion(new[] { "using Syncfusion.Windows.Tools.Controls;" }));
            Assert.True(NamesSyncfusion(new[] { "        <syncfusion:DockingManager x:Name=\"Docking\">" }));
            Assert.True(NamesSyncfusion(new[] { "    xmlns:syncfusion=\"http://schemas.syncfusion.com/wpf\"" }));

            // Prose about Syncfusion is not a use of it, or every doc comment explaining the rule
            // would itself break the rule.
            Assert.False(NamesSyncfusion(new[]
            {
                "        // Registers the Syncfusion licence; see SyncfusionLicense.",
                "        /// <summary>Whether Syncfusion. is licensed.</summary>",
            }));

            Assert.True(RegisterCall.IsMatch("            SyncfusionLicense.Register();"));
            Assert.False(RegisterCall.IsMatch("            SyncfusionLicense.ResolveKey();"));
        }

        /// <summary>Whether any non-comment line names a Syncfusion type or namespace.</summary>
        private static bool NamesSyncfusion(IReadOnlyList<string> lines)
        {
            foreach (string line in lines)
            {
                string trimmed = line.TrimStart();

                if (trimmed.StartsWith("//", StringComparison.Ordinal) ||
                    trimmed.StartsWith("*", StringComparison.Ordinal) ||
                    trimmed.StartsWith("/*", StringComparison.Ordinal) ||
                    trimmed.StartsWith("<!--", StringComparison.Ordinal))
                {
                    continue;
                }

                if (SyncfusionType.IsMatch(line))
                {
                    return true;
                }
            }

            return false;
        }

        private static IEnumerable<string> SourceFiles(string directory, string pattern) =>
            Directory.EnumerateFiles(directory, pattern, SearchOption.AllDirectories)
                .Where(f => !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar) &&
                            !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar));

        /// <summary>Walks up from the test assembly until the solution file is found.</summary>
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

            throw new InvalidOperationException(
                "Could not find the repository root above " + AppDomain.CurrentDomain.BaseDirectory + ".");
        }
    }
}
