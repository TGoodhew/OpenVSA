using System;
using System.Runtime.ExceptionServices;
using System.Threading;
using OpenVSA.Measurement.State;
using Xunit;

namespace OpenVSA.Ui.Tests
{
    /// <summary>
    /// <c>REQ-STA-002</c>: the save dialog names the exclusions rather than leaving them to be
    /// discovered.
    /// </summary>
    public class StateSaveDialogTests
    {
        [Fact]
        public void TheSaveDialogNamesAllFourExclusionsInItsOwnText()
        {
            OnStaThread(() =>
            {
                var dialog = new StateSaveDialog(@"C:\setups\bench" + StateFile.Extension);

                foreach (string word in new[] { "recording", "math", "register", "preference" })
                {
                    Assert.True(
                        dialog.NoticeText.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0,
                        "The save dialog does not mention " + word + "s.");
                }
            });
        }

        [Fact]
        public void TheNoticeIsTheOneTheStateFormatDefines()
        {
            // Taken from the format rather than written into the markup, so the wording and the
            // behaviour it describes cannot drift apart.
            OnStaThread(() =>
                Assert.Equal(
                    StateFile.ExclusionNotice,
                    new StateSaveDialog(string.Empty).NoticeText));
        }

        [Fact]
        public void ThePathIsOfferedAndCanBeChanged()
        {
            OnStaThread(() =>
            {
                var dialog = new StateSaveDialog("suggested" + StateFile.Extension);

                Assert.Equal("suggested" + StateFile.Extension, dialog.Path);

                dialog.Path = "elsewhere" + StateFile.Extension;

                Assert.Equal("elsewhere" + StateFile.Extension, dialog.Path);
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
