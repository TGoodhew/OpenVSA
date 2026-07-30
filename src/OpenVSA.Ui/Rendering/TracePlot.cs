using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OpenVSA.Core.Threading;
using OpenVSA.Dsp.Spectrum;
using OpenVSA.Demod.Results;
using OpenVSA.Measurement.Limits;
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

        /// <summary>Graticule divisions down the screen unless configured otherwise.</summary>
        public const int DefaultVerticalDivisions = PlotLayout.DefaultDivisions;

        /// <summary>Graticule divisions across the screen unless configured otherwise.</summary>
        public const int DefaultHorizontalDivisions = PlotLayout.DefaultDivisions;

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
        private readonly List<FrameworkElement> _measurementAnnotation =
            new List<FrameworkElement>();

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
        private int _horizontalDivisions = DefaultHorizontalDivisions;
        private int _verticalDivisions = DefaultVerticalDivisions;
        private int _yReferencePercent = ReferencePosition.TopPercent;
        private int _xReferencePercent = ReferencePosition.DefaultXPercent;
        private bool _showAnnotation = true;
        private bool _showGridLines = true;
        private TraceAxis _axis;
        private Size _builtFor = Size.Empty;
        private bool _suppressParameterEvents;
        private LimitTest _limitTest;
        private LimitColours _limitColours = new LimitColours();

        private ResultTraceKind _resultKind = ResultTraceKind.None;
        private SymbolTrace _result;
        private IdealStateOverlay _idealStates = IdealStateOverlay.Crosshair;
        private EyeComponent _eyeComponent = EyeComponent.InPhase;
        private double _eyeLength = EyeRasterizer.DefaultLengthSymbols;

        private TraceAccumulator _accumulator = TraceAccumulator.None;
        private Spectrogram _history;
        private SpectrogramMarkers _spectrogramMarkers;
        private SpectrogramColourMap _spectrogramMap = SpectrogramColourMap.Default;
        private SpectrogramLevels _spectrogramLevels = new SpectrogramLevels(-100.0, 0.0);
        private double _spectrogramThresholdBelowTopDb = double.NaN;
        private double _spectrogramThresholdDbm = SpectrogramLevels.NoThresholdDbm;
        private bool _spectrogramEnhance;
        private PlotColor _spectrogramMarkerColour = new PlotColor(0xFF, 0x40, 0x40);
        private PlotColor _traceSelectColour = new PlotColor(0x40, 0xC0, 0xFF);

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

            // Deliberately left in its own column, unlike the bottom band. The top band has the
            // scale readouts on its left and the marker readout on its right, so a centred stack
            // given more width runs over one or the other - both were tried, and both interleave
            // illegibly. Where the bottom band has a free column either side, this one has none.

            _markerText = NewLabel(HorizontalAlignment.Right);
            Place(_markerText, 0, 2, HorizontalAlignment.Right, VerticalAlignment.Top);

            _bottomScale = NewHotSpot(string.Empty, HorizontalAlignment.Left);
            Place(_bottomScale, 2, 0, HorizontalAlignment.Left, VerticalAlignment.Bottom);

            _centerFrequency = NewHotSpot("Center ", HorizontalAlignment.Center);
            _mainTime = NewHotSpot("Time ", HorizontalAlignment.Center);
            SpanFullWidth(
                AddStack(Orientation.Horizontal, 2, 1, HorizontalAlignment.Center,
                    VerticalAlignment.Bottom, _centerFrequency, _mainTime));

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

            // REQ-UI-010 and REQ-UI-021 divide the annotation between two colours, and this is where
            // the division is declared. Everything above describes THIS TRACE -- its Y scale, its
            // format, its averaging, its marker readout -- and takes the trace's own colour, which is
            // REQ-UI-021's visual signature. These four describe THE MEASUREMENT: the X axis, the
            // record length, and two properties of the acquisition every trace in an overlay shares.
            // They take the Annotation colour, because painting the centre frequency in trace A's
            // colour says it belongs to trace A, and it does not.
            _measurementAnnotation.Add(_resolutionBandwidth);
            _measurementAnnotation.Add(_triggerChannel);
            _measurementAnnotation.Add(_centerFrequency);
            _measurementAnnotation.Add(_mainTime);

            // The rubber band drawn while a region is being dragged (REQ-DSP-023). A sibling of
            // the rasterised image rather than something painted into it: the image is redrawn
            // from the trace on every frame, and a band painted there would flicker at the update
            // rate or be lost to the next acquisition.
            _band = new System.Windows.Shapes.Rectangle
            {
                Visibility = Visibility.Collapsed,
                IsHitTestVisible = false,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                StrokeThickness = 1.0,
            };

            SetRowSpan(_band, 3);
            SetColumnSpan(_band, 3);
            SetRow(_band, 0);
            SetColumn(_band, 0);
            Children.Add(_band);

            BuildValues();
            ApplyPalette();
        }

        private readonly System.Windows.Shapes.Rectangle _band;
        private double _dragFromX = double.NaN;
        private double _dragFromY = double.NaN;

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

        /// <summary>
        /// The limit test whose failures recolour the trace, or <c>null</c> for none
        /// (<c>REQ-UI-023</c>).
        /// </summary>
        public LimitTest LimitTest
        {
            get { return _limitTest; }

            set
            {
                _limitTest = value;
                Redraw(_snapshot);
            }
        }

        /// <summary>The four limit colours (<c>REQ-UI-023</c>).</summary>
        /// <exception cref="ArgumentNullException">The value is null.</exception>
        public LimitColours LimitColours
        {
            get { return _limitColours; }

            set
            {
                if (value == null)
                {
                    throw new ArgumentNullException(nameof(value));
                }

                _limitColours = value;
                Redraw(_snapshot);
            }
        }

        /// <summary>Level at the top of the graticule, in dBm. Follows the reference level.</summary>
        public double TopDbm => _topDbm;

        /// <summary>
        /// The axis the current format needs (<c>REQ-DSP-041</c>, <c>REQ-TRC-001</c>).
        /// </summary>
        /// <remarks>
        /// Log magnitude keeps the reference-driven decibel axis the user set; every other format
        /// is a different quantity in a different unit and gets its own. Drawing volts on a decibel
        /// axis puts the whole trace in the bottom pixel row, which reads as no signal rather than
        /// as the wrong axis.
        /// </remarks>
        public TraceAxis Axis => _axis;

        /// <summary>Level at the bottom of the graticule, in dBm.</summary>
        public double BottomDbm => _topDbm - FullScaleDb;

        /// <summary>The whole vertical range of the graticule, in dB.</summary>
        public double FullScaleDb => _decibelsPerDivision * _verticalDivisions;

        /// <summary>Graticule divisions down the screen (<c>REQ-UI-012</c>).</summary>
        public int VerticalDivisions => _verticalDivisions;

        /// <summary>Graticule divisions across the screen (<c>REQ-UI-012</c>).</summary>
        public int HorizontalDivisions => _horizontalDivisions;

        /// <summary>
        /// Where the reference line sits, 0 at the bottom of the grid through 100 at the top
        /// (<c>REQ-UI-013</c>).
        /// </summary>
        /// <remarks>
        /// <para>
        /// Setting it moves the axis, not the trace: the reference level stays where it is and the
        /// top and bottom of the graticule move around it. At 100 % the reference level is the top
        /// of the grid, which is what puts the reference-level annotation at top left; at 50 % it is
        /// the middle, which is what a signed or IQ display needs.
        /// </para>
        /// <para>
        /// <see cref="SetFormat"/> resets this to the format's own default, because that is what the
        /// requirement means by "defaulting to 100 % for Log Mag ... and 50 % for all other
        /// formats" — the default belongs to the format, not to the plot's first format.
        /// </para>
        /// </remarks>
        /// <exception cref="ArgumentOutOfRangeException">Outside 0 to 100.</exception>
        public int YReferencePercent
        {
            get { return _yReferencePercent; }

            set
            {
                ReferencePosition.Validate(value, nameof(value));

                if (_yReferencePercent == value)
                {
                    return;
                }

                _yReferencePercent = value;
                _topDbm = TopForReference(_referenceLevelDbm);

                RefreshScaleText();
                BuildLayout();
                Redraw(_snapshot);
            }
        }

        /// <summary>
        /// Whether trace annotation is drawn and, with it, whether the band is reserved
        /// (<c>REQ-UI-011</c>).
        /// </summary>
        /// <remarks>
        /// Turning it off reclaims the annotation band: the graticule rectangle grows, which is the
        /// requirement's criterion and the reason the setting exists. The trace indicator goes with
        /// it — <c>REQ-UI-040</c> counts it as trace annotation even though it is drawn inside the
        /// graticule. When <c>REQ-UI-007</c>'s fault and lock indicators arrive they will need to be
        /// exempted from this, because a warning that can be switched off by a display preference is
        /// not a warning.
        /// </remarks>
        public bool ShowAnnotation
        {
            get { return _showAnnotation; }

            set
            {
                if (_showAnnotation == value)
                {
                    return;
                }

                _showAnnotation = value;
                ApplyAnnotationVisibility();
                Rebuild(_builtFor);
            }
        }

        /// <summary>Whether the graticule lines are drawn (<c>REQ-UI-011</c>).</summary>
        /// <remarks>
        /// <para>
        /// Independent of <see cref="ShowAnnotation"/>: the rectangle keeps its size and its
        /// background colour, and only the lines go.
        /// </para>
        /// <para>
        /// Named for the graticule rather than for the requirement's menu entry, because this
        /// control derives from <see cref="System.Windows.Controls.Grid"/> and that already has a
        /// <c>ShowGridLines</c> — a design-time aid that draws dashed lines between layout cells.
        /// Two properties one letter apart, one of which silently draws something else, is worth
        /// avoiding even at the cost of the menu entry and the property no longer matching.
        /// </para>
        /// </remarks>
        public bool ShowGraticuleLines
        {
            get { return _showGridLines; }

            set
            {
                if (_showGridLines == value)
                {
                    return;
                }

                _showGridLines = value;
                Redraw(_snapshot);
            }
        }

        /// <summary>
        /// Applies the shared display options to this plot (<c>REQ-UI-011</c>, <c>REQ-UI-012</c>,
        /// <c>REQ-UI-013</c>).
        /// </summary>
        /// <param name="options">The options in force.</param>
        /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
        /// <remarks>
        /// Applied in one call rather than through five property setters so that a change of two
        /// settings costs one rebuild. The plot follows the options; it does not hold them.
        /// </remarks>
        public void ApplyDisplayOptions(TraceDisplayOptions options)
        {
            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            bool reflow = _showAnnotation != options.ShowAnnotation;

            _showAnnotation = options.ShowAnnotation;
            _showGridLines = options.ShowGridLines;
            _horizontalDivisions = options.HorizontalDivisions;
            _verticalDivisions = options.VerticalDivisions;
            _xReferencePercent = options.XReferencePercent;

            // The bottom of the axis is the top less the full scale, and the full scale just
            // changed with the division count - so the top has to be recomputed from the reference
            // rather than left where it was.
            _topDbm = TopForReference(_referenceLevelDbm);

            ApplyAnnotationVisibility();
            RefreshScaleText();

            if (reflow)
            {
                Rebuild(_builtFor);
            }
            else
            {
                BuildLayout();
                Redraw(_snapshot);
            }
        }

        /// <summary>
        /// The top of the graticule that puts a reference level at the reference position.
        /// </summary>
        /// <remarks>
        /// At 100 % the top <em>is</em> the reference level; at 50 % the reference is half a full
        /// scale below the top. The arithmetic is the whole of <c>REQ-UI-013</c>'s scaling half.
        /// </remarks>
        private double TopForReference(double referenceDbm) =>
            ReferencePosition.TopFor(referenceDbm, FullScaleDb, _yReferencePercent);

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

        // ---- Demodulation result displays (REQ-UI-050, REQ-UI-051) --------------------------------

        /// <summary>
        /// Which demodulation result this plot draws, if any (<c>REQ-DEM-080</c>'s catalogue).
        /// </summary>
        /// <remarks>
        /// Separate from <see cref="CurrentFormat"/> and from <see cref="Accumulator"/>, because it
        /// is a different kind of thing again: a format is a view of one spectrum, an accumulator
        /// builds across acquisitions, and a result trace draws something a demodulator produced.
        /// Folding it into <see cref="TraceFormat"/> would break that enumeration's own rule that
        /// every member is a pure function of the same calibrated spectrum.
        /// </remarks>
        public ResultTraceKind ResultKind
        {
            get { return _resultKind; }

            set
            {
                if (_resultKind == value)
                {
                    return;
                }

                _resultKind = value;
                Redraw(_snapshot);
            }
        }

        /// <summary>The demodulated result this plot draws, or <c>null</c>.</summary>
        public SymbolTrace Result
        {
            get { return _result; }

            set
            {
                _result = value;
                Redraw(_snapshot);
            }
        }

        /// <summary>How the ideal states are overlaid (<c>REQ-UI-050</c>).</summary>
        public IdealStateOverlay IdealStates
        {
            get { return _idealStates; }
            set { _idealStates = value; Redraw(_snapshot); }
        }

        /// <summary>The colours a constellation draws with.</summary>
        public ConstellationColours ConstellationColours { get; } = new ConstellationColours();

        /// <summary>The colours an eye draws with.</summary>
        public EyeColours EyeColours { get; } = new EyeColours();

        /// <summary>Which component an eye shows.</summary>
        public EyeComponent EyeComponent
        {
            get { return _eyeComponent; }
            set { _eyeComponent = value; Redraw(_snapshot); }
        }

        /// <summary>
        /// How many symbols an eye spans (<c>REQ-UI-051</c>: 0.1 to 10).
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">Outside the allowed range.</exception>
        public double EyeLengthSymbols
        {
            get { return _eyeLength; }

            set
            {
                if (!EyeRasterizer.IsLengthAllowed(value))
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(value), value,
                        "REQ-UI-051 allows an eye of " + EyeRasterizer.MinimumLengthSymbols +
                        " to " + EyeRasterizer.MaximumLengthSymbols + " symbols.");
                }

                _eyeLength = value;
                Redraw(_snapshot);
            }
        }

        /// <summary>Whether this plot is drawing a demodulation result rather than a spectrum.</summary>
        public bool IsShowingResult =>
            _resultKind != ResultTraceKind.None && _result != null && _result.SymbolCount > 0;

        /// <summary>What the last result render drew (<c>REQ-UI-050</c>'s primitive count).</summary>
        public ConstellationRender LastConstellationRender { get; private set; }

        /// <summary>What the last eye render drew (<c>REQ-UI-051</c>'s folds).</summary>
        public EyeRender LastEyeRender { get; private set; }

        /// <summary>
        /// Draws a demodulation result over the graticule.
        /// </summary>
        /// <remarks>
        /// <strong>The eye is not cleared between acquisitions and the constellation is.</strong>
        /// <c>REQ-UI-051</c> makes accumulation the eye's defining behaviour — "the VSA draws the
        /// first trace, then overlays the second trace, the third trace, and so on" —
        /// while a constellation shows the symbols of the result in front of it. The rasteriser
        /// leaves that decision here because it is a property of the display, not of the drawing.
        /// </remarks>
        private void DrawResult()
        {
            PixelRect graticule = _layout.Graticule;

            switch (_resultKind)
            {
                case ResultTraceKind.Constellation:
                case ResultTraceKind.IqVector:
                    LastConstellationRender = ConstellationRasterizer.Render(
                        _surface,
                        graticule,
                        _result,
                        ConstellationColours,
                        _idealStates,
                        _resultKind == ResultTraceKind.IqVector);
                    break;

                case ResultTraceKind.Eye:
                    LastEyeRender = EyeRasterizer.Render(
                        _surface, graticule, _result, _eyeComponent, _eyeLength, EyeColours);
                    break;
            }
        }

        // ---- Spectrogram (REQ-UI-054) -------------------------------------------------------------

        /// <summary>
        /// What this plot accumulates, and so what it draws (<c>REQ-TRC-001a</c>).
        /// </summary>
        /// <remarks>
        /// Separate from <see cref="CurrentFormat"/> and reached through
        /// <see cref="TraceAccumulator"/> rather than the format list, which is
        /// <c>REQ-UI-054</c>'s criterion and <c>REQ-TRC-001a</c>'s reason for existing. Setting it
        /// redraws; it does not clear the history, because deciding what to keep is
        /// <see cref="AccumulatingTrace"/>'s job and this is a display.
        /// </remarks>
        public TraceAccumulator Accumulator
        {
            get { return _accumulator; }

            set
            {
                if (_accumulator == value)
                {
                    return;
                }

                _accumulator = value;
                Redraw(_snapshot);
            }
        }

        /// <summary>
        /// The accumulated rows this plot draws when <see cref="Accumulator"/> is
        /// <see cref="TraceAccumulator.Spectrogram"/>.
        /// </summary>
        /// <remarks>
        /// Held by reference and read as it grows: the shell adds a row per sweep and the plot
        /// draws whatever is there when it next redraws. Copying it per frame would double the
        /// memory of the one structure in the display path whose size is measured in hundreds of
        /// megabytes.
        /// </remarks>
        public Spectrogram History
        {
            get { return _history; }

            set
            {
                if (ReferenceEquals(_history, value))
                {
                    return;
                }

                _history = value;
                _spectrogramMarkers = value == null ? null : new SpectrogramMarkers(value);

                Redraw(_snapshot);
            }
        }

        /// <summary>
        /// The two markers of <c>REQ-UI-054</c>, or <c>null</c> when there is no history.
        /// </summary>
        public SpectrogramMarkers SpectrogramMarkers => _spectrogramMarkers;

        /// <summary>The colour map the spectrogram is drawn with (<c>REQ-UI-024</c>).</summary>
        /// <exception cref="ArgumentNullException">The value is null.</exception>
        public SpectrogramColourMap SpectrogramMap
        {
            get { return _spectrogramMap; }

            set
            {
                if (value == null)
                {
                    throw new ArgumentNullException(nameof(value));
                }

                if (ReferenceEquals(_spectrogramMap, value))
                {
                    return;
                }

                _spectrogramMap = value;
                Redraw(_snapshot);
            }
        }

        /// <summary>
        /// How far below the loudest cell the display stops drawing (<c>REQ-UI-054</c>, Threshold).
        /// </summary>
        /// <remarks>
        /// <para>
        /// NaN draws every cell, which is where it starts. Raising it removes cells, which is the
        /// criterion.
        /// </para>
        /// <para>
        /// <strong>Relative to the loudest cell in the history, not to the top of the window
        /// Enhance produced.</strong> Enhance narrows the window onto the busiest levels — on a
        /// flat noise floor to about a decibel — so a threshold measured from that top would be
        /// below every cell and remove nothing. The screenshot showed precisely that: Enhance on,
        /// the ladder set to −40 dB, and the display unchanged.
        /// </para>
        /// </remarks>
        public double SpectrogramThresholdBelowTopDb
        {
            get { return _spectrogramThresholdBelowTopDb; }

            set
            {
                if (_spectrogramThresholdBelowTopDb.Equals(value))
                {
                    return;
                }

                _spectrogramThresholdBelowTopDb = value;
                Redraw(_snapshot);
            }
        }

        /// <summary>
        /// The level the last drawn spectrogram actually cut at, in dBm.
        /// </summary>
        /// <remarks>
        /// <see cref="SpectrogramLevels.NoThresholdDbm"/> when nothing is being hidden. Reported
        /// rather than recomputed, like <see cref="SpectrogramLevels"/>, so a reader can check what
        /// was drawn rather than what would be drawn now.
        /// </remarks>
        public double SpectrogramThresholdDbm => _spectrogramThresholdDbm;

        /// <summary>
        /// Whether the colour map is stretched about the busiest levels (<c>REQ-UI-054</c>,
        /// Enhance).
        /// </summary>
        public bool SpectrogramEnhance
        {
            get { return _spectrogramEnhance; }

            set
            {
                if (_spectrogramEnhance == value)
                {
                    return;
                }

                _spectrogramEnhance = value;
                Redraw(_snapshot);
            }
        }

        /// <summary>The spectrogram marker's colour (<c>REQ-UI-022</c>, per trace).</summary>
        public PlotColor SpectrogramMarkerColour
        {
            get { return _spectrogramMarkerColour; }
            set { _spectrogramMarkerColour = value; Redraw(_snapshot); }
        }

        /// <summary>The trace-select marker's colour (<c>REQ-UI-022</c>, per trace).</summary>
        public PlotColor TraceSelectColour
        {
            get { return _traceSelectColour; }
            set { _traceSelectColour = value; Redraw(_snapshot); }
        }

        /// <summary>
        /// The level window the last spectrogram was drawn with.
        /// </summary>
        /// <remarks>
        /// Reported rather than recomputed on demand, for the reason
        /// <c>SettingsDialog.FixedContentSize</c> is: a property that measured would answer a
        /// different question from the one the display answered, and Enhance is exactly the setting
        /// whose effect a reader wants to check against what was actually drawn.
        /// </remarks>
        public SpectrogramLevels SpectrogramLevels => _spectrogramLevels;

        /// <summary>
        /// How many pixels the last spectrogram painted with a cell's colour.
        /// </summary>
        /// <remarks>
        /// What makes "raising Threshold removes cells below it" assertable against the rendering
        /// rather than against the model alone.
        /// </remarks>
        public int SpectrogramCellsDrawn { get; private set; }

        /// <summary>Whether this plot is drawing a spectrogram rather than a trace.</summary>
        public bool IsShowingSpectrogram =>
            _accumulator == TraceAccumulator.Spectrogram && _history != null && _history.RowCount > 0;

        /// <summary>
        /// Moves one of the spectrogram markers to a point on the plot (<c>REQ-UI-054</c>).
        /// </summary>
        /// <param name="which">Which marker.</param>
        /// <param name="point">Where the gesture landed, in this element's coordinates.</param>
        /// <returns>Whether the marker moved.</returns>
        /// <remarks>
        /// Both coordinates of the point reach <see cref="SpectrogramMarkers.MoveTo"/>, which
        /// discards the one that is not on the marker's own axis. Filtering here instead would put
        /// the criterion in the view rather than in the thing the criterion is about.
        /// </remarks>
        public bool MoveSpectrogramMarker(SpectrogramMarkerKind which, Point point)
        {
            if (!IsShowingSpectrogram || _layout == null || _surface == null)
            {
                return false;
            }

            DpiScale dpi = VisualTreeHelper.GetDpi(this);
            PixelRect graticule = _layout.Graticule;

            int x = (int)Math.Round(point.X * dpi.DpiScaleX) - graticule.X;
            int y = (int)Math.Round(point.Y * dpi.DpiScaleY) - graticule.Y;

            SpectrumFrame newest = _history.Newest;

            int bin = TraceEnvelope.IndexFor(
                Clamp(x, 0, graticule.Width - 1), newest.PointCount, graticule.Width);

            int row = SpectrogramRasterizer.RowForY(
                Clamp(y, 0, graticule.Height - 1), graticule.Height, _history.RowCount);

            int rowBefore = _spectrogramMarkers.RowIndex;

            if (!_spectrogramMarkers.MoveTo(which, bin, row))
            {
                return false;
            }

            Redraw(_snapshot);

            // REQ-MKR-007: moving the trace-select marker to a history row is what makes a spectrum
            // trace show that row's data. Announced rather than acted on here, because the trace that
            // shows it is a different window -- this plot is drawing the spectrogram.
            if (_spectrogramMarkers.RowIndex != rowBefore)
            {
                SelectedHistoryRowChanged?.Invoke(this, EventArgs.Empty);
            }

            return true;
        }

        /// <summary>
        /// Raised when the trace-select marker lands on a different history row
        /// (<c>REQ-MKR-007</c>).
        /// </summary>
        public event EventHandler SelectedHistoryRowChanged;

        /// <summary>Which history row the trace-select marker is on, or <c>-1</c>.</summary>
        public int SelectedHistoryRow =>
            IsShowingSpectrogram ? _spectrogramMarkers.RowIndex : -1;

        /// <summary>
        /// The spectrum captured at the trace-select marker's row (<c>REQ-MKR-007</c>).
        /// </summary>
        /// <remarks>
        /// That row's own frame, not a rendering of the map: the requirement asks for the trace to
        /// show "the data captured at that time", so a spectrum drawn from the colour-mapped cells
        /// would be a picture of a picture, and wrong in any format but log magnitude.
        /// </remarks>
        public SpectrumFrame SelectedHistoryFrame =>
            IsShowingSpectrogram ? _spectrogramMarkers.SelectedRow : null;

        private static int Clamp(int value, int low, int high) =>
            value < low ? low : (value > high ? high : value);

        // ---- The display range (REQ-UI-040, #397) -----------------------------------------------

        private double _displayStartHz = double.NaN;
        private double _displayStopHz = double.NaN;

        /// <summary>
        /// The frame as measured, before any display magnification (#397).
        /// </summary>
        /// <remarks>
        /// <strong>Kept because the annotation must describe the measurement, not the
        /// picture.</strong> Under magnification <c>_snapshot.Spectrum</c> is the windowed frame,
        /// and annotating the centre and span from it would report the magnified band as the
        /// measured one — which is the same lie the other way round, and the reason Scale X was
        /// refused for as long as it was.
        /// </remarks>
        private SpectrumFrame _measuredFrame;

        /// <summary>
        /// Takes ownership of the frame this plot will keep redrawing (<c>REQ-NFR-002</c>).
        /// </summary>
        /// <param name="frame">The newly measured frame, or null to let go of the last one.</param>
        /// <remarks>
        /// <para>
        /// A plot redraws from the frame it was last shown -- on resize, on a format change, on a
        /// magnification -- long after the pump callback that produced it returned. It therefore
        /// holds a share of the pooled buffer rather than borrowing one, and gives up the previous
        /// share as it takes the new one.
        /// </para>
        /// <para>
        /// The re-decimated snapshot built by <c>SnapshotFor</c> needs no share of its own: it is
        /// built from this same frame, or from a windowed derivative that owns its own array.
        /// </para>
        /// </remarks>
        private void HoldMeasured(SpectrumFrame frame)
        {
            if (ReferenceEquals(_measuredFrame, frame))
            {
                return;
            }

            SpectrumFrame previous = _measuredFrame;

            frame?.Retain();
            _measuredFrame = frame;
            previous?.Release();
        }

        /// <summary>
        /// Whether the display is magnified into part of the measured span (#397).
        /// </summary>
        /// <remarks>
        /// The one question every other piece of this answers to. When it is false the centre and
        /// span readouts describe both the measurement and the display, and there is nothing extra
        /// to annotate; when it is true they describe only the measurement and the displayed range
        /// has to be said out loud.
        /// </remarks>
        public bool IsMagnified => !double.IsNaN(_displayStartHz) && !double.IsNaN(_displayStopHz);

        /// <summary>The first frequency drawn, in hertz, or the frame's own start.</summary>
        public double DisplayStartHz =>
            IsMagnified ? _displayStartHz : (_measuredFrame?.StartFrequencyHz ?? double.NaN);

        /// <summary>The last frequency drawn, in hertz, or the frame's own stop.</summary>
        public double DisplayStopHz =>
            IsMagnified ? _displayStopHz : (_measuredFrame?.StopFrequencyHz ?? double.NaN);

        /// <summary>The centre of the displayed range, in hertz.</summary>
        public double DisplayCentreHz => (DisplayStartHz + DisplayStopHz) / 2.0;

        /// <summary>The width of the displayed range, in hertz.</summary>
        public double DisplaySpanHz => DisplayStopHz - DisplayStartHz;

        /// <summary>
        /// Magnifies the display into part of the measured span, without re-measuring (#397).
        /// </summary>
        /// <param name="startHz">First frequency to draw.</param>
        /// <param name="stopHz">Last frequency to draw.</param>
        /// <returns>Whether the range was accepted.</returns>
        /// <remarks>
        /// <para>
        /// <strong>This changes the display and nothing else.</strong> The measurement keeps its
        /// centre, its span and its resolution bandwidth; what changes is which of the points
        /// already acquired are spread across the graticule. That is the whole difference between
        /// this and Set centre and span, which re-analyses the dragged band and gives more
        /// resolution rather than the same points magnified.
        /// </para>
        /// <para>
        /// Refused when it would leave fewer than two points to draw. A magnification past the
        /// resolution of the data is not more detail — it is one point stretched across the
        /// screen, which looks like a measurement and is not one.
        /// </para>
        /// </remarks>
        public bool SetDisplayRange(double startHz, double stopHz)
        {
            if (double.IsNaN(startHz) || double.IsNaN(stopHz) || stopHz <= startHz)
            {
                return false;
            }

            SpectrumFrame frame = _measuredFrame;

            if (frame == null)
            {
                return false;
            }

            double first = Math.Max(startHz, frame.StartFrequencyHz);
            double last = Math.Min(stopHz, frame.StopFrequencyHz);

            if (last <= first || (last - first) / frame.BinWidthHz < 2.0)
            {
                return false;
            }

            _displayStartHz = first;
            _displayStopHz = last;

            Rebuild(_builtFor);
            return true;
        }

        /// <summary>Returns the display to the whole measured span (#397).</summary>
        public void ClearDisplayRange()
        {
            if (!IsMagnified)
            {
                return;
            }

            _displayStartHz = double.NaN;
            _displayStopHz = double.NaN;

            Rebuild(_builtFor);
        }

        /// <summary>
        /// The analysis annotation line, for asserting what it says.
        /// </summary>
        /// <remarks>
        /// The window, point count, span and — when the display is magnified — the band actually
        /// drawn. Read from the element the user sees.
        /// </remarks>
        public string AnalysisText => _analysisText.Text ?? string.Empty;

        /// <summary>Whether this plot is holding a frame to draw (#395).</summary>
        public bool HasTrace => _snapshot != null;

        /// <summary>
        /// How many columns the held snapshot was decimated to, or zero.
        /// </summary>
        /// <remarks>
        /// A snapshot whose width no longer matches the graticule is one <see cref="Show"/> will
        /// refuse, so this is what says whether a resize left the plot able to take the next frame.
        /// </remarks>
        public int CurrentSnapshotColumns => _snapshot == null ? 0 : _snapshot.Columns;

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
        /// Draws the trace in a different format (<c>REQ-DSP-041</c>, <c>REQ-TRC-001</c>).
        /// </summary>
        /// <param name="format">The format to draw in.</param>
        /// <returns><c>true</c> if the format changed.</returns>
        /// <exception cref="ArgumentOutOfRangeException">Not a format this plot offers.</exception>
        /// <remarks>
        /// <para>
        /// Goes through the format hot spot rather than round it, so the annotation and the drawing
        /// cannot disagree about which format is on screen — the same reason
        /// <c>REQ-UI-042</c>'s edits go through the settings pane.
        /// </para>
        /// <para>
        /// Nothing is recomputed. <c>REQ-TRC-001</c>'s rule is that a format change is a different
        /// view of one computation, so the held frame is simply re-rendered; that is what lets four
        /// trace windows show four formats of a single acquisition.
        /// </para>
        /// </remarks>
        public bool SetFormat(TraceFormat format)
        {
            string wanted = TraceFormatText.Describe(format);
            int index = -1;

            for (int i = 0; i < TraceFormatText.Names.Count; i++)
            {
                if (string.Equals(TraceFormatText.Names[i], wanted, StringComparison.Ordinal))
                {
                    index = i;
                    break;
                }
            }

            if (index < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(format), format, "This plot does not offer that format.");
            }

            var choice = _format.Value as ChoiceHotSpotValue;

            if (choice == null || choice.SelectedIndex == index)
            {
                return false;
            }

            choice.SelectedIndex = index;
            _format.Refresh();

            // REQ-UI-013: the Y reference default belongs to the format, so changing format takes
            // the format's default. Log Mag hangs from the top of the grid; Real, Phase and IQ are
            // signed and want zero in the middle, and leaving a spectrum's 100 % in force would put
            // half of every constellation off the screen.
            ApplyReferenceDefaultFor(format);
            RebuildAxis(_snapshot);

            if (_snapshot != null)
            {
                Redraw(_snapshot);
            }

            UpdateAnnotationIfPossible();

            return true;
        }

        /// <summary>
        /// Moves the reference line to the default position for a format (<c>REQ-UI-013</c>).
        /// </summary>
        /// <param name="format">The format now in force.</param>
        private void ApplyReferenceDefaultFor(TraceFormat format)
        {
            int wanted = ReferencePosition.DefaultYPercentFor(format);

            if (_yReferencePercent == wanted)
            {
                return;
            }

            _yReferencePercent = wanted;
            _topDbm = TopForReference(_referenceLevelDbm);

            RefreshScaleText();
            BuildLayout();
        }

        /// <summary>
        /// Shows or hides every piece of trace annotation (<c>REQ-UI-011</c>).
        /// </summary>
        /// <remarks>
        /// Collapsed rather than hidden, and the band rows are taken to zero height as well. Merely
        /// making the text invisible would leave the band reserved and the graticule the size it
        /// always was, which is exactly the failure the requirement's criterion names.
        /// </remarks>
        private void ApplyAnnotationVisibility()
        {
            Visibility visibility = _showAnnotation ? Visibility.Visible : Visibility.Collapsed;

            foreach (FrameworkElement element in _annotation)
            {
                element.Visibility = visibility;
            }

            _markerText.Visibility = visibility;

            // The fault indicators are NOT annotation and are exempt from hiding it
            // (REQ-UI-007). Every string they carry means the number on screen is wrong — an
            // overloaded input, an unlocked reference, an uncalibrated state — and the requirement
            // is explicit that these must not be buried. Hiding them because a user wanted a clean
            // picture would be a worse burial than the event log it already forbids.
            _indicatorText.Visibility = Visibility.Visible;

            // Inside the graticule either way, so the offset follows the band that is or is not
            // there: with the annotation off the graticule starts at the top of the control, and a
            // margin still allowing for a band would push the indicator into the trace.
            double inset = _showAnnotation ? AnnotationBandDip + 6.0 : 6.0;

            _indicatorText.Margin = new Thickness(0.0, inset, inset, 0.0);

            var band = new GridLength(_showAnnotation ? AnnotationBandDip : 0.0);

            RowDefinitions[0].Height = band;
            RowDefinitions[2].Height = band;
        }

        /// <summary>
        /// Rebuilds the vertical axis for the format now in force (<c>REQ-DSP-041</c>).
        /// </summary>
        /// <param name="snapshot">The frame to range against, or <c>null</c> for none.</param>
        /// <remarks>
        /// Ranged against the full-resolution trace rather than against the decimated envelope, so
        /// the axis does not jump when the window is resized and the column count changes with it.
        /// </remarks>
        private void RebuildAxis(TraceSnapshot snapshot)
        {
            TraceFormat format = CurrentFormat;
            float[] values = null;

            if (snapshot != null && format != TraceFormat.LogMagnitude &&
                TraceAxis.IsLineTrace(format))
            {
                values = new float[snapshot.Spectrum.PointCount];
                snapshot.Spectrum.Format(format, new Span<float>(values), _formatOptions);
            }

            TraceAxis axis = TraceAxis.For(
                format,
                values == null ? ReadOnlySpan<float>.Empty : new ReadOnlySpan<float>(values),
                _referenceLevelDbm,
                _decibelsPerDivision,
                _verticalDivisions,
                _yReferencePercent);

            bool moved = _axis == null ||
                         Math.Abs(_axis.TopValue - axis.TopValue) > 1e-12 ||
                         Math.Abs(_axis.BottomValue - axis.BottomValue) > 1e-12 ||
                         !string.Equals(_axis.Unit, axis.Unit, StringComparison.Ordinal);

            _axis = axis;

            if (moved)
            {
                RefreshScaleText();
                BuildLayout();
            }
        }

        /// <summary>
        /// Scales the vertical axis to the trace on screen (<c>REQ-UI-065</c>'s Ctrl+W).
        /// </summary>
        /// <returns>Whether there was a trace to scale to.</returns>
        /// <remarks>
        /// <para>
        /// For log magnitude this moves the top of the graticule to the next whole division above
        /// the peak, which keeps the per-division reading the user chose and puts the signal just
        /// under the top line — what an analyser's auto-scale does. The other formats are ranged
        /// from their data on every frame already, so for those this is a re-range now rather than
        /// at the next frame.
        /// </para>
        /// <para>
        /// Ranged against the full-resolution trace, not the decimated envelope: a peak that fell
        /// between two columns would otherwise set the axis slightly low and clip the very thing
        /// the user pressed the key to see.
        /// </para>
        /// </remarks>
        /// <summary>
        /// Scales the vertical axis to hold a range of levels (<c>REQ-UI-063</c>'s Scale Y).
        /// </summary>
        /// <param name="topDbm">The level the top of the graticule should hold.</param>
        /// <param name="bottomDbm">The level the bottom should hold.</param>
        /// <returns><c>true</c> if the axis moved.</returns>
        /// <remarks>
        /// <para>
        /// The per-division step is rounded <em>up</em> to the 1-2-5 ladder, so the region asked
        /// for always fits and the graticule stays readable. An axis of 3.7 dB per division holds
        /// exactly what was dragged and is a scale nobody can read a level off — which is the point
        /// of a graticule.
        /// </para>
        /// <para>
        /// The measurement is not touched. This is a display operation, and the whole reason it is
        /// separate from setting the centre frequency and span is that the two must not be
        /// confusable.
        /// </para>
        /// </remarks>
        public bool ScaleTo(double topDbm, double bottomDbm)
        {
            ThreadAffinity.AssertOnUiThread("Scaling a trace");

            if (double.IsNaN(topDbm) || double.IsNaN(bottomDbm) || bottomDbm >= topDbm)
            {
                return false;
            }

            double perDivision = TraceAxis.NiceStep(
                (topDbm - bottomDbm) / Math.Max(1, _verticalDivisions));

            if (!(perDivision > 0.0))
            {
                return false;
            }

            _decibelsPerDivision = perDivision;
            _topDbm = Math.Ceiling(topDbm / perDivision) * perDivision;

            BuildPerDivisionChoices();
            _perDivisionIsDecibels = true;
            RefreshScaleText();
            BuildLayout();

            if (_snapshot != null)
            {
                Redraw(_snapshot);
            }

            return true;
        }

        public bool AutoScale()
        {
            ThreadAffinity.AssertOnUiThread("Auto-scaling");

            if (_snapshot == null)
            {
                return false;
            }

            if (CurrentFormat != TraceFormat.LogMagnitude)
            {
                RebuildAxis(_snapshot);
                Redraw(_snapshot);
                return true;
            }

            double peak = double.NegativeInfinity;

            foreach (float level in _snapshot.Spectrum.LevelsDbm)
            {
                if (!float.IsNaN(level) && level > peak)
                {
                    peak = level;
                }
            }

            if (double.IsInfinity(peak))
            {
                return false;
            }

            _topDbm = Math.Ceiling(peak / _decibelsPerDivision) * _decibelsPerDivision;

            RefreshScaleText();
            BuildLayout();
            Redraw(_snapshot);

            return true;
        }

        /// <summary>Refreshes the annotation when there is a frame behind it to describe.</summary>
        private void UpdateAnnotationIfPossible()
        {
            if (_snapshot != null)
            {
                UpdateAnnotation(_snapshot.Spectrum);
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

        /// <summary>
        /// The annotation that describes the measurement rather than this trace (<c>REQ-UI-010</c>).
        /// </summary>
        /// <remarks>
        /// The X axis, the record length, and the two acquisition properties every trace in an
        /// overlay shares. These carry <see cref="PlotPalette.Annotation"/>; everything else in
        /// <see cref="AnnotationElements"/> carries <see cref="PlotPalette.Trace"/>, which is
        /// <c>REQ-UI-021</c>. Exposed so that the division can be sampled from a rendered frame
        /// rather than taken on trust — the two colours being genuinely independent is the half of
        /// <c>REQ-UI-010</c> that a shared brush would quietly break.
        /// </remarks>
        public IReadOnlyList<FrameworkElement> MeasurementAnnotationElements =>
            _measurementAnnotation;

        /// <summary>
        /// The active-marker readout drawn above the grid (<c>REQ-MKR-006</c>, <c>REQ-UI-040</c>).
        /// </summary>
        /// <remarks>
        /// Exposed so that the requirement's comparison — this and the Markers window row must show
        /// the same values for the same marker — can be made against what is on screen rather than
        /// against whatever the shell believes it passed in.
        /// </remarks>
        public string MarkerReadoutText => _markerReadout;

        /// <summary>The element holding the trace indicator strings (<c>REQ-UI-041</c>).</summary>
        public FrameworkElement IndicatorElement => _indicatorText;

        /// <summary>
        /// What the fault indicators currently say (<c>REQ-UI-007</c>).
        /// </summary>
        /// <remarks>
        /// Read from the element the user sees, so a test asserting a condition is "on the trace
        /// rather than only in the event log" is reading the trace.
        /// </remarks>
        public string IndicatorText => _indicatorText.Text ?? string.Empty;

        /// <summary>
        /// The brush the trace's annotation is drawn in, which is the trace's own colour
        /// (<c>REQ-UI-021</c>).
        /// </summary>
        /// <remarks>
        /// Exposed so the criterion — "a trace's line and its annotation text sample to the same
        /// colour in the rendered frame" — can be asserted against the rendered control rather than
        /// against the palette it was given. Testing the palette would prove the two settings
        /// agreed, not that the two things on screen did.
        /// </remarks>
        public Brush TraceAnnotationBrush { get; private set; }

        /// <summary>
        /// Whether a drag across the trace selects a region to zoom into, rather than doing
        /// nothing (<c>REQ-DSP-023</c>'s <em>Select Area</em> trace tool).
        /// </summary>
        /// <remarks>
        /// Off by default. A plot that zoomed on every drag would make an imprecise click into a
        /// change of measurement, and the tool is a mode in the reference product for the same
        /// reason.
        /// </remarks>
        public bool SelectAreaEnabled { get; set; }

        /// <summary>The graticule's rectangle within this control, in device-independent pixels.</summary>
        /// <remarks>
        /// Zero band when the annotation is hidden, so a Select Area drag reaches the whole surface
        /// once the graticule has expanded into it (<c>REQ-UI-011</c>).
        /// </remarks>
        public Rect GraticuleBounds
        {
            get
            {
                double band = _showAnnotation ? AnnotationBandDip : 0.0;

                return new Rect(
                    band,
                    band,
                    Math.Max(0.0, ActualWidth - 2.0 * band),
                    Math.Max(0.0, ActualHeight - 2.0 * band));
            }
        }

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
                _topDbm = TopForReference(_referenceLevelDbm);
            }

            if (snapshot.Columns != _layout.Graticule.Width)
            {
                return false;
            }

            // A format this snapshot has no envelope for: the format changed between the pump
            // thread reading the list of wanted formats and this frame arriving. Draw nothing
            // rather than another format's geometry - the next frame carries it.
            if (TraceAxis.IsLineTrace(CurrentFormat) &&
                snapshot.MinMaxFor(CurrentFormat).Length == 0)
            {
                return false;
            }

            _snapshot = snapshot;
            HoldMeasured(snapshot.Spectrum);

            // A new acquisition at a different span cannot keep a magnification into the old one.
            if (IsMagnified &&
                (_displayStartHz < snapshot.Spectrum.StartFrequencyHz ||
                 _displayStopHz > snapshot.Spectrum.StopFrequencyHz))
            {
                _displayStartHz = double.NaN;
                _displayStopHz = double.NaN;
            }

            RebuildAxis(snapshot);
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
        /// Begins a Select Area drag at a position.
        /// </summary>
        /// <param name="position">Where the drag started, within this control.</param>
        /// <returns><c>true</c> if a drag started.</returns>
        /// <remarks>
        /// Public so the gesture can be driven without a mouse — by a test, or by an automation
        /// client. The shell wires the mouse to these three.
        /// </remarks>
        public bool BeginSelectArea(Point position)
        {
            if (!SelectAreaEnabled || _snapshot == null ||
                !GraticuleBounds.Contains(position))
            {
                return false;
            }

            _dragFromX = position.X;
            _dragFromY = position.Y;

            ShowBand(position.X, position.X, position.Y, position.Y);

            return true;
        }

        /// <summary>Extends a Select Area drag.</summary>
        /// <param name="position">The pointer's current position.</param>
        public void ExtendSelectArea(Point position)
        {
            if (double.IsNaN(_dragFromX))
            {
                return;
            }

            ShowBand(_dragFromX, position.X, _dragFromY, position.Y);
        }

        /// <summary>
        /// Ends a Select Area drag, raising <see cref="AreaSelected"/> if it covered anything.
        /// </summary>
        /// <param name="position">Where the drag ended.</param>
        /// <returns><c>true</c> if a selection was reported.</returns>
        /// <remarks>
        /// A drag of a few pixels is a click that moved, not a selection. It is discarded rather
        /// than zooming to a sliver, because a zoom to two pixels of span is far harder to undo
        /// than it was to ask for.
        /// </remarks>
        public bool EndSelectArea(Point position)
        {
            if (double.IsNaN(_dragFromX))
            {
                return false;
            }

            double fromX = _dragFromX;
            double fromY = _dragFromY;

            _dragFromX = double.NaN;
            _dragFromY = double.NaN;
            _band.Visibility = Visibility.Collapsed;

            if (Math.Abs(position.X - fromX) < MinimumSelectionDip)
            {
                return false;
            }

            double first = FrequencyAt(new Point(fromX, position.Y));
            double second = FrequencyAt(position);

            if (double.IsNaN(first) || double.IsNaN(second) || first == second)
            {
                return false;
            }

            EventHandler<AreaSelectedEventArgs> handler = AreaSelected;

            if (handler != null)
            {
                // The rectangle's height goes with it. A drag that stayed on one row reports NaN
                // levels rather than a zero-height region, and Scale Y refuses it: scaling the
                // axis to nothing would leave the trace off the top of the screen with no obvious
                // way back.
                double top = LevelAt(new Point(position.X, fromY));
                double bottom = LevelAt(position);

                handler(
                    this,
                    new AreaSelectedEventArgs(
                        Math.Min(first, second), Math.Max(first, second), top, bottom));
            }

            return true;
        }

        /// <summary>Abandons a Select Area drag without selecting anything.</summary>
        public void CancelSelectArea()
        {
            _dragFromX = double.NaN;
            _band.Visibility = Visibility.Collapsed;
        }

        /// <summary>Whether a Select Area drag is in progress.</summary>
        public bool IsSelectingArea => !double.IsNaN(_dragFromX);

        /// <summary>The shortest drag that counts as a selection, in device-independent pixels.</summary>
        public const double MinimumSelectionDip = 6.0;

        private void ShowBand(double fromX, double toX, double fromY, double toY)
        {
            Rect graticule = GraticuleBounds;

            double left = Math.Max(graticule.Left, Math.Min(fromX, toX));
            double right = Math.Min(graticule.Right, Math.Max(fromX, toX));

            double top = Math.Max(graticule.Top, Math.Min(fromY, toY));
            double bottom = Math.Min(graticule.Bottom, Math.Max(fromY, toY));

            // A drag of a few pixels vertically is a horizontal drag that wandered. Below that the
            // band is drawn full height, so the gesture still reads as "this frequency range".
            bool tall = bottom - top >= MinimumSelectionDip;

            _band.Margin = new Thickness(left, tall ? top : graticule.Top, 0.0, 0.0);
            _band.Width = Math.Max(0.0, right - left);
            _band.Height = tall ? bottom - top : graticule.Height;
            _band.Visibility = Visibility.Visible;
        }

        /// <summary>
        /// The level at a pixel position, or <see cref="double.NaN"/> outside the graticule.
        /// </summary>
        /// <param name="position">Position within this control, in device-independent pixels.</param>
        /// <remarks>
        /// The inverse of the mapping the rasteriser draws with: the top of the graticule holds
        /// <see cref="TopDbm"/> and it falls by <see cref="FullScaleDb"/> to the bottom. Written
        /// here rather than in the caller so that the reading and the drawing cannot disagree
        /// about where a level sits.
        /// </remarks>
        public double LevelAt(Point position)
        {
            Rect graticule = GraticuleBounds;

            if (graticule.Height <= 0.0)
            {
                return double.NaN;
            }

            double fraction = (position.Y - graticule.Top) / graticule.Height;

            fraction = Math.Max(0.0, Math.Min(1.0, fraction));

            return _topDbm - (fraction * FullScaleDb);
        }

        /// <summary>
        /// Raised when a region has been dragged across the trace — the <em>Select Area</em>
        /// gesture (<c>REQ-DSP-023</c>).
        /// </summary>
        /// <remarks>
        /// Carries the two frequencies in hertz, low first. A drag right-to-left means the same
        /// region as one left-to-right; requiring a direction would make half the gestures do
        /// nothing.
        /// </remarks>
        public event EventHandler<AreaSelectedEventArgs> AreaSelected;

        /// <summary>
        /// The frequency at a pixel position, or <see cref="double.NaN"/> outside the graticule.
        /// </summary>
        /// <param name="position">Position within this control, in device-independent pixels.</param>
        /// <remarks>
        /// Interpolated across the graticule rather than snapped to the nearest point, because a
        /// dragged edge is a position on the axis and not a choice among the drawn points — a
        /// selection that snapped would be up to half a point wider or narrower than the one the
        /// user drew.
        /// </remarks>
        public double FrequencyAt(Point position)
        {
            if (_snapshot == null)
            {
                return double.NaN;
            }

            Rect graticule = GraticuleBounds;

            if (graticule.Width <= 0.0)
            {
                return double.NaN;
            }

            double fraction = (position.X - graticule.Left) / graticule.Width;

            fraction = Math.Max(0.0, Math.Min(1.0, fraction));

            SpectrumFrame frame = _snapshot.Spectrum;

            return frame.PointCount < 2
                ? frame.StartFrequencyHz
                : frame.StartFrequencyHz + fraction * frame.BinWidthHz * (frame.PointCount - 1);
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
            HoldMeasured(null);
            Redraw(null);

            _analysisText.Text = string.Empty;
            _markerText.Text = string.Empty;
            _indicatorText.Text = string.Empty;
        }

        /// <summary>
        /// Applies the Annotation font slot to every annotation element (<c>REQ-UI-080</c>).
        /// </summary>
        /// <param name="fonts">The font slots in force.</param>
        /// <exception cref="ArgumentNullException"><paramref name="fonts"/> is null.</exception>
        /// <remarks>
        /// <para>
        /// Every annotation element, including the marker readout and the indicator, so a trace
        /// window is drawn in one typeface rather than in whichever ones happened to be set at
        /// construction.
        /// </para>
        /// <para>
        /// The annotation band's height is fixed (<see cref="AnnotationBandDip"/>), so a very large
        /// annotation font will crowd it. That is the user's choice to make and to see — clamping
        /// the size here would leave a setting that visibly did nothing past a certain point.
        /// </para>
        /// </remarks>
        public void ApplyFonts(FontPreferences fonts)
        {
            if (fonts == null)
            {
                throw new ArgumentNullException(nameof(fonts));
            }

            foreach (FrameworkElement element in _annotation)
            {
                var text = element as TextBlock;

                if (text != null)
                {
                    fonts.ApplyTo(FontSlot.Annotation, text);
                }
            }

            fonts.ApplyTo(FontSlot.Annotation, _markerText);
            fonts.ApplyTo(FontSlot.Annotation, _indicatorText);
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

                // Whole words or nothing, never half a unit. A text block arranged narrower than
                // its text clips silently, and a clipped annotation does not read as truncated —
                // it reads as a different measurement. "RBW 1.000000 kH" is what this produced,
                // and kH is not a unit; WordEllipsis drops the unit whole and says so with the
                // ellipsis rather than inventing one (#396).
                TextTrimming = TextTrimming.WordEllipsis,
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

        /// <summary>
        /// Lets a centred annotation stack use the whole width instead of one column of it.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The three columns exist to place annotation left, centre and right, not to ration a
        /// third of the width to each. A hot spot is a <see cref="TextBlock"/>, and a text block
        /// arranged narrower than its text <em>clips</em> rather than overflowing — centred, it
        /// clips at both ends, which is how <c>Center 1.000000 GHz</c> came to read
        /// <c>er 1.000000 GHz</c> on a narrow trace window.
        /// </para>
        /// <para>
        /// Spanning all three keeps the centring exactly where it was — the columns are equal, so
        /// the midpoint of all three is the midpoint of the middle one, and the centre-frequency
        /// label still sits under the centre of the graticule where it belongs. What changes is
        /// only how much width it may use before the text is cut.
        /// </para>
        /// </remarks>
        private static void SpanFullWidth(FrameworkElement element)
        {
            // Column 0 as well as the span. Spanning three from column 1 covers columns 1 and 2
            // only, which centres the stack two-thirds of the way across instead of in the middle
            // — the annotation-position test caught exactly that.
            SetColumn(element, 0);
            SetColumnSpan(element, 3);
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

            BuildPerDivisionChoices();

            _bottomScale.Value = NumericHotSpotValue.Decibels(BottomDbm);

            _format.Value = new ChoiceHotSpotValue(TraceFormatText.Names, 0);

            // Four figures, not six. The upper band carries the format, the RBW and the trigger
            // channel across one third of the graticule's width, and at six figures the unit was
            // the part that fell off the end — "RBW 1.000000 kH", which is not a unit (#396). Four
            // figures resolve a resolution bandwidth to a part in ten thousand, which is finer
            // than any instrument sets one.
            var bandwidth = NumericHotSpotValue.Frequency(1e3, 1.0, figures: 4);
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
                _topDbm = ((NumericHotSpotValue)_bottomScale.Value).Value + FullScaleDb;
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

        /// <summary>
        /// Brings the scale annotation into line with the axis, in the axis's own unit.
        /// </summary>
        /// <remarks>
        /// The hot spots are rebuilt when the unit changes rather than reformatted, because a hot
        /// spot's value carries its own formatting and parsing: a top scale that displayed volts
        /// and parsed decibels would take a typed entry and silently mean something else by it.
        /// </remarks>
        private void RefreshScaleText()
        {
            TraceAxis axis = _axis;
            _suppressParameterEvents = true;

            try
            {
                if (axis == null || axis.IsDecibels)
                {
                    if (!(_topScale.Value is NumericHotSpotValue) || !_scaleIsDecibels)
                    {
                        _topScale.Value = NumericHotSpotValue.Decibels(_topDbm);
                        _bottomScale.Value = NumericHotSpotValue.Decibels(BottomDbm);
                        _scaleIsDecibels = true;
                    }

                    ((NumericHotSpotValue)_topScale.Value).Value = _topDbm;
                    ((NumericHotSpotValue)_bottomScale.Value).Value = BottomDbm;
                }
                else
                {
                    _topScale.Value = Scaled(axis, axis.TopValue);
                    _bottomScale.Value = Scaled(axis, axis.BottomValue);
                    _scaleIsDecibels = false;
                }

                RefreshPerDivision(axis);

                _topScale.Refresh();
                _bottomScale.Refresh();
            }
            finally
            {
                _suppressParameterEvents = false;
            }
        }

        /// <summary>
        /// Shows the per-division step in the axis's own unit.
        /// </summary>
        /// <remarks>
        /// The decibel choice list is what the user picks from on a log-magnitude trace, and it is
        /// the only axis whose step they choose. On an auto-ranged axis the step is a consequence
        /// of the data, so it is shown as a reading — and it has to be shown in the axis's unit,
        /// because "10 dB/div" over a trace measured in volts is simply a false statement.
        /// </remarks>
        /// <summary>
        /// Restores the decibel per-division ladder, selecting the step in force.
        /// </summary>
        /// <remarks>
        /// The 1-2-5 ladder a graticule is readable at. Rebuilt rather than reselected, because
        /// coming back from an auto-ranged axis means the hot spot is holding a single reading
        /// rather than the ladder.
        /// </remarks>
        private void BuildPerDivisionChoices()
        {
            var steps = new List<double> { 1.0, 2.0, 5.0, 10.0, 20.0 };

            // Whatever the axis is actually set to, even when it is not on the ladder. Scale Y
            // (REQ-UI-063) takes its step from the region dragged, and a ladder that did not carry
            // it would leave the readout naming a division width the graticule does not have -
            // the same lie the per-division annotation told before REQ-TRC-001's formats were
            // wired up.
            if (!steps.Any(s => Math.Abs(s - _decibelsPerDivision) < 1e-9))
            {
                steps.Add(_decibelsPerDivision);
                steps.Sort();
            }

            var text = new string[steps.Count];
            int index = 0;

            for (int i = 0; i < steps.Count; i++)
            {
                text[i] = steps[i].ToString("0.##", CultureInfo.InvariantCulture) + " dB/div";

                if (Math.Abs(steps[i] - _decibelsPerDivision) < 1e-9)
                {
                    index = i;
                }
            }

            _perDivision.Value = new ChoiceHotSpotValue(text, index);
        }

        private void RefreshPerDivision(TraceAxis axis)
        {
            if (axis == null || axis.IsDecibels)
            {
                if (!_perDivisionIsDecibels)
                {
                    BuildPerDivisionChoices();
                    _perDivisionIsDecibels = true;
                }

                return;
            }

            _perDivision.Value = new ChoiceHotSpotValue(
                new[] { EngineeringText.Quantity(axis.PerDivision, axis.Unit) + "/div" });

            _perDivisionIsDecibels = false;
        }

        /// <summary>Whether the scale hot spots are currently in decibels.</summary>
        private bool _scaleIsDecibels = true;

        /// <summary>Whether the per-division hot spot is currently the decibel choice list.</summary>
        private bool _perDivisionIsDecibels = true;

        /// <summary>A scale hot spot's value in an axis's own unit.</summary>
        private static NumericHotSpotValue Scaled(TraceAxis axis, double value)
        {
            TraceAxis captured = axis;

            return new NumericHotSpotValue(
                value,
                Math.Max(1e-15, Math.Abs(axis.PerDivision) / 10.0),
                v => captured.Format(v),
                text =>
                {
                    double parsed;
                    return captured.TryParse(text, out parsed) ? parsed : (double?)null;
                });
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

            // The selection band takes the annotation colour: it is chrome over the trace, not a
            // trace of its own, and giving it the trace colour would make a dragged region look
            // briefly like measured data.
            _band.Stroke = annotation;

            Color wash = ToMediaColor(_palette.Annotation);
            var fill = new SolidColorBrush(Color.FromArgb(48, wash.R, wash.G, wash.B));
            fill.Freeze();

            _band.Fill = fill;

            // REQ-UI-021: a trace's line and its annotation text share one colour. The annotation
            // describing this trace's data — its scales, its format, its RBW, its readouts — is
            // tinted to match the line, which is how a reader tells four overlaid traces apart at a
            // glance. One setting drives both, so there is no per-trace annotation colour to drift
            // out of step with the line. The firmware defect REQ-UI-021 quotes — an annotation
            // colour wrong after preset — is exactly what a second setting produces.
            var traceInk = new SolidColorBrush(ToMediaColor(_palette.Trace));
            traceInk.Freeze();

            TraceAnnotationBrush = traceInk;

            foreach (FrameworkElement element in _annotation)
            {
                var text = element as TextBlock;

                if (text == null)
                {
                    continue;
                }

                // REQ-UI-010's Annotation colour against REQ-UI-021's Trace colour. The two
                // definitions overlap -- "text outside of the graticule" and "specified trace and
                // its annotation" -- and the division is by what the text is ABOUT: see where
                // _measurementAnnotation is filled. Giving every label the trace colour satisfies
                // REQ-UI-021 and leaves Annotation colouring no glyph at all, which is how
                // REQ-UI-010's fourth zone came to be unmeetable.
                text.Foreground = _measurementAnnotation.Contains(element) ? annotation : traceInk;
            }

            _markerText.Foreground = traceInk;

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
            // Zero when the annotation is hidden, which is what makes REQ-UI-011 a change of
            // geometry rather than of visibility: the band is not reserved, so the graticule
            // rectangle grows into it.
            int margin = _showAnnotation ? (int)Math.Round(AnnotationBandDip * dpi.DpiScaleX) : 0;

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

            // The last frame re-decimated to the new width, not discarded (REQ-UI-007's
            // sibling defect, filed as #395). The marshal decimates to the width it was told
            // about, and Show rightly refuses a snapshot built for a different one — so with a
            // measurement running the next frame repairs the display, and with nothing running
            // there is no next frame and the trace disappears on a resize. Redrawing the frame
            // this plot already holds costs one decimation per resize and nothing per frame.
            Redraw(Redecimated());

            if (GraticuleColumns != previousColumns)
            {
                EventHandler handler = GraticuleColumnsChanged;
                if (handler != null)
                {
                    handler(this, EventArgs.Empty);
                }
            }
        }

        /// <summary>
        /// The last frame decimated to the width this plot now has, or <c>null</c>.
        /// </summary>
        /// <remarks>
        /// Returns the held snapshot unchanged when the width has not moved, so a rebuild that
        /// changes only the DPI or the annotation band does no arithmetic it need not.
        /// </remarks>
        private TraceSnapshot Redecimated()
        {
            TraceSnapshot held = _snapshot;

            // Always from the measured frame, never from whatever is currently drawn. Decimating
            // the windowed frame again would magnify a magnification, and clearing the range would
            // restore only as much as the last window happened to hold — which is what the
            // clearing test caught.
            SpectrumFrame measured = _measuredFrame;

            if (held == null || measured == null || _layout == null)
            {
                return held;
            }

            int columns = _layout.Graticule.Width;

            if (columns < 1)
            {
                return held;
            }

            bool matches = held.Columns == columns &&
                           Math.Abs(held.Spectrum.StartFrequencyHz - DisplayStartHz) < 1e-6;

            if (matches)
            {
                return held;
            }

            _snapshot = RenderMarshal.Decimate(
                IsMagnified ? Windowed(measured) : measured,
                columns,
                new[] { CurrentFormat },
                TraceDetector.Normal,
                _formatOptions);

            return _snapshot;
        }

        /// <summary>
        /// The part of a frame the display range selects, as a frame in its own right (#397).
        /// </summary>
        /// <remarks>
        /// A real frame rather than a pair of indices, so everything downstream — the envelope, the
        /// markers, the axis — works on it unchanged. The points are the ones already acquired;
        /// nothing is interpolated, because magnifying a display invents no measurement.
        /// </remarks>
        private SpectrumFrame Windowed(SpectrumFrame frame)
        {
            int first = (int)Math.Floor((_displayStartHz - frame.StartFrequencyHz) / frame.BinWidthHz);
            int last = (int)Math.Ceiling((_displayStopHz - frame.StartFrequencyHz) / frame.BinWidthHz);

            first = Math.Max(0, Math.Min(first, frame.PointCount - 2));
            last = Math.Max(first + 1, Math.Min(last, frame.PointCount - 1));

            ReadOnlySpan<float> levels = frame.LevelsDbm;
            var windowed = new float[last - first + 1];

            for (int i = 0; i < windowed.Length; i++)
            {
                windowed[i] = levels[first + i];
            }

            return SpectrumFrame.FromLevels(
                windowed,
                frame.StartFrequencyHz + first * frame.BinWidthHz,
                frame.BinWidthHz,
                frame.Window,
                frame.EquivalentNoiseBandwidthBins);
        }

        /// <summary>
        /// What the annotation says about a magnified display, or nothing (#397).
        /// </summary>
        /// <remarks>
        /// <para>
        /// <strong>Only when the two differ.</strong> An unmagnified trace already has its centre
        /// and span beneath the X axis describing both the measurement and the picture; a second
        /// pair saying the same thing would be clutter that trains a reader to ignore the line
        /// that matters when it does appear.
        /// </para>
        /// <para>
        /// The word is "Disp", in the terse style <c>REQ-UI-053</c> establishes for readout labels
        /// — the annotation band is short of room, which is what #396 was about.
        /// </para>
        /// </remarks>
        private string DisplayRangeNote() =>
            IsMagnified
                ? "   Disp " + Frequency(DisplayCentreHz) + " ± " + Frequency(DisplaySpanHz / 2.0)
                : string.Empty;

        private void BuildLayout()
        {
            if (_surface == null)
            {
                return;
            }

            TraceAxis axis = _axis;

            _layout = new PlotLayout(
                _surface.Width,
                _surface.Height,
                _marginPixels,
                axis == null ? _topDbm : axis.TopValue,
                axis == null ? BottomDbm : axis.BottomValue,
                _horizontalDivisions,
                _verticalDivisions,
                _yReferencePercent,
                _xReferencePercent);
        }

        private void Redraw(TraceSnapshot snapshot)
        {
            if (_layout == null || _surface == null || _bitmap == null)
            {
                return;
            }

            // This plot's own format, never the frame's log magnitude. Four windows showing four
            // formats of one acquisition is REQ-TRC-001's whole point, and drawing snapshot.MinMax
            // here is what made all four of them the same picture under four labels.
            PlotRasterizer.Render(
                _surface,
                _layout,
                _palette,
                snapshot == null ? ReadOnlySpan<float>.Empty : snapshot.MinMaxFor(CurrentFormat),
                ColumnColours(snapshot),
                _showGridLines);

            if (IsShowingResult)
            {
                DrawResult();
            }
            else if (IsShowingSpectrogram)
            {
                DrawSpectrogram();
            }
            else if (snapshot != null)
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
        /// The per-column trace colours a limit test calls for, or empty if none applies.
        /// </summary>
        /// <remarks>
        /// <c>REQ-UI-023</c>: the failing points of <em>the trace</em> are recoloured. The limit
        /// line, wherever it is drawn, keeps <see cref="LimitColours.Limit"/> — this method has no
        /// way to change it, which is deliberate.
        /// </remarks>
        private ReadOnlySpan<PlotColor> ColumnColours(TraceSnapshot snapshot)
        {
            LimitTest test = _limitTest;

            if (snapshot == null || test == null || _layout == null)
            {
                return ReadOnlySpan<PlotColor>.Empty;
            }

            LimitStanding[] standings = LimitShading.ToColumns(
                LimitShading.Classify(snapshot.Spectrum, test), _layout.Graticule.Width);

            return LimitShading.ShadeTrace(standings, _limitColours, _palette.Trace);
        }

        /// <summary>
        /// Draws the accumulated history as a map, with its two markers (<c>REQ-UI-054</c>).
        /// </summary>
        /// <remarks>
        /// <para>
        /// Over the graticule the rasteriser has already drawn, so the border, the annotation band
        /// and the background all stay as they are. <strong>The graticule's own lines are covered by
        /// the map, deliberately</strong> — a grid drawn over a spectrogram hides one cell in every
        /// ten columns and rows, and those cells are data rather than blank space.
        /// </para>
        /// <para>
        /// The markers are drawn last and perpendicular: the spectrogram marker down the column its
        /// frequency falls in, the trace-select marker across the row its instant selects. Both use
        /// <see cref="SpectrogramRasterizer"/>'s own mapping rather than a second copy of the
        /// arithmetic, so a marker cannot land beside the cell it names.
        /// </para>
        /// </remarks>
        private void DrawSpectrogram()
        {
            PixelRect graticule = _layout.Graticule;

            // The threshold first, from the loudest cell rather than from the window: Enhance moves
            // the window and must not move what the ladder means. Then the window, over the cells
            // the threshold leaves — so the two controls compose, which is what a user turning both
            // on expects.
            double peak = double.IsNaN(_spectrogramThresholdBelowTopDb)
                ? double.NaN
                : SpectrogramScaling.PeakLevelDbm(_history);

            _spectrogramThresholdDbm = double.IsNaN(peak)
                ? SpectrogramLevels.NoThresholdDbm
                : peak - _spectrogramThresholdBelowTopDb;

            _spectrogramLevels = SpectrogramScaling.Window(
                _history, _spectrogramThresholdDbm, _spectrogramEnhance, _spectrogramLevels);

            SpectrogramCellsDrawn = SpectrogramRasterizer.Render(
                _surface,
                graticule,
                _history,
                _spectrogramMap,
                _spectrogramLevels,
                _spectrogramThresholdDbm,
                _palette.TraceBackground);

            SpectrogramMarkers markers = _spectrogramMarkers;

            if (markers == null || !markers.HasRows)
            {
                return;
            }

            int column = SpectrogramRasterizer.ColumnForBin(
                markers.BinIndex, graticule.Width, _history.Newest.PointCount);

            MarkerGlyph.DrawVerticalRule(
                _surface, graticule.X + column, graticule, _spectrogramMarkerColour);

            int row = SpectrogramRasterizer.YForRow(
                markers.RowIndex, graticule.Height, _history.RowCount);

            if (row >= 0)
            {
                MarkerGlyph.DrawHorizontalRule(
                    _surface, graticule.Y + row, graticule, _traceSelectColour);
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
            // The measurement's own frame, never the windowed one: the centre, span and resolution
            // bandwidth describe what was measured whatever the display is magnified into (#397).
            SpectrumFrame measured = _measuredFrame ?? frame;

            _suppressParameterEvents = true;

            try
            {
                Set(_resolutionBandwidth, measured.ResolutionBandwidthHz);
                Set(_centerFrequency, measured.CenterFrequencyHz);

                if (measured.PointCount > 1 && measured.SpanHz > 0.0)
                {
                    Set(_mainTime, (measured.PointCount - 1) / measured.SpanHz);
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
                WindowText.Describe(measured.Window) + "   " +
                measured.PointCount.ToString(CultureInfo.CurrentCulture) + " pts" +
                AveragingNote(measured) + "   Span " + Frequency(measured.SpanHz) +
                TransformNote(measured) + NoiseCorrectionNote(measured) +
                (formatNote.Length == 0 ? string.Empty : "   " + formatNote) +
                DisplayRangeNote();

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
