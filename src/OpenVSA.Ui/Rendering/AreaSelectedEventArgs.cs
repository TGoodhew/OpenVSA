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
        public AreaSelectedEventArgs(double startHz, double stopHz)
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
        }

        /// <summary>Lower edge of the region, in hertz.</summary>
        public double StartHz { get; }

        /// <summary>Upper edge of the region, in hertz.</summary>
        public double StopHz { get; }

        /// <summary>Width of the region, in hertz.</summary>
        public double SpanHz => StopHz - StartHz;

        /// <summary>Centre of the region, in hertz.</summary>
        public double CentreHz => (StartHz + StopHz) / 2.0;

        /// <inheritdoc />
        public override string ToString() =>
            (SpanHz / 1e3).ToString("0.###", CultureInfo.CurrentCulture) + " kHz about " +
            (CentreHz / 1e6).ToString("0.######", CultureInfo.CurrentCulture) + " MHz";
    }
}
