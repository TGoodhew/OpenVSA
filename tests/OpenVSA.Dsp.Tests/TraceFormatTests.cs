using System;
using System.Linq;
using OpenVSA.Core;
using OpenVSA.Dsp.Fft;
using OpenVSA.Dsp.Spectrum;
using OpenVSA.Dsp.Windowing;
using Xunit;

namespace OpenVSA.Dsp.Tests
{
    /// <summary>
    /// <c>REQ-DSP-041</c> and <c>REQ-TRC-001</c>: formats are views of one computation.
    /// </summary>
    public class TraceFormatTests
    {
        [Fact]
        public void ChangingFormatDoesNotRecomputeTheTransform()
        {
            // REQ-TRC-001's criterion, made mechanical: the FFT provider counts its calls, and
            // cycling a held frame through every format must not add one.
            var counting = new CountingFftProvider();
            var computer = new SpectrumComputer(WindowType.FlatTop, counting, null);

            using (IqBlock block = Tone(1024, 12.8e6, 1e9, 128, 0.5))
            {
                SpectrumFrame frame = computer.Compute(block);
                int afterCompute = counting.ForwardCalls;

                foreach (TraceFormat format in Enum.GetValues(typeof(TraceFormat)))
                {
                    var destination = new float[frame.PointCount * TraceFormatter.ValuesPerPoint(format)];
                    frame.Format(format, destination);
                }

                Assert.Equal(1, afterCompute);
                Assert.Equal(afterCompute, counting.ForwardCalls);
            }
        }

        [Fact]
        public void LogAndLinearMagnitudeAgreeAfterConversion()
        {
            // REQ-DSP-041: "Log and Linear Magnitude of the same data agree to within 0.01 dB
            // after conversion" - they are the same number seen two ways, not two computations.
            SpectrumFrame frame = Measured();

            var log = new float[frame.PointCount];
            var linear = new float[frame.PointCount];
            frame.Format(TraceFormat.LogMagnitude, log);
            frame.Format(TraceFormat.LinearMagnitude, linear);

            for (int i = 0; i < frame.PointCount; i++)
            {
                if (linear[i] <= 0.0f)
                {
                    continue;
                }

                double converted = frame.Scale.VoltsSquaredToDbm((double)linear[i] * linear[i]);
                Assert.Equal(log[i], converted, 2);
            }
        }

        [Fact]
        public void RealAndImaginaryRecombineToTheMagnitude()
        {
            SpectrumFrame frame = Measured();

            var real = new float[frame.PointCount];
            var imaginary = new float[frame.PointCount];
            var magnitude = new float[frame.PointCount];

            frame.Format(TraceFormat.Real, real);
            frame.Format(TraceFormat.Imaginary, imaginary);
            frame.Format(TraceFormat.LinearMagnitude, magnitude);

            for (int i = 0; i < frame.PointCount; i++)
            {
                double recombined = Math.Sqrt(
                    (double)real[i] * real[i] + (double)imaginary[i] * imaginary[i]);

                Assert.Equal(magnitude[i], recombined, 9);
            }
        }

        [Fact]
        public void WrappedPhaseStaysWithinHalfATurn()
        {
            SpectrumFrame frame = Measured();
            var phase = new float[frame.PointCount];
            frame.Format(TraceFormat.WrappedPhase, phase);

            Assert.All(phase, p => Assert.InRange(p, -180.0f, 180.0f));
        }

        [Fact]
        public void UnwrappedPhaseFollowsARampWithNoResidualDiscontinuity()
        {
            // REQ-DSP-044: a phase ramp crossing many boundaries unwraps to that ramp. Built
            // directly rather than measured, so the expected slope is known exactly.
            const int points = 400;
            const double radiansPerPoint = 0.7;

            var complex = new float[points * 2];

            for (int i = 0; i < points; i++)
            {
                double angle = i * radiansPerPoint;
                complex[i * 2] = (float)Math.Cos(angle);
                complex[i * 2 + 1] = (float)Math.Sin(angle);
            }

            var unwrapped = new float[points];
            TraceFormatter.Format(
                complex, TraceFormat.UnwrappedPhase, new AmplitudeScale(1.0, 0.0), 1.0, unwrapped);

            double expectedStep = radiansPerPoint * 180.0 / Math.PI;

            // Two decimals, because unwrapped phase is stored as float and this ramp accumulates
            // to some 16 000 degrees: a float there resolves about a thousandth of a degree, so a
            // difference of two of them carries that much quantisation. It is the storage, not the
            // unwrap - the residual is constant rather than growing.
            for (int i = 1; i < points; i++)
            {
                Assert.Equal(expectedStep, unwrapped[i] - unwrapped[i - 1], 2);
            }
        }

        [Fact]
        public void UnwrappedPhaseWouldHaveJumpedWithoutUnwrapping()
        {
            // The wrapped form of the same ramp does jump, which is what makes the test above a
            // real check rather than a restatement.
            const int points = 400;
            var complex = new float[points * 2];

            for (int i = 0; i < points; i++)
            {
                double angle = i * 0.7;
                complex[i * 2] = (float)Math.Cos(angle);
                complex[i * 2 + 1] = (float)Math.Sin(angle);
            }

            var wrapped = new float[points];
            TraceFormatter.Format(
                complex, TraceFormat.WrappedPhase, new AmplitudeScale(1.0, 0.0), 1.0, wrapped);

            bool jumped = Enumerable.Range(1, points - 1)
                .Any(i => Math.Abs(wrapped[i] - wrapped[i - 1]) > 180.0);

            Assert.True(jumped, "The wrapped phase never wrapped, so the ramp was too short to test.");
        }

        [Fact]
        public void GroupDelayOfAConstantPhaseSlopeIsTheSlope()
        {
            // A linear phase against frequency is a pure delay, and its group delay is that
            // delay at every point.
            const int points = 200;
            const double binWidthHz = 1000.0;
            const double delaySeconds = 250e-6;

            var complex = new float[points * 2];

            for (int i = 0; i < points; i++)
            {
                double angle = -2.0 * Math.PI * i * binWidthHz * delaySeconds;
                complex[i * 2] = (float)Math.Cos(angle);
                complex[i * 2 + 1] = (float)Math.Sin(angle);
            }

            var delay = new float[points];
            TraceFormatter.Format(
                complex, TraceFormat.GroupDelay, new AmplitudeScale(1.0, 0.0), binWidthHz, delay);

            for (int i = 1; i < points; i++)
            {
                Assert.Equal(delaySeconds, delay[i], 9);
            }
        }

        [Fact]
        public void TheIqFormatCarriesTwoValuesPerPoint()
        {
            SpectrumFrame frame = Measured();

            Assert.Equal(2, TraceFormatter.ValuesPerPoint(TraceFormat.IQ));
            Assert.Equal(1, TraceFormatter.ValuesPerPoint(TraceFormat.LogMagnitude));

            var iq = new float[frame.PointCount * 2];
            frame.Format(TraceFormat.IQ, iq);

            var real = new float[frame.PointCount];
            frame.Format(TraceFormat.Real, real);

            for (int i = 0; i < frame.PointCount; i++)
            {
                Assert.Equal(real[i], iq[i * 2]);
            }
        }

        [Fact]
        public void TheAccumulatingModesAreNotFormats()
        {
            // REQ-TRC-001a's criterion, asserted over the enumeration itself: Spectrogram,
            // Digital Persistence and Cumulative History accumulate across acquisitions, so they
            // cannot satisfy REQ-TRC-001's no-recomputation rule and must not appear here.
            string[] formats = Enum.GetNames(typeof(TraceFormat));

            Assert.DoesNotContain("Spectrogram", formats);
            Assert.DoesNotContain("DigitalPersistence", formats);
            Assert.DoesNotContain("CumulativeHistory", formats);

            string[] accumulators = Enum.GetNames(typeof(TraceAccumulator));

            Assert.Contains("Spectrogram", accumulators);
            Assert.Contains("DigitalPersistence", accumulators);
            Assert.Contains("CumulativeHistory", accumulators);
        }

        [Fact]
        public void AMismatchedDestinationIsRefused()
        {
            SpectrumFrame frame = Measured();

            Assert.Throws<ArgumentException>(
                () => frame.Format(TraceFormat.LogMagnitude, new float[frame.PointCount + 1]));
            Assert.Throws<ArgumentException>(
                () => frame.Format(TraceFormat.IQ, new float[frame.PointCount]));
        }

        private static SpectrumFrame Measured()
        {
            using (IqBlock block = Tone(1024, 12.8e6, 1e9, 128, 0.5))
            {
                return new SpectrumComputer().Compute(block);
            }
        }

        private static IqBlock Tone(
            int count, double sampleRateHz, double centreHz, int bin, double amplitude)
        {
            var metadata = new IqBlockMetadata(
                sampleCount: count,
                sampleRateHz: sampleRateHz,
                centerFrequencyHz: centreHz,
                isBaseband: false,
                fullScaleVolts: 1.0,
                referenceLevelDbm: 0.0,
                sequenceNumber: 0,
                acquiredUtc: DateTime.UtcNow,
                triggerOffsetSeconds: 0.0,
                triggerCorrectionsApplied: true,
                source: new FrontEndId("test"),
                extended: null);

            IqBlock block = IqBlock.Rent(metadata);
            Span<float> samples = block.GetSamples();

            for (int n = 0; n < count; n++)
            {
                double phase = 2.0 * Math.PI * bin * n / count;
                samples[n * 2] = (float)(amplitude * Math.Cos(phase));
                samples[n * 2 + 1] = (float)(amplitude * Math.Sin(phase));
            }

            return block;
        }

        /// <summary>An FFT provider that counts forward transforms, for the no-recomputation check.</summary>
        private sealed class CountingFftProvider : IFftProvider
        {
            private readonly ManagedFftProvider _inner = new ManagedFftProvider();

            public int ForwardCalls { get; private set; }

            public string Name => "Counting";

            public bool IsNativeAccelerated => false;

            public int SignificandBits => _inner.SignificandBits;

            public bool SupportsLength(int length) => _inner.SupportsLength(length);

            public void Forward(Span<double> interleaved)
            {
                ForwardCalls++;
                _inner.Forward(interleaved);
            }

            public void Inverse(Span<double> interleaved) => _inner.Inverse(interleaved);
        }
    }
}
