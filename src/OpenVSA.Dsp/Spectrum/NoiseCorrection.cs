using System;
using System.Globalization;

namespace OpenVSA.Dsp.Spectrum
{
    /// <summary>
    /// A characterised instrument noise floor, ready to be subtracted from a measurement
    /// (<c>REQ-DSP-024</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Characterised, not modelled.</strong> The floor is measured — terminate the input,
    /// average until the trace is smooth, and keep it — because an instrument's noise floor is not
    /// flat and not predictable from its specification: it rises at the band edges where the
    /// anti-alias filter rolls off, and it has whatever spurs the instrument has. A modelled floor
    /// would subtract the wrong number wherever the model was wrong, and the error would look
    /// exactly like signal.
    /// </para>
    /// <para>
    /// <strong>It carries the resolution bandwidth it was measured at.</strong> Noise power in a
    /// bin is proportional to the bin's noise bandwidth, so a floor characterised at one RBW and
    /// applied at another must be scaled by the ratio. Storing the figure with the floor is what
    /// makes that automatic; storing a bare level in dBm would be a number that is only correct at
    /// a setting nobody recorded.
    /// </para>
    /// <para>
    /// <strong>The floor is a noise floor, so its levels are levels — never phasors.</strong>
    /// Averaging a noise trace coherently converges on zero; the characterisation has to be a power
    /// average, which is what <see cref="FromTrace"/> takes and why it takes a frame that has
    /// already been averaged rather than doing the averaging itself.
    /// </para>
    /// </remarks>
    public sealed class NoiseFloor
    {
        private readonly double[] _levelsDbm;
        private readonly double _startHz;
        private readonly double _binWidthHz;

        private NoiseFloor(
            double[] levelsDbm, double startHz, double binWidthHz, double resolutionBandwidthHz)
        {
            _levelsDbm = levelsDbm;
            _startHz = startHz;
            _binWidthHz = binWidthHz;
            ResolutionBandwidthHz = resolutionBandwidthHz;
        }

        /// <summary>The resolution bandwidth this floor was characterised at, in hertz.</summary>
        public double ResolutionBandwidthHz { get; }

        /// <summary>Number of characterised points.</summary>
        public int PointCount => _levelsDbm.Length;

        /// <summary>Frequency of the first characterised point, in hertz.</summary>
        public double StartFrequencyHz => _startHz;

        /// <summary>Spacing between characterised points, in hertz.</summary>
        public double BinWidthHz => _binWidthHz;

        /// <summary>
        /// A floor that is the same level everywhere.
        /// </summary>
        /// <param name="levelDbm">Noise level per bin, in dBm.</param>
        /// <param name="resolutionBandwidthHz">The RBW it applies at; positive and finite.</param>
        /// <exception cref="ArgumentOutOfRangeException">A value is out of range.</exception>
        /// <remarks>
        /// For a test, and for an instrument whose floor really is flat across the span in use. A
        /// real characterisation comes from <see cref="FromTrace"/>.
        /// </remarks>
        public static NoiseFloor Flat(double levelDbm, double resolutionBandwidthHz)
        {
            RequireLevel(levelDbm, nameof(levelDbm));
            RequirePositive(resolutionBandwidthHz, nameof(resolutionBandwidthHz));

            return new NoiseFloor(
                new[] { levelDbm }, double.NegativeInfinity, 0.0, resolutionBandwidthHz);
        }

        /// <summary>
        /// A floor characterised from a measured noise trace.
        /// </summary>
        /// <param name="noise">
        /// An averaged spectrum of the instrument with no signal present. Its axis and RBW become
        /// the floor's.
        /// </param>
        /// <exception cref="ArgumentNullException"><paramref name="noise"/> is null.</exception>
        /// <exception cref="ArgumentException">The trace has fewer than two points.</exception>
        /// <remarks>
        /// The trace is copied. A floor that shared storage with a live frame would change under a
        /// measurement that was being corrected against it.
        /// </remarks>
        public static NoiseFloor FromTrace(SpectrumFrame noise)
        {
            if (noise == null)
            {
                throw new ArgumentNullException(nameof(noise));
            }

            if (noise.PointCount < 2)
            {
                throw new ArgumentException(
                    "A characterisation needs at least two points to have an axis.", nameof(noise));
            }

            ReadOnlySpan<float> levels = noise.LevelsDbm;
            var copy = new double[levels.Length];

            for (int i = 0; i < levels.Length; i++)
            {
                copy[i] = levels[i];
            }

            return new NoiseFloor(
                copy,
                noise.FrequencyAt(0),
                noise.BinWidthHz,
                noise.ResolutionBandwidthHz);
        }

        /// <summary>
        /// The floor's level at a frequency, scaled to a resolution bandwidth.
        /// </summary>
        /// <param name="frequencyHz">Frequency to read, in hertz.</param>
        /// <param name="resolutionBandwidthHz">The RBW to express it at; positive and finite.</param>
        /// <returns>The level in dBm.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="resolutionBandwidthHz"/> is not positive.</exception>
        /// <remarks>
        /// Nearest point, not interpolated. A noise floor is a slowly varying thing sampled far
        /// more finely than it varies; interpolating between two samples of a random quantity would
        /// dress up the characterisation's own uncertainty as detail. Outside the characterised
        /// span the nearest end is used, which is the honest extrapolation of a measurement that
        /// stopped there.
        /// </remarks>
        public double LevelAt(double frequencyHz, double resolutionBandwidthHz)
        {
            RequirePositive(resolutionBandwidthHz, nameof(resolutionBandwidthHz));

            double level = _binWidthHz > 0.0
                ? _levelsDbm[Nearest(frequencyHz)]
                : _levelsDbm[0];

            // Noise power scales with noise bandwidth, so a floor measured at one RBW reads
            // 10*log10(ratio) different at another.
            return level + 10.0 * Math.Log10(resolutionBandwidthHz / ResolutionBandwidthHz);
        }

        private int Nearest(double frequencyHz)
        {
            int index = (int)Math.Round((frequencyHz - _startHz) / _binWidthHz);

            if (index < 0)
            {
                return 0;
            }

            return index >= _levelsDbm.Length ? _levelsDbm.Length - 1 : index;
        }

        private static void RequirePositive(double value, string name)
        {
            if (!(value > 0.0) || double.IsInfinity(value))
            {
                throw new ArgumentOutOfRangeException(
                    name, value, name + " must be positive and finite.");
            }
        }

        private static void RequireLevel(double value, string name)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                throw new ArgumentOutOfRangeException(name, value, name + " must be finite.");
            }
        }
    }

    /// <summary>
    /// Subtracts a characterised noise floor from a measurement (<c>REQ-DSP-024</c>'s
    /// <em>Noise Correction</em>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Powers subtract; decibels do not.</strong> A measured bin holds the signal's power
    /// plus the instrument's, because the two are uncorrelated and add as powers. So the correction
    /// is <c>P_signal = P_measured − P_noise</c>, done in linear power and converted back — not a
    /// subtraction of the two dB figures, which would be a division and would be wrong by an amount
    /// that grows as the signal approaches the floor. Near the floor is exactly where anyone
    /// switches this on.
    /// </para>
    /// <para>
    /// <strong>What it does not do is invent headroom.</strong> Correction improves the estimate of
    /// a signal that is there; it cannot recover one that is not. A noise-only input corrects to
    /// zero power, and zero power is reported as <see cref="AmplitudeScale.FloorDbm"/> — the same
    /// limit an empty bin reports — rather than as a negative power or a level below it. Half the
    /// bins of a noise-only trace measure above their expected floor and half below, so an
    /// uncorrected-for subtraction would leave a trace of implausibly deep nulls that looked like
    /// measured structure.
    /// </para>
    /// <para>
    /// <strong>Phase does not survive it.</strong> Subtracting an incoherent power leaves a
    /// magnitude, so the corrected frame reports <see cref="SpectrumFrame.HasPhase"/> as false and
    /// the phase formats become unselectable — rather than displaying the phase the signal had
    /// before a power-domain operation was done to its magnitude.
    /// </para>
    /// </remarks>
    public static class NoiseCorrection
    {
        /// <summary>
        /// Subtracts a noise floor from a frame.
        /// </summary>
        /// <param name="frame">The measured spectrum.</param>
        /// <param name="floor">The characterised floor.</param>
        /// <returns>A new frame; the original is unchanged.</returns>
        /// <exception cref="ArgumentNullException">An argument is null.</exception>
        public static SpectrumFrame Apply(SpectrumFrame frame, NoiseFloor floor)
        {
            if (frame == null)
            {
                throw new ArgumentNullException(nameof(frame));
            }

            if (floor == null)
            {
                throw new ArgumentNullException(nameof(floor));
            }

            ReadOnlySpan<float> complex = frame.Complex;
            var corrected = new float[complex.Length];
            AmplitudeScale scale = frame.Scale;
            double rbw = frame.ResolutionBandwidthHz;

            for (int i = 0; i < frame.PointCount; i++)
            {
                double re = complex[i * 2];
                double im = complex[i * 2 + 1];
                double measuredVoltsSquared = re * re + im * im;

                // The floor is a level; turn it back into the volts-squared the frame stores, so
                // the subtraction happens in the one quantity both sides are expressed in.
                double noiseVoltsSquared = scale.DbmToVoltsSquared(
                    floor.LevelAt(frame.FrequencyAt(i), rbw));

                double remaining = measuredVoltsSquared - noiseVoltsSquared;

                // A bin that measured at or below its expected floor has nothing left to report.
                // Zero, not a negative power and not a level below the reported limit.
                corrected[i * 2] = remaining > 0.0 ? (float)Math.Sqrt(remaining) : 0.0f;
                corrected[i * 2 + 1] = 0.0f;
            }

            return frame.WithNoiseCorrection(corrected, true);
        }

        /// <summary>
        /// A one-line description of the correction, for the annotation.
        /// </summary>
        /// <param name="floor">The floor in force, or <c>null</c> when correction is off.</param>
        public static string Describe(NoiseFloor floor) =>
            floor == null
                ? string.Empty
                : "Noise corr " + floor.PointCount.ToString(CultureInfo.CurrentCulture) +
                  " pts at " + floor.ResolutionBandwidthHz.ToString(
                      "0.###", CultureInfo.CurrentCulture) + " Hz RBW";
    }
}
