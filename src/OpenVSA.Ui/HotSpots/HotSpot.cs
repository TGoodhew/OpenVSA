using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace OpenVSA.Ui.HotSpots
{
    /// <summary>
    /// A displayed measurement parameter that is editable where it is read
    /// (<c>REQ-UI-042</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This is the interaction the requirement calls the signature one.</strong> It
    /// collapses <em>find the menu, find the tab, find the field, apply, close</em> into a click on
    /// the number already on screen. The requirement is equally explicit that retrofitting it onto
    /// a finished plot surface is far more expensive than designing for it, which is why the
    /// annotation is real elements rather than rasterised glyphs.
    /// </para>
    /// <para>
    /// The four interactions are the reference product's, verbatim: hover underlines the value and
    /// gives a hand cursor; a single click arms live adjustment by wheel, arrow keys or typing; a
    /// double click asks for the data-entry dialog; a right click offers copy and paste.
    /// </para>
    /// <para>
    /// <strong>Every interaction has a method behind it as well as a handler.</strong> Synthesising
    /// real mouse input needs a live visual tree, a window and a message pump, none of which a unit
    /// test has — so the behaviour lives in <see cref="Enter"/>, <see cref="BeginEdit"/>,
    /// <see cref="Adjust"/> and the rest, and the input handlers do nothing but call them. What the
    /// tests exercise is then the same code the mouse reaches.
    /// </para>
    /// </remarks>
    public class HotSpot : TextBlock
    {
        private IHotSpotValue _value;
        private string _typed = string.Empty;

        /// <summary>Creates a hot spot.</summary>
        public HotSpot()
        {
            Focusable = true;
            IsHitTestVisible = true;

            // Without a background an element only hit-tests where a glyph actually is, so the
            // gaps between characters would fall through and the hover would flicker as the
            // pointer crossed them.
            Background = System.Windows.Media.Brushes.Transparent;

            ContextMenu = BuildContextMenu();

            MouseEnter += (sender, e) => Enter();
            MouseLeave += (sender, e) => Leave();
            MouseWheel += OnWheel;
            KeyDown += OnKeyDown;
            TextInput += OnTextInput;
            LostFocus += (sender, e) => EndEdit(commit: true);
            MouseLeftButtonDown += OnLeftButtonDown;
        }

        /// <summary>Raised after the value changes, however it was changed.</summary>
        public event EventHandler ValueChanged;

        /// <summary>Raised on a double click, for the host to open the data-entry dialog.</summary>
        /// <remarks>
        /// An event rather than a dialog opened here: the dialog belongs to the shell, and a
        /// rendering control that opened windows could not be exercised without one.
        /// </remarks>
        public event EventHandler DialogRequested;

        /// <summary>Text shown before the value, such as <c>RBW </c>. May be empty.</summary>
        public string Label { get; set; } = string.Empty;

        /// <summary>
        /// The quantity being edited.
        /// </summary>
        /// <remarks>Setting it refreshes the displayed text.</remarks>
        public IHotSpotValue Value
        {
            get { return _value; }

            set
            {
                _value = value;
                _typed = string.Empty;
                Refresh();
            }
        }

        /// <summary>Whether the pointer is over this hot spot.</summary>
        public bool IsHovered { get; private set; }

        /// <summary>Whether this hot spot is armed for live adjustment.</summary>
        public bool IsEditing { get; private set; }

        /// <summary>Characters typed since editing began, not yet committed.</summary>
        public string TypedText => _typed;

        /// <summary>
        /// Hover: underline the value and change the cursor to a hand.
        /// </summary>
        /// <remarks>
        /// Both, not either. The underline says <em>this text is a control</em> at a glance across
        /// the whole display; the cursor says it for the one the pointer is on. The requirement
        /// names them together and a user scanning for what is editable relies on the first.
        /// </remarks>
        public void Enter()
        {
            IsHovered = true;
            TextDecorations = System.Windows.TextDecorations.Underline;
            Cursor = Cursors.Hand;
        }

        /// <summary>Leaves hover, restoring the plain text and the arrow.</summary>
        public void Leave()
        {
            IsHovered = false;

            if (!IsEditing)
            {
                TextDecorations = null;
                Cursor = null;
            }
        }

        /// <summary>
        /// Single click: arm live adjustment by wheel, arrow keys or typing.
        /// </summary>
        /// <remarks>
        /// The cursor becomes a vertical scroll arrow rather than anything resembling a keypad,
        /// because WPF has no keypad cursor and the vertical arrows say what the wheel and the up
        /// and down keys are about to do — which is the part of "indicate a numeric entry pad" that
        /// carries the meaning.
        /// </remarks>
        public void BeginEdit()
        {
            if (_value == null)
            {
                return;
            }

            IsEditing = true;
            _typed = string.Empty;
            TextDecorations = System.Windows.TextDecorations.Underline;
            Cursor = Cursors.ScrollNS;
            Focus();
        }

        /// <summary>
        /// Ends live adjustment.
        /// </summary>
        /// <param name="commit">Whether to apply any partially typed entry.</param>
        public void EndEdit(bool commit)
        {
            if (!IsEditing)
            {
                return;
            }

            if (commit)
            {
                CommitTyped();
            }

            IsEditing = false;
            _typed = string.Empty;
            Cursor = IsHovered ? Cursors.Hand : null;
            TextDecorations = IsHovered ? System.Windows.TextDecorations.Underline : null;
            Refresh();
        }

        /// <summary>
        /// Moves the value, as a wheel notch or an arrow key does.
        /// </summary>
        /// <param name="steps">Steps to move; negative moves down.</param>
        /// <returns><c>true</c> if the value changed.</returns>
        /// <remarks>
        /// Deliberately not conditional on <see cref="IsEditing"/>. The requirement says the wheel
        /// and the arrow keys adjust <em>the hovered value</em> without opening a dialog, so
        /// requiring a click first would put back the round-trip the whole feature exists to remove.
        /// </remarks>
        public bool Adjust(int steps)
        {
            if (_value == null || !_value.TryAdjust(steps))
            {
                return false;
            }

            Refresh();
            RaiseValueChanged();
            return true;
        }

        /// <summary>
        /// Accepts a typed character into the pending entry.
        /// </summary>
        /// <param name="character">The character.</param>
        /// <returns><c>true</c> if it was accepted.</returns>
        /// <remarks>
        /// Typing starts an entry whether or not the hot spot was clicked first, and the display
        /// shows what has been typed so far rather than the old value — otherwise there is nothing
        /// on screen to say an entry is in progress.
        /// </remarks>
        public bool Type(char character)
        {
            if (_value == null || character == '\b')
            {
                return Backspace();
            }

            if (char.IsControl(character))
            {
                return false;
            }

            IsEditing = true;
            _typed += character;
            Text = Label + _typed;
            return true;
        }

        /// <summary>Removes the last typed character.</summary>
        /// <returns><c>true</c> if there was one.</returns>
        public bool Backspace()
        {
            if (_typed.Length == 0)
            {
                return false;
            }

            _typed = _typed.Substring(0, _typed.Length - 1);
            Text = Label + _typed;
            return true;
        }

        /// <summary>
        /// Applies the pending typed entry.
        /// </summary>
        /// <returns><c>true</c> if it was understood and changed the value.</returns>
        /// <remarks>
        /// An entry that is not understood is discarded and the previous value shown again, rather
        /// than left on screen looking like a setting that took effect.
        /// </remarks>
        public bool CommitTyped()
        {
            if (_typed.Length == 0 || _value == null)
            {
                Refresh();
                return false;
            }

            bool changed = _value.TrySet(_typed);
            _typed = string.Empty;
            Refresh();

            if (changed)
            {
                RaiseValueChanged();
            }

            return changed;
        }

        /// <summary>Double click: ask the host for the data-entry dialog.</summary>
        public void RequestDialog()
        {
            EventHandler handler = DialogRequested;

            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }
        }

        /// <summary>
        /// Copies the value to the clipboard.
        /// </summary>
        /// <returns><c>true</c> if the clipboard accepted it.</returns>
        /// <remarks>
        /// The clipboard is owned by the session, not by the process, and another application
        /// holding it open makes this fail for a moment. A copy that throws would take the
        /// measurement down with it, so the failure is reported instead.
        /// </remarks>
        public bool Copy()
        {
            if (_value == null)
            {
                return false;
            }

            try
            {
                Clipboard.SetText(_value.Text);
                return true;
            }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                return false;
            }
        }

        /// <summary>Sets the value from the clipboard.</summary>
        /// <returns><c>true</c> if the clipboard held something this understood.</returns>
        public bool Paste()
        {
            if (_value == null)
            {
                return false;
            }

            string text;

            try
            {
                text = Clipboard.ContainsText() ? Clipboard.GetText() : null;
            }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                return false;
            }

            if (string.IsNullOrEmpty(text) || !_value.TrySet(text))
            {
                return false;
            }

            Refresh();
            RaiseValueChanged();
            return true;
        }

        /// <summary>Redisplays the value, discarding any partial entry.</summary>
        public void Refresh() =>
            Text = _value == null ? Label.TrimEnd() : Label + _value.Text;

        private ContextMenu BuildContextMenu()
        {
            var copy = new MenuItem { Header = "Copy" };
            copy.Click += (sender, e) => Copy();

            var paste = new MenuItem { Header = "Paste" };
            paste.Click += (sender, e) => Paste();

            var menu = new ContextMenu();
            menu.Items.Add(copy);
            menu.Items.Add(paste);
            return menu;
        }

        private void OnLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount >= 2)
            {
                RequestDialog();
            }
            else
            {
                BeginEdit();
            }

            e.Handled = true;
        }

        private void OnWheel(object sender, MouseWheelEventArgs e)
        {
            int notches = e.Delta / 120;

            if (notches == 0)
            {
                notches = e.Delta > 0 ? 1 : -1;
            }

            if (Adjust(notches))
            {
                e.Handled = true;
            }
        }

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Up:
                case Key.Right:
                    e.Handled = Adjust(1);
                    break;

                case Key.Down:
                case Key.Left:
                    e.Handled = Adjust(-1);
                    break;

                case Key.Enter:
                    CommitTyped();
                    EndEdit(commit: false);
                    e.Handled = true;
                    break;

                case Key.Escape:
                    EndEdit(commit: false);
                    e.Handled = true;
                    break;

                case Key.Back:
                    e.Handled = Backspace();
                    break;
            }
        }

        private void OnTextInput(object sender, TextCompositionEventArgs e)
        {
            string typed = e.Text;

            if (string.IsNullOrEmpty(typed))
            {
                return;
            }

            for (int i = 0; i < typed.Length; i++)
            {
                Type(typed[i]);
            }

            e.Handled = true;
        }

        private void RaiseValueChanged()
        {
            EventHandler handler = ValueChanged;

            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }
        }

        /// <inheritdoc />
        public override string ToString() =>
            string.Format(
                CultureInfo.InvariantCulture,
                "hot spot '{0}' showing '{1}'",
                Label.Trim(),
                _value == null ? "(nothing)" : _value.Text);
    }
}
