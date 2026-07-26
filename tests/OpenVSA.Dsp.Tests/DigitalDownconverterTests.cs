using System;
using System.Collections.Generic;
using OpenVSA.Core;
using OpenVSA.Dsp.Fft;
using OpenVSA.Dsp.Zoom;
using Xunit;

namespace OpenVSA.Dsp.Tests
{
    /// <summary>
    /// <c>REQ-DSP-023a</c>'s acceptance criterion, measured: "a full-scale tone swept across the
    /// span shows amplitude variation within the ripple figure and no alias or spur above
    /// −100 dBc at any decimation factor".
    /// </summary>
    /// <remarks>
    /// <para>
    /// The sweeps below exploit a property of the downconverter that is worth stating, because it
    /// is what makes these measurements exact rather than statistical: a single complex tone in
    /// gives a single complex tone out whose <em>modulus is constant over the whole record</em>,
    /// <c>|y[j]| = |H(f − ν)|</c>. There is no window, no transform and no leakage between the
    /// input and the number being asserted on, so a 0.02 dB flatness limit is being tested at
    /// 0.02 dB and not at 0.02 dB plus whatever the estimator contributed.
    /// </para>
    /// <para>
    /// The rates and offsets are deliberately unhelpful — 15 MS/s, a shift of 2.317 MHz — so that
    /// no index arithmetic can come out right by landing on a round number.
    /// </para>
    /// </remarks>
    public class DigitalDownconverterTests
    {
        private const double InputRateHz = 15e6;
        private const double ShiftHz = 2.317e6;

        public static IEnumerable<object[]> DecimationFactors =>
            new[]
            {
                new object[] { 2 },
                new object[] { 3 },
                new object[] { 5 },
                new object[] { 16 },
                new object[] { 64 },
                new object[] { 256 },
            };

        [Theory]
        [MemberData(nameof(DecimationFactors))]
        public void TheSpanIsFlatWithinTheRippleAndFlatnessTargets(int decimation)
        {
            var ddc = DigitalDownconverter.ForDecimation(InputRateHz, ShiftHz, decimation);

            const int points = 121;
            double worstFullSpan = double.NegativeInfinity;
            double bestFullSpan = double.PositiveInfinity;
            double worstCentral = 0.0;

            for (int i = 0; i <= points; i++)
            {
                double offset = ddc.UsableBandwidthHz * (i / (double)points - 0.5);
                double db = 20.0 * Math.Log10(ResponseAt(ddc, ShiftHz + offset));

                worstFullSpan = Math.Max(worstFullSpan, db);
                bestFullSpan = Math.Min(bestFullSpan, db);

                if (Math.Abs(offset) <=
                    DdcDesignTargets.FlatnessSpanFraction * ddc.UsableBandwidthHz / 2.0)
                {
                    worstCentral = Math.Max(worstCentral, Math.Abs(db));
                }
            }

            double rippleDb = worstFullSpan - bestFullSpan;

            Assert.True(
                rippleDb <= DdcDesignTargets.PassbandRippleDb,
                "decimation " + decimation + ": peak-to-peak ripple " + rippleDb.ToString("E3") +
                " dB against a target of " + DdcDesignTargets.PassbandRippleDb + " dB.");
            Assert.True(
                worstCentral <= DdcDesignTargets.PassbandFlatnessDb,
                "decimation " + decimation + ": flatness " + worstCentral.ToString("E3") +
                " dB over the central " + DdcDesignTargets.FlatnessSpanFraction +
                " against a target of " + DdcDesignTargets.PassbandFlatnessDb + " dB.");
        }

        [Theory]
        [MemberData(nameof(DecimationFactors))]
        public void NothingOutsideTheGuardBandGetsBackInAboveTheRejectionTarget(int decimation)
        {
            // The alias half of the criterion. Everything that decimation folds into the passband
            // arrives from at or beyond the stopband edge, so sweeping the whole input band and
            // skipping only the transition region tests exactly the frequencies that can alias.
            var ddc = DigitalDownconverter.ForDecimation(InputRateHz, ShiftHz, decimation);

            const int points = 601;
            double worstDb = double.NegativeInfinity;
            double worstAtHz = 0.0;

            // The two stopband edges are where the rejection is weakest, and a uniform grid is
            // most unlikely to land on them. Test them explicitly, then sweep the rest.
            for (int i = -2; i <= points; i++)
            {
                double hz = i < 0
                    ? ShiftHz + (i == -1 ? ddc.StopbandEdgeHz : -ddc.StopbandEdgeHz)
                    : InputRateHz * (i / (double)points - 0.5);

                if (i >= 0 && WrappedDistanceHz(hz - ShiftHz) < ddc.StopbandEdgeHz)
                {
                    continue;
                }

                double db = 20.0 * Math.Log10(ResponseAt(ddc, hz));

                if (db > worstDb)
                {
                    worstDb = db;
                    worstAtHz = hz;
                }
            }

            Assert.True(worstDb > double.NegativeInfinity, "the sweep tested nothing.");
            Assert.True(
                worstDb <= -DdcDesignTargets.StopbandRejectionDb,
                "decimation " + decimation + ": worst alias " + worstDb.ToString("F2") +
                " dBc at " + worstAtHz.ToString("F0") + " Hz, against a target of −" +
                DdcDesignTargets.StopbandRejectionDb + " dBc.");
        }

        [Theory]
        [InlineData(2)]
        [InlineData(4)]
        [InlineData(7)]
        [InlineData(32)]
        public void NoSpurRisesAboveTheSfdrTarget(int decimation)
        {
            // The spur half. A tone placed so that its downconverted image lands exactly on an
            // output bin makes a rectangular-window transform leakage-free, so every bin but that
            // one holds nothing except what the downconverter itself put there. Any other choice
            // of tone measures the window's sidelobes and calls them spurs.
            const int transform = 4096;
            const int bin = 613;

            var ddc = DigitalDownconverter.ForDecimation(InputRateHz, ShiftHz, decimation);

            double toneHz = ShiftHz + bin / (double)transform * ddc.OutputRateHz;
            float[] output = Downconvert(ddc, toneHz, transform);

            var spectrum = new double[transform * 2];

            for (int j = 0; j < transform; j++)
            {
                spectrum[j * 2] = output[j * 2];
                spectrum[j * 2 + 1] = output[j * 2 + 1];
            }

            new ManagedFftProvider().Forward(spectrum);

            double carrier = 0.0;
            double spur = 0.0;
            int spurBin = -1;

            for (int k = 0; k < transform; k++)
            {
                double power = spectrum[k * 2] * spectrum[k * 2] +
                               spectrum[k * 2 + 1] * spectrum[k * 2 + 1];

                if (k == bin)
                {
                    carrier = power;
                }
                else if (power > spur)
                {
                    spur = power;
                    spurBin = k;
                }
            }

            Assert.True(carrier > 0.0, "the carrier did not land in bin " + bin + ".");

            double sfdrDb = 10.0 * Math.Log10(carrier / spur);

            Assert.True(
                sfdrDb >= DdcDesignTargets.SpuriousFreeDynamicRangeDbc,
                "decimation " + decimation + ": SFDR " + sfdrDb.ToString("F1") +
                " dBc, worst spur in bin " + spurBin + ", against a target of " +
                DdcDesignTargets.SpuriousFreeDynamicRangeDbc + " dBc.");
        }

        [Theory]
        [MemberData(nameof(DecimationFactors))]
        public void ATuneToTheToneGivesExactlyUnityAndZeroPhase(int decimation)
        {
            // Unity gain at the tuned frequency is by construction - the shifted taps sum to the
            // low-pass's DC gain there - and zero phase is what says the mixer rotation is
            // referenced to the same instant the group delay was removed at. Reference it to the
            // filter's own output instead and this comes out as a unit vector at some other angle,
            // which no spectrum measurement would ever notice and every demodulation would.
            var ddc = DigitalDownconverter.ForDecimation(InputRateHz, ShiftHz, decimation);
            float[] output = Downconvert(ddc, ShiftHz, 8);

            for (int j = 0; j < 8; j++)
            {
                Assert.Equal(1.0, output[j * 2], 5);
                Assert.Equal(0.0, output[j * 2 + 1], 5);
            }
        }

        [Fact]
        public void PhaseAdvancesFromTheInputSampleEachOutputStandsFor()
        {
            // Output j is the downconverted value of input sample AlignmentOffsetSamples + j*M, so
            // an offset tone must arrive with the phase it had at that input sample - including
            // the offset, not just the step between outputs.
            var ddc = DigitalDownconverter.ForDecimation(InputRateHz, ShiftHz, 16);

            double offsetHz = 0.31 * ddc.UsableBandwidthHz;
            double cyclesPerInputSample = offsetHz / InputRateHz;

            float[] output = Downconvert(ddc, ShiftHz + offsetHz, 12);

            for (int j = 0; j < 12; j++)
            {
                long instant = ddc.AlignmentOffsetSamples + (long)j * ddc.Decimation;
                double expected = 2.0 * Math.PI * cyclesPerInputSample * instant;
                double actual = Math.Atan2(output[j * 2 + 1], output[j * 2]);

                Assert.Equal(0.0, WrappedRadians(actual - expected), 5);
            }
        }

        [Fact]
        public void ForSpanNeverDeliversLessSpanThanAskedFor()
        {
            foreach (double spanHz in new[] { 5.9e6, 1.0e6, 137e3, 23.4e3 })
            {
                var ddc = DigitalDownconverter.ForSpan(InputRateHz, ShiftHz, spanHz);

                Assert.True(
                    ddc.UsableBandwidthHz >= spanHz,
                    "asked for " + spanHz + " Hz, got " + ddc.UsableBandwidthHz + " Hz.");

                // And not wastefully more: one more step of decimation would have fallen short.
                var next = DigitalDownconverter.ForDecimation(
                    InputRateHz, ShiftHz, ddc.Decimation + 1);

                Assert.True(next.UsableBandwidthHz < spanHz);
            }
        }

        [Fact]
        public void TheDesignedEdgesAreWhereTheGeometrySaysTheyAre()
        {
            var ddc = DigitalDownconverter.ForDecimation(InputRateHz, ShiftHz, 16);

            Assert.Equal(InputRateHz / 16.0, ddc.OutputRateHz, 6);
            Assert.Equal(0.8 * InputRateHz / 16.0, ddc.UsableBandwidthHz, 6);

            // The -6 dB cutoff sits at half the output rate whatever the usable fraction is, so
            // the passband and stopband edges are equidistant from it.
            double cutoff = ddc.OutputRateHz / 2.0;

            Assert.Equal(cutoff, (ddc.PassbandEdgeHz + ddc.StopbandEdgeHz) / 2.0, 6);
        }

        [Fact]
        public void ABlockCarriesItsNewRateCentreAndBandwidth()
        {
            var when = new DateTime(2026, 7, 26, 11, 22, 33, DateTimeKind.Utc);
            var ddc = DigitalDownconverter.ForDecimation(InputRateHz, ShiftHz, 8);

            using (IqBlock source = Block(20011, when, 1.7e-3))
            using (IqBlock zoomed = ddc.Downconvert(source))
            {
                Assert.Equal(ddc.OutputCountFor(20011), zoomed.SampleCount);
                Assert.Equal(InputRateHz / 8.0, zoomed.SampleRateHz, 6);
                Assert.Equal(1e9 + ShiftHz, zoomed.CenterFrequencyHz, 6);
                Assert.False(zoomed.IsBaseband);
                Assert.Equal(source.FullScaleVolts, zoomed.FullScaleVolts, 12);
                Assert.Equal(source.ReferenceLevelDbm, zoomed.ReferenceLevelDbm, 12);
                Assert.Equal(source.SequenceNumber, zoomed.SequenceNumber);
                Assert.Equal(source.Source, zoomed.Source);
                Assert.True(source.TriggerCorrectionsApplied == zoomed.TriggerCorrectionsApplied);

                // The front end's alias-free bandwidth is no longer the block's: this filter's is.
                Assert.Equal(
                    ddc.UsableBandwidthHz,
                    (double)zoomed.Extended[IqBlockMetadata.UsableBandwidthKey],
                    3);
                Assert.Equal("keep-me", zoomed.Extended["unrelated"]);
            }
        }

        [Fact]
        public void TheTriggerStillHappenedWhenItHappened()
        {
            // The record starts later, so its timestamp advances and its trigger offset shrinks by
            // the same amount. Move one without the other and the zoomed record reports the
            // trigger at a different instant from the record it came from.
            var when = new DateTime(2026, 7, 26, 11, 22, 33, DateTimeKind.Utc);
            var ddc = DigitalDownconverter.ForDecimation(InputRateHz, ShiftHz, 8);

            using (IqBlock source = Block(20011, when, 1.7e-3))
            using (IqBlock zoomed = ddc.Downconvert(source))
            {
                double advance = ddc.AlignmentOffsetSamples / InputRateHz;

                DateTime before = BlockTimeline.TriggerInstant(
                    source.AcquiredUtc, source.TriggerOffsetSeconds);
                DateTime after = BlockTimeline.TriggerInstant(
                    zoomed.AcquiredUtc, zoomed.TriggerOffsetSeconds);

                Assert.True(advance > 0.0);
                Assert.True(zoomed.AcquiredUtc > source.AcquiredUtc);

                // To the tick, which is what the two halves are rounded at.
                Assert.True(
                    Math.Abs((after - before).Ticks) <= 1,
                    "the trigger moved by " + (after - before).Ticks + " ticks.");
                Assert.Equal(
                    source.TriggerOffsetSeconds - advance, zoomed.TriggerOffsetSeconds, 9);
            }
        }

        [Fact]
        public void ARecordTooShortToFilterIsRefusedWithTheLengthItNeeded()
        {
            var ddc = DigitalDownconverter.ForDecimation(InputRateHz, ShiftHz, 64);

            using (IqBlock source = Block(64, DateTime.UtcNow, 0.0))
            {
                ArgumentException error =
                    Assert.Throws<ArgumentException>(() => ddc.Downconvert(source));

                Assert.Contains(ddc.MinimumInputSamples.ToString(), error.Message);
            }
        }

        [Fact]
        public void ANullBlockIsRefused()
        {
            var ddc = DigitalDownconverter.ForDecimation(InputRateHz, ShiftHz, 4);

            Assert.Throws<ArgumentNullException>(() => ddc.Downconvert((IqBlock)null));
        }

        [Theory]
        [InlineData(1)]
        [InlineData(0)]
        [InlineData(-4)]
        public void ADecimationBelowTwoIsRefused(int decimation)
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => DigitalDownconverter.ForDecimation(InputRateHz, ShiftHz, decimation));
        }

        [Fact]
        public void AShiftThatReachesOutsideTheInputIsRefused()
        {
            // At decimation 2 the wanted band is 6 MHz wide, so its edge leaves the 15 MS/s input
            // once the shift passes 4.5 MHz. There is no data out there to zoom into.
            Assert.Throws<ArgumentOutOfRangeException>(
                () => DigitalDownconverter.ForDecimation(InputRateHz, 5e6, 2));

            var ddc = DigitalDownconverter.ForDecimation(InputRateHz, 4.4e6, 2);

            Assert.Equal(4.4e6, ddc.ShiftHz, 6);
        }

        [Theory]
        [InlineData(0.0)]
        [InlineData(-1.0)]
        [InlineData(double.NaN)]
        [InlineData(double.PositiveInfinity)]
        public void ARateThatIsNotPositiveAndFiniteIsRefused(double rateHz)
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => DigitalDownconverter.ForDecimation(rateHz, 0.0, 4));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => DigitalDownconverter.ForSpan(rateHz, 0.0, 1e3));
        }

        [Theory]
        [InlineData(double.NaN)]
        [InlineData(double.PositiveInfinity)]
        public void AShiftThatIsNotFiniteIsRefused(double shiftHz)
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => DigitalDownconverter.ForDecimation(InputRateHz, shiftHz, 4));
        }

        [Fact]
        public void ASpanTooWideToNarrowToIsRefusedWithTheWidestThatIsNot()
        {
            ArgumentOutOfRangeException error = Assert.Throws<ArgumentOutOfRangeException>(
                () => DigitalDownconverter.ForSpan(InputRateHz, 0.0, 9e6));

            // The widest span that needs no downconversion: 0.8 x 15 MS/s / 2 = 6 MHz.
            Assert.Contains(
                (DdcDesignTargets.UsableBandwidthFraction * InputRateHz / 2.0)
                    .ToString("G6", System.Globalization.CultureInfo.InvariantCulture),
                error.Message);
        }

        [Fact]
        public void AnAbsurdDecimationIsRefusedByTapCountRatherThanByRunningOutOfMemory()
        {
            // What a span given in hertz where megahertz was meant asks for.
            ArgumentOutOfRangeException error = Assert.Throws<ArgumentOutOfRangeException>(
                () => DigitalDownconverter.ForDecimation(InputRateHz, 0.0, 1000000));

            Assert.Contains("tap", error.Message);
        }

        /// <summary>
        /// The steady-state modulus of the downconverter's output for a full-scale tone.
        /// </summary>
        /// <remarks>
        /// Asserts that the modulus really is steady before returning it. If it is not, the tone
        /// is being measured through an edge transient and every figure taken from it is a
        /// different quantity from the one the requirement names.
        /// </remarks>
        private static double ResponseAt(DigitalDownconverter ddc, double toneHz)
        {
            const int outputs = 4;

            float[] output = Downconvert(ddc, toneHz, outputs);
            double first = Math.Sqrt(
                (double)output[0] * output[0] + (double)output[1] * output[1]);

            for (int j = 1; j < outputs; j++)
            {
                double magnitude = Math.Sqrt(
                    (double)output[j * 2] * output[j * 2] +
                    (double)output[j * 2 + 1] * output[j * 2 + 1]);

                Assert.True(
                    Math.Abs(magnitude - first) <= 1e-6 + 1e-6 * first,
                    "the output modulus is not constant: " + first + " then " + magnitude + ".");
            }

            return first;
        }

        private static float[] Downconvert(DigitalDownconverter ddc, double toneHz, int outputs)
        {
            int samples = ddc.MinimumInputSamples + (outputs - 1) * ddc.Decimation;
            var input = new float[samples * 2];
            double cycles = toneHz / InputRateHz;

            for (int n = 0; n < samples; n++)
            {
                double angle = 2.0 * Math.PI * cycles * n;

                input[n * 2] = (float)Math.Cos(angle);
                input[n * 2 + 1] = (float)Math.Sin(angle);
            }

            var output = new float[ddc.OutputCountFor(samples) * 2];
            int count = ddc.Downconvert(input, output);

            Assert.Equal(outputs, count);

            return output;
        }

        private static double WrappedDistanceHz(double differenceHz)
        {
            double cycles = differenceHz / InputRateHz;

            cycles -= Math.Floor(cycles + 0.5);

            return Math.Abs(cycles) * InputRateHz;
        }

        private static double WrappedRadians(double radians)
        {
            double turns = radians / (2.0 * Math.PI);

            return (turns - Math.Floor(turns + 0.5)) * 2.0 * Math.PI;
        }

        private static IqBlock Block(int samples, DateTime when, double triggerOffsetSeconds)
        {
            var extended = new Dictionary<string, object>
            {
                { IqBlockMetadata.UsableBandwidthKey, 12e6 },
                { "unrelated", "keep-me" },
            };

            IqBlock block = IqBlock.Rent(new IqBlockMetadata(
                sampleCount: samples,
                sampleRateHz: InputRateHz,
                centerFrequencyHz: 1e9,
                isBaseband: false,
                fullScaleVolts: 0.316,
                referenceLevelDbm: 0.0,
                sequenceNumber: 17,
                acquiredUtc: when,
                triggerOffsetSeconds: triggerOffsetSeconds,
                triggerCorrectionsApplied: true,
                source: new FrontEndId("test"),
                extended: extended));

            Span<float> data = block.GetSamples();

            for (int n = 0; n < samples; n++)
            {
                double angle = 2.0 * Math.PI * (ShiftHz / InputRateHz) * n;

                data[n * 2] = (float)Math.Cos(angle);
                data[n * 2 + 1] = (float)Math.Sin(angle);
            }

            return block;
        }
    }
}
