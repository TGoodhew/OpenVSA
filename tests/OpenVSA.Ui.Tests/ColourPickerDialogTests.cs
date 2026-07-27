using System;
using System.Runtime.ExceptionServices;
using System.Threading;
using OpenVSA.Ui.Rendering;
using Xunit;

namespace OpenVSA.Ui.Tests
{
    /// <summary>
    /// <c>REQ-UI-014</c>: the colour picker itself.
    /// </summary>
    public class ColourPickerDialogTests
    {
        [Fact]
        public void ThePickerOpensOverTheWholeElementSet()
        {
            OnStaThread(() =>
            {
                var preferences = new ColourPreferences();
                var dialog = new ColourPickerDialog(preferences);

                Assert.Equal(preferences.Entries.Count, dialog.ListedCount);
            });
        }

        [Fact]
        public void ThePickerNeedsPreferencesToEdit()
        {
            OnStaThread(() =>
                Assert.Throws<ArgumentNullException>(() => new ColourPickerDialog(null)));
        }

        [Fact]
        public void OpeningThePickerChangesNothingByItself()
        {
            // Constructing the dialog selects the first element and fills the sliders from it. If
            // that counted as a change, merely opening the picker would write a preferences file
            // full of the defaults it just read.
            OnStaThread(() =>
            {
                var preferences = new ColourPreferences();

                new ColourPickerDialog(preferences);

                Assert.Equal(0, preferences.ChangedCount);
            });
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
                catch (Exception caught)
                {
                    failure = ExceptionDispatchInfo.Capture(caught);
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
