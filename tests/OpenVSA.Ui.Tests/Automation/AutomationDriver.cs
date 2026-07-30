using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace OpenVSA.Ui.Tests.Automation
{
    /// <summary>
    /// Thrown when a smoke-test step fails, carrying the step's name and the visual tree
    /// (<c>REQ-TST-008</c>).
    /// </summary>
    /// <remarks>
    /// The criterion asks that "a failure identifies the step and captures the visual tree at that
    /// point rather than reporting only a timeout". Both are in the message, because a test runner
    /// reports a message and nothing else: an attachment written to disk on a CI runner is an
    /// artefact somebody has to know to go and look for.
    /// </remarks>
    [Serializable]
    public class AutomationStepException : Exception
    {
        /// <summary>Creates the exception.</summary>
        public AutomationStepException()
        {
            Step = string.Empty;
            VisualTree = string.Empty;
        }

        /// <summary>Creates the exception with a message.</summary>
        /// <param name="message">What failed.</param>
        public AutomationStepException(string message)
            : base(message)
        {
            Step = string.Empty;
            VisualTree = string.Empty;
        }

        /// <summary>Creates the exception with a message and a cause.</summary>
        /// <param name="message">What failed.</param>
        /// <param name="inner">The underlying failure.</param>
        public AutomationStepException(string message, Exception inner)
            : base(message, inner)
        {
            Step = string.Empty;
            VisualTree = string.Empty;
        }

        /// <summary>Creates the exception naming the step and carrying the tree.</summary>
        /// <param name="step">The step that failed.</param>
        /// <param name="visualTree">The visual tree at that point.</param>
        /// <param name="inner">The underlying failure.</param>
        public AutomationStepException(string step, string visualTree, Exception inner)
            : base(
                "Step '" + step + "' failed: " +
                (inner == null ? "no reason given" : inner.Message) +
                Environment.NewLine + Environment.NewLine +
                "Visual tree at that point:" + Environment.NewLine + visualTree,
                inner)
        {
            Step = step ?? string.Empty;
            VisualTree = visualTree ?? string.Empty;
        }

        /// <summary>Deserialisation constructor.</summary>
        /// <param name="info">Serialisation data.</param>
        /// <param name="context">Streaming context.</param>
        protected AutomationStepException(
            System.Runtime.Serialization.SerializationInfo info,
            System.Runtime.Serialization.StreamingContext context)
            : base(info, context)
        {
            Step = string.Empty;
            VisualTree = string.Empty;
        }

        /// <summary>The step that failed.</summary>
        public string Step { get; }

        /// <summary>The visual tree as it was when the step failed.</summary>
        public string VisualTree { get; }
    }

    /// <summary>
    /// Drives a shell through WPF's automation peers (<c>REQ-TST-008</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Through the automation layer, not through the view models.</strong> Every action here
    /// goes through the <see cref="AutomationPeer"/> a control publishes and the provider interface
    /// it implements — <see cref="IInvokeProvider"/>, <see cref="IValueProvider"/>,
    /// <see cref="ISelectionItemProvider"/>, <see cref="IToggleProvider"/>. That is the same path a
    /// screen reader or a UI Automation client takes, so a control whose command was never bound, or
    /// whose peer does not implement the pattern it appears to offer, fails here. Calling the click
    /// handler directly would pass in both cases, which is what the criterion is guarding against.
    /// </para>
    /// <para>
    /// <strong>In process, and deliberately.</strong> The peers are WPF's own automation layer, and
    /// driving them in process is what makes the real visual tree available to capture when a step
    /// fails — from another process there is no visual tree to capture, only the automation tree the
    /// server chose to expose. The cross-process client path is exercised separately by the cold-start
    /// measurement of <c>REQ-NFR-025</c>, which launches the shell as a process and drives it through
    /// <c>System.Windows.Automation</c>; between the two, both halves are covered.
    /// </para>
    /// <para>
    /// <strong>A pattern a control does not implement is a failure, not a fallback.</strong> There is
    /// no "if the peer will not do it, set the property instead" path anywhere here — that would be
    /// the suite quietly turning into the direct calls it exists to avoid.
    /// </para>
    /// <para>
    /// <strong>Every action is asynchronous, and that is WPF's design rather than a quirk.</strong>
    /// A peer does not perform the action on the calling thread: <c>ButtonAutomationPeer</c> and
    /// <c>MenuItemAutomationPeer</c> both <c>BeginInvoke</c> the click at
    /// <see cref="DispatcherPriority.Input"/> so that an automation client cannot re-enter the
    /// application from inside its own message handling. A test body running as one dispatcher
    /// operation is <em>above</em> Input priority, so the queued click cannot run until the body
    /// finishes — every assertion in it would read the shell as it was before the button was
    /// pressed. That is exactly what the first version of this suite did, and the symptom was a
    /// press that "did nothing" where the same button raising <c>Click</c> directly worked. So every
    /// action here ends by pumping the dispatcher, which is what a real message loop does and what
    /// makes the peer path testable at all.
    /// </para>
    /// </remarks>
    internal sealed class AutomationDriver
    {
        private readonly ShellWindow _shell;
        private readonly List<string> _steps = new List<string>();

        internal AutomationDriver(ShellWindow shell)
        {
            if (shell == null)
            {
                throw new ArgumentNullException(nameof(shell));
            }

            _shell = shell;
        }

        /// <summary>The steps run so far, in order, so a report can say how far it got.</summary>
        internal IReadOnlyList<string> Steps => _steps;

        /// <summary>
        /// Runs a named step, turning any failure into one that names it and carries the tree.
        /// </summary>
        /// <param name="name">What the step does, in words.</param>
        /// <param name="body">The step.</param>
        /// <exception cref="AutomationStepException">The step failed.</exception>
        internal void Step(string name, Action body)
        {
            _steps.Add(name);

            try
            {
                body();
            }
            catch (Exception failure)
            {
                throw new AutomationStepException(name, VisualTree(), failure);
            }
        }

        /// <summary>
        /// Runs the dispatcher until everything the last action queued has run.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The nested-frame idiom, posted below <see cref="DispatcherPriority.Input"/>: pushing a
        /// frame lets queued operations run even though the test body is itself a dispatcher
        /// operation, and the sentinel at <see cref="DispatcherPriority.ContextIdle"/> cannot run
        /// until everything at Input and Background priority already has.
        /// </para>
        /// <para>
        /// This is not a way round the automation layer. It performs no action and touches no
        /// control; it lets the application respond to the action the peer already queued, which is
        /// the one thing a test that owns the dispatcher thread has to do for itself.
        /// </para>
        /// </remarks>
        internal void Settle()
        {
            var frame = new DispatcherFrame();

            _shell.Dispatcher.BeginInvoke(
                DispatcherPriority.ContextIdle,
                new Action(() => frame.Continue = false));

            Dispatcher.PushFrame(frame);
        }

        /// <summary>
        /// Pumps until a condition holds, for work that finishes off the dispatcher thread.
        /// </summary>
        /// <param name="satisfied">The condition to wait for.</param>
        /// <param name="what">What is being waited for, for the failure message.</param>
        /// <param name="seconds">How long to wait before giving up.</param>
        /// <exception cref="TimeoutException">The condition did not hold in time.</exception>
        /// <remarks>
        /// <see cref="Settle"/> is enough for an action whose whole effect is queued on this thread.
        /// Selecting a front end is not: it awaits a connection on a worker, and the continuation
        /// cannot be posted back until that worker finishes. The timeout is what stops such a step
        /// hanging the suite, and it names what it was waiting for — the criterion's "rather than
        /// reporting only a timeout" applies to this message as much as to any other.
        /// </remarks>
        internal void SettleUntil(Func<bool> satisfied, string what, double seconds = 20.0)
        {
            if (satisfied == null)
            {
                throw new ArgumentNullException(nameof(satisfied));
            }

            Stopwatch waited = Stopwatch.StartNew();

            while (!satisfied())
            {
                if (waited.Elapsed.TotalSeconds > seconds)
                {
                    throw new TimeoutException(
                        "Waited " + seconds.ToString("0.#") + " s for " + what +
                        ", and it never happened.");
                }

                Settle();

                // The dispatcher queue is empty at this point and the work is on a worker thread,
                // so without this the loop is a spin that starves nothing but burns a core.
                Thread.Sleep(5);
            }
        }

        /// <summary>
        /// Invokes a control through <see cref="IInvokeProvider"/>.
        /// </summary>
        /// <param name="element">The control.</param>
        /// <exception cref="InvalidOperationException">Its peer does not support invoking.</exception>
        internal void Invoke(UIElement element)
        {
            var provider = ProviderOf<IInvokeProvider>(element, "Invoke");

            provider.Invoke();
            Settle();
        }

        /// <summary>
        /// Types into a control through <see cref="IValueProvider"/>.
        /// </summary>
        /// <param name="element">The control.</param>
        /// <param name="value">The text to set.</param>
        /// <exception cref="InvalidOperationException">Its peer does not support setting a value.</exception>
        internal void SetValue(UIElement element, string value)
        {
            var provider = ProviderOf<IValueProvider>(element, "Value");

            if (provider.IsReadOnly)
            {
                throw new InvalidOperationException(
                    Describe(element) + " is read-only through automation.");
            }

            provider.SetValue(value);
            Settle();
        }

        /// <summary>Reads a control's value through <see cref="IValueProvider"/>.</summary>
        /// <param name="element">The control.</param>
        internal string ValueOf(UIElement element) =>
            ProviderOf<IValueProvider>(element, "Value").Value;

        /// <summary>
        /// Selects an item of a list through <see cref="ISelectionItemProvider"/>.
        /// </summary>
        /// <param name="list">The list.</param>
        /// <param name="index">Which item.</param>
        /// <exception cref="InvalidOperationException">The item cannot be selected through automation.</exception>
        /// <remarks>
        /// Through the <em>item's</em> peer, which is where the pattern lives: a list's own peer
        /// offers <c>Selection</c>, and it is each item that offers <c>SelectionItem</c>. Setting
        /// <c>SelectedIndex</c> instead would skip the item peers entirely, and a list whose items
        /// publish no peer would still pass.
        /// </remarks>
        internal void SelectItem(ItemsControl list, int index)
        {
            if (list == null)
            {
                throw new ArgumentNullException(nameof(list));
            }

            AutomationPeer listPeer = Peer(list);

            // A closed drop-down has generated no containers, so it publishes no item peers at all
            // and there is nothing to select. Opening it first is not a workaround: it is the order
            // an automation client goes in, and a list that cannot be opened through the pattern is
            // a list a client cannot choose from either.
            var openable = listPeer.GetPattern(PatternInterface.ExpandCollapse)
                as IExpandCollapseProvider;

            if (openable != null && openable.ExpandCollapseState != ExpandCollapseState.Expanded)
            {
                openable.Expand();
                Settle();
            }

            List<ISelectionItemProvider> items = SelectableItemsUnder(listPeer);

            if (index < 0 || index >= items.Count)
            {
                throw new InvalidOperationException(
                    Describe(list) + " publishes " + items.Count +
                    " selectable item peers, so item " + index + " cannot be selected.");
            }

            items[index].Select();
            Settle();

            // Closed again, so the shell is left as a user would leave it rather than with a
            // drop-down standing open over the next step.
            if (openable != null && openable.ExpandCollapseState == ExpandCollapseState.Expanded)
            {
                openable.Collapse();
                Settle();
            }
        }

        /// <summary>
        /// The peers under a list that offer <c>SelectionItem</c>, in the order they appear.
        /// </summary>
        /// <param name="listPeer">The list's peer.</param>
        /// <remarks>
        /// A list's children are not always its items. A <c>ComboBox</c> in the toolbar embedded in a
        /// menu publishes the popup's own scrolling chrome as its child, with the item peers below
        /// that, where the same control in the settings pane publishes the items directly. Collecting
        /// what actually offers the pattern is what makes one method work for both — and it is still
        /// the item peers being selected, not the list's <c>SelectedIndex</c>.
        /// </remarks>
        private static List<ISelectionItemProvider> SelectableItemsUnder(AutomationPeer listPeer)
        {
            var found = new List<ISelectionItemProvider>();

            Collect(listPeer, found);

            return found;
        }

        private static void Collect(AutomationPeer peer, List<ISelectionItemProvider> found)
        {
            foreach (AutomationPeer child in peer.GetChildren() ?? new List<AutomationPeer>())
            {
                var selectable = child.GetPattern(PatternInterface.SelectionItem)
                    as ISelectionItemProvider;

                if (selectable != null)
                {
                    found.Add(selectable);
                }
                else
                {
                    Collect(child, found);
                }
            }
        }

        /// <summary>
        /// Closes any menu left open, through <see cref="IExpandCollapseProvider"/>.
        /// </summary>
        /// <remarks>
        /// <strong>Not tidiness — a shell closed with a menu still open hangs the next one.</strong>
        /// An open WPF menu puts the thread into menu mode, and menu mode outlives the window: the
        /// shell closes, the mode is never popped, and the next shell built on the same
        /// single-threaded-apartment thread never finishes being shown. Every test in this suite
        /// shares one thread (see <see cref="ShellHost"/>), so the symptom is not the test that left
        /// the menu open but the one after it, hanging with no message at all. That is exactly what
        /// happened, and only in a full run: each test passed on its own.
        /// </remarks>
        internal void CloseMenus()
        {
            foreach (object candidate in _shell.MenuBar.Items)
            {
                var top = candidate as MenuItem;

                if (top == null)
                {
                    continue;
                }

                var openable = Peer(top).GetPattern(PatternInterface.ExpandCollapse)
                    as IExpandCollapseProvider;

                if (openable != null &&
                    openable.ExpandCollapseState == ExpandCollapseState.Expanded)
                {
                    openable.Collapse();
                    Settle();
                }
            }
        }

        /// <summary>Toggles a control through <see cref="IToggleProvider"/>.</summary>
        /// <param name="element">The control.</param>
        /// <remarks>
        /// The one that matters most in this shell: a <c>ToggleButton</c>'s peer implements
        /// <c>Toggle</c> by setting <c>IsChecked</c>, which raises <c>Checked</c>/<c>Unchecked</c> and
        /// <strong>never raises <c>Click</c></strong>. Every toolbar toggle here was once bound to
        /// <c>Click</c> alone and did nothing at all for an automation client.
        /// </remarks>
        internal void Toggle(UIElement element)
        {
            ProviderOf<IToggleProvider>(element, "Toggle").Toggle();
            Settle();
        }

        /// <summary>
        /// Expands a menu through <see cref="IExpandCollapseProvider"/>.
        /// </summary>
        /// <param name="item">The menu item.</param>
        /// <remarks>
        /// Submenus are filled on open, so an item inside one does not exist until its parent has
        /// been expanded — expanding through the pattern rather than setting <c>IsSubmenuOpen</c> is
        /// what makes that the same sequence a client would perform.
        /// </remarks>
        internal void Expand(MenuItem item)
        {
            ProviderOf<IExpandCollapseProvider>(item, "ExpandCollapse").Expand();
            Settle();
        }

        /// <summary>
        /// Walks the menu bar to an entry, expanding each header on the way through
        /// <see cref="IExpandCollapseProvider"/>.
        /// </summary>
        /// <param name="path">The captions, outermost first, matched by prefix.</param>
        /// <returns>The entry at the end of the path.</returns>
        /// <exception cref="InvalidOperationException">The path does not lead anywhere.</exception>
        /// <remarks>
        /// <para>
        /// A path rather than a search, because a search finds the wrong item: <c>File ▸ Save ▸
        /// Setup</c> and <c>File ▸ Recall ▸ Setup</c> are both called "Setup", and a walk that looked
        /// for the first "Setup" under File would save when it was asked to recall — and pass,
        /// because saving also produces a file.
        /// </para>
        /// <para>
        /// Expanding as it goes is what fills the dynamic submenus. The instrument list is built on
        /// <c>SubmenuOpened</c>, so its entries do not exist until the header has been opened; going
        /// through the pattern gets them for the same reason a user pointing at the menu does.
        /// </para>
        /// </remarks>
        internal MenuItem MenuPath(params string[] path)
        {
            if (path == null || path.Length == 0)
            {
                throw new ArgumentException("A menu path needs at least one caption.", nameof(path));
            }

            ItemCollection level = _shell.MenuBar.Items;
            MenuItem found = null;

            for (int depth = 0; depth < path.Length; depth++)
            {
                found = Entry(level, path[depth], path, depth);

                if (depth < path.Length - 1)
                {
                    Expand(found);
                    level = found.Items;
                }
            }

            return found;
        }

        /// <summary>Whether a menu path leads to an entry.</summary>
        /// <param name="path">The captions, outermost first.</param>
        internal bool HasMenuPath(params string[] path)
        {
            try
            {
                MenuPath(path);
                return true;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        private static MenuItem Entry(
            ItemCollection level, string caption, IEnumerable<string> path, int depth)
        {
            var offered = new List<string>();

            foreach (object candidate in level)
            {
                var item = candidate as MenuItem;

                if (item == null)
                {
                    continue;
                }

                string name = ShellMenus.NameOf(item.Header as string);

                offered.Add(name);

                // Prefix, so an ellipsis need not be spelled out in every test that opens a dialog.
                if (name.StartsWith(caption, StringComparison.Ordinal))
                {
                    return item;
                }
            }

            throw new InvalidOperationException(
                "The menu path " + string.Join(" ▸ ", path) + " stops at '" + caption +
                "': that level offers " +
                (offered.Count == 0 ? "no entries at all" : string.Join(", ", offered)) +
                ". (Depth " + depth + ".)");
        }

        /// <summary>
        /// The first descendant of the shell with an automation id.
        /// </summary>
        /// <param name="automationId">The id.</param>
        /// <returns>The element.</returns>
        /// <exception cref="InvalidOperationException">Nothing has that id.</exception>
        internal T ById<T>(string automationId)
            where T : UIElement
        {
            foreach (DependencyObject node in Descendants(_shell))
            {
                var candidate = node as T;

                if (candidate != null &&
                    string.Equals(
                        AutomationProperties.GetAutomationId(candidate),
                        automationId,
                        StringComparison.Ordinal))
                {
                    return candidate;
                }
            }

            throw new InvalidOperationException(
                "No " + typeof(T).Name + " has the automation id '" + automationId + "'.");
        }

        /// <summary>
        /// The first descendant control whose automation name matches.
        /// </summary>
        /// <param name="name">The name, matched by prefix so an ellipsis need not be spelled.</param>
        /// <returns>The element.</returns>
        /// <exception cref="InvalidOperationException">Nothing carries that name.</exception>
        /// <remarks>
        /// Matched on the peer's own name rather than on <c>Content</c> or <c>Header</c>, because the
        /// name is what an automation client sees. A control whose caption is set in a way the peer
        /// does not surface is a control an automation client cannot find, and this finds it in
        /// exactly the cases a client would.
        /// </remarks>
        internal T ByName<T>(string name)
            where T : UIElement
        {
            foreach (DependencyObject node in Descendants(_shell))
            {
                var candidate = node as T;

                if (candidate == null)
                {
                    continue;
                }

                string peerName = Peer(candidate).GetName() ?? string.Empty;

                if (peerName.StartsWith(name, StringComparison.Ordinal))
                {
                    return candidate;
                }
            }

            throw new InvalidOperationException(
                "No " + typeof(T).Name + " is named '" + name + "' to automation.");
        }

        /// <summary>Whether a control of that automation name is there to be found.</summary>
        /// <param name="name">The name, matched by prefix.</param>
        internal bool HasNamed<T>(string name)
            where T : UIElement
        {
            try
            {
                ByName<T>(name);
                return true;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        /// <summary>
        /// The shell's visual tree, as text (<c>REQ-TST-008</c>).
        /// </summary>
        /// <remarks>
        /// Indented by depth, with each node's type, name and automation id — which is what makes a
        /// failure diagnosable from a CI log alone. Bounded, because a shell's tree runs to thousands
        /// of nodes and a message nobody can read is not much better than a timeout.
        /// </remarks>
        internal string VisualTree(int maximumNodes = 4000)
        {
            var text = new StringBuilder();
            int written = 0;

            Walk(_shell, 0, text, ref written, maximumNodes);

            if (written >= maximumNodes)
            {
                text.AppendLine("… truncated at " + maximumNodes + " nodes.");
            }

            return text.ToString();
        }

        private static void Walk(
            DependencyObject node, int depth, StringBuilder text, ref int written, int maximum)
        {
            if (node == null || written >= maximum)
            {
                return;
            }

            written++;

            var element = node as FrameworkElement;
            string id = element == null
                ? string.Empty
                : AutomationProperties.GetAutomationId(element);

            text.Append(new string(' ', depth * 2));
            text.Append(node.GetType().Name);

            if (element != null && element.Name.Length > 0)
            {
                text.Append(" '" + element.Name + "'");
            }

            if (id.Length > 0)
            {
                text.Append(" [" + id + "]");
            }

            if (element != null && !element.IsVisible)
            {
                text.Append(" (not visible)");
            }

            text.AppendLine();

            // Only a Visual has a visual tree. A logical child such as a ColumnDefinition is a
            // DependencyObject and not a Visual, and asking VisualTreeHelper about one throws --
            // which would turn every captured tree into a second failure hiding the first.
            if (node is Visual || node is System.Windows.Media.Media3D.Visual3D)
            {
                int children = VisualTreeHelper.GetChildrenCount(node);

                for (int i = 0; i < children; i++)
                {
                    Walk(VisualTreeHelper.GetChild(node, i), depth + 1, text, ref written, maximum);
                }
            }

            // The logical tree as well, so an unrealised tab's content and a menu's unopened
            // submenu are in the dump. They are the very places a failing step tends to be.
            foreach (object child in LogicalTreeHelper.GetChildren(node))
            {
                var logical = child as DependencyObject;

                if (logical == null)
                {
                    continue;
                }

                bool alreadyWalked =
                    (logical is Visual || logical is System.Windows.Media.Media3D.Visual3D) &&
                    ReferenceEquals(VisualTreeHelper.GetParent(logical), node);

                if (!alreadyWalked)
                {
                    Walk(logical, depth + 1, text, ref written, maximum);
                }
            }
        }

        private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
        {
            if (root == null)
            {
                yield break;
            }

            yield return root;

            foreach (object child in LogicalTreeHelper.GetChildren(root))
            {
                var node = child as DependencyObject;

                if (node == null)
                {
                    continue;
                }

                foreach (DependencyObject deeper in Descendants(node))
                {
                    yield return deeper;
                }
            }
        }

        private static AutomationPeer Peer(UIElement element)
        {
            if (element == null)
            {
                throw new ArgumentNullException(nameof(element));
            }

            AutomationPeer peer = UIElementAutomationPeer.CreatePeerForElement(element);

            if (peer == null)
            {
                throw new InvalidOperationException(
                    element.GetType().Name + " publishes no automation peer, so no automation " +
                    "client can reach it.");
            }

            return peer;
        }

        private static T ProviderOf<T>(UIElement element, string pattern)
            where T : class
        {
            AutomationPeer peer = Peer(element);

            var provider = peer.GetPattern(PatternFor(pattern)) as T;

            if (provider == null)
            {
                throw new InvalidOperationException(
                    Describe(element) + " does not support the " + pattern +
                    " pattern, so no automation client can drive it.");
            }

            // Said with the control's name on it. WPF's own ElementNotEnabledException carries no
            // clue which element it was, and a settings pane with fourteen rows in it is a long
            // afternoon: this is the same refusal, reported so it can be acted on.
            if (!peer.IsEnabled())
            {
                throw new InvalidOperationException(
                    Describe(element) + " is disabled, so no automation client can drive it. " +
                    WhyDisabled(element));
            }

            return provider;
        }

        private static PatternInterface PatternFor(string pattern)
        {
            switch (pattern)
            {
                case "Invoke": return PatternInterface.Invoke;
                case "Value": return PatternInterface.Value;
                case "Toggle": return PatternInterface.Toggle;
                case "ExpandCollapse": return PatternInterface.ExpandCollapse;
                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(pattern), pattern, "No such automation pattern is driven here.");
            }
        }

        /// <summary>
        /// Whether the control itself is disabled or an ancestor is.
        /// </summary>
        /// <param name="element">The disabled control.</param>
        /// <remarks>
        /// The two have different answers. A disabled ancestor means the whole area is not offered
        /// yet — the settings pane before a front end is connected — and the step needs to arrange
        /// that first. A control disabled on its own is a control the application is deliberately
        /// withholding, and driving it is the wrong thing to want.
        /// </remarks>
        private static string WhyDisabled(UIElement element)
        {
            for (DependencyObject node = LogicalTreeHelper.GetParent(element);
                 node != null;
                 node = LogicalTreeHelper.GetParent(node))
            {
                var ancestor = node as UIElement;

                if (ancestor != null && !ancestor.IsEnabled)
                {
                    return "Its ancestor " + Describe(ancestor) +
                        " is the one that is disabled, so the whole area is not offered yet.";
                }
            }

            return "Nothing above it is disabled, so it is withheld on its own account.";
        }

        private static string Describe(UIElement element)
        {
            var named = element as FrameworkElement;

            return element.GetType().Name +
                (named != null && named.Name.Length > 0 ? " '" + named.Name + "'" : string.Empty);
        }
    }
}
