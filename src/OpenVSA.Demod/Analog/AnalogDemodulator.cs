using System;
using System.Collections.Generic;
using OpenVSA.Dsp.Fft;
using OpenVSA.Dsp.Windowing;

namespace OpenVSA.Demod.Analog
{
    /// <summary>
    /// AM, FM and PM demodulation of an acquired complex record (<c>REQ-DEM-010a</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>A separate chain from the digital one, and not a step in it.</strong> The digital
    /// chain's fourteen steps are built around a symbol clock, a constellation and a decision — an
    /// analog carrier has none of the three, and threading it through steps that assume all of them
    /// would mean fourteen special cases. What the two share is the acquisition and the trace
    /// surface, which is where the sharing belongs.
    /// </para>
    /// <para>
    /// <strong>The detectors are exact, and that is why SINAD can exceed 60 dB.</strong> The
    /// envelope is <c>|z|</c>, which for <c>A(1 + m·cos)·e^(jθ)</c> is <c>A|1 + m·cos|</c> with no
    /// approximation in it. The frequency is the angle of <c>z[n]·conj(z[n-1])</c>, which is the
    /// phase advance over one sample exactly, needs no unwrapping, and cannot slip a cycle. Neither
    /// introduces a harmonic, so what SINAD measures is the signal and the analysis window rather
    /// than the detector.
    /// </para>
    /// <para>
    /// <strong>What the sample-difference frequency detector does cost</strong> is a first-order
    /// hold: the angle over one sample interval is the AVERAGE frequency across it, not the
    /// instantaneous one at its end, which scales a sinusoidal deviation by
    /// <c>sinc(pi·f_m/f_s)</c>. At a 1 kHz rate sampled at 200 kHz that is 4 parts per million, and
    /// it is a linear scaling rather than a distortion — so it moves the deviation a little and
    /// leaves SINAD alone. Stated because it is a real bias and the criterion's tolerance is 1 %.
    /// </para>
    /// </remarks>
    public sealed class AnalogDemodulator
    {
        /// <summary>Bins either side of a peak counted as belonging to it.</summary>
        /// <remarks>
        /// Four, which is the half-width of a Blackman-Harris main lobe. Fewer would leave part of
        /// the fundamental in the "everything else" of SINAD and report a worse figure than the
        /// signal deserves; more would start absorbing the noise the figure is about.
        /// </remarks>
        private const int LobeHalfWidth = 4;

        /// <summary>Demodulates a record.</summary>
        /// <param name="samples">The acquisition, interleaved real and imaginary.</param>
        /// <param name="sampleRateHz">The rate it was sampled at.</param>
        /// <param name="settings">What to demodulate and how to filter it.</param>
        /// <returns>The demodulated audio, the carrier's own traces, and the metrics.</returns>
        /// <exception cref="ArgumentNullException">An argument is null.</exception>
        /// <exception cref="ArgumentException">The record or the settings cannot be used.</exception>
        public AnalogDemodResult Run(
            float[] samples, double sampleRateHz, AnalogDemodSettings settings)
        {
            if (samples == null)
            {
                throw new ArgumentNullException(nameof(samples));
            }

            if (settings == null)
            {
                throw new ArgumentNullException(nameof(settings));
            }

            settings.Validate();

            if (sampleRateHz <= 0.0)
            {
                throw new ArgumentException("The sample rate is positive.", nameof(sampleRateHz));
            }

            int count = samples.Length / 2;

            // Two samples is the least a frequency detector can work from, and a spectrum of two
            // points measures nothing. Sixteen is the least this will pretend to analyse.
            if (count < 16)
            {
                throw new ArgumentException(
                    "An analog demodulation needs at least 16 samples; this record holds " +
                    count + ".",
                    nameof(samples));
            }

            double[] envelope = Envelope(samples, count);
            double[] frequency = InstantaneousFrequency(samples, count, sampleRateHz);
            double[] phase = InstantaneousPhase(samples, count);

            double carrierAmplitude = Mean(envelope);
            double carrierFrequency = Mean(frequency);

            double[] audio = Detect(settings.Type, envelope, frequency, phase, carrierAmplitude);

            bool filtered = settings.FiltersAudio;

            if (filtered)
            {
                audio = AudioFilters.Apply(audio, sampleRateHz, settings);
            }

            var spectrum = new AudioSpectrum(audio, sampleRateHz);

            return new AnalogDemodResult(
                settings.Type,
                sampleRateHz,
                audio,
                frequency,
                envelope,
                spectrum,
                Measure(settings, audio, envelope, spectrum, carrierAmplitude, carrierFrequency));
        }

        /// <summary>The envelope, sample by sample.</summary>
        private static double[] Envelope(float[] samples, int count)
        {
            var envelope = new double[count];

            for (int sample = 0; sample < count; sample++)
            {
                double i = samples[2 * sample];
                double q = samples[(2 * sample) + 1];

                envelope[sample] = Math.Sqrt((i * i) + (q * q));
            }

            return envelope;
        }

        /// <summary>
        /// Instantaneous frequency, from the angle between consecutive samples.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <strong>Not a difference of two arctangents.</strong> That form has to unwrap, and an
        /// unwrap is a decision about which multiple of 2·pi was meant — wrong once, and every
        /// sample after it is wrong by a constant. The angle of <c>z[n]·conj(z[n-1])</c> is the
        /// advance itself, already in <c>(-pi, pi]</c>, with no state and nothing to get wrong
        /// short of a deviation past half the sample rate, which is aliasing rather than a bug in
        /// the detector.
        /// </para>
        /// <para>
        /// The first sample has no predecessor, so it takes the second's value. Repeating one
        /// sample keeps the audio the same length as the record, which is what lets the frequency,
        /// the envelope and the acquisition share one time axis; the alternative is three traces
        /// that start at different times for a reason no display could show.
        /// </para>
        /// </remarks>
        private static double[] InstantaneousFrequency(
            float[] samples, int count, double sampleRateHz)
        {
            var frequency = new double[count];
            double scale = sampleRateHz / (2.0 * Math.PI);

            for (int sample = 1; sample < count; sample++)
            {
                double i0 = samples[2 * (sample - 1)];
                double q0 = samples[(2 * (sample - 1)) + 1];
                double i1 = samples[2 * sample];
                double q1 = samples[(2 * sample) + 1];

                // z[n] * conj(z[n-1])
                double re = (i1 * i0) + (q1 * q0);
                double im = (q1 * i0) - (i1 * q0);

                frequency[sample] = Math.Atan2(im, re) * scale;
            }

            frequency[0] = frequency[1];

            return frequency;
        }

        /// <summary>
        /// Instantaneous phase, unwrapped, with the carrier's linear ramp removed.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Unwrapping is unavoidable here — phase modulation is the phase, so it cannot be measured
        /// from differences alone — but it is done on the DIFFERENCES, which are already wrapped
        /// into the right interval by the same product as the frequency detector uses. Accumulating
        /// those is an unwrap that cannot pick the wrong branch.
        /// </para>
        /// <para>
        /// The carrier's own offset appears as a straight line through the accumulated phase, and
        /// what is left after a least-squares line is removed is the modulation. Removing the MEAN
        /// alone would leave the ramp in and report a phase deviation that grew with the record.
        /// </para>
        /// </remarks>
        private static double[] InstantaneousPhase(float[] samples, int count)
        {
            var phase = new double[count];

            for (int sample = 1; sample < count; sample++)
            {
                double i0 = samples[2 * (sample - 1)];
                double q0 = samples[(2 * (sample - 1)) + 1];
                double i1 = samples[2 * sample];
                double q1 = samples[(2 * sample) + 1];

                double re = (i1 * i0) + (q1 * q0);
                double im = (q1 * i0) - (i1 * q0);

                phase[sample] = phase[sample - 1] + Math.Atan2(im, re);
            }

            return RemoveLinearTrend(phase);
        }

        /// <summary>Removes the least-squares straight line from a series.</summary>
        private static double[] RemoveLinearTrend(double[] values)
        {
            int count = values.Length;
            double meanX = (count - 1) / 2.0;
            double meanY = Mean(values);

            double covariance = 0.0;
            double variance = 0.0;

            for (int index = 0; index < count; index++)
            {
                double dx = index - meanX;

                covariance += dx * (values[index] - meanY);
                variance += dx * dx;
            }

            double slope = variance <= 0.0 ? 0.0 : covariance / variance;
            var detrended = new double[count];

            for (int index = 0; index < count; index++)
            {
                detrended[index] = values[index] - meanY - (slope * (index - meanX));
            }

            return detrended;
        }

        /// <summary>The recovered audio, in the units the type is measured in.</summary>
        /// <remarks>
        /// AM is a fraction of the carrier rather than volts, so that a depth is read off it
        /// directly and does not depend on the acquisition's own scaling. FM is hertz about the
        /// mean and PM is radians about the fitted line — both already the deviation, because both
        /// detectors measure the parameter and the carrier is its mean.
        /// </remarks>
        private static double[] Detect(
            AnalogDemodType type,
            double[] envelope,
            double[] frequency,
            double[] phase,
            double carrierAmplitude)
        {
            switch (type)
            {
                case AnalogDemodType.Am:
                    var modulation = new double[envelope.Length];

                    if (carrierAmplitude <= 0.0)
                    {
                        return modulation;
                    }

                    for (int sample = 0; sample < envelope.Length; sample++)
                    {
                        modulation[sample] = (envelope[sample] / carrierAmplitude) - 1.0;
                    }

                    return modulation;

                case AnalogDemodType.Fm:
                    double mean = Mean(frequency);
                    var deviation = new double[frequency.Length];

                    for (int sample = 0; sample < frequency.Length; sample++)
                    {
                        deviation[sample] = frequency[sample] - mean;
                    }

                    return deviation;

                default:
                    return (double[])phase.Clone();
            }
        }

        private static AnalogMetrics Measure(
            AnalogDemodSettings settings,
            double[] audio,
            double[] envelope,
            AudioSpectrum spectrum,
            double carrierAmplitude,
            double carrierFrequencyHz)
        {
            double rate = spectrum.FundamentalHz;
            double sinad = spectrum.SinadDb(LobeHalfWidth);
            double distortion = spectrum.DistortionPercent(
                LobeHalfWidth, settings.HarmonicsForDistortion);

            double residualFraction = spectrum.ResidualFraction(
                LobeHalfWidth, settings.HarmonicsForDistortion);

            double peak = 0.0;
            double sumOfSquares = 0.0;

            foreach (double value in audio)
            {
                peak = Math.Max(peak, Math.Abs(value));
                sumOfSquares += value * value;
            }

            double rms = Math.Sqrt(sumOfSquares / audio.Length);
            bool am = settings.Type == AnalogDemodType.Am;
            bool fm = settings.Type == AnalogDemodType.Fm;
            bool pm = settings.Type == AnalogDemodType.Pm;

            return new AnalogMetrics(
                settings.Type,
                am ? DepthPercent(envelope) : double.NaN,
                fm ? peak : double.NaN,
                fm ? rms : double.NaN,
                pm ? peak : double.NaN,
                pm ? rms : double.NaN,
                rate,
                sinad,
                distortion,
                fm ? residualFraction * rms * Math.Sqrt(2.0) : double.NaN,
                am ? residualFraction * rms * Math.Sqrt(2.0) * 100.0 : double.NaN,
                carrierFrequencyHz,
                carrierAmplitude,
                settings.FiltersAudio);
        }

        /// <summary>Modulation depth from the envelope's extremes.</summary>
        private static double DepthPercent(double[] envelope)
        {
            double least = double.MaxValue;
            double most = double.MinValue;

            foreach (double value in envelope)
            {
                least = Math.Min(least, value);
                most = Math.Max(most, value);
            }

            double sum = most + least;

            return sum <= 0.0 ? 0.0 : (most - least) / sum * 100.0;
        }

        private static double Mean(IReadOnlyList<double> values)
        {
            double sum = 0.0;

            foreach (double value in values)
            {
                sum += value;
            }

            return values.Count == 0 ? 0.0 : sum / values.Count;
        }
    }
}
