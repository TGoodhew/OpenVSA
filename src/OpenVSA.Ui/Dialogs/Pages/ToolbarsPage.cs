using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using OpenVSA.Ui.Toolbars;

namespace OpenVSA.Ui.Dialogs.Pages
{
    /// <summary>
    /// One row of one of the page's lists: a path, and what to call it on screen.
    /// </summary>
    /// <remarks>
    /// The path is the thing; the text is for reading. A list bound to strings alone would have to
    /// parse the caption back into a path to know what the user had selected, and a control named
    /// after its own toolbar would then be ambiguous.
    /// </remarks>
    public sealed class ToolbarEntry
    {
        internal ToolbarEntry(string path, string text)
        {
            Path = path;
            Text = text;
        }

        /// <summary>The control's path, or <c>ToolbarLayout.SeparatorPath</c>.</summary>
        public string Path { get; }

        /// <summary>What the list shows.</summary>
        public string Text { get; }

        /// <inheritdoc />
        public override string ToString() => Text;
    }

    /// <summary>
    /// The toolbar customiser of <c>REQ-UI-064</c>: the list of toolbars, the control picker and
    /// the contents editor, side by side.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Three lists, because the requirement names three things.</strong> Not three tabs:
    /// populating a toolbar means having the toolbar, its contents and the available controls in
    /// front of you at once, and a tab strip between them would make the one task three.
    /// </para>
    /// <para>
    /// <strong>Nothing is pending.</strong> Every button edits the live
    /// <see cref="ToolbarLayout"/>, the tray follows immediately, and there is nothing to apply —
    /// which is what <c>REQ-UI-070</c> requires of a settings dialog and why this page has no OK.
    /// A user who does not like what they have done has File &gt; Preset &gt; Toolbars.
    /// </para>
    /// <para>
    /// <strong>The macro bar is absent, and absent by asking rather than by name.</strong> The list
    /// is <see cref="ToolbarLayout.Customisable"/> and the picker is drawn from the customisable
    /// toolbars only, so the bar that <c>REQ-UI-063</c> puts outside the customiser is outside all
    /// three of these lists without this file mentioning it.
    /// </para>
    /// </remarks>
    public sealed class ToolbarsPage : StackPanel
    {
        private const double ListHeight = 250.0;

        private readonly ToolbarLayout _layout;

        private readonly ListBox _toolbars = new ListBox { Height = ListHeight, MinWidth = 180.0 };
        private readonly ListBox _contents = new ListBox { Height = ListHeight, MinWidth = 200.0 };
        private readonly ListBox _picker = new ListBox { Height = ListHeight, MinWidth = 230.0 };

        private readonly TextBox _newName = new TextBox { MinWidth = 120.0 };
        private readonly CheckBox _visible = new CheckBox { Content = "Shown in the toolbar tray" };
        private readonly TextBlock _message = new TextBlock { TextWrapping = TextWrapping.Wrap };

        private readonly Button _create;
        private readonly Button _delete;
        private readonly Button _barUp;
        private readonly Button _barDown;
        private readonly Button _add;
        private readonly Button _remove;
        private readonly Button _controlUp;
        private readonly Button _controlDown;

        private bool _refreshing;

        /// <summary>Creates the page over the shell's live arrangement.</summary>
        /// <param name="layout">The arrangement to edit; changed in place.</param>
        /// <exception cref="ArgumentNullException"><paramref name="layout"/> is null.</exception>
        public ToolbarsPage(ToolbarLayout layout)
        {
            if (layout == null)
            {
                throw new ArgumentNullException(nameof(layout));
            }

            _layout = layout;

            Margin = new Thickness(4.0);

            _create = Command("New toolbar", OnCreate);
            _delete = Command("Delete", OnDelete);
            _barUp = Command("Move up", (sender, e) => MoveBar(-1));
            _barDown = Command("Move down", (sender, e) => MoveBar(1));
            // The arrows point at where the control ends up: Add takes one out of the picker on the
            // right and puts it on the toolbar in the middle, and Remove sends it back.
            _add = Command("◂ Add", OnAdd);
            _remove = Command("Remove ▸", OnRemove);
            _controlUp = Command("Move up", (sender, e) => MoveControl(-1));
            _controlDown = Command("Move down", (sender, e) => MoveControl(1));

            _toolbars.DisplayMemberPath = "Name";
            _contents.DisplayMemberPath = "Text";
            _picker.DisplayMemberPath = "Text";

            _toolbars.SelectionChanged += (sender, e) => ShowSelectedToolbar();
            _visible.Click += OnVisibleChanged;

            // Double-click is how a user who has not found the buttons moves a control, and it is
            // the gesture a control picker is expected to answer.
            _picker.MouseDoubleClick += (sender, e) => OnAdd(sender, null);
            _contents.MouseDoubleClick += (sender, e) => OnRemove(sender, null);

            Build();

            _layout.Changed += OnLayoutChanged;

            Refresh();
        }

        /// <summary>The list of toolbars (<c>REQ-UI-064</c>).</summary>
        public ListBox ToolbarList => _toolbars;

        /// <summary>The contents editor: what is on the selected toolbar, in order.</summary>
        public ListBox ContentsList => _contents;

        /// <summary>The control picker: everything that may be put on it.</summary>
        public ListBox PickerList => _picker;

        /// <summary>Where the name of a new toolbar is typed.</summary>
        public TextBox NewToolbarName => _newName;

        /// <summary>Creates a toolbar named by <see cref="NewToolbarName"/>.</summary>
        public Button CreateButton => _create;

        /// <summary>Deletes the selected custom toolbar.</summary>
        public Button DeleteButton => _delete;

        /// <summary>Moves the selected toolbar towards the front of the tray.</summary>
        public Button ToolbarUpButton => _barUp;

        /// <summary>Moves the selected toolbar towards the back of the tray.</summary>
        public Button ToolbarDownButton => _barDown;

        /// <summary>Puts the picked control on the selected toolbar.</summary>
        public Button AddButton => _add;

        /// <summary>Takes the selected control off the toolbar.</summary>
        public Button RemoveButton => _remove;

        /// <summary>Moves the selected control towards the left of its toolbar.</summary>
        public Button ControlUpButton => _controlUp;

        /// <summary>Moves the selected control towards the right of its toolbar.</summary>
        public Button ControlDownButton => _controlDown;

        /// <summary>Whether the tray shows the selected toolbar.</summary>
        public CheckBox VisibleBox => _visible;

        /// <summary>What the page last said about a refusal, or an empty string.</summary>
        public string Message => _message.Text;

        /// <summary>The toolbar being edited, or <c>null</c>.</summary>
        public ToolbarBar SelectedToolbar => _toolbars.SelectedItem as ToolbarBar;

        /// <summary>Selects a toolbar by name.</summary>
        /// <param name="name">The toolbar's name.</param>
        /// <returns>Whether the customiser lists a toolbar of that name.</returns>
        public bool Select(string name)
        {
            foreach (object item in _toolbars.Items)
            {
                var bar = item as ToolbarBar;

                if (bar != null && string.Equals(bar.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    _toolbars.SelectedItem = bar;
                    return true;
                }
            }

            return false;
        }

        /// <summary>Rebuilds all three lists from the arrangement.</summary>
        public void Refresh()
        {
            _refreshing = true;

            try
            {
                string selected = SelectedToolbar == null ? null : SelectedToolbar.Name;

                _toolbars.Items.Clear();

                foreach (ToolbarBar bar in _layout.Customisable)
                {
                    _toolbars.Items.Add(bar);
                }

                if (selected == null || !Select(selected))
                {
                    _toolbars.SelectedIndex = _toolbars.Items.Count > 0 ? 0 : -1;
                }
            }
            finally
            {
                _refreshing = false;
            }

            ShowSelectedToolbar();
        }

        private static string TextFor(string path)
        {
            if (string.Equals(path, ToolbarLayout.SeparatorPath, StringComparison.Ordinal))
            {
                return "———  (separator)";
            }

            int mark = path.IndexOf(" > ", StringComparison.Ordinal);

            return mark <= 0 ? path : path.Substring(mark + 3);
        }

        private static Button Command(string caption, RoutedEventHandler handler)
        {
            // Left, not stretched: a button under a list stretched to the list's width, and the
            // lists are as wide as the longest control path. Only the screenshot showed it.
            var button = new Button
            {
                Content = caption,
                Margin = new Thickness(0.0, 4.0, 0.0, 0.0),
                Padding = new Thickness(8.0, 3.0, 8.0, 3.0),
                MinWidth = 96.0,
                HorizontalAlignment = HorizontalAlignment.Left,
            };

            button.Click += handler;
            return button;
        }

        /// <summary>
        /// Lays the three lists out side by side, each above its own buttons.
        /// </summary>
        /// <remarks>
        /// Bounded in width, and not by habit: Fixed Size takes the union of every page in the
        /// dialog, so a page that measures itself against infinity makes every other page that
        /// wide. The lists have a fixed height for the same reason — a list asked how tall it would
        /// like to be answers with the height of all its rows.
        /// </remarks>
        private void Build()
        {
            MaxWidth = 900.0;

            Children.Add(new TextBlock
            {
                Text = "Changes apply as they are made. File ▸ Preset ▸ Toolbars restores the " +
                       "preconfigured toolbars and removes custom ones.",
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 860.0,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0.0, 0.0, 0.0, 10.0),
            });

            var columns = new StackPanel { Orientation = Orientation.Horizontal };

            columns.Children.Add(Column("Toolbars", _toolbars, ToolbarButtons()));
            columns.Children.Add(Column("On this toolbar", _contents, ContentsButtons()));
            columns.Children.Add(Column("Available controls", _picker, PickerButtons()));

            Children.Add(columns);

            _message.Margin = new Thickness(0.0, 10.0, 0.0, 0.0);
            _message.MaxWidth = 860.0;
            _message.HorizontalAlignment = HorizontalAlignment.Left;

            Children.Add(_message);
        }

        private static UIElement Column(string heading, ListBox list, UIElement buttons)
        {
            var stack = new StackPanel { Margin = new Thickness(0.0, 0.0, 14.0, 0.0) };

            stack.Children.Add(new TextBlock
            {
                Text = heading,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0.0, 0.0, 0.0, 3.0),
            });

            stack.Children.Add(list);
            stack.Children.Add(buttons);

            return stack;
        }

        private UIElement ToolbarButtons()
        {
            var stack = new StackPanel { Margin = new Thickness(0.0, 4.0, 0.0, 0.0) };

            var naming = new StackPanel { Orientation = Orientation.Horizontal };

            naming.Children.Add(new TextBlock
            {
                Text = "Name",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0.0, 0.0, 6.0, 0.0),
            });

            naming.Children.Add(_newName);
            stack.Children.Add(naming);

            stack.Children.Add(_create);
            stack.Children.Add(_delete);

            var moving = new StackPanel { Orientation = Orientation.Horizontal };

            _barUp.Margin = new Thickness(0.0, 4.0, 6.0, 0.0);
            moving.Children.Add(_barUp);
            moving.Children.Add(_barDown);

            stack.Children.Add(moving);

            _visible.Margin = new Thickness(0.0, 10.0, 0.0, 0.0);
            stack.Children.Add(_visible);

            return stack;
        }

        private UIElement ContentsButtons()
        {
            var stack = new StackPanel { Margin = new Thickness(0.0, 4.0, 0.0, 0.0) };

            var moving = new StackPanel { Orientation = Orientation.Horizontal };

            _controlUp.Margin = new Thickness(0.0, 4.0, 6.0, 0.0);
            moving.Children.Add(_controlUp);
            moving.Children.Add(_controlDown);

            stack.Children.Add(moving);
            stack.Children.Add(_remove);

            return stack;
        }

        private UIElement PickerButtons()
        {
            var stack = new StackPanel { Margin = new Thickness(0.0, 4.0, 0.0, 0.0) };

            stack.Children.Add(_add);

            return stack;
        }

        private void OnLayoutChanged(object sender, EventArgs e)
        {
            if (!_refreshing)
            {
                Refresh();
            }
        }

        private void ShowSelectedToolbar()
        {
            ToolbarBar bar = SelectedToolbar;

            _contents.Items.Clear();
            _picker.Items.Clear();

            foreach (string path in bar == null ? new List<string>() : new List<string>(bar.Controls))
            {
                _contents.Items.Add(new ToolbarEntry(path, TextFor(path)));
            }

            foreach (string path in _layout.Picker(bar))
            {
                _picker.Items.Add(new ToolbarEntry(
                    path,
                    string.Equals(path, ToolbarLayout.SeparatorPath, StringComparison.Ordinal)
                        ? "———  (separator)"
                        : path));
            }

            _refreshing = true;

            try
            {
                _visible.IsChecked = bar != null && bar.IsVisible;
            }
            finally
            {
                _refreshing = false;
            }

            _visible.IsEnabled = bar != null;
            _delete.IsEnabled = bar != null && bar.IsCustom;
            _barUp.IsEnabled = bar != null;
            _barDown.IsEnabled = bar != null;
            _add.IsEnabled = bar != null;
            _remove.IsEnabled = bar != null;
            _controlUp.IsEnabled = bar != null;
            _controlDown.IsEnabled = bar != null;
        }

        private void OnCreate(object sender, RoutedEventArgs e)
        {
            try
            {
                ToolbarBar made = _layout.Create(_newName.Text);

                _newName.Text = string.Empty;

                Refresh();
                Select(made.Name);

                Say("Created '" + made.Name + "'. It is empty; add controls from the picker.");
            }
            catch (ArgumentException refusal)
            {
                Say(refusal.Message);
            }
        }

        private void OnDelete(object sender, RoutedEventArgs e)
        {
            ToolbarBar bar = SelectedToolbar;

            if (bar == null)
            {
                return;
            }

            try
            {
                string name = bar.Name;

                _layout.Delete(bar);
                Refresh();

                Say("Deleted '" + name + "'. What was on it went back to the toolbar it came from.");
            }
            catch (InvalidOperationException refusal)
            {
                Say(refusal.Message);
            }
        }

        private void MoveBar(int delta)
        {
            ToolbarBar bar = SelectedToolbar;

            if (bar == null)
            {
                return;
            }

            if (_layout.MoveBar(bar, delta))
            {
                Refresh();
                Select(bar.Name);
                Say(string.Empty);
            }
        }

        private void MoveControl(int delta)
        {
            ToolbarBar bar = SelectedToolbar;
            int index = _contents.SelectedIndex;

            if (bar == null || index < 0)
            {
                return;
            }

            if (_layout.MoveControl(bar, index, delta))
            {
                ShowSelectedToolbar();
                _contents.SelectedIndex = Math.Max(0, Math.Min(index + delta, _contents.Items.Count - 1));
                Say(string.Empty);
            }
        }

        private void OnAdd(object sender, RoutedEventArgs e)
        {
            ToolbarBar bar = SelectedToolbar;
            var picked = _picker.SelectedItem as ToolbarEntry;

            if (bar == null || picked == null)
            {
                return;
            }

            ToolbarBar was = _layout.BarOf(picked.Path);

            _layout.Place(bar, picked.Path);
            ShowSelectedToolbar();

            _contents.SelectedIndex = _contents.Items.Count - 1;

            Say(was == null || ReferenceEquals(was, bar)
                ? string.Empty
                : "'" + TextFor(picked.Path) + "' moved here from '" + was.Name +
                  "'. A control is on one toolbar at a time.");
        }

        private void OnRemove(object sender, RoutedEventArgs e)
        {
            ToolbarBar bar = SelectedToolbar;
            int index = _contents.SelectedIndex;

            if (bar == null || index < 0)
            {
                return;
            }

            if (_layout.Take(bar, index))
            {
                ShowSelectedToolbar();

                _contents.SelectedIndex =
                    Math.Min(index, _contents.Items.Count - 1);

                Say(string.Empty);
            }
        }

        private void OnVisibleChanged(object sender, RoutedEventArgs e)
        {
            ToolbarBar bar = SelectedToolbar;

            if (bar == null || _refreshing)
            {
                return;
            }

            _layout.SetVisible(bar, _visible.IsChecked == true);
            Say(string.Empty);
        }

        private void Say(string message) => _message.Text = message ?? string.Empty;
    }
}
