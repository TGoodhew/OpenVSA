using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using OpenVSA.Dsp.Spectrum;
using OpenVSA.Hal;
using OpenVSA.Measurement;
using OpenVSA.Ui.Rendering;

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

        private IFrontEnd _activeFrontEnd;
        private SpectrumEngine _engine;

        /// <summary>Creates the shell window.</summary>
        public ShellWindow()
        {
            InitializeComponent();

            _registry = FrontEndRegistry.CreateDefault();
            PopulateSourcesMenu();
            ShowDiscoveryResults();

            Plot.GraticuleColumnsChanged += (sender, e) => _marshal.Columns = Plot.GraticuleColumns;
            _marshal.Columns = Plot.GraticuleColumns;

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

            _activeFrontEnd = created;

            foreach (MenuItem sibling in SourceMenuItems())
            {
                sibling.IsChecked = ReferenceEquals(sibling, clicked);
            }

            StatusText.Content = descriptor.DisplayName + " selected";
            CapabilitiesText.Text = DescribeCapabilities(created);
            PlanText.Text = string.Empty;
            StartItem.IsEnabled = true;
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
            if (_activeFrontEnd == null || _engine != null)
            {
                return;
            }

            // How many points this measurement can have is the instrument's answer, not the
            // shell's: the planner reads the capture depth from the capabilities and reduces the
            // count to fit (REQ-ACQ-001, REQ-DSP-022, REQ-HAL-002).
            PlannedAcquisition planned;

            try
            {
                planned = AcquisitionPlanner.Plan(
                    _activeFrontEnd.Capabilities,
                    DefaultCenterFrequencyHz,
                    DefaultSpanHz,
                    DefaultReferenceLevelDbm);
            }
            catch (ArgumentException failure)
            {
                StatusText.Content = "Cannot measure with this source";
                PlanText.Text = failure.Message;
                return;
            }

            var engine = new SpectrumEngine(_activeFrontEnd, new SpectrumComputer());
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
            PlanText.Text = PlanSummary.Describe(plan, planned, _activeFrontEnd.Capabilities);
            _statusTimer.Start();
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

            if (snapshot != null)
            {
                Plot.Show(snapshot);
            }
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
