using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OpenVSA.Ui.Rendering;

namespace OpenVSA.Ui.Dialogs.Pages
{
    /// <summary>
    /// A swatch and three sliders, for choosing one colour.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Red, green and blue values rather than a colour wheel. A user matching a house style or a
    /// printed report has the numbers, and a wheel makes them unenterable; the swatch beside the
    /// sliders is what serves the user who is choosing by eye.
    /// </para>
    /// <para>
    /// Shared by the Colour and User Map Colour tabs, which is not a saving of a dozen lines but of
    /// a divergence: two colour editors in one dialog that disagreed about whether a slider commits
    /// on release or on movement would look like a bug in one of them.
    /// </para>
    /// </remarks>
    public sealed class RgbEditor : StackPanel
    {
        private readonly Slider _red;
        private readonly Slider _green;
        private readonly Slider _blue;
        private readonly Border _swatch;
        private readonly TextBlock _caption;

        private PlotColor _colour;
        private bool _updating;

        /// <summary>Creates the editor.</summary>
        public RgbEditor()
        {
            _swatch = new Border
            {
                Height = 56.0,
                BorderThickness = new Thickness(1.0),
                BorderBrush = Brushes.Gray,
                Margin = new Thickness(0.0, 0.0, 0.0, 8.0),
            };

            _caption = new TextBlock
            {
                Margin = new Thickness(0.0, 0.0, 0.0, 8.0),
                TextWrapping = TextWrapping.Wrap,
            };

            _red = NewSlider();
            _green = NewSlider();
            _blue = NewSlider();

            Children.Add(_swatch);
            Children.Add(_caption);
            Children.Add(Labelled("Red", _red));
            Children.Add(Labelled("Green", _green));
            Children.Add(Labelled("Blue", _blue));

            _red.ValueChanged += OnComponentChanged;
            _green.ValueChanged += OnComponentChanged;
            _blue.ValueChanged += OnComponentChanged;

            Paint();
        }

        /// <summary>Raised when the user moves a slider, never when the colour is set in code.</summary>
        /// <remarks>
        /// The distinction matters: filling the editor from a newly selected element would otherwise
        /// count as a change, and merely clicking down a list would record every entry as altered.
        /// </remarks>
        public event EventHandler ColourChanged;

        /// <summary>The colour shown.</summary>
        public PlotColor Colour
        {
            get { return _colour; }

            set
            {
                _updating = true;

                try
                {
                    _colour = value;
                    _red.Value = value.R;
                    _green.Value = value.G;
                    _blue.Value = value.B;
                    Paint();
                }
                finally
                {
                    _updating = false;
                }
            }
        }

        /// <summary>Text shown under the swatch — which element, and whether it has been changed.</summary>
        public string Caption
        {
            get { return _caption.Text; }
            set { _caption.Text = value ?? string.Empty; }
        }

        /// <summary>The colour written as it appears in a theme file.</summary>
        public string HexText =>
            string.Format(
                CultureInfo.InvariantCulture,
                "#{0:X2}{1:X2}{2:X2}",
                _colour.R,
                _colour.G,
                _colour.B);

        private static Slider NewSlider() => new Slider
        {
            Minimum = 0.0,
            Maximum = 255.0,
            SmallChange = 1.0,
            LargeChange = 16.0,
            IsSnapToTickEnabled = true,
            TickFrequency = 1.0,
        };

        private static UIElement Labelled(string label, Slider slider)
        {
            var panel = new StackPanel { Margin = new Thickness(0.0, 0.0, 0.0, 4.0) };
            panel.Children.Add(new TextBlock { Text = label });
            panel.Children.Add(slider);
            return panel;
        }

        private void Paint() =>
            _swatch.Background = new SolidColorBrush(
                Color.FromRgb(_colour.R, _colour.G, _colour.B));

        private void OnComponentChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_updating)
            {
                return;
            }

            _colour = new PlotColor(
                (byte)Math.Round(_red.Value),
                (byte)Math.Round(_green.Value),
                (byte)Math.Round(_blue.Value));

            Paint();

            EventHandler handler = ColourChanged;

            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }
        }
    }
}
