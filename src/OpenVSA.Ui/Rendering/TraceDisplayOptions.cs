using System;
using OpenVSA.Measurement.State;

namespace OpenVSA.Ui.Rendering
{
    /// <summary>
    /// The trace-display preferences of the Display Preferences dialog's Trace tab
    /// (<c>REQ-UI-073</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>One piece of state behind two surfaces.</strong> Each of these three has a menu item
    /// as well as a control on the Trace tab, and <c>REQ-UI-070</c>'s third criterion is that
    /// editing a parameter from one surface is visible on the other without either being reopened.
    /// The way to fail that criterion is for the menu item's <c>IsChecked</c> to <em>be</em> the
    /// setting, so the tab has to read it out of a menu; keeping the setting here and having both
    /// surfaces follow <see cref="Changed"/> is what makes the criterion hold rather than nearly
    /// hold.
    /// </para>
    /// <para>
    /// Display preferences rather than part of a setup (<c>REQ-STA-002</c>): whether a failing limit
    /// recolours your trace is about how you like to read a display, and recalling a colleague's
    /// measurement should not change it.
    /// </para>
    /// </remarks>
    public sealed class TraceDisplayOptions
    {
        private bool _forceWhiteBackgroundOnPrint = true;
        private bool _indicateLimitFailures = true;
        private bool _indicateMarginWarnings = true;

        /// <summary>
        /// Whether printing forces a white background (<c>REQ-UI-015</c>).
        /// </summary>
        /// <remarks>
        /// On by default, and the default is the shell's from before this tab existed: large areas
        /// of black do not print well, and the palette darkens the light trace colours rather than
        /// leaving them invisible on white. It affects nothing until something is printed.
        /// </remarks>
        public bool ForceWhiteBackgroundOnPrint
        {
            get { return _forceWhiteBackgroundOnPrint; }

            set
            {
                if (_forceWhiteBackgroundOnPrint == value)
                {
                    return;
                }

                _forceWhiteBackgroundOnPrint = value;
                RaiseChanged();
            }
        }

        /// <summary>Whether a trace that fails a limit is recoloured (<c>REQ-UI-023</c>).</summary>
        public bool IndicateLimitFailures
        {
            get { return _indicateLimitFailures; }

            set
            {
                if (_indicateLimitFailures == value)
                {
                    return;
                }

                _indicateLimitFailures = value;
                RaiseChanged();
            }
        }

        /// <summary>Whether a trace inside the margin is recoloured (<c>REQ-UI-023</c>).</summary>
        public bool IndicateMarginWarnings
        {
            get { return _indicateMarginWarnings; }

            set
            {
                if (_indicateMarginWarnings == value)
                {
                    return;
                }

                _indicateMarginWarnings = value;
                RaiseChanged();
            }
        }

        /// <summary>Raised whenever any of them changes, so every surface can follow.</summary>
        public event EventHandler Changed;

        /// <summary>Writes the options into a display-preferences sidecar.</summary>
        /// <param name="state">The sidecar to write into.</param>
        /// <exception cref="ArgumentNullException"><paramref name="state"/> is null.</exception>
        public void SaveInto(DisplayPreferencesState state)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            state.ForceWhiteBackgroundOnPrint = _forceWhiteBackgroundOnPrint;
            state.IndicateLimitFailures = _indicateLimitFailures;
            state.IndicateMarginWarnings = _indicateMarginWarnings;
        }

        /// <summary>Reads the options back from a display-preferences sidecar.</summary>
        /// <param name="state">The sidecar to read.</param>
        /// <exception cref="ArgumentNullException"><paramref name="state"/> is null.</exception>
        /// <remarks>
        /// Raises <see cref="Changed"/> once if anything moved, rather than once per property: the
        /// surfaces that follow it rebuild themselves from all three together.
        /// </remarks>
        public void LoadFrom(DisplayPreferencesState state)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            bool moved =
                _forceWhiteBackgroundOnPrint != state.ForceWhiteBackgroundOnPrint ||
                _indicateLimitFailures != state.IndicateLimitFailures ||
                _indicateMarginWarnings != state.IndicateMarginWarnings;

            _forceWhiteBackgroundOnPrint = state.ForceWhiteBackgroundOnPrint;
            _indicateLimitFailures = state.IndicateLimitFailures;
            _indicateMarginWarnings = state.IndicateMarginWarnings;

            if (moved)
            {
                RaiseChanged();
            }
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
            "failures " + (_indicateLimitFailures ? "shown" : "hidden") +
            ", margins " + (_indicateMarginWarnings ? "shown" : "hidden") +
            ", printing " + (_forceWhiteBackgroundOnPrint ? "on white" : "as displayed");
    }
}
