using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace OpenVSA.Ui.Rendering
{
    /// <summary>
    /// The spectrogram colour maps of <c>REQ-UI-024</c>.
    /// </summary>
    /// <remarks>
    /// The requirement calls this "the one hard colour statement in the entire reference
    /// documentation, and therefore to be implemented exactly". The names are the reference
    /// product's own spellings, American <c>Color</c> and all, because they are what a user of it
    /// will look for.
    /// </remarks>
    public enum SpectrogramColourMapKind
    {
        /// <summary>64 colours, maximum red and minimum blue. The default.</summary>
        ColorNormal = 0,

        /// <summary>As <see cref="ColorNormal"/>, reversed.</summary>
        ColorReverse,

        /// <summary>64 greys, maximum lightest and minimum darkest.</summary>
        GreyNormal,

        /// <summary>As <see cref="GreyNormal"/>, reversed.</summary>
        GreyReverse,

        /// <summary>A map the user supplied.</summary>
        UserDefined,
    }

    /// <summary>
    /// A spectrogram's value-to-colour map (<c>REQ-UI-024</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Index 0 is the bottom — the minimum.</strong> The requirement states it of the user
    /// map and it holds for all of them, so a map is always read the same way whoever made it. The
    /// built-in maps are generated to that convention rather than written out reversed.
    /// </para>
    /// <para>
    /// <strong>Reducing the count discards from the top.</strong> Also stated, and it is the
    /// surprising direction: a user shortening their map loses the <em>highest</em> colours, so
    /// what was the minimum stays the minimum and the map does not slide underneath the data.
    /// </para>
    /// </remarks>
    public sealed class SpectrogramColourMap
    {
        /// <summary>Entries in each of the built-in maps (<c>REQ-UI-024</c>).</summary>
        public const int StandardEntryCount = 64;

        private readonly ReadOnlyCollection<PlotColor> _entries;

        private SpectrogramColourMap(SpectrogramColourMapKind kind, IList<PlotColor> entries)
        {
            Kind = kind;
            _entries = new ReadOnlyCollection<PlotColor>(entries);
        }

        /// <summary>Which map this is.</summary>
        public SpectrogramColourMapKind Kind { get; }

        /// <summary>The entries, index 0 the minimum and the last the maximum.</summary>
        public IReadOnlyList<PlotColor> Entries => _entries;

        /// <summary>How many entries the map has.</summary>
        public int Count => _entries.Count;

        /// <summary>The colour of the lowest-valued cell.</summary>
        public PlotColor Minimum => _entries[0];

        /// <summary>The colour of the highest-valued cell.</summary>
        public PlotColor Maximum => _entries[_entries.Count - 1];

        /// <summary>The default map.</summary>
        public static SpectrogramColourMap Default => ColorNormal();

        /// <summary>64 colours, minimum blue through to maximum red.</summary>
        public static SpectrogramColourMap ColorNormal() =>
            new SpectrogramColourMap(SpectrogramColourMapKind.ColorNormal, Spectrum(false));

        /// <summary>64 colours, minimum red through to maximum blue.</summary>
        public static SpectrogramColourMap ColorReverse() =>
            new SpectrogramColourMap(SpectrogramColourMapKind.ColorReverse, Spectrum(true));

        /// <summary>64 greys, minimum darkest through to maximum lightest.</summary>
        public static SpectrogramColourMap GreyNormal() =>
            new SpectrogramColourMap(SpectrogramColourMapKind.GreyNormal, Greys(false));

        /// <summary>64 greys, minimum lightest through to maximum darkest.</summary>
        public static SpectrogramColourMap GreyReverse() =>
            new SpectrogramColourMap(SpectrogramColourMapKind.GreyReverse, Greys(true));

        /// <summary>
        /// A map the user supplied.
        /// </summary>
        /// <param name="colours">The entries, index 0 the minimum.</param>
        /// <exception cref="ArgumentNullException"><paramref name="colours"/> is null.</exception>
        /// <exception cref="ArgumentException">Fewer than two entries were given.</exception>
        public static SpectrogramColourMap User(IEnumerable<PlotColor> colours)
        {
            if (colours == null)
            {
                throw new ArgumentNullException(nameof(colours));
            }

            var entries = new List<PlotColor>(colours);

            if (entries.Count < 2)
            {
                throw new ArgumentException(
                    "A colour map needs at least a minimum and a maximum.", nameof(colours));
            }

            return new SpectrogramColourMap(SpectrogramColourMapKind.UserDefined, entries);
        }

        /// <summary>The built-in map of a kind.</summary>
        /// <param name="kind">Which map.</param>
        /// <exception cref="ArgumentOutOfRangeException">
        /// <paramref name="kind"/> is <see cref="SpectrogramColourMapKind.UserDefined"/>, which has
        /// no built-in form, or is not a known kind.
        /// </exception>
        public static SpectrogramColourMap Of(SpectrogramColourMapKind kind)
        {
            switch (kind)
            {
                case SpectrogramColourMapKind.ColorNormal: return ColorNormal();
                case SpectrogramColourMapKind.ColorReverse: return ColorReverse();
                case SpectrogramColourMapKind.GreyNormal: return GreyNormal();
                case SpectrogramColourMapKind.GreyReverse: return GreyReverse();

                case SpectrogramColourMapKind.UserDefined:
                    throw new ArgumentOutOfRangeException(
                        nameof(kind), kind,
                        "A user-defined map has no built-in form; supply its colours to User.");

                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(kind), kind, "Not a known colour map.");
            }
        }

        /// <summary>
        /// The colour for a value between the spectrogram's minimum and maximum.
        /// </summary>
        /// <param name="fraction">Where the value lies, 0 at the minimum and 1 at the maximum.</param>
        /// <returns>The entry for that value; the ends clamp.</returns>
        /// <remarks>
        /// Nearest entry, not interpolated between two. The map <em>is</em> the quantisation — a
        /// 64-entry map with interpolation would be a continuous ramp with 64 control points, which
        /// is a different thing and would make "exactly 64 entries" unobservable on screen.
        /// </remarks>
        public PlotColor At(double fraction)
        {
            if (double.IsNaN(fraction))
            {
                return _entries[0];
            }

            int index = (int)Math.Floor(fraction * _entries.Count);

            if (index < 0)
            {
                index = 0;
            }
            else if (index >= _entries.Count)
            {
                index = _entries.Count - 1;
            }

            return _entries[index];
        }

        /// <summary>
        /// A copy with fewer entries, discarding from the top (<c>REQ-UI-024</c>).
        /// </summary>
        /// <param name="count">Entries to keep; from 2 to <see cref="Count"/>.</param>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is out of range.</exception>
        /// <remarks>
        /// From the top, which is the surprising direction and the one the requirement states.
        /// Discarding from the bottom instead would move what the minimum renders as every time the
        /// count changed, and a spectrogram whose floor colour shifts under it is unreadable.
        /// </remarks>
        public SpectrogramColourMap WithCount(int count)
        {
            if (count < 2 || count > _entries.Count)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(count), count,
                    "A map keeps between 2 and " + _entries.Count + " of its entries.");
            }

            var kept = new List<PlotColor>(count);

            for (int i = 0; i < count; i++)
            {
                kept.Add(_entries[i]);
            }

            return new SpectrogramColourMap(Kind, kept);
        }

        /// <summary>
        /// Where the active selection sits on a sample-map preview, as fractions from the bottom.
        /// </summary>
        /// <param name="lowFraction">Bottom of the selection, 0 to 1.</param>
        /// <param name="highFraction">Top of the selection, 0 to 1.</param>
        /// <returns>The two marks, low first, each clamped to the map.</returns>
        /// <remarks>
        /// The "Sample Map" preview of <c>REQ-UI-024</c> shows the whole map with horizontal marks
        /// for the active selection. Returned as fractions rather than pixels so the preview can be
        /// any height, and ordered so a selection dragged upwards reads the same as one dragged
        /// down.
        /// </remarks>
        public static double[] SelectionMarks(double lowFraction, double highFraction)
        {
            double low = Math.Max(0.0, Math.Min(1.0, Math.Min(lowFraction, highFraction)));
            double high = Math.Max(0.0, Math.Min(1.0, Math.Max(lowFraction, highFraction)));

            return new[] { low, high };
        }

        /// <summary>
        /// The remark worth surfacing beside the grey maps.
        /// </summary>
        /// <remarks>
        /// The brochure's own observation, and a real perceptual point rather than marketing: the
        /// eye resolves more steps along a pure luminance ramp than along a hue ramp, so a grey
        /// spectrogram genuinely shows finer structure than a coloured one.
        /// </remarks>
        public const string GreyScaleTooltip =
            "Grey-scale views provide even greater resolution: the eye resolves more steps along a " +
            "luminance ramp than along a ramp of hues.";

        /// <summary>The display name of a map, as the reference product spells it.</summary>
        /// <param name="kind">Which map.</param>
        /// <exception cref="ArgumentOutOfRangeException">Not a known kind.</exception>
        public static string NameOf(SpectrogramColourMapKind kind)
        {
            switch (kind)
            {
                case SpectrogramColourMapKind.ColorNormal: return "Color Normal";
                case SpectrogramColourMapKind.ColorReverse: return "Color Reverse";
                case SpectrogramColourMapKind.GreyNormal: return "Grey Normal";
                case SpectrogramColourMapKind.GreyReverse: return "Grey Reverse";
                case SpectrogramColourMapKind.UserDefined: return "User Defined";

                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(kind), kind, "Not a known colour map.");
            }
        }

        /// <summary>
        /// The kind a display name refers to.
        /// </summary>
        /// <param name="name">A name as <see cref="NameOf"/> writes it.</param>
        /// <param name="kind">The kind, if the name is one.</param>
        /// <returns>Whether the name was recognised.</returns>
        /// <remarks>
        /// Compared exactly, spaces and American spelling and all. A preferences file naming a map
        /// that does not exist should fall back to the default visibly rather than be quietly
        /// mapped to whichever kind an approximate match landed on.
        /// </remarks>
        public static bool TryParseName(string name, out SpectrogramColourMapKind kind)
        {
            foreach (SpectrogramColourMapKind candidate in
                (SpectrogramColourMapKind[])Enum.GetValues(typeof(SpectrogramColourMapKind)))
            {
                if (string.Equals(NameOf(candidate), name, StringComparison.Ordinal))
                {
                    kind = candidate;
                    return true;
                }
            }

            kind = SpectrogramColourMapKind.ColorNormal;
            return false;
        }

        /// <summary>
        /// The blue-to-red spectrum, 64 entries.
        /// </summary>
        /// <remarks>
        /// A hue sweep from blue at the minimum through cyan, green and yellow to red at the
        /// maximum — the requirement fixes only the two ends, and the path between them is the one
        /// every spectrogram has used since they were drawn on paper. Full saturation and value
        /// throughout, so the map varies in hue alone and a cell's colour says only what its level
        /// is.
        /// </remarks>
        private static IList<PlotColor> Spectrum(bool reversed)
        {
            var entries = new List<PlotColor>(StandardEntryCount);

            for (int i = 0; i < StandardEntryCount; i++)
            {
                double position = i / (double)(StandardEntryCount - 1);
                double hue = (reversed ? position : 1.0 - position) * 240.0;

                entries.Add(FromHue(hue));
            }

            return entries;
        }

        private static IList<PlotColor> Greys(bool reversed)
        {
            var entries = new List<PlotColor>(StandardEntryCount);

            for (int i = 0; i < StandardEntryCount; i++)
            {
                double position = i / (double)(StandardEntryCount - 1);
                var level = (byte)Math.Round(255.0 * (reversed ? 1.0 - position : position));

                entries.Add(new PlotColor(level, level, level));
            }

            return entries;
        }

        /// <summary>A fully saturated colour at a hue in degrees, 0 red through 240 blue.</summary>
        private static PlotColor FromHue(double degrees)
        {
            double sector = degrees / 60.0;
            double fall = 1.0 - Math.Abs(sector % 2.0 - 1.0);

            double r;
            double g;
            double b;

            if (sector < 1.0)
            {
                r = 1.0; g = fall; b = 0.0;
            }
            else if (sector < 2.0)
            {
                r = fall; g = 1.0; b = 0.0;
            }
            else if (sector < 3.0)
            {
                r = 0.0; g = 1.0; b = fall;
            }
            else if (sector < 4.0)
            {
                r = 0.0; g = fall; b = 1.0;
            }
            else
            {
                r = fall; g = 0.0; b = 1.0;
            }

            return new PlotColor(
                (byte)Math.Round(r * 255.0),
                (byte)Math.Round(g * 255.0),
                (byte)Math.Round(b * 255.0));
        }
    }
}
