using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace OpenVSA.Ui
{
    /// <summary>
    /// One keyboard binding of <c>REQ-UI-065</c>: a command, its gesture and what it does.
    /// </summary>
    public sealed class ShellShortcut
    {
        internal ShellShortcut(string action, Key key, ModifierKeys modifiers)
        {
            Action = action;
            Key = key;
            Modifiers = modifiers;
            Command = new RoutedUICommand(action, action.Replace(" ", string.Empty), typeof(ShellShortcuts));
        }

        /// <summary>What the binding does, as <c>REQ-UI-065</c> writes it.</summary>
        public string Action { get; }

        /// <summary>The key.</summary>
        public Key Key { get; }

        /// <summary>The modifiers held with it.</summary>
        public ModifierKeys Modifiers { get; }

        /// <summary>The command the gesture invokes.</summary>
        public RoutedUICommand Command { get; }

        /// <summary>
        /// Whether this binding is safe to install as a window-level input binding.
        /// </summary>
        /// <remarks>
        /// A bare key with no modifier is not. <c>Space</c> is the requirement's own example and the
        /// case its criterion names: an unmodified input binding on the window fires while a text
        /// box has the caret, so typing a space into a frequency field would pause the measurement.
        /// Those bindings are routed through a preview handler that checks what has focus.
        /// </remarks>
        public bool IsSafeAsInputBinding => Modifiers != ModifierKeys.None;

        /// <summary>The gesture as a user would write it.</summary>
        public string Gesture
        {
            get
            {
                string modifiers = string.Empty;

                if ((Modifiers & ModifierKeys.Control) != 0)
                {
                    modifiers += "Ctrl+";
                }

                if ((Modifiers & ModifierKeys.Shift) != 0)
                {
                    modifiers += "Shift+";
                }

                if ((Modifiers & ModifierKeys.Alt) != 0)
                {
                    modifiers += "Alt+";
                }

                return modifiers + Key;
            }
        }

        /// <inheritdoc />
        public override string ToString() => Gesture + " — " + Action;
    }

    /// <summary>
    /// The keyboard bindings of <c>REQ-UI-065</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The reference product's, adopted verbatim.</strong> They are muscle memory for
    /// existing users and cost nothing to match; the requirement's argument is that simple, and the
    /// only work is not getting them wrong.
    /// </para>
    /// <para>
    /// <strong>Space is the one that goes wrong.</strong> An unmodified input binding on the window
    /// fires while a text box has the caret, so a user typing <c>1.5 GHz</c> into the centre
    /// frequency would pause the measurement on the space before <c>GHz</c>. The requirement names
    /// that case, so the unmodified bindings go through a preview handler that asks what has focus
    /// rather than through <see cref="UIElement.InputBindings"/>.
    /// </para>
    /// <para>
    /// <strong>Uniqueness is a property of the table, not of each entry.</strong> Two commands on
    /// one gesture is a binding that silently never fires, and it is invisible until someone
    /// presses the key. <see cref="DuplicateGestures"/> is what a test asserts is empty.
    /// </para>
    /// </remarks>
    public static class ShellShortcuts
    {
        private static readonly ReadOnlyCollection<ShellShortcut> Bindings = Build();

        /// <summary>Every binding, in the order <c>REQ-UI-065</c> tabulates them.</summary>
        public static IReadOnlyList<ShellShortcut> All => Bindings;

        /// <summary>Pause a running measurement, or resume a paused one.</summary>
        public static ShellShortcut PauseOrResume => Bindings[0];

        /// <summary>Restart, discarding current data including averaging.</summary>
        public static ShellShortcut Restart => Bindings[1];

        /// <summary>Open a new trace window.</summary>
        public static ShellShortcut NewTrace => Bindings[2];

        /// <summary>Auto-scale the active trace's vertical axis.</summary>
        public static ShellShortcut AutoScale => Bindings[3];

        /// <summary>Set the selected marker's position.</summary>
        public static ShellShortcut MarkerPosition => Bindings[4];

        /// <summary>Show the Player window.</summary>
        public static ShellShortcut PlayerWindow => Bindings[5];

        /// <summary>Show the Output window.</summary>
        public static ShellShortcut OutputWindow => Bindings[6];

        /// <summary>Save the active trace as a bitmap.</summary>
        public static ShellShortcut SaveBitmap => Bindings[7];

        /// <summary>Context help.</summary>
        public static ShellShortcut ContextHelp => Bindings[8];

        /// <summary>Dynamic help.</summary>
        public static ShellShortcut DynamicHelp => Bindings[9];

        /// <summary>Scale the window's content up (<c>REQ-NFR-007a</c>).</summary>
        public static ShellShortcut ScaleUp => Bindings[10];

        /// <summary>Scale the window's content down (<c>REQ-NFR-007a</c>).</summary>
        public static ShellShortcut ScaleDown => Bindings[11];

        /// <summary>
        /// Gestures bound to more than one action.
        /// </summary>
        /// <returns>The duplicated gestures, or an empty list.</returns>
        /// <remarks>
        /// Always empty, and a test says so. A shadowed binding is one that never fires and gives no
        /// sign of it — the failure mode that survives every review because nobody presses that key.
        /// </remarks>
        public static IReadOnlyList<string> DuplicateGestures()
        {
            var seen = new Dictionary<string, int>(StringComparer.Ordinal);
            var duplicates = new List<string>();

            foreach (ShellShortcut binding in Bindings)
            {
                int count;
                seen.TryGetValue(binding.Gesture, out count);
                seen[binding.Gesture] = count + 1;

                if (count == 1)
                {
                    duplicates.Add(binding.Gesture);
                }
            }

            return duplicates;
        }

        /// <summary>
        /// Whether an element takes typed characters, and so must keep an unmodified key.
        /// </summary>
        /// <param name="element">The element with focus, or <c>null</c>.</param>
        /// <remarks>
        /// Text boxes and the editable part of a combo box, plus the hot spots of
        /// <c>REQ-UI-042</c> — a hot spot in its typing state is a text field in every way that
        /// matters here, and it is the one on the trace surface itself.
        /// </remarks>
        public static bool IsTextEntry(IInputElement element)
        {
            if (element is TextBoxBase)
            {
                return true;
            }

            var box = element as ComboBox;

            if (box != null && box.IsEditable)
            {
                return true;
            }

            var spot = element as HotSpots.HotSpot;

            return spot != null && spot.IsEditing;
        }

        /// <summary>
        /// Installs the bindings on a window.
        /// </summary>
        /// <param name="window">The window.</param>
        /// <param name="run">Invoked with the binding whose gesture was pressed.</param>
        /// <param name="modifiers">
        /// Where the handler reads the modifier keys from, or <c>null</c> for
        /// <see cref="Keyboard.Modifiers"/>.
        /// </param>
        /// <exception cref="ArgumentNullException"><paramref name="window"/> or <paramref name="run"/> is null.</exception>
        /// <remarks>
        /// <para>
        /// One preview handler over one table, rather than input bindings for some gestures and a
        /// handler for the rest. Two mechanisms would mean two orders of evaluation and two places
        /// for a binding to be shadowed, and the whole reason the unmodified ones need a handler —
        /// the focus check — applies to the table as a rule rather than to those entries as an
        /// exception.
        /// </para>
        /// <para>
        /// The commands are bound as well, so the same actions are reachable from a menu or a
        /// toolbar without a second definition of what they do.
        /// </para>
        /// </remarks>
        public static void Install(
            Window window, Action<ShellShortcut> run, Func<ModifierKeys> modifiers = null)
        {
            if (window == null)
            {
                throw new ArgumentNullException(nameof(window));
            }

            if (run == null)
            {
                throw new ArgumentNullException(nameof(run));
            }

            // Per window, never static. Modifier state is hardware state - WPF reads it from the
            // operating system's keyboard - so a test that raises a routed key event cannot hold
            // Control down for the duration of it without driving the real keyboard and taking the
            // machine's focus while the suite runs. What a test injects is that state, never the
            // routing: the event still tunnels through WPF to the handler a real press reaches.
            // A static seam would be shared between two shells shown at once, which is a test that
            // passes alone and fails in the suite.
            Func<ModifierKeys> held = modifiers ?? (() => Keyboard.Modifiers);

            foreach (ShellShortcut binding in Bindings)
            {
                ShellShortcut captured = binding;

                window.CommandBindings.Add(new CommandBinding(
                    binding.Command, (sender, e) => { run(captured); e.Handled = true; }));
            }

            window.PreviewKeyDown += (sender, e) =>
            {
                if (e.Handled)
                {
                    return;
                }

                ShellShortcut binding = For(e.Key, held());

                if (binding == null)
                {
                    return;
                }

                // Only the unmodified gestures are refused by a text field. Ctrl+N in a text box is
                // still New Trace: no field wants it, and refusing every binding while any field
                // had the caret would make most of the table unreachable.
                if (!binding.IsSafeAsInputBinding && IsTextEntry(Keyboard.FocusedElement))
                {
                    return;
                }

                run(binding);
                e.Handled = true;
            };
        }

        /// <summary>
        /// The binding for a gesture, or <c>null</c> if there is none.
        /// </summary>
        /// <param name="key">The key pressed.</param>
        /// <param name="modifiers">The modifiers held.</param>
        public static ShellShortcut For(Key key, ModifierKeys modifiers)
        {
            foreach (ShellShortcut binding in Bindings)
            {
                if (binding.Key == key && binding.Modifiers == modifiers)
                {
                    return binding;
                }
            }

            return null;
        }

        private static ReadOnlyCollection<ShellShortcut> Build() =>
            new ReadOnlyCollection<ShellShortcut>(new List<ShellShortcut>
            {
                new ShellShortcut("Pause or resume", Key.Space, ModifierKeys.None),
                new ShellShortcut(
                    "Restart", Key.Space, ModifierKeys.Control | ModifierKeys.Shift),
                new ShellShortcut("New trace", Key.N, ModifierKeys.Control),
                new ShellShortcut("Auto-scale", Key.W, ModifierKeys.Control),
                new ShellShortcut("Marker position", Key.K, ModifierKeys.Control),
                new ShellShortcut("Player window", Key.H, ModifierKeys.Control),
                new ShellShortcut("Output window", Key.O, ModifierKeys.Control),
                new ShellShortcut("Save bitmap", Key.B, ModifierKeys.Control),
                new ShellShortcut("Context help", Key.F1, ModifierKeys.None),
                new ShellShortcut("Dynamic help", Key.F1, ModifierKeys.Control),

                // REQ-NFR-007a's content scaling. OemPlus and OemMinus are the unshifted keys on
                // the main row; a user who reaches for the numeric keypad gets Add and Subtract,
                // which are separate keys and would otherwise do nothing.
                new ShellShortcut("Scale content up", Key.OemPlus, ModifierKeys.Control),
                new ShellShortcut("Scale content down", Key.OemMinus, ModifierKeys.Control),
            });
    }
}
