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
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using OpenVSA.Core;
using OpenVSA.Dsp.Spectrum;
using OpenVSA.Hal;
using OpenVSA.Measurement;
using OpenVSA.Personality;
using System.IO;
using OpenVSA.Capture.Triggering;
using OpenVSA.Measurement.Contexts;
using OpenVSA.Measurement.Limits;
using OpenVSA.Measurement.Markers;
using OpenVSA.Measurement.State;
using OpenVSA.Ui.Dialogs;
using OpenVSA.Ui.HotSpots;
using OpenVSA.Dsp.Zoom;
using OpenVSA.Ui.Layout;
using OpenVSA.Ui.Menus;
using OpenVSA.Ui.Rendering;
using OpenVSA.Ui.Theming;
using OpenVSA.Ui.ToolWindows;

// Aliased rather than imported: this file's own base class is System.Windows.Window, and importing
// the DSP namespace would make the word ambiguous in a WPF window of all places.
using DspWindow = OpenVSA.Dsp.Windowing.Window;
using WindowType = OpenVSA.Dsp.Windowing.WindowType;
using ChannelFilters = OpenVSA.Dsp.Windowing.ChannelFilters;
using ChannelFilterType = OpenVSA.Dsp.Windowing.ChannelFilterType;

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

        private FrontEndRegistry _registry;
        private PersonalityRegistry _personalities;
        private IMeasurementPersonality _activePersonality;
        private readonly PersonalityResults _results = new PersonalityResults();
        private readonly RenderMarshal _marshal = new RenderMarshal();
        private readonly DispatcherTimer _statusTimer;

        /// <summary>
        /// The measurement contexts this session has (<c>REQ-DAT-010</c>).
        /// </summary>
        /// <remarks>
        /// Declared above <see cref="_markers"/> and <see cref="_contextAnalyser"/> because field
        /// initialisers run in textual order and both of those are built from it.
        /// </remarks>
        private readonly MeasurementContextSet _contextSet = new MeasurementContextSet(ContextName);

        /// <summary>Feeds every context but the active one from the running capture session.</summary>
        private readonly ContextAnalyser _contextAnalyser;

        /// <summary>
        /// The markers of the active context's active trace.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <c>REQ-UI-020</c> letters traces, and <c>REQ-UI-031</c>'s delta label needs the letter to
        /// decide whether to print it, so the set carries it.
        /// </para>
        /// <para>
        /// <strong>Repointed on a context switch rather than cleared and refilled.</strong> Each
        /// context owns its markers (<c>REQ-DAT-010</c>), so switching context is a change of which
        /// set this names — not a change to any set's contents. Rebuilding the markers from the
        /// incoming context's saved state would lose anything placed since it was last saved, and
        /// would do it silently.
        /// </para>
        /// </remarks>
        private MarkerSet _markers;

        /// <summary>
        /// The marker readouts the Markers window shows (<c>REQ-MKR-006</c>).
        /// </summary>
        /// <remarks>
        /// Trace A's set is <see cref="_markers"/>; this collection wraps it so the window and the
        /// above-grid readout render the same text from the same place rather than formatting the
        /// same values twice.
        /// </remarks>
        private readonly MarkerCollection _markerReadouts = new MarkerCollection();

        /// <summary>Measurement results, live once a measurement writes to it.</summary>
        private readonly ToolWindowLog _outputLog = new ToolWindowLog(ToolWindow.Output);

        /// <summary>Instrument traffic, live once a transport writes to it.</summary>
        private readonly ToolWindowLog _scpiLog = new ToolWindowLog(ToolWindow.ScpiLog, 2000);

        /// <summary>Application events, live once something happens.</summary>
        private readonly ToolWindowLog _eventLog = new ToolWindowLog(ToolWindow.EventLog);

        /// <summary>The measurement contexts in the session (<c>REQ-STA-004</c>).</summary>
        private readonly ContextWindowSource _contexts = new ContextWindowSource();

        /// <summary>The eight tool windows of <c>REQ-UI-002</c>.</summary>
        private ToolWindowHost _toolWindows;

        /// <summary>Every colour a user can change, and the ones they have (<c>REQ-UI-014</c>).</summary>
        private readonly ColourPreferences _colours = new ColourPreferences();

        /// <summary>The three font slots of <c>REQ-UI-080</c>.</summary>
        private readonly FontPreferences _fonts = new FontPreferences();

        /// <summary>The Trace tab's display options, shared with the Display menu.</summary>
        private readonly TraceDisplayOptions _traceDisplay = new TraceDisplayOptions();

        /// <summary>The dialog framework's global options (<c>REQ-UI-071</c>).</summary>
        private readonly DialogFrameworkOptions _dialogOptions = new DialogFrameworkOptions();

        /// <summary>
        /// The analysis settings the seven tabs of <c>REQ-UI-072</c> edit (<c>REQ-UI-070</c>).
        /// </summary>
        /// <remarks>
        /// The measurement's definition, and the one place it lives. The settings pane writes into
        /// this and reads back out of it, and so does the Analysis dialog; neither is the state.
        /// </remarks>
        private readonly AnalysisSettings _analysis = new AnalysisSettings();

        /// <summary>The Analysis dialog while it is open, or null (<c>REQ-UI-072</c>).</summary>
        private AnalysisDialog _analysisDialog;

        /// <summary>Whether the settings pane is the one writing, so the change is not echoed.</summary>
        private bool _applyingFromPane;

        /// <summary>The Display Preferences dialog while it is open, or null (<c>REQ-UI-073</c>).</summary>
        /// <remarks>
        /// Held so that asking for it twice raises the one that is open rather than stacking a
        /// second copy over it. A modeless dialog can be left open and forgotten, and two of them
        /// editing the same preferences would each show the other's changes arriving from nowhere.
        /// </remarks>
        private DisplayPreferencesDialog _preferences;

        private ToolbarCustomiserDialog _customiser;

        /// <summary>
        /// The chrome themes on offer, and the one in force (<c>REQ-UI-083</c>).
        /// </summary>
        /// <remarks>
        /// Per shell rather than static, so that a test can build a shell, swap its theme and leave
        /// nothing behind for the next one — the same reason <see cref="PersistPreferences"/>
        /// exists. Applied to the application's resources when there is an application and to the
        /// window's own when there is not, which is what lets the criterion be tested without one.
        /// </remarks>
        private readonly ThemeCatalogue _themes = ThemeCatalogue.Shipped();

        private string _themeName = ThemeCatalogue.DarkName;

        /// <summary>
        /// The rows a spectrogram draws (<c>REQ-UI-054</c>).
        /// </summary>
        /// <remarks>
        /// One history for the shell rather than one per trace window: the accumulator is a
        /// measurement setting, not a per-window one, so four windows set to Spectrogram show four
        /// views of the same accumulation rather than four independent ones started at different
        /// moments.
        /// </remarks>
        private readonly Spectrogram _spectrogramHistory = new Spectrogram();

        private double _spectrogramThresholdBelowTopDb = double.NaN;
        private bool _spectrogramEnhance;
        private TraceAccumulator _appliedAccumulator = TraceAccumulator.None;

        /// <summary>The spectrogram colour map in force (<c>REQ-UI-024</c>).</summary>
        private SpectrogramColourMap _spectrogramMap = SpectrogramColourMap.Default;

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
            // Before InitializeComponent, because ShellWindow.xaml constructs a Syncfusion
            // DockingManager and an unlicensed control puts up a MODAL trial dialog as it is
            // created. App's constructor already registers, but not every path goes through App --
            // the test host builds a shell on its own STA thread -- and there the dialog blocked
            // the dispatcher and failed the snapshot soak. Idempotent, so the ordinary path pays
            // nothing for this.
            SyncfusionLicense.Register();

            InitializeComponent();

            // The active context's markers. Assigned here rather than in a field initialiser because
            // it reads two other fields, and repointed by ActivateContext from then on.
            _markers = _contextSet.Active.Markers.ForTrace('A');
            _contextAnalyser = new ContextAnalyser(_contextSet) { Primary = _contextSet.Active };
            _automation = new OpenVSA.Api.VsaApplication(_contextSet);

            _registry = FrontEndRegistry.CreateDefault();

            // REQ-ARC-003: "discovered on NEXT LAUNCH". Once, here, and never re-probed — a
            // personality that appeared halfway through a session would change what the running
            // measurement means without the user having asked for anything.
            _personalities = PersonalityRegistry.CreateDefault();
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

            // REQ-DSP-012. Both controls are declared in the same grid cell; only the applicable one
            // is in the grid at all, and FollowZeroSpan is what decides which.
            foreach (ChannelFilterType filter in ChannelFilters.All)
            {
                ChannelFilterBox.Items.Add(ChannelFilters.Describe(filter));
            }

            ChannelFilterBox.SelectedIndex = 0;
            FollowZeroSpan();

            // The measured rate and the dropped-frame count of REQ-NFR-012 are status-bar figures,
            // not per-frame ones: updating them from the frame handler would put text layout on the
            // display path sixty times a second to show a number nobody can read that fast.
            _statusTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
            {
                Interval = TimeSpan.FromSeconds(1.0),
            };
            _statusTimer.Tick += (sender, e) => ShowRunningStatistics();

            // Before anything is built, so that the first thing on screen is the theme the user
            // left rather than the default one replaced a moment later. BuildToolWindows is what
            // reads the sidecar, so the name it found is applied immediately after.
            BuildToolWindows();

            ApplyTheme(_themeName);

            // Before the document area, because building the bar is what creates the layout,
            // format and trace-list submenus the document area then fills. REQ-UI-061's contents
            // come from ShellMenuTable; see ShellMenuBinding.cs for what sits behind each item.
            BuildMenuBar();

            // After the bar, because this appends to the Analysis > Type submenu the table just
            // built. REQ-UI-061 fixes that menu's own items as an exact list; its children are the
            // measurement types, and a discovered personality IS a measurement type.
            AddDiscoveredPersonalities();

            BuildDocumentArea();

            // After both, because the Contexts window is built by one and the first context's trace
            // window by the other (REQ-DAT-010, REQ-UI-002).
            ShowContexts();

            // After the document area, so the colours and fonts reach the plots that exist.
            // BuildToolWindows is what read them out of the sidecar; applying them there would
            // paint nothing.
            BuildSpectrogramMapMenu();
            ApplyColours();
            ApplyFonts();

            // The menu follows the options rather than holding them, so that the Trace tab and the
            // Display menu are two views of one setting (REQ-UI-070).
            _traceDisplay.Changed += (sender, e) => FollowTraceDisplayOptions();
            FollowTraceDisplayOptions();

            // The analysis settings are shared between the pane and the Analysis dialog, and a
            // change from either has to reach the measurement. Coalesced, for the reason the hot
            // spots are: a wheel turned through a dozen notches would otherwise re-plan and re-arm
            // the instrument a dozen times.
            _analysisSettle = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(400.0),
            };

            _analysisSettle.Tick += OnAnalysisSettled;
            _analysis.Changed += OnAnalysisChanged;

            // After the analysis settings and the document area, both of which the toolbars show.
            // REQ-UI-063's contents come from ShellToolbars; see ShellToolbarBinding.cs for what
            // sits behind each control.
            BuildToolbars();
            ApplyMouseMode();

            // REQ-UI-006's fields are conditions, so they read correctly from the moment the window
            // exists rather than from the first tick of the statistics timer. A status bar that is
            // blank until something happens is one that says nothing about the state it is in.
            ShowMeasurementStatus();
            ShowStatusFields();

            // After the toolbars, because the accumulator's controls are on one of them, and after
            // the document area, because this is what hands every plot the history it draws.
            ApplyAccumulator();

            // REQ-UI-065. Installed on the window so the gestures reach from anywhere in it, with
            // the unmodified ones routed through a focus check - see ShellShortcuts.
            ShellShortcuts.Install(this, RunShortcut, () => ModifierSource());

            Closed += (sender, e) => ShutDown();
        }

        /// <summary>The chrome themes on offer (<c>REQ-UI-083</c>).</summary>
        public ThemeCatalogue Themes => _themes;

        /// <summary>
        /// The automation object model over this session's contexts.
        /// </summary>
        /// <remarks>
        /// Bound to <see cref="_contextSet"/> rather than given its own list of names, because
        /// <c>REQ-DAT-010</c> requires a context to be the same addressable object in the UI, in a
        /// saved state and in the automation API. Declared after the context set, because field
        /// initialisers run in textual order.
        /// </remarks>
        private readonly OpenVSA.Api.VsaApplication _automation;

        /// <summary>
        /// The automation surface over this shell (<c>REQ-API-001</c>, <c>REQ-LIM-003</c>).
        /// </summary>
        /// <remarks>
        /// <strong>The same objects, not a parallel model.</strong> The limit verdicts the API
        /// reports come from the evaluator this shell feeds and its own display reads, which is
        /// what makes <c>REQ-LIM-003</c>'s "matches the on-screen pass/fail" true by construction
        /// rather than by two computations agreeing.
        /// </remarks>
        public OpenVSA.Api.VsaApplication Automation => _automation;

        /// <summary>The limit evaluator both the display and the API read (<c>REQ-LIM-003</c>).</summary>
        /// <remarks>
        /// The active context's, not the first one's. A limit test is part of a measurement
        /// (<c>REQ-LIM-001</c>), so a session with two contexts has two of them and the one being
        /// evaluated against the trace on screen is the one belonging to the context on screen.
        /// </remarks>
        public LimitEvaluator Limits => _automation.Active.Evaluator;

        /// <summary>
        /// Puts a limit test under evaluation, on screen and in the API together.
        /// </summary>
        /// <param name="test">The test, or <c>null</c> to remove it.</param>
        public void SetLimitTest(LimitTest test)
        {
            Limits.Test = test;

            foreach (char letter in Documents.Traces)
            {
                TracePlot plot = Documents.PlotOf(letter);

                if (plot != null)
                {
                    plot.LimitTest = test;
                }
            }

            ShowLimitVerdict();
        }

        /// <summary>
        /// Puts the standing verdict in the Markers window's <c>Limit</c> row
        /// (<c>REQ-UI-032</c>, <c>REQ-LIM-003</c>).
        /// </summary>
        /// <remarks>
        /// This is the on-screen pass/fail the API's answer is required to match, and it is read
        /// from the same evaluator — so the two cannot disagree without one of them failing to
        /// read at all.
        /// </remarks>
        private void ShowLimitVerdict()
        {
            var source = _toolWindows?.SourceOf(ToolWindow.Markers) as MarkerWindowSource;

            if (source == null)
            {
                return;
            }

            LimitTestResult result = Limits.Latest;

            source.Fields[MarkerWindowReadouts.LimitLabel] = result == null
                ? MarkerWindowReadouts.NotANumber
                : (result.Passed ? "PASS" : "FAIL");

            _toolWindows.Refresh(ToolWindow.Markers);
        }

        private readonly Dictionary<char, TraceWindow> _detached =
            new Dictionary<char, TraceWindow>();

        /// <summary>The traces currently in windows of their own (<c>REQ-UI-003</c>).</summary>
        public IReadOnlyCollection<char> DetachedTraces => _detached.Keys;

        /// <summary>The detached windows as the sidecar records them (<c>REQ-UI-003</c>).</summary>
        public List<DetachedTraceState> DetachedTraceStates()
        {
            var states = new List<DetachedTraceState>(_detached.Count);

            foreach (KeyValuePair<char, TraceWindow> found in _detached)
            {
                Rect where = found.Value.Placement;

                states.Add(new DetachedTraceState
                {
                    Trace = found.Key.ToString(),
                    Left = where.X,
                    Top = where.Y,
                    Width = where.Width,
                    Height = where.Height,
                });
            }

            return states;
        }

        /// <summary>
        /// Pulls a trace out of the document area into a window of its own (<c>REQ-UI-003</c>).
        /// </summary>
        /// <param name="trace">The trace's letter.</param>
        /// <returns>The window, or <c>null</c> when there is no such trace.</returns>
        /// <remarks>
        /// <para>
        /// The content moves rather than being copied: one trace, in one place. A detached window
        /// holding a second plot fed from the same snapshot would be two traces that agree until
        /// one of them is scaled.
        /// </para>
        /// <para>
        /// The last trace cannot be detached, for the reason it cannot be closed — a document area
        /// with nothing in it is a grey rectangle with no way back.
        /// </para>
        /// </remarks>
        public TraceWindow DetachTrace(char trace)
        {
            if (_detached.ContainsKey(trace) || Documents.ContentOf(trace) == null)
            {
                return null;
            }

            if (Documents.Traces.Count <= 1)
            {
                StatusText.Content = "The last trace stays in the main window.";
                return null;
            }

            FrameworkElement content = Documents.ContentOf(trace);

            Documents.RemoveTrace(trace);

            var detachedFrom = content.Parent as Panel;

            if (detachedFrom != null)
            {
                detachedFrom.Children.Remove(content);
            }

            var window = new TraceWindow(trace, content) { Owner = null };

            window.Closed += (sender, e) => _detached.Remove(trace);

            _detached[trace] = window;

            if (Interactive)
            {
                window.Show();
            }

            SaveToolWindowLayout();
            return window;
        }

        /// <summary>
        /// The chrome theme in force, by name (<c>REQ-UI-083</c>).
        /// </summary>
        /// <remarks>
        /// <para>
        /// A name, not a value of a two-valued type. <c>REQ-UI-083</c> forbids an
        /// <c>enum Theme { Light, Dark }</c> switched over to pick values and a boolean "is dark"
        /// anywhere in the rendering or view-model layers, because both satisfy "light and dark"
        /// today and have to be unpicked when a third theme arrives. The name finds a dictionary
        /// and nothing else is decided from it.
        /// </para>
        /// <para>
        /// Setting it applies the theme immediately — "both selectable with no restart" — and a
        /// name this build has no theme for is refused rather than silently ignored, so a
        /// preferences file naming a theme that has gone leaves the shell on the one it had.
        /// </para>
        /// </remarks>
        public string ThemeName
        {
            get { return _themeName; }

            set
            {
                if (string.Equals(_themeName, value, StringComparison.OrdinalIgnoreCase) &&
                    _themes.Current != null)
                {
                    return;
                }

                if (ApplyTheme(value))
                {
                    _themeName = _themes.CurrentName;
                    SaveToolWindowLayout();
                }
            }
        }

        /// <summary>
        /// Installs a chrome theme by name (<c>REQ-UI-083</c>).
        /// </summary>
        /// <param name="name">The theme's name.</param>
        /// <returns>Whether this build has a theme of that name.</returns>
        /// <remarks>
        /// Into the application's resources when there is an application, so every window follows,
        /// and into this window's own when there is not — a shell built by a test has no
        /// <see cref="Application"/> and still has to be themeable, or the criterion could only be
        /// checked by running the program.
        /// </remarks>
        private bool ApplyTheme(string name)
        {
            ResourceDictionary target = Application.Current == null
                ? Resources
                : Application.Current.Resources;

            return _themes.Apply(name, target);
        }

        /// <summary>The trace windows and their arrangement (<c>REQ-UI-005</c>).</summary>
        public TraceDocumentArea DocumentArea => Documents;

        /// <summary>The menu bar of <c>REQ-UI-060</c>.</summary>
        /// <remarks>
        /// Exposed so a test can walk the real bar rather than a description of it. The criterion is
        /// about what the application shows, and a list asserted against a second list proves only
        /// that someone wrote the same thing twice.
        /// </remarks>
        public Menu MenuBar => MainMenu;

        /// <summary>The personalities this shell discovered at launch (<c>REQ-ARC-003</c>).</summary>
        internal PersonalityRegistry Personalities => _personalities;

        /// <summary>What the results panel is showing.</summary>
        internal PersonalityResults Results => _results;

        /// <summary>Whether the Spectrum measurement type is ticked.</summary>
        internal bool SpectrumTypeIsChecked => _spectrumTypeItem != null && _spectrumTypeItem.IsChecked;

        /// <summary>
        /// Runs the active personality over a block, as the pump would.
        /// </summary>
        /// <param name="block">The acquisition.</param>
        /// <remarks>
        /// The seam a shell test needs: acquiring a real block means a front end, a plan and a
        /// running pump, none of which the criterion is about. The path from here on is the pump's
        /// own — the same method <c>BlockAcquired</c> calls.
        /// </remarks>
        internal void MeasureForTest(IqBlock block) => MeasureWithPersonality(block);

        /// <summary>The front-end registry this shell discovered with.</summary>
        /// <remarks>
        /// Exposed so a test can ask which discovered providers need an address without
        /// duplicating the discovery. Read-only: the shell owns when discovery happens.
        /// </remarks>
        internal FrontEndRegistry Registry => _registry;

        /// <summary>
        /// Hands the shell's plot to the document area as trace A and builds the layout menu.
        /// </summary>
        /// <remarks>
        /// The plot is adopted rather than replaced: the measurement pipeline, the hot spots, the
        /// markers and the frame handler are all wired to that one instance, and rebuilding that
        /// wiring for the sake of symmetry would be a large change with nothing to show for it.
        /// </remarks>
        private void BuildDocumentArea()
        {
            Documents.AdoptPrimaryPlot(Plot);

            TracePlot first = Documents.AddTrace('A');

            // Trace windows belong to a context, not to the shell (REQ-DAT-010): the first one is
            // the first context's, and every trace opened afterwards belongs to whichever context
            // was active when it was opened.
            _contextSet.Active.AddTrace('A');

            WireSelectArea(first);
            ColourTrace(first, 'A');
            first.ApplyDisplayOptions(_traceDisplay);

            UpdateMarshalFormats();

            Documents.LayoutChanged += (sender, preset) =>
                StatusText.Content = "Layout: " + preset.Name;

            Documents.ActiveTraceChanged += (sender, trace) =>
            {
                StatusText.Content = "Trace " + trace + " selected";

                // Remembered on the context, so returning to it selects the trace it was left on
                // rather than its first. Guarded, because the document area's active trace can be
                // one belonging to another context during a switch.
                //
                // The marker set is NOT repointed here. Markers are per trace in the model
                // (REQ-MKR-002) but the shell has only ever shown one trace's, and making a trace
                // change swap them is REQ-MKR-002's work rather than REQ-DAT-010's -- what belongs
                // here is that a CONTEXT switch swaps them, which ActivateContext does.
                if (_contextSet.Active.HasTrace(trace))
                {
                    _contextSet.Active.ActiveTrace = trace;
                }

                ShowActiveTrace();
                FillTraceChooser();
            };

            BuildLayoutMenu();

            // The embedded toolbars are built with the menu bar, which is before this - so their
            // choosers were filled from an empty document area. Filled again now that trace A
            // exists, or the trace chooser stays blank and Hide reads as though A were hidden.
            FillTraceChooser();
            FillMarkerChooser();
        }

        /// <summary>
        /// Builds <c>REQ-UI-005</c>'s six layout entries from the preset list.
        /// </summary>
        /// <remarks>
        /// From <see cref="TraceLayoutPreset.Menu"/> rather than written out in XAML, so the
        /// parameterised entries show the N and N×M currently in force and a change to the preset
        /// list cannot leave the menu behind.
        /// </remarks>
        private void BuildLayoutMenu()
        {
            if (_layoutMenu == null)
            {
                return;
            }

            _layoutMenu.Items.Clear();

            foreach (TraceLayoutPreset preset in
                TraceLayoutPreset.Menu(_stackRows, _gridRows, _gridColumns))
            {
                TraceLayoutPreset captured = preset;

                var item = new MenuItem { Header = preset.Name };
                item.Click += (sender, e) => Documents.ApplyLayout(captured);

                _layoutMenu.Items.Add(item);
            }
        }

        private void OnAddTrace(object sender, RoutedEventArgs e)
        {
            if (!OpenTrace(_contextSet.Active))
            {
                StatusText.Content = "Trace letters A to Z are all in use.";
            }
        }

        /// <summary>
        /// Opens a trace window and gives it to a context (<c>REQ-UI-020</c>, <c>REQ-DAT-010</c>).
        /// </summary>
        /// <param name="owner">The context the window belongs to.</param>
        /// <returns><c>false</c> when every letter is in use.</returns>
        /// <remarks>
        /// The letters are the document area's, shared across contexts: a window is one window
        /// whichever context owns it, and two contexts each having a "Trace A" would make the layout
        /// and the state file ambiguous about which one was meant. What is per context is
        /// <em>ownership</em> — <see cref="ActivateContext"/> shows the active context's windows and
        /// hides the rest.
        /// </remarks>
        private bool OpenTrace(MeasurementContext owner)
        {
            // Letters, per REQ-UI-020. The next unused one, so closing B and adding again reuses B
            // rather than walking up the alphabet for the life of the session.
            for (char letter = 'A'; letter <= 'Z'; letter++)
            {
                if (Documents.PlotOf(letter) == null)
                {
                    TracePlot plot = Documents.AddTrace(letter);

                    owner.AddTrace(letter);

                    WireSelectArea(plot);
                    ColourTrace(plot, letter);
                    plot.ApplyDisplayOptions(_traceDisplay);

                    // A new trace opens in the next format round the list rather than as a second
                    // copy of the one beside it. Four windows all showing log magnitude of the same
                    // acquisition would be four identical pictures; REQ-TRC-001's separation of
                    // data from format is what makes them worth having open at once.
                    plot.SetFormat(NextFormat());
                    UpdateMarshalFormats();

                    // The marshal renders to the width of whichever plot asked, so a new plot needs
                    // the current column count before it can draw anything.
                    plot.GraticuleColumnsChanged +=
                        (s, args) => _marshal.Columns = Math.Max(_marshal.Columns, plot.GraticuleColumns);

                    plot.ParameterChanged += (s, args) => UpdateMarshalFormats();

                    if (_frame != null)
                    {
                        plot.SetIndicators(_indicators);
                    }

                    if (ReferenceEquals(owner, _contextSet.Active))
                    {
                        Documents.ActiveTrace = letter;
                    }
                    else
                    {
                        // A window opened for a context that is not on screen is hidden until that
                        // context is activated, and selecting it would point the trace commands at
                        // something the user cannot see.
                        Documents.SetVisible(letter, false);
                    }

                    // More traces than the layout has cells is a layout that needs re-choosing;
                    // Tile Visible is the entry that always fits them all.
                    Documents.ApplyLayout(TraceLayoutPreset.TileVisible());
                    return true;
                }
            }

            return false;
        }

        private void OnRemoveTrace(object sender, RoutedEventArgs e)
        {
            char closing = Documents.ActiveTrace;

            if (!Documents.RemoveTrace(closing))
            {
                StatusText.Content = "The last trace cannot be closed.";
                return;
            }

            // Off whichever context owned it, so the letter can be reused and so a context switch
            // does not try to show a window that is no longer there.
            foreach (MeasurementContext context in _contextSet.Contexts)
            {
                context.RemoveTrace(closing);
            }

            UpdateMarshalFormats();
            Documents.ApplyLayout(TraceLayoutPreset.TileVisible());
        }

        private void OnResizeTraces(object sender, RoutedEventArgs e) => Documents.ResizeTraces();

        /// <summary>
        /// Zooms to a dragged region, without re-acquiring (<c>REQ-DSP-023</c>).
        /// </summary>
        /// <remarks>
        /// <para>
        /// The zoom is a downconversion of the blocks already arriving: the instrument is not
        /// re-tuned, the plan is not re-negotiated, and nothing is re-armed. That is what
        /// <c>REQ-DSP-023</c>'s "using only the captured block" means, and it is why the span can
        /// go far below anything the front end would accept as a setting.
        /// </para>
        /// <para>
        /// <c>ZoomControl</c> owns the 1/256 bound and its message (<c>REQ-REC-004</c>), so a drag
        /// past it is refused here with the same words a typed span would get.
        /// </para>
        /// </remarks>
        private void OnAreaSelected(object sender, AreaSelectedEventArgs area)
        {
            // REQ-UI-063: what a dragged rectangle does is the user's choice, made on the Area
            // Select tool itself. Scaling an axis and re-analysing a band are different operations
            // on one gesture, and running them together would mean a drag meant to magnify the
            // display quietly changing what is being measured.
            if (_areaAction == Rendering.AreaSelectAction.ScaleY)
            {
                ScaleYToArea(sender as TracePlot, area);
                return;
            }

            // Scale X magnifies the display; Scale X and Y does both. Neither touches the
            // measurement, which is what separates them from Set centre and span (#397).
            if (_areaAction == Rendering.AreaSelectAction.ScaleX ||
                _areaAction == Rendering.AreaSelectAction.ScaleBoth)
            {
                var plot = sender as TracePlot;

                ScaleXToArea(plot, area);

                if (_areaAction == Rendering.AreaSelectAction.ScaleBoth)
                {
                    ScaleYToArea(plot, area);
                }

                return;
            }

            SpectrumEngine engine = _engine;

            if (engine == null || engine.Plan == null)
            {
                StatusText.Content = "Nothing is being measured, so there is nothing to zoom into.";
                return;
            }

            if (_zoom == null)
            {
                _zoom = new ZoomControl(engine.Plan.CenterFrequencyHz, engine.Plan.SpanHz);
            }

            try
            {
                _zoom.SelectArea(area.StartHz, area.StopHz);
            }
            catch (ArgumentOutOfRangeException refused)
            {
                StatusText.Content = refused.Message.Split('\n')[0];
                return;
            }

            DigitalDownconverter downconverter;

            if (!_zoom.TryCreateDownconverter(engine.Plan.SampleRateHz, out downconverter))
            {
                StatusText.Content =
                    "That region is as wide as the acquisition; nothing to downconvert.";
                return;
            }

            engine.Zoom = downconverter;
            SetFullSpanEnabled(true);

            StatusText.Content =
                _zoom.Annotation() + " — " + downconverter.Decimation + ":1 downconversion, " +
                "no re-acquisition";
        }

        /// <summary>Returns the analysis to the whole captured band (<c>REQ-DSP-023</c>).</summary>
        private void OnFullSpan(object sender, RoutedEventArgs e)
        {
            SpectrumEngine engine = _engine;

            if (_zoom != null)
            {
                _zoom.FullSpan();
            }

            if (engine != null)
            {
                engine.Zoom = null;
            }

            SetFullSpanEnabled(false);
            StatusText.Content = "Full span.";
        }

        /// <summary>Where the analysis sits inside the captured band (<c>REQ-DSP-023</c>).</summary>
        private ZoomControl _zoom;

        /// <summary>
        /// Gives a trace its colour from the twenty-entry table (<c>REQ-UI-020</c>).
        /// </summary>
        /// <remarks>
        /// One setting, and it drives the line and the trace's own annotation together
        /// (<c>REQ-UI-021</c>) — the plot applies it to both, so there is no second colour here to
        /// keep in step with the first.
        /// </remarks>
        private void ColourTrace(TracePlot plot, char letter)
        {
            plot.Palette = plot.Palette.WithTrace(TraceColours.ForTrace(letter));
        }

        /// <summary>
        /// Prints the active trace, optionally forcing a white background (<c>REQ-UI-015</c>).
        /// </summary>
        /// <remarks>
        /// The option exists because large areas of black do not print well, and the palette's
        /// <c>ForPrinting</c> darkens the light colours rather than leaving them invisible on
        /// white — which is the half of it that a plain background swap misses.
        /// </remarks>
        private void OnPrintTrace(object sender, RoutedEventArgs e)
        {
            if (!Interactive)
            {
                // The picker belongs to the user, not to a test run. See ShellWindow.Interactive:
                // what is skipped is the dialog, never the routing.
                StatusText.Content = "Print trace";
                return;
            }

            TracePlot plot = Documents.ActivePlot;

            if (plot == null)
            {
                return;
            }

            var dialog = new PrintDialog();

            if (dialog.ShowDialog() != true)
            {
                return;
            }

            PlotPalette onScreen = plot.Palette;

            try
            {
                if (_traceDisplay.ForceWhiteBackgroundOnPrint)
                {
                    plot.Palette = onScreen.ForPrinting();
                    plot.UpdateLayout();
                }

                dialog.PrintVisual(plot, "OpenVSA trace " + Documents.ActiveTrace);
            }
            finally
            {
                // Restored whatever the print did, so a failed or cancelled print never leaves the
                // display in the printing palette.
                plot.Palette = onScreen;
            }
        }

        /// <summary>
        /// Wires a plot's mouse to the Select Area gesture.
        /// </summary>
        /// <remarks>
        /// The pointer is captured for the duration, so a drag that leaves the plot still ends
        /// where the button was released rather than being abandoned mid-selection — which is what
        /// happens when the region wanted runs to the very edge of the trace.
        /// </remarks>
        private void WireSelectArea(TracePlot plot)
        {
            // Whatever mouse mode is in force on the Marker Tools toolbar (REQ-UI-063), so a
            // trace opened while Area Select is on can be dragged over at once.
            plot.SelectAreaEnabled =
                _mouseMode == Rendering.MouseMode.AreaSelect ||
                _mouseMode == Rendering.MouseMode.BandPower ||
                _mouseMode == Rendering.MouseMode.TimeGate;
            plot.AreaSelected += OnAreaSelected;

            plot.PreviewMouseLeftButtonDown += (sender, e) =>
            {
                if (MoveSpectrogramMarker(plot, e))
                {
                    e.Handled = true;
                    return;
                }

                if (plot.BeginSelectArea(e.GetPosition(plot)))
                {
                    plot.CaptureMouse();
                    e.Handled = true;
                }
            };

            plot.MouseMove += (sender, e) =>
            {
                if (plot.IsSelectingArea)
                {
                    plot.ExtendSelectArea(e.GetPosition(plot));
                }
            };

            plot.PreviewMouseLeftButtonUp += (sender, e) =>
            {
                if (!plot.IsSelectingArea)
                {
                    return;
                }

                plot.ReleaseMouseCapture();

                if (plot.EndSelectArea(e.GetPosition(plot)))
                {
                    e.Handled = true;
                }
            };
        }

        /// <summary>
        /// Moves one of a spectrogram's two markers, if that is what the click meant
        /// (<c>REQ-UI-054</c>).
        /// </summary>
        /// <param name="plot">The plot that was clicked.</param>
        /// <param name="e">The click.</param>
        /// <returns>Whether a marker moved.</returns>
        /// <remarks>
        /// <para>
        /// <strong>One gesture, two markers, and the modifier chooses.</strong> A plain click moves
        /// the spectrogram marker along the frequency axis; Shift moves the trace-select marker
        /// along the time axis. Two markers on perpendicular axes cannot both follow one
        /// unqualified click, and making the plain click move the frequency marker matches what the
        /// same click does on a spectrum trace.
        /// </para>
        /// <para>
        /// In Marker mode only, like every other click that changes something
        /// (<c>REQ-UI-063</c>'s Marker Tools). Pointer exists so that a click can mean nothing, and
        /// Area Select still gets its drag on a spectrogram.
        /// </para>
        /// </remarks>
        private bool MoveSpectrogramMarker(TracePlot plot, MouseButtonEventArgs e)
        {
            if (_mouseMode != Rendering.MouseMode.Marker || !plot.IsShowingSpectrogram)
            {
                return false;
            }

            bool traceSelect = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;

            SpectrogramMarkerKind which = traceSelect
                ? SpectrogramMarkerKind.TraceSelect
                : SpectrogramMarkerKind.Spectrogram;

            if (!plot.MoveSpectrogramMarker(which, e.GetPosition(plot)))
            {
                return false;
            }

            ShowSpectrogramMarkers(plot);
            return true;
        }

        /// <summary>Says where the two spectrogram markers are (<c>REQ-UI-054</c>).</summary>
        private void ShowSpectrogramMarkers(TracePlot plot)
        {
            SpectrogramMarkers markers = plot.SpectrogramMarkers;

            if (markers == null || !markers.HasRows)
            {
                return;
            }

            StatusText.Content =
                "Spectrogram marker " + EngineeringText.Frequency(markers.FrequencyHz, 6) +
                "; trace select " +
                EngineeringText.Time(markers.SecondsBeforeNewest) + " before the newest sweep " +
                "(row " + markers.RowIndex + " of " + _spectrogramHistory.RowCount + ").";
        }

        /// <summary>
        /// The format a newly opened trace starts in, stepping round the ones that always apply.
        /// </summary>
        /// <remarks>
        /// Only the formats a spectrum always carries. Group delay and the phase formats need
        /// phase, which <c>REQ-TRC-002</c> makes unavailable after power averaging — a new trace
        /// that opened into a format the current averaging forbids would be blank for a reason
        /// nobody could see.
        /// </remarks>
        private TraceFormat NextFormat()
        {
            TraceFormat[] cycle =
            {
                TraceFormat.LogMagnitude,
                TraceFormat.LinearMagnitude,
                TraceFormat.Real,
                TraceFormat.Imaginary,
            };

            TraceFormat next = cycle[_nextFormat % cycle.Length];
            _nextFormat++;

            return next;
        }

        /// <summary>How far round <see cref="NextFormat"/> has stepped.</summary>
        private int _nextFormat = 1;

        /// <summary>The <c>N</c> the Stack menu entry offers.</summary>
        private int _stackRows = 2;

        /// <summary>The <c>N</c> the Grid menu entry offers.</summary>
        private int _gridRows = 2;

        /// <summary>The <c>M</c> the Grid menu entry offers.</summary>
        private int _gridColumns = 2;

        /// <summary>The eight tool windows of <c>REQ-UI-002</c>.</summary>
        public ToolWindowHost ToolWindows => _toolWindows;

        /// <summary>Every colour a user can change (<c>REQ-UI-014</c>).</summary>
        public ColourPreferences Colours => _colours;

        /// <summary>The three font slots (<c>REQ-UI-080</c>).</summary>
        public FontPreferences Fonts => _fonts;

        /// <summary>The trace display options shared by the Trace tab and the Display menu.</summary>
        public TraceDisplayOptions TraceDisplay => _traceDisplay;

        /// <summary>The dialog framework's global options (<c>REQ-UI-071</c>).</summary>
        public DialogFrameworkOptions DialogOptions => _dialogOptions;

        /// <summary>The spectrogram colour map in force (<c>REQ-UI-024</c>).</summary>
        public SpectrogramColourMap SpectrogramMap => _spectrogramMap;

        /// <summary>
        /// Creates the eight tool windows, restores where they were, and attaches their sources.
        /// </summary>
        /// <remarks>
        /// Four have live sources today. The Markers window reads
        /// <see cref="MarkerCollection.Readouts"/>, which is the same text the above-grid readout
        /// shows — <c>REQ-MKR-006</c>'s agreement, by construction. Output, the SCPI Log and the
        /// Event Log are appendable logs that become live the moment anything real is written to
        /// them, and carry transcribed traffic until then.
        ///
        /// Player, Block Diagram and Macros have nothing behind them at all — playback is
        /// <c>REQ-REC-002</c>, the diagram wants a live signal-path model and macros want the
        /// automation API. They show a worked example of what each window is for, labelled as
        /// demonstration data by <see cref="ToolWindowSource.Lines"/> so it cannot be read as a
        /// loaded recording or live traffic.
        /// </remarks>
        private void BuildToolWindows()
        {
            _toolWindows = new ToolWindowHost(Docking, LoadToolWindowLayout());

            _markerReadouts.Update('A', SpectrumFrame.FromLevels(
                new float[] { -100.0f, -100.0f }, 0.0, 1.0, DspWindow.Default, 1.0));

            // Seeded before the logs are attached: SetSource renders what the source says now, and
            // attaching an empty log leaves a pane that stays blank until something is written to
            // it - which for three of these is "never, on a bench with nothing connected".
            foreach (string line in ToolWindowDemonstrations.Output())
            {
                _outputLog.Seed(line);
            }

            foreach (string line in ToolWindowDemonstrations.ScpiLog())
            {
                _scpiLog.Seed(line);
            }

            foreach (string line in ToolWindowDemonstrations.EventLog())
            {
                _eventLog.Seed(line);
            }

            _toolWindows.SetSource(new MarkerWindowSource(_markerReadouts));
            _toolWindows.SetSource(_outputLog);
            _toolWindows.SetSource(_scpiLog);
            _toolWindows.SetSource(_eventLog);
            _toolWindows.SetSource(_contexts);

            foreach (ToolWindow window in Ui.ToolWindows.ToolWindows.All)
            {
                IToolWindowSource demonstration = ToolWindowDemonstrations.For(window);

                if (demonstration != null)
                {
                    _toolWindows.SetSource(demonstration);
                }
            }

            foreach (ToolWindow window in Ui.ToolWindows.ToolWindows.All)
            {
                _toolWindows.SetOpen(window, _toolWindows.Layout.IsOpen(window));
            }
        }

        /// <summary>
        /// Reads the saved tool-window layout, or defaults if there is none.
        /// </summary>
        /// <remarks>
        /// Never throws. A display preference file that cannot be read is worth a default layout
        /// and a line in the event log, not a shell that will not open.
        /// </remarks>
        private ToolWindowLayout LoadToolWindowLayout()
        {
            try
            {
                if (File.Exists(ToolWindowLayoutPath))
                {
                    var saved = SidecarFile.Load<DisplayPreferencesState>(ToolWindowLayoutPath);

                    LoadColours(saved);

                    return ToolWindowLayout.FromState(saved.ToolWindows);
                }
            }
            catch (Exception failure)
            {
                _eventLog.Append(
                    "Tool-window layout could not be read (" + failure.GetType().Name +
                    "); starting from defaults.");
            }

            return new ToolWindowLayout();
        }

        /// <summary>
        /// Whether this shell writes its display preferences when they change.
        /// </summary>
        /// <remarks>
        /// True in the application. A test that builds a shell and closes it would otherwise
        /// rewrite the real user's tool-window layout, colours and dialog modes in
        /// <c>%APPDATA%</c> — which is a suite with a side effect on the machine it runs on, and
        /// one that is invisible until somebody notices two extra panes open next morning.
        /// </remarks>
        public bool PersistPreferences { get; set; } = true;

        /// <summary>Writes the tool-window layout, so it survives a restart (<c>REQ-UI-002</c>).</summary>
        private void SaveToolWindowLayout()
        {
            if (_toolWindows == null || !PersistPreferences)
            {
                return;
            }

            try
            {
                _toolWindows.CaptureSizes();

                var preferences = new DisplayPreferencesState
                {
                    ToolWindows = _toolWindows.Layout.ToState(),
                    SpectrogramColourMap = SpectrogramColourMap.NameOf(_spectrogramMap.Kind),
                    SpectrogramUserMap = UserMapEntries(),
                    Toolbars = ToolbarArrangement.ToState(),
                    ChromeTheme = _themeName,
                    DetachedTraces = DetachedTraceStates(),
                };

                _colours.SaveInto(preferences);
                _fonts.SaveInto(preferences);
                _dialogOptions.SaveInto(preferences);
                _traceDisplay.SaveInto(preferences);

                Directory.CreateDirectory(Path.GetDirectoryName(ToolWindowLayoutPath));
                SidecarFile.Save(preferences, ToolWindowLayoutPath);
            }
            catch (Exception)
            {
                // A layout that could not be saved is a layout that starts at defaults next time.
                // Failing to close over it would be the worse outcome by a wide margin.
            }
        }

        /// <summary>
        /// Reads the saved colours and spectrogram map back out of the display sidecar
        /// (<c>REQ-UI-014</c>, <c>REQ-UI-024</c>).
        /// </summary>
        /// <remarks>
        /// Elements the file names but this build does not know are logged rather than thrown on: a
        /// preferences file written by a later version should cost the user the colours it mentions,
        /// not all of them.
        /// </remarks>
        private void LoadColours(DisplayPreferencesState saved)
        {
            IReadOnlyList<string> unknown = _colours.LoadFrom(saved);

            if (unknown.Count > 0)
            {
                _eventLog.Append(
                    "Display preferences name " + unknown.Count +
                    " colour(s) this build does not have; the rest were applied.");
            }

            IReadOnlyList<string> unknownFonts = _fonts.LoadFrom(saved);

            if (unknownFonts.Count > 0)
            {
                _eventLog.Append(
                    "Display preferences name " + unknownFonts.Count +
                    " font slot(s) this build cannot use; the rest were applied.");
            }

            // REQ-UI-071's Persist Mode: the mode a dialog was closed in has to survive a restart,
            // and this is the read that makes "across restarts" true rather than "within a session".
            IReadOnlyList<string> unknownModes = _dialogOptions.LoadFrom(saved);

            if (unknownModes.Count > 0)
            {
                _eventLog.Append(
                    "Display preferences name " + unknownModes.Count +
                    " dialog mode(s) this build does not have; the rest were applied.");
            }

            _traceDisplay.LoadFrom(saved);

            // REQ-UI-083: a theme this build no longer has leaves the shell on the one it had,
            // reported rather than thrown on — the rule the colours and the toolbars keep, and the
            // case a preferences file naming a removed custom theme produces.
            if (!string.IsNullOrEmpty(saved.ChromeTheme))
            {
                if (_themes.Find(saved.ChromeTheme) == null)
                {
                    _eventLog.Append(
                        "Display preferences name a chrome theme this build does not have ('" +
                        saved.ChromeTheme + "'); using " + _themeName + ".");
                }
                else
                {
                    _themeName = saved.ChromeTheme;
                }
            }

            // REQ-UI-064: a custom toolbar survives a restart. Read before the tray is built, so
            // that the first thing on screen is the arrangement the user left rather than the
            // default one replaced a moment later.
            if (saved.Toolbars != null && saved.Toolbars.Count > 0)
            {
                IReadOnlyList<string> unknownControls = ToolbarArrangement.LoadFrom(saved.Toolbars);

                if (unknownControls.Count > 0)
                {
                    _eventLog.Append(
                        "The saved toolbars name " + unknownControls.Count +
                        " control(s) this build does not have; the rest were restored.");
                }
            }

            SpectrogramColourMapKind kind;

            if (!SpectrogramColourMap.TryParseName(saved.SpectrogramColourMap, out kind))
            {
                _eventLog.Append(
                    "Display preferences name an unknown spectrogram map '" +
                    saved.SpectrogramColourMap + "'; using " +
                    SpectrogramColourMap.NameOf(SpectrogramColourMapKind.ColorNormal) + ".");
                kind = SpectrogramColourMapKind.ColorNormal;
            }

            _spectrogramMap = kind == SpectrogramColourMapKind.UserDefined
                ? UserMapFrom(saved)
                : SpectrogramColourMap.Of(kind);
        }

        /// <summary>
        /// The user map's colours for the sidecar, or an empty list if the map is a built-in one.
        /// </summary>
        /// <remarks>
        /// Written only for a user-defined map. A built-in map is named, not enumerated: writing its
        /// 64 colours out would freeze today's built-in map into the file and make a change to it
        /// invisible to everyone who had ever saved preferences.
        /// </remarks>
        private List<uint> UserMapEntries()
        {
            var colours = new List<uint>();

            if (_spectrogramMap.Kind != SpectrogramColourMapKind.UserDefined)
            {
                return colours;
            }

            foreach (PlotColor colour in _spectrogramMap.Entries)
            {
                colours.Add(ColourPreferences.Pack(colour));
            }

            return colours;
        }

        /// <summary>Rebuilds a user-defined spectrogram map from the sidecar, or falls back.</summary>
        private SpectrogramColourMap UserMapFrom(DisplayPreferencesState saved)
        {
            if (saved.SpectrogramUserMap == null || saved.SpectrogramUserMap.Count < 2)
            {
                _eventLog.Append(
                    "Display preferences select a user spectrogram map but carry no colours for " +
                    "it; using " + SpectrogramColourMap.NameOf(SpectrogramColourMapKind.ColorNormal) +
                    ".");

                return SpectrogramColourMap.Default;
            }

            var colours = new List<PlotColor>(saved.SpectrogramUserMap.Count);

            foreach (uint argb in saved.SpectrogramUserMap)
            {
                colours.Add(ColourPreferences.Unpack(argb));
            }

            return SpectrogramColourMap.User(colours);
        }

        /// <summary>Builds the spectrogram-map menu from the enumeration (<c>REQ-UI-024</c>).</summary>
        /// <remarks>
        /// From the enumeration, so the menu cannot list a map that does not exist or miss one that
        /// does. <em>User Defined</em> is listed but disabled until a user map has been loaded —
        /// present, so its absence is visible, rather than quietly omitted.
        /// </remarks>
        private void BuildSpectrogramMapMenu()
        {
            if (_spectrogramMenu == null)
            {
                return;
            }

            _spectrogramMenu.Items.Clear();

            foreach (SpectrogramColourMapKind kind in
                (SpectrogramColourMapKind[])Enum.GetValues(typeof(SpectrogramColourMapKind)))
            {
                bool user = kind == SpectrogramColourMapKind.UserDefined;

                var item = new MenuItem
                {
                    Header = SpectrogramColourMap.NameOf(kind),
                    IsCheckable = true,
                    IsChecked = _spectrogramMap.Kind == kind,
                    Tag = kind,
                    IsEnabled = !user || _spectrogramMap.Kind == SpectrogramColourMapKind.UserDefined,
                    ToolTip = kind == SpectrogramColourMapKind.GreyNormal ||
                              kind == SpectrogramColourMapKind.GreyReverse
                        ? SpectrogramColourMap.GreyScaleTooltip
                        : null,
                };

                item.Click += OnSpectrogramMapChosen;
                _spectrogramMenu.Items.Add(item);
            }
        }

        private void OnSpectrogramMapChosen(object sender, RoutedEventArgs e)
        {
            var item = sender as MenuItem;

            if (item == null)
            {
                return;
            }

            var kind = (SpectrogramColourMapKind)item.Tag;

            if (kind != SpectrogramColourMapKind.UserDefined)
            {
                _spectrogramMap = SpectrogramColourMap.Of(kind);
            }

            BuildSpectrogramMapMenu();
            FollowSpectrogramMap();

            _eventLog.Append(
                "Spectrogram colour map set to " + SpectrogramColourMap.NameOf(_spectrogramMap.Kind) +
                " (" + _spectrogramMap.Count + " entries).");
        }

        /// <summary>Coalesces analysis changes into one re-plan.</summary>
        private readonly DispatcherTimer _analysisSettle;

        /// <summary>How far the shell content is scaled (<c>REQ-NFR-007a</c>).</summary>
        private double _contentScale = 1.0;

        /// <summary>Smallest content scale, below which the annotation stops being legible.</summary>
        private const double MinimumContentScale = 0.5;

        /// <summary>Largest content scale, above which the trace window has no room left.</summary>
        private const double MaximumContentScale = 3.0;

        /// <summary>How much one press of the scaling keys moves the content scale.</summary>
        private const double ContentScaleStep = 0.1;

        /// <summary>The last shortcut this shell ran, for a test to read back.</summary>
        public string LastShortcut { get; private set; } = string.Empty;

        /// <summary>
        /// Where the shortcut handler reads the modifier keys from.
        /// </summary>
        /// <remarks>
        /// <see cref="Keyboard.Modifiers"/> in the application. Per shell rather than static, so two
        /// windows open at once cannot share it - see <see cref="ShellShortcuts.Install"/> for why
        /// there is a seam here at all.
        /// </remarks>
        public Func<ModifierKeys> ModifierSource { get; set; } = () => Keyboard.Modifiers;

        /// <summary>How far the shell content is scaled (<c>REQ-NFR-007a</c>).</summary>
        public double ContentScale => _contentScale;

        /// <summary>
        /// Runs a keyboard shortcut (<c>REQ-UI-065</c>).
        /// </summary>
        /// <param name="shortcut">The binding whose gesture was pressed.</param>
        /// <remarks>
        /// One switch over the binding table rather than a handler per gesture, so a binding
        /// declared in <see cref="ShellShortcuts"/> and not handled here fails loudly at the point
        /// of use instead of doing nothing quietly.
        /// </remarks>
        private void RunShortcut(ShellShortcut shortcut)
        {
            if (ReferenceEquals(shortcut, ShellShortcuts.PauseOrResume))
            {
                PauseOrResume();
            }
            else if (ReferenceEquals(shortcut, ShellShortcuts.Restart))
            {
                RestartMeasurement();
            }
            else if (ReferenceEquals(shortcut, ShellShortcuts.NewTrace))
            {
                LastShortcut = shortcut.Action;
                OnAddTrace(this, new RoutedEventArgs());
            }
            else if (ReferenceEquals(shortcut, ShellShortcuts.AutoScale))
            {
                AutoScaleActiveTrace();
            }
            else if (ReferenceEquals(shortcut, ShellShortcuts.MarkerPosition))
            {
                PromptForMarkerPosition();
            }
            else if (ReferenceEquals(shortcut, ShellShortcuts.PlayerWindow))
            {
                ShowToolWindow(ToolWindow.Player, shortcut);
            }
            else if (ReferenceEquals(shortcut, ShellShortcuts.OutputWindow))
            {
                ShowToolWindow(ToolWindow.Output, shortcut);
            }
            else if (ReferenceEquals(shortcut, ShellShortcuts.SaveBitmap))
            {
                SaveActiveTraceBitmap();
            }
            else if (ReferenceEquals(shortcut, ShellShortcuts.ContextHelp) ||
                     ReferenceEquals(shortcut, ShellShortcuts.DynamicHelp))
            {
                ShowHelp(shortcut);
            }
            else if (ReferenceEquals(shortcut, ShellShortcuts.ScaleUp))
            {
                ScaleContent(ContentScaleStep, shortcut);
            }
            else if (ReferenceEquals(shortcut, ShellShortcuts.ScaleDown))
            {
                ScaleContent(-ContentScaleStep, shortcut);
            }
            else
            {
                throw new InvalidOperationException(
                    "REQ-UI-065 declares the binding " + shortcut +
                    " and this shell has no action for it.");
            }
        }

        /// <summary>Pauses a running measurement, or resumes a paused one.</summary>
        private void PauseOrResume()
        {
            LastShortcut = ShellShortcuts.PauseOrResume.Action;

            // The same press as the Control toolbar's Pause button, decided by the same state
            // machine. Two paths to one control is how the key and the button end up disagreeing
            // about what a second press means.
            PressPause();
        }

        /// <summary>
        /// Restarts the measurement, discarding what has been accumulated.
        /// </summary>
        /// <remarks>
        /// <c>REQ-UI-063</c> Restart, reached from the keyboard: all current measurement data
        /// including averaging is discarded. Starting a fresh engine is what discards it, because
        /// the averaging lives in the computer the engine owns.
        /// </remarks>
        private async void RestartMeasurement()
        {
            LastShortcut = ShellShortcuts.Restart.Action;
            LastToolbarCommand = "Control > Restart";

            _sweep.Restart();

            // "All current measurement data including averaging is discarded." Said here rather
            // than left to the fact that a fresh engine happens to bring a fresh averager: the
            // requirement asks for it to be asserted, and an assertion needs something to point at.
            SpectrumEngine running = _engine;

            if (running != null && running.Averager != null)
            {
                running.Averager.Reset();
            }

            if (_activeFrontEnd != null)
            {
                await StartMeasurementAsync().ConfigureAwait(true);
            }

            FollowSweep();
        }

        /// <summary>Scales the active trace vertical axis to the trace on it.</summary>
        private void AutoScaleActiveTrace()
        {
            LastShortcut = ShellShortcuts.AutoScale.Action;

            TracePlot plot = Documents.ActivePlot;

            StatusText.Content = plot != null && plot.AutoScale()
                ? "Auto-scaled trace " + Documents.ActiveTrace
                : "Auto-scale needs a trace to scale to.";
        }

        /// <summary>
        /// Asks for the selected marker position (<c>REQ-UI-065</c> Ctrl+K).
        /// </summary>
        /// <remarks>
        /// Modeless and live, as every setting dialog is (<c>REQ-UI-070</c>): the marker moves as
        /// the frequency is typed, and the trace follows it.
        /// </remarks>
        private void PromptForMarkerPosition()
        {
            LastShortcut = ShellShortcuts.MarkerPosition.Action;

            Marker selected = _markers.Selected;

            if (selected == null)
            {
                StatusText.Content = "No marker is selected to position.";
                return;
            }

            var position = NumericHotSpotValue.Frequency(selected.XHz, 1e3);
            position.ProportionalStep = 0.001;

            position.Changed += (sender, e) =>
            {
                _markerReadouts.MoveTo(selected, position.Value);
                RefreshMarkers();
            };

            var dialog = new ValueEntryDialog(
                "Marker " + selected.Number + " position", position)
            {
                Owner = this,
            };

            dialog.Show();
            dialog.Activate();
        }

        /// <summary>Opens a tool window and brings it to the front.</summary>
        private void ShowToolWindow(ToolWindow window, ShellShortcut shortcut)
        {
            LastShortcut = shortcut.Action;

            if (_toolWindows != null)
            {
                _toolWindows.SetOpen(window, true);
                StatusText.Content =
                    Ui.ToolWindows.ToolWindows.NameOf(window) + " window shown";
            }
        }

        /// <summary>
        /// Writes the active trace to a PNG (<c>REQ-UI-065</c> Ctrl+B).
        /// </summary>
        /// <remarks>
        /// The rendered control, not the rasterised surface alone: the annotation is WPF elements
        /// over the bitmap, and a saved image without the scales and the centre frequency would be
        /// a picture of a trace rather than a record of a measurement.
        /// </remarks>
        private void SaveActiveTraceBitmap()
        {
            LastShortcut = ShellShortcuts.SaveBitmap.Action;

            TracePlot plot = Documents.ActivePlot;

            if (plot == null || plot.ActualWidth < 1.0 || plot.ActualHeight < 1.0)
            {
                StatusText.Content = "There is no trace to save.";
                return;
            }

            if (!Interactive)
            {
                // The picker belongs to the user, not to a test run. See ShellWindow.Interactive:
                // what is skipped is the dialog, never the routing.
                StatusText.Content = "Save trace bitmap";
                return;
            }

            var picker = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "PNG image (*.png)|*.png",
                FileName = "OpenVSA trace " + Documents.ActiveTrace + ".png",
                Title = "Save trace bitmap",
            };

            if (picker.ShowDialog(this) != true)
            {
                return;
            }

            try
            {
                DpiScale dpi = VisualTreeHelper.GetDpi(plot);

                var bitmap = new RenderTargetBitmap(
                    (int)Math.Round(plot.ActualWidth * dpi.DpiScaleX),
                    (int)Math.Round(plot.ActualHeight * dpi.DpiScaleY),
                    96.0 * dpi.DpiScaleX,
                    96.0 * dpi.DpiScaleY,
                    PixelFormats.Pbgra32);

                bitmap.Render(plot);

                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));

                using (FileStream file = File.Create(picker.FileName))
                {
                    encoder.Save(file);
                }

                StatusText.Content = "Saved " + Path.GetFileName(picker.FileName);
            }
            catch (Exception failure)
            {
                // Reported rather than thrown at the dispatcher: a full disk or a read-only folder
                // must not take the measurement down with it.
                StatusText.Content = "Could not save the bitmap: " + failure.Message;
            }
        }

        /// <summary>
        /// Answers the help keys (<c>REQ-UI-065</c>).
        /// </summary>
        /// <remarks>
        /// The bindings are reachable and they do something visible. What they cannot do yet is
        /// show help: this build has no help content, and saying so in the status bar and the event
        /// log is the honest answer, because a key that appears to do nothing is
        /// indistinguishable from a binding that was never wired up.
        /// </remarks>
        private void ShowHelp(ShellShortcut shortcut)
        {
            LastShortcut = shortcut.Action;

            string message = shortcut.Action + " is bound to " + shortcut.Gesture +
                ", but this build carries no help content yet.";

            StatusText.Content = message;
            _eventLog.Append(message);
        }

        /// <summary>
        /// Scales the shell content (<c>REQ-NFR-007a</c>, <c>REQ-UI-065</c>).
        /// </summary>
        /// <param name="by">How much to change the scale by.</param>
        /// <param name="shortcut">The binding that asked.</param>
        /// <remarks>
        /// A layout transform on the whole shell rather than a font size, so the trace surface, the
        /// annotation and the chrome grow together. The bounds are there because below a half
        /// nothing is legible and above three the trace window has no room left.
        /// </remarks>
        private void ScaleContent(double by, ShellShortcut shortcut)
        {
            LastShortcut = shortcut.Action;

            double wanted = Math.Round(_contentScale + by, 2);

            _contentScale = wanted < MinimumContentScale
                ? MinimumContentScale
                : (wanted > MaximumContentScale ? MaximumContentScale : wanted);

            Root.LayoutTransform = Math.Abs(_contentScale - 1.0) < 1e-9
                ? null
                : new ScaleTransform(_contentScale, _contentScale);

            StatusText.Content = "Content scale " +
                (_contentScale * 100.0).ToString("0", CultureInfo.CurrentCulture) + " %";
        }

        /// <summary>
        /// Opens the Analysis dialog on a tab (<c>REQ-UI-072</c>).
        /// </summary>
        /// <param name="tab">The tab to show, as <c>REQ-UI-072</c> names it.</param>
        /// <remarks>
        /// Modeless and live, per <c>REQ-UI-070</c>: the measurement keeps running behind it and
        /// every change is applied as it is made. One dialog, raised again rather than duplicated —
        /// two of them editing one measurement would each show the other's changes arriving from
        /// nowhere.
        /// </remarks>
        private void OpenAnalysis(string tab)
        {
            if (_analysisDialog == null)
            {
                var dialog = new AnalysisDialog(_dialogOptions, _analysis);

                dialog.Closed += (sender, e) =>
                {
                    _analysisDialog = null;
                    SaveToolWindowLayout();
                };

                _analysisDialog = dialog;
                dialog.ShowModeless(this);
            }
            else
            {
                _analysisDialog.Activate();
            }

            _analysisDialog.ShowTab(tab);
        }

        /// <summary>
        /// Follows a change to the analysis settings, from whichever surface made it.
        /// </summary>
        /// <remarks>
        /// The detector reaches the render marshal at once, because it costs a redraw and not a
        /// re-acquisition. Everything else waits for the settle timer: a re-plan re-arms the
        /// instrument, and doing that on every keystroke of a frequency would be seconds of GPIB
        /// traffic for a number the user is still typing.
        /// </remarks>
        private void OnAnalysisChanged(object sender, EventArgs e)
        {
            _marshal.Detector = _analysis.Detector;

            ApplyAccumulator();

            // REQ-DSP-012, before the early return below: the pane must follow the mode whether the
            // change came from the pane, the Analysis dialog or a recalled state, and the guard that
            // follows is only about not echoing a pane edit back into the pane.
            FollowZeroSpan();

            if (_applyingFromPane)
            {
                return;
            }

            ShowAnalysisInPane();

            if (_engine != null)
            {
                _analysisSettle.Stop();
                _analysisSettle.Start();
            }
        }

        private async void OnAnalysisSettled(object sender, EventArgs e)
        {
            _analysisSettle.Stop();

            if (_activeFrontEnd != null && _engine != null)
            {
                await StartMeasurementAsync().ConfigureAwait(true);
            }
        }

        /// <summary>
        /// Writes the analysis settings into the settings pane's controls.
        /// </summary>
        /// <remarks>
        /// Guarded, because setting a text box raises its changed handler and an unguarded round
        /// trip would write the value straight back into the settings it just read.
        /// </remarks>
        private void ShowAnalysisInPane()
        {
            _applyingFromPane = true;

            try
            {
                CentreBox.Text = EngineeringText.Frequency(_analysis.CenterFrequencyHz);
                SpanBox.Text = EngineeringText.Frequency(_analysis.SpanHz);
                ResolutionBandwidthBox.Text =
                    EngineeringText.Frequency(_analysis.ResolutionBandwidthHz);

                PointsBox.SelectedItem = _analysis.PointsAreAutomatic
                    ? PointsBox.Items[0]
                    : FindPointsChoice(_analysis.FrequencyPoints) ?? PointsBox.SelectedItem;

                WindowBox.SelectedIndex = IndexOfWindow(_analysis.Window);
            }
            finally
            {
                _applyingFromPane = false;
            }
        }

        /// <summary>
        /// Reads the settings pane's controls into the analysis settings.
        /// </summary>
        /// <returns>Whether every control held something the settings would accept.</returns>
        /// <remarks>
        /// <para>
        /// One batch, so a pane holding five changed values costs one change notification and one
        /// re-plan rather than five of each.
        /// </para>
        /// <para>
        /// Internal rather than private because this is what pressing Apply does, and a test about
        /// what a measurement is set to has to be able to set it the way a user does. Making it
        /// public would put a settings-pane implementation detail in the product's surface, which is
        /// the same trade <see cref="RangeSettingsFor"/> makes.
        /// </para>
        /// </remarks>
        internal bool ReadPaneIntoAnalysis()
        {
            double centre;
            double span;
            double resolutionBandwidth;

            if (!EngineeringText.TryParseFrequency(CentreBox.Text, out centre))
            {
                SettingsMessage.Text = "Centre frequency: '" + CentreBox.Text + "' is not a frequency.";
                return false;
            }

            if (!EngineeringText.TryParseFrequency(SpanBox.Text, out span))
            {
                SettingsMessage.Text = "Span: '" + SpanBox.Text + "' is not a frequency.";
                return false;
            }

            if (!EngineeringText.TryParseFrequency(
                    ResolutionBandwidthBox.Text, out resolutionBandwidth))
            {
                resolutionBandwidth = _analysis.ResolutionBandwidthHz;
            }

            _applyingFromPane = true;

            try
            {
                using (_analysis.Batch())
                {
                    _analysis.CenterFrequencyHz = centre;
                    _analysis.SpanHz = span;

                    if (resolutionBandwidth > 0.0)
                    {
                        _analysis.ResolutionBandwidthHz = resolutionBandwidth;
                    }

                    int points = SelectedPoints();

                    _analysis.PointsAreAutomatic = points == 0;

                    if (points > 0 && AnalysisSettings.FrequencyPointsAreSupported(points))
                    {
                        _analysis.FrequencyPoints = points;
                    }

                    _analysis.Window = SelectedWindow();
                }
            }
            catch (ArgumentOutOfRangeException refused)
            {
                SettingsMessage.Text = refused.Message.Split('\n')[0];
                return false;
            }
            finally
            {
                _applyingFromPane = false;
            }

            return true;
        }

        /// <summary>
        /// Opens Display Preferences: modeless, live, five tabs (<c>REQ-UI-070</c>,
        /// <c>REQ-UI-073</c>).
        /// </summary>
        /// <remarks>
        /// <para>
        /// Shown rather than shown modally, so the measurement keeps updating behind it and the hot
        /// spots stay usable — which is what <c>REQ-UI-070</c> requires and what makes choosing a
        /// grid colour against the live grid possible at all.
        /// </para>
        /// <para>
        /// The preferences are written when the dialog closes rather than on every slider movement.
        /// A colour dragged through two hundred intermediate values would otherwise be two hundred
        /// writes of a file nobody is reading.
        /// </para>
        /// </remarks>
        private void OnDisplayPreferences(object sender, RoutedEventArgs e)
        {
            if (_preferences != null)
            {
                _preferences.Activate();
                return;
            }

            var dialog = new DisplayPreferencesDialog(
                _dialogOptions, _colours, _fonts, _traceDisplay, _spectrogramMap, _themes);

            // REQ-UI-083: chosen here, installed by the shell. The page knows the names; only one
            // place knows how a theme is applied.
            dialog.Window.ThemeChosen = name =>
            {
                ThemeName = name;
                dialog.Window.FollowTheme();

                StatusText.Content = "Theme: " + _themes.CurrentName;
            };

            dialog.ColoursChanged += (s, args) => ApplyColours();
            dialog.FontsChanged += (s, args) => ApplyFonts();

            dialog.SpectrogramMapChanged += (s, args) =>
            {
                _spectrogramMap = dialog.SpectrogramMap;
                BuildSpectrogramMapMenu();
                FollowSpectrogramMap();
            };

            dialog.Closed += (s, args) =>
            {
                _preferences = null;
                SaveToolWindowLayout();

                _eventLog.Append(
                    "Display Preferences closed; " + _colours.ChangedCount +
                    " colour(s) and " + _fonts.ChangedCount +
                    " font slot(s) differ from their defaults.");
            };

            _preferences = dialog;
            dialog.ShowModeless(this);
        }

        /// <summary>
        /// Brings every plot into line with the chosen accumulator (<c>REQ-UI-054</c>).
        /// </summary>
        /// <remarks>
        /// <para>
        /// <strong>Changing the accumulator discards the history, and that is
        /// <c>REQ-TRC-001a</c>'s rule rather than housekeeping</strong> — rows of spectra are not
        /// rows of a persistence map, and carrying them across would present one mode's data under
        /// another mode's name. Changing the <em>format</em> discards nothing, which is why the
        /// two settings are separate in the first place.
        /// </para>
        /// <para>
        /// The threshold, the enhancement and the colour map are pushed on every call rather than
        /// only when the accumulator changes: they are display settings over the same history, and
        /// a plot opened after one was set would otherwise draw the map the others have stopped
        /// using.
        /// </para>
        /// </remarks>
        private void ApplyAccumulator()
        {
            TraceAccumulator chosen = _analysis.Accumulator;

            bool changed = chosen != _appliedAccumulator;

            if (changed)
            {
                _appliedAccumulator = chosen;
                _spectrogramHistory.Clear();
            }

            foreach (char letter in Documents.Traces)
            {
                TracePlot plot = Documents.PlotOf(letter);

                if (plot == null)
                {
                    continue;
                }

                plot.History = _spectrogramHistory;
                plot.SpectrogramMap = _spectrogramMap;
                plot.SpectrogramThresholdBelowTopDb = _spectrogramThresholdBelowTopDb;
                plot.SpectrogramEnhance = _spectrogramEnhance;
                plot.Accumulator = chosen;

                // Per trace, because REQ-UI-022 makes both markers per-trace colours and so
                // per-trace things. A frequency held from a discarded history would place the
                // marker against an axis that no longer exists.
                if (changed && plot.SpectrogramMarkers != null)
                {
                    plot.SpectrogramMarkers.Clear();
                }
            }
        }

        /// <summary>
        /// Opens the toolbar customiser of <c>REQ-UI-064</c> (Utilities ▸ Toolbars…).
        /// </summary>
        /// <remarks>
        /// One at a time, and modeless like every other settings dialog: a second window over the
        /// same arrangement would be two lists that disagree about what is on a toolbar the moment
        /// either is used.
        /// </remarks>
        private void OnToolbarCustomiser(object sender, RoutedEventArgs e)
        {
            if (_customiser != null)
            {
                _customiser.Activate();
                return;
            }

            var dialog = new ToolbarCustomiserDialog(_dialogOptions, ToolbarArrangement);

            dialog.Closed += (s, args) =>
            {
                _customiser = null;
                SaveToolWindowLayout();

                _eventLog.Append(
                    "Toolbars closed; the arrangement is " +
                    (ToolbarArrangement.IsDefault
                        ? "REQ-UI-063's default."
                        : "customised (" + ToolbarArrangement.Bars.Count + " toolbars)."));
            };

            _customiser = dialog;
            dialog.ShowModeless(this);
        }

        /// <summary>
        /// Pushes the chosen font slots onto the surfaces that draw from them (<c>REQ-UI-080</c>).
        /// </summary>
        /// <remarks>
        /// Annotation reaches every trace window; Marker reaches the Markers window and nothing
        /// else. Tabular has no surface yet — the symbol table and error summary of
        /// <c>REQ-UI-052</c> do not exist — so it is settable and persisted but draws nothing, which
        /// is said here rather than left to be discovered.
        /// </remarks>
        private void ApplyFonts()
        {
            foreach (char trace in Documents.Traces)
            {
                TracePlot plot = Documents.PlotOf(trace);

                if (plot != null)
                {
                    plot.ApplyFonts(_fonts);
                }
            }

            if (_toolWindows != null)
            {
                _toolWindows.ApplyFonts(_fonts);
            }
        }

        /// <summary>
        /// Pushes the chosen colours onto every plot.
        /// </summary>
        /// <remarks>
        /// The palette carries the handful of colours the rasteriser draws with; the limit colours
        /// carry the four of <c>REQ-UI-023</c>. Both come from the same preference set, so a colour
        /// changed in the picker cannot reach one and not the other.
        /// </remarks>
        /// <summary>
        /// Pushes the colours onto every surface again (<c>REQ-UI-014</c>).
        /// </summary>
        /// <remarks>
        /// What the colour picker calls when a colour changes, and what a test calls to check a
        /// surface took it. Public because the criterion is that a change reaches the display, and
        /// a test that could only set the preference would be asserting the preference.
        /// </remarks>
        public void RefreshColours() => ApplyColours();

        private void ApplyColours()
        {
            var limits = new LimitColours
            {
                Limit = _colours.ColourOf("Limit"),
                Margin = _colours.ColourOf("Margin"),
                FailLimit = _colours.ColourOf("Fail Limit"),
                FailMargin = _colours.ColourOf("Fail Margin"),
                IndicateFailures = _traceDisplay.IndicateLimitFailures,
                IndicateMargin = _traceDisplay.IndicateMarginWarnings,
            };

            // Per trace, because Trace is a per-trace element in REQ-UI-022 and the rest are not.
            // Building one palette for every plot would give trace B trace A's colour, which is the
            // one thing REQ-UI-021 exists to prevent.
            foreach (char trace in Documents.Traces)
            {
                TracePlot plot = Documents.PlotOf(trace);

                if (plot == null)
                {
                    continue;
                }

                plot.Palette = new PlotPalette(
                    _colours.ColourOf("Trace Background"),
                    _colours.ColourOf("Grid"),
                    _colours.ColourOf("Annotation"),
                    _colours.ColourOf("Annotation Background"),
                    TraceColourOf(trace),
                    _colours.ColourOf("Selected Marker"),
                    _colours.ColourOf("Not Selected Marker"),
                    _colours.ColourOf("Indicator"));

                plot.LimitColours = limits;

                // REQ-UI-022 lists both spectrogram markers among the per-trace elements, so they
                // are looked up per trace like the trace colour itself rather than taken from the
                // palette, which carries the global ones.
                plot.SpectrogramMarkerColour = PerTraceColourOf("SpectrogramMarker", trace);
                plot.TraceSelectColour = PerTraceColourOf("TraceSelect", trace);
            }

            // REQ-UI-032: the Markers window has a background colour of its own, and this is the
            // surface REQ-UI-022's Marker Window Background element exists for.
            if (_toolWindows != null)
            {
                _toolWindows.ApplyColours(_colours);
            }
        }

        /// <summary>
        /// One per-trace themed colour, or the plot's own default when the picker has no entry.
        /// </summary>
        /// <remarks>
        /// The same fall-through <see cref="TraceColourOf"/> makes, and for the same reason: the
        /// picker covers the trace table's twenty letters, and a twenty-first trace has no entry of
        /// its own rather than being an error.
        /// </remarks>
        private PlotColor PerTraceColourOf(string element, char trace)
        {
            string key = "OpenVSA." + element + "." + trace;

            return _colours.Find(key) != null
                ? _colours.Colour(key)
                : TraceColours.ForTrace(trace);
        }

        /// <summary>
        /// One trace's colour: the user's choice for it, or the trace table's.
        /// </summary>
        /// <remarks>
        /// The picker covers the trace table's twenty entries. A twenty-first trace re-uses a
        /// colour by the table's own design (<c>REQ-UI-021</c>), so it has no picker entry of its
        /// own and falls through to the table rather than throwing.
        /// </remarks>
        private PlotColor TraceColourOf(char trace)
        {
            string key = "OpenVSA.Trace." + trace;

            return _colours.Find(key) != null
                ? _colours.Colour(key)
                : TraceColours.ForTrace(trace);
        }

        /// <summary>
        /// The Display menu's limit-indication items, writing the same state the Trace tab does.
        /// </summary>
        /// <remarks>
        /// The menu item does not hold the setting; it sets it. <see cref="TraceDisplayOptions"/>
        /// does, and both surfaces follow its change event — <c>REQ-UI-070</c>'s "each surface
        /// reflects a change made from the other", which is only true if neither surface is the
        /// state.
        /// </remarks>
        private void OnLimitIndicationChanged(object sender, RoutedEventArgs e) =>
            OnTraceDisplayItemChanged(sender, e);

        /// <summary>
        /// The Display menu's trace items, writing the same state the Trace tab does.
        /// </summary>
        private void OnTraceDisplayItemChanged(object sender, RoutedEventArgs e)
        {
            if (_menuFollowing)
            {
                return;
            }

            // Show annotation and Show grid lines are not here: REQ-UI-061's Trace menu does not
            // list them, so they live on the Trace tab of Display Preferences alone. Both surfaces
            // wrote this same object, and the tab still does.
            _traceDisplay.IndicateLimitFailures = _indicateFailuresItem.IsChecked;
            _traceDisplay.IndicateMarginWarnings = _indicateMarginItem.IsChecked;
            _traceDisplay.ForceWhiteBackgroundOnPrint = _forceWhiteBackgroundItem.IsChecked;
        }

        /// <summary>Whether the Display menu is being updated from the options, not by the user.</summary>
        private bool _menuFollowing;

        /// <summary>
        /// Brings the Display menu's check marks into line with the options.
        /// </summary>
        /// <remarks>
        /// Guarded, because setting <c>IsChecked</c> raises the click handler's sibling events and
        /// an unguarded round trip would write the value back to the options it just read.
        /// </remarks>
        private void FollowTraceDisplayOptions()
        {
            _menuFollowing = true;

            try
            {
                _indicateFailuresItem.IsChecked = _traceDisplay.IndicateLimitFailures;
                _indicateMarginItem.IsChecked = _traceDisplay.IndicateMarginWarnings;
                _forceWhiteBackgroundItem.IsChecked = _traceDisplay.ForceWhiteBackgroundOnPrint;
            }
            finally
            {
                _menuFollowing = false;
            }

            ApplyColours();
            ApplyTraceDisplay();

            _eventLog.Append(
                "Trace display: " + _traceDisplay + ".");
        }

        /// <summary>
        /// Pushes the annotation, graticule and reference settings onto every plot
        /// (<c>REQ-UI-011</c>, <c>REQ-UI-012</c>, <c>REQ-UI-013</c>).
        /// </summary>
        /// <remarks>
        /// Every open trace, not just the active one. These are display preferences, not per-trace
        /// settings, and a graticule that changed its division count on one window out of four would
        /// make two traces of one acquisition incomparable by eye.
        /// </remarks>
        private void ApplyTraceDisplay()
        {
            foreach (char trace in Documents.Traces)
            {
                TracePlot plot = Documents.PlotOf(trace);

                if (plot != null)
                {
                    plot.ApplyDisplayOptions(_traceDisplay);
                }
            }
        }

        /// <summary>Where the display sidecar carrying the tool-window layout lives.</summary>
        private static string ToolWindowLayoutPath =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "OpenVSA",
                "layout" + SidecarState.PreferencesExtension);

        /// <summary>The front end currently selected, or null if none has been chosen.</summary>
        public IFrontEnd ActiveFrontEnd => _activeFrontEnd;

        /// <summary>The running measurement, or null if none is running.</summary>
        public SpectrumEngine Engine => _engine;

        /// <summary>
        /// Opens the connection dialog for a front end that needs an address (<c>REQ-HAL-003</c>).
        /// </summary>
        /// <param name="frontEnd">The front end to point at an address.</param>
        /// <returns>Whether an address was chosen.</returns>
        /// <remarks>
        /// Replaceable so that shell tests can drive the choice without a modal window; the default
        /// shows <see cref="ConnectionDialog"/>. A modal dialog in an automated run blocks the
        /// dispatcher until something dismisses it, and nothing will.
        /// </remarks>
        internal Func<IRequiresResource, bool> ChooseResourceForTest { get; set; }

        private Task<bool> ChooseResourceAsync(IRequiresResource frontEnd)
        {
            if (ChooseResourceForTest != null)
            {
                return Task.FromResult(ChooseResourceForTest(frontEnd));
            }

            var dialog = new ConnectionDialog(
                () => _registry.DiscoverResources(CancellationToken.None),
                _registry.CanEnumerateResources)
            {
                Owner = this,
            };

            // The address it already has, pre-selected. Usually the configured one, and usually
            // right — reopening the dialog to change something else should not lose it.
            dialog.Select(frontEnd.ResourceName);

            var chosen = new TaskCompletionSource<bool>();

            // Modeless, per REQ-UI-070: nothing the shell puts up may stop the measurement
            // updating, and a bus scan is the worst thing to freeze it behind — thirty GPIB
            // addresses at 700 ms each is twenty seconds of a window that appears to have hung.
            // Awaiting the close keeps this method reading like the modal version it replaces
            // without any of the blocking.
            dialog.Closed += (sender, e) =>
            {
                if (dialog.ChosenResource != null)
                {
                    frontEnd.UseResource(dialog.ChosenResource);
                }

                chosen.TrySetResult(dialog.ChosenResource != null);
            };

            dialog.Show();

            return chosen.Task;
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

            // REQ-HAL-003: a front end that needs an address asks for one, through the connection
            // dialog, before anything is connected or the previous front end is torn down. Asking
            // first means Cancel leaves the shell exactly as it was rather than disconnected from
            // what it had.
            if (created is IRequiresResource needsResource &&
                !await ChooseResourceAsync(needsResource).ConfigureAwait(true))
            {
                created.Dispose();
                clicked.IsChecked = false;
                return;
            }

            await StopAcquisitionAsync().ConfigureAwait(true);

            if (_activeFrontEnd != null)
            {
                _activeFrontEnd.Dispose();
            }

            _activeFrontEnd = null;
            SetStartEnabled(false);
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
            SetStartEnabled(true);

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
        internal void RangeSettingsFor(IFrontEndCapabilities capabilities)
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
            if (_activeFrontEnd == null || !ReadPaneIntoAnalysis())
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

        /// <summary>The instrument items, so that choosing one unticks the rest.</summary>
        private IEnumerable<MenuItem> SourceMenuItems() =>
            _instrumentsMenu == null
                ? Enumerable.Empty<MenuItem>()
                : _instrumentsMenu.Items.OfType<MenuItem>();

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
                : "Choose an instrument under Hardware ▸ Instruments…, " +
                  "then Acquisition ▸ Control ▸ Start.\n\n" +
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

            // The averaging the Average tab and the state carry. Set here rather than left null:
            // AnalysisSettings has offered averaging since REQ-UI-072 and nothing was applying it,
            // so a measurement ran unaveraged whatever the dialog said — which is also what left
            // REQ-UI-063's Restart with nothing to discard.
            engine.Averager = _analysis.Averaging == AveragingType.Off
                ? null
                : new TraceAverager(_analysis.Averaging, _analysis.AverageCount)
                {
                    RepeatAverage = _analysis.RepeatAverage,
                };

            engine.FrameComputed += OnFrameComputed;
            engine.BlockAcquired += OnBlockAcquired;
            engine.Faulted += OnEngineFaulted;
            engine.Completed += OnEngineCompleted;

            AcquisitionPlan plan;

            try
            {
                plan = await engine.StartAsync(planned.Request, CancellationToken.None)
                    .ConfigureAwait(true);

                _negotiatedPlan = plan;
            }
            catch (Exception failure)
            {
                engine.Dispose();
                StatusText.Content = "Could not start";
                PlanText.Text = failure.Message;
                return;
            }

            _engine = engine;

            // REQ-DAT-010: every context but the active one is analysed from the blocks this session
            // acquires, so two contexts are live against one capture rather than two acquisitions
            // taken a moment apart. The active one is the engine's own inline analysis, which is what
            // FrameComputed above publishes -- naming it here is what stops its transform being done
            // twice.
            _contextAnalyser.Primary = _contextSet.Active;
            _contextAnalyser.Attach(engine);

            // The panel, not just its text: the panel carries the background that keeps the
            // guidance legible over a trace, so hiding only the text would leave a dark rectangle
            // floating over the arrangement.
            DocumentPlaceholderPanel.Visibility = Visibility.Collapsed;
            SetStartEnabled(false);
            SetStopEnabled(true);
            StatusText.Content = "Measuring";

            SettingsMessage.Text = planned.Coerced || plan.Coerced
                ? "Some settings were coerced — see the negotiated plan."
                : "Res BW " + EngineeringText.Frequency(planned.ResolutionBandwidthHz) +
                  ", time record " + EngineeringText.Time(planned.MaxTimeSeconds);

            LogCoercions(plan, planned);
            PlanText.Text = PlanSummary.Describe(plan, planned, _activeFrontEnd.Capabilities);
            _statusTimer.Start();
        }

        /// <summary>
        /// Writes every coercion to the event log, one entry each.
        /// </summary>
        /// <param name="plan">The negotiated plan.</param>
        /// <param name="planned">The planned acquisition.</param>
        /// <remarks>
        /// <para>
        /// <c>REQ-ARC-002</c>: "each coercion raises a user-visible event-log entry". One entry per
        /// coercion, not a summary — the settings pane already says <em>that</em> something was
        /// coerced, and a reader who wants to know <em>what</em> should not have to reconstruct it
        /// from a plan readout that changes on the next Apply.
        /// </para>
        /// <para>
        /// This matters most on a front-end change, which is what the requirement is about: a
        /// recording cannot be retuned and an instrument has its own limits, so switching source
        /// silently rewrites settings the user chose. The event log is where that becomes
        /// answerable afterwards rather than only visible at the moment it happens.
        /// </para>
        /// </remarks>
        private void LogCoercions(AcquisitionPlan plan, PlannedAcquisition planned)
        {
            if (plan != null)
            {
                foreach (ParameterCoercion coercion in plan.Coercions)
                {
                    _eventLog.Append(Describe(coercion, _activeFrontEnd?.DisplayName));
                }
            }

            if (planned != null)
            {
                foreach (ParameterCoercion coercion in planned.Coercions)
                {
                    _eventLog.Append(Describe(coercion, _activeFrontEnd?.DisplayName));
                }
            }
        }

        /// <summary>Test seam for <see cref="Describe"/>.</summary>
        /// <param name="coercion">The coercion.</param>
        /// <param name="source">The front end that imposed it, or null.</param>
        /// <remarks>
        /// Internal rather than making Describe itself internal: the wording is what the
        /// requirement is about, and it should be assertable without constructing a plan, a front
        /// end and a running measurement to get one produced.
        /// </remarks>
        internal static string DescribeCoercionForTest(ParameterCoercion coercion, string source) =>
            Describe(coercion, source);

        /// <summary>One coercion, in words that name the source that imposed it.</summary>
        private static string Describe(ParameterCoercion coercion, string source)
        {
            return (string.IsNullOrEmpty(source) ? "The source" : source) +
                " coerced " + coercion.Parameter + ": requested " +
                coercion.Requested.ToString("G6", CultureInfo.CurrentCulture) + ", honoured " +
                coercion.Honoured.ToString("G6", CultureInfo.CurrentCulture) +
                " — " + coercion.Reason;
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
                    "ranged. Re-select it under Hardware ▸ Instruments… to connect again.");
            }

            // From the analysis settings, not from the entry boxes. Those are one surface over
            // this state and the Analysis dialog is another; planning from either surface directly
            // would mean the measurement followed whichever one had been touched last.
            double centre = _analysis.CenterFrequencyHz;

            if (!capabilities.CenterFrequencyRange.Contains(centre))
            {
                return Reject(
                    "Centre frequency is outside this front end's range of " +
                    EngineeringText.Frequency(capabilities.CenterFrequencyRange.MinHz) + " to " +
                    EngineeringText.Frequency(capabilities.CenterFrequencyRange.MaxHz) + ".");
            }

            double span = _analysis.SpanHz;

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

            WindowType window = _analysis.Window;
            AnalysisPath path = _analysis.Path;

            try
            {
                if (_analysis.PointsAreAutomatic)
                {
                    return AcquisitionPlanner.PlanForResolutionBandwidth(
                        capabilities, centre, span, _analysis.ResolutionBandwidthHz, level,
                        path, window);
                }

                return AcquisitionPlanner.Plan(
                    capabilities, centre, span, _analysis.FrequencyPoints, level, path, window);
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

            // REQ-NFR-002: the shell keeps the newest frame for markers, limits and annotation
            // until the next one replaces it, so it holds a share of its own and gives up the
            // previous one. Every other holder below -- the plots, the spectrogram history, the
            // marker collection -- takes its own share as it stores, so none of them depends on
            // this one outliving them.
            SpectrumFrame previousFrame = _frame;

            snapshot.Spectrum?.Retain();
            _frame = snapshot.Spectrum;

            if (!ReferenceEquals(previousFrame, _frame))
            {
                previousFrame?.Release();
            }

            // REQ-UI-054: the history a spectrogram draws is the sweeps that reached the display.
            // Accumulating on the pump thread instead would record the ones the marshal coalesced
            // away as well, and would put a lock between the acquisition and the rasteriser — a
            // display's history is what was displayed, and REQ-NFR-012's dropped-frame count is
            // what says when the two differ.
            if (_analysis.Accumulator == TraceAccumulator.Spectrogram)
            {
                _spectrogramHistory.Add(snapshot.Spectrum);
            }

            // REQ-LIM-003: one evaluation per frame, published whole, read by the display and by
            // the API. Offered here rather than inside the plot so that a shell with four trace
            // windows open evaluates once rather than four times.
            if (Limits.Test != null)
            {
                Limits.Offer(snapshot.Spectrum);
                ShowLimitVerdict();
            }

            if (Plot.Show(snapshot))
            {
                RefreshMarkers();
            }

            // Every open trace gets the same snapshot, and each draws it in its own format.
            // REQ-TRC-001's rule made visible: one computation, several views, nothing recomputed
            // — the four trace windows of a Grid 2×2 are four renderings of a single acquisition.
            foreach (char letter in Documents.Traces)
            {
                TracePlot plot = Documents.PlotOf(letter);

                if (plot != null && !ReferenceEquals(plot, Plot))
                {
                    plot.Show(snapshot);
                }
            }

            UpdateIndicators(snapshot);

            // The share TakeForRender handed over. Given up last, after every holder above has
            // taken its own -- releasing earlier would hand the buffer back while the plots were
            // still reading it, which the lease would report as an ObjectDisposedException rather
            // than a wrong trace, but a fault either way.
            snapshot.Release();
        }

        /// <summary>
        /// Refreshes the conditions annotated inside the grid (<c>REQ-UI-041</c>,
        /// <c>REQ-UI-007</c>).
        /// </summary>
        /// <remarks>
        /// <para>
        /// <strong>Every condition this stage can observe, and none it cannot.</strong>
        /// <c>REQ-UI-007</c> names seven that invalidate a measurement — ADC overload, unlocked
        /// reference, uncalibrated state, demodulation lock failure, sync not found, pulse not
        /// found and dropped frames. The first, second, third and last are observable here and are
        /// set here. The three demodulation ones belong to a demodulator and are set by
        /// <see cref="SetDemodulationIndicator"/> when one reports; nothing here fabricates a state
        /// it cannot see, which would be worse than not showing it.
        /// </para>
        /// <para>
        /// Set on every frame rather than only on a change, because these are conditions rather
        /// than events: an overload that persists has to stay on screen, and one that clears has to
        /// come off within a display update.
        /// </para>
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

            // REQ-UI-007's own list, each from the condition it names.
            _indicators.SetActive(TraceIndicator.Overload, IsOverloaded(snapshot));
            _indicators.SetActive(TraceIndicator.ReferenceUnlocked, IsReferenceUnlocked);
            _indicators.SetActive(TraceIndicator.CalibrationQuestionable, IsCalibrationQuestionable);
            _indicators.SetActive(TraceIndicator.DroppedFrames, _marshal.FramesDropped > 0);

            foreach (char letter in Documents.Traces)
            {
                TracePlot plot = Documents.PlotOf(letter);

                if (plot != null)
                {
                    plot.SetIndicators(_indicators);
                }
            }
        }

        /// <summary>
        /// Whether the frame shows an overloaded input (<c>REQ-UI-007</c>'s <c>OVx</c>).
        /// </summary>
        /// <remarks>
        /// A frame at or above the reference level has run out of headroom, which is what an
        /// overload is from the analysis side: the converter clipped and the number on screen is
        /// smaller than the signal that produced it.
        /// </remarks>
        private static bool IsOverloaded(TraceSnapshot snapshot)
        {
            ReadOnlySpan<float> levels = snapshot.Spectrum.LevelsDbm;

            for (int i = 0; i < levels.Length; i++)
            {
                if (levels[i] >= snapshot.Spectrum.ReferenceLevelDbm)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Whether the external frequency reference is asked for and not locked
        /// (<c>REQ-UI-007</c>).
        /// </summary>
        /// <remarks>
        /// Only when it is asked for. An instrument running on its internal reference is not
        /// "unlocked" — it is doing what it was told, and an indicator raised on every internally
        /// referenced measurement would be one nobody reads.
        /// </remarks>
        public bool IsReferenceUnlocked { get; private set; }

        /// <summary>
        /// Whether the calibration in force may not apply (<c>REQ-UI-007</c>'s <c>CAL?</c>).
        /// </summary>
        public bool IsCalibrationQuestionable { get; private set; }

        /// <summary>
        /// Sets one of the conditions only a demodulator can observe (<c>REQ-UI-007</c>).
        /// </summary>
        /// <param name="indicator">Carrier lock, sync or pulse.</param>
        /// <param name="active">Whether the condition holds.</param>
        /// <exception cref="ArgumentOutOfRangeException">Not one a demodulator reports.</exception>
        /// <remarks>
        /// The seam a demodulator will report through, and public so the criterion — "each listed
        /// condition is provoked in turn ... and each raises its REQ-UI-041 string in the trace's
        /// upper-right corner" — can be exercised before there is one. Restricted to the three it
        /// owns, so it cannot become a back door for setting conditions the shell is meant to
        /// observe for itself.
        /// </remarks>
        public void SetDemodulationIndicator(TraceIndicator indicator, bool active)
        {
            if (indicator != TraceIndicator.CarrierLock &&
                indicator != TraceIndicator.SyncNotFound &&
                indicator != TraceIndicator.PulseNotFound)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(indicator), indicator,
                    "Only carrier lock, sync and pulse are a demodulator's to report; the rest " +
                    "are observed by the shell.");
            }

            _indicators.SetActive(indicator, active);
            RefreshIndicators();
        }

        /// <summary>
        /// Sets the two hardware conditions of <c>REQ-UI-007</c> the front end reports.
        /// </summary>
        /// <param name="referenceUnlocked">Whether an external reference was asked for and is not locked.</param>
        /// <param name="calibrationQuestionable">Whether the calibration may not apply.</param>
        public void SetHardwareIndicators(bool referenceUnlocked, bool calibrationQuestionable)
        {
            IsReferenceUnlocked = referenceUnlocked;
            IsCalibrationQuestionable = calibrationQuestionable;

            _indicators.SetActive(TraceIndicator.ReferenceUnlocked, referenceUnlocked);
            _indicators.SetActive(TraceIndicator.CalibrationQuestionable, calibrationQuestionable);

            RefreshIndicators();
            ShowStatusFields();
        }

        /// <summary>Pushes the current indicator set onto every trace window.</summary>
        private void RefreshIndicators()
        {
            foreach (char letter in Documents.Traces)
            {
                TracePlot plot = Documents.PlotOf(letter);

                if (plot != null)
                {
                    plot.SetIndicators(_indicators);
                }
            }
        }

        /// <summary>The conditions currently annotated on the traces (<c>REQ-UI-007</c>).</summary>
        public TraceIndicators Indicators => _indicators;

        // ---- The status bar's fields (REQ-UI-006) -------------------------------------------------
        //
        // Exposed so a test can assert what the bar shows and where, rather than a description of
        // it. The placement of the first is the requirement — "measurement status messages
        // specifically at the bottom left" is quoted from the reference product.

        /// <summary>The measurement-status field, which is the leftmost (<c>REQ-UI-006</c>).</summary>
        public System.Windows.Controls.Primitives.StatusBarItem StatusItem => StatusText;

        /// <summary>The calibration-status field.</summary>
        public System.Windows.Controls.Primitives.StatusBarItem CalibrationItem => CalibrationText;

        /// <summary>The external-reference lock field.</summary>
        public System.Windows.Controls.Primitives.StatusBarItem ReferenceItem => ReferenceText;

        /// <summary>The spectrum-rate field.</summary>
        public System.Windows.Controls.Primitives.StatusBarItem RateItem => RateText;

        /// <summary>The measured transfer rate and duty cycle (<c>REQ-NFR-027</c>).</summary>
        public System.Windows.Controls.Primitives.StatusBarItem TransferItem => TransferText;

        /// <summary>The dropped-frame count (<c>REQ-NFR-012</c>).</summary>
        public System.Windows.Controls.Primitives.StatusBarItem DroppedItem => DroppedText;

        /// <summary>The preview-features-in-use field.</summary>
        public System.Windows.Controls.Primitives.StatusBarItem PreviewItem => PreviewText;

        // ---- State, presets and their exclusions ------------------------------------------------

        /// <summary>The name this shell's first measurement context is given (<c>REQ-STA-004</c>).</summary>
        private const string ContextName = MeasurementContextSet.DefaultName;

        /// <summary>
        /// Every context's setup, as a saveable state (<c>REQ-STA-001</c>, <c>REQ-DAT-010</c>).
        /// </summary>
        /// <remarks>
        /// <para>
        /// All the contexts, not just the one on screen. A session with two contexts whose state file
        /// carried one would recall as a session with one measurement configured and one left at
        /// whatever it happened to be — the partial application <c>REQ-STA-004</c> exists to prevent,
        /// arriving through the save instead of through the recall.
        /// </para>
        /// <para>
        /// The active context's setup is read out of the controls first, so what is saved is what is
        /// on screen. The others were read out of the controls when they were last active, which is
        /// the same thing a moment earlier.
        /// </para>
        /// </remarks>
        public ApplicationState CaptureState()
        {
            _contextSet.Active.Setup = CaptureActiveSetup();

            return _contextSet.Capture();
        }

        /// <summary>
        /// The settings pane and plot, expressed as one context's setup (<c>REQ-STA-001</c>).
        /// </summary>
        /// <remarks>
        /// Read from the controls rather than from a parallel model, so what is saved is what is on
        /// screen. A second copy of the settings kept alongside the pane would be one more thing to
        /// keep in step, and the failure would be silent: a state that saved a frequency the user
        /// had changed and not applied.
        /// </remarks>
        private MeasurementState CaptureActiveSetup()
        {
            ApplicationState state = ApplicationState.Default(_contextSet.Active.Name);
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

            // Averaging, detectors, noise correction, gating, overlap and the transform ceiling.
            // AnalysisSettings has carried LoadFrom and SaveInto since REQ-UI-072 and the state
            // path never called either, so a saved setup came back with none of them - a hole in
            // REQ-STA-001 that only showed when something needed to set averaging from a state.
            _analysis.SaveInto(measurement);

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
                    IsVisible = marker.IsVisible,
                });
            }

            return measurement;
        }

        /// <summary>
        /// Applies a recalled measurement to the settings pane and the plot.
        /// </summary>
        /// <param name="measurement">The recalled settings.</param>
        /// <exception cref="ArgumentNullException"><paramref name="measurement"/> is null.</exception>
        public void ApplyState(MeasurementState measurement) =>
            ApplySettings(measurement, restoreMarkers: true);

        /// <summary>
        /// Applies a measurement's settings to the pane and the plot.
        /// </summary>
        /// <param name="measurement">The settings to apply.</param>
        /// <param name="restoreMarkers">
        /// Whether to rebuild the marker set from the state. False on a context switch: the incoming
        /// context's markers are live objects it already owns, and rebuilding them from its last
        /// saved setup would discard anything placed since — silently, and only for the user who had
        /// switched away and back.
        /// </param>
        /// <exception cref="ArgumentNullException"><paramref name="measurement"/> is null.</exception>
        private void ApplySettings(MeasurementState measurement, bool restoreMarkers)
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

            // One change notification for the lot, so a recall costs one re-plan rather than one
            // per setting.
            using (_analysis.Batch())
            {
                _analysis.LoadFrom(measurement);
            }

            FollowAccumulator();

            if (measurement.Traces.Count > 0)
            {
                Plot.FormatHotSpot.Value.TrySet(
                    TraceFormatText.Describe(measurement.Traces[0].Format));
                Plot.FormatHotSpot.Refresh();
            }

            if (!restoreMarkers)
            {
                RefreshMarkers();
                return;
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
            Marker restored;

            if (string.Equals(marker.Type, "Fixed", StringComparison.Ordinal))
            {
                restored = _markers.AddFixed(marker.XHz, marker.YDbm);
            }
            else if (string.Equals(marker.Type, "Delta", StringComparison.Ordinal) &&
                     reference != null)
            {
                restored = _markers.AddDelta(marker.XHz, reference);
            }
            else
            {
                restored = _markers.AddNormal(marker.XHz);
            }

            if (restored != null)
            {
                restored.IsVisible = marker.IsVisible;
            }
        }

        /// <summary>
        /// Where a non-interactive Save Setup writes and Recall Setup reads, or <c>null</c>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <c>REQ-TST-008</c> asks for state save and recall to be driven "through the UI automation
        /// layer rather than by calling view models directly". Invoking the menu item is what
        /// exercises the routing; the file <em>picker</em> cannot be driven from inside the process
        /// that is showing it, because it is modal and blocks the dispatcher the automation is
        /// running on. So the routing runs and the path is nominated here.
        /// </para>
        /// <para>
        /// Internal rather than public: no caller outside this assembly needs it, and putting a test
        /// hook in the product's surface is the thing it is meant to avoid. The same trade
        /// <see cref="RangeSettingsFor"/> and <see cref="ReadPaneIntoAnalysis"/> make. It does
        /// nothing while <see cref="Interactive"/> is true, so a shipped shell always asks.
        /// </para>
        /// </remarks>
        internal string NonInteractiveStatePath { get; set; }

        private void OnSaveState(object sender, RoutedEventArgs e)
        {
            if (!Interactive)
            {
                // The picker belongs to the user, not to a test run. See ShellWindow.Interactive:
                // what is skipped is the dialog, never the routing.
                StatusText.Content = "Save setup";

                if (NonInteractiveStatePath != null)
                {
                    SaveStateTo(NonInteractiveStatePath);
                }

                return;
            }

            var dialog = new StateSaveDialog(SuggestedStatePath()) { Owner = this };

            if (dialog.ShowDialog() != true)
            {
                return;
            }

            SaveStateTo(dialog.Path);
        }

        /// <summary>
        /// Writes the session's state to a path, reporting a failure rather than throwing.
        /// </summary>
        /// <param name="path">Where to write.</param>
        private void SaveStateTo(string path)
        {
            try
            {
                StateFile.Save(CaptureState(), path);
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
            if (!Interactive)
            {
                // The picker belongs to the user, not to a test run. See ShellWindow.Interactive:
                // what is skipped is the dialog, never the routing.
                StatusText.Content = "Recall setup";

                if (NonInteractiveStatePath != null)
                {
                    RecallStateFrom(NonInteractiveStatePath);
                }

                return;
            }

            var picker = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "OpenVSA state (*" + StateFile.Extension + ")|*" + StateFile.Extension,
                Title = "Recall state",
            };

            if (picker.ShowDialog(this) != true)
            {
                return;
            }

            RecallStateFrom(picker.FileName);
        }

        /// <summary>
        /// Reads a state from a path and applies it, reporting a failure rather than throwing.
        /// </summary>
        /// <param name="path">Where to read from.</param>
        private void RecallStateFrom(string path)
        {
            try
            {
                Recall(StateFile.Load(path));
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

            // Read out of the controls first, so a state that does not name the active context
            // leaves it holding what is on screen rather than what it last happened to be assigned.
            _contextSet.Active.Setup = CaptureActiveSetup();

            try
            {
                _contextSet.Recall(state);
            }
            catch (ContextMismatchException mismatch)
            {
                // Reported rather than partially applied: the settings pane is untouched, because
                // nothing has been written to it yet.
                StatusText.Content = "State not recalled";
                SettingsMessage.Text = mismatch.Message;
                return;
            }

            // Every context has its recalled setup now; the pane shows the active one's. The others
            // are applied to the pane as they are activated, which is the only time a pane can show
            // them.
            ApplyState(_contextSet.Active.Setup);
            ShowContexts();
            SettingsMessage.Text = string.Empty;
        }

        private void OnFactoryPreset(object sender, RoutedEventArgs e)
        {
            // REQ-UI-061: the hardware setup is left alone, which is structural - a state carries
            // no front end, so applying one cannot disturb the connection.
            //
            // Named for the active context rather than for "Measurement 1": a factory preset is a
            // setup, not a state file, so it applies to whatever context is on screen. Recalling it
            // under a fixed name would refuse the whole thing the moment a context was renamed.
            Recall(Presets.Factory(_contextSet.Active.Name));
            StatusText.Content = "Factory preset";
        }

        private void OnSavePreset(object sender, RoutedEventArgs e)
        {
            if (!Interactive)
            {
                // The picker belongs to the user, not to a test run. See ShellWindow.Interactive:
                // what is skipped is the dialog, never the routing.
                StatusText.Content = "Save as preset";
                return;
            }

            var dialog = new StateSaveDialog("My preset") { Owner = this, Title = "Save as preset" };

            if (dialog.ShowDialog() != true)
            {
                return;
            }

            try
            {
                // The active context's setup, not the whole session: a preset is one measurement's
                // setup (REQ-STA-005), and one that carried every context would be refused by
                // REQ-STA-004's name matching in any session that did not have the same ones.
                _presets.Save(dialog.Path, ActiveContextState());
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
            // Back to the variants REQ-UI-061 lists, then the user's own below a rule. Counted
            // from the table rather than from a literal, because a variant added there and not
            // here would be quietly deleted every time the menu was opened.
            int listed = ShellMenuTable.At("File > Preset").Children.Count;

            while (_presetMenu.Items.Count > listed)
            {
                _presetMenu.Items.RemoveAt(listed);
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

            _presetMenu.Items.Add(new Separator());

            foreach (string name in names)
            {
                string captured = name;
                var item = new MenuItem { Header = name };
                item.Click += (s, args) => ApplyPreset(captured);
                _presetMenu.Items.Add(item);
            }
        }

        private void ApplyPreset(string name)
        {
            try
            {
                Recall(ForActiveContext(_presets.Load(name)));
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
        /// <summary>
        /// Tells the render marshal which formats are on screen (<c>REQ-TRC-001</c>).
        /// </summary>
        /// <remarks>
        /// <para>
        /// One envelope per distinct format, built on the pump thread. Four windows showing four
        /// formats of one acquisition is what <c>REQ-TRC-001</c> is for, and the marshal can only
        /// build the four if it is told which four — before this, it built one from the log
        /// magnitude and every window drew that, whatever its label said.
        /// </para>
        /// <para>
        /// Called whenever a trace opens, closes or changes format. Distinct formats, not distinct
        /// windows: eight windows showing two formats cost two decimations.
        /// </para>
        /// </remarks>
        private void UpdateMarshalFormats()
        {
            var formats = new List<TraceFormat>();

            foreach (char trace in Documents.Traces)
            {
                TracePlot plot = Documents.PlotOf(trace);

                if (plot != null && !formats.Contains(plot.CurrentFormat))
                {
                    formats.Add(plot.CurrentFormat);
                }
            }

            _marshal.Formats = formats;
        }

        private void OnPlotParameterChanged(object sender, HotSpot spot)
        {
            // A format hot spot changes what has to be decimated, so the marshal is told before
            // anything else is done with the change.
            UpdateMarshalFormats();

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

                // REQ-UI-062: a hidden marker keeps its number, its position and its readout, and
                // loses only its glyph. Skipped here rather than removed from the set, because the
                // set is what the Markers window and the saved state are built from.
                if (index >= 0 && marker.IsVisible)
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

            // The active trace's plot, which is the primary one in every arrangement that has trace A
            // on screen. Named through the document area rather than as Plot so that a context whose
            // windows are other letters draws its markers on a window it owns.
            (Documents.ActivePlot ?? Plot).SetMarkers(primitives, readout);
            FillMarkerChooser();
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
            // REQ-UI-063's Marker Tools: placing a marker is one mouse mode among five, not
            // something every click does. Pointer exists precisely so that a click can mean
            // nothing.
            if (_mouseMode != Rendering.MouseMode.Marker)
            {
                return;
            }

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
            engine.BlockAcquired -= OnBlockAcquired;
            engine.Faulted -= OnEngineFaulted;
            engine.Completed -= OnEngineCompleted;

            // Off this session before it goes: the shell builds a new engine on every Apply, and an
            // analyser still attached to the old one would be feeding contexts from a front end that
            // had been abandoned. The frames the contexts are holding are left alone -- a stopped
            // measurement is still one you can look at.
            _contextAnalyser.Attach(null);

            await engine.StopAsync().ConfigureAwait(true);
            engine.Dispose();

            _marshal.Reset();
            ShowRunningStatistics();

            SetStartEnabled(_activeFrontEnd != null);
            SetStopEnabled(false);
        }

        private void ShowRunningStatistics()
        {
            SpectrumEngine engine = _engine;

            RateText.Content = engine == null
                ? string.Empty
                : engine.MeasuredUpdatesPerSecond.ToString("0.0", CultureInfo.CurrentCulture) +
                  " updates/s";

            // REQ-NFR-012: the dropped-frame count is displayed, not merely counted.
            DroppedText.Content = MeasurementStatusText.DroppedFramesText(_marshal.FramesDropped);

            // REQ-DSP-012: in zero span the measurement's answer is one number, and this is the
            // cadence to read it at.
            ReportZeroSpan();

            ShowMeasurementStatus();
            ShowStatusFields();
        }

        /// <summary>
        /// The measurement status, at the bottom left (<c>REQ-UI-006</c>).
        /// </summary>
        /// <remarks>
        /// <para>
        /// <strong>Derived from what the measurement is doing, not announced by whoever last
        /// touched it.</strong> That is what makes "each status string appears when its condition
        /// holds" true: a status assigned by a code path shows whatever ran last, and
        /// <em>Average Complete</em> would stay on screen until something overwrote it.
        /// </para>
        /// <para>
        /// Transient notices — a preset applied, a marker placed — still write to
        /// <see cref="StatusText"/> and are replaced by the status on the next tick. The two are
        /// different things: a notice is an event, a status is a state the measurement is in.
        /// </para>
        /// </remarks>
        private void ShowMeasurementStatus()
        {
            SpectrumEngine engine = _engine;

            int wanted = _analysis.Averaging == AveragingType.Off ? 0 : _analysis.AverageCount;

            MeasurementStatus status = MeasurementStatusText.For(
                isMeasuring: engine != null,
                isArmed: _isArmedWaitingForTrigger,
                isFillingRecord: _isFillingTimeRecord,
                isGapFree: _negotiatedPlan != null && _negotiatedPlan.SupportsGapFreeStreaming,
                averagesWanted: wanted,
                averagesDone: AveragesCompleted);

            MeasurementStatus = status;
            StatusText.Content = MeasurementStatusText.TextOf(status);
        }

        /// <summary>The measurement status last shown (<c>REQ-UI-006</c>).</summary>
        public MeasurementStatus MeasurementStatus { get; private set; } = OpenVSA.Measurement.MeasurementStatus.Idle;

        /// <summary>Whether the acquisition is armed and its trigger has not yet occurred.</summary>
        private bool _isArmedWaitingForTrigger;

        /// <summary>Whether the time record is still being filled.</summary>
        private bool _isFillingTimeRecord;

        /// <summary>
        /// Reports where the acquisition has got to, for <c>REQ-UI-006</c>'s status strings.
        /// </summary>
        /// <param name="armed">Armed, with the trigger condition not yet met.</param>
        /// <param name="fillingRecord">Triggered, with the record still filling.</param>
        /// <remarks>
        /// Public so the criterion — "driving the measurement into <em>Waiting for Trigger</em> and
        /// <em>Average Complete</em> shows those strings" — can be exercised. The acquisition
        /// pipeline calls it as it moves through those states.
        /// </remarks>
        public void ReportAcquisitionPhase(bool armed, bool fillingRecord)
        {
            _isArmedWaitingForTrigger = armed;
            _isFillingTimeRecord = fillingRecord;

            ShowMeasurementStatus();
        }

        /// <summary>
        /// The four status-bar fields that track a condition rather than a figure
        /// (<c>REQ-UI-006</c>).
        /// </summary>
        /// <remarks>
        /// Calibration, reference lock, measured transfer rate and the preview-feature count. Each
        /// reads its condition rather than showing a fixed value, which is the criterion's own
        /// wording — a bar that always said "Calibrated" would satisfy "the field is present" and
        /// nothing else.
        /// </remarks>
        private void ShowStatusFields()
        {
            IFrontEnd frontEnd = _activeFrontEnd;

            CalibrationText.Content = frontEnd == null
                ? "Cal —"
                : (IsCalibrationQuestionable ? "CAL?" : "Cal OK");

            ReferenceText.Content = frontEnd == null
                ? "Ref —"
                : (!_usingExternalReference
                    ? "Ref internal"
                    : (IsReferenceUnlocked ? "Ref EXT UNLOCKED" : "Ref ext locked"));

            // REQ-NFR-027: the measured transfer rate and the duty cycle it implies, never the
            // headline figure for the bus. A Complex32 sample is eight bytes.
            AcquisitionPlan plan = _negotiatedPlan;

            if (frontEnd == null)
            {
                TransferText.Content = string.Empty;
            }
            else
            {
                double bytesPerSecond = frontEnd.Capabilities.MaxSampleRateHz * 8.0;

                double duty = plan == null || plan.SampleRateHz <= 0.0
                    ? 1.0
                    : Math.Min(1.0, bytesPerSecond / (plan.SampleRateHz * 8.0));

                TransferText.Content =
                    EngineeringText.Quantity(bytesPerSecond, "B/s", 3) + ", duty " +
                    (duty * 100.0).ToString("0", CultureInfo.CurrentCulture) + " %";
            }

            // REQ-UI-006's "beta features in use", adopted as a preview-feature indicator. OpenVSA
            // gates nothing (REQ-LIC-010), so this counts what is in use rather than what is
            // licensed — and reads zero until something registers, which is the honest answer.
            PreviewText.Content = _previewFeatures.Count == 0
                ? "No preview features"
                : _previewFeatures.Count.ToString(CultureInfo.CurrentCulture) +
                  " preview feature(s): " + string.Join(", ", _previewFeatures);
        }

        private readonly SortedSet<string> _previewFeatures =
            new SortedSet<string>(StringComparer.Ordinal);

        private bool _usingExternalReference;

        /// <summary>
        /// The plan the front end negotiated, for <c>REQ-NFR-027</c>'s duty cycle.
        /// </summary>
        /// <remarks>
        /// The negotiated plan rather than the requested one: the duty cycle is about what the link
        /// can actually sustain against what the acquisition actually asks of it, and a request the
        /// instrument declined would give a figure for a measurement nobody is making.
        /// </remarks>
        private AcquisitionPlan _negotiatedPlan;

        /// <summary>The preview features currently in use (<c>REQ-UI-006</c>).</summary>
        public IReadOnlyCollection<string> PreviewFeatures => _previewFeatures;

        /// <summary>
        /// Declares a feature as preview-quality while it is in use (<c>REQ-UI-006</c>).
        /// </summary>
        /// <param name="name">What to call it in the status bar.</param>
        /// <param name="inUse">Whether it is in use.</param>
        /// <exception cref="ArgumentException"><paramref name="name"/> is null or blank.</exception>
        public void SetPreviewFeature(string name, bool inUse)
        {
            if (string.IsNullOrEmpty(name) || name.Trim().Length == 0)
            {
                throw new ArgumentException("A preview feature needs a name.", nameof(name));
            }

            if (inUse)
            {
                _previewFeatures.Add(name.Trim());
            }
            else
            {
                _previewFeatures.Remove(name.Trim());
            }

            ShowStatusFields();
        }

        private void ShutDown()
        {
            // Before the window's controls are gone: the sizes are read off the panes, and a
            // disposed visual tree reports nothing useful.
            SaveToolWindowLayout();

            SpectrumEngine engine = _engine;
            _engine = null;

            _contextAnalyser.Dispose();

            // REQ-NFR-002: every context's held frame goes back, because nothing will ever display
            // it again.
            foreach (MeasurementContext context in _contextSet.Contexts)
            {
                context.ClearFrame();
            }

            if (engine != null)
            {
                // Not awaited: the window is gone and there is nothing left to marshal back to.
                // Dispose cancels the pump, and the front end is disposed below either way.
                engine.FrameComputed -= OnFrameComputed;
                engine.BlockAcquired -= OnBlockAcquired;
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
