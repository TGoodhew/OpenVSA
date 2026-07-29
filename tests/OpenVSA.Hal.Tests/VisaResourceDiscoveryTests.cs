using System;
using System.Collections.Generic;
using System.Linq;
using OpenVSA.Hal.Visa;
using Xunit;
using Xunit.Abstractions;

namespace OpenVSA.Hal.Tests
{
    /// <summary>
    /// <c>REQ-HAL-003</c>: every resource listed, each identified where safe, drivers marked.
    /// </summary>
    /// <remarks>
    /// Every interesting case here is a failure case — an address that does not answer, a resource
    /// kind that must not be written to, no VISA at all — and those are hard to arrange on real
    /// hardware and easy to arrange behind the seam. The bench itself is exercised separately by
    /// <c>OpenVSA.Verify</c>.
    /// </remarks>
    public class VisaResourceDiscoveryTests
    {
        private readonly ITestOutputHelper _output;

        /// <summary>Takes xunit's output sink.</summary>
        /// <param name="output">Where the listing is written.</param>
        public VisaResourceDiscoveryTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void EveryResourceIsListedWhetherItAnswersOrNot()
        {
            // The case this exists for. An HP-IB extender reports all thirty GPIB addresses as
            // present whether an instrument is there or not, so a listing that dropped the silent
            // ones would hide the fact that the bus is lying — and a user comparing the list with
            // the rack would have no way to tell.
            var resources = new[]
            {
                "GPIB0::17::INSTR", "GPIB0::18::INSTR", "GPIB0::19::INSTR",
                "TCPIP0::192.168.1.82::inst1::INSTR",
            };

            var discovery = new VisaResourceDiscovery(
                () => resources,
                (resource, timeout) =>
                {
                    if (resource.Contains("::17::"))
                    {
                        return "Hewlett-Packard,E4406A,US40062429,A.08.10";
                    }

                    if (resource.StartsWith("TCPIP", StringComparison.Ordinal))
                    {
                        return "Agilent Technologies, E4438C, MY45090927, C.05.85";
                    }

                    throw new TimeoutException("Timeout expired before the operation completed.");
                });

            IReadOnlyList<DiscoveredResource> found =
                discovery.Discover(idn => idn.Contains("E4406A") ? "Agilent E4406A" : string.Empty);

            foreach (DiscoveredResource resource in found)
            {
                _output.WriteLine(resource.ToString());
            }

            Assert.Equal(4, found.Count);
            Assert.Equal(2, found.Count(r => r.Answered));
            Assert.Equal(2, found.Count(r => !r.Answered));

            // Order is the resource manager's, so the listing matches what a user sees elsewhere.
            Assert.Equal(resources, found.Select(r => r.ResourceName).ToArray());
        }

        [Fact]
        public void OnlyWhatAnsweredWithAKnownIdentityIsMarkedAsHavingADriver()
        {
            var discovery = new VisaResourceDiscovery(
                () => new[] { "GPIB0::17::INSTR", "GPIB0::22::INSTR", "GPIB0::9::INSTR" },
                (resource, timeout) =>
                    resource.Contains("::17::") ? "Hewlett-Packard,E4406A,US40062429,A.08.10"
                    : resource.Contains("::22::") ? "Keithley Instruments,2401,1234567,C33"
                    : throw new TimeoutException("no listener"));

            IReadOnlyList<DiscoveredResource> found =
                discovery.Discover(idn => idn.Contains("E4406A") ? "Agilent E4406A" : string.Empty);

            Assert.True(found[0].HasDriver);

            // Answered, and no driver for it — which is a different state from "did not answer"
            // and has to look different in the dialog, or a supported instrument that happens to
            // be switched off is indistinguishable from an unsupported one that is on.
            Assert.True(found[1].Answered);
            Assert.False(found[1].HasDriver);

            Assert.False(found[2].Answered);
            Assert.False(found[2].HasDriver);
            Assert.Contains("no listener", found[2].Failure);
        }

        [Fact]
        public void ResourcesThatAreNotSafeToWriteToAreListedButNotProbed()
        {
            // "Identifies each with *IDN? where safe." A raw socket or serial port may be a device
            // for which an unexpected write is a fault, or one expecting its own protocol entirely.
            var probed = new List<string>();

            var discovery = new VisaResourceDiscovery(
                () => new[]
                {
                    "GPIB0::17::INSTR",
                    "TCPIP0::10.0.0.5::5025::SOCKET",
                    "ASRL3::INSTR",
                },
                (resource, timeout) =>
                {
                    probed.Add(resource);
                    return "Something,Model,1,1";
                });

            IReadOnlyList<DiscoveredResource> found = discovery.Discover(idn => string.Empty);

            Assert.Equal(3, found.Count);
            Assert.Single(probed);
            Assert.Contains("GPIB0::17::INSTR", probed);

            Assert.False(found[1].Answered);
            Assert.False(found[2].Answered);
            Assert.Contains("not safe", found[1].Failure);
        }

        [Fact]
        public void NoVisaAtAllIsReportedRatherThanThrown()
        {
            // REQ-NFR-032: the application runs usefully with no VISA installed. A dialog that
            // threw here would make an absent runtime look like a crash.
            var discovery = new VisaResourceDiscovery(
                () => throw new TypeInitializationException("Ivi.Visa.GlobalResourceManager", null),
                (resource, timeout) => string.Empty);

            IReadOnlyList<DiscoveredResource> found = discovery.Discover(idn => string.Empty);

            Assert.Single(found);
            Assert.False(found[0].Answered);
            Assert.Contains("VISA is not available", found[0].Failure);
        }

        [Fact]
        public void ATimeoutSaysSoInWordsRatherThanNamingAnExceptionClass()
        {
            // From a live run against the bench: twenty-eight of thirty-seven resources reported
            // "Exception of type 'Ivi.Visa.IOTimeoutException' was thrown." — the .NET default text
            // for an exception carrying no message of its own. That string is the whole of what a
            // user reads in the Status column, and it names a class rather than saying anything
            // about the bus.
            //
            // The timeout is the ordinary answer on an extender that reports all thirty addresses
            // whether or not anything is plugged in, so it gets the clearest sentence available.
            //
            // The stand-in carries no message, as Ivi.Visa.IOTimeoutException does not.
            // System.TimeoutException would NOT exercise this: it has a message of its own ("The
            // operation has timed out.") and the rule keeps any message that says something.
            var discovery = new VisaResourceDiscovery(
                () => new[] { "GPIB0::9::INSTR" },
                (resource, timeout) => throw new SilentTimeout());

            IReadOnlyList<DiscoveredResource> found = discovery.Discover(idn => string.Empty);

            Assert.Contains(
                VisaResourceDiscovery.IdentifyTimeoutMilliseconds.ToString(), found[0].Failure);
            Assert.Contains("no answer", found[0].Failure);
            Assert.DoesNotContain("Exception of type", found[0].Failure);
        }

        [Fact]
        public void AnExceptionWithNoMessageOfItsOwnIsNamedRatherThanQuoted()
        {
            // The general case behind the one above. Anything whose message is the framework's
            // "Exception of type 'X' was thrown." tells a reader nothing the type does not, so the
            // type is reported directly and the boilerplate dropped.
            var discovery = new VisaResourceDiscovery(
                () => new[] { "GPIB0::9::INSTR" },
                (resource, timeout) => throw new SilentFailure());

            IReadOnlyList<DiscoveredResource> found = discovery.Discover(idn => string.Empty);

            Assert.Contains("SilentFailure", found[0].Failure);
            Assert.DoesNotContain("Exception of type", found[0].Failure);
        }

        [Fact]
        public void ATimeoutThatDoesCarryAMessageKeepsIt()
        {
            // The bug in the first version of the wording above: it substituted "no answer within
            // 700 ms" for EVERY timeout, including ones whose own message was more specific. A
            // driver that says "no listener at this address" is telling the user something the
            // clock cannot.
            var discovery = new VisaResourceDiscovery(
                () => new[] { "GPIB0::9::INSTR" },
                (resource, timeout) => throw new TimeoutException("no listener at this address"));

            IReadOnlyList<DiscoveredResource> found = discovery.Discover(idn => string.Empty);

            Assert.Equal("no listener at this address", found[0].Failure);
        }

        [Fact]
        public void AFailureThatDoesSayWhatWentWrongIsShownAsItIs()
        {
            var discovery = new VisaResourceDiscovery(
                () => new[] { "GPIB0::9::INSTR" },
                (resource, timeout) => throw new InvalidOperationException("the cable is unplugged"));

            IReadOnlyList<DiscoveredResource> found = discovery.Discover(idn => string.Empty);

            Assert.Equal("the cable is unplugged", found[0].Failure);
        }

        [Fact]
        public void TheIdentifyTimeoutIsShortEnoughForAFullBus()
        {
            // Thirty GPIB addresses at the ten-second session default is five minutes of a dialog
            // that appears to have hung. An instrument that is present answers in milliseconds.
            Assert.True(VisaResourceDiscovery.IdentifyTimeoutMilliseconds <= 1000);
            Assert.True(VisaResourceDiscovery.IdentifyTimeoutMilliseconds * 30 < 30000);
        }
    }

    /// <summary>An exception with no message of its own, as several VISA ones have.</summary>
    public sealed class SilentFailure : Exception
    {
    }

    /// <summary>A timeout with no message of its own, as <c>Ivi.Visa.IOTimeoutException</c> is.</summary>
    public sealed class SilentTimeout : Exception
    {
    }
}
