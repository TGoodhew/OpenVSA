using System;
using System.Collections.Generic;
using System.Threading;

namespace OpenVSA.Hal
{
    /// <summary>What a discovered resource turned out to be (<c>REQ-HAL-003</c>).</summary>
    /// <remarks>
    /// Here rather than in the transport assembly that produces it, because the connection dialog
    /// has to show one and <c>REQ-ARC-001</c> bars the UI from referencing any L0 transport. The
    /// alternative — a parallel type in the UI and a mapping between them — would put the meaning of
    /// "answered" in two places, and the two would drift.
    /// </remarks>
    public sealed class DiscoveredResource
    {
        /// <summary>Records a discovered resource.</summary>
        /// <param name="resourceName">The resource string.</param>
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

        /// <summary>The resource string.</summary>
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
    /// A transport that can list the addresses it can reach (<c>REQ-HAL-003</c>).
    /// </summary>
    /// <remarks>
    /// Discovered by the same scan of plug-in assemblies that finds front ends, and by the same
    /// rule — a public type with a parameterless constructor — rather than by a fourth attribute.
    /// A transport is what knows how to enumerate its own bus, and there is at most one enumerator
    /// per transport, so an attribute would carry no information the interface does not.
    /// </remarks>
    public interface IResourceEnumerator
    {
        /// <summary>Lists every resource, identifying those it is safe to.</summary>
        /// <param name="driverFor">
        /// Maps an <c>*IDN?</c> response to a driver display name, or returns empty.
        /// </param>
        /// <param name="cancel">Cancels a long enumeration.</param>
        /// <returns>One entry per resource.</returns>
        IReadOnlyList<DiscoveredResource> Discover(
            Func<string, string> driverFor, CancellationToken cancel);
    }

    /// <summary>
    /// A front end that can say whether an <c>*IDN?</c> response is one it drives.
    /// </summary>
    /// <remarks>
    /// This is what lets the registry mark the resources a driver exists for without any assembly
    /// having to know the whole set. Each driver answers for itself; the registry asks all of them.
    /// </remarks>
    public interface IInstrumentRecogniser
    {
        /// <summary>Whether this front end drives the instrument that gave this response.</summary>
        /// <param name="identity">An <c>*IDN?</c> response.</param>
        bool Recognises(string identity);
    }

    /// <summary>
    /// A front end that needs to be told which address to use before it can connect.
    /// </summary>
    /// <remarks>
    /// The simulator and file playback do not implement this: one has no address and the other is
    /// given a path. Choosing a front end that does implement it is what opens the connection
    /// dialog, which is why no menu entry had to be added for it — <c>REQ-UI-061</c> fixes the menu
    /// contents as an exact list, and a dialog reached by choosing an instrument is reached by an
    /// entry that is already there.
    /// </remarks>
    public interface IRequiresResource
    {
        /// <summary>The address currently configured.</summary>
        string ResourceName { get; }

        /// <summary>Points this front end at an address.</summary>
        /// <param name="resourceName">The resource string.</param>
        void UseResource(string resourceName);
    }
}
