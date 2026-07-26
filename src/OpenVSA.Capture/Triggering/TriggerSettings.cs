using System;
using System.Globalization;
using OpenVSA.Hal;

namespace OpenVSA.Capture.Triggering
{
    /// <summary>
    /// The three hold-off styles of <c>REQ-TRG-003</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// These are not three ways of saying the same thing. <see cref="Conventional"/> blanks for a
    /// fixed time whatever the signal does, which is what you want against a burst whose internal
    /// structure would otherwise re-trigger. <see cref="BelowLevel"/> and <see cref="AboveLevel"/>
    /// make re-arming conditional on the signal, which is what you want when the interval between
    /// events is not known in advance — a fixed window either misses events or lets them through,
    /// and you cannot pick the right one without already knowing the answer.
    /// </para>
    /// </remarks>
    public enum HoldoffStyle
    {
        /// <summary>A fixed blanking window after each trigger.</summary>
        Conventional = 0,

        /// <summary>The signal must stay below the trigger level for the whole hold-off.</summary>
        BelowLevel,

        /// <summary>The signal must stay above the trigger level for the whole hold-off.</summary>
        AboveLevel,
    }

    /// <summary>
    /// How a trigger is armed and where the record sits relative to it
    /// (<c>REQ-TRG-001</c>, <c>REQ-TRG-002</c>, <c>REQ-TRG-003</c>).
    /// </summary>
    /// <remarks>
    /// Immutable, because a trigger search is run against a settings object and a set of settings
    /// that changed halfway through a record would produce trigger instants that no configuration
    /// ever asked for.
    /// </remarks>
    public sealed class TriggerSettings
    {
        /// <summary>Creates trigger settings.</summary>
        /// <param name="style">How the trigger is armed.</param>
        /// <param name="levelVolts">
        /// Magnitude the signal must cross, in volts. Used by <see cref="TriggerStyle.Level"/>.
        /// </param>
        /// <param name="risingEdge">Whether the trigger fires on the rising crossing.</param>
        /// <param name="delaySeconds">
        /// Where the record starts relative to the trigger. Positive waits; <strong>negative is
        /// pre-trigger</strong> (<c>REQ-TRG-002</c>).
        /// </param>
        /// <param name="holdoff">Hold-off style.</param>
        /// <param name="holdoffSeconds">Hold-off duration; must not be negative.</param>
        /// <param name="periodSeconds">Period, for <see cref="TriggerStyle.Periodic"/>.</param>
        /// <exception cref="ArgumentOutOfRangeException">A value is out of range.</exception>
        public TriggerSettings(
            TriggerStyle style = TriggerStyle.Immediate,
            double levelVolts = 0.0,
            bool risingEdge = true,
            double delaySeconds = 0.0,
            HoldoffStyle holdoff = HoldoffStyle.Conventional,
            double holdoffSeconds = 0.0,
            double periodSeconds = 1e-3)
        {
            if (double.IsNaN(delaySeconds) || double.IsInfinity(delaySeconds))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(delaySeconds), delaySeconds, "Trigger delay must be finite.");
            }

            // REQ-TRG-003 says so in as many words, and the reason is that there is nothing for a
            // negative hold-off to mean: it would have to re-arm the trigger before it fired.
            if (holdoffSeconds < 0.0 || double.IsNaN(holdoffSeconds))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(holdoffSeconds), holdoffSeconds,
                    "Hold-off cannot be negative: it is a time to wait after a trigger, and there " +
                    "is no such thing as waiting before one.");
            }

            if (levelVolts < 0.0 || double.IsNaN(levelVolts))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(levelVolts), levelVolts,
                    "The trigger level is a magnitude, so it cannot be negative.");
            }

            if (style == TriggerStyle.Periodic && !(periodSeconds > 0.0))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(periodSeconds), periodSeconds, "A period must be positive.");
            }

            Style = style;
            LevelVolts = levelVolts;
            RisingEdge = risingEdge;
            DelaySeconds = delaySeconds;
            Holdoff = holdoff;
            HoldoffSeconds = holdoffSeconds;
            PeriodSeconds = periodSeconds;
        }

        /// <summary>How the trigger is armed.</summary>
        public TriggerStyle Style { get; }

        /// <summary>Magnitude the signal must cross, in volts.</summary>
        public double LevelVolts { get; }

        /// <summary>Whether the trigger fires on the rising crossing.</summary>
        public bool RisingEdge { get; }

        /// <summary>Where the record starts relative to the trigger; negative is pre-trigger.</summary>
        public double DelaySeconds { get; }

        /// <summary>Hold-off style.</summary>
        public HoldoffStyle Holdoff { get; }

        /// <summary>Hold-off duration, in seconds.</summary>
        public double HoldoffSeconds { get; }

        /// <summary>Period, for a periodic trigger.</summary>
        public double PeriodSeconds { get; }

        /// <summary>Whether the record starts before the trigger event.</summary>
        public bool IsPreTrigger => DelaySeconds < 0.0;

        /// <summary>
        /// The delay in samples, at a given rate.
        /// </summary>
        /// <param name="sampleRateHz">Sample rate, in hertz.</param>
        /// <returns>Samples to move the record start by; negative for pre-trigger.</returns>
        public int DelaySamples(double sampleRateHz) =>
            (int)Math.Round(DelaySeconds * sampleRateHz);

        /// <summary>The hold-off in samples, at a given rate.</summary>
        /// <param name="sampleRateHz">Sample rate, in hertz.</param>
        public int HoldoffSamples(double sampleRateHz) =>
            (int)Math.Round(HoldoffSeconds * sampleRateHz);

        /// <inheritdoc />
        public override string ToString() =>
            Style + " trigger, delay " +
            DelaySeconds.ToString("G4", CultureInfo.CurrentCulture) + " s, " +
            Holdoff + " hold-off " +
            HoldoffSeconds.ToString("G4", CultureInfo.CurrentCulture) + " s";
    }
}
