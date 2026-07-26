using System;
using System.Collections.Generic;
using OpenVSA.Core;
using OpenVSA.Dsp.Spectrum;
using OpenVSA.Dsp.Windowing;
using OpenVSA.Dsp.Zoom;
using OpenVSA.Measurement;
using Xunit;

namespace OpenVSA.Measurement.Tests
{
    /// <summary>
    /// <c>REQ-REC-004</c>'s 1/256 bound and <c>REQ-DSP-023</c>'s zoom controls.
    /// </summary>
    /// <remarks>
    /// The worked numbers are the requirement's own: a 10 MHz capture, a 1 kHz feature 4 MHz from
    /// centre, and a 39.0625 kHz floor.
    /// </remarks>
    public class ZoomControlTests
    {
        private const double SourceCenterHz = 1e9;
        private const double SourceSpanHz = 10e6;

        [Fact]
        public void ANewControlIsAtFullSpan()
        {
            var zoom = new ZoomControl(SourceCenterHz, SourceSpanHz);

            Assert.True(zoom.IsFullSpan);
            Assert.Equal(SourceCenterHz, zoom.CenterFrequencyHz, 6);
            Assert.Equal(SourceSpanHz, zoom.SpanHz, 6);
            Assert.Equal(1.0, zoom.ZoomRatio, 9);
            Assert.Equal(995e6, zoom.StartHz, 6);
            Assert.Equal(1005e6, zoom.StopHz, 6);
        }

        [Fact]
        public void TheFloorIsATwoHundredAndFiftySixthOfTheSourceSpan()
        {
            var zoom = new ZoomControl(SourceCenterHz, SourceSpanHz);

            // The requirement's own figure for a 10 MHz capture.
            Assert.Equal(39062.5, zoom.NarrowestSpanHz, 6);
            Assert.Equal(256, ZoomControl.MaximumZoomRatio);
        }

        [Fact]
        public void TheRequirementsWorkedExampleResolves()
        {
            // A 1 kHz-wide feature 4 MHz from centre, reached by zooming to the floor without
            // re-acquiring. The feature is 25 times narrower than the span it now sits in, where
            // at full span it was one part in ten thousand.
            var zoom = new ZoomControl(SourceCenterHz, SourceSpanHz);

            zoom.SetCenterFrequency(SourceCenterHz + 4e6);
            zoom.SetSpan(zoom.NarrowestSpanHz);

            Assert.Equal(SourceCenterHz + 4e6, zoom.CenterFrequencyHz, 6);
            Assert.Equal(39062.5, zoom.SpanHz, 6);
            Assert.Equal(256.0, zoom.ZoomRatio, 6);
            Assert.True(zoom.SpanHz / 1e3 < 40.0);
        }

        [Fact]
        public void CentreAndSpanArriveAtTheSamePlaceInEitherOrder()
        {
            // At full span the analysis cannot move at all, so a centre set before the span has
            // nowhere to go. Remembering what was asked for is what stops the pair of settings
            // working in one order and silently failing in the other.
            var centreFirst = new ZoomControl(SourceCenterHz, SourceSpanHz);
            var spanFirst = new ZoomControl(SourceCenterHz, SourceSpanHz);

            centreFirst.SetCenterFrequency(SourceCenterHz + 4e6);
            centreFirst.SetSpan(200e3);

            spanFirst.SetSpan(200e3);
            spanFirst.SetCenterFrequency(SourceCenterHz + 4e6);

            Assert.Equal(spanFirst.CenterFrequencyHz, centreFirst.CenterFrequencyHz, 6);
            Assert.Equal(SourceCenterHz + 4e6, centreFirst.CenterFrequencyHz, 6);
        }

        [Fact]
        public void ASpanBelowTheFloorIsRefusedWithTheBoundNamed()
        {
            var zoom = new ZoomControl(SourceCenterHz, SourceSpanHz);

            ArgumentOutOfRangeException error = Assert.Throws<ArgumentOutOfRangeException>(
                () => zoom.SetSpan(zoom.NarrowestSpanHz / 2.0));

            Assert.Contains("256", error.Message);
            Assert.Contains("39.0625 kHz", error.Message);

            // And nothing moved.
            Assert.True(zoom.IsFullSpan);
        }

        [Fact]
        public void TheFloorItselfIsAccepted()
        {
            // Rejecting the number the interface just displayed, because it is a part in 10^12
            // below the bound, would be the least explicable rejection in the product.
            var zoom = new ZoomControl(SourceCenterHz, SourceSpanHz);

            zoom.SetSpan(zoom.NarrowestSpanHz);

            Assert.Equal(39062.5, zoom.SpanHz, 6);

            var again = new ZoomControl(SourceCenterHz, SourceSpanHz);

            again.SetSpan(39062.5 * (1.0 - 1e-12));

            Assert.True(again.ZoomRatio > 255.9);
        }

        [Fact]
        public void ASpanWiderThanWasCapturedIsRefusedAndSaysWhy()
        {
            var zoom = new ZoomControl(SourceCenterHz, SourceSpanHz);

            ArgumentOutOfRangeException error = Assert.Throws<ArgumentOutOfRangeException>(
                () => zoom.SetSpan(SourceSpanHz * 2.0));

            Assert.Contains("Full Span", error.Message);
            Assert.Contains("acquiring again", error.Message);
        }

        [Fact]
        public void ZoomingHoldsTheCentre()
        {
            var zoom = new ZoomControl(SourceCenterHz, SourceSpanHz)
            {
                SpanChange = SpanChangeBehaviour.Zoom,
            };

            zoom.SetCenterFrequency(SourceCenterHz + 3e6);
            zoom.SetSpan(1e6);

            Assert.Equal(SourceCenterHz + 3e6, zoom.CenterFrequencyHz, 6);
        }

        [Fact]
        public void HoldingTheStartMovesTheCentreInstead()
        {
            // A swept analyser's behaviour. For a baseband measurement the start is 0 Hz, which is
            // the case REQ-DSP-023 names.
            var zoom = new ZoomControl(5e6, 10e6)
            {
                SpanChange = SpanChangeBehaviour.HoldStartFrequency,
            };

            Assert.Equal(0.0, zoom.StartHz, 6);

            zoom.SetSpan(1e6);

            Assert.Equal(0.0, zoom.StartHz, 6);
            Assert.Equal(500e3, zoom.CenterFrequencyHz, 6);
        }

        [Fact]
        public void TheTwoBehavioursDisagreeOnTheSameRequest()
        {
            // If they ever agree on a span change from an off-centre position, one of them is not
            // doing anything and the setting is decoration.
            var zooming = new ZoomControl(5e6, 10e6) { SpanChange = SpanChangeBehaviour.Zoom };
            var holding = new ZoomControl(5e6, 10e6)
            {
                SpanChange = SpanChangeBehaviour.HoldStartFrequency,
            };

            zooming.SetSpan(2e6);
            holding.SetSpan(2e6);

            Assert.Equal(5e6, zooming.CenterFrequencyHz, 6);
            Assert.Equal(1e6, holding.CenterFrequencyHz, 6);
        }

        [Fact]
        public void ACentreTooCloseToTheEdgeIsMovedInRatherThanRefused()
        {
            // There is no data past the edge, the nearest position that fits is unambiguous, and
            // the move is written on the frequency axis. Unlike a span past the zoom bound, which
            // is refused, because a zoom that silently stopped would look like one that worked.
            var zoom = new ZoomControl(SourceCenterHz, SourceSpanHz);

            zoom.SetSpan(1e6);

            double settled = zoom.SetCenterFrequency(SourceCenterHz + 100e6);

            Assert.Equal(zoom.SourceStopHz - 500e3, settled, 6);
            Assert.Equal(zoom.SourceStopHz, zoom.StopHz, 6);
            Assert.Equal(1e6, zoom.SpanHz, 6);
        }

        [Fact]
        public void WideningTheSpanNearAnEdgeSlidesTheCentreBackIn()
        {
            var zoom = new ZoomControl(SourceCenterHz, SourceSpanHz);

            zoom.SetSpan(100e3);
            zoom.SetCenterFrequency(SourceCenterHz + 5e6);

            Assert.Equal(zoom.SourceStopHz - 50e3, zoom.CenterFrequencyHz, 6);

            zoom.SetSpan(2e6);

            Assert.Equal(zoom.SourceStopHz - 1e6, zoom.CenterFrequencyHz, 6);
            Assert.True(zoom.StopHz <= zoom.SourceStopHz + 1e-6);
            Assert.True(zoom.StartHz >= zoom.SourceStartHz - 1e-6);
        }

        [Fact]
        public void SelectingAnAreaTakesItsEdgesInEitherOrder()
        {
            // A drag has no preferred direction.
            var forwards = new ZoomControl(SourceCenterHz, SourceSpanHz);
            var backwards = new ZoomControl(SourceCenterHz, SourceSpanHz);

            forwards.SelectArea(1.001e9, 1.002e9);
            backwards.SelectArea(1.002e9, 1.001e9);

            Assert.Equal(forwards.SpanHz, backwards.SpanHz, 6);
            Assert.Equal(forwards.CenterFrequencyHz, backwards.CenterFrequencyHz, 6);
            Assert.Equal(1e6, forwards.SpanHz, 6);
            Assert.Equal(1.0015e9, forwards.CenterFrequencyHz, 6);
        }

        [Fact]
        public void ADragOffTheEndOfTheTraceKeepsThePartThatLandsOnData()
        {
            var zoom = new ZoomControl(SourceCenterHz, SourceSpanHz);

            zoom.SelectArea(1.004e9, 1.020e9);

            Assert.Equal(1e6, zoom.SpanHz, 6);
            Assert.Equal(1.0045e9, zoom.CenterFrequencyHz, 6);
        }

        [Fact]
        public void ADragEntirelyOffTheTraceIsRefused()
        {
            var zoom = new ZoomControl(SourceCenterHz, SourceSpanHz);

            ArgumentOutOfRangeException error = Assert.Throws<ArgumentOutOfRangeException>(
                () => zoom.SelectArea(1.1e9, 1.2e9));

            Assert.Contains("no part of the captured band", error.Message);
        }

        [Fact]
        public void ADragTooNarrowToAllowIsRefusedWithTheSameBound()
        {
            var zoom = new ZoomControl(SourceCenterHz, SourceSpanHz);

            ArgumentOutOfRangeException error = Assert.Throws<ArgumentOutOfRangeException>(
                () => zoom.SelectArea(1.000000e9, 1.000001e9));

            Assert.Contains("256", error.Message);
            Assert.Contains("39.0625 kHz", error.Message);
        }

        [Fact]
        public void FullSpanUndoesEverything()
        {
            var zoom = new ZoomControl(SourceCenterHz, SourceSpanHz);

            zoom.SelectArea(1.0039e9, 1.0041e9);
            Assert.False(zoom.IsFullSpan);

            zoom.FullSpan();

            Assert.True(zoom.IsFullSpan);
            Assert.Equal(SourceCenterHz, zoom.CenterFrequencyHz, 6);
            Assert.Equal(SourceSpanHz, zoom.SpanHz, 6);
        }

        [Fact]
        public void ADownconverterIsBuiltForTheZoomInForce()
        {
            const double rateHz = 15e6;

            var zoom = new ZoomControl(SourceCenterHz, SourceSpanHz);

            zoom.SetCenterFrequency(SourceCenterHz + 4e6);
            zoom.SetSpan(200e3);

            DigitalDownconverter ddc;

            Assert.True(zoom.TryCreateDownconverter(rateHz, out ddc));
            Assert.Equal(4e6, ddc.ShiftHz, 6);
            Assert.True(ddc.UsableBandwidthHz >= zoom.SpanHz);
            Assert.True(ddc.Decimation >= 2);
        }

        [Fact]
        public void NoDownconverterIsNeededWhenTheBlocksAlreadyDeliverTheSpan()
        {
            // A front end digitising at only a little above the span cannot be decimated to it, and
            // asking for one anyway would be asking for an all-pass filter.
            var zoom = new ZoomControl(SourceCenterHz, SourceSpanHz);

            DigitalDownconverter ddc;

            Assert.False(zoom.TryCreateDownconverter(11e6, out ddc));
            Assert.Null(ddc);
        }

        [Fact]
        public void TheAnnotationSaysWhetherThisIsAZoom()
        {
            var zoom = new ZoomControl(SourceCenterHz, SourceSpanHz);

            Assert.Contains("Full span", zoom.Annotation());

            zoom.SetSpan(SourceSpanHz / 64.0);

            Assert.Contains("64", zoom.Annotation());
            Assert.Contains("Zoom", zoom.Annotation());
        }

        [Fact]
        public void AFeatureInvisibleAtFullSpanIsResolvedByZoomingIntoTheSameBlock()
        {
            // REQ-DSP-023's acceptance criterion, end to end: a 1 kHz-wide feature 4 MHz from
            // centre in a 10 MHz span, resolved by zooming to the 39.0625 kHz floor without
            // re-acquiring. The feature is two equal tones 1 kHz apart, because "resolved" then
            // means something a test can count rather than something a reader has to judge.
            //
            // Both analyses take the same 2048-point transform, which is the trade zoom actually
            // makes: for a given transform size, decimating by M buys M times the time record, and
            // RBW comes from time. At full rate 2048 points span 137 us and give a 28 kHz RBW -
            // 28 times too coarse. Decimated by 307 the same 2048 points span 42 ms and give 91 Hz.
            const double rateHz = 15e6;
            const double featureHz = 1e3;
            const int transform = 2048;

            var zoom = new ZoomControl(SourceCenterHz, SourceSpanHz);

            zoom.SetCenterFrequency(SourceCenterHz + 4e6);
            zoom.SetSpan(zoom.NarrowestSpanHz);

            DigitalDownconverter ddc;
            Assert.True(zoom.TryCreateDownconverter(rateHz, out ddc));

            int samples = ddc.MinimumInputSamples + (transform - 1) * ddc.Decimation;

            using (IqBlock block = TwoTones(
                samples, rateHz, 4e6 - featureHz / 2.0, 4e6 + featureHz / 2.0))
            {
                var computer = new SpectrumComputer(WindowType.FlatTop, null, null);

                // At full span the two tones are one peak: the transform that fits this many
                // points at the full rate cannot resolve a kilohertz.
                using (IqBlock head = FirstSamples(block, transform))
                {
                    SpectrumFrame wide = computer.Compute(head);

                    Assert.True(
                        wide.ResolutionBandwidthHz > 10.0 * featureHz,
                        "the full-span RBW was " + wide.ResolutionBandwidthHz +
                        " Hz, fine enough to resolve the feature without zooming.");
                    Assert.Single(NearPeaks(wide, 3.0));
                }

                using (IqBlock zoomed = ddc.Downconvert(block))
                {
                    Assert.True(zoomed.SampleCount >= transform);

                    SpectrumFrame narrow = computer.Compute(zoomed);

                    Assert.True(
                        narrow.ResolutionBandwidthHz < featureHz / 2.0,
                        "the zoomed RBW was " + narrow.ResolutionBandwidthHz + " Hz.");

                    IReadOnlyList<int> peaks = NearPeaks(narrow, 3.0);

                    Assert.Equal(2, peaks.Count);

                    // Within two bins, which is what a peak read off a display is worth.
                    double tolerance = 2.0 * narrow.BinWidthHz;
                    double separation =
                        Math.Abs(narrow.FrequencyAt(peaks[1]) - narrow.FrequencyAt(peaks[0]));

                    Assert.True(
                        Math.Abs(separation - featureHz) <= tolerance,
                        "the two peaks were " + separation + " Hz apart, not " + featureHz + ".");

                    // And they are where they were put, on the original frequency axis - the zoom
                    // moved the samples to baseband and the axis back again.
                    Assert.True(
                        Math.Abs(narrow.FrequencyAt(peaks[0]) -
                                 (SourceCenterHz + 4e6 - featureHz / 2.0)) <= tolerance,
                        "the lower tone read " + narrow.FrequencyAt(peaks[0]) + " Hz.");
                    Assert.True(
                        Math.Abs(narrow.FrequencyAt(peaks[1]) -
                                 (SourceCenterHz + 4e6 + featureHz / 2.0)) <= tolerance,
                        "the upper tone read " + narrow.FrequencyAt(peaks[1]) + " Hz.");
                }
            }
        }

        /// <summary>Local maxima within a few dB of the highest point.</summary>
        private static IReadOnlyList<int> NearPeaks(SpectrumFrame frame, double windowDb)
        {
            double highest = double.NegativeInfinity;

            for (int i = 0; i < frame.PointCount; i++)
            {
                highest = Math.Max(highest, frame.LevelsDbm[i]);
            }

            var peaks = new List<int>();

            for (int i = 1; i < frame.PointCount - 1; i++)
            {
                if (frame.LevelsDbm[i] >= highest - windowDb &&
                    frame.LevelsDbm[i] > frame.LevelsDbm[i - 1] &&
                    frame.LevelsDbm[i] >= frame.LevelsDbm[i + 1])
                {
                    peaks.Add(i);
                }
            }

            return peaks;
        }

        private static IqBlock TwoTones(
            int samples, double rateHz, double firstOffsetHz, double secondOffsetHz)
        {
            IqBlock block = IqBlock.Rent(new IqBlockMetadata(
                sampleCount: samples,
                sampleRateHz: rateHz,
                centerFrequencyHz: SourceCenterHz,
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
            double first = firstOffsetHz / rateHz;
            double second = secondOffsetHz / rateHz;

            for (int n = 0; n < samples; n++)
            {
                double a = 2.0 * Math.PI * first * n;
                double b = 2.0 * Math.PI * second * n;

                data[n * 2] = (float)(0.25 * (Math.Cos(a) + Math.Cos(b)));
                data[n * 2 + 1] = (float)(0.25 * (Math.Sin(a) + Math.Sin(b)));
            }

            return block;
        }

        private static IqBlock FirstSamples(IqBlock block, int samples)
        {
            IqBlock head = IqBlock.Rent(new IqBlockMetadata(
                sampleCount: samples,
                sampleRateHz: block.SampleRateHz,
                centerFrequencyHz: block.CenterFrequencyHz,
                isBaseband: block.IsBaseband,
                fullScaleVolts: block.FullScaleVolts,
                referenceLevelDbm: block.ReferenceLevelDbm,
                sequenceNumber: block.SequenceNumber,
                acquiredUtc: block.AcquiredUtc,
                triggerOffsetSeconds: block.TriggerOffsetSeconds,
                triggerCorrectionsApplied: block.TriggerCorrectionsApplied,
                source: block.Source,
                extended: block.Extended));

            block.GetSamples().Slice(0, samples * 2).CopyTo(head.GetSamples());

            return head;
        }

        [Theory]
        [InlineData(0.0)]
        [InlineData(-1e6)]
        [InlineData(double.NaN)]
        [InlineData(double.PositiveInfinity)]
        public void ASourceSpanThatIsNotPositiveAndFiniteIsRefused(double spanHz)
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => new ZoomControl(SourceCenterHz, spanHz));
        }

        [Theory]
        [InlineData(double.NaN)]
        [InlineData(double.PositiveInfinity)]
        public void ACentreThatIsNotFiniteIsRefused(double hz)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new ZoomControl(hz, SourceSpanHz));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => new ZoomControl(SourceCenterHz, SourceSpanHz).SetCenterFrequency(hz));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => new ZoomControl(SourceCenterHz, SourceSpanHz).SelectArea(hz, 1e9));
        }

        [Theory]
        [InlineData(0.0)]
        [InlineData(-1.0)]
        [InlineData(double.NaN)]
        public void ARateThatIsNotPositiveIsRefused(double rateHz)
        {
            DigitalDownconverter ddc;

            Assert.Throws<ArgumentOutOfRangeException>(
                () => new ZoomControl(SourceCenterHz, SourceSpanHz)
                    .TryCreateDownconverter(rateHz, out ddc));
        }
    }
}
