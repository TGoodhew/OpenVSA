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
using OpenVSA.Measurement.Channels;
using OpenVSA.Measurement.Contexts;
using OpenVSA.Measurement.Limits;
using OpenVSA.Measurement.Markers;
using OpenVSA.Measurement.State;
using OpenVSA.Demod.Results;
using OpenVSA.Personality;
using OpenVSA.TestHarness.Synthesis;

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

        /// <summary>The spectrum computed from the instrument's own block.</summary>
        private SpectrumFrame _measured;

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

                // Kept for the steps that run after this block goes out of scope.
                _measured = frame;

                if (frame != null)
                {
                    int highest = frame.IndexOfPeak();
                    measuredPeakDbm = highest < 0 ? double.NaN : frame.LevelsDbm[highest];

                    ExerciseZoom(block, frame, actualToneHz);
                    ExerciseZoomControls(block, spanHz, actualToneHz);
                    ExerciseTransformCeiling(block, frame);
                    ExerciseDetectors(frame, actualToneHz);
                    ExerciseTraceFormats(frame, actualToneHz);
                    ExerciseNoiseCorrection(block, frame, actualToneHz);
                    ExerciseChannelMeasurements(frame, actualToneHz);
                    ExerciseCrossChannelAvailability();
                    ExerciseMarkerCollection(block, frame, actualToneHz);
                    ExerciseZeroSpan(frame, actualToneHz);
                    ExerciseContexts(block, actualToneHz);
                    ExerciseCompositionOrder(block);
                    ExerciseOverlap(block, actualToneHz);
                    ExerciseGating(block, frame);
                    ExerciseFormats(frame);
                    ExerciseTraceMath(frame);
                    ExerciseRegisters(frame);
                    ExerciseBandMeasurements(frame, actualToneHz);
                    ExerciseMarkers(frame, actualToneHz);
                    ExercisePersonality(block, frame, actualToneHz);
                    ExerciseLimits(frame);
                    ExerciseFrontEndInterchange();
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
            ExercisePresets(centerFrequencyHz, _measured);

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
        /// Renders one real acquisition in several formats (<c>REQ-DSP-041</c>,
        /// <c>REQ-TRC-001</c>).
        /// </summary>
        /// <remarks>
        /// <para>
        /// <c>REQ-DSP-041</c>'s own criterion, on the bench: "Log and Linear Magnitude of the same
        /// data agree to within 0.01 dB after conversion, and Real/Imaginary recombine to the
        /// Magnitude value, so the formats are views of one computation rather than parallel
        /// paths."
        /// </para>
        /// <para>
        /// Compared as a <em>ratio</em> between two bins rather than against an absolute level, so
        /// the check needs no knowledge of the volts-to-dBm offset and cannot be satisfied by an
        /// offset that happens to cancel. The carrier and a floor bin are tens of decibels apart,
        /// which makes the comparison a real one.
        /// </para>
        /// </remarks>
        private void ExerciseTraceFormats(SpectrumFrame full, double toneHz)
        {
            int peak = full.IndexOfPeak();

            if (peak < 0 || full.PointCount < 8)
            {
                return;
            }

            int quiet = peak > full.PointCount / 2 ? 2 : full.PointCount - 3;

            var linear = new float[full.PointCount];
            var real = new float[full.PointCount];
            var imaginary = new float[full.PointCount];

            full.Format(TraceFormat.LinearMagnitude, new Span<float>(linear));
            full.Format(TraceFormat.Real, new Span<float>(real));
            full.Format(TraceFormat.Imaginary, new Span<float>(imaginary));

            Step("REQ-DSP-041", "Log and linear magnitude agree after conversion", () =>
            {
                double decibelRatio = full.LevelsDbm[peak] - full.LevelsDbm[quiet];
                double voltsRatio = 20.0 * Math.Log10(linear[peak] / linear[quiet]);
                double error = decibelRatio - voltsRatio;

                bool ok = linear[quiet] > 0.0 && Math.Abs(error) < 0.01;

                return new Outcome<double>(
                    ok, error,
                    "carrier over a floor bin: " + decibelRatio.ToString("0.0000",
                        System.Globalization.CultureInfo.InvariantCulture) +
                    " dB logarithmic against " + voltsRatio.ToString("0.0000",
                        System.Globalization.CultureInfo.InvariantCulture) +
                    " dB from the volts (" + linear[peak].ToString("0.000000E+00",
                        System.Globalization.CultureInfo.InvariantCulture) + " V and " +
                    linear[quiet].ToString("0.000000E+00",
                        System.Globalization.CultureInfo.InvariantCulture) + " V)");
            });

            Step("REQ-DSP-041", "Real and imaginary recombine to the magnitude", () =>
            {
                double worst = 0.0;

                for (int i = 0; i < full.PointCount; i++)
                {
                    double recombined = Math.Sqrt(
                        (double)real[i] * real[i] + (double)imaginary[i] * imaginary[i]);

                    double magnitude = linear[i];

                    if (magnitude > 0.0)
                    {
                        worst = Math.Max(worst, Math.Abs(recombined - magnitude) / magnitude);
                    }
                }

                return new Outcome<double>(
                    worst < 1e-5, worst,
                    full.PointCount + " points recombined within " + worst.ToString("0.0E+00",
                        System.Globalization.CultureInfo.InvariantCulture) +
                    " of their magnitude — the formats are views of one computation");
            });

            Step("REQ-TRC-001", "Each format decimates to its own envelope, not to one shared", () =>
            {
                // The defect this step exists for: the display built one envelope from the log
                // magnitude and drew it in every window, so four formats were one picture under
                // four labels. Compared here on the values the display would decimate, which is
                // the level at which the two were identical.
                // Decimated here with the same partition the display uses, through the same
                // reduction, so the comparison is of what would actually be drawn.
                int columns = 64;
                int count = full.PointCount;
                double separation = 0.0;

                for (int column = 0; column < columns; column++)
                {
                    int start = (int)((long)column * count / columns);
                    int end = (int)(((long)column + 1) * count / columns);

                    float logLow;
                    float logHigh;
                    float voltsLow;
                    float voltsHigh;

                    TraceDetection.Detect(
                        full.LevelsDbm, start, end, TraceDetector.Normal, true,
                        out logLow, out logHigh);

                    TraceDetection.Detect(
                        linear, start, end, TraceDetector.Normal, false,
                        out voltsLow, out voltsHigh);

                    separation = Math.Max(separation, Math.Abs(logHigh - voltsHigh));
                    separation = Math.Max(separation, Math.Abs(logLow - voltsLow));
                }

                return new Outcome<double>(
                    separation > 1.0, separation,
                    columns + " columns: the two envelopes differ by up to " +
                    separation.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) +
                    " in their own units, so a window set to linear magnitude cannot be drawing " +
                    "the logarithmic one");
            });
        }

        /// <summary>
        /// Reduces a real trace by each display detector (<c>REQ-UI-072</c>).
        /// </summary>
        /// <remarks>
        /// <para>
        /// On the instrument's own trace, which is the point: a unit test invents a column and the
        /// detectors do the obvious thing to it. Here the column spans a real carrier standing tens
        /// of decibels above a real noise floor, which is the case where averaging in decibels
        /// instead of in power is both wrong and plausible-looking.
        /// </para>
        /// <para>
        /// The discriminating figure is the gap between the two averages. Over a column containing
        /// a carrier and its skirts the power mean sits within a decibel or two of the carrier,
        /// while the mean of the decibels sits far below it — so an implementation that averaged
        /// decibels fails here by tens of dB, not by a rounding error.
        /// </para>
        /// </remarks>
        private void ExerciseDetectors(SpectrumFrame full, double toneHz)
        {
            int peakIndex = full.IndexOfPeak();

            if (peakIndex < 0)
            {
                return;
            }

            // A column of the width the display would actually give it: a few hundred points over
            // a graticule several hundred pixels wide puts a handful of bins in each column, and
            // the carrier's skirts are what make the detectors differ.
            int half = Math.Max(2, full.PointCount / 64);
            int start = Math.Max(0, peakIndex - half);
            int end = Math.Min(full.PointCount, peakIndex + half + 1);

            Step("REQ-UI-072", "Every detector reduces the real column as it says it does", () =>
            {
                float normalLow;
                float normalHigh;
                float peakLow;
                float peakHigh;
                float negativeLow;
                float negativeHigh;
                float sampleLow;
                float sampleHigh;

                TraceDetection.Detect(
                    full.LevelsDbm, start, end, TraceDetector.Normal, true,
                    out normalLow, out normalHigh);

                TraceDetection.Detect(
                    full.LevelsDbm, start, end, TraceDetector.Peak, true, out peakLow, out peakHigh);

                TraceDetection.Detect(
                    full.LevelsDbm, start, end, TraceDetector.NegativePeak, true,
                    out negativeLow, out negativeHigh);

                TraceDetection.Detect(
                    full.LevelsDbm, start, end, TraceDetector.Sample, true,
                    out sampleLow, out sampleHigh);

                bool ok = Math.Abs(peakHigh - normalHigh) < 1e-4 &&
                          Math.Abs(peakLow - peakHigh) < 1e-4 &&
                          Math.Abs(negativeLow - normalLow) < 1e-4 &&
                          Math.Abs(negativeHigh - negativeLow) < 1e-4 &&
                          Math.Abs(sampleLow - full.LevelsDbm[start]) < 1e-4 &&
                          normalHigh > normalLow;

                return new Outcome<double>(
                    ok,
                    normalHigh - normalLow,
                    (end - start) + " bins about the carrier at " + Hz(toneHz) + ": Normal spans " +
                    Db(normalLow) + " to " + Db(normalHigh) + ", Peak reads " + Db(peakHigh) +
                    ", Negative peak " + Db(negativeLow) + ", Sample " + Db(sampleLow));
            });

            Step("REQ-UI-072", "The average detector averages power, not decibels", () =>
            {
                float averageLow;
                float averageHigh;

                TraceDetection.Detect(
                    full.LevelsDbm, start, end, TraceDetector.Average, true,
                    out averageLow, out averageHigh);

                // The wrong answer, computed here so the two can be compared on the same data.
                double decibelMean = 0.0;
                int counted = 0;

                for (int i = start; i < end; i++)
                {
                    if (!float.IsNaN(full.LevelsDbm[i]))
                    {
                        decibelMean += full.LevelsDbm[i];
                        counted++;
                    }
                }

                decibelMean /= Math.Max(1, counted);

                float normalLow;
                float normalHigh;

                TraceDetection.Detect(
                    full.LevelsDbm, start, end, TraceDetector.Normal, true,
                    out normalLow, out normalHigh);

                double error = averageLow - decibelMean;

                // The power mean lies between the extrema and above the mean of the decibels, and
                // on a real carrier it is far above it. One decibel of separation would be within
                // the noise; ten is a different arithmetic.
                bool ok = Math.Abs(averageLow - averageHigh) < 1e-4 &&
                          averageLow <= normalHigh + 1e-3 &&
                          averageLow >= normalLow - 1e-3 &&
                          error > 10.0;

                return new Outcome<double>(
                    ok,
                    error,
                    "power mean " + Db(averageLow) + " against a mean of the decibels of " +
                    Db((float)decibelMean) + " — " + error.ToString("0.0",
                        System.Globalization.CultureInfo.InvariantCulture) +
                    " dB apart, between the extrema " + Db(normalLow) + " and " + Db(normalHigh));
            });

            Step("REQ-UI-072", "The detector reduces the trace and never the acquisition", () =>
            {
                // Every point is still there and still what a marker would read: the detector is a
                // display decision, and a step that could not tell the difference would pass on an
                // implementation that had thrown the other points away.
                int points = full.PointCount;
                double peakLevel = full.LevelsDbm[peakIndex];

                float low;
                float high;

                TraceDetection.Detect(
                    full.LevelsDbm, start, end, TraceDetector.Average, true, out low, out high);

                bool ok = full.PointCount == points &&
                          Math.Abs(full.LevelsDbm[peakIndex] - peakLevel) < 1e-9 &&
                          Math.Abs(full.IndexOfPeak() - peakIndex) == 0;

                return new Outcome<double>(
                    ok,
                    peakLevel,
                    points + " points untouched by the reduction; the peak still reads " +
                    Db((float)peakLevel) + " at bin " + peakIndex);
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

        /// <summary>
        /// Adjacent-channel power and an emission mask over the real carrier
        /// (<c>REQ-CHM-001</c>, <c>REQ-CHM-003</c>).
        /// </summary>
        /// <remarks>
        /// The unit tests measure these against flat synthetic densities, where every answer is a
        /// closed form. What the bench adds is a carrier with real skirts, a real noise floor and a
        /// real window — so an ACP reading that agreed with a band-power marker only because both
        /// were reading the same idealised rectangle would come apart here.
        /// </remarks>
        private void ExerciseChannelMeasurements(SpectrumFrame frame, double toneHz)
        {
            // Sized from the trace, not chosen. Every channel and every mask segment has to fall
            // inside the span that was actually measured: a channel placed past the end of the
            // trace integrates no bins at all and reports the amplitude floor, which reads as a
            // superb adjacent-channel ratio and is a measurement of nothing. The steps below
            // assert a bin count for the same reason.
            double headroomHz = Math.Min(
                toneHz - frame.FrequencyAt(0),
                frame.FrequencyAt(frame.PointCount - 1) - toneHz);

            double channelHz = headroomHz / 3.0;
            double offsetHz = 2.0 * channelHz;

            AcpResult acp = Step("REQ-CHM-001", "Adjacent channel power, per offset and per side", () =>
            {
                var measurement = new AcpMeasurement(
                        ChannelDefinition.Rectangular("Carrier", 0.0, channelHz))
                    .Add(ChannelDefinition.Rectangular("Adjacent", offsetHz, channelHz));

                AcpResult measured = measurement.Measure(frame, toneHz);

                ChannelPower lower = measured.Find("Adjacent", ChannelSide.Lower);
                ChannelPower upper = measured.Find("Adjacent", ChannelSide.Upper);

                if (lower == null || upper == null)
                {
                    return Failed<AcpResult>("an offset channel was not reported on both sides");
                }

                // A real carrier's adjacent channels sit on the analyser's noise floor, so both
                // ratios must be well down and neither may come out positive. And every channel
                // must have integrated something: a bin count of zero is how "off the end of the
                // trace" disguises itself as a very good result.
                bool ok = measured.Carrier.Power.BinCount > 0 &&
                          lower.Power.BinCount > 0 &&
                          upper.Power.BinCount > 0 &&
                          lower.RelativeDb < -20.0 &&
                          upper.RelativeDb < -20.0 &&
                          Math.Abs(lower.CentreHz - (toneHz - offsetHz)) < 1.0 &&
                          Math.Abs(upper.CentreHz - (toneHz + offsetHz)) < 1.0 &&
                          Math.Abs(measured.Carrier.RelativeDb) < 1e-9;

                return new Outcome<AcpResult>(
                    ok, measured,
                    "carrier " + Db(measured.Carrier.AbsoluteDbm) + " over " + Hz(channelHz) +
                    " (" + measured.Carrier.Power.BinCount + " bins); at ±" + Hz(offsetHz) +
                    " lower " + lower.RelativeDb.ToString("0.0", CultureInfo.CurrentCulture) +
                    " dBc, upper " + upper.RelativeDb.ToString("0.0", CultureInfo.CurrentCulture) +
                    " dBc");
            });

            if (acp == null)
            {
                return;
            }

            Step("REQ-CHM-001", "Absolute channel power agrees with the band power", () =>
            {
                // The criterion, to 0.1 dB. It holds because both run through the same integration
                // in the DSP layer; a second loop here would drift at the band edges, where which
                // bins are inside is a judgement call.
                BandPower band = BandMeasurements.Power(
                    frame, toneHz - channelHz / 2.0, toneHz + channelHz / 2.0);

                double difference = acp.Carrier.AbsoluteDbm - band.TotalDbm;
                bool ok = Math.Abs(difference) <= 0.1;

                return new Outcome<double>(
                    ok, difference,
                    Db(acp.Carrier.AbsoluteDbm) + " against a band power of " + Db(band.TotalDbm) +
                    " over the same " + Hz(band.BandwidthHz) + ", " +
                    difference.ToString("0.000", CultureInfo.CurrentCulture) + " dB apart");
            });

            Step("REQ-CHM-001", "A root-raised-cosine channel reads below a rectangular one", () =>
            {
                // What is asserted here is the invariant, not the flat-noise prediction. |H(f)|² is
                // at most 1 everywhere, so a shaped channel can never read above a rectangular one
                // over the same span whatever the input - that holds on any signal and is worth
                // pinning on a real one.
                //
                // The exact figure, 10·log10(1+α) below, needs a flat input, and the E4406A's own
                // floor runs 26 dB across the span. Reported for comparison rather than asserted,
                // because asserting it here would be asserting that the instrument's noise floor is
                // flat, which it is not and was never claimed to be. The unit suite tests the
                // prediction against a floor that is.
                const double rollOff = 0.22;

                double symbolRate = channelHz / (1.0 + rollOff);
                double predicted = -10.0 * Math.Log10(1.0 + rollOff);

                // Away from the carrier, so this weighs the shape against noise rather than against
                // a tone's main lobe - and inside the trace, which is the point of sizing from it.
                double noiseCentre = toneHz + offsetHz;

                BandPower shaped = new AcpMeasurement(
                        ChannelDefinition.RootRaisedCosine("N", 0.0, symbolRate, rollOff))
                    .Measure(frame, noiseCentre).Carrier.Power;

                BandPower flat = new AcpMeasurement(
                        ChannelDefinition.Rectangular("N", 0.0, channelHz))
                    .Measure(frame, noiseCentre).Carrier.Power;

                double measured = shaped.TotalDbm - flat.TotalDbm;

                bool ok = shaped.BinCount > 0 &&
                          flat.BinCount > 0 &&
                          measured < 0.0 &&
                          measured > -6.0;

                return new Outcome<double>(
                    ok, measured,
                    "RRC α=" + rollOff.ToString("0.##", CultureInfo.CurrentCulture) + " at " +
                    Hz(symbolRate) + " reads " +
                    measured.ToString("0.000", CultureInfo.CurrentCulture) +
                    " dB below a " + Hz(channelHz) + " rectangle over " + flat.BinCount +
                    " bins; a flat input would predict " +
                    predicted.ToString("0.000", CultureInfo.CurrentCulture) + " dB");
            });

            Step("REQ-CHM-003", "An emission mask names the segment the carrier breaches", () =>
            {
                // Segments in bins, so the mask is the same shape whatever span the instrument was
                // set to. The near one covers the carrier's own main lobe at -40 dBc, which it
                // cannot pass; the far one sits out on the noise floor at -20 dBc, which it passes
                // by tens of dB. Deliberately not a realistic mask - a clean CW carrier through a
                // flat-top window has skirts far below any real mask, so there would be nothing to
                // fail against. What is being exercised is that each segment is judged against its
                // own limit and the failure names the right one.
                double bin = frame.BinWidthHz;

                var mask = new EmissionMask("Exercise mask")
                    .Add(new EmissionMaskSegment("Carrier lobe", 0.0, 2.0 * bin, -40.0))
                    .Add(new EmissionMaskSegment("Floor", 6.0 * bin, 50.0 * bin, -20.0));

                EmissionMaskResult result = mask.Evaluate(
                    frame, ChannelDefinition.Rectangular("Carrier", 0.0, channelHz), toneHz);

                LimitLineResult floor = null;

                foreach (LimitLineResult line in result.LimitResult.Lines)
                {
                    if (line.Line.Name == "Floor upper")
                    {
                        floor = line;
                    }
                }

                bool ok = !result.Passed &&
                          result.OffendingSegment != null &&
                          result.OffendingSegment.StartsWith(
                              "Carrier lobe", StringComparison.Ordinal) &&
                          result.LimitResult.Lines.Count == 4 &&
                          floor != null && floor.Passed && floor.TestedPoints > 0;

                return new Outcome<string>(
                    ok, result.OffendingSegment,
                    result.ToString() + "; 'Floor upper' passed with " +
                    (floor == null
                        ? "no result"
                        : floor.WorstMarginDb.ToString("0.0", CultureInfo.CurrentCulture) +
                          " dB over " + floor.TestedPoints + " points") +
                    ", referenced to a carrier of " + Db(result.CarrierPowerDbm));
            });

            Step("REQ-CHM-003", "The mask runs through the limit engine, not around it", () =>
            {
                // REQ-CHM-003 asks for the shared code path to be asserted, because a second
                // implementation of "above or below" is where an upper/lower inversion comes back.
                double bin = frame.BinWidthHz;

                var mask = new EmissionMask("Exercise mask")
                    .Add(new EmissionMaskSegment("Carrier lobe", 0.0, 2.0 * bin, -40.0))
                    .Add(new EmissionMaskSegment("Floor", 6.0 * bin, 50.0 * bin, -20.0));

                EmissionMaskResult viaMask = mask.Evaluate(frame, toneHz, acp.Carrier.AbsoluteDbm);
                LimitTestResult viaEngine =
                    mask.ToLimitTest(toneHz, acp.Carrier.AbsoluteDbm).Evaluate(frame);

                bool ok = viaEngine.Passed == viaMask.LimitResult.Passed &&
                          viaEngine.Lines.Count == viaMask.LimitResult.Lines.Count;

                for (int i = 0; ok && i < viaEngine.Lines.Count; i++)
                {
                    double a = viaEngine.Lines[i].WorstMarginDb;
                    double b = viaMask.LimitResult.Lines[i].WorstMarginDb;

                    // Both NaN means both lines tested nothing, which is agreement rather than
                    // disagreement - and NaN compares unequal to itself, so it has to be said.
                    bool marginsAgree = double.IsNaN(a) && double.IsNaN(b)
                        ? true
                        : Math.Abs(a - b) < 1e-9;

                    ok = viaEngine.Lines[i].Line.Name == viaMask.LimitResult.Lines[i].Line.Name &&
                         viaEngine.Lines[i].TestedPoints ==
                             viaMask.LimitResult.Lines[i].TestedPoints &&
                         marginsAgree;
                }

                return new Outcome<int>(
                    ok, viaEngine.Lines.Count,
                    viaEngine.Lines.Count + " limit lines, identical names, points and margins " +
                    "either way in: the mask builds a LimitTest and hands it the trace");
            });
        }

        /// <summary>
        /// The cross-channel types are offered only where the front end can supply them
        /// (<c>REQ-DSP-040a</c>).
        /// </summary>
        private void ExerciseCrossChannelAvailability()
        {
            Step("REQ-DSP-040a", "Cross-channel types are absent, not broken, on one channel", () =>
            {
                IFrontEndCapabilities declared = _frontEnd.Capabilities;

                IReadOnlyList<CrossChannelDataType> offered =
                    CrossChannelDataTypes.AvailableFor(declared);

                bool coherent = CrossChannelDataTypes.IsSupportedBy(declared);
                string why = CrossChannelDataTypes.ExplainUnavailability(declared);

                // Whatever this instrument declares, the list and the explanation have to agree
                // with it - and with each other.
                bool ok = coherent
                    ? offered.Count == CrossChannelDataTypes.All.Count && why.Length == 0
                    : offered.Count == 0 && why.Length > 0;

                return new Outcome<int>(
                    ok, offered.Count,
                    declared.ChannelCount + " channel(s), phase coherent: " +
                    declared.SupportsPhaseCoherentChannels + "; " + offered.Count + " of " +
                    CrossChannelDataTypes.All.Count + " cross-channel types offered" +
                    (why.Length == 0 ? string.Empty : " — " + why));
            });
        }

        /// <summary>
        /// Marker coupling, tracking and readouts across two real traces
        /// (<c>REQ-MKR-002</c>, <c>REQ-MKR-004</c>, <c>REQ-MKR-005</c>, <c>REQ-MKR-006</c>).
        /// </summary>
        /// <remarks>
        /// The two traces are the same acquisition analysed at different transform lengths, which
        /// gives them the same span and different point counts — exactly the pair
        /// <c>REQ-MKR-004</c>'s criterion asks for, and one that a coupling implemented on sample
        /// index would pass on synthetic traces of matched length and fail here.
        /// </remarks>
        private void ExerciseMarkerCollection(IqBlock block, SpectrumFrame fine, double toneHz)
        {
            var markers = new MarkerCollection { Coupled = true };

            SpectrumFrame coarse = Step("REQ-MKR-004", "Two traces of the same span, different point counts", () =>
            {
                var computer = new SpectrumComputer(WindowType.FlatTop, null, null)
                {
                    TrimToAnalysisSpan = true,

                    // Half the transform the record would naturally take, so the second trace
                    // covers the same span with half the points.
                    MaxTransformLength = Math.Max(
                        2, SpectrumComputer.TransformLengthFor(block.SampleCount) / 2),
                };

                SpectrumFrame second = computer.Compute(block);

                bool ok = second.PointCount != fine.PointCount &&
                          Math.Abs(second.BinWidthHz - 2.0 * fine.BinWidthHz) <
                              0.01 * fine.BinWidthHz;

                return new Outcome<SpectrumFrame>(
                    ok, second,
                    "trace A: " + fine.PointCount + " points at " + Hz(fine.BinWidthHz) +
                    "; trace B: " + second.PointCount + " points at " + Hz(second.BinWidthHz));
            });

            if (coarse == null)
            {
                return;
            }

            markers.Update('A', fine);
            markers.Update('B', coarse);

            Step("REQ-MKR-004", "Coupled markers move to the same frequency, not the same bin", () =>
            {
                MarkerSet a = markers.ForTrace('A');
                MarkerSet b = markers.ForTrace('B');

                Marker a1 = a.AddNormal(fine.CenterFrequencyHz);
                Marker b1 = b.AddNormal(coarse.CenterFrequencyHz);

                IReadOnlyList<Marker> moved = markers.MoveTo(a1, toneHz);

                int aIndex = a1.IndexIn(fine);
                int bIndex = b1.IndexIn(coarse);

                // The same frequency in both, and - because the point counts differ - not the same
                // index. Coupling by index is the implementation this catches.
                bool ok = moved.Count == 2 &&
                          Math.Abs(a1.XHz - b1.XHz) < 1e-6 &&
                          aIndex != bIndex &&
                          Math.Abs(fine.FrequencyAt(aIndex) - coarse.FrequencyAt(bIndex)) <
                              coarse.BinWidthHz;

                return new Outcome<int>(
                    ok, moved.Count,
                    "marker 1 dragged to " + Hz(toneHz) + " on A moved " + moved.Count +
                    " markers; bin " + aIndex + " on A and bin " + bIndex +
                    " on B, both reading " + Hz(fine.FrequencyAt(aIndex)));
            });

            Step("REQ-MKR-005", "A tracking marker settles on the carrier from beside it", () =>
            {
                MarkerSet a = markers.ForTrace('A');

                // Placed two bins off the carrier, with tracking on. Re-reading the same
                // acquisition is enough to show the search runs: a tracking marker moves onto the
                // nearest peak, and beside a real carrier that is the carrier.
                Marker tracker = a.AddNormal(toneHz + 2.0 * fine.BinWidthHz);
                tracker.TracksPeak = true;

                double before = tracker.XHz;

                markers.Update('A', fine);

                double error = tracker.XHz - toneHz;
                bool ok = Math.Abs(error) <= fine.BinWidthHz && tracker.XHz != before;

                return new Outcome<double>(
                    ok, error,
                    "placed " + Hz(before - toneHz) + " off the carrier, tracked to " +
                    Signed(error) + " Hz of it");
            });

            Step("REQ-MKR-005", "Marker to centre frequency and copy value write what was read", () =>
            {
                var target = new RecordingTarget();
                MarkerSet a = markers.ForTrace('A');

                a.Select(a.Markers[0]);
                a.PeakSearch(fine);

                Marker peak = a.Selected;
                MarkerReading reading = peak.Read(fine);

                double centre = MarkerFunctions.ToCenterFrequency(peak, fine, target);
                double level = MarkerFunctions.ToReferenceLevel(peak, fine, target);
                double copied = MarkerFunctions.CopyValueToParameter(
                    peak, fine, "TriggerLevel", target);

                bool ok = Math.Abs(centre - reading.XHz) < 1e-6 &&
                          Math.Abs(level - reading.YDbm) < 1e-9 &&
                          Math.Abs(copied - reading.YDbm) < 1e-9 &&
                          Math.Abs(target.CenterHz - centre) < 1e-6 &&
                          Math.Abs(target.ReferenceDbm - level) < 1e-9 &&
                          Math.Abs(target.TriggerLevelDbm - copied) < 1e-9;

                return new Outcome<double>(
                    ok, centre,
                    "peak at " + Hz(reading.XHz) + ", " + Db(reading.YDbm) +
                    ": centre set to " + Hz(target.CenterHz) + ", reference to " +
                    Db(target.ReferenceDbm) + ", TriggerLevel to " + Db(target.TriggerLevelDbm));
            });

            Step("REQ-MKR-006", "The two readout surfaces cannot disagree", () =>
            {
                markers.ActiveTrace = 'A';

                MarkerReadout above = markers.ActiveReadout;

                if (above == null)
                {
                    return Failed<int>("no marker was active");
                }

                MarkerReadout row = null;
                int rows = 0;
                int onOtherTraces = 0;

                foreach (MarkerReadout readout in markers.Readouts())
                {
                    rows++;

                    if (readout.TraceLetter != 'A')
                    {
                        onOtherTraces++;
                    }

                    if (ReferenceEquals(readout.Marker, above.Marker))
                    {
                        row = readout;
                    }
                }

                // The window lists every marker on every trace, and the row for the active marker
                // reads identically to the above-grid readout - because there is one readout and
                // both surfaces render it.
                bool ok = row != null &&
                          row.Text == above.Text &&
                          onOtherTraces > 0;

                return new Outcome<int>(
                    ok, rows,
                    rows + " rows across " + markers.TraceCount + " traces, " + onOtherTraces +
                    " of them not on the active trace; the active row reads '" +
                    (row == null ? "(missing)" : row.Text) + "' either way in");
            });
        }

        /// <summary>
        /// Runs the real acquisition through the declared pipeline (<c>REQ-TRC-003</c>).
        /// </summary>
        /// <summary>
        /// Zero-span operation over the real trace (<c>REQ-DSP-012</c>).
        /// </summary>
        /// <param name="frame">The spectrum of the block the instrument produced.</param>
        /// <param name="toneHz">Where the generator says its carrier is.</param>
        /// <remarks>
        /// The control swap is a shell matter and is asserted there. What can only be shown against
        /// a real acquisition is that the channel filter is <em>applied</em>: that the reading follows
        /// the shape and the bandwidth rather than being a setting nothing consults.
        /// </remarks>
        private void ExerciseZeroSpan(SpectrumFrame frame, double toneHz)
        {
            Step("REQ-DSP-012", "A zero-span reading is taken through the channel filter", () =>
            {
                int peak = frame.IndexOfPeak();

                if (peak < 0)
                {
                    return Failed<string>("the measured frame has no peak");
                }

                double peakDbm = frame.LevelsDbm[peak];

                // A zero-span channel sits at the TUNE frequency, and this harness deliberately
                // places the carrier off centre -- which is what makes the pair below a real test
                // rather than two readings of the same thing.
                double offsetHz = Math.Abs(toneHz - frame.CenterFrequencyHz);
                double narrowHz = Math.Max(10.0 * frame.BinWidthHz, 100e3);

                if (!(offsetHz > 2.0 * narrowHz))
                {
                    return Failed<string>(
                        "the carrier is inside the narrow channel, so rejection cannot be shown");
                }

                // Wide enough to contain the off-centre carrier, and narrow enough to exclude it.
                double wideHz = 4.0 * offsetHz;

                BandPower narrow = ZeroSpanMeasurement.Power(
                    frame, ChannelFilterType.Gaussian, narrowHz);
                BandPower wide = ZeroSpanMeasurement.Power(
                    frame, ChannelFilterType.Gaussian, wideHz);
                BandPower unshaped = ZeroSpanMeasurement.Power(
                    frame, ChannelFilterType.None, narrowHz);

                // The carrier is 3 MHz outside a 300 kHz channel, so the narrow Gaussian must reject
                // it while the unshaped reading -- which takes the whole analysed span -- still sees
                // it. A reading that ignored the filter would give the same number twice.
                bool rejectsOutside = unshaped.TotalDbm - narrow.TotalDbm > 20.0;

                // Widened to contain the carrier, the same filter passes it: a filter that rejected
                // everything would satisfy the clause above on its own.
                bool passesInside = wide.TotalDbm > peakDbm - 6.0;

                // And a wider filter can only pass more power than a narrower one at the same centre.
                bool followsBandwidth = wide.TotalDbm >= narrow.TotalDbm - 0.01;

                double noiseBandwidthHz = ZeroSpanMeasurement.NoiseBandwidthHz(
                    frame, ChannelFilterType.Gaussian, narrowHz);

                bool noiseBandwidth = Math.Abs(
                    noiseBandwidthHz / narrowHz -
                    ChannelFilters.GaussianNoiseBandwidthFactor) < 1e-9;

                return new Outcome<string>(
                    rejectsOutside && passesInside && followsBandwidth && noiseBandwidth,
                    "zero span at " + Hz(frame.CenterFrequencyHz),
                    "carrier " + Hz(offsetHz) + " off the tuned centre, peak " +
                    peakDbm.ToString("0.00", CultureInfo.CurrentCulture) + " dBm; a " +
                    Hz(narrowHz) + " Gaussian rejects it to " +
                    narrow.TotalDbm.ToString("0.00", CultureInfo.CurrentCulture) +
                    " dBm against unshaped " +
                    unshaped.TotalDbm.ToString("0.00", CultureInfo.CurrentCulture) +
                    " dBm, and a " + Hz(wideHz) + " one passes it at " +
                    wide.TotalDbm.ToString("0.00", CultureInfo.CurrentCulture) +
                    " dBm; noise bandwidth " + Hz(noiseBandwidthHz));
            });
        }

        /// <summary>
        /// Two measurement contexts over one acquired block (<c>REQ-DAT-010</c>).
        /// </summary>
        /// <param name="block">The block the instrument produced.</param>
        /// <param name="toneHz">Where the generator says its carrier is.</param>
        /// <remarks>
        /// The requirement's criterion is two contexts running concurrently against one capture
        /// session, each with its own trace windows and markers. The unit suite proves that against a
        /// fake front end; this proves it against a block the instrument really produced, which is the
        /// only place the claim "one capture session" can be checked against real samples.
        /// </remarks>
        private void ExerciseContexts(IqBlock block, double toneHz)
        {
            var contexts = new MeasurementContextSet("Bench spectrum");
            MeasurementContext wide = contexts.Active;
            MeasurementContext narrow = contexts.Add("Bench narrow");

            wide.Setup.Analysis.Window = WindowType.Uniform;
            narrow.Setup.Analysis.Window = WindowType.FlatTop;

            wide.AddTrace('A');
            narrow.AddTrace('B');

            using (var analyser = new ContextAnalyser(contexts))
            {
                Step("REQ-DAT-010", "Two contexts analyse one real block, each its own way", () =>
                {
                    analyser.Distribute(block);

                    SpectrumFrame fromWide = wide.TakeLatestFrame();
                    SpectrumFrame fromNarrow = narrow.TakeLatestFrame();

                    try
                    {
                        if (fromWide == null || fromNarrow == null)
                        {
                            return Failed<string>("a context produced no frame");
                        }

                        int widePeak = fromWide.IndexOfPeak();
                        int narrowPeak = fromNarrow.IndexOfPeak();

                        if (widePeak < 0 || narrowPeak < 0)
                        {
                            return Failed<string>("a context's frame has no peak");
                        }

                        // Both found the same carrier, because it is one acquisition; the skirt two
                        // bins out differs, because a flat-top window spreads a tone further than a
                        // uniform one. If the two contexts shared a transform, they would agree.
                        double wideSkirt = fromWide.LevelsDbm[Math.Min(
                            widePeak + 2, fromWide.PointCount - 1)];
                        double narrowSkirt = fromNarrow.LevelsDbm[Math.Min(
                            narrowPeak + 2, fromNarrow.PointCount - 1)];

                        bool sameCarrier =
                            Math.Abs(fromWide.FrequencyAt(widePeak) - toneHz) <
                                2.0 * fromWide.BinWidthHz &&
                            Math.Abs(fromNarrow.FrequencyAt(narrowPeak) - toneHz) <
                                2.0 * fromNarrow.BinWidthHz;

                        bool differentAnalysis = narrowSkirt > wideSkirt + 3.0;

                        // And each context's markers are its own: one placed on the first is not on
                        // the second.
                        wide.Markers.ForTrace('A').AddNormal(fromWide.FrequencyAt(widePeak));

                        bool separateMarkers =
                            wide.Markers.ForTrace('A').Markers.Count == 1 &&
                            narrow.Markers.ForTrace('B').Markers.Count == 0;

                        return new Outcome<string>(
                            sameCarrier && differentAnalysis && separateMarkers,
                            "one block, two contexts",
                            "both found the carrier at " +
                            Hz(fromWide.FrequencyAt(widePeak)) + "; skirt 2 bins out is " +
                            wideSkirt.ToString("0.0", CultureInfo.CurrentCulture) + " dBm uniform " +
                            "against " +
                            narrowSkirt.ToString("0.0", CultureInfo.CurrentCulture) +
                            " dBm flat-top; markers " +
                            wide.Markers.ForTrace('A').Markers.Count + " and " +
                            narrow.Markers.ForTrace('B').Markers.Count);
                    }
                    finally
                    {
                        // REQ-NFR-002: the shares TakeLatestFrame took, and then the contexts' own.
                        if (fromWide != null)
                        {
                            fromWide.Release();
                        }

                        if (fromNarrow != null)
                        {
                            fromNarrow.Release();
                        }

                        wide.ClearFrame();
                        narrow.ClearFrame();
                    }
                });
            }

            Step("REQ-DAT-010", "Both contexts are saved and recalled by name", () =>
            {
                wide.Setup.CenterFrequencyHz = block.CenterFrequencyHz;
                narrow.Setup.CenterFrequencyHz = block.CenterFrequencyHz;
                narrow.Setup.Kind = MeasurementKind.VectorAnalysis;

                ApplicationState saved = contexts.Capture();

                // A fresh session with the same two names in the other order: matching is by name, so
                // the order they were made in must not decide which setup lands where.
                var reopened = new MeasurementContextSet("Bench narrow");
                reopened.Add("Bench spectrum");
                reopened.Recall(saved);

                bool ok =
                    saved.ContextNames().Count == 2 &&
                    reopened["Bench spectrum"].Setup.Analysis.Window == WindowType.Uniform &&
                    reopened["Bench narrow"].Setup.Analysis.Window == WindowType.FlatTop &&
                    reopened["Bench narrow"].Setup.Kind == MeasurementKind.VectorAnalysis;

                return new Outcome<string>(
                    ok,
                    string.Join(", ", saved.ContextNames()),
                    "saved " + saved.ContextNames().Count + " contexts and recalled both into a " +
                    "session that made them in the opposite order");
            });
        }

        private void ExerciseCompositionOrder(IqBlock block)
        {
            Step("REQ-TRC-003", "The pipeline runs the stages in the declared order", () =>
            {
                var pipeline = new AnalysisPipeline(
                    new SpectrumComputer(WindowType.FlatTop, null, null))
                {
                    Gate = new TimeGate(0.0, block.SampleCount / 2.0 / block.SampleRateHz),
                    Averager = new TraceAverager(AveragingType.RmsVideo, 4),
                    Accumulator = new AccumulatingTrace
                    {
                        Accumulator = TraceAccumulator.Spectrogram,
                    },
                    Format = TraceFormat.LogMagnitude,
                };

                pipeline.Run(block);

                IReadOnlyList<AnalysisStage> ran = pipeline.LastRunStages;
                bool ok = ran.Count == CompositionOrder.Stages.Count;

                for (int i = 0; ok && i < ran.Count; i++)
                {
                    ok = ran[i] == CompositionOrder.Stages[i];
                }

                return new Outcome<int>(
                    ok, ran.Count,
                    string.Join(" → ", ran) + ", against a declaration of " +
                    string.Join(" → ", CompositionOrder.Stages));
            });

            Step("REQ-TRC-003", "Gating before windowing is what couples RBW to the gate", () =>
            {
                // The order is pinned by measurement rather than by comment: the window is sized to
                // what survived the gate, so a quarter-length gate coarsens the RBW fourfold on the
                // instrument's own record.
                var gated = new AnalysisPipeline(
                    new SpectrumComputer(WindowType.FlatTop, null, null)
                    {
                        TrimToAnalysisSpan = true,
                    })
                {
                    Gate = new TimeGate(0.0, block.SampleCount / 4.0 / block.SampleRateHz),
                };

                var whole = new AnalysisPipeline(
                    new SpectrumComputer(WindowType.FlatTop, null, null)
                    {
                        TrimToAnalysisSpan = true,
                    });

                gated.Run(block);
                whole.Run(block);

                double ratio = gated.LastFrame.ResolutionBandwidthHz /
                               whole.LastFrame.ResolutionBandwidthHz;

                // The window really was sized to the gated record, not to the whole one.
                bool ok = ratio > 2.0 &&
                          gated.LastWindow.Length < whole.LastWindow.Length;

                return new Outcome<double>(
                    ok, ratio,
                    "a quarter-length gate takes the window from " + whole.LastWindow.Length +
                    " to " + gated.LastWindow.Length + " points and the RBW from " +
                    Hz(whole.LastFrame.ResolutionBandwidthHz) + " to " +
                    Hz(gated.LastFrame.ResolutionBandwidthHz) + ", a ratio of " +
                    ratio.ToString("0.00", CultureInfo.CurrentCulture));
            });

            Step("REQ-TRC-003", "Every combination is legal or refused with a reason", () =>
            {
                int total = 0;
                int refused = 0;
                string sample = null;

                foreach (KeyValuePair<CompositionSelection, CompositionVerdict> entry in
                    CompositionOrder.AllCombinations())
                {
                    total++;

                    if (entry.Value.IsLegal)
                    {
                        continue;
                    }

                    refused++;

                    if (string.IsNullOrEmpty(entry.Value.Reason))
                    {
                        return Failed<int>(entry.Key + " was refused without saying why");
                    }

                    if (sample == null)
                    {
                        sample = entry.Key.ToString();
                    }
                }

                bool ok = total > 0 && refused > 0 && refused < total;

                return new Outcome<int>(
                    ok, total,
                    total + " combinations, " + refused +
                    " refused with a named reason, first being " + sample);
            });
        }

        /// <summary>Records what a marker function wrote, so the exercise can check it arrived.</summary>
        private sealed class RecordingTarget : IMarkerParameterTarget
        {
            public double CenterHz { get; private set; } = double.NaN;

            public double ReferenceDbm { get; private set; } = double.NaN;

            public double TriggerLevelDbm { get; private set; } = double.NaN;

            public void SetCenterFrequency(double hz) => CenterHz = hz;

            public void SetReferenceLevel(double dbm) => ReferenceDbm = dbm;

            public void SetParameter(string parameter, double value)
            {
                if (!string.Equals(parameter, "TriggerLevel", StringComparison.Ordinal))
                {
                    throw new ArgumentException(
                        "This exercise offers only TriggerLevel.", nameof(parameter));
                }

                TriggerLevelDbm = value;
            }
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

        /// <summary>
        /// <c>REQ-ARC-003</c>: a personality measures the block that was acquired, and reaches the
        /// analysis path's answer by its own route.
        /// </summary>
        /// <param name="block">The real acquisition, as the personality receives it.</param>
        /// <param name="frame">The analysis path's spectrum of that same block.</param>
        /// <param name="toneHz">Where the generator was told to put the carrier.</param>
        /// <remarks>
        /// Run here rather than only in the unit suite because a simulated block is scaled by
        /// whatever produced it. What is being checked is that the samples handed to a plug-in by a
        /// real front end carry the calibration their metadata claims — and no mock can disagree
        /// about that, because a mock is where the claim came from.
        /// </remarks>
        private void ExercisePersonality(IqBlock block, SpectrumFrame frame, double toneHz)
        {
            var personality = new BenchPersonality();

            Step("REQ-ARC-003", "A personality measures the real block the analysis path measured", () =>
            {
                if (!personality.CanMeasure(block))
                {
                    return Failed<double>(
                        "the personality refused the acquired block: " + block.SampleCount +
                        " samples, full scale " +
                        block.FullScaleVolts.ToString("G4", CultureInfo.CurrentCulture) + " V");
                }

                IReadOnlyList<PersonalityReading> readings = personality.Measure(block);

                PersonalityReading power = readings.FirstOrDefault(r => r.Name == "Total power");
                PersonalityReading count = readings.FirstOrDefault(r => r.Name == "Samples");

                if (power == null || count == null)
                {
                    return Failed<double>("the personality did not return the readings it declares");
                }

                // The analysis path's own answer for the same block. A band wide enough to hold a
                // flat-top main lobe, for the same reason REQ-MKR-003's step gives.
                double half = 10.0 * frame.BinWidthHz;
                BandPower band = BandMeasurements.Power(frame, toneHz - half, toneHz + half);

                double error = power.Value - band.TotalDbm;

                // A decibel and a half, matching what REQ-MKR-003 allows between two readings of
                // the same carrier. The two routes differ in what they include - the time-domain
                // sum carries the whole span's noise, the band carries twenty bins of it - so they
                // are not required to agree exactly, only to agree as measurements of one signal.
                bool ok = Math.Abs(error) <= 1.5 &&
                          (int)count.Value == block.SampleCount &&
                          power.Unit == "dBm";

                return new Outcome<double>(
                    ok, power.Value,
                    "personality reads " + Db(power.Value) + " over " + block.SampleCount +
                    " samples against the analysis path's " + Db(band.TotalDbm) + " (" +
                    Signed(error) + " dB), and declares " + personality.Standard + " " +
                    personality.StandardRevision);
            });

            Step("REQ-ARC-003", "A personality refuses what it cannot measure, rather than reading zero", () =>
            {
                // The refusal a real acquisition can produce: a front end that declares no full
                // scale. Asked anyway, the measurement would come back at the amplitude floor - a
                // number that sits at the bottom of the graticule and reads as a very weak signal
                // rather than as no answer, which is the more dangerous of the two mistakes.
                // The instrument's own metadata with one field changed, so the refusal is
                // attributable to the full scale and to nothing else about the block.
                using (IqBlock unscaled = IqBlock.Rent(new IqBlockMetadata(
                    sampleCount: block.SampleCount,
                    sampleRateHz: block.SampleRateHz,
                    centerFrequencyHz: block.CenterFrequencyHz,
                    isBaseband: block.IsBaseband,
                    fullScaleVolts: 0.0,
                    referenceLevelDbm: block.ReferenceLevelDbm,
                    sequenceNumber: block.SequenceNumber,
                    acquiredUtc: block.AcquiredUtc,
                    triggerOffsetSeconds: block.TriggerOffsetSeconds,
                    triggerCorrectionsApplied: block.TriggerCorrectionsApplied,
                    source: block.Source,
                    extended: block.Extended)))
                {
                    bool refusedUnscaled = !personality.CanMeasure(unscaled);
                    bool acceptedReal = personality.CanMeasure(block);

                    // Both halves, or the step passes by refusing everything.
                    return new Outcome<bool>(
                        refusedUnscaled && acceptedReal,
                        refusedUnscaled,
                        "a block with no declared full scale is refused: " + refusedUnscaled +
                        "; the instrument's own block is accepted: " + acceptedReal);
                }
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

        /// <summary>
        /// <c>REQ-ARC-002</c>: a measurement setup survives a front-end change, and only what the
        /// new source cannot honour is coerced.
        /// </summary>
        /// <remarks>
        /// Run here rather than in the unit suite because the criterion is explicit that the
        /// E4406A leg is exercised against the instrument and not a mock — and it is right to be.
        /// Coercion is precisely where a real front end differs from a simulated one: the
        /// simulator honours whatever it is asked for, so a test built only on it would assert that
        /// nothing is ever coerced and would pass against a front end that coerced silently.
        /// </remarks>
        private void ExerciseFrontEndInterchange()
        {
            Step("REQ-ARC-002", "One setup, planned against this instrument, survives unchanged", () =>
            {
                // A request the simulator would honour outright and this instrument may not.
                var request = new AcquisitionRequest(
                    centerFrequencyHz: 1.0e9,
                    spanHz: 40.0e6,
                    samplesPerBlock: 65536,
                    referenceLevelDbm: 0.0);

                double centreBefore = request.CenterFrequencyHz;
                double spanBefore = request.SpanHz;
                int samplesBefore = request.SamplesPerBlock;
                double levelBefore = request.ReferenceLevelDbm;

                AcquisitionPlan plan = _frontEnd.Negotiate(request);

                // The request is the setup. It must come back unchanged: a front end that mutated
                // what it was handed would leave the previous instrument's setup unrecoverable
                // after a switch, which is exactly what this requirement forbids.
                bool untouched =
                    request.CenterFrequencyHz == centreBefore &&
                    request.SpanHz == spanBefore &&
                    request.SamplesPerBlock == samplesBefore &&
                    request.ReferenceLevelDbm == levelBefore;

                // Every difference between what was asked and what was planned must be named.
                var unreported = new List<string>();

                if (plan.SpanHz != spanBefore && !plan.Coercions.Any(c => c.Parameter == "Span"))
                {
                    unreported.Add("Span");
                }

                if (plan.SamplesPerBlock != samplesBefore &&
                    !plan.Coercions.Any(c => c.Parameter == "SamplesPerBlock" || c.Parameter == "BlockSize"))
                {
                    unreported.Add("SamplesPerBlock");
                }

                if (plan.ReferenceLevelDbm != levelBefore &&
                    !plan.Coercions.Any(c => c.Parameter == "ReferenceLevel"))
                {
                    unreported.Add("ReferenceLevel");
                }

                bool ok = untouched && unreported.Count == 0;

                string described = plan.Coercions.Count == 0
                    ? "nothing coerced"
                    : string.Join("; ", plan.Coercions.Select(
                        c => c.Parameter + " " + c.Requested.ToString("G4", CultureInfo.CurrentCulture) +
                             " -> " + c.Honoured.ToString("G4", CultureInfo.CurrentCulture)));

                return new Outcome<double>(
                    ok, plan.Coercions.Count,
                    "setup unchanged after negotiation; " + described +
                    (unreported.Count == 0
                        ? "; every difference reported"
                        : "; UNREPORTED: " + string.Join(", ", unreported)));
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
        /// Steps the generator across the span and checks the spectrogram's ridge
        /// (<c>REQ-DSP-043</c>).
        /// </summary>
        /// <param name="centerFrequencyHz">Analysis centre frequency.</param>
        /// <param name="spanHz">Analysis span.</param>
        /// <param name="levelDbm">Generator level.</param>
        /// <param name="ct">Cancellation token.</param>
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

            Step("REQ-UI-054", "Raising the threshold removes cells from a real spectrogram", () =>
            {
                // Against a history of real acquisitions, where the levels are a measured noise
                // floor with a carrier standing in it rather than a distribution chosen to make
                // the arithmetic work. The criterion is monotone: every step up removes cells and
                // none adds any.
                Spectrogram history = trace.Spectrogram;

                if (history.RowCount == 0)
                {
                    return Failed<long>("no history to threshold");
                }

                long everything = SpectrogramScaling.DrawableCellCount(
                    history, SpectrogramLevels.NoThresholdDbm);

                SpectrogramLevels window = SpectrogramScaling.Window(
                    history, SpectrogramLevels.NoThresholdDbm, false,
                    new SpectrogramLevels(-120.0, 0.0));

                long previous = everything;
                bool monotone = true;
                var counted = new List<string>();

                foreach (double below in new[] { 60.0, 40.0, 20.0, 10.0 })
                {
                    long drawn = SpectrogramScaling.DrawableCellCount(
                        history, window.HighDbm - below);

                    monotone &= drawn <= previous;
                    previous = drawn;

                    counted.Add(
                        "−" + below.ToString("0", CultureInfo.CurrentCulture) + " dB: " + drawn);
                }

                bool ok = monotone && everything > 0 && previous < everything;

                return new Outcome<long>(
                    ok, previous,
                    everything + " cells over " + history.RowCount + " rows; " +
                    string.Join(", ", counted) + "; window " + window);
            });

            Step("REQ-UI-054", "Enhance narrows the map onto the levels a real floor occupies", () =>
            {
                // The measured justification for the control. A real floor is 20-odd decibels of
                // shape with a carrier 90 dB above it, so a window taken from the extremes spends
                // most of the colour map on a range nothing occupies.
                Spectrogram history = trace.Spectrogram;

                if (history.RowCount == 0)
                {
                    return Failed<double>("no history to enhance");
                }

                var fallback = new SpectrogramLevels(-120.0, 0.0);

                SpectrogramLevels plain = SpectrogramScaling.Window(
                    history, SpectrogramLevels.NoThresholdDbm, false, fallback);

                SpectrogramLevels enhanced = SpectrogramScaling.Window(
                    history, SpectrogramLevels.NoThresholdDbm, true, fallback);

                bool ok = enhanced.RangeDb < plain.RangeDb &&
                          enhanced.LowDbm >= plain.LowDbm &&
                          enhanced.HighDbm <= plain.HighDbm;

                return new Outcome<double>(
                    ok, plain.RangeDb - enhanced.RangeDb,
                    "plain " + plain + " (" +
                    plain.RangeDb.ToString("0.0", CultureInfo.CurrentCulture) + " dB), enhanced " +
                    enhanced + " (" +
                    enhanced.RangeDb.ToString("0.0", CultureInfo.CurrentCulture) + " dB)");
            });

            Step("REQ-UI-054", "The two spectrogram markers move only along their own axes", () =>
            {
                // Over a real history, so the frequency the marker holds is resolved against an
                // axis an instrument produced rather than one this test made up.
                Spectrogram history = trace.Spectrogram;

                if (history.RowCount < 3)
                {
                    return Failed<double>("not enough history to mark");
                }

                var markers = new SpectrogramMarkers(history);

                markers.MoveTo(SpectrogramMarkerKind.Spectrogram, 0, 0);
                markers.MoveTo(SpectrogramMarkerKind.TraceSelect, 0, 0);

                int bins = history.Newest.PointCount;

                // Diagonal drags: each marker must take one coordinate and discard the other.
                markers.MoveTo(SpectrogramMarkerKind.Spectrogram, bins - 1, history.RowCount - 1);

                bool rowHeld = markers.RowIndex == 0;
                int movedBin = markers.BinIndex;

                markers.MoveTo(SpectrogramMarkerKind.TraceSelect, 0, history.RowCount - 1);

                bool binHeld = markers.BinIndex == movedBin;
                bool rowMoved = markers.RowIndex == history.RowCount - 1;

                bool perpendicular =
                    SpectrogramMarkers.IsVertical(SpectrogramMarkerKind.Spectrogram) &&
                    SpectrogramMarkers.IsHorizontal(SpectrogramMarkerKind.TraceSelect);

                bool ok = rowHeld && binHeld && rowMoved && perpendicular &&
                          ReferenceEquals(markers.SelectedRow, history.Newest);

                return new Outcome<double>(
                    ok, markers.FrequencyHz,
                    "spectrogram marker (vertical) dragged to " + Hz(markers.FrequencyHz) +
                    " left the row at 0: " + rowHeld +
                    "; trace select (horizontal) dragged to row " + markers.RowIndex + " of " +
                    (history.RowCount - 1) + " left the bin at " + movedBin + ": " + binHeld);
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
        /// Two things are checked, and only the first of them is about this instrument. The
        /// connected front end declares no input range control — it ranges its own converter and
        /// takes no range command — so the requirement's last clause applies to it: the function
        /// must be <em>unavailable</em>, and asking anyway must be refused rather than quietly
        /// ignored. That is the strongest statement this bench can make about the real front end,
        /// and it is made against the real front end.
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

        private void ExercisePresets(double centerFrequencyHz, SpectrumFrame measured)
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

            Step("REQ-UI-063", "Restart discards the averaging of a real measurement", () =>
            {
                // The criterion, over frames the instrument produced: "all current measurement data
                // including averaging is discarded", asserted by a non-zero average count returning
                // to zero. Against the averager the shell's engine holds, not a stand-in for it.
                var averager = new TraceAverager(AveragingType.RmsVideo, 64);

                SpectrumFrame frame = measured;

                if (frame == null)
                {
                    return Failed<int>("no spectrum was computed to average");
                }

                for (int sweep = 0; sweep < 5; sweep++)
                {
                    averager.Accumulate(frame);
                }

                int accumulated = averager.Completed;

                averager.Reset();

                return new Outcome<int>(
                    accumulated > 0 && averager.Completed == 0,
                    averager.Completed,
                    accumulated + " sweeps accumulated over " + frame.PointCount +
                    " points, then Restart left " + averager.Completed);
            });

            Step("REQ-UI-063", "The sweep control means two things by a second press", () =>
            {
                // Both branches, on the state machine the Control toolbar and the space bar share.
                // The requirement asks for both because collapsing them is the likely shortcut.
                var single = new SweepControl { IsRunning = true, Mode = SweepMode.Single };
                var continuous = new SweepControl { IsRunning = true, Mode = SweepMode.Continuous };

                single.Press();
                continuous.Press();

                SweepAction stepped = single.Press();
                SweepAction continued = continuous.Press();

                return new Outcome<string>(
                    stepped == SweepAction.Step && continued == SweepAction.Continue &&
                    single.IsPaused && !continuous.IsPaused,
                    stepped + " / " + continued,
                    "Single: " + stepped + ", still held: " + single.IsPaused +
                    "; Continuous: " + continued + ", still held: " + continuous.IsPaused);
            });

            Step("REQ-UI-061", "No preset variant disturbs the reference or the source", () =>
            {
                // Over the settings the instrument is actually running, not a synthetic state: the
                // separation REQ-UI-061 calls out is between the measurement a user is making and
                // the hardware they spent ten minutes getting to talk, and this is that measurement.
                ApplicationState bench = ApplicationState.Default("Bench");
                MeasurementState settings = bench.Measurements[0];

                settings.CenterFrequencyHz = centerFrequencyHz;
                settings.Input.ExternalReference = true;
                settings.Source.IsEnabled = true;
                settings.Source.FrequencyHz = centerFrequencyHz;

                var disturbed = new List<string>();

                foreach (PresetVariant variant in Presets.Variants)
                {
                    MeasurementState after = Presets.Apply(variant, bench).Measurements[0];

                    if (!after.Input.ExternalReference ||
                        !after.Source.IsEnabled ||
                        Math.Abs(after.Source.FrequencyHz - centerFrequencyHz) > 1.0)
                    {
                        disturbed.Add(Presets.NameOf(variant));
                    }
                }

                return new Outcome<int>(
                    disturbed.Count == 0,
                    disturbed.Count,
                    disturbed.Count == 0
                        ? Presets.Variants.Count +
                          " variants applied at " + Hz(centerFrequencyHz) +
                          "; the external reference and the source survived every one"
                        : "disturbed by " + string.Join(", ", disturbed));
            });

            Step("REQ-UI-061", "Preset Measurement returns the settings it names", () =>
            {
                ApplicationState bench = ApplicationState.Default("Bench");
                bench.Measurements[0].CenterFrequencyHz = centerFrequencyHz;
                bench.Measurements[0].SpanHz = 1.234e6;

                MeasurementState after =
                    Presets.Apply(PresetVariant.Measurement, bench).Measurements[0];

                var defaults = new MeasurementState();

                bool reset =
                    Math.Abs(after.CenterFrequencyHz - defaults.CenterFrequencyHz) < 1.0 &&
                    Math.Abs(after.SpanHz - defaults.SpanHz) < 1.0;

                return new Outcome<string>(
                    reset,
                    Hz(after.CenterFrequencyHz) + " / " + Hz(after.SpanHz),
                    reset
                        ? "centre and span went from " + Hz(centerFrequencyHz) + " / " +
                          Hz(1.234e6) + " back to " + Hz(defaults.CenterFrequencyHz) + " / " +
                          Hz(defaults.SpanHz)
                        : "centre and span came back as " + Hz(after.CenterFrequencyHz) + " / " +
                          Hz(after.SpanHz));
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

            ExerciseSyntheticSymbols();
        }

        /// <summary>
        /// Checks that the harness can produce what the demodulation displays need
        /// (<c>REQ-UI-050</c>, <c>REQ-UI-051</c>, <c>REQ-UI-052</c>, <c>REQ-DEM-083</c>).
        /// </summary>
        /// <remarks>
        /// <para>
        /// <strong>No instrument, and that is the point of it being here.</strong> The display
        /// group's criteria are all worded against a signal whose symbols and symbol clock are
        /// known, and none of them can be attempted until something can produce one. These steps
        /// say, in the harness's own report, that it now can — and analyse the generated signal
        /// through the product's own spectrum path rather than through a copy of it, so a
        /// generator that agreed only with itself would fail here.
        /// </para>
        /// <para>
        /// They will be replaced by the displays' own criteria as those are built. Until then this
        /// is what stands between "the displays are not built" and "the displays cannot be built".
        /// </para>
        /// </remarks>
        /// <summary>A symbol-table row's gutter value.</summary>
        private static int Gutter(IReadOnlyList<string> rows, int row) =>
            int.Parse(
                rows[row].Substring(0, SymbolTable.GutterWidth).Trim(),
                CultureInfo.InvariantCulture);

        /// <summary>A symbol-table row with its gutter and separator removed.</summary>
        private static string Body(string row) => row.Substring(SymbolTable.GutterWidth + 1);

        private void ExerciseSyntheticSymbols()
        {
            Step("REQ-UI-050", "A generated constellation has one known point per symbol", () =>
            {
                // "exactly one point per symbol, at the decision instants" needs a signal that says
                // where its decision instants are and what was sent at each.
                var wrong = new List<string>();
                int total = 0;

                foreach (ModulationScheme scheme in ModulationScheme.All)
                {
                    var source = new SyntheticSymbolSource { Scheme = scheme };
                    SyntheticBurst burst = source.Generate(256);

                    total += burst.Symbols.Count;

                    if (burst.DecisionSampleIndices.Count != burst.Symbols.Count ||
                        burst.CorrectlyDecided() != burst.Symbols.Count ||
                        burst.ErrorVectorMagnitude() > 0.01)
                    {
                        wrong.Add(
                            scheme.Name + " (" + burst.CorrectlyDecided() + " of " +
                            burst.Symbols.Count + " decided, EVM " +
                            (burst.ErrorVectorMagnitude() * 100.0).ToString("0.000", CultureInfo.CurrentCulture) +
                            " %)");
                    }
                }

                return new Outcome<int>(
                    wrong.Count == 0, total,
                    wrong.Count == 0
                        ? total + " symbols over " + ModulationScheme.All.Count +
                          " modulations, every one recovered at its own decision instant"
                        : "wrong: " + string.Join(", ", wrong));
            });

            Step("REQ-UI-051", "Each modulation declares the eye openings it should show", () =>
            {
                // "an m-level modulation shows m-1 eyes stacked vertically, counted for at least
                // two values of m". Counted from the constellation rather than declared, so the
                // number a display is checked against cannot be a number someone typed.
                var counted = new List<string>();
                bool agree = true;

                foreach (ModulationScheme scheme in ModulationScheme.All)
                {
                    var levels = new HashSet<double>();

                    foreach (SymbolPoint point in scheme.IdealPoints)
                    {
                        levels.Add(Math.Round(point.I, 6));
                    }

                    agree &= levels.Count - 1 == scheme.EyeOpenings;

                    counted.Add(scheme.Name + " " + scheme.EyeOpenings);
                }

                var clock = new SyntheticSymbolSource { SamplesPerSymbol = 10 }.Generate(32);
                bool evenlyClocked = true;

                for (int i = 1; i < clock.DecisionSampleIndices.Count; i++)
                {
                    evenlyClocked &=
                        clock.DecisionSampleIndices[i] - clock.DecisionSampleIndices[i - 1] == 10;
                }

                return new Outcome<int>(
                    agree && evenlyClocked, ModulationScheme.All.Count,
                    "eyes: " + string.Join(", ", counted) +
                    "; the symbol clock is even to the sample: " + evenlyClocked);
            });

            Step("REQ-DEM-083", "One displaced symbol is identifiable among its neighbours", () =>
            {
                // "verified against a signal in which one symbol is displaced so the correct point
                // is identifiable, which an off-by-one selection fails".
                const int Displaced = 37;

                var source = new SyntheticSymbolSource
                {
                    Scheme = ModulationScheme.Qpsk(),
                    DisplacedSymbolIndex = Displaced,
                    Displacement = 0.4,
                };

                SyntheticBurst burst = source.Generate(120);

                double at = burst.MeasuredAt(Displaced)
                    .DistanceTo(burst.Scheme.IdealPoints[burst.Symbols[Displaced]]);

                double worstOther = 0.0;

                for (int symbol = 0; symbol < burst.Symbols.Count; symbol++)
                {
                    if (symbol == Displaced)
                    {
                        continue;
                    }

                    worstOther = Math.Max(
                        worstOther,
                        burst.MeasuredAt(symbol)
                            .DistanceTo(burst.Scheme.IdealPoints[burst.Symbols[symbol]]));
                }

                return new Outcome<double>(
                    at > worstOther * 10.0, at,
                    "symbol " + Displaced + " is " + at.ToString("0.0000", CultureInfo.CurrentCulture) +
                    " from its ideal point against a worst of " +
                    worstOther.ToString("0.0000", CultureInfo.CurrentCulture) +
                    " for the other " + (burst.Symbols.Count - 1));
            });

            Step("REQ-UI-052", "A generated burst yields a symbol stream and its metrics", () =>
            {
                // The two portions of the one trace: the detected symbol/bit stream below, the
                // error-summary metrics above.
                var source = new SyntheticSymbolSource
                {
                    Scheme = ModulationScheme.Qam16(),
                    SignalToNoiseDb = 25.0,
                };

                SyntheticBurst burst = source.Generate(160);

                IReadOnlyList<string> rows = burst.SymbolStream(binary: true, perRow: 16);
                double evm = burst.ErrorVectorMagnitude();

                bool shaped = rows.Count == 10 &&
                              rows[0].Split(' ').Length == 16 &&
                              rows[0].Split(' ')[0].Length == burst.Scheme.BitsPerSymbol;

                // 25 dB of signal to noise is about 5.6 per cent EVM, which is a figure an error
                // summary would show rather than one that reads as a broken measurement.
                bool plausible = evm > 0.02 && evm < 0.12;

                return new Outcome<double>(
                    shaped && plausible, evm,
                    rows.Count + " rows of " + burst.Scheme.BitsPerSymbol + "-bit symbols, EVM " +
                    (evm * 100.0).ToString("0.00", CultureInfo.CurrentCulture) +
                    " % at 25 dB SNR; first row " + rows[0].Substring(0, 14) + "…");
            });

            Step("REQ-UI-053", "The error summary reproduces the requirement's layout", () =>
            {
                // Rendered against a signal of known impairments, which is the criterion's framing.
                // What is asserted is the layout: the = at a fixed column on every row, RMS then
                // peak then "at symbol N", engineering prefixes rather than exponents, and the
                // terse labels exactly.
                SymbolTrace result = new SyntheticSymbolSource
                {
                    Scheme = ModulationScheme.Qam16(),
                    SignalToNoiseDb = 26.0,
                }.Generate(400).ToSymbolTrace();

                ErrorSummary summary = ErrorSummary.For(result);
                IReadOnlyList<string> rows = summary.Render();

                bool aligned = rows.Count > 0;
                bool labelled = true;

                foreach (string row in rows)
                {
                    aligned &= row.IndexOf('=') == ErrorSummary.EqualsColumn;
                    aligned &= row.IndexOf('E' + "+", StringComparison.Ordinal) < 0;
                }

                foreach (ErrorMetric metric in summary.Metrics)
                {
                    labelled &= ErrorSummary.Labels.Contains(metric.Label);
                }

                string evm = rows.Count > 0 ? rows[0] : string.Empty;

                bool ordered = evm.IndexOf("%rms", StringComparison.Ordinal) > 0 &&
                               evm.IndexOf(" pk", StringComparison.Ordinal) >
                                   evm.IndexOf("%rms", StringComparison.Ordinal) &&
                               evm.IndexOf("at symbol ", StringComparison.Ordinal) >
                                   evm.IndexOf(" pk", StringComparison.Ordinal);

                return new Outcome<int>(
                    aligned && labelled && ordered, rows.Count,
                    rows.Count + " rows, '=' at column " + ErrorSummary.EqualsColumn +
                    " throughout; first row: " + evm.Trim());
            });

            Step("REQ-UI-052", "The symbol table's gutter counts bits in binary and symbols in hex", () =>
            {
                // The criterion's sharpest clause, and the one an implementation gets half right:
                // the two gutters count different quantities over the same stream.
                SymbolTrace result = new SyntheticSymbolSource
                {
                    Scheme = ModulationScheme.Qam16(),
                }.Generate(64).ToSymbolTrace();

                IReadOnlyList<string> binary = SymbolTable.Render(
                    result.Symbols, result.BitsPerSymbol, SymbolTableFormat.Binary, 32);

                IReadOnlyList<string> hex = SymbolTable.Render(
                    result.Symbols, result.BitsPerSymbol, SymbolTableFormat.Hexadecimal, 16);

                int binaryGutter = Gutter(binary, 1);
                int hexGutter = Gutter(hex, 1);

                // Row 1 of the binary table starts at bit 32; row 1 of the hex table at symbol 16.
                bool counted = binaryGutter == 32 && hexGutter == 16;

                // Groups of eight characters separated by a space, in both.
                bool grouped =
                    Body(binary[0]).Split(' ').All(g => g.Length == SymbolTable.GroupSize) &&
                    Body(hex[0]).Split(' ').All(g => g.Length == SymbolTable.GroupSize);

                // And hex is refused below four bits a symbol, with a reason.
                bool refused = !SymbolTable.IsAvailable(SymbolTableFormat.Hexadecimal, 2) &&
                               SymbolTable.ReasonAgainst(SymbolTableFormat.Hexadecimal, 2) != null;

                return new Outcome<int>(
                    counted && grouped && refused, binaryGutter,
                    "binary row 1 at bit " + binaryGutter + ", hex row 1 at symbol " + hexGutter +
                    "; groups of " + SymbolTable.GroupSize +
                    "; hex refused below 4 bits a symbol: " + refused);
            });

            Step("REQ-UI-050", "The generated signal is a real one through the product's own DSP", () =>
            {
                // The generator analysed by the product rather than by itself: a burst whose
                // spectrum did not match its own symbol rate would not be a signal any display
                // could honestly be checked against.
                var source = new SyntheticSymbolSource
                {
                    Scheme = ModulationScheme.Qam16(),
                    SampleRateHz = 12.8e6,
                    SamplesPerSymbol = 8,
                    RollOff = 0.35,
                };

                SyntheticBurst burst = source.Generate(512);

                using (IqBlock block = burst.ToBlock(1e9, DateTime.UtcNow))
                {
                    SpectrumFrame frame =
                        new SpectrumComputer(WindowType.Hann, null, null).Compute(block);

                    ReadOnlySpan<float> levels = frame.LevelsDbm;

                    double peak = double.MinValue;

                    for (int i = 0; i < levels.Length; i++)
                    {
                        peak = Math.Max(peak, levels[i]);
                    }

                    int first = -1;
                    int last = -1;

                    for (int i = 0; i < levels.Length; i++)
                    {
                        if (levels[i] > peak - 20.0)
                        {
                            if (first < 0)
                            {
                                first = i;
                            }

                            last = i;
                        }
                    }

                    double measuredHz = (last - first) * frame.BinWidthHz;
                    double expectedHz = burst.SymbolRateHz * (1.0 + source.RollOff);

                    return new Outcome<double>(
                        measuredHz > expectedHz * 0.75 && measuredHz < expectedHz * 1.25,
                        measuredHz,
                        "symbol rate " + Hz(burst.SymbolRateHz) + " occupies " + Hz(measuredHz) +
                        " at 20 dB down, against " + Hz(expectedHz) + " for a roll-off of " +
                        source.RollOff.ToString("0.00", CultureInfo.CurrentCulture));
                }
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
