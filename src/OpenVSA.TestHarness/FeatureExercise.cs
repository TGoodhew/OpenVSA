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
using OpenVSA.Dsp.Zoom;
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
            double measuredPeakDbm = double.NaN;

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
                    int highest = frame.IndexOfPeak();
                    measuredPeakDbm = highest < 0 ? double.NaN : frame.LevelsDbm[highest];

                    ExerciseZoom(block, frame, actualToneHz);
                    ExerciseZoomControls(block, spanHz, actualToneHz);
                    ExerciseTransformCeiling(block, frame);
                    ExerciseNoiseCorrection(block, frame, actualToneHz);
                    ExerciseOverlap(block, actualToneHz);
                    ExerciseGating(block, frame);
                    ExerciseFormats(frame);
                    ExerciseTraceMath(frame);
                    ExerciseRegisters(frame);
                    ExerciseBandMeasurements(frame, actualToneHz);
                    ExerciseMarkers(frame, actualToneHz);
                    ExerciseLimits(frame);
                    ExerciseUnits(frame);
                    ExerciseCorrections(frame);
                }

                ExerciseTriggering(block);
                ExerciseTimestamps(block);
            }

            ExercisePlanning(spanHz);
            await ExerciseSpectrogramAsync(centerFrequencyHz, spanHz, levelDbm, ct)
                .ConfigureAwait(false);
            await ExerciseAutoRangeAsync(
                centerFrequencyHz, spanHz, toneHz, levelDbm, measuredPeakDbm, ct)
                .ConfigureAwait(false);
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

        /// <summary>
        /// Zooms the acquired block onto the carrier and checks what came out (<c>REQ-DSP-023</c>,
        /// <c>REQ-DSP-023a</c>).
        /// </summary>
        /// <remarks>
        /// What this adds over the unit tests, which already measure ripple and alias rejection on
        /// signals they built themselves: a record whose length and rate the instrument chose, a
        /// carrier the generator placed rather than one synthesised at an exact bin, and the whole
        /// amplitude chain either side of the downconverter. A zoom that changed the level of what
        /// it zoomed into would be invisible to a test that only looks at the downconverter.
        /// </remarks>
        private void ExerciseZoom(IqBlock block, SpectrumFrame full, double toneHz)
        {
            double shiftHz = toneHz - block.CenterFrequencyHz;

            // Capability-driven, not a chosen number: the zoom band has to fit inside what the
            // block actually holds, so the shallowest usable decimation follows from this block's
            // rate and this carrier's offset. Eight is a floor, so that the step is a real zoom
            // even on a wide acquisition.
            double headroomHz = block.SampleRateHz - 2.0 * Math.Abs(shiftHz);
            int decimation = 8;

            if (headroomHz > 0.0)
            {
                decimation = Math.Max(
                    decimation,
                    (int)Math.Ceiling(
                        DdcDesignTargets.UsableBandwidthFraction * block.SampleRateHz / headroomHz));
            }

            IqBlock zoomed = Step("REQ-DSP-023", "Zoom onto the carrier", () =>
            {
                var ddc = DigitalDownconverter.ForDecimation(
                    block.SampleRateHz, shiftHz, decimation);

                if (ddc.OutputCountFor(block.SampleCount) <= 0)
                {
                    return Failed<IqBlock>(
                        block.SampleCount + " samples is short of the " + ddc.MinimumInputSamples +
                        " a " + ddc.TapCount + "-tap filter needs");
                }

                IqBlock narrow = ddc.Downconvert(block);

                return new Outcome<IqBlock>(
                    true, narrow,
                    "decimated by " + decimation + " to " + narrow.SampleCount + " samples at " +
                    Hz(narrow.SampleRateHz) + ", centred on " + Hz(narrow.CenterFrequencyHz) +
                    " through " + ddc.TapCount + " taps");
            });

            if (zoomed == null)
            {
                return;
            }

            using (zoomed)
            {
                var computer = new SpectrumComputer(WindowType.FlatTop, null, null)
                {
                    TrimToAnalysisSpan = true,
                };

                SpectrumFrame narrow = Step("REQ-DSP-023", "The carrier lands at the zoom centre", () =>
                {
                    SpectrumFrame spectrum = computer.Compute(zoomed);
                    int peak = spectrum.IndexOfPeak();

                    if (peak < 0)
                    {
                        return Failed<SpectrumFrame>("the zoomed spectrum had no peak");
                    }

                    double found = spectrum.FrequencyAt(peak);
                    double error = found - toneHz;
                    bool ok = Math.Abs(error) <= 2.0 * spectrum.BinWidthHz;

                    return new Outcome<SpectrumFrame>(
                        ok, spectrum,
                        "peak at " + Hz(found) + " (" + Signed(error) + " Hz from the carrier, " +
                        Hz(spectrum.BinWidthHz) + " bins)");
                });

                if (narrow == null)
                {
                    return;
                }

                Step("REQ-DSP-023a", "Zoom does not change the carrier's level", () =>
                {
                    int wide = full.IndexOfPeak();
                    int close = narrow.IndexOfPeak();

                    if (wide < 0 || close < 0)
                    {
                        return Failed<double>("one of the two spectra had no peak");
                    }

                    double error = narrow.LevelsDbm[close] - full.LevelsDbm[wide];

                    // A tenth of a decibel: twice REQ-DSP-023a's whole passband ripple budget, and
                    // still tight enough that a missing normalisation - the downconverter's DC gain
                    // left as the analytic sinc rather than normalised - would show as about 0.3 dB.
                    bool ok = Math.Abs(error) <= 0.1;

                    return new Outcome<double>(
                        ok, error,
                        Db(full.LevelsDbm[wide]) + " at full span, " + Db(narrow.LevelsDbm[close]) +
                        " zoomed, " + Signed(error) + " dB apart");
                });

                Step("REQ-DSP-023", "The zoomed record's RBW follows its own rate", () =>
                {
                    // Zoom buys resolution by letting a transform of a given size cover more
                    // wall-clock time - and only if the capture holds more time to cover. On a
                    // block of a thousand samples it holds none: the source transform already
                    // spans most of the record, the downconverter's transient takes a slice of
                    // what is left, and the two analyses end up looking at the same number of
                    // microseconds. Asserting that zoom always sharpens the RBW would be asserting
                    // something about the instrument's record length, not about this feature.
                    //
                    // What must hold either way is REQ-DSP-020's relation, computed from the rate
                    // the downconverter declared. Get that rate wrong - divide by the wrong factor,
                    // or leave the parent's - and this is immediate.
                    int points = SpectrumComputer.TransformLengthFor(zoomed.SampleCount);
                    double recordSeconds = points / zoomed.SampleRateHz;
                    double expected = ResolutionBandwidth.ForRecordLength(
                        Window.Get(WindowType.FlatTop, points), recordSeconds);

                    double sourceSeconds =
                        SpectrumComputer.TransformLengthFor(block.SampleCount) /
                        block.SampleRateHz;

                    bool ok = Math.Abs(narrow.ResolutionBandwidthHz - expected) <=
                              1e-6 * expected;

                    return new Outcome<double>(
                        ok, narrow.ResolutionBandwidthHz,
                        "RBW " + Hz(narrow.ResolutionBandwidthHz) + " from " + points +
                        " points at " + Hz(zoomed.SampleRateHz) + "; the zoomed transform spans " +
                        (recordSeconds * 1e6).ToString("0.0", CultureInfo.CurrentCulture) +
                        " us against the source's " +
                        (sourceSeconds * 1e6).ToString("0.0", CultureInfo.CurrentCulture) +
                        " us, so this record has no more resolution to give");
                });

                Step("REQ-DSP-023a", "The zoomed block declares its own alias-free bandwidth", () =>
                {
                    object declared;

                    if (!zoomed.Extended.TryGetValue(
                            IqBlockMetadata.UsableBandwidthKey, out declared) ||
                        !(declared is double))
                    {
                        return Failed<double>("the zoomed block declared no usable bandwidth");
                    }

                    double usable = (double)declared;
                    double expected =
                        DdcDesignTargets.UsableBandwidthFraction * zoomed.SampleRateHz;
                    double span = narrow.FrequencyAt(narrow.PointCount - 1) - narrow.FrequencyAt(0);

                    // Inherited from the front end instead of rewritten, this would still read the
                    // instrument's own figure - tens of megahertz - and the display would draw the
                    // decimation filter's roll-off as though it were measurement data.
                    bool ok = Math.Abs(usable - expected) <= 1.0 && span <= usable * 1.05;

                    return new Outcome<double>(
                        ok, usable,
                        "declares " + Hz(usable) + " usable of " + Hz(zoomed.SampleRateHz) +
                        " sampled; the trimmed spectrum spans " + Hz(span));
                });

                Step("REQ-ACQ-010", "The zoomed record agrees on when the trigger was", () =>
                {
                    DateTime before = BlockTimeline.TriggerInstant(
                        block.AcquiredUtc, block.TriggerOffsetSeconds);
                    DateTime after = BlockTimeline.TriggerInstant(
                        zoomed.AcquiredUtc, zoomed.TriggerOffsetSeconds);

                    long drift = Math.Abs((after - before).Ticks);
                    bool ok = drift <= 1 && zoomed.AcquiredUtc > block.AcquiredUtc;

                    return new Outcome<long>(
                        ok, drift,
                        "record starts " +
                        (zoomed.AcquiredUtc - block.AcquiredUtc).TotalMilliseconds.ToString(
                            "0.000", CultureInfo.CurrentCulture) +
                        " ms later, trigger moved " + drift + " ticks");
                });
            }
        }

        /// <summary>
        /// Drives the zoom controls over the real capture (<c>REQ-DSP-023</c>, <c>REQ-REC-004</c>).
        /// </summary>
        /// <remarks>
        /// The interesting question on a real instrument is not whether the policy bound arithmetic
        /// works — the unit tests settle that — but which of the two limits binds first. On a
        /// thousand-sample block the record runs out long before the 256:1 policy does, and a
        /// harness that only tested the policy would report a feature working that no capture on
        /// this bench can actually reach.
        /// </remarks>
        private void ExerciseZoomControls(IqBlock block, double sourceSpanHz, double toneHz)
        {
            var zoom = new ZoomControl(block.CenterFrequencyHz, sourceSpanHz);

            // A drag a person could plausibly make: a region around the carrier an eighth of the
            // span wide.
            double half = sourceSpanHz / 16.0;

            SpectrumFrame narrow = Step("REQ-DSP-023", "Select Area zooms to a dragged region", () =>
            {
                zoom.SelectArea(toneHz - half, toneHz + half);

                DigitalDownconverter ddc;

                if (!zoom.TryCreateDownconverter(block.SampleRateHz, out ddc))
                {
                    return Failed<SpectrumFrame>(
                        "no downconverter was needed for a " + Hz(zoom.SpanHz) + " span");
                }

                if (ddc.OutputCountFor(block.SampleCount) <= 0)
                {
                    return Failed<SpectrumFrame>(
                        "this record holds " + block.SampleCount + " samples; the zoom needs " +
                        ddc.MinimumInputSamples);
                }

                using (IqBlock zoomed = ddc.Downconvert(block))
                {
                    var computer = new SpectrumComputer(WindowType.FlatTop, null, null)
                    {
                        TrimToAnalysisSpan = true,
                    };

                    SpectrumFrame spectrum = computer.Compute(zoomed);
                    int peak = spectrum.IndexOfPeak();

                    if (peak < 0)
                    {
                        return Failed<SpectrumFrame>("the selected area held no peak");
                    }

                    double error = spectrum.FrequencyAt(peak) - toneHz;
                    bool ok = Math.Abs(error) <= 2.0 * spectrum.BinWidthHz &&
                              Math.Abs(zoom.CenterFrequencyHz - toneHz) <= 1.0;

                    return new Outcome<SpectrumFrame>(
                        ok, spectrum,
                        "dragged " + Hz(2.0 * half) + " about the carrier: " +
                        zoom.Annotation() + ", decimated by " + ddc.Decimation +
                        ", peak " + Signed(error) + " Hz from the carrier");
                }
            });

            Step("REQ-REC-004", "A zoom past the bound is refused with the bound named", () =>
            {
                try
                {
                    zoom.SetSpan(zoom.NarrowestSpanHz / 2.0);

                    return Failed<string>("a span past the bound was accepted");
                }
                catch (ArgumentOutOfRangeException refused)
                {
                    string reason = refused.Message.Split('\n')[0];

                    bool ok = reason.IndexOf(
                                  ZoomControl.MaximumZoomRatio.ToString(CultureInfo.CurrentCulture),
                                  StringComparison.Ordinal) >= 0;

                    return new Outcome<string>(ok, reason, reason);
                }
            });

            Step("REQ-REC-004", "Which limit binds first: the bound or the record", () =>
            {
                // The bound is a product policy and says nothing about samples. Whether a capture
                // can reach it is a separate question, and the honest answer for this block is no -
                // so what matters is that asking is refused for the right reason rather than
                // answered with a record built from a filter's transient.
                zoom.SetSpan(zoom.NarrowestSpanHz);

                DigitalDownconverter ddc;

                if (!zoom.TryCreateDownconverter(block.SampleRateHz, out ddc))
                {
                    return Failed<int>("the deepest allowed zoom needed no downconverter");
                }

                int available = ddc.OutputCountFor(block.SampleCount);

                if (available > 0)
                {
                    return new Outcome<int>(
                        true, available,
                        "the record reaches the " + ZoomControl.MaximumZoomRatio +
                        ":1 bound: " + Hz(zoom.NarrowestSpanHz) + " from " + block.SampleCount +
                        " samples, leaving " + available + " analysed");
                }

                string refusal;

                try
                {
                    ddc.Downconvert(block).Dispose();
                    refusal = null;
                }
                catch (ArgumentException expected)
                {
                    refusal = expected.Message.Split('\n')[0];
                }

                bool ok = refusal != null &&
                          refusal.IndexOf(
                              ddc.MinimumInputSamples.ToString(CultureInfo.CurrentCulture),
                              StringComparison.Ordinal) >= 0;

                return new Outcome<int>(
                    ok, ddc.MinimumInputSamples,
                    "the record binds before the bound does: " + Hz(zoom.NarrowestSpanHz) +
                    " needs " + ddc.MinimumInputSamples + " samples through " + ddc.TapCount +
                    " taps and this block holds " + block.SampleCount + ", refused saying so");
            });

            Step("REQ-DSP-023", "Full Span returns the whole capture", () =>
            {
                zoom.FullSpan();

                bool ok = zoom.IsFullSpan &&
                          Math.Abs(zoom.CenterFrequencyHz - block.CenterFrequencyHz) <= 1.0 &&
                          Math.Abs(zoom.SpanHz - sourceSpanHz) <= 1.0 &&
                          narrow != null;

                return new Outcome<double>(
                    ok, zoom.SpanHz,
                    "back to " + zoom.Annotation() + " at " + Hz(zoom.CenterFrequencyHz));
            });
        }

        /// <summary>
        /// Bounds the transform below what the record could give (<c>REQ-DSP-024</c>).
        /// </summary>
        private void ExerciseTransformCeiling(IqBlock block, SpectrumFrame full)
        {
            Step("REQ-DSP-024", "A transform past Max FFT Size is bounded, not failed", () =>
            {
                // Half of what this record would naturally take, so the ceiling certainly binds
                // whatever length the instrument handed us.
                int ceiling = Math.Max(2, SpectrumComputer.TransformLengthFor(block.SampleCount) / 2);

                var computer = new SpectrumComputer(WindowType.FlatTop, null, null)
                {
                    TrimToAnalysisSpan = true,
                    MaxTransformLength = ceiling,
                };

                SpectrumFrame capped = computer.Compute(block);

                if (!capped.TransformWasCapped)
                {
                    return Failed<double>(
                        "a ceiling of " + ceiling + " did not bind on a " + block.SampleCount +
                        "-sample block");
                }

                double ratio = capped.ResolutionBandwidthHz / full.ResolutionBandwidthHz;
                int peak = capped.IndexOfPeak();

                // Bounded means the measurement still happened. Half the transform is twice the
                // RBW, and the annotation has to say which of the two is on screen.
                bool ok = peak >= 0 &&
                          Math.Abs(ratio - 2.0) < 0.01 &&
                          capped.TransformLength == ceiling &&
                          !full.TransformWasCapped;

                return new Outcome<double>(
                    ok, ratio,
                    "capped at " + ceiling + " of " + full.TransformLength + " points: RBW " +
                    Hz(full.ResolutionBandwidthHz) + " to " + Hz(capped.ResolutionBandwidthHz) +
                    ", still measuring, and the frame says it was capped");
            });
        }

        /// <summary>
        /// Characterises the analyser's own noise floor and corrects against it
        /// (<c>REQ-DSP-024</c>).
        /// </summary>
        /// <remarks>
        /// The floor here is the E4406A's real one, measured from the same acquisition away from
        /// the carrier rather than modelled. What the bench adds over the unit tests is that the
        /// floor has the instrument's shape and the instrument's spurs, and neither is flat.
        /// </remarks>
        private void ExerciseNoiseCorrection(IqBlock block, SpectrumFrame full, double toneHz)
        {
            NoiseFloor floor = Step("REQ-DSP-024", "Characterise the analyser's own noise floor", () =>
            {
                NoiseFloor characterised = NoiseFloor.FromTrace(full);

                double lowest = double.PositiveInfinity;
                double highest = double.NegativeInfinity;

                for (int i = 0; i < full.PointCount; i++)
                {
                    // Away from the carrier, so the figures describe the floor rather than the tone.
                    if (Math.Abs(full.FrequencyAt(i) - toneHz) < 20.0 * full.BinWidthHz)
                    {
                        continue;
                    }

                    lowest = Math.Min(lowest, full.LevelsDbm[i]);
                    highest = Math.Max(highest, full.LevelsDbm[i]);
                }

                bool ok = characterised.PointCount == full.PointCount &&
                          Math.Abs(characterised.ResolutionBandwidthHz -
                                   full.ResolutionBandwidthHz) < 1e-6 &&
                          highest > lowest;

                return new Outcome<NoiseFloor>(
                    ok, characterised,
                    characterised.PointCount + " points at " +
                    Hz(characterised.ResolutionBandwidthHz) + " RBW; the floor runs " +
                    Db(lowest) + " to " + Db(highest) + " away from the carrier, a spread of " +
                    (highest - lowest).ToString("0.0", CultureInfo.CurrentCulture) + " dB");
            });

            if (floor == null)
            {
                return;
            }

            Step("REQ-DSP-024", "Correcting a trace against its own floor bottoms it out", () =>
            {
                // The trace is its own characterisation, so every bin subtracts exactly itself.
                // Nothing may come back negative, and nothing may come back NaN - which is the half
                // of the criterion that is easy to get wrong, because half the bins of any real
                // noise trace sit below the mean of the floor they were measured from.
                SpectrumFrame corrected = NoiseCorrection.Apply(full, floor);

                double worst = double.PositiveInfinity;
                int bad = 0;

                for (int i = 0; i < corrected.PointCount; i++)
                {
                    double level = corrected.LevelsDbm[i];

                    if (double.IsNaN(level) || level < AmplitudeScale.FloorDbm)
                    {
                        bad++;
                    }

                    worst = Math.Min(worst, level);
                }

                bool ok = bad == 0 &&
                          corrected.NoiseCorrected &&
                          !corrected.HasPhase &&
                          worst <= AmplitudeScale.FloorDbm;

                return new Outcome<int>(
                    ok, bad,
                    corrected.PointCount + " points, none negative and none NaN; the lowest reads " +
                    Db(worst) + ", the reported measurement limit");
            });

            Step("REQ-DSP-024", "The carrier survives a correction the noise does not", () =>
            {
                // Correction is a large change to a bin at the floor and almost none to the
                // carrier, which stands tens of dB above it. If the carrier moves measurably, the
                // subtraction is not a power subtraction.
                double floorLevel = full.LevelsDbm[0];
                NoiseFloor flat = NoiseFloor.Flat(floorLevel, full.ResolutionBandwidthHz);

                SpectrumFrame corrected = NoiseCorrection.Apply(full, flat);

                int peak = full.IndexOfPeak();

                if (peak < 0)
                {
                    return Failed<double>("the trace had no peak to check");
                }

                double moved = corrected.LevelsDbm[peak] - full.LevelsDbm[peak];
                double headroom = full.LevelsDbm[peak] - floorLevel;

                bool ok = headroom > 20.0 && Math.Abs(moved) < 0.1;

                return new Outcome<double>(
                    ok, moved,
                    "the carrier stands " + headroom.ToString("0.0", CultureInfo.CurrentCulture) +
                    " dB above a floor taken at " + Db(floorLevel) + " and moved " +
                    moved.ToString("0.000", CultureInfo.CurrentCulture) + " dB");
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

        private void ExerciseUnits(SpectrumFrame frame)
        {
            Step("REQ-AMP-002", "The carrier reads the same power in every unit", () =>
            {
                int peak = frame.IndexOfPeak();
                double dbm = frame.LevelsDbm[peak];

                double voltsPeak = AmplitudeUnits.ToVoltsPeak(dbm, AmplitudeUnit.Dbm, 50.0);
                double back = AmplitudeUnits.FromVoltsPeak(voltsPeak, AmplitudeUnit.Dbm, 50.0);

                double watts = AmplitudeUnits.Convert(
                    dbm, AmplitudeUnit.Dbm, AmplitudeUnit.Watts, 50.0);
                double dbmv = AmplitudeUnits.Convert(
                    dbm, AmplitudeUnit.Dbm, AmplitudeUnit.DbMillivolts, 50.0);

                bool ok = Math.Abs(back - dbm) < 1e-9;

                return new Outcome<double>(
                    ok, voltsPeak,
                    Db(dbm) + " is " + (voltsPeak * 1e3).ToString("0.000", CultureInfo.CurrentCulture) +
                    " mV peak, " + (watts * 1e6).ToString("0.000", CultureInfo.CurrentCulture) +
                    " uW, " + dbmv.ToString("0.00", CultureInfo.CurrentCulture) + " dBmV");
            });

            Step("REQ-AMP-002", "Moving to 75 ohms drops the reading by 1.76 dB", () =>
            {
                // REQ-AMP-002's criterion, on a level the instrument actually measured. The sign
                // matters as much as the figure: the same voltage across a larger resistance
                // dissipates less power, so the reading falls.
                int peak = frame.IndexOfPeak();
                double voltsPeak = AmplitudeUnits.ToVoltsPeak(
                    frame.LevelsDbm[peak], AmplitudeUnit.Dbm, 50.0);

                double at50 = AmplitudeUnits.FromVoltsPeak(voltsPeak, AmplitudeUnit.Dbm, 50.0);
                double at75 = AmplitudeUnits.FromVoltsPeak(voltsPeak, AmplitudeUnit.Dbm, 75.0);

                double change = at75 - at50;
                bool ok = Math.Abs(change - (-1.7609)) < 1e-3;

                return new Outcome<double>(
                    ok, change,
                    Db(at50) + " into 50 ohms is " + Db(at75) + " into 75, a change of " +
                    change.ToString("+0.0000;-0.0000", CultureInfo.CurrentCulture) + " dB");
            });
        }

        private void ExerciseCorrections(SpectrumFrame frame)
        {
            Step("REQ-AMP-003", "A correction of known shape lands exactly on the real trace", () =>
            {
                var table = new CorrectionTable("exercise slope", new[]
                {
                    new CorrectionPoint(frame.StartFrequencyHz, 0.0),
                    new CorrectionPoint(frame.StopFrequencyHz, 10.0),
                });

                SpectrumFrame corrected = Corrections.Apply(frame, table);
                double worst = 0.0;

                for (int i = 0; i < frame.PointCount; i++)
                {
                    // Bins near the noise floor are the ones a float would round badly, so the
                    // comparison is made where there is signal to compare.
                    if (frame.LevelsDbm[i] < AmplitudeScale.FloorDbm + 1.0)
                    {
                        continue;
                    }

                    double expected = table.At(frame.FrequencyAt(i)).MagnitudeDb;
                    double measured = corrected.LevelsDbm[i] - frame.LevelsDbm[i];

                    worst = Math.Max(worst, Math.Abs(measured - expected));
                }

                return new Outcome<double>(
                    worst < 0.01, worst,
                    "0 to 10 dB across the span, applied to the measured trace within " +
                    worst.ToString("G3") + " dB");
            });

            Step("REQ-AMP-004", "A fixture is de-embedded off the real trace", () =>
            {
                // REQ-AMP-004's criterion - 0.05 dB and 0.5 degrees - on a measured spectrum
                // rather than a synthesised one, so the noise and the dynamic range are real.
                CorrectionTable fixture = new CorrectionTable("exercise fixture", new[]
                {
                    new CorrectionPoint(frame.StartFrequencyHz, -0.5, 0.0),
                    new CorrectionPoint(frame.CenterFrequencyHz, -2.5, 45.0),
                    new CorrectionPoint(frame.StopFrequencyHz, -6.0, 130.0),
                });

                SpectrumFrame measured = Corrections.Apply(frame, fixture);
                SpectrumFrame recovered = Corrections.Remove(measured, fixture);

                double worstDb = 0.0;
                double worstDegrees = 0.0;

                for (int i = 0; i < frame.PointCount; i++)
                {
                    if (frame.LevelsDbm[i] < AmplitudeScale.FloorDbm + 1.0)
                    {
                        continue;
                    }

                    worstDb = Math.Max(
                        worstDb, Math.Abs(recovered.LevelsDbm[i] - frame.LevelsDbm[i]));

                    worstDegrees = Math.Max(
                        worstDegrees, Math.Abs(Degrees(recovered, i) - Degrees(frame, i)));
                }

                bool ok = worstDb <= 0.05 && worstDegrees <= 0.5;

                return new Outcome<double>(
                    ok, worstDb,
                    "recovered within " + worstDb.ToString("G3") + " dB and " +
                    worstDegrees.ToString("G3") + " degrees across " + frame.PointCount + " points");
            });

            Step("REQ-AMP-004", "De-embedding is complex, not magnitude-only", () =>
            {
                // A fixture flat in magnitude and not in phase. A magnitude-only implementation
                // would leave the whole phase error behind, which for a modulated signal is the
                // part that shows up as error rather than as level.
                var phaseOnly = new CorrectionTable("phase-only", new[]
                {
                    new CorrectionPoint(frame.StartFrequencyHz, 0.0, 0.0),
                    new CorrectionPoint(frame.StopFrequencyHz, 0.0, 120.0),
                });

                var magnitudeOnly = new CorrectionTable(
                    "its magnitude",
                    phaseOnly.Points.Select(
                        p => new CorrectionPoint(p.FrequencyHz, p.MagnitudeDb)));

                SpectrumFrame measured = Corrections.Apply(frame, phaseOnly);
                SpectrumFrame partial = Corrections.Remove(measured, magnitudeOnly);
                SpectrumFrame full = Corrections.Remove(measured, phaseOnly);

                double worstPartial = 0.0;
                double worstFull = 0.0;

                for (int i = 0; i < frame.PointCount; i++)
                {
                    if (frame.LevelsDbm[i] < AmplitudeScale.FloorDbm + 1.0)
                    {
                        continue;
                    }

                    worstPartial = Math.Max(
                        worstPartial, Math.Abs(Degrees(partial, i) - Degrees(frame, i)));

                    worstFull = Math.Max(worstFull, Math.Abs(Degrees(full, i) - Degrees(frame, i)));
                }

                bool ok = worstPartial > 10.0 && worstFull <= 0.5;

                return new Outcome<double>(
                    ok, worstFull,
                    "magnitude-only leaves " + worstPartial.ToString("F1") +
                    " degrees; complex leaves " + worstFull.ToString("G3"));
            });
        }

        private void ExerciseTimestamps(IqBlock block)
        {
            Step("REQ-ACQ-010", "Timestamps come from a monotonic high-resolution clock", () =>
            {
                // The block this run analysed carries one, and the clock behind it resolves far
                // finer than a system timer tick - which is the whole reason for not using
                // DateTime.UtcNow, whose granularity is longer than a block lasts.
                double blockSeconds = block.SampleCount / block.SampleRateHz;

                bool ok = AcquisitionClock.IsHighResolution &&
                          AcquisitionClock.ResolutionSeconds < blockSeconds &&
                          block.AcquiredUtc.Kind == DateTimeKind.Utc;

                // The relationship REQ-ACQ-010 defines: the timestamp is the first sample, and the
                // trigger lies TriggerOffsetSeconds after it.
                DateTime trigger = BlockTimeline.TriggerInstant(
                    block.AcquiredUtc, block.TriggerOffsetSeconds);

                return new Outcome<double>(
                    ok,
                    AcquisitionClock.ResolutionSeconds,
                    "clock resolves to " +
                    (AcquisitionClock.ResolutionSeconds * 1e9).ToString("F1") +
                    " ns against a block of " +
                    (blockSeconds * 1e6).ToString("F1") + " us; trigger at " +
                    trigger.ToString("HH:mm:ss.fffffff", CultureInfo.InvariantCulture));
            });

            Step("REQ-ACQ-010", "A gap-free timeline advances by exactly the block duration", () =>
            {
                // Asserted on a timeline seeded from the real block's rate and length. This
                // transport arms and reads over the bus for every block, so its own timestamps
                // have real gaps between them and cannot show this - saying so is more honest than
                // asserting a continuity the instrument does not have.
                var timeline = new BlockTimeline(block.AcquiredUtc);
                DateTime first = timeline.Next(block.SampleCount, block.SampleRateHz);

                double expected = block.SampleCount / block.SampleRateHz;
                double worst = 0.0;

                for (int i = 1; i <= 100; i++)
                {
                    double elapsed =
                        (timeline.Next(block.SampleCount, block.SampleRateHz) - first).TotalSeconds;

                    worst = Math.Max(worst, Math.Abs(elapsed - i * expected));
                }

                // One clock tick: two timestamps, each rounded independently. A bound, not a
                // budget - it does not grow with the number of blocks, which is exactly what the
                // accumulating version this replaced did.
                const double oneTickSeconds = 100e-9;

                return new Outcome<double>(
                    worst <= oneTickSeconds, worst,
                    "100 blocks of " + (expected * 1e6).ToString("F2") +
                    " us stay within " + (worst * 1e9).ToString("F1") + " ns in total");
            });
        }

        private void ExercisePlanning(double spanHz)
        {
            Step("REQ-ACQ-002", "An impossible main time is clamped with both remedies named", () =>
            {
                // Against the connected instrument's own capabilities, so the numbers in the
                // message are the ones this front end would actually impose.
                const double wanted = 1.0;

                PlannedAcquisition plan = AcquisitionPlanner.PlanForMainTime(
                    _frontEnd.Capabilities, 1e9, spanHz, wanted, 0.0, AnalysisPath.ComplexZoom);

                ParameterCoercion clamped = plan.Coercions
                    .FirstOrDefault(c => c.Parameter == "MainTimeLength");

                if (clamped == null)
                {
                    return Failed<string>(
                        "a one-second record was accepted, so nothing was clamped to explain");
                }

                bool ok = clamped.Reason.IndexOf("reduce the span", StringComparison.Ordinal) >= 0 &&
                          clamped.Reason.IndexOf("frequency points", StringComparison.Ordinal) >= 0 &&
                          clamped.Honoured < wanted;

                return new Outcome<string>(ok, clamped.Reason, clamped.Reason);
            });

            Step("REQ-DSP-021", "RBW couples to span, uncouples, and refuses what it cannot reach", () =>
            {
                // Against this instrument's declared capture depth, so the reachable range is the
                // one it would actually impose rather than a figure from the specification.
                var control = new ResolutionBandwidthControl(
                    _frontEnd.Capabilities, spanHz, WindowType.FlatTop);

                ResolutionBandwidthRange range = control.Achievable;

                control.SetCoupling(ResolutionBandwidthCoupling.Coupled);
                control.SetSpanToRatio(137.0);

                double coupled = control.SetSpan(spanHz / 2.0);
                bool ratioHeld = Math.Abs(coupled - spanHz / 2.0 / 137.0) < 1e-6;

                // REQ-DSP-020's relation, on the value the coupling produced.
                double enbw = Window.Get(WindowType.FlatTop, 4096).Enbw;
                bool relationExact =
                    Math.Abs(ResolutionBandwidth.ForRecordLength(enbw, control.RecordSeconds) - coupled)
                        < coupled * 1e-9;

                control.SetCoupling(ResolutionBandwidthCoupling.Uncoupled);
                bool held = Math.Abs(control.SetSpan(spanHz) - coupled) < 1e-9;

                bool refused;

                try
                {
                    control.SetResolutionBandwidth(range.MinHz / 10.0);
                    refused = false;
                }
                catch (ArgumentOutOfRangeException rejection)
                {
                    refused = rejection.Message.IndexOf(
                        "finest available", StringComparison.Ordinal) >= 0;
                }

                bool coarseEnough = range.MaxHz > 0.287 * _frontEnd.Capabilities.MaxSpanHz ||
                                    range.MaxHz >= 0.287 * spanHz;

                return new Outcome<double>(
                    ratioHeld && relationExact && held && refused && coarseEnough,
                    coupled,
                    "reachable " + range + " at " + Hz(spanHz) + "; coupled at 137:1 gave " +
                    Hz(coupled) + " over " + Hz(spanHz / 2.0) + " with T_rec " +
                    (control.RecordSeconds * 1e6).ToString("0.0", CultureInfo.CurrentCulture) +
                    " us; uncoupled it held; a tenth of the finest was refused");
            });
        }

        /// <summary>
        /// Exercises auto-ranging against the connected instrument and a real 20 dB drop
        /// (<c>REQ-ACQ-004</c>).
        /// </summary>
        /// <param name="centerFrequencyHz">Analysis centre frequency.</param>
        /// <param name="spanHz">Analysis span.</param>
        /// <param name="toneHz">Carrier frequency the generator is set to.</param>
        /// <param name="levelDbm">Generator level the first acquisition was made at.</param>
        /// <param name="peakDbm">Peak measured at that level, in dBm.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <remarks>
        /// <para>
        /// Two things are checked, and only the first of them is about this instrument. The E4406A
        /// declares no input range control — in Basic mode it ranges its own converter and takes no
        /// range command — so the requirement's last clause applies to it: the function must be
        /// <em>unavailable</em>, and asking anyway must be refused rather than quietly ignored.
        /// That is the strongest statement this bench can make about the real front end, and it is
        /// made against the real front end.
        /// </para>
        /// <para>
        /// The second step then drops the generator by 20 dB — the criterion's own number, on real
        /// hardware — re-measures, and puts the two real peaks through the decision. Range control
        /// has to be declared for that, so the capabilities are wrapped to say so; that wrapper is
        /// the one synthetic element in this exercise and it changes nothing else. What is real is
        /// what matters here: two peaks measured through the instrument, 20 dB apart, with the
        /// noise and the awkward numbers a real measurement has.
        /// </para>
        /// </remarks>
        /// <summary>
        /// Steps the generator across the span and checks the spectrogram's ridge
        /// (<c>REQ-DSP-043</c>).
        /// </summary>
        /// <remarks>
        /// The requirement's criterion is worded to catch a particular failure: a spectrogram that
        /// drew <em>something</em> while having its time axis reversed or its frequency axis
        /// mis-scaled. Testing it needs a signal whose frequency is known at each moment, which is
        /// what the generator is for — a real sweep, one acquisition per step, and the ridge
        /// checked against what the generator said it was doing at the time.
        /// </remarks>
        private async Task ExerciseSpectrogramAsync(
            double centerFrequencyHz, double spanHz, double levelDbm, CancellationToken ct)
        {
            const int steps = 9;

            var trace = new AccumulatingTrace(steps)
            {
                Accumulator = TraceAccumulator.Spectrogram,
            };

            var computer = new SpectrumComputer(WindowType.FlatTop, null, null)
            {
                TrimToAnalysisSpan = true,
            };

            // Across the middle of the span rather than the whole of it, so that the tone stays
            // clear of the roll-off at the band edges where a peak search would be reading the
            // filter rather than the carrier.
            var placed = new double[steps];
            double firstHz = centerFrequencyHz - spanHz / 4.0;
            double lastHz = centerFrequencyHz + spanHz / 4.0;

            for (int i = 0; i < steps; i++)
            {
                double wantedHz = firstHz + (lastHz - firstHz) * i / (steps - 1);

                _stimulus.SetContinuousWave(wantedHz, levelDbm);

                // From the read-back, never from what was asked for: a coerced carrier must move
                // the expectation with it.
                placed[i] = _stimulus.FrequencyHz;

                IqBlock block = await AcquireAsync(centerFrequencyHz, spanHz, ct)
                    .ConfigureAwait(false);

                if (block == null)
                {
                    Record("REQ-DSP-043", "A swept tone renders as a diagonal ridge", false,
                        "no block was produced at step " + i);
                    return;
                }

                using (block)
                {
                    trace.Add(computer.Compute(block));
                }
            }

            Step("REQ-DSP-043", "A swept tone renders as a diagonal ridge", () =>
            {
                Spectrogram history = trace.Spectrogram;

                if (history.RowCount != steps)
                {
                    return Failed<double>(
                        history.RowCount + " rows accumulated of " + steps + " acquisitions");
                }

                double worstBins = 0.0;

                for (int r = 0; r < steps; r++)
                {
                    SpectrumFrame row = history.Row(r);
                    int peak = row.IndexOfPeak();

                    if (peak < 0)
                    {
                        return Failed<double>("row " + r + " held no peak");
                    }

                    worstBins = Math.Max(
                        worstBins,
                        Math.Abs(row.FrequencyAt(peak) - placed[r]) / row.BinWidthHz);
                }

                // Row 0 is the oldest, so the ridge must ascend. A history indexed the other way
                // round draws a ridge that looks entirely plausible and runs backwards in time.
                bool ascends =
                    history.Row(0).FrequencyAt(history.Row(0).IndexOfPeak()) <
                    history.Newest.FrequencyAt(history.Newest.IndexOfPeak());

                bool ok = worstBins <= 1.0 && ascends;

                return new Outcome<double>(
                    ok, worstBins,
                    steps + " steps from " + Hz(placed[0]) + " to " + Hz(placed[steps - 1]) +
                    ": worst row " + worstBins.ToString("0.00", CultureInfo.CurrentCulture) +
                    " bins out of " + Hz(history.Newest.BinWidthHz) + ", ridge ascends: " +
                    ascends + ", over " +
                    history.HistorySeconds.ToString("0.00", CultureInfo.CurrentCulture) + " s");
            });

            Step("REQ-DSP-043", "A trace-select marker reads back the row at that time", () =>
            {
                Spectrogram history = trace.Spectrogram;

                if (history.RowCount < 3)
                {
                    return Failed<double>("not enough history to select within");
                }

                // Between two rows, nearer the earlier one - the position a dragged marker
                // actually lands at.
                int wanted = history.RowCount / 2;
                double gap = history.SecondsBeforeNewest(wanted - 1) -
                             history.SecondsBeforeNewest(wanted);
                DateTime between = history.Row(wanted).AcquiredUtc.AddSeconds(gap * 0.3);

                SpectrumFrame selected = trace.SelectRowAt(between);
                int peak = selected.IndexOfPeak();

                bool ok = ReferenceEquals(selected, history.Row(wanted)) &&
                          peak >= 0 &&
                          Math.Abs(selected.FrequencyAt(peak) - placed[wanted]) <=
                              selected.BinWidthHz;

                return new Outcome<double>(
                    ok, history.SecondsBeforeNewest(wanted),
                    "a marker " + (gap * 0.3 * 1e3).ToString("0.0", CultureInfo.CurrentCulture) +
                    " ms past row " + wanted + " selects row " + wanted + ", reading " +
                    Hz(selected.FrequencyAt(peak)) + " against a carrier placed at " +
                    Hz(placed[wanted]));
            });

            Step("REQ-TRC-001a", "A format change keeps the history; an accumulator change drops it", () =>
            {
                int before = trace.Spectrogram.RowCount;

                if (before == 0)
                {
                    return Failed<int>("no history to preserve");
                }

                SpectrumFrame oldest = trace.Spectrogram.Oldest;

                trace.Format = TraceFormat.UnwrappedPhase;

                bool kept = trace.Spectrogram.RowCount == before &&
                            ReferenceEquals(trace.Spectrogram.Oldest, oldest);

                trace.Accumulator = TraceAccumulator.DigitalPersistence;

                bool dropped = trace.Spectrogram.IsEmpty;

                return new Outcome<int>(
                    kept && dropped, before,
                    before + " rows survived a format change to " + TraceFormat.UnwrappedPhase +
                    " and were discarded by a change to " + TraceAccumulator.DigitalPersistence);
            });
        }

        private async Task ExerciseAutoRangeAsync(
            double centerFrequencyHz,
            double spanHz,
            double toneHz,
            double levelDbm,
            double peakDbm,
            CancellationToken ct)
        {
            IFrontEndCapabilities declared = _frontEnd.Capabilities;

            Step("REQ-ACQ-004", "The instrument's own range control is declared and obeyed", () =>
            {
                AutoRangeAvailability availability = AutoRangeAvailability.For(declared);

                if (!availability.IsAvailable)
                {
                    // Unavailable rather than inert: asking anyway must be refused.
                    try
                    {
                        AutoRange.Adjust(declared, 0.0, -20.0);
                        return Failed<bool>(
                            "the source declares no range control, and auto-range acted anyway");
                    }
                    catch (InvalidOperationException refused)
                    {
                        return new Outcome<bool>(
                            refused.Message.Length > 0,
                            false,
                            "unavailable, and refused when asked: " + refused.Message);
                    }
                }

                AutoRangeResult acted = AutoRange.Adjust(declared, declared.ReferenceLevelRange.MaxDbm, -20.0);

                return new Outcome<bool>(
                    true, true, "available, and a peak 20 dB down moved the level to " +
                    Db(acted.ReferenceLevelDbm));
            });

            if (double.IsNaN(peakDbm))
            {
                Record("REQ-ACQ-004", "A real 20 dB drop is ranged for, and then settles", false,
                    "no peak was measured at the first level, so there is nothing to range against");
                return;
            }

            double droppedLevelDbm = levelDbm - 20.0;
            _stimulus.SetContinuousWave(toneHz, droppedLevelDbm);

            IqBlock dropped = null;

            try
            {
                dropped = await AcquireAsync(centerFrequencyHz, spanHz, ct).ConfigureAwait(false);

                double quietPeakDbm = double.NaN;

                if (dropped != null)
                {
                    var computer = new SpectrumComputer(WindowType.FlatTop, null, null)
                    {
                        TrimToAnalysisSpan = true,
                    };

                    SpectrumFrame quieter = computer.Compute(dropped);
                    int highest = quieter.IndexOfPeak();
                    quietPeakDbm = highest < 0 ? double.NaN : quieter.LevelsDbm[highest];
                }

                double measuredDrop = peakDbm - quietPeakDbm;

                Step("REQ-ACQ-004", "A real 20 dB drop is ranged for, and then settles", () =>
                {
                    if (double.IsNaN(quietPeakDbm))
                    {
                        return Failed<double>("no peak was measured after the generator was dropped");
                    }

                    // Range control declared over this instrument's real limits; see the remarks.
                    IFrontEndCapabilities ranging = new RangeableCapabilities(declared);

                    AutoRangeResult atCarrier = AutoRange.Adjust(
                        ranging, declared.ReferenceLevelRange.MaxDbm, peakDbm);
                    AutoRangeResult afterDrop = AutoRange.Adjust(
                        ranging, atCarrier.ReferenceLevelDbm, quietPeakDbm);
                    AutoRangeResult repeated = AutoRange.Adjust(
                        ranging, afterDrop.ReferenceLevelDbm, quietPeakDbm);

                    double levelDrop = atCarrier.ReferenceLevelDbm - afterDrop.ReferenceLevelDbm;

                    // The level must follow the signal down by what the signal actually dropped,
                    // both must land in the band, and the third pass must do nothing at all.
                    bool ok = atCarrier.Changed &&
                              atCarrier.IsWithinBand &&
                              afterDrop.Changed &&
                              afterDrop.IsWithinBand &&
                              !repeated.Changed &&
                              Math.Abs(levelDrop - measuredDrop) <= HeadroomBand.Default.StepDb + 0.5;

                    return new Outcome<double>(
                        ok,
                        levelDrop,
                        "peak " + Db(peakDbm) + " → " + Db(quietPeakDbm) + " (" +
                        Signed(measuredDrop) + " dB measured); reference " +
                        Db(atCarrier.ReferenceLevelDbm) + " → " + Db(afterDrop.ReferenceLevelDbm) +
                        ", headroom " +
                        afterDrop.HeadroomDb.ToString("0.0", CultureInfo.CurrentCulture) +
                        " dB, and a repeat invocation changed nothing");
                });
            }
            finally
            {
                if (dropped != null)
                {
                    dropped.Dispose();
                }

                // The bench is put back as the rest of the run expects to find it.
                _stimulus.SetContinuousWave(toneHz, levelDbm);
            }
        }

        /// <summary>
        /// A front end's real limits, with input range control declared over them.
        /// </summary>
        /// <remarks>
        /// Used by one exercise step and nowhere else. The instrument to hand cannot be ranged, so
        /// the arithmetic of <c>REQ-ACQ-004</c> could otherwise never meet a real measured peak —
        /// and a decision rule that has only ever seen invented numbers is the thing this harness
        /// exists to distrust. Every other limit is the instrument's own.
        /// </remarks>
        private sealed class RangeableCapabilities : IFrontEndCapabilities
        {
            private readonly IFrontEndCapabilities _inner;

            public RangeableCapabilities(IFrontEndCapabilities inner)
            {
                _inner = inner;
            }

            public FrequencyRange CenterFrequencyRange => _inner.CenterFrequencyRange;
            public double MaxSpanHz => _inner.MaxSpanHz;
            public double MinSpanHz => _inner.MinSpanHz;
            public double MaxSampleRateHz => _inner.MaxSampleRateHz;
            public int MaxSamplesPerBlock => _inner.MaxSamplesPerBlock;
            public long MaxCaptureSamples => _inner.MaxCaptureSamples;
            public bool SupportsBasebandIq => _inner.SupportsBasebandIq;
            public int ChannelCount => _inner.ChannelCount;
            public bool SupportsPhaseCoherentChannels => _inner.SupportsPhaseCoherentChannels;
            public IReadOnlyList<TriggerStyle> TriggerStyles => _inner.TriggerStyles;
            public AmplitudeRange ReferenceLevelRange => _inner.ReferenceLevelRange;
            public bool SupportsExternalRef => _inner.SupportsExternalRef;
            public bool SupportsInputRangeControl => true;
            public bool SupportsRealTimeAnalysis => _inner.SupportsRealTimeAnalysis;
            public long MaxPreTriggerSamples => _inner.MaxPreTriggerSamples;
        }

        private static double Degrees(SpectrumFrame frame, int index) =>
            Math.Atan2(frame.Complex[index * 2 + 1], frame.Complex[index * 2]) * 180.0 / Math.PI;

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
