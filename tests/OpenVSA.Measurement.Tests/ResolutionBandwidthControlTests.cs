using System;
using System.Collections.Generic;
using OpenVSA.Core;
using OpenVSA.Dsp.Spectrum;
using OpenVSA.Dsp.Windowing;
using OpenVSA.Hal;
using OpenVSA.Measurement;
using Xunit;
using Xunit.Abstractions;

namespace OpenVSA.Measurement.Tests
{
    /// <summary>
    /// <c>REQ-DSP-021</c>: the RBW range, the two modes, and how the bandwidth tracks span.
    /// </summary>
    public class ResolutionBandwidthControlTests
    {
        private readonly ITestOutputHelper _output;

        public ResolutionBandwidthControlTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void TheCoarsestBandwidthExceedsTheRequiredFractionOfTheMaximumSpan()
        {
            // The requirement's upper figure: better than 0.287 of the span. Taken at the front
            // end's maximum span, which is where the requirement states it.
            var capabilities = new Capabilities();
            var control = new ResolutionBandwidthControl(capabilities, capabilities.MaxSpanHz);

            ResolutionBandwidthRange range = control.Achievable;

            _output.WriteLine(range.ToString());

            Assert.True(
                range.MaxHz > 0.287 * capabilities.MaxSpanHz,
                "The coarsest RBW must exceed 0.287 of the maximum span, and it is " + range.MaxHz);

            // And it is settable, not merely reported.
            Assert.Equal(range.MaxHz, control.SetResolutionBandwidth(range.MaxHz), 6);
        }

        [Fact]
        public void ABandwidthBelowOneHertzIsSettableWhereTheCaptureIsDeepEnough()
        {
            // The requirement's lower figure. It is a capability question and it is answered as
            // one: a deep-capture source over a narrow span reaches well below 1 Hz. 61 kHz is
            // chosen for being nothing in particular - a span picked to make the arithmetic tidy
            // would hide a rounding that a real setting would not.
            var control = new ResolutionBandwidthControl(new Capabilities(), 61e3);

            ResolutionBandwidthRange range = control.Achievable;

            _output.WriteLine(range.ToString());

            Assert.True(range.MinHz < 1.0, "The finest RBW here is " + range.MinHz + " Hz.");

            double set = control.SetResolutionBandwidth(0.9);

            Assert.Equal(0.9, set, 9);
            Assert.Equal(0.9, control.ResolutionBandwidthHz, 9);
        }

        [Fact]
        public void TheFinestBandwidthComesFromTheFrontEndAndNotFromAConstant()
        {
            // REQ-HAL-002 applied here: a shallow source reaches a coarser floor than a deep one,
            // at the same span, with nothing in the control knowing which is which.
            var deep = new ResolutionBandwidthControl(new Capabilities(), 1e6);
            var shallow = new ResolutionBandwidthControl(
                new Capabilities { Samples = 4096 }, 1e6);

            _output.WriteLine("deep " + deep.Achievable + ", shallow " + shallow.Achievable);

            Assert.True(deep.Achievable.MinHz < shallow.Achievable.MinHz);
        }

        [Fact]
        public void ABandwidthFinerThanTheSourceCanReachIsRejectedWithTheBound()
        {
            // Rejected, not clamped, and the message has to carry the number the user can act on.
            var control = new ResolutionBandwidthControl(new Capabilities(), 40e6);
            double finest = control.Achievable.MinHz;

            ArgumentOutOfRangeException refused = Assert.Throws<ArgumentOutOfRangeException>(
                () => control.SetResolutionBandwidth(finest / 10.0));

            _output.WriteLine(refused.Message);

            Assert.Contains("finest available", refused.Message);
            Assert.Contains("narrower span", refused.Message);

            // Untouched: a rejected setting must not have half-applied.
            Assert.NotEqual(finest / 10.0, control.ResolutionBandwidthHz);
        }

        [Fact]
        public void ABandwidthCoarserThanTheSpanAllowsIsRejectedWithTheBound()
        {
            var control = new ResolutionBandwidthControl(new Capabilities(), 1e6);

            ArgumentOutOfRangeException refused = Assert.Throws<ArgumentOutOfRangeException>(
                () => control.SetResolutionBandwidth(0.9e6));

            _output.WriteLine(refused.Message);

            Assert.Contains("coarser", refused.Message);
            Assert.Contains("wider span", refused.Message);
        }

        [Fact]
        public void CoupledModeHoldsTheRatioAcrossASpanChange()
        {
            // The criterion, in both of its halves: the ratio is held, and what comes out still
            // satisfies REQ-DSP-020's relation exactly rather than approximately.
            var control = new ResolutionBandwidthControl(new Capabilities(), 4.7e6);

            control.SetCoupling(ResolutionBandwidthCoupling.Coupled);
            control.SetSpanToRatio(137.0);

            double before = control.ResolutionBandwidthHz;
            Assert.Equal(4.7e6 / 137.0, before, 9);

            double after = control.SetSpan(1.9e6);

            _output.WriteLine(before + " Hz at 4.7 MHz → " + after + " Hz at 1.9 MHz");

            Assert.Equal(1.9e6 / 137.0, after, 9);
            Assert.Equal(137.0, control.SpanHz / control.ResolutionBandwidthHz, 9);

            // RBW = ENBW / T_rec, exactly, in the units the DSP layer states it in.
            double enbw = Window.Get(control.AnalysisWindow, 4096).Enbw;

            Assert.Equal(
                after, ResolutionBandwidth.ForRecordLength(enbw, control.RecordSeconds), 9);
            Assert.Equal(enbw / after, control.RecordSeconds, 12);
        }

        [Fact]
        public void UncoupledModeLeavesTheBandwidthWhereItWasPut()
        {
            var control = new ResolutionBandwidthControl(new Capabilities(), 4.7e6);

            control.SetCoupling(ResolutionBandwidthCoupling.Uncoupled);
            control.SetResolutionBandwidth(12.5e3);

            double after = control.SetSpan(1.9e6);

            _output.WriteLine("12.5 kHz held across 4.7 MHz → 1.9 MHz: " + after);

            Assert.Equal(12.5e3, after, 9);
            Assert.Equal(12.5e3, control.ResolutionBandwidthHz, 9);
        }

        [Fact]
        public void SettingTheBandwidthWhileCoupledRestatesTheRatio()
        {
            // Otherwise the next span change would quietly undo what was just typed.
            var control = new ResolutionBandwidthControl(new Capabilities(), 4.7e6);

            control.SetCoupling(ResolutionBandwidthCoupling.Coupled);
            control.SetResolutionBandwidth(47e3);

            Assert.Equal(100.0, control.SpanToRatio, 9);
            Assert.Equal(23.5e3, control.SetSpan(2.35e6), 9);
        }

        [Fact]
        public void TheTwoModesProduceDifferentSequencesOverASpanSweep()
        {
            // The criterion asks for demonstrably different, so the two sweeps are run side by side
            // and compared step for step rather than at one span.
            var spans = new[] { 10e6, 7.3e6, 4.7e6, 2.2e6, 1.1e6, 470e3 };

            var arbitrary = new ResolutionBandwidthControl(new Capabilities(), spans[0]);
            var analyser = new ResolutionBandwidthControl(new Capabilities(), spans[0]);

            arbitrary.SetCoupling(ResolutionBandwidthCoupling.Coupled);
            analyser.SetCoupling(ResolutionBandwidthCoupling.Coupled);
            analyser.SetMode(ResolutionBandwidthMode.SpectrumAnalyser);

            var differences = 0;

            foreach (double span in spans)
            {
                double free = arbitrary.SetSpan(span);
                double stepped = analyser.SetSpan(span);

                _output.WriteLine(
                    span / 1e6 + " MHz: arbitrary " + free + " Hz, analyser " + stepped + " Hz");

                // The ladder is never coarser than the bandwidth asked for.
                Assert.True(stepped <= free * (1.0 + 1e-12));
                Assert.True(IsOnLadder(stepped), stepped + " Hz is not a 1-3-10 ladder step.");

                if (Math.Abs(stepped - free) > 1e-9)
                {
                    differences++;
                }
            }

            Assert.True(differences >= 4, "Only " + differences + " of the spans differed.");
        }

        [Theory]
        [InlineData(1500.0, 1000.0)]
        [InlineData(2999.0, 1000.0)]
        [InlineData(3000.0, 3000.0)]
        [InlineData(9999.0, 3000.0)]
        [InlineData(10000.0, 10000.0)]
        [InlineData(0.47, 0.3)]
        public void TheAnalyserLadderSnapsDownwardAndLandsOnItsOwnSteps(double wanted, double expected)
        {
            // Downward, because a coarser answer than the one asked for is a different measurement.
            // The decade boundaries are included deliberately: a value exactly on a step must stay
            // there rather than dropping to the step below through a log10 rounding.
            var control = new ResolutionBandwidthControl(new Capabilities(), 10e6);

            control.SetMode(ResolutionBandwidthMode.SpectrumAnalyser);

            Assert.Equal(expected, control.Snap(wanted), 9);
        }

        [Fact]
        public void ArbitraryModeUsesTheFigureItWasGiven()
        {
            var control = new ResolutionBandwidthControl(new Capabilities(), 10e6);

            Assert.Equal(ResolutionBandwidthMode.Arbitrary, control.Mode);
            Assert.Equal(1234.5, control.Snap(1234.5), 9);
            Assert.Equal(1234.5, control.SetResolutionBandwidth(1234.5), 9);
        }

        [Fact]
        public void SelectingAnalyserModeMovesTheBandwidthOntoTheLadder()
        {
            var control = new ResolutionBandwidthControl(new Capabilities(), 10e6);

            control.SetResolutionBandwidth(1234.5);
            double snapped = control.SetMode(ResolutionBandwidthMode.SpectrumAnalyser);

            Assert.Equal(1000.0, snapped, 9);
            Assert.Equal(1000.0, control.ResolutionBandwidthHz, 9);
        }

        [Fact]
        public void TheLadderOfferedIsBoundedByWhatIsReachable()
        {
            // For a selector: every step it offers must be settable, and the ends must be inside
            // the reachable range rather than one step outside it.
            var control = new ResolutionBandwidthControl(new Capabilities(), 10e6);
            ResolutionBandwidthRange range = control.Achievable;

            control.SetMode(ResolutionBandwidthMode.SpectrumAnalyser);

            IReadOnlyList<double> steps = ResolutionBandwidthControl.LadderWithin(range);

            _output.WriteLine(steps.Count + " steps from " + steps[0] + " Hz down to " +
                              steps[steps.Count - 1] + " Hz");

            Assert.NotEmpty(steps);

            for (int i = 0; i < steps.Count; i++)
            {
                Assert.True(range.Contains(steps[i]), steps[i] + " Hz is outside " + range);
                Assert.Equal(steps[i], control.SetResolutionBandwidth(steps[i]), 9);

                if (i > 0)
                {
                    Assert.True(steps[i] < steps[i - 1], "The ladder must descend.");
                }
            }
        }

        [Fact]
        public void CouplingTakesTheRatioFromWhereTheBandwidthAlreadyIs()
        {
            var control = new ResolutionBandwidthControl(new Capabilities(), 4.7e6);

            control.SetCoupling(ResolutionBandwidthCoupling.Uncoupled);
            control.SetResolutionBandwidth(9.4e3);
            control.SetCoupling(ResolutionBandwidthCoupling.Coupled);

            Assert.Equal(500.0, control.SpanToRatio, 9);
            Assert.Equal(9.4e3, control.ResolutionBandwidthHz, 9);
        }

        [Fact]
        public void NothingIsSetFromValuesThatAreNotBandwidths()
        {
            var control = new ResolutionBandwidthControl(new Capabilities(), 10e6);

            Assert.Throws<ArgumentNullException>(
                () => new ResolutionBandwidthControl(null, 10e6));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => new ResolutionBandwidthControl(new Capabilities(), 0.0));
            Assert.Throws<ArgumentOutOfRangeException>(() => control.SetResolutionBandwidth(0.0));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => control.SetResolutionBandwidth(double.NaN));
            Assert.Throws<ArgumentOutOfRangeException>(() => control.SetSpan(-1.0));
            Assert.Throws<ArgumentOutOfRangeException>(() => control.SetSpanToRatio(0.0));
        }

        private static bool IsOnLadder(double hz)
        {
            double decade = Math.Pow(10.0, Math.Floor(Math.Log10(hz)));
            double mantissa = hz / decade;

            return Math.Abs(mantissa - 1.0) < 1e-9 ||
                   Math.Abs(mantissa - 3.0) < 1e-9 ||
                   Math.Abs(mantissa - 10.0) < 1e-9;
        }

        /// <summary>Capabilities whose capture depth is set per test.</summary>
        private sealed class Capabilities : IFrontEndCapabilities
        {
            private static readonly IReadOnlyList<TriggerStyle> Styles =
                new List<TriggerStyle> { TriggerStyle.Immediate }.AsReadOnly();

            public int Samples { get; set; } = 1 << 22;

            public FrequencyRange CenterFrequencyRange => new FrequencyRange(0.0, 26.5e9);
            public double MaxSpanHz => 40e6;
            public double MinSpanHz => 1.0;
            public double MaxSampleRateHz => 51.2e6;
            public int MaxSamplesPerBlock => Samples;
            public long MaxCaptureSamples => Samples;
            public bool SupportsBasebandIq => true;
            public int ChannelCount => 1;
            public bool SupportsPhaseCoherentChannels => false;
            public IReadOnlyList<TriggerStyle> TriggerStyles => Styles;
            public AmplitudeRange ReferenceLevelRange => new AmplitudeRange(-100.0, 30.0);
            public bool SupportsExternalRef => false;
            public bool SupportsInputRangeControl => true;
            public bool SupportsRealTimeAnalysis => false;
            public long MaxPreTriggerSamples => 0L;
        }
    }
}
