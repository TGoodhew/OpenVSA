using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using OpenVSA.Hal;

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
        private readonly FrontEndRegistry _registry;
        private IFrontEnd _activeFrontEnd;

        /// <summary>Creates the shell window.</summary>
        public ShellWindow()
        {
            InitializeComponent();

            _registry = FrontEndRegistry.CreateDefault();
            PopulateSourcesMenu();
            ShowDiscoveryResults();
        }

        /// <summary>The front end currently selected, or null if none has been chosen.</summary>
        public IFrontEnd ActiveFrontEnd => _activeFrontEnd;

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

        private void SelectFrontEnd(FrontEndDescriptor descriptor, MenuItem clicked)
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

        private static string Hz(double hertz)
        {
            if (hertz >= 1e9)
            {
                return (hertz / 1e9).ToString("0.###", CultureInfo.InvariantCulture) + " GHz";
            }

            if (hertz >= 1e6)
            {
                return (hertz / 1e6).ToString("0.###", CultureInfo.InvariantCulture) + " MHz";
            }

            if (hertz >= 1e3)
            {
                return (hertz / 1e3).ToString("0.###", CultureInfo.InvariantCulture) + " kHz";
            }

            return hertz.ToString("0.###", CultureInfo.InvariantCulture) + " Hz";
        }

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

            StatusText.Content = _registry.Providers.Count == 0
                ? "No signal source available"
                : "Ready";
        }
    }
}
