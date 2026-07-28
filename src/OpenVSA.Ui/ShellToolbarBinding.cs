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

        private readonly ToolbarLayout _toolbarLayout = new ToolbarLayout();

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
        private ToggleButton _enhanceToggle;
        private ComboBox _thresholdBox;

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

        /// <summary>
        /// The toolbars as the user has arranged them (<c>REQ-UI-064</c>).
        /// </summary>
        /// <remarks>
        /// The live arrangement, not a copy: the customiser edits this and the tray follows, which
        /// is <c>REQ-UI-070</c>'s live-settings rule applied to a dialog whose subject is the
        /// toolbars themselves.
        /// </remarks>
        public ToolbarLayout ToolbarArrangement => _toolbarLayout;

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
            RebuildToolbars();

            _sweep.Changed += (sender, e) => FollowSweep();
            _toolbarLayout.Changed += (sender, e) => RebuildToolbars();

            FollowSweep();
        }

        /// <summary>
        /// Rebuilds the tray after the customiser has changed something (<c>REQ-UI-064</c>).
        /// </summary>
        /// <remarks>
        /// <para>
        /// <strong>The caches are emptied first, and that is not tidiness.</strong> Every control
        /// the shell has to keep in step with a setting is remembered by reference — the Pause
        /// caption, the five mouse modes, the three accumulators, the two dropdowns. A rebuild
        /// makes new ones; keeping the old ones would leave the shell updating buttons that are no
        /// longer on any toolbar, and the visible ones showing whatever they were built with.
        /// </para>
        /// <para>
        /// Nothing is subscribed to here. <see cref="BuildToolbars"/> subscribes once; a rebuild
        /// that subscribed again would give every later change one more handler than the last.
        /// </para>
        /// </remarks>
        private void RebuildToolbars()
        {
            _mouseModeButtons.Clear();
            _accumulatorButtons.Clear();

            _pauseButton = null;
            _singleSweepToggle = null;
            _autoRangeSplit = null;
            _activeTraceReadout = null;
            _layoutBox = null;
            _mapBox = null;
            _blockDiagramToggle = null;
            _enhanceToggle = null;
            _thresholdBox = null;

            ShellToolbarBuilder.Build(Toolbars, this, _toolbarLayout);

            FollowSweep();
            FollowAccumulator();
            FollowSpectrogramMap();
            ShowActiveTrace();
        }

        /// <summary>
        /// Returns the toolbars to <c>REQ-UI-063</c>'s arrangement (File &gt; Preset &gt; Toolbars).
        /// </summary>
        /// <remarks>
        /// The five preconfigured toolbars get their declared contents back and every custom one
        /// goes, which is <c>REQ-UI-064</c>'s criterion in one call — and the same call Factory
        /// Defaults makes, because that variant's scope includes the toolbars.
        /// </remarks>
        private void ResetToolbars()
        {
            _toolbarLayout.Reset();

            if (_customiser != null)
            {
                _customiser.Refresh();
            }
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
                    WhenToggled(
                        _singleSweepToggle,
                        () => ChooseSweepMode(SweepMode.Single),
                        () => ChooseSweepMode(SweepMode.Continuous));
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

                    // Set from the window rather than left unchecked, so that a toolbar rebuilt by
                    // the customiser does not report a pane closed while it is open.
                    _blockDiagramToggle.IsChecked =
                        _toolWindows != null && _toolWindows.Layout.IsOpen(ToolWindow.BlockDiagram);

                    WhenToggled(
                        _blockDiagramToggle,
                        () => _toolWindows.SetOpen(ToolWindow.BlockDiagram, true),
                        () => _toolWindows.SetOpen(ToolWindow.BlockDiagram, false));
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

                case "Spectrogram / Colour Map > Enhance":
                    _enhanceToggle = (ToggleButton)created;
                    _enhanceToggle.IsChecked = _spectrogramEnhance;
                    WhenToggled(
                        _enhanceToggle, () => ChooseEnhance(true), () => ChooseEnhance(false));
                    return true;

                case "Spectrogram / Colour Map > Threshold":
                    _thresholdBox = (ComboBox)created;
                    FillThresholdBox();
                    _thresholdBox.SelectionChanged += OnThresholdChosen;
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

        /// <summary>
        /// Answers a toggle being turned on or off, however it was turned.
        /// </summary>
        /// <param name="button">The toggle.</param>
        /// <param name="on">What to do when it goes in.</param>
        /// <param name="off">What to do when it comes out.</param>
        /// <remarks>
        /// <para>
        /// <strong><see cref="System.Windows.Controls.Primitives.ButtonBase.Click"/> is the wrong
        /// event for a toggle, and the difference is not academic.</strong> WPF's automation peer
        /// for a <see cref="ToggleButton"/> implements <c>IToggleProvider.Toggle</c> by changing
        /// <see cref="ToggleButton.IsChecked"/> — it raises <c>Checked</c> and <c>Unchecked</c> and
        /// never raises <c>Click</c>. A control bound to <c>Click</c> therefore lights up and does
        /// nothing when it is operated by a screen reader, by UI Automation, or by anything else
        /// that is not a mouse. Every toggle on <c>REQ-UI-063</c>'s toolbars was bound that way
        /// until a screenshot of the running application showed the Spectrogram button lit with the
        /// accumulator still at None.
        /// </para>
        /// <para>
        /// Guarded by <see cref="_followingToolbar"/>, because the shell also sets
        /// <c>IsChecked</c> to bring a toolbar into line with a setting changed elsewhere — and
        /// that must not be mistaken for a user pressing it.
        /// </para>
        /// </remarks>
        private void WhenToggled(ToggleButton button, Action on, Action off)
        {
            button.Checked += (sender, e) =>
            {
                if (!_followingToolbar)
                {
                    on();
                }
            };

            button.Unchecked += (sender, e) =>
            {
                if (!_followingToolbar)
                {
                    off();
                }
            };
        }

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

            // Unchecking re-selects: exactly one mouse mode is in force at all times, and Pointer
            // is the one that means "a click does nothing". A toggle left out with the mode
            // unchanged would be a toolbar disagreeing with the plots it drives.
            WhenToggled(
                button,
                () => ChooseMouseMode(captured),
                () =>
                {
                    if (_mouseMode == captured)
                    {
                        ChooseMouseMode(captured);
                    }
                });

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

            // Unchecking turns the accumulator off only if this is the one that is on. The builder
            // couples the group by unchecking the others when one goes in, and without the guard
            // that coupling would immediately set the accumulator back to None — the mode would
            // light up and be cancelled in the same gesture.
            WhenToggled(
                button,
                () => ChooseAccumulator(accumulator),
                () =>
                {
                    if (_analysis.Accumulator == accumulator)
                    {
                        ChooseAccumulator(TraceAccumulator.None);
                    }
                });
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

        /// <summary>
        /// The levels the Threshold dropdown offers, in decibels below the top of the map.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <strong>A ladder relative to the map's top, not a typed absolute level.</strong> A
        /// spectrogram's useful range moves with the signal and with Enhance, so an absolute
        /// threshold typed once is wrong the moment either changes; "hide everything more than 40 dB
        /// below the loudest thing here" keeps meaning what it meant. The same argument the
        /// per-division ladder makes for the vertical axis — a readable set of steps beats an
        /// arbitrary number.
        /// </para>
        /// <para>
        /// Zero is not offered. A threshold at the top of the map hides everything, which is a
        /// setting with no use and one click away from a display a user would report as broken.
        /// </para>
        /// </remarks>
        public static readonly double[] ThresholdStepsDb = { 10.0, 20.0, 30.0, 40.0, 50.0, 60.0 };

        /// <summary>What the Threshold dropdown shows when nothing is hidden.</summary>
        public const string ThresholdOff = "No threshold";

        /// <summary>
        /// Whether the colour map is stretched about the busiest levels (<c>REQ-UI-054</c>).
        /// </summary>
        public bool SpectrogramEnhance
        {
            get { return _spectrogramEnhance; }
            set { ChooseEnhance(value); }
        }

        /// <summary>
        /// How far below the loudest cell the display stops drawing, or NaN for no threshold.
        /// </summary>
        public double SpectrogramThresholdBelowTopDb => _spectrogramThresholdBelowTopDb;

        /// <summary>
        /// Chooses the enhancement (<c>REQ-UI-054</c>).
        /// </summary>
        private void ChooseEnhance(bool enhance)
        {
            _spectrogramEnhance = enhance;

            if (_enhanceToggle != null)
            {
                _followingToolbar = true;

                try
                {
                    _enhanceToggle.IsChecked = enhance;
                }
                finally
                {
                    _followingToolbar = false;
                }
            }

            ApplyAccumulator();

            StatusText.Content = enhance
                ? "Enhance on: the colour map is stretched about the levels the history holds."
                : "Enhance off: the colour map spans the whole range of the history.";
        }

        /// <summary>
        /// Chooses the threshold (<c>REQ-UI-054</c>).
        /// </summary>
        /// <param name="belowTopDb">Decibels below the loudest cell, or NaN for none.</param>
        /// <remarks>
        /// Carried to the plots as a relative figure and resolved there against the loudest cell in
        /// the history, so that the number the user chose means the same thing whether or not
        /// Enhance is on.
        /// </remarks>
        public void ChooseThreshold(double belowTopDb)
        {
            _spectrogramThresholdBelowTopDb = belowTopDb;

            ApplyAccumulator();

            StatusText.Content = double.IsNaN(belowTopDb)
                ? "Threshold off: every cell is drawn."
                : "Threshold: cells more than " +
                  belowTopDb.ToString("0", System.Globalization.CultureInfo.CurrentCulture) +
                  " dB below the loudest are hidden.";
        }

        private void FillThresholdBox()
        {
            _thresholdBox.Items.Clear();
            _thresholdBox.Items.Add(ThresholdOff);

            foreach (double step in ThresholdStepsDb)
            {
                _thresholdBox.Items.Add(
                    "−" + step.ToString("0", System.Globalization.CultureInfo.CurrentCulture) + " dB");
            }

            _thresholdBox.SelectedIndex = 0;
        }

        private void OnThresholdChosen(object sender, SelectionChangedEventArgs e)
        {
            if (_followingToolbar || _thresholdBox.SelectedIndex < 0)
            {
                return;
            }

            LastToolbarCommand = "Spectrogram / Colour Map > Threshold";

            ChooseThreshold(_thresholdBox.SelectedIndex == 0
                ? double.NaN
                : ThresholdStepsDb[_thresholdBox.SelectedIndex - 1]);
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
                    ApplyAccumulator();

                    StatusText.Content = "Colour map: " + name;
                    return;
                }
            }
        }

        /// <summary>
        /// Brings the colour-map box and every spectrogram into line with the chosen map.
        /// </summary>
        /// <remarks>
        /// Both, from one call, because they are two views of one setting: a map chosen from the
        /// Trace menu that reached the dropdown but not the display would be the defect
        /// <c>REQ-UI-054</c>'s "Map Colour Scheme switches between the REQ-UI-024 maps" criterion
        /// is there to catch.
        /// </remarks>
        private void FollowSpectrogramMap()
        {
            ApplyAccumulator();

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
