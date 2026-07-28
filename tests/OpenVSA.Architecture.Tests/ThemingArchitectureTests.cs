using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace OpenVSA.Architecture.Tests
{
    /// <summary>
    /// <c>REQ-UI-083</c>'s architectural obligation, checked mechanically.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The requirement names the three ways two themes get shipped in a way that makes a third
    /// expensive — "hard-coded brushes, colours resolved through a <c>switch</c> on a two-valued
    /// enum, or a bool <c>IsDarkMode</c> threaded through view models" — and asks for a test on
    /// each. All three pass on the day they are written and start failing the moment someone takes
    /// the shortcut, which is the only time an architecture test is worth anything.
    /// </para>
    /// <para>
    /// Source rather than compiled metadata, for the reason <see cref="NoInstrumentNamesTests"/>
    /// gives: a literal colour in XAML never reaches IL as anything a reflection test could find,
    /// and it is exactly where one would be written.
    /// </para>
    /// </remarks>
    public class ThemingArchitectureTests
    {
        /// <summary>
        /// A colour written into the rendering path rather than resolved from a theme.
        /// </summary>
        /// <remarks>
        /// Hex literals in markup and named <c>Colors.</c>/<c>Brushes.</c> members in code. Both
        /// are how a brush gets hard-coded; neither survives the arrival of a third theme.
        /// </remarks>
        private static readonly Regex[] HardCodedColours =
        {
            // Background="#FF102030", Color="#CC1E1E24", Foreground="#B0B0B8".
            new Regex(@"(?:Background|Foreground|Color|BorderBrush|Fill|Stroke)\s*=\s*""#[0-9A-Fa-f]{3,8}""",
                RegexOptions.Compiled),

            // Background="Gray", Foreground="Black" — a named WPF colour is as hard-coded as a hex one.
            new Regex(@"(?:Background|Foreground|BorderBrush|Fill|Stroke)\s*=\s*""[A-Z][A-Za-z]{2,}""",
                RegexOptions.Compiled),

            // Brushes.Gray, Colors.Black in code behind the chrome.
            //
            // Transparent is excepted, and it is the only one: it is not a colour choice but the
            // absence of one. A hot spot sets a transparent background so that it hit-tests across
            // the gaps between its glyphs rather than only where ink is, and no theme has an
            // opinion about that.
            new Regex(
                @"\b(?:Brushes|Colors)\s*\.\s*(?!Transparent\b)[A-Z][A-Za-z]+",
                RegexOptions.Compiled),
        };

        /// <summary>
        /// Files whose colours are the product's data or its measurement rendering, not its chrome.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <strong>Two exemptions, and each is a place the requirement puts outside the rule.</strong>
        /// The theme dictionaries <em>are</em> the colours — a theme with no literal colour in it
        /// would be a theme that defined nothing. And <c>REQ-UI-081</c> separates the plot surface
        /// from the chrome: the graticule, traces and annotation are governed by
        /// <c>REQ-UI-022</c>'s colour settings, whose defaults are stated in
        /// <c>ColourPreferences</c>, <c>PlotPalette</c>, <c>TraceColours</c>,
        /// <c>SpectrogramColourMap</c> and <c>LimitColours</c> and are user-settable from the
        /// picker.
        /// </para>
        /// <para>
        /// <c>REQ-UI-022</c>'s own criterion — "a test fails on a literal colour in the
        /// plot-surface rendering path" — is about the <em>rendering</em> path, which is where a
        /// colour would be applied rather than where the default is declared. Widening the
        /// exemption beyond these files is how the check stops meaning anything.
        /// </para>
        /// </remarks>
        private static readonly string[] ColourBearingFiles =
        {
            // The themes themselves.
            "Themes" + "\\",

            // REQ-UI-022's defaults and the maps built from them.
            "ColourPreferences.cs",
            "PlotPalette.cs",
            "PlotColor.cs",
            "TraceColours.cs",
            "SpectrogramColourMap.cs",
            "LimitColours.cs",
            "TraceIndicator.cs",
        };

        [Fact]
        public void NoColourIsHardCodedIntoTheChromeOrThePlotSurface()
        {
            var offences = new List<string>();

            foreach (string file in SourceFiles(Path.Combine(RepositoryRoot(), "src", "OpenVSA.Ui")))
            {
                if (ColourBearingFiles.Any(
                    exempt => file.IndexOf(exempt, StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    continue;
                }

                string[] lines = File.ReadAllLines(file);

                for (int i = 0; i < lines.Length; i++)
                {
                    // A DynamicResource lookup is the correct form and mentions no colour of its
                    // own, so it is not matched; a comment explaining one is not a brush.
                    string line = lines[i];

                    if (IsComment(line))
                    {
                        continue;
                    }

                    foreach (Regex shape in HardCodedColours)
                    {
                        Match match = shape.Match(line);

                        if (match.Success)
                        {
                            offences.Add(
                                Relative(file) + ":" + (i + 1) + "  " + match.Value.Trim());
                        }
                    }
                }
            }

            Assert.True(
                offences.Count == 0,
                "REQ-UI-083: every themed value resolves through a resource dictionary keyed by " +
                "name. These name a colour instead:" + Environment.NewLine +
                string.Join(Environment.NewLine, offences));
        }

        [Fact]
        public void ThemeIdentityIsNotAClosedTwoValuedType()
        {
            // "Theme identity is not a closed two-valued type. No enum Theme { Light, Dark }
            // switched over to pick values."
            var offences = new List<string>();

            // A word boundary after Theme, so this catches `enum Theme` and `enum ChromeTheme` and
            // leaves `enum ThemeScope` alone — that one is REQ-UI-022's Global/PerTrace distinction
            // and says nothing about which theme is in force.
            var themeEnum = new Regex(@"\benum\s+\w*Theme\b", RegexOptions.Compiled);

            foreach (string file in SourceFiles(Path.Combine(RepositoryRoot(), "src")))
            {
                foreach (string line in Code(file))
                {
                    if (themeEnum.IsMatch(line))
                    {
                        offences.Add(Relative(file) + "  " + line.Trim());
                    }
                }
            }

            Assert.True(
                offences.Count == 0,
                "REQ-UI-083: a theme is a name and a dictionary, not a value of an enumeration. " +
                "A switch over one is how a third theme becomes expensive." + Environment.NewLine +
                string.Join(Environment.NewLine, offences));
        }

        [Fact]
        public void NoBooleanDarkModeFlagExistsAnywhere()
        {
            // "and no boolean 'is dark' anywhere in the rendering or view-model layers."
            var offences = new List<string>();

            var darkFlag = new Regex(
                @"\bbool\s+_?[Ii]s(?:Dark|Light)(?:Mode|Theme)?\b|\bbool\s+_?[Dd]ark[Mm]ode\b",
                RegexOptions.Compiled);

            foreach (string file in SourceFiles(Path.Combine(RepositoryRoot(), "src")))
            {
                foreach (string line in Code(file))
                {
                    if (darkFlag.IsMatch(line))
                    {
                        offences.Add(Relative(file) + "  " + line.Trim());
                    }
                }
            }

            Assert.True(
                offences.Count == 0,
                "REQ-UI-083: no boolean dark-mode flag. It satisfies light and dark today and has " +
                "to be unpicked when a third theme arrives." + Environment.NewLine +
                string.Join(Environment.NewLine, offences));
        }

        [Fact]
        public void NoCodeBranchesOnAThemesName()
        {
            // The same shortcut in string form: comparing a theme's name to decide a value is a
            // switch over a two-valued type with the type left out.
            var offences = new List<string>();

            var branchOnName = new Regex(
                @"(?:==|!=|Equals\s*\()\s*""(?:Light|Dark)""|case\s+""(?:Light|Dark)""",
                RegexOptions.Compiled);

            foreach (string file in SourceFiles(Path.Combine(RepositoryRoot(), "src")))
            {
                string[] lines = File.ReadAllLines(file);

                for (int i = 0; i < lines.Length; i++)
                {
                    if (IsComment(lines[i]))
                    {
                        continue;
                    }

                    if (branchOnName.IsMatch(lines[i]))
                    {
                        offences.Add(Relative(file) + ":" + (i + 1) + "  " + lines[i].Trim());
                    }
                }
            }

            Assert.True(
                offences.Count == 0,
                "REQ-UI-083: a name finds a dictionary; nothing else is decided from it." +
                Environment.NewLine + string.Join(Environment.NewLine, offences));
        }

        [Fact]
        public void ThePatternsWouldCatchTheShortcutsIfTheyAppeared()
        {
            // Every one of these checks passes trivially if its pattern matches nothing, so each
            // pattern is shown to match the thing it is looking for.
            Assert.Contains(
                HardCodedColours,
                shape => shape.IsMatch("<Border Background=\"#CC1E1E24\" />"));

            Assert.Contains(
                HardCodedColours,
                shape => shape.IsMatch("<TextBlock Foreground=\"Gray\" />"));

            Assert.Contains(
                HardCodedColours,
                shape => shape.IsMatch("panel.Background = Brushes.Gray;"));

            Assert.Matches(new Regex(@"enum\s+\w*Theme\w*\b"), "public enum Theme { Light, Dark }");

            Assert.Matches(
                new Regex(@"\bbool\s+_?[Ii]s(?:Dark|Light)(?:Mode|Theme)?\b"),
                "private bool _isDarkMode;");

            Assert.Matches(
                new Regex(@"(?:==|!=|Equals\s*\()\s*""(?:Light|Dark)"""),
                "if (name == \"Dark\")");

            // And that a correct DynamicResource reference is not matched, or the rule would forbid
            // the very form it exists to require.
            Assert.DoesNotContain(
                HardCodedColours,
                shape => shape.IsMatch(
                    "<Border Background=\"{DynamicResource OpenVSA.Chrome.OverlayBackground}\" />"));
        }

        /// <summary>
        /// Whether a line is a comment rather than code.
        /// </summary>
        /// <remarks>
        /// These rules are about what the product does, and the clearest way to record why a
        /// shortcut is forbidden is to write the shortcut down in a doc comment beside the code
        /// that avoids it. A scanner that could not tell the two apart would forbid explaining
        /// itself, which is how a rule ends up undocumented.
        /// </remarks>
        private static bool IsComment(string line)
        {
            string trimmed = line.TrimStart();

            return trimmed.StartsWith("//", StringComparison.Ordinal) ||
                   trimmed.StartsWith("*", StringComparison.Ordinal) ||
                   trimmed.StartsWith("<!--", StringComparison.Ordinal);
        }

        /// <summary>A source file's lines with the comments left out.</summary>
        private static IEnumerable<string> Code(string file) =>
            File.ReadAllLines(file).Where(line => !IsComment(line));

        private static string Relative(string file) =>
            file.Substring(RepositoryRoot().Length).TrimStart(Path.DirectorySeparatorChar);

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
