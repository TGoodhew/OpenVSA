using System;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Media.Imaging;
using OpenVSA.Core;
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
                    plot.TopDbm - plot.DecibelsPerDivision * plot.VerticalDivisions,
                    plot.BottomDbm,
                    6);
            });
        }

        [Fact]
        public void ItRefusesAPaletteOfNull()
        {
            OnStaThread(() => Assert.Throws<ArgumentNullException>(() => new TracePlot().Palette = null));
        }

        [Fact]
        public void AnUnaveragedTraceCarriesNoAveragingNote()
        {
            Assert.Equal(string.Empty, TracePlot.AveragingNote(Tone(64, 0.0)));
        }

        [Fact]
        public void IndependentAveragesAreAnnotatedAsACountAlone()
        {
            // Nothing to qualify: separate acquisitions are worth what they number, so a
            // parenthesis here would be noise that trains the reader to skip the one that matters.
            SpectrumFrame averaged = Average(6, overlap: 0.0, window: WindowType.Uniform);

            Assert.Equal("   Avg 6", TracePlot.AveragingNote(averaged));
        }

        [Fact]
        public void OverlappedAveragesAreAnnotatedWithWhatTheyAreWorth()
        {
            // REQ-DSP-031: the effective count is displayed, not merely computed. Six uniformly
            // windowed frames cut from one record at three-quarter overlap are six acquisitions
            // and appreciably fewer independent ones.
            SpectrumFrame averaged = Average(6, overlap: 0.75, window: WindowType.Uniform);

            string note = TracePlot.AveragingNote(averaged);

            Assert.StartsWith("   Avg 6 (", note);
            Assert.EndsWith(" eff)", note);
            Assert.True(averaged.EffectiveAverageCount < 6.0);
        }

        [Fact]
        public void ATaperedWindowLosesTooLittleToOverlapToBeWorthAnnotating()
        {
            // The same overlap under Flat Top costs less than a tenth of an average, because the
            // window has already weighted the shared samples to nearly nothing. Annotating that
            // would be reporting a difference smaller than anyone can read - and it is the reason
            // the note is conditional rather than always printed.
            SpectrumFrame averaged = Average(6, overlap: 0.75, window: WindowType.FlatTop);

            Assert.Equal("   Avg 6", TracePlot.AveragingNote(averaged));
            Assert.True(averaged.EffectiveAverageCount > 5.9);
        }

        [Fact]
        public void ATraceWhoseTransformWasNotCappedSaysNothingAboutIt()
        {
            // REQ-DSP-024. Annotating every trace with its transform length would spend the band's
            // width on a number the point count already implies, and would leave the one case that
            // matters looking like all the others.
            Assert.Equal(string.Empty, TracePlot.TransformNote(Tone(64, 0.0)));
        }

        [Fact]
        public void ACappedTransformIsAnnotatedWithTheSizeItWasCappedTo()
        {
            // The criterion: "the bound is visible in the trace annotation so the user knows the
            // resolution was capped". Nothing about the trace itself shows it - a spectrum measured
            // at half the resolution it could have had looks entirely normal.
            SpectrumFrame capped = Capped(4099, 512);

            Assert.True(capped.TransformWasCapped);
            Assert.Equal("   FFT 512 (capped)", TracePlot.TransformNote(capped));
        }

        [Fact]
        public void ANoiseCorrectedTraceIsAnnotatedAndAnUncorrectedOneIsNot()
        {
            // A corrected trace and an uncorrected one differ most where the signal is weakest,
            // which is where someone is most likely to be reading a number off the screen and
            // least likely to remember which setting was in force.
            SpectrumFrame measured = Tone(64, 0.0);

            Assert.Equal(string.Empty, TracePlot.NoiseCorrectionNote(measured));

            SpectrumFrame corrected = NoiseCorrection.Apply(
                measured, NoiseFloor.Flat(-200.0, measured.ResolutionBandwidthHz));

            Assert.Equal("   Noise corr", TracePlot.NoiseCorrectionNote(corrected));
        }

        [Fact]
        public void ItRefusesAFrameOfNullToAnnotate()
        {
            Assert.Throws<ArgumentNullException>(() => TracePlot.AveragingNote(null));
            Assert.Throws<ArgumentNullException>(() => TracePlot.TransformNote(null));
            Assert.Throws<ArgumentNullException>(() => TracePlot.NoiseCorrectionNote(null));
        }

        /// <summary>A spectrum of a block long enough that the ceiling, not the record, chose N.</summary>
        private static SpectrumFrame Capped(int samples, int ceiling)
        {
            var computer = new SpectrumComputer(WindowType.FlatTop, null, null)
            {
                MaxTransformLength = ceiling,
            };

            IqBlock block = IqBlock.Rent(new IqBlockMetadata(
                sampleCount: samples,
                sampleRateHz: 15e6,
                centerFrequencyHz: 1e9,
                isBaseband: false,
                fullScaleVolts: 1.0,
                referenceLevelDbm: 0.0,
                sequenceNumber: 1,
                acquiredUtc: new DateTime(2026, 7, 26, 0, 0, 0, DateTimeKind.Utc),
                triggerOffsetSeconds: 0.0,
                triggerCorrectionsApplied: false,
                source: new FrontEndId("test"),
                extended: null));

            using (block)
            {
                Span<float> data = block.GetSamples();

                for (int n = 0; n < samples; n++)
                {
                    double angle = 2.0 * Math.PI * 0.1543 * n;

                    data[n * 2] = (float)Math.Cos(angle);
                    data[n * 2 + 1] = (float)Math.Sin(angle);
                }

                return computer.Compute(block);
            }
        }

        // ---- Helpers ---------------------------------------------------------------------------

        /// <summary>Accumulates a number of frames, as though cut at the given overlap.</summary>
        private static SpectrumFrame Average(int frames, double overlap, WindowType window)
        {
            var averager = new TraceAverager(AveragingType.RmsVideo, frames)
            {
                Overlap = overlap,
                RecordSamples = 1024,
            };

            var levels = new float[64];
            SpectrumFrame result = null;

            for (int i = 0; i < frames; i++)
            {
                for (int p = 0; p < levels.Length; p++)
                {
                    levels[p] = -60.0f + i;
                }

                result = averager.Accumulate(
                    SpectrumFrame.FromLevels(levels, 1e9, 1e3, window, 1.0));
            }

            return result;
        }

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
