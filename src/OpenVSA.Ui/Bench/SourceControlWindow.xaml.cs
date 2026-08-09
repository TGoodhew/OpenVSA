using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace OpenVSA.Ui.Bench
{
    /// <summary>
    /// The interactive test signal source of issue #393, scope A.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Deliberately thin.</strong> Everything the issue judges this on —
    /// that the ranges are the instrument's, that a coercion is reported, that an instrument error
    /// reaches the event log rather than a dialog — is <see cref="SourceControlModel"/>'s and is
    /// asserted without a window. What is here is showing it, enabling what can be used, and
    /// hiding the settings that belong to a stimulus that is not selected.
    /// </para>
    /// <para>
    /// <strong>Modeless.</strong> <c>REQ-UI-070</c> requires it of a setting dialog, and this panel
    /// would be useless otherwise: its whole purpose is to change the stimulus while a measurement
    /// runs and watch the trace follow.
    /// </para>
    /// <para>
    /// <strong>No instrument is named here.</strong> Which sources exist, what they are called and
    /// what they can do are all discovered, and <c>REQ-HAL-002</c>'s criterion is a code search over
    /// this assembly for a model number.
    /// </para>
    /// </remarks>
    public partial class SourceControlWindow : Window
    {
        private readonly SourceControlModel _model;

        private bool _filling;

        /// <summary>
        /// Creates the panel over a model.
        /// </summary>
        /// <param name="model">The model; supplies the sources and takes the settings.</param>
        /// <exception cref="ArgumentNullException"><paramref name="model"/> is null.</exception>
        public SourceControlWindow(SourceControlModel model)
        {
            _model = model ?? throw new ArgumentNullException(nameof(model));

            SyncfusionLicense.Register();

            InitializeComponent();

            FillSources();
            FillStimuli();

            SourceChooser.SelectionChanged += (sender, e) => ShowChosenSource();
            StimulusChooser.SelectionChanged += (sender, e) => ShowChosenStimulus();

            ConnectButton.Click += (sender, e) => Connect();
            DisconnectButton.Click += (sender, e) => Disconnect();
            ApplyButton.Click += (sender, e) => Apply();
            OutputToggle.Click += (sender, e) => SetOutput();

            Closed += (sender, e) => _model.Disconnect();

            ShowChosenSource();
            ShowChosenStimulus();
            ShowConnectionState();
        }

        /// <summary>The source the panel would open, or null when none is chosen.</summary>
        public StimulusDescriptor ChosenSource =>
            SourceChooser.SelectedItem is SourceItem item ? item.Descriptor : null;

        /// <summary>The stimulus the panel would send.</summary>
        public StimulusKind SelectedStimulus =>
            StimulusChooser.SelectedItem is StimulusItem item
                ? item.Kind
                : StimulusKind.ContinuousWave;

        /// <summary>The message shown beside the buttons, or an empty string.</summary>
        public string Status => StatusText.Text ?? string.Empty;

        private void FillSources()
        {
            _filling = true;

            try
            {
                SourceChooser.Items.Clear();

                foreach (StimulusDescriptor descriptor in _model.Sources)
                {
                    // Wrapped rather than added directly, and DisplayMemberPath is deliberately not
                    // used: it sets what is drawn and leaves the item's ACCESSIBLE name as the
                    // descriptor's ToString, which is diagnostic - "Simulated source (no
                    // instrument) (OpenVSA.TestHarness)". A screen reader and an automation client
                    // then hear a different name from the one on screen. Found by driving this
                    // panel through UI Automation, which could not select an item by the name it
                    // displays.
                    SourceChooser.Items.Add(new SourceItem(descriptor));
                }

                if (SourceChooser.Items.Count > 0)
                {
                    SourceChooser.SelectedIndex = 0;
                }
                else
                {
                    // The panel is not opened at all in this state — the menu item is disabled with
                    // the reason. Shown anyway, because a window that can be reached by any route
                    // must explain itself rather than appear broken.
                    IdentityText.Text = _model.UnavailableReason;
                    ConnectButton.IsEnabled = false;
                }
            }
            finally
            {
                _filling = false;
            }
        }

        private void FillStimuli()
        {
            _filling = true;

            try
            {
                StimulusChooser.Items.Add(new StimulusItem(StimulusKind.ContinuousWave, "Carrier"));
                StimulusChooser.Items.Add(new StimulusItem(StimulusKind.Multitone, "Multitone comb"));
                StimulusChooser.Items.Add(new StimulusItem(StimulusKind.Noise, "Noise band"));

                StimulusChooser.SelectedIndex = 0;
            }
            finally
            {
                _filling = false;
            }
        }

        private void ShowChosenSource()
        {
            StimulusDescriptor descriptor = ChosenSource;

            if (_filling || descriptor == null)
            {
                return;
            }

            // The address the source offers, shown rather than used silently: a bench instrument's
            // address moves, and a stale one fails in a way that reads like a powered-off
            // instrument. What the panel opens is what the panel shows.
            ResourceBox.Text = descriptor.DefaultResource;
            ResourceBox.IsEnabled = descriptor.RequiresResource;
        }

        private void ShowChosenStimulus()
        {
            if (_filling)
            {
                return;
            }

            StimulusKind kind = SelectedStimulus;

            MultitonePanel.Visibility =
                kind == StimulusKind.Multitone ? Visibility.Visible : Visibility.Collapsed;

            NoisePanel.Visibility =
                kind == StimulusKind.Noise ? Visibility.Visible : Visibility.Collapsed;

            ShowCapability(kind);
        }

        /// <summary>
        /// Says whether the open source can produce the chosen stimulus, before Apply is pressed.
        /// </summary>
        /// <remarks>
        /// A source may produce a carrier and no comb — the harness models each as a capability to
        /// be asked for rather than assumed. Finding out at the click would mean an operator on a
        /// bench discovering it half way through a measurement.
        /// </remarks>
        private void ShowCapability(StimulusKind kind)
        {
            StimulusSource source = _model.Source;

            if (source == null)
            {
                ApplyButton.IsEnabled = false;
                return;
            }

            bool supported =
                kind == StimulusKind.ContinuousWave ||
                (kind == StimulusKind.Multitone && source.CanProduceMultitone) ||
                (kind == StimulusKind.Noise && source.CanProduceNoise);

            ApplyButton.IsEnabled = supported;

            StatusText.Text = supported
                ? string.Empty
                : "This source does not produce that stimulus.";

            if (kind == StimulusKind.Multitone && source.CanProduceMultitone)
            {
                StatusText.Text = string.Empty;
                ToneCountBox.ToolTip =
                    "Between " + source.MinimumTones + " and " + source.MaximumTones + " tones.";
            }

            if (kind == StimulusKind.Noise && source.CanProduceNoise)
            {
                BandwidthRangeText.Text =
                    EngineeringText.Frequency(source.MinimumNoiseBandwidthHz) + " to " +
                    EngineeringText.Frequency(source.MaximumNoiseBandwidthHz);
            }
        }

        private void Connect()
        {
            StimulusDescriptor descriptor = ChosenSource;

            if (descriptor == null)
            {
                return;
            }

            // Failure is reported into the event log by the model and returns false. It is the
            // ordinary case on a bench, not an application error, so nothing is thrown or shown
            // modally here.
            if (_model.Connect(descriptor, ResourceBox.Text))
            {
                ShowInstrumentState();
            }

            ShowConnectionState();
        }

        private void Disconnect()
        {
            _model.Disconnect();
            ShowConnectionState();
        }

        private void ShowConnectionState()
        {
            bool connected = _model.IsConnected;

            ConnectButton.IsEnabled = !connected && SourceChooser.Items.Count > 0;
            DisconnectButton.IsEnabled = connected;
            SourceChooser.IsEnabled = !connected;
            ResourceBox.IsEnabled =
                !connected && ChosenSource != null && ChosenSource.RequiresResource;

            OutputToggle.IsEnabled = connected;

            ShowCapability(SelectedStimulus);

            if (!connected)
            {
                IdentityText.Text = _model.Sources.Count > 0
                    ? "Not connected."
                    : _model.UnavailableReason;

                FrequencyRangeText.Text = string.Empty;
                LevelRangeText.Text = string.Empty;
                OutputToggle.IsChecked = false;
            }
        }

        /// <summary>
        /// Shows what the source says about itself, including the range it will be checked against.
        /// </summary>
        /// <remarks>
        /// The bounds beside the entry fields are the source's own. A source that will not state a
        /// bound leaves that field unranged and says nothing, rather than showing a bound belonging
        /// to some other instrument — see <see cref="SourceControlModel"/> for why an invented one
        /// is worse than none.
        /// </remarks>
        private void ShowInstrumentState()
        {
            StimulusSource source = _model.Source;

            if (source == null)
            {
                return;
            }

            IdentityText.Text = source.Identity;

            SourceLimits limits = _model.Limits;

            FrequencyRangeText.Text = limits.HasFrequencyRange
                ? EngineeringText.Frequency(limits.MinimumFrequencyHz) + " to " +
                  EngineeringText.Frequency(limits.MaximumFrequencyHz)
                : string.Empty;

            LevelRangeText.Text = limits.HasLevelRange
                ? Decibels(limits.MinimumLevelDbm) + " to " + Decibels(limits.MaximumLevelDbm)
                : string.Empty;

            if (string.IsNullOrEmpty(FrequencyBox.Text))
            {
                FrequencyBox.Text = EngineeringText.Frequency(source.FrequencyHz);
            }

            if (string.IsNullOrEmpty(LevelBox.Text))
            {
                LevelBox.Text = Decibels(source.LevelDbm);
            }

            OutputToggle.IsChecked = source.IsOutputEnabled;
        }

        private void Apply()
        {
            StimulusKind kind = SelectedStimulus;

            double frequencyHz;
            double levelDbm;

            if (!Read(FrequencyBox, EngineeringText.TryParseFrequency, "a frequency", out frequencyHz) ||
                !Read(LevelBox, EngineeringText.TryParseDecibels, "a level in dBm", out levelDbm))
            {
                return;
            }

            if (!Accepted(_model.ValidateFrequency(frequencyHz)) ||
                !Accepted(_model.ValidateLevel(levelDbm)))
            {
                return;
            }

            int toneCount = 0;
            double spacingHz = 0.0;
            double bandwidthHz = 0.0;

            if (kind == StimulusKind.Multitone)
            {
                if (!ReadInteger(ToneCountBox, out toneCount) ||
                    !Read(SpacingBox, EngineeringText.TryParseFrequency, "a spacing", out spacingHz))
                {
                    return;
                }

                if (!Accepted(_model.ValidateToneCount(toneCount)) ||
                    !Accepted(_model.ValidateToneSpacing(spacingHz)))
                {
                    return;
                }
            }

            if (kind == StimulusKind.Noise)
            {
                if (!Read(BandwidthBox, EngineeringText.TryParseFrequency, "a bandwidth",
                        out bandwidthHz))
                {
                    return;
                }

                if (!Accepted(_model.ValidateNoiseBandwidth(bandwidthHz)))
                {
                    return;
                }
            }

            StatusText.Text = string.Empty;

            if (_model.Apply(kind, frequencyHz, levelDbm, toneCount, spacingHz, bandwidthHz))
            {
                // Read back into the fields, so what the panel shows is what the instrument
                // settled on rather than what it was asked for. The difference between the two is
                // in the event log; this is the same fact on screen.
                ShowSettled();
            }
        }

        private void ShowSettled()
        {
            StimulusSource source = _model.Source;

            if (source == null)
            {
                return;
            }

            FrequencyBox.Text = EngineeringText.Frequency(source.FrequencyHz);
            LevelBox.Text = Decibels(source.LevelDbm);

            if (SelectedStimulus == StimulusKind.Multitone && source.CanProduceMultitone)
            {
                ToneCountBox.Text = source.ToneCount.ToString(CultureInfo.CurrentCulture);
                SpacingBox.Text = EngineeringText.Frequency(source.ToneSpacingHz);
            }

            if (SelectedStimulus == StimulusKind.Noise && source.CanProduceNoise)
            {
                BandwidthBox.Text = EngineeringText.Frequency(source.NoiseBandwidthHz);
            }

            OutputToggle.IsChecked = source.IsOutputEnabled;
        }

        private void SetOutput()
        {
            _model.SetOutput(OutputToggle.IsChecked == true);

            StimulusSource source = _model.Source;

            if (source != null)
            {
                // From the source, not from the click: a generator that refused to key up must not
                // leave a ticked box saying it did.
                OutputToggle.IsChecked = source.IsOutputEnabled;
            }
        }

        private delegate bool Parser(string text, out double value);

        private bool Read(TextBox box, Parser parse, string what, out double value)
        {
            if (parse(box.Text, out value))
            {
                return true;
            }

            StatusText.Text = "Enter " + what + ".";
            box.Focus();
            box.SelectAll();

            return false;
        }

        private bool ReadInteger(TextBox box, out int value)
        {
            if (int.TryParse(box.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out value))
            {
                return true;
            }

            StatusText.Text = "Enter a whole number of tones.";
            box.Focus();
            box.SelectAll();

            return false;
        }

        /// <summary>Shows a refusal beside the buttons and stops; the event log is for the source.</summary>
        private bool Accepted(string refusal)
        {
            StatusText.Text = refusal ?? string.Empty;

            return refusal == null;
        }

        private static string Decibels(double dbm) =>
            dbm.ToString("0.##", CultureInfo.CurrentCulture) + " dBm";

        /// <summary>One entry in the source chooser, named as it is shown.</summary>
        private sealed class SourceItem
        {
            internal SourceItem(StimulusDescriptor descriptor)
            {
                Descriptor = descriptor;
            }

            internal StimulusDescriptor Descriptor { get; }

            public override string ToString() => Descriptor.DisplayName;
        }

        /// <summary>One entry in the stimulus chooser.</summary>
        private sealed class StimulusItem
        {
            internal StimulusItem(StimulusKind kind, string name)
            {
                Kind = kind;
                Name = name;
            }

            internal StimulusKind Kind { get; }

            internal string Name { get; }

            public override string ToString() => Name;
        }
    }
}
