using System;
using OpenVSA.Core;
using OpenVSA.Dsp.Spectrum;
using OpenVSA.Dsp.Windowing;
using Xunit;

namespace OpenVSA.Dsp.Tests
{
    /// <summary>
    /// The spectrum computation itself: transform length, frequency axis, and the ordering of the
    /// points.
    /// </summary>
    /// <remarks>
    /// The amplitude of the result is <see cref="AmplitudeChainTests"/>' subject; what is checked
    /// here is that a feature lands where its frequency says it should. The two are separable and
    /// separately wrong: a chain can be perfectly calibrated and mirrored about centre.
    /// </remarks>
    public class SpectrumComputerTests
    {
        [Theory]
        [InlineData(1, 1)]
        [InlineData(2, 2)]
        [InlineData(3, 2)]
        [InlineData(4096, 4096)]
        [InlineData(5000, 4096)]
        [InlineData(8191, 4096)]
        [InlineData(8192, 8192)]
        public void TransformLength_IsTheLargestPowerOfTwoThatFits(int samples, int expected)
        {
            Assert.Equal(expected, SpectrumComputer.TransformLengthFor(samples));
        }

        [Fact]
        public void TransformLength_RejectsAnEmptyBlock()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => SpectrumComputer.TransformLengthFor(0));
        }

        [Fact]
        public void TheAxis_SpansTheSampleRateAboutTheCentreFrequency()
        {
            const int length = 1024;
            const double sampleRate = 12.8e6;
            const double centre = 1e9;

            using (IqBlock block = Tone(length, sampleRate, centre, 0, 0.0, isBaseband: false))
            {
                SpectrumFrame frame = new SpectrumComputer().Compute(block);

                Assert.Equal(length, frame.PointCount);
                Assert.Equal(sampleRate / length, frame.BinWidthHz, 6);
                Assert.Equal(centre - sampleRate / 2.0, frame.StartFrequencyHz, 3);
                Assert.Equal(centre + sampleRate / 2.0 - frame.BinWidthHz, frame.StopFrequencyHz, 3);
                Assert.Equal(centre, frame.CenterFrequencyHz, 3);
                Assert.Equal(sampleRate - frame.BinWidthHz, frame.SpanHz, 3);
            }
        }

        [Fact]
        public void TheDisplayedAxisIsTheAnalysisSpan_NotTheAcquiredBand()
        {
            // REQ-ACQ-001: Fs = 1.28 x Span, and the extra 28 % is the anti-alias filter's
            // transition band. Showing it would put a span on the axis that disagrees with the one
            // the user asked the front end for - and would display the roll-off as measurement.
            const int length = 8192;
            const double span = 10e6;
            const double sampleRate = span * 1.28;
            const double centre = 1e9;

            using (IqBlock block = Tone(length, sampleRate, centre, 0, 0.0, isBaseband: false))
            {
                var computer = new SpectrumComputer { TrimToAnalysisSpan = true };
                SpectrumFrame frame = computer.Compute(block);

                // 8192 / 1.28 = 6400 bins across, so 6401 points: odd, symmetric about centre, an
                // available count under REQ-DSP-022, and derived from the block that arrived rather
                // than from the span the caller happened to know about.
                Assert.Equal(6401, frame.PointCount);
                Assert.True(FrequencyPoints.IsValid(frame.PointCount));
                Assert.Equal(span, frame.SpanHz, 3);
                Assert.Equal(centre - span / 2.0, frame.StartFrequencyHz, 3);
                Assert.Equal(centre + span / 2.0, frame.StopFrequencyHz, 3);
                Assert.Equal(centre, frame.FrequencyAt(frame.PointCount / 2), 3);
            }
        }

        [Fact]
        public void ATrimmedAxisStillPutsAToneAtItsOwnFrequency()
        {
            const int length = 8192;
            const double span = 10e6;
            const double sampleRate = span * 1.28;
            const double centre = 1e9;

            foreach (int bin in new[] { -2000, -1, 0, 1, 2000 })
            {
                using (IqBlock block = Tone(length, sampleRate, centre, bin, 0.5, isBaseband: false))
                {
                    var computer = new SpectrumComputer { TrimToAnalysisSpan = true };
                    SpectrumFrame frame = computer.Compute(block);

                    Assert.Equal(
                        centre + bin * sampleRate / length,
                        frame.FrequencyAt(frame.IndexOfPeak()),
                        3);
                }
            }
        }

        [Fact]
        public void AFrontEndsOwnUsableBandwidthBeatsTheProductLaw()
        {
            // Found against a real E4406A: it digitises at 1.5x its information bandwidth, not the
            // product's 1.28x, so trimming by the law showed 11.72 MHz of axis where only 10 MHz
            // was alias-free - 1.7 MHz of the anti-alias filter's roll-off drawn as measurement
            // data. A front end that knows its usable bandwidth says so, and it wins.
            const int length = 1024;
            const double sampleRate = 15e6;
            const double usable = 10e6;

            using (IqBlock block = Tone(
                length, sampleRate, 1e9, 0, 0.0, isBaseband: false, usableBandwidthHz: usable))
            {
                var computer = new SpectrumComputer { TrimToAnalysisSpan = true };
                SpectrumFrame frame = computer.Compute(block);

                // Never wider than the instrument says is alias-free, and within a bin of it: the
                // usable bandwidth is not generally a whole number of bins, and the axis is
                // symmetric, so it lands on the nearest odd count that fits inside.
                Assert.True(
                    frame.SpanHz <= usable && usable - frame.SpanHz <= 2.0 * frame.BinWidthHz,
                    "Axis is " + frame.SpanHz + " Hz against a usable bandwidth of " + usable + " Hz.");
                Assert.True(
                    frame.SpanHz < sampleRate / AcquisitionLaw.ComplexZoomFactor,
                    "The product law would have shown a wider axis than the instrument can support.");
            }
        }

        [Fact]
        public void WithoutADeclaredBandwidthTheProductLawStillApplies()
        {
            // A front end that says nothing gets REQ-ACQ-001's relationship, which is the right
            // default for one built to the product's own acquisition law.
            using (IqBlock block = Tone(8192, 12.8e6, 1e9, 0, 0.0, isBaseband: false))
            {
                var computer = new SpectrumComputer { TrimToAnalysisSpan = true };
                SpectrumFrame frame = computer.Compute(block);

                Assert.Equal(6401, frame.PointCount);
                Assert.Equal(10e6, frame.SpanHz, 3);
            }
        }

        [Fact]
        public void ABlockTooShortForAnAvailablePointCountIsShownWhole()
        {
            // 32 points would trim to 26, which is not a count any setting could ask for
            // (REQ-DSP-022's minimum is 51). Showing what there is beats putting an impossible
            // point count on the screen.
            using (IqBlock block = Tone(32, 12.8e6, 1e9, 4, 0.5, isBaseband: false))
            {
                var computer = new SpectrumComputer { TrimToAnalysisSpan = true };
                SpectrumFrame frame = computer.Compute(block);

                Assert.Equal(32, frame.PointCount);
            }
        }

        [Fact]
        public void TheTrimmedBasebandAxisIsAlsoAnAvailablePointCount()
        {
            // 2.56 rather than 1.28, so the same transform length gives half as many points. Using
            // the complex factor here is the defect REQ-ACQ-001 names, and it would show up as an
            // axis twice as wide as the analysis span.
            using (IqBlock block = Tone(8192, 25.6e6, 0.0, 2000, 0.5, isBaseband: true))
            {
                var computer = new SpectrumComputer { TrimToAnalysisSpan = true };
                SpectrumFrame frame = computer.Compute(block);

                Assert.Equal(3201, frame.PointCount);
                Assert.True(FrequencyPoints.IsValid(frame.PointCount));
                Assert.Equal(10e6, frame.SpanHz, 3);
            }
        }

        [Theory]
        [InlineData(100)]
        [InlineData(-100)]
        [InlineData(511)]
        public void ATone_AppearsAtItsOwnFrequency_NotAtItsMirrorImage(int bin)
        {
            // The half-swap that puts the points in ascending frequency order is easy to get right
            // for positive offsets and mirrored for negative ones, and the result still looks like
            // a spectrum. Both signs are therefore checked.
            const int length = 1024;
            const double sampleRate = 12.8e6;
            const double centre = 1e9;

            using (IqBlock block = Tone(length, sampleRate, centre, bin, 0.5, isBaseband: false))
            {
                SpectrumFrame frame = new SpectrumComputer().Compute(block);

                double expected = centre + bin * sampleRate / length;
                double measured = frame.FrequencyAt(frame.IndexOfPeak());

                Assert.Equal(expected, measured, 3);
            }
        }

        [Fact]
        public void TheBasebandPath_IsOneSidedFromZeroHertz()
        {
            const int length = 1024;
            const double sampleRate = 25.6e6;

            using (IqBlock block = Tone(length, sampleRate, 0.0, length / 4, 0.5, isBaseband: true))
            {
                SpectrumFrame frame = new SpectrumComputer().Compute(block);

                Assert.True(frame.IsBaseband);
                Assert.Equal(length / 2 + 1, frame.PointCount);
                Assert.Equal(0.0, frame.StartFrequencyHz, 6);
                Assert.Equal(sampleRate / 2.0, frame.StopFrequencyHz, 3);
                Assert.Equal(sampleRate / 4.0, frame.FrequencyAt(frame.IndexOfPeak()), 3);
            }
        }

        [Fact]
        public void ResolutionBandwidth_IsTheWindowsNoiseBandwidthTimesTheBinWidth()
        {
            // REQ-DSP-020's coupling, as far as it exists at this stage: RBW is not the bin width,
            // and a flat top's is nearly four times it.
            const int length = 1024;
            const double sampleRate = 12.8e6;

            using (IqBlock block = Tone(length, sampleRate, 1e9, 0, 0.0, isBaseband: false))
            {
                var computer = new SpectrumComputer(WindowType.FlatTop, null, null);
                SpectrumFrame frame = computer.Compute(block);

                Window window = Window.Get(WindowType.FlatTop, length);

                Assert.Equal(window.Enbw, frame.EquivalentNoiseBandwidthBins, 6);
                Assert.Equal(window.Enbw * sampleRate / length, frame.ResolutionBandwidthHz, 3);
            }
        }

        [Fact]
        public void TheFrameCarriesTheBlocksProvenance()
        {
            using (IqBlock block = Tone(256, 12.8e6, 1e9, 10, 0.5, isBaseband: false))
            {
                SpectrumFrame frame = new SpectrumComputer().Compute(block);

                Assert.Equal(block.SequenceNumber, frame.SequenceNumber);
                Assert.Equal(block.AcquiredUtc, frame.AcquiredUtc);
                Assert.Equal(block.Source, frame.Source);
                Assert.Equal(block.ReferenceLevelDbm, frame.ReferenceLevelDbm);
                Assert.Equal(WindowType.FlatTop, frame.Window);
            }
        }

        [Fact]
        public void ABlockThatIsNotAPowerOfTwoLong_IsTruncatedRatherThanPadded()
        {
            // Truncation is visible in the bin width: 3000 samples at 12.8 MHz analysed as 2048
            // gives 6.25 kHz bins, where padding to 4096 would give 3.125 kHz and imply a
            // resolution the acquisition did not have.
            using (IqBlock block = Tone(3000, 12.8e6, 1e9, 100, 0.5, isBaseband: false))
            {
                SpectrumFrame frame = new SpectrumComputer().Compute(block);

                Assert.Equal(2048, frame.PointCount);
                Assert.Equal(12.8e6 / 2048, frame.BinWidthHz, 6);
            }
        }

        [Fact]
        public void AFrameRefusesAnIndexOutsideItself()
        {
            using (IqBlock block = Tone(256, 12.8e6, 1e9, 10, 0.5, isBaseband: false))
            {
                SpectrumFrame frame = new SpectrumComputer().Compute(block);

                Assert.Throws<ArgumentOutOfRangeException>(() => frame.FrequencyAt(-1));
                Assert.Throws<ArgumentOutOfRangeException>(() => frame.FrequencyAt(frame.PointCount));
            }
        }

        private static IqBlock Tone(
            int count,
            double sampleRateHz,
            double centerFrequencyHz,
            int bin,
            double amplitude,
            bool isBaseband,
            double usableBandwidthHz = 0.0)
        {
            var extended = usableBandwidthHz > 0.0
                ? new System.Collections.Generic.Dictionary<string, object>
                    { { IqBlockMetadata.UsableBandwidthKey, usableBandwidthHz } }
                : null;

            var metadata = new IqBlockMetadata(
                sampleCount: count,
                sampleRateHz: sampleRateHz,
                centerFrequencyHz: centerFrequencyHz,
                isBaseband: isBaseband,
                fullScaleVolts: 1.0,
                referenceLevelDbm: 10.0,
                sequenceNumber: 7,
                acquiredUtc: DateTime.UtcNow,
                triggerOffsetSeconds: 0.0,
                triggerCorrectionsApplied: true,
                source: new FrontEndId("test"),
                extended: extended);

            IqBlock block = IqBlock.Rent(metadata);
            Span<float> samples = block.GetSamples();
            int length = SpectrumComputer.TransformLengthFor(count);

            for (int n = 0; n < count; n++)
            {
                double phase = 2.0 * Math.PI * bin * n / length;
                samples[n * 2] = (float)(amplitude * Math.Cos(phase));
                samples[n * 2 + 1] = isBaseband ? 0.0f : (float)(amplitude * Math.Sin(phase));
            }

            return block;
        }
    }
}
