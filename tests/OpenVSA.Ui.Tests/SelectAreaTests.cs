using System;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using OpenVSA.Dsp.Spectrum;
using OpenVSA.Dsp.Windowing;
using OpenVSA.Ui.Rendering;
using Xunit;
using Xunit.Abstractions;

namespace OpenVSA.Ui.Tests
{
    /// <summary>
    /// <c>REQ-DSP-023</c>'s Select Area gesture: dragging a region across a trace reports the
    /// frequencies it covers.
    /// </summary>
    public class SelectAreaTests
    {
        private const double StartHz = 1.000e9;
        private const double BinHz = 12.5e3;
        private const int Points = 801;

        private readonly ITestOutputHelper _output;

        public SelectAreaTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void ADragReportsTheFrequenciesItCovers()
        {
            OnStaThread(() =>
            {
                TracePlot plot = Drawn();
                plot.SelectAreaEnabled = true;

                Rect graticule = plot.GraticuleBounds;

                double fromX = graticule.Left + graticule.Width * 0.25;
                double toX = graticule.Left + graticule.Width * 0.60;

                AreaSelectedEventArgs reported = null;
                plot.AreaSelected += (sender, area) => reported = area;

                Assert.True(plot.BeginSelectArea(new Point(fromX, graticule.Top + 10.0)));
                plot.ExtendSelectArea(new Point(toX, graticule.Top + 20.0));
                Assert.True(plot.EndSelectArea(new Point(toX, graticule.Top + 20.0)));

                Assert.NotNull(reported);

                double span = BinHz * (Points - 1);

                _output.WriteLine(reported.ToString());

                // Within a bin, which is what a dragged edge is worth.
                Assert.True(Math.Abs(reported.StartHz - (StartHz + 0.25 * span)) < BinHz);
                Assert.True(Math.Abs(reported.StopHz - (StartHz + 0.60 * span)) < BinHz);
            });
        }

        [Fact]
        public void ADragRightToLeftMeansTheSameRegion()
        {
            // Requiring a direction would make half the gestures appear to do nothing.
            OnStaThread(() =>
            {
                TracePlot plot = Drawn();
                plot.SelectAreaEnabled = true;

                Rect g = plot.GraticuleBounds;
                double left = g.Left + g.Width * 0.30;
                double right = g.Left + g.Width * 0.70;

                AreaSelectedEventArgs forwards = null;
                AreaSelectedEventArgs backwards = null;

                plot.AreaSelected += (sender, area) =>
                {
                    if (forwards == null)
                    {
                        forwards = area;
                    }
                    else
                    {
                        backwards = area;
                    }
                };

                plot.BeginSelectArea(new Point(left, g.Top + 5.0));
                plot.EndSelectArea(new Point(right, g.Top + 5.0));

                plot.BeginSelectArea(new Point(right, g.Top + 5.0));
                plot.EndSelectArea(new Point(left, g.Top + 5.0));

                Assert.NotNull(backwards);
                Assert.Equal(forwards.StartHz, backwards.StartHz, 3);
                Assert.Equal(forwards.StopHz, backwards.StopHz, 3);
                Assert.True(forwards.StopHz > forwards.StartHz);
            });
        }

        [Fact]
        public void AClickThatMovedALittleIsNotASelection()
        {
            // A zoom to two pixels of span is far harder to undo than it was to ask for.
            OnStaThread(() =>
            {
                TracePlot plot = Drawn();
                plot.SelectAreaEnabled = true;

                Rect g = plot.GraticuleBounds;
                bool raised = false;

                plot.AreaSelected += (sender, area) => raised = true;

                plot.BeginSelectArea(new Point(g.Left + 100.0, g.Top + 5.0));

                Assert.False(
                    plot.EndSelectArea(
                        new Point(g.Left + 100.0 + TracePlot.MinimumSelectionDip / 2.0, g.Top + 5.0)));
                Assert.False(raised);
            });
        }

        [Fact]
        public void NoDragHappensWhenTheToolIsOff()
        {
            // Off by default: a plot that zoomed on every drag would turn an imprecise click into
            // a change of measurement.
            OnStaThread(() =>
            {
                TracePlot plot = Drawn();

                Assert.False(plot.SelectAreaEnabled);

                Rect g = plot.GraticuleBounds;

                Assert.False(plot.BeginSelectArea(new Point(g.Left + 50.0, g.Top + 5.0)));
                Assert.False(plot.IsSelectingArea);
            });
        }

        [Fact]
        public void ADragOutsideTheGraticuleStartsNothing()
        {
            OnStaThread(() =>
            {
                TracePlot plot = Drawn();
                plot.SelectAreaEnabled = true;

                Assert.False(plot.BeginSelectArea(new Point(2.0, 2.0)));
            });
        }

        [Fact]
        public void ADragOnAPlotWithNoTraceStartsNothing()
        {
            OnStaThread(() =>
            {
                var plot = new TracePlot { SelectAreaEnabled = true };

                plot.Measure(new Size(600.0, 400.0));
                plot.Arrange(new Rect(0.0, 0.0, 600.0, 400.0));

                Rect g = plot.GraticuleBounds;

                Assert.False(plot.BeginSelectArea(new Point(g.Left + 50.0, g.Top + 5.0)));
            });
        }

        [Fact]
        public void ACancelledDragReportsNothing()
        {
            OnStaThread(() =>
            {
                TracePlot plot = Drawn();
                plot.SelectAreaEnabled = true;

                Rect g = plot.GraticuleBounds;
                bool raised = false;

                plot.AreaSelected += (sender, area) => raised = true;

                plot.BeginSelectArea(new Point(g.Left + 40.0, g.Top + 5.0));
                Assert.True(plot.IsSelectingArea);

                plot.CancelSelectArea();

                Assert.False(plot.IsSelectingArea);
                Assert.False(plot.EndSelectArea(new Point(g.Left + 300.0, g.Top + 5.0)));
                Assert.False(raised);
            });
        }

        [Fact]
        public void TheFrequencyAtAPositionIsInterpolatedAcrossTheGraticule()
        {
            // Not snapped to the nearest drawn point: a dragged edge is a position on the axis, and
            // snapping would make the selection up to half a point wider or narrower than drawn.
            OnStaThread(() =>
            {
                TracePlot plot = Drawn();
                Rect g = plot.GraticuleBounds;

                double span = BinHz * (Points - 1);

                Assert.Equal(StartHz, plot.FrequencyAt(new Point(g.Left, g.Top)), 0);
                Assert.Equal(StartHz + span, plot.FrequencyAt(new Point(g.Right, g.Top)), 0);
                Assert.Equal(
                    StartHz + span / 2.0,
                    plot.FrequencyAt(new Point(g.Left + g.Width / 2.0, g.Top)),
                    0);

                // Clamped rather than extrapolated outside the graticule.
                Assert.Equal(StartHz, plot.FrequencyAt(new Point(-500.0, g.Top)), 0);
                Assert.Equal(StartHz + span, plot.FrequencyAt(new Point(99999.0, g.Top)), 0);
            });
        }

        [Fact]
        public void AnInvertedOrUnmeasurableRegionIsRefused()
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => new AreaSelectedEventArgs(2e9, 1e9));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => new AreaSelectedEventArgs(1e9, 1e9));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => new AreaSelectedEventArgs(double.NaN, 1e9));
        }

        [Fact]
        public void ARegionKnowsItsSpanAndCentre()
        {
            var area = new AreaSelectedEventArgs(1.000e9, 1.002e9);

            Assert.Equal(2e6, area.SpanHz, 3);
            Assert.Equal(1.001e9, area.CentreHz, 3);
        }

        /// <summary>A plot with a trace drawn on it, measured and arranged.</summary>
        private static TracePlot Drawn()
        {
            var plot = new TracePlot();

            plot.Measure(new Size(900.0, 500.0));
            plot.Arrange(new Rect(0.0, 0.0, 900.0, 500.0));
            plot.UpdateLayout();

            var levels = new float[Points];

            for (int i = 0; i < Points; i++)
            {
                levels[i] = -90.0f;
            }

            levels[Points / 2] = -20.0f;

            SpectrumFrame frame = SpectrumFrame.FromLevels(
                levels, StartHz, BinHz, WindowType.Uniform, 1.0);

            var marshal = new RenderMarshal { Columns = plot.GraticuleColumns };

            marshal.Offer(frame);
            plot.Show(marshal.TakeForRender());

            return plot;
        }

        private static void OnStaThread(Action action)
        {
            ExceptionDispatchInfo failure = null;

            var thread = new Thread(() =>
            {
                try
                {
                    action();
                }
                catch (Exception e)
                {
                    failure = ExceptionDispatchInfo.Capture(e);
                }
            });

            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();

            if (failure != null)
            {
                failure.Throw();
            }
        }
    }
}
