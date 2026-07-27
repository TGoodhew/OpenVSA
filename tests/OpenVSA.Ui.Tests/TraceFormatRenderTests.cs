using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media.Imaging;
using OpenVSA.Dsp.Spectrum;
using OpenVSA.Dsp.Windowing;
using OpenVSA.Ui.Rendering;
using Xunit;

namespace OpenVSA.Ui.Tests
{
    /// <summary>
    /// <c>REQ-TRC-001</c> and <c>REQ-DSP-041</c> at the pixels: a format change changes the trace,
    /// not only its label.
    /// </summary>
    /// <remarks>
    /// This is the seam that was missing. <c>OpenVSA.Dsp.Tests.TraceFormatTests</c> proves the
    /// formatter produces the right numbers, and it always did; what nothing asserted was that the
    /// display ever asked it for them. The render marshal built one envelope from the log magnitude
    /// and every trace window drew that, whatever format it was set to — four windows, four labels,
    /// one picture.
    /// </remarks>
    public class TraceFormatRenderTests
    {
        [Fact]
        public void TheMarshalCarriesAnEnvelopePerFormatOnScreen()
        {
            var marshal = new RenderMarshal
            {
                Columns = 64,
                Formats = new[] { TraceFormat.LogMagnitude, TraceFormat.LinearMagnitude },
            };

            marshal.Offer(Frame());

            TraceSnapshot snapshot = marshal.TakeForRender();

            Assert.NotNull(snapshot);
            Assert.Equal(64 * 2, snapshot.MinMaxFor(TraceFormat.LogMagnitude).Length);
            Assert.Equal(64 * 2, snapshot.MinMaxFor(TraceFormat.LinearMagnitude).Length);

            // Not asked for, so not carried. A plot showing it must draw nothing rather than draw
            // another format's geometry, which is the whole defect.
            Assert.Equal(0, snapshot.MinMaxFor(TraceFormat.Real).Length);
        }

        [Fact]
        public void TwoFormatsOfOneAcquisitionAreDifferentGeometry()
        {
            // The failing assertion, had it existed: log magnitude in dBm and linear magnitude in
            // volts are the same data through different arithmetic, and their envelopes must not
            // be equal — before this they were the same array.
            var marshal = new RenderMarshal
            {
                Columns = 64,
                Formats = new[] { TraceFormat.LogMagnitude, TraceFormat.LinearMagnitude },
            };

            marshal.Offer(Frame());

            TraceSnapshot snapshot = marshal.TakeForRender();

            ReadOnlySpan<float> logarithmic = snapshot.MinMaxFor(TraceFormat.LogMagnitude);
            ReadOnlySpan<float> linear = snapshot.MinMaxFor(TraceFormat.LinearMagnitude);

            bool differ = false;

            for (int i = 0; i < logarithmic.Length; i++)
            {
                if (Math.Abs(logarithmic[i] - linear[i]) > 1e-6)
                {
                    differ = true;
                    break;
                }
            }

            Assert.True(differ, "Log and linear magnitude produced the same envelope.");
        }

        [Fact]
        public void TwoPlotsOverOneSnapshotDrawDifferentPixels()
        {
            // The criterion at the only level that matters. Two windows, one acquisition, two
            // formats — and the assertion is on the rendered surfaces, not on the format property,
            // because the format property was always right.
            Sta.Run(() =>
            {
                TracePlot logarithmic = Laid();
                TracePlot linear = Laid();

                Assert.True(linear.SetFormat(TraceFormat.LinearMagnitude));

                var marshal = new RenderMarshal
                {
                    Columns = logarithmic.GraticuleColumns,
                    Formats = new[] { TraceFormat.LogMagnitude, TraceFormat.LinearMagnitude },
                };

                marshal.Offer(Frame());
                TraceSnapshot snapshot = marshal.TakeForRender();

                Assert.True(logarithmic.Show(snapshot));
                Assert.True(linear.Show(snapshot));

                Assert.False(
                    SamePixels(logarithmic, linear),
                    "Two formats of one acquisition drew the same picture.");
            });
        }

        [Fact]
        public void EachFormatGetsTheAxisItsQuantityNeeds()
        {
            // Drawing volts on a decibel axis puts the whole trace in the bottom pixel row, which
            // reads as no signal rather than as the wrong axis.
            Sta.Run(() =>
            {
                TracePlot plot = Laid();

                var marshal = new RenderMarshal
                {
                    Columns = plot.GraticuleColumns,
                    Formats = new[]
                    {
                        TraceFormat.LogMagnitude, TraceFormat.LinearMagnitude,
                        TraceFormat.WrappedPhase,
                    },
                };

                marshal.Offer(Frame());
                TraceSnapshot snapshot = marshal.TakeForRender();

                Assert.True(plot.Show(snapshot));
                Assert.Equal("dBm", plot.Axis.Unit);

                Assert.True(plot.SetFormat(TraceFormat.LinearMagnitude));
                Assert.True(plot.Show(snapshot));
                Assert.Equal("V", plot.Axis.Unit);

                Assert.True(plot.SetFormat(TraceFormat.WrappedPhase));
                Assert.True(plot.Show(snapshot));
                Assert.Equal("deg", plot.Axis.Unit);

                // Bounded by definition, so it is fixed rather than ranged from the data.
                Assert.Equal(180.0, plot.Axis.TopValue, 6);
                Assert.Equal(-180.0, plot.Axis.BottomValue, 6);
            });
        }

        [Fact]
        public void AVoltsTraceIsNotDrawnAtTheBottomOfADecibelAxis()
        {
            // The visible symptom of the defect, asserted directly: a linear-magnitude trace of a
            // few millivolts on a −80..+20 dBm axis lies in the bottom row. On its own axis it does
            // not.
            Sta.Run(() =>
            {
                TracePlot plot = Laid();

                var marshal = new RenderMarshal
                {
                    Columns = plot.GraticuleColumns,
                    Formats = new[] { TraceFormat.LinearMagnitude },
                };

                marshal.Offer(Frame());

                Assert.True(plot.SetFormat(TraceFormat.LinearMagnitude));
                Assert.True(plot.Show(marshal.TakeForRender()));

                Assert.True(
                    plot.Axis.TopValue < 1.0,
                    "A millivolt trace was given an axis running to " + plot.Axis.TopValue +
                    "; it would draw as a flat line at the bottom.");

                Assert.True(plot.Axis.TopValue > plot.Axis.BottomValue);
            });
        }

        [Fact]
        public void IqIsNotDrawnAsALineAndDoesNotPretendToBe()
        {
            // Two values per point and a constellation, not a curve. An envelope of its interleaved
            // pairs would be a picture that means nothing, so no envelope is built for it.
            var marshal = new RenderMarshal
            {
                Columns = 32,
                Formats = new[] { TraceFormat.IQ },
            };

            marshal.Offer(Frame());

            TraceSnapshot snapshot = marshal.TakeForRender();

            Assert.Equal(0, snapshot.MinMaxFor(TraceFormat.IQ).Length);
            Assert.False(TraceAxis.IsLineTrace(TraceFormat.IQ));

            // And the list never ends up empty: log magnitude stands in so a frame is still drawn.
            var formats = new List<TraceFormat>(snapshot.Formats);

            Assert.Contains(TraceFormat.LogMagnitude, formats);
        }

        [Fact]
        public void DistinctFormatsAreDecimatedOnceEach()
        {
            // Eight windows showing two formats cost two decimations, not eight.
            var marshal = new RenderMarshal
            {
                Columns = 32,
                Formats = new[]
                {
                    TraceFormat.LogMagnitude, TraceFormat.Real, TraceFormat.LogMagnitude,
                    TraceFormat.Real,
                },
            };

            Assert.Equal(2, new List<TraceFormat>(marshal.Formats).Count);
        }

        [Fact]
        public void TheAxisStepsInOneTwoOrFive()
        {
            // Readable division labels: a step of 0.037 volts gives graticule lines nobody can read
            // off, and 0.05 gives ten that anyone can.
            Assert.Equal(0.05, TraceAxis.NiceStep(0.037), 12);
            Assert.Equal(1.0, TraceAxis.NiceStep(0.6), 12);
            Assert.Equal(2.0, TraceAxis.NiceStep(1.1), 12);
            Assert.Equal(200.0, TraceAxis.NiceStep(101.0), 12);

            // And it never returns something a division count could divide by zero.
            Assert.True(TraceAxis.NiceStep(0.0) > 0.0);
            Assert.True(TraceAxis.NiceStep(double.NaN) > 0.0);
        }

        [Fact]
        public void AnAutoRangedAxisContainsItsData()
        {
            var values = new[] { -0.003f, 0.007f, 0.0f, float.NaN };

            TraceAxis axis = TraceAxis.For(
                TraceFormat.Real, values, referenceDbm: 0.0, decibelsPerDivision: 10.0,
                divisions: 10, yReferencePercent: 50);

            Assert.True(axis.TopValue >= 0.007, "The axis clipped the top of the data: " + axis);
            Assert.True(axis.BottomValue <= -0.003, "The axis clipped the bottom: " + axis);
            Assert.Equal("V", axis.Unit);
        }

        [Fact]
        public void AnEmptyOrFlatTraceStillGetsAUsableAxis()
        {
            TraceAxis empty = TraceAxis.For(
                TraceFormat.Real, ReadOnlySpan<float>.Empty, 0.0, 10.0, 10, 50);

            Assert.True(empty.TopValue > empty.BottomValue);

            TraceAxis flat = TraceAxis.For(
                TraceFormat.Real, new[] { 2.0f, 2.0f, 2.0f }, 0.0, 10.0, 10, 50);

            Assert.True(flat.TopValue > 2.0);
            Assert.True(flat.BottomValue < 2.0);
        }

        private static SpectrumFrame Frame()
        {
            // An awkward length, as the bench block has: a tidy power of two hides the class of
            // error that only appears when the point count and the column count share no factor.
            var levels = new float[1021];

            for (int i = 0; i < levels.Length; i++)
            {
                levels[i] = -70.0f;
            }

            // A carrier, so the two formats have something to disagree about.
            levels[500] = -20.0f;
            levels[501] = -24.0f;

            return SpectrumFrame.FromLevels(levels, 1e9 - 5e6, 9784.0, WindowType.FlatTop, 3.8194);
        }

        private static TracePlot Laid()
        {
            var plot = new TracePlot();

            plot.Measure(new Size(800.0, 600.0));
            plot.Arrange(new Rect(0.0, 0.0, 800.0, 600.0));

            return plot;
        }

        private static bool SamePixels(TracePlot first, TracePlot second)
        {
            byte[] one = Pixels(first);
            byte[] two = Pixels(second);

            if (one.Length != two.Length)
            {
                return false;
            }

            for (int i = 0; i < one.Length; i++)
            {
                if (one[i] != two[i])
                {
                    return false;
                }
            }

            return true;
        }

        private static byte[] Pixels(TracePlot plot)
        {
            foreach (UIElement child in plot.Children)
            {
                var image = child as System.Windows.Controls.Image;
                var bitmap = image == null ? null : image.Source as WriteableBitmap;

                if (bitmap == null)
                {
                    continue;
                }

                int stride = bitmap.PixelWidth * 4;
                var pixels = new byte[stride * bitmap.PixelHeight];

                bitmap.CopyPixels(pixels, stride, 0);
                return pixels;
            }

            throw new InvalidOperationException("The plot has no bitmap to read.");
        }
    }
}
