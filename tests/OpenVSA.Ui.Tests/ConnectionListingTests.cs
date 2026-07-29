using System;
using System.Linq;
using OpenVSA.Hal;
using OpenVSA.Ui;
using Xunit;
using Xunit.Abstractions;

namespace OpenVSA.Ui.Tests
{
    /// <summary>
    /// <c>REQ-HAL-003</c>: "The connection dialog lists all VISA resources returned by the resource
    /// manager, identifies each with <c>*IDN?</c> where safe, and marks those for which a driver
    /// exists."
    /// </summary>
    /// <remarks>
    /// Every clause of that criterion is about what the listing contains, and none of it is a
    /// property of a window — so none of it is tested through one. <see cref="ConnectionDialogTests"/>
    /// covers the part that genuinely needs a window.
    /// </remarks>
    public class ConnectionListingTests
    {
        private readonly ITestOutputHelper _output;

        /// <summary>Takes xunit's output sink.</summary>
        /// <param name="output">Where the rendered listing is written.</param>
        public ConnectionListingTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void EveryResourceIsListedIncludingTheSilentOnes()
        {
            // "lists ALL VISA resources returned by the resource manager". Dropping the addresses
            // that did not answer would hide the fact that this bench's HP-IB extender reports all
            // thirty whether an instrument is there or not, and a user comparing the list against
            // the rack would have no way to tell.
            var listing = new ConnectionListing(Bench(), canEnumerate: true);

            foreach (ConnectionRow row in listing.Rows)
            {
                _output.WriteLine(row.ToString());
            }

            Assert.Equal(4, listing.Rows.Count);

            Assert.Contains(listing.Rows, r => r.Resource == "GPIB0::17::INSTR");
            Assert.Contains(listing.Rows, r => r.Resource == "GPIB0::23::INSTR");
            Assert.Contains(listing.Rows, r => r.Resource == "GPIB0::9::INSTR");
            Assert.Contains(listing.Rows, r => r.Resource == "TCPIP0::192.168.1.99::SOCKET");
        }

        [Fact]
        public void OnlyTheOnesWithADriverCanBeConnectedTo()
        {
            // "marks those for which a driver exists". The mark is what Connect is enabled from,
            // so it cannot be decorative.
            var listing = new ConnectionListing(Bench(), canEnumerate: true);

            Assert.Equal(1, listing.ConnectableCount);

            ConnectionRow drivable = listing.Rows.Single(r => r.CanConnect);

            Assert.Equal("GPIB0::17::INSTR", drivable.Resource);
            Assert.Equal("Ready", drivable.Status);
        }

        [Fact]
        public void AnsweredWithNoDriverIsNotTheSameAsDidNotAnswer()
        {
            // The distinction that matters when something is missing from the list: a supported
            // instrument that happens to be switched off must not look identical to an unsupported
            // one that is on. One of those is fixed with a power switch.
            var listing = new ConnectionListing(Bench(), canEnumerate: true);

            ConnectionRow answered = listing.Rows.Single(r => r.Resource == "GPIB0::23::INSTR");
            ConnectionRow silent = listing.Rows.Single(r => r.Resource == "GPIB0::9::INSTR");

            _output.WriteLine("answered: " + answered);
            _output.WriteLine("silent:   " + silent);

            Assert.False(answered.CanConnect);
            Assert.False(silent.CanConnect);

            Assert.NotEqual(answered.Status, silent.Status);

            // The one that answered shows what it said, so a reader can see it is real hardware.
            Assert.Contains("HEWLETT-PACKARD,3458A", answered.Identity);
            Assert.Equal("no driver", answered.Driver);

            // The one that did not shows why, and does not invent an identity for it.
            Assert.Equal("—", silent.Identity);
            Assert.Contains("timed out", silent.Status);
        }

        [Fact]
        public void AResourceThatWasNotProbedSaysThatRatherThanLookingDead()
        {
            // "identifies each with *IDN? WHERE SAFE". A socket that was deliberately left alone
            // and an address that was asked and stayed silent are different facts, and a listing
            // that showed both as "no answer" would be reporting an experiment it never ran.
            var listing = new ConnectionListing(Bench(), canEnumerate: true);

            ConnectionRow socket = listing.Rows.Single(
                r => r.Resource == "TCPIP0::192.168.1.99::SOCKET");

            _output.WriteLine(socket.ToString());

            Assert.Contains("not safe", socket.Status);
            Assert.DoesNotContain("timed out", socket.Status);
        }

        [Fact]
        public void NoVisaAtAllIsSaidDifferentlyFromAnEmptyBus()
        {
            // Three nothings, said differently. REQ-NFR-032 makes "no VISA installed" an ordinary
            // configuration rather than a fault, and a user on such a machine needs to be told that
            // the simulator is still there rather than left looking at an empty grid.
            var noVisa = new ConnectionListing(new DiscoveredResource[0], canEnumerate: false);
            var emptyBus = new ConnectionListing(new DiscoveredResource[0], canEnumerate: true);
            var nothingDrivable = new ConnectionListing(
                Bench().Where(r => !r.HasDriver), canEnumerate: true);

            _output.WriteLine("no VISA:         " + noVisa.Summary);
            _output.WriteLine("empty bus:       " + emptyBus.Summary);
            _output.WriteLine("nothing drives:  " + nothingDrivable.Summary);
            _output.WriteLine("a full bench:    " +
                new ConnectionListing(Bench(), canEnumerate: true).Summary);

            Assert.Contains("simulator", noVisa.Summary);
            Assert.Contains("no resources", emptyBus.Summary);
            Assert.Contains("none of which", nothingDrivable.Summary);

            Assert.NotEqual(noVisa.Summary, emptyBus.Summary);
            Assert.NotEqual(emptyBus.Summary, nothingDrivable.Summary);
        }

        [Fact]
        public void OneResourceIsSingular()
        {
            var listing = new ConnectionListing(
                new[] { new DiscoveredResource("GPIB0::17::INSTR", "a,b,c,d", null, "Driver") },
                canEnumerate: true);

            Assert.Contains("1 resource found", listing.Summary);
            Assert.DoesNotContain("resources", listing.Summary);
        }

        [Fact]
        public void TheOrderTheResourceManagerGaveIsKept()
        {
            // Not sorted. The order VISA reports is the order of the bus, and a reader walking the
            // rack is walking it in that order.
            var listing = new ConnectionListing(Bench(), canEnumerate: true);

            Assert.Equal(
                new[]
                {
                    "GPIB0::9::INSTR", "GPIB0::17::INSTR", "GPIB0::23::INSTR",
                    "TCPIP0::192.168.1.99::SOCKET",
                },
                listing.Rows.Select(r => r.Resource).ToArray());
        }

        [Fact]
        public void ANullListingIsRejectedRatherThanShownEmpty()
        {
            Assert.Throws<ArgumentNullException>(() => new ConnectionListing(null, true));
            Assert.Throws<ArgumentNullException>(() => new ConnectionRow(null));
        }

        /// <summary>
        /// What discovery reports on this bench: one drivable instrument, one that answers and is
        /// not ours, one silent address, and one resource that is listed but never written to.
        /// </summary>
        internal static DiscoveredResource[] Bench()
        {
            return new[]
            {
                new DiscoveredResource(
                    "GPIB0::9::INSTR", null, "*IDN? timed out after 700 ms", null),
                new DiscoveredResource(
                    "GPIB0::17::INSTR",
                    "Hewlett-Packard,E4406A,US00000000,A.08.06",
                    null,
                    "Transmitter tester"),
                new DiscoveredResource(
                    "GPIB0::23::INSTR", "HEWLETT-PACKARD,3458A,0,8.1", null, null),
                new DiscoveredResource(
                    "TCPIP0::192.168.1.99::SOCKET",
                    null,
                    "not probed: writing here is not safe",
                    null),
            };
        }
    }
}
