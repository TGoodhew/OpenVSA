using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace OpenVSA.Ui.Toolbars
{
    /// <summary>
    /// A button with a dropdown beside it, each half meaning something different
    /// (<c>REQ-UI-063</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Two controls, not one with a mode.</strong> The requirement is specific about
    /// Auto-range: "a <strong>split button</strong>: main click auto-ranges all input channels,
    /// dropdown auto-ranges a chosen channel". The distinction matters to a user with one channel
    /// as much as to one with four — the main half is the thing you press without thinking, and it
    /// must not quietly mean "whichever channel was chosen last".
    /// </para>
    /// <para>
    /// Built here rather than taken from the control library. It is forty lines, it can be pressed
    /// by a test without a mouse, and its two halves are separately addressable — which is what the
    /// criterion asks to be shown.
    /// </para>
    /// </remarks>
    public sealed class SplitButton : UserControl
    {
        private readonly Button _main;
        private readonly ToggleButton _arrow;
        private readonly ContextMenu _menu;

        /// <summary>Creates a split button.</summary>
        /// <param name="caption">What the main half says.</param>
        public SplitButton(string caption)
        {
            _main = new Button
            {
                Content = caption,
                Padding = new Thickness(6.0, 1.0, 6.0, 1.0),
                Focusable = false,
            };

            _main.Click += (sender, e) => MainClick?.Invoke(this, EventArgs.Empty);

            _arrow = new ToggleButton
            {
                Content = "▾",
                Padding = new Thickness(2.0, 1.0, 2.0, 1.0),
                Focusable = false,
                ToolTip = "Choose what to act on.",
            };

            _menu = new ContextMenu { PlacementTarget = _arrow, Placement = PlacementMode.Bottom };
            _menu.Closed += (sender, e) => _arrow.IsChecked = false;

            _arrow.Click += (sender, e) => OpenDropDown(_arrow.IsChecked == true);

            var row = new StackPanel { Orientation = Orientation.Horizontal };

            row.Children.Add(_main);
            row.Children.Add(_arrow);

            Content = row;
        }

        /// <summary>Raised when the main half is pressed.</summary>
        public event EventHandler MainClick;

        /// <summary>The main half, so a test can press exactly it.</summary>
        public Button MainButton => _main;

        /// <summary>The dropdown half.</summary>
        public ToggleButton DropDownButton => _arrow;

        /// <summary>What the dropdown offers.</summary>
        public ItemCollection DropDownItems => _menu.Items;

        /// <summary>What the main half says.</summary>
        public string Caption
        {
            get { return _main.Content as string; }
            set { _main.Content = value; }
        }

        /// <summary>Raised just before the dropdown opens, so its contents can be rebuilt.</summary>
        public event EventHandler DropDownOpening;

        /// <summary>
        /// Adds an entry to the dropdown.
        /// </summary>
        /// <param name="caption">What it says.</param>
        /// <param name="chosen">What it does.</param>
        /// <exception cref="ArgumentNullException"><paramref name="chosen"/> is null.</exception>
        public MenuItem AddDropDownItem(string caption, Action chosen)
        {
            if (chosen == null)
            {
                throw new ArgumentNullException(nameof(chosen));
            }

            var item = new MenuItem { Header = caption };

            item.Click += (sender, e) => chosen();
            _menu.Items.Add(item);

            return item;
        }

        /// <summary>Empties the dropdown.</summary>
        public void ClearDropDown() => _menu.Items.Clear();

        /// <summary>
        /// Opens or closes the dropdown.
        /// </summary>
        /// <param name="open">Whether it should be open.</param>
        /// <remarks>
        /// Callable rather than only clickable, so that a test can ask what the dropdown offers
        /// without driving a mouse into a popup on another window.
        /// </remarks>
        public void OpenDropDown(bool open)
        {
            if (open)
            {
                DropDownOpening?.Invoke(this, EventArgs.Empty);
            }

            _arrow.IsChecked = open;
            _menu.IsOpen = open;
        }

        /// <summary>Whether the dropdown is showing.</summary>
        public bool IsDropDownOpen => _menu.IsOpen;

        /// <inheritdoc />
        public override string ToString() => "Split button '" + Caption + "'";
    }
}
