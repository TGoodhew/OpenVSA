using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OpenVSA.Ui.Rendering;

namespace OpenVSA.Ui
{
    /// <summary>
    /// The colour picker of <c>REQ-UI-014</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The list is the element set, not a copy of it.</strong> Every entry comes from
    /// <see cref="ColourPreferences.Entries"/>, which is generated from <c>REQ-UI-022</c>'s
    /// enumeration — so an element added to that enumeration appears here without anyone
    /// remembering to add it, which is the requirement's criterion.
    /// </para>
    /// <para>
    /// Built in code rather than XAML, as the other dialogs here are: a list whose contents are
    /// generated has nothing for a designer to lay out, and keeping it in one file means the
    /// enumeration and its presentation cannot be edited apart.
    /// </para>
    /// <para>
    /// The colour itself is entered as red, green and blue values rather than through a colour
    /// wheel. A user matching a house style or a printed report has the numbers, and a wheel makes
    /// them unenterable; the swatch beside the sliders is what serves the user who is choosing by
    /// eye.
    /// </para>
    /// </remarks>
    public sealed class ColourPickerDialog : Window
    {
        private readonly ColourPreferences _preferences;
        private readonly ListBox _elements;
        private readonly Slider _red;
        private readonly Slider _green;
        private readonly Slider _blue;
        private readonly Border _swatch;
        private readonly TextBlock _value;
        private readonly TextBox _filter;

        private bool _updating;

        /// <summary>Creates the dialog over a set of preferences.</summary>
        /// <param name="preferences">The preferences to edit; changed in place.</param>
        /// <exception cref="ArgumentNullException"><paramref name="preferences"/> is null.</exception>
        public ColourPickerDialog(ColourPreferences preferences)
        {
            if (preferences == null)
            {
                throw new ArgumentNullException(nameof(preferences));
            }

            _preferences = preferences;

            Title = "Colours";
            Width = 620.0;
            Height = 480.0;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;

            _filter = new TextBox { Margin = new Thickness(0.0, 0.0, 0.0, 6.0) };
            _filter.TextChanged += (s, e) => Repopulate();

            _elements = new ListBox();
            _elements.SelectionChanged += OnElementChanged;

            _red = NewSlider();
            _green = NewSlider();
            _blue = NewSlider();

            _swatch = new Border
            {
                Height = 56.0,
                BorderThickness = new Thickness(1.0),
                BorderBrush = Brushes.Gray,
                Margin = new Thickness(0.0, 0.0, 0.0, 8.0),
            };

            _value = new TextBlock { Margin = new Thickness(0.0, 0.0, 0.0, 8.0) };

            Content = BuildLayout();
            Repopulate();
        }

        /// <summary>How many elements the list is currently showing.</summary>
        public int ListedCount => _elements.Items.Count;

        private static Slider NewSlider() => new Slider
        {
            Minimum = 0.0,
            Maximum = 255.0,
            SmallChange = 1.0,
            LargeChange = 16.0,
            IsSnapToTickEnabled = true,
            TickFrequency = 1.0,
        };

        private object BuildLayout()
        {
            var grid = new Grid { Margin = new Thickness(10.0) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.6, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10.0) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.0, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition());
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var left = new DockPanel();
            DockPanel.SetDock(_filter, Dock.Top);
            left.Children.Add(_filter);
            left.Children.Add(_elements);

            Grid.SetColumn(left, 0);
            grid.Children.Add(left);

            var right = new StackPanel();
            right.Children.Add(_swatch);
            right.Children.Add(_value);
            right.Children.Add(Labelled("Red", _red));
            right.Children.Add(Labelled("Green", _green));
            right.Children.Add(Labelled("Blue", _blue));

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

            Grid.SetColumn(right, 2);
            grid.Children.Add(right);

            var close = new Button
            {
                Content = "Close",
                Width = 90.0,
                Padding = new Thickness(8.0, 4.0, 8.0, 4.0),
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0.0, 10.0, 0.0, 0.0),
                IsDefault = true,
                IsCancel = true,
            };
            close.Click += (s, e) => Close();

            Grid.SetRow(close, 1);
            Grid.SetColumnSpan(close, 3);
            grid.Children.Add(close);

            return grid;
        }

        private static UIElement Labelled(string label, Slider slider)
        {
            var panel = new StackPanel { Margin = new Thickness(0.0, 0.0, 0.0, 4.0) };
            panel.Children.Add(new TextBlock { Text = label });
            panel.Children.Add(slider);
            return panel;
        }

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

        private ColourEntry Selected => _elements.SelectedItem as ColourEntry;

        private void OnElementChanged(object sender, SelectionChangedEventArgs e) =>
            ShowColour(Selected);

        private void ShowColour(ColourEntry entry)
        {
            _updating = true;

            try
            {
                if (entry == null)
                {
                    _swatch.Background = Brushes.Transparent;
                    _value.Text = string.Empty;
                    return;
                }

                PlotColor colour = _preferences.Colour(entry.Key);

                _red.Value = colour.R;
                _green.Value = colour.G;
                _blue.Value = colour.B;

                Paint(colour, entry);
            }
            finally
            {
                _updating = false;

                // Attached after the first fill, so loading a selection does not count as a change.
                _red.ValueChanged -= OnComponentChanged;
                _green.ValueChanged -= OnComponentChanged;
                _blue.ValueChanged -= OnComponentChanged;

                _red.ValueChanged += OnComponentChanged;
                _green.ValueChanged += OnComponentChanged;
                _blue.ValueChanged += OnComponentChanged;
            }
        }

        private void Paint(PlotColor colour, ColourEntry entry)
        {
            _swatch.Background = new SolidColorBrush(
                Color.FromRgb(colour.R, colour.G, colour.B));

            _value.Text = string.Format(
                CultureInfo.CurrentCulture,
                "{0}  —  #{1:X2}{2:X2}{3:X2}{4}",
                entry.Key,
                colour.R,
                colour.G,
                colour.B,
                _preferences.IsChanged(entry.Key) ? "  (changed)" : string.Empty);
        }

        private void OnComponentChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_updating)
            {
                return;
            }

            ColourEntry entry = Selected;

            if (entry == null)
            {
                return;
            }

            var colour = new PlotColor(
                (byte)Math.Round(_red.Value),
                (byte)Math.Round(_green.Value),
                (byte)Math.Round(_blue.Value));

            _preferences.Set(entry.Key, colour);
            Paint(colour, entry);
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

        /// <summary>
        /// Raised whenever a colour changes, so the display can follow immediately.
        /// </summary>
        /// <remarks>
        /// Live rather than on OK. Choosing a grid colour against a screenshot of the grid is
        /// guesswork; choosing it against the grid is not.
        /// </remarks>
        public event EventHandler ColoursChanged;
    }
}
