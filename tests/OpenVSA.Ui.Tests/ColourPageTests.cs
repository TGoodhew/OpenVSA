using System;
using OpenVSA.Ui.Dialogs.Pages;
using OpenVSA.Ui.Rendering;
using Xunit;

namespace OpenVSA.Ui.Tests
{
    /// <summary>
    /// <c>REQ-UI-014</c>: the Colour tab of Display Preferences.
    /// </summary>
    /// <remarks>
    /// The picker was a window of its own until <c>REQ-UI-070</c> asked for tabbed, modeless, live
    /// dialogs; it is now a page of one. The behaviour asserted here is the same either way, which
    /// is the point — the requirement is about the list being generated, not about where it lives.
    /// </remarks>
    public class ColourPageTests
    {
        [Fact]
        public void ThePageOpensOverTheWholeElementSet()
        {
            Sta.Run(() =>
            {
                var preferences = new ColourPreferences();
                var page = new ColourPage(preferences);

                Assert.Equal(preferences.Entries.Count, page.ListedCount);
            });
        }

        [Fact]
        public void ThePageNeedsPreferencesToEdit()
        {
            Sta.Run(() => Assert.Throws<ArgumentNullException>(() => new ColourPage(null)));
        }

        [Fact]
        public void OpeningThePageChangesNothingByItself()
        {
            // Constructing the page selects the first element and fills the sliders from it. If
            // that counted as a change, merely opening the tab would write a preferences file full
            // of the defaults it just read.
            Sta.Run(() =>
            {
                var preferences = new ColourPreferences();

                new ColourPage(preferences);

                Assert.Equal(0, preferences.ChangedCount);
            });
        }

        [Fact]
        public void TheFilterNarrowsTheListWithoutLosingTheSetBehindIt()
        {
            Sta.Run(() =>
            {
                var preferences = new ColourPreferences();
                var page = new ColourPage(preferences) { Filter = "Grid" };

                Assert.True(page.ListedCount > 0);
                Assert.True(page.ListedCount < preferences.Entries.Count);

                page.Filter = string.Empty;

                Assert.Equal(preferences.Entries.Count, page.ListedCount);
            });
        }

        [Fact]
        public void SelectingAnElementByKeyShowsThatElement()
        {
            Sta.Run(() =>
            {
                var page = new ColourPage(new ColourPreferences());

                Assert.True(page.Select("OpenVSA.Grid"));
                Assert.NotNull(page.Selected);
                Assert.Equal("OpenVSA.Grid", page.Selected.Key);
            });
        }
    }
}
