using System;

namespace OpenVSA.Dsp.Spectrum
{
    /// <summary>
    /// The display formats of <c>REQ-DSP-041</c>: views of one computation, not parallel paths.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Spectrogram, Digital Persistence and Cumulative History are deliberately absent.</strong>
    /// <c>REQ-TRC-001a</c> makes them a separate axis — <see cref="TraceAccumulator"/> — because
    /// they accumulate across many acquisitions and so cannot satisfy <c>REQ-TRC-001</c>'s rule
    /// that changing format recomputes nothing. Adding one here would be the mistake that
    /// requirement exists to prevent, and a test over this enumeration fails if one appears.
    /// </para>
    /// <para>
    /// Every member is a pure function of the same calibrated complex spectrum, which is what
    /// makes <c>REQ-DSP-041</c>'s criterion hold: log and linear magnitude agree after conversion,
    /// and real and imaginary recombine to the magnitude, because there is only one computation
    /// underneath them.
    /// </para>
    /// </remarks>
    public enum TraceFormat
    {
        /// <summary>Magnitude in dBm — the default for a spectrum.</summary>
        LogMagnitude = 0,

        /// <summary>Magnitude in volts, peak-referenced.</summary>
        LinearMagnitude,

        /// <summary>Real part, in volts.</summary>
        Real,

        /// <summary>Imaginary part, in volts.</summary>
        Imaginary,

        /// <summary>Phase in degrees, wrapped to ±180.</summary>
        WrappedPhase,

        /// <summary>Phase in degrees, unwrapped across ±180 boundaries.</summary>
        UnwrappedPhase,

        /// <summary>Negative derivative of phase with respect to frequency, in seconds.</summary>
        GroupDelay,

        /// <summary>The complex pair itself, for a polar or constellation display.</summary>
        IQ,
    }

    /// <summary>
    /// The accumulating display modes of <c>REQ-TRC-001a</c> — a third axis, not formats.
    /// </summary>
    /// <remarks>
    /// These build up across acquisitions, so the no-recomputation rule that governs
    /// <see cref="TraceFormat"/> cannot apply to them: changing the format leaves an accumulation
    /// intact, while changing the accumulator necessarily discards it. The reference product
    /// groups exactly these three as "3D Map" modes on their own toolbar, which is the same
    /// separation arrived at from the other direction.
    /// </remarks>
    public enum TraceAccumulator
    {
        /// <summary>No accumulation; the trace shows the current acquisition.</summary>
        None = 0,

        /// <summary>A scrolling time–frequency intensity map.</summary>
        Spectrogram,

        /// <summary>Overlaid traces with intensity by hit count.</summary>
        DigitalPersistence,

        /// <summary>Every trace since the accumulator was started, retained.</summary>
        CumulativeHistory,
    }
}
