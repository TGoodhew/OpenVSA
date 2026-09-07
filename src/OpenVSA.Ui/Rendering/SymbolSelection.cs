using System;
using OpenVSA.Demod.Results;

namespace OpenVSA.Ui.Rendering
{
    /// <summary>
    /// Which symbol the displays are pointing at (<c>REQ-DEM-083</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>One selection, not one per display.</strong> The requirement says selecting in the
    /// table highlights the constellation and the eye "and vice versa", which is not two
    /// synchronisations to write but one thing three surfaces read. A selection held on each
    /// display and copied between them is the arrangement in which the copies drift, and the
    /// drift shows up as a highlight on the wrong symbol — which is the failure the requirement
    /// spends its criterion on.
    /// </para>
    /// <para>
    /// <strong>Settling is a property of "same value, no event".</strong> The criterion is that
    /// "selecting in the constellation highlights the table row without re-triggering a further
    /// selection". A surface that hears <see cref="Changed"/> and selects what it has just been
    /// told is the normal case, not a bug, and it terminates here rather than in each surface's
    /// handler: selecting what is already selected raises nothing. That makes the loop finite for
    /// every surface, including ones written later that do not know they had to guard.
    /// </para>
    /// <para>
    /// <strong>A measurement update is not a selection change.</strong> "Selection survives a
    /// measurement update if the symbol still exists, and clears cleanly if it does not" — so
    /// <see cref="Update"/> exists, and it neither reselects nor silently keeps an index that has
    /// run off the end of a shorter result.
    /// </para>
    /// </remarks>
    public sealed class SymbolSelection
    {
        /// <summary>No symbol is selected.</summary>
        public const int None = -1;

        private int _selected = None;

        /// <summary>Raised when the selected symbol changes.</summary>
        public event EventHandler Changed;

        /// <summary>The selected symbol, or <see cref="None"/>.</summary>
        public int Selected => _selected;

        /// <summary>Whether a symbol is selected.</summary>
        public bool HasSelection => _selected != None;

        /// <summary>How many times <see cref="Changed"/> has been raised.</summary>
        /// <remarks>
        /// Reported so that "without re-triggering a further selection" is something a test can
        /// assert. A selection that echoes around the surfaces looks right on the screen and shows
        /// up only as a count.
        /// </remarks>
        public int Changes { get; private set; }

        /// <summary>
        /// Selects a symbol.
        /// </summary>
        /// <param name="symbol">Which symbol, or <see cref="None"/> to select nothing.</param>
        /// <remarks>
        /// Selecting what is already selected does nothing at all — no event, no count. That is
        /// what makes a round trip between two surfaces terminate.
        /// </remarks>
        public void Select(int symbol)
        {
            int wanted = symbol < 0 ? None : symbol;

            if (_selected == wanted)
            {
                return;
            }

            _selected = wanted;
            Changes++;

            Changed?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Selects nothing.</summary>
        public void Clear() => Select(None);

        /// <summary>
        /// Reconciles the selection with a new measurement.
        /// </summary>
        /// <param name="result">The new result, or null when there is none.</param>
        /// <returns>Whether the selection survived.</returns>
        /// <remarks>
        /// The index is the identity — symbol 37 of the new result is what symbol 37 of the old one
        /// was in the only sense a symbol table offers. So a selection inside the new result is
        /// kept without an event, because nothing about the selection changed, and one outside it
        /// is cleared with one, because something did.
        /// </remarks>
        public bool Update(SymbolTrace result)
        {
            if (!HasSelection)
            {
                return false;
            }

            if (result != null && _selected < result.SymbolCount)
            {
                return true;
            }

            Clear();

            return false;
        }
    }
}
