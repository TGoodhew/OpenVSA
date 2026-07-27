using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace OpenVSA.Ui.HotSpots
{
    /// <summary>
    /// The data-entry dialog a hot spot's double click opens (<c>REQ-UI-042</c>), modeless and live
    /// (<c>REQ-UI-070</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately small. The dialog is the <em>slow</em> path — the requirement's whole argument
    /// is that the fast one is the click on the number — and it exists for the case where a value
    /// is being read off a note rather than nudged: a text field that takes a value with its units
    /// in one go.
    /// </para>
    /// <para>
    /// <strong>There is no OK, and the entry applies as it is typed.</strong> That is
    /// <c>REQ-UI-070</c>: no round-trip, and no modal loop stopping the measurement behind the
    /// dialog. A partially typed number that does not parse is simply not applied and says so;
    /// nothing is committed, because everything already was.
    /// </para>
    /// <para>
    /// <strong>Applying on each keystroke is safe here because of what is downstream.</strong>
    /// Typing <c>1.5 GHz</c> passes through <c>1</c> and <c>1.5</c> on the way, and the shell
    /// coalesces hot-spot changes over a settling interval before it re-plans — the same mechanism
    /// that stops a dozen wheel notches becoming a dozen re-arms. Without that coalescing this would
    /// be a bad idea, so it is worth knowing that removing it breaks this dialog and not just the
    /// wheel.
    /// </para>
    /// <para>
    /// <strong>The dialog and the hot spot are two surfaces over one value.</strong> Neither owns
    /// it. The dialog follows <see cref="IHotSpotValue.Changed"/> so that adjusting the hot spot
    /// with the wheel moves the number in the open dialog, and the hot spot follows the same event
    /// so that typing here moves the number on the trace.
    /// </para>
    /// </remarks>
    public sealed class ValueEntryDialog : Window
    {
        private readonly TextBox _entry;
        private readonly TextBlock _note;
        private readonly IHotSpotValue _value;

        private bool _updating;

        /// <summary>Creates the dialog over a value.</summary>
        /// <param name="caption">What is being edited, for the title bar.</param>
        /// <param name="value">The value to set; edited in place.</param>
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
                Margin = new Thickness(12.0, 12.0, 12.0, 4.0),
            };

            _entry.TextChanged += OnEntryChanged;

            _note = new TextBlock
            {
                Margin = new Thickness(12.0, 0.0, 12.0, 6.0),
                MaxWidth = 260.0,
                TextWrapping = TextWrapping.Wrap,
            };

            var close = new Button
            {
                // "Close", never "OK": the value was applied as it was typed, so there is nothing
                // here to accept and nothing to cancel.
                Content = "Close",
                MinWidth = 72.0,
                Margin = new Thickness(4.0),
                IsDefault = true,
                IsCancel = true,
            };

            close.Click += (sender, e) => Close();

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(8.0, 0.0, 8.0, 8.0),
            };

            buttons.Children.Add(close);

            var panel = new StackPanel();
            panel.Children.Add(_entry);
            panel.Children.Add(_note);
            panel.Children.Add(buttons);

            Content = panel;

            _value.Changed += OnValueChangedElsewhere;
            Closed += (sender, e) => _value.Changed -= OnValueChangedElsewhere;

            Loaded += (sender, e) =>
            {
                _entry.SelectAll();
                _entry.Focus();
            };

            PreviewKeyDown += (sender, e) =>
            {
                if (e.Key == Key.Escape)
                {
                    Close();
                }
            };
        }

        /// <summary>The value both this dialog and its hot spot edit.</summary>
        public IHotSpotValue Value => _value;

        /// <summary>The text currently in the entry field.</summary>
        /// <remarks>Setting it applies, exactly as typing it does.</remarks>
        public string EntryText
        {
            get { return _entry.Text; }
            set { _entry.Text = value ?? string.Empty; }
        }

        /// <summary>What the dialog is saying about the last entry, or empty if it took.</summary>
        public string Note => _note.Text;

        /// <summary>
        /// Applies the entry to the value.
        /// </summary>
        /// <returns><c>true</c> if the text was understood and the value changed.</returns>
        /// <remarks>
        /// Public because the behaviour has to be exercisable without a message pump, not because
        /// anything has to call it to commit: the text-changed handler already has.
        /// </remarks>
        public bool Apply()
        {
            bool changed = _value.TrySet(_entry.Text);

            _note.Text = _value.Understands(_entry.Text)
                ? string.Empty
                : "'" + _entry.Text + "' is not a value this setting understands.";

            return changed;
        }

        /// <summary>
        /// Opens the dialog over a hot spot, without blocking the measurement.
        /// </summary>
        /// <param name="owner">The owning window, or <c>null</c>.</param>
        /// <param name="spot">The hot spot being edited.</param>
        /// <returns>The dialog, or <c>null</c> if the hot spot has no value to edit.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="spot"/> is null.</exception>
        /// <remarks>
        /// Modeless: <c>REQ-UI-070</c> requires the measurement to keep updating and the main window
        /// to stay interactive — including the hot spots — while a setting dialog is open. The hot
        /// spot needs no refresh call here; it is following the same value this dialog is.
        /// </remarks>
        public static ValueEntryDialog Prompt(Window owner, HotSpot spot)
        {
            if (spot == null)
            {
                throw new ArgumentNullException(nameof(spot));
            }

            if (spot.Value == null)
            {
                return null;
            }

            var dialog = new ValueEntryDialog(spot.Label, spot.Value) { Owner = owner };

            dialog.Show();
            dialog.Activate();

            return dialog;
        }

        private void OnEntryChanged(object sender, TextChangedEventArgs e)
        {
            if (_updating)
            {
                return;
            }

            Apply();
        }

        private void OnValueChangedElsewhere(object sender, EventArgs e)
        {
            // Not while this field has the caret: the user is mid-entry, and replacing what they
            // are typing with the value their own last keystroke produced would fight them.
            if (_entry.IsKeyboardFocusWithin)
            {
                return;
            }

            _updating = true;

            try
            {
                _entry.Text = _value.Text;
                _note.Text = string.Empty;
            }
            finally
            {
                _updating = false;
            }
        }
    }
}
