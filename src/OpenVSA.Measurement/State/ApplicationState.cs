using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace OpenVSA.Measurement.State
{
    /// <summary>
    /// Marks a property as bookkeeping about the file rather than a setting in it.
    /// </summary>
    /// <remarks>
    /// <c>REQ-STA-001</c> and <c>REQ-STA-005</c> are both verified by walking the state model, and
    /// both are about settings. A schema version is not a setting, and neither is the timestamp the
    /// writer stamps or the record of members from a later schema — treating them as such would
    /// have the factory preset asked to restore a "default" write time. The attribute says which is
    /// which once, here, rather than leaving each test to keep its own list of exemptions.
    /// </remarks>
    [AttributeUsage(AttributeTargets.Property, Inherited = false)]
    public sealed class NotASettingAttribute : Attribute
    {
    }

    /// <summary>
    /// A saved setup: every measurement context's settings, and the schema they were written under.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The container carries the schema version rather than each measurement carrying its own, so
    /// there is one answer to "what shape is this file" and migration has one place to happen
    /// (<c>REQ-STA-003</c>).
    /// </para>
    /// <para>
    /// <see cref="UnknownMembersJson"/> is what makes the format forward-compatible in the way the
    /// requirement asks: a file written by later software, loaded and saved by this one, comes back
    /// with its unrecognised members intact rather than quietly stripped. Losing them would make an
    /// older build a one-way door for anyone sharing setups.
    /// </para>
    /// </remarks>
    public sealed class ApplicationState
    {
        /// <summary>The schema version this software writes.</summary>
        public const int CurrentSchemaVersion = 4;

        /// <summary>The oldest schema version this software can read.</summary>
        public const int OldestReadableSchemaVersion = 1;

        /// <summary>Schema version of this state.</summary>
        [NotASetting]
        public int SchemaVersion { get; set; } = CurrentSchemaVersion;

        /// <summary>What wrote it, for support.</summary>
        [NotASetting]
        public string WrittenBy { get; set; } = "OpenVSA";

        /// <summary>When it was written, as a round-trip UTC string.</summary>
        [NotASetting]
        public string WrittenUtc { get; set; } = string.Empty;

        /// <summary>The measurement contexts.</summary>
        public List<MeasurementState> Measurements { get; set; } = new List<MeasurementState>();

        /// <summary>
        /// Members the loader did not recognise, as JSON, kept so a round-trip does not lose them.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Held as text rather than as a parsed tree so that this model stays free of any
        /// particular JSON library — the shape of the state is a measurement concern and the
        /// encoding is not. <see cref="StateFile"/> fills it on load and merges it back on save,
        /// at whatever depth the unrecognised members were found.
        /// </para>
        /// <para>
        /// Empty for a state built in memory, and empty for a file this software fully understands.
        /// </para>
        /// </remarks>
        [NotASetting]
        public string UnknownMembersJson { get; set; } = string.Empty;

        /// <summary>The context names this state carries, in order.</summary>
        /// <remarks>
        /// A method rather than a property, so that neither the serialiser nor the walk that
        /// enumerates the settings model mistakes a derived view for a setting of its own.
        /// </remarks>
        public IReadOnlyList<string> ContextNames() =>
            Measurements.Select(m => m.ContextName).ToList();

        /// <summary>
        /// The measurement for a context, or <c>null</c> if this state has none.
        /// </summary>
        /// <param name="contextName">The context name; matched exactly.</param>
        /// <remarks>
        /// Ordinal comparison, not culture-aware: a context called "I" must not match one called
        /// "ı" because the state happened to be recalled on a Turkish system.
        /// </remarks>
        public MeasurementState For(string contextName) =>
            Measurements.FirstOrDefault(
                m => string.Equals(m.ContextName, contextName, StringComparison.Ordinal));

        /// <summary>A state holding one default measurement.</summary>
        /// <param name="contextName">The context to name it after.</param>
        public static ApplicationState Default(string contextName = "Measurement 1") =>
            new ApplicationState
            {
                Measurements = { new MeasurementState { ContextName = contextName } },
            };

        /// <inheritdoc />
        public override string ToString() =>
            "state v" + SchemaVersion.ToString(CultureInfo.InvariantCulture) + " with " +
            Measurements.Count.ToString(CultureInfo.CurrentCulture) + " measurement(s)";
    }
}
