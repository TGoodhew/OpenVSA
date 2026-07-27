using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using OpenVSA.Dsp.Spectrum;
using OpenVSA.Hal;
using OpenVSA.Measurement;
using OpenVSA.Ui.Layout;
using OpenVSA.Ui.Rendering;
using OpenVSA.Ui.Toolbars;
using OpenVSA.Ui.ToolWindows;

namespace OpenVSA.Ui
{
    /// <summary>
    /// What the shell puts behind each control of <c>REQ-UI-063</c>'s six toolbars.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Every one of these is a second surface over a setting something else already
    /// owns.</strong> The mouse mode drives the plots, the accumulators are
    /// <see cref="Dialogs.AnalysisSettings"/>'s and the Heatmaps tab shows them, the colour map is
    /// the one the Trace menu chooses, the layout is the document area's. A toolbar that held state
    /// of its own would be a second answer to every question the rest of the shell already answers.
    /// </para>
    /// <para>
    /// <strong>The Control toolbar is decided by <see cref="SweepControl"/>, not here.</strong>
    /// Pause means two different things depending on the sweep mode, and a state machine written
    /// into a click handler can only be tested by clicking.
    /// </para>
    /// </remarks>
    public partial class ShellWindow : IShellToolbarBinding
    {
        private readonly SweepControl _sweep = new SweepControl();

        private readonly Dictionary<MouseMode, ToggleButton> _mouseModeButtons =
            new Dictionary<MouseMode, ToggleButton>();

        private readonly Dictionary<TraceAccumulator, ToggleButton> _accumulatorButtons =
            new Dictionary<TraceAccumulator, ToggleButton>();

        private Button _pauseButton;
        private ToggleButton _singleSweepToggle;
        private SplitButton _autoRangeSplit;
        private TextBlock _activeTraceReadout;
        private ComboBox _layoutBox;
        private ComboBox _mapBox;
        private ToggleButton _blockDiagramToggle;

        private MouseMode _mouseMode = MouseMode.Marker;
        private AreaSelectAction _areaAction = AreaSelectAction.CentreAndSpan;
        private bool _followingToolbar;

        /// <summary>The path of the last toolbar control used, for a test to read back.</summary>
        public string LastToolbarCommand { get; private set; } = string.Empty;

        /// <summary>
        /// The toolbar tray of <c>REQ-UI-063</c>.
        /// </summary>
        /// <remarks>
        /// Exposed for the same reason <see cref="MenuBar"/> is: the criterion is about what the
        /// application shows, and a test that walked a description of it would prove nothing.
        /// </remarks>
        public ToolBarTray ToolbarTray => Toolbars;

        /// <summary>Whether an instrument is open.</summary>
        public bool IsConnected => _activeFrontEnd != null;

        /// <summary>Whether a measurement is under way.</summary>
        public bool IsMeasuring => _engine != null;

        /// <summary>The sweep state the Control toolbar shows and sets (<c>REQ-UI-063</c>).</summary>
        public SweepControl Sweep => _sweep;

        /// <summary>What a click or drag on a trace means (<c>REQ-UI-063</c>'s Marker Tools).</summary>
        public MouseMode MouseMode
        {
            get { return _mouseMode; }
            set { ChooseMouseMode(value); }
        }

        /// <summary>What dragging a rectangle does in Area Select.</summary>
        public AreaSelectAction AreaAction => _areaAction;

        /// <summary>
        /// How many averages the running measurement has accumulated.
        /// </summary>
        /// <remarks>
        /// Zero when nothing is running, and zero again after Restart — which is
        /// <c>REQ-UI-063</c>'s criterion for "all current measurement data including averaging is
        /// discarded", asserted rather than assumed.
        /// </remarks>
        public int AveragesCompleted
        {
            get
            {
                SpectrumEngine engine = _engine;
                TraceAverager averager = engine?.Averager;

                return averager == null ? 0 : averager.Completed;
            }
        }

        /// <summary>Builds the toolbars from <c>REQ-UI-063</c>'s table.</summary>
        private void BuildToolbars()
        {
            ShellToolbarBuilder.Build(Toolbars, this);

            _sweep.Changed += (sender, e) => FollowSweep();
            FollowSweep();
        }

        /// <inheritdoc />
        void IShellToolbarBinding.Ran(string path) => LastToolbarCommand = path;

        /// <inheritdoc />
        bool IShellToolbarBinding.Bind(
            string path, ToolbarControl control, FrameworkElement created)
        {
            switch (path)
            {
                // ---- Control ------------------------------------------------------------------
                case "Control > Restart":
                    ((Button)created).Click += (sender, e) => RestartMeasurement();
                    return true;

                case "Control > Pause":
                    _pauseButton = (Button)created;
                    _pauseButton.Click += (sender, e) => PressPause();
                    return true;

                case "Control > Single Sweep":
                    _singleSweepToggle = (ToggleButton)created;
                    _singleSweepToggle.Click += (sender, e) => ChooseSweepMode(
                        _singleSweepToggle.IsChecked == true ? SweepMode.Single : SweepMode.Continuous);
                    return true;

                case "Control > Auto-range":
                    _autoRangeSplit = (SplitButton)created;
                    _autoRangeSplit.MainClick += (sender, e) => AutoRangeAllChannels();
                    _autoRangeSplit.DropDownOpening += (sender, e) => FillAutoRangeChannels();
                    FillAutoRangeChannels();
                    return true;

                // ---- Marker Tools -------------------------------------------------------------
                case "Marker Tools > Pointer":
                case "Marker Tools > Area Select":
                case "Marker Tools > Marker":
                case "Marker Tools > Band Power":
                case "Marker Tools > Time Gate":
                    BindMouseMode(control.Name, (ToggleButton)created);
                    return true;

                // ---- Record -------------------------------------------------------------------
                case "Record > Disconnect":
                    ((Button)created).Click += (sender, e) => Disconnect();
                    return true;

                // ---- Trace / Block Diagram ----------------------------------------------------
                case "Trace / Block Diagram > Active Trace":
                    _activeTraceReadout = (TextBlock)created;
                    ShowActiveTrace();
                    return true;

                case "Trace / Block Diagram > Trace Layout":
                    _layoutBox = (ComboBox)created;
                    FillLayoutBox();
                    _layoutBox.SelectionChanged += OnLayoutChosen;
                    return true;

                case "Trace / Block Diagram > Block Diagram":
                    _blockDiagramToggle = (ToggleButton)created;
                    _blockDiagramToggle.Click += (sender, e) => _toolWindows.SetOpen(
                        ToolWindow.BlockDiagram, _blockDiagramToggle.IsChecked == true);
                    return true;

                // ---- Spectrogram / Colour Map -------------------------------------------------
                case "Spectrogram / Colour Map > Spectrogram":
                    BindAccumulator(TraceAccumulator.Spectrogram, (ToggleButton)created);
                    return true;

                case "Spectrogram / Colour Map > Digital Persistence":
                    BindAccumulator(TraceAccumulator.DigitalPersistence, (ToggleButton)created);
                    return true;

                case "Spectrogram / Colour Map > Cumulative History":
                    BindAccumulator(TraceAccumulator.CumulativeHistory, (ToggleButton)created);
                    return true;

                case "Spectrogram / Colour Map > Map Colour Scheme":
                    _mapBox = (ComboBox)created;
                    FillMapBox();
                    _mapBox.SelectionChanged += OnMapChosen;
                    return true;

                default:
                    // Not implemented, and REQ-UI-063's table says why.
                    return false;
            }
        }

        // ---- Control -----------------------------------------------------------------------------

        /// <summary>
        /// Answers a press of Pause (<c>REQ-UI-063</c>).
        /// </summary>
        /// <remarks>
        /// The decision is <see cref="SweepControl.Press"/>'s; this carries it out. A second press
        /// single-steps under Single sweep and continues under Continuous, and those are two
        /// branches rather than one because the requirement says so and because collapsing them is
        /// the shortcut a reader of the sentence would take.
        /// </remarks>
        private async void PressPause()
        {
            _sweep.IsRunning = _engine != null || _sweep.IsPaused;

            switch (_sweep.Press())
            {
                case SweepAction.Pause:
                    if (_engine != null)
                    {
                        await StopAcquisitionAsync().ConfigureAwait(true);
                    }

                    StatusText.Content = "Paused";
                    break;

                case SweepAction.Step:
                    // One sweep, and held again. The measurement is started and stopped rather than
                    // resumed, because a single step is exactly one acquisition.
                    if (_activeFrontEnd != null)
                    {
                        await StartMeasurementAsync().ConfigureAwait(true);
                        await StopAcquisitionAsync().ConfigureAwait(true);
                    }

                    StatusText.Content = "Single sweep";
                    break;

                case SweepAction.Continue:
                    if (_activeFrontEnd != null)
                    {
                        await StartMeasurementAsync().ConfigureAwait(true);
                    }

                    StatusText.Content = "Running";
                    break;

                default:
                    StatusText.Content = _activeFrontEnd == null
                        ? "Nothing is connected."
                        : "Nothing is being measured; Restart starts one.";
                    break;
            }

            FollowSweep();
        }

        private void ChooseSweepMode(SweepMode mode)
        {
            _sweep.Mode = mode;

            StatusText.Content = mode == SweepMode.Single
                ? "Single sweep: Pause steps one sweep at a time."
                : "Continuous sweep.";
        }

        /// <summary>Brings the Control toolbar into line with the sweep state.</summary>
        private void FollowSweep()
        {
            if (_pauseButton != null)
            {
                _pauseButton.Content = _sweep.PauseCaption;
            }

            if (_singleSweepToggle != null)
            {
                _singleSweepToggle.IsChecked = _sweep.Mode == SweepMode.Single;
            }
        }

        /// <summary>Ranges every input channel (<c>REQ-UI-063</c>, <c>REQ-ACQ-004</c>).</summary>
        private void AutoRangeAllChannels()
        {
            LastToolbarCommand = "Control > Auto-range";
            OnAutoRange(this, new RoutedEventArgs());
        }

        /// <summary>
        /// Fills the Auto-range dropdown from what the front end declares.
        /// </summary>
        /// <remarks>
        /// One entry per channel the connected instrument says it has — never a fixed list. A
        /// dropdown offering four channels against a single-channel front end is the kind of
        /// hard-coded assumption <c>REQ-HAL-002</c> exists to prevent.
        /// </remarks>
        private void FillAutoRangeChannels()
        {
            if (_autoRangeSplit == null)
            {
                return;
            }

            _autoRangeSplit.ClearDropDown();

            IFrontEnd frontEnd = _activeFrontEnd;

            if (frontEnd == null)
            {
                MenuItem none = _autoRangeSplit.AddDropDownItem("No instrument", () => { });

                none.IsEnabled = false;
                none.ToolTip =
                    "Nothing is connected, so there are no channels to range. Choose an " +
                    "instrument under Hardware > Instruments… first.";

                ToolTipService.SetShowOnDisabled(none, true);
                return;
            }

            int channels = Math.Max(1, frontEnd.Capabilities.ChannelCount);

            for (int channel = 1; channel <= channels; channel++)
            {
                int captured = channel;

                _autoRangeSplit.AddDropDownItem(
                    "Channel " + channel, () => AutoRangeChannel(captured));
            }
        }

        /// <summary>Ranges one chosen channel.</summary>
        private void AutoRangeChannel(int channel)
        {
            LastToolbarCommand = "Control > Auto-range > Channel " + channel;

            OnAutoRange(this, new RoutedEventArgs());
            StatusText.Content = "Auto-ranged channel " + channel;
        }

        // ---- Marker Tools ------------------------------------------------------------------------

        private void BindMouseMode(string name, ToggleButton button)
        {
            MouseMode? mode = MouseModes.ByName(name);

            if (mode == null)
            {
                return;
            }

            MouseMode captured = mode.Value;

            _mouseModeButtons[captured] = button;
            button.IsChecked = _mouseMode == captured;

            button.Click += (sender, e) => ChooseMouseMode(captured);

            if (captured == MouseMode.AreaSelect)
            {
                // What a dragged rectangle does is a choice of its own: REQ-UI-063 says Area Select
                // "can scale X and/or Y, or set centre frequency and span". Offered from the tool
                // itself rather than as a sixth control, because the toolbar's contents are the
                // requirement's list.
                var menu = new ContextMenu();

                foreach (AreaSelectAction action in
                    (AreaSelectAction[])Enum.GetValues(typeof(AreaSelectAction)))
                {
                    AreaSelectAction chosen = action;

                    var item = new MenuItem
                    {
                        Header = MouseModes.NameOf(action),
                        IsCheckable = true,
                        IsChecked = _areaAction == action,
                        ToolTip = MouseModes.ReasonAgainst(action),
                    };

                    if (item.ToolTip != null)
                    {
                        item.IsEnabled = false;
                        ToolTipService.SetShowOnDisabled(item, true);
                    }
                    else
                    {
                        item.Click += (sender, e) => ChooseAreaAction(chosen);
                    }

                    menu.Items.Add(item);
                }

                button.ContextMenu = menu;
            }
        }

        /// <summary>Chooses what a click or drag on a trace means.</summary>
        private void ChooseMouseMode(MouseMode mode)
        {
            _mouseMode = mode;

            _followingToolbar = true;

            try
            {
                foreach (KeyValuePair<MouseMode, ToggleButton> found in _mouseModeButtons)
                {
                    found.Value.IsChecked = found.Key == mode;
                }

            }
            finally
            {
                _followingToolbar = false;
            }

            ApplyMouseMode();

            StatusText.Content = "Mouse mode: " + MouseModes.NameOf(mode) +
                (mode == MouseMode.AreaSelect
                    ? " — " + MouseModes.NameOf(_areaAction).ToLowerInvariant()
                    : string.Empty);
        }

        /// <summary>Tells every plot what a drag on it means now.</summary>
        private void ApplyMouseMode()
        {
            foreach (char letter in Documents.Traces)
            {
                TracePlot plot = Documents.PlotOf(letter);

                if (plot != null)
                {
                    plot.SelectAreaEnabled =
                        _mouseMode == MouseMode.AreaSelect ||
                        _mouseMode == MouseMode.BandPower ||
                        _mouseMode == MouseMode.TimeGate;
                }
            }
        }

        /// <summary>
        /// Scales a trace's vertical axis to a dragged rectangle (<c>REQ-UI-063</c>).
        /// </summary>
        /// <param name="plot">The trace that was dragged over.</param>
        /// <param name="area">The rectangle.</param>
        /// <remarks>
        /// The display only: the measurement is not touched, which is the whole difference between
        /// this action and setting the centre frequency and span. The per-division step is taken
        /// from the ladder rather than set to an arbitrary figure, so that the graticule stays
        /// readable — an axis of 3.7 dB per division is a scale nobody can read a level off.
        /// </remarks>
        private void ScaleYToArea(TracePlot plot, AreaSelectedEventArgs area)
        {
            if (plot == null || !area.HasLevels)
            {
                StatusText.Content =
                    "Scale Y needs a rectangle with some height: drag down as well as across.";
                return;
            }

            plot.ScaleTo(area.TopDbm, area.BottomDbm);

            StatusText.Content =
                "Scaled trace " + Documents.ActiveTrace + " to " +
                area.TopDbm.ToString("0.0", System.Globalization.CultureInfo.CurrentCulture) +
                " dBm over " +
                area.RangeDb.ToString("0.0", System.Globalization.CultureInfo.CurrentCulture) +
                " dB — the measurement is unchanged.";
        }

        private void ChooseAreaAction(AreaSelectAction action)
        {
            _areaAction = action;

            ToggleButton button;

            if (_mouseModeButtons.TryGetValue(MouseMode.AreaSelect, out button) &&
                button.ContextMenu != null)
            {
                foreach (object child in button.ContextMenu.Items)
                {
                    var item = child as MenuItem;

                    if (item != null)
                    {
                        item.IsChecked = string.Equals(
                            item.Header as string,
                            MouseModes.NameOf(action),
                            StringComparison.Ordinal);
                    }
                }
            }

            ChooseMouseMode(MouseMode.AreaSelect);
        }

        // ---- Trace / Block Diagram ---------------------------------------------------------------

        /// <summary>Shows which trace the trace commands act on, by its letter.</summary>
        private void ShowActiveTrace()
        {
            if (_activeTraceReadout != null)
            {
                // The letter, which is how REQ-UI-020 identifies a trace throughout.
                _activeTraceReadout.Text = "Trace " + Documents.ActiveTrace;
            }

            if (_layoutBox != null && _layoutBox.SelectedIndex < 0)
            {
                _layoutBox.SelectedIndex = 0;
            }
        }

        private void FillLayoutBox()
        {
            _layoutBox.Items.Clear();

            foreach (TraceLayoutPreset preset in
                TraceLayoutPreset.Menu(_stackRows, _gridRows, _gridColumns))
            {
                _layoutBox.Items.Add(preset);
            }

            _layoutBox.DisplayMemberPath = "Name";
            _layoutBox.SelectedIndex = 0;
        }

        private void OnLayoutChosen(object sender, SelectionChangedEventArgs e)
        {
            var preset = _layoutBox.SelectedItem as TraceLayoutPreset;

            if (preset != null && !_followingToolbar)
            {
                LastToolbarCommand = "Trace / Block Diagram > Trace Layout";
                Documents.ApplyLayout(preset);
            }
        }

        // ---- Spectrogram / Colour Map ------------------------------------------------------------

        private void BindAccumulator(TraceAccumulator accumulator, ToggleButton button)
        {
            _accumulatorButtons[accumulator] = button;
            button.IsChecked = _analysis.Accumulator == accumulator;

            button.Click += (sender, e) => ChooseAccumulator(
                button.IsChecked == true ? accumulator : TraceAccumulator.None);
        }

        /// <summary>
        /// Chooses the accumulator (<c>REQ-TRC-001a</c>).
        /// </summary>
        /// <remarks>
        /// Written into <see cref="Dialogs.AnalysisSettings"/>, which the Heatmaps tab of the
        /// Analysis dialog also edits. One setting, two surfaces — the toolbar shows what the tab
        /// says and the other way about.
        /// </remarks>
        private void ChooseAccumulator(TraceAccumulator accumulator)
        {
            _analysis.Accumulator = accumulator;

            FollowAccumulator();

            StatusText.Content = accumulator == TraceAccumulator.None
                ? "Accumulator off."
                : "Accumulator: " + accumulator;
        }

        /// <summary>Brings the accumulator buttons into line with the setting.</summary>
        private void FollowAccumulator()
        {
            _followingToolbar = true;

            try
            {
                foreach (KeyValuePair<TraceAccumulator, ToggleButton> found in _accumulatorButtons)
                {
                    found.Value.IsChecked = found.Key == _analysis.Accumulator;
                }
            }
            finally
            {
                _followingToolbar = false;
            }
        }

        private void FillMapBox()
        {
            _mapBox.Items.Clear();

            foreach (SpectrogramColourMapKind kind in
                (SpectrogramColourMapKind[])Enum.GetValues(typeof(SpectrogramColourMapKind)))
            {
                _mapBox.Items.Add(SpectrogramColourMap.NameOf(kind));
            }

            _mapBox.SelectedItem = SpectrogramColourMap.NameOf(_spectrogramMap.Kind);
        }

        private void OnMapChosen(object sender, SelectionChangedEventArgs e)
        {
            if (_followingToolbar)
            {
                return;
            }

            var name = _mapBox.SelectedItem as string;

            foreach (SpectrogramColourMapKind kind in
                (SpectrogramColourMapKind[])Enum.GetValues(typeof(SpectrogramColourMapKind)))
            {
                if (string.Equals(SpectrogramColourMap.NameOf(kind), name, StringComparison.Ordinal) &&
                    kind != SpectrogramColourMapKind.UserDefined)
                {
                    LastToolbarCommand = "Spectrogram / Colour Map > Map Colour Scheme";

                    _spectrogramMap = SpectrogramColourMap.Of(kind);
                    BuildSpectrogramMapMenu();

                    StatusText.Content = "Colour map: " + name;
                    return;
                }
            }
        }

        /// <summary>Brings the colour-map box into line with the map the menu chose.</summary>
        private void FollowSpectrogramMap()
        {
            if (_mapBox == null)
            {
                return;
            }

            _followingToolbar = true;

            try
            {
                _mapBox.SelectedItem = SpectrogramColourMap.NameOf(_spectrogramMap.Kind);
            }
            finally
            {
                _followingToolbar = false;
            }
        }
    }
}
