using System;

namespace OpenVSA.Synthesis
{
    /// <summary>
    /// A modulated signal that goes on for ever, filled block by block at any sample rate
    /// (<c>REQ-SIM-001</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Why this is not <see cref="SyntheticSymbolSource"/>.</strong> That one makes a burst:
    /// a fixed number of symbols, at a whole number of samples per symbol, shaped so that its
    /// samples at the decision instants are exactly the symbols it sent. Those properties are what
    /// let it be checked against its own truth without a demodulator, and they are the wrong
    /// properties for a front end. A front end is handed a sample rate by the acquisition plan and
    /// asked for block after block of a signal that does not restart; the symbol rate is the
    /// user's, so samples per symbol is whatever the two happen to make, and it is rarely a whole
    /// number.
    /// </para>
    /// <para>
    /// <strong>Root raised cosine, because this one is a transmitter.</strong> The burst source
    /// deliberately shapes with the full raised cosine — it is both ends of a link at once. This is
    /// one end: a real transmitter's half of the Nyquist pair, which is what makes the analyser's
    /// matched filter the right thing to apply and what lets a user demodulate this signal with the
    /// demodulator's own defaults. <c>REQ-DEM-020</c> is where that split is stated.
    /// </para>
    /// <para>
    /// <strong>Continuity comes from the symbols being a function of their index.</strong> Nothing
    /// is remembered between blocks except how many samples have gone by: symbol <em>k</em> is a
    /// hash of the seed and <em>k</em>, so the block that starts halfway through symbol 4 096 finds
    /// the same symbol 4 096 the previous block ended with. A generator that drew from a running
    /// random stream would have to be filled in exactly one order, and any test that skipped a block
    /// would get a different signal.
    /// </para>
    /// </remarks>
    public sealed class ContinuousModulatedSource
    {
        /// <summary>The pulse span this source uses unless it is told otherwise.</summary>
        public const int DefaultPulseSpanSymbols = 6;

        /// <summary>Steps per symbol period in the pulse's lookup table.</summary>
        /// <remarks>
        /// The pulse is evaluated at a position that is different for every sample, so it is
        /// tabulated once and read with linear interpolation rather than computed from its closed
        /// form a dozen times per sample. At 1/256 of a symbol the interpolation error is below a
        /// millionth — far under anything a measurement of this signal resolves — and it turns four
        /// trigonometric evaluations per tap into two multiplies.
        /// </remarks>
        private const int TableStepsPerSymbol = 256;

        private ModulationScheme _scheme = ModulationScheme.Qpsk();

        private int _pulseSpanSymbols = DefaultPulseSpanSymbols;

        private double[] _pulse;
        private double _pulseRollOff = double.NaN;

        private long _samplesEmitted;

        /// <summary>The modulation to draw symbols from.</summary>
        /// <exception cref="ArgumentNullException">The value is null.</exception>
        public ModulationScheme Scheme
        {
            get { return _scheme; }
            set { _scheme = value ?? throw new ArgumentNullException(nameof(value)); }
        }

        /// <summary>The symbol rate, in symbols per second.</summary>
        public double SymbolRateHz { get; set; } = 1e6;

        /// <summary>The rate the samples are produced at, in hertz.</summary>
        public double SampleRateHz { get; set; } = 12.8e6;

        /// <summary>The pulse shape's roll-off, from 0 to 1.</summary>
        public double RollOff { get; set; } = 0.35;

        /// <summary>
        /// How many symbol periods either side of centre the transmit pulse spans.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">The value is less than one.</exception>
        /// <remarks>
        /// <para>
        /// <strong>This sets a floor on how good a signal this source can produce, and the floor is
        /// measured.</strong> A root raised cosine cut off after a few symbols is no longer the
        /// filter whose cascade with its matched pair is a Nyquist pulse, so the residue is
        /// intersymbol interference — a real impairment in the signal, not a defect in whatever
        /// measures it. Demodulated at sixteen samples a symbol with the receive filter spanning the
        /// same distance, on 24 August 2026:
        /// </para>
        /// <code>
        /// span  6  ->  0.287 %rms      span 12  ->  0.139 %rms
        /// span  8  ->  0.212 %rms      span 16  ->  0.098 %rms
        /// span 10  ->  0.273 %rms      span 20  ->  0.020 %rms
        /// </code>
        /// <para>
        /// The default of six stays what it was, because it is what every existing measurement of
        /// this source was taken against and because the cost is real — the taps, and the work per
        /// sample, grow with it. A caller that needs a signal clean enough to measure a tenth of a
        /// per cent against has to ask for it, and <c>REQ-DEM-010</c>'s catalogue tests are the
        /// callers that do.
        /// </para>
        /// <para>
        /// The trend is not monotone — ten is worse than eight, and twenty-four worse than twenty —
        /// which is the tail of the pulse changing sign as it is cut. That is worth knowing before
        /// anyone reads a single comparison of two spans as a trend.
        /// </para>
        /// </remarks>
        public int PulseSpanSymbols
        {
            get
            {
                return _pulseSpanSymbols;
            }

            set
            {
                if (value < 1)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(value), value, "A pulse spans at least one symbol either side.");
                }

                if (value != _pulseSpanSymbols)
                {
                    _pulseSpanSymbols = value;

                    // The table is built for a span; changing one invalidates the other.
                    _pulse = null;
                    _pulseRollOff = double.NaN;
                }
            }
        }

        /// <summary>The carrier's offset from the centre of the analysis, in hertz.</summary>
        public double CarrierOffsetHz { get; set; }

        /// <summary>The carrier's phase at the first sample, in radians.</summary>
        public double PhaseRadians { get; set; }

        /// <summary>The signal's amplitude, in volts.</summary>
        public double AmplitudeVolts { get; set; } = 1.0;

        /// <summary>
        /// Noise added to the signal, as a signal-to-noise ratio in decibels.
        /// </summary>
        /// <remarks>
        /// <see cref="double.PositiveInfinity"/> adds none, which is the default.
        /// </remarks>
        public double SignalToNoiseDb { get; set; } = double.PositiveInfinity;

        /// <summary>The seed every stochastic part is derived from (<c>REQ-SIM-003</c>).</summary>
        public long Seed { get; set; }

        /// <summary>How many samples have been produced since the last <see cref="Restart"/>.</summary>
        public long SamplesEmitted => _samplesEmitted;

        /// <summary>Samples per symbol at the current rates; not generally a whole number.</summary>
        public double SamplesPerSymbol => SampleRateHz / SymbolRateHz;

        /// <summary>Starts the signal again from its beginning.</summary>
        public void Restart() => _samplesEmitted = 0;

        /// <summary>
        /// Fills a buffer with the next stretch of the signal.
        /// </summary>
        /// <param name="interleaved">
        /// The buffer, real and imaginary alternating; its length must be even.
        /// </param>
        /// <exception cref="ArgumentException">The buffer's length is odd.</exception>
        /// <exception cref="InvalidOperationException">
        /// The symbol rate is not below the sample rate, so there is no signal to make.
        /// </exception>
        public void Fill(Span<float> interleaved)
        {
            if ((interleaved.Length % 2) != 0)
            {
                throw new ArgumentException(
                    "An interleaved buffer has two values per sample.", nameof(interleaved));
            }

            double perSymbol = SamplesPerSymbol;

            if (!(perSymbol > 2.0))
            {
                throw new InvalidOperationException(
                    "A symbol rate of " + SymbolRateHz + " Hz at a sample rate of " + SampleRateHz +
                    " Hz is " + perSymbol + " samples per symbol. Two is the least that can carry " +
                    "a shaped symbol, and a front end cannot make a signal it could not have " +
                    "acquired.");
            }

            double[] pulse = Pulse();

            int reach = PulseSpanSymbols;
            int samples = interleaved.Length / 2;

            double turnPerSample = 2.0 * Math.PI * CarrierOffsetHz / SampleRateHz;
            double sigma = NoiseSigma();

            for (int sample = 0; sample < samples; sample++)
            {
                long index = _samplesEmitted + sample;
                double position = index / perSymbol;
                long centre = (long)Math.Floor(position);

                double i = 0.0;
                double q = 0.0;

                for (long symbol = centre - reach; symbol <= centre + reach; symbol++)
                {
                    if (symbol < 0)
                    {
                        continue;
                    }

                    double weight = Weight(pulse, position - symbol);

                    if (weight == 0.0)
                    {
                        continue;
                    }

                    SymbolPoint point = _scheme.IdealPoints[SymbolAt(symbol)];

                    i += point.I * weight;
                    q += point.Q * weight;
                }

                double angle = (turnPerSample * index) + PhaseRadians;
                double cos = Math.Cos(angle);
                double sin = Math.Sin(angle);

                double outI = ((i * cos) - (q * sin)) * AmplitudeVolts;
                double outQ = ((i * sin) + (q * cos)) * AmplitudeVolts;

                if (sigma > 0.0)
                {
                    double first;
                    double second;

                    Gaussian(index, out first, out second);

                    outI += first * sigma;
                    outQ += second * sigma;
                }

                interleaved[2 * sample] = (float)outI;
                interleaved[(2 * sample) + 1] = (float)outQ;
            }

            _samplesEmitted += samples;
        }

        /// <summary>Which symbol value sits at a symbol index.</summary>
        /// <param name="symbol">The symbol's index from the start of the signal.</param>
        /// <returns>A symbol value within the scheme.</returns>
        /// <remarks>
        /// A hash of the seed and the index rather than a draw from a stream, so that any block can
        /// be produced without producing the ones before it and every block agrees with its
        /// neighbours about the symbols they share.
        /// </remarks>
        public int SymbolAt(long symbol)
        {
            ulong mixed = Mix((ulong)Seed * 0x9E3779B97F4A7C15UL ^ (ulong)symbol);

            return (int)(mixed % (ulong)_scheme.IdealPoints.Count);
        }

        private static ulong Mix(ulong value)
        {
            // splitmix64's finaliser: cheap, well distributed, and deterministic across runs and
            // machines, which REQ-SIM-003 asks of everything stochastic here.
            value += 0x9E3779B97F4A7C15UL;
            value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL;
            value = (value ^ (value >> 27)) * 0x94D049BB133111EBUL;

            return value ^ (value >> 31);
        }

        private static void Gaussian(long index, out double first, out double second)
        {
            ulong a = Mix((ulong)index * 0xD6E8FEB86659FD93UL);
            ulong b = Mix(a ^ 0xA24BAED4963EE407UL);

            double u1 = 1.0 - ((a >> 11) * (1.0 / 9007199254740992.0));
            double u2 = (b >> 11) * (1.0 / 9007199254740992.0);

            double magnitude = Math.Sqrt(-2.0 * Math.Log(u1));

            first = magnitude * Math.Cos(2.0 * Math.PI * u2);
            second = magnitude * Math.Sin(2.0 * Math.PI * u2);
        }

        private double NoiseSigma()
        {
            if (double.IsPositiveInfinity(SignalToNoiseDb))
            {
                return 0.0;
            }

            // The scheme's points are unit mean power, so the signal's power is the amplitude
            // squared; the noise is split between the two components.
            double signal = AmplitudeVolts * AmplitudeVolts;
            double linear = Math.Pow(10.0, SignalToNoiseDb / 10.0);

            return Math.Sqrt(signal / linear / 2.0);
        }

        private double Weight(double[] pulse, double symbols)
        {
            double steps = (symbols + PulseSpanSymbols) * TableStepsPerSymbol;

            if (steps <= 0.0 || steps >= pulse.Length - 1)
            {
                return 0.0;
            }

            int at = (int)steps;
            double fraction = steps - at;

            return pulse[at] + ((pulse[at + 1] - pulse[at]) * fraction);
        }

        private double[] Pulse()
        {
            if (_pulse != null && _pulseRollOff == RollOff)
            {
                return _pulse;
            }

            int steps = (2 * PulseSpanSymbols * TableStepsPerSymbol) + 1;
            var pulse = new double[steps];

            for (int step = 0; step < steps; step++)
            {
                double t = (step / (double)TableStepsPerSymbol) - PulseSpanSymbols;

                pulse[step] = RootRaisedCosineAt(t, RollOff);
            }

            _pulse = pulse;
            _pulseRollOff = RollOff;

            return pulse;
        }

        /// <summary>The root-raised-cosine pulse, from its definition.</summary>
        /// <param name="t">How far from the centre, in symbol periods.</param>
        /// <param name="rollOff">The roll-off.</param>
        /// <returns>The pulse's value.</returns>
        /// <remarks>
        /// Unity at the centre rather than unit energy: what matters for a transmitter is that the
        /// symbols it sends are the constellation's, and the analyser estimates the amplitude for
        /// itself.
        /// </remarks>
        private static double RootRaisedCosineAt(double t, double rollOff)
        {
            const double Tiny = 1e-9;

            if (Math.Abs(t) < Tiny)
            {
                return 1.0 + (rollOff * ((4.0 / Math.PI) - 1.0));
            }

            if (rollOff > Tiny && Math.Abs(Math.Abs(t) - (1.0 / (4.0 * rollOff))) < 1e-7)
            {
                // The removable singularity at t = ±T/4α.
                double angle = Math.PI / (4.0 * rollOff);

                return (rollOff / Math.Sqrt(2.0)) *
                    (((1.0 + (2.0 / Math.PI)) * Math.Sin(angle)) +
                     ((1.0 - (2.0 / Math.PI)) * Math.Cos(angle)));
            }

            double numerator =
                Math.Sin(Math.PI * t * (1.0 - rollOff)) +
                (4.0 * rollOff * t * Math.Cos(Math.PI * t * (1.0 + rollOff)));

            return numerator / (Math.PI * t * (1.0 - ((4.0 * rollOff * t) * (4.0 * rollOff * t))));
        }
    }
}
