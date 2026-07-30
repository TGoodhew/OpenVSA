using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using OpenVSA.Dsp.Spectrum;
using OpenVSA.Dsp.Windowing;
using OpenVSA.Measurement;

namespace OpenVSA.Ui
{
    /// <summary>
    /// Zero-span/power-spectrum operation in the shell: the control swap and the reading
    /// (<c>REQ-DSP-012</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The window control is removed from the grid, not disabled or hidden.</strong> The
    /// requirement asks that "no window-type selection remains reachable in that mode", and a
    /// collapsed or greyed control is still in the logical tree — still findable by an automation
    /// client, still there for anything walking the pane. Removing it is the only form of the claim
    /// that can be asserted without first arguing about what "reachable" means, and it is what
    /// <c>ZeroSpanChannelFilterTests</c> asserts.
    /// </para>
    /// <para>
    /// <strong>The selection is not cleared when the mode changes.</strong> A user who leaves zero
    /// span and comes back finds the filter they chose, exactly as they find the window they chose;
    /// both are carried in the analysis settings and both are saved. Which of the two was in force is
    /// what <c>AnalysisState.ZeroSpan</c> records, and it takes all three fields to answer "which
    /// filter produced this trace".
    /// </para>
    /// </remarks>
    public partial class ShellWindow
    {
        /// <summary>
        /// Whether the measurement is in zero-span/power-spectrum operation (<c>REQ-DSP-012</c>).
        /// </summary>
        public bool ZeroSpan
        {
            get { return _analysis.ZeroSpan; }
            set { _analysis.ZeroSpan = value; }
        }

        /// <summary>The channel filter shape in force in zero span (<c>REQ-DSP-012</c>).</summary>
        public ChannelFilterType ChannelFilter
        {
            get { return _analysis.ChannelFilter; }
            set { _analysis.ChannelFilter = value; }
        }

        /// <summary>
        /// Whether a window-type selection is offered in the settings pane.
        /// </summary>
        /// <remarks>
        /// Asked of the grid rather than of the control, because the control object goes on existing
        /// after it has been taken out of the pane — the question the requirement asks is whether it
        /// is <em>there</em>.
        /// </remarks>
        public bool WindowSelectionIsInThePane => SettingsGrid.Children.Contains(WindowBox);

        /// <summary>Whether a channel filter selection is offered in the settings pane.</summary>
        public bool ChannelFilterSelectionIsInThePane =>
            SettingsGrid.Children.Contains(ChannelFilterBox);

        /// <summary>
        /// Puts the applicable control in the settings pane and takes the other one out.
        /// </summary>
        /// <remarks>
        /// Called from the constructor and from every analysis-settings change, so the pane follows
        /// the settings whether the mode was changed here, in the Analysis dialog, or by a recalled
        /// state.
        /// </remarks>
        private void FollowZeroSpan()
        {
            bool zeroSpan = _analysis.ZeroSpan;

            Present(ChannelFilterBox, zeroSpan);
            Present(WindowBox, !zeroSpan);

            // "Ch Filter" rather than the full "Channel Filter Shape": the pane's label column is
            // 170 pixels and REQ-UI-013's own lesson is that a clipped label reads as a different
            // setting. The Analysis dialog, which has the room, spells it out in full.
            WindowLabel.Text = zeroSpan ? "Ch Filter" : "Window";

            _followingSettings = true;

            try
            {
                ChannelFilterBox.SelectedIndex = IndexOfChannelFilter(_analysis.ChannelFilter);
            }
            finally
            {
                _followingSettings = false;
            }
        }

        private bool _followingSettings;

        private void Present(UIElement control, bool wanted)
        {
            bool present = SettingsGrid.Children.Contains(control);

            if (wanted == present)
            {
                return;
            }

            if (wanted)
            {
                SettingsGrid.Children.Add(control);
            }
            else
            {
                SettingsGrid.Children.Remove(control);
            }
        }

        private static int IndexOfChannelFilter(ChannelFilterType filter)
        {
            for (int i = 0; i < ChannelFilters.All.Count; i++)
            {
                if (ChannelFilters.All[i] == filter)
                {
                    return i;
                }
            }

            return -1;
        }

        private void OnChannelFilterSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_followingSettings || ChannelFilterBox.SelectedIndex < 0)
            {
                return;
            }

            _analysis.ChannelFilter = ChannelFilters.All[ChannelFilterBox.SelectedIndex];
        }

        /// <summary>
        /// The zero-span reading for the frame on screen, or an empty string outside zero span.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <strong>This is what stops the setting being inert.</strong> A Channel Filter Shape that
        /// is recorded and never applied is a setting that lies; the reading is integrated through
        /// the selected shape, so choosing Gaussian rather than None changes the number — by nothing
        /// at all on a centred tone, and by the difference in noise bandwidth on anything noise-like.
        /// </para>
        /// <para>
        /// Reported once a second on the status timer rather than per frame: it is a number to read,
        /// not a trace, and sixty lines a second would be a log nobody could use.
        /// </para>
        /// </remarks>
        internal string ZeroSpanReading()
        {
            SpectrumFrame frame = _frame;

            if (!_analysis.ZeroSpan || frame == null)
            {
                return string.Empty;
            }

            double bandwidthHz = _analysis.ResolutionBandwidthHz;

            if (!(bandwidthHz > 0.0))
            {
                return string.Empty;
            }

            BandPower power = ZeroSpanMeasurement.Power(
                frame, _analysis.ChannelFilter, bandwidthHz);

            double noiseBandwidthHz = ZeroSpanMeasurement.NoiseBandwidthHz(
                frame, _analysis.ChannelFilter, bandwidthHz);

            return "Zero span: " +
                power.TotalDbm.ToString("0.00", CultureInfo.CurrentCulture) + " dBm at " +
                EngineeringText.Frequency(frame.CenterFrequencyHz) + " through " +
                ChannelFilters.Describe(_analysis.ChannelFilter) +
                ", noise bandwidth " + EngineeringText.Frequency(noiseBandwidthHz);
        }

        /// <summary>
        /// Writes the zero-span reading to the Output window, if there is one to write.
        /// </summary>
        /// <remarks>
        /// The Output window is where <c>REQ-UI-002</c> puts measurement results, and until now
        /// nothing measuring ever wrote to it. The log is bounded, so a reading a second is a
        /// scrolling readout rather than an unbounded file.
        /// </remarks>
        private void ReportZeroSpan()
        {
            string reading = ZeroSpanReading();

            if (reading.Length > 0)
            {
                _outputLog.Append(reading);
            }
        }
    }
}
