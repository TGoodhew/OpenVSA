using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using OpenVSA.Personality;

namespace OpenVSA.Ui
{
    /// <summary>
    /// What the results panel shows for the selected measurement type (<c>REQ-ARC-003</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// A personality returns a name, a value and a unit, and deliberately no formatting —
    /// <see cref="IMeasurementPersonality"/> says why: a plug-in that returned strings would take
    /// the shell's formatting decisions away from it and the same reading would be printed
    /// differently by every one. This is where that decision is made, once.
    /// </para>
    /// <para>
    /// Below the window, so what is displayed can be asserted without one — the same division
    /// <see cref="ConnectionListing"/> keeps.
    /// </para>
    /// </remarks>
    public sealed class PersonalityResults
    {
        private readonly List<string> _lines = new List<string>();

        /// <summary>The name of the personality these readings came from, or empty.</summary>
        public string PersonalityName { get; private set; } = string.Empty;

        /// <summary>The standard and revision the readings were measured against, or empty.</summary>
        public string Standard { get; private set; } = string.Empty;

        /// <summary>The formatted readings, one per line.</summary>
        public IReadOnlyList<string> Lines => new ReadOnlyCollection<string>(_lines);

        /// <summary>How many readings are shown.</summary>
        public int Count => _lines.Count;

        /// <summary>Whether a personality is selected at all.</summary>
        public bool HasPersonality => PersonalityName.Length > 0;

        /// <summary>Records the personality now selected, with no readings yet.</summary>
        /// <param name="personality">The personality, or <c>null</c> for plain spectrum.</param>
        /// <remarks>
        /// The standard and its revision are shown beside the name because
        /// <c>REQ-PER-011</c> requires a declared revision — a result that cannot say what it was
        /// measured against is not a compliance result, and the panel is where a reader would look
        /// for it.
        /// </remarks>
        public void Select(IMeasurementPersonality personality)
        {
            _lines.Clear();

            if (personality == null)
            {
                PersonalityName = string.Empty;
                Standard = string.Empty;
                return;
            }

            PersonalityName = personality.DisplayName;

            Standard = string.IsNullOrEmpty(personality.StandardRevision)
                ? personality.Standard
                : personality.Standard + " " + personality.StandardRevision;
        }

        /// <summary>Replaces the readings shown.</summary>
        /// <param name="readings">What the personality returned; <c>null</c> clears.</param>
        public void Update(IEnumerable<PersonalityReading> readings)
        {
            _lines.Clear();

            if (readings == null)
            {
                return;
            }

            // Two passes so the values line up under one another. A column of numbers whose decimal
            // points wander is markedly harder to scan, and a results panel is read by scanning.
            List<PersonalityReading> ordered = readings.Where(r => r != null).ToList();

            if (ordered.Count == 0)
            {
                return;
            }

            int width = ordered.Max(r => r.Name.Length);

            foreach (PersonalityReading reading in ordered)
            {
                _lines.Add(
                    reading.Name.PadRight(width) + "  " + Format(reading.Value) +
                    (string.IsNullOrEmpty(reading.Unit) ? string.Empty : " " + reading.Unit));
            }
        }

        /// <summary>What the panel says when there is nothing to show.</summary>
        /// <remarks>
        /// Three different nothings again. "No personality selected" is the ordinary state;
        /// "selected but no readings" means the measurement has not run or the personality refused
        /// the block, and a reader needs to know which.
        /// </remarks>
        public string Summary
        {
            get
            {
                if (!HasPersonality)
                {
                    return "Spectrum. No measurement personality is selected.";
                }

                if (_lines.Count == 0)
                {
                    return PersonalityName + " (" + Standard + ") — no readings yet.";
                }

                return PersonalityName + " (" + Standard + ")";
            }
        }

        /// <summary>Formats a value at a fixed width, in a form a reader can compare.</summary>
        /// <param name="value">The value.</param>
        /// <remarks>
        /// Six significant figures, which is more than any of these measurements is good to and
        /// fewer than a double will print. Not-a-number is spelled out rather than shown as "NaN":
        /// a personality that could not compute a reading is saying something, and a reader who has
        /// not met the abbreviation would take it for a fault in the display.
        /// </remarks>
        internal static string Format(double value)
        {
            if (double.IsNaN(value))
            {
                return "not measured";
            }

            if (double.IsPositiveInfinity(value))
            {
                return "unbounded";
            }

            if (double.IsNegativeInfinity(value))
            {
                return "negligible";
            }

            return value.ToString("G6", CultureInfo.CurrentCulture);
        }
    }
}
