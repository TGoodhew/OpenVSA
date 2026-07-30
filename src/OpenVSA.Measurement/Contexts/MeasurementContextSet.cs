using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using OpenVSA.Measurement.State;

namespace OpenVSA.Measurement.Contexts
{
    /// <summary>
    /// The measurement contexts a session has, and which of them is active
    /// (<c>REQ-DAT-010</c>, <c>REQ-STA-004</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Names are the identity, and they are unique.</strong> Everything that addresses a
    /// context from outside the session — a saved state, the automation API, the Contexts window —
    /// does it by name, so two contexts sharing one would make a state file that could be recalled
    /// two different ways. Compared ordinally and case-sensitively: a name is a label the user typed,
    /// and folding case would refuse "Spectrum" and "spectrum" as the same context on the strength of
    /// a rule nobody stated.
    /// </para>
    /// <para>
    /// <strong>A set always has at least one context.</strong> Removing the last one would leave a
    /// session with no measurement to configure and no name to recall a state into, so the last
    /// context is not removable — the same reason the document area's last trace window is not
    /// closeable.
    /// </para>
    /// </remarks>
    public sealed class MeasurementContextSet
    {
        /// <summary>The name a session's first context has unless it is given another.</summary>
        public const string DefaultName = "Measurement 1";

        private readonly List<MeasurementContext> _contexts = new List<MeasurementContext>();

        private MeasurementContext _active;

        /// <summary>
        /// Creates a set holding one context.
        /// </summary>
        /// <param name="firstName">Its name, or <c>null</c> for <see cref="DefaultName"/>.</param>
        public MeasurementContextSet(string firstName = null)
        {
            var first = new MeasurementContext(
                string.IsNullOrWhiteSpace(firstName) ? DefaultName : firstName);

            _contexts.Add(first);
            _active = first;
        }

        /// <summary>Raised after a context is added.</summary>
        public event EventHandler<MeasurementContext> Added;

        /// <summary>Raised after a context is removed.</summary>
        public event EventHandler<MeasurementContext> Removed;

        /// <summary>Raised after a context is renamed, carrying the name it had.</summary>
        public event EventHandler<ContextRenamedEventArgs> Renamed;

        /// <summary>Raised after the active context changes.</summary>
        public event EventHandler<MeasurementContext> ActiveChanged;

        /// <summary>The contexts, in the order they were created.</summary>
        public IReadOnlyList<MeasurementContext> Contexts =>
            new ReadOnlyCollection<MeasurementContext>(_contexts);

        /// <summary>The context names, in the order they were created.</summary>
        public IReadOnlyList<string> Names =>
            new ReadOnlyCollection<string>(_contexts.Select(c => c.Name).ToList());

        /// <summary>How many contexts there are.</summary>
        public int Count => _contexts.Count;

        /// <summary>
        /// The context the UI is showing and the commands act on.
        /// </summary>
        /// <exception cref="ArgumentNullException">The value is null.</exception>
        /// <exception cref="ArgumentException">The value is not in this set.</exception>
        public MeasurementContext Active
        {
            get { return _active; }

            set
            {
                if (value == null)
                {
                    throw new ArgumentNullException(nameof(value));
                }

                if (!_contexts.Contains(value))
                {
                    throw new ArgumentException(
                        "Context '" + value.Name + "' is not in this set.", nameof(value));
                }

                if (ReferenceEquals(_active, value))
                {
                    return;
                }

                _active = value;
                Raise(ActiveChanged, value);
            }
        }

        /// <summary>The context of a name, or <c>null</c> when there is none.</summary>
        /// <param name="name">The context name.</param>
        public MeasurementContext this[string name] =>
            _contexts.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.Ordinal));

        /// <summary>Whether a name is taken.</summary>
        /// <param name="name">The candidate name.</param>
        public bool Has(string name) => this[name] != null;

        /// <summary>
        /// Adds a context.
        /// </summary>
        /// <param name="name">Its name, which must not be one already in the set.</param>
        /// <param name="setup">Its setup, or <c>null</c> for the defaults.</param>
        /// <returns>The new context.</returns>
        /// <exception cref="ArgumentException">The name is blank or already taken.</exception>
        public MeasurementContext Add(string name, MeasurementState setup = null)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("A context needs a name.", nameof(name));
            }

            if (Has(name))
            {
                throw new ArgumentException(
                    "There is already a context called '" + name + "'.", nameof(name));
            }

            var context = new MeasurementContext(name, setup);

            _contexts.Add(context);
            Raise(Added, context);

            return context;
        }

        /// <summary>
        /// A name no context has, derived from a stem.
        /// </summary>
        /// <param name="stem">What to call it, or <c>null</c> for "Measurement".</param>
        /// <returns>The stem with the lowest free number after it.</returns>
        /// <remarks>
        /// Numbered from the free slots rather than from the count, so deleting "Measurement 2" of
        /// three and adding again reuses 2 rather than making a second "Measurement 3" — which
        /// <see cref="Add"/> would refuse, and which would be an odd thing for a New Context command
        /// to do.
        /// </remarks>
        public string UnusedName(string stem = null)
        {
            string root = string.IsNullOrWhiteSpace(stem) ? "Measurement" : stem.Trim();

            for (int number = 1; number <= _contexts.Count + 1; number++)
            {
                string candidate = root + " " +
                    number.ToString(System.Globalization.CultureInfo.InvariantCulture);

                if (!Has(candidate))
                {
                    return candidate;
                }
            }

            // Unreachable: there are Count + 1 candidates and at most Count of them can be taken.
            throw new InvalidOperationException("No unused context name was found.");
        }

        /// <summary>
        /// Removes a context, releasing the frame it was holding.
        /// </summary>
        /// <param name="context">The context to remove.</param>
        /// <returns>
        /// <c>false</c> when it is not in the set, or when it is the only one left.
        /// </returns>
        /// <remarks>
        /// Removing the active context moves the selection to another, because "active" is what the
        /// commands act on and pointing that at something no longer in the set is how a command
        /// appears to do nothing.
        /// </remarks>
        public bool Remove(MeasurementContext context)
        {
            if (context == null || _contexts.Count <= 1 || !_contexts.Remove(context))
            {
                return false;
            }

            // REQ-NFR-002: a pooled buffer held by a context nothing will ever display again is a
            // buffer the pool has lost.
            context.ClearFrame();

            if (ReferenceEquals(_active, context))
            {
                _active = _contexts[0];
                Raise(ActiveChanged, _active);
            }

            Raise(Removed, context);

            return true;
        }

        /// <summary>
        /// Renames a context.
        /// </summary>
        /// <param name="context">The context to rename.</param>
        /// <param name="name">Its new name.</param>
        /// <exception cref="ArgumentNullException"><paramref name="context"/> is null.</exception>
        /// <exception cref="ArgumentException">
        /// The context is not in this set, or the name is blank or taken by another context.
        /// </exception>
        /// <remarks>
        /// The setup is renamed with it. A state saved from a context whose setup still carried the
        /// old name would be a state that could not be recalled into the session that wrote it, and
        /// the failure would arrive a week later as a mismatch nobody could account for.
        /// </remarks>
        public void Rename(MeasurementContext context, string name)
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            if (!_contexts.Contains(context))
            {
                throw new ArgumentException(
                    "Context '" + context.Name + "' is not in this set.", nameof(context));
            }

            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("A context needs a name.", nameof(name));
            }

            string trimmed = name.Trim();

            if (string.Equals(context.Name, trimmed, StringComparison.Ordinal))
            {
                return;
            }

            if (Has(trimmed))
            {
                throw new ArgumentException(
                    "There is already a context called '" + trimmed + "'.", nameof(name));
            }

            string was = context.Name;

            context.Name = trimmed;
            context.Setup.ContextName = trimmed;

            EventHandler<ContextRenamedEventArgs> handler = Renamed;

            if (handler != null)
            {
                handler(this, new ContextRenamedEventArgs(context, was));
            }
        }

        /// <summary>
        /// Every context's setup, as one saveable state (<c>REQ-STA-004</c>).
        /// </summary>
        /// <returns>A state naming every context in the set.</returns>
        /// <remarks>
        /// All of them, not just the active one. A session with two contexts whose state file
        /// carried one would recall as a session with one configured measurement and one silently
        /// left at whatever it happened to be — which is the partial application the requirement
        /// exists to prevent, arriving through the save rather than through the recall.
        /// </remarks>
        public ApplicationState Capture()
        {
            var state = new ApplicationState
            {
                WrittenUtc = DateTime.UtcNow.ToString(
                    "yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture),
            };

            state.Measurements.Clear();

            foreach (MeasurementContext context in _contexts)
            {
                context.Setup.ContextName = context.Name;
                state.Measurements.Add(context.Setup);
            }

            return state;
        }

        /// <summary>
        /// Applies a state to this set, or refuses the whole recall (<c>REQ-STA-004</c>).
        /// </summary>
        /// <param name="state">The state to apply.</param>
        /// <exception cref="ArgumentNullException"><paramref name="state"/> is null.</exception>
        /// <exception cref="ContextMismatchException">
        /// The state names a context this set does not have. Nothing has been applied.
        /// </exception>
        /// <remarks>
        /// The whole-or-nothing check is <see cref="StateRecall.Apply"/>'s, done against a dictionary
        /// built from this set, so there is one statement of what a mismatch is and one message
        /// describing it. Contexts the state does not name keep their setups.
        /// </remarks>
        public void Recall(ApplicationState state)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            var setups = new Dictionary<string, MeasurementState>(StringComparer.Ordinal);

            foreach (MeasurementContext context in _contexts)
            {
                setups[context.Name] = context.Setup;
            }

            // Throws before writing anything if a name does not match.
            StateRecall.Apply(state, setups);

            foreach (MeasurementContext context in _contexts)
            {
                context.Setup = setups[context.Name];
            }
        }

        /// <inheritdoc />
        public override string ToString() =>
            _contexts.Count + " context" + (_contexts.Count == 1 ? string.Empty : "s") +
            ", active '" + _active.Name + "'";

        private void Raise<T>(EventHandler<T> handler, T value)
        {
            if (handler != null)
            {
                handler(this, value);
            }
        }
    }

    /// <summary>A rename, and the name that was replaced.</summary>
    public sealed class ContextRenamedEventArgs : EventArgs
    {
        /// <summary>Creates the arguments.</summary>
        /// <param name="context">The context that was renamed.</param>
        /// <param name="previousName">The name it had.</param>
        public ContextRenamedEventArgs(MeasurementContext context, string previousName)
        {
            Context = context;
            PreviousName = previousName ?? string.Empty;
        }

        /// <summary>The context that was renamed.</summary>
        public MeasurementContext Context { get; }

        /// <summary>The name it had.</summary>
        public string PreviousName { get; }
    }
}
