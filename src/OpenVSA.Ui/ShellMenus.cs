using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace OpenVSA.Ui
{
    /// <summary>
    /// The menu bar of <c>REQ-UI-060</c>, and the names it must not have.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The modern bar, not the Agilent-era one.</strong> The obvious guess — <em>File,
    /// Input, MeasSetup, Display, Trace, Marker, Control</em> — is the older product's and is no
    /// longer current. Three menus were renamed and two demoted: Input became Acquisition,
    /// MeasSetup became Analysis, Markers became Marker, Control became a submenu of Acquisition,
    /// and Display disappeared with its layout functions moving under Window and Trace.
    /// </para>
    /// <para>
    /// <strong><see cref="Superseded"/> is the half that catches the mistake.</strong> A developer
    /// working from older documentation produces the old names quite naturally, and a test that
    /// only checked the ten would pass a bar that had eleven menus with Display among them —
    /// which is exactly what this shell had until <c>REQ-UI-060</c> was implemented.
    /// </para>
    /// </remarks>
    public static class ShellMenus
    {
        private static readonly ReadOnlyCollection<string> Bar =
            new ReadOnlyCollection<string>(new List<string>
            {
                "File", "Edit", "Hardware", "Acquisition", "Analysis",
                "Trace", "Marker", "Utilities", "Window", "Help",
            });

        private static readonly ReadOnlyCollection<string> Old =
            new ReadOnlyCollection<string>(new List<string>
            {
                "Input", "MeasSetup", "Display", "Control", "Markers", "Source",
            });

        /// <summary>The ten menus, in the order the bar shows them.</summary>
        public static IReadOnlyList<string> Names => Bar;

        /// <summary>
        /// Top-level names the bar must not have, because an earlier product used them.
        /// </summary>
        /// <remarks>
        /// <em>Markers</em> and <em>Source</em> are here as well as the four the requirement names:
        /// the first is the pluralised spelling that was renamed, and the second moved under
        /// Hardware. Both are names a reader of the older manual would reach for.
        /// </remarks>
        public static IReadOnlyList<string> Superseded => Old;

        /// <summary>
        /// A menu header as the bar names it, with the access-key marker removed.
        /// </summary>
        /// <param name="header">The header, which may contain an underscore.</param>
        /// <remarks>
        /// WPF writes the access key as a leading underscore — <c>Ana_lysis</c> — and UI Automation
        /// reports the name without it. Comparing raw headers against the requirement's list would
        /// fail on the underscore rather than on the name, which is a test failing for the wrong
        /// reason.
        /// </remarks>
        public static string NameOf(string header) =>
            header == null ? string.Empty : header.Replace("_", string.Empty).Trim();

        /// <summary>Whether a header is one of the superseded names.</summary>
        /// <param name="header">The header.</param>
        public static bool IsSuperseded(string header)
        {
            string name = NameOf(header);

            foreach (string old in Old)
            {
                if (string.Equals(old, name, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
