using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace OpenVSA.Ui.Dialogs
{
    /// <summary>
    /// How a settings dialog lays its pages out (<c>REQ-UI-071</c>, Default Mode).
    /// </summary>
    /// <remarks>
    /// The requirement's four, and no fifth. Every mode renders the same page set — the criterion
    /// is that "every control reachable in one is reachable in the others" — so this is a choice
    /// about presentation and never about what the dialog contains.
    /// </remarks>
    public enum DialogMode
    {
        /// <summary>A tab control with its strip along the top.</summary>
        TabsOnTop = 0,

        /// <summary>A tab control with its strip down the left-hand side.</summary>
        TabsOnLeft,

        /// <summary>A stack of expanders, one per page, scrolling vertically.</summary>
        ExpandersVertical,

        /// <summary>A row of expanders, one per page, scrolling horizontally.</summary>
        ExpandersHorizontal,
    }

    /// <summary>
    /// The names <c>REQ-UI-071</c> gives the four modes, and the parsing back.
    /// </summary>
    /// <remarks>
    /// The requirement's own spelling — <c>Tabs on top</c> — because a user reading their
    /// preferences file should find the words the dialog offered them, exactly as the spectrogram
    /// map keeps <c>Color Normal</c>.
    /// </remarks>
    public static class DialogModes
    {
        private static readonly ReadOnlyCollection<DialogMode> AllModes =
            new ReadOnlyCollection<DialogMode>(new List<DialogMode>
            {
                DialogMode.TabsOnTop,
                DialogMode.TabsOnLeft,
                DialogMode.ExpandersVertical,
                DialogMode.ExpandersHorizontal,
            });

        /// <summary>Every mode, in the order the requirement tabulates them.</summary>
        public static IReadOnlyList<DialogMode> All => AllModes;

        /// <summary>The mode a dialog opens in when nothing says otherwise.</summary>
        /// <remarks>
        /// Tabs on top: it is the arrangement every one of these dialogs has had since the
        /// reference product, and the one a user who has never opened the options will recognise.
        /// </remarks>
        public const DialogMode Default = DialogMode.TabsOnTop;

        /// <summary>The mode's name, as <c>REQ-UI-071</c> writes it.</summary>
        /// <param name="mode">The mode.</param>
        /// <exception cref="ArgumentOutOfRangeException">The mode is not one of the four.</exception>
        public static string NameOf(DialogMode mode)
        {
            switch (mode)
            {
                case DialogMode.TabsOnTop: return "Tabs on top";
                case DialogMode.TabsOnLeft: return "Tabs on left";
                case DialogMode.ExpandersVertical: return "Expanders vertical";
                case DialogMode.ExpandersHorizontal: return "Expanders horizontal";
            }

            throw new ArgumentOutOfRangeException(
                nameof(mode), mode, "There are four dialog modes and this is not one of them.");
        }

        /// <summary>
        /// Reads a mode back from its name.
        /// </summary>
        /// <param name="name">The name, as <see cref="NameOf"/> writes it.</param>
        /// <param name="mode">The mode, or <see cref="Default"/> if the name is not one.</param>
        /// <returns>Whether the name was understood.</returns>
        /// <remarks>
        /// Case-insensitive and tolerant of surrounding space, because this parses a file a user
        /// may have edited. An unrecognised name is reported rather than thrown on: a preferences
        /// file naming a mode this build has never heard of should cost the user their tab
        /// placement, not their whole preferences file.
        /// </remarks>
        public static bool TryParseName(string name, out DialogMode mode)
        {
            mode = Default;

            if (string.IsNullOrEmpty(name))
            {
                return false;
            }

            string trimmed = name.Trim();

            foreach (DialogMode candidate in AllModes)
            {
                if (string.Equals(NameOf(candidate), trimmed, StringComparison.OrdinalIgnoreCase))
                {
                    mode = candidate;
                    return true;
                }
            }

            return false;
        }

        /// <summary>Whether a mode renders its pages as tabs rather than as expanders.</summary>
        /// <param name="mode">The mode.</param>
        public static bool IsTabbed(DialogMode mode) =>
            mode == DialogMode.TabsOnTop || mode == DialogMode.TabsOnLeft;
    }
}
