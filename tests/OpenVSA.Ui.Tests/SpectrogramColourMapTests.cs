using System;
using System.Collections.Generic;
using System.Linq;
using OpenVSA.Ui.Rendering;
using Xunit;

namespace OpenVSA.Ui.Tests
{
    /// <summary>
    /// <c>REQ-UI-024</c>: the spectrogram colour maps.
    /// </summary>
    public sealed class SpectrogramColourMapTests
    {
        [Fact]
        public void EachBuiltInMapHasExactlySixtyFourEntries()
        {
            // The requirement's own number, and the one hard colour statement in the reference
            // documentation.
            Assert.Equal(64, SpectrogramColourMap.ColorNormal().Count);
            Assert.Equal(64, SpectrogramColourMap.ColorReverse().Count);
            Assert.Equal(64, SpectrogramColourMap.GreyNormal().Count);
            Assert.Equal(64, SpectrogramColourMap.GreyReverse().Count);
        }

        [Fact]
        public void ColorNormalRunsBlueAtTheMinimumToRedAtTheMaximum()
        {
            SpectrogramColourMap map = SpectrogramColourMap.ColorNormal();

            Assert.Equal(new PlotColor(0, 0, 255), map.Minimum);
            Assert.Equal(new PlotColor(255, 0, 0), map.Maximum);
        }

        [Fact]
        public void ColorReverseIsColorNormalTurnedAround()
        {
            SpectrogramColourMap normal = SpectrogramColourMap.ColorNormal();
            SpectrogramColourMap reverse = SpectrogramColourMap.ColorReverse();

            Assert.Equal(normal.Maximum, reverse.Minimum);
            Assert.Equal(normal.Minimum, reverse.Maximum);

            for (int i = 0; i < normal.Count; i++)
            {
                Assert.Equal(normal.Entries[normal.Count - 1 - i], reverse.Entries[i]);
            }
        }

        [Fact]
        public void GreyNormalRunsBlackAtTheMinimumToWhiteAtTheMaximum()
        {
            SpectrogramColourMap map = SpectrogramColourMap.GreyNormal();

            Assert.Equal(new PlotColor(0, 0, 0), map.Minimum);
            Assert.Equal(new PlotColor(255, 255, 255), map.Maximum);
        }

        [Fact]
        public void GreyReverseIsGreyNormalTurnedAround()
        {
            SpectrogramColourMap normal = SpectrogramColourMap.GreyNormal();
            SpectrogramColourMap reverse = SpectrogramColourMap.GreyReverse();

            Assert.Equal(new PlotColor(255, 255, 255), reverse.Minimum);
            Assert.Equal(new PlotColor(0, 0, 0), reverse.Maximum);

            for (int i = 0; i < normal.Count; i++)
            {
                Assert.Equal(normal.Entries[normal.Count - 1 - i], reverse.Entries[i]);
            }
        }

        [Fact]
        public void EveryGreyEntryIsActuallyGrey()
        {
            // A grey map that had drifted off the diagonal would still look like a ramp and would
            // no longer be a pure luminance ramp, which is the entire reason it resolves more steps
            // than a coloured one.
            foreach (PlotColor entry in SpectrogramColourMap.GreyNormal().Entries)
            {
                Assert.Equal(entry.R, entry.G);
                Assert.Equal(entry.G, entry.B);
            }
        }

        [Fact]
        public void EveryEntryOfABuiltInMapIsDistinct()
        {
            // 64 entries that collapsed to fewer would quantise the display more coarsely than the
            // requirement asks, without anything failing.
            foreach (SpectrogramColourMapKind kind in new[]
            {
                SpectrogramColourMapKind.ColorNormal,
                SpectrogramColourMapKind.ColorReverse,
                SpectrogramColourMapKind.GreyNormal,
                SpectrogramColourMapKind.GreyReverse,
            })
            {
                SpectrogramColourMap map = SpectrogramColourMap.Of(kind);

                Assert.Equal(64, map.Entries.Distinct().Count());
            }
        }

        [Fact]
        public void ColorNormalIsTheDefault()
        {
            Assert.Equal(SpectrogramColourMapKind.ColorNormal, SpectrogramColourMap.Default.Kind);
        }

        [Fact]
        public void TheFractionZeroPicksTheMinimumAndOnePicksTheMaximum()
        {
            SpectrogramColourMap map = SpectrogramColourMap.ColorNormal();

            Assert.Equal(map.Minimum, map.At(0.0));
            Assert.Equal(map.Maximum, map.At(1.0));
        }

        [Fact]
        public void FractionsOutsideTheRangeClampRatherThanWrap()
        {
            SpectrogramColourMap map = SpectrogramColourMap.ColorNormal();

            Assert.Equal(map.Minimum, map.At(-4.0));
            Assert.Equal(map.Maximum, map.At(4.0));
        }

        [Fact]
        public void AFractionSweepVisitsEverySingleEntry()
        {
            // The map is the quantisation: 64 entries must all be reachable, or the display shows
            // fewer levels than the map claims.
            SpectrogramColourMap map = SpectrogramColourMap.ColorNormal();
            var seen = new HashSet<PlotColor>();

            for (int i = 0; i < 6400; i++)
            {
                seen.Add(map.At(i / 6400.0));
            }

            Assert.Equal(64, seen.Count);
        }

        [Fact]
        public void IndexZeroIsTheBottomOfAUserMap()
        {
            // Stated by the requirement, and the convention every built-in map follows too.
            SpectrogramColourMap map = SpectrogramColourMap.User(new[]
            {
                new PlotColor(1, 1, 1),
                new PlotColor(2, 2, 2),
                new PlotColor(3, 3, 3),
            });

            Assert.Equal(new PlotColor(1, 1, 1), map.Minimum);
            Assert.Equal(new PlotColor(1, 1, 1), map.At(0.0));
            Assert.Equal(new PlotColor(3, 3, 3), map.At(1.0));
        }

        [Fact]
        public void ReducingTheCountDiscardsFromTheTop()
        {
            // The surprising direction, and the one the requirement states. Discarding from the
            // bottom would move what the spectrogram's floor renders as every time the count
            // changed.
            SpectrogramColourMap map = SpectrogramColourMap.User(new[]
            {
                new PlotColor(1, 1, 1),
                new PlotColor(2, 2, 2),
                new PlotColor(3, 3, 3),
                new PlotColor(4, 4, 4),
            });

            SpectrogramColourMap shortened = map.WithCount(2);

            Assert.Equal(2, shortened.Count);
            Assert.Equal(new PlotColor(1, 1, 1), shortened.Entries[0]);
            Assert.Equal(new PlotColor(2, 2, 2), shortened.Entries[1]);
        }

        [Fact]
        public void ShorteningAMapLeavesItsMinimumWhereItWas()
        {
            SpectrogramColourMap map = SpectrogramColourMap.GreyNormal();

            Assert.Equal(map.Minimum, map.WithCount(8).Minimum);
            Assert.Equal(map.Minimum, map.WithCount(2).Minimum);
        }

        [Fact]
        public void AShortenedMapKeepsItsKind()
        {
            Assert.Equal(
                SpectrogramColourMapKind.GreyNormal,
                SpectrogramColourMap.GreyNormal().WithCount(16).Kind);
        }

        [Fact]
        public void AMapCannotBeShortenedBelowTwoEntriesOrLengthened()
        {
            SpectrogramColourMap map = SpectrogramColourMap.GreyNormal();

            Assert.Throws<ArgumentOutOfRangeException>(() => map.WithCount(1));
            Assert.Throws<ArgumentOutOfRangeException>(() => map.WithCount(65));
        }

        [Fact]
        public void AUserMapNeedsAtLeastAMinimumAndAMaximum()
        {
            Assert.Throws<ArgumentException>(
                () => SpectrogramColourMap.User(new[] { new PlotColor(1, 1, 1) }));
        }

        [Fact]
        public void TheNamesAreTheReferenceProductsOwnSpellings()
        {
            Assert.Equal("Color Normal", SpectrogramColourMap.NameOf(SpectrogramColourMapKind.ColorNormal));
            Assert.Equal("Color Reverse", SpectrogramColourMap.NameOf(SpectrogramColourMapKind.ColorReverse));
            Assert.Equal("Grey Normal", SpectrogramColourMap.NameOf(SpectrogramColourMapKind.GreyNormal));
            Assert.Equal("Grey Reverse", SpectrogramColourMap.NameOf(SpectrogramColourMapKind.GreyReverse));
            Assert.Equal("User Defined", SpectrogramColourMap.NameOf(SpectrogramColourMapKind.UserDefined));
        }

        [Fact]
        public void EveryNameParsesBackToItsOwnKind()
        {
            foreach (SpectrogramColourMapKind kind in
                (SpectrogramColourMapKind[])Enum.GetValues(typeof(SpectrogramColourMapKind)))
            {
                SpectrogramColourMapKind parsed;
                Assert.True(SpectrogramColourMap.TryParseName(
                    SpectrogramColourMap.NameOf(kind), out parsed));
                Assert.Equal(kind, parsed);
            }
        }

        [Fact]
        public void AnUnknownNameIsRejectedRatherThanApproximated()
        {
            SpectrogramColourMapKind parsed;

            Assert.False(SpectrogramColourMap.TryParseName("Colour Normal", out parsed));
            Assert.False(SpectrogramColourMap.TryParseName("color normal", out parsed));
        }

        [Fact]
        public void UserDefinedHasNoBuiltInForm()
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => SpectrogramColourMap.Of(SpectrogramColourMapKind.UserDefined));
        }

        [Fact]
        public void TheSampleMapMarksTheSelectionLowFirst()
        {
            // Dragged upwards or downwards, a selection reads the same.
            double[] up = SpectrogramColourMap.SelectionMarks(0.2, 0.8);
            double[] down = SpectrogramColourMap.SelectionMarks(0.8, 0.2);

            Assert.Equal(new[] { 0.2, 0.8 }, up);
            Assert.Equal(up, down);
        }

        [Fact]
        public void SelectionMarksAreClampedToTheMap()
        {
            double[] marks = SpectrogramColourMap.SelectionMarks(-1.0, 3.0);

            Assert.Equal(new[] { 0.0, 1.0 }, marks);
        }
    }
}
