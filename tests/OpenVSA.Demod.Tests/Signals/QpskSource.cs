using System;
using OpenVSA.Demod.Results;
using OpenVSA.Demod.Signal;

namespace OpenVSA.Demod.Tests.Signals
{
    /// <summary>
    /// A QPSK signal with known symbols and known impairments, for the chain to recover.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Written here rather than in the product because it is a transmitter, not an analyser.
    /// <c>REQ-SIM-001</c>'s synthetic source is the product's own, and when it arrives these tests
    /// have a choice to make; until then the thing the chain is tested against has to come from
    /// somewhere, and somewhere that is not the code under test.
    /// </para>
    /// <para>
    /// <strong>Deliberately not sharing the demodulator's arithmetic.</strong> The pulse shaping is
    /// written out here rather than called from <c>PulseShaping</c>: a transmitter and a receiver
    /// that agree because they are the same function agree about their own mistakes too.
    /// </para>
    /// </remarks>
    internal sealed class QpskSource
    {
        private readonly Random _random;

        internal QpskSource(int seed)
        {
            _random = new Random(seed);
        }

        /// <summary>
        /// The constellation to transmit; QPSK unless something else is asked for.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The points were written out here by hand until <c>REQ-DEM-010</c>'s catalogue existed, and
        /// the four they produced are exactly <c>Constellation.Qpsk</c>'s four, so every test written
        /// against this generator keeps the numbers it had.
        /// </para>
        /// <para>
        /// <strong>Both ends of a round trip then share one point list</strong>, which is what makes
        /// such a test prove that the chain recovers what was sent and prove nothing about whether
        /// the geometry is anybody's standard. Only a transmitter settles that — see
        /// <c>evidence/req-e44-007/</c>.
        /// </para>
        /// </remarks>
        internal Constellation Constellation { get; set; } = Constellation.Qpsk();

        /// <summary>Symbols per second.</summary>
        internal double SymbolRateHz { get; set; } = 1e6;

        /// <summary>Samples per second. Deliberately not a whole multiple of the symbol rate.</summary>
        internal double SampleRateHz { get; set; } = 5.3e6;

        /// <summary>The transmit filter's roll-off.</summary>
        internal double Alpha { get; set; } = 0.35;

        /// <summary>How many symbols either side of centre the transmit pulse spans.</summary>
        internal int SymbolSpan { get; set; } = 8;

        /// <summary>The carrier offset put on the signal, in hertz.</summary>
        internal double CarrierOffsetHz { get; set; }

        /// <summary>The carrier phase put on the signal, in radians.</summary>
        internal double PhaseRadians { get; set; }

        /// <summary>The amplitude the signal is scaled by.</summary>
        internal double Amplitude { get; set; } = 1.0;

        /// <summary>A fraction of a symbol the transmitter's clock is offset by.</summary>
        internal double TimingOffsetSymbols { get; set; }

        /// <summary>RMS noise added to each component, relative to the signal's RMS.</summary>
        internal double NoiseFraction { get; set; }

        /// <summary>
        /// A linear channel applied to the shaped waveform, as taps spaced one symbol apart, or
        /// <c>null</c> for none.
        /// </summary>
        /// <remarks>
        /// <strong>Symbol-spaced, and that is the point.</strong> Taps one sample apart at five
        /// samples per symbol are a gentle filter: neighbouring samples of a pulse-shaped signal
        /// are nearly the same sample, so such a channel barely disturbs the symbol instants and an
        /// equaliser has almost nothing to remove. An echo a whole symbol away is inter-symbol
        /// interference, which is the impairment an equaliser exists for.
        /// </remarks>
        internal double[] ChannelTaps { get; set; }

        /// <summary>
        /// Which symbol to displace from its ideal point, or a negative index for none.
        /// </summary>
        /// <remarks>
        /// One symbol wrong and the rest right, which is what an error-vector trace has to be able
        /// to point at (<c>REQ-DEM-080</c>). Displacing the symbol rather than adding noise to a
        /// sample keeps the impairment exactly where the test says it is.
        /// </remarks>
        internal int DisplacedSymbol { get; set; } = -1;

        /// <summary>How far the displaced symbol is moved, in constellation units.</summary>
        internal double Displacement { get; set; }

        /// <summary>An added tone's amplitude, relative to the signal's; zero for none.</summary>
        /// <remarks>
        /// An additive periodic impairment, which is the kind that shows as a LINE in an error
        /// vector spectrum. A phase wobble is periodic too and does not: the error it makes is the
        /// wobble multiplied by the symbol that was sent, and the symbols are random, so it spreads
        /// across the spectrum instead of standing up in one bin. Measured, while writing
        /// REQ-DEM-080's test for that trace.
        /// </remarks>
        internal double SpurFraction { get; set; }

        /// <summary>The added tone's offset from the carrier, in hertz.</summary>
        internal double SpurOffsetHz { get; set; }

        /// <summary>A sinusoidal phase wobble's depth, in radians; zero for none.</summary>
        /// <remarks>
        /// A periodic impairment, which <c>REQ-SIM-002</c>'s set does not have one of and
        /// <c>REQ-DEM-080</c>'s error-vector-spectrum criterion needs: the error sequence then
        /// carries a tone at a known rate, and the spectrum of that sequence has to show it.
        /// </remarks>
        internal double PhaseWobbleRadians { get; set; }

        /// <summary>The phase wobble's rate, in cycles per symbol.</summary>
        internal double PhaseWobbleCyclesPerSymbol { get; set; } = 0.05;

        /// <summary>The symbols the last call generated.</summary>
        internal int[] Symbols { get; private set; }

        /// <summary>Which sample of the last record symbol zero's decision instant falls near.</summary>
        internal int FirstSymbolSample { get; private set; }

        /// <summary>Generates a record.</summary>
        /// <param name="symbolCount">How many symbols to send.</param>
        /// <returns>The record, interleaved real and imaginary.</returns>
        internal float[] Generate(int symbolCount)
        {
            var symbols = new int[symbolCount];

            for (int symbol = 0; symbol < symbolCount; symbol++)
            {
                symbols[symbol] = _random.Next(Constellation.Count);
            }

            return Generate(symbols);
        }

        /// <summary>Generates a record from given symbols.</summary>
        /// <param name="symbols">The symbol values, indices into the constellation.</param>
        /// <returns>The record, interleaved real and imaginary.</returns>
        internal float[] Generate(int[] symbols)
        {
            Symbols = symbols;

            double samplesPerSymbol = SampleRateHz / SymbolRateHz;
            double reach = SymbolSpan * samplesPerSymbol;
            int guard = SymbolSpan + 2;
            int lead = (int)Math.Ceiling(reach) + (int)Math.Ceiling(guard * samplesPerSymbol) + 8;

            int total = lead +
                (int)Math.Ceiling((symbols.Length + guard) * samplesPerSymbol) +
                (int)Math.Ceiling(reach) + 8;

            var shapedI = new double[total];
            var shapedQ = new double[total];

            // The sequence is continued cyclically either side of the symbols being sent, so the
            // record is a continuous transmission rather than one that fades up out of nothing. A
            // dead lead-in is not a neutral choice for a test signal: the demodulator would spend
            // its first few symbols on samples with no signal in them, and the resulting EVM would
            // be an artefact of how the test was generated rather than of anything the chain did.
            for (int index = -guard; index < symbols.Length + guard; index++)
            {
                int symbol = symbols[((index % symbols.Length) + symbols.Length) % symbols.Length];

                double centre = lead + ((index + TimingOffsetSymbols) * samplesPerSymbol);

                ConstellationPoint point = Constellation.Points[symbol];
                double i = point.I;
                double q = point.Q;

                if (index == DisplacedSymbol)
                {
                    i += Displacement;
                    q -= Displacement;
                }

                int first = Math.Max(0, (int)Math.Ceiling(centre - reach));
                int last = Math.Min(total - 1, (int)Math.Floor(centre + reach));

                for (int sample = first; sample <= last; sample++)
                {
                    double weight = RootRaisedCosine((sample - centre) / samplesPerSymbol, Alpha);

                    shapedI[sample] += i * weight;
                    shapedQ[sample] += q * weight;
                }
            }

            Normalise(shapedI, shapedQ, samplesPerSymbol);

            if (ChannelTaps != null)
            {
                ApplyChannel(shapedI, shapedQ, SymbolSpaced(ChannelTaps, samplesPerSymbol));
            }

            // Trimmed to the part of the record every pulse overlapping it was generated for. What
            // is cut is the ramp at each end, where the sum of pulses is incomplete because the
            // sequence had to start somewhere — a stretch of waveform that is not a transmission
            // and that no acquisition of a running signal would contain. Left in, it costs a
            // demodulator its first few symbols and reads as EVM.
            int from = lead - (int)Math.Ceiling(2 * samplesPerSymbol);
            int to = Math.Min(
                total, lead + (int)Math.Ceiling((symbols.Length + 2) * samplesPerSymbol));

            FirstSymbolSample = lead - from;

            return Modulate(shapedI, shapedQ, from, to);
        }

        /// <summary>
        /// The root-raised-cosine pulse, from its definition.
        /// </summary>
        /// <param name="t">How far from the centre, in symbol periods.</param>
        /// <param name="alpha">The roll-off.</param>
        private static double RootRaisedCosine(double t, double alpha)
        {
            const double Tiny = 1e-9;

            if (Math.Abs(t) < Tiny)
            {
                return 1.0 + (alpha * ((4.0 / Math.PI) - 1.0));
            }

            if (alpha > Tiny && Math.Abs(Math.Abs(t) - (1.0 / (4.0 * alpha))) < 1e-7)
            {
                double angle = Math.PI / (4.0 * alpha);

                return (alpha / Math.Sqrt(2.0)) *
                    (((1.0 + (2.0 / Math.PI)) * Math.Sin(angle)) +
                     ((1.0 - (2.0 / Math.PI)) * Math.Cos(angle)));
            }

            double numerator =
                Math.Sin(Math.PI * t * (1.0 - alpha)) +
                (4.0 * alpha * t * Math.Cos(Math.PI * t * (1.0 + alpha)));

            return numerator / (Math.PI * t * (1.0 - ((4.0 * alpha * t) * (4.0 * alpha * t))));
        }

        private static void Normalise(double[] shapedI, double[] shapedQ, double samplesPerSymbol)
        {
            // The pulse above is written at unit peak rather than unit energy, so the waveform's
            // scale depends on how many samples a symbol spans. Normalising here keeps the signal's
            // amplitude a property of the test rather than of the sample rate it chose.
            double energy = 0.0;

            for (int sample = 0; sample < shapedI.Length; sample++)
            {
                energy += (shapedI[sample] * shapedI[sample]) + (shapedQ[sample] * shapedQ[sample]);
            }

            double scale = Math.Sqrt(samplesPerSymbol / (energy < 1e-18 ? 1.0 : energy));

            for (int sample = 0; sample < shapedI.Length; sample++)
            {
                shapedI[sample] *= scale;
                shapedQ[sample] *= scale;
            }
        }

        /// <summary>Spreads symbol-spaced taps onto the sample grid.</summary>
        /// <param name="taps">The taps, one symbol apart.</param>
        /// <param name="samplesPerSymbol">How many samples a symbol spans.</param>
        private static double[] SymbolSpaced(double[] taps, double samplesPerSymbol)
        {
            int spacing = (int)Math.Round(samplesPerSymbol);
            var spread = new double[((taps.Length - 1) * spacing) + 1];

            for (int tap = 0; tap < taps.Length; tap++)
            {
                spread[tap * spacing] = taps[tap];
            }

            return spread;
        }

        private static void ApplyChannel(double[] shapedI, double[] shapedQ, double[] taps)
        {
            var outputI = new double[shapedI.Length];
            var outputQ = new double[shapedQ.Length];
            int centre = taps.Length / 2;

            for (int sample = 0; sample < shapedI.Length; sample++)
            {
                double i = 0.0;
                double q = 0.0;

                for (int tap = 0; tap < taps.Length; tap++)
                {
                    int source = sample + centre - tap;

                    if (source < 0 || source >= shapedI.Length)
                    {
                        continue;
                    }

                    i += taps[tap] * shapedI[source];
                    q += taps[tap] * shapedQ[source];
                }

                outputI[sample] = i;
                outputQ[sample] = q;
            }

            Array.Copy(outputI, shapedI, shapedI.Length);
            Array.Copy(outputQ, shapedQ, shapedQ.Length);
        }

        private float[] Modulate(double[] shapedI, double[] shapedQ, int from, int to)
        {
            var record = new float[2 * (to - from)];
            double turnPerSample = 2.0 * Math.PI * CarrierOffsetHz / SampleRateHz;

            double wobblePerSample = PhaseWobbleRadians == 0.0
                ? 0.0
                : 2.0 * Math.PI * PhaseWobbleCyclesPerSymbol * SymbolRateHz / SampleRateHz;

            for (int index = 0; index < to - from; index++)
            {
                int sample = from + index;
                double angle = (turnPerSample * index) + PhaseRadians;

                if (wobblePerSample != 0.0)
                {
                    angle += PhaseWobbleRadians * Math.Sin(wobblePerSample * index);
                }
                double cos = Math.Cos(angle);
                double sin = Math.Sin(angle);

                double i = ((shapedI[sample] * cos) - (shapedQ[sample] * sin)) * Amplitude;
                double q = ((shapedI[sample] * sin) + (shapedQ[sample] * cos)) * Amplitude;

                if (SpurFraction > 0.0)
                {
                    double spur = 2.0 * Math.PI * SpurOffsetHz * index / SampleRateHz;

                    i += SpurFraction * Amplitude * Math.Cos(spur);
                    q += SpurFraction * Amplitude * Math.Sin(spur);
                }

                if (NoiseFraction > 0.0)
                {
                    i += Gaussian() * NoiseFraction * Amplitude;
                    q += Gaussian() * NoiseFraction * Amplitude;
                }

                record[2 * index] = (float)i;
                record[(2 * index) + 1] = (float)q;
            }

            return record;
        }

        private double Gaussian()
        {
            double first = 1.0 - _random.NextDouble();
            double second = _random.NextDouble();

            return Math.Sqrt(-2.0 * Math.Log(first)) * Math.Cos(2.0 * Math.PI * second);
        }
    }
}
