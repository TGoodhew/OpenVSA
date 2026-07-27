using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OpenVSA.Ui.Rendering;

namespace OpenVSA.Ui.Dialogs.Pages
{
    /// <summary>
    /// The Font tab of Display Preferences: the three slots of <c>REQ-UI-080</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Three slots, each set independently.</strong> One row per slot, and each row's
    /// controls write only that slot — which is the criterion, and the thing that a single
    /// "application font" would fail.
    /// </para>
    /// <para>
    /// <strong>The row says what the slot actually resolved to.</strong> A typeface asked for and a
    /// typeface drawn are different things on a machine that does not have it, and a Marker slot
    /// silently drawing a proportional face would break the column alignment of <c>REQ-UI-033</c>
    /// with nothing on screen to say why. The row shows the resolved family and whether it is fixed
    /// pitch, measured from the glyphs.
    /// </para>
    /// <para>
    /// The family list is every family installed, unfiltered. Filtering it to the fixed-pitch ones
    /// for the Marker and Tabular rows was tempting and is wrong: it would silently remove a face a
    /// user has good reason to want, and the resolved-pitch line already tells them what they have
    /// chosen.
    /// </para>
    /// </remarks>
    public sealed class FontPage : StackPanel
    {
        private static readonly double[] Sizes =
        {
            6.0, 7.0, 8.0, 9.0, 10.0, 11.0, 12.0, 14.0, 16.0, 18.0, 20.0, 24.0, 28.0, 36.0, 48.0,
        };

        private readonly FontPreferences _fonts;
        private readonly Dictionary<FontSlot, Row> _rows = new Dictionary<FontSlot, Row>();

        private bool _updating;

        /// <summary>Creates the page over a set of font preferences.</summary>
        /// <param name="fonts">The slots to edit; changed in place.</param>
        /// <exception cref="ArgumentNullException"><paramref name="fonts"/> is null.</exception>
        public FontPage(FontPreferences fonts)
        {
            if (fonts == null)
            {
                throw new ArgumentNullException(nameof(fonts));
            }

            _fonts = fonts;

            Margin = new Thickness(4.0);
            MinWidth = 560.0;

            Children.Add(new TextBlock
            {
                Text = "Each slot applies globally to its own surfaces. Setting one leaves the " +
                       "others alone.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0.0, 0.0, 0.0, 10.0),
            });

            foreach (FontSlot slot in FontPreferences.Slots)
            {
                var row = new Row(slot, this);
                _rows.Add(slot, row);
                Children.Add(row.Panel);
            }

            var reset = new Button
            {
                Content = "Reset all fonts",
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0.0, 8.0, 0.0, 0.0),
                Padding = new Thickness(8.0, 4.0, 8.0, 4.0),
            };

            reset.Click += (sender, e) =>
            {
                _fonts.ResetAll();
                Refresh();
            };

            Children.Add(reset);

            Refresh();
        }

        /// <summary>The slots this page edits.</summary>
        public FontPreferences Fonts => _fonts;

        /// <summary>Raised whenever a slot changes, so the surfaces can follow immediately.</summary>
        public event EventHandler FontsChanged;

        /// <summary>What one row is describing the slot as, resolved family and pitch included.</summary>
        /// <param name="slot">The slot.</param>
        /// <exception cref="ArgumentOutOfRangeException">This page has no row for that slot.</exception>
        public string DescriptionOf(FontSlot slot)
        {
            Row row;

            if (!_rows.TryGetValue(slot, out row))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(slot), slot, "This page has no row for that slot.");
            }

            return row.Description.Text;
        }

        /// <summary>Sets a slot's family from the page, as choosing it in the list would.</summary>
        /// <param name="slot">The slot.</param>
        /// <param name="family">The family to ask for.</param>
        /// <exception cref="ArgumentOutOfRangeException">This page has no row for that slot.</exception>
        public void ChooseFamily(FontSlot slot, string family)
        {
            Row row;

            if (!_rows.TryGetValue(slot, out row))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(slot), slot, "This page has no row for that slot.");
            }

            row.Families.SelectedItem = family;

            // A family the list does not offer — one named in a preferences file written on another
            // machine — still sets the slot, because the slot records what was asked for.
            if (row.Families.SelectedItem == null)
            {
                Apply(slot, family, _fonts.Choice(slot).SizePoints);
            }
        }

        /// <summary>Sets a slot's size from the page, as choosing it in the list would.</summary>
        /// <param name="slot">The slot.</param>
        /// <param name="points">The size in points.</param>
        /// <exception cref="ArgumentOutOfRangeException">This page has no row for that slot.</exception>
        public void ChooseSize(FontSlot slot, double points)
        {
            Row row;

            if (!_rows.TryGetValue(slot, out row))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(slot), slot, "This page has no row for that slot.");
            }

            row.Sizes.SelectedItem = points;

            if (row.Sizes.SelectedItem == null)
            {
                Apply(slot, _fonts.Choice(slot).Family, points);
            }
        }

        private void Refresh()
        {
            _updating = true;

            try
            {
                foreach (KeyValuePair<FontSlot, Row> pair in _rows)
                {
                    FontChoice choice = _fonts.Choice(pair.Key);

                    pair.Value.Families.SelectedItem = choice.Family;
                    pair.Value.Sizes.SelectedItem = choice.SizePoints;
                    pair.Value.Describe(_fonts);
                }
            }
            finally
            {
                _updating = false;
            }
        }

        private void OnRowChanged(FontSlot slot)
        {
            if (_updating)
            {
                return;
            }

            Row row = _rows[slot];

            var family = row.Families.SelectedItem as string;
            object size = row.Sizes.SelectedItem;

            if (family == null || size == null)
            {
                return;
            }

            Apply(slot, family, (double)size);
        }

        private void Apply(FontSlot slot, string family, double points)
        {
            try
            {
                _fonts.Set(slot, new FontChoice(family, points));
            }
            catch (ArgumentException)
            {
                // A blank family or an out-of-range size. Neither is reachable from the lists; this
                // is the backstop for the programmatic entry points above.
                return;
            }

            _rows[slot].Describe(_fonts);

            EventHandler handler = FontsChanged;

            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }
        }

        /// <summary>One slot's row: name, family, size and what it resolved to.</summary>
        private sealed class Row
        {
            internal Row(FontSlot slot, FontPage page)
            {
                Slot = slot;

                Families = new ComboBox { MinWidth = 200.0, Margin = new Thickness(0.0, 0.0, 8.0, 0.0) };

                var names = new List<string>();

                foreach (FontFamily family in System.Windows.Media.Fonts.SystemFontFamilies)
                {
                    names.Add(family.Source);
                }

                names.Sort(StringComparer.CurrentCultureIgnoreCase);

                foreach (string name in names)
                {
                    Families.Items.Add(name);
                }

                Sizes = new ComboBox { MinWidth = 70.0 };

                foreach (double size in FontPage.Sizes)
                {
                    Sizes.Items.Add(size);
                }

                Description = new TextBlock
                {
                    Margin = new Thickness(0.0, 2.0, 0.0, 0.0),
                    TextWrapping = TextWrapping.Wrap,
                };

                var controls = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Margin = new Thickness(0.0, 2.0, 0.0, 0.0),
                };

                controls.Children.Add(Families);
                controls.Children.Add(Sizes);
                controls.Children.Add(new TextBlock
                {
                    Text = "pt",
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(4.0, 0.0, 0.0, 0.0),
                });

                Panel = new StackPanel { Margin = new Thickness(0.0, 0.0, 0.0, 12.0) };

                Panel.Children.Add(new TextBlock
                {
                    Text = FontPreferences.NameOf(slot) + WhatItDraws(slot),
                    FontWeight = FontWeights.Bold,
                });

                Panel.Children.Add(controls);
                Panel.Children.Add(Description);

                Families.SelectionChanged += (sender, e) => page.OnRowChanged(slot);
                Sizes.SelectionChanged += (sender, e) => page.OnRowChanged(slot);
            }

            internal FontSlot Slot { get; }

            internal StackPanel Panel { get; }

            internal ComboBox Families { get; }

            internal ComboBox Sizes { get; }

            internal TextBlock Description { get; }

            internal void Describe(FontPreferences fonts)
            {
                string resolved = fonts.ResolveFamily(Slot);
                bool fixedPitch = FontPreferences.IsFixedPitch(resolved);

                var text = new System.Text.StringBuilder();

                text.Append("Draws with ").Append(resolved).Append(" — ");
                text.Append(fixedPitch ? "fixed pitch" : "proportional");

                if (!string.Equals(
                        resolved, fonts.Choice(Slot).Family, StringComparison.OrdinalIgnoreCase))
                {
                    text.Append("; ").Append(fonts.Choice(Slot).Family)
                        .Append(" is not installed on this machine");
                }

                if (FontPreferences.RequiresFixedPitch(Slot) && !fixedPitch)
                {
                    text.Append(". Columns will not line up in this face.");
                }

                Description.Text = text.ToString();
            }

            private static string WhatItDraws(FontSlot slot)
            {
                switch (slot)
                {
                    case FontSlot.Annotation: return " — trace-window annotation";
                    case FontSlot.Marker: return " — the Markers window";
                    case FontSlot.Tabular: return " — symbol table and error summary";
                }

                return string.Empty;
            }
        }

        /// <inheritdoc />
        public override string ToString() =>
            _fonts.ChangedCount.ToString(CultureInfo.CurrentCulture) + " of 3 font slots changed";
    }
}
