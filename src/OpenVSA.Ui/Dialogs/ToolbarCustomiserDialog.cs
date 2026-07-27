using System;
using OpenVSA.Ui.Dialogs.Pages;
using OpenVSA.Ui.Toolbars;

namespace OpenVSA.Ui.Dialogs
{
    /// <summary>
    /// The toolbar customiser of <c>REQ-UI-064</c>, reached by Utilities ▸ Toolbars….
    /// </summary>
    /// <remarks>
    /// <para>
    /// One page, and that is the requirement rather than an unfinished dialog. <c>REQ-UI-064</c>
    /// names three things — a list of toolbars, a control picker and a contents editor — and they
    /// are three <em>controls</em>, not three tabs: moving a button from one toolbar to another
    /// means seeing all three at once. A tab strip between them would turn one task into three.
    /// </para>
    /// <para>
    /// It is a <see cref="SettingsDialog"/> so that it is modeless, live and free of an OK button
    /// like every other settings surface (<c>REQ-UI-070</c>), and so that it follows the four
    /// renderings and five options of <c>REQ-UI-071</c> without knowing they exist.
    /// </para>
    /// </remarks>
    public sealed class ToolbarCustomiserDialog : SettingsDialog
    {
        /// <summary>The dialog's name, and the key Persist Mode remembers it by.</summary>
        public const string DialogTitle = "Toolbars";

        private readonly ToolbarsPage _page;

        /// <summary>Creates the customiser over the shell's live arrangement.</summary>
        /// <param name="options">The dialog framework's options.</param>
        /// <param name="layout">The arrangement to edit; changed in place.</param>
        /// <exception cref="ArgumentNullException">An argument is null.</exception>
        public ToolbarCustomiserDialog(DialogFrameworkOptions options, ToolbarLayout layout)
            : base(DialogTitle, options)
        {
            if (layout == null)
            {
                throw new ArgumentNullException(nameof(layout));
            }

            _page = new ToolbarsPage(layout);

            AddPage(DialogTitle, _page);
        }

        /// <summary>The customiser's three lists.</summary>
        public ToolbarsPage Page => _page;

        /// <summary>Rebuilds the lists after something outside the dialog changed the toolbars.</summary>
        public void Refresh() => _page.Refresh();
    }
}
