using System;
using System.Collections.Generic;
using System.Globalization;
using OpenVSA.Demod.Chain;
using OpenVSA.Demod.Help;
using OpenVSA.Demod.Signal;
using OpenVSA.Synthesis;
using Xunit;
using Xunit.Abstractions;

namespace OpenVSA.Demod.Tests
{
    /// <summary>
    /// The filter group: <c>REQ-DEM-020</c>, <c>REQ-DEM-021</c>, <c>REQ-DEM-022</c>,
    /// <c>REQ-DEM-022a</c> and <c>REQ-DEM-023</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Five requirements and one piece of work, because they are five statements about the same
    /// object: what the filters are, which two positions they go in, what the formulas say, how they
    /// are scaled, and how long they are. Splitting them would have meant deciding the normalisation
    /// twice.
    /// </para>
    /// <para>
    /// <strong>Every formula here is written out again rather than called.</strong>
    /// <c>REQ-DEM-022</c> asks for coefficients checked "independently rather than by the
    /// implementation under test", and a test that asked the implementation what it thought would
    /// pass whatever it thought.
    /// </para>
    /// </remarks>
    public class PulseFilterCatalogueTests
    {
        /// <summary>
        /// The EDGE pulse from 3GPP TS 45.004, evaluated by the script in
        /// <c>evidence/req-dem-021/</c> and pasted here so the comparison is visible.
        /// </summary>
        /// <remarks>
        /// <para>
        /// t/T from −2.5 to +2.5 in steps of an eighth, and the value of <c>c₀(t + 5T/2)</c>. The
        /// script computes the phase integral by composite Simpson on g; the implementation uses the
        /// closed form of the same integral. Two languages, two methods, one published definition —
        /// which is what makes agreement to 1e-6 evidence rather than a tautology.
        /// </para>
        /// <para>
        /// <strong>The lower limit of that integral is a reading of the standard.</strong> Both
        /// sides take it from far below zero rather than from zero as the text writes it, because
        /// c₀ is provably symmetric and the literal reading is not, by 1.2e-4. The two readings
        /// differ by 6.1e-5 — above this comparison's own tolerance — so it had to be chosen rather
        /// than fallen into. It is on the issue, and measured in the evidence.
        /// </para>
        /// </remarks>
        private const string PublishedEdgePulse = @"
-2.500000 0.000001922925
-2.375000 0.000010794141
-2.250000 0.000051327471
-2.125000 0.000208841076
-2.000000 0.000734597513
-1.875000 0.002255896078
-1.750000 0.006102362405
-1.625000 0.014657792654
-1.500000 0.031501194282
-1.375000 0.061046497878
-1.250000 0.107579615057
-1.125000 0.173957633642
-1.000000 0.260457371556
-0.875000 0.364141727752
-0.750000 0.478867063544
-0.625000 0.595976351166
-0.500000 0.705700623763
-0.375000 0.799004106405
-0.250000 0.869158938331
-0.125000 0.912292979040
+0.000000 0.926795718405
+0.125000 0.912292979040
+0.250000 0.869158938331
+0.375000 0.799004106405
+0.500000 0.705700623763
+0.625000 0.595976351166
+0.750000 0.478867063544
+0.875000 0.364141727752
+1.000000 0.260457371555
+1.125000 0.173957633642
+1.250000 0.107579615056
+1.375000 0.061046497878
+1.500000 0.031501194283
+1.625000 0.014657792654
+1.750000 0.006102362405
+1.875000 0.002255896078
+2.000000 0.000734597513
+2.125000 0.000208841076
+2.250000 0.000051327471
+2.375000 0.000010794141
+2.500000 0.000001922925
";

        private readonly ITestOutputHelper _output;

        public PulseFilterCatalogueTests(ITestOutputHelper output)
        {
            _output = output;
        }

        public static IEnumerable<object[]> EveryType()
        {
            foreach (PulseFilterType type in Enum.GetValues(typeof(PulseFilterType)))
            {
                yield return new object[] { type };
            }
        }

        // ---- REQ-DEM-021: the catalogue -------------------------------------------------------

        [Fact]
        public void TheCatalogueHasTheNineTypesTheRequirementLists()
        {
            // Root Raised Cosine, Raised Cosine, Gaussian, EDGE, Half Sine, Rectangular, Low-pass,
            // User-defined FIR, None. Counted from the enumeration so that a type added without a
            // requirement, or a requirement's type never added, shows up here.
            var types = new List<string>();

            foreach (PulseFilterType type in Enum.GetValues(typeof(PulseFilterType)))
            {
                types.Add(type.ToString());
            }

            _output.WriteLine(string.Join(", ", types));

            Assert.Equal(9, types.Count);
        }

        [Theory]
        [MemberData(nameof(EveryType))]
        public void EveryTypeIsSelectableForBothRoles(PulseFilterType type)
        {
            // "All nine filter types are selectable for both measurement and reference roles."
            PulseFilter filter = Build(type);

            foreach (FilterRole role in new[] { FilterRole.Measurement, FilterRole.Reference })
            {
                double[] taps = filter.Taps(8, 8, role);

                Assert.Equal(129, taps.Length);

                double magnitude = 0.0;

                foreach (double tap in taps)
                {
                    Assert.False(double.IsNaN(tap), type + " produced a NaN tap in the " + role + " role.");
                    magnitude += Math.Abs(tap);
                }

                Assert.True(magnitude > 0.0, type + " produced nothing but zeroes.");
            }

            _output.WriteLine(filter.ToString());
        }

        [Fact]
        public void EachParameterDemonstrablyChangesTheResponse()
        {
            // "with alpha exposed for RC/RRC and BT for Gaussian, and each parameter demonstrably
            // changing the response". Demonstrated as a difference in the taps rather than asserted.
            AssertDiffers(
                PulseFilter.RootRaisedCosine(0.2), PulseFilter.RootRaisedCosine(0.5), "RRC alpha");

            AssertDiffers(
                PulseFilter.RaisedCosine(0.2), PulseFilter.RaisedCosine(0.5), "RC alpha");

            AssertDiffers(
                PulseFilter.Gaussian(0.3), PulseFilter.Gaussian(0.5), "Gaussian BT");

            AssertDiffers(
                PulseFilter.LowPass(0.5), PulseFilter.LowPass(0.8), "low-pass cutoff");
        }

        [Fact]
        public void TheEdgeFilterIsTheLinearisedGmskPulseOfTheStandard()
        {
            // "The EDGE filter is a distinct type whose coefficients match the linearised-GMSK main
            // pulse c0(t) of 3GPP TS 45.004 to within 1e-6 — a test compares against those published
            // coefficients".
            PulseFilter edge = PulseFilter.Edge();

            double worst = 0.0;
            double at = 0.0;
            int compared = 0;

            foreach (string line in PublishedEdgePulse.Split('\n'))
            {
                string trimmed = line.Trim();

                if (trimmed.Length == 0)
                {
                    continue;
                }

                string[] columns = trimmed.Split(' ');

                double t = double.Parse(columns[0], CultureInfo.InvariantCulture);
                double published = double.Parse(columns[1], CultureInfo.InvariantCulture);
                double error = Math.Abs(edge.At(t) - published);

                if (error > worst)
                {
                    worst = error;
                    at = t;
                }

                compared++;
            }

            _output.WriteLine(
                compared + " published coefficients, worst disagreement " +
                worst.ToString("E3", CultureInfo.InvariantCulture) + " at t = " +
                at.ToString("F3", CultureInfo.InvariantCulture) + "T");

            Assert.Equal(41, compared);
            Assert.True(worst < 1e-6, "The EDGE pulse differs from the standard's by " + worst + ".");
        }

        [Fact]
        public void NoGaussianIsTheEdgePulseAtAnyBandwidthTime()
        {
            // "and fails a Gaussian approximation at any BT, since substituting one is the specific
            // error this note exists to prevent". Swept rather than asserted: what is reported is
            // how near the nearest one gets, so the claim is a measurement.
            PulseFilter edge = PulseFilter.Edge();

            double nearest = double.MaxValue;
            double nearestBt = 0.0;

            for (int step = 1; step <= 200; step++)
            {
                double bt = step * 0.01;
                PulseFilter gaussian = PulseFilter.Gaussian(bt);

                // Both scaled to unit peak, so the comparison is of shape rather than of scale --
                // the most generous reading of "is it a Gaussian" there is.
                double edgePeak = edge.At(0.0);
                double gaussianPeak = gaussian.At(0.0);

                double sum = 0.0;
                int points = 0;

                for (int sample = -40; sample <= 40; sample++)
                {
                    double t = sample / 16.0;
                    double difference =
                        (edge.At(t) / edgePeak) - (gaussian.At(t) / gaussianPeak);

                    sum += difference * difference;
                    points++;
                }

                double rms = Math.Sqrt(sum / points);

                if (rms < nearest)
                {
                    nearest = rms;
                    nearestBt = bt;
                }
            }

            _output.WriteLine(
                "the nearest Gaussian is BT " +
                nearestBt.ToString("F2", CultureInfo.InvariantCulture) + ", and it is out by " +
                nearest.ToString("F5", CultureInfo.InvariantCulture) +
                " rms against a pulse of unit peak");

            Assert.True(
                nearest > 1e-6 * 1000.0,
                "A Gaussian at BT " + nearestBt + " came within " + nearest +
                " of the EDGE pulse, which would make the distinction this test exists for a " +
                "distinction without a difference.");
        }

        [Fact]
        public void NoneLeavesTheInputUntouched()
        {
            // "None applies no shaping, verified by an output identical to the input."
            var input = new double[64];
            var random = new Random(20260824);

            for (int sample = 0; sample < input.Length; sample++)
            {
                input[sample] = random.NextDouble() - 0.5;
            }

            double[] taps = PulseFilter.None().Taps(4, 8, FilterRole.Measurement);
            double[] output = PulseShapingProbe.Convolve(input, taps);

            for (int sample = 0; sample < input.Length; sample++)
            {
                Assert.Equal(input[sample], output[sample], 15);
            }
        }

        // ---- REQ-DEM-022: the mathematics -----------------------------------------------------

        [Theory]
        [InlineData(0.0)]
        [InlineData(0.22)]
        [InlineData(0.35)]
        [InlineData(1.0)]
        public void EveryRaisedCosineTapMatchesTheFormula(double alpha)
        {
            // h_RC(t) = sinc(t/T) · cos(παt/T) / (1 − (2αt/T)²), evaluated here from the requirement
            // rather than from the code under test.
            double worst = 0.0;

            for (int sample = -8 * 16; sample <= 8 * 16; sample++)
            {
                double t = sample / 16.0;

                if (Singular(t, alpha, 0.5))
                {
                    continue;
                }

                double expected = Sinc(t) * Math.Cos(Math.PI * alpha * t) /
                    (1.0 - ((2.0 * alpha * t) * (2.0 * alpha * t)));

                worst = Math.Max(worst, Relative(PulseFilter.RaisedCosine(alpha).At(t), expected));
            }

            _output.WriteLine(
                "RC alpha " + alpha.ToString("F2", CultureInfo.InvariantCulture) +
                ": worst relative difference " + worst.ToString("E2", CultureInfo.InvariantCulture));

            Assert.True(worst < 1e-12, "The raised cosine differs from its formula by " + worst + ".");
        }

        [Theory]
        [InlineData(0.22)]
        [InlineData(0.35)]
        [InlineData(1.0)]
        public void EveryRootRaisedCosineTapMatchesTheFormula(double alpha)
        {
            double worst = 0.0;

            for (int sample = -8 * 16; sample <= 8 * 16; sample++)
            {
                double t = sample / 16.0;

                if (Math.Abs(t) < 1e-9 || Singular(t, alpha, 0.25))
                {
                    continue;
                }

                double expected =
                    (Math.Sin(Math.PI * t * (1.0 - alpha)) +
                     (4.0 * alpha * t * Math.Cos(Math.PI * t * (1.0 + alpha)))) /
                    (Math.PI * t * (1.0 - ((4.0 * alpha * t) * (4.0 * alpha * t))));

                worst = Math.Max(
                    worst, Relative(PulseFilter.RootRaisedCosine(alpha).At(t), expected));
            }

            _output.WriteLine(
                "RRC alpha " + alpha.ToString("F2", CultureInfo.InvariantCulture) +
                ": worst relative difference " + worst.ToString("E2", CultureInfo.InvariantCulture));

            Assert.True(worst < 1e-12);
        }

        [Theory]
        [InlineData(0.3)]
        [InlineData(0.5)]
        public void EveryGaussianTapMatchesTheFormula(double bandwidthTime)
        {
            // h_G(t) = exp(−t²/2σ²T²) / (√(2π)σT), σ = √(ln2)/(2π·BT).
            double sigma = Math.Sqrt(Math.Log(2.0)) / (2.0 * Math.PI * bandwidthTime);
            double worst = 0.0;

            for (int sample = -8 * 16; sample <= 8 * 16; sample++)
            {
                double t = sample / 16.0;

                double expected = Math.Exp(-(t * t) / (2.0 * sigma * sigma)) /
                    (Math.Sqrt(2.0 * Math.PI) * sigma);

                worst = Math.Max(
                    worst, Relative(PulseFilter.Gaussian(bandwidthTime).At(t), expected));
            }

            _output.WriteLine(
                "Gaussian BT " + bandwidthTime.ToString("F2", CultureInfo.InvariantCulture) +
                ": worst relative difference " + worst.ToString("E2", CultureInfo.InvariantCulture));

            Assert.True(worst < 1e-12);
        }

        [Fact]
        public void ZeroRollOffReducesTheRaisedCosineToASinc()
        {
            // "α = 0 reduces the raised cosine to a sinc." And the low-pass at its default cutoff is
            // the same function reached from the other end of the catalogue, which is worth knowing.
            PulseFilter rc = PulseFilter.RaisedCosine(0.0);
            PulseFilter lowPass = PulseFilter.LowPass(0.5);

            for (int sample = -8 * 16; sample <= 8 * 16; sample++)
            {
                double t = sample / 16.0;

                Assert.Equal(Sinc(t), rc.At(t), 12);
                Assert.Equal(Sinc(t), lowPass.At(t), 12);
            }
        }

        [Theory]
        [InlineData(0.22)]
        [InlineData(0.35)]
        [InlineData(0.5)]
        public void TheRemovableSingularitiesAreAnalyticLimitsAndNotEpsilons(double alpha)
        {
            // "Singularities at t=0, t=±T/(2α) (RC) and t=±T/(4α) (RRC) are handled by analytic
            // limits, not epsilon fudging; unit tests assert continuity across those points to
            // 1e-9." Continuity is the test that catches an epsilon: an averaged or fudged value
            // sits off the curve, and approaching from both sides finds it.
            const double Step = 1e-7;

            var places = new List<Tuple<PulseFilter, double, string>>
            {
                Tuple.Create(PulseFilter.RaisedCosine(alpha), 0.0, "RC at t = 0"),
                Tuple.Create(
                    PulseFilter.RaisedCosine(alpha), 1.0 / (2.0 * alpha), "RC at t = +T/2a"),
                Tuple.Create(
                    PulseFilter.RaisedCosine(alpha), -1.0 / (2.0 * alpha), "RC at t = -T/2a"),
                Tuple.Create(PulseFilter.RootRaisedCosine(alpha), 0.0, "RRC at t = 0"),
                Tuple.Create(
                    PulseFilter.RootRaisedCosine(alpha), 1.0 / (4.0 * alpha), "RRC at t = +T/4a"),
                Tuple.Create(
                    PulseFilter.RootRaisedCosine(alpha), -1.0 / (4.0 * alpha), "RRC at t = -T/4a"),
                Tuple.Create(PulseFilter.Gaussian(0.3), 0.0, "Gaussian at t = 0"),
            };

            foreach (Tuple<PulseFilter, double, string> place in places)
            {
                double centre = place.Item1.At(place.Item2);
                double before = place.Item1.At(place.Item2 - Step);
                double after = place.Item1.At(place.Item2 + Step);

                double gap = Math.Max(
                    Math.Abs(centre - before), Math.Abs(centre - after));

                _output.WriteLine(
                    place.Item3 + " (alpha " + alpha.ToString("F2", CultureInfo.InvariantCulture) +
                    "): value " + centre.ToString("F9", CultureInfo.InvariantCulture) +
                    ", discontinuity " + gap.ToString("E2", CultureInfo.InvariantCulture));

                Assert.True(
                    gap < 1e-9,
                    place.Item3 + " jumps by " + gap + " across its singularity, which is what a " +
                    "fudged value looks like.");
            }
        }

        // ---- REQ-DEM-022a: one normalisation, and the cascade ---------------------------------

        [Fact]
        public void TheTwoRolesAreNormalisedTheWayTheCodeSaysTheyAre()
        {
            // The requirement asks the implementation to "state which normalisation applies to the
            // measurement filter and which to the reference filter". Stating it is only worth
            // anything if it is true, so this is the statement as a test.
            foreach (PulseFilterType type in Enum.GetValues(typeof(PulseFilterType)))
            {
                PulseFilter filter = Build(type);

                double[] measurement = filter.Taps(8, 8, FilterRole.Measurement);
                double[] reference = filter.Taps(8, 8, FilterRole.Reference);

                double sum = 0.0;

                foreach (double tap in measurement)
                {
                    sum += tap;
                }

                Assert.Equal(1.0, sum, 12);
                Assert.Equal(1.0, reference[reference.Length / 2], 12);
            }
        }

        [Fact]
        public void ACascadeOfTwoRootsIsTheRaisedCosine()
        {
            // "with filters at unit peak and the discrete convolution scaled by 1/sps, a cascade of
            // two RRC filters of equal α matches the corresponding RC filter to < 5e-6 RMS at
            // ±64-symbol span, and to < 1e-3 RMS at the ±8-symbol default."
            Assert.True(Cascade(8) < 1e-3, "At the ±8 default the cascade was out by " + Cascade(8));
            Assert.True(Cascade(64) < 5e-6, "At ±64 the cascade was out by " + Cascade(64));

            _output.WriteLine(
                "cascade against the raised cosine: ±8 " +
                Cascade(8).ToString("E2", CultureInfo.InvariantCulture) + ", ±16 " +
                Cascade(16).ToString("E2", CultureInfo.InvariantCulture) + ", ±32 " +
                Cascade(32).ToString("E2", CultureInfo.InvariantCulture) + ", ±64 " +
                Cascade(64).ToString("E2", CultureInfo.InvariantCulture));
        }

        // ---- REQ-DEM-020: two filters, independently -----------------------------------------

        [Fact]
        public void TheTwoFiltersAreSetIndependentlyInTypeAndInParameter()
        {
            // "a test sets them to different types with different alphas and asserts each takes
            // effect on its own path."
            var settings = new DemodSettings
            {
                SymbolRateHz = 1e6,
                MeasurementFilter = PulseFilterType.RootRaisedCosine,
                MeasurementFilterAlpha = 0.22,
                ReferenceFilter = PulseFilterType.Gaussian,
                ReferenceFilterBandwidthTime = 0.5,
            };

            settings.Validate();

            Assert.Equal(PulseFilterType.RootRaisedCosine, settings.MeasurementPulse.Type);
            Assert.Equal(0.22, settings.MeasurementPulse.Alpha, 12);

            Assert.Equal(PulseFilterType.Gaussian, settings.ReferencePulse.Type);
            Assert.Equal(0.5, settings.ReferencePulse.BandwidthTime, 12);

            // Each on its own path: changing one leaves the other's taps alone.
            double[] reference = settings.ReferencePulse.Taps(8, 8, FilterRole.Reference);

            settings.MeasurementFilterAlpha = 0.9;

            double[] afterwards = settings.ReferencePulse.Taps(8, 8, FilterRole.Reference);

            for (int tap = 0; tap < reference.Length; tap++)
            {
                Assert.Equal(reference[tap], afterwards[tap], 15);
            }
        }

        [Fact]
        public void AMatchedPairHasNoIntersymbolInterferenceAndAMismatchedOneDoes()
        {
            // "The Nyquist relationship is verified numerically ... with zero ISI at symbol centres,
            // whereas a mismatched pair does not."
            double matched = WorstIsi(0.35, 0.35);
            double mismatched = WorstIsi(0.35, 0.9);

            _output.WriteLine(
                "worst response at a neighbouring symbol centre: matched " +
                matched.ToString("E2", CultureInfo.InvariantCulture) + ", mismatched " +
                mismatched.ToString("E2", CultureInfo.InvariantCulture));

            Assert.True(matched < 1e-3, "A matched pair left " + matched + " of ISI.");
            Assert.True(
                mismatched > 20.0 * matched,
                "A mismatched pair left " + mismatched + ", which is not distinguishable from the " +
                "matched pair's " + matched + " — so this test would not catch a mismatch.");
        }

        [Fact]
        public void TheHelpExplainsTheTransmitterAndReceiverSplit()
        {
            // "The help text states the transmitter/receiver split."
            string help = HelpTopics.Read(HelpTopics.Filters);

            Assert.Contains("split between the transmitter and the receiver", help, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("no intersymbol interference", help, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("emulates the receiver", help, StringComparison.OrdinalIgnoreCase);

            // And the catalogue, which REQ-DEM-021 wants a user to be able to read.
            foreach (string named in new[]
            {
                "Root Raised Cosine", "Raised Cosine", "Gaussian", "EDGE", "Half Sine",
                "Rectangular", "Low-pass", "User-defined FIR", "None",
            })
            {
                Assert.Contains(named, help, StringComparison.Ordinal);
            }
        }

        // ---- REQ-DEM-023: length and truncation -----------------------------------------------

        [Fact]
        public void TheDefaultSpanIsTheLeastTheRequirementRecommends()
        {
            // "a documented default of at least 8 symbols each side for RRC".
            Assert.True(
                DemodSettings.DefaultFilterSymbolSpan >= 8,
                "The default span is " + DemodSettings.DefaultFilterSymbolSpan + " symbols.");

            Assert.Equal(
                DemodSettings.DefaultFilterSymbolSpan, new DemodSettings().FilterSymbolSpan);
        }

        [Theory]
        [InlineData(8)]
        [InlineData(16)]
        [InlineData(32)]
        public void TruncationIsWindowedAndQuieterThanCuttingItOff(int span)
        {
            // "Truncation is windowed, not abrupt: the truncated response's stopband sidelobes are
            // below those of a rectangularly truncated filter of the same span."
            double windowed = WorstSidelobe(span, windowed: true);
            double abrupt = WorstSidelobe(span, windowed: false);

            _output.WriteLine(
                "±" + span + " symbols: windowed " +
                windowed.ToString("F1", CultureInfo.InvariantCulture) + " dB, abrupt " +
                abrupt.ToString("F1", CultureInfo.InvariantCulture) + " dB");

            Assert.True(
                windowed < abrupt,
                "At ±" + span + " the windowed filter's sidelobes are " + windowed +
                " dB and an abrupt cut gives " + abrupt + " dB, so the window is buying nothing.");
        }

        [Fact]
        public void ChangingTheSpanDoesNotChangeTheAmplitudeOfACarrier()
        {
            // "normalisation follows REQ-DEM-022a so changing the span does not change the measured
            // amplitude of a CW tone by more than 0.01 dB". It is not 0.01 dB, it is exact: the
            // measurement role is normalised to unit DC gain, so an unmodulated carrier passes at
            // the level it arrived at whatever the span.
            double worst = 0.0;

            for (int span = 4; span <= 32; span++)
            {
                double[] taps = PulseFilter.RootRaisedCosine(0.35)
                    .Taps(8, span, FilterRole.Measurement);

                double gain = 0.0;

                foreach (double tap in taps)
                {
                    gain += tap;
                }

                worst = Math.Max(worst, Math.Abs(20.0 * Math.Log10(gain)));
            }

            _output.WriteLine(
                "worst deviation of a CW tone's amplitude across spans 4 to 32: " +
                worst.ToString("E2", CultureInfo.InvariantCulture) + " dB");

            Assert.True(worst < 0.01, "A CW tone moved by " + worst + " dB with the span.");
        }

        [Fact]
        public void ShorteningTheSpanDegradesEvmInTheDirectionTheTradePredicts()
        {
            // "Reducing the span degrades EVM on a known-clean signal in the direction and magnitude
            // the filter-span/accuracy trade predicts."
            var source = new ContinuousModulatedSource
            {
                Scheme = ModulationScheme.Qpsk(),
                SymbolRateHz = 1e6,
                SampleRateHz = 16e6,
                RollOff = 0.35,
                PulseSpanSymbols = 20,
                Seed = 20260824,
            };

            var samples = new float[2 * (int)Math.Ceiling(4000 * source.SamplesPerSymbol)];

            source.Fill(samples);

            double shortSpan = Evm(samples, source, 4);
            double defaultSpan = Evm(samples, source, DemodSettings.DefaultFilterSymbolSpan);
            double longSpan = Evm(samples, source, 20);

            _output.WriteLine(
                "EVM against filter span: ±4 " +
                shortSpan.ToString("F4", CultureInfo.InvariantCulture) + " %rms, ±" +
                DemodSettings.DefaultFilterSymbolSpan + " " +
                defaultSpan.ToString("F4", CultureInfo.InvariantCulture) + " %rms, ±20 " +
                longSpan.ToString("F4", CultureInfo.InvariantCulture) + " %rms");

            Assert.True(
                shortSpan > defaultSpan,
                "A four-symbol filter read " + shortSpan + " %rms against the default's " +
                defaultSpan + " %rms, so shortening it cost nothing — which the trade says it must.");

            Assert.True(longSpan < defaultSpan);
        }

        [Fact]
        public void TheHelpCarriesTheSpanAccuracyTrade()
        {
            // "and that trade appears in the user help", which is the half of this requirement a
            // test can most easily be let to forget.
            string help = HelpTopics.Read(HelpTopics.Filters);

            Assert.Contains("0.287 %rms", help, StringComparison.Ordinal);
            Assert.Contains("0.020 %rms", help, StringComparison.Ordinal);
            Assert.Contains("not monotone", help, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("8 symbols", help, StringComparison.Ordinal);
        }

        // ---- helpers, each written out rather than borrowed ------------------------------------

        private static PulseFilter Build(PulseFilterType type)
        {
            switch (type)
            {
                case PulseFilterType.RootRaisedCosine:
                    return PulseFilter.RootRaisedCosine(0.35);

                case PulseFilterType.RaisedCosine:
                    return PulseFilter.RaisedCosine(0.35);

                case PulseFilterType.Gaussian:
                    return PulseFilter.Gaussian(0.3);

                case PulseFilterType.Edge:
                    return PulseFilter.Edge();

                case PulseFilterType.HalfSine:
                    return PulseFilter.HalfSine();

                case PulseFilterType.Rectangular:
                    return PulseFilter.Rectangular();

                case PulseFilterType.LowPass:
                    return PulseFilter.LowPass(0.5);

                case PulseFilterType.None:
                    return PulseFilter.None();

                case PulseFilterType.UserDefined:
                    return PulseFilter.UserDefined(new[] { 0.25, 0.5, 1.0, 0.5, 0.25 }, 2);

                default:
                    throw new InvalidOperationException("No filter for " + type + ".");
            }
        }

        private void AssertDiffers(PulseFilter first, PulseFilter second, string what)
        {
            double[] one = first.Taps(8, 8, FilterRole.Reference);
            double[] two = second.Taps(8, 8, FilterRole.Reference);

            double worst = 0.0;

            for (int tap = 0; tap < one.Length; tap++)
            {
                worst = Math.Max(worst, Math.Abs(one[tap] - two[tap]));
            }

            _output.WriteLine(what + " changed the response by " +
                worst.ToString("F4", CultureInfo.InvariantCulture) + " at its worst tap");

            Assert.True(worst > 1e-3, what + " changed nothing, so it is not a parameter.");
        }

        private static double Sinc(double x)
        {
            if (Math.Abs(x) < 1e-12)
            {
                return 1.0;
            }

            return Math.Sin(Math.PI * x) / (Math.PI * x);
        }

        private static bool Singular(double t, double alpha, double multiple)
        {
            if (alpha < 1e-12)
            {
                return false;
            }

            return Math.Abs(Math.Abs(t) - (multiple / alpha)) < 1e-6;
        }

        private static double Relative(double measured, double expected)
        {
            double scale = Math.Max(Math.Abs(expected), 1e-6);

            return Math.Abs(measured - expected) / scale;
        }

        /// <summary>RMS difference between a cascade of two roots and the raised cosine.</summary>
        private static double Cascade(int span)
        {
            const int Sps = 16;

            // Unit peak, which is the convention REQ-DEM-022a states this identity against, and
            // rectangular -- the identity is a statement about the filter functions rather than
            // about how a measurement truncates them.
            int half = span * Sps;
            var root = new double[(2 * half) + 1];

            for (int tap = 0; tap < root.Length; tap++)
            {
                root[tap] = PulseFilter.RootRaisedCosine(0.35).At((tap - half) / (double)Sps);
            }

            var composite = new double[(2 * root.Length) - 1];

            for (int first = 0; first < root.Length; first++)
            {
                for (int second = 0; second < root.Length; second++)
                {
                    composite[first + second] += root[first] * root[second] / Sps;
                }
            }

            int centre = (composite.Length - 1) / 2;
            double sum = 0.0;
            int points = 0;

            for (int sample = -half; sample <= half; sample++)
            {
                double ideal = PulseFilter.RaisedCosine(0.35).At(sample / (double)Sps);
                double difference = composite[centre + sample] - ideal;

                sum += difference * difference;
                points++;
            }

            return Math.Sqrt(sum / points);
        }

        /// <summary>The largest response at a symbol centre other than the middle one.</summary>
        private static double WorstIsi(double measurementAlpha, double referenceAlpha)
        {
            const int Sps = 16;
            const int Span = 16;

            int half = Span * Sps;
            var first = new double[(2 * half) + 1];
            var second = new double[(2 * half) + 1];

            for (int tap = 0; tap < first.Length; tap++)
            {
                double t = (tap - half) / (double)Sps;

                first[tap] = PulseFilter.RootRaisedCosine(measurementAlpha).At(t);
                second[tap] = PulseFilter.RootRaisedCosine(referenceAlpha).At(t);
            }

            var composite = new double[(2 * first.Length) - 1];

            for (int a = 0; a < first.Length; a++)
            {
                for (int b = 0; b < second.Length; b++)
                {
                    composite[a + b] += first[a] * second[b] / Sps;
                }
            }

            int centre = (composite.Length - 1) / 2;
            double peak = composite[centre];
            double worst = 0.0;

            for (int symbol = 1; symbol <= Span; symbol++)
            {
                worst = Math.Max(worst, Math.Abs(composite[centre + (symbol * Sps)] / peak));
                worst = Math.Max(worst, Math.Abs(composite[centre - (symbol * Sps)] / peak));
            }

            return worst;
        }

        /// <summary>The worst stopband sidelobe of a truncated root raised cosine, in dB.</summary>
        private static double WorstSidelobe(int span, bool windowed)
        {
            const int Sps = 16;
            const double Alpha = 0.35;

            int half = span * Sps;
            var taps = new double[(2 * half) + 1];

            for (int tap = 0; tap < taps.Length; tap++)
            {
                double t = (tap - half) / (double)Sps;

                taps[tap] = windowed
                    ? PulseFilter.RootRaisedCosine(Alpha).Shape(t, span, FilterRole.Reference)
                    : PulseFilter.RootRaisedCosine(Alpha).At(t);
            }

            // A discrete transform, written out: the point is to measure the taps this build makes,
            // and borrowing the project's own FFT would drag its normalisation in with it.
            const int Bins = 2048;

            double peak = 0.0;
            double worst = double.NegativeInfinity;

            var magnitude = new double[Bins];

            for (int bin = 0; bin < Bins; bin++)
            {
                double real = 0.0;
                double imaginary = 0.0;
                // Up to Nyquist, which for taps a sixteenth of a symbol apart is eight symbol
                // rates. Running to Sps instead folds the response back onto itself and the
                // "worst sidelobe" comes out as 0 dB for every filter -- which is what it did.
                double frequency = bin / (double)Bins * (Sps / 2.0);

                for (int tap = 0; tap < taps.Length; tap++)
                {
                    double angle = -2.0 * Math.PI * frequency * (tap - half) / Sps;

                    real += taps[tap] * Math.Cos(angle);
                    imaginary += taps[tap] * Math.Sin(angle);
                }

                magnitude[bin] = Math.Sqrt((real * real) + (imaginary * imaginary));
                peak = Math.Max(peak, magnitude[bin]);
            }

            for (int bin = 0; bin < Bins; bin++)
            {
                double frequency = bin / (double)Bins * (Sps / 2.0);

                // Well outside the filter's own skirt, where what is left is truncation ringing.
                if (frequency < (1.0 + Alpha) * 0.8)
                {
                    continue;
                }

                worst = Math.Max(worst, 20.0 * Math.Log10(magnitude[bin] / peak));
            }

            return worst;
        }

        private static double Evm(
            float[] samples, ContinuousModulatedSource source, int span)
        {
            var settings = new DemodSettings
            {
                Constellation = Constellation.Qpsk(),
                SymbolRateHz = source.SymbolRateHz,
                ResultLengthSymbols = 512,
                FilterSymbolSpan = span,
                MeasurementFilter = PulseFilterType.RootRaisedCosine,
                MeasurementFilterAlpha = source.RollOff,
                ReferenceFilterAlpha = source.RollOff,
            };

            return new Demodulator().Run(samples, source.SampleRateHz, settings).EvmPercent;
        }
    }

    /// <summary>A convolution written out, so that "None changes nothing" is checked against one.</summary>
    internal static class PulseShapingProbe
    {
        internal static double[] Convolve(double[] signal, double[] taps)
        {
            var output = new double[signal.Length];
            int centre = taps.Length / 2;

            for (int sample = 0; sample < signal.Length; sample++)
            {
                double sum = 0.0;

                for (int tap = 0; tap < taps.Length; tap++)
                {
                    int source = sample + centre - tap;

                    if (source < 0 || source >= signal.Length)
                    {
                        continue;
                    }

                    sum += taps[tap] * signal[source];
                }

                output[sample] = sum;
            }

            return output;
        }
    }
}
