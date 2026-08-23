using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using OpenVSA.Demod.Chain;
using OpenVSA.Demod.Help;
using Xunit;
using Xunit.Abstractions;

namespace OpenVSA.Demod.Tests
{
    /// <summary>
    /// <c>REQ-DEM-001</c>: "The same order appears in the user help, and a test compares the
    /// documented sequence against the declaration so the two cannot drift."
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two documents are held to the declaration here, not one. The user help is what the
    /// requirement asks for. The requirements document itself is the other, and it is checked
    /// because it is the thing the declaration was written from: if someone edits the specification
    /// to change the chain, the build should fail until the software follows, rather than the two
    /// disagreeing quietly with the specification looking authoritative.
    /// </para>
    /// <para>
    /// <strong>Both comparisons are against <see cref="ProcessingOrder.Render"/>, not against a
    /// third copy of the list.</strong> A test that spelled the fourteen steps out again would be
    /// the second declaration this requirement exists to prevent — it would pass while the code and
    /// both documents drifted together away from it, and fail when all three were corrected.
    /// </para>
    /// </remarks>
    public class ProcessingOrderDocumentationTests
    {
        private readonly ITestOutputHelper _output;

        public ProcessingOrderDocumentationTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void TheHelpTopicShipsWithTheAssembly()
        {
            string topic = HelpTopics.Read(HelpTopics.ProcessingOrder);

            Assert.False(string.IsNullOrEmpty(topic));
            Assert.Contains("demodulat", topic, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void TheHelpTopicCarriesTheDeclaredOrder()
        {
            IReadOnlyList<string> documented =
                Chain(HelpTopics.Read(HelpTopics.ProcessingOrder));

            Assert.Equal(ProcessingOrder.Render(), documented);
        }

        [Fact]
        public void TheRequirementsDocumentCarriesTheDeclaredOrder()
        {
            IReadOnlyList<string> documented = Chain(Specification());

            foreach (string line in documented)
            {
                _output.WriteLine(line);
            }

            Assert.Equal(ProcessingOrder.Render(), documented);
        }

        [Fact]
        public void TheHelpTopicSaysWhichStepsAreOptionalAndWhereTheLoopGoes()
        {
            string topic = HelpTopics.Read(HelpTopics.ProcessingOrder);

            // Not a spell-check of the prose: these are the three facts about the chain that a
            // reader needs and that the numbered list alone does not give them.
            Assert.Contains("optional", topic, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("re-enters at 8", topic, StringComparison.Ordinal);
            Assert.Contains("bound", topic, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// The numbered chain out of a document, in the form <see cref="ProcessingOrder.Render"/>
        /// writes it.
        /// </summary>
        /// <param name="text">The document.</param>
        /// <returns>One line per step.</returns>
        /// <remarks>
        /// <para>
        /// The two documents write the chain differently, and both are read by this one method
        /// rather than by two that could disagree. The requirements document draws a brace down the
        /// right-hand side to group steps 2 to 6 and breaks step 8 across two lines; the help topic
        /// does neither. So: everything from the first box-drawing character is dropped, a line that
        /// does not start with a number is joined to the one before it, and runs of spaces become
        /// one.
        /// </para>
        /// <para>
        /// Reading the chain out of the first fenced block, rather than out of the whole document,
        /// keeps the prose free to mention a step number without the test trying to parse the
        /// sentence it appears in.
        /// </para>
        /// </remarks>
        private static IReadOnlyList<string> Chain(string text)
        {
            var lines = new List<string>();
            var current = new StringBuilder();
            bool inside = false;

            foreach (string raw in text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
            {
                if (raw.TrimStart().StartsWith("```", StringComparison.Ordinal))
                {
                    if (!inside)
                    {
                        inside = true;

                        continue;
                    }

                    break;
                }

                if (!inside)
                {
                    continue;
                }

                string line = Strip(raw);

                if (line.Length == 0)
                {
                    continue;
                }

                if (Regex.IsMatch(line, @"^\d+\.\s"))
                {
                    Flush(current, lines);
                }

                if (current.Length > 0)
                {
                    current.Append(' ');
                }

                current.Append(line);
            }

            Flush(current, lines);

            return lines;
        }

        private static void Flush(StringBuilder current, List<string> lines)
        {
            if (current.Length > 0)
            {
                lines.Add(current.ToString());
                current.Clear();
            }
        }

        private static string Strip(string line)
        {
            int drawing = line.IndexOfAny(new[] { '─', '│', '┐', '┘', '┌', '└' });

            string stripped = drawing < 0 ? line : line.Substring(0, drawing);

            return Regex.Replace(stripped, @"\s+", " ").Trim();
        }

        /// <summary>Finds the requirements document by walking up from the test assembly.</summary>
        private static string Specification()
        {
            var directory = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);

            while (directory != null)
            {
                string candidate = Path.Combine(
                    directory.FullName, "requirements", "OpenVSA-Requirements.md");

                if (File.Exists(candidate))
                {
                    return Section(File.ReadAllText(candidate));
                }

                directory = directory.Parent;
            }

            throw new FileNotFoundException(
                "Could not find the requirements document above " +
                AppDomain.CurrentDomain.BaseDirectory + ".");
        }

        /// <summary>The part of the specification that states <c>REQ-DEM-001</c>.</summary>
        private static string Section(string specification)
        {
            int start = specification.IndexOf(
                "`REQ-DEM-001` (P0) — Documented processing order", StringComparison.Ordinal);

            Assert.True(start >= 0, "REQ-DEM-001 is not in the requirements document.");

            return specification.Substring(start);
        }
    }
}
