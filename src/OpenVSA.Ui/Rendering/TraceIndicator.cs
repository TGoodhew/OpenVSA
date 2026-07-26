using System;
using System.Collections.Generic;
using System.Globalization;

namespace OpenVSA.Ui.Rendering
{
    /// <summary>
    /// The conditions annotated in the grid's upper-right corner (<c>REQ-UI-041</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Declaration order is display priority.</strong> The requirement lists these in a
    /// specific order and requires that order to be followed when several are true at once, so it
    /// is carried by the enum itself rather than by a table beside it that could drift out of step.
    /// </para>
    /// <para>
    /// The strings these render as are quoted verbatim from the reference product. They are terse
    /// to the point of being cryptic — <c>RNG</c>, <c>EQ</c> — and that is the point: anyone who
    /// has used the original recognises them instantly, and a tidied-up wording would cost that for
    /// nothing.
    /// </para>
    /// </remarks>
    public enum TraceIndicator
    {
        /// <summary>No data has been acquired. Renders <c>NO DATA</c>.</summary>
        NoData = 0,

        /// <summary>The data may not be valid. Renders <c>DATA?</c>.</summary>
        DataQuestionable,

        /// <summary>An input channel is overloaded (<c>REQ-UI-007</c>). Renders <c>OVx</c>.</summary>
        Overload,

        /// <summary>The calibration may not apply. Renders <c>CAL?</c>.</summary>
        CalibrationQuestionable,

        /// <summary>Pulse search found no pulse (<c>REQ-DEM-041</c>). Renders <c>PULSE NOT FOUND</c>.</summary>
        PulseNotFound,

        /// <summary>Sync search found no sync pattern (<c>REQ-DEM-040</c>). Renders <c>SYNC NOT FOUND</c>.</summary>
        SyncNotFound,

        /// <summary>Carrier lock is doubtful (<c>REQ-DEM-036</c>). Renders <c>CARRIER LOCK?</c>.</summary>
        CarrierLock,

        /// <summary>The equaliser is active (<c>REQ-DEM-050</c>). Renders <c>EQ</c>.</summary>
        Equaliser,

        /// <summary>The input range is being adjusted. Renders <c>RNG</c>.</summary>
        Range,

        /// <summary>Every acquired point is being displayed. Renders <c>ALL POINTS</c>.</summary>
        AllPoints,

        /// <summary>The selected channel is not active. Renders <c>INACTIVE CHAN</c>.</summary>
        InactiveChannel,

        /// <summary>The measurement offset may be wrong. Renders <c>MEAS OFFSET?</c>.</summary>
        MeasurementOffset,

        /// <summary>The pulse was too short to measure. Renders <c>PULSE TOO SHORT</c>.</summary>
        PulseTooShort,

        /// <summary>IQ mismatch has been compensated out. Renders <c>IQ COMP</c>.</summary>
        IqCompensated,
    }

    /// <summary>
    /// One active indicator: which condition, and for an overload, which channel.
    /// </summary>
    public struct ActiveIndicator : IEquatable<ActiveIndicator>
    {
        /// <summary>Creates an active indicator.</summary>
        /// <param name="indicator">Which condition.</param>
        /// <param name="channel">
        /// Channel number for <see cref="TraceIndicator.Overload"/>; ignored otherwise.
        /// </param>
        public ActiveIndicator(TraceIndicator indicator, int channel = 1)
        {
            Indicator = indicator;
            Channel = channel;
        }

        /// <summary>Which condition is true.</summary>
        public TraceIndicator Indicator { get; }

        /// <summary>The channel an overload applies to.</summary>
        public int Channel { get; }

        /// <summary>The string this renders as.</summary>
        public string Text => TraceIndicators.TextOf(Indicator, Channel);

        /// <inheritdoc />
        public bool Equals(ActiveIndicator other) =>
            Indicator == other.Indicator &&
            (Indicator != TraceIndicator.Overload || Channel == other.Channel);

        /// <inheritdoc />
        public override bool Equals(object obj) => obj is ActiveIndicator other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode() =>
            Indicator == TraceIndicator.Overload
                ? (int)Indicator * 397 ^ Channel
                : (int)Indicator * 397;

        /// <inheritdoc />
        public override string ToString() => Text;
    }

    /// <summary>
    /// The set of indicators currently true, in the order they are displayed
    /// (<c>REQ-UI-041</c>).
    /// </summary>
    /// <remarks>
    /// Kept apart from the control that draws it so that the priority ordering and the exact
    /// strings can be asserted without a visual tree — and so that the same set can later feed a
    /// second surface without the ordering being restated there.
    /// </remarks>
    public sealed class TraceIndicators
    {
        private readonly List<ActiveIndicator> _active = new List<ActiveIndicator>();

        /// <summary>The indicators currently true, in display priority order.</summary>
        public IReadOnlyList<ActiveIndicator> Active
        {
            get
            {
                // Sorted on read rather than on insert, so that the order is a property of the set
                // and not of the sequence in which conditions happened to be reported.
                var ordered = new List<ActiveIndicator>(_active);
                ordered.Sort(Compare);
                return ordered;
            }
        }

        /// <summary>Whether anything is showing.</summary>
        public bool IsEmpty => _active.Count == 0;

        /// <summary>
        /// The lines to draw, highest priority first, one per active indicator.
        /// </summary>
        public string Text
        {
            get
            {
                IReadOnlyList<ActiveIndicator> ordered = Active;
                var lines = new string[ordered.Count];

                for (int i = 0; i < ordered.Count; i++)
                {
                    lines[i] = ordered[i].Text;
                }

                return string.Join(Environment.NewLine, lines);
            }
        }

        /// <summary>
        /// Marks a condition true, or updates the channel of an overload already showing.
        /// </summary>
        /// <param name="indicator">Which condition.</param>
        /// <param name="channel">Channel number, for an overload.</param>
        /// <remarks>
        /// Overloads on different channels are distinct — two inputs can be overloaded at once and
        /// the user needs to know which — so <c>OV1</c> and <c>OV2</c> both show.
        /// </remarks>
        public void Set(TraceIndicator indicator, int channel = 1)
        {
            var entry = new ActiveIndicator(indicator, channel);

            if (!_active.Contains(entry))
            {
                _active.Add(entry);
            }
        }

        /// <summary>Marks a condition false.</summary>
        /// <param name="indicator">Which condition.</param>
        /// <param name="channel">Channel number, for an overload.</param>
        public void Clear(TraceIndicator indicator, int channel = 1) =>
            _active.Remove(new ActiveIndicator(indicator, channel));

        /// <summary>Marks a condition true or false.</summary>
        /// <param name="indicator">Which condition.</param>
        /// <param name="active">Whether it is true.</param>
        /// <param name="channel">Channel number, for an overload.</param>
        public void SetActive(TraceIndicator indicator, bool active, int channel = 1)
        {
            if (active)
            {
                Set(indicator, channel);
            }
            else
            {
                Clear(indicator, channel);
            }
        }

        /// <summary>Whether a condition is showing.</summary>
        /// <param name="indicator">Which condition.</param>
        /// <param name="channel">Channel number, for an overload.</param>
        public bool IsActive(TraceIndicator indicator, int channel = 1) =>
            _active.Contains(new ActiveIndicator(indicator, channel));

        /// <summary>Marks every condition false.</summary>
        public void ClearAll() => _active.Clear();

        /// <summary>
        /// The string an indicator renders as.
        /// </summary>
        /// <param name="indicator">Which condition.</param>
        /// <param name="channel">Channel number, used only by <see cref="TraceIndicator.Overload"/>.</param>
        /// <exception cref="ArgumentOutOfRangeException">Not a known indicator.</exception>
        /// <remarks>
        /// Verbatim from the reference product, question marks and all. The question mark is not
        /// punctuation here: <c>CAL?</c> means the calibration is doubtful, where <c>CAL</c> would
        /// read as a statement that it is fine.
        /// </remarks>
        public static string TextOf(TraceIndicator indicator, int channel = 1)
        {
            switch (indicator)
            {
                case TraceIndicator.NoData: return "NO DATA";
                case TraceIndicator.DataQuestionable: return "DATA?";
                case TraceIndicator.Overload:
                    return "OV" + channel.ToString(CultureInfo.InvariantCulture);
                case TraceIndicator.CalibrationQuestionable: return "CAL?";
                case TraceIndicator.PulseNotFound: return "PULSE NOT FOUND";
                case TraceIndicator.SyncNotFound: return "SYNC NOT FOUND";
                case TraceIndicator.CarrierLock: return "CARRIER LOCK?";
                case TraceIndicator.Equaliser: return "EQ";
                case TraceIndicator.Range: return "RNG";
                case TraceIndicator.AllPoints: return "ALL POINTS";
                case TraceIndicator.InactiveChannel: return "INACTIVE CHAN";
                case TraceIndicator.MeasurementOffset: return "MEAS OFFSET?";
                case TraceIndicator.PulseTooShort: return "PULSE TOO SHORT";
                case TraceIndicator.IqCompensated: return "IQ COMP";

                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(indicator), indicator, "Not a known trace indicator.");
            }
        }

        private static int Compare(ActiveIndicator left, ActiveIndicator right)
        {
            int byPriority = ((int)left.Indicator).CompareTo((int)right.Indicator);
            return byPriority != 0 ? byPriority : left.Channel.CompareTo(right.Channel);
        }
    }
}
