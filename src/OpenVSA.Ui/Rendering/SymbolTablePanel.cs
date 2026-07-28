using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
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
        private SymbolTableFormat _format = SymbolTableFormat.Binary;
        private int _charactersPerRow = 32;

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

        private void Refresh()
        {
            if (_trace == null || _trace.SymbolCount == 0)
            {
                _summary.Text = "No demodulated result.";
                _stream.Text = string.Empty;
                return;
            }

            _summary.Text = string.Join(
                Environment.NewLine, ErrorSummary.For(_trace).Render());

            IReadOnlyList<string> rows = SymbolTable.Render(
                _trace.Symbols, _trace.BitsPerSymbol, _format, _charactersPerRow);

            _stream.Text = string.Join(Environment.NewLine, rows);
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
