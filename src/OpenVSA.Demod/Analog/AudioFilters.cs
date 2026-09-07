using System;

namespace OpenVSA.Demod.Analog
{
    /// <summary>
    /// Post-detection filtering of the recovered audio (<c>REQ-DEM-010a</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Single-pole sections, applied forwards only, and both choices are deliberate.</strong>
    /// A de-emphasis network IS a single pole — that is what a time constant means — so anything
    /// sharper would not be the thing the standards specify. Running it forwards only keeps its
    /// phase response, which is also part of what it is; a zero-phase forward-and-back pass would
    /// square the magnitude response and halve the time constant, giving a filter that is not
    /// 75 µs de-emphasis under a name that says it is.
    /// </para>
    /// <para>
    /// <strong>The order is de-emphasis, then high-pass, then low-pass.</strong> De-emphasis
    /// belongs to the signal's own definition — it undoes what the transmitter did — so it comes
    /// first, before any choice the measurement makes. The high-pass then removes the carrier's
    /// slow wander before the low-pass sets the audio bandwidth, so that a large slow term is gone
    /// before anything else is decided about the band.
    /// </para>
    /// <para>
    /// <strong>Started from the signal rather than from zero.</strong> A recursive filter begun at
    /// zero spends its first time constants climbing to the signal, and that transient is a large
    /// excursion at the start of the record which a peak deviation would find and report. Each
    /// section starts at the first sample's own value, which is the steady state it would have
    /// reached had the record begun earlier.
    /// </para>
    /// </remarks>
    internal static class AudioFilters
    {
        /// <summary>Applies whatever filtering the settings ask for.</summary>
        /// <param name="audio">The recovered audio.</param>
        /// <param name="sampleRateHz">Its sample rate.</param>
        /// <param name="settings">What to apply.</param>
        /// <returns>The filtered audio, as a new array.</returns>
        internal static double[] Apply(
            double[] audio, double sampleRateHz, AnalogDemodSettings settings)
        {
            double[] filtered = (double[])audio.Clone();

            if (settings.DeEmphasisSeconds > 0.0)
            {
                // THE EXACT POLE, exp(-T/tau), and not T/(T + tau).
                //
                // The second is the backward-difference approximation to the first, and it is the
                // one this was written with. Measured: a tone at the 75 us network's own corner of
                // 2122 Hz came out 3.148 dB down instead of 3.010, because T/(T + tau) puts the
                // filter's real corner slightly low. That is 0.14 dB of error in a figure whose
                // whole purpose is to be the standard's, and it disagreed with Alpha() below --
                // which had a comment explaining why the exact pole is the right one and was not
                // being used here.
                filtered = LowPass(
                    filtered,
                    1.0 - Math.Exp(-1.0 / (sampleRateHz * settings.DeEmphasisSeconds)));
            }

            if (settings.HighPassHz > 0.0)
            {
                filtered = HighPass(filtered, Alpha(settings.HighPassHz, sampleRateHz));
            }

            if (settings.LowPassHz > 0.0)
            {
                filtered = LowPass(filtered, Alpha(settings.LowPassHz, sampleRateHz));
            }

            return filtered;
        }

        /// <summary>The single-pole coefficient for a corner frequency.</summary>
        /// <param name="cornerHz">Where the section is 3 dB down.</param>
        /// <param name="sampleRateHz">The sample rate.</param>
        /// <returns>The coefficient, bounded to a stable range.</returns>
        /// <remarks>
        /// From the exact pole, <c>1 - exp(-2·pi·f/fs)</c>, rather than the bilinear approximation
        /// <c>2·pi·f/(2·pi·f + fs)</c>. The two agree well below Nyquist and part company as the
        /// corner approaches it, where the approximation puts the corner in the wrong place — and a
        /// post-detection low-pass at a good fraction of the audio sample rate is a thing people
        /// actually ask for.
        /// </remarks>
        private static double Alpha(double cornerHz, double sampleRateHz)
        {
            double alpha = 1.0 - Math.Exp(-2.0 * Math.PI * cornerHz / sampleRateHz);

            return Math.Min(1.0, Math.Max(0.0, alpha));
        }

        private static double[] LowPass(double[] values, double alpha)
        {
            var output = new double[values.Length];
            double state = values.Length == 0 ? 0.0 : values[0];

            for (int index = 0; index < values.Length; index++)
            {
                state += alpha * (values[index] - state);
                output[index] = state;
            }

            return output;
        }

        /// <summary>
        /// A high-pass as the signal less its own low-passed self.
        /// </summary>
        /// <remarks>
        /// The complement of the low-pass above, rather than a separately derived difference
        /// equation, so the two corners mean the same thing: a high-pass and a low-pass at the same
        /// frequency sum back to the input exactly. Two independent derivations would agree in the
        /// pass bands and differ by a fraction of a decibel where it matters.
        /// </remarks>
        private static double[] HighPass(double[] values, double alpha)
        {
            double[] low = LowPass(values, alpha);
            var output = new double[values.Length];

            for (int index = 0; index < values.Length; index++)
            {
                output[index] = values[index] - low[index];
            }

            return output;
        }
    }
}
