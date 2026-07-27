using System;
using System.Collections.Generic;
using OpenVSA.Measurement.State;

namespace OpenVSA.Ui.Dialogs
{
    /// <summary>
    /// The dialog framework's five global options (<c>REQ-UI-071</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Global, and live.</strong> The requirement says the framework exposes these
    /// "globally", so one instance serves every dialog and <see cref="Changed"/> lets the ones
    /// already on screen follow a change rather than waiting to be reopened — which is
    /// <c>REQ-UI-070</c>'s no-round-trip rule applied to the framework's own settings.
    /// </para>
    /// <para>
    /// <strong>Default Mode and Persist Mode are two different things, and the distinction is the
    /// whole of the fourth criterion.</strong> Default Mode is what a dialog opens in when nothing
    /// is remembered about it. Persist Mode makes each dialog remember the mode <em>it</em> was
    /// closed in, so a user who prefers tabs on top generally but expanders for one long page gets
    /// both. With Persist Mode off, every dialog opens in Default Mode however it was last closed.
    /// </para>
    /// <para>
    /// The remembered modes are kept here rather than in the dialogs because a dialog does not
    /// outlive its window and the memory has to survive a restart.
    /// </para>
    /// </remarks>
    public sealed class DialogFrameworkOptions
    {
        private readonly Dictionary<string, DialogMode> _remembered =
            new Dictionary<string, DialogMode>(StringComparer.OrdinalIgnoreCase);

        private DialogMode _defaultMode = DialogModes.Default;
        private bool _fixedSize = true;
        private bool _keepOnTop;
        private bool _persistMode = true;
        private bool _tabsCollapsedByDefault;

        /// <summary>
        /// How a dialog lays its pages out when nothing is remembered about it.
        /// </summary>
        public DialogMode DefaultMode
        {
            get { return _defaultMode; }

            set
            {
                if (_defaultMode == value)
                {
                    return;
                }

                _defaultMode = value;
                RaiseChanged();
            }
        }

        /// <summary>
        /// Whether a dialog is sized to the largest page it contains.
        /// </summary>
        /// <remarks>
        /// On by default. The option exists because a dialog that resizes as the user moves between
        /// its tabs makes the controls move under the pointer, and the user is comparing the pages,
        /// not measuring them.
        /// </remarks>
        public bool FixedSize
        {
            get { return _fixedSize; }

            set
            {
                if (_fixedSize == value)
                {
                    return;
                }

                _fixedSize = value;
                RaiseChanged();
            }
        }

        /// <summary>
        /// Whether dialogs stay above the main window.
        /// </summary>
        /// <remarks>
        /// Off by default. These dialogs are modeless and live, so they are meant to be left open
        /// while the measurement is watched; one that could never fall behind would cover the trace
        /// it is being used to adjust.
        /// </remarks>
        public bool KeepOnTop
        {
            get { return _keepOnTop; }

            set
            {
                if (_keepOnTop == value)
                {
                    return;
                }

                _keepOnTop = value;
                RaiseChanged();
            }
        }

        /// <summary>
        /// Whether a dialog reopens in the mode it was closed with, across restarts.
        /// </summary>
        /// <remarks>
        /// Turning it off does not forget what has already been remembered — it stops the memory
        /// being consulted. A user who turns it off to compare, then on again, gets their
        /// arrangement back rather than having had it silently discarded.
        /// </remarks>
        public bool PersistMode
        {
            get { return _persistMode; }

            set
            {
                if (_persistMode == value)
                {
                    return;
                }

                _persistMode = value;
                RaiseChanged();
            }
        }

        /// <summary>
        /// Whether "Tabs on left" starts with its tab strip collapsed to initials.
        /// </summary>
        /// <remarks>
        /// Inert under the other three modes, which is what <c>REQ-UI-071</c> says and what its
        /// criterion tests. It is settable regardless: a setting that could only be changed while
        /// the mode it applies to is selected would be unfindable.
        /// </remarks>
        public bool TabsCollapsedByDefault
        {
            get { return _tabsCollapsedByDefault; }

            set
            {
                if (_tabsCollapsedByDefault == value)
                {
                    return;
                }

                _tabsCollapsedByDefault = value;
                RaiseChanged();
            }
        }

        /// <summary>Raised whenever any option changes, so open dialogs can follow.</summary>
        public event EventHandler Changed;

        /// <summary>
        /// The mode a named dialog should open in.
        /// </summary>
        /// <param name="dialog">The dialog's name.</param>
        /// <returns>The remembered mode if there is one and Persist Mode is on, else
        /// <see cref="DefaultMode"/>.</returns>
        public DialogMode ModeFor(string dialog)
        {
            DialogMode remembered;

            return _persistMode && dialog != null && _remembered.TryGetValue(dialog, out remembered)
                ? remembered
                : _defaultMode;
        }

        /// <summary>
        /// Records the mode a named dialog was closed in.
        /// </summary>
        /// <param name="dialog">The dialog's name.</param>
        /// <param name="mode">The mode it was closed in.</param>
        /// <remarks>
        /// Recorded whether or not Persist Mode is on; <see cref="ModeFor"/> is what consults it.
        /// Writing only while the option is on would mean turning it on mid-session had no effect
        /// until every dialog had been opened and closed once.
        /// </remarks>
        public void RememberMode(string dialog, DialogMode mode)
        {
            if (string.IsNullOrEmpty(dialog))
            {
                return;
            }

            _remembered[dialog] = mode;
        }

        /// <summary>Whether a mode has been remembered for a named dialog.</summary>
        /// <param name="dialog">The dialog's name.</param>
        public bool HasRememberedMode(string dialog) =>
            dialog != null && _remembered.ContainsKey(dialog);

        /// <summary>Forgets every remembered mode.</summary>
        public void ForgetModes() => _remembered.Clear();

        /// <summary>
        /// Writes the options into a display-preferences sidecar.
        /// </summary>
        /// <param name="state">The sidecar to write into.</param>
        /// <exception cref="ArgumentNullException"><paramref name="state"/> is null.</exception>
        /// <remarks>
        /// The remembered modes are sorted by dialog name, so a preferences file does not churn its
        /// line order between saves and a diff of two files shows only what actually differs.
        /// </remarks>
        public void SaveInto(DisplayPreferencesState state)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            var saved = new DialogFrameworkState
            {
                DefaultMode = DialogModes.NameOf(_defaultMode),
                FixedSize = _fixedSize,
                KeepOnTop = _keepOnTop,
                PersistMode = _persistMode,
                TabsCollapsedByDefault = _tabsCollapsedByDefault,
            };

            var names = new List<string>(_remembered.Keys);
            names.Sort(StringComparer.Ordinal);

            foreach (string name in names)
            {
                saved.Modes.Add(new DialogModeState
                {
                    Dialog = name,
                    Mode = DialogModes.NameOf(_remembered[name]),
                });
            }

            state.Dialogs = saved;
        }

        /// <summary>
        /// Reads the options back from a display-preferences sidecar.
        /// </summary>
        /// <param name="state">The sidecar to read.</param>
        /// <returns>Names in the file that are not modes, in the order found.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="state"/> is null.</exception>
        /// <remarks>
        /// Unrecognised mode names are returned rather than thrown on, as unknown colour keys are:
        /// a file written by a later version should cost the user the setting it names, not all of
        /// them. Loading does not raise <see cref="Changed"/> — nothing is open yet to follow it,
        /// and a load that announced itself as a change would write the file back out at startup.
        /// </remarks>
        public IReadOnlyList<string> LoadFrom(DisplayPreferencesState state)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            var unknown = new List<string>();
            DialogFrameworkState saved = state.Dialogs;

            if (saved == null)
            {
                return unknown;
            }

            DialogMode mode;

            if (DialogModes.TryParseName(saved.DefaultMode, out mode))
            {
                _defaultMode = mode;
            }
            else
            {
                unknown.Add(saved.DefaultMode ?? string.Empty);
            }

            _fixedSize = saved.FixedSize;
            _keepOnTop = saved.KeepOnTop;
            _persistMode = saved.PersistMode;
            _tabsCollapsedByDefault = saved.TabsCollapsedByDefault;

            _remembered.Clear();

            if (saved.Modes == null)
            {
                return unknown;
            }

            foreach (DialogModeState entry in saved.Modes)
            {
                if (entry == null || string.IsNullOrEmpty(entry.Dialog))
                {
                    continue;
                }

                if (DialogModes.TryParseName(entry.Mode, out mode))
                {
                    _remembered[entry.Dialog] = mode;
                }
                else
                {
                    unknown.Add(entry.Mode ?? string.Empty);
                }
            }

            return unknown;
        }

        private void RaiseChanged()
        {
            EventHandler handler = Changed;

            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }
        }

        /// <inheritdoc />
        public override string ToString() =>
            DialogModes.NameOf(_defaultMode) +
            (_fixedSize ? ", fixed size" : ", sized to page") +
            (_keepOnTop ? ", kept on top" : string.Empty) +
            (_persistMode ? ", mode persisted" : string.Empty);
    }
}
