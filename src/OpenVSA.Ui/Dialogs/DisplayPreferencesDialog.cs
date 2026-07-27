using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using OpenVSA.Ui.Dialogs.Pages;
using OpenVSA.Ui.Rendering;

namespace OpenVSA.Ui.Dialogs
{
    /// <summary>
    /// The Display Preferences dialog: <c>Trace | Colour | User Map Colour | Font | Window</c>
    /// (<c>REQ-UI-073</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Five tabs, and the requirement is as much about the sixth as about the five.</strong>
    /// There is deliberately no General, Theme or Appearance tab: theming lives under Window and
    /// Colour, and the instinct to add a General tab is exactly what would split it away from them.
    /// <see cref="TabNames"/> is the requirement's list, and the dialog is built from it rather than
    /// from five separate <c>AddPage</c> calls — so a sixth tab cannot be added without editing the
    /// list a test reads.
    /// </para>
    /// <para>
    /// <strong>Nothing here is a copy of anything.</strong> Each page edits the live preference
    /// object the shell is already using, so a change applies as it is made (<c>REQ-UI-070</c>) and
    /// persists per <c>REQ-UI-014</c> when the shell writes its sidecar.
    /// </para>
    /// </remarks>
    public sealed class DisplayPreferencesDialog : SettingsDialog
    {
        /// <summary>The dialog's name, and the key Persist Mode remembers it by.</summary>
        public const string DialogTitle = "Display Preferences";

        private static readonly ReadOnlyCollection<string> Names =
            new ReadOnlyCollection<string>(new List<string>
            {
                "Trace", "Colour", "User Map Colour", "Font", "Window",
            });

        private readonly TracePage _trace;
        private readonly ColourPage _colour;
        private readonly UserMapColourPage _userMap;
        private readonly FontPage _font;
        private readonly WindowPage _window;

        /// <summary>Creates the dialog over the live preferences.</summary>
        /// <param name="options">The dialog framework's options.</param>
        /// <param name="colours">The themeable elements' colours (<c>REQ-UI-014</c>).</param>
        /// <param name="fonts">The three font slots (<c>REQ-UI-080</c>).</param>
        /// <param name="traces">The trace display options.</param>
        /// <param name="spectrogramMap">The spectrogram map to seed the user map from.</param>
        /// <exception cref="ArgumentNullException">An argument is null.</exception>
        public DisplayPreferencesDialog(
            DialogFrameworkOptions options,
            ColourPreferences colours,
            FontPreferences fonts,
            TraceDisplayOptions traces,
            SpectrogramColourMap spectrogramMap)
            : base(DialogTitle, options)
        {
            if (colours == null)
            {
                throw new ArgumentNullException(nameof(colours));
            }

            if (fonts == null)
            {
                throw new ArgumentNullException(nameof(fonts));
            }

            if (traces == null)
            {
                throw new ArgumentNullException(nameof(traces));
            }

            if (spectrogramMap == null)
            {
                throw new ArgumentNullException(nameof(spectrogramMap));
            }

            _trace = new TracePage(traces);
            _colour = new ColourPage(colours);
            _userMap = new UserMapColourPage(spectrogramMap);
            _font = new FontPage(fonts);
            _window = new WindowPage(options);

            foreach (string name in Names)
            {
                AddPage(name, PageFor(name));
            }

            _colour.ColoursChanged += (sender, e) => Raise(ColoursChanged);
            _userMap.MapChanged += (sender, e) => Raise(SpectrogramMapChanged);
            _font.FontsChanged += (sender, e) => Raise(FontsChanged);
        }

        /// <summary>The five tab names, in order, as <c>REQ-UI-073</c> lists them.</summary>
        public static IReadOnlyList<string> TabNames => Names;

        /// <summary>The Trace tab.</summary>
        public TracePage Trace => _trace;

        /// <summary>The Colour tab (<c>REQ-UI-022</c>'s element set).</summary>
        public ColourPage Colour => _colour;

        /// <summary>The User Map Colour tab (<c>REQ-UI-024</c>'s user map).</summary>
        public UserMapColourPage UserMap => _userMap;

        /// <summary>The Font tab (<c>REQ-UI-080</c>'s three slots).</summary>
        public FontPage Font => _font;

        /// <summary>The Window tab (<c>REQ-UI-071</c>'s framework options).</summary>
        public WindowPage Window => _window;

        /// <summary>Raised when a colour changes, so the display can follow immediately.</summary>
        public event EventHandler ColoursChanged;

        /// <summary>Raised when the user spectrogram map changes.</summary>
        public event EventHandler SpectrogramMapChanged;

        /// <summary>Raised when a font slot changes.</summary>
        public event EventHandler FontsChanged;

        /// <summary>The spectrogram map as the User Map Colour tab currently has it.</summary>
        public SpectrogramColourMap SpectrogramMap => _userMap.Map;

        private System.Windows.FrameworkElement PageFor(string name)
        {
            switch (name)
            {
                case "Trace": return _trace;
                case "Colour": return _colour;
                case "User Map Colour": return _userMap;
                case "Font": return _font;
                case "Window": return _window;
            }

            // Unreachable while the list and this switch agree, which is the point of having both:
            // a name added to one and not the other fails here rather than opening a blank tab.
            throw new InvalidOperationException(
                "REQ-UI-073 names a '" + name + "' tab that this dialog has no page for.");
        }

        private void Raise(EventHandler handler)
        {
            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }
        }
    }
}
