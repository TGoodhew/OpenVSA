using System;
using System.Linq;
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
    /// #397: the displayed frequency range, annotated when it differs from the measured one.
    /// </summary>
    /// <remarks>
    /// This is what Area Select ▸ Scale X was refused for until now (<c>REQ-UI-063</c>, #264).
    /// Magnifying the display without saying so leaves the centre and span readouts describing a
    /// band the trace is no longer showing, which is a lie the display tells silently.
    /// </remarks>
    public class DisplayRangeTests
    {
        private const double StartHz = 999.0e6;
        private const double BinHz = 5.0e3;
        private const int Points = 401;

        private readonly ITestOutputHelper _output;

        /// <summary>Takes xunit's output sink.</summary>
        /// <param name="output">Where the annotation is written.</param>
        public DisplayRangeTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void AnUnmagnifiedTraceAnnotatesOneRange()
        {
            // "Show it only when the displayed range differs from the measured one." A second pair
            // of numbers saying the same thing as the first trains a reader to ignore the line
            // that matters when it does appear.
            OnStaThread(() =>
            {
                TracePlot plot = Shown();

                Assert.False(plot.IsMagnified);
                Assert.DoesNotContain("Disp", plot.AnalysisText);

                _output.WriteLine("unmagnified: " + plot.AnalysisText);
            });
        }

        [Fact]
        public void AMagnifiedDisplayAnnotatesBothAndTheyDiffer()
        {
            OnStaThread(() =>
            {
                TracePlot plot = Shown();

                string before = plot.CenterFrequencyHotSpot.Text;

                Assert.True(plot.SetDisplayRange(999.5e6, 1000.5e6));
                Assert.True(plot.IsMagnified);

                _output.WriteLine("magnified: " + plot.AnalysisText);

                // The displayed range is said out loud.
                Assert.Contains("Disp", plot.AnalysisText);

                // And it differs from the measured span, which is the condition for showing it.
                Assert.True(plot.DisplaySpanHz < Points * BinHz);

                // The centre readout still describes the MEASUREMENT. That is the whole point: it
                // would otherwise report the magnified band as the measured one, which is the same
                // lie the other way round.
                Assert.Equal(before, plot.CenterFrequencyHotSpot.Text);
            });
        }

        [Fact]
        public void MagnifyingLeavesTheMeasurementsOwnReadoutsAlone()
        {
            OnStaThread(() =>
            {
                TracePlot plot = Shown();

                string centre = plot.CenterFrequencyHotSpot.Text;
                string rbw = plot.ResolutionBandwidthHotSpot.Text;

                plot.SetDisplayRange(999.4e6, 1000.6e6);

                Assert.Equal(centre, plot.CenterFrequencyHotSpot.Text);
                Assert.Equal(rbw, plot.ResolutionBandwidthHotSpot.Text);

                // The analysis line still reports the measured point count and span.
                Assert.Contains(Points.ToString() + " pts", plot.AnalysisText);
            });
        }

        [Fact]
        public void ClearingReturnsTheDisplayToTheWholeSpan()
        {
            OnStaThread(() =>
            {
                TracePlot plot = Shown();

                plot.SetDisplayRange(999.5e6, 1000.5e6);
                Assert.True(plot.IsMagnified);

                plot.ClearDisplayRange();

                Assert.False(plot.IsMagnified);
                Assert.DoesNotContain("Disp", plot.AnalysisText);

                Assert.Equal(StartHz, plot.DisplayStartHz, 3);
            });
        }

        [Fact]
        public void AMagnificationPastTheResolutionOfTheDataIsRefused()
        {
            // One point stretched across the screen looks like a measurement and is not one. Set
            // centre and span re-analyses the band and gives more resolution instead.
            OnStaThread(() =>
            {
                TracePlot plot = Shown();

                Assert.False(plot.SetDisplayRange(1.0e9, 1.0e9 + BinHz * 0.5));
                Assert.False(plot.IsMagnified);

                // Backwards and degenerate ranges too.
                Assert.False(plot.SetDisplayRange(1000.5e6, 999.5e6));
                Assert.False(plot.SetDisplayRange(double.NaN, 1.0e9));
            });
        }

        [Fact]
        public void ANewAcquisitionOutsideTheMagnifiedBandDropsTheMagnification()
        {
            // A magnification into a band the new measurement does not cover would draw nothing and
            // annotate a range that is not there.
            OnStaThread(() =>
            {
                TracePlot plot = Shown();

                plot.SetDisplayRange(999.5e6, 1000.5e6);
                Assert.True(plot.IsMagnified);

                // Re-tuned a long way off: the old window is outside the new span.
                plot.Show(Snapshot(plot.GraticuleColumns, 2.0e9));

                Assert.False(plot.IsMagnified);
            });
        }

        [Fact]
        public void ScaleXIsNoLongerRefused()
        {
            // REQ-UI-063 (#264): both entries were disabled with a reason for as long as there was
            // no way to say what band was actually drawn.
            Assert.Null(MouseModes.ReasonAgainst(AreaSelectAction.ScaleX));
            Assert.Null(MouseModes.ReasonAgainst(AreaSelectAction.ScaleBoth));
            Assert.Null(MouseModes.ReasonAgainst(AreaSelectAction.ScaleY));
            Assert.Null(MouseModes.ReasonAgainst(AreaSelectAction.CentreAndSpan));
        }

        // ---- Helpers -----------------------------------------------------------------------------

        private static TracePlot Shown()
        {
            var plot = new TracePlot();

            plot.Measure(new Size(800.0, 600.0));
            plot.Arrange(new Rect(0.0, 0.0, 800.0, 600.0));
            plot.UpdateLayout();

            plot.Show(Snapshot(plot.GraticuleColumns, 1.0e9));

            return plot;
        }

        private static TraceSnapshot Snapshot(int columns, double centreHz)
        {
            var levels = new float[Points];

            for (int i = 0; i < levels.Length; i++)
            {
                levels[i] = (float)(-90.0 + 50.0 * Math.Exp(-Math.Pow((i - 200) / 6.0, 2.0)));
            }

            SpectrumFrame frame = SpectrumFrame.FromLevels(
                levels,
                centreHz - (Points - 1) * BinHz / 2.0,
                BinHz,
                WindowType.FlatTop,
                3.8194);

            return RenderMarshal.Decimate(
                frame,
                columns,
                new[] { TraceFormat.LogMagnitude },
                TraceDetector.Normal,
                TraceFormatOptions.Default);
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
