using System.Collections.Generic;
using System.Windows;
using OpenVSA.Measurement.State;
using OpenVSA.Ui.ToolWindows;
using Xunit;

namespace OpenVSA.Ui.Tests
{
    /// <summary>
    /// The panes divide the window rather than racing for the remainder (see #420).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>What went wrong, so the tests assert the right thing.</strong> Measurement and
    /// Hardware asked for a fixed 380 each whatever the window was, the document took what was
    /// left, and an open tool window took what was left of that. On a real machine that came to
    /// <strong>28 pixels</strong> — and 28 was then saved and restored every launch, so the shell
    /// squeezed the pane once and then kept it squeezed for ever.
    /// </para>
    /// <para>
    /// Both halves are tested: that a degenerate saved width is not honoured, and that a first run
    /// divides the width instead of allocating fixed amounts of it.
    /// </para>
    /// </remarks>
    public class PaneBalanceTests
    {
        [Fact]
        public void AWidthTooSmallToOperateIsNotRestored()
        {
            // The real sidecar from the machine this was found on.
            var saved = new List<ToolWindowPlacement>
            {
                new ToolWindowPlacement
                {
                    Name = "Markers",
                    Side = "Right",
                    Width = 28.0,
                    Height = 567.16,
                    IsOpen = true,
                },
            };

            ToolWindowLayout layout = ToolWindowLayout.FromState(saved);

            Assert.True(
                layout[ToolWindow.Markers].Width >= ToolWindowLayout.MinimumWidth,
                "A pane restored to 28 px is open, present and unusable - and cannot show the " +
                "caption buttons a user would need to close it.");

            // The window is still open: it is the width that was rejected, not the user's choice
            // to have the pane at all.
            Assert.True(layout.IsOpen(ToolWindow.Markers));
        }

        [Fact]
        public void AWidthAUserCouldHaveChosenIsRestoredUntouched()
        {
            // The floor must not become a silent re-layout of everyone's arrangement.
            var saved = new List<ToolWindowPlacement>
            {
                new ToolWindowPlacement
                {
                    Name = "Markers",
                    Side = "Right",
                    Width = 460.0,
                    Height = 300.0,
                    IsOpen = true,
                },
            };

            ToolWindowLayout layout = ToolWindowLayout.FromState(saved);

            Assert.Equal(460.0, layout[ToolWindow.Markers].Width);
            Assert.Equal(300.0, layout[ToolWindow.Markers].Height);
        }

        [Fact]
        public void ALayoutFromNothingIsNotMarkedAsRestored()
        {
            // WasRestored is what tells a first run from a returning user, so it carries the whole
            // of "balance the panes, but never rearrange someone's own arrangement".
            Assert.False(ToolWindowLayout.FromState(null).WasRestored);
            Assert.False(ToolWindowLayout.FromState(new List<ToolWindowPlacement>()).WasRestored);

            var saved = new List<ToolWindowPlacement>
            {
                new ToolWindowPlacement { Name = "Markers", Side = "Right", Width = 300.0, Height = 300.0 },
            };

            Assert.True(ToolWindowLayout.FromState(saved).WasRestored);
        }
    }

    /// <summary>The shell half: the panes actually get equal shares of the window.</summary>
    [Collection("Shell")]
    public class ShellPaneBalanceTests
    {
        private readonly ShellHost _host;

        /// <summary>Takes the shared shell thread.</summary>
        /// <param name="host">The thread every shell in this collection is built on.</param>
        public ShellPaneBalanceTests(ShellHost host)
        {
            _host = host;
        }

        [Fact]
        public void TheDockedPanesAndTheDocumentDivideTheWindowEqually()
        {
            _host.Run(() =>
            {
                // readSavedPreferences: false, so this asserts an arrangement rather than whatever
                // layout happens to be saved on the machine running the test. With it left on, the
                // test passed in CI (no sidecar) and failed locally the moment a real one existed.
                var shell = new ShellWindow(false)
                {
                    PersistPreferences = false,
                    Interactive = false,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -4000.0,
                    Top = -4000.0,
                    ShowInTaskbar = false,
                    Width = 1200.0,
                    Height = 720.0,
                };

                shell.Show();
                shell.UpdateLayout();

                // Balanced again explicitly, because an object initializer runs AFTER the
                // constructor: the shell balanced itself against the 1280 declared in XAML before
                // Width was ever set to 1200 here. In the application that is the right answer -
                // the declared width IS the startup width - but a test naming its own width has to
                // ask for the share to be recomputed.
                Assert.Equal(2, shell.BalancePanes());

                // Two XAML panes with no tool window open: they and the document make three, so
                // each should ask for a third of 1200.
                double measurement = Syncfusion.Windows.Tools.Controls.DockingManager
                    .GetDesiredWidthInDockedMode(shell.MeasurementDock);
                double hardware = Syncfusion.Windows.Tools.Controls.DockingManager
                    .GetDesiredWidthInDockedMode(shell.HardwareDock);

                Assert.Equal(400.0, measurement, 0);
                Assert.Equal(400.0, hardware, 0);

                shell.Close();
            });
        }

        [Fact]
        public void ThePanesAreNotLeftWithNoWidthAtAll()
        {
            // Removing the hard-coded 380 from the markup and then skipping the balance on a
            // restored layout collapsed both panes to about ninety pixels. Whatever else it does,
            // building a shell must never leave these two unsized.
            _host.Run(() =>
            {
                var shell = new ShellWindow { PersistPreferences = false, Interactive = false };

                foreach (System.Windows.Controls.ContentControl pane in
                    new[] { shell.MeasurementDock, shell.HardwareDock })
                {
                    double width = Syncfusion.Windows.Tools.Controls.DockingManager
                        .GetDesiredWidthInDockedMode(pane);

                    Assert.True(
                        width >= ToolWindowLayout.MinimumWidth,
                        "A docked pane was left at " + width + " px.");
                }
            });
        }
    }
}
