using System;
using System.Collections.Generic;
using OpenVSA.Dsp.Spectrum;

namespace OpenVSA.Ui.Rendering
{
    /// <summary>
    /// Where the reference line sits on each axis, and the per-format defaults of
    /// <c>REQ-UI-013</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This is a layout decision as much as a scaling one.</strong> A Y reference of 100 %
    /// puts the reference line at the top of the grid, which is what puts the reference-level
    /// annotation at top left for a spectrum (<c>REQ-UI-040</c>). A Y reference of 50 % centres it,
    /// which is what a time-domain or IQ display needs — an origin at the top of the grid would put
    /// half of every constellation off the screen.
    /// </para>
    /// <para>
    /// <strong>The defaults are enumerated over the whole format list, not defaulted in a
    /// switch's fallthrough.</strong> <c>REQ-UI-013</c>'s criterion is that "a format added later
    /// without a default fails the test", so <see cref="DefaultYPercentFor"/> throws for a format it
    /// has not been told about rather than returning 50 % and looking right by accident.
    /// </para>
    /// <para>
    /// <strong>On "Log Mag (lin)".</strong> <c>REQ-UI-013</c> names three formats that take 100 % —
    /// Log Mag, Lin Mag and Log Mag (lin). The third is the reference product's log-magnitude trace
    /// drawn against a linear vertical scale; <c>REQ-DSP-041</c>'s format list, which is what
    /// <see cref="TraceFormat"/> implements, has no such member and so there is nothing here to give
    /// a default to. If it is ever added it belongs with the other two, at 100 %.
    /// </para>
    /// </remarks>
    public static class ReferencePosition
    {
        /// <summary>Lowest settable reference position, as a percentage.</summary>
        public const int MinimumPercent = 0;

        /// <summary>Highest settable reference position, as a percentage.</summary>
        public const int MaximumPercent = 100;

        /// <summary>The Y reference position a spectrum's magnitude formats default to.</summary>
        public const int TopPercent = 100;

        /// <summary>The Y reference position every other format defaults to.</summary>
        public const int CentrePercent = 50;

        /// <summary>The X reference position, which is centred for every format.</summary>
        /// <remarks>
        /// <c>REQ-UI-013</c> gives per-format defaults for Y only. X is centred because the
        /// frequency axis of a spectrum is written about its centre frequency, and a time record is
        /// written about its trigger.
        /// </remarks>
        public const int DefaultXPercent = 50;

        /// <summary>
        /// The Y reference position a format defaults to, as a percentage of the grid height.
        /// </summary>
        /// <param name="format">The trace format.</param>
        /// <returns>100 for the magnitude formats, 50 for everything else.</returns>
        /// <exception cref="ArgumentOutOfRangeException">
        /// The format has no stated default. Deliberate: a format added to <c>REQ-DSP-041</c>
        /// without being given one here fails rather than quietly inheriting 50 %.
        /// </exception>
        public static int DefaultYPercentFor(TraceFormat format)
        {
            switch (format)
            {
                // The reference line at the top of the grid: a spectrum hangs down from its
                // reference level, and the level is the number the display is about.
                case TraceFormat.LogMagnitude:
                case TraceFormat.LinearMagnitude:
                    return TopPercent;

                // Centred: these are signed, and the interesting value is zero rather than the
                // maximum. An IQ display with its origin at the top of the grid shows one quadrant.
                case TraceFormat.Real:
                case TraceFormat.Imaginary:
                case TraceFormat.WrappedPhase:
                case TraceFormat.UnwrappedPhase:
                case TraceFormat.GroupDelay:
                case TraceFormat.IQ:
                    return CentrePercent;
            }

            throw new ArgumentOutOfRangeException(
                nameof(format),
                format,
                "REQ-UI-013 gives every trace format a default Y reference position, and this " +
                "format has not been given one. Add it to ReferencePosition rather than letting " +
                "it inherit a default by accident.");
        }

        /// <summary>
        /// The top of an axis that puts a reference value at a reference position.
        /// </summary>
        /// <param name="referenceValue">The value the reference line reads.</param>
        /// <param name="fullScale">The whole range of the axis, top to bottom.</param>
        /// <param name="yPercent">Where the reference line sits, 0 at the bottom through 100 at the top.</param>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="yPercent"/> is outside 0 to 100.</exception>
        /// <remarks>
        /// At 100 % the top of the axis <em>is</em> the reference value; at 50 % the reference is
        /// half a full scale below the top; at 0 % it is the bottom. This is the whole of
        /// <c>REQ-UI-013</c>'s scaling half, and it lives here rather than in the plot so that it
        /// can be checked against <see cref="PlotLayout.ReferenceLineY"/> — the two have to agree,
        /// and a sign error in either is invisible until they are compared.
        /// </remarks>
        public static double TopFor(double referenceValue, double fullScale, int yPercent)
        {
            Validate(yPercent, nameof(yPercent));

            return referenceValue + (MaximumPercent - yPercent) / 100.0 * fullScale;
        }

        /// <summary>Every format that has a stated default, which is all of them.</summary>
        public static IEnumerable<TraceFormat> Formats
        {
            get
            {
                foreach (TraceFormat format in (TraceFormat[])Enum.GetValues(typeof(TraceFormat)))
                {
                    yield return format;
                }
            }
        }

        /// <summary>
        /// Checks a reference position and returns it.
        /// </summary>
        /// <param name="percent">The position, 0 to 100.</param>
        /// <param name="name">The parameter name to blame.</param>
        /// <returns>The position.</returns>
        /// <exception cref="ArgumentOutOfRangeException">It is outside 0 to 100.</exception>
        /// <remarks>
        /// An integer percentage, which is <c>REQ-UI-013</c>'s "1 % increments" expressed in the
        /// type rather than checked at each use.
        /// </remarks>
        public static int Validate(int percent, string name)
        {
            if (percent < MinimumPercent || percent > MaximumPercent)
            {
                throw new ArgumentOutOfRangeException(
                    name,
                    percent,
                    "A reference position is a whole percentage from " + MinimumPercent + " to " +
                    MaximumPercent + ".");
            }

            return percent;
        }
    }
}
