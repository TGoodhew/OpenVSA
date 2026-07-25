using System;
using System.Globalization;

namespace OpenVSA.Ui
{
    /// <summary>
    /// Reads and writes the engineering-notation quantities a measurement is set up with.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Nobody types 1000000000 into a centre-frequency box. Entry accepts a number followed by an
    /// optional SI multiplier and an optional unit — <c>1.5 GHz</c>, <c>1.5G</c>, <c>1500 MHz</c>,
    /// <c>1.5e9</c> and <c>1500000000</c> are all the same frequency.
    /// </para>
    /// <para>
    /// <strong>The multiplier is case-sensitive; the unit is not.</strong> <c>M</c> is mega and
    /// <c>m</c> is milli, as they are everywhere else in engineering, and both are needed here:
    /// <c>REQ-DSP-021</c> requires resolution bandwidths below 1 Hz to be settable, so <c>mHz</c>
    /// has to mean what it says. Folding case would make <c>10m</c> mean 10 MHz to one user and
    /// 10 mHz to another, with nothing on screen to say which was understood.
    /// </para>
    /// </remarks>
    public static class EngineeringText
    {
        /// <summary>
        /// Parses a frequency, in hertz.
        /// </summary>
        /// <param name="text">Text such as <c>1.5 GHz</c>, <c>1500 MHz</c> or <c>1.5e9</c>.</param>
        /// <param name="hertz">Receives the value in hertz.</param>
        /// <returns><c>true</c> if the text was a frequency.</returns>
        public static bool TryParseFrequency(string text, out double hertz) =>
            TryParseQuantity(text, "hz", out hertz);

        /// <summary>
        /// Parses an amplitude, in dBm.
        /// </summary>
        /// <param name="text">Text such as <c>-10</c>, <c>-10 dBm</c> or <c>+20dBm</c>.</param>
        /// <param name="dbm">Receives the value in dBm.</param>
        /// <returns><c>true</c> if the text was an amplitude.</returns>
        /// <remarks>
        /// No SI multiplier: a level is already logarithmic, and "k dBm" is not a quantity. Only
        /// the unit is optional, so that a bare number is accepted from a user who does not want to
        /// type it.
        /// </remarks>
        public static bool TryParseDecibels(string text, out double dbm)
        {
            dbm = 0.0;

            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            string trimmed = text.Trim();

            if (trimmed.EndsWith("dbm", StringComparison.OrdinalIgnoreCase))
            {
                trimmed = trimmed.Substring(0, trimmed.Length - 3).Trim();
            }
            else if (trimmed.EndsWith("db", StringComparison.OrdinalIgnoreCase))
            {
                trimmed = trimmed.Substring(0, trimmed.Length - 2).Trim();
            }

            return double.TryParse(
                trimmed, NumberStyles.Float, CultureInfo.CurrentCulture, out dbm) &&
                !double.IsNaN(dbm) && !double.IsInfinity(dbm);
        }

        /// <summary>
        /// Formats a frequency in engineering notation.
        /// </summary>
        /// <param name="hertz">The frequency, in hertz.</param>
        /// <param name="decimals">Digits after the point; trailing zeros are kept.</param>
        /// <returns>Text such as <c>1.000000 GHz</c>.</returns>
        public static string Frequency(double hertz, int decimals = 3)
        {
            string format = decimals <= 0 ? "0" : "0." + new string('0', decimals);
            double magnitude = Math.Abs(hertz);

            if (magnitude >= 1e9)
            {
                return (hertz / 1e9).ToString(format, CultureInfo.CurrentCulture) + " GHz";
            }

            if (magnitude >= 1e6)
            {
                return (hertz / 1e6).ToString(format, CultureInfo.CurrentCulture) + " MHz";
            }

            if (magnitude >= 1e3)
            {
                return (hertz / 1e3).ToString(format, CultureInfo.CurrentCulture) + " kHz";
            }

            if (magnitude > 0.0 && magnitude < 1.0)
            {
                return (hertz * 1e3).ToString(format, CultureInfo.CurrentCulture) + " mHz";
            }

            return hertz.ToString(format, CultureInfo.CurrentCulture) + " Hz";
        }

        /// <summary>Formats a time in engineering notation.</summary>
        /// <param name="seconds">The interval, in seconds.</param>
        /// <returns>Text such as <c>80 us</c>.</returns>
        public static string Time(double seconds)
        {
            double magnitude = Math.Abs(seconds);

            if (magnitude >= 1.0)
            {
                return seconds.ToString("0.###", CultureInfo.CurrentCulture) + " s";
            }

            if (magnitude >= 1e-3)
            {
                return (seconds * 1e3).ToString("0.###", CultureInfo.CurrentCulture) + " ms";
            }

            if (magnitude >= 1e-6)
            {
                return (seconds * 1e6).ToString("0.###", CultureInfo.CurrentCulture) + " us";
            }

            return (seconds * 1e9).ToString("0.###", CultureInfo.CurrentCulture) + " ns";
        }

        private static bool TryParseQuantity(string text, string unit, out double value)
        {
            value = 0.0;

            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            string trimmed = text.Trim();

            if (trimmed.Length > unit.Length &&
                trimmed.EndsWith(unit, StringComparison.OrdinalIgnoreCase))
            {
                trimmed = trimmed.Substring(0, trimmed.Length - unit.Length).TrimEnd();
            }

            double multiplier = 1.0;

            if (trimmed.Length > 1)
            {
                char last = trimmed[trimmed.Length - 1];
                bool recognised = true;

                switch (last)
                {
                    case 'G':
                    case 'g':
                        multiplier = 1e9;
                        break;

                    case 'M':
                        multiplier = 1e6;
                        break;

                    case 'k':
                    case 'K':
                        multiplier = 1e3;
                        break;

                    case 'm':
                        multiplier = 1e-3;
                        break;

                    case 'u':
                        multiplier = 1e-6;
                        break;

                    default:
                        recognised = false;
                        break;
                }

                if (recognised)
                {
                    trimmed = trimmed.Substring(0, trimmed.Length - 1).TrimEnd();
                }
            }

            if (!double.TryParse(
                trimmed, NumberStyles.Float, CultureInfo.CurrentCulture, out value))
            {
                return false;
            }

            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                return false;
            }

            value *= multiplier;
            return true;
        }
    }
}
