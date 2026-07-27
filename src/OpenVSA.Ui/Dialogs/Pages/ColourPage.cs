using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using OpenVSA.Ui.Rendering;

namespace OpenVSA.Ui.Dialogs.Pages
{
    /// <summary>
    /// The Colour tab of Display Preferences: every themeable element (<c>REQ-UI-014</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The list is the element set, not a copy of it.</strong> Every entry comes from
    /// <see cref="ColourPreferences.Entries"/>, which is generated from <c>REQ-UI-022</c>'s
    /// enumeration — so an element added to that enumeration appears here without anyone
    /// remembering to add it, which is the requirement's criterion.
    /// </para>
    /// <para>
    /// <strong>Live, with no Apply.</strong> A colour takes effect as the slider moves
    /// (<c>REQ-UI-070</c>). Choosing a grid colour against a screenshot of the grid is guesswork;
    /// choosing it against the grid is not, and that is the whole argument for the dialogs being
    /// modeless in the first place.
    /// </para>
    /// <para>
    /// Built in code rather than XAML, as the other pages here are: a list whose contents are
    /// generated has nothing for a designer to lay out, and keeping it in one file means the
    /// enumeration and its presentation cannot be edited apart.
    /// </para>
    /// </remarks>
    public sealed class ColourPage : Grid
    {
        private readonly ColourPreferences _preferences;
        private readonly ListBox _elements;
        private readonly RgbEditor _editor;
        private readonly TextBox _filter;

        /// <summary>Creates the page over a set of preferences.</summary>
        /// <param name="preferences">The preferences to edit; changed in place.</param>
        /// <exception cref="ArgumentNullException"><paramref name="preferences"/> is null.</exception>
        public ColourPage(ColourPreferences preferences)
        {
            if (preferences == null)
            {
                throw new ArgumentNullException(nameof(preferences));
            }

            _preferences = preferences;

            Margin = new Thickness(4.0);
            MinWidth = 560.0;
            MinHeight = 340.0;

            // The list has several hundred entries and is meant to be scrolled. Without a ceiling
            // it asks for the height of all of them at once, and Fixed Size — which measures each
            // page unconstrained to find the largest — would hand the whole dialog that height.
            MaxHeight = 420.0;
            MaxWidth = 760.0;

            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.6, GridUnitType.Star) });
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10.0) });
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.0, GridUnitType.Star) });

            _filter = new TextBox { Margin = new Thickness(0.0, 0.0, 0.0, 6.0) };
            _filter.TextChanged += (sender, e) => Repopulate();

            _elements = new ListBox();
            _elements.SelectionChanged += (sender, e) => ShowColour(Selected);

            _editor = new RgbEditor();
            _editor.ColourChanged += OnColourEdited;

            var left = new DockPanel();
            DockPanel.SetDock(_filter, Dock.Top);
            left.Children.Add(_filter);
            left.Children.Add(_elements);

            SetColumn(left, 0);
            Children.Add(left);

            var right = new StackPanel();
            right.Children.Add(_editor);

            var reset = new Button
            {
                Content = "Reset to default",
                Margin = new Thickness(0.0, 12.0, 0.0, 0.0),
                Padding = new Thickness(8.0, 4.0, 8.0, 4.0),
            };

            reset.Click += OnResetOne;
            right.Children.Add(reset);

            var resetAll = new Button
            {
                Content = "Reset all colours",
                Margin = new Thickness(0.0, 6.0, 0.0, 0.0),
                Padding = new Thickness(8.0, 4.0, 8.0, 4.0),
            };

            resetAll.Click += OnResetAll;
            right.Children.Add(resetAll);

            SetColumn(right, 2);
            Children.Add(right);

            Repopulate();
        }

        /// <summary>The preferences this page edits.</summary>
        public ColourPreferences Preferences => _preferences;

        /// <summary>How many elements the list is currently showing.</summary>
        public int ListedCount => _elements.Items.Count;

        /// <summary>The text the list is filtered by.</summary>
        public string Filter
        {
            get { return _filter.Text; }
            set { _filter.Text = value ?? string.Empty; }
        }

        /// <summary>The element currently selected, or <c>null</c> if the filter matched nothing.</summary>
        public ColourEntry Selected => _elements.SelectedItem as ColourEntry;

        /// <summary>Selects an element by its resource key.</summary>
        /// <param name="key">The key.</param>
        /// <returns>Whether the list is showing an element with that key.</returns>
        public bool Select(string key)
        {
            foreach (object item in _elements.Items)
            {
                var entry = item as ColourEntry;

                if (entry != null && string.Equals(entry.Key, key, StringComparison.Ordinal))
                {
                    _elements.SelectedItem = entry;
                    return true;
                }
            }

            return false;
        }

        /// <summary>Raised whenever a colour changes, so the display can follow immediately.</summary>
        public event EventHandler ColoursChanged;

        private void Repopulate()
        {
            string filter = _filter.Text ?? string.Empty;

            var shown = new List<ColourEntry>();

            foreach (ColourEntry entry in _preferences.Entries)
            {
                if (filter.Length == 0 ||
                    entry.DisplayName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    shown.Add(entry);
                }
            }

            _elements.ItemsSource = shown;
            _elements.DisplayMemberPath = "DisplayName";

            if (shown.Count > 0)
            {
                _elements.SelectedIndex = 0;
            }
            else
            {
                ShowColour(null);
            }
        }

        private void ShowColour(ColourEntry entry)
        {
            if (entry == null)
            {
                _editor.Caption = string.Empty;
                return;
            }

            _editor.Colour = _preferences.Colour(entry.Key);
            Describe(entry);
        }

        private void Describe(ColourEntry entry) =>
            _editor.Caption = string.Format(
                CultureInfo.CurrentCulture,
                "{0}  —  {1}{2}",
                entry.Key,
                _editor.HexText,
                _preferences.IsChanged(entry.Key) ? "  (changed)" : string.Empty);

        private void OnColourEdited(object sender, EventArgs e)
        {
            ColourEntry entry = Selected;

            if (entry == null)
            {
                return;
            }

            _preferences.Set(entry.Key, _editor.Colour);
            Describe(entry);
            RaiseChanged();
        }

        private void OnResetOne(object sender, RoutedEventArgs e)
        {
            ColourEntry entry = Selected;

            if (entry == null)
            {
                return;
            }

            _preferences.Reset(entry.Key);
            ShowColour(entry);
            RaiseChanged();
        }

        private void OnResetAll(object sender, RoutedEventArgs e)
        {
            _preferences.ResetAll();
            ShowColour(Selected);
            RaiseChanged();
        }

        private void RaiseChanged()
        {
            EventHandler handler = ColoursChanged;

            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }
        }
    }
}
