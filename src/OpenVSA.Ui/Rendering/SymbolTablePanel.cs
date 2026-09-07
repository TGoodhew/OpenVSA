using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using OpenVSA.Demod.Results;

namespace OpenVSA.Ui.Rendering
{
    /// <summary>
    /// <c>REQ-UI-052</c>'s trace: the error summary above, the symbol stream below, one trace.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>One control with two portions, and that is the structural point the requirement
    /// makes.</strong> It says so in its own title — "are ONE trace, split top and bottom" — and
    /// warns that "getting it wrong means building two traces where the product has one". So this
    /// is a single element that a trace window hosts; there is no second trace, and selecting the
    /// trace selects both portions because there is only one thing to select.
    /// </para>
    /// <para>
    /// <strong>Both portions draw from the Tabular slot of <c>REQ-UI-080</c>.</strong> The error
    /// summary's <c>=</c> column and the symbol stream's groups of eight only line up in a
    /// monospaced face; <c>REQ-UI-052</c> says as much, and until this display existed the Tabular
    /// slot had no surface to be judged on.
    /// </para>
    /// </remarks>
    public sealed class SymbolTablePanel : Grid
    {
        private readonly TextBlock _summary;
        private readonly TextBlock _stream;

        private SymbolTrace _trace;

        private MeasurementProvenance _provenance;
        private SymbolTableFormat _format = SymbolTableFormat.Binary;
        private int _charactersPerRow = 32;
        private SymbolSelection _selection;

        /// <summary>Creates an empty panel.</summary>
        public SymbolTablePanel()
        {
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            RowDefinitions.Add(new RowDefinition { Height = new GridLength(1.0, GridUnitType.Star) });

            _summary = Portion();
            _stream = Portion();

            SetRow(_summary, 0);
            SetRow(_stream, 1);

            Children.Add(_summary);
            Children.Add(_stream);

            Refresh();
        }

        /// <summary>The error-summary metrics (<c>REQ-UI-053</c>), the top portion.</summary>
        public TextBlock SummaryPortion => _summary;

        /// <summary>The detected symbol stream, the bottom portion.</summary>
        public TextBlock StreamPortion => _stream;

        /// <summary>The highlight behind a selected symbol (<c>REQ-DEM-083</c>).</summary>
        /// <remarks>
        /// The chrome's own selection colours rather than colours of this panel's choosing: the
        /// stream is text in a themed control, and <c>REQ-UI-083</c> is that every themed value
        /// resolves through a resource dictionary keyed by name. Resolved when the run is built, so
        /// a theme change repaints the highlight with everything else.
        /// </remarks>
        public object SelectionBackgroundKey { get; set; } =
            Theming.ChromeKeys.SelectionBackground;

        /// <summary>The text colour of a selected symbol (<c>REQ-DEM-083</c>).</summary>
        public object SelectionForegroundKey { get; set; } =
            Theming.ChromeKeys.SelectionForeground;

        /// <summary>
        /// Where the selected symbol is in the rendered stream, or
        /// <see cref="SymbolPosition.NotFound"/>.
        /// </summary>
        /// <remarks>
        /// Reported so the criterion — "selecting symbol <em>k</em> highlights ... for symbol
        /// <em>k</em> specifically, which an off-by-one selection fails" — can be checked against a
        /// position rather than against a screenshot.
        /// </remarks>
        public SymbolPosition SelectionPosition { get; private set; } = SymbolPosition.NotFound;

        /// <summary>
        /// The symbol selection this panel shows and sets (<c>REQ-DEM-083</c>), or null.
        /// </summary>
        public SymbolSelection Selection
        {
            get { return _selection; }

            set
            {
                if (ReferenceEquals(_selection, value))
                {
                    return;
                }

                if (_selection != null)
                {
                    _selection.Changed -= OnSelectionChanged;
                }

                _selection = value;

                if (_selection != null)
                {
                    _selection.Changed += OnSelectionChanged;
                }

                Refresh();
            }
        }

        /// <summary>
        /// Selects the symbol a character of the stream belongs to (<c>REQ-DEM-083</c>).
        /// </summary>
        /// <param name="row">Which row of the stream, from zero.</param>
        /// <param name="column">Which column of that row.</param>
        /// <returns>The symbol selected, or <see cref="SymbolSelection.None"/>.</returns>
        public int SelectAt(int row, int column)
        {
            if (_selection == null || _trace == null)
            {
                return SymbolSelection.None;
            }

            int symbol = SymbolTable.SymbolAt(
                _trace.Symbols, row, column, _trace.BitsPerSymbol, _format, _charactersPerRow);

            _selection.Select(symbol);

            return symbol;
        }

        private void OnSelectionChanged(object sender, EventArgs e) => Refresh();

        /// <summary>
        /// How many portions this trace has.
        /// </summary>
        /// <remarks>
        /// Two, always. Reported so a test can assert the structure the requirement is about
        /// rather than count children and hope.
        /// </remarks>
        public int PortionCount => 2;

        /// <summary>The result being shown, or <c>null</c>.</summary>
        public SymbolTrace Result
        {
            get { return _trace; }

            set
            {
                _trace = value;

                if (value != null && !SymbolTable.IsAvailable(_format, value.BitsPerSymbol))
                {
                    // A result whose symbols are too narrow for the chosen format falls back rather
                    // than showing nothing: hex below four bits per symbol is REQ-UI-052's own
                    // exclusion, and binary always works.
                    _format = SymbolTableFormat.Binary;
                }

                // REQ-DEM-083: a selection outside the new result is cleared rather than left
                // pointing past the end of it.
                _selection?.Update(_trace);

                Refresh();
            }
        }

        /// <summary>
        /// How the symbols are spelled (<c>REQ-UI-052</c>).
        /// </summary>
        /// <exception cref="InvalidOperationException">
        /// Hexadecimal was asked for and the result's symbols are too narrow for it.
        /// </exception>
        public SymbolTableFormat Format
        {
            get { return _format; }

            set
            {
                if (_trace != null && !SymbolTable.IsAvailable(value, _trace.BitsPerSymbol))
                {
                    throw new InvalidOperationException(
                        SymbolTable.ReasonAgainst(value, _trace.BitsPerSymbol));
                }

                _format = value;
                Refresh();
            }
        }

        /// <summary>Characters of stream to a row; a whole number of groups of eight.</summary>
        /// <exception cref="ArgumentOutOfRangeException">Not a whole number of groups.</exception>
        public int CharactersPerRow
        {
            get { return _charactersPerRow; }

            set
            {
                if (value < SymbolTable.GroupSize || value % SymbolTable.GroupSize != 0)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(value), value,
                        "A row holds a whole number of groups of " + SymbolTable.GroupSize + ".");
                }

                _charactersPerRow = value;
                Refresh();
            }
        }

        /// <summary>Whether the hexadecimal format can spell this result's symbols.</summary>
        public bool IsHexAvailable =>
            _trace != null && SymbolTable.IsAvailable(SymbolTableFormat.Hexadecimal, _trace.BitsPerSymbol);

        /// <summary>Puts both portions in a font slot (<c>REQ-UI-080</c>'s Tabular).</summary>
        /// <param name="family">The typeface.</param>
        /// <param name="sizePoints">The size, in points.</param>
        /// <exception cref="ArgumentNullException"><paramref name="family"/> is null.</exception>
        /// <remarks>
        /// Both portions together, because they are one trace and a summary in one face above a
        /// stream in another would be the two-trace mistake showing through the styling.
        /// </remarks>
        public void ApplyFont(System.Windows.Media.FontFamily family, double sizePoints)
        {
            if (family == null)
            {
                throw new ArgumentNullException(nameof(family));
            }

            foreach (TextBlock portion in new[] { _summary, _stream })
            {
                portion.FontFamily = family;
                portion.FontSize = sizePoints * 96.0 / 72.0;
            }
        }

        /// <summary>
        /// Draws both portions from the Tabular slot (<c>REQ-UI-080</c>, <c>REQ-UI-052</c>).
        /// </summary>
        /// <param name="fonts">The font preferences.</param>
        /// <exception cref="ArgumentNullException"><paramref name="fonts"/> is null.</exception>
        /// <remarks>
        /// <strong>Tabular, never Annotation, and that is the whole reason the third slot
        /// exists.</strong> <c>REQ-UI-080</c> says so outright: the reference product's two-slot
        /// scheme forces an unhappy compromise here, because general trace annotation reads better
        /// proportional while this block only aligns in a fixed-width face. Taking the Annotation
        /// slot would give the right answer whenever a user happened to have set Annotation to a
        /// monospaced face and the wrong one the rest of the time.
        /// </remarks>
        public void ApplyFonts(FontPreferences fonts)
        {
            if (fonts == null)
            {
                throw new ArgumentNullException(nameof(fonts));
            }

            ApplyFont(fonts.Resolve(FontSlot.Tabular), fonts.Choice(FontSlot.Tabular).SizePoints);
        }

        /// <summary>
        /// The context the metrics were measured in, shown beneath them (<c>REQ-DEM-072</c>).
        /// </summary>
        /// <remarks>
        /// Null until a demodulation has been shown. The panel can be given a trace alone -- the
        /// generated ones the exercise uses are -- and then there is no provenance to show, only the
        /// normalisation the summary computed for itself.
        /// </remarks>
        public MeasurementProvenance Provenance
        {
            get { return _provenance; }

            set
            {
                _provenance = value;
                Refresh();
            }
        }

        private void Refresh()
        {
            if (_trace == null || _trace.SymbolCount == 0)
            {
                _summary.Text = "No demodulated result.";
                _stream.Text = string.Empty;
                return;
            }

            ErrorSummary summary = ErrorSummary.For(_trace);

            // REQ-DEM-072: the metrics and the context that qualifies them, together, because a
            // percentage whose denominator is not on screen is the commonest reason two instruments
            // appear to disagree about EVM -- and the requirement says so in as many words. When
            // the panel has been given a whole result it shows that result's provenance; with only
            // a trace it can still say what the figures were referenced to, which is REQ-DEM-061's
            // half of the same obligation.
            var text = new List<string>(summary.Render());

            text.Add(string.Empty);

            if (_provenance != null)
            {
                text.AddRange(_provenance.Lines);
            }
            else if (summary.Reference != null)
            {
                text.Add(summary.Reference.Describe());
            }

            _summary.Text = string.Join(Environment.NewLine, text);

            IReadOnlyList<string> rows = SymbolTable.Render(
                _trace.Symbols, _trace.BitsPerSymbol, _format, _charactersPerRow);

            string stream = string.Join(Environment.NewLine, rows);

            SelectionPosition = _selection == null || !_selection.HasSelection
                ? SymbolPosition.NotFound
                : SymbolTable.Locate(
                    _trace.Symbols, _selection.Selected, _trace.BitsPerSymbol, _format,
                    _charactersPerRow);

            _stream.Text = stream;
            _stream.Inlines.Clear();

            if (!SelectionPosition.IsFound)
            {
                _stream.Text = stream;
                return;
            }

            // The stream is rebuilt as three runs so the selected symbol's own characters carry the
            // highlight, rather than the whole row. REQ-DEM-083 asks for "position in the eye" and
            // "the corresponding point"; the table's equivalent of a point is the characters that
            // spell the symbol, and highlighting the row would answer a coarser question than the
            // one asked.
            int start = OffsetIn(rows, SelectionPosition);
            int length = Math.Min(SelectionPosition.Length, Math.Max(0, stream.Length - start));

            _stream.Inlines.Add(new Run(stream.Substring(0, start)));

            var highlighted = new Run(stream.Substring(start, length));

            highlighted.SetResourceReference(TextElement.BackgroundProperty, SelectionBackgroundKey);
            highlighted.SetResourceReference(TextElement.ForegroundProperty, SelectionForegroundKey);

            _stream.Inlines.Add(highlighted);
            _stream.Inlines.Add(new Run(stream.Substring(start + length)));
        }

        /// <summary>
        /// Where a position lands in the joined text, spacing and line breaks included.
        /// </summary>
        /// <remarks>
        /// The characters of a symbol are contiguous in the ungrouped stream and not in the
        /// rendered text — a group space or a line break can fall inside one — so this converts
        /// through the rows rather than by arithmetic on the stream offset. A symbol split by a
        /// break has the first part of it highlighted, which is where it starts.
        /// </remarks>
        private static int OffsetIn(IReadOnlyList<string> rows, SymbolPosition position)
        {
            int offset = 0;

            for (int row = 0; row < position.Row && row < rows.Count; row++)
            {
                offset += rows[row].Length + Environment.NewLine.Length;
            }

            return offset + position.Column;
        }

        private static TextBlock Portion() => new TextBlock
        {
            // Monospaced by default, because the alignment of both portions depends on it and a
            // proportional face makes the = column ragged and the groups of eight meaningless.
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            Margin = new Thickness(8.0, 6.0, 8.0, 6.0),
            TextWrapping = TextWrapping.NoWrap,
        };
    }
}
