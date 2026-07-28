using System;
using System.Collections.Generic;
using OpenVSA.Core;
using OpenVSA.Dsp.Spectrum;

namespace OpenVSA.Personality
{
    /// <summary>
    /// A standard-specific measurement, delivered as a plug-in (<c>REQ-ARC-003</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The requirement is that no personality shall require modification of L2–L4 code to be added.
    /// This interface is the whole of what a personality must implement, and the registry finds it
    /// by attribute — so adding GSM, W-CDMA or LTE means dropping an assembly into
    /// <c>Personalities\</c>, and nothing in the host changes.
    /// </para>
    /// <para>
    /// <strong>It consumes an <see cref="IqBlock"/> and produces named readings.</strong> Not a
    /// window, not a control, not a trace: a personality that returned a view would have to know
    /// what the display looks like, and the next one would have to know it too. The measurement is
    /// the plug-in's business and the presentation is the shell's.
    /// </para>
    /// </remarks>
    public interface IMeasurementPersonality
    {
        /// <summary>The name the measurement-type selector shows.</summary>
        string DisplayName { get; }

        /// <summary>The standard this implements, for the selector to group by.</summary>
        string Standard { get; }

        /// <summary>The revision of that standard, so a result can say what it was measured against.</summary>
        /// <remarks>
        /// <c>REQ-PER-011</c> requires a declared standard revision and documented deviations. A
        /// personality that cannot say which revision it implements cannot be checked against one.
        /// </remarks>
        string StandardRevision { get; }

        /// <summary>Whether this personality can measure the block it is offered.</summary>
        /// <param name="block">The acquisition.</param>
        /// <remarks>
        /// Asked before <see cref="Measure"/> so the selector can offer only what will work, rather
        /// than offering everything and erroring on the ones that cannot.
        /// </remarks>
        bool CanMeasure(IqBlock block);

        /// <summary>Measures a block.</summary>
        /// <param name="block">The acquisition.</param>
        /// <returns>Named readings, in the order the personality wants them shown.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="block"/> is null.</exception>
        IReadOnlyList<PersonalityReading> Measure(IqBlock block);
    }

    /// <summary>One named result from a personality.</summary>
    /// <remarks>
    /// A name, a value and a unit, because that is what a table of results needs and no more. A
    /// personality returning a formatted string would take the shell's formatting decisions away
    /// from it, and the same reading would be printed differently by every plug-in.
    /// </remarks>
    public sealed class PersonalityReading
    {
        /// <summary>Creates a reading.</summary>
        /// <param name="name">What was measured.</param>
        /// <param name="value">The value.</param>
        /// <param name="unit">Its unit, or empty when dimensionless.</param>
        /// <exception cref="ArgumentNullException"><paramref name="name"/> is null.</exception>
        public PersonalityReading(string name, double value, string unit)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            Value = value;
            Unit = unit ?? string.Empty;
        }

        /// <summary>What was measured.</summary>
        public string Name { get; }

        /// <summary>The value.</summary>
        public double Value { get; }

        /// <summary>Its unit.</summary>
        public string Unit { get; }

        /// <inheritdoc />
        public override string ToString() =>
            Name + " " + Value.ToString("G6") + (Unit.Length == 0 ? string.Empty : " " + Unit);
    }

    /// <summary>Marks a type the personality registry should instantiate.</summary>
    /// <remarks>
    /// The same discovery rule as front ends (<c>REQ-HAL-003</c>) and FFT providers
    /// (<c>REQ-NFR-004</c>): adding one never means editing a registry in core code. Three plug-in
    /// points, one convention.
    /// </remarks>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
    public sealed class MeasurementPersonalityAttribute : Attribute
    {
    }
}
