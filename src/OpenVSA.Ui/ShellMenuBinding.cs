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

        private ToggleButton _selectAreaButton;
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
        /// The trace tools <c>REQ-UI-061</c> embeds in the Trace menu.
        /// </summary>
        /// <remarks>
        /// <c>REQ-DSP-023</c>'s Select Area and Full Span live here. Select Area is a mode rather
        /// than an always-on gesture, so an imprecise click cannot change the measurement, and a
        /// mode wants a control that stays pressed while it is on — which is what it now has.
        /// </remarks>
        private ToolBar TraceToolbar()
        {
            var bar = new ToolBar { Focusable = false };

            _selectAreaButton = new ToggleButton
            {
                Content = "Select Area",
                ToolTip = "Drag across a trace to analyse just that band, without re-acquiring.",
            };

            _selectAreaButton.Click += OnToggleSelectArea;

            _fullSpanButton = new Button
            {
                Content = "Full Span",
                IsEnabled = false,
                ToolTip = "Return the analysis to the whole captured band.",
            };

            _fullSpanButton.Click += OnFullSpan;

            var close = new Button { Content = "Close Trace" };
            close.Click += OnRemoveTrace;

            bar.Items.Add(_selectAreaButton);
            bar.Items.Add(_fullSpanButton);
            bar.Items.Add(new Separator());
            bar.Items.Add(close);

            return bar;
        }

        /// <summary>The marker tools <c>REQ-UI-061</c> embeds in the Marker menu.</summary>
        private ToolBar MarkerToolbar()
        {
            var bar = new ToolBar { Focusable = false };

            var delete = new Button
            {
                Content = "Delete Selected",
                ToolTip = "Remove the selected marker. All Markers Off removes every one.",
            };

            delete.Click += OnDeleteMarker;

            var select = new Button
            {
                Content = "Select Next",
                ToolTip = "Move the selection to the next marker on this trace.",
            };

            select.Click += (sender, e) => SelectNextMarker();

            bar.Items.Add(delete);
            bar.Items.Add(select);

            return bar;
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

            StatusText.Content = "Measurement type: " + kind;
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

            if (Presets.Has(Presets.CategoriesOf(variant), PresetCategory.DisplayPreferences))
            {
                ResetDisplayPreferences();
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
            ApplicationState current = CaptureState();

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
