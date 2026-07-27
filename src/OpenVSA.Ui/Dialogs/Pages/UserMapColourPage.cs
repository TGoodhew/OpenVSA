using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using OpenVSA.Ui.Rendering;

namespace OpenVSA.Ui.Dialogs.Pages
{
    /// <summary>
    /// The User Map Colour tab of Display Preferences: the user-defined spectrogram map
    /// (<c>REQ-UI-024</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Index 0 is the minimum, and the count discards from the top.</strong> Both are the
    /// requirement's, and the second is the surprising one: reducing the entry count throws away the
    /// <em>highest</em> colours, so what the spectrogram's floor renders as never moves. A page that
    /// trimmed from the bottom would recolour the floor every time the count changed, and a
    /// spectrogram whose floor shifts under it cannot be read.
    /// </para>
    /// <para>
    /// The map is edited as a full 64-entry table with a count over it, rather than as a handful of
    /// control points interpolated between. That is what <c>REQ-UI-024</c> describes and what
    /// <see cref="SpectrogramColourMap.At"/> implements — the map <em>is</em> the quantisation.
    /// </para>
    /// <para>
    /// A new user map starts as a copy of the built-in map currently in force rather than as 64
    /// blacks: a user reaching for this tab wants to adjust the colouring they are looking at, and
    /// starting from nothing would mean rebuilding it before they could begin.
    /// </para>
    /// </remarks>
    public sealed class UserMapColourPage : Grid
    {
        private readonly List<PlotColor> _entries = new List<PlotColor>();
        private readonly ListBox _list;
        private readonly RgbEditor _editor;
        private readonly ComboBox _count;
        private readonly TextBlock _summary;

        private bool _updating;

        /// <summary>Creates the page, seeded from a map.</summary>
        /// <param name="seed">The map to start from.</param>
        /// <exception cref="ArgumentNullException"><paramref name="seed"/> is null.</exception>
        public UserMapColourPage(SpectrogramColourMap seed)
        {
            if (seed == null)
            {
                throw new ArgumentNullException(nameof(seed));
            }

            Margin = new Thickness(4.0);
            MinWidth = 560.0;
            MinHeight = 340.0;

            // As on the Colour tab: sixty-four entries are for scrolling through, not for making
            // the dialog sixty-four rows tall.
            MaxHeight = 420.0;
            MaxWidth = 760.0;

            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.0, GridUnitType.Star) });
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10.0) });
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.0, GridUnitType.Star) });

            _list = new ListBox();
            _list.SelectionChanged += (sender, e) => ShowSelected();

            _count = new ComboBox { Margin = new Thickness(0.0, 0.0, 0.0, 6.0) };

            for (int entries = 2; entries <= SpectrogramColourMap.StandardEntryCount; entries++)
            {
                _count.Items.Add(entries);
            }

            _count.SelectionChanged += OnCountChosen;

            _summary = new TextBlock
            {
                Margin = new Thickness(0.0, 0.0, 0.0, 6.0),
                TextWrapping = TextWrapping.Wrap,
            };

            var left = new DockPanel();

            var heading = new TextBlock
            {
                Text = "Entries — 0 is the minimum",
                Margin = new Thickness(0.0, 0.0, 0.0, 4.0),
            };

            DockPanel.SetDock(heading, Dock.Top);
            DockPanel.SetDock(_summary, Dock.Top);
            left.Children.Add(heading);
            left.Children.Add(_summary);
            left.Children.Add(_list);

            SetColumn(left, 0);
            Children.Add(left);

            _editor = new RgbEditor();
            _editor.ColourChanged += OnColourEdited;

            var right = new StackPanel();
            right.Children.Add(new TextBlock
            {
                Text = "Entries kept (the rest are discarded from the top)",
                Margin = new Thickness(0.0, 0.0, 0.0, 4.0),
                TextWrapping = TextWrapping.Wrap,
            });

            right.Children.Add(_count);
            right.Children.Add(_editor);

            var reset = new Button
            {
                Content = "Start again from the built-in map",
                Margin = new Thickness(0.0, 12.0, 0.0, 0.0),
                Padding = new Thickness(8.0, 4.0, 8.0, 4.0),
            };

            reset.Click += (sender, e) => Seed(SpectrogramColourMap.Default);
            right.Children.Add(reset);

            SetColumn(right, 2);
            Children.Add(right);

            Seed(seed);
        }

        /// <summary>Raised whenever the map changes, so the display can follow immediately.</summary>
        public event EventHandler MapChanged;

        /// <summary>The map as edited: a user-defined map, kept to the chosen count.</summary>
        public SpectrogramColourMap Map =>
            SpectrogramColourMap.User(_entries).WithCount(Count);

        /// <summary>How many entries are kept.</summary>
        public int Count
        {
            get
            {
                object chosen = _count.SelectedItem;
                return chosen == null ? _entries.Count : (int)chosen;
            }

            set
            {
                if (value < 2 || value > _entries.Count)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(value), value,
                        "A map keeps between 2 and " + _entries.Count + " of its entries.");
                }

                _count.SelectedItem = value;
            }
        }

        /// <summary>The entry currently being edited, by index, or −1 if none is.</summary>
        public int SelectedIndex
        {
            get { return _list.SelectedIndex; }

            set
            {
                if (value < -1 || value >= _list.Items.Count)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(value), value, "There is no entry at that position.");
                }

                _list.SelectedIndex = value;
            }
        }

        /// <summary>The colour of one entry.</summary>
        /// <param name="index">The entry's index; 0 is the minimum.</param>
        /// <exception cref="ArgumentOutOfRangeException">There is no entry at that index.</exception>
        public PlotColor EntryAt(int index)
        {
            if (index < 0 || index >= _entries.Count)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(index), index, "There is no entry at that index.");
            }

            return _entries[index];
        }

        /// <summary>
        /// Replaces the whole table from an existing map.
        /// </summary>
        /// <param name="map">The map to copy.</param>
        /// <exception cref="ArgumentNullException"><paramref name="map"/> is null.</exception>
        public void Seed(SpectrogramColourMap map)
        {
            if (map == null)
            {
                throw new ArgumentNullException(nameof(map));
            }

            _updating = true;

            try
            {
                _entries.Clear();

                foreach (PlotColor colour in map.Entries)
                {
                    _entries.Add(colour);
                }

                Repopulate();
                _count.SelectedItem = _entries.Count;
                _list.SelectedIndex = 0;
            }
            finally
            {
                _updating = false;
            }

            RaiseChanged();
        }

        private void Repopulate()
        {
            var labels = new List<string>(_entries.Count);

            for (int i = 0; i < _entries.Count; i++)
            {
                PlotColor colour = _entries[i];

                labels.Add(string.Format(
                    CultureInfo.CurrentCulture,
                    "{0,2}  #{1:X2}{2:X2}{3:X2}",
                    i,
                    colour.R,
                    colour.G,
                    colour.B));
            }

            int was = _list.SelectedIndex;

            _list.ItemsSource = labels;
            _list.SelectedIndex = was >= 0 && was < labels.Count ? was : 0;

            Summarise();
        }

        private void Summarise() =>
            _summary.Text = string.Format(
                CultureInfo.CurrentCulture,
                "{0} entries, {1} kept.",
                _entries.Count,
                Count);

        private void ShowSelected()
        {
            int index = _list.SelectedIndex;

            if (index < 0 || index >= _entries.Count)
            {
                return;
            }

            _updating = true;

            try
            {
                _editor.Colour = _entries[index];
                _editor.Caption = "Entry " + index.ToString(CultureInfo.CurrentCulture) +
                    (index >= Count ? " — discarded at this count" : string.Empty);
            }
            finally
            {
                _updating = false;
            }
        }

        private void OnColourEdited(object sender, EventArgs e)
        {
            int index = _list.SelectedIndex;

            if (_updating || index < 0 || index >= _entries.Count)
            {
                return;
            }

            _entries[index] = _editor.Colour;

            _updating = true;

            try
            {
                Repopulate();
                _list.SelectedIndex = index;
            }
            finally
            {
                _updating = false;
            }

            RaiseChanged();
        }

        private void OnCountChosen(object sender, SelectionChangedEventArgs e)
        {
            Summarise();

            if (!_updating)
            {
                RaiseChanged();
            }
        }

        private void RaiseChanged()
        {
            EventHandler handler = MapChanged;

            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }
        }
    }
}
