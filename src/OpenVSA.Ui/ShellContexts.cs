using System;
using System.Collections.Generic;
using OpenVSA.Dsp.Spectrum;
using OpenVSA.Measurement.Contexts;
using OpenVSA.Measurement.State;
using OpenVSA.Ui.Layout;
using OpenVSA.Ui.ToolWindows;

namespace OpenVSA.Ui
{
    /// <summary>
    /// The shell's side of measurement contexts (<c>REQ-DAT-010</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// A context is a whole measurement — its setup, its trace windows, its markers — and the point
    /// of having more than one is that two can be live at once against a single acquisition. So the
    /// shell's job here is narrow and specific: show the active context's windows, hide the rest,
    /// point the marker commands at the right set, and tell <see cref="ContextAnalyser"/> which
    /// context the capture session is already computing so it does not compute it twice.
    /// </para>
    /// <para>
    /// <strong>Nothing here decides what a context <em>is</em>.</strong> That lives in
    /// <see cref="MeasurementContextSet"/>, below the UI, for the reason the whole tree is arranged
    /// that way: uniqueness of names, whole-or-nothing recall and the analysis a context performs are
    /// arithmetic and rules, and none of them needs a window to be true.
    /// </para>
    /// </remarks>
    public partial class ShellWindow
    {
        /// <summary>
        /// The measurement contexts this session has (<c>REQ-DAT-010</c>).
        /// </summary>
        /// <remarks>
        /// Exposed rather than hidden behind the four commands below, because <c>REQ-DAT-010</c>
        /// requires contexts to be addressable from the automation API as well as from the UI, and
        /// the API sits above this window.
        /// </remarks>
        public MeasurementContextSet Contexts => _contextSet;

        /// <summary>The context on screen.</summary>
        public MeasurementContext ActiveContext => _contextSet.Active;

        /// <summary>
        /// Adds a context, gives it a trace window of its own and leaves it off screen
        /// (<c>REQ-DAT-010</c>).
        /// </summary>
        /// <param name="name">Its name, or <c>null</c> for the next unused one.</param>
        /// <returns>The new context.</returns>
        /// <exception cref="ArgumentException">The name is already taken.</exception>
        /// <remarks>
        /// <para>
        /// <strong>It starts from what is on screen.</strong> A new context inherits the active one's
        /// setup rather than the factory defaults, because the band being captured is shared: one
        /// acquisition cannot be at two centre frequencies, so a new context that reset the frequency
        /// would be describing a measurement the session is not making. What the user then changes —
        /// the window, the point count, the averaging, the detector — is exactly what two contexts
        /// against one capture session are for.
        /// </para>
        /// <para>
        /// It is not activated. Adding a context is not a request to stop looking at the one in
        /// front of you.
        /// </para>
        /// </remarks>
        public MeasurementContext AddContext(string name = null)
        {
            string chosen = string.IsNullOrWhiteSpace(name) ? _contextSet.UnusedName() : name.Trim();

            MeasurementContext added = _contextSet.Add(chosen, CaptureActiveSetup());

            if (!OpenTrace(added))
            {
                // Every letter is in use. The context still exists and is still saveable; what it
                // has not got is a window, and saying so is better than refusing to make it.
                StatusText.Content =
                    "Context '" + added.Name + "' has no trace window: letters A to Z are all in use.";
            }

            ShowContexts();

            return added;
        }

        /// <summary>
        /// Puts a context on screen (<c>REQ-DAT-010</c>).
        /// </summary>
        /// <param name="context">The context to show; one not in the set is refused.</param>
        /// <returns><c>false</c> when it is not in the set, or is already active.</returns>
        /// <remarks>
        /// <para>
        /// The outgoing context's setup is read out of the controls before anything moves, so
        /// returning to it shows what was left rather than what it was last saved as.
        /// </para>
        /// <para>
        /// <strong>The incoming windows are shown before the outgoing ones are hidden.</strong> The
        /// document area refuses to hide its last visible trace — an empty document area has no way
        /// out of itself — so hiding first would leave one of the outgoing context's windows on
        /// screen underneath the incoming one's.
        /// </para>
        /// </remarks>
        public bool ActivateContext(MeasurementContext context)
        {
            if (context == null ||
                ReferenceEquals(context, _contextSet.Active) ||
                !Holds(context))
            {
                return false;
            }

            MeasurementContext leaving = _contextSet.Active;
            leaving.Setup = CaptureActiveSetup();

            foreach (char trace in context.Traces)
            {
                Documents.SetVisible(trace, true);
            }

            foreach (char trace in leaving.Traces)
            {
                Documents.SetVisible(trace, false);
            }

            _contextSet.Active = context;

            // Each context owns its markers, so this is a change of which set the marker commands and
            // the readout name -- not a change to the contents of either.
            _markers = context.Markers.ForTrace(
                context.Traces.Count > 0 ? context.Traces[0] : 'A');

            if (context.Traces.Count > 0 && Documents.PlotOf(context.ActiveTrace) != null)
            {
                Documents.ActiveTrace = context.ActiveTrace;
            }

            // The pane, without the markers: see ApplySettings.
            ApplySettings(context.Setup, restoreMarkers: false);

            Documents.ApplyLayout(TraceLayoutPreset.TileVisible());

            // The capture session computes the active context's spectrum inline on the pump, so the
            // analyser stops computing the one that has just become active and starts computing the
            // one that has just stopped being.
            _contextAnalyser.Primary = context;

            DrawLatestFrom(context);
            ShowContexts();

            StatusText.Content = "Context '" + context.Name + "'";

            return true;
        }

        /// <summary>
        /// Puts a context on screen by name.
        /// </summary>
        /// <param name="name">The context's name.</param>
        /// <returns><c>false</c> when there is no context of that name, or it is already active.</returns>
        public bool ActivateContext(string name) => ActivateContext(_contextSet[name]);

        /// <summary>
        /// Removes a context and closes its trace windows (<c>REQ-DAT-010</c>).
        /// </summary>
        /// <param name="context">The context to remove.</param>
        /// <returns><c>false</c> when it is not in the set, or is the only one left.</returns>
        public bool RemoveContext(MeasurementContext context)
        {
            if (context == null || !Holds(context) || _contextSet.Count <= 1)
            {
                return false;
            }

            // Away from the removed context first, so its windows are not the visible ones when they
            // are closed -- and so the marker set the shell names is one that still has an owner.
            if (ReferenceEquals(context, _contextSet.Active))
            {
                foreach (MeasurementContext other in _contextSet.Contexts)
                {
                    if (!ReferenceEquals(other, context) && ActivateContext(other))
                    {
                        break;
                    }
                }
            }

            var closing = new List<char>(context.Traces);

            if (!_contextSet.Remove(context))
            {
                return false;
            }

            foreach (char trace in closing)
            {
                // The document area keeps its last window whatever happens; a letter it refuses to
                // close is one the removed context no longer owns either way.
                Documents.RemoveTrace(trace);
            }

            UpdateMarshalFormats();
            Documents.ApplyLayout(TraceLayoutPreset.TileVisible());
            ShowContexts();

            StatusText.Content = "Context '" + context.Name + "' removed";

            return true;
        }

        /// <summary>
        /// Renames a context (<c>REQ-DAT-010</c>).
        /// </summary>
        /// <param name="context">The context to rename.</param>
        /// <param name="name">Its new name.</param>
        /// <returns><c>false</c> when the name is blank or another context has it.</returns>
        /// <remarks>
        /// A failure is reported rather than thrown: a name collision is something a user typed, and
        /// the settings message is where the shell says so everywhere else.
        /// </remarks>
        public bool RenameContext(MeasurementContext context, string name)
        {
            if (context == null || !Holds(context))
            {
                return false;
            }

            try
            {
                _contextSet.Rename(context, name);
            }
            catch (ArgumentException refused)
            {
                StatusText.Content = "Context not renamed";
                SettingsMessage.Text = refused.Message;
                return false;
            }

            ShowContexts();

            return true;
        }

        /// <summary>
        /// The active context's setup alone, as a one-measurement state.
        /// </summary>
        /// <returns>A state naming only the context on screen.</returns>
        /// <remarks>
        /// What a preset is: <c>REQ-STA-005</c> makes a preset one measurement's setup, so saving one
        /// from a two-context session must not write both, and applying one must not reset a context
        /// the user was not looking at.
        /// </remarks>
        internal ApplicationState ActiveContextState()
        {
            ApplicationState state = ApplicationState.Default(_contextSet.Active.Name);

            state.Measurements[0] = CaptureActiveSetup();

            return state;
        }

        /// <summary>
        /// Rewrites a single-measurement state to name the active context.
        /// </summary>
        /// <param name="state">The state to rewrite; returned unchanged if it is not a single one.</param>
        /// <returns>The same state.</returns>
        /// <remarks>
        /// <strong>For presets, never for state files.</strong> A preset is a measurement setup
        /// (<c>REQ-STA-005</c>) and applying one to the context in front of you is the whole point of
        /// it; a state file records which contexts a session had, and <c>REQ-STA-004</c> requires a
        /// mismatch there to be refused rather than reinterpreted. Doing this to a recalled state
        /// would defeat the requirement by making every state match.
        /// </remarks>
        internal ApplicationState ForActiveContext(ApplicationState state)
        {
            if (state != null && state.Measurements.Count == 1)
            {
                state.Measurements[0].ContextName = _contextSet.Active.Name;
            }

            return state;
        }

        /// <summary>
        /// Draws a context's newest frame, if it has one.
        /// </summary>
        /// <param name="context">The context.</param>
        /// <remarks>
        /// How a context that has been analysing off screen appears already measuring the moment it
        /// is activated. Offered to the render marshal rather than drawn directly, so the decimation,
        /// the markers, the limit verdict and the indicators all come from the one path a frame
        /// arriving from the pump takes.
        /// </remarks>
        private void DrawLatestFrom(MeasurementContext context)
        {
            SpectrumFrame latest = context.TakeLatestFrame();

            if (latest == null)
            {
                RefreshMarkers();
                return;
            }

            try
            {
                if (_marshal.Offer(latest))
                {
                    DrawPending();
                }
            }
            finally
            {
                // REQ-NFR-002: the share TakeLatestFrame took. The marshal took its own as it
                // accepted the frame, so giving this one up cannot be what returns the buffer.
                latest.Release();
            }
        }

        /// <summary>
        /// Shows the contexts in the Contexts window (<c>REQ-UI-002</c>, <c>REQ-DAT-010</c>).
        /// </summary>
        /// <remarks>
        /// This is where contexts become visible as objects rather than as an internal list: the
        /// window <c>REQ-UI-002</c> requires by that name lists every context and marks the active
        /// one.
        /// </remarks>
        private void ShowContexts()
        {
            _contexts.Set(_contextSet.Names, _contextSet.Active.Name);

            if (_toolWindows != null)
            {
                _toolWindows.Refresh(ToolWindow.Contexts);
            }
        }

        private bool Holds(MeasurementContext context)
        {
            foreach (MeasurementContext candidate in _contextSet.Contexts)
            {
                if (ReferenceEquals(candidate, context))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
