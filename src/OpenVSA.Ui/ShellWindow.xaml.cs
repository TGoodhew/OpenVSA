using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using OpenVSA.Core;
using OpenVSA.Dsp.Spectrum;
using OpenVSA.Hal;
using OpenVSA.Measurement;
using System.IO;
using OpenVSA.Capture.Triggering;
using OpenVSA.Measurement.Markers;
using OpenVSA.Measurement.State;
using OpenVSA.Ui.HotSpots;
using OpenVSA.Ui.Rendering;

// Aliased rather than imported: this file's own base class is System.Windows.Window, and importing
// the DSP namespace would make the word ambiguous in a WPF window of all places.
using DspWindow = OpenVSA.Dsp.Windowing.Window;
using WindowType = OpenVSA.Dsp.Windowing.WindowType;

namespace OpenVSA.Ui
{
    /// <summary>
    /// Main application shell: a docking-window host, not an instrument emulator (REQ-UI-001).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The shell offers front ends without referencing any of them. <c>REQ-ARC-001</c> bars every
    /// layer from L3 upward — this one included — from a compile-time reference to an L0 transport
    /// assembly, so sources come from <see cref="FrontEndRegistry"/> at run time. That is asserted
    /// by <c>OpenVSA.Architecture.Tests</c>, so a convenient <c>using OpenVSA.Hal.Sim</c> fails
    /// the suite rather than quietly re-coupling the layers.
    /// </para>
    /// <para>
    /// Nothing here names an instrument. Every string a user sees comes from the provider's own
    /// <see cref="FrontEndProviderAttribute"/> or from <see cref="IFrontEndCapabilities"/>, which
    /// is what <c>REQ-HAL-002</c> requires and what its "code search for model names returns no
    /// matches" criterion checks.
    /// </para>
    /// </remarks>
    public partial class ShellWindow : Window
    {
        /// <summary>Centre frequency the shell starts at, in hertz.</summary>
        /// <remarks>
        /// Fixed for now, and deliberately not hidden behind a settings dialogue that does not
        /// exist yet: the point of this build is that the analysis chain can be seen running
        /// against a source. The reference level is the top of the graticule, so it is set a
        /// division above the simulator's full-scale tone rather than exactly at it.
        /// </remarks>
        private const double DefaultCenterFrequencyHz = 1e9;

        /// <summary>Span the shell starts at, in hertz.</summary>
        private const double DefaultSpanHz = 10e6;

        /// <summary>Reference level the shell starts at, in dBm.</summary>
        private const double DefaultReferenceLevelDbm = 20.0;

        private readonly FrontEndRegistry _registry;
        private readonly RenderMarshal _marshal = new RenderMarshal();
        private readonly DispatcherTimer _statusTimer;

        /// <summary>
        /// The markers on the one trace that exists so far.
        /// </summary>
        /// <remarks>
        /// Trace 'A': <c>REQ-UI-020</c> letters traces, and <c>REQ-UI-031</c>'s delta label needs
        /// the letter to decide whether to print it. One trace means the cross-trace form cannot
        /// arise yet, but the model carries the letter so that it will be right when it can.
        /// </remarks>
        private readonly MarkerSet _markers = new MarkerSet('A');

        /// <summary>The conditions annotated inside the grid (<c>REQ-UI-041</c>).</summary>
        private readonly TraceIndicators _indicators = new TraceIndicators();

        private readonly DispatcherTimer _hotSpotSettle;

        /// <summary>The user's saved presets (<c>REQ-STA-005</c>).</summary>
        private readonly PresetLibrary _presets = new PresetLibrary(PresetLibrary.DefaultDirectory);

        private IFrontEnd _activeFrontEnd;
        private SpectrumEngine _engine;
        private SpectrumFrame _frame;

        /// <summary>Creates the shell window.</summary>
        public ShellWindow()
        {
            InitializeComponent();

            _registry = FrontEndRegistry.CreateDefault();
            PopulateSourcesMenu();
            ShowDiscoveryResults();

            Plot.GraticuleColumnsChanged += (sender, e) => _marshal.Columns = Plot.GraticuleColumns;
            _marshal.Columns = Plot.GraticuleColumns;

            // REQ-UI-042: a hot spot edited on the plot is the same change as one typed into the
            // settings pane, so it goes through the pane rather than round it - one path to a
            // re-plan, and the pane keeps showing what the measurement is actually set to.
            Plot.ParameterChanged += OnPlotParameterChanged;
            Plot.DialogRequested += (sender, spot) => ValueEntryDialog.Prompt(this, spot);

            // Coalesced, because a wheel turned through a dozen notches would otherwise re-plan and
            // re-arm the instrument a dozen times - seconds of GPIB traffic for a value the user is
            // still moving.
            _hotSpotSettle = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(400.0),
            };

            _hotSpotSettle.Tick += OnHotSpotSettled;

            _indicators.Set(TraceIndicator.NoData);
            Plot.SetIndicators(_indicators);

            foreach (WindowType type in Enum.GetValues(typeof(WindowType)))
            {
                WindowBox.Items.Add(new WindowChoice(type));
            }

            WindowBox.SelectedIndex = IndexOfWindow(DspWindow.Default);

            // The measured rate and the dropped-frame count of REQ-NFR-012 are status-bar figures,
            // not per-frame ones: updating them from the frame handler would put text layout on the
            // display path sixty times a second to show a number nobody can read that fast.
            _statusTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
            {
                Interval = TimeSpan.FromSeconds(1.0),
            };
            _statusTimer.Tick += (sender, e) => ShowRunningStatistics();

            Closed += (sender, e) => ShutDown();
        }

        /// <summary>The front end currently selected, or null if none has been chosen.</summary>
        public IFrontEnd ActiveFrontEnd => _activeFrontEnd;

        /// <summary>The running measurement, or null if none is running.</summary>
        public SpectrumEngine Engine => _engine;

        private void PopulateSourcesMenu()
        {
            var sources = new MenuItem { Header = "_Signal source" };

            if (_registry.Providers.Count == 0)
            {
                sources.Items.Add(new MenuItem { Header = "None discovered", IsEnabled = false });
            }
            else
            {
                foreach (FrontEndDescriptor descriptor in _registry.Providers)
                {
                    FrontEndDescriptor captured = descriptor;
                    var item = new MenuItem { Header = descriptor.DisplayName, IsCheckable = true };
                    item.Click += (sender, e) => SelectFrontEnd(captured, (MenuItem)sender);
                    sources.Items.Add(item);
                }
            }

            HardwareMenu.Items.Add(sources);
        }

        private async void SelectFrontEnd(FrontEndDescriptor descriptor, MenuItem clicked)
        {
            IFrontEnd created;

            try
            {
                created = descriptor.Create();
            }
            catch (InvalidOperationException e)
            {
                // Reported in place rather than thrown at the dispatcher. A provider that cannot
                // be constructed must not take the application down with it.
                clicked.IsChecked = false;
                StatusText.Content = "Could not open " + descriptor.DisplayName;
                CapabilitiesText.Text = e.Message;
                return;
            }

            await StopAcquisitionAsync().ConfigureAwait(true);

            if (_activeFrontEnd != null)
            {
                _activeFrontEnd.Dispose();
            }

            _activeFrontEnd = null;
            StartItem.IsEnabled = false;
            SettingsGrid.IsEnabled = false;

            foreach (MenuItem sibling in SourceMenuItems())
            {
                sibling.IsChecked = ReferenceEquals(sibling, clicked);
            }

            // Connected here rather than at Start, because a real instrument does not know its own
            // limits until it has been asked: REQ-HAL-002 ranges every control from
            // IFrontEndCapabilities, and for a VISA front end those are queried from the
            // instrument. Connecting later would mean offering a settings pane with nothing to
            // range against, and a failure reported a step after the one that caused it.
            StatusText.Content = "Connecting to " + descriptor.DisplayName + "…";
            CapabilitiesText.Text = string.Empty;
            PlanText.Text = string.Empty;

            try
            {
                // Off the dispatcher: ConnectAsync may be synchronous inside, and a VISA session
                // that has to time out takes seconds.
                await Task.Run(() => created.ConnectAsync(CancellationToken.None))
                    .ConfigureAwait(true);
            }
            catch (Exception failure)
            {
                created.Dispose();
                clicked.IsChecked = false;
                StatusText.Content = "Could not connect";
                CapabilitiesText.Text =
                    "Could not connect to " + descriptor.DisplayName + "." + Environment.NewLine +
                    Environment.NewLine + failure.Message;
                return;
            }

            _activeFrontEnd = created;

            StatusText.Content = created.DisplayName + " connected";
            CapabilitiesText.Text = DescribeCapabilities(created);
            StartItem.IsEnabled = true;

            RangeSettingsFor(created.Capabilities);
        }

        // ---- Measurement settings --------------------------------------------------------------

        /// <summary>
        /// Ranges every settings control from the front end's declared capabilities.
        /// </summary>
        /// <param name="capabilities">The capabilities to range against.</param>
        /// <remarks>
        /// <c>REQ-HAL-002</c>, and its acceptance criterion's second half: switching front ends
        /// visibly re-ranges the affected controls. Every limit shown here is read from the
        /// interface — there is no table of models, and a front end that declares a 1 kHz maximum
        /// span gets a 1 kHz maximum span in the UI without anything here being told about it.
        /// </remarks>
        private void RangeSettingsFor(IFrontEndCapabilities capabilities)
        {
            if (capabilities == null)
            {
                SettingsGrid.IsEnabled = false;
                return;
            }

            CentreRange.Text = "Range " + EngineeringText.Frequency(capabilities.CenterFrequencyRange.MinHz) +
                " to " + EngineeringText.Frequency(capabilities.CenterFrequencyRange.MaxHz);
            SpanRange.Text = "Range " + EngineeringText.Frequency(capabilities.MinSpanHz) +
                " to " + EngineeringText.Frequency(capabilities.MaxSpanHz);
            ReferenceLevelRange.Text = "Range " +
                capabilities.ReferenceLevelRange.MinDbm.ToString("0.##", CultureInfo.CurrentCulture) +
                " to " +
                capabilities.ReferenceLevelRange.MaxDbm.ToString("0.##", CultureInfo.CurrentCulture) +
                " dBm";

            double centre = Clamp(DefaultCenterFrequencyHz, capabilities.CenterFrequencyRange.MinHz, capabilities.CenterFrequencyRange.MaxHz);
            double span = Clamp(DefaultSpanHz, capabilities.MinSpanHz, capabilities.MaxSpanHz);
            double level = Clamp(DefaultReferenceLevelDbm, capabilities.ReferenceLevelRange.MinDbm, capabilities.ReferenceLevelRange.MaxDbm);

            CentreBox.Text = EngineeringText.Frequency(centre);
            SpanBox.Text = EngineeringText.Frequency(span);
            ReferenceLevelBox.Text = level.ToString("0.##", CultureInfo.CurrentCulture);
            ResolutionBandwidthBox.Text = EngineeringText.Frequency(span / 100.0);

            PopulatePointsChoices(capabilities);
            PopulateTriggerChoices(capabilities);
            OfferAutoRange(capabilities);

            SettingsGrid.IsEnabled = true;
            SettingsMessage.Text = string.Empty;
        }

        /// <summary>
        /// Enables the auto-range command only where the front end can be ranged
        /// (<c>REQ-ACQ-004</c>).
        /// </summary>
        /// <param name="capabilities">The capabilities to read the answer from.</param>
        /// <remarks>
        /// The tooltip is set on the disabled button and told to show there, as the trigger list
        /// does: an explanation nobody can read is not an explanation.
        /// </remarks>
        private void OfferAutoRange(IFrontEndCapabilities capabilities)
        {
            AutoRangeAvailability availability = AutoRangeAvailability.For(capabilities);

            AutoRangeButton.IsEnabled = availability.IsAvailable;
            AutoRangeButton.ToolTip = availability.IsAvailable
                ? "Set the reference level from the measured peak, leaving " +
                  HeadroomBand.Default + " of headroom."
                : availability.Explanation;

            ToolTipService.SetShowOnDisabled(AutoRangeButton, true);
        }

        /// <summary>
        /// Fills the points list with the counts this front end can actually capture.
        /// </summary>
        /// <remarks>
        /// The list is the instrument's, not the specification's: a front end that can only return
        /// a short block simply does not offer the larger counts, so an unachievable setting cannot
        /// be selected in the first place. Auto heads the list because deriving the count from a
        /// resolution bandwidth is the way a spectrum measurement is usually expressed.
        /// </remarks>
        private void PopulatePointsChoices(IFrontEndCapabilities capabilities)
        {
            int available = AcquisitionPlanner.MaximumPointsFor(capabilities, AnalysisPath.ComplexZoom);

            PointsBox.Items.Clear();
            PointsBox.Items.Add(PointsChoice.Auto);

            foreach (int count in FrequencyPoints.Supported)
            {
                if (count > available)
                {
                    break;
                }

                PointsBox.Items.Add(new PointsChoice(count));
            }

            PointsRange.Text = available == 0
                ? "This front end cannot capture enough samples for a spectrum."
                : "Up to " + available.ToString(CultureInfo.CurrentCulture) +
                  ", from this front end's capture depth";

            PointsBox.SelectedItem = FindPointsChoice(AcquisitionPlanner.DefaultFrequencyPoints)
                ?? (PointsBox.Items.Count > 1 ? PointsBox.Items[PointsBox.Items.Count - 1] : PointsBox.Items[0]);
        }

        private object FindPointsChoice(int points)
        {
            foreach (object item in PointsBox.Items)
            {
                var choice = item as PointsChoice;
                if (choice != null && choice.Points == points)
                {
                    return choice;
                }
            }

            return null;
        }

        /// <summary>
        /// Fills the trigger list from what the front end declares (<c>REQ-TRG-001</c>).
        /// </summary>
        /// <remarks>
        /// Every style appears; the ones this source cannot do are disabled and carry the
        /// explanation as their tooltip. Omitting them instead would leave a user hunting for a
        /// frequency-mask trigger the software does have and this instrument cannot serve.
        /// </remarks>
        private void PopulateTriggerChoices(IFrontEndCapabilities capabilities)
        {
            TriggerBox.Items.Clear();

            foreach (TriggerOption option in TriggerAvailability.For(capabilities))
            {
                var item = new ComboBoxItem
                {
                    Content = option.DisplayName,
                    Tag = option,
                    IsEnabled = option.IsAvailable,
                    ToolTip = option.IsAvailable ? null : option.Explanation,
                };

                // A disabled item does not raise its own tooltip in a closed combo box, so the
                // service is told to show it anyway - otherwise the explanation the requirement
                // asks for is written down and never seen.
                ToolTipService.SetShowOnDisabled(item, true);

                TriggerBox.Items.Add(item);

                if (option.IsAvailable && TriggerBox.SelectedItem == null)
                {
                    TriggerBox.SelectedItem = item;
                }
            }
        }

        /// <summary>The trigger style selected, or Free Run if none is.</summary>
        public TriggerStyle SelectedTriggerStyle()
        {
            var item = TriggerBox.SelectedItem as ComboBoxItem;
            var option = item == null ? null : item.Tag as TriggerOption;

            return option == null ? TriggerStyle.Immediate : option.Style;
        }

        private void OnTriggerSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            TriggerStyle style = SelectedTriggerStyle();

            TriggerNote.Text = style == TriggerStyle.Immediate
                ? "Acquisition starts as soon as it is armed."
                : TriggerAvailability.NameOf(style) + " trigger.";

            Plot.TriggerChannelHotSpot.Value.TrySet(
                style == TriggerStyle.Immediate ? "Free Run" : "Ch 1");
            Plot.TriggerChannelHotSpot.Refresh();
        }

        private void OnPointsSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            bool automatic = SelectedPoints() == 0;

            ResolutionBandwidthBox.IsEnabled = automatic;
            ResolutionBandwidthNote.Text = automatic
                ? "The point count is derived from this and the window (REQ-DSP-022 Auto)."
                : "Set by the point count, the span and the window.";
        }

        private void OnSettingKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                OnApplySettings(sender, new RoutedEventArgs());
            }
        }

        private async void OnApplySettings(object sender, RoutedEventArgs e)
        {
            if (_activeFrontEnd == null)
            {
                return;
            }

            await StartMeasurementAsync().ConfigureAwait(true);
        }

        /// <summary>
        /// Sets the reference level from the measured peak (<c>REQ-ACQ-004</c>).
        /// </summary>
        /// <remarks>
        /// <para>
        /// The decision is <see cref="AutoRange"/>'s; what the shell adds is the three things only
        /// it can do — find the peak on the trace that is actually on screen, put the new level
        /// through the same apply path a typed one takes, and raise the <c>RNG</c> indicator of
        /// <c>REQ-UI-007</c> while it does.
        /// </para>
        /// <para>
        /// The indicator is set before the restart and cleared by the first frame that arrives
        /// after it, which is exactly the interval during which the range is being adjusted. A
        /// flag that were cleared here instead would be raised and lowered within one dispatcher
        /// turn and never drawn.
        /// </para>
        /// </remarks>
        private async void OnAutoRange(object sender, RoutedEventArgs e)
        {
            if (_activeFrontEnd == null || _activeFrontEnd.Capabilities == null)
            {
                return;
            }

            SpectrumFrame frame = _frame;

            if (frame == null)
            {
                // Auto-ranging is a decision about a signal, and there is no signal yet. Said
                // plainly rather than by doing nothing, which would look like a broken button.
                SettingsMessage.Text =
                    "Auto-range needs a measurement to range against. Start the acquisition first.";
                return;
            }

            double level;

            if (!EngineeringText.TryParseDecibels(ReferenceLevelBox.Text, out level))
            {
                SettingsMessage.Text =
                    "Reference level: '" + ReferenceLevelBox.Text + "' is not a level in dBm.";
                return;
            }

            // The trace's own maximum, not a marker: auto-ranging is not a marker operation, and
            // placing one to read a number would leave the user with a marker they did not ask for.
            int highest = PeakSearch.Highest(frame);

            if (highest < 0)
            {
                SettingsMessage.Text =
                    "Auto-range found no peak on the trace to range against.";
                return;
            }

            double peakDbm = frame.LevelsDbm[highest];
            AutoRangeResult decision;

            try
            {
                decision = AutoRange.Adjust(_activeFrontEnd.Capabilities, level, peakDbm);
            }
            catch (InvalidOperationException refused)
            {
                // The backstop for a source that cannot range. The button is disabled for one, so
                // this is reachable only through a keyboard mnemonic on a stale enable state.
                SettingsMessage.Text = refused.Message;
                return;
            }

            SettingsMessage.Text = decision.Message;

            if (!decision.Changed)
            {
                return;
            }

            _indicators.Set(TraceIndicator.Range);
            Plot.SetIndicators(_indicators);

            ReferenceLevelBox.Text =
                decision.ReferenceLevelDbm.ToString("0.##", CultureInfo.CurrentCulture);

            await StartMeasurementAsync().ConfigureAwait(true);

            // Restated: the restart overwrites it with the res BW line, and what the user just did
            // is the more interesting of the two.
            SettingsMessage.Text = decision.Message;
        }

        private int SelectedPoints()
        {
            var choice = PointsBox.SelectedItem as PointsChoice;
            return choice == null ? AcquisitionPlanner.DefaultFrequencyPoints : choice.Points;
        }

        private WindowType SelectedWindow()
        {
            var choice = WindowBox.SelectedItem as WindowChoice;
            return choice == null ? DspWindow.Default : choice.Type;
        }

        private int IndexOfWindow(WindowType type)
        {
            for (int i = 0; i < WindowBox.Items.Count; i++)
            {
                var choice = WindowBox.Items[i] as WindowChoice;
                if (choice != null && choice.Type == type)
                {
                    return i;
                }
            }

            return 0;
        }

        private static double Clamp(double value, double minimum, double maximum)
        {
            if (value < minimum)
            {
                return minimum;
            }

            return value > maximum ? maximum : value;
        }

        /// <summary>A selectable point count; <see cref="Points"/> of 0 means Auto.</summary>
        private sealed class PointsChoice
        {
            public static readonly PointsChoice Auto = new PointsChoice(0);

            public PointsChoice(int points)
            {
                Points = points;
            }

            public int Points { get; }

            public override string ToString() =>
                Points == 0 ? "Auto (from Res BW)" : Points.ToString(CultureInfo.CurrentCulture);
        }

        /// <summary>A selectable window, named as the specification names it.</summary>
        private sealed class WindowChoice
        {
            public WindowChoice(WindowType type)
            {
                Type = type;
            }

            public WindowType Type { get; }

            public override string ToString() => WindowText.Describe(Type);
        }

        private IEnumerable<MenuItem> SourceMenuItems() =>
            HardwareMenu.Items.OfType<MenuItem>()
                .SelectMany(m => m.Items.OfType<MenuItem>());

        /// <summary>
        /// Renders a front end's declared capabilities.
        /// </summary>
        /// <param name="frontEnd">The front end.</param>
        /// <remarks>
        /// Every figure comes from <see cref="IFrontEndCapabilities"/>. This is the shape every
        /// control that ranges itself will take under <c>REQ-HAL-002</c>: ask the capabilities,
        /// never the model.
        /// </remarks>
        private static string DescribeCapabilities(IFrontEnd frontEnd)
        {
            IFrontEndCapabilities capabilities = frontEnd.Capabilities;
            if (capabilities == null)
            {
                return "This front end declares no capabilities.";
            }

            var text = new StringBuilder();
            text.AppendLine("Capabilities, as the front end declares them:");
            text.AppendLine();
            Append(text, "Centre frequency", capabilities.CenterFrequencyRange.ToString());
            Append(text, "Span", Hz(capabilities.MinSpanHz) + " to " + Hz(capabilities.MaxSpanHz));
            Append(text, "Maximum sample rate", Hz(capabilities.MaxSampleRateHz));
            Append(text, "Reference level", capabilities.ReferenceLevelRange.ToString());
            Append(text, "Block size", capabilities.MaxSamplesPerBlock.ToString(CultureInfo.InvariantCulture) + " samples");
            Append(text, "Deepest capture", capabilities.MaxCaptureSamples.ToString(CultureInfo.InvariantCulture) + " samples");
            Append(text, "Channels", capabilities.ChannelCount.ToString(CultureInfo.InvariantCulture) +
                (capabilities.SupportsPhaseCoherentChannels ? " (phase coherent)" : string.Empty));
            Append(text, "Baseband I/Q", capabilities.SupportsBasebandIq ? "yes" : "no");
            Append(text, "External reference", capabilities.SupportsExternalRef ? "yes" : "no");
            Append(text, "Triggers", string.Join(", ", capabilities.TriggerStyles));

            return text.ToString();
        }

        private static void Append(StringBuilder text, string label, string value)
        {
            text.AppendLine("  " + label.PadRight(22) + value);
        }

        private static string Hz(double hertz) => PlanSummary.Frequency(hertz);

        private void ShowDiscoveryResults()
        {
            DiscoveryHeading.Text = _registry.Providers.Count == 1
                ? "1 signal source discovered"
                : _registry.Providers.Count + " signal sources discovered";

            ProviderList.ItemsSource = _registry.Providers
                .Select(p => "  " + p.DisplayName + "  —  " + p.AssemblyName)
                .ToArray();

            // Failures are shown, not swallowed. On a machine with no VISA runtime the transport
            // assembly is present and its types will not load; REQ-NFR-032 requires the
            // application to start anyway, and an operator who can see why an option is missing
            // can act on it.
            FailureHeading.Text = _registry.Failures.Count == 0
                ? string.Empty
                : "Unavailable:";

            FailureList.ItemsSource = _registry.Failures
                .Select(f => "  " + f.Source + " — " + f.Reason)
                .ToArray();

            DocumentPlaceholder.Text = _registry.Providers.Count == 0
                ? "No signal source was discovered, so there is nothing to measure. " +
                  SyncfusionLicense.StatusMessage
                : "Choose a source under Hardware, then Acquisition → Start.\n\n" +
                  SyncfusionLicense.StatusMessage;

            StatusText.Content = _registry.Providers.Count == 0
                ? "No signal source available"
                : "Ready";
        }

        // ---- Acquisition ----------------------------------------------------------------------

        /// <summary>
        /// Starts a measurement against the selected front end.
        /// </summary>
        /// <remarks>
        /// The whole of the analysis chain runs behind this one call: the engine negotiates a plan,
        /// pumps blocks on a pool thread, computes each one's spectrum there, and the render marshal
        /// reduces it to a pixel-column envelope before anything reaches the dispatcher. What the
        /// UI thread does per frame is a <c>WritePixels</c> and six strings.
        /// </remarks>
        private async void OnStart(object sender, RoutedEventArgs e)
        {
            await StartMeasurementAsync().ConfigureAwait(true);
        }

        /// <summary>
        /// Starts, or restarts, the measurement from the current settings.
        /// </summary>
        /// <remarks>
        /// Applying a setting is a restart rather than a live edit. Span, points and window all
        /// change the shape of the acquisition, so continuing a run across the change would mean
        /// the display briefly showing frames computed under two different setups — which is
        /// exactly the kind of thing nobody notices until a measurement is being trusted.
        /// </remarks>
        private async Task StartMeasurementAsync()
        {
            if (_activeFrontEnd == null)
            {
                return;
            }

            await StopAcquisitionAsync().ConfigureAwait(true);

            // How many points this measurement can have is the instrument's answer, not the
            // shell's: the planner reads the capture depth from the capabilities and reduces the
            // count to fit (REQ-ACQ-001, REQ-DSP-022, REQ-HAL-002).
            PlannedAcquisition planned = BuildPlan();

            if (planned == null)
            {
                return;
            }

            var engine = new SpectrumEngine(
                _activeFrontEnd, new SpectrumComputer(planned.Window, null, null));
            engine.FrameComputed += OnFrameComputed;
            engine.Faulted += OnEngineFaulted;
            engine.Completed += OnEngineCompleted;

            AcquisitionPlan plan;

            try
            {
                plan = await engine.StartAsync(planned.Request, CancellationToken.None)
                    .ConfigureAwait(true);
            }
            catch (Exception failure)
            {
                engine.Dispose();
                StatusText.Content = "Could not start";
                PlanText.Text = failure.Message;
                return;
            }

            _engine = engine;

            DocumentPlaceholder.Visibility = Visibility.Collapsed;
            StartItem.IsEnabled = false;
            StopItem.IsEnabled = true;
            StatusText.Content = "Measuring";

            SettingsMessage.Text = planned.Coerced || plan.Coerced
                ? "Some settings were coerced — see the negotiated plan."
                : "Res BW " + EngineeringText.Frequency(planned.ResolutionBandwidthHz) +
                  ", time record " + EngineeringText.Time(planned.MaxTimeSeconds);
            PlanText.Text = PlanSummary.Describe(plan, planned, _activeFrontEnd.Capabilities);
            _statusTimer.Start();
        }

        /// <summary>
        /// Reads the settings controls and plans an acquisition, or reports why it cannot.
        /// </summary>
        /// <returns>The planned acquisition, or <c>null</c> if a setting was rejected.</returns>
        /// <remarks>
        /// Every bound checked here comes from the active front end's capabilities, and a rejected
        /// value is reported with the bound it violated rather than silently clamped —
        /// <c>REQ-HAL-001</c>'s prohibition applied at the point of entry, where the user still has
        /// the number in mind.
        /// </remarks>
        private PlannedAcquisition BuildPlan()
        {
            IFrontEndCapabilities capabilities = _activeFrontEnd.Capabilities;

            if (capabilities == null)
            {
                // A front end that declares nothing cannot have its settings ranged, and guessing
                // limits for it is exactly what REQ-HAL-002 forbids.
                return Reject(
                    "This source has not declared its capabilities, so its settings cannot be " +
                    "ranged. Re-select it under Hardware to connect again.");
            }

            double centre;
            if (!EngineeringText.TryParseFrequency(CentreBox.Text, out centre))
            {
                return Reject("Centre frequency: '" + CentreBox.Text + "' is not a frequency.");
            }

            if (!capabilities.CenterFrequencyRange.Contains(centre))
            {
                return Reject(
                    "Centre frequency is outside this front end's range of " +
                    EngineeringText.Frequency(capabilities.CenterFrequencyRange.MinHz) + " to " +
                    EngineeringText.Frequency(capabilities.CenterFrequencyRange.MaxHz) + ".");
            }

            double span;
            if (!EngineeringText.TryParseFrequency(SpanBox.Text, out span))
            {
                return Reject("Span: '" + SpanBox.Text + "' is not a frequency.");
            }

            if (span < capabilities.MinSpanHz || span > capabilities.MaxSpanHz)
            {
                return Reject(
                    "Span is outside this front end's range of " +
                    EngineeringText.Frequency(capabilities.MinSpanHz) + " to " +
                    EngineeringText.Frequency(capabilities.MaxSpanHz) + ".");
            }

            double level;
            if (!EngineeringText.TryParseDecibels(ReferenceLevelBox.Text, out level))
            {
                return Reject("Reference level: '" + ReferenceLevelBox.Text + "' is not a level in dBm.");
            }

            if (!capabilities.ReferenceLevelRange.Contains(level))
            {
                return Reject(
                    "Reference level is outside this front end's range of " +
                    capabilities.ReferenceLevelRange.MinDbm.ToString("0.##", CultureInfo.CurrentCulture) +
                    " to " +
                    capabilities.ReferenceLevelRange.MaxDbm.ToString("0.##", CultureInfo.CurrentCulture) +
                    " dBm.");
            }

            int points = SelectedPoints();
            WindowType window = SelectedWindow();

            try
            {
                if (points == 0)
                {
                    double resolutionBandwidth;
                    if (!EngineeringText.TryParseFrequency(ResolutionBandwidthBox.Text, out resolutionBandwidth) ||
                        resolutionBandwidth <= 0.0)
                    {
                        return Reject(
                            "Res BW: '" + ResolutionBandwidthBox.Text + "' is not a positive bandwidth.");
                    }

                    return AcquisitionPlanner.PlanForResolutionBandwidth(
                        capabilities, centre, span, resolutionBandwidth, level,
                        AnalysisPath.ComplexZoom, window);
                }

                return AcquisitionPlanner.Plan(
                    capabilities, centre, span, points, level, AnalysisPath.ComplexZoom, window);
            }
            catch (ArgumentException failure)
            {
                return Reject(failure.Message);
            }
        }

        private PlannedAcquisition Reject(string reason)
        {
            SettingsMessage.Text = reason;
            StatusText.Content = "Setting rejected";
            return null;
        }

        private async void OnStop(object sender, RoutedEventArgs e)
        {
            await StopAcquisitionAsync().ConfigureAwait(true);
            StatusText.Content = "Stopped";
        }

        /// <summary>
        /// Receives a frame on the pump thread, reduces it, and posts a draw.
        /// </summary>
        /// <remarks>
        /// <strong>This runs off the UI thread and must stay that way.</strong> The decimation
        /// inside <see cref="RenderMarshal.Offer"/> is the one stage whose cost is proportional to
        /// the point count, so doing it here rather than in the draw callback is what keeps a
        /// 2²⁰-point trace off the dispatcher (<c>REQ-NFR-010</c>, <c>REQ-NFR-021</c>).
        /// </remarks>
        private void OnFrameComputed(object sender, SpectrumFrame frame)
        {
            if (_marshal.Offer(frame))
            {
                Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(DrawPending));
            }
        }

        private void DrawPending()
        {
            TraceSnapshot snapshot = _marshal.TakeForRender();

            if (snapshot == null)
            {
                return;
            }

            _frame = snapshot.Spectrum;

            if (Plot.Show(snapshot))
            {
                RefreshMarkers();
            }

            UpdateIndicators(snapshot);
        }

        /// <summary>
        /// Refreshes the conditions annotated inside the grid (<c>REQ-UI-041</c>).
        /// </summary>
        /// <remarks>
        /// Only the two this stage can know about. The rest belong to conditions detected further
        /// down — carrier lock, sync search, equalisation — and are set from there when those
        /// exist; nothing here fabricates a state it cannot observe.
        /// </remarks>
        private void UpdateIndicators(TraceSnapshot snapshot)
        {
            _indicators.Clear(TraceIndicator.NoData);

            // "RNG": raised when auto-ranging moved the level, and cleared here because a frame
            // measured on the new range is the moment the adjustment is over (REQ-ACQ-004,
            // REQ-UI-007).
            _indicators.Clear(TraceIndicator.Range);

            // "ALL POINTS": every acquired point has a column of its own, so nothing is being
            // enveloped away and what is on screen is the measurement rather than a reduction of it.
            _indicators.SetActive(
                TraceIndicator.AllPoints, snapshot.Columns >= snapshot.Spectrum.PointCount);

            Plot.SetIndicators(_indicators);
        }

        // ---- State, presets and their exclusions ------------------------------------------------

        /// <summary>The context this shell's one measurement belongs to (<c>REQ-STA-004</c>).</summary>
        private const string ContextName = "Measurement 1";

        /// <summary>
        /// The settings pane and plot, expressed as a saveable state (<c>REQ-STA-001</c>).
        /// </summary>
        /// <remarks>
        /// Read from the controls rather than from a parallel model, so what is saved is what is on
        /// screen. A second copy of the settings kept alongside the pane would be one more thing to
        /// keep in step, and the failure would be silent: a state that saved a frequency the user
        /// had changed and not applied.
        /// </remarks>
        public ApplicationState CaptureState()
        {
            ApplicationState state = ApplicationState.Default(ContextName);
            MeasurementState measurement = state.Measurements[0];

            double parsed;

            if (EngineeringText.TryParseFrequency(CentreBox.Text, out parsed))
            {
                measurement.CenterFrequencyHz = parsed;
            }

            if (EngineeringText.TryParseFrequency(SpanBox.Text, out parsed))
            {
                measurement.SpanHz = parsed;
            }

            if (EngineeringText.TryParseFrequency(ResolutionBandwidthBox.Text, out parsed))
            {
                measurement.ResolutionBandwidthHz = parsed;
            }

            if (EngineeringText.TryParseDecibels(ReferenceLevelBox.Text, out parsed))
            {
                measurement.Input.RangeDbm = parsed;
            }

            measurement.ResolutionBandwidthIsAutomatic = SelectedPoints() == 0;
            measurement.Analysis.PointsAreAutomatic = SelectedPoints() == 0;
            measurement.Analysis.FrequencyPoints =
                SelectedPoints() == 0 ? AcquisitionPlanner.DefaultFrequencyPoints : SelectedPoints();
            measurement.Analysis.Window = SelectedWindow();

            measurement.Trigger.Channel = Plot.TriggerChannelHotSpot.Value.Text;

            TraceDisplayState trace = measurement.Traces[0];
            trace.TopDbm = Plot.TopDbm;
            trace.DecibelsPerDivision = Plot.DecibelsPerDivision;

            TraceFormat format;
            if (TraceFormatText.TryParse(Plot.FormatHotSpot.Value.Text, out format))
            {
                trace.Format = format;
            }

            measurement.Markers.Clear();

            foreach (Marker marker in _markers.Markers)
            {
                measurement.Markers.Add(new MarkerState
                {
                    Number = marker.Number,
                    Trace = marker.TraceLetter.ToString(CultureInfo.InvariantCulture),
                    Type = marker.Type.ToString(),
                    XHz = marker.XHz,
                    YDbm = marker.Type == MarkerType.Fixed ? marker.FixedYDbm : 0.0,
                    DeltaReference = marker.Reference == null ? 0 : marker.Reference.Number,
                    IsSelected = marker.IsSelected,
                });
            }

            return state;
        }

        /// <summary>
        /// Applies a recalled measurement to the settings pane and the plot.
        /// </summary>
        /// <param name="measurement">The recalled settings.</param>
        /// <exception cref="ArgumentNullException"><paramref name="measurement"/> is null.</exception>
        public void ApplyState(MeasurementState measurement)
        {
            if (measurement == null)
            {
                throw new ArgumentNullException(nameof(measurement));
            }

            CentreBox.Text = EngineeringText.Frequency(measurement.CenterFrequencyHz, 6);
            SpanBox.Text = EngineeringText.Frequency(measurement.SpanHz, 6);
            ResolutionBandwidthBox.Text =
                EngineeringText.Frequency(measurement.ResolutionBandwidthHz, 6);
            ReferenceLevelBox.Text =
                measurement.Input.RangeDbm.ToString("0.##", CultureInfo.CurrentCulture) + " dBm";

            WindowBox.SelectedIndex = IndexOfWindow(measurement.Analysis.Window);

            if (measurement.Analysis.PointsAreAutomatic)
            {
                SelectAutomaticPoints();
            }
            else
            {
                object choice = FindPointsChoice(measurement.Analysis.FrequencyPoints);

                if (choice != null)
                {
                    PointsBox.SelectedItem = choice;
                }
            }

            Plot.TriggerChannelHotSpot.Value.TrySet(measurement.Trigger.Channel);
            Plot.TriggerChannelHotSpot.Refresh();

            if (measurement.Traces.Count > 0)
            {
                Plot.FormatHotSpot.Value.TrySet(
                    TraceFormatText.Describe(measurement.Traces[0].Format));
                Plot.FormatHotSpot.Refresh();
            }

            foreach (Marker existing in _markers.Markers.ToList())
            {
                _markers.Remove(existing);
            }

            // Two passes: a delta marker needs its reference to exist before it can be made, and a
            // state is free to list them in either order.
            foreach (MarkerState marker in measurement.Markers)
            {
                if (!string.Equals(marker.Type, "Delta", StringComparison.Ordinal))
                {
                    RestoreMarker(marker, null);
                }
            }

            foreach (MarkerState marker in measurement.Markers)
            {
                if (string.Equals(marker.Type, "Delta", StringComparison.Ordinal))
                {
                    RestoreMarker(
                        marker,
                        _markers.Markers.FirstOrDefault(m => m.Number == marker.DeltaReference));
                }
            }

            RefreshMarkers();
        }

        private void RestoreMarker(MarkerState marker, Marker reference)
        {
            if (string.Equals(marker.Type, "Fixed", StringComparison.Ordinal))
            {
                _markers.AddFixed(marker.XHz, marker.YDbm);
            }
            else if (string.Equals(marker.Type, "Delta", StringComparison.Ordinal) &&
                     reference != null)
            {
                _markers.AddDelta(marker.XHz, reference);
            }
            else
            {
                _markers.AddNormal(marker.XHz);
            }
        }

        private void OnSaveState(object sender, RoutedEventArgs e)
        {
            var dialog = new StateSaveDialog(SuggestedStatePath()) { Owner = this };

            if (dialog.ShowDialog() != true)
            {
                return;
            }

            try
            {
                StateFile.Save(CaptureState(), dialog.Path);
                StatusText.Content = "State saved";
            }
            catch (IOException failure)
            {
                StatusText.Content = "Could not save the state";
                SettingsMessage.Text = failure.Message;
            }
            catch (UnauthorizedAccessException failure)
            {
                StatusText.Content = "Could not save the state";
                SettingsMessage.Text = failure.Message;
            }
        }

        private void OnRecallState(object sender, RoutedEventArgs e)
        {
            var picker = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "OpenVSA state (*" + StateFile.Extension + ")|*" + StateFile.Extension,
                Title = "Recall state",
            };

            if (picker.ShowDialog(this) != true)
            {
                return;
            }

            try
            {
                Recall(StateFile.Load(picker.FileName));
                StatusText.Content = "State recalled";
            }
            catch (StateFormatException failure)
            {
                StatusText.Content = "Could not recall the state";
                SettingsMessage.Text = failure.Message;
            }
            catch (IOException failure)
            {
                StatusText.Content = "Could not recall the state";
                SettingsMessage.Text = failure.Message;
            }
        }

        /// <summary>
        /// Applies a state, or refuses it as a whole (<c>REQ-STA-004</c>).
        /// </summary>
        /// <param name="state">The state to apply.</param>
        /// <exception cref="ArgumentNullException"><paramref name="state"/> is null.</exception>
        public void Recall(ApplicationState state)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            var contexts = new Dictionary<string, MeasurementState>(StringComparer.Ordinal)
            {
                { ContextName, CaptureState().Measurements[0] },
            };

            try
            {
                StateRecall.Apply(state, contexts);
            }
            catch (ContextMismatchException mismatch)
            {
                // Reported rather than partially applied: the settings pane is untouched, because
                // nothing has been written to it yet.
                StatusText.Content = "State not recalled";
                SettingsMessage.Text = mismatch.Message;
                return;
            }

            ApplyState(contexts[ContextName]);
            SettingsMessage.Text = string.Empty;
        }

        private void OnFactoryPreset(object sender, RoutedEventArgs e)
        {
            // REQ-UI-061: the hardware setup is left alone, which is structural - a state carries
            // no front end, so applying one cannot disturb the connection.
            Recall(Presets.Factory(ContextName));
            StatusText.Content = "Factory preset";
        }

        private void OnSavePreset(object sender, RoutedEventArgs e)
        {
            var dialog = new StateSaveDialog("My preset") { Owner = this, Title = "Save as preset" };

            if (dialog.ShowDialog() != true)
            {
                return;
            }

            try
            {
                _presets.Save(dialog.Path, CaptureState());
                StatusText.Content = "Preset '" + dialog.Path + "' saved";
            }
            catch (ArgumentException failure)
            {
                StatusText.Content = "Could not save the preset";
                SettingsMessage.Text = failure.Message;
            }
            catch (IOException failure)
            {
                StatusText.Content = "Could not save the preset";
                SettingsMessage.Text = failure.Message;
            }
        }

        /// <summary>
        /// Rebuilds the preset menu when it opens.
        /// </summary>
        /// <remarks>
        /// On open rather than at start-up, so a preset saved this session appears without a
        /// restart — and so one deleted outside the application stops appearing.
        /// </remarks>
        private void OnPresetMenuOpened(object sender, RoutedEventArgs e)
        {
            while (PresetMenu.Items.Count > 1)
            {
                PresetMenu.Items.RemoveAt(1);
            }

            IReadOnlyList<string> names;

            try
            {
                names = _presets.Names;
            }
            catch (IOException)
            {
                return;
            }

            if (names.Count == 0)
            {
                return;
            }

            PresetMenu.Items.Add(new Separator());

            foreach (string name in names)
            {
                string captured = name;
                var item = new MenuItem { Header = name };
                item.Click += (s, args) => ApplyPreset(captured);
                PresetMenu.Items.Add(item);
            }
        }

        private void ApplyPreset(string name)
        {
            try
            {
                Recall(_presets.Load(name));
                StatusText.Content = "Preset '" + name + "'";
            }
            catch (StateFormatException failure)
            {
                StatusText.Content = "Could not apply the preset";
                SettingsMessage.Text = failure.Message;
            }
            catch (IOException failure)
            {
                StatusText.Content = "Could not apply the preset";
                SettingsMessage.Text = failure.Message;
            }
        }

        private static string SuggestedStatePath() =>
            System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "OpenVSA setup" + StateFile.Extension);

        // ---- Hot spots -------------------------------------------------------------------------

        /// <summary>
        /// Applies a hot spot edited on the plot (<c>REQ-UI-042</c>).
        /// </summary>
        /// <remarks>
        /// The value is written into the settings pane and the pane's own apply is run, so the two
        /// cannot disagree about what the measurement is set to — which is the failure mode of
        /// letting the plot talk to the planner directly.
        /// </remarks>
        private void OnPlotParameterChanged(object sender, HotSpot spot)
        {
            if (ReferenceEquals(spot, Plot.CenterFrequencyHotSpot))
            {
                CentreBox.Text = EngineeringText.Frequency(NumberBehind(spot), 6);
            }
            else if (ReferenceEquals(spot, Plot.ResolutionBandwidthHotSpot))
            {
                ResolutionBandwidthBox.Text = EngineeringText.Frequency(NumberBehind(spot), 6);

                // The bandwidth is only a setting when the point count is automatic; otherwise it
                // is derived, and typing into it would be typing into a readout.
                SelectAutomaticPoints();
            }
            else if (ReferenceEquals(spot, Plot.MainTimeHotSpot))
            {
                // REQ-ACQ-001 makes main time (N_f - 1) / Span, so setting the time sets the span.
                double seconds = NumberBehind(spot);

                if (_frame == null || _frame.PointCount < 2 || !(seconds > 0.0))
                {
                    return;
                }

                SpanBox.Text = EngineeringText.Frequency((_frame.PointCount - 1) / seconds, 6);
            }
            else
            {
                // Trace format and trigger channel have nowhere to go yet: one trace, one format.
                return;
            }

            _hotSpotSettle.Stop();
            _hotSpotSettle.Start();
        }

        private void OnHotSpotSettled(object sender, EventArgs e)
        {
            _hotSpotSettle.Stop();
            OnApplySettings(this, new RoutedEventArgs());
        }

        private static double NumberBehind(HotSpot spot)
        {
            var numeric = spot.Value as NumericHotSpotValue;
            return numeric == null ? 0.0 : numeric.Value;
        }

        private void SelectAutomaticPoints()
        {
            foreach (object item in PointsBox.Items)
            {
                var choice = item as PointsChoice;

                if (choice != null && choice.Points == 0)
                {
                    PointsBox.SelectedItem = choice;
                    return;
                }
            }
        }

        // ---- Markers ---------------------------------------------------------------------------

        /// <summary>
        /// Rebuilds the plot's marker overlay and readout from the marker set.
        /// </summary>
        /// <remarks>
        /// Readings are taken here, on the UI thread, against the frame just drawn — a marker reads
        /// what is on screen, so taking the reading anywhere else would let the two disagree by a
        /// frame. It is a handful of array lookups per marker, not work proportional to the trace.
        /// </remarks>
        private void RefreshMarkers()
        {
            var primitives = new List<PlotMarker>(_markers.Markers.Count);
            string readout = string.Empty;

            foreach (Marker marker in _markers.Markers)
            {
                MarkerReading reading = marker.Read(_frame);
                int index = marker.IndexIn(_frame);

                if (index >= 0)
                {
                    // A delta marker's readout is a difference, but its glyph belongs at its own
                    // position and level - not at the difference, which is not a place on the plot.
                    double level = marker.Type == MarkerType.Fixed
                        ? marker.FixedYDbm
                        : _frame.LevelsDbm[index];

                    primitives.Add(new PlotMarker(
                        index, level, marker.Type == MarkerType.Fixed, marker.IsSelected));
                }

                if (marker.IsSelected)
                {
                    readout = DescribeMarker(marker, reading);
                }
            }

            Plot.SetMarkers(primitives, readout);
        }

        /// <summary>The active-marker readout, as <c>REQ-UI-031</c> labels it.</summary>
        private static string DescribeMarker(Marker marker, MarkerReading reading)
        {
            if (!reading.IsValid)
            {
                // REQ-UI-032's convention for a readout that has no value.
                return marker.WindowLabel + "   NAN";
            }

            string level = reading.YDbm.ToString("+0.00;-0.00;0.00", CultureInfo.CurrentCulture) +
                (marker.Type == MarkerType.Delta ? " dB" : " dBm");

            // Two lines: the readout shares the upper band with the trace format and resolution
            // bandwidth, and on one line it is wide enough to collide with them.
            return marker.WindowLabel + "  " + EngineeringText.Frequency(reading.XHz, 6) +
                Environment.NewLine + level;
        }

        private void OnPlotClicked(object sender, MouseButtonEventArgs e)
        {
            int index = Plot.PointAt(e.GetPosition(Plot));

            if (index < 0 || _frame == null)
            {
                return;
            }

            PlaceMarker(() => _markers.AddNormal(_frame.FrequencyAt(index)));
        }

        private void OnAddMarker(object sender, RoutedEventArgs e) =>
            PlaceMarker(() =>
            {
                Marker marker = _markers.AddNormal(PeakFrequency());
                return marker;
            });

        private void OnAddFixed(object sender, RoutedEventArgs e) =>
            PlaceMarker(() =>
            {
                int peak = _frame == null ? -1 : _frame.IndexOfPeak();
                return peak < 0
                    ? null
                    : _markers.AddFixed(_frame.FrequencyAt(peak), _frame.LevelsDbm[peak]);
            });

        private void OnAddDelta(object sender, RoutedEventArgs e) =>
            PlaceMarker(() =>
            {
                Marker reference = _markers.Selected;

                if (reference == null)
                {
                    SettingsMessage.Text = "Select a marker first: a delta marker measures from one.";
                    return null;
                }

                return _markers.AddDelta(PeakFrequency(), reference);
            });

        private void OnPeakSearch(object sender, RoutedEventArgs e) =>
            PlaceMarker(() => _markers.PeakSearch(_frame));

        private void OnNextPeak(object sender, RoutedEventArgs e) =>
            PlaceMarker(() => _markers.NextPeak(_frame));

        private void OnMinimumSearch(object sender, RoutedEventArgs e) =>
            PlaceMarker(() => _markers.MinimumSearch(_frame));

        private void OnDeleteMarker(object sender, RoutedEventArgs e) =>
            PlaceMarker(() =>
            {
                Marker selected = _markers.Selected;

                if (selected != null)
                {
                    _markers.Remove(selected);
                }

                return null;
            });

        private void OnDeleteAllMarkers(object sender, RoutedEventArgs e) =>
            PlaceMarker(() =>
            {
                // Backwards, so a delta marker is removed before the marker it references and the
                // reference-integrity check never has to refuse.
                for (int i = _markers.Markers.Count - 1; i >= 0; i--)
                {
                    _markers.Remove(_markers.Markers[i]);
                }

                return null;
            });

        /// <summary>
        /// Runs a marker operation, reporting the named errors the marker model raises.
        /// </summary>
        /// <remarks>
        /// <c>REQ-MKR-001</c> and <c>REQ-MKR-002</c> both require refusals to be named rather than
        /// silent — the twenty-first marker, and deleting a marker another one measures from. Both
        /// arrive here as messages the user can act on.
        /// </remarks>
        private void PlaceMarker(Func<Marker> operation)
        {
            try
            {
                operation();
                SettingsMessage.Text = string.Empty;
            }
            catch (InvalidOperationException refusal)
            {
                SettingsMessage.Text = refusal.Message;
            }
            catch (ArgumentException refusal)
            {
                SettingsMessage.Text = refusal.Message;
            }

            RefreshMarkers();
        }

        private double PeakFrequency()
        {
            if (_frame == null)
            {
                return 0.0;
            }

            int peak = _frame.IndexOfPeak();
            return peak < 0 ? _frame.CenterFrequencyHz : _frame.FrequencyAt(peak);
        }

        private void OnEngineFaulted(object sender, Exception failure)
        {
            Dispatcher.BeginInvoke(new Action(async () =>
            {
                await StopAcquisitionAsync().ConfigureAwait(true);
                StatusText.Content = "Acquisition failed";
                PlanText.Text = failure.Message;
            }));
        }

        private void OnEngineCompleted(object sender, EventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(async () =>
            {
                await StopAcquisitionAsync().ConfigureAwait(true);
                StatusText.Content = "Source exhausted";
            }));
        }

        private async System.Threading.Tasks.Task StopAcquisitionAsync()
        {
            SpectrumEngine engine = _engine;

            if (engine == null)
            {
                return;
            }

            _engine = null;
            _statusTimer.Stop();

            engine.FrameComputed -= OnFrameComputed;
            engine.Faulted -= OnEngineFaulted;
            engine.Completed -= OnEngineCompleted;

            await engine.StopAsync().ConfigureAwait(true);
            engine.Dispose();

            _marshal.Reset();
            ShowRunningStatistics();

            StartItem.IsEnabled = _activeFrontEnd != null;
            StopItem.IsEnabled = false;
        }

        private void ShowRunningStatistics()
        {
            SpectrumEngine engine = _engine;

            RateText.Content = engine == null
                ? string.Empty
                : engine.MeasuredUpdatesPerSecond.ToString("0.0", CultureInfo.CurrentCulture) +
                  " updates/s";

            // REQ-NFR-012: the dropped-frame count is displayed, not merely counted.
            DroppedText.Content = _marshal.FramesDropped == 0
                ? string.Empty
                : _marshal.FramesDropped.ToString(CultureInfo.CurrentCulture) + " frames dropped";
        }

        private void ShutDown()
        {
            SpectrumEngine engine = _engine;
            _engine = null;

            if (engine != null)
            {
                // Not awaited: the window is gone and there is nothing left to marshal back to.
                // Dispose cancels the pump, and the front end is disposed below either way.
                engine.FrameComputed -= OnFrameComputed;
                engine.Dispose();
            }

            if (_activeFrontEnd != null)
            {
                _activeFrontEnd.Dispose();
                _activeFrontEnd = null;
            }
        }
    }
}
