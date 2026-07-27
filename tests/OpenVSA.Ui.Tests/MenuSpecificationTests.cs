using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using OpenVSA.Ui.Menus;
using Xunit;

namespace OpenVSA.Ui.Tests
{
    /// <summary>
    /// <c>REQ-UI-061</c>'s list, read out of the specification and compared with the table.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The other half of the exactness.</strong> <see cref="ShellMenuContentsTests"/> holds
    /// the menu bar against <see cref="ShellMenuTable"/>; this holds the table against the document
    /// the table was transcribed from. Without it, a mistake made while transcribing a hundred item
    /// names would be enforced rather than caught — the shell would agree with the table perfectly,
    /// and both would be wrong.
    /// </para>
    /// <para>
    /// <strong>Only the levels the specification enumerates.</strong> It lists the top level of all
    /// ten menus and the children of Recall and Preset. What is under Save, Format, Control or Limit
    /// Tests is OpenVSA's own nesting, and comparing it against a document that does not mention it
    /// would be comparing against nothing.
    /// </para>
    /// </remarks>
    public class MenuSpecificationTests
    {
        [Fact]
        public void EveryMenuHasExactlyTheItemsTheSpecificationLists()
        {
            IReadOnlyList<SpecifiedMenu> specified = Parse();

            Assert.Equal(10, specified.Count);

            var differences = new List<string>();

            foreach (SpecifiedMenu menu in specified)
            {
                ShellMenu table = ShellMenuTable.For(menu.Name);

                List<string> listed = menu.Items.Select(i => i.Name).ToList();
                List<string> built = table.Items
                    .Where(e => e.Kind != ShellMenuEntryKind.Separator)
                    .Select(Spelling)
                    .ToList();

                if (!listed.SequenceEqual(built, StringComparer.Ordinal))
                {
                    differences.Add(
                        menu.Name + Environment.NewLine +
                        "  specification: " + string.Join(", ", listed) + Environment.NewLine +
                        "  table:         " + string.Join(", ", built));
                }
            }

            Assert.True(
                differences.Count == 0,
                "REQ-UI-061's list and ShellMenuTable disagree:" + Environment.NewLine +
                string.Join(Environment.NewLine, differences));
        }

        [Fact]
        public void TheSubmenusTheSpecificationEnumeratesMatchToo()
        {
            // Recall and Preset are the two whose contents the requirement writes out. Preset's
            // nine are the ones the "Preset never changes the hardware setup" criterion is about,
            // so getting one of their names wrong would misname a test as well as an item.
            var differences = new List<string>();

            foreach (SpecifiedMenu menu in Parse())
            {
                foreach (SpecifiedItem item in menu.Items)
                {
                    if (item.Children.Count == 0)
                    {
                        continue;
                    }

                    ShellMenuEntry entry = ShellMenuTable.At(menu.Name + " > " + item.Name);

                    Assert.True(
                        entry != null,
                        menu.Name + " > " + item.Name + " is in the specification and not in the table.");

                    List<string> built = entry.Children
                        .Where(e => e.Kind != ShellMenuEntryKind.Separator)
                        .Select(Spelling)
                        .ToList();

                    if (!item.Children.SequenceEqual(built, StringComparer.Ordinal))
                    {
                        differences.Add(
                            menu.Name + " > " + item.Name + Environment.NewLine +
                            "  specification: " + string.Join(", ", item.Children) +
                            Environment.NewLine +
                            "  table:         " + string.Join(", ", built));
                    }
                }
            }

            Assert.True(
                differences.Count == 0,
                "REQ-UI-061's submenus and ShellMenuTable disagree:" + Environment.NewLine +
                string.Join(Environment.NewLine, differences));
        }

        [Fact]
        public void TheParserWouldNoticeIfTheListChanged()
        {
            // A test that can only pass is not a test. These are the shapes the parser has to get
            // right, and each one is a place a naive split would go wrong: a quotation with commas
            // in it, a slash-separated child list, an item whose own name contains a slash, and a
            // parenthesis that is a keyboard gesture rather than a list of children.
            IReadOnlyList<SpecifiedMenu> menus = Parse();

            SpecifiedItem copy = Item(menus, "Edit", "Copy");
            Assert.Empty(copy.Children);

            SpecifiedItem recall = Item(menus, "File", "Recall");
            Assert.Equal(
                new[] { "Setup", "Recording", "Trace", "Layout", "Demo" }, recall.Children.ToArray());

            Assert.NotNull(Item(menus, "Acquisition", "Recording/Playback…"));
            Assert.NotNull(Item(menus, "Trace", "Spectrogram / Colour Map"));

            SpecifiedItem help = Item(menus, "Help", "Help (F1)");
            Assert.Empty(help.Children);

            // And the embedded toolbars are entries in their own right, not items with names.
            Assert.Equal(
                1, menus.First(m => m.Name == "Trace").Items.Count(i => i.IsEmbeddedToolbar));

            Assert.Equal(
                1, menus.First(m => m.Name == "Marker").Items.Count(i => i.IsEmbeddedToolbar));
        }

        // ---- Reading the specification ----------------------------------------------------------

        /// <summary>How the table spells an entry, for comparison with the specification's list.</summary>
        private static string Spelling(ShellMenuEntry entry) =>
            entry.Kind == ShellMenuEntryKind.EmbeddedToolbar ? SpecifiedItem.ToolbarMarker : entry.Spec;

        private static SpecifiedItem Item(
            IReadOnlyList<SpecifiedMenu> menus, string menu, string name)
        {
            SpecifiedItem found = menus
                .First(m => string.Equals(m.Name, menu, StringComparison.Ordinal))
                .Items
                .FirstOrDefault(i => string.Equals(i.Name, name, StringComparison.Ordinal));

            Assert.True(found != null, menu + " > " + name + " was not parsed out of the list.");

            return found;
        }

        /// <summary>
        /// Reads <c>REQ-UI-061</c>'s ten bullets out of the requirements document.
        /// </summary>
        private static IReadOnlyList<SpecifiedMenu> Parse()
        {
            string[] lines = File.ReadAllLines(SpecificationPath());

            int start = Array.FindIndex(
                lines, l => l.StartsWith("**`REQ-UI-061`", StringComparison.Ordinal));

            Assert.True(start >= 0, "REQ-UI-061 is not in the requirements document.");

            var menus = new List<SpecifiedMenu>();
            var bullet = new StringBuilder();
            bool inNote = false;

            for (int index = start + 1; index < lines.Length; index++)
            {
                string line = lines[index];

                if (line.StartsWith("**AC:**", StringComparison.Ordinal))
                {
                    break;
                }

                string trimmed = line.Trim();

                if (line.StartsWith("- **", StringComparison.Ordinal))
                {
                    Flush(bullet, menus);
                    bullet.Append(trimmed);
                    inNote = false;
                    continue;
                }

                if (trimmed.Length == 0)
                {
                    Flush(bullet, menus);
                    inNote = false;
                    continue;
                }

                // Several bullets carry a note as an indented italic paragraph of their own -
                // File's "Preset never changes the hardware setup" and the one about Licenses…
                // under Utilities, which runs to three lines. They are prose about the list rather
                // than part of it, and a note runs until the bullet or the blank line after it.
                if (trimmed.StartsWith("*Note:", StringComparison.Ordinal) ||
                    trimmed.StartsWith("*(The", StringComparison.Ordinal))
                {
                    inNote = true;
                }

                if (!inNote && bullet.Length > 0 &&
                    line.StartsWith("  ", StringComparison.Ordinal))
                {
                    bullet.Append(' ').Append(trimmed);
                }
            }

            Flush(bullet, menus);

            return menus;
        }

        private static void Flush(StringBuilder bullet, List<SpecifiedMenu> menus)
        {
            if (bullet.Length > 0)
            {
                menus.Add(SpecifiedMenu.Read(bullet.ToString()));
                bullet.Clear();
            }
        }

        /// <summary>
        /// Finds the requirements document by walking up from the test assembly.
        /// </summary>
        private static string SpecificationPath()
        {
            var directory = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);

            while (directory != null)
            {
                string candidate = Path.Combine(
                    directory.FullName, "requirements", "OpenVSA-Requirements.md");

                if (File.Exists(candidate))
                {
                    return candidate;
                }

                directory = directory.Parent;
            }

            throw new FileNotFoundException(
                "Could not find the requirements document above " +
                AppDomain.CurrentDomain.BaseDirectory + ".");
        }
    }

    /// <summary>One of <c>REQ-UI-061</c>'s ten menu bullets, as the document writes it.</summary>
    internal sealed class SpecifiedMenu
    {
        private SpecifiedMenu(string name, IReadOnlyList<SpecifiedItem> items)
        {
            Name = name;
            Items = items;
        }

        public string Name { get; }

        public IReadOnlyList<SpecifiedItem> Items { get; }

        /// <summary>
        /// Reads one bullet: <c>- **Trace** — Trace List, New Trace, …</c>.
        /// </summary>
        public static SpecifiedMenu Read(string bullet)
        {
            int open = bullet.IndexOf("**", StringComparison.Ordinal);
            int close = bullet.IndexOf("**", open + 2, StringComparison.Ordinal);

            string name = bullet.Substring(open + 2, close - open - 2);

            // Everything after the em dash. The Analysis bullet has an italic "(in order)" between
            // the name and the dash, so this cannot key on the name's end.
            int dash = bullet.IndexOf('—', close);
            string rest = bullet.Substring(dash + 1).Trim().TrimEnd('.');

            return new SpecifiedMenu(name, Split(rest).Select(SpecifiedItem.Read).ToList());
        }

        /// <summary>
        /// Splits a bullet's text into items on commas and semicolons at bracket depth zero.
        /// </summary>
        /// <remarks>
        /// Depth matters: the Preset submenu's nine children are a comma-separated list inside one
        /// item's brackets, and Edit's Copy carries a quotation with commas in it. Splitting on
        /// every comma would turn one menu of six items into a menu of nineteen.
        /// </remarks>
        private static IEnumerable<string> Split(string text)
        {
            var piece = new StringBuilder();
            int depth = 0;

            foreach (char letter in text)
            {
                if (letter == '(')
                {
                    depth++;
                }
                else if (letter == ')')
                {
                    depth--;
                }

                if (depth == 0 && (letter == ',' || letter == ';'))
                {
                    yield return piece.ToString();
                    piece.Clear();
                    continue;
                }

                piece.Append(letter);
            }

            if (piece.Length > 0)
            {
                yield return piece.ToString();
            }
        }
    }

    /// <summary>One item of one of <c>REQ-UI-061</c>'s menus.</summary>
    internal sealed class SpecifiedItem
    {
        /// <summary>How the table stands in for "(embedded trace toolbar)" and its sibling.</summary>
        public const string ToolbarMarker = "*embedded toolbar*";

        private SpecifiedItem(string name, IReadOnlyList<string> children, bool isToolbar)
        {
            Name = name;
            Children = children;
            IsEmbeddedToolbar = isToolbar;
        }

        public string Name { get; }

        public IReadOnlyList<string> Children { get; }

        public bool IsEmbeddedToolbar { get; }

        /// <summary>Reads one item, with its children if the document enumerates them.</summary>
        public static SpecifiedItem Read(string text)
        {
            string item = text.Trim();

            // "**Properties:**" and "**Calculation:**" are headings within the Trace menu's list,
            // and "then" introduces the group after them. Neither is an item.
            int heading = item.IndexOf(":**", StringComparison.Ordinal);

            if (item.StartsWith("**", StringComparison.Ordinal) && heading > 0)
            {
                item = item.Substring(heading + 3).Trim();
            }

            if (item.StartsWith("then ", StringComparison.Ordinal))
            {
                item = item.Substring(5).Trim();
            }

            if (item.StartsWith("*(", StringComparison.Ordinal))
            {
                return new SpecifiedItem(ToolbarMarker, new string[0], true);
            }

            var children = new List<string>();
            int open = item.IndexOf('(');

            if (open > 0 && item.EndsWith(")", StringComparison.Ordinal))
            {
                string inside = item.Substring(open + 1, item.Length - open - 2);

                // Children only when the brackets hold a list. Edit's Copy carries a quotation of
                // the requirement's own prose, and Help's "(F1)" is a keyboard gesture; neither is
                // a submenu, and treating either as one would invent items.
                if (inside.IndexOf('"') >= 0)
                {
                    // A gloss on what the item does, quoted from the reference product's own
                    // documentation. Not children, and not part of the name either.
                    item = item.Substring(0, open).Trim();
                }
                else if (inside.IndexOf('/') >= 0 || inside.IndexOf(',') >= 0)
                {
                    children.AddRange(
                        inside.Split('/', ',').Select(c => c.Trim()).Where(c => c.Length > 0));

                    item = item.Substring(0, open).Trim();
                }
            }

            // The submenu arrow is notation, not part of the name.
            item = item.Replace("▸", string.Empty).Trim();

            return new SpecifiedItem(item, children, false);
        }
    }
}
