using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using OpenVSA.Hal;

namespace OpenVSA.Ui
{
    /// <summary>One row of the connection dialog (<c>REQ-HAL-003</c>).</summary>
    /// <remarks>
    /// The wording lives here rather than in the window so it can be asserted without one. Every
    /// column is a string chosen for a reader deciding which address to connect to, and the
    /// decisions in that choice are the ones worth testing.
    /// </remarks>
    public sealed class ConnectionRow
    {
        /// <summary>Builds a row from what discovery reported.</summary>
        /// <param name="resource">The discovered resource.</param>
        /// <exception cref="ArgumentNullException"><paramref name="resource"/> is null.</exception>
        public ConnectionRow(DiscoveredResource resource)
        {
            if (resource == null)
            {
                throw new ArgumentNullException(nameof(resource));
            }

            Resource = resource.ResourceName;
            CanConnect = resource.HasDriver;

            // Answered and did-not-answer are kept visibly distinct, because a supported instrument
            // that is switched off would otherwise look exactly like an unsupported one that is on.
            Identity = resource.Answered ? resource.Identity : "—";

            Driver = resource.HasDriver
                ? resource.Driver
                : resource.Answered ? "no driver" : "—";

            Status = resource.Answered
                ? (resource.HasDriver ? "Ready" : "Answered, but nothing here drives it")
                : resource.Failure;
        }

        /// <summary>The resource string.</summary>
        public string Resource { get; }

        /// <summary>The <c>*IDN?</c> response, or a dash.</summary>
        public string Identity { get; }

        /// <summary>The driver that claims it, or why there is none.</summary>
        public string Driver { get; }

        /// <summary>What a reader needs to know about this row.</summary>
        public string Status { get; }

        /// <summary>Whether connecting to this row would do anything.</summary>
        public bool CanConnect { get; }

        /// <inheritdoc />
        public override string ToString() =>
            Resource + " | " + Identity + " | " + Driver + " | " + Status;
    }

    /// <summary>
    /// The connection dialog's contents, without the dialog (<c>REQ-HAL-003</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>REQ-HAL-003</c>'s criterion is about what the listing contains: every resource the
    /// resource manager returned, each identified where safe, and those with a driver marked. None
    /// of that is a property of a window, so none of it is tested through one.
    /// </para>
    /// <para>
    /// The enumeration itself is supplied rather than reached for. <c>REQ-ARC-001</c> bars this
    /// assembly from referencing any transport, so the shell passes in
    /// <see cref="FrontEndRegistry.DiscoverResources"/> and this never learns what VISA is.
    /// </para>
    /// </remarks>
    public sealed class ConnectionListing
    {
        private readonly List<ConnectionRow> _rows = new List<ConnectionRow>();

        /// <summary>Builds a listing from what was discovered.</summary>
        /// <param name="resources">The discovered resources; may be empty.</param>
        /// <param name="canEnumerate">
        /// Whether any transport could enumerate at all, which is a different thing from finding
        /// nothing.
        /// </param>
        /// <exception cref="ArgumentNullException"><paramref name="resources"/> is null.</exception>
        public ConnectionListing(IEnumerable<DiscoveredResource> resources, bool canEnumerate)
        {
            if (resources == null)
            {
                throw new ArgumentNullException(nameof(resources));
            }

            _rows.AddRange(resources.Select(r => new ConnectionRow(r)));
            CanEnumerate = canEnumerate;
        }

        /// <summary>The rows, in the order the resource manager gave them.</summary>
        /// <remarks>
        /// Not sorted, and not filtered to the ones with drivers. Silent addresses are the evidence
        /// that a bus is reporting more than is on it — which this bench's HP-IB extender does for
        /// all thirty GPIB addresses — and a user comparing the list against the rack has no other
        /// way to see it.
        /// </remarks>
        public IReadOnlyList<ConnectionRow> Rows => new ReadOnlyCollection<ConnectionRow>(_rows);

        /// <summary>Whether any transport could enumerate.</summary>
        public bool CanEnumerate { get; }

        /// <summary>How many rows a driver exists for.</summary>
        public int ConnectableCount => _rows.Count(r => r.CanConnect);

        /// <summary>The line shown when there is nothing to choose from.</summary>
        /// <remarks>
        /// Three different nothings, said differently. "No VISA at all" is
        /// <c>REQ-NFR-032</c>'s ordinary case and not a fault; "VISA found nothing" means the bus is
        /// empty or the interface is not configured; "found things but none are drivable" means the
        /// list below is worth reading. An empty grid says all three at once, which is to say
        /// nothing.
        /// </remarks>
        public string Summary
        {
            get
            {
                if (!CanEnumerate)
                {
                    return "No transport on this machine can enumerate instruments. " +
                           "The simulator and file playback are still available.";
                }

                if (_rows.Count == 0)
                {
                    return "The resource manager returned no resources.";
                }

                if (ConnectableCount == 0)
                {
                    return _rows.Count + " " + (_rows.Count == 1 ? "resource" : "resources") +
                           " found, none of which a driver here recognises.";
                }

                return _rows.Count + " " + (_rows.Count == 1 ? "resource" : "resources") +
                       " found, " + ConnectableCount + " with a driver.";
            }
        }
    }
}
