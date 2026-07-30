using System;
using System.Linq;
using OpenVSA.Dsp.Windowing;
using OpenVSA.Measurement.Contexts;
using OpenVSA.Measurement.Markers;
using OpenVSA.Measurement.State;
using Xunit;

namespace OpenVSA.Measurement.Tests.Contexts
{
    /// <summary>
    /// Measurement contexts as first-class, nameable, addressable objects (<c>REQ-DAT-010</c>).
    /// </summary>
    public class MeasurementContextSetTests
    {
        [Fact]
        public void ASessionStartsWithOneActiveContext()
        {
            var set = new MeasurementContextSet();

            Assert.Equal(1, set.Count);
            Assert.Equal(MeasurementContextSet.DefaultName, set.Active.Name);
            Assert.Same(set.Active, set[MeasurementContextSet.DefaultName]);
        }

        [Fact]
        public void ContextsAreAddressedByName()
        {
            var set = new MeasurementContextSet("Spectrum");
            MeasurementContext demod = set.Add("QPSK demod");

            Assert.Same(demod, set["QPSK demod"]);
            Assert.Equal(new[] { "Spectrum", "QPSK demod" }, set.Names.ToArray());
            Assert.Null(set["nothing called this"]);
        }

        [Fact]
        public void ANameCannotBeTakenTwice()
        {
            var set = new MeasurementContextSet("Spectrum");

            // Recall matches on the name (REQ-STA-004), so two contexts sharing one would make a
            // state file that could be applied two different ways.
            ArgumentException refused =
                Assert.Throws<ArgumentException>(() => set.Add("Spectrum"));

            Assert.Contains("Spectrum", refused.Message, StringComparison.Ordinal);
            Assert.Equal(1, set.Count);
        }

        [Fact]
        public void CaseIsPartOfTheName()
        {
            var set = new MeasurementContextSet("Spectrum");

            // Not a collision: folding case would refuse a name on the strength of a rule nobody
            // stated. Ordinal throughout, as the state matching is.
            set.Add("spectrum");

            Assert.Equal(2, set.Count);
        }

        [Fact]
        public void ABlankNameIsRefused()
        {
            var set = new MeasurementContextSet();

            Assert.Throws<ArgumentException>(() => set.Add("   "));
            Assert.Throws<ArgumentException>(() => set.Add(null));
        }

        [Fact]
        public void TheLastContextCannotBeRemoved()
        {
            var set = new MeasurementContextSet();

            // A session with no context has no measurement to configure and no name to recall a
            // state into.
            Assert.False(set.Remove(set.Active));
            Assert.Equal(1, set.Count);
        }

        [Fact]
        public void RemovingTheActiveContextMovesTheSelection()
        {
            var set = new MeasurementContextSet("Spectrum");
            MeasurementContext demod = set.Add("QPSK demod");
            set.Active = demod;

            MeasurementContext announced = null;
            set.ActiveChanged += (sender, context) => announced = context;

            Assert.True(set.Remove(demod));

            Assert.Equal("Spectrum", set.Active.Name);
            Assert.Same(set.Active, announced);
        }

        [Fact]
        public void RenamingCarriesTheSetupWithIt()
        {
            var set = new MeasurementContextSet("Measurement 1");
            string was = null;

            set.Renamed += (sender, e) => was = e.PreviousName;
            set.Rename(set.Active, "Adjacent channel");

            Assert.Equal("Adjacent channel", set.Active.Name);

            // The setup's name too: a state saved from a context whose setup still said
            // "Measurement 1" could not be recalled into the session that wrote it.
            Assert.Equal("Adjacent channel", set.Active.Setup.ContextName);
            Assert.Equal("Measurement 1", was);
        }

        [Fact]
        public void RenamingToATakenNameIsRefused()
        {
            var set = new MeasurementContextSet("Spectrum");
            MeasurementContext demod = set.Add("QPSK demod");

            Assert.Throws<ArgumentException>(() => set.Rename(demod, "Spectrum"));
            Assert.Equal("QPSK demod", demod.Name);
        }

        [Fact]
        public void RenamingToTheSameNameIsNotAChange()
        {
            var set = new MeasurementContextSet("Spectrum");
            int renames = 0;

            set.Renamed += (sender, e) => renames++;
            set.Rename(set.Active, "Spectrum");

            Assert.Equal(0, renames);
        }

        [Fact]
        public void AnUnusedNameFillsTheGapRatherThanCountingUp()
        {
            var set = new MeasurementContextSet("Measurement 1");
            set.Add("Measurement 2");
            MeasurementContext third = set.Add("Measurement 3");

            set.Remove(third);

            Assert.Equal("Measurement 3", set.UnusedName());

            set.Add("Measurement 3");
            set.Remove(set["Measurement 2"]);

            // 2, not 4: numbering from the count would name it after a context that already exists,
            // which Add would then refuse.
            Assert.Equal("Measurement 2", set.UnusedName());
        }

        [Fact]
        public void EachContextHasItsOwnMarkers()
        {
            var set = new MeasurementContextSet("Spectrum");
            MeasurementContext demod = set.Add("QPSK demod");

            MarkerSet onSpectrum = set["Spectrum"].Markers.ForTrace('A');
            MarkerSet onDemod = demod.Markers.ForTrace('A');

            onSpectrum.AddNormal(1.0e9);
            onSpectrum.AddNormal(1.001e9);
            onDemod.AddNormal(2.4e9);

            // A marker put on a spectrum has no meaning on a constellation, so the two collections
            // are separate objects and not two views of one.
            Assert.Equal(2, onSpectrum.Markers.Count);
            Assert.Equal(1, onDemod.Markers.Count);
            Assert.NotSame(onSpectrum, onDemod);
        }

        [Fact]
        public void EachContextHasItsOwnTraceWindows()
        {
            var set = new MeasurementContextSet("Spectrum");
            MeasurementContext spectrum = set.Active;
            MeasurementContext demod = set.Add("QPSK demod");

            spectrum.AddTrace('A');
            spectrum.AddTrace('B');
            demod.AddTrace('C');

            Assert.Equal(new[] { 'A', 'B' }, spectrum.Traces.ToArray());
            Assert.Equal(new[] { 'C' }, demod.Traces.ToArray());
            Assert.False(demod.HasTrace('A'));

            // The active trace is the context's own, so switching context does not point the trace
            // commands at a window belonging to the other one.
            Assert.Equal('A', spectrum.ActiveTrace);
            Assert.Equal('C', demod.ActiveTrace);
            Assert.Throws<ArgumentException>(() => demod.ActiveTrace = 'A');
        }

        [Fact]
        public void ClosingAContextsActiveTraceMovesToAnotherOfItsOwn()
        {
            var context = new MeasurementContext("Spectrum");

            context.AddTrace('A');
            context.AddTrace('B');
            context.ActiveTrace = 'B';

            Assert.True(context.RemoveTrace('B'));
            Assert.Equal('A', context.ActiveTrace);
            Assert.False(context.RemoveTrace('B'));
        }

        // ---- Save and recall by name (REQ-DAT-010's second clause, over REQ-STA-004) -------------

        [Fact]
        public void EveryContextIsSaved()
        {
            var set = new MeasurementContextSet("Spectrum");
            set.Active.Setup.CenterFrequencyHz = 1.0e9;

            MeasurementContext demod = set.Add("QPSK demod");
            demod.Setup.CenterFrequencyHz = 2.4e9;
            demod.Setup.Kind = MeasurementKind.DigitalDemodulation;

            ApplicationState state = set.Capture();

            // Both, not just the active one. A state carrying one of two contexts recalls as a
            // session with one measurement configured and one silently left as it was.
            Assert.Equal(new[] { "Spectrum", "QPSK demod" }, state.ContextNames().ToArray());
            Assert.Equal(1.0e9, state.For("Spectrum").CenterFrequencyHz);
            Assert.Equal(
                MeasurementKind.DigitalDemodulation, state.For("QPSK demod").Kind);
        }

        [Fact]
        public void BothContextsAreRecalledByName()
        {
            var saved = new MeasurementContextSet("Spectrum");
            saved.Active.Setup.CenterFrequencyHz = 1.0e9;
            saved.Active.Setup.Analysis.Window = WindowType.Hann;

            MeasurementContext savedDemod = saved.Add("QPSK demod");
            savedDemod.Setup.CenterFrequencyHz = 2.4e9;
            savedDemod.Setup.Analysis.Window = WindowType.Gaussian;

            ApplicationState state = saved.Capture();

            // A fresh session with the same two names, created in the other order: matching is by
            // name, so the order they were made in must not decide which setup lands where.
            var recalled = new MeasurementContextSet("QPSK demod");
            recalled.Add("Spectrum");

            recalled.Recall(state);

            Assert.Equal(1.0e9, recalled["Spectrum"].Setup.CenterFrequencyHz);
            Assert.Equal(WindowType.Hann, recalled["Spectrum"].Setup.Analysis.Window);
            Assert.Equal(2.4e9, recalled["QPSK demod"].Setup.CenterFrequencyHz);
            Assert.Equal(WindowType.Gaussian, recalled["QPSK demod"].Setup.Analysis.Window);
        }

        [Fact]
        public void ARecalledSetupIsRenamedToTheContextItLandsIn()
        {
            var set = new MeasurementContextSet("Spectrum");
            var state = new ApplicationState();
            state.Measurements.Clear();
            state.Measurements.Add(new MeasurementState { ContextName = "Spectrum" });

            set.Recall(state);
            set.Rename(set["Spectrum"], "Something else");

            // Capture must write what the session has now, not what the file it came from said.
            Assert.Equal("Something else", set.Capture().ContextNames().Single());
        }

        [Fact]
        public void AStateNamingAnUnknownContextChangesNothing()
        {
            var set = new MeasurementContextSet("Spectrum");
            set.Active.Setup.CenterFrequencyHz = 1.0e9;
            set.Add("QPSK demod").Setup.CenterFrequencyHz = 2.4e9;

            var state = new ApplicationState();
            state.Measurements.Clear();
            state.Measurements.Add(new MeasurementState
            {
                ContextName = "Spectrum",
                CenterFrequencyHz = 5.8e9,
            });
            state.Measurements.Add(new MeasurementState
            {
                ContextName = "Pulse",
                CenterFrequencyHz = 10.0e9,
            });

            ContextMismatchException mismatch =
                Assert.Throws<ContextMismatchException>(() => set.Recall(state));

            // Named, so the message is something the user can act on.
            Assert.Equal(new[] { "Pulse" }, mismatch.Missing.ToArray());
            Assert.Contains("QPSK demod", mismatch.Available);

            // And nothing applied: the context the state DID match keeps the frequency it had. The
            // whole point of the requirement is that a half-applied recall never happens.
            Assert.Equal(1.0e9, set["Spectrum"].Setup.CenterFrequencyHz);
            Assert.Equal(2.4e9, set["QPSK demod"].Setup.CenterFrequencyHz);
        }

        [Fact]
        public void AContextTheStateDoesNotNameKeepsItsSetup()
        {
            var set = new MeasurementContextSet("Spectrum");
            set.Add("QPSK demod").Setup.CenterFrequencyHz = 2.4e9;

            var state = new ApplicationState();
            state.Measurements.Clear();
            state.Measurements.Add(new MeasurementState
            {
                ContextName = "Spectrum",
                CenterFrequencyHz = 5.8e9,
            });

            set.Recall(state);

            Assert.Equal(5.8e9, set["Spectrum"].Setup.CenterFrequencyHz);
            Assert.Equal(2.4e9, set["QPSK demod"].Setup.CenterFrequencyHz);
        }
    }
}
