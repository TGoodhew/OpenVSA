using System;
using System.Globalization;

namespace OpenVSA.TestHarness
{
    /// <summary>
    /// What a stimulus source says it can produce, read from the source itself (issue #393).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Read, not declared.</strong> The measurement side of OpenVSA takes every range it
    /// offers from <c>IFrontEndCapabilities</c> rather than from a table of models, and issue #393
    /// asks the generator side to keep the same discipline: "ranges and limits come from the
    /// instrument's own MIN/MAX queries, not hard-coded". A panel that offered 250 kHz to 4 GHz
    /// because that is what some generator does would let a user ask for a carrier this one cannot
    /// produce, and the instrument would quietly clip it — which is a measurement taken against a
    /// signal nobody asked for.
    /// </para>
    /// <para>
    /// <strong>Unknown is a value, not an error.</strong> Any limit the source will not answer for
    /// is <see cref="double.NaN"/>, and <see cref="IsKnown"/> says so. A probe that a particular
    /// firmware rejects must not stop the rest of the panel working — and on this bench that is not
    /// hypothetical: a rejected query on one node was found to time out and leave its error queued
    /// for an unrelated operation to be blamed for. So a limit that cannot be had is absent and
    /// says it is absent, rather than being replaced with a plausible number.
    /// </para>
    /// </remarks>
    public sealed class StimulusLimits
    {
        /// <summary>Every limit unknown, for a source that will not answer for any of them.</summary>
        public static readonly StimulusLimits Unknown =
            new StimulusLimits(double.NaN, double.NaN, double.NaN, double.NaN);

        /// <summary>Creates a set of limits.</summary>
        /// <param name="minimumFrequencyHz">Lowest carrier, in hertz, or <c>NaN</c> if unknown.</param>
        /// <param name="maximumFrequencyHz">Highest carrier, in hertz, or <c>NaN</c>.</param>
        /// <param name="minimumLevelDbm">Lowest output level, in dBm, or <c>NaN</c>.</param>
        /// <param name="maximumLevelDbm">Highest output level, in dBm, or <c>NaN</c>.</param>
        public StimulusLimits(
            double minimumFrequencyHz,
            double maximumFrequencyHz,
            double minimumLevelDbm,
            double maximumLevelDbm)
        {
            MinimumFrequencyHz = minimumFrequencyHz;
            MaximumFrequencyHz = maximumFrequencyHz;
            MinimumLevelDbm = minimumLevelDbm;
            MaximumLevelDbm = maximumLevelDbm;
        }

        /// <summary>Lowest carrier the source will produce, in hertz.</summary>
        public double MinimumFrequencyHz { get; }

        /// <summary>Highest carrier the source will produce, in hertz.</summary>
        public double MaximumFrequencyHz { get; }

        /// <summary>Lowest output level, in dBm.</summary>
        public double MinimumLevelDbm { get; }

        /// <summary>Highest output level, in dBm.</summary>
        public double MaximumLevelDbm { get; }

        /// <summary>Whether both frequency limits were had from the source.</summary>
        public bool HasFrequencyRange =>
            IsKnown(MinimumFrequencyHz) && IsKnown(MaximumFrequencyHz);

        /// <summary>Whether both level limits were had from the source.</summary>
        public bool HasLevelRange =>
            IsKnown(MinimumLevelDbm) && IsKnown(MaximumLevelDbm);

        /// <summary>Whether a limit was answered for.</summary>
        /// <param name="limit">The limit.</param>
        public static bool IsKnown(double limit) => !double.IsNaN(limit);

        /// <inheritdoc />
        public override string ToString() =>
            string.Format(
                CultureInfo.InvariantCulture,
                "{0} to {1} Hz, {2} to {3} dBm",
                Describe(MinimumFrequencyHz),
                Describe(MaximumFrequencyHz),
                Describe(MinimumLevelDbm),
                Describe(MaximumLevelDbm));

        private static string Describe(double limit) =>
            IsKnown(limit) ? limit.ToString("R", CultureInfo.InvariantCulture) : "unknown";
    }

    /// <summary>
    /// A stimulus source that will say what it can produce (issue #393).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Separate from <see cref="IStimulusSource"/> for the reason
    /// <see cref="IMultitoneStimulus"/> is.</strong> A headless scenario states its own frequency
    /// and level and does not need to ask; it is the interactive panel that has to range its
    /// controls before the user types anything. A source that cannot answer is still a perfectly
    /// good source for every scenario, and asking is how a caller finds out before it starts.
    /// </para>
    /// <para>
    /// <strong><see cref="ReadLimits"/> does not throw.</strong> Its whole job is to report what
    /// the source will and will not answer for, and an exception is a poor way to say "this one
    /// firmware does not accept that query". What it must do instead is leave the instrument in the
    /// state it found it — including its error queue, which is the part that bites.
    /// </para>
    /// </remarks>
    public interface IStimulusLimits
    {
        /// <summary>
        /// Asks the source what it can produce.
        /// </summary>
        /// <returns>
        /// The limits, with <see cref="double.NaN"/> for anything the source would not answer for.
        /// Never <c>null</c>.
        /// </returns>
        StimulusLimits ReadLimits();
    }

    /// <summary>
    /// Marks a stimulus source that the shell may discover and offer (issue #393).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The same shape as <c>FrontEndProviderAttribute</c>, and for the same reason.</strong>
    /// <c>REQ-NFR-032</c> requires the application to start with no hardware and no VISA installed,
    /// so the shell cannot reference this assembly at compile time — it references
    /// <c>OpenVSA.Hal.Visa</c>, and <c>REQ-ARC-001</c> bars test infrastructure from becoming a
    /// dependency of the product besides. The shell therefore finds sources the only way it can:
    /// by loading this assembly if it is there and looking for this attribute.
    /// </para>
    /// <para>
    /// <strong>Which means the shell matches this attribute by name, not by type.</strong> There is
    /// no shared assembly for it to match against — that is the point. The cost is that renaming
    /// this attribute, or the members of the interfaces above, breaks a binding no compiler can
    /// see; the guard is a test in the shell's own suite that loads this assembly and asserts every
    /// member it late-binds is still here, naming the one that is not.
    /// </para>
    /// </remarks>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
    public sealed class StimulusProviderAttribute : Attribute
    {
        /// <summary>Marks a source with the name to offer it under.</summary>
        /// <param name="displayName">Name shown in the shell.</param>
        /// <exception cref="ArgumentException"><paramref name="displayName"/> is missing.</exception>
        public StimulusProviderAttribute(string displayName)
        {
            if (string.IsNullOrEmpty(displayName))
            {
                throw new ArgumentException("A display name is required.", nameof(displayName));
            }

            DisplayName = displayName;
        }

        /// <summary>Name shown in the shell.</summary>
        public string DisplayName { get; }

        /// <summary>
        /// Whether the source has to be told a resource string before it can connect.
        /// </summary>
        /// <remarks>
        /// A source that needs one is constructed from it; one that does not is constructed with no
        /// arguments. Declared here rather than inferred from the available constructors, because
        /// a source may perfectly well have both and the difference is what the panel asks the user
        /// for.
        /// </remarks>
        public bool RequiresResource { get; set; }

        /// <summary>The default resource to offer, for a source that needs one.</summary>
        /// <remarks>
        /// Offered as a starting point in the panel, not used silently. The address of an instrument
        /// on a bench moves, and a wrong one fails in a way that reads exactly like a powered-off
        /// instrument, so what the panel shows is what it will open — visible and editable.
        /// </remarks>
        public string DefaultResource { get; set; }
    }
}
