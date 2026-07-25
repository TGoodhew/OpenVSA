using System.Text;
using OpenVSA.Dsp.Windowing;

namespace OpenVSA.Ui
{
    /// <summary>
    /// Names the window functions as <c>REQ-DSP-010</c> tabulates them.
    /// </summary>
    /// <remarks>
    /// The enum spells them <c>FlatTop</c> and <c>BlackmanHarris</c>; the specification, the
    /// literature and the reference product all spell them "Flat Top" and "Blackman-Harris". A user
    /// looking for one in a list will look for the spelling they know, so the split happens here
    /// rather than being duplicated in every place a window is named.
    /// </remarks>
    public static class WindowText
    {
        /// <summary>The display name of a window.</summary>
        /// <param name="type">The window.</param>
        public static string Describe(WindowType type)
        {
            if (type == WindowType.BlackmanHarris)
            {
                return "Blackman-Harris";
            }

            if (type == WindowType.KaiserBessel)
            {
                return "Kaiser-Bessel";
            }

            string name = type.ToString();
            var spaced = new StringBuilder(name.Length + 4);

            for (int i = 0; i < name.Length; i++)
            {
                if (i > 0 && char.IsUpper(name[i]))
                {
                    spaced.Append(' ');
                }

                spaced.Append(name[i]);
            }

            return spaced.ToString();
        }
    }
}
