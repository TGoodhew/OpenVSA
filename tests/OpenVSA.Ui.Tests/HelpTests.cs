using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using OpenVSA.Demod.Chain;
using OpenVSA.Ui;
using OpenVSA.Ui.Help;
using Xunit;
using Xunit.Abstractions;

namespace OpenVSA.Ui.Tests
{
    /// <summary>
    /// <c>REQ-DEM-001</c>'s "the same order appears in the user help", read from the shell's end:
    /// the help keys show the topic, and what they show is the declared order.
    /// </summary>
    /// <remarks>
    /// The chain's own tests hold the shipped topic to the declaration. These hold the shell to the
    /// shipped topic, which is the other half: a help key that showed nothing, or that showed a
    /// stripped-down copy of the list, would pass every test on the other side of that boundary.
    /// </remarks>
    [Collection("Shell")]
    public class HelpTests
    {
        private readonly ITestOutputHelper _output;
        private readonly ShellHost _host;

        public HelpTests(ShellHost host, ITestOutputHelper output)
        {
            _host = host;
            _output = output;
        }

        [Fact]
        public void TheHelpKeyPutsTheTopicInTheOutputWindow()
        {
            _host.Run(() =>
            {
                var shell = new ShellWindow
                {
                    PersistPreferences = false,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -4000.0,
                    Top = -4000.0,
                    ShowInTaskbar = false,
                };

                shell.Show();

                try
                {
                    // Through the input system, as REQ-UI-065's own tests drive it: what is being
                    // asserted is that pressing the key shows the help, and calling the handler
                    // would assert only that the handler works.
                    Press(shell, Key.F1);

                    IReadOnlyList<string> written = shell.OutputLog.Lines;

                    foreach (string step in ProcessingOrder.Render())
                    {
                        Assert.Contains(step, written);
                    }

                    Assert.Contains(
                        HelpPresentation.Title(HelpPresentation.DefaultTopic), written);
                }
                finally
                {
                    shell.Close();
                }
            });
        }

        [Fact]
        public void TheShownTopicCarriesTheDeclaredProcessingOrder()
        {
            IReadOnlyList<string> shown = HelpPresentation.Lines(HelpPresentation.DefaultTopic);

            foreach (string step in ProcessingOrder.Render())
            {
                Assert.Contains(step, shown);
            }

            // In order, and not merely present: the steps must appear in the sequence they are
            // declared in, or the page would be a set of facts rather than a chain.
            var positions = ProcessingOrder.Render()
                .Select(step => IndexOf(shown, step))
                .ToArray();

            Assert.Equal(positions.OrderBy(position => position).ToArray(), positions);
        }

        [Fact]
        public void TheTopicIsFlattenedRatherThanShownWithItsMarkers()
        {
            IReadOnlyList<string> shown = HelpPresentation.Lines(HelpPresentation.DefaultTopic);

            Assert.DoesNotContain(shown, line => line.StartsWith("#", StringComparison.Ordinal));
            Assert.DoesNotContain(shown, line => line.Contains("**"));
            Assert.DoesNotContain(shown, line => line.StartsWith("```", StringComparison.Ordinal));

            // Not so flattened that nothing is left: the headings survive as text.
            Assert.Contains(shown, line => line.IndexOf(
                "optional", StringComparison.OrdinalIgnoreCase) >= 0);

            _output.WriteLine(string.Join(Environment.NewLine, shown.Take(20)));
        }

        [Fact]
        public void TheTitleIsTheTopicsOwnHeading()
        {
            string title = HelpPresentation.Title(HelpPresentation.DefaultTopic);

            Assert.False(string.IsNullOrEmpty(title));
            Assert.DoesNotContain("#", title);
            Assert.Equal(title, title.Trim());

            _output.WriteLine(title);
        }

        [Fact]
        public void AskingForATopicThatDoesNotShipIsRefused()
        {
            Assert.Throws<ArgumentException>(() => HelpPresentation.Lines("no-such-topic"));
        }

        /// <summary>Raises a real key press on the shell and lets WPF route it.</summary>
        private static void Press(ShellWindow window, Key key)
        {
            PresentationSource source = PresentationSource.FromVisual(window);

            Assert.NotNull(source);

            window.ModifierSource = () => ModifierKeys.None;

            var args = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key)
            {
                RoutedEvent = Keyboard.PreviewKeyDownEvent,
            };

            window.RaiseEvent(args);
        }

        private static int IndexOf(IReadOnlyList<string> lines, string wanted)
        {
            for (int index = 0; index < lines.Count; index++)
            {
                if (string.Equals(lines[index], wanted, StringComparison.Ordinal))
                {
                    return index;
                }
            }

            return -1;
        }
    }
}
