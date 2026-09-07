using System;

namespace OpenVSA.Demod.Results
{
    /// <summary>
    /// The pre-demodulation window and where the Result Length sits inside it
    /// (<c>REQ-DEM-032</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Wider than the result on purpose.</strong> The requirement asks for a window 20 %
    /// larger than the Result Length "so transition regions are visible" — a burst's leading and
    /// trailing ramps, the settling of a filter, whatever the result window was positioned to
    /// exclude. The result traces show what was measured; this shows what was there.
    /// </para>
    /// <para>
    /// <strong>It is not a second measurement.</strong> These are the same samples the result was
    /// cut from, at the same rate, read over a wider span — so nothing here can disagree with the
    /// result, and nothing here feeds it. The chain writes this in step 7 and never reads it back.
    /// </para>
    /// <para>
    /// <strong>A separate family from <c>REQ-DEM-080</c>'s catalogue.</strong> Those fifteen traces
    /// are all cut to the Result Length; these three are not, and folding them into one list would
    /// make "samples of the analysed window" mean two different lengths in one enumeration. The
    /// requirement keeps the words apart — "pre-demodulation traces" against "result traces" — and
    /// so does this.
    /// </para>
    /// </remarks>
    public sealed class PreDemodWaveform
    {
        private readonly float[] _samples;

        internal PreDemodWaveform(
            float[] samples,
            int samplesPerSymbol,
            double symbolRateHz,
            int resultOffsetSamples,
            int resultSymbolCount,
            double symbolSpan)
        {
            _samples = samples ?? new float[0];
            SamplesPerSymbol = samplesPerSymbol;
            SymbolRateHz = symbolRateHz;
            ResultOffsetSamples = resultOffsetSamples;
            ResultSymbolCount = resultSymbolCount;
            SymbolSpan = symbolSpan;
        }

        /// <summary>The complex waveform, interleaved real and imaginary.</summary>
        public ReadOnlySpan<float> Samples => new ReadOnlySpan<float>(_samples);

        /// <summary>How many complex samples the window holds.</summary>
        public int SampleCount => _samples.Length / 2;

        /// <summary>Samples per symbol, at the internal processing rate.</summary>
        public int SamplesPerSymbol { get; }

        /// <summary>The symbol rate, in hertz.</summary>
        public double SymbolRateHz { get; }

        /// <summary>Where the first result symbol sits in this window, in samples.</summary>
        public int ResultOffsetSamples { get; }

        /// <summary>How many symbols the Result Length window holds.</summary>
        public int ResultSymbolCount { get; }

        /// <summary>
        /// How many symbols this window spans, which need not be whole.
        /// </summary>
        /// <remarks>
        /// The number the criterion's 1.2 factor is checked against. Fractional because the window
        /// is cut in samples and 0.2 of a Result Length is rarely a whole number of them; reporting
        /// it rounded would hide by how much.
        /// </remarks>
        public double SymbolSpan { get; }

        /// <summary>
        /// This window's span as a multiple of the Result Length.
        /// </summary>
        /// <remarks>
        /// <c>REQ-DEM-032</c>'s factor, computed rather than assumed, so a test can ask the result
        /// what it did instead of recomputing it from the settings and comparing an expectation
        /// with itself.
        /// </remarks>
        public double SpanFactor =>
            ResultSymbolCount <= 0 ? double.NaN : SymbolSpan / ResultSymbolCount;

        /// <summary>The sample at an index, as a constellation point.</summary>
        /// <param name="index">The sample index.</param>
        /// <returns>The point.</returns>
        /// <exception cref="ArgumentOutOfRangeException">The index is outside the window.</exception>
        public ConstellationPoint SampleAt(int index)
        {
            if (index < 0 || index >= SampleCount)
            {
                throw new ArgumentOutOfRangeException(nameof(index));
            }

            return new ConstellationPoint(_samples[2 * index], _samples[(2 * index) + 1]);
        }

        /// <inheritdoc />
        public override string ToString() =>
            "pre-demodulation window: " + SampleCount + " samples, " +
            SymbolSpan.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) +
            " symbols, " +
            SpanFactor.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) +
            " x the Result Length";
    }
}
