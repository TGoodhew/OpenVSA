using System;
using OpenVSA.Core;
using OpenVSA.Core.Threading;
using OpenVSA.Dsp.Fft;
using OpenVSA.Dsp.Windowing;

namespace OpenVSA.Dsp.Spectrum
{
    /// <summary>
    /// Turns an <see cref="IqBlock"/> into a <see cref="SpectrumFrame"/>: window, transform,
    /// magnitude, amplitude chain.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>REQ-DSP-001</c>: whole-block, non-causal analysis. The block is windowed and transformed
    /// as a unit, with no state carried between blocks — which is what lets the same computation
    /// serve live acquisition and recorded playback without a mode switch.
    /// </para>
    /// <para>
    /// <strong>Scratch buffers are held, output arrays are not.</strong> The transform scratch is
    /// reused across frames (<c>REQ-NFR-002</c>); the level array is fresh per frame because the
    /// frame is published to another thread and must never be written again — see
    /// <see cref="SpectrumFrame"/>.
    /// </para>
    /// <para>
    /// <strong>Not thread-safe, deliberately.</strong> One instance belongs to one pump. Sharing
    /// one across threads would have them overwrite each other's scratch, and a lock would serialise
    /// the most expensive stage in the product; two pumps should hold two computers.
    /// </para>
    /// </remarks>
    public sealed class SpectrumComputer
    {
        private readonly IFftProvider _fft;
        private readonly AmplitudeChain _amplitudeChain;

        private double[] _scratch;
        private Window _window;

        /// <summary>Creates a computer with the default window, the configured FFT provider and a 50 Ω chain.</summary>
        public SpectrumComputer()
            : this(Window.Default, null, null)
        {
        }

        /// <summary>Creates a computer.</summary>
        /// <param name="windowType">Analysis window; <see cref="Window.Default"/> is Flat Top per <c>REQ-DSP-010</c>.</param>
        /// <param name="fftProvider">FFT provider, or <c>null</c> for the configured one (<c>REQ-NFR-004</c>).</param>
        /// <param name="amplitudeChain">Amplitude chain, or <c>null</c> for the 50 Ω default.</param>
        public SpectrumComputer(WindowType windowType, IFftProvider fftProvider, AmplitudeChain amplitudeChain)
        {
            WindowType = windowType;
            _fft = fftProvider ?? FftProviders.Active;
            _amplitudeChain = amplitudeChain ?? new AmplitudeChain();
        }

        /// <summary>The analysis window.</summary>
        public WindowType WindowType { get; }

        /// <summary>The FFT provider in use.</summary>
        public IFftProvider FftProvider => _fft;

        /// <summary>The amplitude chain in use (<c>REQ-AMP-001</c>).</summary>
        public AmplitudeChain AmplitudeChain => _amplitudeChain;

        /// <summary>
        /// Width of the frequency axis to display, in hertz. Zero shows the whole Nyquist band.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The acquired band is deliberately wider than the analysis span — <c>REQ-ACQ-001</c>'s
        /// <c>Fs = 1.28 Span</c> — and the surplus is where the front end's anti-alias filter rolls
        /// off. Displaying it would show the user the transition band as if it were measurement
        /// data, and would put a number on the axis that disagrees with the span they asked for.
        /// </para>
        /// <para>
        /// The trim is symmetric about the centre bin and always leaves an odd number of points, so
        /// the axis is exactly <c>Span</c> wide with a point at each end and one at the centre —
        /// which is why the reference product's point counts are odd (801, 6401) rather than powers
        /// of two. The rest of <c>REQ-ACQ-001</c> and <c>REQ-DSP-022</c> — deriving the point count
        /// from RBW, the <c>50·2^k</c> constraint, Auto mode — belongs to the acquisition planner
        /// and is not done here.
        /// </para>
        /// </remarks>
        public double DisplaySpanHz { get; set; }

        /// <summary>
        /// The transform length used for a block of the given size: the largest power of two that
        /// fits.
        /// </summary>
        /// <param name="sampleCount">Complex samples available; must be positive.</param>
        /// <returns>The transform length, in complex points.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="sampleCount"/> is not positive.</exception>
        /// <remarks>
        /// Truncating rather than zero-padding. Padding to the next power of two would interpolate
        /// the display without improving resolution and would quietly change the noise bandwidth
        /// the levels are referred to; discarding the tail states the loss plainly. The acquisition
        /// planner of <c>REQ-ACQ-001</c> exists so that in a configured measurement there is no
        /// tail to discard.
        /// </remarks>
        public static int TransformLengthFor(int sampleCount)
        {
            if (sampleCount <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(sampleCount), sampleCount, "Sample count must be positive.");
            }

            int length = 1;
            while (length <= sampleCount / 2)
            {
                length <<= 1;
            }

            return length;
        }

        /// <summary>
        /// Computes the spectrum of a block.
        /// </summary>
        /// <param name="block">The block; not retained, and not modified.</param>
        /// <returns>An immutable frame, in ascending frequency order.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="block"/> is null.</exception>
        /// <exception cref="ArgumentException">The provider cannot transform the resulting length.</exception>
        public SpectrumFrame Compute(IqBlock block)
        {
            if (block == null)
            {
                throw new ArgumentNullException(nameof(block));
            }

            // REQ-NFR-010: the UI thread never performs DSP. Active in Debug builds only.
            ThreadAffinity.AssertNotOnUiThread("spectrum computation");

            int n = TransformLengthFor(block.SampleCount);

            if (!_fft.SupportsLength(n))
            {
                throw new ArgumentException(
                    "FFT provider '" + _fft.Name + "' cannot transform " + n + " points.",
                    nameof(block));
            }

            if (_window == null || _window.Length != n || _window.Type != WindowType)
            {
                _window = Window.Get(WindowType, n);
            }

            if (_scratch == null || _scratch.Length < n * 2)
            {
                _scratch = new double[n * 2];
            }

            ReadOnlySpan<float> samples = block.GetSamples();
            ReadOnlySpan<double> coefficients = _window.Coefficients;
            double[] scratch = _scratch;

            // Widen and window in one pass. REQ-DSP-002 puts the accumulation in double, and doing
            // the multiply here rather than through Window.ApplyTo saves a second sweep of what is,
            // at 2^20 points, 16 MB of scratch.
            for (int i = 0; i < n; i++)
            {
                double w = coefficients[i];
                scratch[i * 2] = samples[i * 2] * w;
                scratch[i * 2 + 1] = samples[i * 2 + 1] * w;
            }

            _fft.Forward(new Span<double>(scratch, 0, n * 2));

            AmplitudeScale scale = _amplitudeChain.ScaleFor(block, _window, n);
            double binWidth = block.SampleRateHz / n;

            return block.IsBaseband
                ? OneSided(block, scratch, n, scale, binWidth)
                : TwoSided(block, scratch, n, scale, binWidth);
        }

        /// <summary>
        /// The complex zoom/IF path: bins shifted so the axis ascends, trimmed to the display span.
        /// </summary>
        private SpectrumFrame TwoSided(
            IqBlock block, double[] scratch, int n, AmplitudeScale scale, double binWidth)
        {
            int half = n / 2;

            // Bins either side of the centre. Untrimmed, that is the whole band; trimmed, it is
            // half the display span, and the point count is 2m+1 so the axis is symmetric.
            int m = half;

            if (DisplaySpanHz > 0.0)
            {
                int requested = (int)Math.Round(DisplaySpanHz / 2.0 / binWidth);
                m = requested < 1 ? 1 : (requested > half ? half : requested);
            }

            int points = DisplaySpanHz > 0.0 ? m * 2 + 1 : n;
            int first = DisplaySpanHz > 0.0 ? half - m : 0;
            var levels = new float[points];

            for (int i = 0; i < points; i++)
            {
                // Display index i is raw bin (first + i + N/2) mod N: the negative half comes first.
                int shifted = first + i;
                int k = shifted < half ? shifted + half : shifted - half;
                levels[i] = (float)scale.ToDbm(scratch[k * 2], scratch[k * 2 + 1]);
            }

            return SpectrumFrame.Adopt(
                levels,
                startFrequencyHz: block.CenterFrequencyHz + (first - half) * binWidth,
                binWidthHz: binWidth,
                centerFrequencyHz: block.CenterFrequencyHz,
                sampleRateHz: block.SampleRateHz,
                isBaseband: false,
                window: WindowType,
                equivalentNoiseBandwidthBins: _window.Enbw,
                referenceLevelDbm: block.ReferenceLevelDbm,
                sequenceNumber: block.SequenceNumber,
                acquiredUtc: block.AcquiredUtc,
                source: block.Source);
        }

        /// <summary>
        /// The real-baseband path: bins 0 … N/2, from 0 Hz, with the interior bins doubled.
        /// </summary>
        /// <remarks>
        /// The frequency axis starts at 0 Hz and ignores <c>CenterFrequencyHz</c>, which is 0 for a
        /// baseband acquisition by definition (<c>REQ-ACQ-001</c>'s real-baseband path). DC and
        /// Nyquist are not doubled — see <see cref="AmplitudeChain.OneSidedBinGainDb"/>.
        /// </remarks>
        private SpectrumFrame OneSided(
            IqBlock block, double[] scratch, int n, AmplitudeScale scale, double binWidth)
        {
            int points = n / 2 + 1;

            if (DisplaySpanHz > 0.0)
            {
                // From 0 Hz rather than about a centre, so no symmetry to preserve: the span is
                // simply the number of bins that fit in it.
                int requested = (int)Math.Round(DisplaySpanHz / binWidth) + 1;
                points = requested < 2 ? 2 : (requested > points ? points : requested);
            }

            var levels = new float[points];
            AmplitudeScale doubled = scale.WithAdditionalOffset(AmplitudeChain.OneSidedBinGainDb);

            for (int k = 0; k < points; k++)
            {
                AmplitudeScale applied = k == 0 || k == n / 2 ? scale : doubled;
                levels[k] = (float)applied.ToDbm(scratch[k * 2], scratch[k * 2 + 1]);
            }

            return SpectrumFrame.Adopt(
                levels,
                startFrequencyHz: 0.0,
                binWidthHz: binWidth,
                centerFrequencyHz: (points - 1) * binWidth / 2.0,
                sampleRateHz: block.SampleRateHz,
                isBaseband: true,
                window: WindowType,
                equivalentNoiseBandwidthBins: _window.Enbw,
                referenceLevelDbm: block.ReferenceLevelDbm,
                sequenceNumber: block.SequenceNumber,
                acquiredUtc: block.AcquiredUtc,
                source: block.Source);
        }
    }
}
