using System;
using System.Collections.Generic;

namespace OpenVSA.Dsp.Spectrum
{
    /// <summary>
    /// The amplitude units of <c>REQ-AMP-002</c>.
    /// </summary>
    /// <remarks>
    /// Three families, and the distinction between them is where the mistakes live. The
    /// <c>dBm</c> and <c>W</c> readings are <em>powers</em> and so depend on the reference
    /// impedance; the <c>dBV</c> family and the volt readings are <em>voltages</em> and do not.
    /// Converting between the families without saying which impedance is meant is the error
    /// <c>REQ-AMP-002</c>'s "explicitly" is written against.
    /// </remarks>
    public enum AmplitudeUnit
    {
        /// <summary>Power in decibels relative to a milliwatt.</summary>
        Dbm = 0,

        /// <summary>Power in watts.</summary>
        Watts,

        /// <summary>Voltage in decibels relative to a millivolt RMS.</summary>
        DbMillivolts,

        /// <summary>Voltage in decibels relative to a microvolt RMS.</summary>
        DbMicrovolts,

        /// <summary>Voltage in decibels relative to a volt RMS.</summary>
        DbVolts,

        /// <summary>Volts peak.</summary>
        VoltsPeak,

        /// <summary>Volts RMS.</summary>
        VoltsRms,
    }

    /// <summary>
    /// Converts between the amplitude units, through the reference impedance
    /// (<c>REQ-AMP-002</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Every conversion involving power takes the impedance as an argument.</strong> There
    /// is no default parameter and no ambient setting here: a conversion that silently assumed
    /// 50 Ω would read 1.76 dB high on a 75 Ω measurement, which is small enough to be mistaken
    /// for a calibration error and large enough to matter.
    /// </para>
    /// <para>
    /// <strong>Volts peak is the internal currency</strong>, because that is what a
    /// <see cref="SpectrumFrame"/> holds and what <c>REQ-AMP-001</c>'s chain produces. Everything
    /// else is expressed as a conversion to and from it, so there is one hub rather than a mesh of
    /// pairwise conversions that can disagree.
    /// </para>
    /// <para>
    /// <strong>The decibel-volt family is referred to RMS</strong>, as the industry uses it:
    /// 0 dBV is 1 V RMS, not 1 V peak. Referring them to peak would put every reading 3.01 dB out
    /// against every other instrument.
    /// </para>
    /// </remarks>
    public static class AmplitudeUnits
    {
        /// <summary>Reference impedances a measurement is normally made into.</summary>
        /// <remarks>
        /// 50 Ω for radio, 75 Ω for video and cable. Others are legal —
        /// <see cref="Convert(double, AmplitudeUnit, AmplitudeUnit, double)"/> takes any positive
        /// impedance — these are the two a selector offers.
        /// </remarks>
        public static IReadOnlyList<double> CommonImpedancesOhms { get; } = new[] { 50.0, 75.0 };

        /// <summary>Every unit, in the order a selector offers them.</summary>
        public static IReadOnlyList<AmplitudeUnit> All { get; } =
            (AmplitudeUnit[])Enum.GetValues(typeof(AmplitudeUnit));

        /// <summary>The symbol a unit is displayed with.</summary>
        /// <param name="unit">The unit.</param>
        /// <exception cref="ArgumentOutOfRangeException">Not a known unit.</exception>
        public static string SymbolOf(AmplitudeUnit unit)
        {
            switch (unit)
            {
                case AmplitudeUnit.Dbm: return "dBm";
                case AmplitudeUnit.Watts: return "W";
                case AmplitudeUnit.DbMillivolts: return "dBmV";
                case AmplitudeUnit.DbMicrovolts: return "dBµV";
                case AmplitudeUnit.DbVolts: return "dBV";
                case AmplitudeUnit.VoltsPeak: return "V pk";
                case AmplitudeUnit.VoltsRms: return "V rms";

                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(unit), unit, "Not a known amplitude unit.");
            }
        }

        /// <summary>
        /// Whether a unit expresses power, and so depends on the reference impedance.
        /// </summary>
        /// <param name="unit">The unit.</param>
        /// <exception cref="ArgumentOutOfRangeException">Not a known unit.</exception>
        public static bool IsPower(AmplitudeUnit unit)
        {
            // Called for its argument check as much as its answer.
            SymbolOf(unit);

            return unit == AmplitudeUnit.Dbm || unit == AmplitudeUnit.Watts;
        }

        /// <summary>
        /// Converts a value between units.
        /// </summary>
        /// <param name="value">The value.</param>
        /// <param name="from">The unit it is in.</param>
        /// <param name="to">The unit to express it in.</param>
        /// <param name="referenceImpedanceOhms">Reference impedance; must be positive.</param>
        /// <exception cref="ArgumentOutOfRangeException">A value or unit is out of range.</exception>
        public static double Convert(
            double value, AmplitudeUnit from, AmplitudeUnit to, double referenceImpedanceOhms)
        {
            if (!(referenceImpedanceOhms > 0.0) || double.IsInfinity(referenceImpedanceOhms))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(referenceImpedanceOhms), referenceImpedanceOhms,
                    "A reference impedance must be positive and finite.");
            }

            if (from == to)
            {
                return value;
            }

            return FromVoltsPeak(ToVoltsPeak(value, from, referenceImpedanceOhms), to, referenceImpedanceOhms);
        }

        /// <summary>
        /// Expresses a value in volts peak — the internal currency.
        /// </summary>
        /// <param name="value">The value.</param>
        /// <param name="unit">The unit it is in.</param>
        /// <param name="referenceImpedanceOhms">Reference impedance; must be positive.</param>
        /// <exception cref="ArgumentOutOfRangeException">A value or unit is out of range.</exception>
        public static double ToVoltsPeak(double value, AmplitudeUnit unit, double referenceImpedanceOhms)
        {
            if (!(referenceImpedanceOhms > 0.0) || double.IsInfinity(referenceImpedanceOhms))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(referenceImpedanceOhms), referenceImpedanceOhms,
                    "A reference impedance must be positive and finite.");
            }

            switch (unit)
            {
                case AmplitudeUnit.VoltsPeak:
                    return value;

                case AmplitudeUnit.VoltsRms:
                    return value * Math.Sqrt(2.0);

                case AmplitudeUnit.DbVolts:
                    return Math.Pow(10.0, value / 20.0) * Math.Sqrt(2.0);

                case AmplitudeUnit.DbMillivolts:
                    return Math.Pow(10.0, value / 20.0) * 1e-3 * Math.Sqrt(2.0);

                case AmplitudeUnit.DbMicrovolts:
                    return Math.Pow(10.0, value / 20.0) * 1e-6 * Math.Sqrt(2.0);

                case AmplitudeUnit.Watts:
                    // P = V_pk² / 2R, so V_pk = sqrt(2·R·P). The impedance is here and not
                    // implied, which is the whole of REQ-AMP-002's "explicitly".
                    return value <= 0.0 ? 0.0 : Math.Sqrt(2.0 * referenceImpedanceOhms * value);

                case AmplitudeUnit.Dbm:
                    return Math.Sqrt(
                        2.0 * referenceImpedanceOhms * Math.Pow(10.0, (value - 30.0) / 10.0));

                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(unit), unit, "Not a known amplitude unit.");
            }
        }

        /// <summary>
        /// Expresses volts peak in a unit.
        /// </summary>
        /// <param name="voltsPeak">The amplitude, in volts peak.</param>
        /// <param name="unit">The unit to express it in.</param>
        /// <param name="referenceImpedanceOhms">Reference impedance; must be positive.</param>
        /// <exception cref="ArgumentOutOfRangeException">A value or unit is out of range.</exception>
        /// <remarks>
        /// An amplitude of zero has no logarithm; the decibel units return
        /// <see cref="AmplitudeScale.FloorDbm"/> rather than negative infinity, so a blank bin
        /// plots at the bottom of the graticule instead of taking the axis with it.
        /// </remarks>
        public static double FromVoltsPeak(
            double voltsPeak, AmplitudeUnit unit, double referenceImpedanceOhms)
        {
            if (!(referenceImpedanceOhms > 0.0) || double.IsInfinity(referenceImpedanceOhms))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(referenceImpedanceOhms), referenceImpedanceOhms,
                    "A reference impedance must be positive and finite.");
            }

            double rms = voltsPeak / Math.Sqrt(2.0);
            double watts = voltsPeak * voltsPeak / (2.0 * referenceImpedanceOhms);

            switch (unit)
            {
                case AmplitudeUnit.VoltsPeak: return voltsPeak;
                case AmplitudeUnit.VoltsRms: return rms;
                case AmplitudeUnit.Watts: return watts;

                case AmplitudeUnit.DbVolts:
                    return rms > 0.0 ? 20.0 * Math.Log10(rms) : AmplitudeScale.FloorDbm;

                case AmplitudeUnit.DbMillivolts:
                    return rms > 0.0 ? 20.0 * Math.Log10(rms / 1e-3) : AmplitudeScale.FloorDbm;

                case AmplitudeUnit.DbMicrovolts:
                    return rms > 0.0 ? 20.0 * Math.Log10(rms / 1e-6) : AmplitudeScale.FloorDbm;

                case AmplitudeUnit.Dbm:
                    return watts > 0.0
                        ? 10.0 * Math.Log10(watts) + 30.0
                        : AmplitudeScale.FloorDbm;

                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(unit), unit, "Not a known amplitude unit.");
            }
        }

        /// <summary>
        /// How much a power reading of a fixed voltage moves when the reference impedance changes.
        /// </summary>
        /// <param name="fromOhms">The impedance the reading was made against.</param>
        /// <param name="toOhms">The impedance to express it against.</param>
        /// <returns>The change, in dB; negative when moving to a higher impedance.</returns>
        /// <exception cref="ArgumentOutOfRangeException">An impedance is not positive.</exception>
        /// <remarks>
        /// <c>10·log10(from/to)</c>. Between 50 Ω and 75 Ω that is −1.76 dB, the figure
        /// <c>REQ-AMP-002</c>'s criterion names — <em>down</em>, because the same voltage across a
        /// larger resistance dissipates less power. Getting the sign the other way round is the
        /// easy mistake, and it is the one a bare magnitude in a test would not catch.
        /// </remarks>
        public static double ImpedanceChangeDb(double fromOhms, double toOhms)
        {
            if (!(fromOhms > 0.0) || !(toOhms > 0.0))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(fromOhms), fromOhms, "Reference impedances must be positive.");
            }

            return 10.0 * Math.Log10(fromOhms / toOhms);
        }
    }
}
