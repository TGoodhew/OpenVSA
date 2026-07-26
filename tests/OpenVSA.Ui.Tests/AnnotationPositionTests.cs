using System;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using OpenVSA.Dsp.Spectrum;
using OpenVSA.Dsp.Windowing;
using OpenVSA.Ui.HotSpots;
using OpenVSA.Ui.Rendering;
using Xunit;
using Xunit.Abstractions;

namespace OpenVSA.Ui.Tests
{
    /// <summary>
    /// <c>REQ-UI-040</c>: where each piece of annotation sits, measured from the arranged control.
    /// </summary>
    public class AnnotationPositionTests
    {
        private const double Width = 900.0;
        private const double Height = 640.0;

        private readonly ITestOutputHelper _output;

        public AnnotationPositionTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void TheIndicatorStringsAreTheOnlyAnnotationInsideTheGraticule()
        {
            // The requirement states this as a test in as many words: it fails if any other
            // annotation's bounds intersect the grid rectangle. Nothing else here is a proxy for
            // it - the bounds come from the arranged visual tree.
            OnStaThread(() =>
            {
                TracePlot plot = Laid();
                Rect graticule = plot.GraticuleBounds;

                foreach (FrameworkElement element in plot.AnnotationElements)
                {
                    Rect bounds = plot.BoundsOf(element);

                    _output.WriteLine(Describe(element) + " at " + bounds);

                    Assert.False(
                        bounds.IntersectsWith(graticule),
                        Describe(element) + " at " + bounds +
                        " overlaps the graticule at " + graticule + ".");
                }
            });
        }

        [Fact]
        public void TheIndicatorStringsAreInsideTheGraticulesUpperRightCorner()
        {
            OnStaThread(() =>
            {
                TracePlot plot = Laid();

                plot.SetIndicators(Overloaded());

                // Re-arranged, because the element had no text and so no size until now.
                Lay(plot);

                Rect graticule = plot.GraticuleBounds;
                Rect bounds = plot.BoundsOf(plot.IndicatorElement);

                _output.WriteLine("indicators at " + bounds + " in a graticule of " + graticule);

                Assert.True(graticule.Contains(bounds), "The indicators are not inside the grid.");

                // Upper right: nearer the top than the bottom, and nearer the right than the left.
                Assert.True(bounds.Top - graticule.Top < graticule.Bottom - bounds.Bottom);
                Assert.True(graticule.Right - bounds.Right < bounds.Left - graticule.Left);
            });
        }

        [Fact]
        public void TheIndicatorStringsRenderInTheIndicatorColour()
        {
            // Their own colour, and the reason is positional: they are the only annotation over the
            // trace background rather than the annotation background.
            OnStaThread(() =>
            {
                TracePlot plot = Laid();

                var indicator = (System.Windows.Controls.TextBlock)plot.IndicatorElement;
                var brush = (System.Windows.Media.SolidColorBrush)indicator.Foreground;

                Assert.Equal(plot.Palette.Indicator.R, brush.Color.R);
                Assert.Equal(plot.Palette.Indicator.G, brush.Color.G);
                Assert.Equal(plot.Palette.Indicator.B, brush.Color.B);

                var scale = (System.Windows.Controls.TextBlock)plot.TopScaleHotSpot;
                var annotation = (System.Windows.Media.SolidColorBrush)scale.Foreground;

                Assert.NotEqual(brush.Color, annotation.Color);
            });
        }

        [Fact]
        public void TheActiveMarkerReadoutSitsAboveTheGridAndToTheRight()
        {
            OnStaThread(() =>
            {
                TracePlot plot = Laid();
                plot.SetMarkers(new PlotMarker[0], "Mkr 1  1.000000 GHz");
                Lay(plot);

                Rect graticule = plot.GraticuleBounds;
                FrameworkElement readout = Named(plot, "Mkr 1");
                Rect bounds = plot.BoundsOf(readout);

                _output.WriteLine("marker readout at " + bounds);

                Assert.True(bounds.Bottom <= graticule.Top + 0.5, "It is not above the grid.");
                Assert.True(
                    bounds.Left > graticule.Left + graticule.Width / 2.0,
                    "It is not to the right.");
            });
        }

        [Fact]
        public void TheYAxisScalesSitTopLeftAndBottomLeftWithPerDivisionBelowTheTop()
        {
            OnStaThread(() =>
            {
                TracePlot plot = Laid();
                Rect graticule = plot.GraticuleBounds;

                Rect top = plot.BoundsOf(plot.TopScaleHotSpot);
                Rect perDivision = plot.BoundsOf(plot.PerDivisionHotSpot);
                Rect bottom = plot.BoundsOf(plot.BottomScaleHotSpot);

                _output.WriteLine(
                    "top " + top + ", per-division " + perDivision + ", bottom " + bottom);

                // Top-left, with per-division below it.
                Assert.True(top.Top < graticule.Top);
                Assert.True(top.Left < graticule.Left + graticule.Width / 2.0);
                Assert.True(perDivision.Top >= top.Bottom - 0.5);
                Assert.True(perDivision.Left < graticule.Left + graticule.Width / 2.0);

                // Bottom-left.
                Assert.True(bottom.Bottom > graticule.Bottom);
                Assert.True(bottom.Left < graticule.Left + graticule.Width / 2.0);
            });
        }

        [Fact]
        public void FormatBandwidthAndTriggerChannelSitInTheUpperBand()
        {
            OnStaThread(() =>
            {
                TracePlot plot = Laid();
                Rect graticule = plot.GraticuleBounds;

                foreach (HotSpot spot in new[]
                         {
                             plot.FormatHotSpot,
                             plot.ResolutionBandwidthHotSpot,
                             plot.TriggerChannelHotSpot,
                         })
                {
                    Rect bounds = plot.BoundsOf(spot);

                    _output.WriteLine(spot.Text + " at " + bounds);
                    Assert.True(bounds.Bottom <= graticule.Top + 0.5, spot.Text + " is not above the grid.");
                }
            });
        }

        [Fact]
        public void CentreFrequencyAndMainTimeAreCentredBeneathTheXAxis()
        {
            OnStaThread(() =>
            {
                TracePlot plot = Laid();
                Rect graticule = plot.GraticuleBounds;

                Rect centre = plot.BoundsOf(plot.CenterFrequencyHotSpot);
                Rect time = plot.BoundsOf(plot.MainTimeHotSpot);

                _output.WriteLine("centre frequency " + centre + ", main time " + time);

                foreach (Rect bounds in new[] { centre, time })
                {
                    Assert.True(bounds.Top >= graticule.Bottom - 0.5, "Not beneath the X axis.");

                    // Centred: overlapping the middle third of the width.
                    Assert.True(bounds.Right > graticule.Left + graticule.Width / 3.0);
                    Assert.True(bounds.Left < graticule.Left + 2.0 * graticule.Width / 3.0);
                }
            });
        }

        [Fact]
        public void EveryHotSpotTheRequirementListsIsPresent()
        {
            // REQ-UI-042's confirmed list: the three Y-axis scales, trace format, resolution
            // bandwidth, trigger channel, main time length and centre frequency.
            OnStaThread(() =>
            {
                TracePlot plot = Laid();

                Assert.Equal(8, plot.HotSpots.Count);

                foreach (HotSpot spot in plot.HotSpots)
                {
                    Assert.NotNull(spot.Value);
                    Assert.False(string.IsNullOrEmpty(spot.Text));
                }
            });
        }

        [Fact]
        public void ChangingAHotSpotReportsItToTheHost()
        {
            OnStaThread(() =>
            {
                TracePlot plot = Laid();
                HotSpot reported = null;
                plot.ParameterChanged += (sender, spot) => reported = spot;

                plot.CenterFrequencyHotSpot.Adjust(1);

                Assert.Same(plot.CenterFrequencyHotSpot, reported);
            });
        }

        [Fact]
        public void ChangingTheVerticalScaleRescalesTheAxisWithoutLeavingTheControl()
        {
            // The two hot spots the plot owns itself: they change how the trace is drawn and
            // nothing about the acquisition, so routing them out and back would make the axis lag
            // the click by a frame.
            OnStaThread(() =>
            {
                TracePlot plot = Laid();

                double top = plot.TopDbm;

                Assert.True(plot.TopScaleHotSpot.Adjust(5));
                Assert.Equal(top + 5.0, plot.TopDbm, 6);
                Assert.Equal(plot.TopDbm - plot.DecibelsPerDivision * TracePlot.VerticalDivisions,
                    plot.BottomDbm, 6);

                // Per-division steps the 1-2-5 ladder; moving it re-scales without moving the top.
                Assert.True(plot.PerDivisionHotSpot.Adjust(-1));
                Assert.Equal(5.0, plot.DecibelsPerDivision, 6);
                Assert.Equal(plot.TopDbm - 50.0, plot.BottomDbm, 6);
            });
        }

        [Fact]
        public void SettingTheBottomScaleMovesTheTopRatherThanStretchingTheAxis()
        {
            // Otherwise the per-division reading beside it would stop being true, and the two
            // annotations would contradict each other.
            OnStaThread(() =>
            {
                TracePlot plot = Laid();

                Assert.True(plot.BottomScaleHotSpot.Adjust(-10));

                Assert.Equal(
                    plot.DecibelsPerDivision * TracePlot.VerticalDivisions,
                    plot.TopDbm - plot.BottomDbm,
                    6);
            });
        }

        [Fact]
        public void AFrameRefreshesTheAnnotationButNotAValueBeingTyped()
        {
            // A measurement updating sixty times a second would otherwise overwrite a half-typed
            // entry between two keystrokes.
            OnStaThread(() =>
            {
                TracePlot plot = Laid();

                plot.CenterFrequencyHotSpot.BeginEdit();
                plot.CenterFrequencyHotSpot.Type('2');
                plot.CenterFrequencyHotSpot.Type('G');

                plot.Show(Snapshot(plot));

                Assert.Equal("Center 2G", plot.CenterFrequencyHotSpot.Text);

                // And once the entry ends, the frame's own value shows again.
                plot.CenterFrequencyHotSpot.EndEdit(commit: false);
                plot.Show(Snapshot(plot));

                Assert.Contains("1.000000 GHz", plot.CenterFrequencyHotSpot.Text);
            });
        }

        private static TraceIndicators Overloaded()
        {
            var indicators = new TraceIndicators();
            indicators.Set(TraceIndicator.Overload, 1);
            return indicators;
        }

        private static FrameworkElement Named(TracePlot plot, string containing)
        {
            foreach (FrameworkElement element in Descendants(plot))
            {
                var text = element as System.Windows.Controls.TextBlock;

                if (text != null && text.Text.Contains(containing))
                {
                    return text;
                }
            }

            throw new InvalidOperationException("No annotation containing '" + containing + "'.");
        }

        private static System.Collections.Generic.IEnumerable<FrameworkElement> Descendants(
            FrameworkElement root)
        {
            int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);

            for (int i = 0; i < count; i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i) as FrameworkElement;

                if (child == null)
                {
                    continue;
                }

                yield return child;

                foreach (FrameworkElement grandchild in Descendants(child))
                {
                    yield return grandchild;
                }
            }
        }

        private static string Describe(FrameworkElement element)
        {
            var text = element as System.Windows.Controls.TextBlock;
            return text == null ? element.GetType().Name : "'" + text.Text + "'";
        }

        private static TraceSnapshot Snapshot(TracePlot plot)
        {
            var levels = new float[801];

            for (int i = 0; i < levels.Length; i++)
            {
                levels[i] = -60.0f;
            }

            SpectrumFrame frame = SpectrumFrame.FromLevels(
                levels, 1e9 - 5e6, 12500.0, WindowType.FlatTop, 3.8194);

            var marshal = new RenderMarshal { Columns = plot.GraticuleColumns };
            marshal.Offer(frame);

            return marshal.TakeForRender();
        }

        private static TracePlot Laid()
        {
            var plot = new TracePlot();
            Lay(plot);
            return plot;
        }

        private static void Lay(TracePlot plot)
        {
            plot.Measure(new Size(Width, Height));
            plot.Arrange(new Rect(0.0, 0.0, Width, Height));
            plot.UpdateLayout();
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
