using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using OpenVSA.Hal;
using Xunit;
using Xunit.Abstractions;

namespace OpenVSA.Ui.Tests
{
    /// <summary>
    /// <c>REQ-SIM-001</c> from the shell's end: a front end that makes its own signal can be told
    /// what to make, and one that acquires from an instrument is not asked.
    /// </summary>
    /// <remarks>
    /// The menu is built from what the source declares, so these tests declare a source rather than
    /// connecting one. That is the point of the interface: the shell may not reference
    /// <c>OpenVSA.Hal.Sim</c> (<c>REQ-ARC-001</c>, and <c>LayeringTests</c> enforces it), so what it
    /// knows about a synthetic source is exactly what <c>ISyntheticSource</c> says — which is what a
    /// test can stand in for.
    /// </remarks>
    [Collection("Shell")]
    public class SyntheticSignalMenuTests
    {
        private readonly ShellHost _host;
        private readonly ITestOutputHelper _output;

        public SyntheticSignalMenuTests(ShellHost host, ITestOutputHelper output)
        {
            _host = host;
            _output = output;
        }

        [Fact]
        public void TheMenuOffersWhatTheSourceDeclaresAndNothingElse()
        {
            _host.Run(() =>
            {
                ShellWindow shell = Built();

                try
                {
                    var source = new DeclaredSource("QPSK", "16QAM");

                    MenuItem menu = shell.SyntheticSignalMenu(source);

                    string[] offered = Headers(menu);

                    Assert.Equal(
                        new[] { "Unmodulated carrier", "QPSK", "16QAM" }, offered);

                    _output.WriteLine(string.Join(", ", offered));
                }
                finally
                {
                    shell.Close();
                }
            });
        }

        [Fact]
        public void TheCurrentSignalIsTheOneTicked()
        {
            _host.Run(() =>
            {
                ShellWindow shell = Built();

                try
                {
                    var source = new DeclaredSource("QPSK", "16QAM") { Modulation = "16QAM" };

                    MenuItem menu = shell.SyntheticSignalMenu(source);

                    Assert.Equal(
                        new[] { "16QAM" },
                        Items(menu).Where(item => item.IsChecked).Select(Header).ToArray());

                    source.Modulation = null;

                    Assert.Equal(
                        new[] { "Unmodulated carrier" },
                        Items(shell.SyntheticSignalMenu(source))
                            .Where(item => item.IsChecked)
                            .Select(Header)
                            .ToArray());
                }
                finally
                {
                    shell.Close();
                }
            });
        }

        [Fact]
        public void ChoosingASignalSetsItAndTakesTheSymbolRateFromTheMeasurement()
        {
            _host.Run(() =>
            {
                ShellWindow shell = Built();

                try
                {
                    var source = new DeclaredSource("QPSK");

                    shell.Contexts.Active.Setup.SpanHz = 10e6;
                    shell.Contexts.Active.Setup.Demod.SymbolRateHz = 1.5e6;

                    Click(shell.SyntheticSignalMenu(source), "QPSK");

                    Assert.Equal("QPSK", source.Modulation);
                    Assert.Equal(1.5e6, source.SymbolRateHz);

                    // Said, not silently applied: the two are independent from here, and a reader
                    // has to be able to see which rate the source was given.
                    Assert.Contains(
                        shell.EventLog.Lines,
                        line => line.IndexOf("QPSK", StringComparison.Ordinal) >= 0 &&
                                line.IndexOf("Simulated source", StringComparison.Ordinal) >= 0);

                    _output.WriteLine(shell.EventLog.Lines.Last());
                }
                finally
                {
                    shell.Close();
                }
            });
        }

        [Fact]
        public void TheSourceAndTheMeasurementStayIndependentAfterwards()
        {
            // REQ-DEM-030's signature test exists to be run: a source that followed the analyser's
            // symbol rate for ever could never be made to disagree with it, and disagreeing on
            // purpose is a measurement.
            _host.Run(() =>
            {
                ShellWindow shell = Built();

                try
                {
                    var source = new DeclaredSource("QPSK");

                    shell.Contexts.Active.Setup.Demod.SymbolRateHz = 1e6;

                    Click(shell.SyntheticSignalMenu(source), "QPSK");

                    Assert.Equal(1e6, source.SymbolRateHz);

                    shell.Contexts.Active.Setup.Demod.SymbolRateHz = 1.0001e6;

                    Assert.Equal(1e6, source.SymbolRateHz);
                }
                finally
                {
                    shell.Close();
                }
            });
        }

        [Fact]
        public void ChoosingAnUnmodulatedCarrierTurnsTheModulationOff()
        {
            _host.Run(() =>
            {
                ShellWindow shell = Built();

                try
                {
                    var source = new DeclaredSource("QPSK") { Modulation = "QPSK" };

                    Click(shell.SyntheticSignalMenu(source), "Unmodulated carrier");

                    Assert.Null(source.Modulation);
                }
                finally
                {
                    shell.Close();
                }
            });
        }

        [Fact]
        public void ASignalTheSourceRefusesIsReportedRatherThanLeftLookingApplied()
        {
            _host.Run(() =>
            {
                ShellWindow shell = Built();

                try
                {
                    // A source that declares a format and then refuses it is a defect, and the
                    // shell's job is to say so rather than to leave a ticked menu item over a
                    // source transmitting something else.
                    var source = new DeclaredSource("QPSK") { Refuse = true };

                    Click(shell.SyntheticSignalMenu(source), "QPSK");

                    Assert.Null(source.Modulation);

                    Assert.Contains(
                        shell.EventLog.Lines,
                        line => line.IndexOf("Simulated source:", StringComparison.Ordinal) >= 0);

                    _output.WriteLine(shell.EventLog.Lines.Last());
                }
                finally
                {
                    shell.Close();
                }
            });
        }

        private static string[] Headers(MenuItem menu) =>
            Items(menu).Select(Header).ToArray();

        private static string Header(MenuItem item) => item.Header as string;

        private static IEnumerable<MenuItem> Items(MenuItem menu) =>
            menu.Items.OfType<MenuItem>();

        private static void Click(MenuItem menu, string header)
        {
            MenuItem item = Items(menu).FirstOrDefault(
                candidate => string.Equals(Header(candidate), header, StringComparison.Ordinal));

            Assert.NotNull(item);

            item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
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

        /// <summary>A source that declares what it can transmit, and remembers what it was told.</summary>
        private sealed class DeclaredSource : ISyntheticSource
        {
            private readonly ReadOnlyCollection<string> _modulations;

            internal DeclaredSource(params string[] modulations)
            {
                _modulations = new ReadOnlyCollection<string>(new List<string>(modulations));
            }

            /// <summary>Whether to refuse whatever it is asked for, as a broken source would.</summary>
            internal bool Refuse { get; set; }

            public IReadOnlyList<string> Modulations => _modulations;

            public double MinimumSamplesPerSymbol => 2.0;

            public string Modulation
            {
                get { return _modulation; }

                set
                {
                    if (Refuse && !string.IsNullOrEmpty(value))
                    {
                        throw new ArgumentException("this source will not.", nameof(value));
                    }

                    _modulation = value;
                }
            }

            public double SymbolRateHz { get; set; }

            public double RollOff { get; set; } = 0.35;

            private string _modulation;
        }
    }
}
