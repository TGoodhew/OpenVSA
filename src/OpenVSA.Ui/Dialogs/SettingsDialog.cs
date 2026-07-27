using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace OpenVSA.Ui.Dialogs
{
    /// <summary>
    /// A tabbed, modeless, live settings dialog (<c>REQ-UI-070</c>) with the four renderings and
    /// five options of <c>REQ-UI-071</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>There is no OK and no Apply, and that is the requirement, not an omission.</strong>
    /// A control on a page edits live state the moment it is moved; the dialog never holds a pending
    /// copy of anything, so there is nothing for an Apply button to do and nothing for a Cancel
    /// button to undo. <see cref="CommitButtonCount"/> exists so a test can assert the absence
    /// rather than take this paragraph's word for it. Closing the window is not a decision about the
    /// settings: they were applied as they were made.
    /// </para>
    /// <para>
    /// <strong>Modeless is enforced, not merely intended.</strong> <see cref="ShowDialog"/> throws;
    /// <see cref="ShowModeless"/> is the way to open one. A modal settings dialog would stop the
    /// measurement updating behind it and would put the hot spots out of reach, which is precisely
    /// the pair of things <c>REQ-UI-070</c> requires to keep working.
    /// </para>
    /// <para>
    /// <strong>One page set, four presentations.</strong> Each page's element is moved between
    /// renderings rather than duplicated, so "every control reachable in one is reachable in the
    /// others" holds by construction: there is only ever one of each control, and
    /// <see cref="IsReachable"/> walks the live logical tree to prove it landed somewhere.
    /// </para>
    /// <para>
    /// <strong>Fixed Size is imposed on the page host, not on the window.</strong> Giving the host a
    /// minimum equal to the union of every page means the window sizes itself once, to the largest
    /// page, and switching tabs cannot change it — without this code having to guess at the height
    /// of a title bar or the width of a window border, which is what a hard-coded window size would
    /// amount to.
    /// </para>
    /// </remarks>
    public class SettingsDialog : Window
    {
        private readonly List<SettingsPage> _pages = new List<SettingsPage>();

        /// <summary>Each page's own minimum, as its author set it, before Fixed Size raises it.</summary>
        private readonly Dictionary<SettingsPage, Size> _floors = new Dictionary<SettingsPage, Size>();

        private readonly ReadOnlyCollection<SettingsPage> _readOnlyPages;
        private readonly DialogFrameworkOptions _options;
        private readonly Border _host;
        private readonly ComboBox _modeBox;
        private readonly ToggleButton _tabNames;

        private DialogMode _mode;
        private Size _union = Size.Empty;
        private int _selected;
        private bool _tabsExpanded;
        private bool _building;

        /// <summary>Creates a settings dialog.</summary>
        /// <param name="name">The dialog's name; its title, and the key Persist Mode remembers it by.</param>
        /// <param name="options">The framework options, shared with every other dialog.</param>
        /// <exception cref="ArgumentException"><paramref name="name"/> is null or blank.</exception>
        /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
        public SettingsDialog(string name, DialogFrameworkOptions options)
        {
            if (string.IsNullOrEmpty(name) || name.Trim().Length == 0)
            {
                throw new ArgumentException("A dialog needs a name.", nameof(name));
            }

            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            DialogName = name.Trim();
            _options = options;
            _readOnlyPages = new ReadOnlyCollection<SettingsPage>(_pages);

            Title = DialogName;
            SizeToContent = SizeToContent.WidthAndHeight;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;

            _mode = options.ModeFor(DialogName);
            _tabsExpanded = !options.TabsCollapsedByDefault;

            // Set here as well as in ShowModeless, so a dialog that is constructed and inspected
            // without being shown still reports what it would do.
            Topmost = options.KeepOnTop;

            _host = new Border { Margin = new Thickness(10.0, 10.0, 10.0, 4.0) };

            _modeBox = new ComboBox { MinWidth = 170.0, Margin = new Thickness(6.0, 0.0, 0.0, 0.0) };

            foreach (DialogMode mode in DialogModes.All)
            {
                _modeBox.Items.Add(DialogModes.NameOf(mode));
            }

            _modeBox.SelectedIndex = (int)_mode;
            _modeBox.SelectionChanged += OnModeChosen;

            _tabNames = new ToggleButton
            {
                Content = "Tab names",
                Margin = new Thickness(10.0, 0.0, 0.0, 0.0),
                Padding = new Thickness(8.0, 2.0, 8.0, 2.0),
                IsChecked = _tabsExpanded,
            };

            _tabNames.Checked += (sender, e) => SetTabsExpanded(true);
            _tabNames.Unchecked += (sender, e) => SetTabsExpanded(false);

            Content = BuildFrame();

            options.Changed += OnOptionsChanged;

            Closed += (sender, e) =>
            {
                options.Changed -= OnOptionsChanged;
                options.RememberMode(DialogName, _mode);
            };
        }

        /// <summary>
        /// The dialog's name: its title, and its key for Persist Mode.
        /// </summary>
        /// <remarks>
        /// Not <see cref="FrameworkElement.Name"/>, which has to be a valid identifier and so could
        /// not hold "Display Preferences" at all.
        /// </remarks>
        public string DialogName { get; }

        /// <summary>The pages, in the order they are presented.</summary>
        public IReadOnlyList<SettingsPage> Pages => _readOnlyPages;

        /// <summary>The page names, in order.</summary>
        public IReadOnlyList<string> PageNames
        {
            get
            {
                var names = new List<string>(_pages.Count);

                foreach (SettingsPage page in _pages)
                {
                    names.Add(page.Name);
                }

                return names;
            }
        }

        /// <summary>The framework options this dialog follows.</summary>
        public DialogFrameworkOptions Options => _options;

        /// <summary>The element holding whichever presentation is in force.</summary>
        public FrameworkElement PageHost => _host;

        /// <summary>The current presentation's root element.</summary>
        public UIElement Presentation => _host.Child;

        /// <summary>
        /// How this dialog is laying its pages out.
        /// </summary>
        /// <remarks>
        /// This dialog's mode, which is not necessarily the framework's Default Mode: Persist Mode
        /// exists precisely so one dialog can differ from the default and keep differing.
        /// </remarks>
        public DialogMode Mode
        {
            get { return _mode; }

            set
            {
                if (_mode == value)
                {
                    return;
                }

                _mode = value;
                _modeBox.SelectedIndex = (int)value;

                // Recorded on every change rather than only on close. The two are the same thing —
                // the last mode set is the mode it was closed in — and recording it here means
                // Persist Mode does not depend on a window-closing event ever being raised, which
                // is what makes it survive a process that is killed rather than closed.
                _options.RememberMode(DialogName, value);

                Rebuild();
            }
        }

        /// <summary>The page currently in front, by position.</summary>
        /// <exception cref="ArgumentOutOfRangeException">There is no page at that position.</exception>
        public int SelectedIndex
        {
            get { return _selected; }

            set
            {
                if (_pages.Count == 0 && value == 0)
                {
                    return;
                }

                if (value < 0 || value >= _pages.Count)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(value), value, "There is no page at that position.");
                }

                if (_selected == value)
                {
                    return;
                }

                _selected = value;
                ShowSelection();
            }
        }

        /// <summary>
        /// Whether the tab strip is collapsed to initials (<c>REQ-UI-071</c>).
        /// </summary>
        /// <remarks>
        /// False under every mode but "Tabs on left", whatever the option says. That is the option's
        /// stated scope, and reporting it as collapsed under a mode that has no left-hand strip
        /// would be a lie a test could not distinguish from the setting being ignored.
        /// </remarks>
        public bool AreTabsCollapsed => _mode == DialogMode.TabsOnLeft && !_tabsExpanded;

        /// <summary>
        /// How many OK, Apply or Cancel buttons the dialog has, anywhere in it.
        /// </summary>
        /// <remarks>
        /// Always zero, and <c>REQ-UI-070</c>'s criterion is that a test says so. A live dialog has
        /// nothing to commit and nothing to roll back; a Cancel counts here because a button that
        /// implied the changes could be undone would be the more misleading of the three.
        /// </remarks>
        public int CommitButtonCount
        {
            get
            {
                int found = 0;
                CountCommitButtons(this, ref found);
                return found;
            }
        }

        /// <summary>
        /// The size every page is guaranteed, or an empty size when Fixed Size is off.
        /// </summary>
        /// <remarks>
        /// The union of the pages' unconstrained desired sizes, which is what "sized to the largest
        /// tab they contain" means when one page is the widest and another the tallest. Reported
        /// from what the last rebuild measured rather than measured again here: measuring restores
        /// each page's own minimum in order to take a reading, and a property getter that quietly
        /// undid the Fixed Size it was asked about would be a trap.
        /// </remarks>
        public Size FixedContentSize => _options.FixedSize ? _union : Size.Empty;

        /// <summary>
        /// Adds a page.
        /// </summary>
        /// <param name="page">The page.</param>
        /// <exception cref="ArgumentNullException"><paramref name="page"/> is null.</exception>
        /// <exception cref="ArgumentException">A page of that name has already been added.</exception>
        public void AddPage(SettingsPage page)
        {
            if (page == null)
            {
                throw new ArgumentNullException(nameof(page));
            }

            foreach (SettingsPage existing in _pages)
            {
                if (string.Equals(existing.Name, page.Name, StringComparison.OrdinalIgnoreCase))
                {
                    throw new ArgumentException(
                        "This dialog already has a page named '" + page.Name + "'.", nameof(page));
                }
            }

            _pages.Add(page);
            _floors.Add(page, new Size(page.Content.MinWidth, page.Content.MinHeight));

            Rebuild();
        }

        /// <summary>Adds a page from a name and an element.</summary>
        /// <param name="name">The page's name.</param>
        /// <param name="content">The page's one element.</param>
        public void AddPage(string name, FrameworkElement content) =>
            AddPage(new SettingsPage(name, content));

        /// <summary>
        /// Whether an element is somewhere in the presentation currently built.
        /// </summary>
        /// <param name="element">The element to look for.</param>
        /// <remarks>
        /// The logical tree, not the visual one. An unselected tab's content is absent from the
        /// visual tree by design — that is what makes tabs cheap — so a visual walk would report
        /// every mode as having lost four pages out of five.
        /// </remarks>
        public bool IsReachable(DependencyObject element)
        {
            if (element == null || _host.Child == null)
            {
                return false;
            }

            return Contains(_host.Child, element);
        }

        /// <summary>
        /// Shows the dialog without blocking the measurement (<c>REQ-UI-070</c>).
        /// </summary>
        /// <param name="owner">The owning window, or <c>null</c>.</param>
        public void ShowModeless(Window owner)
        {
            if (owner != null && !ReferenceEquals(owner, this))
            {
                Owner = owner;
            }

            Topmost = _options.KeepOnTop;
            Show();
            Activate();
        }

        /// <summary>
        /// Refused: a setting dialog is never modal (<c>REQ-UI-070</c>).
        /// </summary>
        /// <exception cref="InvalidOperationException">Always.</exception>
        /// <remarks>
        /// A hidden member rather than an override, because <see cref="Window.ShowDialog"/> is not
        /// virtual. It is still worth having: the mistake this catches is someone reaching for the
        /// familiar call on a concrete dialog type, and it fails at the point of the mistake with
        /// the reason attached. The complementary check — that no source file in the shell calls
        /// <c>ShowDialog</c> on one of these — is an architecture test, because that is the form of
        /// the criterion that no signature can express.
        /// </remarks>
        public new bool? ShowDialog() =>
            throw new InvalidOperationException(
                "REQ-UI-070: setting dialogs are modeless. A modal one would stop the measurement " +
                "updating and put the hot spots out of reach. Use ShowModeless instead.");

        /// <summary>
        /// Rebuilds the presentation for the current mode.
        /// </summary>
        /// <remarks>
        /// Every page is detached first. The elements are shared between presentations, and a
        /// <see cref="UIElement"/> can have one parent — attaching one that a discarded TabItem
        /// still holds throws, and it throws deep inside WPF rather than here.
        /// </remarks>
        private void Rebuild()
        {
            if (_building)
            {
                return;
            }

            _building = true;

            try
            {
                foreach (SettingsPage page in _pages)
                {
                    Detach(page.Content);
                }

                if (_selected >= _pages.Count)
                {
                    _selected = Math.Max(0, _pages.Count - 1);
                }

                _host.Child = DialogModes.IsTabbed(_mode) ? BuildTabs() : BuildExpanders();

                ApplyFixedSize();

                _tabNames.Visibility = _mode == DialogMode.TabsOnLeft
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
            finally
            {
                _building = false;
            }
        }

        private UIElement BuildTabs()
        {
            var tabs = new TabControl
            {
                TabStripPlacement = _mode == DialogMode.TabsOnLeft ? Dock.Left : Dock.Top,
            };

            bool collapsed = AreTabsCollapsed;

            foreach (SettingsPage page in _pages)
            {
                tabs.Items.Add(new TabItem
                {
                    Header = collapsed ? page.Initial : page.Name,
                    ToolTip = page.Name,
                    Content = page.Content,
                });
            }

            if (_pages.Count > 0)
            {
                tabs.SelectedIndex = _selected;
            }

            tabs.SelectionChanged += OnTabChanged;
            return tabs;
        }

        private UIElement BuildExpanders()
        {
            bool vertical = _mode == DialogMode.ExpandersVertical;

            var stack = new StackPanel
            {
                Orientation = vertical ? Orientation.Vertical : Orientation.Horizontal,
            };

            for (int i = 0; i < _pages.Count; i++)
            {
                SettingsPage page = _pages[i];

                var expander = new Expander
                {
                    Header = page.Name,
                    Content = page.Content,

                    // Only the selected page starts open. Five open expanders is a dialog taller
                    // than the screen; the rest are one click away and the scroll viewer copes
                    // with however many the user opens.
                    IsExpanded = i == _selected,
                    ExpandDirection = vertical ? ExpandDirection.Down : ExpandDirection.Right,
                    Margin = new Thickness(0.0, 0.0, vertical ? 0.0 : 6.0, vertical ? 4.0 : 0.0),
                };

                int position = i;
                expander.Expanded += (sender, e) => OnExpanded(position);

                stack.Children.Add(expander);
            }

            return new ScrollViewer
            {
                Content = stack,
                VerticalScrollBarVisibility =
                    vertical ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled,
                HorizontalScrollBarVisibility =
                    vertical ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto,
            };
        }

        private object BuildFrame()
        {
            var footer = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(10.0, 0.0, 10.0, 10.0),
            };

            footer.Children.Add(new TextBlock
            {
                Text = "Layout:",
                VerticalAlignment = VerticalAlignment.Center,
            });

            footer.Children.Add(_modeBox);
            footer.Children.Add(_tabNames);

            var close = new Button
            {
                // "Close", never "OK": nothing is committed here, because everything already was.
                Content = "Close",
                MinWidth = 90.0,
                Margin = new Thickness(16.0, 0.0, 0.0, 0.0),
                Padding = new Thickness(8.0, 2.0, 8.0, 2.0),
                IsCancel = true,
            };

            close.Click += (sender, e) => Close();
            footer.Children.Add(close);

            var frame = new DockPanel { LastChildFill = true };

            DockPanel.SetDock(footer, Dock.Bottom);
            frame.Children.Add(footer);
            frame.Children.Add(_host);

            return frame;
        }

        /// <summary>
        /// Gives every page the same minimum: the union of them all (<c>REQ-UI-071</c>).
        /// </summary>
        /// <remarks>
        /// <para>
        /// <strong>On the pages, not on the host.</strong> A minimum on the host would leave the
        /// presentation's own chrome outside it, and a tab strip is chrome: the window would come
        /// out one strip taller when the tallest page was in front than when a short one was, which
        /// is exactly the resizing the option exists to prevent. Every page being the same size
        /// means the strip adds the same amount whichever page is selected.
        /// </para>
        /// <para>
        /// A page's own minimum is remembered and restored, so turning Fixed Size off does not
        /// discard a floor the page's author set for their own reasons — and the union is measured
        /// against those floors rather than against the union it was last given, which would
        /// otherwise ratchet upwards every time this ran.
        /// </para>
        /// </remarks>
        private void ApplyFixedSize()
        {
            if (!_options.FixedSize)
            {
                _union = Size.Empty;
                Restore();
                return;
            }

            _union = Union();

            foreach (SettingsPage page in _pages)
            {
                Size floor = _floors[page];

                page.Content.MinWidth = Math.Max(floor.Width, _union.Width);
                page.Content.MinHeight = Math.Max(floor.Height, _union.Height);
            }
        }

        private void Restore()
        {
            foreach (SettingsPage page in _pages)
            {
                Size floor = _floors[page];

                page.Content.MinWidth = floor.Width;
                page.Content.MinHeight = floor.Height;
            }
        }

        private Size Union()
        {
            Restore();

            double width = 0.0;
            double height = 0.0;

            Size limit = MeasureLimit();

            foreach (SettingsPage page in _pages)
            {
                page.Content.Measure(limit);

                Size desired = page.Content.DesiredSize;

                width = Math.Max(width, desired.Width);
                height = Math.Max(height, desired.Height);

                // Measured here only to learn the size; the layout pass that follows measures it
                // again against the constraint it will actually get.
                page.Content.InvalidateMeasure();
            }

            return new Size(width, height);
        }

        /// <summary>
        /// The largest a page is measured against when the union is taken.
        /// </summary>
        /// <remarks>
        /// Not infinity. A page holding a list is happy to ask for the height of every row it has,
        /// and Fixed Size would then hand that height to the whole dialog — a settings window taller
        /// than the screen, with its Close button below the taskbar. A page that wants more than
        /// this is a page that is meant to scroll. The allowance is for the window's own chrome and
        /// the footer, which are outside the page and still have to fit on the same screen.
        /// </remarks>
        private static Size MeasureLimit()
        {
            const double Chrome = 120.0;

            Rect work = SystemParameters.WorkArea;

            return new Size(
                Math.Max(200.0, work.Width - Chrome),
                Math.Max(200.0, work.Height - Chrome));
        }

        private void ShowSelection()
        {
            var tabs = _host.Child as TabControl;

            if (tabs != null)
            {
                tabs.SelectedIndex = _selected;
                return;
            }

            var scroller = _host.Child as ScrollViewer;
            var stack = scroller == null ? null : scroller.Content as StackPanel;

            if (stack == null)
            {
                return;
            }

            for (int i = 0; i < stack.Children.Count; i++)
            {
                var expander = stack.Children[i] as Expander;

                if (expander != null && i == _selected)
                {
                    expander.IsExpanded = true;
                }
            }
        }

        private void OnTabChanged(object sender, SelectionChangedEventArgs e)
        {
            var tabs = sender as TabControl;

            // Selection changes bubble from combo boxes on the pages themselves, and one of those
            // is not a change of page.
            if (_building || tabs == null || !ReferenceEquals(e.OriginalSource, tabs))
            {
                return;
            }

            if (tabs.SelectedIndex >= 0)
            {
                _selected = tabs.SelectedIndex;
            }
        }

        private void OnExpanded(int position)
        {
            if (!_building)
            {
                _selected = position;
            }
        }

        private void OnModeChosen(object sender, SelectionChangedEventArgs e)
        {
            if (_building || _modeBox.SelectedIndex < 0)
            {
                return;
            }

            Mode = (DialogMode)_modeBox.SelectedIndex;
        }

        private void SetTabsExpanded(bool expanded)
        {
            if (_tabsExpanded == expanded)
            {
                return;
            }

            _tabsExpanded = expanded;

            if (DialogModes.IsTabbed(_mode))
            {
                Rebuild();
            }
        }

        /// <summary>
        /// Follows a change to the global options while open (<c>REQ-UI-070</c>).
        /// </summary>
        /// <remarks>
        /// Default Mode is deliberately not applied to a dialog already open: it is the mode a
        /// dialog opens in, and moving an open dialog's tabs out from under the user because they
        /// changed a default is not what the option says. Fixed Size, Keep on Top and the collapsed
        /// strip are all statements about how dialogs look now, so they take effect now.
        /// </remarks>
        private void OnOptionsChanged(object sender, EventArgs e)
        {
            Topmost = _options.KeepOnTop;
            _tabsExpanded = !_options.TabsCollapsedByDefault;
            _tabNames.IsChecked = _tabsExpanded;

            Rebuild();
        }

        private static void Detach(UIElement element)
        {
            DependencyObject parent = LogicalTreeHelper.GetParent(element);

            var content = parent as ContentControl;

            if (content != null)
            {
                content.Content = null;
                return;
            }

            var decorator = parent as Decorator;

            if (decorator != null)
            {
                decorator.Child = null;
                return;
            }

            var panel = parent as Panel;

            if (panel != null)
            {
                panel.Children.Remove(element);
            }
        }

        private static bool Contains(DependencyObject root, DependencyObject sought)
        {
            if (ReferenceEquals(root, sought))
            {
                return true;
            }

            foreach (object child in LogicalTreeHelper.GetChildren(root))
            {
                var node = child as DependencyObject;

                if (node != null && Contains(node, sought))
                {
                    return true;
                }
            }

            return false;
        }

        private static void CountCommitButtons(DependencyObject root, ref int found)
        {
            var button = root as ButtonBase;

            if (button != null && IsCommitCaption(button.Content as string))
            {
                found++;
            }

            foreach (object child in LogicalTreeHelper.GetChildren(root))
            {
                var node = child as DependencyObject;

                if (node != null)
                {
                    CountCommitButtons(node, ref found);
                }
            }
        }

        private static bool IsCommitCaption(string caption)
        {
            if (string.IsNullOrEmpty(caption))
            {
                return false;
            }

            string trimmed = caption.Replace("_", string.Empty).Trim().TrimEnd('.', '…');

            return string.Equals(trimmed, "OK", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(trimmed, "Apply", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(trimmed, "Cancel", StringComparison.OrdinalIgnoreCase);
        }

        /// <inheritdoc />
        public override string ToString() =>
            DialogName + ": " + _pages.Count + " page(s), " + DialogModes.NameOf(_mode);
    }
}
