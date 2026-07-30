using System;
using System.Linq;
using OpenVSA.Api;
using OpenVSA.Measurement.Contexts;
using Xunit;

namespace OpenVSA.Measurement.Tests.Contexts
{
    /// <summary>
    /// Contexts as addressable objects in the automation API (<c>REQ-DAT-010</c>).
    /// </summary>
    /// <remarks>
    /// The requirement asks for contexts to be "first-class, addressable, nameable objects in the UI,
    /// in saved states and in the automation API". This file is the third of those. The rest of
    /// <c>REQ-API-001</c>'s hierarchy is its own requirement and is not built here.
    /// </remarks>
    public class ContextAutomationTests
    {
        [Fact]
        public void TheApiAddressesTheSessionsOwnContexts()
        {
            var set = new MeasurementContextSet("Spectrum");
            MeasurementContext demod = set.Add("QPSK demod");

            var api = new VsaApplication(set);

            Assert.Equal(
                new[] { "Spectrum", "QPSK demod" },
                api.Measurements.Select(m => m.Name).ToArray());

            // The context itself, not a copy of its name: a script that reaches a context through
            // the API is holding the object the UI and the state file are holding.
            Assert.Same(demod, api.Measurement("QPSK demod").Context);
            Assert.Same(set.Active, api.Active.Context);
        }

        [Fact]
        public void AContextAddedOrRemovedAfterwardsIsSeen()
        {
            var set = new MeasurementContextSet("Spectrum");
            var api = new VsaApplication(set);

            Assert.Single(api.Measurements);

            MeasurementContext added = set.Add("Pulse");

            // A projection rather than a copy taken at construction: a script running while the user
            // adds a context must be able to address it.
            Assert.Equal(2, api.Measurements.Count);
            Assert.Same(added, api.Measurement("Pulse").Context);

            set.Remove(added);

            Assert.Single(api.Measurements);
            Assert.Null(api.Measurement("Pulse"));
        }

        [Fact]
        public void ARenamedContextIsAddressedByItsNewName()
        {
            var set = new MeasurementContextSet("Spectrum");
            var api = new VsaApplication(set);

            VsaMeasurement held = api.Measurement("Spectrum");

            set.Rename(set["Spectrum"], "Adjacent channel");

            // Read through, not copied: a name captured at construction would be addressable by
            // nothing after a rename, and the object a script was holding would go stale.
            Assert.Equal("Adjacent channel", held.Name);
            Assert.Same(held, api.Measurement("Adjacent channel"));
            Assert.Null(api.Measurement("Spectrum"));
        }

        [Fact]
        public void TheActiveContextIsTheOneTheSessionSays()
        {
            var set = new MeasurementContextSet("Spectrum");
            MeasurementContext demod = set.Add("QPSK demod");

            var api = new VsaApplication(set);

            Assert.Equal("Spectrum", api.Active.Name);

            set.Active = demod;

            Assert.Equal("QPSK demod", api.Active.Name);
        }

        [Fact]
        public void AnExactNameWinsOverOneDifferingOnlyInCase()
        {
            var set = new MeasurementContextSet("Spectrum");
            set.Add("spectrum");

            var api = new VsaApplication(set);

            // Both exist, because context names are compared ordinally (REQ-STA-004 matches a state
            // on them). A lookup that folded case first would return whichever came first in the
            // list rather than the one asked for.
            Assert.Equal("spectrum", api.Measurement("spectrum").Name);
            Assert.Equal("Spectrum", api.Measurement("Spectrum").Name);

            // And a name that matches neither exactly still resolves, so a script written before the
            // second context existed keeps working.
            Assert.NotNull(api.Measurement("SPECTRUM"));
        }

        [Fact]
        public void EachContextKeepsItsOwnLimitEvaluatorAcrossCalls()
        {
            var set = new MeasurementContextSet("Spectrum");
            set.Add("QPSK demod");

            var api = new VsaApplication(set);

            // REQ-LIM-003's verdicts accumulate on the evaluator, so a new wrapper on every call
            // would report a limit test that had never been evaluated.
            Assert.Same(api.Measurement("Spectrum"), api.Measurement("Spectrum"));
            Assert.Same(
                api.Measurement("Spectrum").Evaluator, api.Measurement("Spectrum").Evaluator);

            // And they are separate: a limit test belongs to a measurement, not to the session.
            Assert.NotSame(
                api.Measurement("Spectrum").Evaluator, api.Measurement("QPSK demod").Evaluator);
        }

        [Fact]
        public void AnApplicationWithNoSessionHasNoActiveMeasurementAndNoContexts()
        {
            var api = new VsaApplication("Measurement 1");

            // "Active" is a property of a running session: there is nothing on screen for one of
            // these to be, and saying so is better than nominating the first.
            Assert.Null(api.Active);
            Assert.Null(api.Measurements[0].Context);
            Assert.Equal("Measurement 1", api.Measurements[0].Name);
        }

        [Fact]
        public void ABoundApplicationCannotBeMadeWithoutASession()
        {
            Assert.Throws<ArgumentNullException>(
                () => new VsaApplication((MeasurementContextSet)null));
        }
    }
}
