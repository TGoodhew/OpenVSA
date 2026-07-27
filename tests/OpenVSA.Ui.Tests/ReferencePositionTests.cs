using System;
using System.Collections.Generic;
using System.Windows;
using OpenVSA.Dsp.Spectrum;
using OpenVSA.Ui.Rendering;
using Xunit;

namespace OpenVSA.Ui.Tests
{
    /// <summary>
    /// <c>REQ-UI-013</c>: reference position defaults, and the layout they set.
    /// </summary>
    public class ReferencePositionTests
    {
        [Fact]
        public void EveryFormatHasAStatedDefault()
        {
            // "enumerated over the full REQ-DSP-041 format list so a format added later without a
            // default fails the test" - which is why this walks the enumeration rather than naming
            // the eight formats it happens to have today.
            foreach (TraceFormat format in ReferencePosition.Formats)
            {
                int percent = ReferencePosition.DefaultYPercentFor(format);

                Assert.True(
                    percent == ReferencePosition.TopPercent ||
                    percent == ReferencePosition.CentrePercent,
                    format + " defaults to " + percent + " %, which is neither 100 nor 50.");
            }
        }

        [Fact]
        public void TheMagnitudeFormatsReferenceTheTopAndTheRestTheCentre()
        {
            Assert.Equal(100, ReferencePosition.DefaultYPercentFor(TraceFormat.LogMagnitude));
            Assert.Equal(100, ReferencePosition.DefaultYPercentFor(TraceFormat.LinearMagnitude));

            foreach (TraceFormat format in new[]
            {
                TraceFormat.Real, TraceFormat.Imaginary, TraceFormat.WrappedPhase,
                TraceFormat.UnwrappedPhase, TraceFormat.GroupDelay, TraceFormat.IQ,
            })
            {
                Assert.Equal(50, ReferencePosition.DefaultYPercentFor(format));
            }
        }

        [Fact]
        public void AFormatWithNoStatedDefaultFails()
        {
            // The criterion's own failure mode, forced: a value outside the enumeration stands in
            // for a format added to REQ-DSP-041 and not given a default here. A switch that fell
            // through to 50 % would return quietly and the criterion would be untestable.
            Assert.Throws<ArgumentOutOfRangeException>(
                () => ReferencePosition.DefaultYPercentFor((TraceFormat)999));
        }

        [Fact]
        public void BothAxesAcceptZeroToOneHundredAndNothingElse()
        {
            for (int percent = 0; percent <= 100; percent++)
            {
                Assert.Equal(percent, ReferencePosition.Validate(percent, "percent"));
            }

            Assert.Throws<ArgumentOutOfRangeException>(() => ReferencePosition.Validate(-1, "percent"));
            Assert.Throws<ArgumentOutOfRangeException>(() => ReferencePosition.Validate(101, "percent"));

            Assert.Throws<ArgumentOutOfRangeException>(
                () => new PlotLayout(400, 300, 40, 0.0, -100.0, 10, 10, 101));

            Assert.Throws<ArgumentOutOfRangeException>(
                () => new PlotLayout(400, 300, 40, 0.0, -100.0, 10, 10, 50, -1));
        }

        [Fact]
        public void TheReferenceLineLandsAtTheRequestedFractionOfTheGridHeight()
        {
            // Every whole percentage, not three sampled ones: the rounding is where this goes wrong
            // and it goes wrong at particular values.
            var reference = new PlotLayout(400, 300, 40);
            int height = reference.Graticule.Height;

            for (int percent = 0; percent <= 100; percent++)
            {
                var layout = new PlotLayout(400, 300, 40, 0.0, -100.0, 10, 10, percent);

                double wanted = layout.Graticule.Bottom - 1 - percent / 100.0 * (height - 1);

                Assert.True(
                    Math.Abs(layout.ReferenceLineY() - wanted) <= 1.0,
                    percent + " % put the reference line at " + layout.ReferenceLineY() +
                    " rather than " + wanted + ".");
            }
        }

        [Fact]
        public void TheEndsOfTheRangeAreTheEdgesOfTheGrid()
        {
            var layout = new PlotLayout(400, 300, 40, 0.0, -100.0, 10, 10, 100, 0);

            Assert.Equal(layout.Graticule.Y, layout.ReferenceLineY());
            Assert.Equal(layout.Graticule.X, layout.ReferenceLineX());

            var other = new PlotLayout(400, 300, 40, 0.0, -100.0, 10, 10, 0, 100);

            Assert.Equal(other.Graticule.Bottom - 1, other.ReferenceLineY());
            Assert.Equal(other.Graticule.Right - 1, other.ReferenceLineX());
        }

        [Fact]
        public void TheScalingAndTheLayoutAgreeAboutWhereTheReferenceIs()
        {
            // The two halves of the requirement, checked against each other. TopFor decides what
            // value the top of the axis holds; ReferenceLineY decides which row the reference is
            // drawn on. A sign error in either is invisible until they are compared.
            const double Reference = -20.0;
            const double FullScale = 100.0;

            for (int percent = 0; percent <= 100; percent++)
            {
                double top = ReferencePosition.TopFor(Reference, FullScale, percent);

                var layout = new PlotLayout(
                    400, 300, 40, top, top - FullScale, 10, 10, percent);

                Assert.True(
                    Math.Abs(layout.ValueToY(Reference) - layout.ReferenceLineY()) <= 1,
                    "At " + percent + " % the reference level maps to row " +
                    layout.ValueToY(Reference) + " but the reference line is drawn at row " +
                    layout.ReferenceLineY() + ".");
            }
        }

        [Fact]
        public void AtOneHundredPerCentTheTopOfTheAxisIsTheReferenceLevel()
        {
            Assert.Equal(-20.0, ReferencePosition.TopFor(-20.0, 100.0, 100), 9);
            Assert.Equal(30.0, ReferencePosition.TopFor(-20.0, 100.0, 50), 9);
            Assert.Equal(80.0, ReferencePosition.TopFor(-20.0, 100.0, 0), 9);
        }

        [Fact]
        public void APlotTakesItsReferenceDefaultFromItsFormat()
        {
            // The layout decision the requirement is really about: a spectrum hangs from the top of
            // the grid, and an IQ display is centred, without anyone setting anything.
            Sta.Run(() =>
            {
                var plot = new TracePlot();
                plot.Measure(new Size(800.0, 600.0));
                plot.Arrange(new Rect(0.0, 0.0, 800.0, 600.0));

                Assert.Equal(TraceFormat.LogMagnitude, plot.CurrentFormat);
                Assert.Equal(100, plot.YReferencePercent);

                Assert.True(plot.SetFormat(TraceFormat.IQ));
                Assert.Equal(50, plot.YReferencePercent);

                Assert.True(plot.SetFormat(TraceFormat.LinearMagnitude));
                Assert.Equal(100, plot.YReferencePercent);

                Assert.True(plot.SetFormat(TraceFormat.WrappedPhase));
                Assert.Equal(50, plot.YReferencePercent);
            });
        }

        [Fact]
        public void MovingTheReferenceMovesTheAxisAndNotTheReferenceLevel()
        {
            // At 100 % the reference level is the top of the graticule; at 50 % it is the middle,
            // so the top rises by half a full scale and the bottom rises with it. The level itself
            // does not move - that is the difference between a layout decision and a re-range.
            Sta.Run(() =>
            {
                var plot = new TracePlot();
                plot.Measure(new Size(800.0, 600.0));
                plot.Arrange(new Rect(0.0, 0.0, 800.0, 600.0));

                double top = plot.TopDbm;
                double fullScale = plot.FullScaleDb;

                plot.YReferencePercent = 50;

                Assert.Equal(top + fullScale / 2.0, plot.TopDbm, 6);
                Assert.Equal(plot.TopDbm - fullScale, plot.BottomDbm, 6);

                plot.YReferencePercent = 100;

                Assert.Equal(top, plot.TopDbm, 6);
            });
        }

        [Fact]
        public void APlotRefusesAReferencePositionOutsideTheRange()
        {
            Sta.Run(() =>
            {
                var plot = new TracePlot();

                Assert.Throws<ArgumentOutOfRangeException>(() => plot.YReferencePercent = 101);
                Assert.Throws<ArgumentOutOfRangeException>(() => plot.YReferencePercent = -1);

                var options = new TraceDisplayOptions();

                Assert.Throws<ArgumentOutOfRangeException>(() => options.XReferencePercent = 101);
                Assert.Throws<ArgumentOutOfRangeException>(() => options.XReferencePercent = -1);
            });
        }

        [Fact]
        public void EveryFormatIsCoveredByTheEnumeration()
        {
            // The enumeration and the format list are the same list, so a format added to one and
            // not the other cannot pass unnoticed.
            var seen = new List<TraceFormat>(ReferencePosition.Formats);

            Assert.Equal(Enum.GetValues(typeof(TraceFormat)).Length, seen.Count);
        }
    }
}
