using System;
using System.Text;
using System.Windows;
using System.Windows.Media;
using OpenVSA.Ui.Layout;
using OpenVSA.Ui.Rendering;
using Xunit;
using Xunit.Abstractions;

namespace OpenVSA.Ui.Tests
{
    /// <summary>
    /// TEMPORARY (#420): why the trace plot disappears when a Syncfusion skin is applied.
    /// </summary>
    [Collection("Shell")]
    public class SkinDiagnosticTests
    {
        private readonly ShellHost _host;
        private readonly ITestOutputHelper _output;

        public SkinDiagnosticTests(ShellHost host, ITestOutputHelper output)
        {
            _host = host;
            _output = output;
        }

        [Fact]
        public void WhereDidThePlotGo()
        {
            _host.Run(() =>
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

                TraceDocumentArea area = shell.DocumentArea;
                var report = new StringBuilder();

                report.AppendLine("theme        : " + shell.ThemeName);
                report.AppendLine("skin failure : " +
                    (OpenVSA.Ui.Theming.ThemeCatalogue.SkinFailure ?? "(none)"));
                report.AppendLine("traces       : " + area.Traces.Count);
                report.AppendLine("active trace : " + area.ActiveTrace);

                TracePlot plot = area.ActivePlot;

                report.AppendLine("active plot  : " + (plot == null ? "NULL" : "present"));

                if (plot != null)
                {
                    report.AppendLine("  IsVisible     : " + plot.IsVisible);
                    report.AppendLine("  Visibility    : " + plot.Visibility);
                    report.AppendLine("  ActualWidth   : " + plot.ActualWidth);
                    report.AppendLine("  ActualHeight  : " + plot.ActualHeight);
                    report.AppendLine("  Opacity       : " + plot.Opacity);
                    report.AppendLine("  Background    : " + (plot.Background ?? (Brush)Brushes.Transparent));
                    report.AppendLine("  Children      : " + plot.Children.Count);
                    report.AppendLine("  IsLoaded      : " + plot.IsLoaded);

                    DependencyObject cursor = VisualTreeHelper.GetParent(plot);
                    int depth = 0;

                    while (cursor != null && depth < 24)
                    {
                        var element = cursor as FrameworkElement;

                        report.AppendLine("  parent[" + depth + "] " + cursor.GetType().Name +
                            (element == null
                                ? string.Empty
                                : "  " + element.ActualWidth + "x" + element.ActualHeight +
                                  "  vis=" + element.Visibility + " loaded=" + element.IsLoaded));

                        cursor = VisualTreeHelper.GetParent(cursor);
                        depth++;
                    }

                    if (depth == 0)
                    {
                        report.AppendLine("  THE PLOT HAS NO VISUAL PARENT - it is not in the tree.");
                    }
                }

                _output.WriteLine(report.ToString());
                shell.Close();
            });
        }
    }
}
