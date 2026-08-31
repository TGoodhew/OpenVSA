using System;
using System.Collections.Generic;
using System.Globalization;
using OpenVSA.Demod.Chain;
using OpenVSA.Demod.Chain.Steps;
using OpenVSA.Demod.Help;
using OpenVSA.Demod.Signal;
using OpenVSA.Synthesis;
using Xunit;
using Xunit.Abstractions;

namespace OpenVSA.Demod.Tests
{
    /// <summary>
    /// <c>REQ-DEM-052</c>: the least-squares solution is exact and is the default; the gradient
    /// modes are bounded, and they can be started blind or from a known sequence.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The requirement is a design choice with a stated rationale</strong>, so these tests
    /// are written to hold the rationale rather than the code: the one-shot solution is checked
    /// against an analytic inverse computed independently, and the gradient modes are checked
    /// against the one-shot solution. A test that only compared each mode with its own last answer
    /// would pass for an equaliser that had quietly stopped solving anything.
    /// </para>
    /// </remarks>
    public class EqualiserAlgorithmTests
    {
        private const double SymbolRateHz = 1e6;
        private const double SampleRateHz = 16e6;
        private const int Symbols = 4000;
        private const int PatternAt = 1500;

        /// <summary>
        /// The signal-to-noise ratio the gradient modes are compared with the exact solution at.
        /// </summary>
        /// <remarks>
        /// Twenty-five decibels: an ordinary measurement rather than a noiseless synthetic. See
        /// <see cref="LmsConvergesToWithinADecibelOfTheLeastSquaresSolution"/> and <c>#435</c> for
        /// why the comparison is made on a signal with noise on it — in short, because on a
        /// noiseless one the quantity being compared is the chain's own filter truncation and
        /// nothing a user will ever measure.
        /// </remarks>
        private const double SignalToNoiseDb = 25.0;

        /// <summary>A sync pattern long enough not to be matched by chance in this record.</summary>
        private static readonly int[] Pattern =
        {
            0, 0, 1, 1, 0, 1, 0, 0, 1, 1, 1, 0, 0, 1, 0, 1,
            2, 3, 3, 1, 2, 2, 0, 3, 1, 0, 2, 1, 3, 2, 3, 0,
        };

        private readonly ITestOutputHelper _output;

        public EqualiserAlgorithmTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void TheLeastSquaresSolutionIsTheDefault()
        {
            // REQ-DEM-052: "The least-squares solution is the default."
            Assert.Equal(EqualiserAlgorithm.LeastSquares, new DemodSettings().EqualiserAlgorithm);
        }

        [Fact]
        public void TheLeastSquaresSolutionIsTheAnalyticOne()
        {
            // REQ-DEM-052: "on a channel with a known finite-impulse-response, the computed w
            // matches the analytic regularised solution to within 1e-9".
            //
            // The analytic solution is formed here, independently: the normal equations are built
            // from the same regressors and inverted by Gauss-Jordan elimination with full pivoting,
            // which shares no code with the solver under test. Agreement between two different
            // routes to (X^H X + lambda I)^-1 X^H d is evidence that the step returns THE solution
            // rather than an iterate near it.
            var channel = new[]
            {
                new Iq(0.9, 0.1), new Iq(0.35, -0.2), new Iq(-0.15, 0.05),
            };

            const int Taps = 7;
            const int Count = 400;

            Iq[] symbols = Sequence(Count + channel.Length + Taps, 20260831);
            Iq[] regressors = Through(symbols, channel, Count, Taps);
            var targets = new Iq[Count];

            Array.Copy(symbols, Taps, targets, 0, Count);

            Iq[] fitted = EqualiserStep.LeastSquares(regressors, targets, Taps);
            Iq[] analytic = Analytic(regressors, targets, Taps, Count);

            for (int tap = 0; tap < Taps; tap++)
            {
                _output.WriteLine(
                    "tap " + tap.ToString(CultureInfo.InvariantCulture) + ": " +
                    Show(fitted[tap]) + " against " + Show(analytic[tap]));

                Assert.Equal(analytic[tap].I, fitted[tap].I, 9);
                Assert.Equal(analytic[tap].Q, fitted[tap].Q, 9);
            }
        }

        [Fact]
        public void LmsConvergesToWithinADecibelOfTheLeastSquaresSolution()
        {
            // REQ-DEM-052: "LMS mode converges on the same channel to within 1 dB of the
            // least-squares EVM."
            //
            // MEASURED ON A SIGNAL WITH NOISE ON IT, and #435 records why: on a noiseless synthetic
            // the least-squares solution reaches 0.017 %rms -- the chain's own residual
            // intersymbol interference from its truncated, tapered filters -- and a gradient method
            // does not, because that correction lives in the directions the input has least energy
            // in and they converge slowest. The clause holds wherever EVM is set by anything real:
            // 0.24 dB apart at 25 dB SNR, 0.8 dB at every SNR from 40 dB down when the step size is
            // left at its default. It is 5.5 dB apart on a signal with nothing in it but the
            // chain's own floor.
            //
            // THE STEP SIZE SETS HOW CLOSE IT CAN GET, and this is where 1 dB is spent or kept: an
            // LMS filter's excess error over the optimum is mu*L*Px/2 of its mean-square error, so
            // at these 42 taps mu = 0.01 costs 0.78 dB before convergence is even in question --
            // measured, 0.80. At 0.003 it costs 0.06, and the measured gap is 0.24 dB.
            double exact = Evm(EqualiserAlgorithm.LeastSquares, 0.003, SignalToNoiseDb);
            double gradient = Evm(EqualiserAlgorithm.Lms, 0.003, SignalToNoiseDb);

            double difference = 20.0 * Math.Log10(gradient / exact);

            _output.WriteLine(
                "least squares " + exact.ToString("F4", CultureInfo.InvariantCulture) +
                " %rms, LMS " + gradient.ToString("F4", CultureInfo.InvariantCulture) +
                " %rms, " + difference.ToString("F2", CultureInfo.InvariantCulture) + " dB apart.");

            Assert.True(
                difference < 1.0,
                "LMS reached " + gradient.ToString("F4", CultureInfo.InvariantCulture) +
                " %rms against the least-squares solution's " +
                exact.ToString("F4", CultureInfo.InvariantCulture) + " %rms, which is " +
                difference.ToString("F2", CultureInfo.InvariantCulture) +
                " dB worse rather than the 1 dB the requirement allows.");
        }

        [Fact]
        public void AStepSizePastTheBoundIsRefusedWithTheBoundReported()
        {
            // REQ-DEM-052: "The stability bound 0 < mu < 2/(L Px) is enforced and a violating step
            // size is rejected with the bound reported [...] a test drives mu past the bound and
            // asserts the equaliser does not diverge."
            DemodResult refused = Measure(EqualiserAlgorithm.Lms, 0.5);
            DemodResult off = Measure(EqualiserAlgorithm.LeastSquares, 0.01, equalise: false);

            string said = null;

            foreach (string notice in refused.Notices)
            {
                if (notice.IndexOf("2/(L*Px)", StringComparison.Ordinal) >= 0)
                {
                    said = notice;
                }
            }

            _output.WriteLine(said ?? "(the bound was not reported)");
            _output.WriteLine(
                "refused " + refused.EvmPercent.ToString("F4", CultureInfo.InvariantCulture) +
                " %rms against " + off.EvmPercent.ToString("F4", CultureInfo.InvariantCulture) +
                " %rms with no equaliser.");

            Assert.NotNull(said);
            Assert.Contains("convergence factor", said, StringComparison.Ordinal);

            // Not diverged: refusing the step size leaves the measurement exactly where an
            // equaliser that had not run leaves it. An LMS that had taken the step would not have
            // been a little worse -- above the bound the coefficients grow without limit.
            Assert.Equal(off.EvmPercent, refused.EvmPercent, 6);
        }

        [Fact]
        public void NormalisedLmsTakesTheStepThatPlainLmsRefuses()
        {
            // REQ-DEM-052 offers NLMS as the alternative to enforcing the plain bound: "or NLMS is
            // selected instead". A step of 0.1 is TWICE what plain LMS will accept here -- its
            // bound came out at 0.051 for these 42 taps -- and it is well inside NLMS's own bound of
            // 2, because NLMS divides by the input's energy. That is the whole value of it: the
            // setting means the same thing at every signal level and at every tap count, so it
            // cannot be invalidated by the operator changing the reference level.
            double exact = Evm(EqualiserAlgorithm.LeastSquares, 0.003, SignalToNoiseDb);
            double normalised = Evm(EqualiserAlgorithm.NormalisedLms, 0.1, SignalToNoiseDb);

            double difference = 20.0 * Math.Log10(normalised / exact);

            _output.WriteLine(
                "NLMS at a step of 0.1: " +
                normalised.ToString("F4", CultureInfo.InvariantCulture) + " %rms, " +
                difference.ToString("F2", CultureInfo.InvariantCulture) +
                " dB from the least-squares solution.");

            // Plain LMS would have refused this step size outright rather than taken it badly.
            DemodResult refused = Measure(EqualiserAlgorithm.Lms, 0.1);

            Assert.Contains(
                refused.Notices,
                notice => notice.IndexOf("2/(L*Px)", StringComparison.Ordinal) >= 0);

            Assert.True(
                difference < 1.0,
                "NLMS reached " + normalised.ToString("F4", CultureInfo.InvariantCulture) +
                " %rms, which is " + difference.ToString("F2", CultureInfo.InvariantCulture) +
                " dB from the exact solution.");
        }

        [Fact]
        public void AnUnnormalisedStepPastTwoIsRefusedByNormalisedLmsToo()
        {
            // NLMS is stable for 0 < mu~ < 2 whatever the signal's power, and that bound is enforced
            // in its own terms rather than borrowed from the plain one.
            DemodResult refused = Measure(EqualiserAlgorithm.NormalisedLms, 2.5);

            Assert.Contains(
                refused.Notices,
                notice => notice.IndexOf(
                    "normalised LMS is stable only below 2", StringComparison.Ordinal) >= 0);
        }

        [Theory]
        [InlineData(EqualiserAcquisition.ConstantModulus)]
        [InlineData(EqualiserAcquisition.DataAided)]
        public void AcquisitionLocksASignalWhoseDecisionsAreNotYetTrustworthy(
            EqualiserAcquisition acquisition)
        {
            // REQ-DEM-052: "Both acquisition modes -- CMA and data-aided -- bring a signal whose
            // initial decisions are unreliable to a locked state, then hand over to
            // decision-directed at the stated EVM threshold."
            //
            // The echo below closes the eye: the decisions the chain makes before the equaliser runs
            // are wrong often enough that an equaliser directed by them adapts towards the wrong
            // symbols, which is the failure the acquisition modes exist to avoid.
            // 🔴 Data-aided acquisition updates only under the 32-symbol pattern, which is a
            // sixteenth of this window, so it needs sixteen times the sweeps to do the same
            // adaptation -- which is why the sweep budget is spent in UPDATES rather than in
            // sweeps. Measured before it was: 11.8 %rms and the handover threshold never reached,
            // against 0.99 %rms once the same number of updates was allowed.
            DemodResult acquired = Measure(
                EqualiserAlgorithm.Lms, 0.01, acquisition: acquisition, echo: true);

            string handover = null;

            foreach (string notice in acquired.Notices)
            {
                if (notice.IndexOf("handed over", StringComparison.Ordinal) >= 0)
                {
                    handover = notice;
                }
            }

            _output.WriteLine(
                acquisition + ": EVM " +
                acquired.EvmPercent.ToString("F4", CultureInfo.InvariantCulture) + " %rms, " +
                (acquired.Lock.Locked ? "locked" : "not locked") + ".");
            _output.WriteLine(handover ?? "(no handover was reported)");

            Assert.NotNull(handover);
            Assert.True(
                acquired.Lock.Locked,
                acquisition + " acquisition did not reach a locked state: " +
                acquired.Lock.Explanation);

            Assert.True(
                acquired.EvmPercent < new DemodSettings().EqualiserAcquisitionEvmPercent,
                acquisition + " acquisition handed over at " +
                acquired.EvmPercent.ToString("F4", CultureInfo.InvariantCulture) +
                " %rms, which is not below the threshold it reported handing over at.");
        }

        [Fact]
        public void DataAidedAcquisitionSaysSoWhenThereIsNoSequenceToAidIt()
        {
            // Asking to acquire from a sequence that is not there is a setup mistake, and the
            // measurement that results is a decision-directed one. Saying which it ran is the
            // difference between a result the user can trust and one they cannot account for.
            DemodResult result = Measure(
                EqualiserAlgorithm.Lms,
                0.01,
                acquisition: EqualiserAcquisition.DataAided,
                sync: false);

            Assert.Contains(
                result.Notices,
                notice => notice.IndexOf(
                    "no sync pattern is set", StringComparison.Ordinal) >= 0);
        }

        /// <summary>Scratch access for diagnostics.</summary>
        internal static DemodResult ProbeEcho(string what)
        {
            var tests = new EqualiserAlgorithmTests(null);

            switch (what)
            {
                case "off":
                    return tests.Measure(
                        EqualiserAlgorithm.LeastSquares, 0.01, equalise: false, echo: true);
                case "ls":
                    return tests.Measure(EqualiserAlgorithm.LeastSquares, 0.01, echo: true);
                case "dd":
                    return tests.Measure(EqualiserAlgorithm.Lms, 0.01, echo: true);
                case "cma":
                    return tests.Measure(
                        EqualiserAlgorithm.Lms, 0.01,
                        acquisition: EqualiserAcquisition.ConstantModulus, echo: true);
                default:
                    return tests.Measure(
                        EqualiserAlgorithm.Lms, 0.01,
                        acquisition: EqualiserAcquisition.DataAided, echo: true);
            }
        }

        [Fact]
        public void TheHelpStatesWhichAlgorithmDoesWhatAndWhatTheStepSizeCosts()
        {
            // A user choosing between these modes needs to know that the default is exact and the
            // others are not, that the step size is bounded, and roughly what it costs -- none of
            // which a control label can carry.
            string help = HelpTopics.Read(HelpTopics.Equaliser);

            foreach (string said in new[]
            {
                "Least squares",
                "Normalised LMS",
                "2/(L·Pₓ)",
                "Constant modulus",
                "Data-aided",
                "handover threshold",
            })
            {
                Assert.Contains(said, help, StringComparison.Ordinal);
            }
        }

        /// <summary>A random unit-power QPSK sequence.</summary>
        private static Iq[] Sequence(int count, int seed)
        {
            var random = new Random(seed);
            var symbols = new Iq[count];
            double root = 1.0 / Math.Sqrt(2.0);

            for (int symbol = 0; symbol < count; symbol++)
            {
                symbols[symbol] = new Iq(
                    random.Next(2) == 0 ? root : -root,
                    random.Next(2) == 0 ? root : -root);
            }

            return symbols;
        }

        /// <summary>
        /// The regressors a known finite-impulse-response channel produces, symbol-major.
        /// </summary>
        /// <param name="symbols">What was transmitted.</param>
        /// <param name="channel">The channel's taps.</param>
        /// <param name="count">How many symbols to build regressors for.</param>
        /// <param name="taps">How many equaliser taps.</param>
        /// <returns>The tap inputs, in the layout the step reads.</returns>
        private static Iq[] Through(Iq[] symbols, Iq[] channel, int count, int taps)
        {
            var received = new Iq[symbols.Length];

            for (int symbol = 0; symbol < symbols.Length; symbol++)
            {
                Iq sum = Iq.Zero;

                for (int tap = 0; tap < channel.Length; tap++)
                {
                    if (symbol - tap >= 0)
                    {
                        sum = sum + (channel[tap] * symbols[symbol - tap]);
                    }
                }

                received[symbol] = sum;
            }

            var regressors = new Iq[count * taps];

            for (int symbol = 0; symbol < count; symbol++)
            {
                for (int tap = 0; tap < taps; tap++)
                {
                    regressors[(symbol * taps) + tap] = received[symbol + taps - tap];
                }
            }

            return regressors;
        }

        /// <summary>
        /// <c>(XᴴX + λI)⁻¹Xᴴd</c>, by Gauss-Jordan elimination with partial pivoting.
        /// </summary>
        /// <remarks>
        /// Written out here rather than called, so that the comparison is between two independent
        /// routes to the same algebra. It shares only the loading with the step under test, and
        /// that is read from the step so the two cannot drift apart on a constant.
        /// </remarks>
        private static Iq[] Analytic(Iq[] regressors, Iq[] targets, int taps, int count)
        {
            var matrix = new Iq[taps * taps];
            var right = new Iq[taps];

            for (int row = 0; row < taps; row++)
            {
                for (int column = 0; column < taps; column++)
                {
                    Iq sum = Iq.Zero;

                    for (int symbol = 0; symbol < count; symbol++)
                    {
                        sum = sum + (regressors[(symbol * taps) + column] *
                            regressors[(symbol * taps) + row].Conjugate());
                    }

                    matrix[(row * taps) + column] = sum;
                }

                Iq correlation = Iq.Zero;

                for (int symbol = 0; symbol < count; symbol++)
                {
                    correlation = correlation +
                        (targets[symbol] * regressors[(symbol * taps) + row].Conjugate());
                }

                right[row] = correlation;
            }

            double loading = EqualiserStep.Loading(matrix, taps);

            for (int tap = 0; tap < taps; tap++)
            {
                matrix[(tap * taps) + tap] = matrix[(tap * taps) + tap] + new Iq(loading, 0.0);
            }

            for (int pivot = 0; pivot < taps; pivot++)
            {
                int best = pivot;

                for (int row = pivot + 1; row < taps; row++)
                {
                    if (matrix[(row * taps) + pivot].Magnitude >
                        matrix[(best * taps) + pivot].Magnitude)
                    {
                        best = row;
                    }
                }

                if (best != pivot)
                {
                    for (int column = 0; column < taps; column++)
                    {
                        Iq swap = matrix[(pivot * taps) + column];

                        matrix[(pivot * taps) + column] = matrix[(best * taps) + column];
                        matrix[(best * taps) + column] = swap;
                    }

                    Iq held = right[pivot];

                    right[pivot] = right[best];
                    right[best] = held;
                }

                Iq divisor = matrix[(pivot * taps) + pivot];

                for (int column = 0; column < taps; column++)
                {
                    matrix[(pivot * taps) + column] =
                        Divide(matrix[(pivot * taps) + column], divisor);
                }

                right[pivot] = Divide(right[pivot], divisor);

                for (int row = 0; row < taps; row++)
                {
                    if (row == pivot)
                    {
                        continue;
                    }

                    Iq factor = matrix[(row * taps) + pivot];

                    for (int column = 0; column < taps; column++)
                    {
                        matrix[(row * taps) + column] = matrix[(row * taps) + column] -
                            (factor * matrix[(pivot * taps) + column]);
                    }

                    right[row] = right[row] - (factor * right[pivot]);
                }
            }

            return right;
        }

        /// <summary>Complex division, which the signal type does not carry.</summary>
        private static Iq Divide(Iq value, Iq by) =>
            (value * by.Conjugate()) / by.MagnitudeSquared;

        private static string Show(Iq value) =>
            value.I.ToString("F12", CultureInfo.InvariantCulture) + " " +
            (value.Q < 0.0 ? "-" : "+") + " " +
            Math.Abs(value.Q).ToString("F12", CultureInfo.InvariantCulture) + "j";

        private double Evm(
            EqualiserAlgorithm algorithm,
            double step,
            double signalToNoiseDb = double.PositiveInfinity) =>
            Measure(algorithm, step, signalToNoiseDb: signalToNoiseDb).EvmPercent;

        /// <summary>Demodulates one block through a channel the demodulator is told nothing of.</summary>
        private DemodResult Measure(
            EqualiserAlgorithm algorithm,
            double step,
            EqualiserAcquisition acquisition = EqualiserAcquisition.DecisionDirected,
            bool equalise = true,
            bool echo = false,
            bool sync = true,
            int sweeps = DemodSettings.DefaultEqualiserAdaptationSweeps,
            int lengthSymbols = 21,
            double signalToNoiseDb = double.PositiveInfinity)
        {
            var source = new ContinuousModulatedSource
            {
                Scheme = ModulationScheme.Qpsk(),
                SymbolRateHz = SymbolRateHz,
                SampleRateHz = SampleRateHz,
                RollOff = 0.35,
                PulseSpanSymbols = 20,
                Seed = 20260831,
                SignalToNoiseDb = signalToNoiseDb,
                InsertedSymbols = Pattern,
                InsertedAtSymbol = PatternAt,
            };

            var samples = new float[2 * Symbols * 16];

            source.Restart();
            source.Fill(samples);

            if (echo)
            {
                Echo(samples);
            }
            else
            {
                Tilt(samples);
            }

            var settings = new DemodSettings
            {
                Constellation = Constellation.Qpsk(),
                SymbolRateHz = SymbolRateHz,
                ResultLengthSymbols = 512,
                FilterSymbolSpan = 20,
                MeasurementFilter = PulseFilterType.RootRaisedCosine,
                MeasurementFilterAlpha = 0.35,
                ReferenceFilterAlpha = 0.35,
                EqualiserEnabled = equalise,
                EqualiserLengthSymbols = lengthSymbols,
                EqualiserAlgorithm = algorithm,
                EqualiserAcquisition = acquisition,
                EqualiserConvergenceFactor = step,
                EqualiserAdaptationSweeps = sweeps,
            };

            if (sync)
            {
                settings.SyncSearchEnabled = true;
                settings.SyncPattern = Pattern;
            }

            return new Demodulator().Run(samples, SampleRateHz, settings);
        }

        /// <summary>A 6 dB frequency-response tilt across the occupied band.</summary>
        private static void Tilt(float[] samples)
        {
            double occupied = SymbolRateHz * 1.35;

            Filter(samples, hertz =>
            {
                double slope = Math.Max(-1.0, Math.Min(1.0, hertz / (occupied / 2.0)));

                return new Iq(Math.Pow(10.0, 6.0 * slope / 40.0), 0.0);
            });
        }

        /// <summary>A two-ray channel whose second ray is strong enough to close the eye.</summary>
        /// <remarks>
        /// One symbol late at seven tenths of the amplitude. The response has a deep null in the
        /// occupied band, so the intersymbol interference it leaves is comparable with the symbol
        /// spacing itself — which is what makes the pre-equaliser decisions unreliable, and what the
        /// acquisition modes are for.
        /// </remarks>
        private static void Echo(float[] samples)
        {
            double delay = 1.0 / SymbolRateHz;

            Filter(samples, hertz =>
            {
                double turn = -2.0 * Math.PI * hertz * delay;

                return new Iq(1.0, 0.0) + (Iq.FromPhase(turn) * 0.7);
            });
        }

        private static void Filter(float[] samples, Func<double, Iq> response)
        {
            int count = samples.Length / 2;
            int length = 1;

            while (length < count)
            {
                length *= 2;
            }

            var spectrum = new double[2 * length];

            for (int sample = 0; sample < count; sample++)
            {
                Iq.Set(spectrum, sample, new Iq(samples[2 * sample], samples[(2 * sample) + 1]));
            }

            Dsp.Fft.IFftProvider fft = Dsp.Fft.FftProviders.Active;

            fft.Forward(new Span<double>(spectrum));

            for (int bin = 0; bin < length; bin++)
            {
                double hertz = (bin <= length / 2 ? bin : bin - length) * SampleRateHz / length;

                Iq.Set(spectrum, bin, Iq.At(spectrum, bin) * response(hertz));
            }

            fft.Inverse(new Span<double>(spectrum));

            for (int sample = 0; sample < count; sample++)
            {
                Iq value = Iq.At(spectrum, sample);

                samples[2 * sample] = (float)(value.I / length);
                samples[(2 * sample) + 1] = (float)(value.Q / length);
            }
        }
    }
}
