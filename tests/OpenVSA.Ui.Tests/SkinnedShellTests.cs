using System.Windows;
using System.Windows.Media;
using OpenVSA.Ui.Rendering;
using OpenVSA.Ui.Theming;
using Xunit;

namespace OpenVSA.Ui.Tests
{
    /// <summary>
    /// The Syncfusion skin a chrome theme names must not cost the trace plot (<c>REQ-UI-081</c>,
    /// <c>REQ-UI-083</c>; see #420).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This guards a defect that was live and completely silent.</strong> Applying the skin
    /// to the shell <em>window</em> reaches the <c>DockingManager</c>, and a skinned
    /// <c>DockingManager</c> does not host its documents. Nothing throws and nothing logs: the
    /// document tab is still drawn and still says "Trace 1", the plot is still in the tree and still
    /// reports <c>Visibility.Visible</c> — and it is <c>0x0</c>, never loaded, with no visual parent
    /// above the fifth level. On screen the graticule, the annotation band and the trace window's
    /// own chrome are simply absent, and the region measures as one flat colour where it had fifty.
    /// </para>
    /// <para>
    /// <strong>Which is why the assertions are what they are.</strong> A test that asked whether the
    /// plot exists, or whether it is visible, passes on the broken build — both were true while the
    /// shell was drawing nothing. The load-bearing checks are the arranged size and the visual
    /// parent chain, because those are the two that actually went wrong.
    /// </para>
    /// </remarks>
    [Collection("Shell")]
    public class SkinnedShellTests
    {
        private readonly ShellHost _host;

        /// <summary>Takes the shared shell thread.</summary>
        /// <param name="host">The thread every shell in this collection is built on.</param>
        public SkinnedShellTests(ShellHost host)
        {
            _host = host;
        }

        [Fact]
        public void TheShippedThemesNameASkin()
        {
            // The skin is data the theme carries. If a shipped theme stopped naming one the shell
            // would still run and still theme its own surfaces, so nothing else here would fail.
            ThemeCatalogue catalogue = ThemeCatalogue.Shipped();

            Assert.Equal("FluentDark", catalogue.SkinFor(ThemeCatalogue.DarkName));
            Assert.Equal("FluentLight", catalogue.SkinFor(ThemeCatalogue.LightName));
        }

        [Fact]
        public void ASkinnedShellStillArrangesItsTracePlot()
        {
            _host.Run(() =>
            {
                ShellWindow shell = Shown();

                Assert.Null(ThemeCatalogue.SkinFailure);

                TracePlot plot = shell.DocumentArea.ActivePlot;

                Assert.NotNull(plot);

                // The two that were wrong. A plot the layout system never gave a size to draws
                // nothing, however present and however Visible it claims to be.
                Assert.True(
                    plot.ActualWidth > 0.0 && plot.ActualHeight > 0.0,
                    "The trace plot arranged to " + plot.ActualWidth + "x" + plot.ActualHeight +
                    ". A skinned DockingManager that does not host its documents leaves it 0x0.");

                Assert.True(plot.IsVisible, "The trace plot is not visible.");

                shell.Close();
            });
        }

        [Fact]
        public void TheTracePlotIsHostedInTheDocumentContainer()
        {
            // The size check above would also pass if the plot were parented somewhere unintended.
            // This walks the chain the working build has and the broken one lost.
            _host.Run(() =>
            {
                ShellWindow shell = Shown();

                DependencyObject cursor = shell.DocumentArea;
                int depth = 0;

                while (cursor != null)
                {
                    cursor = VisualTreeHelper.GetParent(cursor);
                    depth++;
                }

                Assert.True(
                    depth > 10,
                    "The document area's visual parent chain is only " + depth + " deep. It stops " +
                    "at five when the DockingManager is skinned and never hosts the document.");

                shell.Close();
            });
        }

        private static ShellWindow Shown()
        {
            var shell = new ShellWindow
            {
                PersistPreferences = false,
                Interactive = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -4000.0,
                Top = -4000.0,
                ShowInTaskbar = false,
                Width = 1280.0,
                Height = 720.0,
            };

            shell.Show();
            shell.UpdateLayout();

            return shell;
        }
    }
}
