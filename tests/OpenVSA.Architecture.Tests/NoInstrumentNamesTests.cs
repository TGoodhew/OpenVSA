using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace OpenVSA.Architecture.Tests
{
    /// <summary>
    /// <c>REQ-HAL-002</c>: a code search for instrument model names in the UI returns no matches.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The requirement's own acceptance criterion, run rather than performed by hand. It is the
    /// half of capability-driven UI that can be checked mechanically: the other half — that
    /// switching front ends visibly re-ranges the controls — needs two front ends to switch
    /// between, and only the simulator exists so far.
    /// </para>
    /// <para>
    /// Source rather than compiled metadata, deliberately. A model name in a comment is the
    /// beginning of a special case, and the compiled assembly would not show it.
    /// </para>
    /// </remarks>
    public class NoInstrumentNamesTests
    {
        /// <summary>
        /// Patterns that match the model-number shapes of the instruments this product supports.
        /// </summary>
        /// <remarks>
        /// Shapes, not a list of models. A list would have to be maintained in step with every
        /// front end anyone ever writes, and the first model it did not name would pass.
        /// </remarks>
        private static readonly Regex[] ModelNumberShapes =
        {
            // Keysight/Agilent instrument numbering: E4406A, N9020A, E4438C.
            new Regex(@"\b[A-Z]\d{4}[A-Z]\b", RegexOptions.Compiled),

            // The 89600 family itself, and the 89400 series before it.
            new Regex(@"\b89\d{3}[A-Z]?\b", RegexOptions.Compiled),

            // Bare four-digit series numbers that name an instrument rather than a quantity.
            new Regex(@"\b(?:8560|8563|3325|4155)[A-Z]?\b", RegexOptions.Compiled),
        };

        [Fact]
        public void TheUiNamesNoInstrument()
        {
            string ui = Path.Combine(RepositoryRoot(), "src", "OpenVSA.Ui");
            var offences = new List<string>();

            foreach (string file in SourceFiles(ui))
            {
                string[] lines = File.ReadAllLines(file);

                for (int i = 0; i < lines.Length; i++)
                {
                    foreach (Regex shape in ModelNumberShapes)
                    {
                        Match match = shape.Match(lines[i]);

                        if (match.Success)
                        {
                            offences.Add(
                                Path.GetFileName(file) + ":" + (i + 1) + " — '" + match.Value +
                                "' in: " + lines[i].Trim());
                        }
                    }
                }
            }

            Assert.True(
                offences.Count == 0,
                "REQ-HAL-002 violation: the UI must range itself from IFrontEndCapabilities and " +
                "never from a model. Found:" + Environment.NewLine +
                string.Join(Environment.NewLine, offences));
        }

        [Fact]
        public void ThePatternsWouldCatchAModelNameIfOneAppeared()
        {
            // A test that can only pass is not a test. These are the strings the search is for, and
            // they must match, or the assertion above is vacuous.
            foreach (string model in new[] { "E4406A", "N9020A", "89600", "89601B", "8563E" })
            {
                Assert.True(
                    ModelNumberShapes.Any(shape => shape.IsMatch("using the " + model + " front end")),
                    model + " would not have been caught.");
            }

            // And it must not fire on the things the UI legitimately says.
            foreach (string innocent in new[]
            {
                "REQ-HAL-002", "REQ-NFR-020", "1.28 x Span", "409601 points", "1024 samples",
                "-100 dBm", "Flat Top", "OpenVSA.Hal.Sim",
            })
            {
                Assert.False(
                    ModelNumberShapes.Any(shape => shape.IsMatch(innocent)),
                    "'" + innocent + "' is not a model name but was matched as one.");
            }
        }

        private static IEnumerable<string> SourceFiles(string directory) =>
            Directory.EnumerateFiles(directory, "*.*", SearchOption.AllDirectories)
                .Where(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ||
                            f.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
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
