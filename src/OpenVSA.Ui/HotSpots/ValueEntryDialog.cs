using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace OpenVSA.Ui.HotSpots
{
    /// <summary>
    /// The data-entry dialog a hot spot's double click opens (<c>REQ-UI-042</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately small. The dialog is the <em>slow</em> path — the requirement's whole argument
    /// is that the fast one is the click on the number — and it exists for the case where a value
    /// is being read off a note rather than nudged: a text field that takes a value with its units
    /// in one go.
    /// </para>
    /// <para>
    /// <see cref="Apply"/> is separate from showing the window so the behaviour can be exercised
    /// without a message pump; <see cref="Prompt"/> is the whole interaction for the shell to call.
    /// </para>
    /// </remarks>
    public sealed class ValueEntryDialog : Window
    {
        private readonly TextBox _entry;
        private readonly IHotSpotValue _value;

        /// <summary>Creates the dialog over a value.</summary>
        /// <param name="caption">What is being edited, for the title bar.</param>
        /// <param name="value">The value to set.</param>
        /// <exception cref="ArgumentNullException"><paramref name="value"/> is null.</exception>
        public ValueEntryDialog(string caption, IHotSpotValue value)
        {
            if (value == null)
            {
                throw new ArgumentNullException(nameof(value));
            }

            _value = value;

            Title = string.IsNullOrEmpty(caption) ? "Value" : caption.Trim();
            SizeToContent = SizeToContent.WidthAndHeight;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;

            _entry = new TextBox
            {
                Text = value.Text,
                MinWidth = 220.0,
                Margin = new Thickness(12.0, 12.0, 12.0, 6.0),
            };

            var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 72.0, Margin = new Thickness(4.0) };
            var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 72.0, Margin = new Thickness(4.0) };

            ok.Click += (sender, e) => Close(Apply());
            cancel.Click += (sender, e) => Close(false);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(8.0, 0.0, 8.0, 8.0),
            };

            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);

            var panel = new StackPanel();
            panel.Children.Add(_entry);
            panel.Children.Add(buttons);

            Content = panel;

            Loaded += (sender, e) =>
            {
                _entry.SelectAll();
                _entry.Focus();
            };

            PreviewKeyDown += (sender, e) =>
            {
                if (e.Key == Key.Escape)
                {
                    Close(false);
                }
            };
        }

        /// <summary>The text currently in the entry field.</summary>
        public string EntryText
        {
            get { return _entry.Text; }
            set { _entry.Text = value ?? string.Empty; }
        }

        /// <summary>
        /// Applies the entry to the value.
        /// </summary>
        /// <returns><c>true</c> if the text was understood and the value changed.</returns>
        public bool Apply() => _value.TrySet(_entry.Text);

        /// <summary>
        /// Shows the dialog over a hot spot and applies the result.
        /// </summary>
        /// <param name="owner">The owning window, or <c>null</c>.</param>
        /// <param name="spot">The hot spot being edited.</param>
        /// <returns><c>true</c> if the value changed.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="spot"/> is null.</exception>
        public static bool Prompt(Window owner, HotSpot spot)
        {
            if (spot == null)
            {
                throw new ArgumentNullException(nameof(spot));
            }

            if (spot.Value == null)
            {
                return false;
            }

            var dialog = new ValueEntryDialog(spot.Label, spot.Value) { Owner = owner };

            bool? accepted = dialog.ShowDialog();

            if (accepted != true)
            {
                return false;
            }

            spot.Refresh();
            return true;
        }

        private void Close(bool accepted)
        {
            try
            {
                DialogResult = accepted;
            }
            catch (InvalidOperationException)
            {
                // Only a window shown modally has a dialog result. One shown any other way still
                // closes, and refusing to would strand it on screen.
            }

            base.Close();
        }
    }
}
