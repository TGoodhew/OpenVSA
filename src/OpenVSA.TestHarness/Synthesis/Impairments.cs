using System;
using System.Collections.Generic;

namespace OpenVSA.TestHarness.Synthesis
{
    /// <summary>
    /// The impairments <c>REQ-SIM-002</c> requires, injected independently and quantitatively.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every one of these is stated in the units the requirement states it in, so a test asks for
    /// what the specification asks for and no conversion sits between the two.
    /// </para>
    /// <para>
    /// <strong>Independence is the property that matters and the one that is easy to lose.</strong>
    /// A generator that applied gain imbalance after quadrature skew, or droop before the carrier
    /// offset, would produce a signal in which each measured impairment depends on the others — and
    /// every individual measurement would still look right. The application order below is fixed
    /// and each step is written so that it changes only its own observable.
    /// </para>
    /// </remarks>
    /// <summary>One path of a tapped-delay-line channel (<c>REQ-SIM-002</c>).</summary>
    /// <remarks>
    /// The delay is in samples rather than seconds so a tap lands exactly where it is asked to. A
    /// fractional delay would need an interpolating filter, and the filter's own response would be
    /// mixed into every tap estimate made afterwards — the channel under test would include the
    /// apparatus used to build it.
    /// </remarks>
    public sealed class MultipathTap
    {
        /// <summary>Creates a tap.</summary>
        /// <param name="delaySamples">Delay, in samples; zero for the direct path.</param>
        /// <param name="gainDb">Gain, in dB, relative to the direct path at unity.</param>
        /// <param name="phaseDegrees">Phase rotation, in degrees.</param>
        public MultipathTap(int delaySamples, double gainDb, double phaseDegrees = 0.0)
        {
            if (delaySamples < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(delaySamples), "A tap cannot arrive before the signal that produced it.");
            }

            DelaySamples = delaySamples;
            GainDb = gainDb;
            PhaseDegrees = phaseDegrees;
        }

        /// <summary>Delay, in samples.</summary>
        public int DelaySamples { get; }

        /// <summary>Gain, in dB.</summary>
        public double GainDb { get; }

        /// <summary>Phase, in degrees.</summary>
        public double PhaseDegrees { get; }

        /// <summary>The tap as a complex weight.</summary>
        /// <param name="re">Real part.</param>
        /// <param name="im">Imaginary part.</param>
        public void ToComplex(out double re, out double im)
        {
            double magnitude = Math.Pow(10.0, GainDb / 20.0);
            double radians = PhaseDegrees * Math.PI / 180.0;

            re = magnitude * Math.Cos(radians);
            im = magnitude * Math.Sin(radians);
        }

        /// <inheritdoc />
        public override string ToString()
        {
            return DelaySamples + " samples, " + GainDb.ToString("F1") + " dB, " +
                PhaseDegrees.ToString("F1") + Char.ConvertFromUtf32(0x00B0);
        }
    }

    public sealed class Impairments
    {
        /// <summary>Signal-to-noise ratio in dB; infinity for none.</summary>
        public double SignalToNoiseDb { get; set; } = double.PositiveInfinity;

        /// <summary>Carrier frequency offset, in hertz.</summary>
        public double CarrierOffsetHz { get; set; }

        /// <summary>Carrier phase offset, in degrees.</summary>
        public double CarrierPhaseDegrees { get; set; }

        /// <summary>I-versus-Q gain imbalance, in dB.</summary>
        public double GainImbalanceDb { get; set; }

        /// <summary>Quadrature skew, in degrees away from 90°.</summary>
        public double QuadratureSkewDegrees { get; set; }

        /// <summary>I/Q origin offset, in dB relative to the signal amplitude.</summary>
        /// <remarks>Negative. Negative infinity means none.</remarks>
        public double OriginOffsetDb { get; set; } = double.NegativeInfinity;

        /// <summary>Amplitude droop, in dB per symbol.</summary>
        public double DroopDbPerSymbol { get; set; }

        /// <summary>Timing offset, as a fraction of a symbol.</summary>
        public double TimingOffsetSymbols { get; set; }

        /// <summary>Symbol-clock error, in parts per million.</summary>
        public double ClockErrorPpm { get; set; }

        /// <summary>Phase noise, as an RMS phase deviation in degrees.</summary>
        /// <remarks>
        /// A single RMS figure rather than a mask. The requirement says "mask-specified", which
        /// needs a shaped noise process and a mask to specify it with; an RMS deviation is the one
        /// figure that can be injected and measured back without one, and it is what the residual
        /// phase scatter of a signal actually shows.
        /// </remarks>
        public double PhaseNoiseDegreesRms { get; set; }

        /// <summary>AM/AM compression, in dB of gain lost at full scale.</summary>
        /// <remarks>
        /// A soft-limiting characteristic: the gain falls with envelope amplitude, reaching this
        /// much compression at unit envelope. Zero for none.
        /// </remarks>
        public double CompressionDb { get; set; }

        /// <summary>AM/PM conversion, in degrees of phase shift at full scale.</summary>
        public double AmToPmDegrees { get; set; }

        /// <summary>Seed for the noise, so a scenario is reproducible.</summary>
        /// <summary>
        /// The tapped-delay-line channel, empty for a clean path.
        /// </summary>
        /// <remarks>
        /// A list rather than a scalar because a channel has no single magnitude to request: two
        /// taps at the same delay with opposite phases are a notch, and the same two a symbol apart
        /// are frequency-selective fading. The other eleven impairments each have one number; this
        /// one is a response.
        /// </remarks>
        public IList<MultipathTap> Multipath { get; } = new List<MultipathTap>();

        /// <summary>Seed for every random stream.</summary>
        public int Seed { get; set; } = 20260728;
    }

    /// <summary>
    /// A signal with known symbol instants, impaired by a known amount.
    /// </summary>
    /// <remarks>
    /// Deliberately a plain QPSK carrier rather than anything a personality would recognise. The
    /// point is that every impairment is recoverable from the samples without demodulating, and a
    /// simpler signal makes each recovery a closed-form calculation rather than an estimation.
    /// </remarks>
    public sealed class ImpairedSignal
    {
        private readonly double[] _i;
        private readonly double[] _q;

        private readonly double[] _preChannelI;
        private readonly double[] _preChannelQ;

        private ImpairedSignal(double[] i, double[] q, int[] instants, Impairments requested,
            double symbolRateHz, double sampleRateHz, double[] preChannelI, double[] preChannelQ)
        {
            _i = i;
            _q = q;
            _preChannelI = preChannelI;
            _preChannelQ = preChannelQ;
            SymbolInstants = instants;
            Requested = requested;
            SymbolRateHz = symbolRateHz;
            SampleRateHz = sampleRateHz;
        }

        /// <summary>Sample count.</summary>
        public int Length => _i.Length;

        /// <summary>The in-phase samples.</summary>
        public double[] I => _i;

        /// <summary>The quadrature samples.</summary>
        public double[] Q => _q;

        /// <summary>The in-phase samples as they entered the channel.</summary>
        /// <remarks>
        /// The reference a tap estimate is made against. Keeping it is the same licence
        /// <see cref="ImpairmentMeasurement.SignalToNoiseDb"/> takes in knowing where the
        /// constellation points are: this measures the generator, so the generator's own reference
        /// is admissible. A channel estimator in the product would have to earn its reference from
        /// a training sequence, and would be a different piece of code with a different test.
        /// </remarks>
        public double[] PreChannelI => _preChannelI;

        /// <summary>The quadrature samples as they entered the channel.</summary>
        public double[] PreChannelQ => _preChannelQ;

        /// <summary>
        /// The sample index of each symbol's decision instant, as the generator placed them.
        /// </summary>
        /// <remarks>
        /// The <em>ideal</em> instants, before timing offset and clock error move the signal under
        /// them. That is what makes those two impairments measurable: the difference between where
        /// the symbols are and where they were meant to be is the impairment.
        /// </remarks>
        public int[] SymbolInstants { get; }

        /// <summary>What was asked for.</summary>
        public Impairments Requested { get; }

        /// <summary>Symbol rate, in hertz.</summary>
        public double SymbolRateHz { get; }

        /// <summary>Sample rate, in hertz.</summary>
        public double SampleRateHz { get; }

        /// <summary>Generates an impaired signal.</summary>
        /// <param name="impairments">What to inject; <c>null</c> for a clean signal.</param>
        /// <param name="symbols">How many symbols.</param>
        /// <param name="samplesPerSymbol">Samples per symbol.</param>
        /// <returns>The signal.</returns>
        public static ImpairedSignal Generate(
            Impairments impairments, int symbols = 4096, int samplesPerSymbol = 8)
        {
            Impairments wanted = impairments ?? new Impairments();

            const double SampleRateHz = 12.8e6;
            double symbolRate = SampleRateHz / samplesPerSymbol;

            int length = symbols * samplesPerSymbol;

            var i = new double[length];
            var q = new double[length];
            var instants = new int[symbols];

            // The four QPSK points in a fixed cycle, NOT a random sequence.
            //
            // Every moment-based measurement here — imbalance from the second moments, skew from
            // the cross-moment, origin offset from the mean — is exact over one cycle: the four
            // points sum to zero on each axis and their I·Q products sum to zero too. A random
            // sequence leaves a residue of order 1/sqrt(N), which at 4 096 symbols is about 0.9° of
            // apparent skew, and the first version of this failed by roughly that much on every
            // skew case. Randomness buys nothing here and costs a tolerance.
            var symbolI = new double[symbols];
            var symbolQ = new double[symbols];

            for (int s = 0; s < symbols; s++)
            {
                symbolI[s] = (s & 1) == 0 ? 1.0 : -1.0;
                symbolQ[s] = (s & 2) == 0 ? 1.0 : -1.0;
                instants[s] = s * samplesPerSymbol + samplesPerSymbol / 2;
            }

            // Timing offset and clock error move where the symbol actually lands relative to the
            // instant recorded above. Applied first, because everything after it is a per-sample
            // transformation that does not care where the symbol boundaries are.
            double drift = wanted.ClockErrorPpm * 1e-6;

            for (int n = 0; n < length; n++)
            {
                double position = n / (double)samplesPerSymbol;

                // Where in the symbol stream this sample falls, once the clock is wrong and the
                // timing is offset.
                double shifted = position * (1.0 + drift) - wanted.TimingOffsetSymbols;

                // Floor, not round. A sample-and-hold symbol occupies [s, s+1), so the mid-symbol
                // decision instant sits at s + 0.5 — and rounding that lands on s + 1, sampling
                // the NEXT symbol at every instant. That off-by-one made four separate
                // measurements disagree with what had been injected.
                int index = (int)Math.Floor(shifted);
                index = index < 0 ? 0 : (index >= symbols ? symbols - 1 : index);

                i[n] = symbolI[index];
                q[n] = symbolQ[index];
            }

            double[] preChannelI;
            double[] preChannelQ;

            Apply(wanted, i, q, samplesPerSymbol, SampleRateHz, out preChannelI, out preChannelQ);

            return new ImpairedSignal(
                i, q, instants, wanted, symbolRate, SampleRateHz, preChannelI, preChannelQ);
        }

        /// <summary>
        /// Applies each impairment in a fixed order chosen so none disturbs another's measurement.
        /// </summary>
        /// <remarks>
        /// Droop, then imbalance and skew, then origin offset, then the carrier, then noise. Each
        /// step's observable survives the ones after it: a rotation does not change the I and Q
        /// second-moment <em>ratio</em> once the imbalance is already in, and additive noise does
        /// not move a mean. Reordering these is what couples two impairments together.
        /// </remarks>
        private static void Apply(
            Impairments wanted, double[] i, double[] q, int samplesPerSymbol, double sampleRateHz,
            out double[] preChannelI, out double[] preChannelQ)
        {
            double skew = wanted.QuadratureSkewDegrees * Math.PI / 180.0;
            double gainI = Math.Pow(10.0, wanted.GainImbalanceDb / 40.0);
            double gainQ = 1.0 / gainI;

            double origin = double.IsNegativeInfinity(wanted.OriginOffsetDb)
                ? 0.0
                : Math.Pow(10.0, wanted.OriginOffsetDb / 20.0);

            var noise = new DeterministicNormal(wanted.Seed);

            // Its own stream, so turning phase noise on does not change the AWGN sequence. Sharing
            // one generator would make two impairments interfere through their randomness alone,
            // which is exactly the coupling this requirement forbids.
            var phaseNoise = new DeterministicNormal(wanted.Seed ^ 0x2C9F);

            double sigma = double.IsPositiveInfinity(wanted.SignalToNoiseDb)
                ? 0.0
                : Math.Sqrt(Math.Pow(10.0, -wanted.SignalToNoiseDb / 10.0));

            for (int n = 0; n < i.Length; n++)
            {
                double symbols = n / (double)samplesPerSymbol;

                double droop = Math.Pow(10.0, wanted.DroopDbPerSymbol * symbols / 20.0);

                double ii = i[n] * droop;
                double qq = q[n] * droop;

                // Imbalance on the axes, then skew as a shear of Q onto I. A shear rather than a
                // rotation: quadrature skew is the two axes ceasing to be perpendicular, which is
                // not the same as the whole constellation turning.
                ii *= gainI;
                qq *= gainQ;

                double skewedI = ii + qq * Math.Sin(skew);
                double skewedQ = qq * Math.Cos(skew);

                // Split across the axes so the offset VECTOR has the requested magnitude relative
                // to the signal, rather than each axis having it and the vector being sqrt(2)
                // larger than asked for.
                skewedI += origin * 0.70710678118654752;
                skewedQ += origin * 0.70710678118654752;

                // AM/AM and AM/PM: both are functions of the envelope, so they are applied
                // together and before the carrier. Applying them after would make the compression
                // depend on where the carrier happened to have rotated to, which is not what a
                // compressing amplifier does.
                if (wanted.CompressionDb != 0.0 || wanted.AmToPmDegrees != 0.0)
                {
                    double envelope = Math.Sqrt(skewedI * skewedI + skewedQ * skewedQ) / Math.Sqrt(2.0);

                    double gain = Math.Pow(10.0, -wanted.CompressionDb * envelope * envelope / 20.0);
                    double twist = wanted.AmToPmDegrees * envelope * envelope * Math.PI / 180.0;

                    double c = Math.Cos(twist);
                    double sn = Math.Sin(twist);

                    double ci = skewedI * c - skewedQ * sn;
                    double cq = skewedI * sn + skewedQ * c;

                    skewedI = ci * gain;
                    skewedQ = cq * gain;
                }

                double phase = 2.0 * Math.PI * wanted.CarrierOffsetHz * n / sampleRateHz
                             + wanted.CarrierPhaseDegrees * Math.PI / 180.0;

                // Phase noise rides on the carrier phase, which is what phase noise is. Additive
                // noise would be AWGN with extra steps and would show up in the SNR instead.
                if (wanted.PhaseNoiseDegreesRms != 0.0)
                {
                    phase += phaseNoise.Next() * wanted.PhaseNoiseDegreesRms * Math.PI / 180.0;
                }

                double cos = Math.Cos(phase);
                double sin = Math.Sin(phase);

                i[n] = skewedI * cos - skewedQ * sin;
                q[n] = skewedI * sin + skewedQ * cos;
            }

            // The channel comes after the transmitter and before the receiver's noise, because that
            // is where it is. Putting AWGN before it would make the requested SNR a property of the
            // transmitter, and a channel that attenuated the signal would then appear to improve it.
            preChannelI = (double[])i.Clone();
            preChannelQ = (double[])q.Clone();

            Convolve(wanted.Multipath, i, q);

            // Noise last, and in a separate pass. It draws I then Q per sample exactly as the loop
            // above did, so every existing measurement sees the same sequence it always did.
            if (sigma > 0.0)
            {
                for (int n = 0; n < i.Length; n++)
                {
                    i[n] += sigma * noise.Next();
                    q[n] += sigma * noise.Next();
                }
            }
        }

        /// <summary>Convolves the signal with the tapped-delay-line channel, in place.</summary>
        /// <remarks>
        /// Samples before the record began are taken as zero rather than wrapped. A wrap would make
        /// the first symbols carry energy from the last, which is a circular channel and not a
        /// physical one; the affected span is the longest delay, and every measurement here reads
        /// thousands of symbols.
        /// </remarks>
        private static void Convolve(IList<MultipathTap> taps, double[] i, double[] q)
        {
            if (taps == null || taps.Count == 0)
            {
                return;
            }

            var outputI = new double[i.Length];
            var outputQ = new double[q.Length];

            foreach (MultipathTap tap in taps)
            {
                tap.ToComplex(out double re, out double im);

                for (int n = tap.DelaySamples; n < i.Length; n++)
                {
                    double sourceI = i[n - tap.DelaySamples];
                    double sourceQ = q[n - tap.DelaySamples];

                    outputI[n] += sourceI * re - sourceQ * im;
                    outputQ[n] += sourceI * im + sourceQ * re;
                }
            }

            Array.Copy(outputI, i, i.Length);
            Array.Copy(outputQ, q, q.Length);
        }

        /// <summary>A uniform stream from a seed, stable across framework versions.</summary>
        private sealed class DeterministicUniform
        {
            private ulong _state;

            public DeterministicUniform(int seed)
            {
                _state = (ulong)seed * 6364136223846793005UL + 1442695040888963407UL;
            }

            public double Next()
            {
                _state = _state * 6364136223846793005UL + 1442695040888963407UL;
                return ((_state >> 11) & 0x1FFFFFFFFFFFFFUL) / (double)(1UL << 53);
            }
        }

        /// <summary>Gaussian samples from a seed, stable across framework versions.</summary>
        private sealed class DeterministicNormal
        {
            private readonly DeterministicUniform _uniform;
            private double _spare;
            private bool _hasSpare;

            public DeterministicNormal(int seed)
            {
                _uniform = new DeterministicUniform(seed);
            }

            public double Next()
            {
                if (_hasSpare)
                {
                    _hasSpare = false;
                    return _spare;
                }

                double u1, u2, s;

                do
                {
                    u1 = 2.0 * _uniform.Next() - 1.0;
                    u2 = 2.0 * _uniform.Next() - 1.0;
                    s = u1 * u1 + u2 * u2;
                }
                while (s >= 1.0 || s == 0.0);

                double factor = Math.Sqrt(-2.0 * Math.Log(s) / s);

                _spare = u2 * factor;
                _hasSpare = true;

                return u1 * factor;
            }
        }
    }
}
