using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OpenVSA.Core.Threading;
using OpenVSA.Dsp.Spectrum;

namespace OpenVSA.Ui.Rendering
{
    /// <summary>
    /// The plot surface: a rasterised graticule and trace with WPF text in the annotation band.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The split is the one <c>REQ-UI-010</c> and <c>REQ-UI-042</c> together force. Trace geometry
    /// goes through <see cref="PlotRasterizer"/> into a <see cref="WriteableBitmap"/>, because at
    /// the point counts of <c>REQ-NFR-021</c> nothing else keeps up. Annotation is real
    /// <see cref="TextBlock"/>s on top, because the hot spots of <c>REQ-UI-042</c> need
    /// hit-testing, hover feedback and in-place editing — all of which are cheap against elements
    /// and expensive against rasterised glyphs, and that requirement is explicit that retrofitting
    /// the editing model later is the costly path.
    /// </para>
    /// <para>
    /// <strong>What this control does not do is compute.</strong> It receives a
    /// <see cref="TraceSnapshot"/> whose envelope was already decimated by the render marshal on the
    /// pump thread, and does nothing per acquired point — only per pixel. That is what keeps the
    /// dispatcher inside <c>REQ-NFR-010</c>'s "no DSP, no blocking wait over 16 ms".
    /// </para>
    /// <para>
    /// The annotation set here is the minimum a spectrum needs to be read: reference level, scale,
    /// window and RBW, the frequency axis ends, and the peak. <c>REQ-UI-040</c>'s full catalogue of
    /// annotation positions and <c>REQ-UI-042</c>'s click-to-edit behaviour are later work, and the
    /// text elements exist here so that work has something to attach to.
    /// </para>
    /// </remarks>
    public sealed class TracePlot : Grid
    {
        /// <summary>Annotation band thickness, in device-independent pixels.</summary>
        public const double AnnotationBandDip = 44.0;

        /// <summary>Vertical scale, in dB per graticule division.</summary>
        public const double DecibelsPerDivision = 10.0;

        private readonly Image _image;
        private readonly TextBlock _levelText;
        private readonly TextBlock _peakText;
        private readonly TextBlock _analysisText;
        private readonly TextBlock _startText;
        private readonly TextBlock _spanText;
        private readonly TextBlock _stopText;

        private PlotPalette _palette = PlotPalette.Dark;
        private WriteableBitmap _bitmap;
        private PixelSurface _surface;
        private PlotLayout _layout;
        private byte[] _transfer;
        private double _topDbm = 20.0;
        private int _marginPixels = 48;
        private Size _builtFor = Size.Empty;

        /// <summary>Creates an empty plot.</summary>
        public TracePlot()
        {
            _image = new Image
            {
                Stretch = Stretch.Fill,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
            };

            RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.NearestNeighbor);
            RenderOptions.SetEdgeMode(_image, EdgeMode.Aliased);
            Children.Add(_image);

            _levelText = AddAnnotation(HorizontalAlignment.Left, VerticalAlignment.Top);
            _peakText = AddAnnotation(HorizontalAlignment.Center, VerticalAlignment.Top);
            _analysisText = AddAnnotation(HorizontalAlignment.Right, VerticalAlignment.Top);
            _startText = AddAnnotation(HorizontalAlignment.Left, VerticalAlignment.Bottom);
            _spanText = AddAnnotation(HorizontalAlignment.Center, VerticalAlignment.Bottom);
            _stopText = AddAnnotation(HorizontalAlignment.Right, VerticalAlignment.Bottom);

            ApplyPalette();
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

        /// <summary>Raised when the graticule changes width, and so the column count to decimate to.</summary>
        public event EventHandler GraticuleColumnsChanged;

        /// <summary>
        /// Pixel columns across the graticule: what a <see cref="RenderMarshal"/> must decimate to.
        /// </summary>
        public int GraticuleColumns => _layout == null ? 0 : _layout.Graticule.Width;

        /// <summary>The colours of <c>REQ-UI-010</c>'s four zones.</summary>
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
        public double BottomDbm => _topDbm - DecibelsPerDivision * (_layout == null ? 10 : _layout.VerticalDivisions);

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
            // axis rather than sending the trace off the top of the screen.
            if (Math.Abs(snapshot.Spectrum.ReferenceLevelDbm - _topDbm) > 1e-9)
            {
                _topDbm = snapshot.Spectrum.ReferenceLevelDbm;
                BuildLayout();
            }

            if (snapshot.Columns != _layout.Graticule.Width)
            {
                return false;
            }

            Redraw(snapshot);
            return true;
        }

        /// <summary>Draws an empty graticule, discarding any trace.</summary>
        public void Clear()
        {
            ThreadAffinity.AssertOnUiThread("Clearing a trace");
            Redraw(null);

            _levelText.Text = string.Empty;
            _peakText.Text = string.Empty;
            _analysisText.Text = string.Empty;
            _startText.Text = string.Empty;
            _spanText.Text = string.Empty;
            _stopText.Text = string.Empty;
        }

        private TextBlock AddAnnotation(HorizontalAlignment horizontal, VerticalAlignment vertical)
        {
            var text = new TextBlock
            {
                HorizontalAlignment = horizontal,
                VerticalAlignment = vertical,
                Margin = new Thickness(8.0, 6.0, 8.0, 6.0),
                FontFamily = new FontFamily("Consolas"),
                FontSize = 11.0,
                IsHitTestVisible = false,
                TextAlignment = horizontal == HorizontalAlignment.Right
                    ? TextAlignment.Right
                    : TextAlignment.Left,
            };

            Children.Add(text);
            return text;
        }

        private void ApplyPalette()
        {
            var brush = new SolidColorBrush(ToMediaColor(_palette.Annotation));
            brush.Freeze();

            foreach (UIElement child in Children)
            {
                var text = child as TextBlock;
                if (text != null)
                {
                    text.Foreground = brush;
                }
            }

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
                _topDbm - DecibelsPerDivision * 10.0);
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

        private void UpdateAnnotation(SpectrumFrame frame)
        {
            _levelText.Text =
                "Ref " + Level(frame.ReferenceLevelDbm) + Environment.NewLine +
                DecibelsPerDivision.ToString("0", CultureInfo.CurrentCulture) + " dB/div";

            int peak = frame.IndexOfPeak();
            _peakText.Text = peak < 0
                ? string.Empty
                : "Peak  " + Frequency(frame.FrequencyAt(peak)) + "   " + Level(frame.LevelsDbm[peak]);

            _analysisText.Text =
                WindowText.Describe(frame.Window) + Environment.NewLine +
                "RBW " + Frequency(frame.ResolutionBandwidthHz) + "   " +
                frame.PointCount.ToString(CultureInfo.CurrentCulture) + " pts";

            _startText.Text = "Start " + Frequency(frame.StartFrequencyHz);
            _stopText.Text = "Stop " + Frequency(frame.StopFrequencyHz);
            _spanText.Text =
                "Center " + Frequency(frame.CenterFrequencyHz) + "   Span " + Frequency(frame.SpanHz);
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
