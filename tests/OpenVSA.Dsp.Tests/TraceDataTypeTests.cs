using System;
using System.Collections.Generic;
using System.Linq;
using OpenVSA.Core;
using OpenVSA.Dsp.Spectrum;
using OpenVSA.Dsp.Windowing;
using Xunit;
using Xunit.Abstractions;

namespace OpenVSA.Dsp.Tests
{
    /// <summary>
    /// <c>REQ-DSP-040</c>'s base data types, and the configurable settings of
    /// <c>REQ-DSP-044</c> and <c>REQ-DSP-045</c>.
    /// </summary>
    public class TraceDataTypeTests
    {
        private readonly ITestOutputHelper _output;

        public TraceDataTypeTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void EveryTypeTheRequirementListsIsAvailable()
        {
            // The reference product's Option 200 set, verbatim. A type missing here is one no
            // trace could be set to, which is the failure a list of names catches and a feature
            // test does not.
            string[] required =
            {
                "Spectrum", "Raw Main Time", "Instantaneous Main Time", "PSD", "Autocorrelation",
                "CCDF", "CDF", "PDF", "Correction", "Math", "No Data",
            };

            string[] available = TraceDataTypes.All.Select(TraceDataTypes.Describe).ToArray();

            _output.WriteLine(string.Join(" · ", available));

            Assert.Equal(required, available);
        }

        [Fact]
        public void AnUnknownTypeIsRefusedRatherThanNamedEmptily()
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => TraceDataTypes.Describe((TraceDataType)99));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => TraceDataTypes.FormatsFor((TraceDataType)99));
        }

        [Theory]
        [InlineData(TraceDataType.Ccdf)]
        [InlineData(TraceDataType.Cdf)]
        [InlineData(TraceDataType.Pdf)]
        [InlineData(TraceDataType.PowerSpectralDensity)]
        [InlineData(TraceDataType.Autocorrelation)]
        public void ATypeWithNoPhaseCannotBeDrawnInAFormatThatNeedsOne(TraceDataType type)
        {
            // The same rule REQ-TRC-002 applies after power averaging, applied to the data itself:
            // a CCDF is a distribution of powers, and squaring discarded the phase before the
            // distribution was formed.
            Assert.False(TraceDataTypes.CarriesPhase(type));

            IReadOnlyList<TraceFormat> formats = TraceDataTypes.FormatsFor(type);

            Assert.NotEmpty(formats);
            Assert.DoesNotContain(TraceFormat.WrappedPhase, formats);
            Assert.DoesNotContain(TraceFormat.UnwrappedPhase, formats);
            Assert.DoesNotContain(TraceFormat.GroupDelay, formats);
            Assert.Contains(TraceFormat.LogMagnitude, formats);
        }

        [Theory]
        [InlineData(TraceDataType.Spectrum)]
        [InlineData(TraceDataType.RawMainTime)]
        [InlineData(TraceDataType.InstantaneousMainTime)]
        public void ATypeWithPhaseCanBeDrawnInEveryFormat(TraceDataType type)
        {
            Assert.True(TraceDataTypes.CarriesPhase(type));

            Assert.Equal(
                Enum.GetValues(typeof(TraceFormat)).Length,
                TraceDataTypes.FormatsFor(type).Count);
        }

        [Fact]
        public void NoDataHasNoFormatsAndIsNotAMeasurement()
        {
            Assert.Empty(TraceDataTypes.FormatsFor(TraceDataType.NoData));

            Assert.False(TraceDataTypes.IsMeasured(TraceDataType.NoData));
            Assert.False(TraceDataTypes.IsMeasured(TraceDataType.Correction));
            Assert.False(TraceDataTypes.IsMeasured(TraceDataType.Math));
            Assert.True(TraceDataTypes.IsMeasured(TraceDataType.Spectrum));
        }

        // ---- REQ-DSP-045: the aperture ----------------------------------------------------------

        [Fact]
        public void APureDelayReadsAsThatDelayAtEveryAperture()
        {
            // The closed-form check, and it must hold whatever aperture is chosen: widening the
            // aperture averages the derivative, and averaging a constant leaves it alone. An
            // aperture that changed the answer on a flat delay would be an aperture that scaled
            // the result rather than spanning it.
            const double delaySeconds = 250e-9;
            const double binWidthHz = 10e3;

            SpectrumFrame frame = PureDelay(512, binWidthHz, delaySeconds);

            foreach (int aperture in new[] { 1, 2, 4, 16, 64 })
            {
                var delay = new float[frame.PointCount];
                frame.Format(TraceFormat.GroupDelay, delay, new TraceFormatOptions(aperture));

                double worst = 0.0;

                for (int i = 0; i < delay.Length; i++)
                {
                    worst = Math.Max(worst, Math.Abs(delay[i] - delaySeconds));
                }

                _output.WriteLine(
                    "aperture " + aperture + ": worst departure " + worst.ToString("G3") + " s");

                Assert.True(
                    worst <= 1e-9,
                    "At an aperture of " + aperture + " bins the delay read " +
                    worst.ToString("G3") + " s away from " + delaySeconds + " s.");
            }
        }

        [Fact]
        public void WideningTheApertureSmoothsANoisyTrace()
        {
            // What the aperture is for, and why REQ-DSP-045 puts it in the annotation: it trades
            // resolution for quiet, and a trace cannot be read without knowing which was chosen.
            SpectrumFrame noisy = NoisyDelay(1024, 10e3, 250e-9, sigma: 0.25, seed: 99);

            double narrow = Roughness(noisy, aperture: 1);
            double wide = Roughness(noisy, aperture: 32);

            _output.WriteLine(
                "roughness at 1 bin " + narrow.ToString("G3") + ", at 32 bins " + wide.ToString("G3"));

            Assert.True(
                wide < narrow / 2.0,
                "A 32-bin aperture gave a roughness of " + wide.ToString("G3") +
                " against " + narrow.ToString("G3") + " at one bin, so it is not smoothing.");
        }

        [Fact]
        public void TheApertureAppearsInTheAnnotationAndOnlyWhereItApplies()
        {
            var options = new TraceFormatOptions(apertureBins: 16);

            Assert.Equal("Aperture 16 bins", options.Describe(TraceFormat.GroupDelay));
            Assert.Equal("Aperture 1 bin", TraceFormatOptions.Default.Describe(TraceFormat.GroupDelay));

            // Printing an aperture beside a log-magnitude trace would tell the reader about a
            // setting that had no bearing on what they are looking at.
            Assert.Equal(string.Empty, options.Describe(TraceFormat.LogMagnitude));
            Assert.Equal(string.Empty, options.Describe(TraceFormat.LinearMagnitude));
        }

        // ---- REQ-DSP-044: the jump tolerance and the reference point ----------------------------

        [Fact]
        public void TheUnwrapReferencePointIsTheFirstPointAndIsAnnotated()
        {
            // REQ-DSP-044 requires the reference point to be documented. It is the first point of
            // the trace, whose unwrapped value equals its wrapped value - so a phase trace is
            // reproducible between two runs of the same measurement.
            Assert.Equal(0, TraceFormatOptions.ReferencePointIndex);

            SpectrumFrame frame = PhaseRamp(256, 10e3, turnsPerTrace: 8.0);

            var wrapped = new float[frame.PointCount];
            var unwrapped = new float[frame.PointCount];

            frame.Format(TraceFormat.WrappedPhase, wrapped);
            frame.Format(TraceFormat.UnwrappedPhase, unwrapped);

            Assert.Equal(
                wrapped[TraceFormatOptions.ReferencePointIndex],
                unwrapped[TraceFormatOptions.ReferencePointIndex],
                4);

            Assert.Contains(
                "point 0",
                TraceFormatOptions.Default.Describe(TraceFormat.UnwrappedPhase));
        }

        [Fact]
        public void WrappedAndUnwrappedAgreeModuloAFullTurnAtEveryPoint()
        {
            SpectrumFrame frame = PhaseRamp(256, 10e3, turnsPerTrace: 8.0);

            var wrapped = new float[frame.PointCount];
            var unwrapped = new float[frame.PointCount];

            frame.Format(TraceFormat.WrappedPhase, wrapped);
            frame.Format(TraceFormat.UnwrappedPhase, unwrapped);

            for (int i = 0; i < wrapped.Length; i++)
            {
                double difference = unwrapped[i] - wrapped[i];
                double turns = difference / 360.0;

                Assert.True(
                    Math.Abs(turns - Math.Round(turns)) < 1e-3,
                    "At point " + i + " the two traces differ by " + difference +
                    " degrees, which is not a whole number of turns.");
            }
        }

        [Fact]
        public void AJumpToleranceOfAFullTurnNeverUnwraps()
        {
            // The tolerance has to do something, or every test above would pass on an
            // implementation that ignored it. At 360 degrees no step can exceed it, so the
            // unwrapped trace is the wrapped one.
            SpectrumFrame frame = PhaseRamp(256, 10e3, turnsPerTrace: 8.0);

            var wrapped = new float[frame.PointCount];
            var unwrapped = new float[frame.PointCount];

            frame.Format(TraceFormat.WrappedPhase, wrapped);
            frame.Format(
                TraceFormat.UnwrappedPhase, unwrapped, new TraceFormatOptions(1, 360.0));

            for (int i = 0; i < wrapped.Length; i++)
            {
                Assert.Equal(wrapped[i], unwrapped[i], 3);
            }
        }

        [Fact]
        public void TheOptionsAreValidated()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new TraceFormatOptions(0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new TraceFormatOptions(-1));
            Assert.Throws<ArgumentOutOfRangeException>(() => new TraceFormatOptions(1, 0.0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new TraceFormatOptions(1, 361.0));

            TraceFormatOptions options = TraceFormatOptions.Default.WithAperture(8);
            Assert.Equal(8, options.ApertureBins);
            Assert.Equal(180.0, options.WithJumpTolerance(180.0).JumpToleranceDegrees, 6);
        }

        // ---- Signals -----------------------------------------------------------------------

        /// <summary>A flat response with a pure delay: phase falling linearly with frequency.</summary>
        private static SpectrumFrame PureDelay(int points, double binWidthHz, double delaySeconds)
        {
            var complex = new float[points * 2];

            for (int i = 0; i < points; i++)
            {
                double phase = -2.0 * Math.PI * i * binWidthHz * delaySeconds;
                complex[i * 2] = (float)Math.Cos(phase);
                complex[i * 2 + 1] = (float)Math.Sin(phase);
            }

            return SpectrumFrame.FromComplex(complex, 1e9, binWidthHz, WindowType.Uniform, 1.0);
        }

        /// <summary>The same, with phase noise on it.</summary>
        private static SpectrumFrame NoisyDelay(
            int points, double binWidthHz, double delaySeconds, double sigma, int seed)
        {
            var complex = new float[points * 2];
            var random = new Random(seed);

            for (int i = 0; i < points; i++)
            {
                double phase = -2.0 * Math.PI * i * binWidthHz * delaySeconds +
                    sigma * Gaussian(random);

                complex[i * 2] = (float)Math.Cos(phase);
                complex[i * 2 + 1] = (float)Math.Sin(phase);
            }

            return SpectrumFrame.FromComplex(complex, 1e9, binWidthHz, WindowType.Uniform, 1.0);
        }

        /// <summary>A phase ramp crossing many full turns.</summary>
        private static SpectrumFrame PhaseRamp(int points, double binWidthHz, double turnsPerTrace)
        {
            var complex = new float[points * 2];

            for (int i = 0; i < points; i++)
            {
                double phase = 2.0 * Math.PI * turnsPerTrace * i / points;
                complex[i * 2] = (float)Math.Cos(phase);
                complex[i * 2 + 1] = (float)Math.Sin(phase);
            }

            return SpectrumFrame.FromComplex(complex, 1e9, binWidthHz, WindowType.Uniform, 1.0);
        }

        /// <summary>Mean absolute difference between neighbouring points: how rough a trace is.</summary>
        private static double Roughness(SpectrumFrame frame, int aperture)
        {
            var delay = new float[frame.PointCount];
            frame.Format(TraceFormat.GroupDelay, delay, new TraceFormatOptions(aperture));

            double sum = 0.0;

            for (int i = 1; i < delay.Length; i++)
            {
                sum += Math.Abs(delay[i] - delay[i - 1]);
            }

            return sum / (delay.Length - 1);
        }

        private static double Gaussian(Random random)
        {
            double u1 = 1.0 - random.NextDouble();
            double u2 = random.NextDouble();
            return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
        }
    }
}
