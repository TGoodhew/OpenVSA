using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows;
using OpenVSA.Ui.Dialogs.Pages;

namespace OpenVSA.Ui.Dialogs
{
    /// <summary>
    /// The Analysis dialog:
    /// <c>Frequency | ResBW | Time | Detectors | Conversion | Average | Heatmaps</c>
    /// (<c>REQ-UI-072</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Seven tabs, these names, this order, and no others.</strong> Built by walking
    /// <see cref="TabNames"/> rather than from seven <c>AddPage</c> calls, so an eighth tab cannot
    /// appear without editing the list a test reads — the same construction the Display
    /// Preferences dialog uses, for the same reason.
    /// </para>
    /// <para>
    /// <strong>None of them is a placeholder.</strong> The requirement says so explicitly, and
    /// <see cref="AnalysisPage.RowCount"/> is what lets a test assert it rather than a reader
    /// take a screenshot's word for it. Every row edits a live setting on
    /// <see cref="AnalysisSettings"/>; there is no page that only explains itself.
    /// </para>
    /// <para>
    /// It is a <see cref="SettingsDialog"/>, so it obeys the <c>REQ-UI-070</c> and
    /// <c>REQ-UI-071</c> framework rules by construction: modeless, live, no OK or Apply, four
    /// layout modes, Fixed Size, Keep on Top and a remembered mode of its own.
    /// </para>
    /// </remarks>
    public sealed class AnalysisDialog : SettingsDialog
    {
        /// <summary>The dialog's name, and the key Persist Mode remembers it by.</summary>
        public const string DialogTitle = "Analysis";

        private static readonly ReadOnlyCollection<string> Names =
            new ReadOnlyCollection<string>(new List<string>
            {
                "Frequency", "ResBW", "Time", "Detectors", "Conversion", "Average", "Heatmaps",
            });

        private readonly FrequencyPage _frequency;
        private readonly ResolutionBandwidthPage _resolutionBandwidth;
        private readonly TimePage _time;
        private readonly DetectorPage _detectors;
        private readonly ConversionPage _conversion;
        private readonly AveragePage _average;
        private readonly HeatmapPage _heatmaps;

        /// <summary>Creates the dialog over the live analysis settings.</summary>
        /// <param name="options">The dialog framework's options.</param>
        /// <param name="settings">The settings to edit; changed in place.</param>
        /// <exception cref="ArgumentNullException">An argument is null.</exception>
        public AnalysisDialog(DialogFrameworkOptions options, AnalysisSettings settings)
            : base(DialogTitle, options)
        {
            if (settings == null)
            {
                throw new ArgumentNullException(nameof(settings));
            }

            Settings = settings;

            _frequency = new FrequencyPage(settings);
            _resolutionBandwidth = new ResolutionBandwidthPage(settings);
            _time = new TimePage(settings);
            _detectors = new DetectorPage(settings);
            _conversion = new ConversionPage(settings);
            _average = new AveragePage(settings);
            _heatmaps = new HeatmapPage(settings);

            foreach (string name in Names)
            {
                AddPage(name, PageFor(name));
            }
        }

        /// <summary>The seven tab names, in order, as <c>REQ-UI-072</c> lists them.</summary>
        public static IReadOnlyList<string> TabNames => Names;

        /// <summary>The settings every tab edits.</summary>
        public AnalysisSettings Settings { get; }

        /// <summary>The pages, as the typed objects they are.</summary>
        public IEnumerable<AnalysisPage> AnalysisPages
        {
            get
            {
                yield return _frequency;
                yield return _resolutionBandwidth;
                yield return _time;
                yield return _detectors;
                yield return _conversion;
                yield return _average;
                yield return _heatmaps;
            }
        }

        /// <summary>
        /// Brings a named tab to the front.
        /// </summary>
        /// <param name="name">The tab's name, as <see cref="TabNames"/> writes it.</param>
        /// <returns>Whether there is a tab of that name.</returns>
        /// <remarks>
        /// The Analysis menu lists the tabs individually — <em>Frequency…</em>, <em>ResBW…</em> —
        /// and each opens this one dialog on its own tab. Seven dialogs would be seven windows
        /// editing one measurement, which is what the tab set exists to avoid.
        /// </remarks>
        public bool ShowTab(string name)
        {
            for (int i = 0; i < Names.Count; i++)
            {
                if (string.Equals(Names[i], name, StringComparison.OrdinalIgnoreCase))
                {
                    SelectedIndex = i;
                    return true;
                }
            }

            return false;
        }

        private FrameworkElement PageFor(string name)
        {
            switch (name)
            {
                case "Frequency": return _frequency;
                case "ResBW": return _resolutionBandwidth;
                case "Time": return _time;
                case "Detectors": return _detectors;
                case "Conversion": return _conversion;
                case "Average": return _average;
                case "Heatmaps": return _heatmaps;
            }

            // Unreachable while the list and this switch agree, which is the point of having both:
            // a name added to one and not the other fails here rather than opening a blank tab.
            throw new InvalidOperationException(
                "REQ-UI-072 names a '" + name + "' tab that this dialog has no page for.");
        }
    }
}
