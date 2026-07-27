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
        /// Fills a tray with the six toolbars.
        /// </summary>
        /// <param name="tray">The tray to fill; emptied first.</param>
        /// <param name="binding">What the shell supplies for each control.</param>
        /// <exception cref="ArgumentNullException">An argument is null.</exception>
        /// <exception cref="InvalidOperationException">
        /// A control has neither an action nor a reason, or has both.
        /// </exception>
        public static void Build(ToolBarTray tray, IShellToolbarBinding binding)
        {
            if (tray == null)
            {
                throw new ArgumentNullException(nameof(tray));
            }

            if (binding == null)
            {
                throw new ArgumentNullException(nameof(binding));
            }

            tray.ToolBars.Clear();

            int band = 0;
            int index = 0;

            foreach (ShellToolbar declared in ShellToolbars.All)
            {
                var bar = new ToolBar
                {
                    Band = band,
                    BandIndex = index,

                    // So a test — and a user reading the customiser — can tell which is which.
                    // The tray gives a toolbar no name of its own.
                    Name = "Toolbar" + declared.Name.Replace(" ", string.Empty).Replace("/", string.Empty),
                    Tag = declared.Name,
                    ToolTip = declared.Name,
                };

                var grouped = new Dictionary<string, List<ToggleButton>>(StringComparer.Ordinal);

                foreach (ToolbarControl control in declared.Controls)
                {
                    if (control.Kind == ToolbarControlKind.Separator)
                    {
                        bar.Items.Add(new Separator());
                        continue;
                    }

                    bar.Items.Add(Make(declared, control, binding, grouped));
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

        private static FrameworkElement Make(
            ShellToolbar declared,
            ToolbarControl control,
            IShellToolbarBinding binding,
            Dictionary<string, List<ToggleButton>> grouped)
        {
            string path = ShellToolbars.PathOf(declared.Name, control.Name);
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

        private static FrameworkElement Create(ToolbarControl control)
        {
            switch (control.Kind)
            {
                case ToolbarControlKind.Toggle:
                case ToolbarControlKind.Radio:
                    return new ToggleButton
                    {
                        Content = control.Name,
                        Padding = new Thickness(6.0, 1.0, 6.0, 1.0),
                        Focusable = false,
                    };

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
