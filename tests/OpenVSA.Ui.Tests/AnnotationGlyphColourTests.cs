using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OpenVSA.Ui.Rendering;
using Xunit;
using Xunit.Abstractions;

namespace OpenVSA.Ui.Tests
{
    /// <summary>
    /// <c>REQ-UI-010</c>'s fourth sample, and <c>REQ-UI-021</c>'s from the frame rather than the brush.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Why this could not be written before.</strong> Three of <c>REQ-UI-010</c>'s four zones
    /// are rasterised, and <see cref="PlotRasterizerTests"/> samples them headlessly. The fourth is
    /// "<c>Annotation</c> on the glyphs of measurement-wide annotation", and annotation text is not
    /// rasterised — it is real WPF elements, because <c>REQ-UI-042</c>'s hot spots need hit-testing,
    /// hover feedback and in-place editing, all of which are cheap against elements and expensive
    /// against rasterised glyphs. So this renders the laid-out control through
    /// <see cref="RenderTargetBitmap"/> and samples that.
    /// </para>
    /// <para>
    /// <strong>Not circular.</strong> The region sampled is chosen <em>geometrically</em> — an
    /// element's own bounds, transformed into the control's space — and the colour is then asserted
    /// there. Searching the band for a pixel of the expected colour and concluding the colour is
    /// present would prove nothing at all.
    /// </para>
    /// <para>
    /// <strong>Antialiasing is handled by choosing the colours, not by widening a tolerance.</strong>
    /// Glyph pixels are blends of the ink and the background it sits on, so an exact match fails on
    /// almost every pixel of real text. The palette here puts the annotation background at black and
    /// each ink on one channel, so every blend along that edge leaves the other two channels near
    /// zero: the assertion is that the expected channel dominates, which no amount of blending or
    /// subpixel fringing turns into a different channel.
    /// </para>
    /// </remarks>
    public class AnnotationGlyphColourTests
    {
        /// <summary>Black, so a glyph blend moves only the ink's own channel.</summary>
        private static readonly PlotColor Background = new PlotColor(0, 0, 0);

        /// <summary>Measurement-wide annotation ink: red.</summary>
        private static readonly PlotColor MeasurementInk = new PlotColor(255, 0, 0);

        /// <summary>Per-trace ink: green.</summary>
        private static readonly PlotColor TraceInk = new PlotColor(0, 255, 0);

        private readonly ITestOutputHelper _output;

        /// <summary>Takes the output helper, so the sampled channels are visible.</summary>
        /// <param name="output">Where the samples are written.</param>
        public AnnotationGlyphColourTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void MeasurementAnnotationGlyphsCarryTheAnnotationColour()
        {
            OnStaThread(() =>
            {
                TracePlot plot = Painted();

                // The division exists at all: if every element were in one group there would be
                // nothing to tell apart, and both halves of this suite would be vacuous.
                Assert.NotEmpty(plot.MeasurementAnnotationElements);
                Assert.True(
                    plot.AnnotationElements.Count > plot.MeasurementAnnotationElements.Count,
                    "Every annotation element is measurement-wide, so no element carries the trace " +
                    "colour and REQ-UI-021 has nothing to hold.");

                byte[] frame = Render(plot, out int width, out int height, out int stride);

                foreach (FrameworkElement element in WithText(plot.MeasurementAnnotationElements))
                {
                    Channels ink = InkIn(plot, element, frame, width, height, stride);

                    _output.WriteLine(Describe(element) + ": " + ink);

                    Assert.True(
                        ink.IsMostly(Channel.Red),
                        Describe(element) + " should be drawn in the Annotation colour, and its " +
                        "strongest glyph pixel is " + ink + ".");
                }
            });
        }

        [Fact]
        public void ATraceOwnAnnotationGlyphsCarryTheTraceColour()
        {
            OnStaThread(() =>
            {
                TracePlot plot = Painted();
                byte[] frame = Render(plot, out int width, out int height, out int stride);

                var measurement = new HashSet<FrameworkElement>(plot.MeasurementAnnotationElements);
                int checked_ = 0;

                foreach (FrameworkElement element in WithText(plot.AnnotationElements))
                {
                    if (measurement.Contains(element))
                    {
                        continue;
                    }

                    Channels ink = InkIn(plot, element, frame, width, height, stride);

                    _output.WriteLine(Describe(element) + ": " + ink);

                    // REQ-UI-021 sampled from the rendered frame, which its own criterion asks for
                    // and the existing brush-property assertion does not reach: a brush nothing is
                    // assigned to satisfies that check and paints no glyph.
                    Assert.True(
                        ink.IsMostly(Channel.Green),
                        Describe(element) + " describes this trace and should carry the trace " +
                        "colour; its strongest glyph pixel is " + ink + ".");

                    checked_++;
                }

                Assert.True(checked_ > 0, "No per-trace annotation element had any text to sample.");
            });
        }

        [Fact]
        public void ChangingTheAnnotationColourLeavesATracesOwnAnnotationAlone()
        {
            OnStaThread(() =>
            {
                TracePlot plot = Painted();

                FrameworkElement measurement = WithText(plot.MeasurementAnnotationElements)[0];
                FrameworkElement own = FirstTraceOwn(plot);

                byte[] before = Render(plot, out int width, out int height, out int stride);
                Channels ownBefore = InkIn(plot, own, before, width, height, stride);

                // Blue, so a change that leaked into the trace's own annotation would show up as a
                // channel neither ink uses rather than as a shade of one of them.
                plot.Palette = plot.Palette.WithAnnotation(new PlotColor(0, 0, 255));
                plot.UpdateLayout();

                byte[] after = Render(plot, out width, out height, out stride);

                Assert.True(
                    InkIn(plot, measurement, after, width, height, stride).IsMostly(Channel.Blue),
                    "The measurement annotation did not follow the Annotation colour.");

                Channels ownAfter = InkIn(plot, own, after, width, height, stride);

                _output.WriteLine("trace-own before " + ownBefore + ", after " + ownAfter);

                Assert.True(
                    ownAfter.IsMostly(Channel.Green),
                    "A trace's own annotation moved when the Annotation colour changed: " + ownAfter);
            });
        }

        [Fact]
        public void ChangingTheTraceColourLeavesTheMeasurementAnnotationAlone()
        {
            OnStaThread(() =>
            {
                TracePlot plot = Painted();

                FrameworkElement measurement = WithText(plot.MeasurementAnnotationElements)[0];

                plot.Palette = plot.Palette.WithTrace(new PlotColor(0, 0, 255));
                plot.UpdateLayout();

                byte[] frame = Render(plot, out int width, out int height, out int stride);
                Channels ink = InkIn(plot, measurement, frame, width, height, stride);

                _output.WriteLine("measurement annotation after a trace-colour change: " + ink);

                // The other direction, and the one that fails if the two are one brush: a shared
                // brush passes the test above and fails this one.
                Assert.True(
                    ink.IsMostly(Channel.Red),
                    "The measurement annotation followed the trace colour: " + ink);
            });
        }

        // ---- Fixtures ---------------------------------------------------------------------------

        /// <summary>
        /// A laid-out plot with annotation text in it and the four colours set apart.
        /// </summary>
        private static TracePlot Painted()
        {
            var plot = new TracePlot
            {
                Palette = PlotPalette.Dark
                    .WithAnnotationBackground(Background)
                    .WithTraceBackground(Background)
                    .WithGrid(new PlotColor(40, 40, 40))
                    .WithAnnotation(MeasurementInk)
                    .WithTrace(TraceInk),
            };

            plot.Measure(new Size(900.0, 500.0));
            plot.Arrange(new Rect(0.0, 0.0, 900.0, 500.0));
            plot.UpdateLayout();

            return plot;
        }

        private static FrameworkElement FirstTraceOwn(TracePlot plot)
        {
            var measurement = new HashSet<FrameworkElement>(plot.MeasurementAnnotationElements);

            foreach (FrameworkElement element in WithText(plot.AnnotationElements))
            {
                if (!measurement.Contains(element))
                {
                    return element;
                }
            }

            throw new InvalidOperationException("No per-trace annotation element carries any text.");
        }

        /// <summary>
        /// The elements that actually have something drawn in them.
        /// </summary>
        /// <remarks>
        /// An empty label occupies no pixels, so sampling its bounds finds background and would fail
        /// for a reason that has nothing to do with colour. Which labels are populated depends on
        /// what has been measured, so it is asked rather than assumed.
        /// </remarks>
        private static List<FrameworkElement> WithText(IReadOnlyList<FrameworkElement> elements)
        {
            var found = new List<FrameworkElement>();

            foreach (FrameworkElement element in elements)
            {
                var text = element as TextBlock;

                if (text != null && !string.IsNullOrWhiteSpace(text.Text) &&
                    text.ActualWidth > 1.0 && text.ActualHeight > 1.0 &&
                    text.Visibility == Visibility.Visible)
                {
                    found.Add(element);
                }
            }

            return found;
        }

        private static byte[] Render(
            TracePlot plot, out int width, out int height, out int stride)
        {
            width = (int)Math.Ceiling(plot.ActualWidth);
            height = (int)Math.Ceiling(plot.ActualHeight);

            var bitmap = new RenderTargetBitmap(width, height, 96.0, 96.0, PixelFormats.Pbgra32);

            bitmap.Render(plot);

            stride = width * 4;
            var pixels = new byte[stride * height];
            bitmap.CopyPixels(pixels, stride, 0);

            return pixels;
        }

        /// <summary>
        /// The strongest ink found inside an element's own bounds.
        /// </summary>
        /// <remarks>
        /// The bounds are transformed from the element into the control, so the region is chosen by
        /// geometry rather than by colour. The pixel taken is the one furthest from the background,
        /// which is a glyph's core: averaging over the rectangle would mostly average background,
        /// because text is thin.
        /// </remarks>
        private static Channels InkIn(
            TracePlot plot,
            FrameworkElement element,
            byte[] pixels,
            int width,
            int height,
            int stride)
        {
            GeneralTransform transform = element.TransformToAncestor(plot);
            Rect bounds = transform.TransformBounds(
                new Rect(0.0, 0.0, element.ActualWidth, element.ActualHeight));

            int left = Math.Max(0, (int)Math.Floor(bounds.Left));
            int top = Math.Max(0, (int)Math.Floor(bounds.Top));
            int right = Math.Min(width, (int)Math.Ceiling(bounds.Right));
            int bottom = Math.Min(height, (int)Math.Ceiling(bounds.Bottom));

            var strongest = new Channels(0, 0, 0);
            int best = -1;

            for (int y = top; y < bottom; y++)
            {
                for (int x = left; x < right; x++)
                {
                    int offset = (y * stride) + (x * 4);

                    var here = new Channels(
                        pixels[offset + 2], pixels[offset + 1], pixels[offset]);

                    int distance = here.R + here.G + here.B;

                    if (distance > best)
                    {
                        best = distance;
                        strongest = here;
                    }
                }
            }

            return strongest;
        }

        private static string Describe(FrameworkElement element)
        {
            var text = element as TextBlock;

            return element.GetType().Name +
                (text == null ? string.Empty : " '" + text.Text.Trim() + "'");
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

        /// <summary>Which channel an ink is meant to be.</summary>
        private enum Channel
        {
            Red,
            Green,
            Blue,
        }

        /// <summary>One sampled pixel.</summary>
        private struct Channels
        {
            internal Channels(byte r, byte g, byte b)
            {
                R = r;
                G = g;
                B = b;
            }

            internal byte R { get; }

            internal byte G { get; }

            internal byte B { get; }

            /// <summary>
            /// Whether one channel carries this pixel, allowing for a glyph blend.
            /// </summary>
            /// <param name="channel">The channel the ink should be on.</param>
            /// <remarks>
            /// Half brightness rather than full, because the strongest pixel of small antialiased
            /// text is not a fully covered one; and at least twice either other channel, which
            /// separates "red ink, partly blended into black" from every other colour on the surface
            /// including a grey grid line and a white default foreground.
            /// </remarks>
            internal bool IsMostly(Channel channel)
            {
                byte mine = channel == Channel.Red ? R : channel == Channel.Green ? G : B;
                byte first = channel == Channel.Red ? G : R;
                byte second = channel == Channel.Blue ? G : B;

                return mine >= 128 && mine > first * 2 && mine > second * 2;
            }

            /// <inheritdoc />
            public override string ToString() => "R" + R + " G" + G + " B" + B;
        }
    }
}
