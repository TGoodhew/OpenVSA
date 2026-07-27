using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace OpenVSA.Ui.Dialogs.Pages
{
    /// <summary>
    /// The shape every tab of the Analysis dialog takes: labelled rows over live settings
    /// (<c>REQ-UI-072</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Every row is live.</strong> A control writes the setting as it is changed and reads
    /// it back when something else changes it, which is <c>REQ-UI-070</c> applied one row at a
    /// time. There is no per-page apply and no pending copy of anything.
    /// </para>
    /// <para>
    /// <strong>A rejected value is reported in place and changes nothing.</strong> The settings
    /// object validates; this catches the refusal and says so under the row, leaving the setting as
    /// it was. Silently clamping would be the worse failure — the user would read a number back
    /// that they had not typed and would not know why.
    /// </para>
    /// <para>
    /// Built in code rather than XAML, as the rest of the dialogs here are: these rows are
    /// generated from lists — the windows, the detectors, the supported point counts — and a
    /// designer has nothing to lay out that the enumerations do not already decide.
    /// </para>
    /// </remarks>
    public abstract class AnalysisPage : StackPanel
    {
        private readonly List<Action> _refreshers = new List<Action>();
        private readonly AnalysisSettings _settings;

        private bool _updating;

        /// <summary>Creates a page over the live settings.</summary>
        /// <param name="settings">The settings to edit; changed in place.</param>
        /// <exception cref="ArgumentNullException"><paramref name="settings"/> is null.</exception>
        protected AnalysisPage(AnalysisSettings settings)
        {
            if (settings == null)
            {
                throw new ArgumentNullException(nameof(settings));
            }

            _settings = settings;

            Margin = new Thickness(4.0);
            MinWidth = 440.0;

            // Bounded, so the explanatory paragraphs wrap instead of setting the dialog's width.
            // Fixed Size takes the union of every page, so one page that measures 1500 pixels wide
            // makes all seven of them that wide - the same lesson the colour list taught.
            MaxWidth = 620.0;

            settings.Changed += OnSettingsChanged;
            Unloaded += (sender, e) => settings.Changed -= OnSettingsChanged;
        }

        /// <summary>The settings this page edits.</summary>
        public AnalysisSettings Settings => _settings;

        /// <summary>How many editable rows the page has.</summary>
        /// <remarks>
        /// <c>REQ-UI-072</c> requires that every tab "is populated — none is a placeholder", and a
        /// test can only assert that against a number the page reports about itself.
        /// </remarks>
        public int RowCount { get; private set; }

        /// <summary>What the page is saying about the last rejected entry, or empty.</summary>
        public string Note { get; private set; } = string.Empty;

        /// <summary>Re-reads every row from the settings.</summary>
        public void Refresh()
        {
            _updating = true;

            try
            {
                foreach (Action refresher in _refreshers)
                {
                    refresher();
                }
            }
            finally
            {
                _updating = false;
            }
        }

        /// <summary>Adds an explanatory paragraph, which is not a row.</summary>
        /// <param name="text">The text.</param>
        protected void AddNote(string text) =>
            Children.Add(new TextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0.0, 0.0, 0.0, 10.0),
            });

        /// <summary>
        /// Adds a row choosing from a fixed list.
        /// </summary>
        /// <typeparam name="T">The option type.</typeparam>
        /// <param name="label">The row's label.</param>
        /// <param name="options">The options, in the order to offer them.</param>
        /// <param name="name">How an option is written.</param>
        /// <param name="read">Reads the setting.</param>
        /// <param name="write">Writes the setting.</param>
        protected ComboBox AddChoice<T>(
            string label,
            IEnumerable<T> options,
            Func<T, string> name,
            Func<T> read,
            Action<T> write)
        {
            var box = new ComboBox { MinWidth = 220.0 };
            var values = new List<T>(options);

            foreach (T option in values)
            {
                box.Items.Add(name(option));
            }

            box.SelectionChanged += (sender, e) =>
            {
                if (_updating || box.SelectedIndex < 0)
                {
                    return;
                }

                Apply(() => write(values[box.SelectedIndex]));
            };

            Row(label, box);

            _refreshers.Add(() =>
            {
                string wanted = name(read());

                for (int i = 0; i < values.Count; i++)
                {
                    if (string.Equals(name(values[i]), wanted, StringComparison.Ordinal))
                    {
                        box.SelectedIndex = i;
                        return;
                    }
                }

                box.SelectedIndex = -1;
            });

            return box;
        }

        /// <summary>
        /// Adds a row taking a number, with its own formatting and parsing.
        /// </summary>
        /// <param name="label">The row's label.</param>
        /// <param name="read">Reads the setting.</param>
        /// <param name="write">Writes the setting.</param>
        /// <param name="format">Formats the value for display.</param>
        /// <param name="parse">Parses typed text, returning <c>null</c> if it is not understood.</param>
        protected TextBox AddNumber(
            string label,
            Func<double> read,
            Action<double> write,
            Func<double, string> format,
            Func<string, double?> parse)
        {
            var box = new TextBox { MinWidth = 220.0 };

            box.TextChanged += (sender, e) =>
            {
                if (_updating)
                {
                    return;
                }

                double? parsed = parse(box.Text);

                if (parsed == null)
                {
                    Complain("'" + box.Text + "' is not a value this setting understands.");
                    return;
                }

                Apply(() => write(parsed.Value));
            };

            Row(label, box);

            _refreshers.Add(() => box.Text = format(read()));

            return box;
        }

        /// <summary>Adds a row that is a switch.</summary>
        /// <param name="label">The row's label.</param>
        /// <param name="read">Reads the setting.</param>
        /// <param name="write">Writes the setting.</param>
        protected CheckBox AddCheck(string label, Func<bool> read, Action<bool> write)
        {
            var box = new CheckBox
            {
                Content = label,
                Margin = new Thickness(0.0, 0.0, 0.0, 8.0),
            };

            box.Checked += (sender, e) => { if (!_updating) { Apply(() => write(true)); } };
            box.Unchecked += (sender, e) => { if (!_updating) { Apply(() => write(false)); } };

            Children.Add(box);
            RowCount++;

            _refreshers.Add(() => box.IsChecked = read());

            return box;
        }

        /// <summary>Adds a read-only row, for a quantity that is derived rather than set.</summary>
        /// <param name="label">The row's label.</param>
        /// <param name="read">Reads the derived text.</param>
        protected TextBlock AddDerived(string label, Func<string> read)
        {
            var text = new TextBlock
            {
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
            };

            Row(label, text, counts: false);

            _refreshers.Add(() => text.Text = read());

            return text;
        }

        /// <summary>Adds a line that follows the settings without being one.</summary>
        /// <param name="read">Reads the text.</param>
        protected TextBlock AddFollowingNote(Func<string> read)
        {
            var text = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0.0, 0.0, 0.0, 10.0),
            };

            Children.Add(text);
            _refreshers.Add(() => text.Text = read());

            return text;
        }

        /// <summary>Formats a frequency for a row.</summary>
        protected static string Frequency(double hertz) => EngineeringText.Frequency(hertz);

        /// <summary>Parses a frequency typed into a row.</summary>
        protected static double? ParseFrequency(string text)
        {
            double parsed;

            return EngineeringText.TryParseFrequency(text, out parsed) ? parsed : (double?)null;
        }

        /// <summary>Formats an interval for a row.</summary>
        protected static string Time(double seconds) => EngineeringText.Time(seconds);

        /// <summary>Parses an interval typed into a row, with an optional trailing "s".</summary>
        protected static double? ParseTime(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return null;
            }

            string trimmed = text.Trim();

            if (trimmed.EndsWith("s", StringComparison.OrdinalIgnoreCase) && trimmed.Length > 1)
            {
                trimmed = trimmed.Substring(0, trimmed.Length - 1);
            }

            double parsed;

            return EngineeringText.TryParseFrequency(trimmed, out parsed) ? parsed : (double?)null;
        }

        /// <summary>Formats a plain number for a row.</summary>
        protected static string Plain(double value) =>
            value.ToString("0.####", CultureInfo.CurrentCulture);

        /// <summary>Parses a plain number typed into a row.</summary>
        protected static double? ParsePlain(string text)
        {
            double parsed;

            return double.TryParse(
                text, NumberStyles.Float, CultureInfo.CurrentCulture, out parsed)
                ? parsed
                : (double?)null;
        }

        private void Row(string label, UIElement control, bool counts = true)
        {
            var row = new DockPanel { Margin = new Thickness(0.0, 0.0, 0.0, 8.0) };

            var caption = new TextBlock
            {
                Text = label,
                Width = 170.0,
                VerticalAlignment = VerticalAlignment.Center,
            };

            DockPanel.SetDock(caption, Dock.Left);
            row.Children.Add(caption);
            row.Children.Add(control);

            Children.Add(row);

            if (counts)
            {
                RowCount++;
            }
        }

        private void Apply(Action write)
        {
            try
            {
                write();
                Complain(string.Empty);
            }
            catch (ArgumentOutOfRangeException refused)
            {
                // Reported and not applied. The settings object refused, so nothing moved; saying
                // so beats leaving the entry looking as though it took.
                Complain(refused.Message.Split('\n')[0]);
            }
        }

        private void Complain(string message)
        {
            Note = message ?? string.Empty;

            EventHandler handler = NoteChanged;

            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }
        }

        /// <summary>Raised when the page has something to say about the last entry.</summary>
        public event EventHandler NoteChanged;

        private void OnSettingsChanged(object sender, EventArgs e) => Refresh();
    }
}
