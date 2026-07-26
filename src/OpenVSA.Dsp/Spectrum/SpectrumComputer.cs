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
        /// Whether to display the analysis span rather than the whole acquired band.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The acquired band is deliberately wider than the analysis span — <c>REQ-ACQ-001</c>'s
        /// <c>Fs = 1.28 · Span</c> — and the surplus is where the front end's anti-alias filter
        /// rolls off. Displaying it shows the user the transition band as if it were measurement
        /// data, and puts a number on the axis that disagrees with the span they asked for.
        /// </para>
        /// <para>
        /// <strong>The point count follows the block, not a request.</strong> It is derived from
        /// the transform length of the data that actually arrived, through
        /// <see cref="AcquisitionLaw.PointsForTransformLength"/> — so what is displayed is always
        /// what the instrument was able to capture. Nothing here is told a span, a point count or a
        /// sample rate to expect, which is what keeps it correct for a front end that quantises its
        /// sample rate instead of honouring <c>1.28 · Span</c> exactly.
        /// </para>
        /// <para>
        /// The trim is symmetric about the centre bin on the complex path and starts at 0 Hz on the
        /// real one, and the resulting count is always an available one under
        /// <see cref="FrequencyPoints"/> — every power-of-two transform length maps to a
        /// <c>50 · 2^k + 1</c> point count by construction. A block too short to yield an available
        /// count is displayed whole rather than trimmed to an unavailable one.
        /// </para>
        /// </remarks>
        public bool TrimToAnalysisSpan { get; set; }

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

            double usableBandwidth = UsableBandwidthOf(block);

            return block.IsBaseband
                ? OneSided(block, scratch, n, scale, binWidth, usableBandwidth)
                : TwoSided(block, scratch, n, scale, binWidth, usableBandwidth);
        }

        /// <summary>
        /// The alias-free bandwidth a block declares, or 0 if it declares none.
        /// </summary>
        /// <param name="block">The block.</param>
        /// <remarks>
        /// A front end that knows how much of its sample rate is usable says so; the product's
        /// <c>Fs = 1.28 · Span</c> is then not consulted, because it describes the reference
        /// product rather than the instrument that produced this block.
        /// </remarks>
        private static double UsableBandwidthOf(IqBlock block)
        {
            object declared;

            if (block.Extended == null ||
                !block.Extended.TryGetValue(IqBlockMetadata.UsableBandwidthKey, out declared))
            {
                return 0.0;
            }

            if (!(declared is double))
            {
                return 0.0;
            }

            var bandwidth = (double)declared;
            return bandwidth > 0.0 && !double.IsInfinity(bandwidth) ? bandwidth : 0.0;
        }

        /// <summary>
        /// Points to display for a transform length, or 0 to display the whole acquired band.
        /// </summary>
        /// <param name="transformLength">The transform length the block actually gave.</param>
        /// <param name="path">The acquisition path the block came from.</param>
        /// <param name="usableBandwidthHz">The alias-free bandwidth the block declares, or 0.</param>
        /// <param name="binWidthHz">The bin width, in hertz.</param>
        /// <remarks>
        /// A block too short for an available point count is shown whole. Trimming it to an
        /// unavailable count would put a point count on screen that no setting could have asked
        /// for, and the honest thing at that size is to show what there is.
        /// </remarks>
        private int DisplayPointsFor(
            int transformLength, AnalysisPath path, double usableBandwidthHz, double binWidthHz)
        {
            if (!TrimToAnalysisSpan)
            {
                return 0;
            }

            if (usableBandwidthHz > 0.0 && binWidthHz > 0.0)
            {
                // The instrument's own figure wins. It is not required to land on an available
                // point count - REQ-DSP-022's ladder describes what a measurement may be *asked*
                // for, and this is what a particular instrument actually delivered.
                //
                // Rounded down, never up: the bandwidth is the width the front end says is
                // alias-free, so a display a bin wider than that is showing the filter's roll-off
                // as though it were data - which is the whole reason for trimming at all.
                int declared = path == AnalysisPath.ComplexZoom
                    ? (int)Math.Floor(usableBandwidthHz / 2.0 / binWidthHz) * 2 + 1
                    : (int)Math.Floor(usableBandwidthHz / binWidthHz) + 1;

                if (declared >= 2 && declared <= transformLength)
                {
                    return declared;
                }
            }

            int points = AcquisitionLaw.PointsForTransformLength(transformLength, path);
            return FrequencyPoints.IsValid(points) ? points : 0;
        }

        /// <summary>
        /// The complex zoom/IF path: bins shifted so the axis ascends, trimmed to the analysis span.
        /// </summary>
        private SpectrumFrame TwoSided(
            IqBlock block, double[] scratch, int n, AmplitudeScale scale, double binWidth,
            double usableBandwidthHz)
        {
            int half = n / 2;
            int trimmed = DisplayPointsFor(n, AnalysisPath.ComplexZoom, usableBandwidthHz, binWidth);

            // Bins either side of the centre. Untrimmed, that is the whole band; trimmed, the count
            // is 2m+1 so the axis is symmetric with a point exactly at the centre frequency.
            int points = trimmed > 0 ? trimmed : n;
            int m = (points - 1) / 2;
            int first = trimmed > 0 ? half - m : 0;
            var levels = new float[points * 2];
            double volts = scale.VoltsPerUnit;

            for (int i = 0; i < points; i++)
            {
                // Display index i is raw bin (first + i + N/2) mod N: the negative half comes first.
                int shifted = first + i;
                int k = shifted < half ? shifted + half : shifted - half;

                // Calibrated volts, not decibels: every format of REQ-DSP-041 is derived from
                // these, so the chain is applied once here and nowhere else.
                levels[i * 2] = (float)(scratch[k * 2] * volts);
                levels[i * 2 + 1] = (float)(scratch[k * 2 + 1] * volts);
            }

            return SpectrumFrame.Adopt(
                levels,
                scale,
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
            IqBlock block, double[] scratch, int n, AmplitudeScale scale, double binWidth,
            double usableBandwidthHz)
        {
            // From 0 Hz rather than about a centre, so there is no symmetry to preserve: the
            // displayed points are simply the first of them.
            int trimmed = DisplayPointsFor(n, AnalysisPath.RealBaseband, usableBandwidthHz, binWidth);
            int points = trimmed > 0 ? trimmed : n / 2 + 1;

            var levels = new float[points * 2];
            double single = scale.VoltsPerUnit;
            double doubled = scale.WithLinearGain(2.0).VoltsPerUnit;

            for (int k = 0; k < points; k++)
            {
                double volts = k == 0 || k == n / 2 ? single : doubled;
                levels[k * 2] = (float)(scratch[k * 2] * volts);
                levels[k * 2 + 1] = (float)(scratch[k * 2 + 1] * volts);
            }

            return SpectrumFrame.Adopt(
                levels,
                scale,
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
