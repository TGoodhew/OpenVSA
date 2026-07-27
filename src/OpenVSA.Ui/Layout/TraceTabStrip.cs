using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace OpenVSA.Ui.Layout
{
    /// <summary>
    /// The tab strip over a trace tab group (<c>REQ-UI-004</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The close button is to the left of every tab, and there is exactly one of it.</strong>
    /// This is unusual, it is deliberate, and the requirement flags it as the detail a developer
    /// will "correct" into the conventional right-hand position. So the criterion is asserted on
    /// <em>rendered geometry</em> — the button's bounds against the leftmost tab's bounds after a
    /// real arrange — rather than on a style name or a panel's Dock property, either of which can
    /// be right while the thing on screen is wrong.
    /// </para>
    /// <para>
    /// <strong>No per-tab close button.</strong> Also asserted, by walking the visual tree of every
    /// tab and finding no button in it. One close button closes the active trace; that is the whole
    /// model, and adding a per-tab one would make the left-hand button ambiguous.
    /// </para>
    /// <para>
    /// <strong>The active tab is bold, and it is the only one.</strong> Weight rather than colour,
    /// which is also what <c>REQ-UI-091</c> wants of any state worth showing: colour alone conveys
    /// nothing to a reader who cannot distinguish it.
    /// </para>
    /// </remarks>
    public sealed class TraceTabStrip : Control
    {
        private readonly Button _close;
        private readonly StackPanel _tabs;
        private readonly DockPanel _root;
        private readonly List<TextBlock> _labels = new List<TextBlock>();
        private readonly List<char> _traces = new List<char>();

        private char _active;

        /// <summary>Creates an empty strip.</summary>
        public TraceTabStrip()
        {
            _close = new Button
            {
                Name = "CloseTabGroup",
                Content = "✕",
                Width = 22.0,
                Height = 20.0,
                Margin = new Thickness(2.0, 2.0, 6.0, 2.0),
                ToolTip = "Close the active trace",
                Focusable = true,
            };

            _tabs = new StackPanel { Orientation = Orientation.Horizontal };

            _root = new DockPanel { LastChildFill = true };

            // Docked first, so it takes the left edge before the tabs get any of it. Order of
            // addition is what DockPanel honours, which is precisely why the geometry assertion
            // rather than this line is what the requirement is tested against.
            DockPanel.SetDock(_close, Dock.Left);
            _root.Children.Add(_close);
            _root.Children.Add(_tabs);

            AddVisualChild(_root);
            AddLogicalChild(_root);

            _close.Click += (sender, e) => RaiseCloseRequested();
        }

        /// <summary>Raised when the close button is pressed, naming the trace to close.</summary>
        public event EventHandler<char> CloseRequested;

        /// <summary>Raised when a tab is clicked, naming the trace it belongs to.</summary>
        public event EventHandler<char> TraceSelected;

        /// <summary>The one close button, which sits to the left of every tab.</summary>
        public FrameworkElement CloseButton => _close;

        /// <summary>The traces in this group, left to right.</summary>
        public IReadOnlyList<char> Traces => new ReadOnlyCollection<char>(_traces);

        /// <summary>
        /// The active trace, whose tab is the bold one.
        /// </summary>
        /// <exception cref="ArgumentException">That trace is not in this group.</exception>
        public char ActiveTrace
        {
            get { return _active; }

            set
            {
                if (!_traces.Contains(value))
                {
                    throw new ArgumentException(
                        "Trace " + value + " is not in this tab group.", nameof(value));
                }

                _active = value;
                ApplyWeights();
            }
        }

        /// <summary>
        /// Replaces the traces in this group.
        /// </summary>
        /// <param name="traces">The trace letters, left to right.</param>
        /// <param name="active">Which is active; the first if not in the list.</param>
        /// <exception cref="ArgumentNullException"><paramref name="traces"/> is null.</exception>
        public void SetTraces(IReadOnlyList<char> traces, char active)
        {
            if (traces == null)
            {
                throw new ArgumentNullException(nameof(traces));
            }

            _traces.Clear();
            _labels.Clear();
            _tabs.Children.Clear();

            foreach (char trace in traces)
            {
                _traces.Add(trace);

                var label = new TextBlock
                {
                    Text = "Trace " + trace,
                    Margin = new Thickness(10.0, 3.0, 10.0, 3.0),
                    VerticalAlignment = VerticalAlignment.Center,
                };

                var tab = new Border
                {
                    Name = "Tab" + trace,
                    Child = label,
                    BorderThickness = new Thickness(1.0),
                    Padding = new Thickness(0.0),
                };

                char captured = trace;
                tab.MouseLeftButtonDown += (sender, e) => Select(captured);

                _labels.Add(label);
                _tabs.Children.Add(tab);
            }

            _active = _traces.Contains(active)
                ? active
                : (_traces.Count > 0 ? _traces[0] : '\0');

            ApplyWeights();
            InvalidateMeasure();
        }

        /// <summary>
        /// The bounds of a trace's tab within this control, after arrangement.
        /// </summary>
        /// <param name="trace">The trace letter.</param>
        /// <returns>The tab's rectangle, or <see cref="Rect.Empty"/> if it has none.</returns>
        /// <exception cref="ArgumentException">That trace is not in this group.</exception>
        public Rect TabBounds(char trace)
        {
            int index = _traces.IndexOf(trace);

            if (index < 0)
            {
                throw new ArgumentException(
                    "Trace " + trace + " is not in this tab group.", nameof(trace));
            }

            return BoundsOf((FrameworkElement)_tabs.Children[index]);
        }

        /// <summary>The close button's bounds within this control, after arrangement.</summary>
        public Rect CloseButtonBounds => BoundsOf(_close);

        /// <summary>Whether a trace's tab is rendered bold.</summary>
        /// <param name="trace">The trace letter.</param>
        /// <exception cref="ArgumentException">That trace is not in this group.</exception>
        public bool IsBold(char trace)
        {
            int index = _traces.IndexOf(trace);

            if (index < 0)
            {
                throw new ArgumentException(
                    "Trace " + trace + " is not in this tab group.", nameof(trace));
            }

            return _labels[index].FontWeight == FontWeights.Bold;
        }

        /// <summary>
        /// Every button inside the tabs themselves, which must always be none.
        /// </summary>
        /// <remarks>
        /// The requirement's negative criterion, answerable rather than asserted by inspection:
        /// "no per-tab close button exists". A test walks this and expects an empty list.
        /// </remarks>
        public IReadOnlyList<Button> ButtonsInsideTabs()
        {
            var found = new List<Button>();

            foreach (UIElement child in _tabs.Children)
            {
                CollectButtons(child, found);
            }

            return new ReadOnlyCollection<Button>(found);
        }

        /// <summary>Selects a trace, as clicking its tab does.</summary>
        /// <param name="trace">The trace letter.</param>
        /// <exception cref="ArgumentException">That trace is not in this group.</exception>
        public void Select(char trace)
        {
            ActiveTrace = trace;

            EventHandler<char> handler = TraceSelected;

            if (handler != null)
            {
                handler(this, trace);
            }
        }

        /// <inheritdoc />
        protected override int VisualChildrenCount => 1;

        /// <inheritdoc />
        protected override Visual GetVisualChild(int index) => _root;

        /// <inheritdoc />
        protected override Size MeasureOverride(Size constraint)
        {
            _root.Measure(constraint);

            return _root.DesiredSize;
        }

        /// <inheritdoc />
        protected override Size ArrangeOverride(Size finalSize)
        {
            _root.Arrange(new Rect(new Point(0.0, 0.0), finalSize));

            return finalSize;
        }

        private void RaiseCloseRequested()
        {
            EventHandler<char> handler = CloseRequested;

            if (handler != null && _traces.Count > 0)
            {
                handler(this, _active);
            }
        }

        private void ApplyWeights()
        {
            for (int i = 0; i < _labels.Count; i++)
            {
                _labels[i].FontWeight =
                    _traces[i] == _active ? FontWeights.Bold : FontWeights.Normal;
            }
        }

        private Rect BoundsOf(FrameworkElement element)
        {
            if (element.ActualWidth <= 0.0 && element.ActualHeight <= 0.0)
            {
                return Rect.Empty;
            }

            GeneralTransform transform = element.TransformToAncestor(this);
            Point corner = transform.Transform(new Point(0.0, 0.0));

            return new Rect(corner, new Size(element.ActualWidth, element.ActualHeight));
        }

        private static void CollectButtons(DependencyObject node, List<Button> found)
        {
            var button = node as Button;

            if (button != null)
            {
                found.Add(button);
            }

            int children = VisualTreeHelper.GetChildrenCount(node);

            for (int i = 0; i < children; i++)
            {
                CollectButtons(VisualTreeHelper.GetChild(node, i), found);
            }
        }
    }
}
