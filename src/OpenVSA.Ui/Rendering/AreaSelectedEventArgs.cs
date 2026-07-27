using System;
using System.Globalization;

namespace OpenVSA.Ui.Rendering
{
    /// <summary>
    /// A region dragged across a trace — the <em>Select Area</em> gesture (<c>REQ-DSP-023</c>).
    /// </summary>
    /// <remarks>
    /// The two edges are ordered low then high whichever way the drag went. A drag right-to-left
    /// means the same region as one left-to-right, and requiring a direction would make half the
    /// gestures appear to do nothing.
    /// </remarks>
    public sealed class AreaSelectedEventArgs : EventArgs
    {
        /// <summary>Creates the arguments.</summary>
        /// <param name="startHz">Lower edge of the region, in hertz.</param>
        /// <param name="stopHz">Upper edge, in hertz; must be above the lower.</param>
        /// <exception cref="ArgumentOutOfRangeException">An edge is not finite, or they are inverted.</exception>
        public AreaSelectedEventArgs(
            double startHz, double stopHz, double topDbm = double.NaN, double bottomDbm = double.NaN)
        {
            if (double.IsNaN(startHz) || double.IsInfinity(startHz))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(startHz), startHz, "An edge must be finite.");
            }

            if (!(stopHz > startHz) || double.IsInfinity(stopHz))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(stopHz), stopHz, "The upper edge must lie above the lower one.");
            }

            StartHz = startHz;
            StopHz = stopHz;

            // Ordered high then low whichever way the drag went, for the same reason the two
            // frequencies are ordered low then high.
            TopDbm = Math.Max(topDbm, bottomDbm);
            BottomDbm = Math.Min(topDbm, bottomDbm);
        }

        /// <summary>Lower edge of the region, in hertz.</summary>
        public double StartHz { get; }

        /// <summary>Upper edge of the region, in hertz.</summary>
        public double StopHz { get; }

        /// <summary>Width of the region, in hertz.</summary>
        public double SpanHz => StopHz - StartHz;

        /// <summary>Centre of the region, in hertz.</summary>
        public double CentreHz => (StartHz + StopHz) / 2.0;

        /// <summary>
        /// Upper edge of the region, in dBm, or <see cref="double.NaN"/> if it had no height.
        /// </summary>
        /// <remarks>
        /// <c>REQ-UI-063</c> calls this gesture "select a rectangular area of any trace", and the
        /// rectangle has two dimensions for a reason: Area Select "can scale X <em>and/or Y</em>,
        /// or set centre frequency and span". A band with no height can only ever do one of the
        /// three.
        /// </remarks>
        public double TopDbm { get; private set; }

        /// <summary>Lower edge of the region, in dBm, or <see cref="double.NaN"/>.</summary>
        public double BottomDbm { get; private set; }

        /// <summary>Height of the region, in decibels, or <see cref="double.NaN"/>.</summary>
        public double RangeDb => TopDbm - BottomDbm;

        /// <summary>Whether the region has a usable height.</summary>
        public bool HasLevels =>
            !double.IsNaN(TopDbm) && !double.IsNaN(BottomDbm) && TopDbm > BottomDbm;

        /// <inheritdoc />
        public override string ToString() =>
            (SpanHz / 1e3).ToString("0.###", CultureInfo.CurrentCulture) + " kHz about " +
            (CentreHz / 1e6).ToString("0.######", CultureInfo.CurrentCulture) + " MHz";
    }
}
