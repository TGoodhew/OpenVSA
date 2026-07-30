using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Threading;

namespace OpenVSA.Soak
{
    /// <summary>
    /// The little of the shell's UI the soak needs to drive, through automation peers.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A cut-down cousin of <c>OpenVSA.Ui.Tests.Automation.AutomationDriver</c>. It is not shared,
    /// because sharing it would mean a product assembly referencing a test one or a third library
    /// existing for two callers — and this needs only two verbs where that needs eight.
    /// </para>
    /// <para>
    /// <strong>No menu is ever opened.</strong> Invoking a control's peer does not require its
    /// containing popup to have been shown, and an open WPF menu puts the thread into menu mode,
    /// which outlives the window it was opened from. Over a night and several hundred cycles that is
    /// a risk with nothing to gain: the soak is measuring the shell, not proving the menus work.
    /// </para>
    /// </remarks>
    internal sealed class ShellDriver
    {
        private readonly Window _shell;
        private readonly Menu _menuBar;

        internal ShellDriver(Window shell, Menu menuBar)
        {
            _shell = shell ?? throw new ArgumentNullException(nameof(shell));
            _menuBar = menuBar ?? throw new ArgumentNullException(nameof(menuBar));
        }

        /// <summary>Runs the dispatcher until everything queued has run.</summary>
        /// <remarks>
        /// A peer queues its action at <see cref="DispatcherPriority.Input"/>, below the priority
        /// this code runs at, so without this nothing an invoke asked for has happened yet.
        /// </remarks>
        internal void Settle()
        {
            var frame = new DispatcherFrame();

            _shell.Dispatcher.BeginInvoke(
                DispatcherPriority.ContextIdle,
                new Action(() => frame.Continue = false));

            Dispatcher.PushFrame(frame);
        }

        /// <summary>Invokes a control through <see cref="IInvokeProvider"/>, then settles.</summary>
        /// <param name="element">The control.</param>
        /// <exception cref="InvalidOperationException">Its peer will not invoke.</exception>
        internal void Invoke(UIElement element)
        {
            AutomationPeer peer = UIElementAutomationPeer.CreatePeerForElement(element)
                ?? throw new InvalidOperationException(
                    element.GetType().Name + " publishes no automation peer.");

            var provider = peer.GetPattern(PatternInterface.Invoke) as IInvokeProvider
                ?? throw new InvalidOperationException(
                    element.GetType().Name + " does not support the Invoke pattern.");

            if (!peer.IsEnabled())
            {
                throw new InvalidOperationException(
                    Describe(element) + " is disabled, so it cannot be invoked.");
            }

            provider.Invoke();
            Settle();
        }

        /// <summary>Walks the menu bar to an entry, without opening anything.</summary>
        /// <param name="path">The captions, outermost first, matched by prefix.</param>
        /// <exception cref="InvalidOperationException">The path leads nowhere.</exception>
        internal MenuItem MenuPath(params string[] path)
        {
            ItemCollection level = _menuBar.Items;
            MenuItem found = null;

            for (int depth = 0; depth < path.Length; depth++)
            {
                found = Entry(level, path[depth], path);

                if (depth < path.Length - 1)
                {
                    level = found.Items;
                }
            }

            return found;
        }

        /// <summary>The toolbar embedded at the head of a top-level menu.</summary>
        /// <param name="menu">The menu's caption.</param>
        internal ToolBar Toolbar(string menu) => (ToolBar)MenuPath(menu).Items[0];

        /// <summary>A captioned control on a toolbar.</summary>
        /// <param name="bar">The toolbar.</param>
        /// <param name="caption">The caption, matched exactly.</param>
        /// <exception cref="InvalidOperationException">Nothing on it carries that caption.</exception>
        internal static UIElement Control(ItemsControl bar, string caption)
        {
            foreach (object child in bar.Items)
            {
                var content = child as ContentControl;

                if (content != null &&
                    string.Equals(content.Content as string, caption, StringComparison.Ordinal))
                {
                    return content;
                }
            }

            throw new InvalidOperationException("'" + caption + "' is not on that toolbar.");
        }

        private static MenuItem Entry(ItemCollection level, string caption, IEnumerable<string> path)
        {
            var offered = new List<string>();

            foreach (object candidate in level)
            {
                var item = candidate as MenuItem;

                if (item == null)
                {
                    continue;
                }

                string name = (item.Header as string ?? string.Empty).Replace("_", string.Empty).Trim();

                offered.Add(name);

                if (name.StartsWith(caption, StringComparison.Ordinal))
                {
                    return item;
                }
            }

            throw new InvalidOperationException(
                "The menu path " + string.Join(" > ", path) + " stops at '" + caption +
                "': that level offers " + string.Join(", ", offered) + ".");
        }

        private static string Describe(UIElement element)
        {
            var named = element as FrameworkElement;

            return element.GetType().Name +
                (named != null && named.Name.Length > 0 ? " '" + named.Name + "'" : string.Empty);
        }
    }
}
