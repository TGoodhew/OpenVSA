using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using OpenVSA.Core;
using OpenVSA.Dsp.Spectrum;
using OpenVSA.Hal;
using OpenVSA.Measurement.Markers;
using OpenVSA.Personality;
using OpenVSA.Measurement.State;
using OpenVSA.Ui.HotSpots;
using OpenVSA.Ui.Menus;
using OpenVSA.Ui.Rendering;
using OpenVSA.Ui.ToolWindows;

namespace OpenVSA.Ui
{
    /// <summary>
    /// What the shell puts behind each entry of <c>REQ-UI-061</c>'s menu contents.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>One switch, and everything it does not name is disabled with a reason.</strong> The
    /// requirement's criterion is that no item is "present and inert", and the way to be sure of
    /// that is to make the two halves fit together at construction: an entry the switch does not
    /// name must carry a reason in <see cref="ShellMenuTable"/>, and an entry it does name must not.
    /// <see cref="ShellMenuBuilder"/> throws either way round, so the shell will not open with a
    /// dead item on it and a reason cannot go stale after the work is done.
    /// </para>
    /// <para>
    /// <strong>The dynamic submenus fill themselves when opened.</strong> The instruments, the
    /// user's presets, the open traces, the trace formats and the layout presets are all discovered
    /// rather than declared, and filling them at start-up would mean a menu that is right once. They
    /// are filled here as well, so that a submenu is never empty — an empty submenu does not open at
    /// all, and an item that will not open reads as a broken one.
    /// </para>
    /// </remarks>
    public partial class ShellWindow : IShellMenuBinding
    {
        private MenuItem _presetMenu;
        private MenuItem _instrumentsMenu;
        private MenuItem _layoutMenu;
        private MenuItem _spectrogramMenu;
        private MenuItem _formatMenu;
        private MenuItem _yScaleMenu;
        private MenuItem _traceListMenu;
        private MenuItem _startItem;
        private MenuItem _stopItem;
        private MenuItem _disconnectItem;
        private MenuItem _forceWhiteBackgroundItem;
        private MenuItem _indicateFailuresItem;
        private MenuItem _indicateMarginItem;
        private MenuItem _spectrumTypeItem;
        private MenuItem _typeMenu;
        private readonly List<MenuItem> _personalityItems = new List<MenuItem>();

        private ComboBox _traceChooser;
        private ComboBox _markerChooser;
        private ToggleButton _hideTraceButton;
        private ToggleButton _hideMarkerButton;
        private Button _fullSpanButton;

        private MeasurementKind _measurementKind = MeasurementKind.Spectrum;

        /// <summary>
        /// Whether the shell may open dialogs, take the clipboard or close itself.
        /// </summary>
        /// <remarks>
        /// <para>
        /// True in the application, and that is the only thing it is in the application. It is false
        /// in the tests that drive every menu item in turn, because three of the things a menu item
        /// legitimately does — putting a modal file picker on screen, replacing the contents of the
        /// machine's clipboard, and closing the window — reach outside the test and cannot be
        /// answered from inside it.
        /// </para>
        /// <para>
        /// What is skipped is the dialog, never the routing. The click still reaches the handler and
        /// the handler still runs the part of itself that does not prompt, which is what
        /// <c>REQ-UI-061</c>'s "enabled and functional" is being checked for.
        /// </para>
        /// </remarks>
        public bool Interactive { get; set; } = true;

        /// <summary>The path of the last menu item run, for a test to read back.</summary>
        public string LastCommand { get; private set; } = string.Empty;

        /// <summary>
        /// The markers on the primary trace (<c>REQ-MKR-001</c>).
        /// </summary>
        /// <remarks>
        /// Exposed for the same reason <see cref="MenuBar"/> and <see cref="DocumentArea"/> are:
        /// the embedded markers toolbar of <c>REQ-UI-062</c> is specified in terms of what it does
        /// to the markers, and a test of it has to be able to look at them.
        /// </remarks>
        public OpenVSA.Measurement.Markers.MarkerSet Markers => _markers;

        /// <summary>Builds the menu bar from <c>REQ-UI-061</c>'s table.</summary>
        private void BuildMenuBar() => ShellMenuBuilder.Build(MainMenu, this);

        /// <inheritdoc />
        void IShellMenuBinding.Ran(string path) => LastCommand = path;

        /// <inheritdoc />
        MenuItem IShellMenuBinding.Bind(string path, ShellMenuEntry entry)
        {
            // REQ-UI-072's seven tabs, which REQ-UI-061's Analysis menu lists individually. Matched
            // against the dialog's own tab names rather than written out again, so the menu cannot
            // offer a tab the dialog does not have or miss one it does - and the requirement's
            // stated order is the table's, checked there.
            string tab = TabBehind(path);

            if (tab != null)
            {
                string chosen = tab;
                return Runs((sender, e) => OpenAnalysis(chosen));
            }

            switch (path)
            {
                // ---- File ---------------------------------------------------------------------
                case "File > Recall > Setup":
                    return Runs(OnRecallState);

                case "File > Preset":
                    return _presetMenu = Fills(OnPresetMenuOpened);

                case "File > Preset > Measurement":
                    return PresetItem(PresetVariant.Measurement);

                case "File > Preset > Measurement to Defaults":
                    return PresetItem(PresetVariant.MeasurementToDefaults);

                case "File > Preset > Setup":
                    return PresetItem(PresetVariant.Setup);

                case "File > Preset > Traces":
                    return PresetItem(PresetVariant.Traces);

                case "File > Preset > Application and Traces":
                    return PresetItem(PresetVariant.ApplicationAndTraces);

                case "File > Preset > Display Preferences":
                    return PresetItem(PresetVariant.DisplayPreferences);

                case "File > Preset > Toolbars":
                    return PresetItem(PresetVariant.Toolbars);

                case "File > Preset > Factory Defaults":
                    return PresetItem(PresetVariant.FactoryDefaults);

                case "File > Save > Setup":
                    return Runs(OnSaveState);

                case "File > Save > Preset":
                    return Runs(OnSavePreset);

                case "File > Export > Trace bitmap":
                    return Runs((sender, e) => SaveActiveTraceBitmap());

                case "File > Print > Print trace":
                    return Runs(OnPrintTrace);

                case "File > Print > Force white background":
                    return _forceWhiteBackgroundItem = Ticked(
                        _traceDisplay.ForceWhiteBackgroundOnPrint, OnLimitIndicationChanged);

                case "File > Exit":
                    return Runs((sender, e) => OnExit());

                // ---- Edit ---------------------------------------------------------------------
                case "Edit > Copy":
                    return Runs((sender, e) => CopyActiveTrace());

                case "Edit > Copy Markers":
                    return Runs((sender, e) => CopyMarkers());

                // ---- Hardware -----------------------------------------------------------------
                case "Hardware > Instruments…":
                    _instrumentsMenu = Fills((sender, e) => PopulateInstrumentsMenu());
                    PopulateInstrumentsMenu();
                    return _instrumentsMenu;

                case "Hardware > Rediscover":
                    return Runs((sender, e) => Rediscover());

                case "Hardware > Disconnect":
                    _disconnectItem = Runs((sender, e) => Disconnect());
                    ShowConnectionState();
                    return _disconnectItem;

                // ---- Acquisition --------------------------------------------------------------
                case "Acquisition > Amplitude…":
                    return Runs((sender, e) => ShowSetting(ReferenceLevelBox, "reference level"));

                case "Acquisition > Trigger…":
                    return Runs((sender, e) => ShowSetting(TriggerBox, "trigger"));

                case "Acquisition > Player Window":
                    return _toolWindows.MenuItemOf(ToolWindow.Player);

                case "Acquisition > Control > Start":
                    _startItem = Runs(OnStart);
                    SetStartEnabled(false);
                    return _startItem;

                case "Acquisition > Control > Stop":
                    _stopItem = Runs(OnStop);
                    SetStopEnabled(false);
                    return _stopItem;

                case "Acquisition > Control > Pause":
                    return Runs((sender, e) => PauseOrResume());

                case "Acquisition > Control > Restart":
                    return Runs((sender, e) => RestartMeasurement());

                // ---- Analysis -----------------------------------------------------------------
                case "Analysis > Type":
                    return _typeMenu = new MenuItem();

                case "Analysis > Type > Spectrum":
                    return _spectrumTypeItem = Ticked(
                        true, (sender, e) => ChooseMeasurementKind(MeasurementKind.Spectrum));

                // ---- Trace --------------------------------------------------------------------
                case "Trace > Trace List":
                    _traceListMenu = Fills((sender, e) => PopulateTraceListMenu());
                    PopulateTraceListMenu();
                    return _traceListMenu;

                case "Trace > New Trace":
                    return Runs(OnAddTrace);

                case "Trace > Format":
                    _formatMenu = Fills((sender, e) => PopulateFormatMenu());
                    PopulateFormatMenu();
                    return _formatMenu;

                case "Trace > Y Scale":
                    _yScaleMenu = Fills((sender, e) => PopulateYScaleMenu());
                    PopulateYScaleMenu();
                    return _yScaleMenu;

                case "Trace > OBW…":
                    return Runs((sender, e) => ShowOccupiedBandwidth());

                case "Trace > ACP…":
                    return Runs((sender, e) => ShowAdjacentChannelPower());

                case "Trace > Limit Tests… > Indicate limit failures":
                    return _indicateFailuresItem = Ticked(
                        _traceDisplay.IndicateLimitFailures, OnLimitIndicationChanged);

                case "Trace > Limit Tests… > Indicate margin warnings":
                    return _indicateMarginItem = Ticked(
                        _traceDisplay.IndicateMarginWarnings, OnLimitIndicationChanged);

                case "Trace > Spectrogram / Colour Map":
                    _spectrogramMenu = Fills((sender, e) => BuildSpectrogramMapMenu());
                    return _spectrogramMenu;

                case "Trace > Auto Scale":
                    return Runs((sender, e) => AutoScaleActiveTrace());

                case "Trace > Copy Trace":
                    return Runs((sender, e) => CopyActiveTraceToNewWindow());

                // ---- Marker -------------------------------------------------------------------
                case "Marker > Markers Window":
                    return _toolWindows.MenuItemOf(ToolWindow.Markers);

                case "Marker > New Marker > Normal":
                    return Runs(OnAddMarker);

                case "Marker > New Marker > Delta":
                    return Runs(OnAddDelta);

                case "Marker > New Marker > Fixed":
                    return Runs(OnAddFixed);

                case "Marker > Position…":
                    return Runs((sender, e) => PromptForMarkerPosition());

                case "Marker > Peak Search > Peak":
                    return Runs(OnPeakSearch);

                case "Marker > Peak Search > Next peak":
                    return Runs(OnNextPeak);

                case "Marker > Peak Search > Minimum":
                    return Runs(OnMinimumSearch);

                case "Marker > Copy to Clipboard":
                    return Runs((sender, e) => CopyMarkers());

                case "Marker > All Markers Off":
                    return Runs(OnDeleteAllMarkers);

                // ---- Utilities ----------------------------------------------------------------
                case "Utilities > Display Preferences…":
                    return Runs(OnDisplayPreferences);

                case "Utilities > Toolbars…":
                    return Runs(OnToolbarCustomiser);

                // ---- Window -------------------------------------------------------------------
                case "Window > Output":
                    return _toolWindows.MenuItemOf(ToolWindow.Output);

                case "Window > SCPI Log":
                    return _toolWindows.MenuItemOf(ToolWindow.ScpiLog);

                case "Window > Event Log":
                    return _toolWindows.MenuItemOf(ToolWindow.EventLog);

                case "Window > Contexts":
                    return _toolWindows.MenuItemOf(ToolWindow.Contexts);

                case "Window > Block Diagram":
                    return _toolWindows.MenuItemOf(ToolWindow.BlockDiagram);

                case "Window > Macros":
                    return _toolWindows.MenuItemOf(ToolWindow.Macros);

                case "Window > Trace Layout":
                    _layoutMenu = Fills((sender, e) => BuildLayoutMenu());
                    return _layoutMenu;

                case "Window > Resize Traces":
                    return Runs(OnResizeTraces);

                // ---- Help ---------------------------------------------------------------------
                case "Help > Help":
                    return Runs((sender, e) => ShowHelp(ShellShortcuts.ContextHelp));

                case "Help > Dynamic Help":
                    return Runs((sender, e) => ShowHelp(ShellShortcuts.DynamicHelp));

                case "Help > Privacy":
                    return Runs((sender, e) => ShowPrivacy());

                case "Help > About":
                    return Runs((sender, e) => ShowAbout());

                default:
                    // Not implemented, and REQ-UI-061's table says why. The builder disables the
                    // item and shows the reason; if there is no reason there, it throws.
                    return null;
            }
        }

        /// <inheritdoc />
        ToolBar IShellMenuBinding.Toolbar(string path, ShellMenuEntry entry)
        {
            switch (path)
            {
                case "Trace > Trace tools":
                    return TraceToolbar();

                case "Marker > Marker tools":
                    return MarkerToolbar();

                default:
                    return null;
            }
        }

        // ---- Item construction ---------------------------------------------------------------

        /// <summary>
        /// The Analysis dialog tab an entry opens, or <c>null</c> if it opens none.
        /// </summary>
        /// <param name="path">The entry's path.</param>
        private static string TabBehind(string path)
        {
            const string Prefix = "Analysis > ";

            if (!path.StartsWith(Prefix, StringComparison.Ordinal))
            {
                return null;
            }

            string name = path.Substring(Prefix.Length).TrimEnd('…');

            foreach (string tab in Dialogs.AnalysisDialog.TabNames)
            {
                if (string.Equals(tab, name, StringComparison.Ordinal))
                {
                    return tab;
                }
            }

            return null;
        }

        /// <summary>Enables or disables Acquisition &gt; Control &gt; Start.</summary>
        private void SetStartEnabled(bool on)
        {
            if (_startItem != null)
            {
                _startItem.IsEnabled = on;
                _startItem.ToolTip = on
                    ? "Begin acquiring from the connected instrument."
                    : "Nothing is connected. Choose an instrument under Hardware > Instruments… " +
                      "first.";

                ToolTipService.SetShowOnDisabled(_startItem, true);
            }
        }

        /// <summary>Enables or disables Acquisition &gt; Control &gt; Stop.</summary>
        private void SetStopEnabled(bool on)
        {
            if (_stopItem != null)
            {
                _stopItem.IsEnabled = on;
                _stopItem.ToolTip = on
                    ? "Stop acquiring. The last frame stays on the trace."
                    : "Nothing is being acquired, so there is nothing to stop.";

                ToolTipService.SetShowOnDisabled(_stopItem, true);
            }
        }

        /// <summary>Enables or disables Full Span on the embedded trace toolbar.</summary>
        private void SetFullSpanEnabled(bool on)
        {
            if (_fullSpanButton != null)
            {
                _fullSpanButton.IsEnabled = on;
            }
        }

        private static MenuItem Runs(RoutedEventHandler handler)
        {
            var item = new MenuItem();
            item.Click += handler;
            return item;
        }

        private static MenuItem Ticked(bool on, RoutedEventHandler handler)
        {
            var item = new MenuItem { IsCheckable = true, IsChecked = on };
            item.Click += handler;
            return item;
        }

        private static MenuItem Fills(RoutedEventHandler onOpened)
        {
            var item = new MenuItem();
            item.SubmenuOpened += onOpened;
            return item;
        }

        // ---- The dynamic submenus -------------------------------------------------------------

        /// <summary>
        /// Fills the instrument list with what discovery found (<c>REQ-NFR-032</c>).
        /// </summary>
        private void PopulateInstrumentsMenu()
        {
            if (_instrumentsMenu == null)
            {
                return;
            }

            _instrumentsMenu.Items.Clear();

            if (_registry.Providers.Count == 0)
            {
                // Said rather than left blank. REQ-NFR-032's visible half is that the application
                // starts with no hardware and says what is available and what is not.
                _instrumentsMenu.Items.Add(new MenuItem
                {
                    Header = "None discovered",
                    IsEnabled = false,
                    ToolTip = "No front end provider reported an instrument. Hardware > " +
                              "Rediscover looks again; the Hardware pane says what each provider " +
                              "reported and why.",
                });

                ToolTipService.SetShowOnDisabled(
                    (MenuItem)_instrumentsMenu.Items[0], true);

                return;
            }

            foreach (FrontEndDescriptor descriptor in _registry.Providers)
            {
                FrontEndDescriptor captured = descriptor;

                var item = new MenuItem
                {
                    Header = descriptor.DisplayName,
                    IsCheckable = true,
                    IsChecked = _activeFrontEnd != null &&
                                string.Equals(
                                    _activeFrontEnd.DisplayName,
                                    descriptor.DisplayName,
                                    StringComparison.Ordinal),
                };

                item.Click += (sender, e) => SelectFrontEnd(captured, (MenuItem)sender);
                _instrumentsMenu.Items.Add(item);
            }
        }

        /// <summary>Fills the trace list with the traces that are open (<c>REQ-UI-020</c>).</summary>
        private void PopulateTraceListMenu()
        {
            if (_traceListMenu == null)
            {
                return;
            }

            _traceListMenu.Items.Clear();

            foreach (char letter in Documents.Traces)
            {
                char captured = letter;

                var item = new MenuItem
                {
                    Header = "Trace " + letter,
                    IsCheckable = true,
                    IsChecked = Documents.ActiveTrace == letter,
                };

                item.Click += (sender, e) => Documents.ActiveTrace = captured;
                _traceListMenu.Items.Add(item);
            }

            if (_traceListMenu.Items.Count == 0)
            {
                _traceListMenu.Items.Add(new MenuItem { Header = "No traces", IsEnabled = false });
            }
        }

        /// <summary>
        /// Fills the format list from the formats a trace can be shown in (<c>REQ-TRC-001</c>).
        /// </summary>
        private void PopulateFormatMenu()
        {
            if (_formatMenu == null)
            {
                return;
            }

            _formatMenu.Items.Clear();

            TracePlot plot = Documents.ActivePlot;

            foreach (TraceFormat format in TraceFormatText.Formats)
            {
                TraceFormat captured = format;

                var item = new MenuItem
                {
                    Header = TraceFormatText.Describe(format),
                    IsCheckable = true,
                    IsChecked = plot != null && plot.CurrentFormat == format,
                };

                item.Click += (sender, e) => ChooseFormat(captured);
                _formatMenu.Items.Add(item);
            }
        }

        /// <summary>
        /// Fills the Y scale list with the per-division steps the plot offers (<c>REQ-UI-012</c>).
        /// </summary>
        private void PopulateYScaleMenu()
        {
            if (_yScaleMenu == null)
            {
                return;
            }

            _yScaleMenu.Items.Clear();

            TracePlot plot = Documents.ActivePlot;

            if (plot == null)
            {
                _yScaleMenu.Items.Add(new MenuItem { Header = "No trace", IsEnabled = false });
                return;
            }

            // From the hot spot's own ladder rather than a second list of steps. The plot rebuilds
            // it when the axis stops being decibels (REQ-TRC-001), and a menu holding the old one
            // would offer dB/div on a phase trace.
            var ladder = plot.PerDivisionHotSpot.Value as ChoiceHotSpotValue;

            if (ladder == null)
            {
                _yScaleMenu.Items.Add(new MenuItem { Header = "No scale", IsEnabled = false });
                return;
            }

            foreach (string choice in ladder.Options)
            {
                string captured = choice;

                var item = new MenuItem
                {
                    Header = choice,
                    IsCheckable = true,
                    IsChecked = string.Equals(
                        choice, ladder.Text, StringComparison.Ordinal),
                };

                item.Click += (sender, e) => ChoosePerDivision(captured);
                _yScaleMenu.Items.Add(item);
            }
        }

        // ---- The embedded toolbars -------------------------------------------------------------

        /// <summary>
        /// The trace toolbar embedded at the top of the Trace menu (<c>REQ-UI-062</c>).
        /// </summary>
        /// <remarks>
        /// <para>
        /// <strong>Select, add, remove, hide</strong> — the four the requirement names, plus
        /// <c>REQ-DSP-023</c>'s Full Span, which is a trace-level control with nowhere else to
        /// live: <c>REQ-UI-063</c>'s toolbars have exact contents and it is not among them.
        /// </para>
        /// <para>
        /// <strong>Hiding is not removing.</strong> A hidden trace is still open, still fed and
        /// still in the trace list; what it has lost is its window. That is what makes the pair
        /// worth having — a trace kept for comparison can be got out of the way without being
        /// closed and rebuilt.
        /// </para>
        /// <para>
        /// Nothing here is a <see cref="MenuItem"/>, which is what keeps the menu open while it is
        /// used: <c>REQ-UI-062</c>'s last criterion is that acting on the toolbar takes effect
        /// "without first dismissing the menu", and that is the whole point of embedding one.
        /// </para>
        /// </remarks>
        private ToolBar TraceToolbar()
        {
            var bar = new ToolBar { Focusable = false };

            _traceChooser = new ComboBox
            {
                MinWidth = 74.0,
                Focusable = false,
                ToolTip = "The trace the trace commands act on.",
            };

            _traceChooser.SelectionChanged += OnTraceChosenFromToolbar;

            var add = new Button
            {
                Content = "New",
                ToolTip = "Open another trace, in the next format round the list.",
            };

            add.Click += OnAddTrace;

            var remove = new Button
            {
                Content = "Close",
                ToolTip = "Close the active trace. The last one cannot be closed.",
            };

            remove.Click += OnRemoveTrace;

            _hideTraceButton = new ToggleButton
            {
                Content = "Hide",
                ToolTip = "Take the active trace's window off the arrangement, keeping the trace.",
            };

            // Checked and Unchecked, not Click. A ToggleButton driven through the automation layer
            // has its IsChecked set by the peer, which raises those two and never raises Click -- so
            // bound to Click this button lit up for a screen reader and did nothing, which is what
            // REQ-TST-008's smoke suite caught. Every toggle on the main toolbars already goes
            // through WhenToggled; these two embedded in menus were the ones left out.
            WhenToggled(_hideTraceButton, () => ShowActiveTrace(false), () => ShowActiveTrace(true));

            _fullSpanButton = new Button
            {
                Content = "Full Span",
                IsEnabled = false,
                ToolTip = "Return the analysis to the whole captured band.",
            };

            _fullSpanButton.Click += OnFullSpan;

            bar.Items.Add(_traceChooser);
            bar.Items.Add(add);
            bar.Items.Add(remove);
            bar.Items.Add(_hideTraceButton);
            bar.Items.Add(new Separator());
            bar.Items.Add(_fullSpanButton);

            FillTraceChooser();

            return bar;
        }

        /// <summary>
        /// The markers toolbar embedded at the top of the Marker menu (<c>REQ-UI-062</c>).
        /// </summary>
        /// <remarks>
        /// The same four operations for markers: which one is selected, add, remove, hide. A
        /// hidden marker keeps its number, its position and its readout, and loses its glyph.
        /// </remarks>
        private ToolBar MarkerToolbar()
        {
            var bar = new ToolBar { Focusable = false };

            _markerChooser = new ComboBox
            {
                MinWidth = 74.0,
                Focusable = false,
                ToolTip = "The selected marker.",
            };

            _markerChooser.SelectionChanged += OnMarkerChosenFromToolbar;

            var add = new Button
            {
                Content = "New",
                ToolTip = "Place a marker at the highest point of the trace.",
            };

            add.Click += OnAddMarker;

            var remove = new Button
            {
                Content = "Delete",
                ToolTip = "Remove the selected marker. All Markers Off removes every one.",
            };

            remove.Click += OnDeleteMarker;

            _hideMarkerButton = new ToggleButton
            {
                Content = "Hide",
                ToolTip = "Stop drawing the selected marker, keeping its number and position.",
            };

            // Checked and Unchecked rather than Click -- see the trace toolbar's Hide button.
            WhenToggled(
                _hideMarkerButton, () => ShowSelectedMarker(false), () => ShowSelectedMarker(true));

            bar.Items.Add(_markerChooser);
            bar.Items.Add(add);
            bar.Items.Add(remove);
            bar.Items.Add(_hideMarkerButton);

            FillMarkerChooser();

            return bar;
        }

        /// <summary>Fills the embedded trace toolbar's chooser with the open traces.</summary>
        private void FillTraceChooser()
        {
            if (_traceChooser == null)
            {
                return;
            }

            _followingToolbar = true;

            try
            {
                _traceChooser.Items.Clear();

                // The active context's windows, not every window open (REQ-DAT-010). Another
                // context's traces are hidden and are not its to select; listing them would offer
                // the user a trace that belongs to a measurement they are not looking at.
                foreach (char letter in _contextSet.Active.Traces)
                {
                    // Hidden traces are listed too, marked as such: they are still open, and the
                    // chooser is how a user gets back to one in order to show it again.
                    _traceChooser.Items.Add(
                        Documents.IsVisible(letter)
                            ? "Trace " + letter
                            : "Trace " + letter + " (hidden)");
                }

                _traceChooser.SelectedIndex = IndexOfTrace(Documents.ActiveTrace);

                // Inside the guard, now that the button acts on Checked and Unchecked: bringing it
                // into line with the trace it describes must not read as somebody pressing it.
                if (_hideTraceButton != null)
                {
                    _hideTraceButton.IsChecked = !Documents.IsVisible(Documents.ActiveTrace);
                }
            }
            finally
            {
                _followingToolbar = false;
            }
        }

        private int IndexOfTrace(char trace)
        {
            // The same list the chooser was filled from, or the selected index would name a
            // different trace from the one it highlights.
            IReadOnlyList<char> traces = _contextSet.Active.Traces;

            for (int index = 0; index < traces.Count; index++)
            {
                if (traces[index] == trace)
                {
                    return index;
                }
            }

            return -1;
        }

        private void OnTraceChosenFromToolbar(object sender, SelectionChangedEventArgs e)
        {
            // Indexed into the list the chooser was filled from: the active context's traces.
            IReadOnlyList<char> traces = _contextSet.Active.Traces;

            if (_followingToolbar || _traceChooser.SelectedIndex < 0 ||
                _traceChooser.SelectedIndex >= traces.Count)
            {
                return;
            }

            Documents.ActiveTrace = traces[_traceChooser.SelectedIndex];
        }

        /// <summary>Takes the active trace's window off the arrangement, or puts it back.</summary>
        /// <param name="wanted">Whether the trace is to be shown.</param>
        /// <remarks>
        /// Told which way round rather than reading the button back. The two callers are the
        /// button's own <c>Checked</c> and <c>Unchecked</c>, so the direction is already known — and
        /// reading <c>IsChecked</c> inside the handler is what tied this to <c>Click</c>, which the
        /// automation layer does not raise.
        /// </remarks>
        private void ShowActiveTrace(bool wanted)
        {
            char trace = Documents.ActiveTrace;

            if (!Documents.SetVisible(trace, wanted))
            {
                // The last visible trace cannot be hidden: an empty document area is a state with
                // no way out of it from the document area itself.
                FollowingToolbar(
                    () => _hideTraceButton.IsChecked = !Documents.IsVisible(trace));

                StatusText.Content = wanted
                    ? "Trace " + trace + " is already shown."
                    : "The last visible trace cannot be hidden.";

                return;
            }

            FillTraceChooser();

            StatusText.Content = wanted
                ? "Trace " + trace + " shown"
                : "Trace " + trace + " hidden — it is still open and still measuring.";
        }

        /// <summary>What the chooser shows when the trace carries no markers.</summary>
        /// <remarks>
        /// A constant because two places have to agree on it: the one that puts it there and the one
        /// that recognises it as already being there.
        /// </remarks>
        private const string NoMarkersEntry = "No markers";

        /// <summary>The entry the chooser shows for a marker.</summary>
        /// <param name="marker">The marker.</param>
        /// <remarks>
        /// Factored out because <see cref="FillMarkerChooser"/> both writes these and compares
        /// against them. Were the two spellings to drift, the comparison would either never match —
        /// rebuilding every frame, which is what this is here to stop — or match when it should not,
        /// which would leave a stale label on screen. One expression cannot drift from itself.
        /// </remarks>
        private static string MarkerChooserEntry(Marker marker) =>
            marker.IsVisible ? marker.WindowLabel : marker.WindowLabel + " (hidden)";

        /// <summary>
        /// Fills the embedded markers toolbar's chooser, if it does not already say the right thing.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <strong>This runs on every drawn frame.</strong> <c>RefreshMarkers</c> calls it from the
        /// drawing path, so at sixty frames a second it was clearing and refilling a live
        /// <see cref="ComboBox"/>'s items sixty times a second — and with no markers placed, which
        /// is the ordinary state, that is <c>Clear</c>, <c>Add("No markers")</c> and
        /// <c>SelectedIndex = 0</c> for ever. Each pass drives a −1 → 0 selection transition and
        /// raises <c>SelectionChanged</c> twice, all of it to arrive back at what was already there.
        /// </para>
        /// <para>
        /// <strong>The guard compares the answer, not a proxy for it.</strong> A version counter on
        /// the marker set was the obvious alternative and is the more dangerous one: the entries
        /// depend on each marker's number, type, visibility, trace letter and its reference's letter
        /// and number, and <c>IsVisible</c> is set on the marker directly rather than through the
        /// set — so a counter would have to be threaded through every one of those, and the cost of
        /// missing one is a chooser that silently shows the wrong marker. Recomputing the entries
        /// and comparing them with what is displayed cannot go stale, because it is the same
        /// computation the rebuild would do.
        /// </para>
        /// </remarks>
        private void FillMarkerChooser()
        {
            if (_markerChooser == null)
            {
                return;
            }

            IReadOnlyList<Marker> markers = _markers.Markers;
            int wanted = markers.Count == 0 ? 0 : IndexOfSelectedMarker();

            _followingToolbar = true;

            try
            {
                if (MarkerChooserDiffers(markers, wanted))
                {
                    _markerChooser.Items.Clear();

                    foreach (Marker marker in markers)
                    {
                        _markerChooser.Items.Add(MarkerChooserEntry(marker));
                    }

                    if (markers.Count == 0)
                    {
                        _markerChooser.Items.Add(NoMarkersEntry);
                    }

                    _markerChooser.SelectedIndex = wanted;
                }

                // Inside the guard, for the reason FillTraceChooser's is. Set unconditionally: WPF
                // raises nothing when a dependency property is assigned the value it already holds,
                // so this costs nothing to leave outside the comparison and cannot fall out of step
                // with a marker whose visibility changed without changing its entry.
                Marker selected = _markers.Selected;

                if (_hideMarkerButton != null)
                {
                    _hideMarkerButton.IsChecked = selected != null && !selected.IsVisible;
                    _hideMarkerButton.IsEnabled = selected != null;
                }
            }
            finally
            {
                _followingToolbar = false;
            }
        }

        /// <summary>Whether the chooser shows anything other than what it should.</summary>
        /// <param name="markers">The markers it should be showing.</param>
        /// <param name="wanted">The index it should have selected.</param>
        private bool MarkerChooserDiffers(IReadOnlyList<Marker> markers, int wanted)
        {
            ItemCollection items = _markerChooser.Items;

            if (items.Count != Math.Max(1, markers.Count) || _markerChooser.SelectedIndex != wanted)
            {
                return true;
            }

            if (markers.Count == 0)
            {
                return !string.Equals(items[0] as string, NoMarkersEntry, StringComparison.Ordinal);
            }

            for (int index = 0; index < markers.Count; index++)
            {
                if (!string.Equals(
                    items[index] as string, MarkerChooserEntry(markers[index]), StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        private int IndexOfSelectedMarker()
        {
            IReadOnlyList<Marker> markers = _markers.Markers;

            for (int index = 0; index < markers.Count; index++)
            {
                if (markers[index].IsSelected)
                {
                    return index;
                }
            }

            return -1;
        }

        private void OnMarkerChosenFromToolbar(object sender, SelectionChangedEventArgs e)
        {
            if (_followingToolbar || _markerChooser.SelectedIndex < 0 ||
                _markerChooser.SelectedIndex >= _markers.Markers.Count)
            {
                return;
            }

            _markers.Select(_markers.Markers[_markerChooser.SelectedIndex]);

            RefreshMarkers();
            FillMarkerChooser();
        }

        /// <summary>Stops drawing the selected marker, or draws it again.</summary>
        /// <param name="wanted">Whether the marker is to be drawn.</param>
        /// <remarks>Told which way round, for the reason <see cref="ShowActiveTrace"/> is.</remarks>
        private void ShowSelectedMarker(bool wanted)
        {
            Marker selected = _markers.Selected;

            if (selected == null)
            {
                StatusText.Content = "No marker is selected to hide.";
                return;
            }

            selected.IsVisible = wanted;

            RefreshMarkers();
            FillMarkerChooser();

            StatusText.Content = selected.IsVisible
                ? selected.WindowLabel + " shown"
                : selected.WindowLabel + " hidden — it still reads " +
                  EngineeringText.Frequency(selected.XHz, 6) + ".";
        }

        // ---- What the new items do --------------------------------------------------------------

        /// <summary>Closes the shell (<c>REQ-UI-061</c> File &gt; Exit).</summary>
        private void OnExit()
        {
            if (!Interactive)
            {
                StatusText.Content = "Exit";
                return;
            }

            Close();
        }

        /// <summary>
        /// Writes the active trace to the clipboard as text (<c>REQ-UI-061</c> Edit &gt; Copy).
        /// </summary>
        /// <remarks>
        /// The requirement asks for "the contents of a trace, marker readout, or trace hotspot".
        /// Text rather than an image: what a user pastes into a spreadsheet or a notebook has to be
        /// the numbers, and the picture is already on File &gt; Export &gt; Trace bitmap.
        /// </remarks>
        private void CopyActiveTrace()
        {
            SpectrumFrame frame = _frame;

            if (frame == null)
            {
                StatusText.Content = "There is no measured trace to copy.";
                return;
            }

            var text = new System.Text.StringBuilder();

            text.AppendLine("Frequency (Hz)\tLevel (dBm)");

            for (int point = 0; point < frame.LevelsDbm.Length; point++)
            {
                text.Append(
                    frame.FrequencyAt(point).ToString("0.###", CultureInfo.InvariantCulture));
                text.Append('\t');
                text.AppendLine(
                    frame.LevelsDbm[point].ToString("0.###", CultureInfo.InvariantCulture));
            }

            PutOnClipboard(text.ToString(), frame.LevelsDbm.Length + " trace points");
        }

        /// <summary>Writes the marker readouts to the clipboard (<c>REQ-MKR-006</c>).</summary>
        private void CopyMarkers()
        {
            if (_markers.Markers.Count == 0)
            {
                StatusText.Content = "There are no markers to copy.";
                return;
            }

            if (_frame == null)
            {
                StatusText.Content = "There is no measurement to read the markers against.";
                return;
            }

            var text = new System.Text.StringBuilder();

            text.AppendLine("Marker	Frequency (Hz)	Level");

            foreach (Marker marker in _markers.Markers)
            {
                MarkerReading reading = marker.Read(_frame);

                text.Append(marker.WindowLabel).Append('	');

                if (!reading.IsValid)
                {
                    // REQ-UI-032's convention for a readout that has no value, carried into the
                    // clipboard rather than pasted as a blank cell.
                    text.AppendLine("NAN	NAN");
                    continue;
                }

                text.Append(reading.XHz.ToString("0.###", CultureInfo.InvariantCulture));
                text.Append('	');
                text.Append(reading.YDbm.ToString("0.##", CultureInfo.InvariantCulture));
                text.AppendLine(marker.Type == MarkerType.Delta ? " dB" : " dBm");
            }

            PutOnClipboard(text.ToString(), _markers.Markers.Count + " marker readouts");
        }

        private void PutOnClipboard(string text, string what)
        {
            if (!Interactive)
            {
                // The clipboard belongs to the machine, not to this window. A test suite that
                // replaced whatever the user had copied would be a side effect nobody asked for -
                // the same reason PersistPreferences exists.
                StatusText.Content = "Copied " + what;
                return;
            }

            try
            {
                Clipboard.SetText(text);
                StatusText.Content = "Copied " + what;
            }
            catch (System.Runtime.InteropServices.ExternalException failure)
            {
                // Another process can hold the clipboard open. Reported rather than thrown at the
                // dispatcher: a failed copy must not take the measurement down.
                StatusText.Content = "Could not copy: " + failure.Message;
            }
        }

        /// <summary>Looks for instruments again (<c>REQ-NFR-032</c>).</summary>
        private void Rediscover()
        {
            _registry = FrontEndRegistry.CreateDefault();

            PopulateInstrumentsMenu();
            ShowDiscoveryResults();

            StatusText.Content = _registry.Providers.Count == 1
                ? "Rediscovered: 1 instrument"
                : "Rediscovered: " + _registry.Providers.Count + " instruments";
        }

        /// <summary>Closes the connection to the active front end.</summary>
        private async void Disconnect()
        {
            if (_activeFrontEnd == null)
            {
                StatusText.Content = "Nothing is connected.";
                return;
            }

            string name = _activeFrontEnd.DisplayName;

            if (_engine != null)
            {
                await StopAcquisitionAsync().ConfigureAwait(true);
            }

            _activeFrontEnd.Dispose();
            _activeFrontEnd = null;

            SettingsGrid.IsEnabled = false;
            ShowConnectionState();
            PopulateInstrumentsMenu();

            StatusText.Content = "Disconnected from " + name;
        }

        /// <summary>Enables or disables the items that need a connection.</summary>
        private void ShowConnectionState()
        {
            if (_disconnectItem == null)
            {
                return;
            }

            _disconnectItem.IsEnabled = _activeFrontEnd != null;

            // Disabled by state rather than by phase, so the reason is about now rather than about
            // the road map - but it is still a reason, which is what the requirement asks for.
            _disconnectItem.ToolTip = _activeFrontEnd == null
                ? "Nothing is connected. Choose an instrument under Hardware > Instruments… first."
                : "Close the connection to " + _activeFrontEnd.DisplayName + ".";

            ToolTipService.SetShowOnDisabled(_disconnectItem, true);
        }

        /// <summary>Brings a setting in the measurement pane to the user's attention.</summary>
        /// <param name="control">The control to focus.</param>
        /// <param name="what">What it sets, for the status bar.</param>
        /// <remarks>
        /// <c>REQ-UI-061</c> lists Amplitude… and Trigger… as menu items, and both settings are on
        /// the measurement pane rather than in dialogs of their own. Taking the user to the control
        /// is the honest implementation of the item: it does the thing the item names, and it does
        /// not build a second surface for a setting that already has one.
        /// </remarks>
        private void ShowSetting(Control control, string what)
        {
            MeasurementPane.BringIntoView();

            control.Focus();

            var box = control as TextBox;

            if (box != null)
            {
                box.SelectAll();
            }

            StatusText.Content = SettingsGrid.IsEnabled
                ? "Set the " + what + " in the Measurement pane."
                : "The " + what + " needs an instrument: choose one under Hardware > Instruments….";
        }

        /// <summary>Chooses what kind of measurement this is.</summary>
        private void ChooseMeasurementKind(MeasurementKind kind)
        {
            _measurementKind = kind;

            if (_spectrumTypeItem != null)
            {
                _spectrumTypeItem.IsChecked = kind == MeasurementKind.Spectrum;
            }

            SelectPersonality(null);

            StatusText.Content = "Measurement type: " + kind;
        }

        /// <summary>
        /// Appends every discovered personality to Analysis ▸ Type (<c>REQ-ARC-003</c>).
        /// </summary>
        /// <remarks>
        /// <para>
        /// The criterion is that a personality assembly dropped into <c>Personalities\</c> "is
        /// discovered on next launch, <strong>appears in the measurement-type selector</strong>,
        /// and runs — with no rebuild of the host". Analysis ▸ Type is that selector, and it is
        /// already in <c>REQ-UI-061</c>'s list, so nothing is added to the menu bar: the four
        /// built-in types keep their places and the discovered ones follow them.
        /// </para>
        /// <para>
        /// Nothing here names a personality, a standard or an assembly. The shell learns what
        /// exists by asking the registry, which is the whole of what "no modification of L2–L4
        /// code" means in practice.
        /// </para>
        /// </remarks>
        private void AddDiscoveredPersonalities()
        {
            if (_personalities == null || _typeMenu == null)
            {
                return;
            }

            if (_personalities.Personalities.Count == 0)
            {
                return;
            }

            _typeMenu.Items.Add(new Separator());

            foreach (IMeasurementPersonality personality in _personalities.Personalities)
            {
                IMeasurementPersonality captured = personality;

                var item = new MenuItem
                {
                    Header = personality.DisplayName,
                    IsCheckable = true,
                    ToolTip = personality.Standard +
                        (string.IsNullOrEmpty(personality.StandardRevision)
                            ? string.Empty
                            : " " + personality.StandardRevision),
                };

                item.Click += (sender, e) => SelectPersonality(captured);

                _typeMenu.Items.Add(item);
                _personalityItems.Add(item);
            }
        }

        private void OnBlockAcquired(object sender, IqBlock block) => MeasureWithPersonality(block);

        /// <summary>Runs an action on the UI thread, immediately when already there.</summary>
        /// <param name="action">What to run.</param>
        /// <remarks>
        /// <see cref="System.Windows.Threading.Dispatcher.BeginInvoke(Delegate, object[])"/>
        /// unconditionally would leave the
        /// work queued when the caller is already the UI thread, and anything that then waited for
        /// it to be applied would have to pump the dispatcher to get it — which, from the dispatcher
        /// thread itself, is a nested frame that does not always come back. A test doing exactly
        /// that hung.
        /// </remarks>
        private void OnUi(Action action)
        {
            if (Dispatcher.CheckAccess())
            {
                action();
                return;
            }

            Dispatcher.BeginInvoke(action);
        }

        /// <summary>Shows what the active personality last measured.</summary>
        private void ShowResults()
        {
            ResultsHeading.Text = _results.Summary;
            ResultsList.ItemsSource = _results.Lines;
        }

        /// <summary>
        /// Runs the active personality over a block, on the pump thread (<c>REQ-ARC-003</c>).
        /// </summary>
        /// <param name="block">The acquisition, valid only for this call.</param>
        /// <remarks>
        /// <para>
        /// The readings are computed here and marshalled to the UI, because the block is disposed
        /// the moment this returns — <see cref="OpenVSA.Measurement.SpectrumEngine.BlockAcquired"/>
        /// says so. Posting the
        /// block to the dispatcher and measuring it there would read a buffer that had gone back to
        /// whoever owns it.
        /// </para>
        /// <para>
        /// A personality that refuses the block leaves the previous readings alone rather than
        /// blanking them. A measurement type that cannot use this acquisition has not produced a
        /// result of "nothing"; it has not produced a result.
        /// </para>
        /// </remarks>
        private void MeasureWithPersonality(IqBlock block)
        {
            IMeasurementPersonality personality = _activePersonality;

            if (personality == null || block == null || !personality.CanMeasure(block))
            {
                return;
            }

            IReadOnlyList<PersonalityReading> readings;

            try
            {
                readings = personality.Measure(block);
            }
            catch (Exception e)
            {
                // Reported and the personality dropped, rather than left throwing on every block
                // for the rest of the session. REQ-ARC-003 makes personalities third-party code;
                // one that faults must not take the measurement down with it.
                OnUi(() =>
                {
                    StatusText.Content = personality.DisplayName + " faulted and was deselected";
                    CapabilitiesText.Text = e.Message;
                    SelectPersonality(null);
                });

                return;
            }

            OnUi(() =>
            {
                if (!ReferenceEquals(_activePersonality, personality))
                {
                    // The type changed while this was in flight. Showing these would put one
                    // personality's readings under another's name.
                    return;
                }

                _results.Update(readings);
                ShowResults();
            });
        }

        /// <summary>Makes a personality the active measurement type, or none.</summary>
        /// <param name="personality">The personality, or <c>null</c> for plain spectrum.</param>
        private void SelectPersonality(IMeasurementPersonality personality)
        {
            _activePersonality = personality;

            foreach (MenuItem item in _personalityItems)
            {
                item.IsChecked = personality != null &&
                    string.Equals(
                        item.Header as string, personality.DisplayName, StringComparison.Ordinal);
            }

            if (_spectrumTypeItem != null && personality != null)
            {
                _spectrumTypeItem.IsChecked = false;
            }

            _results.Select(personality);
            ShowResults();

            if (personality != null)
            {
                StatusText.Content = "Measurement type: " + personality.DisplayName;
            }
        }

        private void ChooseFormat(TraceFormat format)
        {
            TracePlot plot = Documents.ActivePlot;

            if (plot == null || !plot.SetFormat(format))
            {
                StatusText.Content = "That format is not available for this trace.";
                return;
            }

            UpdateMarshalFormats();
            PopulateFormatMenu();

            StatusText.Content = "Trace " + Documents.ActiveTrace + ": " +
                TraceFormatText.Describe(format);
        }

        private void ChoosePerDivision(string choice)
        {
            TracePlot plot = Documents.ActivePlot;

            if (plot == null || !plot.PerDivisionHotSpot.Value.TrySet(choice))
            {
                return;
            }

            plot.PerDivisionHotSpot.Refresh();
            PopulateYScaleMenu();

            StatusText.Content = "Trace " + Documents.ActiveTrace + ": " + choice;
        }

        /// <summary>
        /// Reports the occupied bandwidth of the active trace (<c>REQ-DSP-040</c>).
        /// </summary>
        private void ShowOccupiedBandwidth()
        {
            SpectrumFrame frame = _frame;

            if (frame == null)
            {
                StatusText.Content = "There is no measured trace to compute over.";
                return;
            }

            OccupiedBandwidth occupied = BandMeasurements.Occupied(frame);

            string report =
                "Occupied bandwidth (99 %): " + PlanSummary.Frequency(occupied.BandwidthHz) +
                ", from " + PlanSummary.Frequency(occupied.LowerEdgeHz) +
                " to " + PlanSummary.Frequency(occupied.UpperEdgeHz);

            StatusText.Content = report;
            _outputLog.Append(report);
        }

        /// <summary>
        /// Reports adjacent channel power for the active trace (<c>REQ-DSP-041</c>).
        /// </summary>
        /// <remarks>
        /// Over the measured span, with the carrier taken as the middle third and one channel each
        /// side of it. A channel plan of the user's own is what Trace &gt; Calculation would set,
        /// and that waits on the channel definitions of Phase 2 — but a figure computed over a
        /// stated arrangement is worth more than an item that does nothing.
        /// </remarks>
        private void ShowAdjacentChannelPower()
        {
            SpectrumFrame frame = _frame;

            if (frame == null)
            {
                StatusText.Content = "There is no measured trace to compute over.";
                return;
            }

            double channel = frame.SpanHz / 3.0;

            AdjacentChannelPower power = BandMeasurements.Adjacent(
                frame, frame.CenterFrequencyHz, channel, new[] { channel });

            var report = new System.Text.StringBuilder();

            report.Append("ACP over ").Append(PlanSummary.Frequency(channel));
            report.Append(" channels: carrier ");
            report.Append(power.Carrier.TotalDbm.ToString("0.0", CultureInfo.CurrentCulture));
            report.Append(" dBm");

            foreach (AdjacentChannel adjacent in power.Channels)
            {
                report.Append(", ");
                report.Append(adjacent.OffsetHz > 0.0 ? "upper " : "lower ");
                report.Append(adjacent.RatioDb.ToString("0.0", CultureInfo.CurrentCulture));
                report.Append(" dBc");
            }

            StatusText.Content = report.ToString();
            _outputLog.Append(report.ToString());
        }

        /// <summary>Opens a second window on the active trace's data (<c>REQ-TRC-001</c>).</summary>
        private void CopyActiveTraceToNewWindow()
        {
            TracePlot source = Documents.ActivePlot;

            if (source == null)
            {
                StatusText.Content = "There is no trace to copy.";
                return;
            }

            TraceFormat format = source.CurrentFormat;
            char from = Documents.ActiveTrace;

            OnAddTrace(this, new RoutedEventArgs());

            TracePlot copy = Documents.ActivePlot;

            if (copy == null || copy == source)
            {
                return;
            }

            copy.SetFormat(format);
            UpdateMarshalFormats();

            StatusText.Content =
                "Trace " + Documents.ActiveTrace + " copied from trace " + from;
        }

        private void SelectNextMarker()
        {
            IReadOnlyList<Marker> markers = _markers.Markers;

            if (markers.Count == 0)
            {
                StatusText.Content = "There are no markers to select.";
                return;
            }

            int at = -1;

            for (int index = 0; index < markers.Count; index++)
            {
                if (markers[index].IsSelected)
                {
                    at = index;
                    break;
                }
            }

            _markers.Select(markers[(at + 1) % markers.Count]);
            RefreshMarkers();
        }

        /// <summary>
        /// Says what OpenVSA sends where (<c>REQ-UI-061</c> Help &gt; Privacy).
        /// </summary>
        /// <remarks>
        /// A short answer, and a true one. It is on the menu because a user is entitled to ask, and
        /// because the answer being "nothing, to nowhere" is worth stating rather than leaving to be
        /// inferred from the absence of a setting.
        /// </remarks>
        private void ShowPrivacy()
        {
            const string Statement =
                "OpenVSA sends nothing anywhere: no telemetry, no usage reporting, no update " +
                "check and no licence check. It talks to the instruments you point it at, and to " +
                "nothing else.";

            StatusText.Content = "Privacy: OpenVSA sends nothing anywhere.";
            _outputLog.Append(Statement);

            if (_toolWindows != null)
            {
                _toolWindows.SetOpen(ToolWindow.Output, true);
            }
        }

        /// <summary>What this build is (<c>REQ-UI-061</c> Help &gt; About).</summary>
        private void ShowAbout()
        {
            System.Reflection.Assembly assembly = typeof(ShellWindow).Assembly;

            string about =
                "OpenVSA " + assembly.GetName().Version +
                " — an open vector signal analyser, free and without licensing (REQ-LIC-010). " +
                "Built on .NET Framework " + Environment.Version + ". " +
                "Source and issue tracker: github.com/TGoodhew/OpenVSA";

            StatusText.Content = "OpenVSA " + assembly.GetName().Version;
            _outputLog.Append(about);

            if (_toolWindows != null)
            {
                _toolWindows.SetOpen(ToolWindow.Output, true);
            }
        }

        /// <summary>
        /// Applies one of <c>REQ-UI-061</c>'s preset variants.
        /// </summary>
        /// <param name="variant">Which preset was asked for.</param>
        /// <remarks>
        /// <para>
        /// <strong>The hardware setup is left alone, and that is structural rather than
        /// careful.</strong> The preset is computed by <see cref="Presets.Apply"/> over the state,
        /// and a state carries no front end, no resource string and no connection; the two parts of
        /// the hardware setup it does carry — the frequency reference and the source — are copied
        /// back by every variant. Nothing here disconnects, re-tunes or re-arms an instrument.
        /// </para>
        /// <para>
        /// Display preferences are reset separately because they are not in the state at all:
        /// <c>REQ-STA-002</c> keeps them in the sidecar, and the two files are deliberately
        /// independent.
        /// </para>
        /// </remarks>
        private void ApplyPreset(PresetVariant variant)
        {
            ApplicationState next = Presets.Apply(variant, StartingPoint(variant));

            ApplyState(next.Measurements[0]);

            PresetCategory scope = Presets.CategoriesOf(variant);

            if (Presets.Has(scope, PresetCategory.DisplayPreferences))
            {
                ResetDisplayPreferences();
            }

            // REQ-UI-064: File > Preset > Toolbars restores the five preconfigured toolbars to
            // their default contents and removes the custom ones. Asked of the scope rather than
            // of the variant, so that Factory Defaults — whose scope also names the toolbars —
            // resets them by the same route rather than by a second branch that could disagree.
            if (Presets.Has(scope, PresetCategory.Toolbars))
            {
                ResetToolbars();
            }

            StatusText.Content = "Preset: " + Presets.NameOf(variant);
            _eventLog.Append(
                "Preset " + Presets.NameOf(variant) + " applied. The hardware setup is unchanged.");
        }

        /// <summary>
        /// The state a preset resets from.
        /// </summary>
        /// <remarks>
        /// <see cref="PresetVariant.Measurement"/> starts from the user's saved startup preset when
        /// there is one, which is the whole difference between it and
        /// <see cref="PresetVariant.MeasurementToDefaults"/>. A startup preset that cannot be read
        /// is worth a line in the event log and the defaults, not a refusal.
        /// </remarks>
        private ApplicationState StartingPoint(PresetVariant variant)
        {
            // The active context alone, not every context in the session. A preset is one
            // measurement's setup (REQ-STA-005) and Presets.Apply works on Measurements[0], so a
            // multi-context state here would reset the pane from whichever context happened to be
            // first -- which is the active one only until a second context exists.
            ApplicationState current = ActiveContextState();

            if (variant != PresetVariant.Measurement)
            {
                return current;
            }

            try
            {
                if (!_presets.Contains(Presets.StartupName))
                {
                    return current;
                }

                ApplicationState startup = _presets.Load(Presets.StartupName);

                // The startup preset supplies the settings; the hardware half of the current state
                // still has to survive, and Presets.Apply is what carries it across.
                startup.Measurements[0].ContextName = current.Measurements[0].ContextName;
                startup.Measurements[0].Source = current.Measurements[0].Source;
                startup.Measurements[0].Input.ExternalReference =
                    current.Measurements[0].Input.ExternalReference;

                return startup;
            }
            catch (System.IO.IOException failure)
            {
                _eventLog.Append("The startup preset could not be read: " + failure.Message);
                return current;
            }
            catch (StateFormatException failure)
            {
                _eventLog.Append("The startup preset could not be read: " + failure.Message);
                return current;
            }
        }

        /// <summary>Returns colours, typefaces and trace display options to their defaults.</summary>
        private void ResetDisplayPreferences()
        {
            _colours.ResetAll();
            _fonts.ResetAll();
            _traceDisplay.ResetAll();

            ApplyColours();
            ApplyFonts();
            ApplyTraceDisplay();
            FollowTraceDisplayOptions();
        }

        private MenuItem PresetItem(PresetVariant variant) =>
            Runs((sender, e) => ApplyPreset(variant));
    }
}
