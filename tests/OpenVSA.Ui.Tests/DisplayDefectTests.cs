using System;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using OpenVSA.Dsp.Spectrum;
using OpenVSA.Dsp.Windowing;
using OpenVSA.Ui.HotSpots;
using OpenVSA.Ui.Rendering;
using Xunit;
using Xunit.Abstractions;

namespace OpenVSA.Ui.Tests
{
    /// <summary>
    /// The two display defects found by photographing the running shell (#395, #396).
    /// </summary>
    public class DisplayDefectTests
    {
        private readonly ITestOutputHelper _output;

        /// <summary>Takes xunit's output sink.</summary>
        /// <param name="output">Where measured figures are written.</param>
        public DisplayDefectTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void AResizedPlotKeepsTheTraceItWasShowing()
        {
            // #395. The marshal decimates to the width it was told about, and Show rightly refuses
            // a snapshot built for a different one — so a resize threw the frame away, and with a
            // measurement running the next frame repaired it while with nothing running there was
            // no next frame. Now the plot re-decimates what it already holds.
            OnStaThread(() =>
            {
                var plot = new TracePlot();

                Lay(plot, 800.0, 600.0);

                int columns = plot.GraticuleColumns;

                Assert.True(columns > 0);

                plot.Show(Snapshot(columns));

                Assert.True(plot.HasTrace, "The plot did not take the first snapshot.");

                // Narrower, then wider again. Nothing else happens in between: no new frame.
                Lay(plot, 520.0, 600.0);

                Assert.True(
                    plot.HasTrace,
                    "The trace was thrown away when the plot was made narrower.");

                Lay(plot, 900.0, 600.0);

                Assert.True(
                    plot.HasTrace,
                    "The trace was thrown away when the plot was made wider.");

                // And the snapshot it holds matches the width it now is, so the next real frame is
                // not refused either.
                Assert.Equal(plot.GraticuleColumns, plot.CurrentSnapshotColumns);
            });
        }

        [Fact]
        public void ARedecimatedTraceStillDrawsInk()
        {
            // Holding a snapshot is not the same as drawing one.
            OnStaThread(() =>
            {
                var plot = new TracePlot();

                Lay(plot, 800.0, 600.0);
                plot.Show(Snapshot(plot.GraticuleColumns));

                Lay(plot, 540.0, 600.0);

                Assert.True(
                    Ink(plot) > 100,
                    "The resized plot drew " + Ink(plot) + " trace pixels.");
            });
        }

        [Fact]
        public void AnAnnotationLabelIsShownWholeOrEndsInAnEllipsis()
        {
            // #396. A text block arranged narrower than its text clips silently, and a clipped
            // annotation does not read as truncated — it reads as a different measurement.
            // "RBW 1.000000 kH" is what this produced, and kH is not a unit.
            OnStaThread(() =>
            {
                var plot = new TracePlot();

                Lay(plot, 800.0, 600.0);

                foreach (HotSpot spot in plot.HotSpots)
                {
                    Assert.Equal(TextTrimming.WordEllipsis, spot.TextTrimming);

                    Assert.NotEqual(TextTrimming.CharacterEllipsis, spot.TextTrimming);
                    Assert.NotEqual(TextTrimming.None, spot.TextTrimming);
                }
            });
        }

        [Fact]
        public void TheResolutionBandwidthFitsTheBandItSharesWithTwoOthers()
        {
            // The upper band carries the format, the RBW and the trigger channel across one third
            // of the graticule's width. Six figures put the unit past the edge; four do not.
            OnStaThread(() =>
            {
                var plot = new TracePlot();

                Lay(plot, 800.0, 600.0);

                string shown = plot.ResolutionBandwidthHotSpot.Text;

                _output.WriteLine("RBW annotation: '" + shown + "'");

                // The unit is present and whole. "kH" is the failure this guards.
                Assert.EndsWith("Hz", shown);
                Assert.DoesNotContain("kH ", shown + " ");

                // And it is short enough that the three of them fit the band. Measured rather than
                // asserted about the figures, because the band's width is what actually decides it.
                Assert.True(
                    shown.Length <= 18,
                    "The RBW annotation is " + shown.Length + " characters: '" + shown + "'.");
            });
        }

        [Fact]
        public void ACentreFrequencyKeepsItsSixFigures()
        {
            // The RBW's precision came down; the centre frequency's must not. On a gigahertz
            // carrier the sixth figure is a real hertz, and that readout has a band of its own.
            OnStaThread(() =>
            {
                var plot = new TracePlot();

                Lay(plot, 800.0, 600.0);

                plot.CenterFrequencyHotSpot.Value.TrySet("1.000001 GHz");
                plot.CenterFrequencyHotSpot.Refresh();

                _output.WriteLine("Centre annotation: '" + plot.CenterFrequencyHotSpot.Text + "'");

                Assert.Contains("1.000001", plot.CenterFrequencyHotSpot.Text);
            });
        }

        // ---- Helpers -----------------------------------------------------------------------------

        private static void Lay(TracePlot plot, double width, double height)
        {
            plot.Measure(new Size(width, height));
            plot.Arrange(new Rect(0.0, 0.0, width, height));
            plot.UpdateLayout();
        }

        private static TraceSnapshot Snapshot(int columns)
        {
            var levels = new float[801];

            for (int i = 0; i < levels.Length; i++)
            {
                levels[i] = (float)(-90.0 + 40.0 * Math.Exp(-Math.Pow((i - 400) / 12.0, 2.0)));
            }

            SpectrumFrame frame = SpectrumFrame.FromLevels(
                levels, 1e9, 1e3, WindowType.FlatTop, 3.8194);

            return RenderMarshal.Decimate(
                frame,
                columns,
                new[] { TraceFormat.LogMagnitude },
                TraceDetector.Normal,
                TraceFormatOptions.Default);
        }

        /// <summary>How many pixels of the plot are neither background nor grid.</summary>
        private static int Ink(TracePlot plot)
        {
            var bitmap = ImageOf(plot) as System.Windows.Media.Imaging.WriteableBitmap;

            if (bitmap == null)
            {
                return 0;
            }

            int stride = bitmap.PixelWidth * 4;
            var pixels = new byte[stride * bitmap.PixelHeight];

            bitmap.CopyPixels(pixels, stride, 0);

            PlotColor trace = plot.Palette.Trace;
            int inked = 0;

            for (int at = 0; at < pixels.Length; at += 4)
            {
                if (pixels[at] == trace.B && pixels[at + 1] == trace.G && pixels[at + 2] == trace.R)
                {
                    inked++;
                }
            }

            return inked;
        }

        private static System.Windows.Media.ImageSource ImageOf(TracePlot plot)
        {
            foreach (UIElement child in plot.Children)
            {
                var image = child as Image;

                if (image != null)
                {
                    return image.Source;
                }
            }

            return null;
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
