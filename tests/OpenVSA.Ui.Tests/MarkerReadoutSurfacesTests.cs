using System;
using System.Collections.Generic;
using System.Linq;
using OpenVSA.Dsp.Spectrum;
using OpenVSA.Dsp.Windowing;
using OpenVSA.Measurement.Markers;
using OpenVSA.Ui.ToolWindows;
using Xunit;
using Xunit.Abstractions;

namespace OpenVSA.Ui.Tests
{
    /// <summary>
    /// <c>REQ-MKR-006</c>: the two readout surfaces, and that they cannot drift apart.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The criterion is explicit about what it is guarding against: "the Markers window row and the
    /// above-grid readout are compared after a marker move and must agree, since two independently
    /// computed readouts drifting apart is the failure this guards against". There were two, and they
    /// had drifted — one said <c>NAN</c> where the other said <c>--</c>, one spelled the frequency in
    /// engineering units and the other in fixed MHz, one forced a sign on the level.
    /// </para>
    /// <para>
    /// So these tests compare the surfaces rather than each surface against a literal. A test that
    /// asserted each one's expected text separately is precisely the test that passes while the two
    /// disagree.
    /// </para>
    /// </remarks>
    [Collection("Shell")]
    public class MarkerReadoutSurfacesTests
    {
        private readonly ShellHost _host;
        private readonly ITestOutputHelper _output;

        /// <summary>Takes the shared STA host.</summary>
        /// <param name="host">The host whose thread the shell is built on.</param>
        /// <param name="output">Where the two surfaces' text is written.</param>
        public MarkerReadoutSurfacesTests(ShellHost host, ITestOutputHelper output)
        {
            _host = host;
            _output = output;
        }

        [Fact]
        public void TheMarkersWindowListsAMarkerTheShellHasPlaced()
        {
            WithShell(shell =>
            {
                // The label is not the test. REQ-UI-032's window draws its rows whether or not there
                // are markers -- "Mkr 1  NAN" is the empty template -- so asserting a row exists
                // would pass against a window that lists nothing, which is exactly the state this
                // is here to catch.
                Assert.Contains(
                    RowsFor(shell), row => row.Contains("Mkr 1") && row.Contains("NAN"));

                GiveAFrame(shell, 'A');

                Marker placed = shell.Markers.AddNormal(1.001e9);
                shell.Markers.Select(placed);
                shell.RefreshMarkers();

                MarkerReadout readout = shell.MarkerReadouts.ActiveReadout;
                IReadOnlyList<string> rows = RowsFor(shell);

                Assert.NotNull(readout);
                _output.WriteLine(readout.XText + "  |  " + string.Join(" | ", rows));

                // The wiring defect this exists to stop: the window read a MarkerCollection nothing
                // ever added a marker to, while every real marker sat in the active context's own.
                // Its rows stayed on the empty template for ever, and no test noticed because none
                // went through the shell to place a marker.
                Assert.Contains(
                    rows, row => row.Contains(readout.XText) && row.Contains(readout.YText));
            });
        }

        [Fact]
        public void BothSurfacesShowTheSameValuesForTheSameMarker()
        {
            WithShell(shell =>
            {
                GiveAFrame(shell, 'A');

                Marker marker = shell.Markers.AddNormal(1.002e9);
                shell.Markers.Select(marker);
                shell.RefreshMarkers();

                MarkerReadout readout = shell.MarkerReadouts.ActiveReadout;

                Assert.NotNull(readout);

                string aboveGrid = shell.ActiveMarkerReadoutText;
                string windowRow = RowsFor(shell).First(r => r.Contains(readout.Label));

                _output.WriteLine("above grid: " + aboveGrid.Replace(Environment.NewLine, " / "));
                _output.WriteLine("window row: " + windowRow);

                // The values, not the layout. The above-grid readout is deliberately two lines
                // because it shares the upper band with the format and RBW, so the strings differ --
                // and every value in one has to appear in the other.
                Assert.Contains(readout.Label, aboveGrid, StringComparison.Ordinal);
                Assert.Contains(readout.XText, aboveGrid, StringComparison.Ordinal);
                Assert.Contains(readout.YText, aboveGrid, StringComparison.Ordinal);

                Assert.Contains(readout.XText, windowRow, StringComparison.Ordinal);
                Assert.Contains(readout.YText, windowRow, StringComparison.Ordinal);
            });
        }

        [Fact]
        public void TheyStillAgreeAfterTheMarkerMoves()
        {
            WithShell(shell =>
            {
                GiveAFrame(shell, 'A');

                Marker marker = shell.Markers.AddNormal(1.001e9);
                shell.Markers.Select(marker);
                shell.RefreshMarkers();

                string before = shell.ActiveMarkerReadoutText;

                shell.MarkerReadouts.MoveTo(marker, 1.004e9);
                shell.RefreshMarkers();

                MarkerReadout readout = shell.MarkerReadouts.ActiveReadout;
                string after = shell.ActiveMarkerReadoutText;
                string windowRow = RowsFor(shell).First(r => r.Contains(readout.Label));

                _output.WriteLine("before: " + before.Replace(Environment.NewLine, " / "));
                _output.WriteLine("after:  " + after.Replace(Environment.NewLine, " / "));
                _output.WriteLine("window: " + windowRow);

                // "compared after a marker move" -- the criterion's own wording, because a readout
                // computed once at placement agrees trivially and disagrees the moment anything
                // happens.
                Assert.NotEqual(before, after);
                Assert.Contains(readout.XText, after, StringComparison.Ordinal);
                Assert.Contains(readout.XText, windowRow, StringComparison.Ordinal);
                Assert.Contains(readout.YText, after, StringComparison.Ordinal);
                Assert.Contains(readout.YText, windowRow, StringComparison.Ordinal);
            });
        }

        [Fact]
        public void TheAboveGridReadoutFollowsTheActiveMarker()
        {
            WithShell(shell =>
            {
                GiveAFrame(shell, 'A');

                Marker first = shell.Markers.AddNormal(1.001e9);
                Marker second = shell.Markers.AddNormal(1.003e9);

                shell.Markers.Select(first);
                shell.RefreshMarkers();
                string one = shell.ActiveMarkerReadoutText;

                shell.Markers.Select(second);
                shell.RefreshMarkers();
                string two = shell.ActiveMarkerReadoutText;

                _output.WriteLine("marker 1: " + one.Replace(Environment.NewLine, " / "));
                _output.WriteLine("marker 2: " + two.Replace(Environment.NewLine, " / "));

                Assert.Contains(first.WindowLabel, one, StringComparison.Ordinal);
                Assert.Contains(second.WindowLabel, two, StringComparison.Ordinal);
                Assert.NotEqual(one, two);
            });
        }

        [Fact]
        public void TheWindowListsMarkersOnTracesOtherThanTheActiveOne()
        {
            WithShell(shell =>
            {
                GiveAFrame(shell, 'A', 'B');
                shell.Markers.AddNormal(1.001e9);

                // A second trace's markers. The requirement is explicit that the window lists every
                // marker on every trace, "since a marker on a trace you are not looking at is
                // exactly the one you forget about".
                MarkerCollection collection = shell.MarkerReadouts;
                MarkerSet other = collection.ForTrace('B');
                other.AddNormal(1.002e9);

                shell.RefreshMarkers();

                IReadOnlyList<string> rows = RowsFor(shell);

                _output.WriteLine(string.Join(" | ", rows));

                Assert.Contains(rows, row => row.Contains("A"));
                Assert.Contains(rows, row => row.Contains("B"));
            });
        }

        [Fact]
        public void AReadoutWithNoValueIsSpelledTheOneWay()
        {
            // The drift that was there: NAN above the grid against -- in the window. One constant
            // now, so a reader seeing both at once does not have to wonder whether they differ.
            Assert.Equal("--", MarkerReadout.NoValue);
        }

        /// <summary>
        /// A frame for the markers to read, spanning 1.000 to 1.005 GHz.
        /// </summary>
        /// <remarks>
        /// A readout is a <em>reading</em>, and a shell that has never measured has no frame to take
        /// one from — the collection holds a synthetic two-point placeholder until the first draw, so
        /// two markers at different frequencies both read out of range and produce the same text.
        /// That is correct behaviour and it makes a comparison of two surfaces vacuous, so the tests
        /// supply a frame rather than working around it.
        /// </remarks>
        private static void GiveAFrame(ShellWindow shell, params char[] traces)
        {
            var levels = new float[501];

            for (int i = 0; i < levels.Length; i++)
            {
                // A slope, so every bin reads a different level and a marker that moved shows it.
                levels[i] = -100.0f + (i * 0.1f);
            }

            foreach (char trace in traces)
            {
                shell.MarkerReadouts.Update(
                    trace,
                    SpectrumFrame.FromLevels(levels, 1.0e9, 1.0e4, WindowType.Uniform, 1.0));
            }
        }

        private static IReadOnlyList<string> RowsFor(ShellWindow shell)
        {
            IToolWindowSource source = shell.ToolWindows.SourceOf(ToolWindow.Markers);

            Assert.NotNull(source);
            source.Refresh();

            return source.Lines;
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
    }
}
