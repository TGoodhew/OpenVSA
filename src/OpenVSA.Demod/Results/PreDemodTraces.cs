using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using OpenVSA.Demod.Chain;
using OpenVSA.Dsp.Fft;
using OpenVSA.Dsp.Windowing;

namespace OpenVSA.Demod.Results
{
    /// <summary>The three pre-demodulation traces of <c>REQ-DEM-032</c>.</summary>
    public enum PreDemodTrace
    {
        /// <summary>The measured complex waveform over the wider window.</summary>
        Time = 0,

        /// <summary>The averaged magnitude spectrum of that window.</summary>
        Spectrum,

        /// <summary>The magnitude spectrum of that window as one un-averaged transform.</summary>
        InstantaneousSpectrum,
    }

    /// <summary>One pre-demodulation trace's data, as the displays take it.</summary>
    /// <remarks>
    /// Shaped like <see cref="ResultTraceData"/> because the displays draw both, and different for
    /// the one reason that matters: it carries the <see cref="Window"/> it came from, so a display
    /// can mark where the Result Length sits inside it. Without that a wider trace is just a longer
    /// one, and the transition regions it exists to show cannot be told from signal.
    /// </remarks>
    public sealed class PreDemodTraceData
    {
        internal PreDemodTraceData(
            PreDemodTrace trace,
            ResultTraceDomain domain,
            bool isComplex,
            IList<double> values,
            double xStart,
            double xStep,
            string unit,
            PreDemodWaveform window)
        {
            Trace = trace;
            Domain = domain;
            IsComplex = isComplex;
            Values = new ReadOnlyCollection<double>(values ?? new List<double>());
            XStart = xStart;
            XStep = xStep;
            Unit = unit ?? string.Empty;
            Window = window;
        }

        /// <summary>Which trace this is.</summary>
        public PreDemodTrace Trace { get; }

        /// <summary>What the horizontal axis counts.</summary>
        public ResultTraceDomain Domain { get; }

        /// <summary>Whether <see cref="Values"/> is interleaved pairs rather than scalars.</summary>
        public bool IsComplex { get; }

        /// <summary>The data; interleaved real and imaginary when <see cref="IsComplex"/>.</summary>
        public IReadOnlyList<double> Values { get; }

        /// <summary>The first point's position on the horizontal axis.</summary>
        public double XStart { get; }

        /// <summary>The step between points on the horizontal axis.</summary>
        public double XStep { get; }

        /// <summary>The values' unit, where they have one.</summary>
        public string Unit { get; }

        /// <summary>The window these came from, and where the Result Length sits in it.</summary>
        public PreDemodWaveform Window { get; }

        /// <summary>How many points the trace holds.</summary>
        public int Count => IsComplex ? Values.Count / 2 : Values.Count;

        /// <inheritdoc />
        public override string ToString() =>
            Trace + ": " + Count + " point(s), " + Domain +
            (Unit.Length == 0 ? string.Empty : ", " + Unit);
    }

    /// <summary>
    /// The pre-demodulation traces: the same samples the result was cut from, over a window 20 %
    /// wider (<c>REQ-DEM-032</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Why these are not in <c>REQ-DEM-080</c>'s catalogue.</strong> Every trace there is
    /// cut to the Result Length; these three are deliberately not, and putting them in one list
    /// would make a trace's domain ambiguous about which window its samples belong to. The
    /// requirements keep the two families apart in their own words — "pre-demodulation traces"
    /// against "result traces" — and <c>REQ-DEM-080</c>'s criteria close its list at fifteen, so a
    /// new family arriving through <c>Enum.GetValues</c> would quietly change what that number
    /// means. See <see cref="ResultTraces.All"/>, which is written out for exactly that reason.
    /// </para>
    /// <para>
    /// <strong>Spectrum and Instantaneous Spectrum differ by estimator, not by window.</strong> The
    /// requirement gives all three traces the same 1.2 x window, so the distinction has to lie in
    /// how the spectrum is formed. Instantaneous Spectrum is one transform of the whole window —
    /// literally the spectrum of this record, transients and all, which is what makes it the one to
    /// read for a burst edge. Spectrum averages the magnitudes of half-overlapped segments of that
    /// same window, trading resolution for a steadier estimate, which is what makes it the one to
    /// read for a noise floor or a spur. Where the window is too short to divide they coincide, and
    /// that is correct rather than a degenerate case.
    /// </para>
    /// <para>
    /// <strong>Hann, and the same window function for both.</strong> A pre-demodulation spectrum is
    /// read for shape and for what lies beside the carrier, not for the amplitude of a tone, so the
    /// analyser's Flat Top default (<c>REQ-DSP-010</c>) would buy accuracy nobody is asking of this
    /// trace at nearly three times the bin width. Using one window function for both spectra also
    /// means the two traces differ <em>only</em> by the averaging, which is the difference they
    /// exist to show.
    /// </para>
    /// </remarks>
    public static class PreDemodTraces
    {
        /// <summary>Segments the averaged spectrum splits the window into.</summary>
        /// <remarks>
        /// Four, half-overlapped: enough to visibly steady the estimate, few enough that the
        /// resolution given up against the instantaneous trace stays a factor of four rather than
        /// an order of magnitude.
        /// </remarks>
        private const int Segments = 4;

        private static readonly ReadOnlyCollection<PreDemodTrace> Catalogue =
            new ReadOnlyCollection<PreDemodTrace>(
                new List<PreDemodTrace>
                {
                    PreDemodTrace.Time,
                    PreDemodTrace.Spectrum,
                    PreDemodTrace.InstantaneousSpectrum,
                });

        /// <summary>The three traces the requirement names.</summary>
        public static IReadOnlyList<PreDemodTrace> All => Catalogue;

        /// <summary>Whether a result can produce a pre-demodulation trace.</summary>
        /// <param name="result">The demodulation.</param>
        /// <param name="trace">The trace.</param>
        /// <returns>Whether <see cref="Take"/> would produce data.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="result"/> is null.</exception>
        public static bool IsAvailable(DemodResult result, PreDemodTrace trace)
        {
            if (result == null)
            {
                throw new ArgumentNullException(nameof(result));
            }

            return result.PreDemod != null && result.PreDemod.SampleCount > 0;
        }

        /// <summary>Why a trace is not available, for a display to say so.</summary>
        /// <param name="result">The demodulation.</param>
        /// <param name="trace">The trace.</param>
        /// <returns>The reason, or an empty string when the trace is available.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="result"/> is null.</exception>
        public static string ReasonUnavailable(DemodResult result, PreDemodTrace trace) =>
            IsAvailable(result, trace)
                ? string.Empty
                : "This demodulation cut no pre-demodulation window, so there is nothing wider " +
                  "than the result to draw. Any record long enough to produce a result is long " +
                  "enough to produce one of these, so this is a measurement that did not run.";

        /// <summary>Produces a pre-demodulation trace's data.</summary>
        /// <param name="result">The demodulation.</param>
        /// <param name="trace">The trace.</param>
        /// <returns>The data.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="result"/> is null.</exception>
        /// <exception cref="InvalidOperationException">
        /// The trace is not available; <see cref="ReasonUnavailable"/> says why.
        /// </exception>
        public static PreDemodTraceData Take(DemodResult result, PreDemodTrace trace)
        {
            if (!IsAvailable(result, trace))
            {
                throw new InvalidOperationException(ReasonUnavailable(result, trace));
            }

            PreDemodWaveform window = result.PreDemod;

            switch (trace)
            {
                case PreDemodTrace.Time:
                    return Time(window);

                case PreDemodTrace.Spectrum:
                    return Spectrum(window, trace, Segments);

                default:
                    return Spectrum(window, trace, 1);
            }
        }

        /// <summary>The complex waveform, with time measured from the first result symbol.</summary>
        /// <param name="window">The pre-demodulation window.</param>
        /// <remarks>
        /// <strong>The clock starts at the first result symbol, not at the window's first
        /// sample</strong>, so the leading transition region reads as negative time. That is the
        /// trace's whole purpose: it says what was excluded and on which side. An origin at the
        /// window's own start would put the result somewhere in the middle of a trace whose axis
        /// agreed with nothing else on the screen.
        /// </remarks>
        private static PreDemodTraceData Time(PreDemodWaveform window)
        {
            var values = new List<double>(window.SampleCount * 2);

            for (int sample = 0; sample < window.SampleCount; sample++)
            {
                ConstellationPoint point = window.SampleAt(sample);

                values.Add(point.I);
                values.Add(point.Q);
            }

            double rate = window.SymbolRateHz * window.SamplesPerSymbol;
            double step = rate <= 0.0 ? 1.0 : 1.0 / rate;

            return new PreDemodTraceData(
                PreDemodTrace.Time,
                ResultTraceDomain.Sample,
                true,
                values,
                -window.ResultOffsetSamples * step,
                step,
                string.Empty,
                window);
        }

        /// <summary>
        /// The magnitude spectrum of the window, averaged over <paramref name="segments"/> of it.
        /// </summary>
        /// <param name="window">The pre-demodulation window.</param>
        /// <param name="trace">Which of the two spectra is being produced.</param>
        /// <param name="segments">How many half-overlapped segments to average; 1 for none.</param>
        /// <remarks>
        /// <para>
        /// <strong>Magnitudes are averaged, not complex values.</strong> Averaging the transforms
        /// would let segments whose phases differ cancel, and the noise floor this trace is read for
        /// would fall by the square root of the segment count — an estimator that reports less noise
        /// the more of it you look at.
        /// </para>
        /// <para>
        /// <strong>The transform length is the power of two at or below the segment length</strong>,
        /// never above it, so every bin is filled with signal. Zero-padding up interpolates the
        /// spectrum: it makes a smoother-looking trace out of exactly the same information, and a
        /// spur that is one bin wide would be drawn as though it had been resolved.
        /// </para>
        /// <para>
        /// Zero frequency is rotated to the middle. This is a complex baseband spectrum and the
        /// negative frequencies are signal, not an artefact of the transform to be discarded.
        /// </para>
        /// </remarks>
        private static PreDemodTraceData Spectrum(
            PreDemodWaveform window, PreDemodTrace trace, int segments)
        {
            int available = window.SampleCount;

            // Half-overlapped segments of length L cover (n+1)/2 * L samples, so this is the
            // longest segment that fits n of them into what there is.
            int wanted = segments <= 1 ? available : (2 * available) / (segments + 1);
            int length = 1;

            while (length * 2 <= wanted)
            {
                length *= 2;
            }

            IFftProvider fft = FftProviders.Active;

            if (length < 2 || length > available || !fft.SupportsLength(length))
            {
                return Empty(trace, window);
            }

            int step = segments <= 1 ? length : Math.Max(1, length / 2);
            int taken = 0;

            var sum = new double[length];
            var shaped = new double[2 * length];
            Window taper = Window.Get(WindowType.Hann, length);

            for (int start = 0; start + length <= available; start += step)
            {
                ReadOnlySpan<double> weights = taper.Coefficients;

                for (int sample = 0; sample < length; sample++)
                {
                    ConstellationPoint point = window.SampleAt(start + sample);
                    double weight = weights[sample];

                    shaped[2 * sample] = point.I * weight;
                    shaped[(2 * sample) + 1] = point.Q * weight;
                }

                fft.Forward(new Span<double>(shaped));

                for (int bin = 0; bin < length; bin++)
                {
                    double re = shaped[2 * bin];
                    double im = shaped[(2 * bin) + 1];

                    sum[bin] += Math.Sqrt((re * re) + (im * im));
                }

                taken++;

                if (segments <= 1)
                {
                    break;
                }
            }

            if (taken == 0)
            {
                return Empty(trace, window);
            }

            double rate = window.SymbolRateHz * window.SamplesPerSymbol;
            double scale = 1.0 / (taken * taper.CoherentGain * length);
            var values = new List<double>(length);

            for (int bin = 0; bin < length; bin++)
            {
                int source = (bin + (length / 2)) % length;
                double magnitude = sum[source] * scale;

                // Floored rather than guarded: an exactly zero bin is a real outcome on a padded
                // or silent record, and log of it would put negative infinity into a trace a
                // display has to scale.
                values.Add(20.0 * Math.Log10(Math.Max(magnitude, 1e-30)));
            }

            return new PreDemodTraceData(
                trace,
                ResultTraceDomain.Frequency,
                false,
                values,
                rate <= 0.0 ? -(length / 2) : -rate / 2.0,
                rate <= 0.0 ? 1.0 : rate / length,
                "dB",
                window);
        }

        private static PreDemodTraceData Empty(PreDemodTrace trace, PreDemodWaveform window) =>
            new PreDemodTraceData(
                trace,
                ResultTraceDomain.Frequency,
                false,
                new List<double>(),
                0.0,
                1.0,
                "dB",
                window);
    }
}
