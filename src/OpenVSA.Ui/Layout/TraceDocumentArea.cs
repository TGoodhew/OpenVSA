using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using OpenVSA.Ui.Rendering;

namespace OpenVSA.Ui.Layout
{
    /// <summary>
    /// The document area: trace windows arranged by <see cref="TraceLayoutEngine"/>, with tab
    /// strips and draggable boundaries (<c>REQ-UI-001</c>, <c>REQ-UI-004</c>, <c>REQ-UI-005</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Absolutely positioned from the slot model.</strong> The layout engine already
    /// produces pixel rectangles that tile the area exactly, so the control places each cell at the
    /// rectangle it was given rather than re-deriving the arrangement through nested grids. That is
    /// what makes what is on screen the same thing the layout tests assert about — a second
    /// arrangement expressed in <c>Grid</c> rows and <c>*</c> widths would be a second
    /// implementation to keep in agreement.
    /// </para>
    /// <para>
    /// <strong>The first trace can adopt an existing plot.</strong> The shell's measurement
    /// pipeline, hot spots, markers and frame handler are all wired to one
    /// <see cref="TracePlot"/>; handing that same instance in as trace A keeps every one of them
    /// working while the rest of the traces get plots of their own. Rebuilding that wiring for the
    /// sake of symmetry would be a large change with nothing to show for it.
    /// </para>
    /// <para>
    /// <strong>Splitters exist only where a boundary is draggable.</strong> One is created between
    /// each vertically adjacent pair that shares an edge, and dragging it calls
    /// <see cref="TraceLayoutEngine.DragBoundary"/> — so the on-screen gesture and the tested model
    /// are the same code, and the drag records a Custom layout exactly as
    /// <c>REQ-UI-005</c>'s Previous Layout expects.
    /// </para>
    /// </remarks>
    public sealed class TraceDocumentArea : Control
    {
        /// <summary>Thickness of a draggable boundary, in device-independent pixels.</summary>
        public const double SplitterThickness = 6.0;

        /// <summary>Height of a tab strip, in device-independent pixels.</summary>
        public const double TabStripHeight = 26.0;

        private readonly Canvas _canvas = new Canvas();
        private readonly Dictionary<char, TracePlot> _plots = new Dictionary<char, TracePlot>();
        private readonly List<char> _traces = new List<char>();

        /// <summary>Traces that are open but have no window (<c>REQ-UI-062</c>).</summary>
        private readonly HashSet<char> _hidden = new HashSet<char>();
        private readonly List<TraceTabStrip> _strips = new List<TraceTabStrip>();
        private readonly TraceLayoutHistory _history = new TraceLayoutHistory();

        private IReadOnlyList<TraceSlot> _slots =
            new ReadOnlyCollection<TraceSlot>(new TraceSlot[0]);

        private TracePlot _adopted;
        private char _active = 'A';

        /// <summary>Creates an empty document area.</summary>
        public TraceDocumentArea()
        {
            AddVisualChild(_canvas);
            AddLogicalChild(_canvas);

            ClipToBounds = true;
        }

        /// <summary>Raised when the active trace changes.</summary>
        public event EventHandler<char> ActiveTraceChanged;

        /// <summary>Raised when the arrangement changes, naming the layout now in force.</summary>
        public event EventHandler<TraceLayoutPreset> LayoutChanged;

        /// <summary>The trace letters currently open, left to right.</summary>
        public IReadOnlyList<char> Traces => new ReadOnlyCollection<char>(_traces);

        /// <summary>The layout in force.</summary>
        public TraceLayoutPreset Layout => _history.Current;

        /// <summary>The arrangement in force.</summary>
        public IReadOnlyList<TraceSlot> Slots => _slots;

        /// <summary>The tab strips, one per occupied slot.</summary>
        public IReadOnlyList<TraceTabStrip> TabStrips =>
            new ReadOnlyCollection<TraceTabStrip>(_strips);

        /// <summary>
        /// The active trace, whose tab is bold and whose plot the shell drives.
        /// </summary>
        /// <exception cref="ArgumentException">That trace is not open.</exception>
        public char ActiveTrace
        {
            get { return _active; }

            set
            {
                if (!_traces.Contains(value))
                {
                    throw new ArgumentException("Trace " + value + " is not open.", nameof(value));
                }

                if (_active == value)
                {
                    return;
                }

                _active = value;

                foreach (TraceTabStrip strip in _strips)
                {
                    bool holdsIt = Holds(strip.Traces, value);

                    if (holdsIt)
                    {
                        strip.ActiveTrace = value;
                    }

                    // Exactly one tab in the whole area is bold: the active trace's. Every group
                    // has a tab on top, so bolding each group's own would make every tiled trace
                    // look active at once.
                    strip.HighlightsActive = holdsIt;
                }

                Raise(ActiveTraceChanged, value);
            }
        }

        /// <summary>
        /// The traces that have a window in the arrangement, in order.
        /// </summary>
        /// <remarks>
        /// A hidden trace is still open, still fed and still in <see cref="Traces"/>: what it has
        /// lost is its place on screen. That distinction is the reason <c>REQ-UI-062</c> asks for
        /// hiding as well as removing — a trace being kept for comparison should not have to be
        /// closed and rebuilt to get it out of the way for a minute.
        /// </remarks>
        public IReadOnlyList<char> VisibleTraces
        {
            get
            {
                var visible = new List<char>(_traces.Count);

                foreach (char trace in _traces)
                {
                    if (!_hidden.Contains(trace))
                    {
                        visible.Add(trace);
                    }
                }

                return new ReadOnlyCollection<char>(visible);
            }
        }

        /// <summary>Whether a trace has a window in the arrangement.</summary>
        /// <param name="trace">The trace letter.</param>
        public bool IsVisible(char trace) => _traces.Contains(trace) && !_hidden.Contains(trace);

        /// <summary>
        /// Shows or hides a trace's window (<c>REQ-UI-062</c>, <c>REQ-STA-001</c>).
        /// </summary>
        /// <param name="trace">The trace letter.</param>
        /// <param name="visible">Whether it should have a window.</param>
        /// <returns><c>true</c> if the arrangement changed.</returns>
        /// <remarks>
        /// The last visible trace cannot be hidden — an empty document area is a state with no way
        /// out of it from the document area itself. Hiding the active trace moves the selection to
        /// one that is still on screen, because "active" means the trace the trace commands act on
        /// and pointing that at something invisible is how a command appears to do nothing.
        /// </remarks>
        public bool SetVisible(char trace, bool visible)
        {
            if (!_traces.Contains(trace) || IsVisible(trace) == visible)
            {
                return false;
            }

            if (!visible && VisibleTraces.Count <= 1)
            {
                return false;
            }

            if (visible)
            {
                _hidden.Remove(trace);
            }
            else
            {
                _hidden.Add(trace);
            }

            if (!visible && _active == trace)
            {
                ActiveTrace = VisibleTraces[0];
            }

            ApplyLayout(_history.Current);

            return true;
        }

        /// <summary>The plot drawing a trace, or <c>null</c> if it is not open.</summary>
        /// <param name="trace">The trace letter.</param>
        public TracePlot PlotOf(char trace)
        {
            TracePlot plot;

            return _plots.TryGetValue(trace, out plot) ? plot : null;
        }

        /// <summary>The plot drawing the active trace.</summary>
        public TracePlot ActivePlot => PlotOf(_active);

        /// <summary>Every open plot, so a display-wide setting can reach all of them.</summary>
        /// <remarks>
        /// A colour applies to the display, not to whichever plot happens to be in front
        /// (<c>REQ-UI-014</c>). A caller that reached only <see cref="ActivePlot"/> would leave the
        /// other plots on the old palette until each was next touched.
        /// </remarks>
        public IReadOnlyList<TracePlot> Plots =>
            new ReadOnlyCollection<TracePlot>(new List<TracePlot>(_plots.Values));

        /// <summary>
        /// Hands an existing plot to the area, to be used for the first trace.
        /// </summary>
        /// <param name="plot">The plot the shell has already wired up.</param>
        /// <exception cref="ArgumentNullException"><paramref name="plot"/> is null.</exception>
        /// <exception cref="InvalidOperationException">Traces have already been opened.</exception>
        public void AdoptPrimaryPlot(TracePlot plot)
        {
            if (plot == null)
            {
                throw new ArgumentNullException(nameof(plot));
            }

            if (_traces.Count > 0)
            {
                throw new InvalidOperationException(
                    "A plot can only be adopted before any trace is opened; trace " + _traces[0] +
                    " already has one.");
            }

            _adopted = plot;
        }

        /// <summary>
        /// Opens a trace, giving it a plot and a place in the layout.
        /// </summary>
        /// <param name="trace">The trace letter.</param>
        /// <returns>The plot drawing it.</returns>
        /// <exception cref="ArgumentException">That trace is already open.</exception>
        public TracePlot AddTrace(char trace)
        {
            if (_traces.Contains(trace))
            {
                throw new ArgumentException("Trace " + trace + " is already open.", nameof(trace));
            }

            TracePlot plot;

            if (_traces.Count == 0 && _adopted != null)
            {
                plot = _adopted;
                _adopted = null;
            }
            else
            {
                plot = new TracePlot();
            }

            _traces.Add(trace);
            _plots[trace] = plot;

            if (_traces.Count == 1)
            {
                _active = trace;
            }

            Rebuild();

            return plot;
        }

        /// <summary>
        /// Closes a trace.
        /// </summary>
        /// <param name="trace">The trace letter.</param>
        /// <returns><c>false</c> when it was not open, or when it is the last one.</returns>
        /// <remarks>
        /// The last trace is not closeable. A document area with nothing in it is a grey rectangle
        /// with no way back to a measurement, and the close button would then be a trap.
        /// </remarks>
        public bool RemoveTrace(char trace)
        {
            if (!_traces.Contains(trace) || _traces.Count <= 1)
            {
                return false;
            }

            _traces.Remove(trace);
            _hidden.Remove(trace);
            _plots.Remove(trace);

            if (_active == trace)
            {
                _active = _traces[0];
                Raise(ActiveTraceChanged, _active);
            }

            Rebuild();

            return true;
        }

        /// <summary>
        /// Applies a layout preset (<c>REQ-UI-005</c>).
        /// </summary>
        /// <param name="preset">The preset; <see cref="TraceLayoutKind.Previous"/> reverts.</param>
        /// <exception cref="ArgumentNullException"><paramref name="preset"/> is null.</exception>
        public void ApplyLayout(TraceLayoutPreset preset)
        {
            if (preset == null)
            {
                throw new ArgumentNullException(nameof(preset));
            }

            _history.Apply(preset, VisibleTraces, AreaWidth, AreaHeight);
            Rebuild(reArrange: false);

            Raise(LayoutChanged, _history.Current);
        }

        /// <summary>Redistributes the trace windows evenly (<c>REQ-UI-004</c>).</summary>
        public void ResizeTraces()
        {
            _history.ResizeTraces(VisibleTraces, AreaWidth, AreaHeight);
            Rebuild(reArrange: false);

            Raise(LayoutChanged, _history.Current);
        }

        /// <inheritdoc />
        protected override int VisualChildrenCount => 1;

        /// <inheritdoc />
        protected override Visual GetVisualChild(int index) => _canvas;

        /// <inheritdoc />
        protected override Size MeasureOverride(Size constraint)
        {
            _canvas.Measure(constraint);

            return new Size(
                double.IsInfinity(constraint.Width) ? 0.0 : constraint.Width,
                double.IsInfinity(constraint.Height) ? 0.0 : constraint.Height);
        }

        /// <inheritdoc />
        protected override Size ArrangeOverride(Size finalSize)
        {
            _canvas.Arrange(new Rect(new Point(0.0, 0.0), finalSize));

            // The slot model is in pixels, so a resize re-arranges rather than scaling what was
            // there: a stack of four stays a stack of four at the new height.
            Rebuild();

            return finalSize;
        }

        private int AreaWidth => Math.Max(1, (int)Math.Round(ActualWidth));

        private int AreaHeight => Math.Max(1, (int)Math.Round(ActualHeight));

        private void Rebuild(bool reArrange = true)
        {
            if (_traces.Count == 0)
            {
                _canvas.Children.Clear();
                _strips.Clear();
                return;
            }

            if (reArrange)
            {
                _slots = _history.Current.Kind == TraceLayoutKind.Custom
                    ? _history.CurrentSlots
                    : TraceLayoutEngine.Arrange(
                        _history.Current, VisibleTraces, AreaWidth, AreaHeight);
            }
            else
            {
                _slots = _history.CurrentSlots;
            }

            _canvas.Children.Clear();
            _strips.Clear();

            foreach (TraceSlot slot in _slots)
            {
                if (slot.Traces.Count > 0)
                {
                    _canvas.Children.Add(BuildCell(slot));
                }
            }

            AddSplitters();
        }

        private FrameworkElement BuildCell(TraceSlot slot)
        {
            var strip = new TraceTabStrip { Height = TabStripHeight };

            bool holdsActive = Holds(slot.Traces, _active);

            strip.SetTraces(slot.Traces, holdsActive ? _active : slot.Active);
            strip.HighlightsActive = holdsActive;
            strip.TraceSelected += (sender, trace) => ActiveTrace = trace;
            strip.CloseRequested += (sender, trace) => RemoveTrace(trace);

            _strips.Add(strip);

            var content = new Grid();

            // Only the tab on top is drawn: the others are in the group and reachable by their
            // tab, which is what a tab group means.
            TracePlot plot = PlotOf(strip.ActiveTrace);

            if (plot != null)
            {
                // A plot can only have one parent, and it moves between cells as the layout
                // changes, so it is detached from wherever it was before being placed.
                var previous = plot.Parent as Panel;

                if (previous != null)
                {
                    previous.Children.Remove(plot);
                }

                content.Children.Add(plot);
            }

            var cell = new DockPanel { LastChildFill = true };

            DockPanel.SetDock(strip, Dock.Top);
            cell.Children.Add(strip);
            cell.Children.Add(content);

            var frame = new Border
            {
                Child = cell,
                BorderThickness = new Thickness(1.0),
                Width = slot.Width,
                Height = slot.Height,
            };

            // The frame round a trace window is chrome, not plot surface: REQ-UI-081 draws the line
            // at the graticule, and a resource reference is what makes this side of it follow a
            // theme change (REQ-UI-083).
            frame.SetResourceReference(Border.BorderBrushProperty, Theming.ChromeKeys.Border);

            Canvas.SetLeft(frame, slot.Left);
            Canvas.SetTop(frame, slot.Top);

            return frame;
        }

        private void AddSplitters()
        {
            for (int i = 0; i < _slots.Count - 1; i++)
            {
                TraceSlot above = _slots[i];
                TraceSlot below = _slots[i + 1];

                if (above.Bottom != below.Top ||
                    above.Left != below.Left ||
                    above.Width != below.Width ||
                    above.Traces.Count == 0 ||
                    below.Traces.Count == 0)
                {
                    continue;
                }

                var splitter = new Thumb
                {
                    Width = above.Width,
                    Height = SplitterThickness,
                    Cursor = Cursors.SizeNS,
                    Opacity = 0.0,
                };

                int boundary = i;
                splitter.DragDelta += (sender, e) => Drag(boundary, e.VerticalChange);

                Canvas.SetLeft(splitter, above.Left);
                Canvas.SetTop(splitter, above.Bottom - SplitterThickness / 2.0);

                _canvas.Children.Add(splitter);
            }
        }

        private void Drag(int boundary, double verticalChange)
        {
            int delta = (int)Math.Round(verticalChange);

            if (delta == 0 || boundary >= _slots.Count - 1)
            {
                return;
            }

            // The same call the layout tests exercise, so the gesture and the model cannot part
            // company - and recorded as Custom, which is what Previous Layout expects of a
            // hand arrangement.
            _history.RecordCustom(TraceLayoutEngine.DragBoundary(_slots, boundary, delta));

            Rebuild(reArrange: false);

            Raise(LayoutChanged, _history.Current);
        }

        /// <summary>
        /// Whether a list of trace letters holds one.
        /// </summary>
        /// <remarks>
        /// Spelled out rather than using <c>Contains</c>. On a <c>IReadOnlyList&lt;char&gt;</c> the
        /// compiler prefers <c>MemoryExtensions.Contains</c>, which is a substring search wanting a
        /// <c>StringComparison</c> — it fails to compile here, but the same call on a
        /// <c>List&lt;char&gt;</c> would silently bind to the span overload elsewhere.
        /// </remarks>
        private static bool Holds(IReadOnlyList<char> traces, char trace)
        {
            for (int i = 0; i < traces.Count; i++)
            {
                if (traces[i] == trace)
                {
                    return true;
                }
            }

            return false;
        }

        private void Raise<T>(EventHandler<T> handler, T value)
        {
            if (handler != null)
            {
                handler(this, value);
            }
        }
    }
}
