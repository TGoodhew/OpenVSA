using System;

namespace OpenVSA.Measurement
{
    /// <summary>Whether the measurement runs on, or takes one sweep and stops.</summary>
    public enum SweepMode
    {
        /// <summary>Acquire and compute continuously.</summary>
        Continuous = 0,

        /// <summary>One sweep at a time, on request.</summary>
        Single,
    }

    /// <summary>What a press of Pause asks the measurement to do next.</summary>
    public enum SweepAction
    {
        /// <summary>Nothing: there is no measurement to control.</summary>
        None = 0,

        /// <summary>Begin acquiring.</summary>
        Start,

        /// <summary>Stop acquiring, keeping what is on the trace.</summary>
        Pause,

        /// <summary>Take exactly one more sweep and stop again.</summary>
        Step,

        /// <summary>Resume acquiring continuously.</summary>
        Continue,

        /// <summary>Discard everything accumulated and begin again.</summary>
        Restart,
    }

    /// <summary>
    /// The Control toolbar's state machine (<c>REQ-UI-063</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Two buttons whose meaning depends on each other.</strong> The requirement quotes the
    /// reference product: Pause "pauses a running measurement; a second click <em>single-steps</em>
    /// when Sweep is Single, or <em>continues</em> when Continuous". So the second click means two
    /// different things depending on a setting made on a different button, and the criterion says
    /// both branches are to be tested "since collapsing them is the likely shortcut".
    /// </para>
    /// <para>
    /// <strong>Here rather than in the shell, and that is the point.</strong> A state machine buried
    /// in a click handler can only be tested by clicking, which on a measurement means hardware and
    /// timing. This one is decided by <see cref="Press"/> and can be walked through every sequence
    /// of presses in a few microseconds; the shell does what the answer says.
    /// </para>
    /// </remarks>
    public sealed class SweepControl
    {
        private SweepMode _mode = SweepMode.Continuous;
        private bool _isRunning;
        private bool _isPaused;

        /// <summary>Raised when the mode, the running state or the paused state changes.</summary>
        public event EventHandler Changed;

        /// <summary>Whether the measurement runs on or takes one sweep at a time.</summary>
        /// <remarks>
        /// Changing the mode does not start, stop or step anything. A user setting Single while a
        /// measurement runs is saying what the <em>next</em> press of Pause should mean, and a mode
        /// switch that halted the measurement would be a control that did two things.
        /// </remarks>
        public SweepMode Mode
        {
            get
            {
                return _mode;
            }

            set
            {
                if (_mode == value)
                {
                    return;
                }

                _mode = value;
                Raise();
            }
        }

        /// <summary>Whether a measurement is under way, paused or not.</summary>
        public bool IsRunning
        {
            get
            {
                return _isRunning;
            }

            set
            {
                if (_isRunning == value)
                {
                    return;
                }

                _isRunning = value;

                if (!_isRunning)
                {
                    _isPaused = false;
                }

                Raise();
            }
        }

        /// <summary>Whether a running measurement is currently held.</summary>
        public bool IsPaused => _isRunning && _isPaused;

        /// <summary>What the Pause button says at the moment.</summary>
        /// <remarks>
        /// The caption is part of the behaviour: a button that reads "Pause" while a second press
        /// would single-step is telling the user the wrong thing about what they are about to do.
        /// </remarks>
        public string PauseCaption
        {
            get
            {
                if (!IsPaused)
                {
                    return "Pause";
                }

                return _mode == SweepMode.Single ? "Single" : "Continue";
            }
        }

        /// <summary>
        /// Answers a press of the Pause button, and moves to the state it leaves behind.
        /// </summary>
        /// <returns>What the measurement should do.</returns>
        public SweepAction Press()
        {
            if (!_isRunning)
            {
                // Nothing is running: the first press starts one. The reference product's Restart
                // is described as "starts a measurement or restarts one that was paused", and Pause
                // is the same button in the other direction.
                return SweepAction.None;
            }

            if (!_isPaused)
            {
                _isPaused = true;
                Raise();
                return SweepAction.Pause;
            }

            if (_mode == SweepMode.Single)
            {
                // Single-steps: one more sweep, and back to being held. The paused state does not
                // change, which is what makes a run of presses step a sweep at a time.
                return SweepAction.Step;
            }

            _isPaused = false;
            Raise();
            return SweepAction.Continue;
        }

        /// <summary>
        /// Answers a press of Restart.
        /// </summary>
        /// <returns>Always <see cref="SweepAction.Restart"/>.</returns>
        /// <remarks>
        /// Restart is not conditional: "starts a measurement or restarts one that was paused", and
        /// either way "all current measurement data including averaging is discarded". It clears
        /// the paused state, because a restart that left the measurement held would leave the user
        /// looking at an empty trace with no obvious way to fill it.
        /// </remarks>
        public SweepAction Restart()
        {
            _isRunning = true;
            _isPaused = false;
            Raise();

            return SweepAction.Restart;
        }

        /// <summary>Whether a sweep taken now should be the last one.</summary>
        public bool StopsAfterOneSweep => _mode == SweepMode.Single;

        private void Raise() => Changed?.Invoke(this, EventArgs.Empty);

        /// <inheritdoc />
        public override string ToString() =>
            _mode + (IsRunning ? (IsPaused ? ", paused" : ", running") : ", stopped");
    }
}
