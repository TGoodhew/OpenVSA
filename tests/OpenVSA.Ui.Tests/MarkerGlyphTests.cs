using System;
using OpenVSA.Ui.Rendering;
using Xunit;

namespace OpenVSA.Ui.Tests
{
    /// <summary>
    /// <c>REQ-UI-030</c>: glyph shapes, placement and selection, measured from the rendered frame.
    /// </summary>
    /// <remarks>
    /// The requirement names two subtleties a developer will otherwise miss — the diamond is offset
    /// above the point while the X is centred on it, and selection is conveyed by fill rather than
    /// by colour index. Both are asserted here against sampled pixels rather than against the
    /// drawing code's intent.
    /// </remarks>
    public class MarkerGlyphTests
    {
        private static readonly PlotColor Ink = PlotColor.FromArgb(0xFFFF0000);

        [Fact]
        public void ADiamondsBoundsLieEntirelyAboveTheDataPoint()
        {
            var surface = new PixelSurface(80, 80);
            MarkerGlyph.DrawDiamond(surface, 40, 50, Ink, filled: true);

            PixelRect bounds = BoundsOfInk(surface);

            Assert.True(
                bounds.Bottom - 1 < 50,
                "The diamond reaches row " + (bounds.Bottom - 1) + ", which is not above row 50.");
            Assert.Equal(MarkerGlyph.DiamondBounds(40, 50).Y, bounds.Y);
        }

        [Fact]
        public void AnXIsCentredOnTheDataPointToWithinOnePixel()
        {
            var surface = new PixelSurface(80, 80);
            MarkerGlyph.DrawCross(surface, 40, 50, Ink, filled: false);

            Point centroid = CentroidOfInk(surface);

            Assert.InRange(centroid.X, 39.0, 41.0);
            Assert.InRange(centroid.Y, 49.0, 51.0);
        }

        [Fact]
        public void TheTwoGlyphsAreDistinguishedByThatPlacement()
        {
            // The pairing the requirement's criterion turns on: the X's centroid coincides with the
            // data point and the diamond's does not.
            var withCross = new PixelSurface(80, 80);
            var withDiamond = new PixelSurface(80, 80);

            MarkerGlyph.DrawCross(withCross, 40, 50, Ink, filled: false);
            MarkerGlyph.DrawDiamond(withDiamond, 40, 50, Ink, filled: false);

            Assert.InRange(CentroidOfInk(withCross).Y, 49.0, 51.0);
            Assert.True(CentroidOfInk(withDiamond).Y < 45.0);
        }

        [Fact]
        public void TheSelectedMarkerIsFilledAndAnUnselectedOneIsHollow()
        {
            var selected = new PixelSurface(80, 80);
            var unselected = new PixelSurface(80, 80);

            MarkerGlyph.DrawDiamond(selected, 40, 50, Ink, filled: true);
            MarkerGlyph.DrawDiamond(unselected, 40, 50, Ink, filled: false);

            // The interior distinguishes them: the centre of a hollow diamond is background.
            int centreY = 50 - MarkerGlyph.HalfSize - MarkerGlyph.DiamondClearance;

            Assert.Equal(Ink, selected.GetPixel(40, centreY));
            Assert.NotEqual(Ink, unselected.GetPixel(40, centreY));

            // Both are still diamonds: the outline is present either way.
            Assert.Equal(Ink, unselected.GetPixel(40 - MarkerGlyph.HalfSize, centreY));
            Assert.Equal(Ink, unselected.GetPixel(40 + MarkerGlyph.HalfSize, centreY));
        }

        [Fact]
        public void ColourComesFromTheCallerBySelectionState_NotFromAMarkerNumber()
        {
            // REQ-UI-030: two unselected markers of different numbers render the same colour.
            // Nothing in the glyph API accepts a marker number, which is what makes colouring by
            // index impossible rather than merely discouraged.
            var surface = new PixelSurface(80, 80);

            MarkerGlyph.DrawDiamond(surface, 20, 50, PlotPalette.Dark.NotSelectedMarker, false);
            MarkerGlyph.DrawDiamond(surface, 60, 50, PlotPalette.Dark.NotSelectedMarker, false);

            int centreY = 50 - MarkerGlyph.HalfSize - MarkerGlyph.DiamondClearance;

            Assert.Equal(
                surface.GetPixel(20 - MarkerGlyph.HalfSize, centreY),
                surface.GetPixel(60 - MarkerGlyph.HalfSize, centreY));
        }

        [Fact]
        public void AGlyphAtTheEdgeIsClippedRatherThanThrowing()
        {
            var surface = new PixelSurface(20, 20);

            MarkerGlyph.DrawDiamond(surface, 0, 0, Ink, true);
            MarkerGlyph.DrawCross(surface, 19, 19, Ink, true);
            MarkerGlyph.DrawDiamond(surface, 19, 3, Ink, false);
        }

        [Fact]
        public void ItRefusesASurfaceOfNull()
        {
            Assert.Throws<ArgumentNullException>(() => MarkerGlyph.DrawDiamond(null, 0, 0, Ink, true));
            Assert.Throws<ArgumentNullException>(() => MarkerGlyph.DrawCross(null, 0, 0, Ink, true));
        }

        private struct Point
        {
            public double X;
            public double Y;
        }

        private static PixelRect BoundsOfInk(PixelSurface surface)
        {
            int left = int.MaxValue, top = int.MaxValue, right = int.MinValue, bottom = int.MinValue;

            for (int y = 0; y < surface.Height; y++)
            {
                for (int x = 0; x < surface.Width; x++)
                {
                    if (!surface.GetPixel(x, y).Equals(Ink))
                    {
                        continue;
                    }

                    left = Math.Min(left, x);
                    top = Math.Min(top, y);
                    right = Math.Max(right, x);
                    bottom = Math.Max(bottom, y);
                }
            }

            Assert.True(left <= right, "Nothing was drawn.");
            return new PixelRect(left, top, right - left + 1, bottom - top + 1);
        }

        private static Point CentroidOfInk(PixelSurface surface)
        {
            double sumX = 0.0, sumY = 0.0;
            int count = 0;

            for (int y = 0; y < surface.Height; y++)
            {
                for (int x = 0; x < surface.Width; x++)
                {
                    if (surface.GetPixel(x, y).Equals(Ink))
                    {
                        sumX += x;
                        sumY += y;
                        count++;
                    }
                }
            }

            Assert.True(count > 0, "Nothing was drawn.");
            return new Point { X = sumX / count, Y = sumY / count };
        }
    }
}
