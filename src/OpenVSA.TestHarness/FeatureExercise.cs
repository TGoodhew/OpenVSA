using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OpenVSA.Capture.Triggering;
using OpenVSA.Core;
using OpenVSA.Dsp.Spectrum;
using OpenVSA.Dsp.Windowing;
using OpenVSA.Hal;
using OpenVSA.Measurement;
using OpenVSA.Measurement.Limits;
using OpenVSA.Measurement.Markers;
using OpenVSA.Measurement.State;

namespace OpenVSA.TestHarness
{
    /// <summary>One exercised feature and how it behaved.</summary>
    public sealed class ExerciseResult
    {
        /// <summary>Creates a result.</summary>
        /// <param name="requirement">The requirement being exercised.</param>
        /// <param name="name">What was done.</param>
        /// <param name="passed">Whether it behaved as required.</param>
        /// <param name="detail">The observation, whether it passed or not.</param>
        public ExerciseResult(string requirement, string name, bool passed, string detail)
        {
            Requirement = requirement ?? string.Empty;
            Name = name ?? string.Empty;
            Passed = passed;
            Detail = detail ?? string.Empty;
        }

        /// <summary>The requirement being exercised.</summary>
        public string Requirement { get; }

        /// <summary>What was done.</summary>
        public string Name { get; }

        /// <summary>Whether it behaved as required.</summary>
        public bool Passed { get; }

        /// <summary>The observation.</summary>
        public string Detail { get; }

        /// <inheritdoc />
        public override string ToString() =>
            (Passed ? "PASS " : "FAIL ") + Requirement.PadRight(14) + Name.PadRight(56) + "  " +
            Detail;
    }

    /// <summary>
    /// Drives every feature that can be driven against one real acquisition, and reports on each.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This is not the unit suite run again.</strong> The unit tests build their own
    /// signals; this takes a block the instrument actually produced — with its real sample rate,
    /// its real point count, its own noise and its own trigger metadata — and pushes it through
    /// the features in the order a measurement would. Several classes of defect only appear here:
    /// a feature that assumes a power-of-two record, one that assumes noiseless data, one that
    /// works on a synthetic block and not on a 23 000-sample one.
    /// </para>
    /// <para>
    /// <strong>Every step reports rather than throws.</strong> A feature that fails must not stop
    /// the ones after it from being exercised — the point of the run is to find out how much
    /// works, and an exception on the third of fifteen steps answers that question far less well
    /// than fifteen verdicts do.
    /// </para>
    /// </remarks>
    public sealed class FeatureExercise
    {
        private readonly IFrontEnd _frontEnd;
        private readonly IStimulusSource _stimulus;
        private readonly List<ExerciseResult> _results = new List<ExerciseResult>();

        /// <summary>Creates an exercise over a front end and a stimulus source.</summary>
        /// <param name="frontEnd">The instrument to acquire from.</param>
        /// <param name="stimulus">The generator to drive.</param>
        /// <exception cref="ArgumentNullException">An argument is null.</exception>
        public FeatureExercise(IFrontEnd frontEnd, IStimulusSource stimulus)
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

        /// <summary>Offset of the exercise tone from the analysis centre, in hertz.</summary>
        public const double ToneOffsetHz = 3.1e6;

        /// <summary>
        /// Acquires one real block and exercises every feature that can be exercised on it.
        /// </summary>
        /// <param name="centerFrequencyHz">Analysis centre frequency.</param>
        /// <param name="spanHz">Analysis span.</param>
        /// <param name="levelDbm">Generator level.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>One result per feature, in the order they were exercised.</returns>
        public async Task<IReadOnlyList<ExerciseResult>> RunAsync(
            double centerFrequencyHz, double spanHz, double levelDbm, CancellationToken ct)
        {
            _results.Clear();

            double toneHz = centerFrequencyHz + ToneOffsetHz;

            _stimulus.SetContinuousWave(toneHz, levelDbm);
            _stimulus.SetOutput(true);

            // From the generator's read-back, never from what was asked for: a coerced or
            // externally retuned carrier must move the expectation with it.
            double actualToneHz = _stimulus.FrequencyHz;

            IqBlock block = await AcquireAsync(centerFrequencyHz, spanHz, ct).ConfigureAwait(false);

            if (block == null)
            {
                Record("REQ-HAL-001", "Acquire one block from the instrument", false,
                    "no block was produced");
                return _results;
            }

            using (block)
            {
                Record("REQ-HAL-001", "Acquire one block from the instrument", true,
                    block.SampleCount.ToString(CultureInfo.CurrentCulture) + " samples at " +
                    Hz(block.SampleRateHz));

                SpectrumFrame frame = Spectrum(block, actualToneHz);

                if (frame != null)
                {
                    ExerciseOverlap(block, actualToneHz);
                    ExerciseGating(block, frame);
                    ExerciseFormats(frame);
                    ExerciseTraceMath(frame);
                    ExerciseRegisters(frame);
                    ExerciseBandMeasurements(frame, actualToneHz);
                    ExerciseMarkers(frame, actualToneHz);
                    ExerciseLimits(frame);
                }

                ExerciseTriggering(block);
            }

            ExerciseState(centerFrequencyHz, spanHz);
            ExercisePresets(centerFrequencyHz);

            return _results;
        }

        // ---- The exercises ---------------------------------------------------------------------

        private SpectrumFrame Spectrum(IqBlock block, double toneHz)
        {
            return Step("REQ-DSP-001", "Spectrum of the acquired block", () =>
            {
                var computer = new SpectrumComputer(WindowType.FlatTop, null, null)
                {
                    TrimToAnalysisSpan = true,
                };

                SpectrumFrame frame = computer.Compute(block);
                int peak = frame.IndexOfPeak();

                if (peak < 0)
                {
                    return Failed<SpectrumFrame>("the spectrum had no peak");
                }

                double found = frame.FrequencyAt(peak);
                double error = found - toneHz;
                bool ok = Math.Abs(error) <= 2.0 * frame.BinWidthHz;

                return new Outcome<SpectrumFrame>(
                    ok,
                    frame,
                    frame.PointCount.ToString(CultureInfo.CurrentCulture) + " points, peak at " +
                    Hz(found) + " (" + Signed(error) + " Hz from the carrier, bin " +
                    Hz(frame.BinWidthHz) + ")");
            });
        }

        private void ExerciseOverlap(IqBlock block, double toneHz)
        {
            Step("REQ-ACQ-003", "Overlap 50 % doubles the frame count", () =>
            {
                int record = SpectrumComputer.TransformLengthFor(block.SampleCount) / 2;

                int none = FrameExtraction.FrameCount(block.SampleCount, record, 0.0);
                int half = FrameExtraction.FrameCount(block.SampleCount, record, 0.5);

                bool ok = none > 0 && Math.Abs(half - 2 * none) <= 1;

                return new Outcome<int>(
                    ok,
                    record,
                    none + " frames of " + record + " samples at 0 %, " + half + " at 50 %");
            });

            Step("REQ-ACQ-003", "Every overlapped frame still finds the carrier", () =>
            {
                int record = SpectrumComputer.TransformLengthFor(block.SampleCount) / 2;
                var computer = new SpectrumComputer(WindowType.FlatTop, null, null);

                int frames = 0;
                double worst = 0.0;
                double bin = 0.0;

                foreach (IqBlock cut in FrameExtraction.Extract(block, record, 0.5))
                {
                    using (cut)
                    {
                        SpectrumFrame spectrum = computer.Compute(cut);
                        bin = spectrum.BinWidthHz;

                        int peak = spectrum.IndexOfPeak();
                        double error = peak < 0
                            ? double.PositiveInfinity
                            : Math.Abs(spectrum.FrequencyAt(peak) - toneHz);

                        worst = Math.Max(worst, error);
                        frames++;
                    }
                }

                bool ok = frames > 1 && worst <= 2.0 * bin;

                return new Outcome<int>(
                    ok, frames,
                    frames + " frames, worst peak error " + Hz(worst) + " against a " + Hz(bin) +
                    " bin");
            });

            Step("REQ-DSP-031", "Overlapped averages are worth less than their count", () =>
            {
                int record = SpectrumComputer.TransformLengthFor(block.SampleCount) / 2;
                var computer = new SpectrumComputer(WindowType.FlatTop, null, null);
                var averager = new TraceAverager(AveragingType.RmsVideo, 1000)
                {
                    Overlap = 0.75,
                    RecordSamples = record,
                };

                SpectrumFrame averaged = null;

                foreach (IqBlock cut in FrameExtraction.Extract(block, record, 0.75))
                {
                    using (cut)
                    {
                        averaged = averager.Accumulate(computer.Compute(cut));
                    }
                }

                if (averaged == null)
                {
                    return Failed<double>("no frames were averaged");
                }

                bool ok = averaged.EffectiveAverageCount > 0.0 &&
                          averaged.EffectiveAverageCount <= averaged.AverageCount;

                return new Outcome<double>(
                    ok,
                    averaged.EffectiveAverageCount,
                    averaged.AverageCount + " overlapped frames are worth " +
                    averaged.EffectiveAverageCount.ToString("0.00", CultureInfo.CurrentCulture) +
                    " independent averages");
            });
        }

        private void ExerciseGating(IqBlock block, SpectrumFrame ungated)
        {
            Step("REQ-DSP-050", "Gating a quarter of the record coarsens the RBW fourfold", () =>
            {
                double recordSeconds = block.SampleCount / block.SampleRateHz;
                var gate = new TimeGate(0.0, recordSeconds / 4.0);

                using (IqBlock gated = gate.Apply(block))
                {
                    var computer = new SpectrumComputer(WindowType.FlatTop, null, null)
                    {
                        TrimToAnalysisSpan = true,
                    };

                    SpectrumFrame frame = computer.Compute(gated);
                    double ratio = frame.ResolutionBandwidthHz / ungated.ResolutionBandwidthHz;

                    // Four within a factor the truncation to a power of two can account for: the
                    // gated record is a quarter of the samples but the transform is the largest
                    // power of two that fits either way.
                    bool ok = ratio > 2.0 && ratio < 8.0;

                    return new Outcome<double>(
                        ok, ratio,
                        "RBW " + Hz(ungated.ResolutionBandwidthHz) + " to " +
                        Hz(frame.ResolutionBandwidthHz) + ", ratio " +
                        ratio.ToString("0.00", CultureInfo.CurrentCulture));
                }
            });
        }

        private void ExerciseTraceMath(SpectrumFrame frame)
        {
            Step("REQ-DSP-046", "A real trace minus itself is zero everywhere", () =>
            {
                SpectrumFrame difference = TraceMath.Apply("Subtract", frame, frame);

                double worst = 0.0;

                for (int i = 0; i < difference.Complex.Length; i++)
                {
                    worst = Math.Max(worst, Math.Abs(difference.Complex[i]));
                }

                return new Outcome<double>(worst == 0.0, worst, "largest residual " + worst + " V");
            });

            Step("REQ-DSP-046", "A real trace divided by itself is one everywhere", () =>
            {
                SpectrumFrame ratio = TraceMath.Apply("Divide", frame, frame);

                double worst = 0.0;

                for (int i = 0; i < ratio.PointCount; i++)
                {
                    worst = Math.Max(worst, Math.Abs(ratio.Complex[i * 2] - 1.0f));
                    worst = Math.Max(worst, Math.Abs(ratio.Complex[i * 2 + 1]));
                }

                return new Outcome<double>(
                    worst < 1e-5, worst,
                    "largest departure from 1 + 0j is " +
                    worst.ToString("G4", CultureInfo.CurrentCulture));
            });

            Step("REQ-DSP-046", "Dividing by an empty trace reads NAN or INF", () =>
            {
                var zeros = new float[frame.PointCount * 2];
                SpectrumFrame empty = SpectrumFrame.FromComplex(
                    zeros, frame.StartFrequencyHz, frame.BinWidthHz, frame.Window,
                    frame.EquivalentNoiseBandwidthBins);

                SpectrumFrame ratio = TraceMath.Apply("Divide", frame, empty);

                int nonFinite = 0;

                for (int i = 0; i < ratio.Complex.Length; i++)
                {
                    if (float.IsNaN(ratio.Complex[i]) || float.IsInfinity(ratio.Complex[i]))
                    {
                        nonFinite++;
                    }
                }

                bool ok = nonFinite == ratio.Complex.Length;

                return new Outcome<int>(
                    ok, nonFinite,
                    nonFinite + " of " + ratio.Complex.Length +
                    " values are non-finite, none is a quiet zero");
            });

            Step("REQ-DSP-046", "Magnitude of a real trace discards its phase", () =>
            {
                SpectrumFrame magnitude = TraceMath.Apply("Magnitude", frame, Complex32.Zero);

                bool ok = frame.HasPhase && !magnitude.HasPhase;

                return new Outcome<bool>(
                    ok, ok,
                    "source carries phase: " + frame.HasPhase + ", magnitude carries phase: " +
                    magnitude.HasPhase);
            });

            Step("REQ-DSP-046", "Incommensurate traces are refused by name", () =>
            {
                SpectrumFrame elsewhere = SpectrumFrame.FromComplex(
                    new float[frame.PointCount * 2],
                    frame.StartFrequencyHz + 1e9,
                    frame.BinWidthHz,
                    frame.Window,
                    frame.EquivalentNoiseBandwidthBins);

                try
                {
                    TraceMath.Apply("Subtract", frame, elsewhere);
                    return Failed<string>("two unrelated axes were combined by index");
                }
                catch (IncommensurableTracesException refused)
                {
                    return new Outcome<string>(true, refused.Message, refused.Message);
                }
            });
        }

        private void ExerciseFormats(SpectrumFrame frame)
        {
            Step("REQ-DSP-044", "Wrapped and unwrapped phase agree modulo a turn", () =>
            {
                var wrapped = new float[frame.PointCount];
                var unwrapped = new float[frame.PointCount];

                frame.Format(TraceFormat.WrappedPhase, wrapped);
                frame.Format(TraceFormat.UnwrappedPhase, unwrapped);

                double worst = 0.0;

                for (int i = 0; i < wrapped.Length; i++)
                {
                    double turns = (unwrapped[i] - wrapped[i]) / 360.0;
                    worst = Math.Max(worst, Math.Abs(turns - Math.Round(turns)));
                }

                // The reference point is the first point of the trace, and its unwrapped value is
                // its wrapped one - which is what makes a phase trace reproducible between runs.
                bool anchored = Math.Abs(
                    unwrapped[TraceFormatOptions.ReferencePointIndex] -
                    wrapped[TraceFormatOptions.ReferencePointIndex]) < 1e-3;

                return new Outcome<double>(
                    worst < 1e-3 && anchored,
                    worst,
                    "worst departure from a whole turn " + worst.ToString("G3") +
                    ", anchored at point 0: " + anchored);
            });

            Step("REQ-DSP-045", "A wider aperture smooths the real group-delay trace", () =>
            {
                double narrow = Roughness(frame, 1);
                double wide = Roughness(frame, 32);

                // Averaging the derivative over more bins trades resolution for quiet. On the
                // noisy phase of a real acquisition the effect is large and in one direction.
                bool ok = wide < narrow;

                return new Outcome<double>(
                    ok, wide,
                    "roughness " + narrow.ToString("G3") + " s at 1 bin against " +
                    wide.ToString("G3") + " s at 32 bins");
            });

            Step("REQ-DSP-040", "The base data types offer only formats their data supports", () =>
            {
                IReadOnlyList<TraceFormat> spectrum =
                    TraceDataTypes.FormatsFor(TraceDataType.Spectrum);

                IReadOnlyList<TraceFormat> ccdf = TraceDataTypes.FormatsFor(TraceDataType.Ccdf);

                bool ok = TraceDataTypes.All.Count == 11 &&
                          spectrum.Contains(TraceFormat.UnwrappedPhase) &&
                          !ccdf.Contains(TraceFormat.UnwrappedPhase) &&
                          ccdf.Contains(TraceFormat.LogMagnitude);

                return new Outcome<int>(
                    ok, TraceDataTypes.All.Count,
                    TraceDataTypes.All.Count + " types; Spectrum offers " + spectrum.Count +
                    " formats, CCDF " + ccdf.Count + " with no phase among them");
            });
        }

        private static double Roughness(SpectrumFrame frame, int aperture)
        {
            var delay = new float[frame.PointCount];
            frame.Format(TraceFormat.GroupDelay, delay, new TraceFormatOptions(aperture));

            double sum = 0.0;

            for (int i = 1; i < delay.Length; i++)
            {
                sum += Math.Abs(delay[i] - delay[i - 1]);
            }

            return delay.Length > 1 ? sum / (delay.Length - 1) : 0.0;
        }

        private void ExerciseRegisters(SpectrumFrame frame)
        {
            Step("REQ-DSP-046", "A register returns a real trace bit-identically", () =>
            {
                var registers = new TraceRegisters();
                registers.Store(3, frame);

                SpectrumFrame recalled = registers.Recall(3);
                int differing = 0;

                for (int i = 0; i < frame.Complex.Length; i++)
                {
                    if (BitConverter.ToInt32(BitConverter.GetBytes(frame.Complex[i]), 0) !=
                        BitConverter.ToInt32(BitConverter.GetBytes(recalled.Complex[i]), 0))
                    {
                        differing++;
                    }
                }

                return new Outcome<int>(
                    differing == 0, differing,
                    registers.NameOf(3) + " holds " + recalled.PointCount + " points, " +
                    differing + " bits differ");
            });
        }

        private void ExerciseBandMeasurements(SpectrumFrame frame, double toneHz)
        {
            Step("REQ-MKR-003", "Band power over the carrier matches its peak level", () =>
            {
                // A flat-top main lobe is several bins wide, so the band has to be wide enough to
                // contain it or the reading is of part of a tone rather than of the tone.
                double half = 10.0 * frame.BinWidthHz;
                BandPower power = BandMeasurements.Power(frame, toneHz - half, toneHz + half);

                int peak = frame.IndexOfPeak();
                double peakDbm = frame.LevelsDbm[peak];
                double error = power.TotalDbm - peakDbm;

                bool ok = Math.Abs(error) <= 1.5;

                return new Outcome<double>(
                    ok, power.TotalDbm,
                    "band power " + Db(power.TotalDbm) + " over " + power.BinCount +
                    " bins against a peak of " + Db(peakDbm) + " (" + Signed(error) + " dB)");
            });

            Step("REQ-CHM-002", "The carrier's 3 dB bandwidth is a few bins wide", () =>
            {
                OccupiedBandwidth bandwidth = BandMeasurements.XDecibelsDown(frame, 3.0);

                bool ok = bandwidth.BandwidthHz > 0.0 &&
                          bandwidth.BandwidthHz < 50.0 * frame.BinWidthHz;

                return new Outcome<double>(
                    ok, bandwidth.BandwidthHz,
                    "3 dB bandwidth " + Hz(bandwidth.BandwidthHz) + " over a " +
                    Hz(frame.BinWidthHz) + " bin");
            });
        }

        private void ExerciseMarkers(SpectrumFrame frame, double toneHz)
        {
            Step("REQ-MKR-001", "A peak-search marker reads the carrier", () =>
            {
                // Peak search moves the selected marker rather than creating one, so a marker has
                // to exist first - which is what the shell's own peak-search command does.
                var markers = new MarkerSet('A');
                markers.AddNormal(frame.CenterFrequencyHz);

                Marker marker = markers.PeakSearch(frame);

                if (marker == null)
                {
                    return Failed<double>("peak search moved no marker");
                }

                MarkerReading reading = marker.Read(frame);
                double error = reading.XHz - toneHz;

                bool ok = reading.IsValid && Math.Abs(error) <= 2.0 * frame.BinWidthHz;

                return new Outcome<double>(
                    ok, reading.XHz,
                    marker.WindowLabel + " at " + Hz(reading.XHz) + ", " + Db(reading.YDbm) +
                    " (" + Signed(error) + " Hz from the carrier)");
            });

            Step("REQ-MKR-002", "A delta marker reads a difference, not a level", () =>
            {
                var markers = new MarkerSet('A');
                markers.AddNormal(frame.CenterFrequencyHz);

                Marker reference = markers.PeakSearch(frame);

                if (reference == null)
                {
                    return Failed<double>("peak search moved no marker");
                }

                // Well away from the carrier, so the difference is large and unmistakable.
                double away = frame.StartFrequencyHz + frame.SpanHz * 0.1;
                Marker delta = markers.AddDelta(away, reference);

                MarkerReading reading = delta.Read(frame);

                bool ok = reading.IsValid && reading.YDbm < -10.0;

                return new Outcome<double>(
                    ok, reading.YDbm,
                    delta.WindowLabel + " reads " +
                    reading.YDbm.ToString("+0.00;-0.00", CultureInfo.CurrentCulture) +
                    " dB at " + Signed(reading.XHz) + " Hz");
            });
        }

        private void ExerciseLimits(SpectrumFrame frame)
        {
            Step("REQ-LIM-002", "A limit the real trace passes, and one it fails", () =>
            {
                int peak = frame.IndexOfPeak();
                double peakDbm = frame.LevelsDbm[peak];

                LimitLine generous = new LimitLine("Generous", LimitSide.Upper)
                    .Add(frame.StartFrequencyHz, peakDbm + 10.0)
                    .Add(frame.StopFrequencyHz, peakDbm + 10.0);

                LimitLine tight = new LimitLine("Tight", LimitSide.Upper)
                    .Add(frame.StartFrequencyHz, peakDbm - 10.0)
                    .Add(frame.StopFrequencyHz, peakDbm - 10.0);

                LimitTestResult passes = new LimitTest("Passes").Add(generous).Evaluate(frame);
                LimitTestResult fails = new LimitTest("Fails").Add(tight).Evaluate(frame);

                bool ok = passes.Passed && !fails.Passed;

                return new Outcome<double>(
                    ok, passes.WorstMarginDb,
                    "peak " + Db(peakDbm) + ": limit at +10 dB passes with " +
                    passes.WorstMarginDb.ToString("0.0", CultureInfo.CurrentCulture) +
                    " dB margin, limit at -10 dB fails");
            });
        }

        private void ExerciseTriggering(IqBlock block)
        {
            Step("REQ-TRG-001", "The instrument is offered exactly the styles it declares", () =>
            {
                IReadOnlyList<TriggerOption> options =
                    TriggerAvailability.For(_frontEnd.Capabilities);

                var available = options.Where(o => o.IsAvailable).ToList();
                var greyed = options.Where(o => !o.IsAvailable).ToList();

                bool ok = available.Count > 0 &&
                          greyed.All(o => !string.IsNullOrWhiteSpace(o.Explanation)) &&
                          !TriggerAvailability.Offers(
                              _frontEnd.Capabilities, TriggerStyle.FrequencyMask);

                return new Outcome<int>(
                    ok, available.Count,
                    "offered " + string.Join(", ", available.Select(o => o.DisplayName)) +
                    "; greyed " + string.Join(", ", greyed.Select(o => o.DisplayName)) +
                    ", each with a reason");
            });

            Step("REQ-TRG-001", "A steady carrier above the level triggers at all", () =>
            {
                // The regression step for the defect this exercise found. Half the peak envelope
                // is a level a continuous carrier sits above from the first sample and never
                // approaches from below, so a strict edge search finds nothing and the analyser
                // waits for ever on a signal that plainly satisfies the condition.
                //
                // Kept separate from the step below deliberately: that one uses the mean envelope,
                // which a real carrier's noise crosses hundreds of times, and would therefore pass
                // whether or not this defect were fixed.
                double peakVolts = PeakMagnitude(block);

                if (!(peakVolts > 0.0))
                {
                    return Failed<int>("the block is silent, so nothing can trigger on it");
                }

                var settings = new TriggerSettings(
                    TriggerStyle.Level, levelVolts: peakVolts * 0.5);

                IReadOnlyList<int> instants = TriggerSearch.Instants(block, settings);

                double startVolts = Math.Sqrt(block.GetSample(0).MagnitudeSquared);
                bool startsAbove = startVolts > peakVolts * 0.5;

                if (!startsAbove)
                {
                    return Failed<int>(
                        "the block does not start above half its peak envelope, so this step " +
                        "is not exercising the case it exists for");
                }

                bool ok = instants.Count > 0 && instants[0] == 0;

                return new Outcome<int>(
                    ok, instants.Count,
                    "record starts at " +
                    (startVolts * 1e3).ToString("0.000", CultureInfo.CurrentCulture) +
                    " mV against a level of " +
                    (peakVolts * 0.5 * 1e3).ToString("0.000", CultureInfo.CurrentCulture) +
                    " mV: " + instants.Count + " trigger(s), first at sample " +
                    (instants.Count > 0 ? instants[0].ToString(CultureInfo.CurrentCulture) : "none"));
            });

            Step("REQ-TRG-002", "A level trigger fires on the real block, with pre-trigger", () =>
            {
                // A carrier's envelope wobbles about its mean, so the mean is a level the real
                // signal genuinely crosses. Half the peak, which a made-up burst crosses on the way
                // up, a steady carrier never reaches from below at all.
                double level = MeanMagnitude(block);

                if (!(level > 0.0))
                {
                    return Failed<int>("the block is silent, so nothing can trigger on it");
                }

                const int preTrigger = 256;

                var settings = new TriggerSettings(
                    TriggerStyle.Level,
                    levelVolts: level,
                    delaySeconds: -preTrigger / block.SampleRateHz);

                IReadOnlyList<int> instants = TriggerSearch.Instants(block, settings);

                if (instants.Count == 0)
                {
                    return Failed<int>("nothing crossed the mean envelope");
                }

                // The first trigger far enough in for the pre-trigger to exist. An earlier one
                // would be refused, correctly, and would say nothing about the offset.
                int occurrence = -1;

                for (int i = 0; i < instants.Count; i++)
                {
                    if (instants[i] >= preTrigger)
                    {
                        occurrence = i;
                        break;
                    }
                }

                if (occurrence < 0)
                {
                    return Failed<int>(
                        instants.Count + " triggers, none more than " + preTrigger +
                        " samples into the record");
                }

                int record = Math.Min(512, block.SampleCount - instants[occurrence]);

                using (IqBlock triggered =
                       TriggerSearch.Extract(block, settings, record, occurrence))
                {
                    if (triggered == null)
                    {
                        return Failed<int>("the triggered record did not fit inside the block");
                    }

                    double offset = triggered.TriggerOffsetSeconds;
                    double expected = preTrigger / block.SampleRateHz;
                    bool ok = Math.Abs(offset - expected) < 1e-12;

                    return new Outcome<int>(
                        ok, instants.Count,
                        instants.Count + " triggers; record " + occurrence + " starts " +
                        (offset * 1e6).ToString("0.00", CultureInfo.CurrentCulture) +
                        " us (" + preTrigger + " samples) before its trigger");
                }
            });

            Step("REQ-TRG-003", "Hold-off styles differ on the real block", () =>
            {
                double level = MeanMagnitude(block);
                int holdoffSamples = Math.Max(4, block.SampleCount / 32);
                double holdoffSeconds = holdoffSamples / block.SampleRateHz;

                int none = Triggers(block, level, HoldoffStyle.Conventional, 0.0);
                int conventional = Triggers(block, level, HoldoffStyle.Conventional, holdoffSeconds);
                int below = Triggers(block, level, HoldoffStyle.BelowLevel, holdoffSeconds);
                int above = Triggers(block, level, HoldoffStyle.AboveLevel, holdoffSeconds);

                // Conventional blanks a fixed window and so thins the crossings; the conditional
                // styles need a run of the signal on one side of the level, which a carrier's
                // envelope noise does not give them - so they must be scarcer still. Three styles
                // that agreed would be three names for one behaviour.
                bool ok = none > conventional && conventional > below && conventional > above;

                return new Outcome<int>(
                    ok, conventional,
                    "over " + holdoffSamples + " samples: none " + none + ", conventional " +
                    conventional + ", below-level " + below + ", above-level " + above);
            });
        }

        private void ExerciseState(double centerFrequencyHz, double spanHz)
        {
            Step("REQ-STA-001", "A setup survives save, reset and recall", () =>
            {
                ApplicationState saved = ApplicationState.Default("Bench");
                saved.Measurements[0].CenterFrequencyHz = centerFrequencyHz;
                saved.Measurements[0].SpanHz = spanHz;
                saved.Measurements[0].Analysis.Window = WindowType.FlatTop;
                saved.Measurements[0].Analysis.Overlap = 0.75;
                saved.Measurements[0].Trigger.Style = TriggerStyle.Level;

                string path = Path.Combine(
                    Path.GetTempPath(),
                    "OpenVSA.exercise." + Guid.NewGuid().ToString("N") + StateFile.Extension);

                try
                {
                    StateFile.Save(saved, path);
                    ApplicationState back = StateFile.Load(path);

                    MeasurementState m = back.Measurements[0];

                    bool ok = Math.Abs(m.CenterFrequencyHz - centerFrequencyHz) < 1e-3 &&
                              Math.Abs(m.SpanHz - spanHz) < 1e-3 &&
                              m.Analysis.Window == WindowType.FlatTop &&
                              Math.Abs(m.Analysis.Overlap - 0.75) < 1e-12 &&
                              m.Trigger.Style == TriggerStyle.Level;

                    return new Outcome<long>(
                        ok,
                        new FileInfo(path).Length,
                        new FileInfo(path).Length + " bytes of readable JSON; centre, span, " +
                        "window, overlap and trigger all returned");
                }
                finally
                {
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                    }
                }
            });

            Step("REQ-STA-003", "A member from a later schema survives the round trip", () =>
            {
                string json = StateFile.Write(ApplicationState.Default("Bench"));
                string doctored = json.TrimEnd().TrimEnd('}') + ",\n  \"futureSetting\": 42\n}";

                ApplicationState loaded = StateFile.Read(doctored);
                string rewritten = StateFile.Write(loaded);

                bool ok = rewritten.IndexOf("futureSetting", StringComparison.Ordinal) >= 0 &&
                          rewritten.IndexOf("42", StringComparison.Ordinal) >= 0;

                return new Outcome<bool>(
                    ok, ok,
                    ok
                        ? "an unrecognised member written by later software came back intact"
                        : "an unrecognised member was discarded by the round trip");
            });

            Step("REQ-STA-004", "A recall naming an absent context is refused whole", () =>
            {
                var state = new ApplicationState
                {
                    Measurements =
                    {
                        new MeasurementState { ContextName = "Bench", SpanHz = 1e6 },
                        new MeasurementState { ContextName = "Nowhere", SpanHz = 2e6 },
                    },
                };

                var contexts = new Dictionary<string, MeasurementState>(StringComparer.Ordinal)
                {
                    { "Bench", new MeasurementState { ContextName = "Bench", SpanHz = 42e6 } },
                };

                try
                {
                    StateRecall.Apply(state, contexts);
                    return Failed<string>("a partial recall was applied");
                }
                catch (ContextMismatchException refused)
                {
                    bool intact = Math.Abs(contexts["Bench"].SpanHz - 42e6) < 1e-6;

                    return new Outcome<string>(
                        intact && refused.Missing.Contains("Nowhere"),
                        refused.Message,
                        "refused naming 'Nowhere'; the matching context was left untouched");
                }
            });
        }

        private void ExercisePresets(double centerFrequencyHz)
        {
            Step("REQ-STA-005", "A user preset is saved, applied and deleted", () =>
            {
                string directory = Path.Combine(
                    Path.GetTempPath(), "OpenVSA.exercise." + Guid.NewGuid().ToString("N"));

                try
                {
                    var library = new PresetLibrary(directory);

                    ApplicationState mine = ApplicationState.Default("Bench");
                    mine.Measurements[0].CenterFrequencyHz = centerFrequencyHz;

                    library.Save("Bench exercise", mine);

                    // A second library over the same directory is what a restart is.
                    var afterRestart = new PresetLibrary(directory);

                    ApplicationState applied = afterRestart.Load("Bench exercise");
                    bool survived =
                        Math.Abs(applied.Measurements[0].CenterFrequencyHz - centerFrequencyHz) < 1e-3;

                    bool deleted = afterRestart.Delete("Bench exercise");

                    return new Outcome<bool>(
                        survived && deleted && afterRestart.Names.Count == 0,
                        survived,
                        "saved to " + Path.GetFileName(directory) +
                        ", found again by a fresh library, and deleted");
                }
                finally
                {
                    if (Directory.Exists(directory))
                    {
                        Directory.Delete(directory, recursive: true);
                    }
                }
            });

            Step("REQ-STA-005", "The factory preset leaves the hardware setup alone", () =>
            {
                string json = StateFile.Write(Presets.Factory("Bench"));

                string[] hardware = { "resource", "visa", "gpib", "instrument" };
                string mentioned = hardware.FirstOrDefault(
                    w => json.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0);

                return new Outcome<string>(
                    mentioned == null,
                    mentioned,
                    mentioned == null
                        ? "a preset names no front end, resource or connection"
                        : "a preset mentions '" + mentioned + "'");
            });
        }

        // ---- Acquisition -----------------------------------------------------------------------

        /// <summary>
        /// Configures the instrument and takes one settled block.
        /// </summary>
        /// <remarks>
        /// Blocks rather than frames, because half of what is being exercised works on the time
        /// record: gating, overlap and triggering all need the samples, not the spectrum.
        /// Discarding the first few is <see cref="VerificationRunner.SettlingFrames"/>'s reasoning
        /// — the instrument auto-ranges at the start of a measurement.
        /// </remarks>
        private async Task<IqBlock> AcquireAsync(
            double centerFrequencyHz, double spanHz, CancellationToken ct)
        {
            if (_frontEnd.State == FrontEndState.Disconnected)
            {
                await _frontEnd.ConnectAsync(ct).ConfigureAwait(false);
            }

            int transform = AcquisitionLaw.TransformLengthFor(801, AnalysisPath.ComplexZoom);

            AcquisitionPlan plan = _frontEnd.Negotiate(
                new AcquisitionRequest(centerFrequencyHz, spanHz, transform, 0.0));

            await _frontEnd.ConfigureAsync(plan, ct).ConfigureAwait(false);
            await _frontEnd.ArmAsync(ct).ConfigureAwait(false);

            IqBlock kept = null;

            try
            {
                for (int i = 0; i <= VerificationRunner.SettlingFrames; i++)
                {
                    IqBlock block = await _frontEnd.AcquireNextAsync(ct).ConfigureAwait(false);

                    if (block == null)
                    {
                        break;
                    }

                    if (kept != null)
                    {
                        kept.Dispose();
                    }

                    kept = block;
                }
            }
            finally
            {
                await _frontEnd.AbortAsync().ConfigureAwait(false);
            }

            return kept;
        }

        private static double PeakMagnitude(IqBlock block)
        {
            double peak = 0.0;

            for (int n = 0; n < block.SampleCount; n++)
            {
                double magnitudeSquared = block.GetSample(n).MagnitudeSquared;

                if (magnitudeSquared > peak)
                {
                    peak = magnitudeSquared;
                }
            }

            return Math.Sqrt(peak);
        }

        private static int Triggers(
            IqBlock block, double level, HoldoffStyle style, double holdoffSeconds) =>
            TriggerSearch.Instants(
                block,
                new TriggerSettings(
                    TriggerStyle.Level,
                    levelVolts: level,
                    holdoff: style,
                    holdoffSeconds: holdoffSeconds)).Count;

        /// <summary>
        /// The mean envelope of a block, in volts.
        /// </summary>
        /// <remarks>
        /// The level a real carrier actually crosses. Its envelope wobbles about the mean, where
        /// half the peak — the obvious choice against a synthesised burst — is a level a steady
        /// carrier sits above and never approaches from below.
        /// </remarks>
        private static double MeanMagnitude(IqBlock block)
        {
            double sum = 0.0;

            for (int n = 0; n < block.SampleCount; n++)
            {
                sum += Math.Sqrt(block.GetSample(n).MagnitudeSquared);
            }

            return block.SampleCount > 0 ? sum / block.SampleCount : 0.0;
        }

        // ---- Reporting -------------------------------------------------------------------------

        private sealed class Outcome<T>
        {
            public Outcome(bool passed, T value, string detail)
            {
                Passed = passed;
                Value = value;
                Detail = detail;
            }

            public bool Passed { get; }

            public T Value { get; }

            public string Detail { get; }
        }

        private static Outcome<T> Failed<T>(string detail) =>
            new Outcome<T>(false, default(T), detail);

        /// <summary>
        /// Runs one step, recording what happened even when it throws.
        /// </summary>
        /// <remarks>
        /// A feature that fails must not stop the ones after it from being exercised. The point of
        /// the run is to find out how much works, and an exception on the third of fifteen steps
        /// answers that far less well than fifteen verdicts.
        /// </remarks>
        private T Step<T>(string requirement, string name, Func<Outcome<T>> step)
        {
            try
            {
                Outcome<T> outcome = step();
                Record(requirement, name, outcome.Passed, outcome.Detail);
                return outcome.Passed ? outcome.Value : default(T);
            }
            catch (Exception failure)
            {
                Record(
                    requirement, name, false,
                    failure.GetType().Name + ": " + failure.Message.Split('\n')[0]);
                return default(T);
            }
        }

        private void Record(string requirement, string name, bool passed, string detail) =>
            _results.Add(new ExerciseResult(requirement, name, passed, detail));

        private static string Hz(double hertz)
        {
            double magnitude = Math.Abs(hertz);

            if (magnitude >= 1e9)
            {
                return (hertz / 1e9).ToString("0.000000", CultureInfo.CurrentCulture) + " GHz";
            }

            if (magnitude >= 1e6)
            {
                return (hertz / 1e6).ToString("0.0000", CultureInfo.CurrentCulture) + " MHz";
            }

            if (magnitude >= 1e3)
            {
                return (hertz / 1e3).ToString("0.000", CultureInfo.CurrentCulture) + " kHz";
            }

            return hertz.ToString("0.0", CultureInfo.CurrentCulture) + " Hz";
        }

        private static string Db(double dbm) =>
            dbm.ToString("0.00", CultureInfo.CurrentCulture) + " dBm";

        private static string Signed(double value) =>
            value.ToString("+0.0;-0.0;0.0", CultureInfo.CurrentCulture);
    }
}
