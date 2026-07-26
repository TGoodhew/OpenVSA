using System;

namespace OpenVSA.Dsp.Spectrum
{
    /// <summary>
    /// The averaging types of <c>REQ-DSP-030</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Coherent and incoherent are not interchangeable.</strong> The requirement's
    /// criterion is that they behave differently and measurably so: coherent averaging of a
    /// triggered tone in noise improves signal-to-noise by <c>10·log10(N)</c>, while incoherent
    /// averaging leaves signal-to-noise where it was and instead reduces the <em>variance</em> of
    /// the estimate by <c>N</c>. Conflating them is the classic error, and it is easy to miss
    /// because both make a trace look smoother.
    /// </para>
    /// </remarks>
    public enum AveragingType
    {
        /// <summary>No averaging; each acquisition replaces the last.</summary>
        Off = 0,

        /// <summary>Power-domain (incoherent) average; runs to the count, then stops.</summary>
        RmsVideo,

        /// <summary>Power-domain, exponentially weighted, running indefinitely.</summary>
        RmsVideoExponential,

        /// <summary>Coherent (vector) average of the complex spectrum; runs to the count.</summary>
        Time,

        /// <summary>Coherent, exponentially weighted, running indefinitely.</summary>
        TimeExponential,

        /// <summary>Per-bin maximum; runs to the count.</summary>
        PeakHold,

        /// <summary>Per-bin maximum, running indefinitely.</summary>
        ContinuousPeakHold,
    }

    /// <summary>
    /// Accumulates successive spectra according to an <see cref="AveragingType"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Coherent averaging works on the complex spectrum; incoherent on power.</strong>
    /// That distinction is the whole of the requirement. Averaging complex values lets noise
    /// uncorrelated with the trigger cancel, so the tone rises out of it; averaging powers cannot
    /// cancel anything, because power is positive — it only reduces the scatter of the estimate.
    /// </para>
    /// <para>
    /// <strong>A power-averaged spectrum has no phase.</strong> Squaring discards it and no
    /// average of magnitudes can recover it, so the resulting frame reports
    /// <see cref="SpectrumFrame.HasPhase"/> as false and <c>REQ-TRC-002</c> uses that to make the
    /// phase formats unselectable rather than showing something meaningless.
    /// </para>
    /// <para>
    /// Not thread-safe: one averager belongs to one trace on one pump.
    /// </para>
    /// </remarks>
    public sealed class TraceAverager
    {
        private double[] _accumulator;
        private int _points;

        /// <summary>Creates an averager.</summary>
        /// <param name="type">Which average to compute.</param>
        /// <param name="count">Averages to run to; must be at least one.</param>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is less than one.</exception>
        public TraceAverager(AveragingType type, int count = 10)
        {
            if (count < 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(count), count, "An average runs over at least one acquisition.");
            }

            Type = type;
            Count = count;
        }

        /// <summary>Which average this computes.</summary>
        public AveragingType Type { get; }

        /// <summary>Averages to run to.</summary>
        public int Count { get; }

        /// <summary>Acquisitions accumulated so far.</summary>
        public int Completed { get; private set; }

        /// <summary>
        /// Whether a linear (non-exponential) average has reached its count.
        /// </summary>
        /// <remarks>
        /// Exponential and continuous types never complete: they run indefinitely by definition,
        /// so asking whether they are finished has no answer other than no.
        /// </remarks>
        public bool IsComplete => IsLinear && Completed >= Count;

        /// <summary>
        /// Whether reaching the count restarts the average rather than holding it.
        /// </summary>
        /// <remarks>
        /// <c>REQ-DSP-030</c>'s Repeat Average. Off, a completed average is held and further
        /// acquisitions are ignored, which is what makes a finished measurement stay put while it
        /// is read.
        /// </remarks>
        public bool RepeatAverage { get; set; }

        /// <summary>Whether this type averages the complex spectrum rather than power.</summary>
        public bool IsCoherent => Type == AveragingType.Time || Type == AveragingType.TimeExponential;

        /// <summary>Whether this type runs to a count rather than indefinitely.</summary>
        public bool IsLinear =>
            Type == AveragingType.RmsVideo ||
            Type == AveragingType.Time ||
            Type == AveragingType.PeakHold;

        /// <summary>Whether the result of this type carries phase.</summary>
        public bool ProducesPhase => Type == AveragingType.Off || IsCoherent;

        /// <summary>
        /// Accumulates a frame and returns the averaged result.
        /// </summary>
        /// <param name="frame">The newly computed spectrum.</param>
        /// <returns>
        /// The average so far, or <paramref name="frame"/> unchanged when averaging is off. A
        /// completed non-repeating average returns its held result and ignores the input.
        /// </returns>
        /// <exception cref="ArgumentNullException"><paramref name="frame"/> is null.</exception>
        public SpectrumFrame Accumulate(SpectrumFrame frame)
        {
            if (frame == null)
            {
                throw new ArgumentNullException(nameof(frame));
            }

            if (Type == AveragingType.Off)
            {
                return frame;
            }

            if (IsComplete && !RepeatAverage)
            {
                return Result(frame);
            }

            if (IsComplete && RepeatAverage)
            {
                Reset();
            }

            if (_accumulator == null || _points != frame.PointCount)
            {
                Reset();
                _points = frame.PointCount;
                _accumulator = new double[_points * 2];
            }

            ReadOnlySpan<float> complex = frame.Complex;
            Completed++;

            switch (Type)
            {
                case AveragingType.Time:
                case AveragingType.TimeExponential:
                    AccumulateCoherent(complex);
                    break;

                case AveragingType.RmsVideo:
                case AveragingType.RmsVideoExponential:
                    AccumulatePower(complex);
                    break;

                case AveragingType.PeakHold:
                case AveragingType.ContinuousPeakHold:
                    AccumulatePeak(complex);
                    break;

                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(Type), Type, "Unknown averaging type.");
            }

            return Result(frame);
        }

        /// <summary>Discards the accumulation and starts again.</summary>
        public void Reset()
        {
            Completed = 0;
            _accumulator = null;
            _points = 0;
        }

        /// <summary>
        /// Coherent: average the complex values, so uncorrelated noise cancels.
        /// </summary>
        private void AccumulateCoherent(ReadOnlySpan<float> complex)
        {
            double alpha = Weight();

            for (int i = 0; i < _accumulator.Length; i++)
            {
                _accumulator[i] += alpha * (complex[i] - _accumulator[i]);
            }
        }

        /// <summary>
        /// Incoherent: average the powers. Phase is discarded here and cannot be recovered.
        /// </summary>
        private void AccumulatePower(ReadOnlySpan<float> complex)
        {
            double alpha = Weight();

            for (int i = 0; i < _points; i++)
            {
                double re = complex[i * 2];
                double im = complex[i * 2 + 1];
                double power = re * re + im * im;

                _accumulator[i * 2] += alpha * (power - _accumulator[i * 2]);
                _accumulator[i * 2 + 1] = 0.0;
            }
        }

        /// <summary>Per-bin maximum magnitude, keeping the bin that produced it.</summary>
        private void AccumulatePeak(ReadOnlySpan<float> complex)
        {
            for (int i = 0; i < _points; i++)
            {
                double re = complex[i * 2];
                double im = complex[i * 2 + 1];
                double power = re * re + im * im;

                if (Completed == 1 || power > _accumulator[i * 2])
                {
                    _accumulator[i * 2] = power;
                    _accumulator[i * 2 + 1] = 0.0;
                }
            }
        }

        /// <summary>
        /// The weight this acquisition carries.
        /// </summary>
        /// <remarks>
        /// <c>1/n</c> while ramping, which makes the first <c>N</c> acquisitions an exact running
        /// mean rather than an exponential one biased towards the first sample. An exponential type
        /// then holds <c>α = 1/N</c> for ever; a linear one has stopped by that point.
        /// </remarks>
        private double Weight()
        {
            int ramp = Completed < Count ? Completed : Count;
            return 1.0 / ramp;
        }

        /// <summary>Builds the frame the average currently represents.</summary>
        private SpectrumFrame Result(SpectrumFrame latest)
        {
            if (_accumulator == null)
            {
                return latest;
            }

            var complex = new float[_accumulator.Length];

            if (IsCoherent)
            {
                for (int i = 0; i < complex.Length; i++)
                {
                    complex[i] = (float)_accumulator[i];
                }
            }
            else
            {
                // Power back to a magnitude, with no phase to restore: the imaginary part is zero
                // and the frame says so, rather than implying a phase of nought.
                for (int i = 0; i < _points; i++)
                {
                    complex[i * 2] = (float)Math.Sqrt(_accumulator[i * 2]);
                    complex[i * 2 + 1] = 0.0f;
                }
            }

            return latest.WithComplex(complex, ProducesPhase, Completed);
        }
    }
}
