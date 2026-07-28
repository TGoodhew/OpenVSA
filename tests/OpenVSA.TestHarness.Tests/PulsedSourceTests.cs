using System;
using System.Linq;
using OpenVSA.TestHarness.Synthesis;
using Xunit;
using Xunit.Abstractions;

namespace OpenVSA.TestHarness.Tests
{
    /// <summary>
    /// <c>REQ-SIM-004</c>: a generated burst, measured back from its own samples, reproduces the
    /// on and off times to within one sample, the ramp shape to within 1 % of its transition time,
    /// and the inter-burst noise floor to within 0.5 dB.
    /// </summary>
    /// <remarks>
    /// Measured back from the samples, never from the generator's own bookkeeping. A test that
    /// asked <c>PulsedRecord</c> where it put the bursts would confirm that a number was stored and
    /// retrieved; the requirement asks whether the samples actually say what was requested.
    /// </remarks>
    public class PulsedSourceTests
    {
        private readonly ITestOutputHelper _output;

        /// <summary>Takes xunit's output sink.</summary>
        /// <param name="output">Where the recovered figures are written.</param>
        public PulsedSourceTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void OnAndOffTimesAreRecoveredToWithinOneSample()
        {
            var source = new PulsedSource
            {
                OnSamples = 4096,
                OffSamples = 2048,
                BurstCount = 3,
                RampSamples = 0,
                Ramp = RampShape.Rectangular,
                NoiseFloorDb = -60.0,
            };

            PulsedRecord record = source.Generate();

            // Half the on-state amplitude: far above a -60 dB floor and far below the burst, so the
            // crossing is unambiguous for a rectangular edge.
            double threshold = source.Amplitude * 0.5;

            int[] rising = Crossings(record, threshold, rising: true);
            int[] falling = Crossings(record, threshold, rising: false);

            _output.WriteLine(
                "rising at " + string.Join(", ", rising) +
                "; falling at " + string.Join(", ", falling));

            Assert.Equal(3, rising.Length);
            Assert.Equal(3, falling.Length);

            for (int burst = 0; burst < 3; burst++)
            {
                int measuredOn = falling[burst] - rising[burst];

                Assert.True(
                    Math.Abs(measuredOn - source.OnSamples) <= 1,
                    "Burst " + burst + " measured " + measuredOn + " samples on, requested " +
                    source.OnSamples + ".");

                if (burst > 0)
                {
                    int measuredOff = rising[burst] - falling[burst - 1];

                    Assert.True(
                        Math.Abs(measuredOff - source.OffSamples) <= 1,
                        "Gap " + burst + " measured " + measuredOff + " samples off, requested " +
                        source.OffSamples + ".");
                }
            }
        }

        [Theory]
        [InlineData(RampShape.Linear)]
        [InlineData(RampShape.RaisedCosine)]
        public void TheRampOccupiesItsRequestedTransitionTime(RampShape shape)
        {
            const int Ramp = 128;

            var source = new PulsedSource
            {
                OnSamples = 4096,
                OffSamples = 1024,
                BurstCount = 1,
                RampSamples = Ramp,
                Ramp = shape,
                NoiseFloorDb = -80.0,
            };

            PulsedRecord record = source.Generate();

            // The transition, measured between the 10 % and 90 % points of the envelope, and
            // compared against what those points are for the requested shape rather than against
            // the full ramp length — a raised cosine spends real time near both ends, and calling
            // that an error would be measuring the definition rather than the signal.
            // The first rising edge, which now follows the opening gap.
            int tenth = FirstAbove(record, source.Amplitude * 0.1);
            int ninetieth = FirstAbove(record, source.Amplitude * 0.9);

            double measured = ninetieth - tenth;
            // Derived from the shape's own definition rather than hard-coded. My first attempt
            // guessed 2/3 of the ramp for a raised cosine; the true 10-90 width is
            // (acos(-0.8) - acos(0.8)) / pi = 0.5903 of it, and the generator was right while the
            // expectation was wrong. A closed form here also means changing the ramp definition
            // fails this test rather than silently moving the target.
            double expected = shape == RampShape.Linear
                ? Ramp * 0.8
                : Ramp * (Math.Acos(-0.8) - Math.Acos(0.8)) / Math.PI;

            _output.WriteLine(
                shape + ": 10-90 % over " + measured.ToString("F1") + " samples, expected " +
                expected.ToString("F1"));

            Assert.True(
                Math.Abs(measured - expected) <= Ramp * 0.01,
                shape + " transition measured " + measured + " samples between the 10 % and 90 % " +
                "points, expected " + expected.ToString("F1") + " — outside 1 % of the " + Ramp +
                "-sample transition time.");
        }

        [Theory]
        [InlineData(-30.0)]
        [InlineData(-40.0)]
        [InlineData(-55.0)]
        public void TheInterBurstNoiseFloorIsRecoveredToWithinHalfADecibel(double floorDb)
        {
            var source = new PulsedSource
            {
                OnSamples = 2048,
                OffSamples = 8192,
                BurstCount = 2,
                RampSamples = 32,
                NoiseFloorDb = floorDb,
            };

            PulsedRecord record = source.Generate();

            // Well inside the opening gap, clear of the first rising edge.
            int start = 1024;
            int stop = source.OffSamples - 1024;

            double sum = 0.0;

            for (int n = start; n < stop; n++)
            {
                double magnitude = record.MagnitudeAt(n);
                sum += magnitude * magnitude;
            }

            // RMS of the complex envelope. The floor is stated in amplitude terms, and for a
            // Gaussian pair of deviation s the envelope RMS is s * sqrt(2).
            double rms = Math.Sqrt(sum / (stop - start));
            double measuredDb = 20.0 * Math.Log10(rms / (source.Amplitude * Math.Sqrt(2.0)));

            _output.WriteLine(
                "requested " + floorDb.ToString("F1") + " dB, measured " +
                measuredDb.ToString("F2") + " dB");

            Assert.True(
                Math.Abs(measuredDb - floorDb) <= 0.5,
                "Floor measured " + measuredDb.ToString("F2") + " dB against a requested " +
                floorDb.ToString("F1") + " dB.");
        }

        [Fact]
        public void TheSameSeedGivesBitIdenticalSamples()
        {
            // REQ-SIM-003, which REQ-SIM-004's criterion leans on: "generation is seeded and
            // reproducible, so these are exact comparisons against the requested parameters rather
            // than against a previous run".
            PulsedRecord first = new PulsedSource { Seed = 4242, BurstCount = 2 }.Generate();
            PulsedRecord second = new PulsedSource { Seed = 4242, BurstCount = 2 }.Generate();
            PulsedRecord other = new PulsedSource { Seed = 4243, BurstCount = 2 }.Generate();

            Assert.Equal(first.SampleCount, second.SampleCount);

            for (int i = 0; i < first.SampleCount * 2; i++)
            {
                Assert.Equal(first.Samples[i], second.Samples[i]);
            }

            // And a different seed gives a different record, or the seed is not doing anything.
            bool differs = false;

            for (int i = 0; i < first.SampleCount * 2 && !differs; i++)
            {
                differs = first.Samples[i] != other.Samples[i];
            }

            Assert.True(differs, "Two seeds produced identical records.");
        }

        [Fact]
        public void RampsLongerThanTheBurstAreRefused()
        {
            // Ramps are counted inside the on time, so two of them cannot exceed it. Refused at
            // generation rather than silently clipped, which would produce a burst whose measured
            // on time did not match the request and blame the measurement.
            var source = new PulsedSource { OnSamples = 100, RampSamples = 60 };

            InvalidOperationException failure =
                Assert.Throws<InvalidOperationException>(() => source.Generate());

            Assert.Contains("counted inside the on time", failure.Message);
        }

        /// <summary>Sample indices where the envelope crosses a threshold.</summary>
        private static int[] Crossings(PulsedRecord record, double threshold, bool rising)
        {
            var found = new System.Collections.Generic.List<int>();

            for (int n = 1; n < record.SampleCount; n++)
            {
                double previous = record.MagnitudeAt(n - 1);
                double current = record.MagnitudeAt(n);

                if (rising && previous < threshold && current >= threshold)
                {
                    found.Add(n);
                }
                else if (!rising && previous >= threshold && current < threshold)
                {
                    found.Add(n);
                }
            }

            return found.ToArray();
        }

        private static int FirstAbove(PulsedRecord record, double threshold)
        {
            for (int n = 0; n < record.SampleCount; n++)
            {
                if (record.MagnitudeAt(n) >= threshold)
                {
                    return n;
                }
            }

            throw new InvalidOperationException("The envelope never reached " + threshold + ".");
        }
    }
}
