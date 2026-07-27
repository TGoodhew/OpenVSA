using System;
using System.Windows;
using System.Windows.Controls;
using OpenVSA.Ui.Rendering;

namespace OpenVSA.Ui.Dialogs.Pages
{
    /// <summary>
    /// The Trace tab of Display Preferences (<c>REQ-UI-073</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The display preferences that are about a trace window rather than about a colour, a typeface
    /// or a window: whether annotation and grid lines are drawn (<c>REQ-UI-011</c>), how many
    /// graticule divisions there are (<c>REQ-UI-012</c>), where the X reference sits
    /// (<c>REQ-UI-013</c>), how limit results are indicated (<c>REQ-UI-023</c>) and whether printing
    /// forces a white background (<c>REQ-UI-015</c>).
    /// </para>
    /// <para>
    /// <strong>Several of these have a menu item as well.</strong> Both surfaces read and write
    /// <see cref="TraceDisplayOptions"/> and both follow its <c>Changed</c> event, so a change made
    /// on either is visible on the other immediately — <c>REQ-UI-070</c>'s third criterion, in a
    /// place where it can be seen without a measurement running.
    /// </para>
    /// </remarks>
    public sealed class TracePage : StackPanel
    {
        private readonly TraceDisplayOptions _options;
        private readonly CheckBox _failures;
        private readonly CheckBox _margins;
        private readonly CheckBox _whiteBackground;
        private readonly CheckBox _annotation;
        private readonly CheckBox _gridLines;
        private readonly ComboBox _horizontal;
        private readonly ComboBox _vertical;
        private readonly ComboBox _xReference;

        private bool _updating;

        /// <summary>Creates the page over the trace display options.</summary>
        /// <param name="options">The options to edit; changed in place.</param>
        /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
        public TracePage(TraceDisplayOptions options)
        {
            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            _options = options;

            Margin = new Thickness(4.0);
            MinWidth = 420.0;

            Children.Add(new TextBlock
            {
                Text = "How trace windows draw results.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0.0, 0.0, 0.0, 10.0),
            });

            _annotation = Check(
                "Show annotation — the graticule expands when it is off",
                on => _options.ShowAnnotation = on);

            _gridLines = Check(
                "Show grid lines",
                on => _options.ShowGridLines = on);

            Children.Add(new TextBlock
            {
                Text = "Graticule divisions",
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0.0, 6.0, 0.0, 4.0),
            });

            var divisions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0.0, 0.0, 0.0, 10.0),
            };

            _horizontal = Divisions(v => _options.HorizontalDivisions = v);
            _vertical = Divisions(v => _options.VerticalDivisions = v);

            divisions.Children.Add(Caption("Across"));
            divisions.Children.Add(_horizontal);
            divisions.Children.Add(Caption("Down"));
            divisions.Children.Add(_vertical);

            Children.Add(divisions);

            Children.Add(new TextBlock
            {
                Text = "X reference position",
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0.0, 0.0, 0.0, 4.0),
            });

            var reference = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0.0, 0.0, 0.0, 10.0),
            };

            _xReference = new ComboBox { MinWidth = 80.0 };

            for (int percent = ReferencePosition.MinimumPercent;
                 percent <= ReferencePosition.MaximumPercent;
                 percent++)
            {
                _xReference.Items.Add(percent);
            }

            _xReference.SelectionChanged += (sender, e) =>
            {
                if (!_updating && _xReference.SelectedItem != null)
                {
                    _options.XReferencePercent = (int)_xReference.SelectedItem;
                }
            };

            reference.Children.Add(_xReference);
            reference.Children.Add(Caption("% from the left edge"));
            Children.Add(reference);

            Children.Add(new TextBlock
            {
                Text = "The Y reference position defaults per format — 100 % for the magnitude " +
                       "formats and 50 % for the rest — and follows the format rather than being " +
                       "set here.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0.0, 0.0, 0.0, 10.0),
            });

            _failures = Check(
                "Indicate limit failures — recolour the trace where it fails",
                on => _options.IndicateLimitFailures = on);

            _margins = Check(
                "Indicate margin warnings — recolour the trace inside the margin",
                on => _options.IndicateMarginWarnings = on);

            _whiteBackground = Check(
                "Force a white background when printing",
                on => _options.ForceWhiteBackgroundOnPrint = on);

            Children.Add(new TextBlock
            {
                Text = "Printing on white also darkens the light trace colours, so a trace drawn " +
                       "for a dark display is still legible on paper.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(20.0, 0.0, 0.0, 0.0),
            });

            options.Changed += OnOptionsChanged;
            Unloaded += (sender, e) => options.Changed -= OnOptionsChanged;

            Refresh();
        }

        /// <summary>The options this page edits.</summary>
        public TraceDisplayOptions Options => _options;

        private static TextBlock Caption(string text) => new TextBlock
        {
            Text = text,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0.0, 0.0, 6.0, 0.0),
        };

        private ComboBox Divisions(Action<int> set)
        {
            var box = new ComboBox { MinWidth = 70.0, Margin = new Thickness(0.0, 0.0, 12.0, 0.0) };

            for (int count = TraceDisplayOptions.MinimumDivisions;
                 count <= TraceDisplayOptions.MaximumDivisions;
                 count++)
            {
                box.Items.Add(count);
            }

            box.SelectionChanged += (sender, e) =>
            {
                if (!_updating && box.SelectedItem != null)
                {
                    set((int)box.SelectedItem);
                }
            };

            return box;
        }

        private CheckBox Check(string caption, Action<bool> set)
        {
            var box = new CheckBox
            {
                Content = caption,
                Margin = new Thickness(0.0, 0.0, 0.0, 8.0),
            };

            box.Checked += (sender, e) => Apply(set, true);
            box.Unchecked += (sender, e) => Apply(set, false);

            Children.Add(box);
            return box;
        }

        private void Apply(Action<bool> set, bool value)
        {
            if (!_updating)
            {
                set(value);
            }
        }

        private void OnOptionsChanged(object sender, EventArgs e) => Refresh();

        private void Refresh()
        {
            _updating = true;

            try
            {
                _failures.IsChecked = _options.IndicateLimitFailures;
                _margins.IsChecked = _options.IndicateMarginWarnings;
                _whiteBackground.IsChecked = _options.ForceWhiteBackgroundOnPrint;
                _annotation.IsChecked = _options.ShowAnnotation;
                _gridLines.IsChecked = _options.ShowGridLines;
                _horizontal.SelectedItem = _options.HorizontalDivisions;
                _vertical.SelectedItem = _options.VerticalDivisions;
                _xReference.SelectedItem = _options.XReferencePercent;
            }
            finally
            {
                _updating = false;
            }
        }
    }
}
