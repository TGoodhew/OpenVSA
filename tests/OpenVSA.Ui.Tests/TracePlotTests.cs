using System;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Media.Imaging;
using OpenVSA.Dsp.Spectrum;
using OpenVSA.Dsp.Windowing;
using OpenVSA.Ui.Rendering;
using Xunit;

namespace OpenVSA.Ui.Tests
{
    /// <summary>
    /// The plot control: that laying it out gives it a graticule, and that a marshalled frame
    /// reaches its pixels.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the seam the rest of the render tests cannot reach. <see cref="PlotRasterizerTests"/>
    /// proves the rasteriser draws what it is asked to; this proves the control asks it, with the
    /// dimensions it claims, and hands the result to a bitmap of the same size — the three places a
    /// correct rasteriser still ends up displaying nothing.
    /// </para>
    /// <para>
    /// WPF elements need an STA thread, and one is created per test rather than the suite being
    /// given an apartment: no window, no dispatcher and no message pump are involved, because the
    /// control is measured and arranged directly.
    /// </para>
    /// </remarks>
    public class TracePlotTests
    {
        [Fact]
        public void LayingItOutGivesItAGraticuleToDecimateTo()
        {
            OnStaThread(() =>
            {
                TracePlot plot = Laid(out _, out _);

                Assert.True(plot.GraticuleColumns > 0);
                Assert.True(plot.GraticuleColumns < 800);
            });
        }

        [Fact]
        public void TheGraticuleWidthIsAnnouncedWhenItChanges()
        {
            OnStaThread(() =>
            {
                var plot = new TracePlot();
                int announced = 0;
                plot.GraticuleColumnsChanged += (sender, e) => announced++;

                Lay(plot, 800, 600);
                Assert.Equal(1, announced);

                Lay(plot, 1000, 600);
                Assert.Equal(2, announced);

                // Same width again: nothing to re-announce, and nothing for the marshal to redo.
                Lay(plot, 1000, 700);
                Assert.Equal(2, announced);
            });
        }

        [Fact]
        public void AMarshalledFrameReachesThePixels()
        {
            OnStaThread(() =>
            {
                TracePlot plot = Laid(out int width, out int height);
                plot.Palette = PlotPalette.Dark;

                var marshal = new RenderMarshal { Columns = plot.GraticuleColumns };
                marshal.Offer(Tone(plot.GraticuleColumns * 4, plot.TopDbm));

                Assert.True(plot.Show(marshal.TakeForRender()));

                // The trace colour must actually be somewhere in the bitmap. A control that
                // rasterised into a surface it then failed to blit passes every other assertion
                // here and shows an empty graticule.
                Assert.True(
                    Contains(plot, PlotPalette.Dark.Trace, width, height),
                    "No pixel of the trace colour reached the bitmap.");
            });
        }

        [Fact]
        public void AFrameDecimatedToADifferentWidthIsDiscarded_NotDrawnAtTheWrongScale()
        {
            OnStaThread(() =>
            {
                TracePlot plot = Laid(out _, out _);

                var marshal = new RenderMarshal { Columns = plot.GraticuleColumns };
                marshal.Offer(Tone(1024, plot.TopDbm));
                TraceSnapshot stale = marshal.TakeForRender();

                // A resize between the marshal and the draw: the envelope no longer matches the
                // graticule, and drawing it would stretch the spectrum across the wrong axis.
                Lay(plot, 1100, 600);

                Assert.False(plot.Show(stale));
            });
        }

        [Fact]
        public void TheReferenceLevelSetsTheTopOfTheGraticule()
        {
            OnStaThread(() =>
            {
                TracePlot plot = Laid(out _, out _);

                Assert.Equal(
                    plot.TopDbm - TracePlot.DecibelsPerDivision * 10.0, plot.BottomDbm, 6);
            });
        }

        [Fact]
        public void ItRefusesAPaletteOfNull()
        {
            OnStaThread(() => Assert.Throws<ArgumentNullException>(() => new TracePlot().Palette = null));
        }

        // ---- Helpers ---------------------------------------------------------------------------

        private static TracePlot Laid(out int width, out int height)
        {
            var plot = new TracePlot();
            Lay(plot, 800, 600);

            var bitmap = (WriteableBitmap)FindImageSource(plot);
            width = bitmap.PixelWidth;
            height = bitmap.PixelHeight;

            return plot;
        }

        private static void Lay(TracePlot plot, double width, double height)
        {
            plot.Measure(new Size(width, height));
            plot.Arrange(new Rect(0.0, 0.0, width, height));
        }

        private static System.Windows.Media.ImageSource FindImageSource(TracePlot plot)
        {
            foreach (UIElement child in plot.Children)
            {
                var image = child as System.Windows.Controls.Image;
                if (image != null)
                {
                    return image.Source;
                }
            }

            throw new InvalidOperationException("The plot has no image to draw into.");
        }

        private static bool Contains(TracePlot plot, PlotColor colour, int width, int height)
        {
            var bitmap = (WriteableBitmap)FindImageSource(plot);
            var pixels = new byte[bitmap.BackBufferStride * height];
            bitmap.CopyPixels(pixels, bitmap.BackBufferStride, 0);

            for (int offset = 0; offset + 3 < pixels.Length; offset += 4)
            {
                if (pixels[offset] == colour.B &&
                    pixels[offset + 1] == colour.G &&
                    pixels[offset + 2] == colour.R)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>A frame whose levels sit inside the graticule, peaking near the top.</summary>
        private static SpectrumFrame Tone(int points, double topDbm)
        {
            var levels = new float[points];

            for (int i = 0; i < points; i++)
            {
                levels[i] = (float)(topDbm - 60.0);
            }

            levels[points / 2] = (float)(topDbm - 10.0);

            return SpectrumFrame.FromLevels(levels, 1e9, 1e3, WindowType.FlatTop, 3.8194);
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
