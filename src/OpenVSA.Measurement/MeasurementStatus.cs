using System.Globalization;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace OpenVSA.Measurement
{
    /// <summary>
    /// The measurement status strings of <c>REQ-UI-006</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>An enumeration rather than free text, because the criterion is about the
    /// strings.</strong> <c>REQ-UI-006</c> lists them and its acceptance criterion says "driving
    /// the measurement into <em>Waiting for Trigger</em> and <em>Average Complete</em> shows those
    /// strings" — so a status bar that showed "waiting for trigger…" or "Trigger wait" would fail,
    /// and free text is how that happens. The status bar formats these; nothing else invents one.
    /// </para>
    /// <para>
    /// Other messages still reach the bar — a preset applied, a marker placed — and those are
    /// transient notices rather than measurement status. The difference is that a status here is a
    /// state the measurement is <em>in</em>, and it is recomputed rather than announced.
    /// </para>
    /// </remarks>
    public enum MeasurementStatus
    {
        /// <summary>Nothing is being measured.</summary>
        Idle = 0,

        /// <summary>Armed, and the trigger condition has not yet occurred.</summary>
        WaitingForTrigger,

        /// <summary>Triggered, and the time record is still being filled.</summary>
        FillingTimeRecord,

        /// <summary>Acquiring and computing.</summary>
        Running,

        /// <summary>Running gap-free, so no acquisition is being missed.</summary>
        RealTime,

        /// <summary>The requested number of averages has been accumulated.</summary>
        AverageComplete,
    }

    /// <summary>The strings <c>REQ-UI-006</c> shows for each status.</summary>
    public static class MeasurementStatusText
    {
        private static readonly ReadOnlyCollection<MeasurementStatus> AllStatuses =
            new ReadOnlyCollection<MeasurementStatus>(
                (MeasurementStatus[])Enum.GetValues(typeof(MeasurementStatus)));

        /// <summary>Every status, in the order they are declared.</summary>
        public static IReadOnlyList<MeasurementStatus> All => AllStatuses;

        /// <summary>
        /// The string for a status, exactly as <c>REQ-UI-006</c> writes it.
        /// </summary>
        /// <param name="status">The status.</param>
        /// <exception cref="ArgumentOutOfRangeException">Not a known status.</exception>
        /// <remarks>
        /// Verbatim, including the capitalisation. The requirement quotes these from the reference
        /// product and a test asserts them as literals, so "Measurement Running" would fail where
        /// "Measurement running" passes — which is the point of quoting them.
        /// </remarks>
        public static string TextOf(MeasurementStatus status)
        {
            switch (status)
            {
                case MeasurementStatus.Idle: return "Ready";
                case MeasurementStatus.WaitingForTrigger: return "Waiting for Trigger";
                case MeasurementStatus.FillingTimeRecord: return "Filling Time Record";
                case MeasurementStatus.Running: return "Measurement running";
                case MeasurementStatus.RealTime: return "Real-Time Measurement";
                case MeasurementStatus.AverageComplete: return "Average Complete";

                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(status), status, "Not a known measurement status.");
            }
        }

        /// <summary>
        /// The status a measurement is in, from what it is doing.
        /// </summary>
        /// <param name="isMeasuring">Whether an engine is running.</param>
        /// <param name="isArmed">Whether it is armed and waiting for its trigger.</param>
        /// <param name="isFillingRecord">Whether the time record is still being filled.</param>
        /// <param name="isGapFree">Whether the acquisition is gap-free.</param>
        /// <param name="averagesWanted">How many averages were asked for; zero for none.</param>
        /// <param name="averagesDone">How many have been accumulated.</param>
        /// <remarks>
        /// <para>
        /// Derived rather than assigned, which is what makes the criterion's "each status string
        /// appears when its condition holds" true by construction. A status set by whichever code
        /// path last ran would show <em>Average Complete</em> for as long as nobody overwrote it.
        /// </para>
        /// <para>
        /// The order matters and is the order a user cares about: a completed average is the answer
        /// they were waiting for, so it outranks "running"; waiting for a trigger outranks filling a
        /// record because nothing is being filled yet.
        /// </para>
        /// </remarks>
        /// <summary>
        /// What the status bar says about dropped frames (<c>REQ-NFR-012</c>).
        /// </summary>
        /// <param name="dropped">How many frames the render marshal has dropped.</param>
        /// <remarks>
        /// <para>
        /// Empty at zero, deliberately. A permanent "0 frames dropped" is noise that a reader stops
        /// seeing, and the whole value of this readout is that it appears when something has
        /// changed. The count is monotonic, so once it is there it stays.
        /// </para>
        /// <para>
        /// Here rather than in the shell so the wording can be asserted without a window — the same
        /// division the rest of this file keeps.
        /// </para>
        /// </remarks>
        public static string DroppedFramesText(long dropped)
        {
            if (dropped <= 0L)
            {
                return string.Empty;
            }

            return dropped.ToString(CultureInfo.CurrentCulture) +
                (dropped == 1L ? " frame dropped" : " frames dropped");
        }

        public static MeasurementStatus For(
            bool isMeasuring,
            bool isArmed,
            bool isFillingRecord,
            bool isGapFree,
            int averagesWanted,
            int averagesDone)
        {
            if (!isMeasuring)
            {
                return MeasurementStatus.Idle;
            }

            if (averagesWanted > 0 && averagesDone >= averagesWanted)
            {
                return MeasurementStatus.AverageComplete;
            }

            if (isArmed)
            {
                return MeasurementStatus.WaitingForTrigger;
            }

            if (isFillingRecord)
            {
                return MeasurementStatus.FillingTimeRecord;
            }

            return isGapFree ? MeasurementStatus.RealTime : MeasurementStatus.Running;
        }
    }
}
