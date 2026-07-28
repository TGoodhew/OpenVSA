using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Controls;
using System.Windows.Media;
using OpenVSA.Measurement.Markers;
using OpenVSA.Ui.Rendering;
using OpenVSA.Ui.ToolWindows;
using Xunit;
using Xunit.Abstractions;

namespace OpenVSA.Ui.Tests
{
    /// <summary>
    /// <c>REQ-UI-032</c>: the Markers window's labels, fields and value spellings.
    /// </summary>
    /// <remarks>
    /// The criterion is about exact strings — "every listed readout label and field appears with
    /// exactly the spelling given" and "asserted as exact strings" for <c>NAN</c> and <c>INF</c> —
    /// so these are literal comparisons throughout.
    /// </remarks>
    public class MarkerWindowReadoutTests
    {
        private readonly ITestOutputHelper _output;

        /// <summary>Takes xunit's output sink.</summary>
        /// <param name="output">Where the rendered rows are written.</param>
        public MarkerWindowReadoutTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void EveryReadoutLabelIsSpelledExactlyAsTheRequirementWritesIt()
        {
            Assert.Equal(
                new[]
                {
                    "Mkr N", "Mkr NΔTR", "Freq N", "OBW", "BW", "ACP Ref", "Power", "Density",
                    "Limit",
                },
                MarkerWindowReadouts.Labels.ToArray());

            // The expansions the house style avoids, absent in both directions.
            Assert.DoesNotContain("ACP Reference", MarkerWindowReadouts.Labels);
            Assert.DoesNotContain("Marker", MarkerWindowReadouts.Labels);
            Assert.DoesNotContain("Occupied Bandwidth", MarkerWindowReadouts.Labels);
        }

        [Fact]
        public void EveryFieldIsSpelledExactlyAsTheRequirementWritesIt()
        {
            Assert.Equal(
                new[] { "Carrier", "Channel Type", "Layer", "Sym" },
                MarkerWindowReadouts.Fields.ToArray());

            // "Sym", not "Symbol" — the terse form the requirement uses.
            Assert.DoesNotContain("Symbol", MarkerWindowReadouts.Fields);
        }

        [Fact]
        public void TheNumberedLabelsTakeTheMarkerNumber()
        {
            Assert.Equal("Mkr 3", MarkerWindowReadouts.Numbered("Mkr N", 3));
            Assert.Equal("Mkr 3ΔTR", MarkerWindowReadouts.Numbered("Mkr NΔTR", 3));
            Assert.Equal("Freq 12", MarkerWindowReadouts.Numbered("Freq N", 12));
        }

        [Fact]
        public void AnInvalidValueRendersTheLiteralNanAndAnOverflowTheLiteralInf()
        {
            // "not a framework-default 'NaN'/'∞' or a blank — asserted as exact strings".
            Assert.Equal("NAN", MarkerWindowReadouts.Value(double.NaN));
            Assert.Equal("INF", MarkerWindowReadouts.Value(double.PositiveInfinity));
            Assert.Equal("-INF", MarkerWindowReadouts.Value(double.NegativeInfinity));

            // What the framework would have produced, and what the requirement rejects.
            Assert.NotEqual(double.NaN.ToString(), MarkerWindowReadouts.Value(double.NaN));
            Assert.NotEqual("∞", MarkerWindowReadouts.Value(double.PositiveInfinity));
            Assert.NotEqual(string.Empty, MarkerWindowReadouts.Value(double.NaN));

            // The unit is dropped from both: NAN dBm claims a unit for a number that does not exist.
            Assert.Equal("NAN", MarkerWindowReadouts.Value(double.NaN, "dBm"));
            Assert.Equal("INF", MarkerWindowReadouts.Value(double.PositiveInfinity, "dBm"));

            // And a real value still carries its unit.
            Assert.Equal("-20.33 dBm", MarkerWindowReadouts.Value(-20.334, "dBm"));
        }

        [Fact]
        public void TwoDimensionalIqFormatsOfferBothPairsAndDefaultToMagAndPhase()
        {
            Assert.Equal(IqReadoutPair.MagnitudeAndPhase, MarkerWindowReadouts.DefaultIqPair);

            Assert.Equal("Mag & Phase", MarkerWindowReadouts.NameOf(IqReadoutPair.MagnitudeAndPhase));
            Assert.Equal("Real & Imag", MarkerWindowReadouts.NameOf(IqReadoutPair.RealAndImaginary));

            // A point on the +45° diagonal: magnitude √2, phase 45°.
            string[] polar = MarkerWindowReadouts.IqComponents(
                IqReadoutPair.MagnitudeAndPhase, 1.0, 1.0);

            Assert.Contains("1.414214", polar[0]);
            Assert.Contains("45.000", polar[1]);

            string[] rectangular = MarkerWindowReadouts.IqComponents(
                IqReadoutPair.RealAndImaginary, 1.0, 1.0);

            Assert.Contains("1.000000", rectangular[0]);
            Assert.Contains("1.000000", rectangular[1]);

            // A magnitude of zero has no direction, so its phase is NAN rather than zero degrees.
            string[] origin = MarkerWindowReadouts.IqComponents(
                IqReadoutPair.MagnitudeAndPhase, 0.0, 0.0);

            Assert.Equal("NAN", origin[1]);
        }

        [Fact]
        public void RowsOfDifferingDigitContentLineUpInAColumn()
        {
            // REQ-UI-033's second criterion — "marker readouts of differing digit content align in
            // a column, which is the property the fixed-width face exists to provide". Asserted on
            // the character positions, which is what a fixed-width face turns into pixels.
            var rows = new[]
            {
                MarkerWindowReadouts.Row("Mkr 1", MarkerWindowReadouts.Value(-20.33, "dBm")),
                MarkerWindowReadouts.Row("Channel Type", MarkerWindowReadouts.Value(1234.5, "Hz")),
                MarkerWindowReadouts.Row("BW", MarkerWindowReadouts.NotANumber),
                MarkerWindowReadouts.Row("ACP Ref", MarkerWindowReadouts.Value(-1.0, "dB")),
            };

            foreach (string row in rows)
            {
                _output.WriteLine("|" + row + "|");
            }

            // Every value ENDS in the same column, whatever the label's length or the digits. The
            // value column is right-aligned, which is what makes a column of numbers readable: the
            // units line up and so do the digits above the point. Asserting the start instead would
            // be asserting that every number has the same width, which is the opposite of the
            // property wanted here.
            Assert.Single(rows.Select(r => r.Length).Distinct());

            Assert.Equal(
                MarkerWindowReadouts.LabelColumnWidth + MarkerWindowReadouts.ValueColumnWidth,
                rows[0].Length);
        }

        [Fact]
        public void MissingArgumentsAreRefused()
        {
            Assert.Throws<ArgumentNullException>(() => MarkerWindowReadouts.Numbered(null, 1));
            Assert.Throws<ArgumentNullException>(() => MarkerWindowReadouts.Row(null));
            Assert.Throws<ArgumentNullException>(() => MarkerWindowReadouts.Row("Mkr 1", null));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => MarkerWindowReadouts.NameOf((IqReadoutPair)99));
        }
    }

    /// <summary>
    /// <c>REQ-UI-032</c> and <c>REQ-UI-033</c> at the window itself.
    /// </summary>
    [Collection("Shell")]
    public class MarkersWindowTests
    {
        private readonly ShellHost _host;

        /// <summary>Takes the shared shell thread.</summary>
        /// <param name="host">The thread every shell in this collection is built on.</param>
        public MarkersWindowTests(ShellHost host)
        {
            _host = host;
        }

        [Fact]
        public void EveryLabelAndFieldAppearsInTheWindow()
        {
            _host.Run(() =>
            {
                var shell = Built();

                string shown = shell.ToolWindows.MarkersText.Text;

                foreach (string field in MarkerWindowReadouts.Fields)
                {
                    Assert.Contains(field, shown);
                }

                foreach (string label in MarkerWindowReadouts.Labels)
                {
                    // The three numbered ones appear with a number substituted, so the expected
                    // text is the numbered form rather than the template.
                    string expected = MarkerWindowReadouts.Numbered(label, 1);

                    Assert.Contains(expected, shown);
                }
            });
        }

        [Fact]
        public void UncomputedReadingsShowNanRatherThanBeingAbsent()
        {
            // REQ-DEM-071 puts it plainly: a metric that is applicable but not yet computed shows
            // NAN rather than a stale value. A row that appeared only once it had a value would be
            // a row a user could not find.
            _host.Run(() =>
            {
                var shell = Built();

                string shown = shell.ToolWindows.MarkersText.Text;

                Assert.Contains("OBW", shown);
                Assert.Contains("NAN", shown);

                // And a reading that arrives replaces the NAN with a number.
                var source = (MarkerWindowSource)shell.ToolWindows.SourceOf(ToolWindow.Markers);

                source.Readings[MarkerWindowReadouts.OccupiedBandwidthLabel] = 1.2345;
                shell.ToolWindows.Refresh(ToolWindow.Markers);

                Assert.Contains("1.23", shell.ToolWindows.MarkersText.Text);
            });
        }

        [Fact]
        public void TheWindowHasItsOwnBackgroundColour()
        {
            // "The window has its own background colour (MarkerWindowBackground)". Before this the
            // element was in the colour picker and changed nothing on screen.
            _host.Run(() =>
            {
                var shell = Built();

                var wanted = new PlotColor(0x12, 0x34, 0x56);

                shell.Colours.Set("OpenVSA.MarkerWindowBackground", wanted);
                shell.RefreshColours();

                var brush = shell.ToolWindows.MarkersPane.Background as SolidColorBrush;

                Assert.True(brush != null, "The Markers pane has no background brush.");
                Assert.Equal(Color.FromArgb(wanted.A, wanted.R, wanted.G, wanted.B), brush.Color);
            });
        }

        [Fact]
        public void TheWindowsFontIsFixedWidthByTheResolvedFacesPitch()
        {
            // REQ-UI-033: "asserted by querying the resolved typeface's pitch rather than its name,
            // so a proportional face substituted for a missing font fails".
            _host.Run(() =>
            {
                var shell = Built();

                TextBlock text = shell.ToolWindows.MarkersText;

                Assert.True(
                    FontPreferences.IsFixedPitch(text.FontFamily.Source),
                    "The Markers window is in '" + text.FontFamily.Source + "', which is not fixed pitch.");

                // And it is the Marker slot's face, not the Annotation slot's.
                var fonts = new FontPreferences();

                Assert.Equal(fonts.ResolveFamily(FontSlot.Marker), text.FontFamily.Source);
            });
        }

        private static ShellWindow Built() =>
            new ShellWindow { PersistPreferences = false, Interactive = false };
    }
}
