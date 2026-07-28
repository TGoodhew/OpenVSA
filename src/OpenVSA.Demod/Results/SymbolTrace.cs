using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;

namespace OpenVSA.Demod.Results
{
    /// <summary>A point on the complex plane, in normalised constellation units.</summary>
    public readonly struct ConstellationPoint
    {
        /// <summary>Creates a point.</summary>
        /// <param name="i">In-phase component.</param>
        /// <param name="q">Quadrature component.</param>
        public ConstellationPoint(double i, double q)
        {
            I = i;
            Q = q;
        }

        /// <summary>In-phase component.</summary>
        public double I { get; }

        /// <summary>Quadrature component.</summary>
        public double Q { get; }

        /// <summary>Distance from another point.</summary>
        /// <param name="other">The other point.</param>
        public double DistanceTo(ConstellationPoint other)
        {
            double di = I - other.I;
            double dq = Q - other.Q;

            return Math.Sqrt(di * di + dq * dq);
        }

        /// <inheritdoc />
        public override string ToString() =>
            "(" + I.ToString("0.000", CultureInfo.InvariantCulture) + ", " +
            Q.ToString("0.000", CultureInfo.InvariantCulture) + ")";
    }

    /// <summary>
    /// A demodulated result: the symbols, where they were decided, and the waveform they came from.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>One object for four displays, because they are four views of one result.</strong>
    /// <c>REQ-UI-050</c>'s constellation is <see cref="Measured"/> at the decision instants,
    /// <c>REQ-UI-051</c>'s eye is <see cref="Samples"/> folded on <see cref="SamplesPerSymbol"/>,
    /// and <c>REQ-UI-052</c>'s one trace is <see cref="Symbols"/> under the metrics computed from
    /// the first two. Splitting them into four sources is how the symbol table comes to disagree
    /// with the constellation about which symbol is which.
    /// </para>
    /// <para>
    /// <strong>Ideal and measured are both carried.</strong> A constellation overlays the ideal
    /// states (<c>REQ-UI-050</c>) and an error summary is the distance between the two
    /// (<c>REQ-UI-053</c>); a result that carried only the measured points could do neither, and
    /// one that carried only the decided symbol would have thrown away the error before anything
    /// could measure it.
    /// </para>
    /// <para>
    /// <strong>Here rather than in <c>OpenVSA.Dsp</c>.</strong> <c>REQ-DSP-040</c> keeps the base
    /// trace types free of any demodulation dependency and an architecture test enforces it, so
    /// the result of a demodulation lives on this side of that line. <c>OpenVSA.Measurement</c>
    /// already references this assembly, which is what lets the shell and the harness both reach
    /// it.
    /// </para>
    /// </remarks>
    public sealed class SymbolTrace
    {
        private readonly float[] _samples;
        private readonly ReadOnlyCollection<int> _symbols;
        private readonly ReadOnlyCollection<int> _decisions;
        private readonly ReadOnlyCollection<ConstellationPoint> _ideal;
        private readonly ReadOnlyCollection<ConstellationPoint> _measured;
        private readonly ReadOnlyCollection<int> _modulationTypes;

        /// <summary>Creates a result.</summary>
        /// <param name="modulation">What the symbols were decided against, for the annotation.</param>
        /// <param name="bitsPerSymbol">Bits one symbol carries; at least one.</param>
        /// <param name="levelsPerAxis">Distinct levels on the I axis; at least two.</param>
        /// <param name="symbols">The decided symbol values.</param>
        /// <param name="ideal">The ideal point for each symbol, in the same order.</param>
        /// <param name="measured">The measured point for each symbol, in the same order.</param>
        /// <param name="decisionSampleIndices">Which sample each decision instant falls on.</param>
        /// <param name="samples">Interleaved real and imaginary samples the result came from.</param>
        /// <param name="samplesPerSymbol">The symbol clock, in samples; at least two.</param>
        /// <param name="symbolRateHz">The symbol rate, in hertz.</param>
        /// <param name="modulationTypes">
        /// Which modulation each symbol belongs to, for a mixed-modulation signal
        /// (<c>REQ-UI-050</c>), or <c>null</c> when every symbol is the same.
        /// </param>
        /// <exception cref="ArgumentNullException">A required argument is null.</exception>
        /// <exception cref="ArgumentException">The per-symbol lists are not the same length.</exception>
        /// <exception cref="ArgumentOutOfRangeException">A count is below its minimum.</exception>
        public SymbolTrace(
            string modulation,
            int bitsPerSymbol,
            int levelsPerAxis,
            IList<int> symbols,
            IList<ConstellationPoint> ideal,
            IList<ConstellationPoint> measured,
            IList<int> decisionSampleIndices,
            float[] samples,
            int samplesPerSymbol,
            double symbolRateHz,
            IList<int> modulationTypes = null)
        {
            if (symbols == null)
            {
                throw new ArgumentNullException(nameof(symbols));
            }

            if (ideal == null)
            {
                throw new ArgumentNullException(nameof(ideal));
            }

            if (measured == null)
            {
                throw new ArgumentNullException(nameof(measured));
            }

            if (decisionSampleIndices == null)
            {
                throw new ArgumentNullException(nameof(decisionSampleIndices));
            }

            if (samples == null)
            {
                throw new ArgumentNullException(nameof(samples));
            }

            if (bitsPerSymbol < 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(bitsPerSymbol), bitsPerSymbol, "A symbol carries at least one bit.");
            }

            if (levelsPerAxis < 2)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(levelsPerAxis), levelsPerAxis,
                    "A modulation has at least two levels on an axis.");
            }

            if (samplesPerSymbol < 2)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(samplesPerSymbol), samplesPerSymbol,
                    "A symbol needs at least two samples for an eye to have a shape between " +
                    "decisions.");
            }

            if (ideal.Count != symbols.Count ||
                measured.Count != symbols.Count ||
                decisionSampleIndices.Count != symbols.Count)
            {
                throw new ArgumentException(
                    "The per-symbol lists disagree: " + symbols.Count + " symbols, " +
                    ideal.Count + " ideal, " + measured.Count + " measured, " +
                    decisionSampleIndices.Count + " decision instants.",
                    nameof(symbols));
            }

            if (modulationTypes != null && modulationTypes.Count != symbols.Count)
            {
                throw new ArgumentException(
                    "A mixed-modulation result names a modulation for every symbol; " +
                    modulationTypes.Count + " were given for " + symbols.Count + " symbols.",
                    nameof(modulationTypes));
            }

            Modulation = modulation ?? string.Empty;
            BitsPerSymbol = bitsPerSymbol;
            LevelsPerAxis = levelsPerAxis;
            SamplesPerSymbol = samplesPerSymbol;
            SymbolRateHz = symbolRateHz;

            _symbols = new ReadOnlyCollection<int>(symbols);
            _ideal = new ReadOnlyCollection<ConstellationPoint>(ideal);
            _measured = new ReadOnlyCollection<ConstellationPoint>(measured);
            _decisions = new ReadOnlyCollection<int>(decisionSampleIndices);
            _samples = samples;

            _modulationTypes = modulationTypes == null
                ? null
                : new ReadOnlyCollection<int>(modulationTypes);
        }

        /// <summary>What the symbols were decided against.</summary>
        public string Modulation { get; }

        /// <summary>Bits one symbol carries.</summary>
        public int BitsPerSymbol { get; }

        /// <summary>
        /// Distinct levels on the I axis — the <em>m</em> of <c>REQ-UI-051</c>'s m − 1 eyes.
        /// </summary>
        public int LevelsPerAxis { get; }

        /// <summary>How many eyes an eye diagram of this result shows (<c>REQ-UI-051</c>).</summary>
        public int EyeOpenings => LevelsPerAxis - 1;

        /// <summary>The decided symbol values, in order.</summary>
        public IReadOnlyList<int> Symbols => _symbols;

        /// <summary>The ideal point for each symbol.</summary>
        public IReadOnlyList<ConstellationPoint> Ideal => _ideal;

        /// <summary>The measured point for each symbol — what a constellation draws.</summary>
        public IReadOnlyList<ConstellationPoint> Measured => _measured;

        /// <summary>
        /// Which sample each symbol's decision instant falls on (<c>REQ-UI-050</c>).
        /// </summary>
        public IReadOnlyList<int> DecisionSampleIndices => _decisions;

        /// <summary>
        /// Which modulation each symbol belongs to, or <c>null</c> when they all agree.
        /// </summary>
        /// <remarks>
        /// <c>REQ-UI-050</c>'s "a mixed-modulation signal colours symbols by modulation type via
        /// the <c>Mod Type N</c> entries". Null rather than an array of zeros, so that a display
        /// can tell "one modulation" from "every symbol happens to be type 0" and use the
        /// <c>Symbol</c> colour for the first.
        /// </remarks>
        public IReadOnlyList<int> ModulationTypes => _modulationTypes;

        /// <summary>Whether the symbols carry more than one modulation.</summary>
        public bool IsMixedModulation => _modulationTypes != null;

        /// <summary>Interleaved real and imaginary samples, two per complex sample.</summary>
        public ReadOnlySpan<float> Samples => new ReadOnlySpan<float>(_samples);

        /// <summary>How many complex samples the waveform holds.</summary>
        public int SampleCount => _samples.Length / 2;

        /// <summary>The symbol clock, in samples.</summary>
        public int SamplesPerSymbol { get; }

        /// <summary>The symbol rate, in hertz.</summary>
        public double SymbolRateHz { get; }

        /// <summary>How many symbols the result holds.</summary>
        public int SymbolCount => _symbols.Count;

        /// <summary>The sample at an index, as a point.</summary>
        /// <param name="index">Which complex sample.</param>
        /// <exception cref="ArgumentOutOfRangeException">There is no such sample.</exception>
        public ConstellationPoint SampleAt(int index)
        {
            if (index < 0 || index >= SampleCount)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(index), index, "This result holds " + SampleCount + " samples.");
            }

            return new ConstellationPoint(_samples[index * 2], _samples[index * 2 + 1]);
        }

        /// <summary>
        /// The error vector for a symbol: measured minus ideal.
        /// </summary>
        /// <param name="symbol">Which symbol.</param>
        /// <exception cref="ArgumentOutOfRangeException">There is no such symbol.</exception>
        public ConstellationPoint ErrorAt(int symbol)
        {
            if (symbol < 0 || symbol >= _symbols.Count)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(symbol), symbol, "This result holds " + _symbols.Count + " symbols.");
            }

            return new ConstellationPoint(
                _measured[symbol].I - _ideal[symbol].I,
                _measured[symbol].Q - _ideal[symbol].Q);
        }

        /// <summary>
        /// The largest absolute value the waveform reaches, for scaling a display.
        /// </summary>
        /// <remarks>
        /// Over both components and the whole record, so that an eye and a constellation of the
        /// same result are drawn to the same scale — two displays of one signal that disagreed
        /// about its amplitude would be two signals as far as a reader is concerned.
        /// </remarks>
        public double PeakExcursion()
        {
            double peak = 0.0;

            for (int i = 0; i < _samples.Length; i++)
            {
                double magnitude = Math.Abs(_samples[i]);

                if (magnitude > peak)
                {
                    peak = magnitude;
                }
            }

            return peak;
        }

        /// <inheritdoc />
        public override string ToString() =>
            Modulation + ", " + _symbols.Count + " symbols, " + SamplesPerSymbol +
            " samples per symbol, " + EyeOpenings + " eye(s)";
    }
}
