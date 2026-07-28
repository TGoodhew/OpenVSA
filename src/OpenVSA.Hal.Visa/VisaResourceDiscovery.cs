using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;

namespace OpenVSA.Hal.Visa
{
    /// <summary>What a VISA resource turned out to be.</summary>
    public sealed class DiscoveredResource
    {
        /// <summary>Records a discovered resource.</summary>
        /// <param name="resourceName">The VISA resource string.</param>
        /// <param name="identity">The <c>*IDN?</c> response, or empty when it did not answer.</param>
        /// <param name="failure">Why it did not answer, or empty when it did.</param>
        /// <param name="driver">The driver's display name, or empty when none matches.</param>
        /// <exception cref="ArgumentNullException"><paramref name="resourceName"/> is null.</exception>
        public DiscoveredResource(string resourceName, string identity, string failure, string driver)
        {
            ResourceName = resourceName ?? throw new ArgumentNullException(nameof(resourceName));
            Identity = identity ?? string.Empty;
            Failure = failure ?? string.Empty;
            Driver = driver ?? string.Empty;
        }

        /// <summary>The VISA resource string.</summary>
        public string ResourceName { get; }

        /// <summary>The <c>*IDN?</c> response, or empty.</summary>
        public string Identity { get; }

        /// <summary>Why identification failed, or empty when it succeeded.</summary>
        public string Failure { get; }

        /// <summary>The matching driver's display name, or empty.</summary>
        public string Driver { get; }

        /// <summary>Whether something answered at this address.</summary>
        /// <remarks>
        /// The distinction that makes a listing usable on this bench. An HP-IB extender reports
        /// every one of its thirty addresses as present whether an instrument is there or not, so
        /// "the resource manager returned it" means very little and "it answered <c>*IDN?</c>"
        /// means a great deal.
        /// </remarks>
        public bool Answered => Identity.Length > 0;

        /// <summary>Whether a driver exists for what answered.</summary>
        public bool HasDriver => Driver.Length > 0;

        /// <inheritdoc />
        public override string ToString() =>
            ResourceName + " — " +
            (Answered ? Identity : "no answer (" + Failure + ")") +
            (HasDriver ? " [" + Driver + "]" : string.Empty);
    }

    /// <summary>
    /// Enumerates VISA resources and says what each one is (<c>REQ-HAL-003</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>REQ-HAL-003</c> asks for a listing of everything the resource manager returns, each
    /// identified with <c>*IDN?</c> "where safe", and those with a driver marked. The middle clause
    /// is not politeness — it is what makes the listing worth showing at all on a bench with an
    /// HP-IB extender, where <c>viFindRsrc</c> reports all thirty GPIB addresses regardless of what
    /// is plugged in.
    /// </para>
    /// <para>
    /// <strong>"Where safe" is taken seriously.</strong> Opening a session and writing to an
    /// address is not free of consequence: some instruments reset their output on a bus command,
    /// and a listing that disturbed a running measurement would be worse than no listing. So the
    /// timeout is short, the query is the one command SCPI guarantees every instrument answers,
    /// nothing else is written, and a resource kind that cannot be identified without side effects
    /// is skipped rather than probed.
    /// </para>
    /// <para>
    /// Below the UI so the dialog does the showing and this does the finding — the same division
    /// the rest of the shell keeps, and the reason this can be tested without a window.
    /// </para>
    /// </remarks>
    public sealed class VisaResourceDiscovery
    {
        /// <summary>How long to wait for <c>*IDN?</c> before giving up on an address.</summary>
        /// <remarks>
        /// Short on purpose. Thirty addresses at a ten-second timeout is five minutes of a dialog
        /// that appears to have hung; an instrument that is present answers in milliseconds.
        /// </remarks>
        public const int IdentifyTimeoutMilliseconds = 700;

        private readonly Func<IEnumerable<string>> _enumerate;
        private readonly Func<string, int, string> _identify;

        /// <summary>Creates a discovery over the real VISA resource manager.</summary>
        public VisaResourceDiscovery()
            : this(VisaSession.Find, DefaultIdentify)
        {
        }

        /// <summary>Creates a discovery over supplied enumeration and identification.</summary>
        /// <param name="enumerate">Returns resource strings.</param>
        /// <param name="identify">Returns the <c>*IDN?</c> response, or throws.</param>
        /// <exception cref="ArgumentNullException">An argument is null.</exception>
        /// <remarks>
        /// The seam exists so the behaviour that matters — what happens when an address does not
        /// answer, and which resources are skipped — can be tested without a bench. Every
        /// interesting case here is a failure case, and failures are hard to arrange on real
        /// hardware and easy to arrange here.
        /// </remarks>
        public VisaResourceDiscovery(
            Func<IEnumerable<string>> enumerate,
            Func<string, int, string> identify)
        {
            _enumerate = enumerate ?? throw new ArgumentNullException(nameof(enumerate));
            _identify = identify ?? throw new ArgumentNullException(nameof(identify));
        }

        /// <summary>
        /// Resource kinds that are listed but never written to.
        /// </summary>
        /// <remarks>
        /// A raw socket or a serial port has no guarantee that <c>*IDN?</c> is meaningful or even
        /// harmless — the far end may be a power supply expecting its own protocol, or a device for
        /// which an unexpected write is a fault. These are shown with no identity rather than
        /// probed, which is what "where safe" means.
        /// </remarks>
        public static readonly string[] NotProbed = { "SOCKET", "ASRL", "RAW" };

        /// <summary>Lists every resource, identifying those it is safe to.</summary>
        /// <param name="driverFor">
        /// Maps an <c>*IDN?</c> response to a driver display name, or returns empty. Supplied by
        /// the caller so this assembly does not have to know the whole registry.
        /// </param>
        /// <param name="cancel">Cancels a long enumeration.</param>
        /// <returns>One entry per resource, in the order the resource manager gave them.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="driverFor"/> is null.</exception>
        public IReadOnlyList<DiscoveredResource> Discover(
            Func<string, string> driverFor,
            CancellationToken cancel = default(CancellationToken))
        {
            if (driverFor == null)
            {
                throw new ArgumentNullException(nameof(driverFor));
            }

            var found = new List<DiscoveredResource>();

            IEnumerable<string> resources;

            try
            {
                resources = _enumerate() ?? Enumerable.Empty<string>();
            }
            catch (Exception e)
            {
                // No VISA installed is the normal case for a machine with no instruments
                // (REQ-NFR-032), not a fault. An empty list says the same thing as a crash and
                // lets the application start.
                return new ReadOnlyCollection<DiscoveredResource>(
                    new[] { new DiscoveredResource("(none)", null, "VISA is not available — " + e.Message, null) });
            }

            foreach (string resource in resources)
            {
                cancel.ThrowIfCancellationRequested();

                if (IsUnsafeToProbe(resource))
                {
                    found.Add(new DiscoveredResource(
                        resource, null, "not identified: writing to this kind of resource is not safe", null));
                    continue;
                }

                string identity;

                try
                {
                    identity = (_identify(resource, IdentifyTimeoutMilliseconds) ?? string.Empty).Trim();
                }
                catch (Exception e)
                {
                    // The common case on a bus with an extender: the address is reported present
                    // and nothing answers. Recorded, listed, and not treated as an error.
                    found.Add(new DiscoveredResource(resource, null, Shorten(e.Message), null));
                    continue;
                }

                found.Add(new DiscoveredResource(
                    resource, identity, null, identity.Length == 0 ? null : driverFor(identity)));
            }

            return new ReadOnlyCollection<DiscoveredResource>(found);
        }

        /// <summary>Whether a resource is one of the kinds never written to.</summary>
        /// <param name="resource">The resource string.</param>
        public static bool IsUnsafeToProbe(string resource) =>
            resource != null &&
            NotProbed.Any(kind => resource.IndexOf(kind, StringComparison.OrdinalIgnoreCase) >= 0);

        private static string DefaultIdentify(string resource, int timeoutMilliseconds)
        {
            using (VisaSession session = VisaSession.Open(resource, timeoutMilliseconds))
            {
                return session.Query("*IDN?");
            }
        }

        /// <summary>A message short enough for a list cell.</summary>
        private static string Shorten(string message)
        {
            if (string.IsNullOrEmpty(message))
            {
                return "no answer";
            }

            int stop = message.IndexOfAny(new[] { '\r', '\n' });
            string first = stop < 0 ? message : message.Substring(0, stop);

            return first.Length <= 90 ? first : first.Substring(0, 87) + "…";
        }
    }
}
