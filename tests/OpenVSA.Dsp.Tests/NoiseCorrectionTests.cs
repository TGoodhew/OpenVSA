using System;
using OpenVSA.Core;
using OpenVSA.Dsp.Spectrum;
using OpenVSA.Dsp.Windowing;
using Xunit;
using Xunit.Abstractions;

namespace OpenVSA.Dsp.Tests
{
    /// <summary>
    /// <c>REQ-DSP-024</c>'s <em>Noise Correction</em>: "measuring a signal at a known level above a
    /// characterised noise floor returns a power closer to the analytic value than with it off, and
    /// correcting a noise-only input does not produce a negative power — it floors at the reported
    /// measurement limit."
    /// </summary>
    /// <remarks>
    /// The noise here is pseudo-random from a fixed seed, so the runs are reproducible while the
    /// signal is still the sum of a tone and something the correction cannot have been tuned to.
    /// A deterministic "noise" would let a subtraction that was wrong in shape pass by cancelling
    /// against itself.
    /// </remarks>
    public class NoiseCorrectionTests
    {
        private const double RateHz = 15e6;
        private const double CenterHz = 1e9;
        private const int Samples = 4096;

        private readonly ITestOutputHelper _output;

        public NoiseCorrectionTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void CorrectionMovesASignalNearTheFloorTowardsItsTrueLevel()
        {
            // The criterion. A tone a few dB above the noise reads high, because the bin holds the
            // tone's power plus the instrument's; subtracting the characterised floor takes that
            // back off.
            //
            // Both traces are averaged, because that is the only way the question is answerable.
            // A single bin's noise power is exponentially distributed - its standard deviation
            // equals its mean - so on one sweep the correction is a coin toss whatever the
            // arithmetic does. Averaging is also how anyone actually uses this: the setting is
            // reached for when a signal is near the floor, which is when a single sweep is not
            // worth reading.
            const double toneVolts = 0.0003;
            const double noiseVolts = 0.02;
            const int sweeps = 32;

            SpectrumFrame clean = Measure(toneVolts, 0.0, 1);
            SpectrumFrame noisy = Average(toneVolts, noiseVolts, 100, sweeps);

            NoiseFloor floor = NoiseFloor.FromTrace(Average(0.0, noiseVolts, 500, sweeps));
            SpectrumFrame corrected = NoiseCorrection.Apply(noisy, floor);

            int bin = clean.IndexOfPeak();

            double truth = clean.LevelsDbm[bin];
            double before = Math.Abs(noisy.LevelsDbm[bin] - truth);
            double after = Math.Abs(corrected.LevelsDbm[bin] - truth);

            _output.WriteLine(
                "true " + truth.ToString("0.000") + " dBm, uncorrected error " +
                before.ToString("0.000") + " dB, corrected error " + after.ToString("0.000") +
                " dB");

            Assert.True(
                before > 1.0,
                "the tone was " + before + " dB out uncorrected; it is not near enough to the " +
                "floor for this to be testing anything.");
            // Most of the error, not merely some of it. "Closer" alone would be satisfied by a
            // correction that was directionally right and numerically useless.
            //
            // What is left is the estimator's own noise rather than a bias: at 32 sweeps the floor
            // is known to about 0.8 dB, and here it is subtracting a power seven times the
            // signal's, so that uncertainty arrives at the answer multiplied by seven. More sweeps
            // would shrink it; nothing in the arithmetic would.
            Assert.True(
                after < before / 2.0,
                "correction moved the reading from " + before + " dB out to " + after +
                " dB out, which is less than half of it.");
        }

        [Fact]
        public void ANoiseOnlyInputFloorsAtTheReportedMeasurementLimit()
        {
            // The other half of the criterion, and the one that is easy to get wrong: half the bins
            // of a noise-only trace sit below their expected floor, so an unguarded subtraction
            // leaves a trace of implausibly deep nulls that reads as measured structure.
            SpectrumFrame noise = Measure(0.0, 0.02, 11);
            NoiseFloor floor = Characterise(0.02, 12);

            SpectrumFrame corrected = NoiseCorrection.Apply(noise, floor);

            int floored = 0;

            for (int i = 0; i < corrected.PointCount; i++)
            {
                double level = corrected.LevelsDbm[i];

                Assert.False(double.IsNaN(level), "point " + i + " corrected to NaN.");
                Assert.True(
                    level >= AmplitudeScale.FloorDbm,
                    "point " + i + " corrected to " + level +
                    " dBm, below the reported measurement limit of " + AmplitudeScale.FloorDbm + ".");

                if (level <= AmplitudeScale.FloorDbm)
                {
                    floored++;
                }
            }

            _output.WriteLine(
                floored + " of " + corrected.PointCount + " points corrected to the floor");

            // Around half of them, since the measured trace scatters either side of the
            // characterisation. Anything close to none would mean the subtraction was not
            // happening at all.
            Assert.True(
                floored > corrected.PointCount / 10,
                "only " + floored + " points floored; the correction cannot be doing much.");
        }

        [Fact]
        public void ASignalWellAboveTheFloorIsBarelyTouched()
        {
            // Correction is a small change to a strong signal and a large one to a weak signal.
            // If a tone 40 dB up moves measurably, the subtraction is not a power subtraction.
            SpectrumFrame strong = Measure(1.0, 0.02, 21);
            NoiseFloor floor = Characterise(0.02, 22);

            SpectrumFrame corrected = NoiseCorrection.Apply(strong, floor);
            int bin = strong.IndexOfPeak();

            Assert.True(
                Math.Abs(corrected.LevelsDbm[bin] - strong.LevelsDbm[bin]) < 0.01,
                "a strong tone moved by " +
                Math.Abs(corrected.LevelsDbm[bin] - strong.LevelsDbm[bin]) + " dB.");
        }

        [Fact]
        public void SubtractionIsInPowerNotInDecibels()
        {
            // The failure this guards against reads as plausible: subtracting the two dB figures is
            // a division of powers, so a bin measured 3 dB above the floor would correct to 3 dBm
            // rather than to the floor level itself. In power, 3 dB above is exactly twice, and
            // taking the floor off leaves the floor.
            const double floorDbm = -90.0;
            double measuredDbm = floorDbm + 10.0 * Math.Log10(2.0);

            SpectrumFrame frame = SpectrumFrame.FromLevels(
                new[] { (float)measuredDbm, (float)measuredDbm },
                CenterHz, 1e3, WindowType.FlatTop, 3.8194);
            SpectrumFrame corrected =
                NoiseCorrection.Apply(frame, NoiseFloor.Flat(floorDbm, frame.ResolutionBandwidthHz));

            Assert.Equal(floorDbm, corrected.LevelsDbm[0], 6);

            // Where the dB-domain mistake would have given the difference of the two figures.
            Assert.NotEqual(measuredDbm - floorDbm, corrected.LevelsDbm[0], 3);
        }

        [Fact]
        public void TheFloorScalesWithResolutionBandwidth()
        {
            // Noise power is proportional to noise bandwidth, so a floor characterised at one RBW
            // reads 3 dB higher at twice the RBW. A floor stored as a bare level in dBm would be a
            // number that is only right at a setting nobody wrote down.
            NoiseFloor floor = NoiseFloor.Flat(-90.0, 1000.0);

            Assert.Equal(-90.0, floor.LevelAt(1e9, 1000.0), 9);
            Assert.Equal(-90.0 + 10.0 * Math.Log10(2.0), floor.LevelAt(1e9, 2000.0), 9);
            Assert.Equal(-90.0 + 10.0 * Math.Log10(0.5), floor.LevelAt(1e9, 500.0), 9);

            // A decade of bandwidth is exactly 10 dB, which is the one case where the round number
            // is the right one.
            Assert.Equal(-100.0, floor.LevelAt(1e9, 100.0), 9);
        }

        [Fact]
        public void ACharacterisationKeepsItsShapeAcrossFrequency()
        {
            // The reason the floor is a trace and not a number: an instrument's floor rises at the
            // band edges where the anti-alias filter rolls off.
            var levels = new float[64];

            for (int i = 0; i < levels.Length; i++)
            {
                levels[i] = (float)(-100.0 + 20.0 * Math.Abs(i - 31.5) / 31.5);
            }

            SpectrumFrame shaped = SpectrumFrame.FromLevels(levels, 1e9, 1e3, WindowType.FlatTop, 3.8194);
            NoiseFloor floor = NoiseFloor.FromTrace(shaped);

            Assert.Equal(64, floor.PointCount);
            Assert.Equal(shaped.ResolutionBandwidthHz, floor.ResolutionBandwidthHz, 9);

            // Read back at the RBW it was taken at: the shape survives.
            Assert.Equal(
                levels[0], floor.LevelAt(shaped.FrequencyAt(0), shaped.ResolutionBandwidthHz), 4);
            Assert.Equal(
                levels[32], floor.LevelAt(shaped.FrequencyAt(32), shaped.ResolutionBandwidthHz), 4);
            Assert.True(
                floor.LevelAt(shaped.FrequencyAt(0), shaped.ResolutionBandwidthHz) >
                floor.LevelAt(shaped.FrequencyAt(31), shaped.ResolutionBandwidthHz));
        }

        [Fact]
        public void OutsideTheCharacterisedSpanTheNearestEndIsUsed()
        {
            // The honest extrapolation of a measurement that stopped there. The alternative -
            // correcting by zero outside the characterisation - would leave a step in the trace at
            // the edge of a range the user cannot see.
            var levels = new float[] { -100.0f, -95.0f, -90.0f, -85.0f };
            SpectrumFrame trace = SpectrumFrame.FromLevels(levels, 1e9, 1e3, WindowType.FlatTop, 3.8194);
            NoiseFloor floor = NoiseFloor.FromTrace(trace);

            double rbw = trace.ResolutionBandwidthHz;

            Assert.Equal(-100.0, floor.LevelAt(1e9 - 1e9, rbw), 4);
            Assert.Equal(-85.0, floor.LevelAt(1e9 + 1e9, rbw), 4);
        }

        [Fact]
        public void ACorrectedTraceCarriesNoPhaseAndSaysSo()
        {
            // Subtracting an incoherent power leaves a magnitude. REQ-TRC-002 makes the phase
            // formats unselectable on such a trace rather than showing a zero as though measured.
            SpectrumFrame frame = Measure(0.5, 0.01, 31);

            Assert.True(frame.HasPhase);

            SpectrumFrame corrected =
                NoiseCorrection.Apply(frame, Characterise(0.01, 32));

            Assert.False(corrected.HasPhase);
            Assert.True(corrected.NoiseCorrected);
            Assert.False(frame.NoiseCorrected);
        }

        [Fact]
        public void CorrectionLeavesTheOriginalAlone()
        {
            SpectrumFrame frame = Measure(0.5, 0.01, 41);
            var before = new float[frame.PointCount];

            for (int i = 0; i < before.Length; i++)
            {
                before[i] = frame.LevelsDbm[i];
            }

            NoiseCorrection.Apply(frame, Characterise(0.01, 42));

            for (int i = 0; i < before.Length; i++)
            {
                Assert.Equal(before[i], frame.LevelsDbm[i]);
            }
        }

        [Fact]
        public void TheAnnotationNamesTheCharacterisationItUsed()
        {
            NoiseFloor floor = NoiseFloor.Flat(-95.0, 1234.5);

            Assert.Equal(string.Empty, NoiseCorrection.Describe(null));
            Assert.Contains("1234.5", NoiseCorrection.Describe(floor));
            Assert.Contains("Noise corr", NoiseCorrection.Describe(floor));
        }

        [Fact]
        public void MissingArgumentsAreRefused()
        {
            Assert.Throws<ArgumentNullException>(
                () => NoiseCorrection.Apply(null, NoiseFloor.Flat(-90.0, 1e3)));
            Assert.Throws<ArgumentNullException>(
                () => NoiseCorrection.Apply(Measure(0.5, 0.0, 51), null));
            Assert.Throws<ArgumentNullException>(() => NoiseFloor.FromTrace(null));
        }

        [Theory]
        [InlineData(0.0)]
        [InlineData(-1.0)]
        [InlineData(double.NaN)]
        public void ACharacterisationBandwidthThatIsNotPositiveIsRefused(double rbwHz)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => NoiseFloor.Flat(-90.0, rbwHz));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => NoiseFloor.Flat(-90.0, 1e3).LevelAt(1e9, rbwHz));
        }

        [Fact]
        public void ATraceTooShortToHaveAnAxisIsRefused()
        {
            Assert.Throws<ArgumentException>(
                () => NoiseFloor.FromTrace(
                    SpectrumFrame.FromLevels(new[] { -90.0f }, 1e9, 1e3, WindowType.FlatTop, 3.8194)));
        }

        /// <summary>A spectrum of a tone plus pseudo-random noise.</summary>
        private static SpectrumFrame Measure(double toneVolts, double noiseVolts, int seed)
        {
            using (IqBlock block = Signal(toneVolts, noiseVolts, seed))
            {
                return new SpectrumComputer(WindowType.FlatTop, null, null).Compute(block);
            }
        }

        /// <summary>An RMS average of several sweeps, each with its own noise realisation.</summary>
        private static SpectrumFrame Average(
            double toneVolts, double noiseVolts, int firstSeed, int sweeps)
        {
            var computer = new SpectrumComputer(WindowType.FlatTop, null, null);
            var averager = new TraceAverager(AveragingType.RmsVideo, sweeps);
            SpectrumFrame averaged = null;

            for (int i = 0; i < sweeps; i++)
            {
                using (IqBlock block = Signal(toneVolts, noiseVolts, firstSeed + i))
                {
                    averaged = averager.Accumulate(computer.Compute(block));
                }
            }

            return averaged;
        }

        /// <summary>
        /// A floor characterised the way an instrument's is: measure the noise alone, with nothing
        /// on the input, and keep the trace.
        /// </summary>
        private static NoiseFloor Characterise(double noiseVolts, int seed) =>
            NoiseFloor.FromTrace(Measure(0.0, noiseVolts, seed));

        private static IqBlock Signal(double toneVolts, double noiseVolts, int seed)
        {
            IqBlock block = IqBlock.Rent(new IqBlockMetadata(
                sampleCount: Samples,
                sampleRateHz: RateHz,
                centerFrequencyHz: CenterHz,
                isBaseband: false,
                fullScaleVolts: 1.0,
                referenceLevelDbm: 0.0,
                sequenceNumber: 1,
                acquiredUtc: new DateTime(2026, 7, 26, 0, 0, 0, DateTimeKind.Utc),
                triggerOffsetSeconds: 0.0,
                triggerCorrectionsApplied: false,
                source: new FrontEndId("test"),
                extended: null));

            Span<float> data = block.GetSamples();
            var random = new Random(seed);

            // An awkward offset, deliberately not on a bin centre.
            const double cycles = 2.3117e6 / RateHz;

            for (int n = 0; n < Samples; n++)
            {
                double angle = 2.0 * Math.PI * cycles * n;

                data[n * 2] = (float)(toneVolts * Math.Cos(angle) + noiseVolts * Gaussian(random));
                data[n * 2 + 1] = (float)(toneVolts * Math.Sin(angle) + noiseVolts * Gaussian(random));
            }

            return block;
        }

        /// <summary>Box-Muller, one value per call; the discarded half costs nothing here.</summary>
        private static double Gaussian(Random random)
        {
            double u = 1.0 - random.NextDouble();
            double v = random.NextDouble();

            return Math.Sqrt(-2.0 * Math.Log(u)) * Math.Cos(2.0 * Math.PI * v);
        }
    }
}
