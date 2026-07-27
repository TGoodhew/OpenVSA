using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using OpenVSA.Measurement.State;

namespace OpenVSA.Ui.Rendering
{
    /// <summary>
    /// The three font slots of <c>REQ-UI-080</c>.
    /// </summary>
    /// <remarks>
    /// The reference product has two — Annotation and Marker. The third exists because the symbol
    /// table and the error summary of <c>REQ-UI-052</c> need a fixed-width face and general trace
    /// annotation reads better proportional, and two slots force one of those to be wrong.
    /// </remarks>
    public enum FontSlot
    {
        /// <summary>Trace-window annotation. May be proportional.</summary>
        Annotation = 0,

        /// <summary>The Markers window (<c>REQ-UI-033</c>). Fixed-width by default.</summary>
        Marker,

        /// <summary>Symbol table and error summary (<c>REQ-UI-052</c>). Fixed-width by default.</summary>
        Tabular,
    }

    /// <summary>
    /// A typeface and a size, as one font slot is set (<c>REQ-UI-080</c>).
    /// </summary>
    /// <remarks>
    /// The family is the name the user asked for, not the one that was found. Storing the resolved
    /// family would mean a preferences file written on a machine without Consolas permanently
    /// recorded Courier New, and the setting would not come back when the file was opened on a
    /// machine that has it.
    /// </remarks>
    public sealed class FontChoice : IEquatable<FontChoice>
    {
        /// <summary>Smallest size a slot may be set to, in points.</summary>
        public const double MinimumPoints = 6.0;

        /// <summary>Largest size a slot may be set to, in points.</summary>
        public const double MaximumPoints = 48.0;

        /// <summary>Creates a choice.</summary>
        /// <param name="family">The typeface asked for.</param>
        /// <param name="sizePoints">The size in points.</param>
        /// <exception cref="ArgumentException"><paramref name="family"/> is null or blank.</exception>
        /// <exception cref="ArgumentOutOfRangeException">The size is outside the settable range.</exception>
        public FontChoice(string family, double sizePoints)
        {
            if (string.IsNullOrEmpty(family) || family.Trim().Length == 0)
            {
                throw new ArgumentException("A font slot needs a typeface.", nameof(family));
            }

            if (double.IsNaN(sizePoints) ||
                sizePoints < MinimumPoints ||
                sizePoints > MaximumPoints)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(sizePoints),
                    sizePoints,
                    "A font size is between " + MinimumPoints + " and " + MaximumPoints + " points.");
            }

            Family = family.Trim();
            SizePoints = sizePoints;
        }

        /// <summary>The typeface asked for.</summary>
        public string Family { get; }

        /// <summary>The size, in points.</summary>
        public double SizePoints { get; }

        /// <summary>
        /// The size in device-independent pixels, which is what WPF's <c>FontSize</c> takes.
        /// </summary>
        /// <remarks>
        /// A WPF device-independent pixel is 1/96 inch and a point is 1/72, so the ratio is 4/3.
        /// The slot is stored in points because that is the unit the requirement states the
        /// recommended defaults in, and the unit a user comparing with another application will
        /// have in mind.
        /// </remarks>
        public double SizeDip => SizePoints * 96.0 / 72.0;

        /// <inheritdoc />
        public bool Equals(FontChoice other) =>
            other != null &&
            string.Equals(Family, other.Family, StringComparison.OrdinalIgnoreCase) &&
            SizePoints.Equals(other.SizePoints);

        /// <inheritdoc />
        public override bool Equals(object obj) => Equals(obj as FontChoice);

        /// <inheritdoc />
        public override int GetHashCode() =>
            StringComparer.OrdinalIgnoreCase.GetHashCode(Family) ^ SizePoints.GetHashCode();

        /// <inheritdoc />
        public override string ToString() =>
            Family + " " + SizePoints.ToString("0.#", CultureInfo.CurrentCulture) + " pt";
    }

    /// <summary>
    /// The three font slots, what they are set to, and how they resolve on this machine
    /// (<c>REQ-UI-080</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Three slots, applied globally, each to its own surfaces.</strong> Setting one leaves
    /// the other two alone — which sounds too obvious to test until you notice that the natural
    /// implementation, one font for "the application", is exactly what the reference product's
    /// two-slot scheme is a partial escape from.
    /// </para>
    /// <para>
    /// <strong>Only changes are stored</strong>, as with the colours of <c>REQ-UI-014</c>: a slot
    /// left alone follows the default, including after the default changes.
    /// </para>
    /// <para>
    /// <strong>The default is what the requirement recommends, and the fallback is documented
    /// rather than silent.</strong> Segoe UI ships with every supported Windows and Consolas with
    /// every one since Vista, so both are all but certain to be present — but "all but" is why
    /// <see cref="Resolve"/> walks a list, and why <see cref="Fallbacks"/> is public: a Marker slot
    /// that quietly resolved to a proportional face would break the column alignment
    /// <c>REQ-UI-033</c> exists for, and nobody would know why.
    /// </para>
    /// </remarks>
    public sealed class FontPreferences
    {
        private static readonly ReadOnlyCollection<FontSlot> AllSlots =
            new ReadOnlyCollection<FontSlot>(new List<FontSlot>
            {
                FontSlot.Annotation,
                FontSlot.Marker,
                FontSlot.Tabular,
            });

        private static readonly ReadOnlyCollection<string> ProportionalFallbacks =
            new ReadOnlyCollection<string>(new List<string>
            {
                "Segoe UI", "Tahoma", "Arial", "Global User Interface",
            });

        private static readonly ReadOnlyCollection<string> FixedPitchFallbacks =
            new ReadOnlyCollection<string>(new List<string>
            {
                "Consolas", "Courier New", "Lucida Console", "Global Monospace",
            });

        private static HashSet<string> _installed;

        private readonly Dictionary<FontSlot, FontChoice> _changed =
            new Dictionary<FontSlot, FontChoice>();

        /// <summary>Every slot, in the order <c>REQ-UI-080</c> introduces them.</summary>
        public static IReadOnlyList<FontSlot> Slots => AllSlots;

        /// <summary>How many slots differ from their defaults.</summary>
        public int ChangedCount => _changed.Count;

        /// <summary>Raised whenever a slot changes, so the surfaces can follow immediately.</summary>
        public event EventHandler Changed;

        /// <summary>
        /// The default for a slot: the requirement's recommendation.
        /// </summary>
        /// <param name="slot">The slot.</param>
        /// <exception cref="ArgumentOutOfRangeException">There is no such slot.</exception>
        public static FontChoice DefaultFor(FontSlot slot)
        {
            switch (slot)
            {
                case FontSlot.Annotation: return new FontChoice("Segoe UI", 9.0);
                case FontSlot.Marker: return new FontChoice("Consolas", 9.0);
                case FontSlot.Tabular: return new FontChoice("Consolas", 9.0);
            }

            throw new ArgumentOutOfRangeException(
                nameof(slot), slot, "There are three font slots and this is not one of them.");
        }

        /// <summary>Whether a slot is required to resolve to a fixed-width face.</summary>
        /// <param name="slot">The slot.</param>
        /// <remarks>
        /// Marker and Tabular are; Annotation may be proportional. This is the reason the third slot
        /// exists, so it is stated once here rather than assumed at each use.
        /// </remarks>
        public static bool RequiresFixedPitch(FontSlot slot) =>
            slot == FontSlot.Marker || slot == FontSlot.Tabular;

        /// <summary>The families tried, in order, when a slot's own family is unavailable.</summary>
        /// <param name="slot">The slot.</param>
        public static IReadOnlyList<string> Fallbacks(FontSlot slot) =>
            RequiresFixedPitch(slot) ? FixedPitchFallbacks : ProportionalFallbacks;

        /// <summary>The name a slot is given in the Font tab and in the preferences file.</summary>
        /// <param name="slot">The slot.</param>
        /// <exception cref="ArgumentOutOfRangeException">There is no such slot.</exception>
        public static string NameOf(FontSlot slot)
        {
            switch (slot)
            {
                case FontSlot.Annotation: return "Annotation";
                case FontSlot.Marker: return "Marker";
                case FontSlot.Tabular: return "Tabular";
            }

            throw new ArgumentOutOfRangeException(
                nameof(slot), slot, "There are three font slots and this is not one of them.");
        }

        /// <summary>Reads a slot back from its name.</summary>
        /// <param name="name">The name.</param>
        /// <param name="slot">The slot, if the name is one.</param>
        /// <returns>Whether the name was understood.</returns>
        public static bool TryParseName(string name, out FontSlot slot)
        {
            slot = FontSlot.Annotation;

            if (string.IsNullOrEmpty(name))
            {
                return false;
            }

            string trimmed = name.Trim();

            foreach (FontSlot candidate in AllSlots)
            {
                if (string.Equals(NameOf(candidate), trimmed, StringComparison.OrdinalIgnoreCase))
                {
                    slot = candidate;
                    return true;
                }
            }

            return false;
        }

        /// <summary>What a slot is set to: the user's choice, or the default.</summary>
        /// <param name="slot">The slot.</param>
        /// <exception cref="ArgumentOutOfRangeException">There is no such slot.</exception>
        public FontChoice Choice(FontSlot slot)
        {
            FontChoice chosen;

            return _changed.TryGetValue(slot, out chosen) ? chosen : DefaultFor(slot);
        }

        /// <summary>What a slot is set to.</summary>
        /// <param name="slot">The slot.</param>
        public FontChoice this[FontSlot slot] => Choice(slot);

        /// <summary>Whether a slot differs from its default.</summary>
        /// <param name="slot">The slot.</param>
        public bool IsChanged(FontSlot slot) => _changed.ContainsKey(slot);

        /// <summary>
        /// Sets one slot.
        /// </summary>
        /// <param name="slot">The slot.</param>
        /// <param name="choice">The typeface and size.</param>
        /// <exception cref="ArgumentNullException"><paramref name="choice"/> is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException">There is no such slot.</exception>
        /// <remarks>
        /// Setting a slot back to its default drops the change rather than recording it, so
        /// <see cref="ChangedCount"/> answers "how much have I altered" truthfully and a user who
        /// changes their mind leaves nothing in the file.
        /// </remarks>
        public void Set(FontSlot slot, FontChoice choice)
        {
            if (choice == null)
            {
                throw new ArgumentNullException(nameof(choice));
            }

            FontChoice was = Choice(slot);

            if (choice.Equals(DefaultFor(slot)))
            {
                _changed.Remove(slot);
            }
            else
            {
                _changed[slot] = choice;
            }

            if (!choice.Equals(was))
            {
                RaiseChanged();
            }
        }

        /// <summary>Puts one slot back to its default.</summary>
        /// <param name="slot">The slot.</param>
        /// <returns>Whether it had been changed.</returns>
        public bool Reset(FontSlot slot)
        {
            if (!_changed.Remove(slot))
            {
                return false;
            }

            RaiseChanged();
            return true;
        }

        /// <summary>Puts every slot back to its default.</summary>
        public void ResetAll()
        {
            if (_changed.Count == 0)
            {
                return;
            }

            _changed.Clear();
            RaiseChanged();
        }

        /// <summary>
        /// The family a slot actually draws with on this machine.
        /// </summary>
        /// <param name="slot">The slot.</param>
        /// <returns>The chosen family if it is installed, else the first installed fallback.</returns>
        /// <remarks>
        /// The last entry of each fallback list is one of WPF's own composite families — <em>Global
        /// Monospace</em>, <em>Global User Interface</em> — which resolve on every machine, so this
        /// always has an answer.
        /// </remarks>
        public string ResolveFamily(FontSlot slot)
        {
            string asked = Choice(slot).Family;

            if (IsInstalled(asked))
            {
                return asked;
            }

            IReadOnlyList<string> fallbacks = Fallbacks(slot);

            foreach (string candidate in fallbacks)
            {
                if (IsInstalled(candidate))
                {
                    return candidate;
                }
            }

            return fallbacks[fallbacks.Count - 1];
        }

        /// <summary>The typeface and size a slot draws with, ready to apply to an element.</summary>
        /// <param name="slot">The slot.</param>
        public FontFamily Resolve(FontSlot slot) => new FontFamily(ResolveFamily(slot));

        /// <summary>Applies a slot to a control.</summary>
        /// <param name="slot">The slot.</param>
        /// <param name="element">The control to set the family and size on.</param>
        /// <exception cref="ArgumentNullException"><paramref name="element"/> is null.</exception>
        public void ApplyTo(FontSlot slot, System.Windows.Controls.Control element)
        {
            if (element == null)
            {
                throw new ArgumentNullException(nameof(element));
            }

            element.FontFamily = Resolve(slot);
            element.FontSize = Choice(slot).SizeDip;
        }

        /// <summary>Applies a slot to a run of text.</summary>
        /// <param name="slot">The slot.</param>
        /// <param name="element">The text block to set the family and size on.</param>
        /// <exception cref="ArgumentNullException"><paramref name="element"/> is null.</exception>
        /// <remarks>
        /// A second overload rather than one taking <see cref="FrameworkElement"/>, because
        /// <c>FontFamily</c> is declared separately on <c>Control</c> and on <c>TextBlock</c> and
        /// there is no common base that carries it — and the trace annotation this has to reach is
        /// text blocks.
        /// </remarks>
        public void ApplyTo(FontSlot slot, System.Windows.Controls.TextBlock element)
        {
            if (element == null)
            {
                throw new ArgumentNullException(nameof(element));
            }

            element.FontFamily = Resolve(slot);
            element.FontSize = Choice(slot).SizeDip;
        }

        /// <summary>Whether a family is installed on this machine.</summary>
        /// <param name="family">The family name.</param>
        public static bool IsInstalled(string family)
        {
            if (string.IsNullOrEmpty(family))
            {
                return false;
            }

            return Installed().Contains(family.Trim());
        }

        /// <summary>
        /// Whether a family draws every character at the same advance width.
        /// </summary>
        /// <param name="family">The family name.</param>
        /// <returns><c>false</c> if it is proportional, or if its glyphs cannot be measured.</returns>
        /// <remarks>
        /// <para>
        /// Measured from the glyphs, not read off a list of names anyone would have to maintain.
        /// The criterion of <c>REQ-UI-080</c> is that Marker and Tabular "resolve to fixed-width
        /// typefaces by default, asserted on the resolved face's pitch" — and a name list would
        /// assert that someone had spelled Consolas correctly, which is not the same claim.
        /// </para>
        /// <para>
        /// The probe characters are the extremes a proportional face separates most: an <c>i</c>, a
        /// full stop, an <c>m</c> and a <c>W</c>. A face that draws those four the same width draws
        /// a column of numbers straight.
        /// </para>
        /// </remarks>
        public static bool IsFixedPitch(string family)
        {
            if (string.IsNullOrEmpty(family))
            {
                return false;
            }

            GlyphTypeface glyphs;

            try
            {
                var typeface = new Typeface(
                    new FontFamily(family),
                    FontStyles.Normal,
                    FontWeights.Normal,
                    FontStretches.Normal);

                if (!typeface.TryGetGlyphTypeface(out glyphs))
                {
                    return false;
                }
            }
            catch (ArgumentException)
            {
                // A family name that is not a family at all. Reported as "not fixed pitch" rather
                // than thrown, because the caller's question is whether it is safe to draw a table
                // with, and the answer to that is no either way.
                return false;
            }

            double? width = null;

            foreach (char probe in new[] { 'i', '.', 'm', 'W' })
            {
                ushort index;

                if (!glyphs.CharacterToGlyphMap.TryGetValue(probe, out index))
                {
                    return false;
                }

                double advance = glyphs.AdvanceWidths[index];

                if (width == null)
                {
                    width = advance;
                }
                else if (Math.Abs(advance - width.Value) > 1e-6)
                {
                    return false;
                }
            }

            return width != null;
        }

        /// <summary>
        /// Writes the changed slots into a display-preferences sidecar.
        /// </summary>
        /// <param name="state">The sidecar to write into.</param>
        /// <exception cref="ArgumentNullException"><paramref name="state"/> is null.</exception>
        public void SaveInto(DisplayPreferencesState state)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            var fonts = new List<FontSlotState>(_changed.Count);

            // In slot order rather than dictionary order, so the file does not churn between saves.
            foreach (FontSlot slot in AllSlots)
            {
                FontChoice chosen;

                if (_changed.TryGetValue(slot, out chosen))
                {
                    fonts.Add(new FontSlotState
                    {
                        Slot = NameOf(slot),
                        Family = chosen.Family,
                        SizePoints = chosen.SizePoints,
                    });
                }
            }

            state.Fonts = fonts;

            // REQ-UI-033's Markers-window family has its own field from before the slots existed.
            // Written from the Marker slot so the two cannot disagree about what the Markers window
            // is set to.
            state.MarkerFontFamily = Choice(FontSlot.Marker).Family;
            state.AnnotationFontSize = Choice(FontSlot.Annotation).SizePoints;
        }

        /// <summary>
        /// Reads the changed slots back from a display-preferences sidecar.
        /// </summary>
        /// <param name="state">The sidecar to read.</param>
        /// <returns>Slot names in the file that this build does not have, in the order found.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="state"/> is null.</exception>
        /// <remarks>
        /// An unreadable entry costs the user that slot, not the file: a size outside the settable
        /// range, or a slot name from a later version, is reported and the rest are applied.
        /// </remarks>
        public IReadOnlyList<string> LoadFrom(DisplayPreferencesState state)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            _changed.Clear();
            var unknown = new List<string>();

            if (state.Fonts == null)
            {
                return unknown;
            }

            foreach (FontSlotState saved in state.Fonts)
            {
                FontSlot slot;

                if (saved == null || !TryParseName(saved.Slot, out slot))
                {
                    unknown.Add(saved == null ? string.Empty : saved.Slot ?? string.Empty);
                    continue;
                }

                try
                {
                    Set(slot, new FontChoice(saved.Family, saved.SizePoints));
                }
                catch (ArgumentException)
                {
                    unknown.Add(NameOf(slot));
                }
            }

            return unknown;
        }

        private static HashSet<string> Installed()
        {
            HashSet<string> known = _installed;

            if (known != null)
            {
                return known;
            }

            known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (FontFamily family in System.Windows.Media.Fonts.SystemFontFamilies)
            {
                known.Add(family.Source);

                foreach (string name in family.FamilyNames.Values)
                {
                    known.Add(name);
                }
            }

            // The composite families WPF resolves itself; they are not in the system list but they
            // always draw, which is what makes them usable as the end of a fallback chain.
            known.Add("Global Monospace");
            known.Add("Global User Interface");
            known.Add("Global Serif");
            known.Add("Global Sans Serif");

            _installed = known;
            return known;
        }

        private void RaiseChanged()
        {
            EventHandler handler = Changed;

            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }
        }

        /// <inheritdoc />
        public override string ToString() =>
            "Annotation " + Choice(FontSlot.Annotation) +
            ", Marker " + Choice(FontSlot.Marker) +
            ", Tabular " + Choice(FontSlot.Tabular);
    }
}
