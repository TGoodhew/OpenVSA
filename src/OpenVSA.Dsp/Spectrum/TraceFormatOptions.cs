using System;
using System.Globalization;

namespace OpenVSA.Dsp.Spectrum
{
    /// <summary>
    /// The settings the phase and group-delay formats are computed with
    /// (<c>REQ-DSP-044</c>, <c>REQ-DSP-045</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>These are settings, not constants, and both requirements say so.</strong> Group
    /// delay is <c>−dφ/dω</c> over a <em>configurable</em> aperture, and unwrapping takes a
    /// <em>configurable</em> jump tolerance. Both change what is displayed, so both have to be
    /// visible: <see cref="Describe"/> is what the annotation shows, because a group-delay trace
    /// without its aperture is a number nobody can reproduce.
    /// </para>
    /// <para>
    /// Immutable, so a trace and the annotation beside it cannot describe different settings.
    /// </para>
    /// </remarks>
    public sealed class TraceFormatOptions
    {
        /// <summary>The aperture a group-delay trace uses when none is chosen, in bins.</summary>
        /// <remarks>
        /// One bin is the narrowest aperture there is — the difference between neighbours — and so
        /// the least smoothed and the noisiest. It is the default because it is the one that adds
        /// nothing to the measurement: any wider aperture is a deliberate trade of resolution for
        /// quiet, and should be asked for rather than arrived at.
        /// </remarks>
        public const int DefaultApertureBins = 1;

        /// <summary>The unwrap jump tolerance when none is chosen, in degrees.</summary>
        /// <remarks>
        /// Half a turn: the standard ±π threshold, where a step larger than half a turn is taken to
        /// be a wrap rather than a real excursion.
        /// </remarks>
        public const double DefaultJumpToleranceDegrees = 180.0;

        /// <summary>Creates the options.</summary>
        /// <param name="apertureBins">Group-delay aperture, in bins; at least one.</param>
        /// <param name="jumpToleranceDegrees">
        /// Step above which an unwrap assumes a wrap, in degrees; greater than zero and no more
        /// than a full turn.
        /// </param>
        /// <exception cref="ArgumentOutOfRangeException">A value is out of range.</exception>
        public TraceFormatOptions(
            int apertureBins = DefaultApertureBins,
            double jumpToleranceDegrees = DefaultJumpToleranceDegrees)
        {
            if (apertureBins < 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(apertureBins), apertureBins,
                    "An aperture spans at least one bin; there is no derivative over none.");
            }

            if (!(jumpToleranceDegrees > 0.0) || jumpToleranceDegrees > 360.0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(jumpToleranceDegrees), jumpToleranceDegrees,
                    "The jump tolerance runs from just above 0 to 360 degrees. A tolerance of a " +
                    "full turn never unwraps; one of zero unwraps at every point.");
            }

            ApertureBins = apertureBins;
            JumpToleranceDegrees = jumpToleranceDegrees;
        }

        /// <summary>The settings a trace uses when none are given.</summary>
        public static TraceFormatOptions Default { get; } = new TraceFormatOptions();

        /// <summary>
        /// Group-delay aperture, in bins (<c>REQ-DSP-045</c>).
        /// </summary>
        /// <remarks>
        /// The span the phase difference is taken over. Widening it averages the derivative and so
        /// smooths a noisy trace, at the cost of resolving less — which is why the figure has to
        /// appear in the annotation rather than being chosen and forgotten.
        /// </remarks>
        public int ApertureBins { get; }

        /// <summary>Step above which an unwrap assumes a wrap, in degrees (<c>REQ-DSP-044</c>).</summary>
        public double JumpToleranceDegrees { get; }

        /// <summary>
        /// The reference point unwrapped phase is measured from.
        /// </summary>
        /// <remarks>
        /// <c>REQ-DSP-044</c> requires the reference point to be documented, and this is the
        /// documentation: <strong>the first point of the trace</strong>, whose unwrapped value is
        /// its wrapped value. Any other choice — the peak, the centre — moves with the signal, and
        /// a phase trace measured from a moving reference is not reproducible between two runs of
        /// the same measurement.
        /// </remarks>
        public const int ReferencePointIndex = 0;

        /// <summary>Returns a copy with a different aperture.</summary>
        /// <param name="apertureBins">The new aperture, in bins.</param>
        /// <exception cref="ArgumentOutOfRangeException">The value is out of range.</exception>
        public TraceFormatOptions WithAperture(int apertureBins) =>
            new TraceFormatOptions(apertureBins, JumpToleranceDegrees);

        /// <summary>Returns a copy with a different jump tolerance.</summary>
        /// <param name="jumpToleranceDegrees">The new tolerance, in degrees.</param>
        /// <exception cref="ArgumentOutOfRangeException">The value is out of range.</exception>
        public TraceFormatOptions WithJumpTolerance(double jumpToleranceDegrees) =>
            new TraceFormatOptions(ApertureBins, jumpToleranceDegrees);

        /// <summary>
        /// What the annotation shows for a format, or empty when the format uses none of this.
        /// </summary>
        /// <param name="format">The format being displayed.</param>
        /// <remarks>
        /// Shown only where it applies. Printing an aperture beside a log-magnitude trace would be
        /// telling the reader about a setting that had no bearing on what they are looking at.
        /// </remarks>
        public string Describe(TraceFormat format)
        {
            if (format == TraceFormat.GroupDelay)
            {
                return "Aperture " + ApertureBins.ToString(CultureInfo.CurrentCulture) +
                    (ApertureBins == 1 ? " bin" : " bins");
            }

            if (format == TraceFormat.UnwrappedPhase)
            {
                return "Unwrapped from point " +
                    ReferencePointIndex.ToString(CultureInfo.CurrentCulture) + ", jump " +
                    JumpToleranceDegrees.ToString("0.#", CultureInfo.CurrentCulture) + "°";
            }

            return string.Empty;
        }

        /// <inheritdoc />
        public override string ToString() =>
            "aperture " + ApertureBins.ToString(CultureInfo.CurrentCulture) + " bins, jump " +
            JumpToleranceDegrees.ToString("0.#", CultureInfo.CurrentCulture) + " degrees";
    }
}
