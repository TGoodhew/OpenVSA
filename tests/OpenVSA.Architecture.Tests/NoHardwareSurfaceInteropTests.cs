using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace OpenVSA.Architecture.Tests
{
    /// <summary>
    /// <c>REQ-NFR-005</c>: the trace surface is the software rasteriser, and no <c>D3DImage</c>,
    /// <c>HwndHost</c> or D3D9Ex shared-surface path exists.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The requirement offered <c>D3DImage</c> + a shared-surface bridge as an alternative to the
    /// software rasteriser until it was amended on 2026-07-29. The alternative was **withdrawn**,
    /// not deprioritised, on two measurements: rasterising is 1.4 % of a 2²⁰-point frame and is
    /// invariant in point count, and <c>D3DImage</c> degrades to software under RDP and without
    /// WDDM — so a design resting on it has no path in the environments a bench instrument is
    /// actually operated from.
    /// </para>
    /// <para>
    /// <strong>Why this is a test and not a note in the specification.</strong> A withdrawn design
    /// is exactly the kind of thing that comes back: it is written down in the document's own
    /// history, it sounds faster, and nothing in the code says no. Reintroducing it should be a
    /// deliberate act that fails the build and gets discussed, not a drift that is discovered when
    /// somebody runs the product over Remote Desktop.
    /// </para>
    /// <para>
    /// Scoped to <c>src/</c> and not to the tests, so that a test may still *name* these types in
    /// order to assert their absence — including this one.
    /// </para>
    /// </remarks>
    public class NoHardwareSurfaceInteropTests
    {
        /// <summary>The interop surface the amended requirement forbids, and why each is named.</summary>
        /// <remarks>
        /// <c>D3DImage</c> and <c>HwndHost</c> are the two WPF hosting routes to a hardware
        /// surface; <c>IDirect3D</c>, <c>GetSharedHandle</c> and <c>CreateTexture</c> are the
        /// D3D9Ex shared-surface bridge the requirement spelled out, named separately because the
        /// bridge could be built behind a differently-named wrapper and the hosting types would
        /// then be the only trace of it.
        /// </remarks>
        private static readonly string[] Forbidden =
        {
            "D3DImage",
            "HwndHost",
            "IDirect3DSurface9",
            "IDirect3DDevice9Ex",
            "IDXGIResource",
            "GetSharedHandle",
        };

        [Fact]
        public void TheShellHostsNoHardwareSurface()
        {
            var offences = new List<string>();

            foreach (string file in SourceFiles(Path.Combine(RepositoryRoot(), "src")))
            {
                foreach (string offence in Occurrences(File.ReadAllLines(file)))
                {
                    offences.Add(Path.GetFileName(file) + " — " + offence);
                }
            }

            Assert.True(
                offences.Count == 0,
                "REQ-NFR-005 (amended 2026-07-29) violation: the D3DImage / D3D9Ex shared-surface " +
                "path was withdrawn, because it degrades to software under RDP and without WDDM " +
                "and would buy back 1.4 % of a frame. Rendering goes through the WriteableBitmap " +
                "software rasteriser. Found:" + Environment.NewLine +
                string.Join(Environment.NewLine, offences));
        }

        [Fact]
        public void TheSearchWouldCatchAHardwareSurfaceIfOneAppeared()
        {
            // A shape check passes by finding nothing, so it has to be shown to find something.
            Assert.Equal(
                new[] { "1: private D3DImage _surface;" },
                Occurrences(new[] { "        private D3DImage _surface;" }).ToArray());

            Assert.Single(Occurrences(new[] { "            var host = new HwndHost();" }));
            Assert.Single(Occurrences(new[] { "            resource.GetSharedHandle(out handle);" }));

            // Prose about the withdrawal is not a reintroduction of it. Without this the amended
            // requirement could not be explained in a comment beside the code that keeps it, which
            // is where the explanation is worth the most.
            Assert.Empty(Occurrences(new[]
            {
                "        // REQ-NFR-005: no D3DImage path -- withdrawn on measurement.",
                "        /// <summary>Not a HwndHost; see the amended requirement.</summary>",
            }));

            // And the ordinary rasteriser is not an offence.
            Assert.Empty(Occurrences(new[] { "            _bitmap.WritePixels(rect, buffer, stride, 0);" }));
        }

        /// <summary>The lines of a file that reach for a hardware surface.</summary>
        /// <remarks>
        /// Comment lines are skipped rather than matched, for the reason the second test states:
        /// the rule has to be explicable beside the code that keeps it. Same convention as the
        /// theming architecture tests.
        /// </remarks>
        private static IEnumerable<string> Occurrences(IReadOnlyList<string> lines)
        {
            for (int i = 0; i < lines.Count; i++)
            {
                string line = lines[i];
                string trimmed = line.TrimStart();

                if (trimmed.StartsWith("//", StringComparison.Ordinal) ||
                    trimmed.StartsWith("*", StringComparison.Ordinal) ||
                    trimmed.StartsWith("/*", StringComparison.Ordinal))
                {
                    continue;
                }

                foreach (string name in Forbidden)
                {
                    if (Regex.IsMatch(line, @"\b" + Regex.Escape(name) + @"\b"))
                    {
                        yield return (i + 1) + ": " + line.Trim();
                        break;
                    }
                }
            }
        }

        private static IEnumerable<string> SourceFiles(string directory) =>
            Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)
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
