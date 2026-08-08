using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace OpenVSA.Ui.Theming
{
    /// <summary>
    /// Paints the window's own caption bar with the chrome theme (<c>REQ-UI-083</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The caption bar is not ours to style, so it is told rather than drawn.</strong> The
    /// title, the icon and the minimise/maximise/close buttons are drawn by the desktop window
    /// manager outside the WPF visual tree: no resource dictionary, no control template and no
    /// implicit style reaches them. A dark shell under a light caption bar was the one remaining
    /// place where the window disagreed with itself, and the only mechanism that reaches it is
    /// <c>DwmSetWindowAttribute</c>.
    /// </para>
    /// <para>
    /// <strong>The colours are read from the theme, not decided here.</strong> The caption takes
    /// <see cref="ChromeKeys.WindowBackground"/>, its text <see cref="ChromeKeys.WindowForeground"/>
    /// and its border <see cref="ChromeKeys.Border"/>. That is what keeps <c>REQ-UI-083</c>'s
    /// architectural rule intact — no code here compares a theme's name against a literal, and a
    /// third theme supplied later as a dictionary gets a matching caption bar with nothing added.
    /// </para>
    /// <para>
    /// <strong>Dark mode is derived from the background's luminance, not from the theme's name.</strong>
    /// The system buttons keep their own hover and pressed fills, which the caption colour does not
    /// reach; <c>DWMWA_USE_IMMERSIVE_DARK_MODE</c> is what makes those light-on-dark. Deriving it
    /// from the colour the theme actually supplies is the difference between a mechanism and a
    /// hard-coded pair — a custom dark theme gets light system buttons without being enumerated
    /// anywhere.
    /// </para>
    /// <para>
    /// <strong>Silent where the platform is older, by design.</strong> The caption, text and border
    /// attributes arrived in Windows 11 build 22000; the immersive dark mode attribute is older.
    /// <c>DwmSetWindowAttribute</c> answers an unknown attribute with a failure code, which is
    /// ignored: on Windows 10 the shell themes and the caption bar does not, which is the same
    /// outcome as not calling it and strictly better than refusing to start.
    /// </para>
    /// </remarks>
    public static class TitleBar
    {
        private const double MidPoint = 128.0;

        private const int UseImmersiveDarkMode = 20;
        private const int BorderColour = 34;
        private const int CaptionColour = 35;
        private const int TextColour = 36;

        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(
            IntPtr window, int attribute, ref int value, int size);

        /// <summary>
        /// Applies the current theme's colours to a window's caption bar.
        /// </summary>
        /// <param name="window">The window; ignored if null or not yet given a handle.</param>
        /// <remarks>
        /// Safe to call before the handle exists and safe to call repeatedly — a theme change calls
        /// it again, and the caption bar follows without a restart in the way the rest of the chrome
        /// does.
        /// </remarks>
        public static void Apply(Window window)
        {
            if (window == null)
            {
                return;
            }

            IntPtr handle = new WindowInteropHelper(window).Handle;

            if (handle == IntPtr.Zero)
            {
                return;
            }

            Color? background = ColourOf(window, ChromeKeys.WindowBackground);

            if (background == null)
            {
                return;
            }

            // The yes-or-no is Win32's, not ours: DWMWA_USE_IMMERSIVE_DARK_MODE takes a BOOL. It is
            // computed here from the colour the theme supplied and passed straight out, so nothing
            // holds it and there is nothing for a third theme to unpick -- which is the thing
            // REQ-UI-083 forbids, and why ThemingArchitectureTests looks for a stored 'is dark'.
            int immersive = Luminance(background.Value) < MidPoint ? 1 : 0;

            DwmSetWindowAttribute(handle, UseImmersiveDarkMode, ref immersive, sizeof(int));

            Set(handle, CaptionColour, background.Value);

            Color? foreground = ColourOf(window, ChromeKeys.WindowForeground);

            if (foreground != null)
            {
                Set(handle, TextColour, foreground.Value);
            }

            Color? border = ColourOf(window, ChromeKeys.Border);

            if (border != null)
            {
                Set(handle, BorderColour, border.Value);
            }
        }

        /// <summary>Rec. 601 luma of a colour, 0 to 255.</summary>
        /// <param name="colour">The colour.</param>
        /// <remarks>
        /// A property of a colour, not of a theme. The threshold it is compared against is a
        /// perceptual constant, which is why it is here and not in a dictionary — a theme supplies
        /// colours, and how light a colour is follows from the colour.
        /// </remarks>
        public static double Luminance(Color colour) =>
            (0.299 * colour.R) + (0.587 * colour.G) + (0.114 * colour.B);

        private static void Set(IntPtr handle, int attribute, Color colour)
        {
            // COLORREF is 0x00BBGGRR, which is the reverse of the order the bytes are named in.
            int value = colour.R | (colour.G << 8) | (colour.B << 16);

            DwmSetWindowAttribute(handle, attribute, ref value, sizeof(int));
        }

        private static Color? ColourOf(Window window, string key)
        {
            var brush = window.TryFindResource(key) as SolidColorBrush;

            return brush == null ? (Color?)null : brush.Color;
        }
    }
}
