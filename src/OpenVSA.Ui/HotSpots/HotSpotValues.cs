using System;
using System.Collections.Generic;
using System.Globalization;

namespace OpenVSA.Ui.HotSpots
{
    /// <summary>
    /// An editable quantity behind a hot spot (<c>REQ-UI-042</c>).
    /// </summary>
    /// <remarks>
    /// The interaction — hover, click, wheel, type, dialog — is the same whatever is being edited,
    /// and only the quantity differs. Separating them is what lets a scale in decibels, a frequency
    /// in hertz and a choice from a list all be hot spots without <see cref="HotSpot"/> knowing
    /// which of them it is showing.
    /// </remarks>
    public interface IHotSpotValue
    {
        /// <summary>The value, as it is displayed.</summary>
        string Text { get; }

        /// <summary>
        /// Moves the value by a number of steps, as a wheel notch or an arrow key asks.
        /// </summary>
        /// <param name="steps">Steps to move; negative moves down.</param>
        /// <returns><c>true</c> if the value changed.</returns>
        bool TryAdjust(int steps);

        /// <summary>
        /// Sets the value from typed or pasted text.
        /// </summary>
        /// <param name="text">The text.</param>
        /// <returns><c>true</c> if the text was understood and the value changed.</returns>
        bool TrySet(string text);
    }

    /// <summary>
    /// A hot spot over a number, with a step, limits, and its own formatting and parsing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Formatting and parsing are supplied rather than assumed, because the quantities this covers
    /// are not written the same way: a frequency is <c>1.5 GHz</c>, a scale is <c>10 dB/div</c> and
    /// a time is <c>80 us</c>. Both directions are needed — a hot spot the user can read but not
    /// type back into would fail the requirement's "typing adjusts the value" outright.
    /// </para>
    /// <para>
    /// Adjustment is additive by a fixed step, which is what an arrow key on a linear quantity
    /// should do. A quantity that wants a proportional step supplies a
    /// <see cref="ProportionalStep"/> instead, so that stepping a 1 kHz bandwidth does not have to
    /// use the same increment as stepping a 10 MHz one.
    /// </para>
    /// </remarks>
    public sealed class NumericHotSpotValue : IHotSpotValue
    {
        private readonly Func<double, string> _format;
        private readonly Func<string, double?> _parse;

        private double _value;

        /// <summary>Creates a numeric hot spot value.</summary>
        /// <param name="value">The initial value.</param>
        /// <param name="step">Additive step per wheel notch or arrow key; must be positive.</param>
        /// <param name="format">Formats the value for display.</param>
        /// <param name="parse">Parses typed text, returning <c>null</c> if it is not understood.</param>
        /// <exception cref="ArgumentNullException"><paramref name="format"/> or <paramref name="parse"/> is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="step"/> is not positive.</exception>
        public NumericHotSpotValue(
            double value, double step, Func<double, string> format, Func<string, double?> parse)
        {
            if (format == null)
            {
                throw new ArgumentNullException(nameof(format));
            }

            if (parse == null)
            {
                throw new ArgumentNullException(nameof(parse));
            }

            if (!(step > 0.0) || double.IsInfinity(step))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(step), step, "A step must be positive and finite.");
            }

            _value = value;
            _format = format;
            _parse = parse;
            Step = step;
            Minimum = double.NegativeInfinity;
            Maximum = double.PositiveInfinity;
        }

        /// <summary>The value.</summary>
        public double Value
        {
            get { return _value; }

            set { _value = Clamp(value); }
        }

        /// <summary>Additive step per wheel notch or arrow key.</summary>
        public double Step { get; set; }

        /// <summary>
        /// Fractional step per notch, or 0 for additive stepping.
        /// </summary>
        /// <remarks>
        /// For quantities spanning decades — a resolution bandwidth runs from millihertz to
        /// megahertz — where a fixed increment is either uselessly small at one end or uselessly
        /// coarse at the other.
        /// </remarks>
        public double ProportionalStep { get; set; }

        /// <summary>Lower limit; the value never goes below it.</summary>
        public double Minimum { get; set; }

        /// <summary>Upper limit; the value never goes above it.</summary>
        public double Maximum { get; set; }

        /// <inheritdoc />
        public string Text => _format(_value);

        /// <inheritdoc />
        public bool TryAdjust(int steps)
        {
            if (steps == 0)
            {
                return false;
            }

            double moved = ProportionalStep > 0.0
                ? _value * Math.Pow(1.0 + ProportionalStep, steps)
                : _value + steps * Step;

            double clamped = Clamp(moved);

            if (clamped.Equals(_value))
            {
                return false;
            }

            _value = clamped;
            return true;
        }

        /// <inheritdoc />
        public bool TrySet(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            double? parsed = _parse(text);

            if (parsed == null)
            {
                return false;
            }

            double clamped = Clamp(parsed.Value);

            if (clamped.Equals(_value))
            {
                return false;
            }

            _value = clamped;
            return true;
        }

        /// <summary>A hot spot over a level in dBm.</summary>
        /// <param name="dbm">The initial level.</param>
        /// <param name="step">Step per notch, in dB.</param>
        public static NumericHotSpotValue Decibels(double dbm, double step = 1.0) =>
            new NumericHotSpotValue(
                dbm,
                step,
                v => v.ToString("0.00", CultureInfo.CurrentCulture) + " dBm",
                text =>
                {
                    double parsed;
                    return EngineeringText.TryParseDecibels(text, out parsed)
                        ? parsed
                        : (double?)null;
                });

        /// <summary>A hot spot over a frequency in hertz.</summary>
        /// <param name="hertz">The initial frequency.</param>
        /// <param name="step">Step per notch, in hertz.</param>
        public static NumericHotSpotValue Frequency(double hertz, double step) =>
            new NumericHotSpotValue(
                hertz,
                step,
                v => EngineeringText.Frequency(v, 6),
                text =>
                {
                    double parsed;
                    return EngineeringText.TryParseFrequency(text, out parsed)
                        ? parsed
                        : (double?)null;
                });

        /// <summary>A hot spot over an interval in seconds.</summary>
        /// <param name="seconds">The initial interval.</param>
        /// <param name="step">Step per notch, in seconds.</param>
        public static NumericHotSpotValue Time(double seconds, double step) =>
            new NumericHotSpotValue(
                seconds,
                step,
                EngineeringText.Time,
                text =>
                {
                    // Times are typed with the same SI multipliers as frequencies, and share the
                    // parser; only the unit differs and it is optional.
                    double parsed;
                    return EngineeringText.TryParseFrequency(TrimSeconds(text), out parsed)
                        ? parsed
                        : (double?)null;
                });

        private static string TrimSeconds(string text)
        {
            string trimmed = text.Trim();

            return trimmed.EndsWith("s", StringComparison.OrdinalIgnoreCase) &&
                   trimmed.Length > 1
                ? trimmed.Substring(0, trimmed.Length - 1)
                : trimmed;
        }

        private double Clamp(double value)
        {
            if (double.IsNaN(value))
            {
                return _value;
            }

            if (value < Minimum)
            {
                return Minimum;
            }

            return value > Maximum ? Maximum : value;
        }
    }

    /// <summary>
    /// A hot spot over a choice from a fixed list — a trace format, a trigger channel.
    /// </summary>
    /// <remarks>
    /// Stepping wraps rather than stopping at the ends. A list of formats has no natural first or
    /// last, and a wheel that stops dead at "IQ" reads as a stuck control rather than as a
    /// boundary.
    /// </remarks>
    public sealed class ChoiceHotSpotValue : IHotSpotValue
    {
        private readonly List<string> _options;

        private int _index;

        /// <summary>Creates a choice.</summary>
        /// <param name="options">The options, in the order stepping visits them.</param>
        /// <param name="index">The initially selected option.</param>
        /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
        /// <exception cref="ArgumentException"><paramref name="options"/> is empty.</exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is outside the list.</exception>
        public ChoiceHotSpotValue(IEnumerable<string> options, int index = 0)
        {
            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            _options = new List<string>(options);

            if (_options.Count == 0)
            {
                throw new ArgumentException("A choice needs at least one option.", nameof(options));
            }

            if (index < 0 || index >= _options.Count)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(index), index, "Outside the list of options.");
            }

            _index = index;
        }

        /// <summary>The options, in order.</summary>
        public IReadOnlyList<string> Options => _options;

        /// <summary>The selected option's position in the list.</summary>
        /// <exception cref="ArgumentOutOfRangeException">The value is outside the list.</exception>
        public int SelectedIndex
        {
            get { return _index; }

            set
            {
                if (value < 0 || value >= _options.Count)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(value), value, "Outside the list of options.");
                }

                _index = value;
            }
        }

        /// <inheritdoc />
        public string Text => _options[_index];

        /// <inheritdoc />
        public bool TryAdjust(int steps)
        {
            if (steps == 0 || _options.Count < 2)
            {
                return false;
            }

            // Modulo twice, because C#'s remainder keeps the sign of the dividend and a negative
            // index is not an option position.
            int moved = ((_index + steps) % _options.Count + _options.Count) % _options.Count;

            if (moved == _index)
            {
                return false;
            }

            _index = moved;
            return true;
        }

        /// <inheritdoc />
        public bool TrySet(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            string trimmed = text.Trim();

            for (int i = 0; i < _options.Count; i++)
            {
                if (string.Equals(_options[i], trimmed, StringComparison.OrdinalIgnoreCase))
                {
                    if (i == _index)
                    {
                        return false;
                    }

                    _index = i;
                    return true;
                }
            }

            return false;
        }
    }
}
