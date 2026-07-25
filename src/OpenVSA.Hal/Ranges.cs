using System;
using System.Globalization;

namespace OpenVSA.Hal
{
    /// <summary>An inclusive frequency range in hertz.</summary>
    public readonly struct FrequencyRange
    {
        /// <summary>Lowest supported frequency, in hertz.</summary>
        public readonly double MinHz;

        /// <summary>Highest supported frequency, in hertz.</summary>
        public readonly double MaxHz;

        /// <summary>Creates a range.</summary>
        /// <param name="minHz">Lowest supported frequency; must be finite and not above <paramref name="maxHz"/>.</param>
        /// <param name="maxHz">Highest supported frequency; must be finite.</param>
        /// <exception cref="ArgumentOutOfRangeException">The bounds are not finite or are inverted.</exception>
        public FrequencyRange(double minHz, double maxHz)
        {
            if (double.IsNaN(minHz) || double.IsInfinity(minHz) ||
                double.IsNaN(maxHz) || double.IsInfinity(maxHz))
            {
                throw new ArgumentOutOfRangeException(nameof(minHz), "Range bounds must be finite.");
            }

            if (minHz > maxHz)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(minHz), minHz, "Range minimum must not exceed its maximum.");
            }

            MinHz = minHz;
            MaxHz = maxHz;
        }

        /// <summary>Whether <paramref name="hz"/> lies within this range, inclusive.</summary>
        /// <param name="hz">Frequency to test, in hertz.</param>
        public bool Contains(double hz) => hz >= MinHz && hz <= MaxHz;

        /// <summary>Returns <paramref name="hz"/> clamped into this range.</summary>
        /// <param name="hz">Frequency to clamp, in hertz.</param>
        public double Clamp(double hz) => hz < MinHz ? MinHz : (hz > MaxHz ? MaxHz : hz);

        /// <inheritdoc />
        public override string ToString() =>
            string.Format(CultureInfo.InvariantCulture, "{0} Hz to {1} Hz", MinHz, MaxHz);
    }

    /// <summary>An inclusive amplitude range in dBm.</summary>
    public readonly struct AmplitudeRange
    {
        /// <summary>Lowest supported level, in dBm.</summary>
        public readonly double MinDbm;

        /// <summary>Highest supported level, in dBm.</summary>
        public readonly double MaxDbm;

        /// <summary>Creates a range.</summary>
        /// <param name="minDbm">Lowest supported level; must be finite and not above <paramref name="maxDbm"/>.</param>
        /// <param name="maxDbm">Highest supported level; must be finite.</param>
        /// <exception cref="ArgumentOutOfRangeException">The bounds are not finite or are inverted.</exception>
        public AmplitudeRange(double minDbm, double maxDbm)
        {
            if (double.IsNaN(minDbm) || double.IsInfinity(minDbm) ||
                double.IsNaN(maxDbm) || double.IsInfinity(maxDbm))
            {
                throw new ArgumentOutOfRangeException(nameof(minDbm), "Range bounds must be finite.");
            }

            if (minDbm > maxDbm)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(minDbm), minDbm, "Range minimum must not exceed its maximum.");
            }

            MinDbm = minDbm;
            MaxDbm = maxDbm;
        }

        /// <summary>Whether <paramref name="dbm"/> lies within this range, inclusive.</summary>
        /// <param name="dbm">Level to test, in dBm.</param>
        public bool Contains(double dbm) => dbm >= MinDbm && dbm <= MaxDbm;

        /// <summary>Returns <paramref name="dbm"/> clamped into this range.</summary>
        /// <param name="dbm">Level to clamp, in dBm.</param>
        public double Clamp(double dbm) => dbm < MinDbm ? MinDbm : (dbm > MaxDbm ? MaxDbm : dbm);

        /// <inheritdoc />
        public override string ToString() =>
            string.Format(CultureInfo.InvariantCulture, "{0} dBm to {1} dBm", MinDbm, MaxDbm);
    }
}
