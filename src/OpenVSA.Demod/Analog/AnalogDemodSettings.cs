using System;
using System.Globalization;

namespace OpenVSA.Demod.Analog
{
    /// <summary>Which parameter of the carrier is demodulated (<c>REQ-DEM-010a</c>).</summary>
    public enum AnalogDemodType
    {
        /// <summary>Amplitude modulation: the envelope.</summary>
        Am = 0,

        /// <summary>Frequency modulation: the rate of change of phase.</summary>
        Fm,

        /// <summary>Phase modulation: the phase itself, with the carrier's ramp removed.</summary>
        Pm,
    }

    /// <summary>
    /// What an analog demodulation is asked to do (<c>REQ-DEM-010a</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The post-detection filters are off by default and that is deliberate.</strong> They
    /// are there because the requirement asks for them and because a real audio measurement wants
    /// them, but every one of them changes the amplitude of what is measured — a de-emphasis of
    /// 75 µs is 3 dB down at 2.1 kHz — so a deviation measured through one is a deviation of the
    /// filtered audio, not of the signal. A user who has not asked for that should not silently get
    /// it, and <see cref="AnalogMetrics.FilteredAudio"/> says which they got.
    /// </para>
    /// </remarks>
    public sealed class AnalogDemodSettings
    {
        /// <summary>The 75 µs de-emphasis of FM broadcast in the Americas and Korea.</summary>
        public const double DeEmphasis75Microseconds = 75e-6;

        /// <summary>The 50 µs de-emphasis used elsewhere.</summary>
        public const double DeEmphasis50Microseconds = 50e-6;

        /// <summary>Which parameter is demodulated.</summary>
        public AnalogDemodType Type { get; set; } = AnalogDemodType.Fm;

        /// <summary>
        /// Post-detection low-pass corner, in hertz; zero for none.
        /// </summary>
        /// <remarks>
        /// The audio bandwidth: what is above it is not modulation anybody asked to measure. Applied
        /// after de-emphasis, because de-emphasis is part of the signal's own definition and this is
        /// the measurement's choice.
        /// </remarks>
        public double LowPassHz { get; set; }

        /// <summary>
        /// Post-detection high-pass corner, in hertz; zero for none.
        /// </summary>
        /// <remarks>
        /// Removes the carrier's own drift from an audio measurement — a slow frequency wander is
        /// not modulation, and it would otherwise land in the deviation figure as a large, slow
        /// contribution. It does not remove the carrier itself: the detector has already done that
        /// by measuring about the mean.
        /// </remarks>
        public double HighPassHz { get; set; }

        /// <summary>
        /// De-emphasis time constant, in seconds; zero for none.
        /// </summary>
        /// <remarks>
        /// A single-pole low-pass at <c>1/(2·pi·tau)</c>, which is what a de-emphasis network is:
        /// 75 µs is a corner at 2.12 kHz, 50 µs at 3.18 kHz. Named by time constant rather than by
        /// corner frequency because that is how the standards state it and how a bench operator
        /// asks for it.
        /// </remarks>
        public double DeEmphasisSeconds { get; set; }

        /// <summary>
        /// How many harmonics of the modulation rate distortion is summed over.
        /// </summary>
        /// <remarks>
        /// Five, which reaches the fifth harmonic of the rate. Enough to catch the low-order
        /// distortion an analog detector or a transmitter actually produces, and bounded because a
        /// sum over every harmonic up to the band edge is a sum over mostly noise.
        /// </remarks>
        public int HarmonicsForDistortion { get; set; } = 5;

        /// <summary>Whether any post-detection filtering is asked for.</summary>
        public bool FiltersAudio =>
            LowPassHz > 0.0 || HighPassHz > 0.0 || DeEmphasisSeconds > 0.0;

        /// <summary>Checks the settings hold together.</summary>
        /// <exception cref="ArgumentException">A setting is outside its range.</exception>
        public void Validate()
        {
            Require(LowPassHz >= 0.0, "A post-detection low-pass corner is not negative.");
            Require(HighPassHz >= 0.0, "A post-detection high-pass corner is not negative.");
            Require(DeEmphasisSeconds >= 0.0, "A de-emphasis time constant is not negative.");

            Require(
                HarmonicsForDistortion >= 2,
                "Distortion is a sum over at least the second harmonic; " +
                HarmonicsForDistortion.ToString(CultureInfo.InvariantCulture) +
                " would make THD zero by definition rather than by measurement.");

            // Both corners at once is a band-pass and is allowed; the two crossing over is not a
            // band-pass but a band-stop nobody asked for, and it would report an audio power of
            // almost nothing as though the signal had none.
            Require(
                LowPassHz <= 0.0 || HighPassHz <= 0.0 || HighPassHz < LowPassHz,
                "The post-detection high-pass at " +
                HighPassHz.ToString("G6", CultureInfo.InvariantCulture) +
                " Hz is above the low-pass at " +
                LowPassHz.ToString("G6", CultureInfo.InvariantCulture) +
                " Hz, which passes nothing. Set the high-pass below the low-pass, or turn one off.");
        }

        private static void Require(bool condition, string message)
        {
            if (!condition)
            {
                throw new ArgumentException(message);
            }
        }
    }
}
