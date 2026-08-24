using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using OpenVSA.Core;
using OpenVSA.Demod.Chain;
using OpenVSA.Demod.Signal;
using OpenVSA.Measurement.Contexts;
using OpenVSA.Measurement.State;
using OpenVSA.Synthesis;
using OpenVSA.TestHarness.Synthesis;
using OpenVSA.Ui.Rendering;
using Xunit;
using Xunit.Abstractions;

namespace OpenVSA.Ui.Tests
{
    /// <summary>
    /// The shell's end of digital demodulation: choosing the measurement type, and a demodulated
    /// result reaching the display (<c>REQ-UI-061</c>, <c>REQ-DEM-001</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Driven through the context, which is how a result reaches the shell in the product: the pump
    /// hands each block to every context, a context whose setup asks for demodulation raises
    /// <c>ResultAnalysed</c>, and the shell draws it. Calling the shell's own handler instead would
    /// prove the handler works and say nothing about whether anything reaches it.
    /// </para>
    /// <para>
    /// <strong>Nothing here waits on a dispatcher priority.</strong> The first version drained the
    /// queue with <c>Dispatcher.Invoke</c> at <c>Render</c> and <c>ApplicationIdle</c> to let the
    /// shell's marshalling run. It passed here in six seconds and hung CI for half an hour: a
    /// priority-ordered wait only returns when the queue drains to that priority, and on a headless
    /// runner there is no guarantee it ever does. The shell now shows a result inline when it is
    /// already on the UI thread, which is both less work in the product and a test that cannot wait
    /// for something that may not come.
    /// </para>
    /// </remarks>
    [Collection("Shell")]
    public class DemodulationDisplayTests
    {
        private const double CentreHz = 1e9;

        private readonly ShellHost _host;
        private readonly ITestOutputHelper _output;

        public DemodulationDisplayTests(ShellHost host, ITestOutputHelper output)
        {
            _host = host;
            _output = output;
        }

        [Fact]
        public void ChoosingDigitalDemodulationPutsTheActiveContextIntoIt()
        {
            _host.Run(() =>
            {
                ShellWindow shell = Built();

                try
                {
                    MeasurementContext active = shell.Contexts.Active;

                    Assert.Equal(MeasurementKind.Spectrum, active.Setup.Kind);
                    Assert.False(active.IsDemodulating);

                    Choose(shell, "Digital Demodulation");

                    Assert.Equal(MeasurementKind.DigitalDemodulation, active.Setup.Kind);
                    Assert.True(active.IsDemodulating);

                    // REQ-DEM-030's default arrives with the choice.
                    Assert.Equal(active.Setup.SpanHz / 2.0, active.Setup.Demod.SymbolRateHz);

                    // And the active window is ready to draw one.
                    Assert.Equal(
                        ResultTraceKind.Constellation, shell.Documents.ActivePlot.ResultKind);
                }
                finally
                {
                    shell.Close();
                }
            });
        }

        [Fact]
        public void AResultLengthTooShortForTheFormatIsWarnedAboutInTheEventLog()
        {
            // REQ-DEM-031: "The UI shall warn when Result Length is below the recommended minimum
            // for the chosen format", and its criterion names the case — 1024-QAM at a Result
            // Length of 50 produces "a visible, specific warning naming the recommended minimum".
            //
            // The chain works out what to say, because it is the thing that knows both numbers; the
            // shell is where it becomes visible. Said ONCE rather than per block: a demodulation
            // raises a result many times a second and this notice is a property of the setup.
            _host.Run(() =>
            {
                ShellWindow shell = Built();

                try
                {
                    Choose(shell, "Digital Demodulation");

                    MeasurementContext active = shell.Contexts.Active;

                    active.Setup.Demod.SymbolRateHz =
                        12.8e6 / SyntheticSymbolSource.DefaultSamplesPerSymbol;

                    active.Setup.Demod.Format = "1024QAM";
                    active.Setup.Demod.ResultLengthSymbols = 50;
                    active.Setup.Demod.MeasurementFilter = PulseFilterType.None;

                    for (int block = 0; block < 3; block++)
                    {
                        using (IqBlock samples = Block(400))
                        {
                            active.Analyse(samples);
                        }
                    }

                    string log = string.Join(
                        Environment.NewLine, shell.EventLog.Lines);

                    _output.WriteLine(log);

                    Assert.Contains("below the 1024 recommended", log, StringComparison.Ordinal);
                    Assert.Contains("REQ-DEM-031", log, StringComparison.Ordinal);

                    // Once, not three times: three blocks were analysed.
                    int said = 0;
                    int at = log.IndexOf("below the 1024 recommended", StringComparison.Ordinal);

                    while (at >= 0)
                    {
                        said++;
                        at = log.IndexOf(
                            "below the 1024 recommended", at + 1, StringComparison.Ordinal);
                    }

                    Assert.Equal(1, said);
                }
                finally
                {
                    shell.Close();
                }
            });
        }

        [Fact]
        public void ADemodulatedBlockReachesTheDisplay()
        {
            _host.Run(() =>
            {
                ShellWindow shell = Built();

                try
                {
                    Choose(shell, "Digital Demodulation");

                    MeasurementContext active = shell.Contexts.Active;

                    // The symbol rate the burst was generated at, entered as REQ-DEM-030 requires:
                    // supplied, not estimated.
                    active.Setup.Demod.SymbolRateHz =
                        12.8e6 / SyntheticSymbolSource.DefaultSamplesPerSymbol;

                    active.Setup.Demod.ResultLengthSymbols = 128;

                    // The generator shapes with a full raised cosine, so the matched half must not
                    // be applied again -- see PulseFilterType.None.
                    active.Setup.Demod.MeasurementFilter = PulseFilterType.None;

                    using (IqBlock block = Block(400))
                    {
                        active.Analyse(block);
                    }

                    DemodResult result = shell.LatestResult;

                    Assert.NotNull(result);
                    Assert.Equal(128, result.Trace.SymbolCount);

                    _output.WriteLine(
                        result.Trace.Modulation + ", EVM " + result.EvmPercent + " %rms");

                    // The display has it, not just the shell.
                    TracePlot plot = shell.Documents.ActivePlot;

                    Assert.Same(result.Trace, plot.Result);
                    Assert.True(plot.IsShowingResult);
                }
                finally
                {
                    shell.Close();
                }
            });
        }

        [Fact]
        public void LeavingDigitalDemodulationTakesTheResultOffTheScreen()
        {
            _host.Run(() =>
            {
                ShellWindow shell = Built();

                try
                {
                    Choose(shell, "Digital Demodulation");

                    MeasurementContext active = shell.Contexts.Active;

                    active.Setup.Demod.SymbolRateHz =
                        12.8e6 / SyntheticSymbolSource.DefaultSamplesPerSymbol;

                    active.Setup.Demod.ResultLengthSymbols = 128;
                    active.Setup.Demod.MeasurementFilter = PulseFilterType.None;

                    using (IqBlock block = Block(400))
                    {
                        active.Analyse(block);
                    }

                    Assert.NotNull(shell.LatestResult);

                    Choose(shell, "Spectrum");

                    // A constellation left on screen after its measurement was turned off is a real
                    // measurement of a signal that may since have gone.
                    Assert.Null(shell.LatestResult);
                    Assert.Null(shell.Documents.ActivePlot.Result);
                    Assert.Equal(ResultTraceKind.None, shell.Documents.ActivePlot.ResultKind);
                    Assert.Equal(MeasurementKind.Spectrum, active.Setup.Kind);
                }
                finally
                {
                    shell.Close();
                }
            });
        }

        [Fact]
        public void ASettingThatCannotBeDemodulatedIsSaidRatherThanSwallowed()
        {
            _host.Run(() =>
            {
                ShellWindow shell = Built();

                try
                {
                    Choose(shell, "Digital Demodulation");

                    MeasurementContext active = shell.Contexts.Active;

                    // Three orders out: the resampling leaves a couple of samples, which is not a
                    // waveform. The measurement must not stop, and the user must be told.
                    active.Setup.Demod.SymbolRateHz = 1000.0;

                    using (IqBlock block = Block(400))
                    {
                        active.Analyse(block);
                    }

                    Assert.Null(shell.LatestResult);

                    string[] logged = shell.EventLog.Lines.ToArray();

                    Assert.Contains(
                        logged,
                        line => line.IndexOf("Demodulation:", StringComparison.Ordinal) >= 0);

                    _output.WriteLine(
                        logged.Last(
                            line => line.IndexOf("Demodulation:", StringComparison.Ordinal) >= 0));
                }
                finally
                {
                    shell.Close();
                }
            });
        }

        /// <summary>Clicks a measurement type under Analysis &gt; Type.</summary>
        /// <remarks>
        /// Through the menu the shell built, rather than by calling the handler: what is being
        /// asserted includes that the item is there, is enabled and is wired to something.
        /// </remarks>
        private static void Choose(ShellWindow shell, string type)
        {
            foreach (object candidate in shell.MenuBar.Items)
            {
                var top = candidate as MenuItem;
                MenuItem found = top == null ? null : Descendant(top, type);

                if (found != null)
                {
                    found.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

                    return;
                }
            }

            throw new InvalidOperationException("There is no '" + type + "' measurement type.");
        }

        private static MenuItem Descendant(MenuItem parent, string name)
        {
            foreach (object candidate in parent.Items)
            {
                var item = candidate as MenuItem;

                if (item == null)
                {
                    continue;
                }

                if (string.Equals(
                    ShellMenus.NameOf(item.Header as string), name, StringComparison.Ordinal))
                {
                    return item;
                }

                MenuItem deeper = Descendant(item, name);

                if (deeper != null)
                {
                    return deeper;
                }
            }

            return null;
        }

        private static ShellWindow Built()
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

            return shell;
        }

        private static IqBlock Block(int symbols)
        {
            var source = new SyntheticSymbolSource
            {
                Scheme = ModulationScheme.Qpsk(),
                Seed = 4,
            };

            return source.Generate(symbols).ToBlock(CentreHz, DateTime.UtcNow);
        }
    }
}
