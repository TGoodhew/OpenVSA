using System;
using System.Collections.Generic;
using System.Globalization;
using OpenVSA.Core;

namespace OpenVSA.Dsp.Zoom
{
    /// <summary>
    /// Shifts a chosen frequency to zero and decimates to match — the downconverter zoom and
    /// playback re-analysis are built on (<c>REQ-DSP-023a</c>, <c>REQ-DSP-023</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The mixer is folded into the taps.</strong> Written out, a downconverter multiplies
    /// every input sample by <c>e^(−j2πνn)</c>, low-passes the result and throws away all but every
    /// <c>M</c>th sample — a full-rate pass over the record and a sine and cosine per input sample,
    /// nearly all of it discarded. The same output falls out of
    /// <c>y[m] = e^(−j2πν·n) · Σ_k h[k]·e^(+j2πνk) · x[n−k]</c> with <c>n = mM + D</c>: shifting the
    /// low-pass onto the carrier turns it into a complex band-pass, and the only mixing left is one
    /// rotation per <em>output</em> sample. That is not an optimisation of the obvious form, it is
    /// the same algebra rearranged, so the two agree to the last bit and one of them costs
    /// <c>M</c> times less trigonometry.
    /// </para>
    /// <para>
    /// <strong>Unity gain at the tuned frequency, by construction.</strong> The low-pass is
    /// normalised to unity at DC, and the shifted taps have gain <c>Σ h[k]</c> at exactly the
    /// frequency they were shifted to. A tone at the tuned frequency therefore emerges at its
    /// original amplitude with no correction applied anywhere, which is what keeps zoom out of
    /// <c>REQ-AMP-001</c>'s amplitude chain rather than adding a term to it.
    /// </para>
    /// <para>
    /// <strong>The −6 dB cutoff always lands at half the output rate.</strong> Decimating by
    /// <c>M</c> folds everything at and above <c>Fs_out − f_pass</c> into the passband, so placing
    /// the passband edge at <c>α·Fs_out/2</c> puts the stopband edge at <c>Fs_out(1 − α/2)</c> and
    /// the midpoint of the two at <c>Fs_out/2</c> whatever <c>α</c> is. The transition band is then
    /// symmetric about the fold, which is the arrangement that lets one filter serve every
    /// decimation factor with the same shape and the same
    /// <see cref="DdcDesignTargets.UsableBandwidthFraction"/> of usable span.
    /// </para>
    /// <para>
    /// <strong>Phase is referenced to the input record, not to the filter.</strong> Output
    /// <c>j</c> is the downconverted value of input sample
    /// <c>AlignmentOffsetSamples + j × Decimation</c>: group delay removed from the amplitude and
    /// the same instant used for the mixer rotation. Reference the rotation to the filter's output
    /// instant instead and the record still looks perfect on a spectrum — the error is a constant
    /// phase — while every demodulated symbol is rotated by an amount that changes with the tap
    /// count.
    /// </para>
    /// </remarks>
    public sealed class DigitalDownconverter
    {
        /// <summary>
        /// The largest filter this class will build, in taps.
        /// </summary>
        /// <remarks>
        /// Tap count grows in proportion to the decimation factor, so a factor arriving from a
        /// mis-scaled span — hertz where megahertz was meant — asks for a filter of tens of
        /// millions of taps and fails as an out-of-memory exception a long way from its cause. This
        /// limit is far above any legitimate zoom (<c>REQ-REC-004</c> bounds zoom depth at 1/256 of
        /// the source span, which is a decimation of a few hundred) and exists only so that the
        /// mistake is reported as the mistake it is.
        /// </remarks>
        public const int MaximumTapCount = 1 << 20;

        private readonly PolyphaseDecimator _decimator;
        private readonly double _inputRateHz;
        private readonly double _shiftHz;
        private readonly double _shiftCycles;

        // The normalised shift split so that the coarse part times a sample index is exact. See
        // CyclesAt.
        private readonly double _shiftCoarse;
        private readonly double _shiftFine;

        private DigitalDownconverter(
            double inputRateHz, double shiftHz, int decimation, double[] lowPass)
        {
            _inputRateHz = inputRateHz;
            _shiftHz = shiftHz;
            _shiftCycles = shiftHz / inputRateHz;

            _shiftCoarse = Math.Round(_shiftCycles * TwoPower22) / TwoPower22;
            _shiftFine = _shiftCycles - _shiftCoarse;

            var tapsI = new double[lowPass.Length];
            var tapsQ = new double[lowPass.Length];

            for (int k = 0; k < lowPass.Length; k++)
            {
                double cycles = CyclesAt(k);
                double angle = TwoPi * cycles;

                tapsI[k] = lowPass[k] * Math.Cos(angle);
                tapsQ[k] = lowPass[k] * Math.Sin(angle);
            }

            _decimator = PolyphaseDecimator.WithComplexTaps(tapsI, tapsQ, decimation);
        }

        /// <summary>
        /// Builds a downconverter for a given decimation factor.
        /// </summary>
        /// <param name="inputRateHz">Input sample rate, in hertz; positive and finite.</param>
        /// <param name="shiftHz">
        /// The frequency, relative to the input's centre, that is brought to zero. Positive tunes
        /// upwards.
        /// </param>
        /// <param name="decimation">
        /// Decimation factor; at least 2. A factor of 1 would ask for an all-pass filter with a
        /// zero-width transition band, which is not a filter — a re-centring that does not narrow
        /// the span is a change of frequency axis, not a downconversion.
        /// </param>
        /// <exception cref="ArgumentOutOfRangeException">A value is out of range.</exception>
        public static DigitalDownconverter ForDecimation(
            double inputRateHz, double shiftHz, int decimation)
        {
            if (!(inputRateHz > 0.0) || double.IsInfinity(inputRateHz))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(inputRateHz), inputRateHz, "A sample rate must be positive and finite.");
            }

            if (double.IsNaN(shiftHz) || double.IsInfinity(shiftHz))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(shiftHz), shiftHz, "A frequency shift must be finite.");
            }

            if (decimation < 2)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(decimation), decimation,
                    "A downconverter decimates by at least 2; a shift without decimation is a " +
                    "change of centre frequency, not a zoom.");
            }

            double usableHz = DdcDesignTargets.UsableBandwidthFraction * inputRateHz / decimation;

            if (Math.Abs(shiftHz) + usableHz / 2.0 > inputRateHz / 2.0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(shiftHz), shiftHz,
                    "The wanted band, " + Hz(usableHz) + " wide about " + Hz(shiftHz) +
                    ", reaches outside the input's " + Hz(inputRateHz) +
                    " sample rate, where there is no data to zoom into.");
            }

            int taps = FirDesign.TapCountFor(
                (1.0 - DdcDesignTargets.UsableBandwidthFraction) / decimation,
                DdcDesignTargets.DesignStopbandDb);

            if (taps > MaximumTapCount)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(decimation), decimation,
                    "Decimating by " + decimation.ToString(CultureInfo.InvariantCulture) +
                    " needs " + taps.ToString(CultureInfo.InvariantCulture) +
                    " taps, past the " + MaximumTapCount.ToString(CultureInfo.InvariantCulture) +
                    "-tap limit. Check the units of the span this factor came from.");
            }

            double[] lowPass = FirDesign.LowPass(
                0.5 / decimation,
                (1.0 - DdcDesignTargets.UsableBandwidthFraction) / decimation,
                DdcDesignTargets.DesignStopbandDb);

            return new DigitalDownconverter(inputRateHz, shiftHz, decimation, lowPass);
        }

        /// <summary>
        /// Builds a downconverter that delivers at least a wanted span.
        /// </summary>
        /// <param name="inputRateHz">Input sample rate, in hertz; positive and finite.</param>
        /// <param name="shiftHz">
        /// The frequency, relative to the input's centre, that becomes the centre of the zoomed
        /// span.
        /// </param>
        /// <param name="spanHz">Wanted analysis span, in hertz; positive and finite.</param>
        /// <remarks>
        /// The decimation factor is rounded <em>down</em>, so the delivered
        /// <see cref="UsableBandwidthHz"/> is never narrower than the span asked for. Rounding to
        /// nearest would sometimes return a measurement over a narrower span than the one
        /// requested, with the annotation still reading what the user typed.
        /// <para>
        /// This does not apply <c>REQ-REC-004</c>'s 1/256 zoom bound. That bound is a property of
        /// the measurement — it depends on the source span, which this class never sees — and
        /// enforcing a policy here as well would put the message the user is shown in two places.
        /// </para>
        /// </remarks>
        /// <exception cref="ArgumentOutOfRangeException">A value is out of range.</exception>
        public static DigitalDownconverter ForSpan(
            double inputRateHz, double shiftHz, double spanHz)
        {
            if (!(inputRateHz > 0.0) || double.IsInfinity(inputRateHz))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(inputRateHz), inputRateHz, "A sample rate must be positive and finite.");
            }

            if (!(spanHz > 0.0) || double.IsInfinity(spanHz))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(spanHz), spanHz, "A span must be positive and finite.");
            }

            double exact = DdcDesignTargets.UsableBandwidthFraction * inputRateHz / spanHz;
            double widest = DdcDesignTargets.UsableBandwidthFraction * inputRateHz / 2.0;

            if (!(exact >= 2.0))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(spanHz), spanHz,
                    "A span of " + Hz(spanHz) + " needs no downconversion at a " + Hz(inputRateHz) +
                    " sample rate; the widest span this narrows to is " + Hz(widest) + ".");
            }

            int decimation = exact >= int.MaxValue ? int.MaxValue : (int)Math.Floor(exact);

            return ForDecimation(inputRateHz, shiftHz, decimation);
        }

        /// <summary>Decimation factor.</summary>
        public int Decimation => _decimator.Decimation;

        /// <summary>Number of taps in the band-pass.</summary>
        public int TapCount => _decimator.TapCount;

        /// <summary>Input sample rate, in hertz.</summary>
        public double InputRateHz => _inputRateHz;

        /// <summary>Output sample rate, in hertz.</summary>
        public double OutputRateHz => _inputRateHz / _decimator.Decimation;

        /// <summary>The frequency brought to zero, relative to the input's centre, in hertz.</summary>
        public double ShiftHz => _shiftHz;

        /// <summary>
        /// The alias-free, flat bandwidth delivered, in hertz:
        /// <see cref="DdcDesignTargets.UsableBandwidthFraction"/> of <see cref="OutputRateHz"/>.
        /// </summary>
        public double UsableBandwidthHz =>
            DdcDesignTargets.UsableBandwidthFraction * OutputRateHz;

        /// <summary>Passband edge, in hertz from the tuned frequency.</summary>
        public double PassbandEdgeHz => UsableBandwidthHz / 2.0;

        /// <summary>
        /// The frequency, in hertz from the tuned frequency, at and beyond which rejection is full.
        /// </summary>
        public double StopbandEdgeHz => OutputRateHz - PassbandEdgeHz;

        /// <summary>Filter group delay, in input samples.</summary>
        public int GroupDelaySamples => _decimator.GroupDelaySamples;

        /// <summary>
        /// Index of the input sample the first output corresponds to.
        /// </summary>
        /// <remarks>
        /// Output <c>j</c> is the downconverted value of input sample
        /// <c>AlignmentOffsetSamples + j × Decimation</c>. A zoomed record therefore starts this
        /// many input samples into the original, and ends <see cref="GroupDelaySamples"/> before
        /// it does; see <see cref="PolyphaseDecimator"/> for why those samples are dropped rather
        /// than manufactured.
        /// </remarks>
        public int AlignmentOffsetSamples => _decimator.AlignmentOffsetSamples;

        /// <summary>The shortest input record that yields any output at all.</summary>
        public int MinimumInputSamples => _decimator.MinimumInputSamples;

        /// <summary>
        /// How many output samples an input record of a given length yields.
        /// </summary>
        /// <param name="inputSampleCount">Complex samples available; must not be negative.</param>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="inputSampleCount"/> is negative.</exception>
        public int OutputCountFor(int inputSampleCount) =>
            _decimator.OutputCountFor(inputSampleCount);

        /// <summary>
        /// Downconverts a record.
        /// </summary>
        /// <param name="input">Interleaved I,Q input; its length must be even.</param>
        /// <param name="output">
        /// Interleaved I,Q output; must hold at least <see cref="OutputCountFor"/> complex samples.
        /// </param>
        /// <returns>The number of complex samples written.</returns>
        /// <exception cref="ArgumentException">A span is the wrong length.</exception>
        public int Downconvert(ReadOnlySpan<float> input, Span<float> output)
        {
            int count = _decimator.Decimate(input, output);

            for (int j = 0; j < count; j++)
            {
                long instant = (long)AlignmentOffsetSamples +
                               (long)j * _decimator.Decimation +
                               GroupDelaySamples;

                // The band-pass leaves the signal where it was; this rotation is what puts the
                // tuned frequency at zero. Negative, because the shift is downwards.
                double angle = -TwoPi * CyclesAt(instant);
                double cos = Math.Cos(angle);
                double sin = Math.Sin(angle);

                double i = output[j * 2];
                double q = output[j * 2 + 1];

                output[j * 2] = (float)(i * cos - q * sin);
                output[j * 2 + 1] = (float)(i * sin + q * cos);
            }

            return count;
        }

        /// <summary>
        /// Downconverts a block, returning a new block with its metadata brought up to date.
        /// </summary>
        /// <param name="block">The block to zoom into; not disposed by this call.</param>
        /// <returns>A new block the caller owns and must dispose.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="block"/> is null.</exception>
        /// <exception cref="ArgumentException">The block is too short to yield any output.</exception>
        /// <remarks>
        /// <para>
        /// Every field that the downconversion changes is changed here, because a zoomed block that
        /// carried its parent's sample rate or centre frequency would be read by the whole analysis
        /// chain as a measurement of something else. The centre moves by <see cref="ShiftHz"/>, the
        /// rate divides by <see cref="Decimation"/>, and the block is complex whatever the parent
        /// was.
        /// </para>
        /// <para>
        /// <strong>The timestamp and the trigger offset move together and in opposite
        /// directions.</strong> The record now starts <see cref="AlignmentOffsetSamples"/> input
        /// samples later, so its first-sample timestamp advances by that much and the trigger —
        /// which has not moved — is that much less far after it. Advancing only one of the two
        /// leaves <c>AcquisitionClock.TriggerInstant</c> reporting that the trigger happened at a
        /// different moment for the zoomed record than for the record it came from.
        /// </para>
        /// <para>
        /// <c>REQ-DAT-001</c>'s alias-free-bandwidth key is rewritten rather than inherited: after
        /// decimation the block's usable bandwidth is this filter's, not the front end's, and a
        /// stale value would let a display draw the roll-off of a filter that is no longer in the
        /// path.
        /// </para>
        /// </remarks>
        public IqBlock Downconvert(IqBlock block)
        {
            if (block == null)
            {
                throw new ArgumentNullException(nameof(block));
            }

            int count = OutputCountFor(block.SampleCount);

            if (count == 0)
            {
                throw new ArgumentException(
                    "A record of " + block.SampleCount.ToString(CultureInfo.InvariantCulture) +
                    " samples is too short to decimate by " +
                    Decimation.ToString(CultureInfo.InvariantCulture) + ": the " +
                    TapCount.ToString(CultureInfo.InvariantCulture) +
                    "-tap filter needs at least " +
                    MinimumInputSamples.ToString(CultureInfo.InvariantCulture) + ".",
                    nameof(block));
            }

            double advanceSeconds = AlignmentOffsetSamples / block.SampleRateHz;

            var extended = new Dictionary<string, object>(block.Extended.Count + 1);

            foreach (KeyValuePair<string, object> entry in block.Extended)
            {
                extended[entry.Key] = entry.Value;
            }

            extended[IqBlockMetadata.UsableBandwidthKey] =
                DdcDesignTargets.UsableBandwidthFraction * block.SampleRateHz / Decimation;

            var metadata = new IqBlockMetadata(
                sampleCount: count,
                sampleRateHz: block.SampleRateHz / Decimation,
                centerFrequencyHz: block.CenterFrequencyHz + _shiftHz,
                isBaseband: false,
                fullScaleVolts: block.FullScaleVolts,
                referenceLevelDbm: block.ReferenceLevelDbm,
                sequenceNumber: block.SequenceNumber,
                acquiredUtc: block.AcquiredUtc.AddTicks(
                    (long)Math.Round(advanceSeconds * TimeSpan.TicksPerSecond)),
                triggerOffsetSeconds: block.TriggerOffsetSeconds - advanceSeconds,
                triggerCorrectionsApplied: block.TriggerCorrectionsApplied,
                source: block.Source,
                extended: extended);

            IqBlock zoomed = IqBlock.Rent(metadata);

            try
            {
                Downconvert(block.GetSamples(), zoomed.GetSamples());
            }
            catch
            {
                zoomed.Dispose();
                throw;
            }

            return zoomed;
        }

        /// <summary>
        /// The mixer phase at an input sample, in cycles, reduced to <c>[0, 1)</c>.
        /// </summary>
        /// <remarks>
        /// Splitting the normalised shift at 2^−22 makes <c>coarse × n</c> exact for every sample
        /// index an <see cref="IqBlock"/> can hold — 22 bits of shift and 31 of index fit inside a
        /// <see cref="double"/>'s 53 — so the reduction loses nothing and the residual term is
        /// small enough that its own rounding is 250 dB down. Multiplying the shift by a large
        /// index directly would leave a phase error that grows with the record, and a phase error
        /// that grows with the record is a spur that appears only on long ones.
        /// </remarks>
        private double CyclesAt(long sampleIndex)
        {
            double coarse = _shiftCoarse * sampleIndex;
            double cycles = (coarse - Math.Floor(coarse)) + _shiftFine * sampleIndex;

            return cycles - Math.Floor(cycles);
        }

        private static string Hz(double value) =>
            value.ToString("G6", CultureInfo.InvariantCulture) + " Hz";

        private const double TwoPi = 2.0 * Math.PI;
        private const double TwoPower22 = 4194304.0;
    }
}
