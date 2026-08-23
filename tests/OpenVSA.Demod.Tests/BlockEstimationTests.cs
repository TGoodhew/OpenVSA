using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using OpenVSA.Demod.Chain;
using OpenVSA.Demod.Results;
using OpenVSA.Demod.Tests.Signals;
using Xunit;
using Xunit.Abstractions;

namespace OpenVSA.Demod.Tests
{
    /// <summary>
    /// <c>REQ-DEM-002</c>: steps 3 and 8 estimate over the whole block rather than tracking, and
    /// <c>REQ-DEM-030</c>: the symbol rate is supplied and applied exactly as entered.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>What these tests can and cannot show.</strong> <c>REQ-DEM-002</c> is marked a
    /// <em>design choice</em> and says so in its own rationale: the symbol-rate-error signature is
    /// "a necessary, not a sufficient, condition", because a converged tracking loop with a one-shot
    /// frequency estimate and a mid-block phase reference would produce a similar shape. So a
    /// failure here is conclusive and a pass is corroborating, and these tests are written to be
    /// read that way — the assertions are on the shape the specification names, not on an inference
    /// about the implementation.
    /// </para>
    /// <para>
    /// <strong>The one that <em>would</em> tell them apart is here too.</strong> A causal loop has
    /// to acquire, and while it is acquiring its output is worse; a block estimator has no such
    /// phase because it sees the whole block before it answers.
    /// <see cref="ThereIsNoSettlingTransientAtTheStartOfTheBlock"/> is that test, and it is the one
    /// the requirement's rationale actually leans on.
    /// </para>
    /// </remarks>
    public class BlockEstimationTests
    {
        /// <summary>How many bins the block is divided into to see the profile.</summary>
        /// <remarks>
        /// Sixteen over a thousand symbols: 64 symbols a bin, enough for the RMS in each to be
        /// steady and enough bins for a straight line through them to mean something.
        /// </remarks>
        private const int Bins = 16;

        private readonly ITestOutputHelper _output;

        public BlockEstimationTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public void TheSymbolRateErrorSignatureAppears()
        {
            // REQ-DEM-030's acceptance criterion, word for word: "With a deliberate symbol-rate
            // error of 100 ppm, EVM versus symbol index shall exhibit a minimum near the centre of
            // the Result Length, growing approximately linearly toward both ends."
            IReadOnlyList<double> profile = Profile(100.0);

            Report(100.0, profile);

            int minimum = ArgMin(profile);

            Assert.InRange(minimum, (Bins / 2) - 2, (Bins / 2) + 1);

            // Growing toward BOTH ends, not merely somewhere: each half is checked on its own, so a
            // profile that rose on one side and was flat on the other could not pass.
            Assert.True(
                profile[0] > profile[minimum] * 4.0,
                "The first bin is " + profile[0] + " %rms against a minimum of " + profile[minimum] + ".");

            Assert.True(
                profile[Bins - 1] > profile[minimum] * 4.0,
                "The last bin is " + profile[Bins - 1] + " %rms against a minimum of " +
                profile[minimum] + ".");

            // "Approximately linearly": EVM against distance from the minimum is fitted with a
            // straight line, and the fit has to be a good one. A V that curved would pass the two
            // assertions above and fail this.
            double slope;
            double quality;

            Fit(profile, minimum, out slope, out quality);

            _output.WriteLine(
                "slope " + slope.ToString("F4") + " %rms per bin, R² " + quality.ToString("F4"));

            Assert.True(slope > 0.0);
            Assert.True(quality > 0.9, "The straight-line fit's R² was only " + quality + ".");
        }

        [Fact]
        public void TheSignatureGrowsInProportionToTheErrorInjected()
        {
            // The signature is not just a shape: its size is set by the error. A demodulator that
            // produced a V of its own making -- a windowing artefact, an edge effect -- would give
            // the same V whatever the symbol rate was, and this is what separates the two.
            double half = SlopeAt(50.0);
            double one = SlopeAt(100.0);
            double two = SlopeAt(200.0);

            _output.WriteLine(
                "slopes: 50 ppm " + half.ToString("F4") + ", 100 ppm " + one.ToString("F4") +
                ", 200 ppm " + two.ToString("F4"));

            Assert.InRange(one / half, 1.7, 2.3);
            Assert.InRange(two / one, 1.7, 2.3);
        }

        [Fact]
        public void TheSymbolRateIsAppliedExactlyAsEnteredAndNotCorrected()
        {
            // REQ-DEM-030's substance. If the demodulator estimated or corrected the symbol rate,
            // the 100 ppm error would be taken out and the EVM would be the clean signal's. It is
            // an order of magnitude worse, which is the error still being there.
            double clean = Overall(0.0);
            double erroneous = Overall(100.0);

            _output.WriteLine(
                "EVM at 0 ppm " + clean.ToString("F3") + " %rms, at 100 ppm " +
                erroneous.ToString("F3") + " %rms");

            Assert.True(clean < 0.5);
            Assert.True(
                erroneous > clean * 8.0,
                "100 ppm of symbol-rate error cost only " + erroneous + " %rms against " + clean +
                " clean, which is what correcting it would look like.");
        }

        [Fact]
        public void ThereIsNoSettlingTransientAtTheStartOfTheBlock()
        {
            // This is the test REQ-DEM-002's rationale rests on: "a causal tracking loop would
            // additionally show a settling transient at the start that the documentation does not
            // describe". The signal carries a carrier offset and a phase, so there is something for
            // a loop to acquire; a block estimator answers for the first symbol with the same
            // information it has for the last.
            IReadOnlyList<double> profile = Profile(0.0);

            Report(0.0, profile);

            double[] sorted = profile.OrderBy(value => value).ToArray();
            double median = sorted[sorted.Length / 2];

            Assert.True(
                profile[0] < median * 1.5,
                "The first bin of the block was " + profile[0] + " %rms against a median of " +
                median + ", which is what acquiring would look like.");

            // And the whole profile is flat, not merely its first bin: nothing about where a symbol
            // sits in the block changes how well it is estimated.
            Assert.True(
                sorted[sorted.Length - 1] < sorted[0] * 2.0,
                "The profile ran from " + sorted[0] + " to " + sorted[sorted.Length - 1] + " %rms.");
        }

        [Fact]
        public void ItLocksOnAShortBurst()
        {
            // The other documented behaviour REQ-DEM-002 is inferred from. Fifty symbols is
            // REQ-DEM-031's stated minimum for QPSK, and a loop with a settling time comparable to
            // the block would have nothing left to measure.
            var source = Source();

            float[] record = source.Generate(120);

            var settings = new DemodSettings
            {
                SymbolRateHz = source.SymbolRateHz,
                ResultLengthSymbols = 50,
            };

            DemodResult result = new Demodulator().Run(record, source.SampleRateHz, settings);

            _output.WriteLine(
                result.Trace.SymbolCount + " symbols, EVM " + result.EvmPercent.ToString("F3") +
                " %rms, " + result.Convergence);

            Assert.Equal(50, result.Trace.SymbolCount);
            Assert.True(result.Converged);
            Assert.True(
                result.EvmPercent < 1.0,
                "EVM on a fifty-symbol block was " + result.EvmPercent + " %rms.");
        }

        [Fact]
        public void NothingInTheDemodulatorOffersToEstimateTheSymbolRate()
        {
            // REQ-DEM-030 is a prohibition as much as a setting, and a prohibition erodes by
            // someone adding the helpful thing. The symbol rate arrives as a setting and there is
            // no other way in.
            var offenders = new List<string>();

            foreach (Type type in Assembly.Load("OpenVSA.Demod").GetTypes())
            {
                foreach (MemberInfo member in type.GetMembers(
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance |
                    BindingFlags.Static | BindingFlags.DeclaredOnly))
                {
                    string name = member.Name;

                    bool estimates =
                        name.IndexOf("SymbolRate", StringComparison.Ordinal) >= 0 &&
                        (name.IndexOf("Estimate", StringComparison.Ordinal) >= 0 ||
                         name.IndexOf("Recover", StringComparison.Ordinal) >= 0 ||
                         name.IndexOf("Search", StringComparison.Ordinal) >= 0 ||
                         name.IndexOf("Correct", StringComparison.Ordinal) >= 0);

                    if (estimates)
                    {
                        offenders.Add(type.Name + "." + name);
                    }
                }
            }

            Assert.True(
                offenders.Count == 0,
                "REQ-DEM-030: the symbol rate is supplied, never estimated. " +
                string.Join(", ", offenders.ToArray()));
        }

        private static QpskSource Source() =>
            new QpskSource(5)
            {
                SymbolRateHz = 1e6,
                SampleRateHz = 5.3e6,
                CarrierOffsetHz = 3000.0,
                PhaseRadians = 0.4,
                Amplitude = 0.4,
            };

        /// <summary>Demodulates a clean signal with the symbol rate entered wrongly by a given error.</summary>
        /// <param name="ppm">The symbol-rate error to inject, in parts per million.</param>
        private static DemodResult Demodulate(double ppm)
        {
            var source = Source();

            float[] record = source.Generate(1600);

            var settings = new DemodSettings
            {
                // The error is injected by entering the wrong rate, not by generating a wrong
                // signal: REQ-DEM-030's subject is what the demodulator does with the number it is
                // given, and this is that number being wrong.
                SymbolRateHz = source.SymbolRateHz * (1.0 + (ppm * 1e-6)),
                ResultLengthSymbols = 1024,
            };

            return new Demodulator().Run(record, source.SampleRateHz, settings);
        }

        private static double Overall(double ppm) => Demodulate(ppm).EvmPercent;

        private static IReadOnlyList<double> Profile(double ppm) => Profile(Demodulate(ppm).Trace);

        /// <summary>RMS EVM within each of <see cref="Bins"/> equal stretches of the block.</summary>
        /// <param name="trace">The result.</param>
        /// <returns>One percentage per bin, in symbol order.</returns>
        /// <remarks>
        /// Referenced to the RMS of the ideal points, which is <c>ErrorSummary</c>'s own convention,
        /// so a bin's figure means the same thing as the summary's overall EVM.
        /// </remarks>
        private static IReadOnlyList<double> Profile(SymbolTrace trace)
        {
            double reference = 0.0;

            foreach (ConstellationPoint ideal in trace.Ideal)
            {
                reference += (ideal.I * ideal.I) + (ideal.Q * ideal.Q);
            }

            reference = Math.Sqrt(reference / trace.SymbolCount);

            var profile = new List<double>(Bins);
            int per = trace.SymbolCount / Bins;

            for (int bin = 0; bin < Bins; bin++)
            {
                double sum = 0.0;

                for (int symbol = bin * per; symbol < (bin + 1) * per; symbol++)
                {
                    ConstellationPoint error = trace.ErrorAt(symbol);

                    sum += (error.I * error.I) + (error.Q * error.Q);
                }

                profile.Add(Math.Sqrt(sum / per) / reference * 100.0);
            }

            return profile;
        }

        private static double SlopeAt(double ppm)
        {
            IReadOnlyList<double> profile = Profile(ppm);

            double slope;
            double quality;

            Fit(profile, ArgMin(profile), out slope, out quality);

            return slope;
        }

        /// <summary>Fits EVM against distance from the minimum with a straight line.</summary>
        /// <param name="profile">The binned EVM.</param>
        /// <param name="minimum">Which bin the minimum fell in.</param>
        /// <param name="slope">The fitted slope, in per cent per bin.</param>
        /// <param name="quality">The coefficient of determination.</param>
        private static void Fit(
            IReadOnlyList<double> profile, int minimum, out double slope, out double quality)
        {
            double sumX = 0.0;
            double sumY = 0.0;
            double sumXy = 0.0;
            double sumXx = 0.0;

            for (int bin = 0; bin < profile.Count; bin++)
            {
                double distance = Math.Abs(bin - minimum);

                sumX += distance;
                sumY += profile[bin];
                sumXy += distance * profile[bin];
                sumXx += distance * distance;
            }

            int count = profile.Count;
            double determinant = (count * sumXx) - (sumX * sumX);

            slope = ((count * sumXy) - (sumX * sumY)) / determinant;

            double intercept = (sumY - (slope * sumX)) / count;
            double mean = sumY / count;
            double residual = 0.0;
            double total = 0.0;

            for (int bin = 0; bin < profile.Count; bin++)
            {
                double predicted = intercept + (slope * Math.Abs(bin - minimum));

                residual += (profile[bin] - predicted) * (profile[bin] - predicted);
                total += (profile[bin] - mean) * (profile[bin] - mean);
            }

            quality = total < 1e-18 ? 0.0 : 1.0 - (residual / total);
        }

        private static int ArgMin(IReadOnlyList<double> profile)
        {
            int best = 0;

            for (int bin = 1; bin < profile.Count; bin++)
            {
                if (profile[bin] < profile[best])
                {
                    best = bin;
                }
            }

            return best;
        }

        private void Report(double ppm, IReadOnlyList<double> profile)
        {
            _output.WriteLine(ppm.ToString("F0") + " ppm, EVM by sixteenth of the block:");

            for (int bin = 0; bin < profile.Count; bin++)
            {
                _output.WriteLine(
                    "  " + bin.ToString().PadLeft(2) + "  " + profile[bin].ToString("F3") +
                    " %rms  " + new string('#', (int)Math.Round(profile[bin] * 4.0)));
            }
        }
    }
}
