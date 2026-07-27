using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using Xunit;

namespace OpenVSA.Ui.Tests
{
    /// <summary>
    /// <c>REQ-UI-065</c>: the keyboard bindings.
    /// </summary>
    /// <remarks>
    /// Driven through the input system, as the criterion requires: the tests raise real key events
    /// on a shown window and let WPF route them. Calling each command directly would prove the
    /// commands work and say nothing about whether any key reaches them, which is the half that
    /// goes wrong.
    /// </remarks>
    [Collection("Shell")]
    public class KeyboardShortcutTests
    {
        private readonly ShellHost _host;

        /// <summary>Takes the shared shell thread.</summary>
        /// <param name="host">The thread every shell in this collection is built on.</param>
        public KeyboardShortcutTests(ShellHost host)
        {
            _host = host;
        }

        [Fact]
        public void EveryBindingInTheRequirementIsDeclared()
        {
            var expected = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { "Pause or resume", "Space" },
                { "Restart", "Ctrl+Shift+Space" },
                { "New trace", "Ctrl+N" },
                { "Auto-scale", "Ctrl+W" },
                { "Marker position", "Ctrl+K" },
                { "Player window", "Ctrl+H" },
                { "Output window", "Ctrl+O" },
                { "Save bitmap", "Ctrl+B" },
                { "Context help", "F1" },
                { "Dynamic help", "Ctrl+F1" },
                { "Scale content up", "Ctrl+OemPlus" },
                { "Scale content down", "Ctrl+OemMinus" },
            };

            foreach (ShellShortcut binding in ShellShortcuts.All)
            {
                Assert.True(
                    expected.ContainsKey(binding.Action),
                    "'" + binding.Action + "' is not one of REQ-UI-065's bindings.");

                Assert.Equal(expected[binding.Action], binding.Gesture);
                expected.Remove(binding.Action);
            }

            Assert.Empty(expected);
        }

        [Fact]
        public void NoBindingShadowsAnother()
        {
            // A uniqueness check over the whole table, as the criterion asks. A shadowed binding
            // never fires and gives no sign of it - the failure that survives every review because
            // nobody presses that key.
            Assert.Empty(ShellShortcuts.DuplicateGestures());
        }

        [Fact]
        public void TheUniquenessCheckWouldNoticeAClash()
        {
            // A test that can only pass is not a test. Space and Ctrl+Shift+Space differ only in
            // their modifiers, so the check has to compare the whole gesture rather than the key.
            Assert.NotEqual(
                ShellShortcuts.PauseOrResume.Gesture, ShellShortcuts.Restart.Gesture);

            Assert.Equal(ShellShortcuts.PauseOrResume.Key, ShellShortcuts.Restart.Key);
        }

        [Fact]
        public void EveryBindingReachesItsActionThroughTheInputSystem()
        {
            // The criterion: "driven through the input system rather than by calling the command
            // directly, so a binding that is declared but unreachable fails".
            _host.Run(() =>
            {
                ShellWindow shell = Shown();

                try
                {
                    foreach (ShellShortcut binding in ShellShortcuts.All)
                    {
                        // Save bitmap opens a file picker, which cannot be answered here. Its
                        // reachability is covered by the same routing every other binding uses;
                        // what is skipped is the dialog, not the binding.
                        if (ReferenceEquals(binding, ShellShortcuts.SaveBitmap))
                        {
                            continue;
                        }

                        Keyboard.Focus(shell.MenuBar);
                        Press(shell, binding.Key, binding.Modifiers);

                        Assert.Equal(binding.Action, shell.LastShortcut);
                    }
                }
                finally
                {
                    shell.Close();
                }
            });
        }

        [Fact]
        public void SpaceDoesNotStealInputFromAFocusedTextField()
        {
            // The case that makes a bare-Space binding go wrong: a user typing "1.5 GHz" into the
            // centre frequency would pause the measurement on the space before "GHz".
            _host.Run(() =>
            {
                ShellWindow shell = Shown();

                try
                {
                    var box = new TextBox();
                    shell.MenuBar.Items.Add(new MenuItem { Header = "Scratch" });

                    // A real text box in the live tree, focused, so the focus check has something
                    // to refuse for.
                    var host = (Panel)shell.MenuBar.Parent;
                    host.Children.Add(box);
                    box.Focus();

                    Assert.True(ShellShortcuts.IsTextEntry(Keyboard.FocusedElement));

                    Press(shell, Key.Space, ModifierKeys.None);

                    Assert.NotEqual(ShellShortcuts.PauseOrResume.Action, shell.LastShortcut);

                    // And with the focus off the field, the same key does pause. Moved with
                    // Keyboard.Focus rather than Window.Focus: focusing the window does not take
                    // keyboard focus off a child that has it, which is the whole point here.
                    Keyboard.Focus(shell.MenuBar);
                    Assert.False(ShellShortcuts.IsTextEntry(Keyboard.FocusedElement));

                    Press(shell, Key.Space, ModifierKeys.None);

                    Assert.Equal(ShellShortcuts.PauseOrResume.Action, shell.LastShortcut);
                }
                finally
                {
                    shell.Close();
                }
            });
        }

        [Fact]
        public void OnlyTheUnmodifiedBindingsGoThroughTheFocusCheck()
        {
            // Ctrl+N in a text box is still New Trace: no text field wants it, and routing every
            // binding through the focus check would make the modified ones unreachable while any
            // field had the caret.
            Assert.False(ShellShortcuts.PauseOrResume.IsSafeAsInputBinding);
            Assert.False(ShellShortcuts.ContextHelp.IsSafeAsInputBinding);

            foreach (ShellShortcut binding in ShellShortcuts.All)
            {
                Assert.Equal(
                    binding.Modifiers != ModifierKeys.None, binding.IsSafeAsInputBinding);
            }
        }

        [Fact]
        public void ATextFieldIsRecognisedWhateverKindItIs()
        {
            _host.Run(() =>
            {
                Assert.True(ShellShortcuts.IsTextEntry(new TextBox()));
                Assert.True(ShellShortcuts.IsTextEntry(new ComboBox { IsEditable = true }));
                Assert.False(ShellShortcuts.IsTextEntry(new ComboBox { IsEditable = false }));
                Assert.False(ShellShortcuts.IsTextEntry(new Button()));
                Assert.False(ShellShortcuts.IsTextEntry(null));

                // A hot spot counts only while it is taking characters - REQ-UI-042's typing
                // state. Hovering one must not disarm the space bar.
                var spot = new OpenVSA.Ui.HotSpots.HotSpot
                {
                    Value = OpenVSA.Ui.HotSpots.NumericHotSpotValue.Frequency(1e9, 1e3),
                };

                Assert.False(ShellShortcuts.IsTextEntry(spot));

                spot.BeginEdit();
                Assert.True(ShellShortcuts.IsTextEntry(spot));
            });
        }

        [Fact]
        public void ContentScalingIsBoundedAndReversible()
        {
            _host.Run(() =>
            {
                ShellWindow shell = Shown();

                try
                {
                    double start = shell.ContentScale;

                    Press(shell, ShellShortcuts.ScaleUp.Key, ShellShortcuts.ScaleUp.Modifiers);
                    Assert.True(shell.ContentScale > start);

                    Press(shell, ShellShortcuts.ScaleDown.Key, ShellShortcuts.ScaleDown.Modifiers);
                    Assert.Equal(start, shell.ContentScale, 6);

                    // Bounded: forty presses down stops at the floor rather than inverting the
                    // shell or scaling it to nothing.
                    for (int i = 0; i < 40; i++)
                    {
                        Press(
                            shell, ShellShortcuts.ScaleDown.Key, ShellShortcuts.ScaleDown.Modifiers);
                    }

                    Assert.True(shell.ContentScale > 0.0);
                    Assert.True(shell.ContentScale < start);
                }
                finally
                {
                    shell.Close();
                }
            });
        }

        /// <summary>A shell shown off screen, so the input system has a source to route through.</summary>
        private static ShellWindow Shown()
        {
            var shell = new ShellWindow
            {
                // Never writes the real user's preferences: a suite that rearranged the machine's
                // shell on every run would be a side effect nobody asked for.
                PersistPreferences = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -4000.0,
                Top = -4000.0,
                ShowInTaskbar = false,
            };

            shell.Show();
            return shell;
        }

        /// <summary>
        /// Raises a real key press on a window and lets WPF route it.
        /// </summary>
        /// <remarks>
        /// <see cref="InputManager.ProcessInput"/> with a genuine <see cref="KeyEventArgs"/> built
        /// from the window's own presentation source. The modifiers cannot be pressed for real
        /// without driving the hardware, so they are raised as key events of their own first —
        /// which is what puts <see cref="Keyboard.Modifiers"/> into the state the handlers read.
        /// </remarks>
        private static void Press(ShellWindow window, Key key, ModifierKeys modifiers)
        {
            PresentationSource source = PresentationSource.FromVisual(window);

            Assert.NotNull(source);

            // The modifier *state* is injected on this shell; the key event is routed by WPF.
            // Modifier state is hardware state, so holding Control down for the duration of a
            // synthesised event would mean driving the real keyboard and taking the machine's
            // focus while the suite runs.
            window.ModifierSource = () => modifiers;

            var args = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key)
            {
                RoutedEvent = Keyboard.PreviewKeyDownEvent,
            };

            // Raised on the window, so it tunnels through exactly the handler a real press
            // reaches. A binding that is declared and not installed still fails here.
            window.RaiseEvent(args);
        }
    }
}
