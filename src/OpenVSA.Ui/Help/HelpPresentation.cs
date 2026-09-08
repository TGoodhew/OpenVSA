using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using OpenVSA.Demod.Help;

namespace OpenVSA.Ui.Help
{
    /// <summary>
    /// Turns a shipped help topic into lines the shell can show.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The Output window, not a new one.</strong> Help &gt; Privacy and Help &gt; About
    /// already answer by writing into the Output window and putting a summary on the status bar,
    /// and this follows them. A window of its own would need its own chrome, its own theming and
    /// its own place in the layout; a tool window of its own would be worse, because
    /// <c>REQ-UI-061</c> fixes the Window menu's contents exactly and a test fails the build if
    /// anything is added to it.
    /// </para>
    /// <para>
    /// <strong>Markdown, flattened rather than rendered.</strong> The topics are written in
    /// Markdown because that is what they are read as in the repository. A log pane shows text, so
    /// the markers that carry no meaning once the text is in a pane — heading hashes, emphasis
    /// asterisks, code fences and backticks — are taken off, and nothing is re-flowed. A renderer
    /// would be a better answer and is a help system's job, not this one's.
    /// </para>
    /// </remarks>
    internal static class HelpPresentation
    {
        /// <summary>The topic the help keys show.</summary>
        /// <remarks>
        /// <para>
        /// <strong>🔴 The decision this constant existed to announce is now due.</strong> Its
        /// remark used to read "one topic ships, so there is nothing to choose between", and that
        /// was already untrue at four topics. <see cref="HelpTopics.Names"/> now lists SIX —
        /// <c>REQ-DEM-010a</c>'s analog page and <c>REQ-DEM-032</c>'s result-window page joined the
        /// four — and F1 shows this one. <strong>Five shipped topics are unreachable from the
        /// shell.</strong>
        /// </para>
        /// <para>
        /// Not fixed here, and the reason is a boundary rather than an oversight:
        /// <c>REQ-UI-061</c> fixes the menu bar's contents as an exact list with a test that fails
        /// the build if anything is added, so a Help submenu listing the topics is that
        /// requirement's business and not this file's. What choosing between topics really needs is
        /// an index and a context — a help system — which is <c>REQ-UI</c> work nobody has specified
        /// yet.
        /// </para>
        /// <para>
        /// Until then the pages are readable in <c>docs/help/</c> and embedded in the assembly, so
        /// nothing is lost; it is the shell's route to them that is missing. Recorded in #441.
        /// </para>
        /// </remarks>
        internal const string DefaultTopic = HelpTopics.ProcessingOrder;

        /// <summary>A topic, as lines to write into a pane.</summary>
        /// <param name="name">The topic's name.</param>
        /// <returns>The lines, with the Markdown markers taken off.</returns>
        /// <exception cref="ArgumentException">There is no such topic.</exception>
        internal static IReadOnlyList<string> Lines(string name)
        {
            var lines = new List<string>();
            bool blank = false;

            foreach (string raw in HelpTopics.Read(name).Split(
                new[] { "\r\n", "\n" }, StringSplitOptions.None))
            {
                string line = Flatten(raw);

                if (line.Length == 0)
                {
                    // Runs of blank lines collapse to one. Markdown uses them as paragraph
                    // separators and a pane does not need two of them to see a gap.
                    if (lines.Count > 0)
                    {
                        blank = true;
                    }

                    continue;
                }

                if (blank)
                {
                    lines.Add(string.Empty);
                    blank = false;
                }

                lines.Add(line);
            }

            return new ReadOnlyCollection<string>(lines);
        }

        /// <summary>The one-line summary the status bar shows.</summary>
        /// <param name="name">The topic's name.</param>
        /// <returns>The topic's first heading, or its name if it has none.</returns>
        internal static string Title(string name)
        {
            foreach (string raw in HelpTopics.Read(name).Split(
                new[] { "\r\n", "\n" }, StringSplitOptions.None))
            {
                string line = raw.Trim();

                if (line.StartsWith("#", StringComparison.Ordinal))
                {
                    return Flatten(line);
                }
            }

            return name;
        }

        private static string Flatten(string raw)
        {
            string line = raw.TrimEnd();

            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                return string.Empty;
            }

            string trimmed = line.TrimStart();

            if (trimmed.StartsWith("#", StringComparison.Ordinal))
            {
                line = trimmed.TrimStart('#').TrimStart();
            }

            return line.Replace("**", string.Empty).Replace("`", string.Empty).TrimEnd();
        }
    }
}
