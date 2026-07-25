using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using OpenVSA.Core;
using OpenVSA.Hal;
using OpenVSA.Hal.Sim;
using Xunit;

namespace OpenVSA.Hal.Tests
{
    /// <summary>
    /// Covers <c>REQ-SIM-003</c> (deterministic seeded generation), <c>REQ-HAL-003</c> (discovery
    /// by attribute) and the generated signal's agreement with closed-form expectations.
    /// </summary>
    public class SimulatedFrontEndTests
    {
        private static AcquisitionRequest Request(int samples = 4096) =>
            new AcquisitionRequest(1e9, 1e6, samples, -10.0);

        private static async Task<float[]> AcquireSamples(SimulatedSignalSettings settings, int samples = 4096)
        {
            using (var frontEnd = new SimulatedFrontEnd(settings))
            {
                await frontEnd.ConnectAsync(CancellationToken.None);
                AcquisitionPlan plan = frontEnd.Negotiate(Request(samples));
                await frontEnd.ConfigureAsync(plan, CancellationToken.None);
                await frontEnd.ArmAsync(CancellationToken.None);

                using (IqBlock block = await frontEnd.AcquireNextAsync(CancellationToken.None))
                {
                    return block.GetSamples().ToArray();
                }
            }
        }

        // ---- REQ-ACQ-001: the requested path decides the sample-rate law ----------------------

        [Fact]
        public void TheComplexPathUsesTheComplexRateLaw()
        {
            using (var frontEnd = new SimulatedFrontEnd())
            {
                AcquisitionPlan plan = frontEnd.Negotiate(
                    new AcquisitionRequest(1e9, 10e6, 8192, -10.0, AnalysisPath.ComplexZoom));

                Assert.Equal(AnalysisPath.ComplexZoom, plan.Path);
                Assert.Equal(12.8e6, plan.SampleRateHz, 3);
            }
        }

        [Fact]
        public void TheRealPathUsesTheRealRateLaw_AndDeliversRealSamples()
        {
            using (var frontEnd = new SimulatedFrontEnd())
            {
                AcquisitionPlan plan = frontEnd.Negotiate(
                    new AcquisitionRequest(1e9, 10e6, 8192, -10.0, AnalysisPath.RealBaseband));

                Assert.Equal(AnalysisPath.RealBaseband, plan.Path);
                Assert.Equal(25.6e6, plan.SampleRateHz, 3);
            }
        }

        [Fact]
        public async Task ARealBasebandBlockHasNoQuadratureChannel()
        {
            using (var frontEnd = new SimulatedFrontEnd())
            {
                await frontEnd.ConnectAsync(CancellationToken.None);
                AcquisitionPlan plan = frontEnd.Negotiate(
                    new AcquisitionRequest(1e9, 10e6, 1024, -10.0, AnalysisPath.RealBaseband));
                await frontEnd.ConfigureAsync(plan, CancellationToken.None);
                await frontEnd.ArmAsync(CancellationToken.None);

                using (IqBlock block = await frontEnd.AcquireNextAsync(CancellationToken.None))
                {
                    // A baseband digitiser has no local oscillator to tune, so the block is from
                    // 0 Hz whatever centre frequency was asked for.
                    Assert.True(block.IsBaseband);
                    Assert.Equal(0.0, block.CenterFrequencyHz);

                    float[] samples = block.GetSamples().ToArray();
                    for (int n = 0; n < block.SampleCount; n++)
                    {
                        Assert.Equal(0.0f, samples[n * 2 + 1]);
                    }
                }
            }
        }

        // ---- REQ-SIM-003: deterministic, seeded generation -----------------------------------

        [Fact]
        public async Task SameSeedAndParameters_ProduceBitIdenticalStreams()
        {
            // REQ-SIM-003 AC, asserted as bit equality rather than a tolerance. Anything looser
            // would pass on a generator that is merely similar, which is not reproducible.
            var settings = new Func<SimulatedSignalSettings>(() => new SimulatedSignalSettings
            {
                ToneOffsetHz = 12345.0,
                AmplitudeVolts = 0.5,
                SnrDb = 20.0,
                Seed = 42,
            });

            float[] first = await AcquireSamples(settings());
            float[] second = await AcquireSamples(settings());

            Assert.Equal(first.Length, second.Length);

            // Compared as raw bytes, not as floats. Float equality would treat -0.0 and 0.0 as
            // identical and any NaN as unequal to itself, neither of which is what "bit-identical"
            // means. (BitConverter.SingleToInt32Bits would be the tidy way and does not exist on
            // .NET Framework 4.7.2 — RISK-05 again.)
            var firstBytes = new byte[first.Length * sizeof(float)];
            var secondBytes = new byte[second.Length * sizeof(float)];
            Buffer.BlockCopy(first, 0, firstBytes, 0, firstBytes.Length);
            Buffer.BlockCopy(second, 0, secondBytes, 0, secondBytes.Length);

            for (int i = 0; i < firstBytes.Length; i++)
            {
                Assert.True(
                    firstBytes[i] == secondBytes[i],
                    "Streams diverged at byte " + i + " (sample " + (i / sizeof(float)) + ")");
            }
        }

        [Fact]
        public async Task DifferentSeeds_ProduceDifferentNoise()
        {
            float[] a = await AcquireSamples(new SimulatedSignalSettings { SnrDb = 20.0, Seed = 1 });
            float[] b = await AcquireSamples(new SimulatedSignalSettings { SnrDb = 20.0, Seed = 2 });

            Assert.False(a.SequenceEqual(b), "Different seeds must give different noise.");
        }

        [Fact]
        public void DeterministicRandom_StreamIsStableAcrossInstances()
        {
            var first = new DeterministicRandom(2026);
            var second = new DeterministicRandom(2026);

            for (int i = 0; i < 1000; i++)
            {
                Assert.Equal(first.NextUInt64(), second.NextUInt64());
            }
        }

        [Fact]
        public void DeterministicRandom_UniformIsInRange()
        {
            var random = new DeterministicRandom(7);

            for (int i = 0; i < 10000; i++)
            {
                double value = random.NextDouble();
                Assert.InRange(value, 0.0, 1.0);
                Assert.NotEqual(1.0, value);
            }
        }

        [Fact]
        public void DeterministicRandom_GaussianHasExpectedMoments()
        {
            // Closed-form reference per REQ-TST-001: zero mean, unit variance. Bounds are loose
            // enough not to be flaky and tight enough to catch a scale error.
            var random = new DeterministicRandom(99);
            const int n = 200000;

            double sum = 0.0, sumSquares = 0.0;
            for (int i = 0; i < n; i++)
            {
                double value = random.NextGaussian();
                sum += value;
                sumSquares += value * value;
            }

            double mean = sum / n;
            double variance = sumSquares / n - mean * mean;

            Assert.InRange(mean, -0.02, 0.02);
            Assert.InRange(variance, 0.97, 1.03);
        }

        // ---- Generated signal agrees with closed-form expectations ---------------------------

        [Fact]
        public async Task CleanTone_HasConstantEnvelopeAtTheRequestedAmplitude()
        {
            // With no noise the envelope is exactly the requested amplitude at every sample. That
            // is a closed-form check on amplitude scaling, and it fails if the generator applies a
            // stray factor of 2 or sqrt(2) — the classic error, and the one REQ-E44-002a exists to
            // guard against on the instrument side.
            const double amplitude = 0.25;
            float[] samples = await AcquireSamples(new SimulatedSignalSettings
            {
                ToneOffsetHz = 1000.0,
                AmplitudeVolts = amplitude,
                SnrDb = double.PositiveInfinity,
            });

            for (int n = 0; n < samples.Length / 2; n++)
            {
                double i = samples[n * 2];
                double q = samples[n * 2 + 1];
                double envelope = Math.Sqrt(i * i + q * q);

                Assert.InRange(envelope, amplitude - 1e-6, amplitude + 1e-6);
            }
        }

        [Fact]
        public async Task CleanTone_AdvancesPhaseAtTheRequestedRate()
        {
            // Phase step between consecutive samples is 2*pi*f/Fs. Checking the increment rather
            // than absolute phase makes this independent of the starting phase.
            const double toneHz = 50000.0;
            const int samples = 1024;

            using (var frontEnd = new SimulatedFrontEnd(new SimulatedSignalSettings
            {
                ToneOffsetHz = toneHz,
                AmplitudeVolts = 1.0,
                SnrDb = double.PositiveInfinity,
            }))
            {
                await frontEnd.ConnectAsync(CancellationToken.None);
                AcquisitionPlan plan = frontEnd.Negotiate(Request(samples));
                await frontEnd.ConfigureAsync(plan, CancellationToken.None);
                await frontEnd.ArmAsync(CancellationToken.None);

                using (IqBlock block = await frontEnd.AcquireNextAsync(CancellationToken.None))
                {
                    double expectedStep = 2.0 * Math.PI * toneHz / plan.SampleRateHz;

                    for (int n = 1; n < 100; n++)
                    {
                        double previous = block.GetSample(n - 1).Phase;
                        double current = block.GetSample(n).Phase;

                        double step = current - previous;
                        while (step <= -Math.PI) step += 2.0 * Math.PI;
                        while (step > Math.PI) step -= 2.0 * Math.PI;

                        Assert.InRange(step, expectedStep - 1e-5, expectedStep + 1e-5);
                    }
                }
            }
        }

        [Fact]
        public async Task NoisePower_MatchesTheRequestedSnr()
        {
            // Closed form: for a complex tone of amplitude A, signal power is A^2/2, so noise power
            // at a given SNR is (A^2/2)/10^(SNR/10). Measured by subtracting the known clean tone,
            // which isolates the noise exactly rather than estimating it.
            const double amplitude = 1.0;
            const double snrDb = 20.0;
            const int samples = 65536;

            float[] clean = await AcquireSamples(new SimulatedSignalSettings
            {
                ToneOffsetHz = 1000.0, AmplitudeVolts = amplitude,
                SnrDb = double.PositiveInfinity, Seed = 5,
            }, samples);

            float[] noisy = await AcquireSamples(new SimulatedSignalSettings
            {
                ToneOffsetHz = 1000.0, AmplitudeVolts = amplitude,
                SnrDb = snrDb, Seed = 5,
            }, samples);

            double noisePower = 0.0;
            for (int i = 0; i < clean.Length; i++)
            {
                double difference = noisy[i] - clean[i];
                noisePower += difference * difference;
            }

            noisePower /= samples;

            double signalPower = amplitude * amplitude / 2.0;
            double expected = signalPower / Math.Pow(10.0, snrDb / 10.0);

            Assert.InRange(noisePower, expected * 0.95, expected * 1.05);
        }

        // ---- REQ-HAL-003: discovery by attribute ---------------------------------------------

        [Fact]
        public void SimulatedFrontEnd_IsDiscoverableByAttribute()
        {
            // REQ-HAL-003: front ends are found through [FrontEndProvider], so adding one never
            // means editing a registry in core code.
            Type[] providers = typeof(SimulatedFrontEnd).Assembly
                .GetTypes()
                .Where(t => t.GetCustomAttribute<FrontEndProviderAttribute>() != null)
                .ToArray();

            Assert.Contains(typeof(SimulatedFrontEnd), providers);

            var attribute = typeof(SimulatedFrontEnd).GetCustomAttribute<FrontEndProviderAttribute>();
            Assert.Equal("Simulated source", attribute.DisplayName);

            foreach (Type provider in providers)
            {
                Assert.True(
                    typeof(IFrontEnd).IsAssignableFrom(provider),
                    provider.Name + " is marked [FrontEndProvider] but does not implement IFrontEnd.");
            }
        }

        // ---- Lifecycle ------------------------------------------------------------------------

        [Fact]
        public async Task AcquireBeforeArm_Throws()
        {
            using (var frontEnd = new SimulatedFrontEnd())
            {
                await frontEnd.ConnectAsync(CancellationToken.None);
                await frontEnd.ConfigureAsync(
                    frontEnd.Negotiate(Request()), CancellationToken.None);

                await Assert.ThrowsAsync<InvalidOperationException>(
                    () => frontEnd.AcquireNextAsync(CancellationToken.None));
            }
        }

        [Fact]
        public async Task ConfigureBeforeConnect_Throws()
        {
            using (var frontEnd = new SimulatedFrontEnd())
            {
                AcquisitionPlan plan = frontEnd.Negotiate(Request());

                await Assert.ThrowsAsync<InvalidOperationException>(
                    () => frontEnd.ConfigureAsync(plan, CancellationToken.None));
            }
        }

        [Fact]
        public async Task StateAdvancesThroughTheLifecycle()
        {
            using (var frontEnd = new SimulatedFrontEnd())
            {
                Assert.Equal(FrontEndState.Disconnected, frontEnd.State);

                await frontEnd.ConnectAsync(CancellationToken.None);
                Assert.Equal(FrontEndState.Connected, frontEnd.State);

                await frontEnd.ConfigureAsync(frontEnd.Negotiate(Request()), CancellationToken.None);
                Assert.Equal(FrontEndState.Configured, frontEnd.State);

                await frontEnd.ArmAsync(CancellationToken.None);
                Assert.Equal(FrontEndState.Armed, frontEnd.State);

                using (await frontEnd.AcquireNextAsync(CancellationToken.None))
                {
                    Assert.Equal(FrontEndState.Acquiring, frontEnd.State);
                }

                await frontEnd.AbortAsync();
                Assert.Equal(FrontEndState.Configured, frontEnd.State);
            }
        }
    }
}
