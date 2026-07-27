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
        /// <summary>Fewest divisions a graticule may be set to.</summary>
        /// <remarks>
        /// Two: one division is a rectangle with no interior line, which is a graticule in name
        /// only. Below that the count stops meaning anything.
        /// </remarks>
        public const int MinimumDivisions = 2;

        /// <summary>Most divisions a graticule may be set to.</summary>
        /// <remarks>
        /// Twenty. Beyond it the lines are closer together than the annotation that labels them at
        /// any usable size, and the grid reads as a wash rather than as a scale.
        /// </remarks>
        public const int MaximumDivisions = 20;

        private bool _forceWhiteBackgroundOnPrint = true;
        private bool _indicateLimitFailures = true;
        private bool _indicateMarginWarnings = true;
        private bool _showAnnotation = true;
        private bool _showGridLines = true;
        private int _horizontalDivisions = PlotLayout.DefaultDivisions;
        private int _verticalDivisions = PlotLayout.DefaultDivisions;
        private int _xReferencePercent = ReferencePosition.DefaultXPercent;

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

        /// <summary>
        /// Whether trace annotation is drawn, and so whether the graticule has room reserved for it
        /// (<c>REQ-UI-011</c>).
        /// </summary>
        /// <remarks>
        /// Turning it off is not a visibility change: the annotation band is reclaimed and the
        /// graticule expands into it. That is the requirement's criterion - "toggling Show
        /// Annotation changes the plot rectangle's size, not merely text visibility" - and it is
        /// what makes the setting worth having, because the reason to turn annotation off is to see
        /// more trace.
        /// </remarks>
        public bool ShowAnnotation
        {
            get { return _showAnnotation; }

            set
            {
                if (_showAnnotation == value)
                {
                    return;
                }

                _showAnnotation = value;
                RaiseChanged();
            }
        }

        /// <summary>
        /// Whether the graticule lines are drawn (<c>REQ-UI-011</c>).
        /// </summary>
        /// <remarks>
        /// Independent of <see cref="ShowAnnotation"/>, which the requirement states explicitly.
        /// The graticule rectangle keeps its size and its background either way; only the lines go.
        /// </remarks>
        public bool ShowGridLines
        {
            get { return _showGridLines; }

            set
            {
                if (_showGridLines == value)
                {
                    return;
                }

                _showGridLines = value;
                RaiseChanged();
            }
        }

        /// <summary>Graticule columns (<c>REQ-UI-012</c>).</summary>
        /// <exception cref="ArgumentOutOfRangeException">Outside the settable range.</exception>
        public int HorizontalDivisions
        {
            get { return _horizontalDivisions; }
            set { Set(ref _horizontalDivisions, value, nameof(value)); }
        }

        /// <summary>Graticule rows (<c>REQ-UI-012</c>).</summary>
        /// <exception cref="ArgumentOutOfRangeException">Outside the settable range.</exception>
        public int VerticalDivisions
        {
            get { return _verticalDivisions; }
            set { Set(ref _verticalDivisions, value, nameof(value)); }
        }

        /// <summary>
        /// Where the reference column sits, 0 at the left edge through 100 at the right
        /// (<c>REQ-UI-013</c>).
        /// </summary>
        /// <remarks>
        /// X only. The Y reference position is per format and defaults differently for each, so it
        /// belongs to the trace rather than to the display as a whole - see
        /// <see cref="ReferencePosition.DefaultYPercentFor"/>.
        /// </remarks>
        /// <exception cref="ArgumentOutOfRangeException">Outside 0 to 100.</exception>
        public int XReferencePercent
        {
            get { return _xReferencePercent; }

            set
            {
                ReferencePosition.Validate(value, nameof(value));

                if (_xReferencePercent == value)
                {
                    return;
                }

                _xReferencePercent = value;
                RaiseChanged();
            }
        }

        /// <summary>Raised whenever any of them changes, so every surface can follow.</summary>
        public event EventHandler Changed;

        /// <summary>
        /// Returns every option to its default (<c>REQ-UI-061</c> Preset &gt; Display Preferences).
        /// </summary>
        /// <remarks>
        /// From a fresh instance rather than by assigning the defaults again here. Written the
        /// second way, an option whose default changed would go on being preset to the old one, and
        /// nothing would report it — the same reason the factory preset is a fresh state rather
        /// than a list of values.
        /// </remarks>
        public void ResetAll()
        {
            var defaults = new TraceDisplayOptions();

            ForceWhiteBackgroundOnPrint = defaults.ForceWhiteBackgroundOnPrint;
            IndicateLimitFailures = defaults.IndicateLimitFailures;
            IndicateMarginWarnings = defaults.IndicateMarginWarnings;
            ShowAnnotation = defaults.ShowAnnotation;
            ShowGridLines = defaults.ShowGridLines;
            HorizontalDivisions = defaults.HorizontalDivisions;
            VerticalDivisions = defaults.VerticalDivisions;
            XReferencePercent = defaults.XReferencePercent;
        }

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
            state.ShowAnnotation = _showAnnotation;
            state.ShowGridLines = _showGridLines;
            state.HorizontalDivisions = _horizontalDivisions;
            state.VerticalDivisions = _verticalDivisions;
            state.XReferencePercent = _xReferencePercent;
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

            int horizontal = Clamp(state.HorizontalDivisions);
            int vertical = Clamp(state.VerticalDivisions);
            int reference = state.XReferencePercent < ReferencePosition.MinimumPercent ||
                            state.XReferencePercent > ReferencePosition.MaximumPercent
                ? ReferencePosition.DefaultXPercent
                : state.XReferencePercent;

            bool moved =
                _forceWhiteBackgroundOnPrint != state.ForceWhiteBackgroundOnPrint ||
                _indicateLimitFailures != state.IndicateLimitFailures ||
                _indicateMarginWarnings != state.IndicateMarginWarnings ||
                _showAnnotation != state.ShowAnnotation ||
                _showGridLines != state.ShowGridLines ||
                _horizontalDivisions != horizontal ||
                _verticalDivisions != vertical ||
                _xReferencePercent != reference;

            _forceWhiteBackgroundOnPrint = state.ForceWhiteBackgroundOnPrint;
            _indicateLimitFailures = state.IndicateLimitFailures;
            _indicateMarginWarnings = state.IndicateMarginWarnings;
            _showAnnotation = state.ShowAnnotation;
            _showGridLines = state.ShowGridLines;
            _horizontalDivisions = horizontal;
            _verticalDivisions = vertical;
            _xReferencePercent = reference;

            if (moved)
            {
                RaiseChanged();
            }
        }

        /// <summary>
        /// Brings a division count from a file into range.
        /// </summary>
        /// <remarks>
        /// Clamped rather than thrown on. A preferences file is a thing a user can edit, and a
        /// nonsense division count should cost them a graticule they recognise, not a shell that
        /// will not start.
        /// </remarks>
        private static int Clamp(int divisions)
        {
            if (divisions < MinimumDivisions)
            {
                return MinimumDivisions;
            }

            return divisions > MaximumDivisions ? MaximumDivisions : divisions;
        }

        private void Set(ref int field, int value, string name)
        {
            if (value < MinimumDivisions || value > MaximumDivisions)
            {
                throw new ArgumentOutOfRangeException(
                    name,
                    value,
                    "A graticule has between " + MinimumDivisions + " and " + MaximumDivisions +
                    " divisions on an axis.");
            }

            if (field == value)
            {
                return;
            }

            field = value;
            RaiseChanged();
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
            _horizontalDivisions + "x" + _verticalDivisions + " divisions" +
            (_showAnnotation ? ", annotated" : ", no annotation") +
            (_showGridLines ? ", grid lines" : ", no grid lines") +
            ", failures " + (_indicateLimitFailures ? "shown" : "hidden") +
            ", margins " + (_indicateMarginWarnings ? "shown" : "hidden") +
            ", printing " + (_forceWhiteBackgroundOnPrint ? "on white" : "as displayed");
    }
}
