using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using OpenVSA.Measurement.State;

namespace OpenVSA.Ui.Toolbars
{
    /// <summary>
    /// One toolbar as the user currently has it (<c>REQ-UI-064</c>).
    /// </summary>
    /// <remarks>
    /// <see cref="ShellToolbar"/> is what <c>REQ-UI-063</c> declares; this is what is on screen. The
    /// two are separate types because they have separate lifetimes: the declaration is the
    /// requirement and never changes, and this is edited, saved and reset.
    /// </remarks>
    public sealed class ToolbarBar
    {
        private readonly List<string> _controls = new List<string>();
        private readonly ReadOnlyCollection<string> _readOnly;

        internal ToolbarBar(string name, bool isCustom, bool isCustomisable)
        {
            Name = name;
            IsCustom = isCustom;
            IsCustomisable = isCustomisable;
            _readOnly = new ReadOnlyCollection<string>(_controls);
        }

        /// <summary>The toolbar's name, as the tray and the customiser show it.</summary>
        public string Name { get; internal set; }

        /// <summary>Whether the user created it, rather than <c>REQ-UI-063</c> declaring it.</summary>
        public bool IsCustom { get; }

        /// <summary>
        /// Whether the customiser of <c>REQ-UI-064</c> may edit it.
        /// </summary>
        /// <remarks>
        /// False for the macro-button bar alone, which both <c>REQ-UI-063</c> and
        /// <c>REQ-UI-064</c> put outside the customiser. Carried per bar rather than tested by name
        /// so that the rule is asked of the bar rather than restated wherever it matters.
        /// </remarks>
        public bool IsCustomisable { get; }

        /// <summary>Whether the tray shows it.</summary>
        public bool IsVisible { get; internal set; } = true;

        /// <summary>
        /// What is on it, in order, each by the path it is known by.
        /// </summary>
        /// <remarks>
        /// A control's path is where <c>REQ-UI-063</c> put it — <c>Control &gt; Pause</c> — whatever
        /// toolbar it now sits on. That is deliberate: the path is the control's identity, so moving
        /// a button to a custom bar cannot change what the shell binds to it.
        /// </remarks>
        public IReadOnlyList<string> Controls => _readOnly;

        internal List<string> Mutable => _controls;

        /// <inheritdoc />
        public override string ToString() =>
            Name + " (" + _controls.Count + (IsCustom ? " controls, custom)" : " controls)");
    }

    /// <summary>
    /// The toolbars as the user has arranged them, and every edit <c>REQ-UI-064</c> asks for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>A control's identity is its home path, not its position.</strong> Every control the
    /// customiser can place already exists in <see cref="ShellToolbars"/>, and it keeps the path
    /// declared there wherever it is put. So <see cref="ShellToolbarBuilder"/> asks the shell to
    /// bind <c>Control &gt; Pause</c> whether Pause is on the Control toolbar or on a custom one,
    /// and the shell's binding switch does not know that customisation exists.
    /// </para>
    /// <para>
    /// <strong>A control appears at most once.</strong> The alternative — the same button on two
    /// toolbars — would mean two live controls over one setting, and the shell holds a single
    /// reference to each of the ones it has to keep in step (the Pause caption, the mouse-mode
    /// group, the accumulator group). Placing a control that is somewhere else moves it; that is
    /// what makes "restores the five preconfigured toolbars to their default contents" a
    /// restoration rather than a merge.
    /// </para>
    /// <para>
    /// <strong>The macro-button bar is not here to be edited.</strong> It is in
    /// <see cref="Bars"/>, because it is one of the six the tray shows, and absent from
    /// <see cref="Customisable"/>, which is what the customiser lists and what the picker draws
    /// from. Both directions are asserted.
    /// </para>
    /// </remarks>
    public sealed class ToolbarLayout
    {
        /// <summary>
        /// The entry that stands for a rule between groups.
        /// </summary>
        /// <remarks>
        /// Not a path — a path always contains <see cref="ShellToolbars.PathOf"/>'s separator, so
        /// this can never collide with a control's name. Separators are exempt from the
        /// appears-at-most-once rule: there is nothing behind one to be in two places at once.
        /// </remarks>
        public const string SeparatorPath = "|";

        private readonly List<ToolbarBar> _bars = new List<ToolbarBar>();
        private readonly ReadOnlyCollection<ToolbarBar> _readOnly;

        /// <summary>Creates the layout <c>REQ-UI-063</c> declares.</summary>
        public ToolbarLayout()
        {
            _readOnly = new ReadOnlyCollection<ToolbarBar>(_bars);
            Fill();
        }

        /// <summary>Raised whenever anything about the arrangement changes.</summary>
        public event EventHandler Changed;

        /// <summary>Every toolbar, in the order the tray shows them.</summary>
        public IReadOnlyList<ToolbarBar> Bars => _readOnly;

        /// <summary>
        /// The toolbars the customiser lists — every one but the macro bar (<c>REQ-UI-064</c>).
        /// </summary>
        public IReadOnlyList<ToolbarBar> Customisable
        {
            get
            {
                var listed = new List<ToolbarBar>(_bars.Count);

                foreach (ToolbarBar bar in _bars)
                {
                    if (bar.IsCustomisable)
                    {
                        listed.Add(bar);
                    }
                }

                return listed;
            }
        }

        /// <summary>
        /// Whether this is the arrangement <c>REQ-UI-063</c> declares.
        /// </summary>
        /// <remarks>
        /// What File &gt; Preset &gt; Toolbars returns it to, and what decides whether the display
        /// sidecar carries a toolbar section at all: a user who has never customised anything
        /// should not have today's default arrangement frozen into their preferences file.
        /// </remarks>
        public bool IsDefault
        {
            get
            {
                var reference = new ToolbarLayout();

                if (reference._bars.Count != _bars.Count)
                {
                    return false;
                }

                for (int index = 0; index < _bars.Count; index++)
                {
                    ToolbarBar mine = _bars[index];
                    ToolbarBar theirs = reference._bars[index];

                    if (!string.Equals(mine.Name, theirs.Name, StringComparison.Ordinal) ||
                        mine.IsVisible != theirs.IsVisible ||
                        mine.Controls.Count != theirs.Controls.Count)
                    {
                        return false;
                    }

                    for (int position = 0; position < mine.Controls.Count; position++)
                    {
                        if (!string.Equals(
                            mine.Controls[position],
                            theirs.Controls[position],
                            StringComparison.Ordinal))
                        {
                            return false;
                        }
                    }
                }

                return true;
            }
        }

        /// <summary>The toolbar of that name, or <c>null</c>.</summary>
        /// <param name="name">The toolbar's name.</param>
        public ToolbarBar Find(string name)
        {
            foreach (ToolbarBar bar in _bars)
            {
                if (string.Equals(bar.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return bar;
                }
            }

            return null;
        }

        /// <summary>Which toolbar a control is on, or <c>null</c> if it is on none.</summary>
        /// <param name="path">The control's path.</param>
        public ToolbarBar BarOf(string path)
        {
            if (string.IsNullOrEmpty(path) || string.Equals(path, SeparatorPath, StringComparison.Ordinal))
            {
                return null;
            }

            foreach (ToolbarBar bar in _bars)
            {
                if (bar.Mutable.Contains(path))
                {
                    return bar;
                }
            }

            return null;
        }

        /// <summary>
        /// Creates an empty custom toolbar (<c>REQ-UI-064</c>).
        /// </summary>
        /// <param name="name">What to call it.</param>
        /// <returns>The new toolbar.</returns>
        /// <exception cref="ArgumentException">
        /// The name is blank, or a toolbar of that name already exists.
        /// </exception>
        public ToolbarBar Create(string name)
        {
            string trimmed = (name ?? string.Empty).Trim();

            if (trimmed.Length == 0)
            {
                throw new ArgumentException("A toolbar needs a name.", nameof(name));
            }

            if (Find(trimmed) != null)
            {
                throw new ArgumentException(
                    "There is already a toolbar named '" + trimmed + "'.", nameof(name));
            }

            var bar = new ToolbarBar(trimmed, true, true);

            _bars.Add(bar);
            Announce();

            return bar;
        }

        /// <summary>
        /// Deletes a custom toolbar, returning whatever was on it to where it came from.
        /// </summary>
        /// <param name="bar">The toolbar to delete.</param>
        /// <exception cref="ArgumentNullException"><paramref name="bar"/> is null.</exception>
        /// <exception cref="InvalidOperationException">
        /// It is one of <c>REQ-UI-063</c>'s six, which are not the user's to delete.
        /// </exception>
        /// <remarks>
        /// Returning the controls home rather than discarding them is what keeps a deletion from
        /// costing a user a button they cannot find again: the picker would still offer it, but a
        /// toolbar that quietly emptied itself of Restart is a worse thing to explain than one that
        /// put Restart back where it started.
        /// </remarks>
        public void Delete(ToolbarBar bar)
        {
            if (bar == null)
            {
                throw new ArgumentNullException(nameof(bar));
            }

            if (!bar.IsCustom)
            {
                throw new InvalidOperationException(
                    "'" + bar.Name + "' is one of REQ-UI-063's toolbars. File > Preset > Toolbars " +
                    "returns it to its default contents; it cannot be deleted.");
            }

            foreach (string path in bar.Controls)
            {
                if (!string.Equals(path, SeparatorPath, StringComparison.Ordinal))
                {
                    SendHome(path);
                }
            }

            bar.Mutable.Clear();
            _bars.Remove(bar);

            Announce();
        }

        /// <summary>
        /// Moves a toolbar up or down the list (<c>REQ-UI-064</c>).
        /// </summary>
        /// <param name="bar">The toolbar to move.</param>
        /// <param name="delta">How far, and which way; negative is towards the front.</param>
        /// <returns>Whether it moved.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="bar"/> is null.</exception>
        public bool MoveBar(ToolbarBar bar, int delta)
        {
            if (bar == null)
            {
                throw new ArgumentNullException(nameof(bar));
            }

            int from = _bars.IndexOf(bar);

            if (from < 0)
            {
                return false;
            }

            int to = Clamp(from + delta, 0, _bars.Count - 1);

            if (to == from)
            {
                return false;
            }

            _bars.RemoveAt(from);
            _bars.Insert(to, bar);

            Announce();
            return true;
        }

        /// <summary>
        /// Moves a control up or down its toolbar (<c>REQ-UI-064</c>).
        /// </summary>
        /// <param name="bar">The toolbar the control is on.</param>
        /// <param name="index">Where the control currently sits.</param>
        /// <param name="delta">How far, and which way; negative is towards the left.</param>
        /// <returns>Whether it moved.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="bar"/> is null.</exception>
        public bool MoveControl(ToolbarBar bar, int index, int delta)
        {
            if (bar == null)
            {
                throw new ArgumentNullException(nameof(bar));
            }

            if (index < 0 || index >= bar.Mutable.Count)
            {
                return false;
            }

            int to = Clamp(index + delta, 0, bar.Mutable.Count - 1);

            if (to == index)
            {
                return false;
            }

            string moved = bar.Mutable[index];

            bar.Mutable.RemoveAt(index);
            bar.Mutable.Insert(to, moved);

            Announce();
            return true;
        }

        /// <summary>
        /// Puts a control on a toolbar, taking it off whichever one it was on.
        /// </summary>
        /// <param name="bar">The toolbar to put it on.</param>
        /// <param name="path">The control's path, or <see cref="SeparatorPath"/>.</param>
        /// <param name="index">Where to put it, or a negative number for the end.</param>
        /// <exception cref="ArgumentNullException"><paramref name="bar"/> is null.</exception>
        /// <exception cref="ArgumentException">
        /// The path names no control, or the toolbar is not the customiser's to edit.
        /// </exception>
        public void Place(ToolbarBar bar, string path, int index = -1)
        {
            if (bar == null)
            {
                throw new ArgumentNullException(nameof(bar));
            }

            if (!bar.IsCustomisable)
            {
                throw new ArgumentException(
                    "'" + bar.Name + "' is outside the customiser (REQ-UI-063).", nameof(bar));
            }

            bool separator = string.Equals(path, SeparatorPath, StringComparison.Ordinal);

            if (!separator && !Exists(path))
            {
                throw new ArgumentException(
                    "REQ-UI-063 declares no control at '" + path + "'.", nameof(path));
            }

            if (!separator)
            {
                Detach(path);
            }

            int at = index < 0 || index > bar.Mutable.Count ? bar.Mutable.Count : index;

            bar.Mutable.Insert(at, path);
            Announce();
        }

        /// <summary>
        /// Takes a control off a toolbar, leaving it on none.
        /// </summary>
        /// <param name="bar">The toolbar to take it off.</param>
        /// <param name="index">Where the control sits.</param>
        /// <returns>Whether anything was taken off.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="bar"/> is null.</exception>
        /// <remarks>
        /// A removed control is not lost: it is what the picker offers, on every toolbar, until it
        /// is placed again — and File &gt; Preset &gt; Toolbars puts it back where it belongs.
        /// </remarks>
        public bool Take(ToolbarBar bar, int index)
        {
            if (bar == null)
            {
                throw new ArgumentNullException(nameof(bar));
            }

            if (!bar.IsCustomisable || index < 0 || index >= bar.Mutable.Count)
            {
                return false;
            }

            bar.Mutable.RemoveAt(index);
            Announce();

            return true;
        }

        /// <summary>Whether the tray shows a toolbar.</summary>
        /// <param name="bar">The toolbar.</param>
        /// <param name="visible">Whether to show it.</param>
        /// <exception cref="ArgumentNullException"><paramref name="bar"/> is null.</exception>
        public void SetVisible(ToolbarBar bar, bool visible)
        {
            if (bar == null)
            {
                throw new ArgumentNullException(nameof(bar));
            }

            if (bar.IsVisible == visible)
            {
                return;
            }

            bar.IsVisible = visible;
            Announce();
        }

        /// <summary>
        /// What the control picker offers for one toolbar (<c>REQ-UI-064</c>).
        /// </summary>
        /// <param name="bar">The toolbar being edited, or <c>null</c> for everything placeable.</param>
        /// <remarks>
        /// Every control of every customisable toolbar that is not already on this one, in the order
        /// <c>REQ-UI-063</c> declares them, with a separator at the end — one thing that can be added
        /// as many times as a user wants a rule. The macro bar's contents are absent, because it is
        /// absent from the customiser.
        /// </remarks>
        public IReadOnlyList<string> Picker(ToolbarBar bar)
        {
            var offered = new List<string>();

            foreach (ShellToolbar declared in ShellToolbars.All)
            {
                if (!declared.IsCustomisable)
                {
                    continue;
                }

                foreach (ToolbarControl control in declared.Controls)
                {
                    if (control.Kind == ToolbarControlKind.Separator)
                    {
                        continue;
                    }

                    string path = ShellToolbars.PathOf(declared.Name, control.Name);

                    if (bar == null || !bar.Mutable.Contains(path))
                    {
                        offered.Add(path);
                    }
                }
            }

            offered.Add(SeparatorPath);
            return offered;
        }

        /// <summary>Returns every toolbar to what <c>REQ-UI-063</c> declares (<c>REQ-UI-064</c>).</summary>
        public void Reset()
        {
            Fill();
            Announce();
        }

        /// <summary>What to write into the display sidecar, or an empty list when nothing changed.</summary>
        public List<ToolbarBarState> ToState()
        {
            var written = new List<ToolbarBarState>();

            if (IsDefault)
            {
                return written;
            }

            foreach (ToolbarBar bar in _bars)
            {
                written.Add(new ToolbarBarState
                {
                    Name = bar.Name,
                    IsCustom = bar.IsCustom,
                    IsVisible = bar.IsVisible,
                    Controls = new List<string>(bar.Controls),
                });
            }

            return written;
        }

        /// <summary>
        /// Reads a saved arrangement back (<c>REQ-UI-064</c>: it survives a restart).
        /// </summary>
        /// <param name="saved">What the sidecar holds; an empty list means the defaults.</param>
        /// <returns>The paths the file named that this build does not have.</returns>
        /// <remarks>
        /// <para>
        /// Unknown paths are dropped and reported rather than thrown on, and controls this build has
        /// that the file never mentions are appended to their home toolbars. Between them those two
        /// rules mean a preferences file written by another version costs the user the buttons it
        /// disagrees about, never the whole arrangement and never a control that exists but is
        /// nowhere to be found.
        /// </para>
        /// <para>
        /// A saved name matching one of <c>REQ-UI-063</c>'s is that toolbar, however the file marks
        /// it: a file cannot turn the Control toolbar into a deletable custom one by saying so.
        /// </para>
        /// </remarks>
        public IReadOnlyList<string> LoadFrom(IEnumerable<ToolbarBarState> saved)
        {
            var unknown = new List<string>();

            Fill();

            if (saved == null)
            {
                return unknown;
            }

            var rebuilt = new List<ToolbarBar>();
            var placed = new HashSet<string>(StringComparer.Ordinal);

            foreach (ToolbarBarState state in saved)
            {
                if (state == null || string.IsNullOrEmpty(state.Name))
                {
                    continue;
                }

                ToolbarBar declared = Find(state.Name);
                ToolbarBar bar = declared ?? new ToolbarBar(state.Name.Trim(), true, true);

                if (declared != null)
                {
                    if (rebuilt.Contains(declared))
                    {
                        continue;
                    }

                    declared.Mutable.Clear();
                }

                bar.IsVisible = state.IsVisible;

                if (bar.IsCustomisable && state.Controls != null)
                {
                    foreach (string path in state.Controls)
                    {
                        if (string.Equals(path, SeparatorPath, StringComparison.Ordinal))
                        {
                            bar.Mutable.Add(path);
                            continue;
                        }

                        if (!Exists(path))
                        {
                            unknown.Add(path);
                            continue;
                        }

                        if (placed.Add(path))
                        {
                            bar.Mutable.Add(path);
                        }
                    }
                }
                else if (!bar.IsCustomisable)
                {
                    // The macro bar's contents are REQ-UI-063's, not the file's: it is outside the
                    // customiser, so a file has no business rearranging it.
                    foreach (string path in DefaultControlsOf(bar.Name))
                    {
                        bar.Mutable.Add(path);
                        placed.Add(path);
                    }
                }

                rebuilt.Add(bar);
            }

            // Anything REQ-UI-063 declares that the file left out is put back on its home toolbar,
            // which has to be in the list for that to be possible.
            foreach (ToolbarBar bar in _bars)
            {
                if (!rebuilt.Contains(bar))
                {
                    bar.Mutable.Clear();
                    rebuilt.Add(bar);
                }
            }

            _bars.Clear();
            _bars.AddRange(rebuilt);

            foreach (KeyValuePair<string, ToolbarControl> found in ShellToolbars.AllControls())
            {
                if (!placed.Contains(found.Key))
                {
                    SendHome(found.Key);
                }
            }

            Announce();
            return unknown;
        }

        private static int Clamp(int value, int low, int high) =>
            value < low ? low : (value > high ? high : value);

        private static bool Exists(string path)
        {
            foreach (KeyValuePair<string, ToolbarControl> found in ShellToolbars.AllControls())
            {
                if (string.Equals(found.Key, path, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        private static IEnumerable<string> DefaultControlsOf(string toolbar)
        {
            foreach (ShellToolbar declared in ShellToolbars.All)
            {
                if (!string.Equals(declared.Name, toolbar, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                foreach (ToolbarControl control in declared.Controls)
                {
                    yield return control.Kind == ToolbarControlKind.Separator
                        ? SeparatorPath
                        : ShellToolbars.PathOf(declared.Name, control.Name);
                }
            }
        }

        private void Fill()
        {
            _bars.Clear();

            foreach (ShellToolbar declared in ShellToolbars.All)
            {
                var bar = new ToolbarBar(declared.Name, false, declared.IsCustomisable);

                foreach (string path in DefaultControlsOf(declared.Name))
                {
                    bar.Mutable.Add(path);
                }

                _bars.Add(bar);
            }
        }

        /// <summary>Takes a control off whichever toolbar has it.</summary>
        private void Detach(string path)
        {
            foreach (ToolbarBar bar in _bars)
            {
                bar.Mutable.Remove(path);
            }
        }

        /// <summary>
        /// Puts a control back on the toolbar <c>REQ-UI-063</c> declares it on.
        /// </summary>
        /// <remarks>
        /// At the end rather than at its declared position. Restoring the order as well would need
        /// the whole toolbar rebuilt, and that is what File &gt; Preset &gt; Toolbars is for; this
        /// is the weaker guarantee that nothing goes missing.
        /// </remarks>
        private void SendHome(string path)
        {
            int mark = path.IndexOf(" > ", StringComparison.Ordinal);

            if (mark <= 0)
            {
                return;
            }

            ToolbarBar home = Find(path.Substring(0, mark));

            if (home != null && !home.Mutable.Contains(path))
            {
                home.Mutable.Add(path);
            }
        }

        private void Announce() => Changed?.Invoke(this, EventArgs.Empty);
    }
}
