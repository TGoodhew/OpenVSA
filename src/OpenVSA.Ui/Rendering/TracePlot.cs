using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OpenVSA.Core.Threading;
using OpenVSA.Dsp.Spectrum;
using OpenVSA.Ui.HotSpots;

namespace OpenVSA.Ui.Rendering
{
    /// <summary>
    /// The plot surface: a rasterised graticule and trace with editable WPF annotation over it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The split is the one <c>REQ-UI-010</c> and <c>REQ-UI-042</c> together force. Trace geometry
    /// goes through <see cref="PlotRasterizer"/> into a <see cref="WriteableBitmap"/>, because at
    /// the point counts of <c>REQ-NFR-021</c> nothing else keeps up. Annotation is real elements on
    /// top, because the hot spots of <c>REQ-UI-042</c> need hit-testing, hover feedback and in-place
    /// editing — all of which are cheap against elements and expensive against rasterised glyphs,
    /// and that requirement is explicit that retrofitting the editing model later is the costly
    /// path.
    /// </para>
    /// <para>
    /// <strong>Positions are <c>REQ-UI-040</c>'s, and the arrangement enforces them.</strong> The
    /// annotation band is a fixed-height row above and below the graticule and a fixed-width margin
    /// either side, so no annotation can drift over the trace as its text grows. The single
    /// exception is the indicator strings, which the requirement puts <em>inside</em> the grid's
    /// upper-right corner — and which are the only thing drawn there.
    /// </para>
    /// <para>
    /// <strong>What this control does not do is compute.</strong> It receives a
    /// <see cref="TraceSnapshot"/> whose envelope was already decimated by the render marshal on the
    /// pump thread, and does nothing per acquired point — only per pixel. That is what keeps the
    /// dispatcher inside <c>REQ-NFR-010</c>'s "no DSP, no blocking wait over 16 ms".
    /// </para>
    /// </remarks>
    public sealed class TracePlot : Grid
    {
        /// <summary>Annotation band thickness, in device-independent pixels.</summary>
        public const double AnnotationBandDip = 44.0;

        /// <summary>Vertical scale a plot starts at, in dB per graticule division.</summary>
        public const double DefaultDecibelsPerDivision = 10.0;

        /// <summary>Graticule divisions down the screen.</summary>
        public const int VerticalDivisions = 10;

        private readonly Image _image;
        private readonly HotSpot _topScale;
        private readonly HotSpot _perDivision;
        private readonly HotSpot _bottomScale;
        private readonly HotSpot _format;
        private readonly HotSpot _resolutionBandwidth;
        private readonly HotSpot _triggerChannel;
        private readonly HotSpot _centerFrequency;
        private readonly HotSpot _mainTime;
        private readonly TextBlock _analysisText;
        private readonly TextBlock _markerText;
        private readonly TextBlock _indicatorText;
        private readonly List<FrameworkElement> _annotation = new List<FrameworkElement>();
        private readonly List<HotSpot> _hotSpots = new List<HotSpot>();

        private TraceSnapshot _snapshot;
        private IReadOnlyList<PlotMarker> _markers = new PlotMarker[0];
        private string _markerReadout = string.Empty;

        private PlotPalette _palette = PlotPalette.Dark;
        private WriteableBitmap _bitmap;
        private PixelSurface _surface;
        private PlotLayout _layout;
        private byte[] _transfer;
        private double _topDbm = 20.0;
        private double _referenceLevelDbm = 20.0;
        private double _decibelsPerDivision = DefaultDecibelsPerDivision;
        private TraceFormatOptions _formatOptions = TraceFormatOptions.Default;
        private int _marginPixels = 48;
        private Size _builtFor = Size.Empty;
        private bool _suppressParameterEvents;

        /// <summary>Creates an empty plot.</summary>
        public TracePlot()
        {
            _image = new Image
            {
                Stretch = Stretch.Fill,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                IsHitTestVisible = false,
            };

            RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.NearestNeighbor);
            RenderOptions.SetEdgeMode(_image, EdgeMode.Aliased);

            // Three columns and three rows, with the image spanning all of them. The outer rows are
            // fixed at the annotation band's thickness rather than sized to their content, which is
            // what makes REQ-UI-040's "all other trace annotation lies outside the graticule" a
            // property of the layout instead of a property of how long the strings happen to be.
            for (int i = 0; i < 3; i++)
            {
                ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.0, GridUnitType.Star) });
            }

            RowDefinitions.Add(new RowDefinition { Height = new GridLength(AnnotationBandDip) });
            RowDefinitions.Add(new RowDefinition { Height = new GridLength(1.0, GridUnitType.Star) });
            RowDefinitions.Add(new RowDefinition { Height = new GridLength(AnnotationBandDip) });

            SetColumnSpan(_image, 3);
            SetRowSpan(_image, 3);
            Children.Add(_image);

            // REQ-UI-040's recommended placement, which follows from the Y-reference default:
            // Y-axis top scale top-left with per-division below it, Y-axis bottom scale
            // bottom-left, trace format / resolution bandwidth / trigger channel in the upper band,
            // centre frequency and main time length centred beneath the X axis, and the
            // active-marker readout above the grid to the right.
            _topScale = NewHotSpot(string.Empty, HorizontalAlignment.Left);
            _perDivision = NewHotSpot(string.Empty, HorizontalAlignment.Left);
            AddStack(Orientation.Vertical, 0, 0, HorizontalAlignment.Left, VerticalAlignment.Top,
                _topScale, _perDivision);

            _format = NewHotSpot(string.Empty, HorizontalAlignment.Center);
            _resolutionBandwidth = NewHotSpot("RBW ", HorizontalAlignment.Center);
            _triggerChannel = NewHotSpot("Trig ", HorizontalAlignment.Center);
            _analysisText = NewLabel(HorizontalAlignment.Center);

            StackPanel upper = AddStack(
                Orientation.Vertical, 0, 1, HorizontalAlignment.Center, VerticalAlignment.Top);
            upper.Children.Add(Row(_format, _resolutionBandwidth, _triggerChannel));
            upper.Children.Add(_analysisText);

            _markerText = NewLabel(HorizontalAlignment.Right);
            Place(_markerText, 0, 2, HorizontalAlignment.Right, VerticalAlignment.Top);

            _bottomScale = NewHotSpot(string.Empty, HorizontalAlignment.Left);
            Place(_bottomScale, 2, 0, HorizontalAlignment.Left, VerticalAlignment.Bottom);

            _centerFrequency = NewHotSpot("Center ", HorizontalAlignment.Center);
            _mainTime = NewHotSpot("Time ", HorizontalAlignment.Center);
            AddStack(Orientation.Horizontal, 2, 1, HorizontalAlignment.Center,
                VerticalAlignment.Bottom, _centerFrequency, _mainTime);

            // The one piece of annotation that belongs inside the graticule (REQ-UI-040), pushed in
            // from the top right by the band's thickness so it clears the graticule's own border.
            _indicatorText = NewLabel(HorizontalAlignment.Right);
            _indicatorText.VerticalAlignment = VerticalAlignment.Top;
            _indicatorText.Margin = new Thickness(0.0, AnnotationBandDip + 6.0, AnnotationBandDip + 6.0, 0.0);
            SetRowSpan(_indicatorText, 3);
            SetColumnSpan(_indicatorText, 3);
            SetRow(_indicatorText, 0);
            SetColumn(_indicatorText, 0);
            Children.Add(_indicatorText);

            BuildValues();
            ApplyPalette();
        }

        /// <summary>Raised when the graticule changes width, and so the column count to decimate to.</summary>
        public event EventHandler GraticuleColumnsChanged;

        /// <summary>
        /// Raised when a hot spot's value is changed by the user (<c>REQ-UI-042</c>).
        /// </summary>
        /// <remarks>
        /// The plot knows what a parameter now reads; only the shell knows what to do about it —
        /// re-plan the acquisition, re-scale the axis, or change the trigger. Reporting it rather
        /// than acting on it is what keeps the control free of the measurement.
        /// </remarks>
        public event EventHandler<HotSpot> ParameterChanged;

        /// <summary>Raised when a hot spot asks for its data-entry dialog (double click).</summary>
        public event EventHandler<HotSpot> DialogRequested;

        /// <summary>
        /// Pixel columns across the graticule: what a <see cref="RenderMarshal"/> must decimate to.
        /// </summary>
        public int GraticuleColumns => _layout == null ? 0 : _layout.Graticule.Width;

        /// <summary>The colours of <c>REQ-UI-010</c>'s zones.</summary>
        /// <exception cref="ArgumentNullException">The value is null.</exception>
        public PlotPalette Palette
        {
            get { return _palette; }

            set
            {
                if (value == null)
                {
                    throw new ArgumentNullException(nameof(value));
                }

                _palette = value;
                ApplyPalette();
                Redraw(null);
            }
        }

        /// <summary>Level at the top of the graticule, in dBm. Follows the reference level.</summary>
        public double TopDbm => _topDbm;

        /// <summary>Level at the bottom of the graticule, in dBm.</summary>
        public double BottomDbm => _topDbm - _decibelsPerDivision * VerticalDivisions;

        /// <summary>Vertical scale, in dB per graticule division.</summary>
        public double DecibelsPerDivision => _decibelsPerDivision;

        /// <summary>The hot spot over the Y-axis top scale.</summary>
        public HotSpot TopScaleHotSpot => _topScale;

        /// <summary>The hot spot over the Y-axis per-division scale.</summary>
        public HotSpot PerDivisionHotSpot => _perDivision;

        /// <summary>The hot spot over the Y-axis bottom scale.</summary>
        public HotSpot BottomScaleHotSpot => _bottomScale;

        /// <summary>The hot spot over the trace format.</summary>
        public HotSpot FormatHotSpot => _format;

        /// <summary>The hot spot over the resolution bandwidth.</summary>
        public HotSpot ResolutionBandwidthHotSpot => _resolutionBandwidth;

        /// <summary>The hot spot over the trigger channel.</summary>
        public HotSpot TriggerChannelHotSpot => _triggerChannel;

        /// <summary>The hot spot over the centre frequency.</summary>
        public HotSpot CenterFrequencyHotSpot => _centerFrequency;

        /// <summary>The hot spot over the main time length.</summary>
        public HotSpot MainTimeHotSpot => _mainTime;

        /// <summary>Every hot spot on the plot, in the order they were created.</summary>
        public IReadOnlyList<HotSpot> HotSpots => _hotSpots;

        /// <summary>
        /// The group-delay aperture and unwrap tolerance in force (<c>REQ-DSP-045</c>,
        /// <c>REQ-DSP-044</c>).
        /// </summary>
        /// <remarks>
        /// Both requirements ask for the setting to appear in the trace annotation, and the reason
        /// is the same in each: the setting changes what is drawn, so a trace without it is a
        /// measurement nobody can reproduce. <see cref="UpdateAnnotation"/> shows it only for the
        /// formats it bears on.
        /// </remarks>
        /// <exception cref="ArgumentNullException">The value is null.</exception>
        public TraceFormatOptions FormatOptions
        {
            get { return _formatOptions; }

            set
            {
                if (value == null)
                {
                    throw new ArgumentNullException(nameof(value));
                }

                _formatOptions = value;

                if (_snapshot != null)
                {
                    UpdateAnnotation(_snapshot.Spectrum);
                }
            }
        }

        /// <summary>The format the trace is currently drawn in.</summary>
        public TraceFormat CurrentFormat
        {
            get
            {
                TraceFormat format;

                return TraceFormatText.TryParse(_format.Value.Text, out format)
                    ? format
                    : TraceFormat.LogMagnitude;
            }
        }

        /// <summary>
        /// Trace annotation other than the indicator strings.
        /// </summary>
        /// <remarks>
        /// Exposed so that <c>REQ-UI-040</c>'s "the indicator strings are the only annotation drawn
        /// inside the graticule" can be measured from the arranged control rather than asserted by
        /// inspection.
        /// </remarks>
        public IReadOnlyList<FrameworkElement> AnnotationElements => _annotation;

        /// <summary>The element holding the trace indicator strings (<c>REQ-UI-041</c>).</summary>
        public FrameworkElement IndicatorElement => _indicatorText;

        /// <summary>The graticule's rectangle within this control, in device-independent pixels.</summary>
        public Rect GraticuleBounds =>
            new Rect(
                AnnotationBandDip,
                AnnotationBandDip,
                Math.Max(0.0, ActualWidth - 2.0 * AnnotationBandDip),
                Math.Max(0.0, ActualHeight - 2.0 * AnnotationBandDip));

        /// <summary>
        /// Where an element sits within this control, in device-independent pixels.
        /// </summary>
        /// <param name="element">A descendant of this control.</param>
        /// <exception cref="ArgumentNullException"><paramref name="element"/> is null.</exception>
        public Rect BoundsOf(FrameworkElement element)
        {
            if (element == null)
            {
                throw new ArgumentNullException(nameof(element));
            }

            Point origin = element.TranslatePoint(new Point(0.0, 0.0), this);
            return new Rect(origin, element.RenderSize);
        }

        /// <summary>
        /// Sets the trace indicator strings shown in the grid's upper-right corner
        /// (<c>REQ-UI-041</c>).
        /// </summary>
        /// <param name="indicators">The active indicators, or <c>null</c> for none.</param>
        public void SetIndicators(TraceIndicators indicators)
        {
            ThreadAffinity.AssertOnUiThread("Setting trace indicators");

            _indicatorText.Text = indicators == null ? string.Empty : indicators.Text;
        }

        /// <summary>Arranges the control, rebuilding the surface when its size has changed.</summary>
        /// <param name="finalSize">The size the parent allotted.</param>
        /// <returns>The size used.</returns>
        /// <remarks>
        /// Here rather than in a <see cref="FrameworkElement.SizeChanged"/> handler because this is
        /// called synchronously by <see cref="UIElement.Arrange"/>, whereas the event is queued by
        /// the layout manager and never fires for an element that is not in a live visual tree. The
        /// difference is what lets the whole chain be exercised in a test with no window,
        /// dispatcher or message pump.
        /// </remarks>
        protected override Size ArrangeOverride(Size finalSize)
        {
            Size arranged = base.ArrangeOverride(finalSize);

            if (arranged != _builtFor)
            {
                _builtFor = arranged;
                Rebuild(arranged);
            }

            return arranged;
        }

        /// <summary>Rebuilds the surface when the control moves to a display of a different DPI.</summary>
        /// <param name="oldDpi">The previous scale.</param>
        /// <param name="newDpi">The new scale.</param>
        protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
        {
            base.OnDpiChanged(oldDpi, newDpi);
            Rebuild(_builtFor);
        }

        /// <summary>
        /// Draws a snapshot.
        /// </summary>
        /// <param name="snapshot">The snapshot; ignored if it was decimated to a different width.</param>
        /// <returns><c>true</c> if it was drawn, <c>false</c> if it was stale after a resize.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="snapshot"/> is null.</exception>
        public bool Show(TraceSnapshot snapshot)
        {
            if (snapshot == null)
            {
                throw new ArgumentNullException(nameof(snapshot));
            }

            ThreadAffinity.AssertOnUiThread("Drawing a trace");

            if (_layout == null)
            {
                return false;
            }

            // The reference level sets the top of the graticule, so a change of range re-scales the
            // axis rather than sending the trace off the top of the screen. Compared against the
            // last reference level rather than against the current top, so that a top scale the
            // user set by hand is not undone by the next frame.
            if (Math.Abs(snapshot.Spectrum.ReferenceLevelDbm - _referenceLevelDbm) > 1e-9)
            {
                _referenceLevelDbm = snapshot.Spectrum.ReferenceLevelDbm;
                _topDbm = _referenceLevelDbm;
                BuildLayout();
            }

            if (snapshot.Columns != _layout.Graticule.Width)
            {
                return false;
            }

            _snapshot = snapshot;
            Redraw(snapshot);
            return true;
        }

        /// <summary>
        /// Sets the markers to draw over the trace, and the active-marker readout.
        /// </summary>
        /// <param name="markers">The glyphs to draw, or <c>null</c> for none.</param>
        /// <param name="readout">Text for the readout above the grid, right (<c>REQ-UI-040</c>).</param>
        /// <remarks>
        /// Redraws immediately from the last frame rather than waiting for the next one, so
        /// selecting or moving a marker is seen at once even on a stopped measurement.
        /// </remarks>
        public void SetMarkers(IReadOnlyList<PlotMarker> markers, string readout)
        {
            ThreadAffinity.AssertOnUiThread("Setting markers");

            _markers = markers ?? new PlotMarker[0];
            _markerReadout = readout ?? string.Empty;
            _markerText.Text = _markerReadout;

            Redraw(_snapshot);
        }

        /// <summary>
        /// The trace point a pixel position corresponds to, for placing a marker by clicking.
        /// </summary>
        /// <param name="position">Position within this control, in device-independent pixels.</param>
        /// <returns>A point index, or −1 if the position is outside the graticule or there is no trace.</returns>
        public int PointAt(Point position)
        {
            if (_layout == null || _snapshot == null)
            {
                return -1;
            }

            DpiScale dpi = VisualTreeHelper.GetDpi(this);
            int x = (int)Math.Round(position.X * dpi.DpiScaleX);
            int y = (int)Math.Round(position.Y * dpi.DpiScaleY);

            if (!_layout.Graticule.Contains(x, y))
            {
                return -1;
            }

            return TraceEnvelope.IndexFor(
                x - _layout.Graticule.X, _snapshot.Spectrum.PointCount, _snapshot.Columns);
        }

        /// <summary>Draws an empty graticule, discarding any trace.</summary>
        public void Clear()
        {
            ThreadAffinity.AssertOnUiThread("Clearing a trace");

            _snapshot = null;
            Redraw(null);

            _analysisText.Text = string.Empty;
            _markerText.Text = string.Empty;
            _indicatorText.Text = string.Empty;
        }

        // ---- Annotation construction -----------------------------------------------------------

        private HotSpot NewHotSpot(string label, HorizontalAlignment horizontal)
        {
            var spot = new HotSpot
            {
                Label = label,
                Margin = new Thickness(6.0, 1.0, 6.0, 1.0),
                FontFamily = new FontFamily("Consolas"),
                FontSize = 11.0,
                HorizontalAlignment = horizontal,
                TextAlignment = AlignmentOf(horizontal),
            };

            spot.ValueChanged += OnHotSpotChanged;
            spot.DialogRequested += OnHotSpotDialogRequested;

            _hotSpots.Add(spot);
            _annotation.Add(spot);
            return spot;
        }

        private TextBlock NewLabel(HorizontalAlignment horizontal)
        {
            var text = new TextBlock
            {
                Margin = new Thickness(6.0, 1.0, 6.0, 1.0),
                FontFamily = new FontFamily("Consolas"),
                FontSize = 11.0,
                IsHitTestVisible = false,
                HorizontalAlignment = horizontal,
                TextAlignment = AlignmentOf(horizontal),
            };

            return text;
        }

        private static TextAlignment AlignmentOf(HorizontalAlignment horizontal)
        {
            if (horizontal == HorizontalAlignment.Right)
            {
                return TextAlignment.Right;
            }

            return horizontal == HorizontalAlignment.Center
                ? TextAlignment.Center
                : TextAlignment.Left;
        }

        private static StackPanel Row(params UIElement[] children)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal };

            foreach (UIElement child in children)
            {
                panel.Children.Add(child);
            }

            return panel;
        }

        private StackPanel AddStack(
            Orientation orientation,
            int row,
            int column,
            HorizontalAlignment horizontal,
            VerticalAlignment vertical,
            params UIElement[] children)
        {
            var panel = new StackPanel { Orientation = orientation };

            foreach (UIElement child in children)
            {
                panel.Children.Add(child);
            }

            Place(panel, row, column, horizontal, vertical);
            return panel;
        }

        private void Place(
            FrameworkElement element,
            int row,
            int column,
            HorizontalAlignment horizontal,
            VerticalAlignment vertical)
        {
            element.HorizontalAlignment = horizontal;
            element.VerticalAlignment = vertical;

            SetRow(element, row);
            SetColumn(element, column);
            Children.Add(element);

            if (!_annotation.Contains(element) && !(element is Panel))
            {
                _annotation.Add(element);
            }
        }

        /// <summary>
        /// Gives every hot spot the quantity it edits, with the step an arrow key should move it by.
        /// </summary>
        /// <remarks>
        /// Steps are chosen from what the parameter is for rather than from its magnitude: a
        /// reference level moves in whole decibels, a per-division scale through the 1-2-5 ladder a
        /// graticule is readable at, and a centre frequency by a proportion, because it is set
        /// anywhere from kilohertz to gigahertz and no single increment suits both ends.
        /// </remarks>
        private void BuildValues()
        {
            _topScale.Value = NumericHotSpotValue.Decibels(_topDbm);

            _perDivision.Value = new ChoiceHotSpotValue(
                new[] { "1 dB/div", "2 dB/div", "5 dB/div", "10 dB/div", "20 dB/div" }, 3);

            _bottomScale.Value = NumericHotSpotValue.Decibels(BottomDbm);

            _format.Value = new ChoiceHotSpotValue(TraceFormatText.Names, 0);

            var bandwidth = NumericHotSpotValue.Frequency(1e3, 1.0);
            bandwidth.ProportionalStep = 0.1;
            bandwidth.Minimum = 1e-3;
            _resolutionBandwidth.Value = bandwidth;

            _triggerChannel.Value = new ChoiceHotSpotValue(new[] { "Ch 1", "Ch 2", "Ext", "Free Run" }, 0);

            var center = NumericHotSpotValue.Frequency(1e9, 1e3);
            center.ProportionalStep = 0.01;
            center.Minimum = 0.0;
            _centerFrequency.Value = center;

            var time = NumericHotSpotValue.Time(1e-3, 1e-6);
            time.ProportionalStep = 0.1;
            time.Minimum = 1e-12;
            _mainTime.Value = time;
        }

        private void OnHotSpotChanged(object sender, EventArgs e)
        {
            var spot = (HotSpot)sender;

            ApplyScaleChange(spot);

            // Changing format changes which settings bear on the trace, so the note beside it has
            // to follow at once rather than at the next frame - on a stopped measurement there may
            // not be one.
            if (ReferenceEquals(spot, _format) && _snapshot != null)
            {
                UpdateAnnotation(_snapshot.Spectrum);
            }

            if (_suppressParameterEvents)
            {
                return;
            }

            EventHandler<HotSpot> handler = ParameterChanged;

            if (handler != null)
            {
                handler(this, spot);
            }
        }

        /// <summary>
        /// Applies the two hot spots the plot itself owns: the vertical scale and its top.
        /// </summary>
        /// <remarks>
        /// Everything else is the shell's business. These two are not: they change nothing about
        /// the acquisition, only how it is drawn, and routing them out and back would make the axis
        /// lag the click by a frame.
        /// </remarks>
        private void ApplyScaleChange(HotSpot spot)
        {
            if (ReferenceEquals(spot, _topScale))
            {
                _topDbm = ((NumericHotSpotValue)_topScale.Value).Value;
            }
            else if (ReferenceEquals(spot, _bottomScale))
            {
                // The bottom follows the top and the scale, so setting it moves the top rather than
                // stretching the axis - which is what keeps the per-division reading true.
                _topDbm = ((NumericHotSpotValue)_bottomScale.Value).Value +
                    _decibelsPerDivision * VerticalDivisions;
            }
            else if (ReferenceEquals(spot, _perDivision))
            {
                double parsed;

                if (double.TryParse(
                        ((ChoiceHotSpotValue)_perDivision.Value).Text.Split(' ')[0],
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out parsed) &&
                    parsed > 0.0)
                {
                    _decibelsPerDivision = parsed;
                }
            }
            else
            {
                return;
            }

            RefreshScaleText();
            BuildLayout();
            Redraw(_snapshot);
        }

        private void RefreshScaleText()
        {
            _suppressParameterEvents = true;

            try
            {
                ((NumericHotSpotValue)_topScale.Value).Value = _topDbm;
                ((NumericHotSpotValue)_bottomScale.Value).Value = BottomDbm;
                _topScale.Refresh();
                _bottomScale.Refresh();
            }
            finally
            {
                _suppressParameterEvents = false;
            }
        }

        private void OnHotSpotDialogRequested(object sender, EventArgs e)
        {
            EventHandler<HotSpot> handler = DialogRequested;

            if (handler != null)
            {
                handler(this, (HotSpot)sender);
            }
        }

        private void ApplyPalette()
        {
            var annotation = new SolidColorBrush(ToMediaColor(_palette.Annotation));
            annotation.Freeze();

            foreach (FrameworkElement element in _annotation)
            {
                var text = element as TextBlock;

                if (text != null)
                {
                    text.Foreground = annotation;
                }
            }

            _markerText.Foreground = annotation;

            // Its own colour, because it is the only annotation over the trace background rather
            // than the annotation background (REQ-UI-040).
            var indicator = new SolidColorBrush(ToMediaColor(_palette.Indicator));
            indicator.Freeze();
            _indicatorText.Foreground = indicator;

            Background = new SolidColorBrush(ToMediaColor(_palette.AnnotationBackground));
        }

        private static Color ToMediaColor(PlotColor color) =>
            Color.FromArgb(color.A, color.R, color.G, color.B);

        /// <summary>
        /// Recreates the surface, bitmap and layout for the control's current size and DPI.
        /// </summary>
        /// <remarks>
        /// The bitmap is sized in device pixels, not device-independent ones, so that a graticule
        /// line is one physical pixel on a 150 % display rather than a blurred pair. That is also
        /// what makes the decimation exact: one column of the envelope is one column of the screen.
        /// </remarks>
        private void Rebuild(Size size)
        {
            DpiScale dpi = VisualTreeHelper.GetDpi(this);
            int width = (int)Math.Round(size.Width * dpi.DpiScaleX);
            int height = (int)Math.Round(size.Height * dpi.DpiScaleY);
            int margin = (int)Math.Round(AnnotationBandDip * dpi.DpiScaleX);

            // Below this the annotation band leaves no graticule and PlotLayout refuses to be
            // built - a legitimate transient while a docked pane is being dragged.
            if (width <= margin * 2 + 2 || height <= margin * 2 + 2)
            {
                _layout = null;
                _image.Source = null;
                return;
            }

            int previousColumns = GraticuleColumns;

            _surface = new PixelSurface(width, height);
            _transfer = new byte[_surface.Stride * height];
            _bitmap = new WriteableBitmap(
                width, height, 96.0 * dpi.DpiScaleX, 96.0 * dpi.DpiScaleY, PixelFormats.Bgra32, null);
            _image.Source = _bitmap;

            _marginPixels = margin;
            BuildLayout();
            Redraw(null);

            if (GraticuleColumns != previousColumns)
            {
                EventHandler handler = GraticuleColumnsChanged;
                if (handler != null)
                {
                    handler(this, EventArgs.Empty);
                }
            }
        }

        private void BuildLayout()
        {
            if (_surface == null)
            {
                return;
            }

            _layout = new PlotLayout(
                _surface.Width,
                _surface.Height,
                _marginPixels,
                _topDbm,
                BottomDbm);
        }

        private void Redraw(TraceSnapshot snapshot)
        {
            if (_layout == null || _surface == null || _bitmap == null)
            {
                return;
            }

            PlotRasterizer.Render(
                _surface,
                _layout,
                _palette,
                snapshot == null ? ReadOnlySpan<float>.Empty : snapshot.MinMax);

            if (snapshot != null)
            {
                DrawMarkers(snapshot);
            }

            _surface.CopyTo(_transfer);
            _bitmap.WritePixels(
                new Int32Rect(0, 0, _surface.Width, _surface.Height),
                _transfer,
                _surface.Stride,
                0);

            if (snapshot != null)
            {
                UpdateAnnotation(snapshot.Spectrum);
            }
        }

        /// <summary>
        /// Draws each marker's glyph over the trace.
        /// </summary>
        /// <remarks>
        /// After the trace, so a glyph is never hidden by the geometry it marks, and using the same
        /// index-to-column mapping the envelope used — otherwise the glyph lands beside its feature.
        /// </remarks>
        private void DrawMarkers(TraceSnapshot snapshot)
        {
            foreach (PlotMarker marker in _markers)
            {
                if (marker.PointIndex < 0 || marker.PointIndex >= snapshot.Spectrum.PointCount)
                {
                    continue;
                }

                int column = TraceEnvelope.ColumnFor(
                    marker.PointIndex, snapshot.Spectrum.PointCount, snapshot.Columns);

                int x = _layout.Graticule.X + column;
                int y = _layout.ValueToY(marker.LevelDbm);

                PlotColor colour = marker.IsSelected
                    ? _palette.SelectedMarker
                    : _palette.NotSelectedMarker;

                if (marker.IsFixed)
                {
                    MarkerGlyph.DrawCross(_surface, x, y, colour, marker.IsSelected);
                }
                else
                {
                    MarkerGlyph.DrawDiamond(_surface, x, y, colour, marker.IsSelected);
                }
            }
        }

        /// <summary>
        /// The averaging part of the annotation: how many acquisitions, and what they are worth
        /// (<c>REQ-DSP-031</c>).
        /// </summary>
        /// <remarks>
        /// The effective figure is shown only when it differs from the count, which is exactly when
        /// the frames were overlapped. Printing "12 (12 eff)" on every unoverlapped measurement
        /// would train the reader to ignore the parenthesis, and the parenthesis is the whole
        /// point: it is the number a confidence statement about the trace has to be made from.
        /// </remarks>
        /// <param name="frame">The frame being annotated.</param>
        /// <exception cref="ArgumentNullException"><paramref name="frame"/> is null.</exception>
        public static string AveragingNote(SpectrumFrame frame)
        {
            if (frame == null)
            {
                throw new ArgumentNullException(nameof(frame));
            }

            if (frame.AverageCount <= 1)
            {
                return string.Empty;
            }

            string note = "   Avg " + frame.AverageCount.ToString(CultureInfo.CurrentCulture);

            // A tenth of an average is below what anyone reads off a display, so a difference
            // smaller than that is rounding rather than correlation.
            if (frame.AverageCount - frame.EffectiveAverageCount < 0.1)
            {
                return note;
            }

            return note + " (" +
                frame.EffectiveAverageCount.ToString("0.0", CultureInfo.CurrentCulture) +
                " eff)";
        }

        /// <summary>
        /// The annotation for a transform bounded by <em>Max FFT Size</em> (<c>REQ-DSP-024</c>).
        /// </summary>
        /// <param name="frame">The frame to describe.</param>
        /// <returns>Empty when the record, not the ceiling, chose the transform length.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="frame"/> is null.</exception>
        /// <remarks>
        /// <para>
        /// Shown only when the ceiling actually bound. Annotating every trace with its transform
        /// length would spend the band's width on a number that is almost always implied by the
        /// point count, and would leave the one case that matters looking like all the others.
        /// </para>
        /// <para>
        /// The word is "capped" rather than "limited" because the measurement is not limited in the
        /// sense of being wrong — it is coarser than the samples could have made it, and that is
        /// something the reader can act on by raising the ceiling.
        /// </para>
        /// </remarks>
        public static string TransformNote(SpectrumFrame frame)
        {
            if (frame == null)
            {
                throw new ArgumentNullException(nameof(frame));
            }

            return frame.TransformWasCapped
                ? "   FFT " + frame.TransformLength.ToString(CultureInfo.CurrentCulture) +
                  " (capped)"
                : string.Empty;
        }

        /// <summary>
        /// The annotation for a noise-corrected trace (<c>REQ-DSP-024</c>).
        /// </summary>
        /// <param name="frame">The frame to describe.</param>
        /// <returns>Empty when no correction was applied.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="frame"/> is null.</exception>
        /// <remarks>
        /// A corrected trace and an uncorrected one of the same signal differ most where the signal
        /// is weakest, which is where someone is most likely to be reading a number off the screen
        /// and least likely to remember which setting was in force.
        /// </remarks>
        public static string NoiseCorrectionNote(SpectrumFrame frame)
        {
            if (frame == null)
            {
                throw new ArgumentNullException(nameof(frame));
            }

            return frame.NoiseCorrected ? "   Noise corr" : string.Empty;
        }

        /// <summary>
        /// Refreshes the annotation from a frame, leaving alone anything the user is editing.
        /// </summary>
        /// <remarks>
        /// The exemption matters more than it looks: a measurement updating sixty times a second
        /// would otherwise overwrite a half-typed entry between two keystrokes.
        /// </remarks>
        private void UpdateAnnotation(SpectrumFrame frame)
        {
            _suppressParameterEvents = true;

            try
            {
                Set(_resolutionBandwidth, frame.ResolutionBandwidthHz);
                Set(_centerFrequency, frame.CenterFrequencyHz);

                if (frame.PointCount > 1 && frame.SpanHz > 0.0)
                {
                    Set(_mainTime, (frame.PointCount - 1) / frame.SpanHz);
                }
            }
            finally
            {
                _suppressParameterEvents = false;
            }

            // The aperture and the unwrap reference appear only for the formats they bear on
            // (REQ-DSP-045, REQ-DSP-044), so the annotation never describes a setting that had no
            // effect on what is being looked at.
            string formatNote = _formatOptions.Describe(CurrentFormat);

            _analysisText.Text =
                WindowText.Describe(frame.Window) + "   " +
                frame.PointCount.ToString(CultureInfo.CurrentCulture) + " pts" +
                AveragingNote(frame) + "   Span " + Frequency(frame.SpanHz) +
                TransformNote(frame) + NoiseCorrectionNote(frame) +
                (formatNote.Length == 0 ? string.Empty : "   " + formatNote);

            if (_markers.Count == 0)
            {
                // No marker: the readout slot shows the peak, which is what a user reaches for
                // first and is labelled so it cannot be mistaken for a marker.
                int peak = frame.IndexOfPeak();
                _markerText.Text = peak < 0
                    ? string.Empty
                    : "Peak  " + Frequency(frame.FrequencyAt(peak)) + Environment.NewLine +
                      Level(frame.LevelsDbm[peak]);
            }
            else
            {
                _markerText.Text = _markerReadout;
            }
        }

        private static void Set(HotSpot spot, double value)
        {
            if (spot.IsEditing)
            {
                return;
            }

            var numeric = (NumericHotSpotValue)spot.Value;
            numeric.Value = value;
            spot.Refresh();
        }

        private static string Level(double dbm) =>
            dbm <= AmplitudeScale.FloorDbm
                ? "-- dBm"
                : dbm.ToString("+0.00;-0.00;0.00", CultureInfo.CurrentCulture) + " dBm";

        /// <summary>Engineering-notation frequency, to the resolution a spectrum axis needs.</summary>
        /// <remarks>
        /// Six decimals, where the hardware pane uses three: an axis end and a marker readout are
        /// where a hertz matters, and rounding them to the pane's precision would make a 1 Hz span
        /// read as though both ends were the same frequency.
        /// </remarks>
        private static string Frequency(double hertz) => EngineeringText.Frequency(hertz, 6);
    }
}
