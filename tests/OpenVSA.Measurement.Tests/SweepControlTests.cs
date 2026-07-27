using OpenVSA.Measurement;
using Xunit;

namespace OpenVSA.Measurement.Tests
{
    /// <summary>
    /// <c>REQ-UI-063</c>'s Control toolbar: what a second press of Pause means.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The requirement quotes the reference product — Pause "pauses a running measurement; a second
    /// click single-steps when Sweep is Single, or continues when Continuous" — and its criterion
    /// names the shortcut it expects to be taken: "both branches tested, <strong>since collapsing
    /// them is the likely shortcut</strong>".
    /// </para>
    /// <para>
    /// So both branches are here, and each is written so that a collapsed implementation fails it:
    /// the Single case asserts the measurement is <em>still held</em> afterwards, and the
    /// Continuous case that it is not.
    /// </para>
    /// </remarks>
    public class SweepControlTests
    {
        [Fact]
        public void ASecondPressSingleStepsUnderSingleSweep()
        {
            var sweep = new SweepControl { IsRunning = true, Mode = SweepMode.Single };

            Assert.Equal(SweepAction.Pause, sweep.Press());
            Assert.True(sweep.IsPaused);

            Assert.Equal(SweepAction.Step, sweep.Press());

            // Still held: that is what makes a run of presses step a sweep at a time, and it is the
            // half a collapsed implementation gets wrong by resuming instead.
            Assert.True(sweep.IsPaused);

            Assert.Equal(SweepAction.Step, sweep.Press());
            Assert.Equal(SweepAction.Step, sweep.Press());
            Assert.True(sweep.IsPaused);
        }

        [Fact]
        public void ASecondPressContinuesUnderContinuousSweep()
        {
            var sweep = new SweepControl { IsRunning = true, Mode = SweepMode.Continuous };

            Assert.Equal(SweepAction.Pause, sweep.Press());
            Assert.True(sweep.IsPaused);

            Assert.Equal(SweepAction.Continue, sweep.Press());

            // Running again, so the press after this one pauses rather than stepping.
            Assert.False(sweep.IsPaused);
            Assert.Equal(SweepAction.Pause, sweep.Press());
        }

        [Fact]
        public void TheTwoBranchesDoNotAgree()
        {
            // The test that fails if the two are collapsed into one. Same state, same press, and
            // the answers have to differ.
            var single = new SweepControl { IsRunning = true, Mode = SweepMode.Single };
            var continuous = new SweepControl { IsRunning = true, Mode = SweepMode.Continuous };

            single.Press();
            continuous.Press();

            Assert.NotEqual(single.Press(), continuous.Press());
        }

        [Fact]
        public void TheCaptionSaysWhatTheNextPressWillDo()
        {
            // A button reading "Pause" while a second press would single-step is telling the user
            // the wrong thing about what they are about to do.
            var sweep = new SweepControl();

            Assert.Equal("Pause", sweep.PauseCaption);

            sweep.IsRunning = true;
            Assert.Equal("Pause", sweep.PauseCaption);

            sweep.Press();
            Assert.Equal("Continue", sweep.PauseCaption);

            sweep.Mode = SweepMode.Single;
            Assert.Equal("Single", sweep.PauseCaption);

            sweep.Mode = SweepMode.Continuous;
            Assert.Equal("Continue", sweep.PauseCaption);
        }

        [Fact]
        public void ChangingTheModeDoesNotStartOrStopAnything()
        {
            // A user setting Single while a measurement runs is saying what the next press should
            // mean, not asking for the measurement to stop. A mode switch that halted it would be
            // a control doing two things.
            var sweep = new SweepControl { IsRunning = true };

            sweep.Press();
            Assert.True(sweep.IsPaused);

            sweep.Mode = SweepMode.Single;

            Assert.True(sweep.IsRunning);
            Assert.True(sweep.IsPaused);

            sweep.Mode = SweepMode.Continuous;

            Assert.True(sweep.IsRunning);
            Assert.True(sweep.IsPaused);
        }

        [Fact]
        public void PressingPauseWithNothingRunningDoesNothing()
        {
            var sweep = new SweepControl();

            Assert.Equal(SweepAction.None, sweep.Press());
            Assert.False(sweep.IsPaused);
        }

        [Fact]
        public void RestartClearsTheHold()
        {
            // "Starts a measurement or restarts one that was paused." A restart that left the
            // measurement held would leave the user looking at an empty trace with no obvious way
            // to fill it.
            var sweep = new SweepControl { IsRunning = true, Mode = SweepMode.Single };

            sweep.Press();
            Assert.True(sweep.IsPaused);

            Assert.Equal(SweepAction.Restart, sweep.Restart());

            Assert.True(sweep.IsRunning);
            Assert.False(sweep.IsPaused);

            // And from stopped, it starts.
            var stopped = new SweepControl();

            Assert.Equal(SweepAction.Restart, stopped.Restart());
            Assert.True(stopped.IsRunning);
        }

        [Fact]
        public void StoppingClearsTheHoldToo()
        {
            var sweep = new SweepControl { IsRunning = true };

            sweep.Press();
            Assert.True(sweep.IsPaused);

            sweep.IsRunning = false;

            // Not "paused with nothing running", which would make the next press mean whatever the
            // last mode happened to be.
            Assert.False(sweep.IsPaused);
        }

        [Fact]
        public void EveryChangeIsAnnounced()
        {
            // The toolbar follows this rather than holding its own copy, so a change nobody hears
            // about is a caption that stops matching the state.
            var sweep = new SweepControl();
            int changes = 0;

            sweep.Changed += (sender, e) => changes++;

            sweep.IsRunning = true;
            sweep.Mode = SweepMode.Single;
            sweep.Press();
            sweep.Restart();

            Assert.Equal(4, changes);

            // And a setting written with the value it already has says nothing.
            int quiet = changes;

            sweep.Mode = SweepMode.Single;
            sweep.IsRunning = true;

            Assert.Equal(quiet, changes);
        }
    }
}
