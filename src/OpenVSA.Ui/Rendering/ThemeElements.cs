using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;

namespace OpenVSA.Ui.Rendering
{
    /// <summary>
    /// Whether a themeable element is one colour for the whole display or one per trace
    /// (<c>REQ-UI-022</c>).
    /// </summary>
    public enum ThemeScope
    {
        /// <summary>One colour, applying across every trace.</summary>
        Global = 0,

        /// <summary>One colour per trace, indexed by the trace's own index.</summary>
        PerTrace,
    }

    /// <summary>
    /// The independently colourable elements of <c>REQ-UI-022</c>, and the resource keys they
    /// resolve to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The requirement's own list, verbatim.</strong> It is the reference product's actual
    /// enumeration rather than a set someone thought a spectrum analyser ought to have, which is
    /// why entries like <em>Slot Midamble</em> and <em>Mod Type N</em> appear here long before
    /// anything draws them. The criterion is that every named element resolves to a distinct key
    /// "so a missing or misspelled key fails rather than silently falling back to a default brush",
    /// and a test enumerates this list against the dictionary.
    /// </para>
    /// <para>
    /// <strong>Names are the display names; keys are derived from them.</strong> One list, so a
    /// name and its key cannot drift — the failure the criterion is really about is a key that
    /// exists under a slightly different spelling from the one the theme file uses, which resolves
    /// to nothing and paints the fallback brush.
    /// </para>
    /// <para>
    /// The <em>Emitter</em> and <em>Group</em> ranges are generated rather than written out: 32 and
    /// 48 entries typed by hand are 80 chances to skip a number, and the criterion tests the ends
    /// of both ranges precisely because that is where such a mistake lands.
    /// </para>
    /// </remarks>
    public sealed class ThemeElement
    {
        internal ThemeElement(string name, ThemeScope scope)
        {
            Name = name;
            Scope = scope;
        }

        /// <summary>The element's display name, as <c>REQ-UI-022</c> writes it.</summary>
        public string Name { get; }

        /// <summary>Whether it is global or per trace.</summary>
        public ThemeScope Scope { get; }

        /// <summary>
        /// The resource-dictionary key: the name with its spaces removed, prefixed <c>OpenVSA.</c>.
        /// </summary>
        /// <remarks>
        /// Derived rather than stored, so a name and its key are one thing. A stored key is a
        /// second spelling to keep in step, and the criterion exists because that is what goes
        /// wrong.
        /// </remarks>
        public string Key
        {
            get
            {
                var compact = new System.Text.StringBuilder("OpenVSA.");

                foreach (char letter in Name)
                {
                    if (letter != ' ' && letter != '/' && letter != '(' && letter != ')')
                    {
                        compact.Append(letter);
                    }
                }

                return compact.ToString();
            }
        }

        /// <summary>
        /// The key for a per-trace element on a given trace.
        /// </summary>
        /// <param name="traceIndex">The trace's index, from zero.</param>
        /// <exception cref="InvalidOperationException">This element is global.</exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="traceIndex"/> is negative.</exception>
        public string KeyForTrace(int traceIndex)
        {
            if (Scope != ThemeScope.PerTrace)
            {
                throw new InvalidOperationException(
                    Name + " is a global colour; it has one key, not one per trace.");
            }

            if (traceIndex < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(traceIndex), traceIndex, "A trace index counts from zero.");
            }

            return Key + "." + TraceColours.LetterAt(traceIndex);
        }

        /// <inheritdoc />
        public override string ToString() => Name + " (" + Scope + ")";
    }

    /// <summary>
    /// The themeable element set of <c>REQ-UI-022</c>.
    /// </summary>
    public static class ThemeElements
    {
        /// <summary>Emitter colours in the per-trace set.</summary>
        public const int EmitterCount = 32;

        /// <summary>Group colours in the per-trace set.</summary>
        public const int GroupCount = 48;

        /// <summary>Modulation-type colours in the global set.</summary>
        /// <remarks>
        /// The requirement writes "Mod Type N" without fixing N. Eight is the number of modulation
        /// types a demodulation display distinguishes at once before the colours stop being
        /// distinguishable; stated here so the set is enumerable, and changeable in one place if
        /// <c>REQ-DEM</c> asks for more.
        /// </remarks>
        public const int ModulationTypeCount = 8;

        private static readonly ReadOnlyCollection<ThemeElement> AllElements = Build();

        /// <summary>Every themeable element, global first then per trace.</summary>
        public static IReadOnlyList<ThemeElement> All => AllElements;

        /// <summary>The global elements.</summary>
        public static IReadOnlyList<ThemeElement> Global => Where(ThemeScope.Global);

        /// <summary>The per-trace elements.</summary>
        public static IReadOnlyList<ThemeElement> PerTrace => Where(ThemeScope.PerTrace);

        /// <summary>
        /// The element with a given name.
        /// </summary>
        /// <param name="name">The display name, compared exactly.</param>
        /// <returns>The element, or <c>null</c> if there is none.</returns>
        public static ThemeElement ByName(string name)
        {
            foreach (ThemeElement element in AllElements)
            {
                if (string.Equals(element.Name, name, StringComparison.Ordinal))
                {
                    return element;
                }
            }

            return null;
        }

        /// <summary>
        /// Every resource key the theme dictionary must define, for a given trace count.
        /// </summary>
        /// <param name="traces">How many traces the theme covers; at least one.</param>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="traces"/> is less than one.</exception>
        public static IReadOnlyList<string> KeysFor(int traces)
        {
            if (traces < 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(traces), traces, "A theme covers at least one trace.");
            }

            var keys = new List<string>();

            foreach (ThemeElement element in AllElements)
            {
                if (element.Scope == ThemeScope.Global)
                {
                    keys.Add(element.Key);
                    continue;
                }

                for (int trace = 0; trace < traces; trace++)
                {
                    keys.Add(element.KeyForTrace(trace));
                }
            }

            return new ReadOnlyCollection<string>(keys);
        }

        private static IReadOnlyList<ThemeElement> Where(ThemeScope scope)
        {
            var found = new List<ThemeElement>();

            foreach (ThemeElement element in AllElements)
            {
                if (element.Scope == scope)
                {
                    found.Add(element);
                }
            }

            return new ReadOnlyCollection<ThemeElement>(found);
        }

        private static ReadOnlyCollection<ThemeElement> Build()
        {
            var elements = new List<ThemeElement>();

            // Global, in the requirement's own order.
            string[] global =
            {
                "ACP", "ACP annotation", "Annotation", "Annotation Background", "Grid",
                "Indicator", "Limit", "Fail Limit", "Margin", "Fail Margin",
                "Marker Window Background", "Selected Marker", "Not Selected Marker",
                "OBW", "OBW annotation", "Slot Annotation", "Slot Data", "Slot MAC",
                "Slot Midamble", "Slot Pilot Downlink", "Slot Pilot Uplink", "Slot Preamble",
                "Slot Selected", "Trace Background",
            };

            foreach (string name in global)
            {
                elements.Add(new ThemeElement(name, ThemeScope.Global));
            }

            for (int type = 1; type <= ModulationTypeCount; type++)
            {
                elements.Add(new ThemeElement(
                    "Mod Type " + type.ToString(CultureInfo.InvariantCulture), ThemeScope.Global));
            }

            // Per trace.
            string[] perTrace =
            {
                "Trace", "Symbol", "Average", "Pilot", "Spectrogram Marker", "Trace Select",
            };

            foreach (string name in perTrace)
            {
                elements.Add(new ThemeElement(name, ThemeScope.PerTrace));
            }

            for (int emitter = 1; emitter <= EmitterCount; emitter++)
            {
                elements.Add(new ThemeElement(
                    "Emitter " + emitter.ToString(CultureInfo.InvariantCulture),
                    ThemeScope.PerTrace));
            }

            for (int group = 1; group <= GroupCount; group++)
            {
                elements.Add(new ThemeElement(
                    "Group " + group.ToString(CultureInfo.InvariantCulture), ThemeScope.PerTrace));
            }

            return new ReadOnlyCollection<ThemeElement>(elements);
        }
    }
}
