using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace OpenVSA.Architecture.Tests
{
    /// <summary>
    /// <c>REQ-UI-070</c>: no setting dialog is modal.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The requirement's first acceptance criterion, checked in the only form that can express it.
    /// "No setting dialog is modal" is a statement about every dialog in the shell, and no signature
    /// can carry it: <c>SettingsDialog.ShowDialog</c> throws, but that only catches the mistake made
    /// through the concrete type. A source search catches the one made anywhere.
    /// </para>
    /// <para>
    /// <strong>A setting dialog is not the same thing as a question with an answer.</strong> What
    /// the requirement forbids is a dialog that edits how the measurement is made and blocks the
    /// measurement while it does — the display preferences, the analysis setup, a hot spot's value.
    /// A print dialog, a file picker and the save-state prompt are none of those: each asks one
    /// question, gets one answer and is finished, and carrying on measuring behind them would leave
    /// the user answering a question about a state that had already moved. They are named in
    /// <see cref="ModalIsCorrect"/> rather than exempted by accident.
    /// </para>
    /// </remarks>
    public class NoModalSettingDialogsTests
    {
        /// <summary>Dialog types for which modal is the right answer, not a violation.</summary>
        private static readonly string[] ModalIsCorrect =
        {
            "PrintDialog", "OpenFileDialog", "SaveFileDialog", "StateSaveDialog",
        };

        private static readonly Regex Call =
            new Regex(@"(?:(?<receiver>[A-Za-z_]\w*)\s*\.\s*)?\bShowDialog\s*\(", RegexOptions.Compiled);

        /// <summary>The refusal's own declaration and the doc comments that name it.</summary>
        private static readonly Regex Declaration =
            new Regex(@"(public new bool\? ShowDialog)|(<see cref=""ShowDialog"")", RegexOptions.Compiled);

        [Fact]
        public void TheShellShowsNoSettingDialogModally()
        {
            var offences = new List<string>();

            foreach (string file in SourceFiles(Path.Combine(RepositoryRoot(), "src", "OpenVSA.Ui")))
            {
                string[] lines = File.ReadAllLines(file);

                foreach (string offence in ModalCalls(lines))
                {
                    offences.Add(Path.GetFileName(file) + " — " + offence);
                }
            }

            Assert.True(
                offences.Count == 0,
                "REQ-UI-070 violation: a setting dialog shown modally stops the measurement " +
                "updating and puts the hot spots out of reach. Found:" + Environment.NewLine +
                string.Join(Environment.NewLine, offences));
        }

        [Fact]
        public void TheSearchWouldCatchAModalDialogIfOneAppeared()
        {
            // A test that can only pass is not a test.
            Assert.Equal(
                new[] { "2: dialog.ShowDialog();" },
                ModalCalls(new[]
                {
                    "            var dialog = new DisplayPreferencesDialog(options);",
                    "            dialog.ShowDialog();",
                }).ToArray());

            // A dialog whose type cannot be found at all is reported rather than assumed innocent.
            Assert.Single(ModalCalls(new[] { "            somethingUnknown.ShowDialog();" }));

            // And the ones for which modal is correct are not reported.
            Assert.Empty(ModalCalls(new[]
            {
                "            var printing = new PrintDialog();",
                "            if (printing.ShowDialog() != true)",
            }));

            Assert.Empty(ModalCalls(new[]
            {
                "            var picker = new Microsoft.Win32.OpenFileDialog();",
                "            if (picker.ShowDialog(this) != true)",
            }));
        }

        /// <summary>
        /// The lines of a file that show a dialog modally, other than the permitted ones.
        /// </summary>
        /// <remarks>
        /// The receiver's type is looked up rather than matched on the same line, because the
        /// construction and the call are almost never on one line — <c>var picker = new
        /// SaveFileDialog { … };</c> then <c>picker.ShowDialog(this)</c> four lines later. Matching
        /// the line alone would report every permitted dialog in the shell and the check would be
        /// turned off within a week.
        /// </remarks>
        private static IEnumerable<string> ModalCalls(IReadOnlyList<string> lines)
        {
            for (int i = 0; i < lines.Count; i++)
            {
                string line = lines[i];

                if (Declaration.IsMatch(line))
                {
                    continue;
                }

                Match call = Call.Match(line);

                if (!call.Success)
                {
                    continue;
                }

                string receiver = call.Groups["receiver"].Value;

                if (receiver.Length > 0 && ModalIsCorrect.Contains(TypeOf(receiver, lines, i)))
                {
                    continue;
                }

                yield return (i + 1) + ": " + line.Trim();
            }
        }

        /// <summary>The type a local was constructed as, or empty if it cannot be found above.</summary>
        private static string TypeOf(string receiver, IReadOnlyList<string> lines, int before)
        {
            var construction = new Regex(
                @"\b" + Regex.Escape(receiver) + @"\s*=\s*new\s+(?<type>[\w\.]+)");

            for (int i = before; i >= 0; i--)
            {
                Match match = construction.Match(lines[i]);

                if (match.Success)
                {
                    string type = match.Groups["type"].Value;
                    int dot = type.LastIndexOf('.');

                    return dot < 0 ? type : type.Substring(dot + 1);
                }
            }

            return string.Empty;
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
