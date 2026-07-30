using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using OpenVSA.Dsp.Windowing;
using OpenVSA.Measurement.State;
using OpenVSA.Ui.Dialogs;
using Xunit;
using Xunit.Abstractions;

namespace OpenVSA.Ui.Tests
{
    /// <summary>
    /// <c>REQ-DSP-012</c>: "Entering zero-span or power-spectrum operation replaces the window-type
    /// control with a Channel Filter Shape control offering Gaussian and None/anti-alias-only, and no
    /// window-type selection remains reachable in that mode. The selected shape is recorded in the
    /// trace state, so a saved measurement records which filter produced it."
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Replaced, not disabled.</strong> The load-bearing word in the criterion is
    /// <em>reachable</em>, so the strong assertion here is a logical-tree walk that finds no control
    /// offering a window type anywhere — and it is shown to find one outside zero span, because a
    /// walk that passes by finding nothing proves nothing about the walk.
    /// </para>
    /// <para>
    /// Both surfaces are covered: the settings pane, and the ResBW tab of the Analysis dialog. Either
    /// alone would leave the other as a way back to a setting the requirement says is gone.
    /// </para>
    /// </remarks>
    [Collection("Shell")]
    public class ZeroSpanChannelFilterTests
    {
        private readonly ShellHost _host;
        private readonly ITestOutputHelper _output;

        /// <summary>Takes the shared STA host.</summary>
        /// <param name="host">The host whose thread the shell is built on.</param>
        /// <param name="output">Where the offered shapes are written.</param>
        public ZeroSpanChannelFilterTests(ShellHost host, ITestOutputHelper output)
        {
            _host = host;
            _output = output;
        }

        [Fact]
        public void EnteringZeroSpanReplacesTheWindowControlInThePane()
        {
            WithShell(shell =>
            {
                Assert.False(shell.ZeroSpan);
                Assert.True(shell.WindowSelectionIsInThePane);
                Assert.False(shell.ChannelFilterSelectionIsInThePane);
                Assert.Equal("Window", shell.WindowLabel.Text);

                shell.ZeroSpan = true;

                // Replaced: the window control is out of the pane and the channel filter is in it.
                Assert.False(shell.WindowSelectionIsInThePane);
                Assert.True(shell.ChannelFilterSelectionIsInThePane);
                Assert.Equal("Ch Filter", shell.WindowLabel.Text);

                // And back, because leaving the mode must not lose the window the user chose.
                shell.ZeroSpan = false;

                Assert.True(shell.WindowSelectionIsInThePane);
                Assert.False(shell.ChannelFilterSelectionIsInThePane);
                Assert.Equal("Window", shell.WindowLabel.Text);
            });
        }

        [Fact]
        public void TheControlOffersExactlyGaussianAndNoneAntiAliasOnly()
        {
            WithShell(shell =>
            {
                shell.ZeroSpan = true;

                string[] offered = shell.ChannelFilterBox.Items
                    .Cast<object>()
                    .Select(i => i.ToString())
                    .ToArray();

                _output.WriteLine(string.Join(" | ", offered));

                Assert.Equal(2, offered.Length);
                Assert.Equal("Gaussian", offered[0]);

                // "None" on its own would claim the band is unfiltered, and it is not: the front
                // end's anti-alias filter is still there. The criterion says "None/anti-alias-only".
                Assert.Contains("anti-alias", offered[1], StringComparison.Ordinal);

                // Choosing one on the control is what changes the measurement's setting -- the
                // control is not a display of something set elsewhere.
                shell.ChannelFilterBox.SelectedIndex = 1;
                Assert.Equal(ChannelFilterType.None, shell.ChannelFilter);

                shell.ChannelFilterBox.SelectedIndex = 0;
                Assert.Equal(ChannelFilterType.Gaussian, shell.ChannelFilter);
            });
        }

        [Fact]
        public void NoWindowTypeSelectionRemainsReachableInThatMode()
        {
            WithShell(shell =>
            {
                // First the discriminating half: outside zero span the walk MUST find one, or it is
                // a search that would pass over a shell with the control still in it.
                IReadOnlyList<string> before = WindowSelectionsIn(shell);

                Assert.NotEmpty(before);
                _output.WriteLine("Outside zero span: " + string.Join(", ", before));

                shell.ZeroSpan = true;

                IReadOnlyList<string> after = WindowSelectionsIn(shell);

                Assert.True(
                    after.Count == 0,
                    "A window-type selection is still reachable in zero span: " +
                    string.Join(", ", after));
            });
        }

        [Fact]
        public void TheAnalysisDialogSwapsTheControlToo()
        {
            Sta.Run(() =>
            {
                var settings = new AnalysisSettings();
                var dialog = new AnalysisDialog(new DialogFrameworkOptions(), settings);

                Assert.True(dialog.ShowTab("ResBW"));

                // The pane is one way to the setting; the dialog is the other, and a mode honoured
                // in one place only leaves a way back to a control the requirement says is gone.
                Assert.NotEmpty(WindowSelectionsUnder(dialog.Pages.Select(p => p.Content)));

                settings.ZeroSpan = true;

                IReadOnlyList<string> after =
                    WindowSelectionsUnder(dialog.Pages.Select(p => p.Content));

                Assert.True(
                    after.Count == 0,
                    "The Analysis dialog still offers a window type in zero span: " +
                    string.Join(", ", after));

                // And it offers the channel filter instead, spelled out in full because this dialog
                // has the room the pane's 170-pixel label column does not.
                Assert.Contains(
                    "Channel Filter Shape",
                    LabelsUnder(dialog.Pages.Select(p => p.Content)));
            });
        }

        [Fact]
        public void TheShapeAndTheModeAreRecordedInTheTraceState()
        {
            WithShell(shell =>
            {
                shell.ZeroSpan = true;
                shell.ChannelFilter = ChannelFilterType.None;

                ApplicationState saved = shell.CaptureState();
                AnalysisState analysis = saved.Measurements[0].Analysis;

                // "so a saved measurement records which filter produced it" -- which takes both
                // fields to answer, because the window is still saved too.
                Assert.True(analysis.ZeroSpan);
                Assert.Equal(ChannelFilterType.None, analysis.ChannelFilter);

                // Changed away, then recalled: the state is what puts it back, not the pane.
                shell.ZeroSpan = false;
                shell.ChannelFilter = ChannelFilterType.Gaussian;

                shell.Recall(saved);

                Assert.True(shell.ZeroSpan);
                Assert.Equal(ChannelFilterType.None, shell.ChannelFilter);

                // And the pane followed the recalled mode, rather than waiting to be told again.
                Assert.False(shell.WindowSelectionIsInThePane);
                Assert.True(shell.ChannelFilterSelectionIsInThePane);
            });
        }

        [Fact]
        public void TheModeSurvivesASaveAndRecallOfEveryContext()
        {
            WithShell(shell =>
            {
                shell.ZeroSpan = true;
                shell.ChannelFilter = ChannelFilterType.None;

                shell.AddContext("Spectrum");

                Assert.True(shell.ActivateContext("Spectrum"));

                // A second context, in ordinary span. Two contexts differing in mode is exactly
                // what REQ-DAT-010 makes possible, and the state must carry both.
                shell.ZeroSpan = false;

                ApplicationState saved = shell.CaptureState();

                Assert.True(saved.For("Measurement 1").Analysis.ZeroSpan);
                Assert.Equal(
                    ChannelFilterType.None, saved.For("Measurement 1").Analysis.ChannelFilter);
                Assert.False(saved.For("Spectrum").Analysis.ZeroSpan);
            });
        }

        [Fact]
        public void ThereIsNoReadingUntilThereIsAFrame()
        {
            WithShell(shell =>
            {
                // Nothing measuring, so nothing to report -- rather than a reading of zero, which
                // would be a number for a measurement that has not been made.
                shell.ZeroSpan = true;

                Assert.Equal(string.Empty, shell.ZeroSpanReading());
            });
        }

        private void WithShell(Action<ShellWindow> body)
        {
            _host.Run(() =>
            {
                var shell = new ShellWindow { PersistPreferences = false, Interactive = false };

                try
                {
                    body(shell);
                }
                finally
                {
                    shell.Close();
                }
            });
        }

        /// <summary>
        /// Every control in the shell that offers a window type to choose from.
        /// </summary>
        /// <remarks>
        /// A logical-tree walk, because that is what "reachable" means for a WPF surface: an
        /// unselected tab's content is out of the visual tree by design, and a visual walk would
        /// report a control as gone simply because its tab was not in front.
        /// </remarks>
        private static IReadOnlyList<string> WindowSelectionsIn(ShellWindow shell) =>
            WindowSelectionsUnder(new object[] { shell });

        private static IReadOnlyList<string> WindowSelectionsUnder(IEnumerable<object> roots)
        {
            // A window name that no channel filter shares. "Gaussian" is BOTH a window
            // (REQ-DSP-010) and a channel filter shape (REQ-DSP-012) -- two different things that
            // happen to have the same name -- so a search for window names alone reports the channel
            // filter control as a window control and the criterion can never be met. Matching on the
            // names only one of the two uses is what tells them apart.
            var names = new HashSet<string>(StringComparer.Ordinal);

            foreach (WindowType window in (WindowType[])Enum.GetValues(typeof(WindowType)))
            {
                string spelled = WindowText.Describe(window);
                ChannelFilterType shared;

                if (!ChannelFilters.TryParse(spelled, out shared))
                {
                    names.Add(spelled);
                }
            }

            Assert.True(
                names.Count > 1,
                "Every window name is also a channel filter name, so this walk cannot tell a " +
                "window control from a channel filter control.");

            var found = new List<string>();

            foreach (object root in roots)
            {
                foreach (DependencyObject node in Descendants(root as DependencyObject))
                {
                    var items = node as ItemsControl;

                    if (items == null)
                    {
                        continue;
                    }

                    foreach (object item in items.Items)
                    {
                        string text = item == null ? string.Empty : item.ToString();

                        if (names.Contains(text))
                        {
                            found.Add(
                                (items.Name.Length > 0 ? items.Name : items.GetType().Name) +
                                " offers " + text);
                            break;
                        }
                    }
                }
            }

            return found;
        }

        private static IReadOnlyList<string> LabelsUnder(IEnumerable<object> roots)
        {
            var labels = new List<string>();

            foreach (object root in roots)
            {
                foreach (DependencyObject node in Descendants(root as DependencyObject))
                {
                    var text = node as TextBlock;

                    if (text != null && !string.IsNullOrWhiteSpace(text.Text))
                    {
                        labels.Add(text.Text);
                    }
                }
            }

            return labels;
        }

        private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
        {
            if (root == null)
            {
                yield break;
            }

            yield return root;

            foreach (object child in LogicalTreeHelper.GetChildren(root))
            {
                var node = child as DependencyObject;

                if (node == null)
                {
                    continue;
                }

                foreach (DependencyObject deeper in Descendants(node))
                {
                    yield return deeper;
                }
            }
        }
    }
}
