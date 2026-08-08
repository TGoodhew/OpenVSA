using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace OpenVSA.Ui.Toolbars
{
    /// <summary>What the shell supplies for each control of <see cref="ShellToolbars"/>.</summary>
    public interface IShellToolbarBinding
    {
        /// <summary>
        /// Wires up one control.
        /// </summary>
        /// <param name="path">The control's path, such as <c>Control &gt; Restart</c>.</param>
        /// <param name="control">What the requirement says it is.</param>
        /// <param name="created">The control the builder made.</param>
        /// <returns><c>true</c> if the shell wired it up.</returns>
        bool Bind(string path, ToolbarControl control, FrameworkElement created);

        /// <summary>Called when a control is used, before the shell's own handler.</summary>
        /// <param name="path">The control's path.</param>
        void Ran(string path);
    }

    /// <summary>
    /// Builds <c>REQ-UI-063</c>'s six toolbars into the shell's tray.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The same arrangement as the menu bar, for the same reasons: the contents are the
    /// requirement, so they are declared once in <see cref="ShellToolbars"/> and built from there,
    /// and a control that is neither bound nor given a reason throws while the window is being
    /// constructed rather than presenting a user with a button that does nothing.
    /// </para>
    /// <para>
    /// <strong>Grouping is enforced here, not left to the caller.</strong> Marker Tools is a radio
    /// group — "selecting one mouse mode deselects the others" — and the accumulators are a group
    /// that can also be empty. Both are done by the builder, so the shell cannot implement one of
    /// them as five independent toggles by accident.
    /// </para>
    /// </remarks>
    public static class ShellToolbarBuilder
    {
        /// <summary>
        /// Fills a tray with the toolbars <c>REQ-UI-063</c> declares.
        /// </summary>
        /// <param name="tray">The tray to fill; emptied first.</param>
        /// <param name="binding">What the shell supplies for each control.</param>
        /// <exception cref="ArgumentNullException">An argument is null.</exception>
        /// <exception cref="InvalidOperationException">
        /// A control has neither an action nor a reason, or has both.
        /// </exception>
        public static void Build(ToolBarTray tray, IShellToolbarBinding binding) =>
            Build(tray, binding, new ToolbarLayout());

        /// <summary>
        /// Fills a tray with the toolbars as the user has arranged them.
        /// </summary>
        /// <param name="tray">The tray to fill; emptied first.</param>
        /// <param name="binding">What the shell supplies for each control.</param>
        /// <param name="layout">The arrangement (<c>REQ-UI-064</c>).</param>
        /// <exception cref="ArgumentNullException">An argument is null.</exception>
        /// <exception cref="InvalidOperationException">
        /// A control has neither an action nor a reason, or has both.
        /// </exception>
        /// <remarks>
        /// <strong>The layout says where a control is; <see cref="ShellToolbars"/> still says what
        /// it is.</strong> A control keeps the path the requirement declares it under whatever
        /// toolbar it has been moved to, so the shell's binding switch never sees that
        /// customisation happened, and the bound-or-reasoned rule is enforced on a customised
        /// arrangement exactly as on the default one.
        /// </remarks>
        public static void Build(ToolBarTray tray, IShellToolbarBinding binding, ToolbarLayout layout)
        {
            if (tray == null)
            {
                throw new ArgumentNullException(nameof(tray));
            }

            if (binding == null)
            {
                throw new ArgumentNullException(nameof(binding));
            }

            if (layout == null)
            {
                throw new ArgumentNullException(nameof(layout));
            }

            tray.ToolBars.Clear();

            int band = 0;
            int index = 0;

            foreach (ToolbarBar arranged in layout.Bars)
            {
                if (!arranged.IsVisible)
                {
                    continue;
                }

                var bar = new ToolBar
                {
                    Band = band,
                    BandIndex = index,

                    // So a test — and a user reading the customiser — can tell which is which.
                    // The tray gives a toolbar no name of its own.
                    Name = Identifier(arranged.Name),
                    Tag = arranged.Name,
                    ToolTip = arranged.Name,
                };

                var grouped = new Dictionary<string, List<ToggleButton>>(StringComparer.Ordinal);

                foreach (string path in arranged.Controls)
                {
                    ToolbarControl control = ShellToolbars.ControlAt(path);

                    if (control == null || control.Kind == ToolbarControlKind.Separator)
                    {
                        // Either the layout's own separator or a path this build has no control
                        // for — ToolbarLayout reports the second when it reads the file, so a rule
                        // is the better of the two things to draw.
                        bar.Items.Add(new Separator());
                        continue;
                    }

                    bar.Items.Add(Make(path, control, binding, grouped));
                }

                Couple(grouped);

                tray.ToolBars.Add(bar);

                // Two bands: five preconfigured toolbars are more than fits across a 1280-wide
                // window on one row, and a tray that wraps mid-toolbar hides half of one.
                index++;

                if (index == 3)
                {
                    band++;
                    index = 0;
                }
            }
        }

        /// <summary>
        /// A toolbar's name as a XAML identifier.
        /// </summary>
        /// <remarks>
        /// A custom toolbar's name is whatever the user typed, and <see cref="FrameworkElement.Name"/>
        /// throws on anything that is not an identifier — so the punctuation goes and the prefix
        /// guarantees a leading letter whatever is left.
        /// </remarks>
        private static string Identifier(string name)
        {
            var text = new System.Text.StringBuilder("Toolbar");

            foreach (char letter in name ?? string.Empty)
            {
                if (char.IsLetterOrDigit(letter) || letter == '_')
                {
                    text.Append(letter);
                }
            }

            return text.ToString();
        }

        private static FrameworkElement Make(
            string path,
            ToolbarControl control,
            IShellToolbarBinding binding,
            Dictionary<string, List<ToggleButton>> grouped)
        {
            FrameworkElement made = Create(control);

            // Tagged with the name the requirement gives it, so that a test - and the customiser
            // of REQ-UI-064 - can find a control without guessing from its caption. A dropdown and
            // a readout have no caption to guess from.
            made.Tag = control.Name;

            bool bound = binding.Bind(path, control, made);

            if (bound && !control.IsImplemented)
            {
                throw new InvalidOperationException(
                    "The shell binds '" + path + "', but REQ-UI-063's table still gives a reason " +
                    "why it is unavailable: \"" + control.Reason + "\" Remove the reason.");
            }

            if (!bound && control.IsImplemented)
            {
                throw new InvalidOperationException(
                    "'" + path + "' is on REQ-UI-063's toolbar, the shell does not bind it, and " +
                    "the table gives no reason why it is unavailable. Every control must be " +
                    "either enabled and functional or disabled with a reason.");
            }

            made.ToolTip = control.IsImplemented ? control.Tip : control.Reason;

            if (!control.IsImplemented)
            {
                made.IsEnabled = false;

                // Without this the tooltip never appears: WPF suppresses tooltips on disabled
                // elements, which would leave the reason where nobody could read it.
                ToolTipService.SetShowOnDisabled(made, true);
            }
            else if (made is ButtonBase)
            {
                string captured = path;
                ((ButtonBase)made).Click += (sender, e) => binding.Ran(captured);
            }

            var toggle = made as ToggleButton;

            if (toggle != null && control.Group != null)
            {
                List<ToggleButton> group;

                if (!grouped.TryGetValue(control.Group, out group))
                {
                    group = new List<ToggleButton>();
                    grouped[control.Group] = group;
                }

                group.Add(toggle);
            }

            return made;
        }

        /// <summary>
        /// The resource key of the style a toolbar toggle is drawn with, if the host supplies one.
        /// </summary>
        public const string ToggleStyleKey = "OpenVSA.ToolbarToggle";

        private static FrameworkElement Create(ToolbarControl control)
        {
            switch (control.Kind)
            {
                case ToolbarControlKind.Toggle:
                case ToolbarControlKind.Radio:
                    var toggle = new ToggleButton
                    {
                        Content = control.Name,
                        Padding = new Thickness(6.0, 1.0, 6.0, 1.0),
                        Focusable = false,
                    };

                    // Asked for by key, on the control itself. ToolBar.ToggleButtonStyleKey is the
                    // documented way and it does NOT work here: the skin assigns its own style to
                    // the toolbar's children, which beats the ToolBar's key lookup, so the toggles
                    // went on painting a solid accent block. A style set on the element wins.
                    //
                    // A resource reference rather than a lookup, so the style is found wherever the
                    // control ends up and follows a theme change. If the host defines no such
                    // style - a toolbar built in a test, say - nothing is set and the stock
                    // appearance stands.
                    toggle.SetResourceReference(FrameworkElement.StyleProperty, ToggleStyleKey);

                    return toggle;

                case ToolbarControlKind.Split:
                    return new SplitButton(control.Name);

                case ToolbarControlKind.Dropdown:
                    return new ComboBox { MinWidth = 130.0, Focusable = false };

                case ToolbarControlKind.Readout:
                    return new TextBlock
                    {
                        Margin = new Thickness(6.0, 0.0, 6.0, 0.0),
                        VerticalAlignment = VerticalAlignment.Center,
                    };

                default:
                    return new Button
                    {
                        Content = control.Name,
                        Padding = new Thickness(6.0, 1.0, 6.0, 1.0),
                        Focusable = false,
                    };
            }
        }

        /// <summary>
        /// Makes a group of toggles mutually exclusive.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Rather than <see cref="RadioButton"/> with a group name, because a radio group has no
        /// way back to "none" and one of the two groups needs one — the accumulators are a setting
        /// whose fourth value is <em>no accumulator</em>, reached by pressing the chosen one again.
        /// </para>
        /// <para>
        /// The Marker Tools group has no such value: <c>Pointer</c> is the mode that does nothing,
        /// so unchecking the last one selects Pointer instead of leaving no mode at all. Either way
        /// the criterion holds — selecting one deselects the others.
        /// </para>
        /// </remarks>
        private static void Couple(Dictionary<string, List<ToggleButton>> grouped)
        {
            foreach (KeyValuePair<string, List<ToggleButton>> group in grouped)
            {
                List<ToggleButton> members = group.Value;

                foreach (ToggleButton member in members)
                {
                    ToggleButton captured = member;

                    captured.Checked += (sender, e) =>
                    {
                        foreach (ToggleButton other in members)
                        {
                            if (!ReferenceEquals(other, captured))
                            {
                                other.IsChecked = false;
                            }
                        }
                    };
                }
            }
        }
    }
}
