using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using OpenVSA.Measurement.State;

namespace OpenVSA.Ui.Rendering
{
    /// <summary>
    /// One colourable thing as the colour picker lists it (<c>REQ-UI-014</c>).
    /// </summary>
    public sealed class ColourEntry
    {
        internal ColourEntry(ThemeElement element, int traceIndex, string key, PlotColor fallback)
        {
            Element = element;
            TraceIndex = traceIndex;
            Key = key;
            Default = fallback;
        }

        /// <summary>The <c>REQ-UI-022</c> element this entry colours.</summary>
        public ThemeElement Element { get; }

        /// <summary>The trace it applies to, or −1 for a global element.</summary>
        public int TraceIndex { get; }

        /// <summary>The resource key, from the element itself.</summary>
        public string Key { get; }

        /// <summary>The colour when nothing has been changed.</summary>
        public PlotColor Default { get; }

        /// <summary>How the picker names this entry.</summary>
        public string DisplayName =>
            TraceIndex < 0
                ? Element.Name
                : Element.Name + " " + TraceColours.LetterAt(TraceIndex);

        /// <inheritdoc />
        public override string ToString() => DisplayName;
    }

    /// <summary>
    /// The colours a user has changed, and the picker's list of what can be changed
    /// (<c>REQ-UI-014</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The list is generated from the element set, never written out.</strong> The
    /// requirement's criterion is that every element of <c>REQ-UI-022</c> is reachable from the
    /// picker, "so an element added without a picker entry fails". A hand-written picker list is
    /// exactly the thing that would drift: someone adds <em>Slot Pilot Uplink</em> to the element
    /// set, nobody adds it to the dialog, and the element is themeable in name only. Enumerating
    /// <see cref="ThemeElements"/> makes the two impossible to separate.
    /// </para>
    /// <para>
    /// <strong>Only changes are stored.</strong> An element left alone has no entry in the file at
    /// all, so it follows whatever the default theme says — including after the default changes.
    /// Writing all several hundred colours out would freeze today's defaults into every user's
    /// preferences file the first time they changed one colour.
    /// </para>
    /// <para>
    /// <strong>The file is the display sidecar, not the state.</strong> <c>REQ-STA-002</c> keeps
    /// display preferences out of a saved setup, so recalling a colleague's measurement does not
    /// repaint your display.
    /// </para>
    /// </remarks>
    public sealed class ColourPreferences
    {
        /// <summary>Traces the picker offers per-trace colours for, unless told otherwise.</summary>
        /// <remarks>
        /// The trace-colour table's size (<c>REQ-UI-021</c>): beyond it the letters keep going but
        /// the colours repeat, so a picker entry past the table would offer to change a colour that
        /// another trace also uses.
        /// </remarks>
        public const int DefaultTraceCount = TraceColours.TableSize;

        private readonly Dictionary<string, PlotColor> _changed =
            new Dictionary<string, PlotColor>(StringComparer.Ordinal);

        private readonly ReadOnlyCollection<ColourEntry> _entries;
        private readonly Dictionary<string, ColourEntry> _byKey;

        /// <summary>Creates a set covering <see cref="DefaultTraceCount"/> traces.</summary>
        public ColourPreferences()
            : this(DefaultTraceCount)
        {
        }

        /// <summary>Creates a set covering a given number of traces.</summary>
        /// <param name="traceCount">How many traces get per-trace entries; at least one.</param>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="traceCount"/> is less than one.</exception>
        public ColourPreferences(int traceCount)
        {
            if (traceCount < 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(traceCount), traceCount, "A picker covers at least one trace.");
            }

            TraceCount = traceCount;

            var entries = new List<ColourEntry>();
            _byKey = new Dictionary<string, ColourEntry>(StringComparer.Ordinal);

            foreach (ThemeElement element in ThemeElements.All)
            {
                if (element.Scope == ThemeScope.Global)
                {
                    Record(entries, new ColourEntry(
                        element, -1, element.Key, DefaultFor(element, -1)));
                    continue;
                }

                for (int trace = 0; trace < traceCount; trace++)
                {
                    Record(entries, new ColourEntry(
                        element, trace, element.KeyForTrace(trace), DefaultFor(element, trace)));
                }
            }

            _entries = new ReadOnlyCollection<ColourEntry>(entries);
        }

        /// <summary>How many traces this set covers.</summary>
        public int TraceCount { get; }

        /// <summary>Everything the picker lists, global first then per trace.</summary>
        public IReadOnlyList<ColourEntry> Entries => _entries;

        /// <summary>How many colours differ from their defaults.</summary>
        public int ChangedCount => _changed.Count;

        /// <summary>The entry for a resource key, or <c>null</c> if there is none.</summary>
        /// <param name="key">The resource key.</param>
        public ColourEntry Find(string key)
        {
            if (key == null)
            {
                return null;
            }

            ColourEntry entry;
            return _byKey.TryGetValue(key, out entry) ? entry : null;
        }

        /// <summary>
        /// The colour in force for a key: what the user chose, or the default.
        /// </summary>
        /// <param name="key">The resource key.</param>
        /// <exception cref="ArgumentException">The key is not a themeable element.</exception>
        /// <remarks>
        /// An unknown key throws rather than returning a fallback brush, for the reason
        /// <c>REQ-UI-022</c> gives: a misspelled key that silently paints grey is a bug nobody
        /// notices until a customer photographs the screen.
        /// </remarks>
        public PlotColor Colour(string key)
        {
            ColourEntry entry = Find(key);

            if (entry == null)
            {
                throw new ArgumentException(
                    "There is no themeable element with the key '" + key + "'.", nameof(key));
            }

            PlotColor chosen;
            return _changed.TryGetValue(key, out chosen) ? chosen : entry.Default;
        }

        /// <summary>The colour in force for a global element, by its display name.</summary>
        /// <param name="name">The element's name, as <c>REQ-UI-022</c> writes it.</param>
        /// <exception cref="ArgumentException">There is no such global element.</exception>
        public PlotColor ColourOf(string name)
        {
            ThemeElement element = ThemeElements.ByName(name);

            if (element == null || element.Scope != ThemeScope.Global)
            {
                throw new ArgumentException(
                    "There is no global themeable element named '" + name + "'.", nameof(name));
            }

            return Colour(element.Key);
        }

        /// <summary>Whether a key has been changed from its default.</summary>
        /// <param name="key">The resource key.</param>
        public bool IsChanged(string key) => key != null && _changed.ContainsKey(key);

        /// <summary>
        /// Changes a colour.
        /// </summary>
        /// <param name="key">The resource key.</param>
        /// <param name="colour">The new colour.</param>
        /// <exception cref="ArgumentException">The key is not a themeable element.</exception>
        /// <remarks>
        /// Setting a colour back to its default drops the change rather than recording it, so a
        /// user who opens the picker and changes their mind leaves no trace in the file — and so
        /// <see cref="ChangedCount"/> answers "how much have I altered" truthfully.
        /// </remarks>
        public void Set(string key, PlotColor colour)
        {
            ColourEntry entry = Find(key);

            if (entry == null)
            {
                throw new ArgumentException(
                    "There is no themeable element with the key '" + key + "'.", nameof(key));
            }

            if (colour.Equals(entry.Default))
            {
                _changed.Remove(key);
                return;
            }

            _changed[key] = colour;
        }

        /// <summary>Puts one colour back to its default.</summary>
        /// <param name="key">The resource key.</param>
        /// <returns>Whether it had been changed.</returns>
        public bool Reset(string key) => key != null && _changed.Remove(key);

        /// <summary>Puts every colour back to its default.</summary>
        public void ResetAll() => _changed.Clear();

        /// <summary>
        /// Writes the changed colours into a display-preferences sidecar.
        /// </summary>
        /// <param name="state">The sidecar to write into.</param>
        /// <exception cref="ArgumentNullException"><paramref name="state"/> is null.</exception>
        /// <remarks>
        /// Sorted by key, so a preferences file does not churn its line order between saves and a
        /// diff of two files shows only what actually differs.
        /// </remarks>
        public void SaveInto(DisplayPreferencesState state)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            var keys = new List<string>(_changed.Keys);
            keys.Sort(StringComparer.Ordinal);

            var colours = new List<ElementColourState>(keys.Count);

            foreach (string key in keys)
            {
                colours.Add(new ElementColourState { Element = key, Argb = Pack(_changed[key]) });
            }

            state.Colours = colours;
        }

        /// <summary>
        /// Reads the changed colours back from a display-preferences sidecar.
        /// </summary>
        /// <param name="state">The sidecar to read.</param>
        /// <returns>Keys in the file that are not themeable elements, in the order found.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="state"/> is null.</exception>
        /// <remarks>
        /// Unknown keys are returned rather than thrown on. A preferences file written by a later
        /// version, or by a build with more traces, will name elements this one has never heard of;
        /// discarding the rest of the user's colours over one of them would be the worse failure.
        /// The caller can report them.
        /// </remarks>
        public IReadOnlyList<string> LoadFrom(DisplayPreferencesState state)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            _changed.Clear();
            var unknown = new List<string>();

            if (state.Colours == null)
            {
                return unknown;
            }

            foreach (ElementColourState colour in state.Colours)
            {
                if (colour == null || colour.Element == null)
                {
                    continue;
                }

                if (Find(colour.Element) == null)
                {
                    unknown.Add(colour.Element);
                    continue;
                }

                Set(colour.Element, Unpack(colour.Argb));
            }

            return unknown;
        }

        /// <summary>Packs a colour as opaque ARGB, as the sidecar stores it.</summary>
        /// <param name="colour">The colour.</param>
        public static uint Pack(PlotColor colour) =>
            0xFF000000u |
            ((uint)colour.R << 16) |
            ((uint)colour.G << 8) |
            colour.B;

        /// <summary>Unpacks a colour from ARGB, discarding the alpha.</summary>
        /// <param name="argb">The packed value.</param>
        public static PlotColor Unpack(uint argb) =>
            new PlotColor((byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);

        private void Record(List<ColourEntry> entries, ColourEntry entry)
        {
            entries.Add(entry);
            _byKey[entry.Key] = entry;
        }

        /// <summary>
        /// The default colour for an element.
        /// </summary>
        /// <remarks>
        /// The elements something already draws take that thing's colour, so the picker opens
        /// showing what is on screen rather than a guess. The rest — the emitter, group and slot
        /// families, which nothing draws yet — are generated on a golden-angle hue sweep, which
        /// gives 48 adjacent entries that are all distinguishable from their neighbours. The
        /// alternative, one placeholder grey for everything unimplemented, would make the picker's
        /// own list unreadable and would hide the day a real default went missing.
        /// </remarks>
        private static PlotColor DefaultFor(ThemeElement element, int traceIndex)
        {
            var limits = new LimitColours();
            PlotPalette palette = PlotPalette.Dark;

            switch (element.Name)
            {
                case "Trace Background": return palette.TraceBackground;
                case "Grid": return palette.Grid;
                case "Annotation": return palette.Annotation;
                case "Annotation Background": return palette.AnnotationBackground;
                case "Indicator": return palette.Indicator;
                case "Selected Marker": return palette.SelectedMarker;
                case "Not Selected Marker": return palette.NotSelectedMarker;
                case "Marker Window Background": return palette.AnnotationBackground;

                case "Limit": return limits.Limit;
                case "Fail Limit": return limits.FailLimit;
                case "Margin": return limits.Margin;
                case "Fail Margin": return limits.FailMargin;

                case "Trace":
                case "Trace Select":
                    return TraceColours.ForIndex(Math.Max(0, traceIndex));
            }

            return Generated(element.Name, traceIndex);
        }

        /// <summary>
        /// A distinguishable colour derived from an element's name and trace.
        /// </summary>
        /// <remarks>
        /// The golden angle, 137.508°, is the hue step that keeps successive entries as far apart
        /// as possible however many there turn out to be — the reason it is used is that the counts
        /// here (8 modulation types, 32 emitters, 48 groups) are all provisional, and a fixed
        /// <c>360/n</c> step would need revisiting every time one of them changed.
        /// </remarks>
        private static PlotColor Generated(string name, int traceIndex)
        {
            int index = 0;

            foreach (char letter in name)
            {
                index = index * 31 + letter;
            }

            index = Math.Abs(index) + Math.Max(0, traceIndex);

            double hue = index * 137.508 % 360.0;
            return FromHue(hue);
        }

        private static PlotColor FromHue(double degrees)
        {
            // Value 0.85 rather than 1.0, so a generated colour never reads as a pure primary and
            // cannot be mistaken for one of the fixed defaults above.
            const double Value = 0.85 * 255.0;

            double sector = degrees / 60.0;
            double fall = 1.0 - Math.Abs(sector % 2.0 - 1.0);

            double r, g, b;

            if (sector < 1.0) { r = 1.0; g = fall; b = 0.0; }
            else if (sector < 2.0) { r = fall; g = 1.0; b = 0.0; }
            else if (sector < 3.0) { r = 0.0; g = 1.0; b = fall; }
            else if (sector < 4.0) { r = 0.0; g = fall; b = 1.0; }
            else if (sector < 5.0) { r = fall; g = 0.0; b = 1.0; }
            else { r = 1.0; g = 0.0; b = fall; }

            return new PlotColor(
                (byte)Math.Round(r * Value),
                (byte)Math.Round(g * Value),
                (byte)Math.Round(b * Value));
        }

        /// <inheritdoc />
        public override string ToString() =>
            _entries.Count.ToString(CultureInfo.CurrentCulture) + " colours, " +
            _changed.Count.ToString(CultureInfo.CurrentCulture) + " changed";
    }
}
