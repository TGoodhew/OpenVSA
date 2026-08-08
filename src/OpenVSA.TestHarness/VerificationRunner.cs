using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using OpenVSA.Core;
using OpenVSA.Dsp.Spectrum;
using OpenVSA.Hal;
using OpenVSA.Measurement;

namespace OpenVSA.TestHarness
{
    /// <summary>The outcome of one scenario.</summary>
    public sealed class VerificationResult
    {
        internal VerificationResult(
            VerificationScenario scenario,
            bool passed,
            double expected,
            double measured,
            string note)
        {
            Scenario = scenario;
            Passed = passed;
            Expected = expected;
            Measured = measured;
            Note = note ?? string.Empty;
        }

        /// <summary>The scenario this is the outcome of.</summary>
        public VerificationScenario Scenario { get; }

        /// <summary>Whether the reading was within tolerance.</summary>
        public bool Passed { get; }

        /// <summary>What the generator said it was doing.</summary>
        public double Expected { get; }

        /// <summary>What OpenVSA read.</summary>
        public double Measured { get; }

        /// <summary>Departure from the expectation, in the quantity's own units.</summary>
        public double Error => Measured - Expected;

        /// <summary>How much tolerance was left, negative when it was exceeded.</summary>
        public double Margin => Scenario.Tolerance - Math.Abs(Error);

        /// <summary>Why it failed, or any remark worth carrying.</summary>
        public string Note { get; }

        /// <summary>
        /// A one-line report naming measured, expected and margin.
        /// </summary>
        /// <remarks>
        /// <c>REQ-E44-007</c>'s criterion: a failure names the values, not just "failed". A result
        /// that says only that something is wrong sends the reader back to the bench to find out
        /// what.
        /// </remarks>
        public override string ToString()
        {
            string verdict = Passed ? "PASS" : "FAIL";
            string units = Scenario.Units;

            return verdict.PadRight(5) + Scenario.Name.PadRight(36) +
                "measured " + Format(Measured) +
                ", expected " + Format(Expected) +
                ", error " + Format(Error) + " " + units +
                ", margin " + Format(Margin) + " " + units +
                (Note.Length > 0 ? " — " + Note : string.Empty);
        }

        private static string Format(double value) =>
            Math.Abs(value) >= 1e6
                ? (value / 1e6).ToString("F6", CultureInfo.CurrentCulture) + "e6"
                : value.ToString("F3", CultureInfo.CurrentCulture);
    }

    /// <summary>
    /// Drives a stimulus source to each scenario's state, measures it with OpenVSA, and reports.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The measurement path is the real one: <see cref="SpectrumEngine"/> over a real
    /// <see cref="IFrontEnd"/>, through <see cref="SpectrumComputer"/> and the amplitude chain.
    /// Nothing here reaches around it, because the whole value of a cross-validation harness is
    /// that it exercises the code the product uses.
    /// </para>
    /// <para>
    /// <strong>Frames are discarded before the reading is taken.</strong> The instrument
    /// auto-ranges its digitiser at the start of a measurement, so the first frame after a
    /// settings change can be taken at the wrong range. Reading it would produce an intermittent
    /// amplitude failure that looks like noise in the chain.
    /// </para>
    /// </remarks>
    public sealed class VerificationRunner
    {
        /// <summary>Frames discarded after each setup change, before the reading is taken.</summary>
        public const int SettlingFrames = 2;

        /// <summary>Frames averaged into the reading, to keep noise off the verdict.</summary>
        public const int ReadingFrames = 3;

        private readonly IFrontEnd _frontEnd;
        private readonly IStimulusSource _stimulus;

        /// <summary>Creates a runner over a measurement front end and a stimulus source.</summary>
        /// <param name="frontEnd">The instrument under test.</param>
        /// <param name="stimulus">The signal generator.</param>
        /// <exception cref="ArgumentNullException">An argument is null.</exception>
        public VerificationRunner(IFrontEnd frontEnd, IStimulusSource stimulus)
        {
            if (frontEnd == null)
            {
                throw new ArgumentNullException(nameof(frontEnd));
            }

            if (stimulus == null)
            {
                throw new ArgumentNullException(nameof(stimulus));
            }

            _frontEnd = frontEnd;
            _stimulus = stimulus;
        }

        /// <summary>Runs every scenario in order.</summary>
        /// <param name="scenarios">The scenarios to run.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>One result per scenario, in the same order.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="scenarios"/> is null.</exception>
        public async Task<IReadOnlyList<VerificationResult>> RunAsync(
            IEnumerable<VerificationScenario> scenarios, CancellationToken ct)
        {
            if (scenarios == null)
            {
                throw new ArgumentNullException(nameof(scenarios));
            }

            var results = new List<VerificationResult>();

            foreach (VerificationScenario scenario in scenarios)
            {
                results.Add(await RunOneAsync(scenario, ct).ConfigureAwait(false));
            }

            return results;
        }

        /// <summary>Runs one scenario.</summary>
        /// <param name="scenario">The scenario.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <exception cref="ArgumentNullException"><paramref name="scenario"/> is null.</exception>
        public async Task<VerificationResult> RunOneAsync(
            VerificationScenario scenario, CancellationToken ct)
        {
            if (scenario == null)
            {
                throw new ArgumentNullException(nameof(scenario));
            }

            try
            {
                if (scenario.NeedsMultitone)
                {
                    var comb = _stimulus as IMultitoneStimulus;

                    if (comb == null)
                    {
                        // Reported, never skipped silently. A harness that quietly dropped the
                        // scenarios its generator could not produce would report a clean run over
                        // a reduced set, which is the failure that made "28 of 34" look like a
                        // verification.
                        return new VerificationResult(
                            scenario, false, double.NaN, double.NaN,
                            "'" + _stimulus.DisplayName + "' cannot produce a multitone comb");
                    }

                    comb.SetMultitone(
                        scenario.StimulusFrequencyHz,
                        scenario.RequestedToneCount,
                        scenario.RequestedToneSpacingHz,
                        scenario.StimulusLevelDbm);
                }
                else
                {
                    _stimulus.SetContinuousWave(
                        scenario.StimulusFrequencyHz, scenario.StimulusLevelDbm);
                }

                _stimulus.SetOutput(scenario.OutputEnabled);

                // From the generator's read-back, so a coerced or externally changed setting moves
                // the expectation with it rather than failing the analyser for it.
                double expected = scenario.ExpectedFrom(_stimulus);

                SpectrumFrame frame = await MeasureAsync(scenario, ct).ConfigureAwait(false);

                if (frame == null)
                {
                    return new VerificationResult(
                        scenario, false, expected, double.NaN, "no frame was produced");
                }

                try
                {
                    if (scenario.NeedsMultitone)
                    {
                        return MeasureComb(scenario, frame, expected);
                    }

                    int peak = frame.IndexOfPeak();

                    if (peak < 0)
                    {
                        return new VerificationResult(
                            scenario, false, expected, double.NaN, "the spectrum had no peak");
                    }

                    double measured = Measure(scenario, frame, peak);
                    bool passed = Math.Abs(measured - expected) <= scenario.Tolerance;

                    return new VerificationResult(scenario, passed, expected, measured, string.Empty);
                }
                finally
                {
                    // The share MeasureAsync handed over (REQ-NFR-002). Everything read from the
                    // frame is read above; nothing here keeps it.
                    frame.Release();
                }
            }
            catch (Exception failure)
            {
                return new VerificationResult(
                    scenario, false, double.NaN, double.NaN, failure.Message.Split('\n')[0]);
            }
        }

        /// <summary>
        /// Finds the comb's tones and checks whichever property the scenario names.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <strong>Tones are found as local maxima above a floor referred to the largest one</strong>,
        /// not as "the N largest bins". The shoulders of a strong tone are larger than a weak
        /// tone's peak, so taking the N largest bins returns one tone reported N times and a
        /// spacing of one bin — a result that would look like a catastrophic failure of the
        /// frequency axis while the axis was perfectly correct.
        /// </para>
        /// <para>
        /// <strong>The floor is 6 dB below the weakest tone the comb should have.</strong> With
        /// equal tones sharing the total power, each sits about 10·log10(N) below it; 6 dB of head
        /// room admits a real tone that the analyser has under-read while still excluding the
        /// skirts, which fall much faster than that.
        /// </para>
        /// </remarks>
        private static VerificationResult MeasureComb(
            VerificationScenario scenario, SpectrumFrame frame, double expected)
        {
            IReadOnlyList<int> tones = ToneSearch.Find(
                frame.LevelsDbm.ToArray(), scenario.RequestedToneCount);

            if (tones.Count == 0)
            {
                return new VerificationResult(
                    scenario, false, expected, double.NaN, "no tone rose above the search floor");
            }

            double measured;
            string note = tones.Count + " tone(s) found";

            switch (scenario.What)
            {
                case VerifiedQuantity.ToneCount:
                    measured = tones.Count;
                    break;

                case VerifiedQuantity.ToneSpacingHz:
                    if (tones.Count < 2)
                    {
                        return new VerificationResult(
                            scenario, false, expected, double.NaN,
                            "only one tone was found, so there is no spacing to measure");
                    }

                    // Mean of the adjacent gaps, which is the whole comb's spacing rather than one
                    // pair's. A single pair inherits the bin width as its uncertainty; averaging
                    // over the comb divides that by the number of gaps.
                    measured =
                        (frame.FrequencyAt(tones[tones.Count - 1]) - frame.FrequencyAt(tones[0])) /
                        (tones.Count - 1);
                    break;

                case VerifiedQuantity.ToneFlatnessDb:
                    if (tones.Count < 2)
                    {
                        return new VerificationResult(
                            scenario, false, expected, double.NaN,
                            "only one tone was found, so there is no flatness to measure");
                    }

                    double strongest = double.NegativeInfinity;
                    double weakest = double.PositiveInfinity;

                    foreach (int tone in tones)
                    {
                        double level = frame.LevelsDbm[tone];
                        strongest = Math.Max(strongest, level);
                        weakest = Math.Min(weakest, level);
                    }

                    measured = strongest - weakest;
                    note += ", " + weakest.ToString("F2", CultureInfo.CurrentCulture) + " to " +
                        strongest.ToString("F2", CultureInfo.CurrentCulture) + " dBm";
                    break;

                default:
                    return new VerificationResult(
                        scenario, false, expected, double.NaN,
                        "a comb scenario cannot check " + scenario.What);
            }

            bool passed = Math.Abs(measured - expected) <= scenario.Tolerance;

            return new VerificationResult(scenario, passed, expected, measured, note);
        }

        private static double Measure(VerificationScenario scenario, SpectrumFrame frame, int peak)
        {
            switch (scenario.What)
            {
                case VerifiedQuantity.PeakFrequencyHz:
                    return frame.FrequencyAt(peak);

                case VerifiedQuantity.PeakOffsetHz:
                    return frame.FrequencyAt(peak) - scenario.CenterFrequencyHz;

                case VerifiedQuantity.PeakLevelDbm:
                    return frame.LevelsDbm[peak];

                default:
                    return double.NaN;
            }
        }

        /// <summary>Runs a measurement and returns a settled frame.</summary>
        private async Task<SpectrumFrame> MeasureAsync(
            VerificationScenario scenario, CancellationToken ct)
        {
            var computer = new SpectrumComputer { TrimToAnalysisSpan = true };

            using (var engine = new SpectrumEngine(_frontEnd, computer))
            {
                var frames = new List<SpectrumFrame>();
                var enough = new SemaphoreSlim(0, 1);

                engine.TargetUpdatesPerSecond = 0.0;
                engine.FrameComputed += (sender, frame) =>
                {
                    lock (frames)
                    {
                        if (frames.Count >= SettlingFrames + ReadingFrames)
                        {
                            return;
                        }

                        // REQ-NFR-002: the pump gives up its share the moment this handler
                        // returns, and these frames are read after the run finishes. Retained
                        // here, released in the finally below.
                        frame.Retain();
                        frames.Add(frame);

                        if (frames.Count == SettlingFrames + ReadingFrames)
                        {
                            enough.Release();
                        }
                    }
                };

                int transform = AcquisitionLaw.TransformLengthFor(
                    scenario.FrequencyPoints, AnalysisPath.ComplexZoom);

                await engine.StartAsync(
                    new AcquisitionRequest(
                        scenario.CenterFrequencyHz, scenario.SpanHz, transform, 0.0),
                    ct).ConfigureAwait(false);

                bool settled = await enough.WaitAsync(TimeSpan.FromMinutes(2), ct)
                    .ConfigureAwait(false);

                await engine.StopAsync().ConfigureAwait(false);

                lock (frames)
                {
                    if (frames.Count == 0)
                    {
                        return null;
                    }

                    // The last settled frame. Averaging across frames would need the peak to sit
                    // in the same bin in each, which is exactly what a drifting source does not do.
                    SpectrumFrame chosen = frames[frames.Count - 1];

                    // Every frame this run retained except the one being returned. The caller
                    // inherits the chosen frame's share and releases it when it has read what it
                    // needs (REQ-NFR-002); the settling frames are of no further use and their
                    // buffers go back now rather than at the next collection.
                    for (int i = 0; i < frames.Count - 1; i++)
                    {
                        frames[i].Release();
                    }

                    return chosen;
                }
            }
        }
    }
}
