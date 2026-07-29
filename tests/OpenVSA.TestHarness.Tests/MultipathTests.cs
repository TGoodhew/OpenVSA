using System;
using System.Linq;
using OpenVSA.TestHarness.Synthesis;
using Xunit;
using Xunit.Abstractions;

namespace OpenVSA.TestHarness.Tests
{
    /// <summary>
    /// <c>REQ-SIM-002</c>'s twelfth impairment: the tapped-delay-line channel.
    /// </summary>
    /// <remarks>
    /// The other eleven are scalars read back from a moment or a fit. A channel is a response, so it
    /// is injected as taps and recovered by solving for taps — and unlike the others it can be asked
    /// for something the reference sequence cannot answer, which is why the estimate carries its own
    /// identifiability and these tests exercise both sides of that.
    /// </remarks>
    public class MultipathTests
    {
        private const int SamplesPerSymbol = 8;

        private readonly ITestOutputHelper _output;

        /// <summary>Takes xunit's output sink.</summary>
        /// <param name="output">Where the recovered taps are written.</param>
        public MultipathTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void ATwoPathChannelIsRecoveredToWithinOnePercent()
        {
            // "matches the magnitude requested to within 1 %". For a tap that is the complex weight,
            // so the gain is checked as a ratio and the phase as an angle rather than both as dB.
            var wanted = new Impairments();
            wanted.Multipath.Add(new MultipathTap(0, 0.0));
            wanted.Multipath.Add(new MultipathTap(SamplesPerSymbol, -6.0, 40.0));

            ImpairedSignal signal = ImpairedSignal.Generate(wanted);

            ChannelEstimate estimate = ImpairmentMeasurement.MultipathTaps(
                signal, 0, SamplesPerSymbol);

            _output.WriteLine(estimate.ToString());

            Assert.True(estimate.IsIdentifiable);

            AssertTap(wanted.Multipath[0], estimate.Taps[0]);
            AssertTap(wanted.Multipath[1], estimate.Taps[1]);
        }

        [Fact]
        public void AFractionOfASymbolIsRecoveredToo()
        {
            // A delay shorter than a symbol is the case that produces frequency-selective fading
            // inside the occupied bandwidth rather than plain inter-symbol interference, and it is
            // the one a channel model is usually wanted for.
            var wanted = new Impairments();
            wanted.Multipath.Add(new MultipathTap(0, 0.0));
            wanted.Multipath.Add(new MultipathTap(3, -3.0, -110.0));

            ImpairedSignal signal = ImpairedSignal.Generate(wanted);

            ChannelEstimate estimate = ImpairmentMeasurement.MultipathTaps(signal, 0, 3);

            _output.WriteLine(estimate.ToString());

            Assert.True(estimate.IsIdentifiable);

            AssertTap(wanted.Multipath[0], estimate.Taps[0]);
            AssertTap(wanted.Multipath[1], estimate.Taps[1]);
        }

        [Fact]
        public void ThreeTapsAreRecoveredTogether()
        {
            var wanted = new Impairments();
            wanted.Multipath.Add(new MultipathTap(0, 0.0));
            wanted.Multipath.Add(new MultipathTap(5, -9.0, 25.0));
            wanted.Multipath.Add(new MultipathTap(11, -14.0, 160.0));

            ImpairedSignal signal = ImpairedSignal.Generate(wanted);

            ChannelEstimate estimate = ImpairmentMeasurement.MultipathTaps(signal, 0, 5, 11);

            _output.WriteLine(estimate.ToString());

            Assert.True(estimate.IsIdentifiable);

            for (int t = 0; t < 3; t++)
            {
                AssertTap(wanted.Multipath[t], estimate.Taps[t]);
            }
        }

        [Fact]
        public void ADelaySetThisReferenceCannotDistinguishIsRefusedRatherThanAnswered()
        {
            // The finding this whole design exists for. The generator's symbol pattern repeats every
            // four symbols so that the moment-based measurements are exact, and the price is that
            // four copies one symbol apart are linearly dependent: there is no unique set of taps
            // that produces the observed signal, and least squares will happily return one anyway.
            //
            // A refusal here is the correct answer. A number would not be.
            var wanted = new Impairments();
            wanted.Multipath.Add(new MultipathTap(0, 0.0));
            wanted.Multipath.Add(new MultipathTap(SamplesPerSymbol, -6.0, 40.0));

            ImpairedSignal signal = ImpairedSignal.Generate(wanted);

            ChannelEstimate estimate = ImpairmentMeasurement.MultipathTaps(
                signal, 0, SamplesPerSymbol, 2 * SamplesPerSymbol, 3 * SamplesPerSymbol);

            _output.WriteLine(estimate.ToString());

            Assert.False(
                estimate.IsIdentifiable,
                "Four copies one symbol apart of a four-symbol cyclic pattern were reported as " +
                "identifiable, which they are not.");
        }

        [Fact]
        public void AnIdentifiableSetScoresFarAboveTheThreshold()
        {
            // Otherwise the refusal above proves only that the threshold is high, not that it
            // separates the two cases. The gap between them is the evidence.
            var wanted = new Impairments();
            wanted.Multipath.Add(new MultipathTap(0, 0.0));
            wanted.Multipath.Add(new MultipathTap(SamplesPerSymbol, -6.0, 40.0));

            ImpairedSignal signal = ImpairedSignal.Generate(wanted);

            double good = ImpairmentMeasurement.MultipathTaps(
                signal, 0, SamplesPerSymbol).Identifiability;

            double bad = ImpairmentMeasurement.MultipathTaps(
                signal, 0, SamplesPerSymbol, 2 * SamplesPerSymbol, 3 * SamplesPerSymbol)
                .Identifiability;

            _output.WriteLine(
                "identifiable " + good.ToString("G4") + " against unidentifiable " +
                bad.ToString("G4") + ", threshold " +
                ImpairmentMeasurement.MinimumIdentifiability.ToString("G4"));

            Assert.True(good > 100.0 * ImpairmentMeasurement.MinimumIdentifiability);
        }

        [Fact]
        public void NoChannelReadsAsAStraightThrough()
        {
            // The zero case, and a regression on the ordering change that moved AWGN into its own
            // pass: with no taps the signal must be exactly what it was before.
            ImpairedSignal signal = ImpairedSignal.Generate(new Impairments());

            ChannelEstimate estimate = ImpairmentMeasurement.MultipathTaps(signal, 0);

            _output.WriteLine(estimate.ToString());

            Assert.True(Math.Abs(estimate.Taps[0].GainDb) < 1e-9);
            Assert.True(Math.Abs(estimate.Taps[0].PhaseDegrees) < 1e-9);
        }

        [Fact]
        public void ATapCannotArriveBeforeTheSignalThatProducedIt()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new MultipathTap(-1, 0.0));
        }

        [Fact]
        public void AskingForNoDelaysIsRejectedRatherThanReturningNothing()
        {
            ImpairedSignal signal = ImpairedSignal.Generate(new Impairments());

            Assert.Throws<ArgumentException>(
                () => ImpairmentMeasurement.MultipathTaps(signal));
        }

        [Fact]
        public void TheChannelIsRecoveredThroughNoise()
        {
            // Least squares over thirty thousand samples should shrug off 20 dB of SNR, and if it
            // does not then the estimator is fitting something other than the channel.
            var wanted = new Impairments { SignalToNoiseDb = 20.0 };
            wanted.Multipath.Add(new MultipathTap(0, 0.0));
            wanted.Multipath.Add(new MultipathTap(SamplesPerSymbol, -6.0, 40.0));

            ImpairedSignal signal = ImpairedSignal.Generate(wanted);

            ChannelEstimate estimate = ImpairmentMeasurement.MultipathTaps(
                signal, 0, SamplesPerSymbol);

            // The requested figure, not ImpairmentMeasurement.SignalToNoiseDb, which scores the
            // residual against the unit constellation and therefore counts the channel's own
            // attenuation as noise. Printing that here would read as a 14 dB shortfall that is not
            // one.
            _output.WriteLine(estimate + "; requested SNR " + wanted.SignalToNoiseDb + " dB");

            AssertTap(wanted.Multipath[1], estimate.Taps[1], gainTolerance: 0.02, phaseTolerance: 1.0);
        }

        [Fact]
        public void NoiseIsAddedAfterTheChannelRatherThanBeforeIt()
        {
            // Where the noise goes is a physical claim, not a detail: a receiver's noise floor does
            // not pass through the channel.
            //
            // **The obvious test for this cannot fail.** Comparing the measured SNR with and
            // without a lossy channel shows a large drop either way, because
            // ImpairmentMeasurement.SignalToNoiseDb scores the residual against the unit
            // constellation and a 6 dB attenuation is itself a large residual. Both orderings give
            // the same number, so the check would pass while proving nothing. I wrote that version
            // first and it "passed".
            //
            // The noise is therefore isolated directly: generate the same channel twice, once with
            // AWGN and once without, and subtract. What is left is the noise and only the noise.
            // Added after the channel, its power does not depend on the channel at all; added
            // before, 6 dB of loss would take 6 dB off it with the signal.
            double withChannel = NoisePower(-6.0);
            double withoutChannel = NoisePower(null);

            double difference = 10.0 * Math.Log10(withChannel / withoutChannel);

            _output.WriteLine(
                "isolated noise power: no channel " +
                (10.0 * Math.Log10(withoutChannel)).ToString("F3") + " dB, through 6 dB of loss " +
                (10.0 * Math.Log10(withChannel)).ToString("F3") + " dB, difference " +
                difference.ToString("F3") + " dB");

            Assert.True(
                Math.Abs(difference) < 0.1,
                "A 6 dB channel loss moved the noise power by " + difference.ToString("F3") +
                " dB, so the noise is passing through the channel rather than being added after " +
                "it. A receiver's noise floor does not.");
        }

        [Fact]
        public void EveryImpairmentTheRequirementNamesCanBeAsked()
        {
            // The list is twelve long and this is the twelfth. A count rather than prose, because
            // "and multipath" was the item that sat unbuilt behind eleven passing tests.
            var all = new Impairments
            {
                SignalToNoiseDb = 25.0,
                CarrierOffsetHz = 1000.0,
                CarrierPhaseDegrees = 10.0,
                GainImbalanceDb = 0.5,
                QuadratureSkewDegrees = 2.0,
                OriginOffsetDb = -35.0,
                DroopDbPerSymbol = -0.001,
                TimingOffsetSymbols = 0.1,
                ClockErrorPpm = 20.0,
                PhaseNoiseDegreesRms = 1.0,
                CompressionDb = 0.5,
                AmToPmDegrees = 2.0,
            };

            all.Multipath.Add(new MultipathTap(SamplesPerSymbol / 2, -12.0, 30.0));

            ImpairedSignal signal = ImpairedSignal.Generate(all);

            Assert.Equal(4096 * SamplesPerSymbol, signal.Length);
            Assert.Single(signal.Requested.Multipath);

            // And nothing produced a NaN on the way through, which a channel applied in the wrong
            // place — over a buffer it was still writing — would do.
            Assert.DoesNotContain(signal.I.Concat(signal.Q), double.IsNaN);
        }

        [Fact]
        public void TheOtherElevenImpairmentsDoNotDisturbTheRecoveredTaps()
        {
            // One direction of the independence clause, and the one that can hold. Every other
            // impairment is applied before the channel, so a tap estimate must come back unchanged
            // with all eleven switched on.
            //
            // Not tautological: it fails if AM/AM or AM/PM compression is moved after the channel,
            // which makes the path non-linear and the least-squares fit meaningless. That reordering
            // is an easy thing to do and its symptom is a tap estimate that is quietly wrong.
            var wanted = new Impairments
            {
                SignalToNoiseDb = 30.0,
                CarrierOffsetHz = 1000.0,
                CarrierPhaseDegrees = 10.0,
                GainImbalanceDb = 0.5,
                QuadratureSkewDegrees = 2.0,
                OriginOffsetDb = -35.0,
                DroopDbPerSymbol = -0.001,
                TimingOffsetSymbols = 0.1,
                ClockErrorPpm = 20.0,
                PhaseNoiseDegreesRms = 1.0,
                CompressionDb = 0.5,
                AmToPmDegrees = 2.0,
            };

            wanted.Multipath.Add(new MultipathTap(0, 0.0));
            wanted.Multipath.Add(new MultipathTap(5, -9.0, 25.0));

            ChannelEstimate estimate = ImpairmentMeasurement.MultipathTaps(
                ImpairedSignal.Generate(wanted), 0, 5);

            _output.WriteLine("with all eleven others injected: " + estimate);

            Assert.True(estimate.IsIdentifiable);

            AssertTap(wanted.Multipath[0], estimate.Taps[0], gainTolerance: 0.01, phaseTolerance: 0.5);
            AssertTap(wanted.Multipath[1], estimate.Taps[1], gainTolerance: 0.01, phaseTolerance: 0.5);
        }

        [Fact]
        public void AChannelLeavesTheDroopSlopeAndTheNoisePowerAlone()
        {
            // The other direction, for the two measurements it can leave alone. A channel is
            // time-invariant, so it cannot produce a trend across the record; and the noise is added
            // after it, so it cannot scale it.
            var wanted = new Impairments();
            wanted.Multipath.Add(new MultipathTap(0, 0.0));
            wanted.Multipath.Add(new MultipathTap(SamplesPerSymbol, -20.0));

            double cleanDroop = ImpairmentMeasurement.DroopDbPerSymbol(
                ImpairedSignal.Generate(new Impairments()));

            double channelDroop = ImpairmentMeasurement.DroopDbPerSymbol(
                ImpairedSignal.Generate(wanted));

            _output.WriteLine(
                "droop: clean " + cleanDroop.ToString("E2") + ", through the channel " +
                channelDroop.ToString("E2") + " dB/symbol");

            Assert.True(
                Math.Abs(channelDroop - cleanDroop) < 1e-6,
                "A time-invariant channel produced a trend across the record.");
        }

        [Fact]
        public void WhatAChannelDoesDisturbIsMeasuredRatherThanAsserted()
        {
            // **This is the clause REQ-SIM-002 states that a channel cannot meet, and the number is
            // here rather than a tolerance chosen to hide it.**
            //
            // The requirement asks that injecting one impairment leave every other's measured value
            // unchanged. Eleven of the twelve are scalars applied pointwise, and they can. The
            // twelfth is a linear filter, and it cannot — not because of how it is applied, but
            // because of what it is:
            //
            //   * At the symbol instants, a filter whose response is not unity is indistinguishable
            //     from a gain and a phase rotation. That is a channel and a carrier phase offset
            //     producing the same samples, so no measurement can separate them.
            //   * A tap a symbol or more away adds inter-symbol interference, and the generator's
            //     reference sequence is cyclic with period four — which means a symbol and its
            //     predecessor are perfectly correlated on I (the product is -1 at every instant) and
            //     uncorrelated on Q. A delayed copy therefore biases the I and Q second moments by
            //     different amounts, and those moments are what gain imbalance is read from.
            //
            // The second is a consequence of the cyclic sequence, which exists so the other
            // measurements are exact rather than accurate to one part in root-N. Both are recorded
            // on the issue. This test pins the magnitudes so that a change in the generator that
            // alters them is visible, and asserts only what the arithmetic above predicts.
            var wanted = new Impairments();
            wanted.Multipath.Add(new MultipathTap(0, 0.0));
            wanted.Multipath.Add(new MultipathTap(SamplesPerSymbol, -20.0));

            ImpairedSignal signal = ImpairedSignal.Generate(wanted);

            double imbalance = ImpairmentMeasurement.GainImbalanceDb(signal);

            _output.WriteLine(
                "a -20 dB echo one symbol away reads as " + imbalance.ToString("F3") +
                " dB of gain imbalance");

            // 1 + 2c*E[I.Iprev] + c^2 against 1 + 2c*E[Q.Qprev] + c^2, with c = 0.1, E[I.Iprev] = -1
            // and E[Q.Qprev] = 0: 10*log10(0.81/1.01) = -0.962 dB.
            double predicted = 10.0 * Math.Log10(0.81 / 1.01);

            Assert.True(
                Math.Abs(imbalance - predicted) < 0.01,
                "The coupling is " + imbalance.ToString("F3") + " dB where the sequence's own " +
                "autocorrelation predicts " + predicted.ToString("F3") + " dB. The mechanism is " +
                "not the one recorded on the issue.");
        }

        /// <summary>
        /// The mean noise power alone, by generating the same signal twice and differencing.
        /// </summary>
        /// <param name="lossDb">Channel loss in dB, or <c>null</c> for no channel at all.</param>
        private static double NoisePower(double? lossDb)
        {
            var noisy = new Impairments { SignalToNoiseDb = 20.0 };
            var quiet = new Impairments();

            if (lossDb.HasValue)
            {
                noisy.Multipath.Add(new MultipathTap(0, lossDb.Value));
                quiet.Multipath.Add(new MultipathTap(0, lossDb.Value));
            }

            ImpairedSignal a = ImpairedSignal.Generate(noisy);
            ImpairedSignal b = ImpairedSignal.Generate(quiet);

            double sum = 0.0;

            for (int n = 0; n < a.Length; n++)
            {
                double di = a.I[n] - b.I[n];
                double dq = a.Q[n] - b.Q[n];

                sum += di * di + dq * dq;
            }

            return sum / a.Length;
        }

        private void AssertTap(
            MultipathTap wanted,
            MultipathTap measured,
            double gainTolerance = 0.01,
            double phaseTolerance = 0.5)
        {
            double wantedGain = Math.Pow(10.0, wanted.GainDb / 20.0);
            double measuredGain = Math.Pow(10.0, measured.GainDb / 20.0);

            double error = Math.Abs(measuredGain - wantedGain) / wantedGain;

            _output.WriteLine(
                "wanted " + wanted + " -> measured " + measured +
                " (gain error " + (error * 100.0).ToString("F3") + " %)");

            Assert.True(
                error < gainTolerance,
                "Tap at " + wanted.DelaySamples + " samples: gain error " +
                (error * 100.0).ToString("F3") + " %.");

            double phaseError = Math.Abs(
                Unwrap(measured.PhaseDegrees - wanted.PhaseDegrees));

            Assert.True(
                phaseError < phaseTolerance,
                "Tap at " + wanted.DelaySamples + " samples: phase error " +
                phaseError.ToString("F3") + " degrees.");
        }

        private static double Unwrap(double degrees)
        {
            while (degrees > 180.0)
            {
                degrees -= 360.0;
            }

            while (degrees < -180.0)
            {
                degrees += 360.0;
            }

            return degrees;
        }
    }
}
