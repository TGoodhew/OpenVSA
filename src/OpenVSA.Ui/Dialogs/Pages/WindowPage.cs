using System;
using System.Windows;
using System.Windows.Controls;
using OpenVSA.Ui.Theming;

namespace OpenVSA.Ui.Dialogs.Pages
{
    /// <summary>
    /// The Window tab of Display Preferences: the dialog framework's global options
    /// (<c>REQ-UI-071</c>, <c>REQ-UI-073</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This tab is where "there is no General tab" is paid for.</strong>
    /// <c>REQ-UI-073</c> is explicit that theming lives under Window and Colour and that adding a
    /// General or Appearance tab is the natural instinct and the wrong one. The framework's own
    /// options are about how windows behave, so Window is where they belong.
    /// </para>
    /// <para>
    /// <strong>Every control here is live, including the ones that change this dialog.</strong>
    /// Switching Default Mode or turning Fixed Size off rearranges the dialog the control is in,
    /// which is the clearest demonstration of <c>REQ-UI-070</c> there is — and the reason
    /// <see cref="DialogFrameworkOptions.Changed"/> exists rather than the options being read once
    /// at construction.
    /// </para>
    /// <para>
    /// Tabs Collapsed by Default is offered under every mode and marked as applying to one. A
    /// setting that could only be changed while the mode it applies to is selected would be
    /// unfindable, and hiding it would leave the user unable to turn it back off from a collapsed
    /// strip.
    /// </para>
    /// </remarks>
    public sealed class WindowPage : StackPanel
    {
        private readonly DialogFrameworkOptions _options;
        private readonly ThemeCatalogue _themes;
        private readonly ComboBox _mode;

        private ComboBox _theme;

        /// <summary>
        /// Told when a chrome theme is chosen (<c>REQ-UI-083</c>).
        /// </summary>
        /// <remarks>
        /// The page offers the names the catalogue has and reports which was picked; the shell
        /// installs it. A page that installed it itself would be a second place that knows how a
        /// theme is applied, and the third theme this requirement exists to make cheap would have
        /// to be taught to both.
        /// </remarks>
        public Action<string> ThemeChosen { get; set; } = name => { };

        /// <summary>The chrome-theme chooser, or <c>null</c> when the page was built without one.</summary>
        public ComboBox ThemeBox => _theme;
        private readonly CheckBox _fixedSize;
        private readonly CheckBox _keepOnTop;
        private readonly CheckBox _persistMode;
        private readonly CheckBox _tabsCollapsed;
        private readonly TextBlock _collapsedNote;

        private bool _updating;

        /// <summary>Creates the page over the framework options.</summary>
        /// <param name="options">The options to edit; changed in place.</param>
        /// <param name="themes">The chrome themes on offer, or <c>null</c> to omit the chooser.</param>
        /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
        public WindowPage(DialogFrameworkOptions options, ThemeCatalogue themes = null)
        {
            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            _options = options;
            _themes = themes;

            Margin = new Thickness(4.0);
            MinWidth = 420.0;

            if (themes != null)
            {
                BuildThemeChooser();
            }

            Children.Add(new TextBlock
            {
                Text = "How every settings dialog lays itself out and behaves.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0.0, 0.0, 0.0, 10.0),
            });

            Children.Add(new TextBlock { Text = "Default Mode" });

            _mode = new ComboBox
            {
                MinWidth = 220.0,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0.0, 2.0, 0.0, 10.0),
            };

            foreach (DialogMode mode in DialogModes.All)
            {
                _mode.Items.Add(DialogModes.NameOf(mode));
            }

            _mode.SelectionChanged += OnModeChosen;
            Children.Add(_mode);

            _fixedSize = Check(
                "Fixed Size — size dialogs to the largest tab they contain",
                on => _options.FixedSize = on);

            _keepOnTop = Check(
                "Keep on Top — dialogs stay above the main window",
                on => _options.KeepOnTop = on);

            _persistMode = Check(
                "Persist Mode — a dialog reopens in the mode it was closed with",
                on => _options.PersistMode = on);

            _tabsCollapsed = Check(
                "Tabs Collapsed by Default",
                on => _options.TabsCollapsedByDefault = on);

            _collapsedNote = new TextBlock
            {
                Margin = new Thickness(20.0, 0.0, 0.0, 10.0),
                TextWrapping = TextWrapping.Wrap,
            };

            Children.Add(_collapsedNote);

            var forget = new Button
            {
                Content = "Forget remembered dialog modes",
                HorizontalAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(8.0, 4.0, 8.0, 4.0),
                Margin = new Thickness(0.0, 6.0, 0.0, 0.0),
            };

            forget.Click += (sender, e) => _options.ForgetModes();
            Children.Add(forget);

            options.Changed += OnOptionsChanged;
            Unloaded += (sender, e) => options.Changed -= OnOptionsChanged;

            Refresh();
        }

        /// <summary>The options this page edits.</summary>
        public DialogFrameworkOptions Options => _options;

        /// <summary>The note under Tabs Collapsed by Default, which says where it applies.</summary>
        public string CollapsedNote => _collapsedNote.Text;

        /// <summary>
        /// The chrome-theme chooser (<c>REQ-UI-083</c>, and <c>REQ-UI-073</c>'s placement).
        /// </summary>
        /// <remarks>
        /// <para>
        /// On the Window tab because <c>REQ-UI-073</c> says theming lives under Window and Colour,
        /// and that the instinct to add a General or Appearance tab for it is the wrong one.
        /// </para>
        /// <para>
        /// <strong>Filled from the catalogue, never from a list written here.</strong> That is what
        /// makes a third theme cost a dictionary: it appears in this box without this file being
        /// touched, which is the same rule <c>ColourPreferences.Entries</c> keeps for the colour
        /// picker.
        /// </para>
        /// </remarks>
        private void BuildThemeChooser()
        {
            Children.Add(new TextBlock
            {
                Text = "Theme",
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0.0, 0.0, 0.0, 2.0),
            });

            _theme = new ComboBox
            {
                MinWidth = 220.0,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0.0, 2.0, 0.0, 2.0),
            };

            foreach (string name in _themes.Names)
            {
                _theme.Items.Add(name);
            }

            _theme.SelectedItem = _themes.CurrentName;

            _theme.SelectionChanged += (sender, e) =>
            {
                var chosen = _theme.SelectedItem as string;

                if (!_updating && !string.IsNullOrEmpty(chosen))
                {
                    ThemeChosen(chosen);
                }
            };

            Children.Add(_theme);

            Children.Add(new TextBlock
            {
                Text = "Applies at once. A theme styles the window, menus, toolbars and panes; " +
                       "the graticule, traces and annotation follow the Colour tab and do not " +
                       "change with it.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0.0, 0.0, 0.0, 12.0),
            });
        }

        /// <summary>Brings the theme box into line with the theme in force.</summary>
        public void FollowTheme()
        {
            if (_theme == null || _themes == null)
            {
                return;
            }

            _updating = true;

            try
            {
                _theme.SelectedItem = _themes.CurrentName;
            }
            finally
            {
                _updating = false;
            }
        }

        private CheckBox Check(string caption, Action<bool> set)
        {
            var box = new CheckBox
            {
                Content = caption,
                Margin = new Thickness(0.0, 0.0, 0.0, 8.0),
            };

            box.Checked += (sender, e) => Apply(set, true);
            box.Unchecked += (sender, e) => Apply(set, false);

            Children.Add(box);
            return box;
        }

        private void Apply(Action<bool> set, bool value)
        {
            if (_updating)
            {
                return;
            }

            set(value);
        }

        private void OnModeChosen(object sender, SelectionChangedEventArgs e)
        {
            if (_updating || _mode.SelectedIndex < 0)
            {
                return;
            }

            _options.DefaultMode = (DialogMode)_mode.SelectedIndex;
        }

        private void OnOptionsChanged(object sender, EventArgs e) => Refresh();

        private void Refresh()
        {
            _updating = true;

            try
            {
                _mode.SelectedIndex = (int)_options.DefaultMode;
                _fixedSize.IsChecked = _options.FixedSize;
                _keepOnTop.IsChecked = _options.KeepOnTop;
                _persistMode.IsChecked = _options.PersistMode;
                _tabsCollapsed.IsChecked = _options.TabsCollapsedByDefault;

                _collapsedNote.Text = _options.DefaultMode == DialogMode.TabsOnLeft
                    ? "Applies to the tab strip on the left."
                    : "Applies to \"Tabs on left\" only; it has no effect in " +
                      DialogModes.NameOf(_options.DefaultMode) + ".";
            }
            finally
            {
                _updating = false;
            }
        }
    }
}
