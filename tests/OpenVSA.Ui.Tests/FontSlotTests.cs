using System;
using System.Collections.Generic;
using OpenVSA.Measurement.State;
using OpenVSA.Ui.Rendering;
using Xunit;

namespace OpenVSA.Ui.Tests
{
    /// <summary>
    /// <c>REQ-UI-080</c>: the three font slots.
    /// </summary>
    public class FontSlotTests
    {
        [Fact]
        public void ThereAreExactlyThreeSlots()
        {
            Assert.Equal(
                new[] { FontSlot.Annotation, FontSlot.Marker, FontSlot.Tabular },
                new List<FontSlot>(FontPreferences.Slots));
        }

        [Fact]
        public void TheDefaultsAreTheRecommendedOnes()
        {
            // "Segoe UI 9 pt for chrome and annotation, Consolas 9 pt for Markers, symbol table and
            // error summary" — the requirement's own recommendation, since the reference product's
            // defaults are unpublished.
            Assert.Equal("Segoe UI", FontPreferences.DefaultFor(FontSlot.Annotation).Family);
            Assert.Equal(9.0, FontPreferences.DefaultFor(FontSlot.Annotation).SizePoints, 3);

            Assert.Equal("Consolas", FontPreferences.DefaultFor(FontSlot.Marker).Family);
            Assert.Equal("Consolas", FontPreferences.DefaultFor(FontSlot.Tabular).Family);
        }

        [Fact]
        public void SettingOneSlotLeavesTheOtherTwoUnchanged()
        {
            // The criterion, and the thing a single "application font" would fail.
            var fonts = new FontPreferences();

            fonts.Set(FontSlot.Marker, new FontChoice("Courier New", 14.0));

            Assert.Equal("Courier New", fonts[FontSlot.Marker].Family);
            Assert.Equal(14.0, fonts[FontSlot.Marker].SizePoints, 3);

            Assert.Equal(FontPreferences.DefaultFor(FontSlot.Annotation), fonts[FontSlot.Annotation]);
            Assert.Equal(FontPreferences.DefaultFor(FontSlot.Tabular), fonts[FontSlot.Tabular]);
        }

        [Fact]
        public void MarkerAndTabularResolveToFixedPitchByDefault()
        {
            // Asserted on the resolved face's pitch, measured from its glyphs — not on the family
            // name, which would only assert that someone had spelled Consolas correctly.
            Sta.Run(() =>
            {
                var fonts = new FontPreferences();

                foreach (FontSlot slot in new[] { FontSlot.Marker, FontSlot.Tabular })
                {
                    string resolved = fonts.ResolveFamily(slot);

                    Assert.True(
                        FontPreferences.IsFixedPitch(resolved),
                        FontPreferences.NameOf(slot) + " resolved to " + resolved +
                        ", which is not fixed pitch.");
                }
            });
        }

        [Fact]
        public void AnnotationMayBeProportional()
        {
            // The whole reason the third slot exists: general annotation reads better proportional,
            // and the reference product's two-slot scheme forces a compromise on the tables.
            Sta.Run(() =>
            {
                var fonts = new FontPreferences();

                Assert.False(FontPreferences.IsFixedPitch(fonts.ResolveFamily(FontSlot.Annotation)));
                Assert.False(FontPreferences.RequiresFixedPitch(FontSlot.Annotation));
                Assert.True(FontPreferences.RequiresFixedPitch(FontSlot.Marker));
                Assert.True(FontPreferences.RequiresFixedPitch(FontSlot.Tabular));
            });
        }

        [Fact]
        public void ThePitchTestTellsTheTwoApart()
        {
            // The discriminator: a pitch test that answered "fixed" to everything would pass the
            // test above and would let a proportional Markers window through.
            Sta.Run(() =>
            {
                Assert.True(FontPreferences.IsFixedPitch("Courier New"));
                Assert.False(FontPreferences.IsFixedPitch("Times New Roman"));

                // A name that is not a family at all is not fixed pitch either, rather than a throw
                // from a caller who only wanted to know whether a table would line up.
                Assert.False(FontPreferences.IsFixedPitch("No Such Typeface At All"));
            });
        }

        [Fact]
        public void AnUnavailableFamilyFallsBackToADocumentedOne()
        {
            Sta.Run(() =>
            {
                var fonts = new FontPreferences();
                fonts.Set(FontSlot.Marker, new FontChoice("No Such Typeface At All", 9.0));

                string resolved = fonts.ResolveFamily(FontSlot.Marker);

                Assert.Contains(resolved, new List<string>(FontPreferences.Fallbacks(FontSlot.Marker)));
                Assert.True(FontPreferences.IsFixedPitch(resolved));

                // The slot still records what was asked for: a preferences file written on a
                // machine without the face must not permanently record the substitute.
                Assert.Equal("No Such Typeface At All", fonts[FontSlot.Marker].Family);
            });
        }

        [Fact]
        public void PointsBecomeDeviceIndependentPixels()
        {
            // A point is 1/72 inch and a WPF pixel 1/96, so 9 pt is 12 units. Getting this wrong
            // makes every font in the application 25 per cent too small and looks like a theme.
            Assert.Equal(12.0, new FontChoice("Consolas", 9.0).SizeDip, 6);
        }

        [Fact]
        public void OnlyChangedSlotsAreStored()
        {
            var fonts = new FontPreferences();
            var state = new DisplayPreferencesState();

            fonts.SaveInto(state);
            Assert.Empty(state.Fonts);

            fonts.Set(FontSlot.Tabular, new FontChoice("Courier New", 11.0));
            fonts.SaveInto(state);

            Assert.Single(state.Fonts);
            Assert.Equal("Tabular", state.Fonts[0].Slot);

            // Setting it back to the default drops the change rather than recording it.
            fonts.Set(FontSlot.Tabular, FontPreferences.DefaultFor(FontSlot.Tabular));
            fonts.SaveInto(state);

            Assert.Empty(state.Fonts);
            Assert.Equal(0, fonts.ChangedCount);
        }

        [Fact]
        public void SlotsSurviveTheSidecar()
        {
            var before = new FontPreferences();
            before.Set(FontSlot.Annotation, new FontChoice("Tahoma", 10.0));
            before.Set(FontSlot.Marker, new FontChoice("Courier New", 8.0));

            var state = new DisplayPreferencesState();
            before.SaveInto(state);

            var after = new FontPreferences();
            Assert.Empty(after.LoadFrom(state));

            Assert.Equal(new FontChoice("Tahoma", 10.0), after[FontSlot.Annotation]);
            Assert.Equal(new FontChoice("Courier New", 8.0), after[FontSlot.Marker]);
            Assert.Equal(FontPreferences.DefaultFor(FontSlot.Tabular), after[FontSlot.Tabular]);
        }

        [Fact]
        public void AnUnreadableEntryCostsThatSlotAndNothingElse()
        {
            var state = new DisplayPreferencesState
            {
                Fonts = new List<FontSlotState>
                {
                    new FontSlotState { Slot = "Annotation", Family = "Tahoma", SizePoints = 10.0 },
                    new FontSlotState { Slot = "Heading", Family = "Tahoma", SizePoints = 10.0 },
                    new FontSlotState { Slot = "Marker", Family = "Consolas", SizePoints = 900.0 },
                },
            };

            var fonts = new FontPreferences();
            IReadOnlyList<string> unknown = fonts.LoadFrom(state);

            Assert.Equal(new[] { "Heading", "Marker" }, new List<string>(unknown));
            Assert.Equal("Tahoma", fonts[FontSlot.Annotation].Family);
            Assert.Equal(FontPreferences.DefaultFor(FontSlot.Marker), fonts[FontSlot.Marker]);
        }

        [Fact]
        public void ASlotAnnouncesItsOwnChange()
        {
            int announced = 0;

            var fonts = new FontPreferences();
            fonts.Changed += (sender, e) => announced++;

            fonts.Set(FontSlot.Marker, new FontChoice("Courier New", 9.0));
            Assert.Equal(1, announced);

            // Setting it to what it already is is not a change, and a surface that redrew for it
            // would redraw on every keystroke of a font name.
            fonts.Set(FontSlot.Marker, new FontChoice("Courier New", 9.0));
            Assert.Equal(1, announced);

            fonts.Reset(FontSlot.Marker);
            Assert.Equal(2, announced);
        }

        [Fact]
        public void ASizeOutsideTheSettableRangeIsRefused()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new FontChoice("Consolas", 1.0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new FontChoice("Consolas", 500.0));
            Assert.Throws<ArgumentException>(() => new FontChoice("  ", 9.0));
        }
    }
}
