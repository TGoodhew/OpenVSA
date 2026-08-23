using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using OpenVSA.Core;

namespace OpenVSA.Synthesis
{
    /// <summary>
    /// A generated modulated burst, together with the truth about it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The truth is the point.</strong> Every acceptance criterion in the display group is
    /// worded against a signal whose symbols and symbol clock are known —
    /// "<em>checked against the generated signal's known symbol clock, so a half-symbol offset
    /// fails</em>", "<em>verified against a signal in which one symbol is displaced so the correct
    /// point is identifiable, which an off-by-one selection fails</em>". A waveform on its own
    /// cannot fail either of those; a waveform that comes with
    /// <see cref="DecisionSampleIndices"/> and <see cref="Symbols"/> can.
    /// </para>
    /// <para>
    /// The samples are the same interleaved layout the rest of the product uses, so a burst goes
    /// through the real acquisition and analysis path rather than round it.
    /// </para>
    /// </remarks>
    public sealed class SyntheticBurst
    {
        private readonly float[] _samples;
        private readonly ReadOnlyCollection<int> _symbols;
        private readonly ReadOnlyCollection<int> _decisions;

        internal SyntheticBurst(
            ModulationScheme scheme,
            float[] samples,
            IList<int> symbols,
            IList<int> decisionSampleIndices,
            int samplesPerSymbol,
            double sampleRateHz,
            int displacedSymbolIndex)
        {
            Scheme = scheme;
            _samples = samples;
            _symbols = new ReadOnlyCollection<int>(symbols);
            _decisions = new ReadOnlyCollection<int>(decisionSampleIndices);
            SamplesPerSymbol = samplesPerSymbol;
            SampleRateHz = sampleRateHz;
            DisplacedSymbolIndex = displacedSymbolIndex;
        }

        /// <summary>The modulation the symbols were drawn from.</summary>
        public ModulationScheme Scheme { get; }

        /// <summary>Interleaved real and imaginary samples, two per complex sample.</summary>
        public ReadOnlySpan<float> Samples => new ReadOnlySpan<float>(_samples);

        /// <summary>How many complex samples there are.</summary>
        public int SampleCount => _samples.Length / 2;

        /// <summary>The symbol values that were transmitted, in order.</summary>
        public IReadOnlyList<int> Symbols => _symbols;

        /// <summary>
        /// Which sample each symbol's decision instant falls on.
        /// </summary>
        /// <remarks>
        /// <c>REQ-UI-050</c>'s "points drawn only at symbol decision instants" and
        /// <c>REQ-UI-051</c>'s vertical reference lines are both this list. A display that folded
        /// or sampled half a symbol out disagrees with it, which is the failure both criteria name.
        /// </remarks>
        public IReadOnlyList<int> DecisionSampleIndices => _decisions;

        /// <summary>Samples per symbol; the symbol clock in samples.</summary>
        public int SamplesPerSymbol { get; }

        /// <summary>The sample rate, in hertz.</summary>
        public double SampleRateHz { get; }

        /// <summary>The symbol rate, in hertz.</summary>
        public double SymbolRateHz => SampleRateHz / SamplesPerSymbol;

        /// <summary>
        /// Which symbol was moved off its ideal point, or −1 if none was.
        /// </summary>
        /// <remarks>
        /// <c>REQ-DEM-083</c> asks for exactly this: "a signal in which one symbol is displaced so
        /// the correct point is identifiable, which an off-by-one selection fails". Without it, a
        /// selection that highlighted symbol <em>k</em> ± 1 would look right, because every other
        /// symbol of the same value sits in the same place.
        /// </remarks>
        public int DisplacedSymbolIndex { get; }

        /// <summary>The value at a symbol's decision instant, as measured from the waveform.</summary>
        /// <param name="symbol">Which symbol, from zero.</param>
        /// <exception cref="ArgumentOutOfRangeException">There is no such symbol.</exception>
        public SymbolPoint MeasuredAt(int symbol)
        {
            if (symbol < 0 || symbol >= _decisions.Count)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(symbol), symbol, "This burst has " + _decisions.Count + " symbols.");
            }

            int at = _decisions[symbol] * 2;

            return new SymbolPoint(_samples[at], _samples[at + 1]);
        }

        /// <summary>
        /// The detected symbol stream, as <c>REQ-UI-052</c>'s bottom portion shows it.
        /// </summary>
        /// <param name="binary">Whether to spell each symbol in bits rather than as a value.</param>
        /// <param name="perRow">How many symbols to a row.</param>
        /// <returns>One line per row.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="perRow"/> is not positive.</exception>
        public IReadOnlyList<string> SymbolStream(bool binary, int perRow)
        {
            if (perRow < 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(perRow), perRow, "A row holds at least one symbol.");
            }

            var rows = new List<string>();
            var row = new StringBuilder();

            for (int i = 0; i < _symbols.Count; i++)
            {
                if (row.Length > 0)
                {
                    row.Append(' ');
                }

                row.Append(binary
                    ? Scheme.BitsOf(_symbols[i])
                    : _symbols[i].ToString(CultureInfo.InvariantCulture));

                if ((i + 1) % perRow == 0)
                {
                    rows.Add(row.ToString());
                    row.Clear();
                }
            }

            if (row.Length > 0)
            {
                rows.Add(row.ToString());
            }

            return rows;
        }

        /// <summary>
        /// The error vector magnitude of the burst, as a fraction of the reference power.
        /// </summary>
        /// <remarks>
        /// Referenced to the average power of the ideal constellation, which is unity by
        /// <see cref="ModulationScheme"/>'s normalisation — so this is the RMS EVM an error summary
        /// would show, and the metric <c>REQ-UI-053</c>'s top portion lays out.
        /// </remarks>
        public double ErrorVectorMagnitude()
        {
            double sum = 0.0;

            for (int symbol = 0; symbol < _symbols.Count; symbol++)
            {
                SymbolPoint measured = MeasuredAt(symbol);
                SymbolPoint ideal = Scheme.IdealPoints[_symbols[symbol]];

                double distance = measured.DistanceTo(ideal);

                sum += distance * distance;
            }

            return _symbols.Count == 0 ? 0.0 : Math.Sqrt(sum / _symbols.Count);
        }

        /// <summary>
        /// How many symbols a decision at the burst's own instants recovers correctly.
        /// </summary>
        /// <remarks>
        /// The generator's own check on itself. A pulse shape or a clock that is wrong makes this
        /// fall short of the symbol count, and every criterion resting on "the known symbols" would
        /// then be resting on nothing.
        /// </remarks>
        public int CorrectlyDecided()
        {
            int right = 0;

            for (int symbol = 0; symbol < _symbols.Count; symbol++)
            {
                double error;

                if (Scheme.Decide(MeasuredAt(symbol), out error) == _symbols[symbol])
                {
                    right++;
                }
            }

            return right;
        }

        /// <summary>
        /// The burst as a block, so it can go through the real acquisition path.
        /// </summary>
        /// <param name="centerFrequencyHz">What to say the block was tuned to.</param>
        /// <param name="acquiredUtc">The block's timestamp; must be UTC.</param>
        /// <exception cref="ArgumentException"><paramref name="acquiredUtc"/> is not UTC.</exception>
        public IqBlock ToBlock(double centerFrequencyHz, DateTime acquiredUtc)
        {
            if (acquiredUtc.Kind != DateTimeKind.Utc)
            {
                throw new ArgumentException("An instant must be UTC.", nameof(acquiredUtc));
            }

            IqBlock block = IqBlock.Rent(new IqBlockMetadata(
                sampleCount: SampleCount,
                sampleRateHz: SampleRateHz,
                centerFrequencyHz: centerFrequencyHz,
                isBaseband: false,
                fullScaleVolts: 1.0,
                referenceLevelDbm: 0.0,
                sequenceNumber: 1,
                acquiredUtc: acquiredUtc,
                triggerOffsetSeconds: 0.0,
                triggerCorrectionsApplied: false,
                source: new FrontEndId("synthetic"),
                extended: null));

            Span<float> destination = block.GetSamples();

            for (int i = 0; i < _samples.Length; i++)
            {
                destination[i] = _samples[i];
            }

            return block;
        }

        /// <inheritdoc />
        public override string ToString() =>
            Scheme.Name + ", " + _symbols.Count + " symbols at " +
            (SymbolRateHz / 1e3).ToString("0.###", CultureInfo.InvariantCulture) + " kHz, " +
            SamplesPerSymbol + " samples per symbol";
    }

    /// <summary>
    /// Generates modulated bursts whose symbols and symbol clock are known
    /// (<c>REQ-UI-050</c>, <c>REQ-UI-051</c>, <c>REQ-UI-052</c>, <c>REQ-DEM-083</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Root-raised-cosine shaped, because an unshaped signal cannot exercise an eye.</strong>
    /// A rectangular pulse gives an eye with vertical sides and no inter-symbol interference to
    /// show, which is the one shape that makes a wrong fold look right. The filter is applied once
    /// here rather than as a matched pair, so the pulse at the decision instants is a raised cosine
    /// — zero at every other symbol instant, which is what makes <see cref="SyntheticBurst.Symbols"/>
    /// recoverable exactly and the generator checkable against itself.
    /// </para>
    /// <para>
    /// <strong>Deterministic from a seed.</strong> A display test that failed once and passed on
    /// the next run would be worse than no test; the symbol sequence and the noise are both drawn
    /// from a seeded generator, so a failure can be looked at again.
    /// </para>
    /// </remarks>
    public sealed class SyntheticSymbolSource
    {
        /// <summary>Samples per symbol when nothing else is asked for.</summary>
        /// <remarks>
        /// Eight, which is enough for an eye to have a shape between the decision instants and few
        /// enough that a burst of a thousand symbols is a block a real front end could have
        /// produced.
        /// </remarks>
        public const int DefaultSamplesPerSymbol = 8;

        /// <summary>The pulse shape's roll-off when nothing else is asked for.</summary>
        public const double DefaultRollOff = 0.35;

        /// <summary>How many symbols either side of centre the pulse shape spans.</summary>
        public const int PulseSpanSymbols = 6;

        /// <summary>The modulation to draw symbols from.</summary>
        /// <exception cref="ArgumentNullException">The value is null.</exception>
        public ModulationScheme Scheme
        {
            get { return _scheme; }
            set { _scheme = value ?? throw new ArgumentNullException(nameof(value)); }
        }

        private ModulationScheme _scheme = ModulationScheme.Qpsk();

        /// <summary>Samples per symbol; the symbol clock in samples.</summary>
        public int SamplesPerSymbol { get; set; } = DefaultSamplesPerSymbol;

        /// <summary>The sample rate the burst claims, in hertz.</summary>
        public double SampleRateHz { get; set; } = 12.8e6;

        /// <summary>The pulse shape's roll-off, from 0 to 1.</summary>
        public double RollOff { get; set; } = DefaultRollOff;

        /// <summary>
        /// Noise added to each sample, as a signal-to-noise ratio in decibels.
        /// </summary>
        /// <remarks>
        /// <see cref="double.PositiveInfinity"/> adds none, which is what a test of geometry wants:
        /// a clean signal has an eye whose openings are unambiguous and a constellation whose
        /// points sit exactly on their ideal states.
        /// </remarks>
        public double SignalToNoiseDb { get; set; } = double.PositiveInfinity;

        /// <summary>The seed the symbols and the noise are drawn from.</summary>
        public int Seed { get; set; } = 20260727;

        /// <summary>
        /// Which symbol to move off its ideal point, or −1 to move none.
        /// </summary>
        /// <remarks>
        /// <c>REQ-DEM-083</c>'s "one symbol is displaced so the correct point is identifiable".
        /// </remarks>
        public int DisplacedSymbolIndex { get; set; } = -1;

        /// <summary>How far the displaced symbol is moved, in constellation units.</summary>
        public double Displacement { get; set; } = 0.35;

        /// <summary>
        /// Generates a burst.
        /// </summary>
        /// <param name="symbolCount">How many symbols to send; at least one.</param>
        /// <exception cref="ArgumentOutOfRangeException">The settings are outside their ranges.</exception>
        public SyntheticBurst Generate(int symbolCount)
        {
            if (symbolCount < 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(symbolCount), symbolCount, "A burst carries at least one symbol.");
            }

            if (SamplesPerSymbol < 2)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(SamplesPerSymbol), SamplesPerSymbol,
                    "A symbol needs at least two samples for anything to happen between decisions.");
            }

            if (!(RollOff >= 0.0) || RollOff > 1.0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(RollOff), RollOff, "A roll-off runs from 0 to 1.");
            }

            var random = new Random(Seed);

            var symbols = new List<int>(symbolCount);

            for (int i = 0; i < symbolCount; i++)
            {
                symbols.Add(random.Next(_scheme.Order));
            }

            // The points actually transmitted, which differ from the ideal ones at the displaced
            // symbol and nowhere else.
            var sent = new SymbolPoint[symbolCount];

            for (int i = 0; i < symbolCount; i++)
            {
                SymbolPoint ideal = _scheme.IdealPoints[symbols[i]];

                sent[i] = i == DisplacedSymbolIndex
                    ? new SymbolPoint(ideal.I + Displacement, ideal.Q - Displacement)
                    : ideal;
            }

            double[] pulse = RaisedCosinePulse();

            // Head and tail room for the pulse's own span, so the first and last symbols are shaped
            // by the whole filter rather than by half of it.
            int lead = PulseSpanSymbols * SamplesPerSymbol;
            int sampleCount = lead * 2 + symbolCount * SamplesPerSymbol;

            var samples = new float[sampleCount * 2];
            var decisions = new List<int>(symbolCount);

            for (int i = 0; i < symbolCount; i++)
            {
                int centre = lead + i * SamplesPerSymbol;

                decisions.Add(centre);

                for (int tap = 0; tap < pulse.Length; tap++)
                {
                    int at = centre - lead + tap;

                    if (at < 0 || at >= sampleCount)
                    {
                        continue;
                    }

                    samples[at * 2] += (float)(sent[i].I * pulse[tap]);
                    samples[at * 2 + 1] += (float)(sent[i].Q * pulse[tap]);
                }
            }

            AddNoise(samples, random);

            return new SyntheticBurst(
                _scheme, samples, symbols, decisions, SamplesPerSymbol, SampleRateHz,
                DisplacedSymbolIndex);
        }

        private void AddNoise(float[] samples, Random random)
        {
            if (double.IsPositiveInfinity(SignalToNoiseDb))
            {
                return;
            }

            // Referenced to unit average symbol power, which is what ModulationScheme normalises to,
            // so the figure asked for is the figure a receiver would measure.
            double sigma = Math.Sqrt(Math.Pow(10.0, -SignalToNoiseDb / 10.0) / 2.0);

            for (int i = 0; i < samples.Length; i += 2)
            {
                double u1 = 1.0 - random.NextDouble();
                double u2 = random.NextDouble();
                double magnitude = Math.Sqrt(-2.0 * Math.Log(u1));

                samples[i] += (float)(sigma * magnitude * Math.Cos(2.0 * Math.PI * u2));
                samples[i + 1] += (float)(sigma * magnitude * Math.Sin(2.0 * Math.PI * u2));
            }
        }

        /// <summary>
        /// The raised-cosine pulse, sampled at the output rate.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <strong>Raised cosine, not root raised cosine, and that is deliberate.</strong> A real
        /// link splits the shaping between transmitter and receiver so that the two together are a
        /// raised cosine; this generator is both ends at once, and what the display work needs is a
        /// waveform that is <em>already</em> matched-filtered — one whose value at each decision
        /// instant is exactly the symbol that was sent, because the raised cosine is zero at every
        /// other symbol instant. That is what makes the burst checkable against its own truth.
        /// </para>
        /// <para>
        /// The two limits are removable singularities: at <c>t = 0</c> and at
        /// <c>t = ±1/(2α)</c> symbol periods the closed form divides by zero, and the values below
        /// are the limits. Leaving them out gives a pulse with a notch at the centre and an eye
        /// that never opens.
        /// </para>
        /// </remarks>
        private double[] RaisedCosinePulse()
        {
            int half = PulseSpanSymbols * SamplesPerSymbol;
            var taps = new double[half * 2 + 1];

            for (int i = 0; i < taps.Length; i++)
            {
                double t = (i - half) / (double)SamplesPerSymbol;

                taps[i] = RaisedCosineAt(t, RollOff);
            }

            return taps;
        }

        private static double RaisedCosineAt(double t, double rollOff)
        {
            if (Math.Abs(t) < 1e-12)
            {
                return 1.0;
            }

            if (rollOff > 0.0 && Math.Abs(Math.Abs(t) - 1.0 / (2.0 * rollOff)) < 1e-9)
            {
                return Math.PI / 4.0 * Sinc(1.0 / (2.0 * rollOff));
            }

            double denominator = 1.0 - Math.Pow(2.0 * rollOff * t, 2.0);

            return Sinc(t) * Math.Cos(Math.PI * rollOff * t) / denominator;
        }

        private static double Sinc(double x) =>
            Math.Abs(x) < 1e-12 ? 1.0 : Math.Sin(Math.PI * x) / (Math.PI * x);
    }
}
