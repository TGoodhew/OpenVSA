using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace OpenVSA.TestHarness.Synthesis
{
    /// <summary>How a burst's amplitude gets from off to on.</summary>
    public enum RampShape
    {
        /// <summary>Instant. The transition occupies no samples at all.</summary>
        Rectangular = 0,

        /// <summary>Straight line in amplitude across the transition.</summary>
        Linear,

        /// <summary>Raised cosine across the transition — the shape a real burst has.</summary>
        RaisedCosine,
    }

    /// <summary>
    /// A bursted signal with settable on/off times, ramp shape and inter-burst noise floor
    /// (<c>REQ-SIM-004</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// For exercising pulse search. The requirement's criterion is that a generated burst,
    /// <strong>measured back from its own samples</strong>, reproduces what was asked for — so this
    /// carries no forward dependency on a demodulator, and the comparison is against the requested
    /// parameters rather than against a previous run.
    /// </para>
    /// <para>
    /// <strong>The noise floor is the reason this is not trivial.</strong> A burst against silence
    /// is easy to find and tells a pulse search nothing; a real one sits on a floor, and where the
    /// edge is depends on where you decide the floor ends. Generating a known floor is what makes
    /// an on-time measurement mean something.
    /// </para>
    /// </remarks>
    public sealed class PulsedSource
    {
        /// <summary>Sample rate, in hertz.</summary>
        public double SampleRateHz { get; set; } = 12.8e6;

        /// <summary>Burst length, in samples.</summary>
        public int OnSamples { get; set; } = 4096;

        /// <summary>Gap between bursts, in samples.</summary>
        public int OffSamples { get; set; } = 4096;

        /// <summary>How many bursts to generate.</summary>
        public int BurstCount { get; set; } = 4;

        /// <summary>Transition length, in samples, at each edge.</summary>
        /// <remarks>
        /// Counted inside the on time, not added to it: a burst declared 4 096 samples long with a
        /// 64-sample ramp is 4 096 samples from the start of the rising edge to the end of the
        /// falling one. The alternative — ramps outside the on time — makes the on time depend on
        /// the ramp, and then two bursts with different ramps and the same declared length are
        /// different lengths.
        /// </remarks>
        public int RampSamples { get; set; } = 64;

        /// <summary>The shape of each transition.</summary>
        public RampShape Ramp { get; set; } = RampShape.RaisedCosine;

        /// <summary>On-state amplitude, linear.</summary>
        public double Amplitude { get; set; } = 0.5;

        /// <summary>
        /// Noise floor between bursts, in dB relative to the on-state amplitude.
        /// </summary>
        /// <remarks>Negative. -40 means the floor sits 40 dB below the burst.</remarks>
        public double NoiseFloorDb { get; set; } = -40.0;

        /// <summary>Seed, so a scenario is exactly reproducible (<c>REQ-SIM-003</c>).</summary>
        public int Seed { get; set; } = 20260728;

        /// <summary>Generates the interleaved I/Q record.</summary>
        /// <returns>The record and where its bursts are.</returns>
        /// <exception cref="InvalidOperationException">The parameters cannot produce a burst.</exception>
        public PulsedRecord Generate()
        {
            if (OnSamples <= 0 || OffSamples < 0 || BurstCount <= 0)
            {
                throw new InvalidOperationException("A burst needs a positive on time and count.");
            }

            if (RampSamples * 2 > OnSamples)
            {
                throw new InvalidOperationException(
                    "The two ramps are longer than the burst: " + RampSamples + " × 2 > " + OnSamples +
                    ". Ramps are counted inside the on time.");
            }

            int period = OffSamples + OnSamples;

            // A trailing gap as well as a leading one, so the last burst has a falling edge inside
            // the record. Without it the final transition coincides with the end of the samples and
            // is unmeasurable — the record would contain one more rising edge than falling.
            int total = period * BurstCount + OffSamples;

            var samples = new float[total * 2];
            var starts = new List<int>();

            // Amplitude, not power: the floor is stated in dB relative to the on-state amplitude,
            // and a Gaussian pair of this deviation gives that envelope on average.
            double floor = Amplitude * Math.Pow(10.0, NoiseFloorDb / 20.0);
            var random = new DeterministicNormal(Seed);

            // Each period is OFF then ON, so the record opens with a gap. A record that began
            // mid-burst would give its first burst no rising edge to find, which is both awkward to
            // measure and unlike anything a pulse search meets in practice.
            for (int burst = 0; burst < BurstCount; burst++)
            {
                int periodStart = burst * period;
                starts.Add(periodStart + OffSamples);

                for (int n = 0; n < period; n++)
                {
                    int index = periodStart + n;
                    int intoBurst = n - OffSamples;
                    double envelope = intoBurst >= 0 ? EnvelopeAt(intoBurst) : 0.0;

                    // A carrier at a quarter of the sample rate, so the burst has something in it
                    // that a spectrum can find and the envelope is not the whole signal.
                    double phase = Math.PI * 0.5 * index;

                    samples[index * 2] =
                        (float)(Amplitude * envelope * Math.Cos(phase) + floor * random.Next());
                    samples[index * 2 + 1] =
                        (float)(Amplitude * envelope * Math.Sin(phase) + floor * random.Next());
                }
            }

            return new PulsedRecord(samples, starts, this);
        }

        /// <summary>The envelope at a sample offset within the on time, from 0 to 1.</summary>
        private double EnvelopeAt(int n)
        {
            if (Ramp == RampShape.Rectangular || RampSamples == 0)
            {
                return 1.0;
            }

            int fromEnd = OnSamples - 1 - n;
            int edge = Math.Min(n, fromEnd);

            if (edge >= RampSamples)
            {
                return 1.0;
            }

            // Position through the transition, 0 at the outside edge and 1 where full on begins.
            double t = (edge + 0.5) / RampSamples;

            return Ramp == RampShape.Linear
                ? t
                : 0.5 * (1.0 - Math.Cos(Math.PI * t));
        }

        /// <summary>
        /// Gaussian samples from a seed, with no dependence on the platform's random source.
        /// </summary>
        /// <remarks>
        /// <c>REQ-SIM-003</c> requires bit-identical streams from the same seed. <c>Random</c>'s
        /// sequence is not contractually stable across framework versions, so the generator is
        /// written out: a fixed linear congruential source and Box-Muller.
        /// </remarks>
        private sealed class DeterministicNormal
        {
            private ulong _state;
            private double _spare;
            private bool _hasSpare;

            public DeterministicNormal(int seed)
            {
                _state = (ulong)seed * 6364136223846793005UL + 1442695040888963407UL;
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
                    u1 = 2.0 * Uniform() - 1.0;
                    u2 = 2.0 * Uniform() - 1.0;
                    s = u1 * u1 + u2 * u2;
                }
                while (s >= 1.0 || s == 0.0);

                double factor = Math.Sqrt(-2.0 * Math.Log(s) / s);

                _spare = u2 * factor;
                _hasSpare = true;

                return u1 * factor;
            }

            private double Uniform()
            {
                _state = _state * 6364136223846793005UL + 1442695040888963407UL;
                return ((_state >> 11) & 0x1FFFFFFFFFFFFFUL) / (double)(1UL << 53);
            }
        }
    }

    /// <summary>A generated pulsed record and what was asked for.</summary>
    public sealed class PulsedRecord
    {
        private readonly float[] _samples;

        internal PulsedRecord(float[] samples, IList<int> burstStarts, PulsedSource source)
        {
            _samples = samples;
            BurstStarts = new ReadOnlyCollection<int>(burstStarts);
            RequestedOnSamples = source.OnSamples;
            RequestedOffSamples = source.OffSamples;
            RequestedRampSamples = source.RampSamples;
            RequestedNoiseFloorDb = source.NoiseFloorDb;
            Ramp = source.Ramp;
            Amplitude = source.Amplitude;
            SampleRateHz = source.SampleRateHz;
        }

        /// <summary>Interleaved I/Q.</summary>
        public ReadOnlySpan<float> Samples => new ReadOnlySpan<float>(_samples);

        /// <summary>Complex sample count.</summary>
        public int SampleCount => _samples.Length / 2;

        /// <summary>Where each burst begins, in samples.</summary>
        public IReadOnlyList<int> BurstStarts { get; }

        /// <summary>The requested on time, in samples.</summary>
        public int RequestedOnSamples { get; }

        /// <summary>The requested off time, in samples.</summary>
        public int RequestedOffSamples { get; }

        /// <summary>The requested ramp length, in samples.</summary>
        public int RequestedRampSamples { get; }

        /// <summary>The requested floor, in dB below the on-state amplitude.</summary>
        public double RequestedNoiseFloorDb { get; }

        /// <summary>The requested ramp shape.</summary>
        public RampShape Ramp { get; }

        /// <summary>The on-state amplitude.</summary>
        public double Amplitude { get; }

        /// <summary>Sample rate, in hertz.</summary>
        public double SampleRateHz { get; }

        /// <summary>The envelope magnitude at a sample.</summary>
        public double MagnitudeAt(int sample)
        {
            double i = _samples[sample * 2];
            double q = _samples[sample * 2 + 1];

            return Math.Sqrt(i * i + q * q);
        }
    }
}
